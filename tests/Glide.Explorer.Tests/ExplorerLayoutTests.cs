using Glide.App.Explorer;

namespace Glide.Explorer.Tests;

[Trait("Area", "Explorer")]
public sealed class ExplorerLayoutTests
{
    [Fact]
    public void Columns_fit_the_width_and_details_is_one_column()
    {
        var (w, _) = ExplorerViewModes.TileSize(ExplorerViewModes.Medium);
        Assert.Equal(1, ExplorerLayout.ColumnCount(ExplorerViewModes.Details, 5000));
        Assert.Equal(1, ExplorerLayout.ColumnCount(ExplorerViewModes.Medium, 1));
        Assert.Equal(3, ExplorerLayout.ColumnCount(ExplorerViewModes.Medium, 3 * w + 2 * ExplorerLayout.GridSpacing));
        Assert.Equal(2, ExplorerLayout.ColumnCount(ExplorerViewModes.Medium, 3 * w + 2 * ExplorerLayout.GridSpacing - 1));
    }

    [Fact]
    public void Tile_rect_is_row_major_with_spacing()
    {
        var (w, h) = ExplorerViewModes.TileSize(ExplorerViewModes.Large);
        var s = ExplorerLayout.GridSpacing;
        Assert.Equal((w + s, h + s, w, h), ExplorerLayout.TileRect(ExplorerViewModes.Large, 4, 3, 0));
        var (_, dh) = ExplorerViewModes.TileSize(ExplorerViewModes.Details);
        Assert.Equal((0, 7 * dh, 640, dh), ExplorerLayout.TileRect(ExplorerViewModes.Details, 7, 9, 640));
    }

    [Fact]
    public void Scroll_to_show_moves_only_as_far_as_needed()
    {
        Assert.Equal(100, ExplorerLayout.ScrollToShow(150, 200, 100, 300));          // already visible
        Assert.Equal(40, ExplorerLayout.ScrollToShow(40, 90, 100, 300));              // above: tile top
        Assert.Equal(0, ExplorerLayout.ScrollToShow(-20, 30, 100, 300));
        Assert.Equal(520 + 10 - 300 + 10, ExplorerLayout.ScrollToShow(470, 520, 100, 300)); // below
        Assert.Equal(100, ExplorerLayout.ScrollToShow(470, 520, 100, 0));             // no viewport yet
    }

    [Fact]
    public void Status_texts()
    {
        Assert.Equal("1 item", ExplorerLayout.ItemCountText(1));
        Assert.Equal("0 items", ExplorerLayout.ItemCountText(0));
        Assert.Equal("", ExplorerLayout.SelectionText([]));
        var dir = new ExplorerEntry(Path.Combine(Path.GetTempPath(), "d"), true, 0, DateTime.Now);
        var file = new ExplorerEntry(Path.Combine(Path.GetTempPath(), "a.jpg"), false, 2048, DateTime.Now);
        Assert.Equal("1 item selected   " + ExplorerModel.FormatSize(2048), ExplorerLayout.SelectionText([file]));
        Assert.Equal("2 items selected   " + ExplorerModel.FormatSize(2048), ExplorerLayout.SelectionText([file, dir]));
        Assert.Equal("1 item selected", ExplorerLayout.SelectionText([dir]));
        Assert.Null(ExplorerLayout.EmptyText(3, true, false, false));
        Assert.Null(ExplorerLayout.EmptyText(0, false, false, false));
        Assert.Equal("No items match your filter.", ExplorerLayout.EmptyText(0, true, true, true));
        Assert.Equal("No folders or images in this folder.", ExplorerLayout.EmptyText(0, true, false, true));
        Assert.Equal("This folder is empty.", ExplorerLayout.EmptyText(0, true, false, false));
    }
}
