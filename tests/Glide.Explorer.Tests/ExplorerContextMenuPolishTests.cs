using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.VisualTree;
using Glide.App.Explorer;

namespace Glide.Explorer.Tests;

/// <summary>
/// 5.0.6 tester reports on the Explorer tab's context menu and toolbar: header alignment, icons, multi-selection
/// labels and targets, the right-clicked item's focus border, the Show in File Explorer tooltip, the Sort button and
/// the view-mode labels.
/// </summary>
[Collection(ExplorerHeadlessCollection.Name)]
[Trait("Area", "Explorer")]
public sealed class ExplorerContextMenuPolishTests
{
    private static void Raise(MenuItem item) => item.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(MenuItem.ClickEvent));

    private static List<MenuItem> Entries(ContextMenu menu) =>
        menu.Items.OfType<MenuItem>().Where(i => i.Header?.ToString() != "-").ToList();

    private static List<string> Headers(ContextMenu menu) =>
        menu.Items.OfType<MenuItem>().Select(i => i.Header?.ToString() ?? "").ToList();

    private static MenuItem Item(ContextMenu menu, string header) =>
        menu.Items.OfType<MenuItem>().First(i => string.Equals(i.Header?.ToString(), header, StringComparison.Ordinal));

    private static Point CaptionCenter(ExplorerHarness h, string name)
    {
        var caption = h.Tile(name)!.GetVisualDescendants().OfType<TextBlock>().First(t => Equals(t.Tag, "name"));
        return caption.TranslatePoint(new Point(caption.Bounds.Width / 2, caption.Bounds.Height / 2), h.Window)!.Value;
    }

    private static IReadOnlyList<string> FocusedTiles(ExplorerHarness h) =>
        h.Surface.Repeater.Children.OfType<Border>().Where(t => t.Classes.Contains("focused"))
            .Select(t => ((ExplorerEntry)t.Tag!).Name).ToList();

    // ---- 1. alignment -----------------------------------------------------------------------------

    [Fact]
    public async Task Menu_headers_stay_left_aligned_when_the_right_click_lands_on_a_centred_caption()
    {
        await ExplorerHarness.Run(h =>
        {
            h.FocusList();
            h.Key(Key.A, RawInputModifiers.Control);
            // In the thumbnail views the caption under a tile is a centred TextBlock. The menu used to be opened on the
            // element under the pointer, so it inherited that TextBlock's TextAlignment=Center.
            h.Click(CaptionCenter(h, "a.png"), MouseButton.Right);
            var menu = h.ActiveMenu!;
            Assert.True(menu.IsOpen);
            h.Pump();
            menu.UpdateLayout();
            foreach (var item in Entries(menu))
            {
                var header = item.GetVisualDescendants().OfType<ContentPresenter>().First(p => p.Name == "PART_HeaderPresenter");
                var text = header.GetVisualDescendants().OfType<TextBlock>().First();
                Assert.True(text.TextAlignment is TextAlignment.Left or TextAlignment.Start,
                    $"'{item.Header}' is {text.TextAlignment}-aligned");
            }
            return Task.CompletedTask;
        });
    }

    // ---- 2. icons ---------------------------------------------------------------------------------

    [Fact]
    public async Task Every_entry_of_the_Explorer_menus_has_an_icon()
    {
        await ExplorerHarness.Run(h =>
        {
            void AssertIcons(string what)
            {
                var missing = Entries(h.ActiveMenu!).Where(i => i.Icon is null).Select(i => i.Header?.ToString()).ToList();
                Assert.True(missing.Count == 0, $"{what}: no icon on {string.Join(", ", missing)}");
                h.Call("CloseActiveOwnedMenu", new object?[] { null });
            }
            h.Click("a.png", MouseButton.Right); AssertIcons("image");
            h.Click("Alpha", MouseButton.Right); AssertIcons("folder");
            h.Click("Alpha");
            h.Click("b.png", modifiers: RawInputModifiers.Control);
            h.Click("b.png", MouseButton.Right); AssertIcons("multi-selection");
            h.Click(h.EmptySpotInList(), MouseButton.Right); AssertIcons("background");
            return Task.CompletedTask;
        });
    }

    // ---- 3. multi-selection -----------------------------------------------------------------------

    [Fact]
    public async Task Multi_selection_menu_says_Copy_N_paths_and_Properties_opens_the_right_clicked_item()
    {
        await ExplorerHarness.Run(async h =>
        {
            h.Click("Alpha");
            h.Click("a.png", modifiers: RawInputModifiers.Control);
            h.Click("b.png", modifiers: RawInputModifiers.Control);
            h.Click("a.png", MouseButton.Right);
            var menu = h.ActiveMenu!;
            Assert.Contains("Copy 3 paths", Headers(menu));
            Assert.Contains("Properties of a.png", Headers(menu));

            Raise(Item(menu, "Copy 3 paths"));
            await h.WaitUntil(() => ExplorerHost.LastCopiedText?.Split(Environment.NewLine).Length == 3, "3 paths copied");
            Assert.Equal(3, ExplorerHost.LastCopiedText!.Split(Environment.NewLine).Length);

            h.Click("a.png", MouseButton.Right);
            Raise(Item(h.ActiveMenu!, "Properties of a.png"));
            Assert.Equal(new[] { Path.Combine(h.Root, "a.png") }, h.Properties);
        });
    }

    [Fact]
    public async Task Right_clicked_item_keeps_the_focus_border_while_the_menu_is_open()
    {
        await ExplorerHarness.Run(h =>
        {
            h.FocusList();
            h.Key(Key.A, RawInputModifiers.Control);
            h.Click("c.png", MouseButton.Right);
            Assert.True(h.ActiveMenu!.IsOpen);
            Assert.Equal(new[] { "c.png" }, FocusedTiles(h));
            Assert.Contains("Properties of c.png", Headers(h.ActiveMenu!));

            h.Call("CloseActiveOwnedMenu", new object?[] { null });
            h.FocusList();
            Assert.Equal(new[] { "c.png" }, FocusedTiles(h)); // the clicked item stays the current one
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task Keyboard_menu_Properties_targets_the_focused_item()
    {
        await ExplorerHarness.Run(h =>
        {
            h.Click("a.png");
            h.Click("c.png", modifiers: RawInputModifiers.Shift); // a, b, c selected; c is the focused item
            h.Key(Key.F10, RawInputModifiers.Shift);
            var menu = h.ActiveMenu!;
            Assert.Equal(new[] { "c.png" }, FocusedTiles(h));
            Raise(Item(menu, "Properties of c.png"));
            Assert.Equal(new[] { Path.Combine(h.Root, "c.png") }, h.Properties);
            return Task.CompletedTask;
        });
    }

    // ---- 4. tooltip -------------------------------------------------------------------------------

    [Fact]
    public async Task Show_in_File_Explorer_tooltip_mentions_the_right_click_menu_and_keeps_the_live_keys()
    {
        await ExplorerHarness.Run(h =>
        {
            h.Settings.Hotkeys["explorer.showInWindows"] = new List<string> { "Ctrl+Shift+W" };
            h.Call("RefreshDynamicTooltips");
            var tip = ToolTip.GetTip(h.Surface.WindowsExplorerButton)?.ToString() ?? "";
            Assert.Contains("Ctrl+Shift+W", tip);
            Assert.Contains("Right-click", tip);
            Assert.Contains("other file manager", tip);

            h.Settings.AlternativeFileManagerPath = @"C:\Tools\TotalCmd64.exe";
            h.Settings.Hotkeys["explorer.showInAlternative"] = new List<string> { "Ctrl+Shift+T" };
            h.Call("RefreshDynamicTooltips");
            tip = ToolTip.GetTip(h.Surface.WindowsExplorerButton)?.ToString() ?? "";
            Assert.Contains("Right-click", tip);
            Assert.Contains("TotalCmd64 (Ctrl+Shift+T)", tip);
            Assert.Contains("Ctrl+Shift+W", tip);
            return Task.CompletedTask;
        });
    }

    // ---- 5. sort button ---------------------------------------------------------------------------

    [Fact]
    public async Task Sort_button_sits_next_to_the_view_box_shows_the_sort_and_opens_the_shared_sort_menu()
    {
        await ExplorerHarness.Run(async h =>
        {
            var button = h.Surface.FindControl<Button>("BrowserSortButton");
            Assert.NotNull(button);
            Assert.Equal(Grid.GetColumn(h.Surface.ViewModeCombo) - 1, Grid.GetColumn(button!));
            string ButtonText() => string.Concat(button.GetLogicalDescendantsText());
            Assert.Contains("Name", ButtonText());
            Assert.Contains("Sort", ToolTip.GetTip(button)?.ToString() ?? "");

            // The context menu's Sort by submenu and the button's menu are the same list.
            h.Click(h.EmptySpotInList(), MouseButton.Right);
            var sortBy = Item(h.ActiveMenu!, "Sort by").Items.OfType<MenuItem>().Select(i => i.Header?.ToString() ?? "").ToList();
            h.Call("CloseActiveOwnedMenu", new object?[] { null });

            h.ClickButton(button);
            var menu = h.ActiveMenu!;
            Assert.Equal(sortBy, Headers(menu));
            Raise(Item(menu, "Date modified ▲"));
            await h.SettleAsync();
            Assert.Contains("Date modified", ButtonText());
            Assert.Contains("Date modified", ToolTip.GetTip(button)?.ToString() ?? "");

            h.ClickButton(button);
            Raise(Item(h.ActiveMenu!, "Date modified ▼"));
            await h.SettleAsync();
            Assert.Contains("descending", ToolTip.GetTip(button)?.ToString() ?? "");
            Assert.Equal(ExplorerSortKey.Modified, h.Get<ExplorerSortKey>("_explorerSortKey"));
            Assert.False(h.Get<bool>("_explorerSortAscending"));
        });
    }

    // ---- 6. view-mode labels ----------------------------------------------------------------------

    [Fact]
    public async Task View_box_uses_short_labels_that_fit_when_closed_and_saved_values_are_unchanged()
    {
        await ExplorerHarness.Run(async h =>
        {
            var combo = h.Surface.ViewModeCombo;
            Assert.Equal(new[] { "Thumbnails (XL)", "Thumbnails (L)", "Thumbnails (M)", "Thumbnails (S)", "List", "Details" },
                combo.Items.OfType<ComboBoxItem>().Select(i => i.Content?.ToString()));
            for (var i = 0; i < combo.ItemCount; i++)
            {
                combo.SelectedIndex = i;
                await h.SettleAsync();
                var label = ((ComboBoxItem)combo.Items[i]!).Content!.ToString()!;
                var shown = combo.GetVisualDescendants().OfType<TextBlock>().First(t => t.Text == label);
                var natural = new TextBlock { Text = label, FontSize = shown.FontSize, FontFamily = shown.FontFamily, FontWeight = shown.FontWeight };
                natural.Measure(Size.Infinity);
                Assert.True(shown.Bounds.Width + 0.5 >= natural.DesiredSize.Width,
                    $"'{label}' is cut off: {shown.Bounds.Width:0.#} of {natural.DesiredSize.Width:0.#} px");
            }
            // Stored values are the old names: a saved "Large thumbnails" still means the Large view, and choosing a view
            // in the toolbar never rewrites the setting.
            Assert.Equal(ExplorerViewModes.Large, ExplorerViewModes.IndexOf("Large thumbnails"));
            Assert.Equal(ExplorerViewModes.ExtraLarge, ExplorerViewModes.IndexOf("Extra large icons"));
            Assert.Equal("Large thumbnails", h.Settings.ExplorerDefaultView);
        });
    }
}

internal static class LogicalTextExtensions
{
    /// <summary>The text of every TextBlock inside a control (its content may be a panel of icon + text).</summary>
    public static IEnumerable<string> GetLogicalDescendantsText(this Control control) =>
        control.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text ?? "").Prepend(control is ContentControl { Content: string s } ? s : "");
}
