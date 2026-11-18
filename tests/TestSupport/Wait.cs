using System.Diagnostics;

namespace Glide.Tests.Support;

/// <summary>
/// Shared polling helper for tests (linked into each test project). Use it instead of "sleep a fixed time, then assert":
/// it returns as soon as the condition holds and fails with a clear message when it does not hold within the timeout.
/// <para>
/// UI tests pass <c>pump: Dispatcher.UIThread.RunJobs</c> so queued dispatcher work runs between polls. A fixed delay is
/// still right when a test proves that something does NOT happen within a window (a negative wait); keep those explicit
/// and commented.
/// </para>
/// </summary>
public static class Wait
{
    public const int DefaultTimeoutMs = 10_000;

    /// <summary>Polls <paramref name="condition"/> every <paramref name="pollMs"/> ms; throws <see cref="TimeoutException"/>
    /// naming <paramref name="what"/> if it is still false after <paramref name="timeoutMs"/>.</summary>
    public static async Task UntilAsync(Func<bool> condition, string what = "condition", int timeoutMs = DefaultTimeoutMs,
        Action? pump = null, int pollMs = 10)
    {
        if (!await TryUntilAsync(condition, timeoutMs, pump, pollMs))
            throw new TimeoutException($"Timed out after {timeoutMs} ms waiting for: {what}");
    }

    /// <summary>Like <see cref="UntilAsync"/> but returns false instead of throwing when the timeout passes.</summary>
    public static async Task<bool> TryUntilAsync(Func<bool> condition, int timeoutMs = DefaultTimeoutMs, Action? pump = null, int pollMs = 10)
    {
        var clock = Stopwatch.StartNew();
        while (true)
        {
            pump?.Invoke();
            if (condition()) return true;
            if (clock.ElapsedMilliseconds >= timeoutMs) return false;
            await Task.Delay(pollMs).ConfigureAwait(true);
        }
    }

    /// <summary>Waits for <paramref name="task"/> with a timeout; throws <see cref="TimeoutException"/> naming
    /// <paramref name="what"/> instead of hanging the run.</summary>
    public static async Task<T> ForAsync<T>(Task<T> task, string what = "task", int timeoutMs = DefaultTimeoutMs)
    {
        if (await Task.WhenAny(task, Task.Delay(timeoutMs)).ConfigureAwait(true) != task)
            throw new TimeoutException($"Timed out after {timeoutMs} ms waiting for: {what}");
        return await task.ConfigureAwait(true);
    }

    /// <summary>Blocking variant for non-async tests (no UI pump).</summary>
    public static void Until(Func<bool> condition, string what = "condition", int timeoutMs = DefaultTimeoutMs, int pollMs = 10)
    {
        var clock = Stopwatch.StartNew();
        while (!condition())
        {
            if (clock.ElapsedMilliseconds >= timeoutMs) throw new TimeoutException($"Timed out after {timeoutMs} ms waiting for: {what}");
            Thread.Sleep(pollMs);
        }
    }
}
