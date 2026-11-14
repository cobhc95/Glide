namespace Glide.Core;

/// <summary>How the status bar's collapse / expand button is drawn.</summary>
public enum StatusButtonLook
{
    /// <summary>The normal framed button.</summary>
    Full,
    /// <summary>Only a very small, unobtrusive arrow: no button face, border or shade.</summary>
    Arrow
}

/// <summary>An invisible, window-space rectangle (inclusive edges): the reveal area, or the bar box it is derived from. Only ever used to measure pointer positions.</summary>
public readonly record struct StatusRevealZone(double Left, double Top, double Right, double Bottom)
{
    public double Width => Right - Left;
    public double Height => Bottom - Top;
    public bool Contains(double x, double y) => x >= Left && x <= Right && y >= Top && y <= Bottom;
}

/// <summary>
/// Pure rules for the status bar's hover reveal (collapsed bar with "reveal on hover" and the "Auto-hide" mode) and the
/// collapsed expand button, so the window code only measures the pointer and applies the answer (and tests need no UI).
/// </summary>
public static class StatusBarAutoHide
{
    /// <summary>Default adjustment of the reveal area, in DIP: 0 means exactly the box the status bar occupies.</summary>
    public const int DefaultRevealZoneExpand = 0;
    public const int MinRevealZoneExpand = -40;
    public const int MaxRevealZoneExpand = 300;
    /// <summary>However far a negative adjustment shrinks the area, it never gets smaller than this.</summary>
    public const double MinRevealZoneHeight = 6;
    public const double MinRevealZoneWidth = 40;
    /// <summary>How long the pointer may stay outside the bar (plus its margin) before a revealed bar hides again.</summary>
    public const long HideDelayMs = 600;

    /// <summary>Clamps a saved / typed reveal-area adjustment to the supported range (-40..300 DIP).</summary>
    public static int NormalizeRevealZoneExpand(int configuredDip) => Math.Clamp(configuredDip, MinRevealZoneExpand, MaxRevealZoneExpand);

    /// <summary>
    /// The window-space box a status bar of the given layout rectangle occupies when it is shown, used when the layout could
    /// not be measured (a degenerate window): a one-row bar centred above the usual 14 DIP bottom margin.
    /// </summary>
    public static StatusRevealZone FallbackBarRect(double windowWidth, double windowHeight)
    {
        var width = Math.Max(0, windowWidth);
        var height = Math.Max(0, windowHeight);
        var barWidth = Math.Min(930, Math.Max(0, width - 28));
        var barHeight = Math.Min(48, height);
        var left = (width - barWidth) / 2;
        var bottom = Math.Max(0, height - 14);
        return new StatusRevealZone(left, Math.Max(0, bottom - barHeight), left + barWidth, bottom);
    }

    /// <summary>
    /// The invisible reveal area derived from the status bar's real layout box (<paramref name="bar"/>, window space, as laid out
    /// when the bar is shown: one thin strip in a wide window, a taller box when the bar wraps into several rows, and following
    /// its Left / Center / Right placement). With an adjustment of 0 the area is exactly that box. A positive adjustment grows it
    /// outward by that many DIP on every side; a negative one shrinks it inward (never below 6 DIP tall and 40 DIP wide, kept
    /// centred on the bar). The result is always clamped to the window.
    /// </summary>
    public static StatusRevealZone RevealZone(StatusRevealZone bar, double windowWidth, double windowHeight, int adjustDip)
    {
        var adjust = NormalizeRevealZoneExpand(adjustDip);
        var left = bar.Left - adjust;
        var top = bar.Top - adjust;
        var right = bar.Right + adjust;
        var bottom = bar.Bottom + adjust;
        if (adjust < 0)
        {
            var centerX = (bar.Left + bar.Right) / 2;
            var centerY = (bar.Top + bar.Bottom) / 2;
            if (right - left < MinRevealZoneWidth) { left = centerX - MinRevealZoneWidth / 2; right = centerX + MinRevealZoneWidth / 2; }
            if (bottom - top < MinRevealZoneHeight) { top = centerY - MinRevealZoneHeight / 2; bottom = centerY + MinRevealZoneHeight / 2; }
        }
        var width = Math.Max(0, windowWidth);
        var height = Math.Max(0, windowHeight);
        left = Math.Clamp(left, 0, width);
        right = Math.Clamp(right, left, width);
        top = Math.Clamp(top, 0, height);
        bottom = Math.Clamp(bottom, top, height);
        return new StatusRevealZone(left, top, right, bottom);
    }

    /// <summary>Whether the bar is drawn at all: always when auto-hide is off, otherwise only while revealed.</summary>
    public static bool BarVisible(bool autoHideEnabled, bool revealed) => !autoHideEnabled || revealed;

    /// <summary>
    /// The collapse / expand button. Collapsed with "Collapsed button in lower-right corner" OFF the button stays
    /// where the collapse button was and shows only a small arrow until the pointer is over it; every other
    /// state (expanded bar, corner mode) keeps the normal button.
    /// </summary>
    public static StatusButtonLook ExpandButtonLook(bool collapsed, bool collapsedToCorner, bool autoHideEnabled, bool pointerOverButton)
    {
        if (autoHideEnabled || !collapsed || collapsedToCorner) return StatusButtonLook.Full;
        return pointerOverButton ? StatusButtonLook.Full : StatusButtonLook.Arrow;
    }
}

/// <summary>
/// Show / hide hysteresis for the hover reveal. A hidden bar is revealed when the pointer enters the reveal area (the box the bar occupies, adjusted by the user setting); once
/// shown it stays while the pointer is over the bar plus a small margin (the "keep" region, so a grown reveal area does not make
/// the bar linger) and for a short delay after the pointer leaves it, so crossing the gap between the zone and the bar never
/// makes it flicker away. It also never hides while one of its menus is open (a ghost menu would be left behind), and nothing
/// changes while a mouse button is down (dragging the image or the window never reveals or hides it). After the bar hides
/// because the pointer lingered in the zone away from it, it is not revealed again until the pointer has left the zone.
/// </summary>
public sealed class StatusRevealTracker
{
    private bool _armed = true;
    private long? _outsideSince;

    /// <summary>True while a revealed bar is waiting out the hide delay (the caller should re-check after a short time).</summary>
    public bool HidePending => _outsideSince is not null;

    /// <summary>Returns whether the bar should be shown after this pointer update (always false when the reveal feature is off).</summary>
    public bool Update(bool enabled, bool currentlyRevealed, bool pointerInRevealZone, bool pointerOverKeepRegion,
        bool menuOpen, bool buttonsDown, long nowMs, bool hideImmediately = false)
    {
        if (!enabled) { _outsideSince = null; _armed = true; return false; }
        if (!currentlyRevealed)
        {
            _outsideSince = null;
            if (buttonsDown) return false;                  // dragging never triggers the reveal
            if (!pointerInRevealZone) { _armed = true; return false; }
            return _armed;                                  // entering the zone reveals (once armed)
        }
        if (buttonsDown || menuOpen || pointerOverKeepRegion) { _outsideSince = null; return true; }
        _outsideSince ??= nowMs;
        if (!hideImmediately && nowMs - _outsideSince.Value < StatusBarAutoHide.HideDelayMs) return true;
        _outsideSince = null;
        _armed = !pointerInRevealZone;                      // lingering in the zone: wait for the pointer to leave it first
        return false;
    }
}
