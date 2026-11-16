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
    public void Default_tab_hint_mentions_both_close_gestures_and_the_menu()
    {
        var hint = BindingHints.TabMouseHint("Drag to reorder", Gestures(), null);
        Assert.Equal("Drag to reorder • middle-click or double-click to close • right-click for tab actions.", hint);
    }

    [Fact]
    public void Tab_hint_drops_a_disabled_double_click()
    {
        var hint = BindingHints.TabMouseHint("Drag to reorder", Gestures(("tabs.doubleClick", GestureCatalog.Nothing)), null);
        Assert.DoesNotContain("double-click", hint);
        Assert.Contains("middle-click to close", hint);
    }

    [Fact]
    public void Tab_hint_has_no_close_text_when_both_gestures_do_nothing_or_do_something_else()
    {
        var hint = BindingHints.TabMouseHint("Drag to reorder", Gestures(("tabs.doubleClick", GestureCatalog.Nothing), ("tabs.middleClick", GestureCatalog.DuplicateTab)), null);
        Assert.DoesNotContain("close", hint);
        Assert.DoesNotContain("middle-click", hint);
    }

    [Fact]
    public void Tab_hint_follows_an_explicit_close_binding_and_a_disabled_menu()
    {
        var hint = BindingHints.TabMouseHint("Drag to reorder", Gestures(("tabs.doubleClick", GestureCatalog.CloseTab), ("tabs.rightClick", GestureCatalog.Nothing)), null);
        Assert.Contains("double-click", hint);
        Assert.DoesNotContain("right-click", hint);
    }

    [Fact]
    public void Tab_hint_sees_custom_gestures_on_the_same_input()
    {
        var custom = new List<CustomGesture> { new() { Area = GestureAreas.Tabs, Input = GestureInputs.MiddleClick, Action = GestureCatalog.Nothing } };
        var hint = BindingHints.TabMouseHint("Drag to reorder", Gestures(), custom);
        Assert.DoesNotContain("middle-click", hint);
        Assert.Contains("double-click to close", hint);
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

    [Theory]
    [InlineData(new[] { "a" }, "a")]
    [InlineData(new[] { "a", "b" }, "a or b")]
    [InlineData(new[] { "a", "b", "c" }, "a, b or c")]
    public void JoinOr_reads_naturally(string[] items, string expected) => Assert.Equal(expected, BindingHints.JoinOr(items));
}
