namespace Glide.App.Explorer;

/// <summary>
/// Stage D step 5: the Explorer tab's layout and status-line decisions, moved out of <c>MainWindow.Browser.cs</c>.
/// Pure (numbers and entries in, numbers and strings out): MainWindow keeps the controls and applies the results.
/// Used by keyboard navigation, rubber-band selection and scroll-into-view.
/// </summary>
internal static class ExplorerLayout
{
    public const double GridSpacing = 6;
    public const double ScrollPadding = 10;

    /// <summary>Columns of tiles that fit in <paramref name="availableWidth"/> (Details is always one column).</summary>
    public static int ColumnCount(int viewMode, double availableWidth)
    {
        if (viewMode == ExplorerViewModes.Details) return 1;
        var (width, _) = ExplorerViewModes.TileSize(viewMode);
        return Math.Max(1, (int)Math.Floor((availableWidth + GridSpacing) / (width + GridSpacing)));
    }

    /// <summary>Tile rectangle (x, y, width, height) in ItemsRepeater coordinates.</summary>
    /// <summary><paramref name="infoLines"/>: extra info lines under thumbnails (round 3 #6), which make icon tiles taller.</summary>
    public static (double X, double Y, double Width, double Height) TileRect(int viewMode, int index, int columns, double detailsRowWidth, int infoLines = 0)
    {
        var (width, height) = ExplorerViewModes.TileSize(viewMode, infoLines);
        if (viewMode == ExplorerViewModes.Details) return (0, index * height, detailsRowWidth, height);
        columns = Math.Max(1, columns);
        var row = index / columns;
        var column = index % columns;
        return (column * (width + GridSpacing), row * (height + GridSpacing), width, height);
    }

    /// <summary>The vertical scroll offset that brings a tile spanning [top, bottom) into view, or the current one.</summary>
    public static double ScrollToShow(double tileTop, double tileBottom, double offset, double viewport)
    {
        if (viewport <= 0) return offset;
        var top = tileTop + ScrollPadding;
        var bottom = tileBottom + ScrollPadding;
        if (top < offset) return Math.Max(0, top - ScrollPadding);
        if (bottom > offset + viewport) return bottom - viewport + ScrollPadding;
        return offset;
    }

    /// <summary>Drive bar (navigation pane) width limits in pixels (round 3 #12; Settings: explorer.navPaneWidth).</summary>
    public const int NavPaneMinWidth = 120, NavPaneMaxWidth = 600, NavPaneDefaultWidth = 208;

    /// <summary>A drive bar width within the limits; 0 or less (unset / corrupt) gives the default.</summary>
    public static int ClampNavPaneWidth(double width) =>
        !double.IsFinite(width) || width <= 0 ? NavPaneDefaultWidth : (int)Math.Round(Math.Clamp(width, NavPaneMinWidth, NavPaneMaxWidth));

    /// <summary>"1 item" / "1,234 items".</summary>
    public static string ItemCountText(int count) => count == 1 ? "1 item" : $"{count:N0} items";

    /// <summary>"3 items selected   1.2 MB" (total size of the selected files; folders add no size, as in Windows Explorer's file total); empty when nothing is selected.</summary>
    public static string SelectionText(IReadOnlyCollection<ExplorerEntry> selected)
    {
        if (selected.Count == 0) return "";
        var files = selected.Where(e => !e.IsDirectory).ToList();
        var size = files.Count > 0 ? "   " + ExplorerModel.FormatSize(files.Sum(f => f.Size)) : "";
        return (selected.Count == 1 ? "1 item selected" : $"{selected.Count:N0} items selected") + size;
    }

    /// <summary>The empty-folder message, or null while the listing has items or has not loaded.</summary>
    public static string? EmptyText(int count, bool loaded, bool filtered, bool onlyImages) =>
        count != 0 || !loaded ? null
        : filtered ? "No items match your filter."
        : onlyImages ? "No folders or images in this folder." : "This folder is empty.";
}
