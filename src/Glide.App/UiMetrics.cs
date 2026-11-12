namespace Glide.App;

/// <summary>
/// Single source for window/tab/dialog metrics, z-order ladder and shared print/quality defaults.
/// Replaces magic numbers scattered across <c>MainWindow</c>, settings and dialogs (4.5.2 audit).
/// </summary>
public static class UiMetrics
{
    public const double WindowMinWidth = 180;
    public const double WindowMinHeight = 360;
    public const double DialogMinWidth = 180;
    public const double DialogMaxWidth = 560;
    public const int TabMinWidth = 80;
    public const int TabMaxWidth = 240;
    public const int BrowseCacheLength = 1;
    public const int MaxOverlayPlacements = 128;
    public const double DefaultZoomStep = 1.15;
    public const int StatusDebounceMs = 80;
    /// <summary>Minimum selection buffer (view DIP) on each side of the image while the buffer is
    /// enabled; the user-configured percentage of the empty space can only make it larger.</summary>
    public const double SelectionEdgeMargin = 24.0;

    public static class ZIndex
    {
        public const int OverlayArea = 50;
        public const int Dialog = 400;
        public const int Toast = 450;
        public const int BrowserChrome = 200;
        public const int TabDragPreview = 320;
        public const int TopmostOverlay = 1000;
        public const int DetachedDrag = 1600;
    }

    public static class PrintDefaults
    {
        public const float JpegQuality = 0.92f;
        public const int PreviewLongestSide = 1800;
        public const int RefineLongestSide = 2400;
        public const int DpiFallback = 96;
    }
}
