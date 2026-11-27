using Avalonia.Headless;
using Avalonia.Input;
using Glide.App.Explorer;

namespace Glide.Explorer.Tests;

/// <summary>Keyboard / drag behaviours added to match Windows File Explorer (docs/audits/explorer-windows-parity.md).</summary>
[Collection(ExplorerHeadlessCollection.Name)]
[Trait("Area", "Explorer")]
public sealed class ExplorerWindowsParityTests
{
    // Default tree at 1200 px: five columns. Row 0 = Alpha, Beta, Gamma, a.png, b.png; row 1 = c.png.

    [Fact]
    public async Task F3_moves_the_keyboard_to_the_filter_box()
    {
        await ExplorerHarness.Run(h =>
        {
            h.FocusList();
            h.Key(Key.F3);
            Assert.True(h.Surface.FilterBox.IsFocused);
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task Ctrl_Shift_C_copies_the_full_path_of_the_selection()
    {
        await ExplorerHarness.Run(async h =>
        {
            h.Click("a.png");
            h.Key(Key.C, RawInputModifiers.Control | RawInputModifiers.Shift);
            await h.WaitUntil(() => ExplorerHost.LastCopiedText is not null, "path copied");
            Assert.Equal(Path.Combine(h.Root, "a.png"), ExplorerHost.LastCopiedText);
            Assert.Null(h.Clipboard.TryGet()); // not a file copy
        });
    }

    [Fact]
    public async Task Ctrl_Insert_copies_and_Shift_Insert_pastes_like_Ctrl_C_and_Ctrl_V()
    {
        await ExplorerHarness.Run(async h =>
        {
            h.Click("a.png");
            h.Key(Key.Insert, RawInputModifiers.Control);
            var content = h.Clipboard.TryGet();
            Assert.NotNull(content);
            Assert.False(content!.Cut);
            Assert.Equal(new[] { Path.Combine(h.Root, "a.png") }, content.Paths);

            await h.NavigateAsync(Path.Combine(h.Root, "Beta"));
            h.FocusList();
            h.Key(Key.Insert, RawInputModifiers.Shift);
            await h.WaitUntil(() => File.Exists(Path.Combine(h.Root, "Beta", "a.png")), "pasted into Beta");
        });
    }

    [Fact]
    public async Task Ctrl_Shift_digits_choose_the_view_like_Windows()
    {
        await ExplorerHarness.Run(async h =>
        {
            h.FocusList();
            h.Key(Key.D6, RawInputModifiers.Control | RawInputModifiers.Shift);
            await h.SettleAsync();
            Assert.Equal(ExplorerViewModes.Details, h.Get<int>("_browserViewMode"));
            h.Key(Key.D1, RawInputModifiers.Control | RawInputModifiers.Shift);
            await h.SettleAsync();
            Assert.Equal(0, h.Get<int>("_browserViewMode"));
            Assert.Equal(0, h.Surface.ViewModeCombo.SelectedIndex);
        });
    }

    [Fact]
    public async Task Up_on_the_first_row_and_Down_on_the_last_row_stay_put()
    {
        await ExplorerHarness.Run(async h =>
        {
            h.Click("Beta");
            h.Key(Key.Up);
            Assert.Equal(new[] { "Beta" }, h.SelectedNames); // not Alpha (index 0)
            h.Click("c.png");
            h.Key(Key.Down);
            Assert.Equal(new[] { "c.png" }, h.SelectedNames);
            h.Click("a.png");
            h.Key(Key.Down); // a shorter last row: the last item
            Assert.Equal(new[] { "c.png" }, h.SelectedNames);
            h.Key(Key.Up);
            Assert.Equal(new[] { "Alpha" }, h.SelectedNames.Take(1)); // c.png is column 0
            await Task.CompletedTask;
        });
    }

    [Fact]
    public async Task Shift_digit_hotkeys_still_work_from_the_Explorer_list()
    {
        await ExplorerHarness.Run(h =>
        {
            h.FocusList();
            var routed = new List<string>();
            h.Window.ActionRouted += (a, _) => routed.Add(a.Id);
            h.Window.ActionInterceptorForTests = (_, _) => true; // do not launch anything
            // Shift+1 = "Open in external program 1": a symbol (!), not a name character.
            h.Window.KeyPress(Key.D1, RawInputModifiers.Shift, PhysicalKey.None, "!");
            h.Window.KeyTextInput("!");
            h.Window.KeyRelease(Key.D1, RawInputModifiers.Shift, PhysicalKey.None, "!");
            h.Pump();
            Assert.Contains("external.1", routed);
            return Task.CompletedTask;
        });
    }

    [Theory]
    [InlineData(@"C:\a\x.png", @"C:\b", KeyModifiers.None, false)]      // same drive: move
    [InlineData(@"C:\a\x.png", @"D:\b", KeyModifiers.None, true)]       // another drive: copy
    [InlineData(@"C:\a\x.png", @"c:\b", KeyModifiers.None, false)]      // drive letter case does not matter
    [InlineData(@"C:\a\x.png", @"D:\b", KeyModifiers.Shift, false)]    // Shift forces a move
    [InlineData(@"C:\a\x.png", @"C:\b", KeyModifiers.Control, true)]    // Ctrl forces a copy
    [InlineData(@"C:\a\x.png", @"D:\b", KeyModifiers.Control | KeyModifiers.Shift, true)]
    [InlineData(@"\\server\share\x.png", @"C:\b", KeyModifiers.None, true)]
    public void Drop_defaults_to_move_on_one_drive_and_copy_across_drives(string source, string destination, KeyModifiers modifiers, bool copy) =>
        Assert.Equal(copy, Glide.App.MainWindow.ExplorerDropIsCopy(new[] { source }, destination, modifiers));
}
