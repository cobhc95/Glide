namespace Glide.Core.Updates;

/// <summary>
/// What Glide remembers between runs about update checks (persisted as additive keys of the settings file). Plain data:
/// every decision about it lives in <see cref="UpdateSchedule"/> and <see cref="UpdateChecker"/>, so it can be tested with a fake clock.
/// All times are UTC.
/// </summary>
public sealed class UpdateCheckState
{
    /// <summary>When a check last completed with a usable answer from GitHub.</summary>
    public DateTime? LastCheckedUtc { get; set; }
    /// <summary>When a check was last attempted (successful or not); drives the failure back-off.</summary>
    public DateTime? LastAttemptUtc { get; set; }
    /// <summary>True when the most recent attempt failed (offline, rate limited, unusable answer).</summary>
    public bool LastAttemptFailed { get; set; }
    /// <summary>The newest stable version GitHub reported (e.g. "5.0.2"), or empty.</summary>
    public string LatestKnownVersion { get; set; } = string.Empty;
    /// <summary>Its release page as reported by GitHub (validated again before it is opened), or empty.</summary>
    public string LatestKnownUrl { get; set; } = string.Empty;
    /// <summary>When the latest known release was published, if GitHub said.</summary>
    public DateTime? LatestKnownPublishedUtc { get; set; }
    public string LastPromptedVersion { get; set; } = string.Empty;
    public DateTime? LastPromptedUtc { get; set; }

    public UpdateCheckState Clone() => (UpdateCheckState)MemberwiseClone();

    public bool SameAs(UpdateCheckState other) =>
        LastCheckedUtc == other.LastCheckedUtc && LastAttemptUtc == other.LastAttemptUtc && LastAttemptFailed == other.LastAttemptFailed &&
        string.Equals(LatestKnownVersion, other.LatestKnownVersion, StringComparison.Ordinal) &&
        string.Equals(LatestKnownUrl, other.LatestKnownUrl, StringComparison.Ordinal) &&
        LatestKnownPublishedUtc == other.LatestKnownPublishedUtc &&
        string.Equals(LastPromptedVersion, other.LastPromptedVersion, StringComparison.Ordinal) && LastPromptedUtc == other.LastPromptedUtc;

    /// <summary>The newest known version, or null when none is known (or the stored text is junk).</summary>
    public UpdateVersion? LatestKnown => UpdateVersion.TryParse(LatestKnownVersion, out var v) && !v.IsPreRelease ? v : null;

    /// <summary>Records the result of one check at <paramref name="now"/>.</summary>
    public void Apply(UpdateCheckOutcome outcome, DateTime now)
    {
        if (outcome.Status is UpdateCheckStatus.Busy or UpdateCheckStatus.Unavailable) return;
        LastAttemptUtc = now;
        if (outcome.Status == UpdateCheckStatus.Failed)
        {
            LastAttemptFailed = true;
            return;
        }
        LastAttemptFailed = false;
        LastCheckedUtc = now;
        if (outcome.Release is { IsStable: true } release)
        {
            LatestKnownVersion = release.Version.ToString();
            LatestKnownUrl = release.HtmlUrl ?? string.Empty;
            LatestKnownPublishedUtc = release.PublishedAt?.UtcDateTime;
        }
    }

    /// <summary>Records that the update prompt for <paramref name="version"/> was shown at <paramref name="now"/>.</summary>
    public void MarkPrompted(string version, DateTime now)
    {
        LastPromptedVersion = version;
        LastPromptedUtc = now;
    }
}
