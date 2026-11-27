using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;

namespace Glide.Explorer.Tests;

/// <summary>
/// Windows File Explorer type-to-select in the Explorer tab: a letter selects the next item that starts with it, the same
/// letter again cycles, several letters typed within about a second match a prefix, case does not matter. Windows wins over
/// single-letter viewer hotkeys (F, O, U, ...) while the list has the keyboard; text boxes and modified keys are untouched.
/// </summary>
[Collection(ExplorerHeadlessCollection.Name)]
[Trait("Area", "Explorer")]
public sealed class ExplorerTypeAheadTests
{
    private static void Tree(string root)
    {
        foreach (var folder in new[] { "docs", "Downloads", "Fonts", "images", "Imports", "music" })
            Directory.CreateDirectory(Path.Combine(root, folder));
        foreach (var file in new[] { "ice.png", "fax.png", "open.png", "update.png", "report.png", "1st.png" })
            File.WriteAllBytes(Path.Combine(root, file), ExplorerHarness.Png(8, 8, 10, 20, 30));
    }

    /// <summary>One typed character the way the platform delivers it: key down, text input, key up.</summary>
    private static void Press(ExplorerHarness h, char c, RawInputModifiers modifiers = RawInputModifiers.None)
    {
        var key = char.IsDigit(c) ? Key.D0 + (c - '0') : Key.A + (char.ToLowerInvariant(c) - 'a');
        var text = c.ToString();
        h.Window.KeyPress(key, modifiers, PhysicalKey.None, text);
        h.Window.KeyTextInput(text);
        h.Window.KeyRelease(key, modifiers, PhysicalKey.None, text);
        h.Pump();
    }

    private static void Expire(ExplorerHarness h) => h.Set("_explorerTypeAheadTick", Environment.TickCount64 - 5000);

    private static string? Selected(ExplorerHarness h) => h.CurrentIndex >= 0 && h.SelectionSet.Count == 1 ? h.Names[h.CurrentIndex] : null;

    [Fact]
    public async Task A_letter_selects_the_first_match_even_when_it_is_the_first_item_and_nothing_is_selected()
    {
        await ExplorerHarness.Run(h =>
        {
            h.FocusList();
            Assert.Empty(h.SelectionSet);
            var first = h.Names[0];
            Press(h, char.ToLowerInvariant(first[0]));
            Assert.Equal(first, Selected(h));
            return Task.CompletedTask;
        }, Tree);
    }

    [Fact]
    public async Task The_same_letter_again_cycles_through_the_matches_and_wraps()
    {
        await ExplorerHarness.Run(h =>
        {
            h.FocusList();
            var matches = h.Names.Where(n => n.StartsWith("i", StringComparison.OrdinalIgnoreCase)).ToList();
            Assert.Equal(3, matches.Count); // images, Imports, ice.png
            foreach (var expected in matches.Concat(matches.Take(1)))
            {
                Press(h, 'i');
                Assert.Equal(expected, Selected(h));
            }
            return Task.CompletedTask;
        }, Tree);
    }

    [Fact]
    public async Task Letters_typed_quickly_match_the_prefix()
    {
        await ExplorerHarness.Run(h =>
        {
            h.FocusList();
            Press(h, 'i');
            Press(h, 'm');
            Press(h, 'p');
            Assert.Equal("Imports", Selected(h));
            Expire(h);
            Press(h, 'i');
            Press(h, 'm');
            Press(h, 'a');
            Assert.Equal("images", Selected(h));
            return Task.CompletedTask;
        }, Tree);
    }

    [Fact]
    public async Task The_prefix_resets_after_a_pause_and_matching_ignores_case()
    {
        await ExplorerHarness.Run(h =>
        {
            h.FocusList();
            Press(h, 'I', RawInputModifiers.Shift);
            Assert.True(Selected(h)!.StartsWith("i", StringComparison.OrdinalIgnoreCase));
            Expire(h);
            Press(h, 'm'); // a fresh "m", not "im"
            Assert.Equal("music", Selected(h));
            Press(h, 'u'); // "mu"
            Assert.Equal("music", Selected(h));
            return Task.CompletedTask;
        }, Tree);
    }

    [Fact]
    public async Task Digits_select_names_that_start_with_them()
    {
        await ExplorerHarness.Run(h =>
        {
            h.FocusList();
            Press(h, '1'); // 1 = fit height in the viewer
            Assert.Equal("1st.png", Selected(h));
            return Task.CompletedTask;
        }, Tree);
    }

    [Fact]
    public async Task Windows_wins_over_single_letter_viewer_hotkeys_while_the_list_has_the_keyboard()
    {
        await ExplorerHarness.Run(h =>
        {
            h.FocusList();
            var routed = new List<string>();
            h.Window.ActionRouted += (a, _) => routed.Add(a.Id);
            // Real platforms only deliver the text input of a key down nobody handled: the hotkey route must leave these keys alone.
            var handledKeyDowns = new List<Key>();
            h.Window.AddHandler(InputElement.KeyDownEvent, (_, e) => { if (e.Handled) handledKeyDowns.Add(e.Key); }, Avalonia.Interactivity.RoutingStrategies.Bubble, true);
            // F = fullscreen, O = open file, U = refresh in the viewer; here they jump to Fonts / open.png / update.png.
            Press(h, 'f');
            Assert.Equal("Fonts", Selected(h));
            Expire(h);
            Press(h, 'o');
            Assert.Equal("open.png", Selected(h));
            Expire(h);
            Press(h, 'u');
            Assert.Equal("update.png", Selected(h));
            Assert.Empty(routed);
            Assert.Empty(handledKeyDowns);
            Assert.Equal(WindowState.Normal, h.Window.WindowState);
            return Task.CompletedTask;
        }, Tree);
    }

    [Fact]
    public async Task Modified_keys_keep_their_hotkeys_and_never_type_ahead()
    {
        await ExplorerHarness.Run(h =>
        {
            h.FocusList();
            var routed = new List<string>();
            h.Window.ActionRouted += (a, _) => routed.Add(a.Id);
            h.Window.KeyPress(Key.O, RawInputModifiers.Control, PhysicalKey.None, null);
            h.Window.KeyRelease(Key.O, RawInputModifiers.Control, PhysicalKey.None, null);
            h.Pump();
            Assert.Contains("file.open", routed);
            Assert.Empty(h.SelectionSet);
            return Task.CompletedTask;
        }, Tree);
    }

    [Fact]
    public async Task Typing_in_the_filter_box_or_address_bar_never_moves_the_list_selection()
    {
        await ExplorerHarness.Run(async h =>
        {
            h.Surface.FilterBox.Focus();
            h.Pump();
            Press(h, 'd');
            Press(h, 'o');
            await h.WaitUntil(() => h.Surface.FilterBox.Text == "do", "filter text");
            Assert.Empty(h.SelectionSet);

            h.Surface.FilterBox.Text = "";
            h.Surface.AddressBar.BeginEdit();
            h.Pump();
            Press(h, 'f');
            Assert.Empty(h.SelectionSet);
        }, Tree);
    }

    [Fact]
    public async Task Typing_in_the_rename_box_does_not_move_the_selection()
    {
        await ExplorerHarness.Run(h =>
        {
            h.FocusList();
            Press(h, 'f'); // Fonts
            h.Key(Key.F2);
            Press(h, 'd');
            Press(h, 'o');
            Assert.Equal("Fonts", Selected(h));
            h.Key(Key.Escape);
            return Task.CompletedTask;
        }, Tree);
    }

    [Fact]
    public async Task Space_continues_a_prefix_being_typed()
    {
        await ExplorerHarness.Run(h =>
        {
            h.FocusList();
            Press(h, 'd');
            Press(h, 'o');
            Press(h, 'w');
            Assert.Equal("Downloads", Selected(h));
            // A space inside the 1 s window belongs to the name being typed (no viewer "next image" meanwhile).
            h.Window.KeyPress(Key.Space, RawInputModifiers.None, PhysicalKey.None, " ");
            h.Window.KeyTextInput(" ");
            h.Window.KeyRelease(Key.Space, RawInputModifiers.None, PhysicalKey.None, " ");
            h.Pump();
            Assert.Equal("Downloads", Selected(h)); // buffer "dow " matches nothing; selection stays
            return Task.CompletedTask;
        }, Tree);
    }
}
