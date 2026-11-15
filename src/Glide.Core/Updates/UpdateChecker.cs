namespace Glide.Core.Updates;

public enum UpdateCheckStatus
{
    /// <summary>GitHub's newest stable release is not newer than the running version (or could not be read as a newer one).</summary>
    UpToDate,
    UpdateAvailable,
    /// <summary>The request or its answer was unusable (offline, rate limited, bad data). Silent in automatic checks.</summary>
    Failed,
    /// <summary>Another check (in this or another Glide window) is already running; nothing was requested.</summary>
    Busy,
    /// <summary>No feed is configured; nothing was requested.</summary>
    Unavailable
}

public sealed record UpdateCheckOutcome(UpdateCheckStatus Status, UpdateRelease? Release = null, string? FailureReason = null)
{
    public bool Completed => Status is UpdateCheckStatus.UpToDate or UpdateCheckStatus.UpdateAvailable;
}

/// <summary>
/// Runs one update check: asks the <see cref="IUpdateFeed"/>, parses the answer and compares it with the running version.
/// It never touches state or the UI (the caller applies the outcome with <see cref="UpdateCheckState.Apply"/>), and only one check
/// runs at a time across all Glide windows of the process (a shared gate): a second caller gets <see cref="UpdateCheckStatus.Busy"/>
/// at once instead of queuing another request.
/// </summary>
public sealed class UpdateChecker
{
    /// <summary>The process-wide gate used by production code; tests pass their own so they cannot interfere with each other.</summary>
    public static SemaphoreSlim SharedGate { get; } = new(1, 1);

    private readonly IUpdateFeed _feed;
    private readonly UpdateVersion _current;
    private readonly SemaphoreSlim _gate;

    public UpdateChecker(IUpdateFeed feed, UpdateVersion current, SemaphoreSlim? gate = null)
    {
        _feed = feed ?? throw new ArgumentNullException(nameof(feed));
        _current = current ?? throw new ArgumentNullException(nameof(current));
        _gate = gate ?? SharedGate;
    }

    public UpdateVersion CurrentVersion => _current;

    /// <summary>True while a check holds the gate.</summary>
    public bool IsBusy => _gate.CurrentCount == 0;

    public async Task<UpdateCheckOutcome> CheckAsync(CancellationToken cancellationToken = default)
    {
        if (!_gate.Wait(0, CancellationToken.None)) return new UpdateCheckOutcome(UpdateCheckStatus.Busy);
        try
        {
            UpdateFeedResponse response;
            try
            {
                response = await _feed.GetLatestReleaseAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return new UpdateCheckOutcome(UpdateCheckStatus.Failed, FailureReason: "cancelled");
            }
            catch (Exception ex)
            {
                return new UpdateCheckOutcome(UpdateCheckStatus.Failed, FailureReason: "feed_error:" + ex.GetType().Name);
            }

            if (response.Status == UpdateFeedStatus.Unavailable) return new UpdateCheckOutcome(UpdateCheckStatus.Unavailable);
            if (!response.Ok) return new UpdateCheckOutcome(UpdateCheckStatus.Failed, FailureReason: "feed:" + response.Status);

            var parsed = UpdateReleaseParser.Parse(response.Body);
            if (!parsed.Ok || parsed.Release is null) return new UpdateCheckOutcome(UpdateCheckStatus.Failed, FailureReason: "parse:" + parsed.Status);
            return Evaluate(parsed.Release, _current);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>A newer, stable, published version is an update; equal, older, draft and pre-release versions are not.</summary>
    public static UpdateCheckOutcome Evaluate(UpdateRelease release, UpdateVersion current) =>
        release.IsStable && release.Version > current
            ? new UpdateCheckOutcome(UpdateCheckStatus.UpdateAvailable, release)
            : new UpdateCheckOutcome(UpdateCheckStatus.UpToDate, release);
}
