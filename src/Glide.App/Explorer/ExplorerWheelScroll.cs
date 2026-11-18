namespace Glide.App.Explorer;

/// <summary>Values of the "Explorer scroll amount" setting (explorer.scrollMode).</summary>
public static class ExplorerScrollModes
{
    public const string OneRow = "One row per notch";
    public const string FixedRows = "Rows per notch (fixed)";
    public const string PerView = "Per view (1 / 2 / 3)";
    public const string Pixels = "Pixels per notch";
    public static readonly string[] All = [OneRow, FixedRows, PerView, Pixels];
    public const string Default = OneRow;

    public static string Normalize(string? value) =>
        All.FirstOrDefault(x => string.Equals(x, value?.Trim(), StringComparison.OrdinalIgnoreCase)) ?? Default;
}

/// <summary>
/// Explorer-tab mouse-wheel scrolling (like XnViewMP, browsers and file managers), not by pages.
/// One notch (WHEEL_DELTA 120, which Avalonia reports as Delta.Y = 1) scrolls by the "Explorer scroll amount" mode:
/// one row (default), a fixed number of rows, 1 / 2 / 3 rows by view, or a fixed number of pixels. A row is the
/// view's row pitch. High-resolution and notchless wheels report fractional deltas and scroll proportionally.
/// The "Explorer scroll speed" setting multiplies every mode.
/// </summary>
internal static class ExplorerWheelScroll
{
    public const double MinSpeed = 0.25, MaxSpeed = 4.0, DefaultSpeed = 1.0;
    public const int MinRows = 1, MaxRows = 20, DefaultRows = 3;
    public const int MinPixels = 10, MaxPixels = 1000, DefaultPixels = 100;

    public static double ClampSpeed(double speed) =>
        double.IsFinite(speed) && speed > 0 ? Math.Clamp(speed, MinSpeed, MaxSpeed) : DefaultSpeed;

    public static int ClampRows(int rows) => Math.Clamp(rows, MinRows, MaxRows);
    public static int ClampPixels(int pixels) => Math.Clamp(pixels, MinPixels, MaxPixels);

    /// <summary>Rows scrolled by one wheel notch at 1x speed in the "Per view" mode.</summary>
    public static int RowsPerNotch(int viewMode) => viewMode switch
    {
        ExplorerViewModes.ExtraLarge or ExplorerViewModes.Large => 1,
        ExplorerViewModes.Medium or ExplorerViewModes.Small => 2,
        _ => 3
    };

    /// <summary>Distance from one row's top to the next (tile height plus the layout's row spacing).</summary>
    public static double RowPitch(int viewMode, double gridSpacing, int infoLines = 0)
    {
        var (_, height) = ExplorerViewModes.TileSize(viewMode, infoLines);
        return viewMode == ExplorerViewModes.Details ? height : height + gridSpacing;
    }

    /// <summary>Pixels one notch scrolls at 1x speed for the given mode.</summary>
    public static double PixelsPerNotch(int viewMode, double gridSpacing, string? mode, int rows, int pixels, int infoLines = 0) =>
        ExplorerScrollModes.Normalize(mode) switch
        {
            ExplorerScrollModes.FixedRows => ClampRows(rows) * RowPitch(viewMode, gridSpacing, infoLines),
            ExplorerScrollModes.PerView => RowsPerNotch(viewMode) * RowPitch(viewMode, gridSpacing, infoLines),
            ExplorerScrollModes.Pixels => ClampPixels(pixels),
            _ => RowPitch(viewMode, gridSpacing, infoLines)
        };

    /// <summary>
    /// Pixels to scroll for a wheel delta (positive Delta.Y = wheel away from the user = scroll up, so the
    /// returned offset change is negative). <paramref name="deltaY"/> is in notches (1.0 = 120 units).
    /// </summary>
    public static double OffsetChange(int viewMode, double deltaY, double speed, double gridSpacing,
        string? mode = ExplorerScrollModes.Default, int rows = DefaultRows, int pixels = DefaultPixels, int infoLines = 0) =>
        -deltaY * PixelsPerNotch(viewMode, gridSpacing, mode, rows, pixels, infoLines) * ClampSpeed(speed);
}
