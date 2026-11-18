namespace Glide.Core.Status;

/// <summary>Everything the status-bar visibility decision depends on, as one value (Stage D, C9).</summary>
public readonly record struct StatusChromeInputs(
    bool WholeAppOverlay,
    bool ShowStatusSurface,
    bool AutoHide,
    bool Collapsed,
    bool SessionClosed,
    bool Fullscreen,
    bool FullscreenAlwaysOn,
    bool HoverRevealed,
    bool ImageVisible,
    bool HomeVisible);

/// <summary>Which status surfaces are visible.</summary>
public readonly record struct StatusChromeVisibility(bool ViewerSurface, bool HomeSurface, bool RestoreButton);

/// <summary>
/// Stage D (C9): the pure status-bar visibility rules formerly inlined in <c>MainWindow.ApplyStatusVisibility</c>.
/// Windowed collapse keeps the bar present but collapsed to its own button; fullscreen suppresses the bar and leaves
/// the corner restore button instead; Auto-hide replaces collapsing and hides the bar until hover reveals it.
/// </summary>
public static class StatusChromePolicy
{
    /// <summary>Auto-hide replaces collapsing: the collapse flag is honoured only when Auto-hide is off.</summary>
    public static bool CollapsedNow(bool collapsed, bool autoHide) => collapsed && !autoHide;

    public static StatusChromeVisibility Decide(in StatusChromeInputs s)
    {
        if (s.WholeAppOverlay) return default;
        var collapsedNow = CollapsedNow(s.Collapsed, s.AutoHide);
        var allowed = s.ShowStatusSurface && (!s.Fullscreen || s.FullscreenAlwaysOn || s.AutoHide);
        var expanded = allowed && !collapsedNow && !s.SessionClosed;
        var collapsedOnly = s.ShowStatusSurface && collapsedNow && !s.SessionClosed && !s.Fullscreen;
        var barVisible = StatusBarAutoHide.BarVisible(s.AutoHide, s.HoverRevealed);
        var present = (expanded || collapsedOnly) && barVisible;
        return new StatusChromeVisibility(
            ViewerSurface: present && s.ImageVisible,
            HomeSurface: present && s.HomeVisible && (!s.Fullscreen || s.AutoHide),
            RestoreButton: s.ShowStatusSurface && s.Fullscreen && !s.AutoHide &&
                (collapsedNow || !s.FullscreenAlwaysOn) && (s.ImageVisible || s.HomeVisible));
    }

    /// <summary>"Only the collapse/expand button remains": compact (parked in the corner) or ghost (laid out, transparent).</summary>
    public static (bool CollapsedOnly, bool Compact, bool Ghost) CollapsedLook(
        bool previewReveal, bool showStatusSurface, bool collapsedNow, bool hoverRevealed, bool sessionClosed,
        bool fullscreen, bool wholeAppOverlay, bool collapsedToCorner)
    {
        var collapsedOnly = !previewReveal && showStatusSurface && collapsedNow && !hoverRevealed &&
            !sessionClosed && !fullscreen && !wholeAppOverlay;
        return (collapsedOnly, collapsedOnly && collapsedToCorner, collapsedOnly && !collapsedToCorner);
    }
}
