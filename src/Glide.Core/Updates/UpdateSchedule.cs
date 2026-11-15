namespace Glide.Core.Updates;

/// <summary>Why the update prompt is being held back right now (it is retried a minute later).</summary>
public enum UpdatePromptDeferral
{
    None,
    /// <summary>No visible, active Glide window to show it over.</summary>
    NoWindow,
    Fullscreen,
    Slideshow,
    /// <summary>Another dialog (Settings, a confirmation...) is open.</summary>
    ModalDialog,
    /// <summary>The user is typing in a text box.</summary>
    Typing
}

/// <summary>
/// The schedule and prompt rules of the update checker, as pure functions of the persisted <see cref="UpdateCheckState"/>, the
/// running version and a clock value, so they can be tested without time or UI.
/// </summary>
public static class UpdateSchedule
{
    /// <summary>An automatic check runs at most this often.</summary>
    public static readonly TimeSpan CheckInterval = TimeSpan.FromHours(24);
    /// <summary>After a failed attempt the next automatic attempt waits at least this long (never a tight retry loop).</summary>
    public static readonly TimeSpan FailureBackoff = TimeSpan.FromHours(6);
    /// <summary>The prompt for one version is shown at most once in this window ("once a day"; a little under 24 h so a daily start still sees it).</summary>
    public static readonly TimeSpan PromptRepeatInterval = TimeSpan.FromHours(20);
    /// <summary>The first automatic check waits this long after the main window is shown.</summary>
    public static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(20);
    /// <summary>How often a running instance re-evaluates the schedule and a deferred prompt.</summary>
    public static readonly TimeSpan TickInterval = TimeSpan.FromMinutes(1);
    /// <summary>A stored time further in the future than this means the clock was moved back; it is then treated as expired.</summary>
    public static readonly TimeSpan ClockSkewTolerance = TimeSpan.FromMinutes(5);

    private static bool IsFuture(DateTime? stamp, DateTime now) => stamp is { } s && s - now > ClockSkewTolerance;

    /// <summary>True when an automatic check should run now.</summary>
    public static bool IsCheckDue(UpdateCheckState state, DateTime now)
    {
        var intervalElapsed = state.LastCheckedUtc is not { } last || IsFuture(last, now) || now - last >= CheckInterval;
        if (!intervalElapsed) return false;
        if (state.LastAttemptFailed && state.LastAttemptUtc is { } attempt && !IsFuture(attempt, now) && now - attempt < FailureBackoff)
            return false;
        return true;
    }

    /// <summary>True when the newest known stable version is newer than <paramref name="current"/>.</summary>
    public static bool IsUpdateAvailable(UpdateCheckState state, UpdateVersion current) =>
        state.LatestKnown is { } latest && latest > current;

    /// <summary>
    /// Whether the update prompt should be offered now (before looking at whether the screen is a good moment): an update is
    /// known, automatic checks and prompts are on, and this version has not been prompted within <see cref="PromptRepeatInterval"/>.
    /// </summary>
    public static bool ShouldPrompt(UpdateCheckState state, UpdateVersion current, bool checksEnabled, bool dontRemind, DateTime now)
    {
        if (!checksEnabled || dontRemind || !IsUpdateAvailable(state, current)) return false;
        if (UpdateVersion.TryParse(state.LastPromptedVersion, out var prompted) && state.LatestKnown is { } latest && prompted.SameAs(latest) &&
            state.LastPromptedUtc is { } promptedAt && !IsFuture(promptedAt, now) && now - promptedAt < PromptRepeatInterval)
            return false;
        return true;
    }

    /// <summary>The first reason the prompt must wait, or <see cref="UpdatePromptDeferral.None"/>.</summary>
    public static UpdatePromptDeferral Defer(bool hasActiveWindow, bool fullscreen, bool slideshowActive, bool modalOpen, bool typing)
    {
        if (!hasActiveWindow) return UpdatePromptDeferral.NoWindow;
        if (fullscreen) return UpdatePromptDeferral.Fullscreen;
        if (slideshowActive) return UpdatePromptDeferral.Slideshow;
        if (modalOpen) return UpdatePromptDeferral.ModalDialog;
        if (typing) return UpdatePromptDeferral.Typing;
        return UpdatePromptDeferral.None;
    }
}
