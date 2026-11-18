using Glide.Core.Commands;

namespace Glide.Core.Tests;

/// <summary>
/// Guard: the mouse behaviour Glide offered before the 4.8.0 gesture redesign. Fails if an old action
/// id disappears or changes meaning, if an old slot id disappears, if an old saved (slot, action)
/// binding would no longer be honoured, or if an old combination stops being offered for any reason
/// other than the documented 4.8.0 validity rules (one-shots are not offered on wheels or drags,
/// drag-only actions are not offered on clicks, overlay rows offer only overlay-capable actions).
/// </summary>
[Trait("Area", "Gestures")]
public sealed class GestureLegacyMapTests
{
    /// <summary>The 23 pre-4.8.0 actions: id -> (label, command).</summary>
    public static readonly (string Id, string Label, GlideCommand Command)[] LegacyActions =
    [
        ("default", "Use Glide/default behavior", GlideCommand.None),
        ("nothing", "Do nothing", GlideCommand.None),
        ("next", "Next image", GlideCommand.NextImage),
        ("previous", "Previous image", GlideCommand.PreviousImage),
        ("zoomIn", "Zoom in", GlideCommand.ZoomIn),
        ("zoomOut", "Zoom out", GlideCommand.ZoomOut),
        ("fit", "Fit image", GlideCommand.FitImage),
        ("actual", "100% / actual size", GlideCommand.ActualSize),
        ("toggleFitActual", "Toggle Fit / 100%", GlideCommand.ToggleFitActual),
        ("fullscreen", "Toggle fullscreen", GlideCommand.ToggleFullscreen),
        // 5.0.x registry: the gesture now names the same command as the "Open viewer context menu" hotkey.
        ("contextMenu", "Open context menu", GlideCommand.ShowContextMenu),
        ("clearSelection", "Clear selection", GlideCommand.ClearSelection),
        ("toggleInfo", "Toggle image information", GlideCommand.ToggleImageInfo),
        ("settings", "Open Settings", GlideCommand.Settings),
        ("pan", "Pan image", GlideCommand.None),
        ("createSelection", "Create selection", GlideCommand.None),
        ("moveWindow", "Move native window", GlideCommand.None),
        ("resetOverlayZoom", "Reset selected overlay zoom", GlideCommand.ResetSelectedOverlayZoom),
        ("bringOverlayFront", "Bring selected overlay to front", GlideCommand.BringSelectedOverlayFront),
        ("zoomWheel", "Zoom in/out", GlideCommand.None),
        ("zoomWheelReversed", "Zoom in/out (reversed)", GlideCommand.None),
        ("browseWheel", "Previous/next image", GlideCommand.None),
        ("browseWheelReversed", "Previous/next image (reversed)", GlideCommand.None)
    ];

    /// <summary>The 25 pre-4.8.0 slots with their old "Default (...)" text.</summary>
    public static readonly (string Id, string OldDefault)[] LegacySlots =
    [
        ("windowed.leftClickImage", "nothing"), ("windowed.rightClickImage", "context menu"), ("windowed.middleClickImage", "nothing"),
        ("windowed.doubleLeftImage", "enter fullscreen (if enabled)"), ("windowed.doubleRightImage", "nothing"),
        ("fullscreen.leftClickImage", "next image (if fullscreen clicks navigate)"), ("fullscreen.rightClickImage", "previous image (if fullscreen clicks navigate)"),
        ("fullscreen.middleClickImage", "nothing"), ("drag.leftImage", "per the left-drag setting"), ("drag.rightImage", "pan a zoomed image"),
        ("drag.middleImage", "pan (if middle-drag pans)"), ("drag.leftEmpty", "move the window"), ("drag.rightEmpty", "context menu"),
        ("wheel.windowed", "previous / next image"), ("wheel.windowedCtrl", "zoom (if Ctrl+wheel zooms)"), ("wheel.fullscreen", "previous / next image"),
        ("wheel.fullscreenCtrl", "zoom (if Ctrl+wheel zooms)"), ("wheel.rightHeld", "same as the wheel without the right button"),
        ("selection.leftClick", "zoom into the selection"), ("selection.rightClick", "zoom out from the selection"),
        ("overlay.doubleClick", "(reset overlay zoom)"), ("overlay.middleClick", "(reset overlay zoom)"), ("overlay.wheel", "(zoom overlay)"),
        ("overlay.ctrlWheel", "(zoom overlay)"), ("background.doubleLeft", "enter fullscreen (if enabled)")
    ];

    private static readonly HashSet<string> LegacyWheelOnly = new() { "zoomWheel", "zoomWheelReversed", "browseWheel", "browseWheelReversed" };
    private static readonly HashSet<string> LegacySingleDirection = new() { "zoomIn", "zoomOut", "next", "previous" };

    /// <summary>Pre-4.8.0 ActionsForSlot: wheel slots hid the four single-direction items, other slots hid the wheel-only items.</summary>
    public static IEnumerable<string> LegacyOffered(string slotId) => LegacyActions.Select(a => a.Id)
        .Where(id => slotId.Contains("wheel", StringComparison.OrdinalIgnoreCase) ? !LegacySingleDirection.Contains(id) : !LegacyWheelOnly.Contains(id));

    [Fact]
    public void Every_legacy_action_id_still_exists_with_the_same_meaning()
    {
        foreach (var (id, label, command) in LegacyActions)
        {
            var action = GestureCatalog.FindAction(id);
            Assert.NotNull(action);
            Assert.Equal(id, action!.Id);
            Assert.Equal(label, action.Label);
            Assert.Equal(command, action.Command);
        }
    }

    [Fact]
    public void Every_legacy_slot_still_exists()
    {
        foreach (var (id, _) in LegacySlots) Assert.NotNull(GestureCatalog.FindSlot(id));
    }

    [Fact]
    public void Every_legacy_saved_binding_is_still_honoured()
    {
        foreach (var (slot, _) in LegacySlots)
            foreach (var action in LegacyOffered(slot))
            {
                var resolved = GestureCatalog.ResolveAction(slot, new Dictionary<string, string> { [slot] = action });
                Assert.Equal(action, resolved);
            }
    }

    /// <summary>Why a pre-4.8.0 combination may no longer appear in a dropdown (it is still honoured).</summary>
    private static string? DocumentedExclusion(GestureSlotDefinition slot, GestureActionDefinition action)
    {
        if (slot.Kind == GestureInputKind.Wheel && action.Inputs != GestureInputKinds.Wheel) return "one-shot / drag action on a wheel";
        if (slot.Kind == GestureInputKind.Drag && (action.Inputs & GestureInputKinds.Drag) == 0) return "one-shot action on a drag";
        if (slot.Kind is GestureInputKind.Click or GestureInputKind.DoubleClick && action.Inputs == GestureInputKinds.Drag) return "drag-only action on a click";
        if (slot.Area == GestureAreas.Overlay) return "overlay rows run only overlay actions (others were ignored by the overlay policy)";
        if (action.Id == GestureCatalog.MoveWindow && slot.Area == GestureAreas.Fullscreen) return "no window to move in fullscreen";
        return null;
    }

    [Fact]
    public void Legacy_combinations_stay_offered_unless_a_documented_rule_excludes_them()
    {
        var undocumented = new List<string>();
        foreach (var (slotId, _) in LegacySlots)
        {
            var slot = GestureCatalog.FindSlot(slotId)!;
            foreach (var actionId in LegacyOffered(slotId))
            {
                if (GestureCatalog.IsOffered(slotId, actionId)) continue;
                var action = GestureCatalog.FindAction(actionId)!;
                if (DocumentedExclusion(slot, action) is null) undocumented.Add($"{slotId} -> {actionId}");
            }
        }
        Assert.True(undocumented.Count == 0, "Previously offered combinations dropped without a documented rule:\n" + string.Join("\n", undocumented));
    }

    [Fact]
    public void Folded_choice_lists_map_onto_actions()
    {
        // Slideshow "Left-click during slideshow" choices and the Explorer open choices became actions.
        Assert.Equal(new[] { "Pause / resume slideshow", "Next image", "Previous image", "Stop slideshow", "Do nothing" }, Glide.Core.Settings.SlideshowLeftClickActions.All);
        foreach (var id in new[] { GestureCatalog.Next, GestureCatalog.Previous, GestureCatalog.StopSlideshow, GestureCatalog.PassThrough })
            Assert.True(GestureCatalog.IsOffered("slideshow.leftClick", id), id);
        Assert.True(GestureCatalog.IsOffered("explorer.doubleClick", GestureCatalog.OpenInNewTab));
        Assert.True(GestureCatalog.IsOffered("explorer.middleClick", GestureCatalog.OpenInCurrentTab));
        // Tab double / middle click close: Default closes; Do nothing = the old toggle off.
        Assert.Equal("close tab", GestureCatalog.DefaultBehavior["tabs.middleClick"]);
        Assert.Equal("close tab", GestureCatalog.DefaultBehavior["tabs.doubleClick"]);
        Assert.True(GestureCatalog.IsOffered("tabs.middleClick", GestureCatalog.Nothing));
    }
}
