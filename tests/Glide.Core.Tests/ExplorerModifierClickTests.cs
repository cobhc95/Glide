using Glide.Core.Commands;

namespace Glide.Core.Tests;

/// <summary>Round 4 C3: modifier + double-click rows in the Explorer, from the action registry (Alt = properties).</summary>
[Trait("Area", "Gestures")]
public sealed class ExplorerModifierClickTests
{
    [Fact]
    public void Alt_ctrl_and_shift_double_click_have_their_own_explorer_rows()
    {
        Assert.Equal("explorer.doubleClick", GestureCatalog.ExplorerDoubleClickSlot(GestureModifiers.None));
        Assert.Equal("explorer.altDoubleClick", GestureCatalog.ExplorerDoubleClickSlot(GestureModifiers.Alt));
        Assert.Equal("explorer.ctrlDoubleClick", GestureCatalog.ExplorerDoubleClickSlot(GestureModifiers.Ctrl));
        Assert.Equal("explorer.shiftDoubleClick", GestureCatalog.ExplorerDoubleClickSlot(GestureModifiers.Shift));
        Assert.Equal("properties", GestureCatalog.DefaultBehavior["explorer.altDoubleClick"]);
        Assert.Equal("explorer.itemProperties", GestureCatalog.DefaultRunAction("explorer.altDoubleClick"));
        Assert.Null(GestureCatalog.DefaultRunAction("explorer.ctrlDoubleClick"));
    }

    [Fact]
    public void Properties_is_a_registry_command_offered_on_the_explorer_rows()
    {
        var action = AppActionRegistry.Find("explorer.itemProperties");
        Assert.NotNull(action);
        Assert.Equal(GlideCommand.ExplorerItemProperties, action!.Command);
        foreach (var slot in new[] { "explorer.altDoubleClick", "explorer.ctrlDoubleClick", "explorer.doubleClick", "explorer.middleClick" })
            Assert.Contains(GestureCatalog.ActionsForSlot(slot), a => a.Id == "explorer.itemProperties");
        Assert.DoesNotContain(GestureCatalog.ActionsForSlot("windowed.leftClickImage"), a => a.Id == "explorer.itemProperties");
    }

    [Fact]
    public void A_custom_binding_overrides_the_alt_default()
    {
        var bindings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["explorer.altDoubleClick"] = GestureCatalog.OpenInNewTab };
        Assert.Equal(GestureCatalog.OpenInNewTab, GestureCatalog.ResolveAction("explorer.altDoubleClick", bindings, null, GestureModifiers.Alt));
        Assert.Equal(GestureCatalog.Default, GestureCatalog.ResolveAction("explorer.altDoubleClick", null, null, GestureModifiers.Alt));
    }
}
