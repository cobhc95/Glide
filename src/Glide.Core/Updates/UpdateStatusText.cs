using System.Globalization;

namespace Glide.Core.Updates;

public enum UpdateStatusKind { NotChecked, UpToDate, UpdateAvailable, CouldNotReach, Checking }

/// <summary>The two lines of the Settings > Updates status: when Glide last checked, and what it found.</summary>
public sealed record UpdateStatusInfo(UpdateStatusKind Kind, string LastCheckedLine, string ResultLine, bool UpdateAvailable)
{
    public string FullText => string.IsNullOrEmpty(LastCheckedLine) ? ResultLine : LastCheckedLine + " " + ResultLine;
}

/// <summary>Wording of the update status line and of the prompt's "Released N days ago" text (pure, testable).</summary>
public static class UpdateStatusText
{
    public const string NotCheckedYet = "Not checked yet";
    public const string CouldNotReachGitHub = "Couldn't reach GitHub";
    public const string Checking = "Checking...";

    public static UpdateStatusInfo Describe(UpdateCheckState state, UpdateVersion current, DateTime nowUtc, bool checking = false, TimeZoneInfo? zone = null)
    {
        var lastLine = state.LastCheckedUtc is { } last ? "Last checked: " + FormatWhen(last, nowUtc, zone) + "." : string.Empty;
        var available = UpdateSchedule.IsUpdateAvailable(state, current);
        if (checking) return new UpdateStatusInfo(UpdateStatusKind.Checking, lastLine, Checking, available);
        if (state.LastAttemptFailed) return new UpdateStatusInfo(UpdateStatusKind.CouldNotReach, lastLine, CouldNotReachGitHub, available);
        if (state.LastCheckedUtc is null) return new UpdateStatusInfo(UpdateStatusKind.NotChecked, string.Empty, NotCheckedYet, false);
        if (available && state.LatestKnown is { } latest)
            return new UpdateStatusInfo(UpdateStatusKind.UpdateAvailable, lastLine, $"Glide {latest} is available", true);
        return new UpdateStatusInfo(UpdateStatusKind.UpToDate, lastLine, $"You're up to date ({current})", false);
    }

    /// <summary>"today 21:40", "yesterday 21:40", "14 Nov 21:40" or "14 Nov 2025 21:40" (local time).</summary>
    public static string FormatWhen(DateTime whenUtc, DateTime nowUtc, TimeZoneInfo? zone = null)
    {
        zone ??= TimeZoneInfo.Local;
        var local = TimeZoneInfo.ConvertTimeFromUtc(AsUtc(whenUtc), zone);
        var today = TimeZoneInfo.ConvertTimeFromUtc(AsUtc(nowUtc), zone).Date;
        var time = local.ToString("HH:mm", CultureInfo.InvariantCulture);
        if (local.Date == today) return "today " + time;
        if (local.Date == today.AddDays(-1)) return "yesterday " + time;
        return local.Year == today.Year
            ? local.ToString("d MMM", CultureInfo.InvariantCulture) + " " + time
            : local.ToString("d MMM yyyy", CultureInfo.InvariantCulture) + " " + time;
    }

    /// <summary>"Released 2 days ago on GitHub" (the prompt's subtitle).</summary>
    public static string ReleasedAgo(DateTime? publishedUtc, DateTime nowUtc)
    {
        if (publishedUtc is not { } published) return "Released on GitHub";
        var days = (int)Math.Floor((nowUtc - published).TotalDays);
        if (days <= 0) return "Released today on GitHub";
        if (days == 1) return "Released 1 day ago on GitHub";
        if (days < 60) return $"Released {days} days ago on GitHub";
        if (days < 730) return $"Released {days / 30} months ago on GitHub";
        return $"Released {days / 365} years ago on GitHub";
    }

    private static DateTime AsUtc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc => value,
        DateTimeKind.Local => value.ToUniversalTime(),
        _ => DateTime.SpecifyKind(value, DateTimeKind.Utc)
    };
}
