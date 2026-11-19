namespace Glide.Core;

/// <summary>
/// Headless idle cursor auto-hide policy (fullscreen, and the normal window's picture area). Hides after
/// <see cref="HideDelay"/> without movement; any real movement (beyond <see cref="JitterDistance"/>), click or wheel
/// shows it again at once. Suppressed (never hidden) while the pointer is over chrome, a button is held (drag), a text
/// box has focus or a popup is open. Time is injected for testing.
/// </summary>
public sealed class FullscreenCursorAutoHide
{
    public static readonly TimeSpan DefaultHideDelay = TimeSpan.FromSeconds(2);
    /// <summary>Per-event movement (DIPs) below this is treated as sensor jitter.</summary>
    public const double JitterDistance = 2.0;
    /// <summary>Suggested polling interval for the UI timer that calls <see cref="Tick"/>.</summary>
    public static readonly TimeSpan TickInterval = TimeSpan.FromMilliseconds(200);

    private bool _active;
    private bool _suppressed;
    private bool _popupOpen;
    private DateTime _lastActivity;
    private (double X, double Y)? _lastPos;

    /// <summary>Idle time before hiding (the shared "Hide cursor after (s)" setting).</summary>
    public TimeSpan HideDelay { get; set; } = DefaultHideDelay;

    public bool IsHidden { get; private set; }
    public bool IsActive => _active;

    /// <summary>Seconds setting clamped to 0.5..30 and rounded to 0.5 s steps (NaN: default 2 s).</summary>
    public static double ClampDelaySeconds(double seconds) =>
        double.IsFinite(seconds) ? Math.Clamp(Math.Round(seconds * 2, MidpointRounding.AwayFromZero) / 2, 0.5, 30) : 2;

    /// <summary>Seconds setting to a clamped delay (0.5..30 s, 0.5 s steps).</summary>
    public static TimeSpan DelayFromSeconds(double seconds) => TimeSpan.FromSeconds(ClampDelaySeconds(seconds));

    /// <summary>"Always while over the picture": hide without waiting (still never while suppressed or a popup is open).</summary>
    public bool AlwaysHide { get; set; }

    /// <summary>Feature enabled for the current window mode: cursor visible, countdown starts.</summary>
    public void Activate(DateTime now)
    {
        _active = true;
        IsHidden = false;
        _lastActivity = now;
    }

    /// <summary>Leave the mode, lose focus or disable: cursor visible, policy idle.</summary>
    public void Deactivate()
    {
        _active = false;
        IsHidden = false;
        _lastPos = null;
    }

    /// <summary>Pointer over chrome / outside the picture, a drag, or a text box has focus: keep visible.</summary>
    public bool SetSuppressed(bool suppressed, DateTime now)
    {
        if (_suppressed == suppressed) return false;
        _suppressed = suppressed;
        _lastActivity = now;
        if (suppressed && IsHidden) { IsHidden = false; return true; }
        return false;
    }

    /// <summary>
    /// A popup (context menu, sub-menu, drop-down, flyout) is open. Popups are separate top-level windows, so
    /// the main window receives no pointer events while the pointer is inside one. While open the cursor is shown
    /// and never hidden; closing restarts the idle countdown. Returns true when visibility changed.
    /// </summary>
    public bool SetPopupOpen(bool open, DateTime now)
    {
        if (_popupOpen == open) return false;
        _popupOpen = open;
        _lastActivity = now;
        if (open && IsHidden) { IsHidden = false; return true; }
        return false;
    }

    /// <summary>True while a popup holds the cursor visible.</summary>
    public bool IsPopupOpen => _popupOpen;

    /// <summary>Pointer motion. Real movement shows the cursor at once and restarts the countdown; jitter is ignored. Returns true when visibility changed.</summary>
    public bool OnPointerMoved(DateTime now, double x, double y)
    {
        if (!_active) return false;
        var prev = _lastPos;
        _lastPos = (x, y);
        if (prev is { } p && Math.Abs(x - p.X) < JitterDistance && Math.Abs(y - p.Y) < JitterDistance) return false;
        return OnPointerActivity(now);
    }

    /// <summary>Click or wheel: show at once and restart the countdown. Returns true when visibility changed.</summary>
    public bool OnPointerActivity(DateTime now)
    {
        if (!_active) return false;
        _lastActivity = now;
        if (AlwaysHide || !IsHidden) return false;
        IsHidden = false;
        return true;
    }

    /// <summary>Periodic check; returns true when visibility changed.</summary>
    public bool Tick(DateTime now)
    {
        if (!_active || _suppressed || _popupOpen || IsHidden) return false;
        if (AlwaysHide || now - _lastActivity >= HideDelay)
        {
            IsHidden = true;
            return true;
        }
        return false;
    }
}
