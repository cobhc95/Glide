using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;
using Glide.App.Explorer;
using Glide.Core.Settings;

namespace Glide.Explorer.Tests;

/// <summary>Round 3 #6: optional date / resolution / file size lines under Explorer thumbnails.</summary>
[Collection(ExplorerHeadlessCollection.Name)]
[Trait("Area", "Explorer")]
public sealed class ExplorerTileInfoTests
{
    private static List<string?> InfoTexts(Control tile) =>
        tile.GetVisualDescendants().OfType<TextBlock>().Where(t => t.Classes.Contains("exInfo")).Select(t => t.Text).ToList();

    [Fact]
    public void Each_info_line_adds_one_line_to_icon_tiles_only()
    {
        foreach (var mode in new[] { ExplorerViewModes.ExtraLarge, ExplorerViewModes.Large, ExplorerViewModes.Medium })
        {
            var (w0, h0) = ExplorerViewModes.TileSize(mode);
            var (w3, h3) = ExplorerViewModes.TileSize(mode, 3);
            Assert.Equal(w0, w3);
            Assert.Equal(h0 + 3 * ExplorerViewModes.InfoLineHeight, h3, 6);
            // The top band stays as thick as the side bands.
            Assert.Equal((w0 - ExplorerViewModes.IconTileChrome - ExplorerViewModes.PreviewSize(mode).Width) / 2, ExplorerViewModes.IconTopInset(mode));
        }
        var (_, h) = ExplorerViewModes.TileSize(ExplorerViewModes.Large, 2);
        var s = ExplorerLayout.GridSpacing;
        Assert.Equal(h + s, ExplorerLayout.TileRect(ExplorerViewModes.Large, 3, 3, 0, 2).Y);
        Assert.Equal(h + s, ExplorerWheelScroll.RowPitch(ExplorerViewModes.Large, s, 2));
        Assert.Equal(0, new ExplorerTileInfo().Lines);
        Assert.Equal(2, new ExplorerTileInfo(true, false, true).Lines);
    }

    [Fact]
    public void The_settings_are_off_by_default_and_catalogued_for_search()
    {
        var state = new GlideSettingsState();
        Assert.False(state.ExplorerInfoDate || state.ExplorerInfoResolution || state.ExplorerInfoFileSize);
        foreach (var (id, term) in new[] { ("explorer.infoDate", "date"), ("explorer.infoResolution", "resolution"), ("explorer.infoFileSize", "file size") })
        {
            var setting = SettingsCatalog.All.Single(x => x.Id == id);
            Assert.Equal(SettingKind.Toggle, setting.Kind);
            Assert.Equal(false, setting.DefaultValue);
            Assert.Contains(term, setting.SearchTerms);
            Assert.Contains("under thumbnails", setting.SearchTerms);
        }
    }

    [Fact]
    public async Task Off_by_default_tiles_show_no_info()
    {
        await ExplorerHarness.Run(async h =>
        {
            await h.SettleAsync();
            Assert.Empty(InfoTexts(h.Tile("a.png")!));
            Assert.Equal(ExplorerViewModes.TileSize(ExplorerViewModes.Large).Height, h.Tile("a.png")!.Bounds.Height);
        });
    }

    [Fact]
    public async Task Chosen_info_shows_under_the_caption_inside_the_selection_fill_and_the_tiles_grow()
    {
        await ExplorerHarness.Run(async h =>
        {
            h.Settings.ExplorerInfoDate = true;
            h.Settings.ExplorerInfoResolution = true;
            h.Settings.ExplorerInfoFileSize = true;
            h.Call("ApplyExplorerTileInfo");
            await h.SettleAsync();
            var tile = h.Tile("a.png")!;
            await h.WaitUntil(() => InfoTexts(h.Tile("a.png")!).Contains("48 × 32"), "resolution line filled in");
            tile = h.Tile("a.png")!;
            var info = new FileInfo(Path.Combine(h.Root, "a.png"));
            var texts = InfoTexts(tile);
            Assert.Equal(new[] { ExplorerModel.FormatDate(info.LastWriteTime), "48 × 32", ExplorerModel.FormatSize(info.Length) }, texts);
            Assert.Equal(ExplorerViewModes.TileSize(ExplorerViewModes.Large, 3).Height, tile.Bounds.Height);

            // Every info line lies inside the selection fill (which spans the tile), below the caption.
            h.Click("a.png");
            await h.SettleAsync();
            tile = h.Tile("a.png")!;
            var fill = tile.GetVisualDescendants().OfType<Border>().Single(b => b.Name == "SelectionFill");
            var fillRect = new Rect(fill.TranslatePoint(new Point(0, 0), tile)!.Value, fill.Bounds.Size);
            var caption = tile.GetVisualDescendants().OfType<Panel>().Single(p => p.Name == "NameHost");
            var captionBottom = caption.TranslatePoint(new Point(0, caption.Bounds.Height), tile)!.Value.Y;
            foreach (var line in tile.GetVisualDescendants().OfType<TextBlock>().Where(t => t.Classes.Contains("exInfo")))
            {
                var r = new Rect(line.TranslatePoint(new Point(0, 0), tile)!.Value, line.Bounds.Size);
                Assert.True(fillRect.Contains(r), $"info line {r} outside the fill {fillRect}");
                Assert.True(r.Y >= captionBottom - 0.5, $"info line {r} overlaps the caption (bottom {captionBottom})");
            }

            // A folder keeps the same height; its resolution and size lines stay empty.
            var folder = h.Tile("Alpha")!;
            Assert.Equal(tile.Bounds.Height, folder.Bounds.Height);
            Assert.Equal(3, InfoTexts(folder).Count);
            Assert.Equal("", InfoTexts(folder)[1]);
            Assert.Equal("", InfoTexts(folder)[2]);

            // Layout math (keyboard / rubber band) stays in step with the taller tiles.
            foreach (var name in new[] { "Alpha", "b.png", "c.png" })
            {
                var index = h.Names.ToList().IndexOf(name);
                var rect = (Rect)h.Call("ExplorerTileRect", index)!;
                var real = h.Tile(name)!.TranslatePoint(new Point(0, 0), h.Surface.Repeater)!.Value;
                Assert.True(Math.Abs(rect.Y - real.Y) < 1.5 && Math.Abs(rect.X - real.X) < 1.5, $"{name}: math {rect.TopLeft} vs real {real}");
            }

            // Only the chosen lines; List view never shows them.
            h.Settings.ExplorerInfoDate = false;
            h.Settings.ExplorerInfoResolution = false;
            h.Call("ApplyExplorerTileInfo");
            await h.SettleAsync();
            Assert.Equal(new[] { ExplorerModel.FormatSize(info.Length) }, InfoTexts(h.Tile("a.png")!));
            Assert.Equal(ExplorerViewModes.TileSize(ExplorerViewModes.Large, 1).Height, h.Tile("a.png")!.Bounds.Height);
            h.Surface.ViewModeCombo.SelectedIndex = ExplorerViewModes.List;
            await h.SettleAsync();
            Assert.Empty(InfoTexts(h.Tile("a.png")!));
            Assert.Equal(ExplorerViewModes.TileSize(ExplorerViewModes.List).Height, h.Tile("a.png")!.Bounds.Height);
        });
    }
}
