using Glide.Core.Commands;

namespace Glide.Core.Tests;

/// <summary>Hints that describe a configurable binding must follow the live bindings and vanish when the input does nothing.</summary>
[Trait("Area", "Hotkeys")]
public sealed class BindingHintsTests
{
    private static Dictionary<string, string> Gestures(params (string Slot, string Action)[] overrides)
    {
        var map = GestureCatalog.CreateDefaultMap();
        foreach (var (slot, action) in overrides) map[slot] = action;
        return map;
    }

    [Fact]
    public void Default_tab_hint_is_one_line_of_chips_with_the_close_gesture_the_menu_and_the_resize_key()
    {
        var chips = BindingHints.TabMouseHintChips(Gestures(), null, "Ctrl");
        Assert.Equal([new HintChip("Drag", "move"), new HintChip("Middle", "close"), new HintChip("Right", "more"), new HintChip("Ctrl+Wheel", "size")], chips);
        Assert.Equal("[Drag] move · [Middle] close · [Right] more · [Ctrl+Wheel] size", BindingHints.TabMouseHint(Gestures(), null, "Ctrl"));
    }

    [Theory]
    [InlineData("Shift", "Shift+Wheel")]
    [InlineData("Alt", "Alt+Wheel")]
    [InlineData("None", null)]
    public void The_size_chip_names_the_chosen_resize_key_and_is_hidden_for_None(string modifier, string? chip)
    {
        var chips = BindingHints.TabMouseHintChips(Gestures(), null, modifier);
        Assert.Equal(chip, chips.SingleOrDefault(c => c.Action == "size").Input);
    }

    [Fact]
    public void Tab_hint_names_double_click_when_middle_click_does_not_close()
    {
        var chips = BindingHints.TabMouseHintChips(Gestures(("tabs.middleClick", GestureCatalog.DuplicateTab)), null, "None");
        Assert.DoesNotContain(chips, c => c.Input == "Middle");
        Assert.Contains(new HintChip("Double-click", "close"), chips);
    }

    [Fact]
    public void Tab_hint_has_no_close_chip_when_both_gestures_do_nothing_or_do_something_else()
    {
        var chips = BindingHints.TabMouseHintChips(Gestures(("tabs.doubleClick", GestureCatalog.Nothing), ("tabs.middleClick", GestureCatalog.DuplicateTab)), null, "None");
        Assert.DoesNotContain(chips, c => c.Action == "close");
    }

    [Fact]
    public void Tab_hint_follows_an_explicit_close_binding_and_a_disabled_menu()
    {
        var hint = BindingHints.TabMouseHint(Gestures(("tabs.middleClick", GestureCatalog.Nothing), ("tabs.doubleClick", GestureCatalog.CloseTab), ("tabs.rightClick", GestureCatalog.Nothing)), null);
        Assert.Equal("[Drag] move · [Double-click] close", hint);
    }

    [Fact]
    public void Tab_hint_sees_custom_gestures_on_the_same_input()
    {
        var custom = new List<CustomGesture> { new() { Area = GestureAreas.Tabs, Input = GestureInputs.MiddleClick, Action = GestureCatalog.Nothing } };
        var chips = BindingHints.TabMouseHintChips(Gestures(), custom, "None");
        Assert.DoesNotContain(chips, c => c.Input == "Middle");
        Assert.Contains(new HintChip("Double-click", "close"), chips);
    }

    [Fact]
    public void Shortcuts_are_appended_only_when_bound()
    {
        Assert.Equal("New tab", BindingHints.WithShortcuts("New tab", []));
        Assert.Equal("New tab (Ctrl+T / F9)", BindingHints.WithShortcuts("New tab", ["Ctrl+T", "F9"]));
    }

    [Fact]
    public void Shortcut_list_reads_the_live_hotkey_map()
    {
        var map = HotkeyCatalog.CreateDefaultMap();
        Assert.Contains("Ctrl+O", BindingHints.ShortcutList("file.open", map));
        map["file.open"] = new List<string> { "F9" };
        Assert.Equal("F9", BindingHints.ShortcutList("file.open", map));
        map["file.open"] = new List<string>();
        Assert.Equal(string.Empty, BindingHints.ShortcutList("file.open", map));
    }

    [Fact]
    public void Fixed_shortcuts_come_from_the_registry()
    {
        Assert.Equal("Ctrl+F", BindingHints.FixedShortcut("fixed.explorer.filter"));
        Assert.Equal("Esc", BindingHints.FixedShortcut("fixed.overlay.areaCancel"));
        Assert.Null(BindingHints.FixedShortcut("file.open"));
        Assert.Null(BindingHints.FixedShortcut("nope"));
    }
}
