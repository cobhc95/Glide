using Glide.Core.Commands;

namespace Glide.Core.Tests;

/// <summary>Hints that describe a configurable binding must follow the live bindings and vanish when the input does nothing.</summary>
public sealed class BindingHintsTests
{
    private static Dictionary<string, string> Gestures(params (string Slot, string Action)[] overrides)
    {
        var map = GestureCatalog.CreateDefaultMap();
        foreach (var (slot, action) in overrides) map[slot] = action;
        return map;
    }

    [Fact]
    public void Default_tab_hint_is_one_short_line_with_the_close_gesture_and_the_menu()
    {
        var hint = BindingHints.TabMouseHint(Gestures(), null);
        Assert.Equal("Drag to move · Middle-click to close · Right-click for more", hint);
    }

    [Fact]
    public void Tab_hint_names_double_click_when_middle_click_does_not_close()
    {
        var hint = BindingHints.TabMouseHint(Gestures(("tabs.middleClick", GestureCatalog.DuplicateTab)), null);
        Assert.DoesNotContain("Middle-click", hint);
        Assert.Contains("Double-click to close", hint);
    }

    [Fact]
    public void Tab_hint_has_no_close_text_when_both_gestures_do_nothing_or_do_something_else()
    {
        var hint = BindingHints.TabMouseHint(Gestures(("tabs.doubleClick", GestureCatalog.Nothing), ("tabs.middleClick", GestureCatalog.DuplicateTab)), null);
        Assert.DoesNotContain("close", hint);
        Assert.DoesNotContain("click to", hint);
    }

    [Fact]
    public void Tab_hint_follows_an_explicit_close_binding_and_a_disabled_menu()
    {
        var hint = BindingHints.TabMouseHint(Gestures(("tabs.middleClick", GestureCatalog.Nothing), ("tabs.doubleClick", GestureCatalog.CloseTab), ("tabs.rightClick", GestureCatalog.Nothing)), null);
        Assert.Equal("Drag to move · Double-click to close", hint);
    }

    [Fact]
    public void Tab_hint_sees_custom_gestures_on_the_same_input()
    {
        var custom = new List<CustomGesture> { new() { Area = GestureAreas.Tabs, Input = GestureInputs.MiddleClick, Action = GestureCatalog.Nothing } };
        var hint = BindingHints.TabMouseHint(Gestures(), custom);
        Assert.DoesNotContain("Middle-click", hint);
        Assert.Contains("Double-click to close", hint);
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
