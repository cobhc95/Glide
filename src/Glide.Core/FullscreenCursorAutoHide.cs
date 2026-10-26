namespace Glide.Core;

/// <summary>
/// Headless fullscreen cursor auto-hide policy. Hides after <see cref="HideDelay"/> without
/// movement; once hidden, only sustained movement (<see cref="RevealSustain"/> of continuous
/// motion, no gap longer than <see cref="MaxMovementGap"/>) reveals it again, so bumps and
/// jitter do not. Clicks/keys are deliberately not inputs. Time is injected for testing.
/// </summary>
public sealed class FullscreenCursorAutoHide
{
    public static readonly TimeSpan HideDelay = TimeSpan.FromSeconds(3);
    public static readonly TimeSpan RevealSustain = TimeSpan.FromSeconds(1);
    public static readonly TimeSpan MaxMovementGap = TimeSpan.FromMilliseconds(250);
    /// <summary>Per-event movement (DIPs) below this is treated as sensor jitter.</summary>
    public const double JitterDistance = 2.0;
    /// <summary>Suggested polling interval for the UI timer that calls <see cref="Tick"/>.</summary>
    public static readonly TimeSpan TickInterval = TimeSpan.FromMilliseconds(200);

    private bool _active;
    private bool _suppressed;
    private DateTime _lastActivity;
    private DateTime? _streakStart;
    private DateTime _lastStreakMove;
    private (double X, double Y)? _lastPos;

    public bool IsHidden { get; private set; }
    public bool IsActive => _active;

    /// <summary>Enter fullscreen (with the feature enabled): cursor visible, countdown starts.</summary>
    public void Activate(DateTime now)
    {
        _active = true;
        IsHidden = false;
        _lastActivity = now;
        _streakStart = null;
    }

    /// <summary>Leave fullscreen, lose focus or disable: cursor visible, policy idle.</summary>
    public void Deactivate()
    {
        _active = false;
        IsHidden = false;
        _streakStart = null;
        _lastPos = null;
    }

    /// <summary>Pointer over an overlay/control, or a menu/dialog is open: keep visible.</summary>
    public bool SetSuppressed(bool suppressed, DateTime now)
    {
        if (_suppressed == suppressed) return false;
        _suppressed = suppressed;
        _lastActivity = now;
        _streakStart = null;
        if (suppressed && IsHidden) { IsHidden = false; return true; }
        return false;
    }

    /// <summary>Returns true when visibility changed.</summary>
    public bool OnPointerMoved(DateTime now, double x, double y)
    {
        if (!_active) return false;
        var prev = _lastPos;
        _lastPos = (x, y);
        if (prev is { } p && Math.Abs(x - p.X) < JitterDistance && Math.Abs(y - p.Y) < JitterDistance) return false;
        if (prev is null) return false; // first sample establishes a baseline only

        if (!IsHidden)
        {
            _lastActivity = now;
            return false;
        }
        if (_streakStart is null || now - _lastStreakMove > MaxMovementGap) _streakStart = now;
        _lastStreakMove = now;
        if (now - _streakStart.Value >= RevealSustain)
        {
            IsHidden = false;
            _streakStart = null;
            _lastActivity = now;
            return true;
        }
        return false;
    }

    /// <summary>Periodic check; returns true when visibility changed.</summary>
    public bool Tick(DateTime now)
    {
        if (!_active || _suppressed) return false;
        if (IsHidden)
        {
            if (_streakStart is not null && now - _lastStreakMove > MaxMovementGap) _streakStart = null;
            return false;
        }
        if (now - _lastActivity >= HideDelay)
        {
            IsHidden = true;
            _streakStart = null;
            return true;
        }
        return false;
    }
}
