using System.Diagnostics;

namespace Glide.App.Services;

internal enum LaunchElectionOutcome
{
    /// <summary>A running owner acknowledged the request; this launcher exits.</summary>
    Forwarded,
    /// <summary>This process won the single-instance election and becomes the UI/broker owner.</summary>
    Elected,
    /// <summary>A hidden (--background) launch found a live Glide; there is nothing to show, so it exits.</summary>
    ExitHiddenDuplicate,
    /// <summary>
    /// No forward was wanted (reuse off) or the owner never answered within the budget: this process starts its
    /// own window as a follower (the broker's takeover monitor claims ownership if the old owner goes away).
    /// </summary>
    StartFollowerUi,
}

/// <summary>
/// The follower half of the single-instance launch. Before 5.0.8 a launcher that lost the election and could not
/// complete a 2 s handshake exited with code 0 whenever a presence object still existed, and also exited when its
/// final re-election lost to a transient handle (another launcher probing the object, or a dying owner). Both
/// ended a user's launch with no window and no trace (docs/audits/silent-exit-investigation.md). This loop keeps
/// retrying forward + election until <c>budget</c> expires and then starts a window rather than vanishing.
/// </summary>
internal static class LaunchElection
{
    internal static readonly TimeSpan DefaultBudget = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan ForwardAttempt = TimeSpan.FromSeconds(2);

    internal static LaunchElectionOutcome Resolve(
        bool hasRequest,
        bool startHidden,
        Func<TimeSpan, bool> tryForward,
        Func<bool> claimPresence,
        TimeSpan budget,
        Action<TimeSpan>? sleep = null,
        Func<TimeSpan>? elapsed = null,
        Action<string>? trace = null,
        Func<bool>? ownerIncompatible = null)
    {
        sleep ??= Thread.Sleep;
        var started = Stopwatch.GetTimestamp();
        elapsed ??= () => Stopwatch.GetElapsedTime(started);
        var attempt = 0;
        while (true)
        {
            attempt++;
            if (hasRequest)
            {
                var remaining = budget - elapsed();
                var window = remaining < ForwardAttempt ? remaining : ForwardAttempt;
                if (window > TimeSpan.Zero && tryForward(window))
                {
                    trace?.Invoke($"follower_forwarded attempt={attempt} elapsedMs={(int)elapsed().TotalMilliseconds}");
                    return LaunchElectionOutcome.Forwarded;
                }
            }
            // 5.1.7: the owner cannot take this launch (other user/session, normal launch vs elevated owner):
            // waiting out the budget cannot help, so open this process's own window now.
            if (hasRequest && ownerIncompatible?.Invoke() == true)
            {
                trace?.Invoke($"owner_incompatible attempt={attempt} elapsedMs={(int)elapsed().TotalMilliseconds}");
                return LaunchElectionOutcome.StartFollowerUi;
            }
            if (claimPresence())
            {
                trace?.Invoke($"follower_elected attempt={attempt} elapsedMs={(int)elapsed().TotalMilliseconds}");
                return LaunchElectionOutcome.Elected;
            }
            if (!hasRequest)
                return startHidden ? LaunchElectionOutcome.ExitHiddenDuplicate : LaunchElectionOutcome.StartFollowerUi;
            if (elapsed() >= budget)
            {
                trace?.Invoke($"owner_unresponsive attempts={attempt} elapsedMs={(int)elapsed().TotalMilliseconds}");
                return LaunchElectionOutcome.StartFollowerUi;
            }
            sleep(TimeSpan.FromMilliseconds(100));
        }
    }
}
