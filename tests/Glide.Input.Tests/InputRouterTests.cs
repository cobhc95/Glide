using Glide.Core.Commands;
using Glide.Input;
using Xunit;

namespace Glide.Input.Tests;

public sealed class InputRouterTests
{
    [Theory]
    [InlineData("LEFT", GlideCommand.PreviousImage)]
    [InlineData("RIGHT", GlideCommand.NextImage)]
    [InlineData("HOME", GlideCommand.FirstImage)]
    [InlineData("END", GlideCommand.LastImage)]
    public void PlainKeysMapToSemanticCommands(string key, GlideCommand expected)
        => Assert.Equal(expected, new InputRouter().ResolveKey(key));

    [Fact]
    public void CtrlOMapsToOpenFile()
        => Assert.Equal(GlideCommand.OpenFile, new InputRouter().ResolveKey("O", control: true));

    [Theory]
    [InlineData("T", true, false, GlideCommand.NewTab)]
    [InlineData("T", true, true, GlideCommand.RestoreClosedTab)]
    [InlineData("K", true, true, GlideCommand.DuplicateTab)]
    [InlineData("F11", false, false, GlideCommand.ToggleFullscreen)]
    [InlineData("SPACE", false, false, GlideCommand.NextImage)]
    [InlineData("S", false, false, GlideCommand.StartPauseSlideshow)]
    [InlineData("S", true, true, GlideCommand.SaveOverlayLayout)]
    [InlineData("L", true, true, GlideCommand.LoadOverlayLayout)]
    [InlineData("TAB", true, false, GlideCommand.NextTab)]
    [InlineData("TAB", true, true, GlideCommand.PreviousTab)]
    public void Phase3Bindings_MapToSemanticCommands(string key, bool control, bool shift, GlideCommand expected)
        => Assert.Equal(expected, new InputRouter().ResolveKey(key, control: control, shift: shift));

    [Fact]
    public void CustomBindingsOverridePhysicalShortcutLookup()
    {
        var bindings = HotkeyCatalog.CreateDefaultMap();
        bindings["view.fullscreen"] = new List<string> { "Ctrl+Shift+F" };
        var router = new InputRouter();
        Assert.Equal(GlideCommand.ToggleFullscreen, router.ResolveShortcut("Ctrl+Shift+F", bindings));
        Assert.Equal(GlideCommand.None, router.ResolveShortcut("F11", bindings));
    }
    [Fact]
    public void DefaultShortcutOwnership_IsConflictFree()
    {
        var collisions = HotkeyCatalog.All
            .SelectMany(action => action.DefaultShortcuts.Select(shortcut => (action.Id, shortcut)))
            .GroupBy(x => x.shortcut, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Select(x => x.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1)
            .ToArray();
        Assert.Empty(collisions);
    }

    [Fact]
    public void Gesture_binding_resolves_through_framework_free_router()
    {
        var router = new InputRouter();
        var map = GestureCatalog.CreateDefaultMap();
        map["wheel.windowed"] = GestureCatalog.ZoomIn;
        Assert.Equal(GestureCatalog.ZoomIn, router.ResolveGesture("wheel.windowed", map));
        Assert.Equal(GlideCommand.ZoomIn, router.ResolveGestureCommand("wheel.windowed", map));
    }

    [Fact]
    public void DefaultKeyboardHotkeys_ContainNoMouseInputs()
    {
        var defaults = HotkeyCatalog.CreateDefaultMap();
        Assert.DoesNotContain(defaults.SelectMany(x => x.Value), shortcut => shortcut.Contains("Mouse", StringComparison.OrdinalIgnoreCase));
    }
}
