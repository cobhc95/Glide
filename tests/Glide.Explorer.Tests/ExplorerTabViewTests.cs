using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.VisualTree;
using Glide.App.Explorer;

namespace Glide.Explorer.Tests;

/// <summary>Views (Extra large ... Details), sorting, filtering, scrolling, thumbnails and the rendered look of the Explorer tab.</summary>
[Collection(ExplorerHeadlessCollection.Name)]
public sealed class ExplorerTabViewTests
{
    public ExplorerTabViewTests() { }

    private static void SortTree(string root)
    {
        ExplorerHarness.DefaultTree(root);
        // Distinct sizes and dates: big (largest, oldest), mid, small (smallest, newest).
        File.WriteAllBytes(Path.Combine(root, "big.png"), ExplorerHarness.Png(120, 120, 1, 2, 3));
        File.WriteAllBytes(Path.Combine(root, "mid.png"), ExplorerHarness.Png(60, 60, 1, 2, 3));
        File.WriteAllBytes(Path.Combine(root, "small.png"), ExplorerHarness.Png(8, 8, 1, 2, 3));
        File.SetLastWriteTime(Path.Combine(root, "big.png"), new DateTime(2020, 1, 1));
        File.SetLastWriteTime(Path.Combine(root, "mid.png"), new DateTime(2022, 1, 1));
        File.SetLastWriteTime(Path.Combine(root, "small.png"), new DateTime(2024, 1, 1));
        File.SetLastWriteTime(Path.Combine(root, "a.png"), new DateTime(2019, 1, 1));
        File.SetLastWriteTime(Path.Combine(root, "b.png"), new DateTime(2019, 1, 2));
        File.SetLastWriteTime(Path.Combine(root, "c.png"), new DateTime(2019, 1, 3));
        File.WriteAllBytes(Path.Combine(root, "photo.bmp"), new byte[64]);
        File.SetLastWriteTime(Path.Combine(root, "photo.bmp"), new DateTime(2023, 6, 1));
    }

    private static IReadOnlyList<string> Files(ExplorerHarness h) => h.Entries.Where(e => !e.IsDirectory).Select(e => e.Name).ToList();

    // ---- view modes ---------------------------------------------------------------------------------

    [Fact]
    public async Task View_combo_changes_the_tile_geometry_for_every_mode()
    {
        await ExplorerHarness.Run(async h =>
        {
            Assert.Equal(6, h.Surface.ViewModeCombo.ItemCount);
            Assert.Equal(ExplorerViewModes.Large, h.Surface.ViewModeCombo.SelectedIndex); // default "Large icons"
            for (var mode = 0; mode < ExplorerViewModes.Names.Length; mode++)
            {
                h.Surface.ViewModeCombo.SelectedIndex = mode;
                await h.SettleAsync();
                var tile = h.Tile("a.png")!;
                var (w, hgt) = ExplorerViewModes.TileSize(mode);
                if (double.IsNaN(w)) Assert.True(tile.Bounds.Width > 400, "details row is as wide as its columns");
                else Assert.Equal(w, tile.Bounds.Width);
                Assert.Equal(hgt, tile.Bounds.Height);
                Assert.Equal(mode == ExplorerViewModes.Details, h.Surface.DetailsHeader.IsVisible);
                Assert.True(h.Surface.Repeater.Children.Count >= 6, $"mode {mode} realized {h.Surface.Repeater.Children.Count} tiles");
                // Every mode keeps the layout math used by keyboard / rubber-band selection in step with the real tiles.
                foreach (var name in new[] { "Alpha", "b.png", "c.png" })
                {
                    var index = h.Names.ToList().IndexOf(name);
                    var rect = (Rect)h.Call("ExplorerTileRect", index)!;
                    var real = h.Tile(name)!.TranslatePoint(new Point(0, 0), h.Surface.Repeater)!.Value;
                    Assert.True(Math.Abs(rect.X - real.X) < 1.5 && Math.Abs(rect.Y - real.Y) < 1.5, $"mode {mode} tile {name}: math {rect.TopLeft} vs real {real}; repeater {h.Surface.Repeater.Bounds} scroll {h.Surface.ScrollViewer.Bounds} viewport {h.Surface.ScrollViewer.Viewport} extent {h.Surface.ScrollViewer.Extent} cols {h.Call("ExplorerColumnCount")} xs {string.Join(",", h.Surface.Repeater.Children.OfType<Border>().Select(t => t.Bounds.X))}");
                }
            }
        });
    }

    [Fact]
    public async Task Default_view_comes_from_the_setting_and_selection_survives_a_view_change()
    {
        await ExplorerHarness.Run(async h =>
        {
            h.Settings.ExplorerDefaultView = "Details";
            await h.OpenExplorerAsync(Path.Combine(h.Root, "Alpha")); // a new tab uses the setting
            Assert.Equal(ExplorerViewModes.Details, h.Surface.ViewModeCombo.SelectedIndex);
            Assert.True(h.Surface.DetailsHeader.IsVisible);
            h.Click("inner.png");
            h.Surface.ViewModeCombo.SelectedIndex = ExplorerViewModes.ExtraLarge;
            await h.SettleAsync();
            Assert.Equal(new[] { "inner.png" }, h.SelectedNames);
            Assert.Contains("selected", h.Tile("inner.png")!.Classes);
        });
    }

    [Fact]
    public async Task Details_view_shows_name_date_type_and_size_columns_and_a_header_that_sorts()
    {
        await ExplorerHarness.Run(async h =>
        {
            h.Surface.ViewModeCombo.SelectedIndex = ExplorerViewModes.Details;
            await h.SettleAsync();
            var row = h.Tile("big.png")!;
            var texts = row.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text).ToList();
            Assert.Contains("big.png", texts);
            Assert.Contains("PNG image", texts);
            Assert.Contains(new DateTime(2020, 1, 1).ToString("g"), texts);
            Assert.Contains(ExplorerModel.FormatSize(new FileInfo(Path.Combine(h.Root, "big.png")).Length), texts);
            Assert.Contains("File folder", h.Tile("Alpha")!.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text));

            var headers = h.Surface.DetailsHeader.GetVisualDescendants().OfType<Button>().Select(b => b.Name).ToList();
            Assert.Equal(new[] { "SortHeaderName", "SortHeaderDatemodified", "SortHeaderType", "SortHeaderSize" }, headers);
            var size = h.Surface.DetailsHeader.GetVisualDescendants().OfType<Button>().First(b => b.Name == "SortHeaderSize");
            h.ClickButton(size);
            await h.SettleAsync();
            Assert.Equal(new[] { "small.png", "a.png", "b.png", "c.png", "mid.png", "big.png" }.OrderBy(n => new FileInfo(Path.Combine(h.Root, n)).Length).ThenBy(n => n).ToArray(), Files(h).Where(n => n != "photo.bmp").ToArray());
            // Clicking the same header again flips the direction.
            size = h.Surface.DetailsHeader.GetVisualDescendants().OfType<Button>().First(b => b.Name == "SortHeaderSize");
            h.ClickButton(size);
            await h.SettleAsync();
            Assert.Equal("big.png", Files(h)[0]);
            // Folders stay first.
            Assert.Equal(new[] { "Alpha", "Beta", "Gamma" }, h.Names.Take(3).ToArray());
        }, SortTree);
    }

    [Fact]
    public async Task Details_columns_can_be_resized_by_dragging_the_grip()
    {
        await ExplorerHarness.Run(async h =>
        {
            h.Surface.ViewModeCombo.SelectedIndex = ExplorerViewModes.Details;
            await h.SettleAsync();
            var widths = h.Get<double[]>("_explorerColumnWidths");
            var before = widths[1];
            var grip = h.Surface.DetailsHeader.GetVisualDescendants().OfType<Border>().First(b => b.Name == "ColumnGrip1");
            var from = grip.TranslatePoint(new Point(3, 8), h.Window)!.Value;
            h.Window.MouseMove(from);
            h.Window.MouseDown(from, MouseButton.Left);
            h.Window.MouseMove(new Point(from.X + 60, from.Y), RawInputModifiers.LeftMouseButton);
            h.Window.MouseUp(new Point(from.X + 60, from.Y), MouseButton.Left);
            h.Pump();
            await h.SettleAsync();
            Assert.True(widths[1] > before + 40, $"column width {before} -> {widths[1]}");
            // The realized rows follow the new width.
            var row = h.Tile("a.png")!;
            var grid = row.GetVisualDescendants().OfType<Grid>().First(g => g.ColumnDefinitions.Count == 4);
            Assert.Equal(widths[1], grid.ColumnDefinitions[1].Width.Value);
        });
    }

    // ---- sorting and grouping -----------------------------------------------------------------------

    [Fact]
    public async Task Background_menu_sort_by_changes_the_order_and_the_menu_ticks_the_active_choice()
    {
        await ExplorerHarness.Run(async h =>
        {
            async Task<List<MenuItem>> OpenSortMenu()
            {
                h.Click(h.EmptySpotInList(), MouseButton.Right);
                await Task.Yield();
                var menu = h.ActiveMenu;
                Assert.NotNull(menu);
                Assert.True(menu!.IsOpen);
                return menu.Items.OfType<MenuItem>().First(i => (string?)i.Header == "Sort by").Items.OfType<MenuItem>().ToList();
            }
            static void Choose(IEnumerable<MenuItem> items, string startsWith) =>
                items.First(e => e.Header!.ToString()!.StartsWith(startsWith, StringComparison.Ordinal)).RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(MenuItem.ClickEvent));
            static IEnumerable<string> Headers(IEnumerable<MenuItem> items) => items.Select(i => i.Header?.ToString() ?? "-");
            IEnumerable<ExplorerEntry> OnlyFiles() => h.Entries.Where(e => !e.IsDirectory);

            var entries = await OpenSortMenu();
            Assert.Equal(new[] { "Name  ✓", "Date modified", "Type", "Size", "-", "Ascending  ✓", "Descending", "-", "Group folders first  ✓" }, Headers(entries).ToArray());
            Choose(entries, "Size");
            await h.SettleAsync();
            Assert.Equal(OnlyFiles().OrderBy(f => f.Size).ThenBy(f => f.Name, StringComparer.OrdinalIgnoreCase).Select(f => f.Name), OnlyFiles().Select(f => f.Name));
            Assert.Equal(0, OnlyFiles().First().Size - OnlyFiles().Min(f => f.Size)); // smallest first

            entries = await OpenSortMenu();
            Assert.Contains("Size  ✓", Headers(entries));
            Choose(entries, "Descending");
            await h.SettleAsync();
            Assert.Equal("big.png", OnlyFiles().First().Name);
            Assert.Equal(new[] { "Alpha", "Beta", "Gamma" }, h.Names.Take(3).ToArray()); // folders stay first

            entries = await OpenSortMenu();
            Choose(entries, "Date modified");
            await h.SettleAsync();
            Assert.Equal("small.png", OnlyFiles().First().Name); // newest first (Descending is still on)
            Assert.Equal("a.png", OnlyFiles().Last().Name);      // oldest last

            entries = await OpenSortMenu();
            Choose(entries, "Ascending");
            await h.SettleAsync();
            Assert.Equal("a.png", OnlyFiles().First().Name);

            // Group folders first off: files and folders interleave by name ("a.png" < "Alpha" case-insensitively? no: Alpha < a.png? natural order decides).
            entries = await OpenSortMenu();
            Choose(entries, "Name");
            await h.SettleAsync();
            entries = await OpenSortMenu();
            Choose(entries, "Group folders first");
            await h.SettleAsync();
            var names = h.Names.ToList();
            var natural = names.OrderBy(n => n, Glide.Core.NaturalStringComparer.Instance).ToList();
            Assert.Equal(natural, names); // plain natural order, folders no longer pinned to the top
            Assert.NotEqual(new[] { "Alpha", "Beta", "Gamma" }, names.Take(3).ToArray());
            entries = await OpenSortMenu();
            Assert.Contains("Name  ✓", Headers(entries));
            Assert.DoesNotContain("Group folders first  ✓", Headers(entries));
        }, SortTree);
    }

    [Fact]
    public async Task Sort_by_name_is_natural_and_type_sort_groups_extensions()
    {
        await ExplorerHarness.Run(async h =>
        {
            Assert.Equal(new[] { "img2.png", "img10.png" }, Files(h).Where(n => n.StartsWith("img", StringComparison.Ordinal)).ToArray());
            h.Call("SetExplorerSort", ExplorerSortKey.Type, true);
            await h.SettleAsync();
            Assert.Equal("photo.bmp", Files(h)[0]); // "BMP image" sorts before "PNG image"
            h.Call("SetExplorerSort", ExplorerSortKey.Name, false);
            await h.SettleAsync();
            Assert.Equal(new[] { "Gamma", "Beta", "Alpha" }, h.Names.Take(3).ToArray()); // folders first, descending
        }, root =>
        {
            ExplorerHarness.DefaultTree(root);
            File.WriteAllBytes(Path.Combine(root, "img10.png"), ExplorerHarness.Png(8, 8, 1, 1, 1));
            File.WriteAllBytes(Path.Combine(root, "img2.png"), ExplorerHarness.Png(8, 8, 1, 1, 1));
            File.WriteAllBytes(Path.Combine(root, "photo.bmp"), new byte[64]);
        });
    }

    // ---- filter -------------------------------------------------------------------------------------

    [Fact]
    public async Task Filter_box_filters_the_folder_as_you_type_Ctrl_F_focuses_it_and_Esc_clears()
    {
        await ExplorerHarness.Run(async h =>
        {
            h.FocusList();
            h.Key(Key.F, RawInputModifiers.Control);
            await h.WaitUntil(() => h.Surface.FilterBox.IsFocused, "filter focused");
            h.Surface.FilterBox.Text = "a";
            await h.WaitUntil(() => h.Names.Count < 6, "filtered");
            await h.SettleAsync();
            Assert.Equal(new[] { "Alpha", "Beta", "Gamma", "a.png" }, h.Names); // names containing "a"
            Assert.Equal("4 items", h.Surface.ItemCountText.Text);
            h.Surface.FilterBox.Text = "zzz";
            await h.WaitUntil(() => h.Names.Count == 0, "no match");
            Assert.True(h.Surface.EmptyText.IsVisible);
            Assert.Equal("No items match your filter.", h.Surface.EmptyText.Text);
            h.Key(Key.Escape); // clears the box and hands focus back to the list
            await h.WaitUntil(() => h.Names.Count == 6, "filter cleared");
            Assert.Equal("", h.Surface.FilterBox.Text ?? "");
            Assert.False(h.Surface.EmptyText.IsVisible);
            // Navigating to another folder clears a filter too.
            h.Surface.FilterBox.Text = "Alp";
            await h.WaitUntil(() => h.Names.Count == 1, "filtered to Alpha");
            await h.SettleAsync();
            h.Click("Alpha");
            h.Key(Key.Enter);
            await h.SettleAsync();
            Assert.Equal("", h.Surface.FilterBox.Text ?? "");
            Assert.Equal(2, h.Names.Count);
        });
    }

    // ---- scrolling, virtualisation, thumbnails ------------------------------------------------------

    [Fact]
    public async Task Mouse_wheel_scrolls_a_long_folder_and_only_visible_tiles_are_realized()
    {
        await ExplorerHarness.Run(async h =>
        {
            Assert.Equal(301, h.Names.Count); // Sub + 300 images
            var scroll = h.Surface.ScrollViewer;
            Assert.True(scroll.Extent.Height > scroll.Viewport.Height * 3, "a long list needs scrolling");
            Assert.True(h.Surface.Repeater.Children.Count < 80, $"virtualised: {h.Surface.Repeater.Children.Count} tiles realized for 301 entries");
            Assert.Equal(0, scroll.Offset.Y);
            var centre = scroll.TranslatePoint(new Point(scroll.Bounds.Width / 2, scroll.Bounds.Height / 2), h.Window)!.Value;
            h.Window.MouseMove(centre);
            h.Window.MouseWheel(centre, new Vector(0, -3));
            h.Pump();
            await h.SettleAsync();
            Assert.True(scroll.Offset.Y > 20, $"wheel moved the list to {scroll.Offset.Y}");
            var after = scroll.Offset.Y;
            h.Window.MouseWheel(centre, new Vector(0, 3));
            h.Pump();
            Assert.True(scroll.Offset.Y < after, "wheel up scrolls back");
            // Keyboard End jumps to the last item and scrolls it into view.
            h.FocusList();
            h.Key(Key.End);
            await h.SettleAsync();
            Assert.Equal(new[] { "img299.png" }, h.SelectedNames);
            Assert.True(scroll.Offset.Y > scroll.Extent.Height - scroll.Viewport.Height - 40, "scrolled to the bottom");
            Assert.NotNull(h.Tile("img299.png"));
            h.Key(Key.Home);
            await h.SettleAsync();
            Assert.Equal(0, scroll.Offset.Y);
        }, ExplorerHarness.ManyFilesTree);
    }

    [Fact]
    public async Task Ctrl_wheel_steps_through_the_icon_sizes()
    {
        await ExplorerHarness.Run(async h =>
        {
            var centre = h.Surface.ScrollViewer.TranslatePoint(new Point(300, 300), h.Window)!.Value;
            h.Window.MouseMove(centre);
            h.Window.MouseWheel(centre, new Vector(0, 1), RawInputModifiers.Control); // wheel up = bigger
            h.Pump();
            await h.SettleAsync();
            Assert.Equal(ExplorerViewModes.ExtraLarge, h.Surface.ViewModeCombo.SelectedIndex);
            h.Window.MouseWheel(centre, new Vector(0, -1), RawInputModifiers.Control);
            h.Window.MouseWheel(centre, new Vector(0, -1), RawInputModifiers.Control);
            h.Pump();
            await h.SettleAsync();
            Assert.Equal(ExplorerViewModes.Medium, h.Surface.ViewModeCombo.SelectedIndex);
        });
    }

    [Fact]
    public async Task Image_tiles_get_real_thumbnails_asynchronously_and_folders_never_do()
    {
        await ExplorerHarness.Run(async h =>
        {
            var imageTile = h.Tile("a.png")!;
            await h.WaitUntil(() => imageTile.GetVisualDescendants().OfType<Image>().Any(i => i.Source is not null), "thumbnail decoded");
            var thumb = imageTile.GetVisualDescendants().OfType<Image>().First(i => i.Source is not null);
            Assert.True(thumb.Source!.Size.Width > 0 && thumb.Source.Size.Width <= 152 + 1, $"thumbnail width {thumb.Source.Size.Width} (bounded decode)");
            // The glyph placeholder is only shown while loading.
            var glyph = imageTile.GetVisualDescendants().OfType<Glide.App.Controls.GlideIconView>().First();
            Assert.False(glyph.IsVisible);
            // Folder tiles use the folder glyph, no bitmap.
            var folder = h.Tile("Alpha")!;
            Assert.Empty(folder.GetVisualDescendants().OfType<Image>());
            var folderGlyph = folder.GetVisualDescendants().OfType<Glide.App.Controls.GlideIconView>().First();
            Assert.Equal("Folder", folderGlyph.Kind);
        });
    }

    [Fact]
    public async Task Thumbnails_are_released_when_tiles_scroll_away_and_memory_stays_bounded()
    {
        await ExplorerHarness.Run(async h =>
        {
            var scroll = h.Surface.ScrollViewer;
            await h.WaitUntil(() => h.Surface.Repeater.GetVisualDescendants().OfType<Image>().Count(i => i.Source is not null) > 5, "first thumbnails");
            var first = h.Tile("img000.png")!;
            var firstImage = first.GetVisualDescendants().OfType<Image>().First();
            scroll.Offset = new Vector(0, scroll.Extent.Height - scroll.Viewport.Height);
            h.Pump();
            await h.SettleAsync();
            await h.WaitUntil(() => h.Surface.Repeater.GetVisualDescendants().OfType<Image>().Any(i => i.Source is not null), "thumbnails at the bottom");
            Assert.Null(firstImage.Source); // the scrolled-away tile dropped its bitmap
            var loaded = h.Surface.Repeater.GetVisualDescendants().OfType<Image>().Count(i => i.Source is not null);
            Assert.True(loaded < 80, $"{loaded} decoded thumbnails alive");
        }, ExplorerHarness.ManyFilesTree);
    }

    // ---- renders (PNG files for visual review when GLIDE_EXPLORER_RENDER_DIR is set) --------------

    [Fact]
    public async Task Render_all_views_with_selection_and_hover()
    {
        await ExplorerHarness.Run(async h =>
        {
            await h.WaitUntil(() => h.Surface.Repeater.GetVisualDescendants().OfType<Image>().Any(i => i.Source is not null), "thumbnails");
            await Task.Delay(150);
            h.Pump();
            h.Render("10-large");
            h.Click("b.png");
            h.Window.MouseMove(h.TileCenter("c.png"));
            h.Render("11-large-selected-hover");
            string[] names = { "12-extra-large", "13-large", "14-medium", "15-small", "16-list", "17-details" };
            for (var mode = 0; mode < 6; mode++)
            {
                h.Surface.ViewModeCombo.SelectedIndex = mode;
                await h.SettleAsync();
                await Task.Delay(120);
                h.Pump();
                h.Render(names[mode]);
            }
            h.Click("Beta");
            h.Click("a.png", modifiers: RawInputModifiers.Control);
            h.Render("18-details-multiselect");
            h.Surface.AddressBar.BeginEdit();
            h.Pump();
            h.Render("19-address-editing");
            h.Key(Key.Escape);
        }, SortTree);
    }
}
