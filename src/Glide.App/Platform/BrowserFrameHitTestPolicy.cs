using Avalonia;

namespace Glide.App.Platform;

/// <summary>Window state as the native frame sees it (Maximized is the HWND's IsZoomed truth, not Avalonia's cache).</summary>
internal enum FrameWindowMode { Normal, Maximized, FullScreen }

/// <summary>Everything WM_NCHITTEST needs, in physical screen pixels. Null rectangles are hidden / not laid out.</summary>
internal readonly record struct FrameHitTestInput(
    FrameWindowMode Mode,
    PixelRect Window,
    int MonitorTop,
    double Scaling,
    PixelRect? ProtectedClient = null,
    PixelRect? Close = null,
    PixelRect? Maximize = null,
    PixelRect? Minimize = null);

/// <summary>
/// <see cref="Code"/> is the HT* value to return, or <see cref="BrowserFrameHitTestPolicy.Defer"/> to let the previous
/// window procedure (Avalonia, then DefWindowProc, which answers HTCLIENT for Glide's zero-size non-client area)
/// decide. <see cref="Reason"/> names the rule that won; it is only used by the diagnostic trace.
/// </summary>
internal readonly record struct FrameHitTestResult(int Code, string Reason);

/// <summary>
/// The pure WM_NCHITTEST decision of <see cref="WindowsBrowserFrameController"/>, factored out so every rule can be
/// unit-tested with injected rectangles and DPI scaling (the controller only gathers the native inputs).
///
/// Order (first match wins):
/// <list type="number">
/// <item>Normal state: resize edges. The outermost physical top pixel always resizes (Firefox / Chromium); below it,
/// the top resize band yields to interactive title-bar controls so their top pixels click instead of resizing.</item>
/// <item>Fullscreen: the absolute top-right corner is HTCLOSE, everything else HTCLIENT.</item>
/// <item>The title-action strip is always client (a stale caption rectangle must never turn Home into HTCLOSE).</item>
/// <item>Minimize / maximize / close rectangles report native HT* codes (Windows 11 Snap Layouts).</item>
/// <item>Interactive controls in the top band (the "+", tabs) are HTCLIENT.</item>
/// <item>Otherwise defer.</item>
/// </list>
/// </summary>
internal static class BrowserFrameHitTestPolicy
{
    public const int Defer = 0;
    public const int HtClient = 1;
    public const int HtMinButton = 8;
    public const int HtMaxButton = 9;
    public const int HtLeft = 10;
    public const int HtRight = 11;
    public const int HtTop = 12;
    public const int HtTopLeft = 13;
    public const int HtTopRight = 14;
    public const int HtBottom = 15;
    public const int HtBottomLeft = 16;
    public const int HtBottomRight = 17;
    public const int HtClose = 20;

    /// <summary>Physical pixels at the very top of a Normal window that always resize, even above a control.</summary>
    public const int TopPriorityPixels = 1;

    public static int ResizeEdge(double scaling) => Math.Max(5, (int)Math.Round(6 * Math.Max(1.0, scaling)));
    public static int ResizeCorner(double scaling) => Math.Max(12, (int)Math.Round(16 * Math.Max(1.0, scaling)));
    public static int TopBand(double scaling) => Math.Max(12, (int)Math.Ceiling(16 * Math.Max(1.0, scaling)));
    public static int FullscreenCloseWidth(double scaling) => Math.Max(46, (int)Math.Ceiling(46 * Math.Max(1.0, scaling)));
    public static int FullscreenCloseHeight(double scaling) => Math.Max(44, (int)Math.Ceiling(44 * Math.Max(1.0, scaling)));

    /// <summary>
    /// The physical-pixel rectangle of a control whose top-left maps to <paramref name="origin"/> (PointToScreen) and
    /// whose DIP size is <paramref name="size"/>. Ceiling keeps the far edge pixel that the control partially covers.
    /// </summary>
    public static PixelRect? ScreenRect(PixelPoint origin, Size size, double scaling)
    {
        if (size.Width <= 0 || size.Height <= 0) return null;
        var scale = Math.Max(0.1, scaling);
        return new PixelRect(origin.X, origin.Y, (int)Math.Ceiling(size.Width * scale), (int)Math.Ceiling(size.Height * scale));
    }

    public static bool Contains(PixelRect? rect, int x, int y) =>
        rect is { } r && x >= r.X && x < r.X + r.Width && y >= r.Y && y < r.Y + r.Height;

    /// <summary>True when <paramref name="y"/> is in the band where title-bar controls touch the top edge.</summary>
    public static bool IsInTopBand(in FrameHitTestInput input, int y)
    {
        // A maximized HWND can start above its monitor (by the frame thickness): measure from the lower of the two.
        var top = input.Mode == FrameWindowMode.Maximized ? Math.Max(input.Window.Y, input.MonitorTop) : input.Window.Y;
        return y >= input.Window.Y && y < top + TopBand(input.Scaling);
    }

    public static FrameHitTestResult Decide(in FrameHitTestInput input, int x, int y, Func<int, int, bool> interactiveAt)
    {
        // Evaluate the (comparatively expensive) Avalonia hit test at most once per message, and only when needed.
        var probe = new InteractiveProbe(interactiveAt, x, y);

        if (input.Mode == FrameWindowMode.Normal)
        {
            var resize = ResizeBorder(input, x, y, ref probe);
            if (resize != HtClient) return new FrameHitTestResult(resize, "resize");
        }

        if (input.Mode == FrameWindowMode.FullScreen)
        {
            var r = input.Window;
            var w = FullscreenCloseWidth(input.Scaling);
            var h = FullscreenCloseHeight(input.Scaling);
            if (x >= r.Right - w && x < r.Right && y >= r.Y && y < r.Y + h) return new FrameHitTestResult(HtClose, "fullscreen_corner_close");
            return new FrameHitTestResult(HtClient, "fullscreen_client");
        }

        if (Contains(input.ProtectedClient, x, y)) return new FrameHitTestResult(HtClient, "title_actions");
        if (Contains(input.Close, x, y)) return new FrameHitTestResult(HtClose, "caption_close");
        if (Contains(input.Maximize, x, y)) return new FrameHitTestResult(HtMaxButton, "caption_maximize");
        if (Contains(input.Minimize, x, y)) return new FrameHitTestResult(HtMinButton, "caption_minimize");

        if (IsInTopBand(input, y) && probe.Get()) return new FrameHitTestResult(HtClient, "top_band_control");
        return new FrameHitTestResult(Defer, "defer");
    }

    private static int ResizeBorder(in FrameHitTestInput input, int x, int y, ref InteractiveProbe interactive)
    {
        var rect = input.Window;
        var edge = ResizeEdge(input.Scaling);
        var corner = ResizeCorner(input.Scaling);

        var onLeft = x >= rect.X && x < rect.X + edge;
        var onRight = x < rect.Right && x >= rect.Right - edge;
        var onTop = y >= rect.Y && y < rect.Y + edge;
        var onBottom = y < rect.Bottom && y >= rect.Bottom - edge;

        // Firefox/Chromium keep the outermost edge resizable even when tab/title content paints to the top.
        if (y >= rect.Y && y < rect.Y + TopPriorityPixels && x >= rect.X && x < rect.Right)
        {
            if (x < rect.X + corner) return HtTopLeft;
            if (x >= rect.Right - corner) return HtTopRight;
            return HtTop;
        }
        // Over an interactive title-bar control only the outermost pixel resizes; the rest of the band belongs to the
        // control, otherwise its top pixels look hovered but a press there starts a native resize.
        if (onTop && !onLeft && !onRight && interactive.Get()) onTop = false;
        if (onTop && onLeft) return HtTopLeft;
        if (onTop && onRight) return HtTopRight;
        if (onBottom && onLeft) return HtBottomLeft;
        if (onBottom && onRight) return HtBottomRight;
        if (onLeft) return HtLeft;
        if (onRight) return HtRight;
        if (onTop) return HtTop;
        if (onBottom) return HtBottom;
        return HtClient;
    }

    /// <summary>Allocation-free memo of the interactive-control probe (WM_NCHITTEST runs on every mouse move).</summary>
    private struct InteractiveProbe(Func<int, int, bool> probe, int x, int y)
    {
        private bool _known;
        private bool _value;

        public bool Get()
        {
            if (_known) return _value;
            _known = true;
            try { _value = probe(x, y); } catch { _value = false; }
            return _value;
        }
    }
}
