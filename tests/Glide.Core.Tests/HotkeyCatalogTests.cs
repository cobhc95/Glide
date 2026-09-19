using Glide.Core.Commands;
using Xunit;

namespace Glide.Core.Tests;

public sealed class HotkeyCatalogTests
{
    [Fact]
    public void Hotkey_contract_has_68_editable_hotkey_rows()
    {
        Assert.Equal(68, HotkeyCatalog.All.Count);
        Assert.Equal(68, HotkeyCatalog.All.Select(x => x.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public void Overlay_layout_defaults_are_ctrl_shifted_and_collision_free()
    {
        var bindings = HotkeyCatalog.CreateDefaultMap();

        Assert.Equal(new[] { "Ctrl+Shift+S" }, bindings["overlay.saveLayout"]);
        Assert.Equal(new[] { "Ctrl+Shift+L" }, bindings["overlay.loadLayout"]);
        Assert.Empty(bindings["slideshow.stop"]);

        var collisions = HotkeyCatalog.All
            .SelectMany(action => action.DefaultShortcuts.Select(shortcut => (action.Id, shortcut)))
            .GroupBy(x => x.shortcut, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Select(x => x.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1)
            .ToArray();
        Assert.Empty(collisions);
    }

    [Fact]
    public void Default_shortcuts_are_owned_by_at_most_one_action()
    {
        var collisions = HotkeyCatalog.All
            .SelectMany(action => action.DefaultShortcuts.Select(shortcut => (action.Id, shortcut)))
            .GroupBy(x => x.shortcut, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Select(x => x.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1)
            .ToArray();
        Assert.Empty(collisions);
    }

    [Fact]
    public void IrfanView_compatible_actual_size_binding_includes_ctrl_h()
    {
        var bindings = HotkeyCatalog.CreateDefaultMap();
        Assert.Contains("Ctrl+H", HotkeyCatalog.ShortcutsForCommand(GlideCommand.ActualSize, bindings));
    }

    [Fact]
    public void Command_shortcuts_aggregate_all_action_aliases()
    {
        var bindings = HotkeyCatalog.CreateDefaultMap();
        var next = HotkeyCatalog.ShortcutsForCommand(GlideCommand.NextImage, bindings);
        Assert.Equal(new[] { "Space", "Right", "PageDown" }, next);

        bindings["nav.nextRight"] = new List<string> { "Space", "N" };
        next = HotkeyCatalog.ShortcutsForCommand(GlideCommand.NextImage, bindings);
        Assert.Equal(new[] { "Space", "N", "PageDown" }, next);
    }
    [Fact]
    public void Glide_and_IrfanView_viewing_presets_are_distinct_and_complete()
    {
        var glide = HotkeyCatalog.CreatePresetMap("Glide default");
        Assert.Contains("F", glide["view.fullscreen"]);
        Assert.Equal(new[] { "1" }, glide["view.fitHeight"]);
        Assert.Equal(new[] { "2" }, glide["view.fitWidth"]);
        Assert.Contains("3", glide["view.actual"]);
        Assert.Contains("Ctrl+H", glide["view.actual"]);

        var irfan = HotkeyCatalog.CreatePresetMap("IrfanView");
        Assert.Equal(new[] { "F11", "Enter" }, irfan["view.fullscreen"]);
        Assert.Contains("F", irfan["view.fit"]);
        Assert.Equal(new[] { "6" }, irfan["view.fitHeight"]);
        Assert.Equal(new[] { "5", "W" }, irfan["view.fitWidth"]);
        Assert.Equal(new[] { "Ctrl+H", "1" }, irfan["view.actual"]);
    }

    [Fact]
    public void Familiarity_presets_are_opt_in_complete_and_distinct()
    {
        Assert.Equal(new[] { "Glide default", "Windows Photos", "IrfanView", "nomacs", "FastStone", "XnView MP" }, HotkeyCatalog.PresetNames);

        var glide = HotkeyCatalog.CreatePresetMap("Glide default");
        var unknown = HotkeyCatalog.CreatePresetMap("future viewer");
        Assert.Equal(glide["view.actual"], unknown["view.actual"]);
        Assert.Contains("Ctrl+H", glide["view.actual"]);

        var nomacs = HotkeyCatalog.CreatePresetMap("nomacs");
        Assert.Equal(new[] { "Ctrl+1" }, nomacs["view.actual"]);
        Assert.Equal(new[] { "F11" }, nomacs["view.fullscreen"]);
        Assert.Empty(nomacs["nav.nextSpace"]);
        Assert.Equal(new[] { "Space" }, nomacs["slideshow.startPause"]);

        var fastStone = HotkeyCatalog.CreatePresetMap("FastStone");
        Assert.Contains("Space", fastStone["nav.nextSpace"]);
        Assert.Contains("PageDown", fastStone["nav.nextPage"]);
        Assert.Equal(new[] { "F11" }, fastStone["view.fullscreen"]);

        var xn = HotkeyCatalog.CreatePresetMap("XnView MP");
        Assert.Equal(new[] { "Multiply" }, xn["view.actual"]);
    }


    [Fact]
    public void Glide31_new_window_shortcuts_are_distinct_and_canonical()
    {
        var bindings = HotkeyCatalog.CreateDefaultMap();
        Assert.Equal(new[] { "Ctrl+N" }, HotkeyCatalog.ShortcutsForCommand(GlideCommand.NewWindowSameImage, bindings));
        Assert.Equal(new[] { "Ctrl+Shift+N" }, HotkeyCatalog.ShortcutsForCommand(GlideCommand.DuplicateSessionWindow, bindings));
    }
}
