using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.VisualTree;
using Glide.App.Explorer;

namespace Glide.Explorer.Tests;

/// <summary>
/// Icon-mode captions: a wrapped file name must stay inside its tile and its selection fill in every state.
/// Before 5.0.7 the tile heights left room for only ~1.5 caption lines, so the second line of a selected tile
/// (the "g" of "3m90fxiwwfo61.png") was drawn below the orange selection background.
/// </summary>
[Collection(ExplorerHeadlessCollection.Name)]
public sealed class ExplorerTileCaptionTests
{
    private const string NoSpaces = "3m90fxiwwfo61.png";
    private const string LongNoSpaces = "aVeryLongFileNameWithoutAnySpacesAtAllThatMustBreakAnywhereAndStillNeedsMoreRoomThanTwoLines0123456789.png";
    private const string LongWithSpaces = "a long file name with many words that needs more than two caption lines even in the extra large view.png";
    private const string Short = "a.png";

    private static void CaptionTree(string root)
    {
        foreach (var name in new[] { NoSpaces, LongNoSpaces, LongWithSpaces, Short })
            File.WriteAllBytes(Path.Combine(root, name), ExplorerHarness.Png(48, 32, 90, 120, 200));
    }

    private static void AssertCaptionInside(ExplorerHarness h, string name, string state, int mode)
    {
        var tile = h.Tile(name)!;
        var block = tile.GetVisualDescendants().OfType<TextBlock>().First(t => Equals(t.Tag, "name"));
        var host = (Control)block.Parent!;
        var fill = tile.GetVisualDescendants().OfType<Border>().First(b => b.Name == "SelectionFill");
        var where = $"mode {mode} {state} '{name}'";
        Assert.True(host.Bounds.Height <= ExplorerViewModes.CaptionLineHeight * ExplorerViewModes.CaptionLines + 0.5, $"{where}: caption box {host.Bounds.Height} taller than the reserved lines");

        var lines = block.TextLayout.TextLines.Count;
        Assert.True(lines <= ExplorerViewModes.CaptionLines, $"{where}: {lines} caption lines");
        Assert.True(block.TextLayout.Height <= host.Bounds.Height + 0.5, $"{where}: text {block.TextLayout.Height} taller than its box {host.Bounds.Height}");

        var hostRect = new Rect(host.TranslatePoint(new Point(0, 0), tile)!.Value, host.Bounds.Size);
        var fillRect = new Rect(fill.TranslatePoint(new Point(0, 0), tile)!.Value, fill.Bounds.Size);
        var tileRect = new Rect(tile.Bounds.Size);
        Assert.True(tileRect.Contains(hostRect), $"{where}: caption {hostRect} leaves the tile {tileRect}");
        Assert.True(fillRect.Contains(hostRect), $"{where}: caption {hostRect} leaves the selection fill {fillRect}");
    }

    [Fact]
    public async Task Wrapped_captions_stay_inside_the_tile_and_selection_in_every_icon_mode_and_state()
    {
        await ExplorerHarness.Run(async h =>
        {
            foreach (var mode in new[] { ExplorerViewModes.Medium, ExplorerViewModes.Large, ExplorerViewModes.ExtraLarge })
            {
                h.Surface.ViewModeCombo.SelectedIndex = mode;
                await h.SettleAsync();
                foreach (var name in new[] { NoSpaces, LongNoSpaces, LongWithSpaces, Short })
                    AssertCaptionInside(h, name, "unselected", mode);

                h.Click(NoSpaces);
                h.Click(LongNoSpaces, modifiers: RawInputModifiers.Control); // selected + focused
                h.Window.MouseMove(h.TileCenter(LongWithSpaces));             // hovered
                h.Pump();
                Assert.Contains("selected", h.Tile(NoSpaces)!.Classes);
                foreach (var name in new[] { NoSpaces, LongNoSpaces, LongWithSpaces, Short })
                    AssertCaptionInside(h, name, "selected/focused/hovered", mode);

                // Short names do not wrap; long names fill both lines and end with an ellipsis (the tooltip has the rest).
                Assert.Equal(1, h.Tile(Short)!.GetVisualDescendants().OfType<TextBlock>().First(t => Equals(t.Tag, "name")).TextLayout.TextLines.Count);
                foreach (var name in new[] { LongNoSpaces, LongWithSpaces })
                {
                    var layout = h.Tile(name)!.GetVisualDescendants().OfType<TextBlock>().First(t => Equals(t.Tag, "name")).TextLayout;
                    Assert.Equal(2, layout.TextLines.Count);
                    var last = string.Concat(layout.TextLines[^1].TextRuns.Select(r => r.Text.ToString())).TrimEnd();
                    Assert.True(last.EndsWith('…'), $"mode {mode} '{name}': clipped caption ends with '{last}', not an ellipsis; lines: {string.Join(" | ", layout.TextLines.Select(l => string.Concat(l.TextRuns.Select(r => r.Text.ToString())) + "@" + l.Width))}");
                }
            }
        }, CaptionTree);
    }

    private static Border Part(Border tile, string name) => tile.GetVisualDescendants().OfType<Border>().First(b => b.Name == name);

    private static Rect RectIn(Control part, Control tile) => new(part.TranslatePoint(new Point(0, 0), tile)!.Value, part.Bounds.Size);

    [Fact]
    public async Task Selection_outline_is_3px_on_the_fill_edge_and_selecting_never_moves_content()
    {
        await ExplorerHarness.Run(async h =>
        {
            foreach (var mode in new[] { ExplorerViewModes.Medium, ExplorerViewModes.Large, ExplorerViewModes.ExtraLarge,
                                         ExplorerViewModes.Small, ExplorerViewModes.List, ExplorerViewModes.Details })
            {
                h.Surface.ViewModeCombo.SelectedIndex = mode;
                await h.SettleAsync();
                // The selection survives a view change: deselect so "before" is the unselected layout.
                if (h.Tile(NoSpaces)!.Classes.Contains("selected"))
                {
                    h.Click(NoSpaces, modifiers: RawInputModifiers.Control);
                    h.Pump();
                }
                var tile = h.Tile(NoSpaces)!;
                Assert.DoesNotContain("selected", tile.Classes);
                var name = tile.GetVisualDescendants().OfType<TextBlock>().First(t => Equals(t.Tag, "name"));
                var before = (Bounds: tile.Bounds.Size, Name: RectIn(name, tile), Padding: tile.Padding, Border: tile.BorderThickness);
                Assert.True(Part(tile, "SelectionOutline").BorderThickness.Left <= 1, "unselected tile has no 3 px outline");

                h.Click(NoSpaces); // selected + focused
                h.Pump();
                tile = h.Tile(NoSpaces)!;
                name = tile.GetVisualDescendants().OfType<TextBlock>().First(t => Equals(t.Tag, "name"));
                var where = $"mode {mode}";
                Assert.Equal(before.Bounds, tile.Bounds.Size);
                Assert.Equal(before.Padding, tile.Padding);
                Assert.Equal(before.Border, tile.BorderThickness);
                Assert.Equal(before.Name, RectIn(name, tile));

                var outline = Part(tile, "SelectionOutline");
                var fill = Part(tile, "SelectionFill");
                Assert.Equal(new Thickness(ExplorerViewModes.SelectionOutlineThickness), outline.BorderThickness);
                // No gap: the outline is drawn on the fill's own edge, and the tile's own border stays transparent.
                Assert.Equal(RectIn(fill, tile), RectIn(outline, tile));
                Assert.True(tile.BorderBrush is null || tile.BorderBrush is Avalonia.Media.ISolidColorBrush { Color.A: 0 }, $"{where}: tile border {tile.BorderBrush}");
                Assert.True(new Rect(tile.Bounds.Size).Contains(RectIn(fill, tile)), $"{where}: fill leaves the tile");
                if (ExplorerViewModes.IsIconMode(mode))
                {
                    // The brown band above the thumbnail is as thick as the bands left and right of it.
                    var thumb = RectIn((Control)((StackPanel)((Grid)tile.Child!).Children[1]).Children[0], tile);
                    var f = RectIn(fill, tile);
                    Assert.Equal(thumb.Left - f.Left, thumb.Top - f.Top, 0.51);
                    Assert.Equal(f.Right - thumb.Right, thumb.Top - f.Top, 0.51);
                }
                // Selected + focused shows the inner focus hairline; the content (icon modes) is never under the outline.
                Assert.True(Part(tile, "FocusHairline").Opacity > 0.5, $"{where}: focus hairline hidden");
                if (ExplorerViewModes.IsIconMode(mode))
                {
                    var inner = RectIn(fill, tile).Deflate(ExplorerViewModes.SelectionOutlineThickness);
                    var content = RectIn((Control)((Grid)tile.Child!).Children[1], tile);
                    Assert.True(inner.Contains(content), $"{where}: outline covers content {content} (inner {inner})");
                }

                // Selected only (focus moved elsewhere with Ctrl+click): solid 3 px, no hairline.
                h.Click(Short, modifiers: RawInputModifiers.Control);
                h.Pump();
                Assert.Equal(3, Part(h.Tile(NoSpaces)!, "SelectionOutline").BorderThickness.Left);
                Assert.True(Part(h.Tile(NoSpaces)!, "FocusHairline").Opacity < 0.01);
                // Focused only: Ctrl+click toggles the focused item out of the selection, leaving a 1 px focus outline.
                h.Click(Short, modifiers: RawInputModifiers.Control);
                h.Pump();
                var focusedOnly = h.Tile(Short)!;
                Assert.DoesNotContain("selected", focusedOnly.Classes);
                Assert.Contains("focused", focusedOnly.Classes);
                Assert.Equal(1, Part(focusedOnly, "SelectionOutline").BorderThickness.Left);
                Assert.True(Part(focusedOnly, "SelectionFill").Opacity < 0.2);
            }
        }, CaptionTree);
    }

    [Fact]
    public async Task Names_that_break_mid_word_keep_the_extension_whole()
    {
        Assert.Equal(13, Glide.App.Controls.CaptionTextBlock.ExtensionBreakIndex(NoSpaces));
        Assert.Equal(-1, Glide.App.Controls.CaptionTextBlock.ExtensionBreakIndex("README"));
        Assert.Equal(-1, Glide.App.Controls.CaptionTextBlock.ExtensionBreakIndex(".hidden"));
        await ExplorerHarness.Run(async h =>
        {
            h.Surface.ViewModeCombo.SelectedIndex = ExplorerViewModes.Medium;
            await h.SettleAsync();
            var layout = h.Tile(NoSpaces)!.GetVisualDescendants().OfType<TextBlock>().First(t => Equals(t.Tag, "name")).TextLayout;
            var lines = layout.TextLines.Select(l => string.Concat(l.TextRuns.Select(r => r.Text.ToString())).Trim()).ToList();
            Assert.True(lines.Count == 1 || (lines.Count == 2 && lines[0] == "3m90fxiwwfo61" && lines[1] == ".png"), $"lines: {string.Join(" | ", lines)}");
            // Too long to keep the stem on one line: the old wrapping (two lines, ellipsis) is kept.
            var longLayout = h.Tile(LongNoSpaces)!.GetVisualDescendants().OfType<TextBlock>().First(t => Equals(t.Tag, "name")).TextLayout;
            Assert.Equal(2, longLayout.TextLines.Count);
        }, CaptionTree);
    }

    // ---- README screenshots (written only when GLIDE_EXPLORER_RENDER_DIR is set) --------------------------

    /// <summary>The file names of the published 5.0 Explorer screenshots; the pictures are synthetic gradients.</summary>
    private static readonly string[] ScreenshotNames =
    {
        "3m90fxiwwfo61.png", "5sh7n01d9b311.jpg", "8ptxnpbe1g221.jpg", "03325_valleyofthestars_2880x1800.jpg",
        "04001_thepierofhanaleibay_2880x1800.jpg", "04026_manlysunrise_2880x1800.jpg", "106481.jpg", "509987.jpg",
        "551537.jpg", "586388.jpg", "677357.png", "893848.png", "976156.png", "1245403.png", "1308922.jpg", "1347287.jpeg",
        "aerial-view-green-mountains-8k-wallpaper.jpg", "altogether-lovely-iain-campbell-wallpaper.jpg", "blue-moon-morning-5k-84.jpg",
        "fire_kissed_water-wallpaper-1920x1080.jpg", "FsKmARzXgAASlkN.jpeg", "Full-HD-Free-Wallpaper-7.jpg", "IMG_3632.JPG",
        "jaws-beach-in-the-moonlight-wallpaper.jpg", "JbXSLOt.jpg", "joshua-sortino-xZqr8WtYEJ0-unsplash.jpg",
        "leafless-tree-on-brown-field-wallpaper.jpg", "LyKTuqo.jpg", "Screenshot 2024-04-03 221500.png",
        "space-7680x4320-8k-wallpaper-nebula.jpg", "triumph-volumetric-light-wallpaper.jpg", "wallpaperflare.com_wallpaper.jpg"
    };

    private static void ScreenshotTree(string root)
    {
        var folder = Path.Combine(root, "Dark");
        Directory.CreateDirectory(folder);
        for (var i = 0; i < ScreenshotNames.Length; i++)
        {
            // Dark, wallpaper-like two-colour gradients with a soft glow: generated, never real photos.
            var hue = i * 137.5 % 360;
            var (r1, g1, b1) = Hsv(hue, 0.75, 0.55);
            var (r2, g2, b2) = Hsv((hue + 60) % 360, 0.85, 0.12);
            var gx = 40 + i * 23 % 80; var gy = 20 + i * 11 % 40;
            File.WriteAllBytes(Path.Combine(folder, ScreenshotNames[i]), ExplorerHarness.Png(160, 90, (x, y) =>
            {
                var t = (x + y) / 250.0;
                var glow = Math.Max(0, 1 - Math.Sqrt((x - gx) * (x - gx) + (y - gy) * (y - gy)) / 35.0) * 120;
                byte Mix(byte a, byte b) => (byte)Math.Clamp(a * (1 - t) + b * t + glow, 0, 255);
                return (Mix(r1, r2), Mix(g1, g2), Mix(b1, b2));
            }));
        }
    }

    private static (byte, byte, byte) Hsv(double h, double s, double v)
    {
        var c = v * s; var x = c * (1 - Math.Abs(h / 60 % 2 - 1)); var m = v - c;
        var (r, g, b) = h < 60 ? (c, x, 0.0) : h < 120 ? (x, c, 0.0) : h < 180 ? (0.0, c, x) : h < 240 ? (0.0, x, c) : h < 300 ? (x, 0.0, c) : (c, 0.0, x);
        return ((byte)((r + m) * 255), (byte)((g + m) * 255), (byte)((b + m) * 255));
    }

    [Fact]
    public async Task Render_readme_explorer_screenshots()
    {
        var dir = Environment.GetEnvironmentVariable("GLIDE_EXPLORER_RENDER_DIR");
        if (string.IsNullOrWhiteSpace(dir)) return;
        Directory.CreateDirectory(dir);
        // GLIDE_EXPLORER_RENDER_FOLDER: render a real folder (opened read-only, nothing is written to it) with the
        // machine's real Quick access and drive locations in the sidebar instead of the synthetic test tree.
        var realFolder = Environment.GetEnvironmentVariable("GLIDE_EXPLORER_RENDER_FOLDER");
        var useReal = !string.IsNullOrWhiteSpace(realFolder) && Directory.Exists(realFolder);
        await ExplorerHarness.Run(async h =>
        {
            if (useReal)
            {
                ExplorerLocations.QuickAccessOverride = null;
                ExplorerLocations.DrivesOverride = null;
            }
            await h.OpenExplorerAsync(useReal ? realFolder! : Path.Combine(h.Root, "Dark"));
            // One "Dark" tab, as in the published screenshots.
            var active = h.Workspace.Active!;
            foreach (var other in h.Workspace.Tabs.Where(t => !ReferenceEquals(t, active)).ToList())
                await (Task)h.Call("CloseTabByIdAsync", other.Id)!;
            await h.SettleAsync();
            // GLIDE_EXPLORER_RENDER_ACCENT: a built-in accent preset name (e.g. "Orange") applied through the app's own theme applier.
            var accent = Environment.GetEnvironmentVariable("GLIDE_EXPLORER_RENDER_ACCENT");
            if (!string.IsNullOrWhiteSpace(accent))
                Glide.App.ThemePaletteApplier.Apply(null, accent, null, 100, "", "", "", "");
            // Tiles capture the accent brush when they are built, so rebuild them after the accent changed.
            if (!string.IsNullOrWhiteSpace(accent))
            {
                h.Surface.ViewModeCombo.SelectedIndex = ExplorerViewModes.Small;
                await h.SettleAsync();
            }
            h.Surface.ViewModeCombo.SelectedIndex = ExplorerViewModes.Medium;
            await h.SettleAsync();
            h.Click(ScreenshotNames[0]);
            const double scale = 1.5; // the 5.0 screenshots were taken at 150 % display scaling
            foreach (var (name, width, height) in new[]
                     {
                         ("explorer-full", 1920, 1128), ("explorer-compact", 921, 621), ("explorer-portrait", 870, 971),
                         ("explorer-ultrawide", 1821, 780), ("explorer-landscape", 1421, 780)
                     })
            {
                h.Window.Width = width / scale;
                h.Window.Height = height / scale;
                await h.SettleAsync();
                await h.WaitUntil(() => h.Surface.Repeater.GetVisualDescendants().OfType<Image>().Count(i => i.Source is not null) >= 4, "thumbnails");
                if (useReal)
                {
                    // Real photos (up to 8K) take longer to thumbnail: wait until every realised tile has its picture.
                    for (var i = 0; i < 150; i++)
                    {
                        h.Pump();
                        var images = h.Surface.Repeater.GetVisualDescendants().OfType<Image>().ToList();
                        if (images.Count > 0 && images.All(img => img.Source is not null)) break;
                        await Task.Delay(200);
                    }
                }
                await Task.Delay(200);
                h.Pump();
                h.Window.UpdateLayout();
                using var bitmap = new Avalonia.Media.Imaging.RenderTargetBitmap(new PixelSize(width, height), new Vector(96 * scale, 96 * scale));
                bitmap.Render(h.Window);
                bitmap.Save(Path.Combine(Environment.GetEnvironmentVariable("GLIDE_EXPLORER_RENDER_DIR")!, name + ".png"));
            }
            // Close-up of the selected tile (its wrapped caption inside the selection fill), at 3x.
            h.Window.Width = 1920 / scale;
            h.Window.Height = 1128 / scale;
            await h.SettleAsync();
            h.Pump();
            h.Window.UpdateLayout();
            var tile = h.Tile(ScreenshotNames[0]);
            if (tile is not null)
            {
                const double zoom = 3;
                var origin = tile.TranslatePoint(new Point(0, 0), h.Window)!.Value;
                var full = new PixelSize((int)Math.Ceiling(h.Window.Bounds.Width * zoom), (int)Math.Ceiling(h.Window.Bounds.Height * zoom));
                using var whole = new Avalonia.Media.Imaging.RenderTargetBitmap(full, new Vector(96 * zoom, 96 * zoom));
                whole.Render(h.Window);
                const double pad = 12;
                var x = Math.Max(0, (int)((origin.X - pad) * zoom));
                var y = Math.Max(0, (int)((origin.Y - pad) * zoom));
                var w = Math.Min(full.Width - x, (int)((tile.Bounds.Width + 2 * pad) * zoom));
                var hgt = Math.Min(full.Height - y, (int)((tile.Bounds.Height + 2 * pad) * zoom));
                using var crop = new Avalonia.Media.Imaging.WriteableBitmap(new PixelSize(w, hgt), new Vector(96, 96),
                    Avalonia.Platform.PixelFormat.Bgra8888, Avalonia.Platform.AlphaFormat.Premul);
                using (var fb = crop.Lock())
                    whole.CopyPixels(new PixelRect(x, y, w, hgt), fb.Address, fb.RowBytes * hgt, fb.RowBytes);
                crop.Save(Path.Combine(dir, "explorer-selected-tile-closeup.png"));
            }
            h.Window.Width = 1200;
            h.Window.Height = 800;
        }, ScreenshotTree);
    }
}
