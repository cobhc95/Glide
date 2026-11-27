namespace Glide.Core.Commands;

/// <summary>
/// Framework-free contract for Glide's configurable pointer gesture slots. UI adapters identify
/// the physical context; this catalogue owns stable slot/action IDs, labels, defaults, the area /
/// input / modifier metadata of every slot and the per-input validity rules of every action.
/// </summary>
public sealed record GestureSlotDefinition(string Id, string Label, string Context, string DefaultActionId)
{
    /// <summary>Area id (see <see cref="GestureCatalog.Areas"/>).</summary>
    public string Area { get; init; } = GestureAreas.Windowed;
    /// <summary>Physical input id (see <see cref="GestureInputs"/>).</summary>
    public string Input { get; init; } = GestureInputs.LeftClick;
    /// <summary>Key held as part of this slot (wheel slots only); <see cref="GestureModifiers.None"/> otherwise.</summary>
    public string Modifier { get; init; } = GestureModifiers.None;
    public GestureInputKind Kind => GestureInputs.KindOf(Input);
    /// <summary>The short row label used inside an area, for example "Ctrl + wheel".</summary>
    public string InputLabel => GestureInputs.Label(Input, Modifier);
}

/// <summary>What kind of physical input a slot is; actions declare the kinds they make sense for.</summary>
public enum GestureInputKind { Click, DoubleClick, Drag, Wheel }

[Flags]
public enum GestureInputKinds
{
    None = 0,
    Click = 1,
    DoubleClick = 2,
    Drag = 4,
    Wheel = 8,
    OneShot = Click | DoubleClick,
    All = Click | DoubleClick | Drag | Wheel
}

public sealed record GestureActionDefinition(string Id, string Label, GlideCommand Command = GlideCommand.None)
{
    /// <summary>Input kinds this action is meaningful for. One-shot commands never apply to a wheel
    /// (it fires many times per second) or a drag (which is continuous).</summary>
    public GestureInputKinds Inputs { get; init; } = GestureInputKinds.OneShot;
    /// <summary>Areas where the action is offered. Null means every area.</summary>
    public IReadOnlyList<string>? Areas { get; init; }
    /// <summary>Group shown in the "All actions" list and as the tag on search matches.</summary>
    public string Category { get; init; } = GestureActionCategories.General;
    public bool NeedsImage { get; init; }
    public bool NeedsSelection { get; init; }
    /// <summary>Destructive actions (delete file, close program) are never part of the catalogue;
    /// the flag exists so a test can keep it that way.</summary>
    public bool Destructive { get; init; }
    /// <summary>Wheel actions whose effect follows the wheel direction.</summary>
    public bool DirectionAware { get; init; }
    /// <summary>The master-registry action this gesture action comes from (AppActionRegistry id).</summary>
    public string? ActionId { get; init; }
}

public sealed record GestureAreaDefinition(string Id, string Label, string Icon);

public static class GestureAreas
{
    public const string Windowed = "windowed";
    public const string Fullscreen = "fullscreen";
    public const string Background = "background";
    public const string Selection = "selection";
    public const string Slideshow = "slideshow";
    public const string Overlay = "overlay";
    public const string Tabs = "tabs";
    public const string TitleBar = "titleBar";
    public const string Explorer = "explorer";
}

public static class GestureInputs
{
    public const string LeftClick = "leftClick";
    public const string RightClick = "rightClick";
    public const string MiddleClick = "middleClick";
    public const string DoubleLeft = "doubleLeft";
    public const string DoubleRight = "doubleRight";
    public const string LeftDrag = "leftDrag";
    public const string RightDrag = "rightDrag";
    public const string MiddleDrag = "middleDrag";
    public const string Wheel = "wheel";
    public const string RightWheel = "rightWheel";
    /// <summary>Mouse side button 1 (XButton1, "Back"). Added in 5.0.0; saved bindings of older files simply have no such slot.
    /// Default (5.0.8): pass-through, not used by Glide.</summary>
    public const string BackButton = "backButton";
    /// <summary>Mouse side button 2 (XButton2, "Forward"). Default (5.0.8): pass-through, not used by Glide.</summary>
    public const string ForwardButton = "forwardButton";

    public static readonly string[] All = [LeftClick, RightClick, MiddleClick, DoubleLeft, DoubleRight, LeftDrag, RightDrag, MiddleDrag, Wheel, RightWheel, BackButton, ForwardButton];

    public static GestureInputKind KindOf(string? input) => input switch
    {
        DoubleLeft or DoubleRight => GestureInputKind.DoubleClick,
        LeftDrag or RightDrag or MiddleDrag => GestureInputKind.Drag,
        Wheel or RightWheel => GestureInputKind.Wheel,
        _ => GestureInputKind.Click
    };

    public static string BaseLabel(string? input) => input switch
    {
        LeftClick => "Left click",
        RightClick => "Right click",
        MiddleClick => "Middle click",
        DoubleLeft => "Double-left click",
        DoubleRight => "Double-right click",
        LeftDrag => "Left-drag",
        RightDrag => "Right-drag",
        MiddleDrag => "Middle-drag",
        Wheel => "Wheel",
        RightWheel => "Right button + wheel",
        BackButton => "Mouse back button",
        ForwardButton => "Mouse forward button",
        _ => input ?? string.Empty
    };

    public static string Label(string? input, string? modifier)
    {
        var baseLabel = BaseLabel(input);
        var mod = GestureModifiers.Normalize(modifier);
        if (mod == GestureModifiers.None) return baseLabel;
        var key = GestureModifiers.Label(mod);
        return input == Wheel ? $"{key} + wheel" : $"{key} + {baseLabel.ToLowerInvariant()}";
    }
}

public static class GestureModifiers
{
    public const string None = "none";
    public const string Ctrl = "ctrl";
    public const string Shift = "shift";
    public const string Alt = "alt";
    public static readonly string[] All = [None, Ctrl, Shift, Alt];

    /// <summary>One modifier per gesture: Ctrl wins over Shift, Shift over Alt.</summary>
    public static string From(bool ctrl, bool shift, bool alt) => ctrl ? Ctrl : shift ? Shift : alt ? Alt : None;

    public static string Normalize(string? modifier) =>
        All.FirstOrDefault(x => string.Equals(x, modifier, StringComparison.OrdinalIgnoreCase)) ?? None;

    public static string Label(string? modifier) => Normalize(modifier) switch
    {
        Ctrl => "Ctrl",
        Shift => "Shift",
        Alt => "Alt",
        _ => "None"
    };
}

public static class GestureActionCategories
{
    public const string General = "General";
    public const string Navigation = "Navigation";
    public const string View = "View";
    public const string Window = "Window";
    public const string Tabs = "Tabs";
    public const string File = "File";
    public const string Slideshow = "Slideshow";
    public const string Selection = "Selection";
    public const string Overlay = "Window in Window";
    public const string Explorer = "Explorer";
    /// <summary>Generated "Toggle: ..." / "Cycle: ..." setting actions (see AppActionRegistry).</summary>
    public const string Settings = "Settings";
    public static readonly string[] Order = [General, Navigation, View, Selection, Window, Tabs, Slideshow, File, Overlay, Explorer, Settings];
}

/// <summary>One user-defined gesture: an input plus an optional held key in an area, mapped to an action.</summary>
public sealed class CustomGesture
{
    public string Area { get; set; } = GestureAreas.Windowed;
    public string Input { get; set; } = GestureInputs.MiddleClick;
    public string Modifier { get; set; } = GestureModifiers.None;
    public string Action { get; set; } = GestureCatalog.Nothing;

    public CustomGesture Clone() => new() { Area = Area, Input = Input, Modifier = Modifier, Action = Action };
}

public static class GestureCatalog
{
    public const string Default = "default";
    public const string Nothing = "nothing";
    public const string Next = "next";
    public const string Previous = "previous";
    public const string ZoomIn = "zoomIn";
    public const string ZoomOut = "zoomOut";
    public const string Fit = "fit";
    public const string Actual = "actual";
    public const string ToggleFitActual = "toggleFitActual";
    public const string Fullscreen = "fullscreen";
    public const string ContextMenu = "contextMenu";
    public const string ClearSelection = "clearSelection";
    public const string ToggleInfo = "toggleInfo";
    public const string Settings = "settings";
    public const string Pan = "pan";
    public const string CreateSelection = "createSelection";
    public const string MoveWindow = "moveWindow";
    public const string ResetOverlayZoom = "resetOverlayZoom";
    public const string BringOverlayFront = "bringOverlayFront";
    // Direction-aware wheel actions (meaningful for wheel slots only). Paired directions are
    // merged into one item whose effect follows the wheel direction: up = zoom in / previous,
    // down = zoom out / next. The "(reversed)" variants invert that order.
    public const string ZoomWheel = "zoomWheel";
    public const string BrowseWheel = "browseWheel";
    public const string ZoomWheelReversed = "zoomWheelReversed";
    public const string BrowseWheelReversed = "browseWheelReversed";

    // 4.8.0 additions.
    /// <summary>The slot does not claim the input: the click/drag continues as if this context
    /// (fullscreen click navigation, a selection, a running slideshow) were not there.</summary>
    public const string PassThrough = "passThrough";
    public const string FirstImage = "firstImage";
    public const string LastImage = "lastImage";
    public const string JumpForward10 = "jumpForward10";
    public const string JumpBack10 = "jumpBack10";
    public const string NextFolder = "nextFolder";
    public const string PreviousFolder = "previousFolder";
    public const string Zoom200 = "zoom200";
    public const string RotateLeft = "rotateLeft";
    public const string RotateRight = "rotateRight";
    public const string FlipHorizontal = "flipHorizontal";
    public const string FlipVertical = "flipVertical";
    public const string NewTab = "newTab";
    public const string CloseTab = "closeTab";
    public const string ReopenClosedTab = "reopenClosedTab";
    public const string DuplicateTab = "duplicateTab";
    public const string DetachTab = "detachTab";
    public const string ToggleStatusBar = "toggleStatusBar";
    public const string ToggleAlwaysOnTop = "toggleAlwaysOnTop";
    public const string MaximizeRestore = "maximizeRestore";
    public const string MinimizeWindow = "minimizeWindow";
    public const string ToggleSlideshow = "toggleSlideshow";
    public const string StopSlideshow = "stopSlideshow";
    public const string CopyImage = "copyImage";
    public const string ShowInExplorer = "showInExplorer";
    public const string OpenInCurrentTab = "openInCurrentTab";
    public const string OpenInNewTab = "openInNewTab";
    public const string PanWheel = "panWheel";
    public const string PanWheelHorizontal = "panWheelHorizontal";
    public const string JumpWheel = "jumpWheel";
    public const string FolderWheel = "folderWheel";
    public const string RotateWheel = "rotateWheel";
    public const string TabWheel = "tabWheel";
    // 5.0.0 browser-style tab history (the registry action ids double as gesture ids).
    public const string NavigateBack = "nav.back";
    public const string NavigateForward = "nav.forward";

    public const int MaxCustomGesturesPerArea = 6;

    /// <summary>
    /// What "Default" runs for a slot whose Glide behaviour is a registry action rather than surface-owned code: the
    /// mouse side buttons (XButton1 / XButton2) go Back / Forward in every area that has them (5.0.0; the first 5.0
    /// build left them unbound). Null for every other slot, whose Default is handled by the surface itself.
    /// </summary>
    /// Since 5.0.8 the side buttons are pass-through by default ("Default (not used by Glide)"): Glide leaves them entirely
    /// to Windows / the mouse software, so this returns null for every slot. Kept as the hook for future registry defaults.
    /// Round 4 (A1): in the Explorer area they go Back / Forward in the tab history again by default (every other area
    /// stays pass-through; the image view handles "Back to the folder it was opened from" itself, see MainWindow).
    public static string? DefaultActionFor(string? slotId) => slotId switch
    {
        "explorer.backButton" => NavigateBack,
        "explorer.forwardButton" => NavigateForward,
        _ => null
    };

    public static IReadOnlyList<GestureAreaDefinition> Areas { get; } = new GestureAreaDefinition[]
    {
        new(GestureAreas.Windowed, "Windowed image", "Image"),
        new(GestureAreas.Fullscreen, "Fullscreen image", "Fullscreen"),
        new(GestureAreas.Background, "Empty background", "Fit"),
        new(GestureAreas.Selection, "Selection", "Copy"),
        new(GestureAreas.Slideshow, "Slideshow", "Play"),
        new(GestureAreas.Overlay, "Window in Window", "Overlays"),
        new(GestureAreas.Tabs, "Tabs", "Tabs"),
        new(GestureAreas.TitleBar, "Title bar", "Interface"),
        new(GestureAreas.Explorer, "Explorer", "Browser")
    };

    public static string AreaLabel(string? area) =>
        Areas.FirstOrDefault(a => string.Equals(a.Id, area, StringComparison.OrdinalIgnoreCase))?.Label ?? area ?? string.Empty;

    /// <summary>
    /// Every action a gesture row can offer. Read from the master <see cref="AppActionRegistry"/> (5.0.x): the 56
    /// historical actions keep their ids, labels, areas and order; every other registry action that may be bound
    /// to a pointer input (commands and generated setting toggles) follows, so a new feature appears here
    /// automatically. Lazily built because the registry itself reads the settings catalogue, which reads the slots.
    /// </summary>
    public static IReadOnlyList<GestureActionDefinition> Actions => ActionsHolder.Value;

    private static class ActionsHolder
    {
        public static readonly IReadOnlyList<GestureActionDefinition> Value = AppActionRegistry.All
            .Where(a => a.Gesture is not null)
            .OrderBy(a => a.Gesture!.Order)
            .Select(a => new GestureActionDefinition(a.Gesture!.Id, a.Gesture.Label, a.Kind == AppActionKind.Command ? a.Command : GlideCommand.None)
            {
                Inputs = a.Gesture.Inputs,
                Areas = a.Gesture.Areas,
                Category = a.Gesture.Category,
                NeedsImage = a.Gesture.NeedsImage,
                NeedsSelection = a.Gesture.NeedsSelection,
                Destructive = a.Destructive,
                DirectionAware = a.Gesture.DirectionAware,
                ActionId = a.Id
            })
            .ToArray();
    }

    /// <summary>The 56 gesture action ids that existed before the registry, in their historical order. Saved
    /// gesture bindings use these ids; the compatibility tests keep every one of them resolvable.</summary>
    public static readonly string[] LegacyActionIds =
    [
        Default, Nothing, Next, Previous, ZoomIn, ZoomOut, Fit, Actual, ToggleFitActual, Fullscreen, ContextMenu, ClearSelection,
        ToggleInfo, Settings, Pan, CreateSelection, MoveWindow, ResetOverlayZoom, BringOverlayFront, ZoomWheel, ZoomWheelReversed,
        BrowseWheel, BrowseWheelReversed, PassThrough, FirstImage, LastImage, JumpForward10, JumpBack10, NextFolder, PreviousFolder,
        Zoom200, RotateLeft, RotateRight, FlipHorizontal, FlipVertical, NewTab, CloseTab, ReopenClosedTab, DuplicateTab, DetachTab,
        ToggleStatusBar, ToggleAlwaysOnTop, MaximizeRestore, MinimizeWindow, ToggleSlideshow, StopSlideshow, CopyImage, ShowInExplorer,
        OpenInCurrentTab, OpenInNewTab, PanWheel, PanWheelHorizontal, JumpWheel, FolderWheel, RotateWheel, TabWheel
    ];

    private static GestureSlotDefinition S(string id, string area, string input, string modifier = GestureModifiers.None)
    {
        var label = $"{AreaLabel(area)}: {GestureInputs.Label(input, modifier)}";
        return new GestureSlotDefinition(id, label, AreaLabel(area), Default) { Area = area, Input = input, Modifier = modifier };
    }

    public static IReadOnlyList<GestureSlotDefinition> Slots { get; } = new[]
    {
        S("windowed.leftClickImage", GestureAreas.Windowed, GestureInputs.LeftClick),
        S("windowed.rightClickImage", GestureAreas.Windowed, GestureInputs.RightClick),
        S("windowed.middleClickImage", GestureAreas.Windowed, GestureInputs.MiddleClick),
        S("windowed.doubleLeftImage", GestureAreas.Windowed, GestureInputs.DoubleLeft),
        S("windowed.doubleRightImage", GestureAreas.Windowed, GestureInputs.DoubleRight),
        S("windowed.backButtonImage", GestureAreas.Windowed, GestureInputs.BackButton),
        S("windowed.forwardButtonImage", GestureAreas.Windowed, GestureInputs.ForwardButton),
        S("drag.leftImage", GestureAreas.Windowed, GestureInputs.LeftDrag),
        S("drag.rightImage", GestureAreas.Windowed, GestureInputs.RightDrag),
        S("drag.middleImage", GestureAreas.Windowed, GestureInputs.MiddleDrag),
        S("wheel.windowed", GestureAreas.Windowed, GestureInputs.Wheel),
        S("wheel.windowedCtrl", GestureAreas.Windowed, GestureInputs.Wheel, GestureModifiers.Ctrl),
        S("wheel.windowedShift", GestureAreas.Windowed, GestureInputs.Wheel, GestureModifiers.Shift),
        S("wheel.windowedAlt", GestureAreas.Windowed, GestureInputs.Wheel, GestureModifiers.Alt),
        // Default = ordinary wheel behaviour. Any other action consumes the right button, so the
        // context menu does not open when it is released after scrolling.
        S("wheel.rightHeld", GestureAreas.Windowed, GestureInputs.RightWheel),

        S("fullscreen.leftClickImage", GestureAreas.Fullscreen, GestureInputs.LeftClick),
        S("fullscreen.rightClickImage", GestureAreas.Fullscreen, GestureInputs.RightClick),
        S("fullscreen.middleClickImage", GestureAreas.Fullscreen, GestureInputs.MiddleClick),
        S("fullscreen.doubleLeftImage", GestureAreas.Fullscreen, GestureInputs.DoubleLeft),
        S("fullscreen.backButtonImage", GestureAreas.Fullscreen, GestureInputs.BackButton),
        S("fullscreen.forwardButtonImage", GestureAreas.Fullscreen, GestureInputs.ForwardButton),
        S("wheel.fullscreen", GestureAreas.Fullscreen, GestureInputs.Wheel),
        S("wheel.fullscreenCtrl", GestureAreas.Fullscreen, GestureInputs.Wheel, GestureModifiers.Ctrl),
        S("wheel.fullscreenShift", GestureAreas.Fullscreen, GestureInputs.Wheel, GestureModifiers.Shift),
        S("wheel.fullscreenAlt", GestureAreas.Fullscreen, GestureInputs.Wheel, GestureModifiers.Alt),

        S("background.doubleLeft", GestureAreas.Background, GestureInputs.DoubleLeft),
        S("drag.leftEmpty", GestureAreas.Background, GestureInputs.LeftDrag),
        S("drag.rightEmpty", GestureAreas.Background, GestureInputs.RightDrag),

        S("selection.leftClick", GestureAreas.Selection, GestureInputs.LeftClick),
        S("selection.rightClick", GestureAreas.Selection, GestureInputs.RightClick),

        S("slideshow.leftClick", GestureAreas.Slideshow, GestureInputs.LeftClick),
        S("slideshow.rightClick", GestureAreas.Slideshow, GestureInputs.RightClick),
        S("slideshow.middleClick", GestureAreas.Slideshow, GestureInputs.MiddleClick),
        S("slideshow.wheel", GestureAreas.Slideshow, GestureInputs.Wheel),

        S("overlay.middleClick", GestureAreas.Overlay, GestureInputs.MiddleClick),
        S("overlay.doubleClick", GestureAreas.Overlay, GestureInputs.DoubleLeft),
        S("overlay.rightDrag", GestureAreas.Overlay, GestureInputs.RightDrag),
        S("overlay.wheel", GestureAreas.Overlay, GestureInputs.Wheel),
        S("overlay.ctrlWheel", GestureAreas.Overlay, GestureInputs.Wheel, GestureModifiers.Ctrl),

        S("tabs.rightClick", GestureAreas.Tabs, GestureInputs.RightClick),
        S("tabs.middleClick", GestureAreas.Tabs, GestureInputs.MiddleClick),
        S("tabs.doubleClick", GestureAreas.Tabs, GestureInputs.DoubleLeft),
        S("tabs.backButton", GestureAreas.Tabs, GestureInputs.BackButton),
        S("tabs.forwardButton", GestureAreas.Tabs, GestureInputs.ForwardButton),
        S("tabs.wheel", GestureAreas.Tabs, GestureInputs.Wheel),

        S("titleBar.rightClick", GestureAreas.TitleBar, GestureInputs.RightClick),
        S("titleBar.middleClick", GestureAreas.TitleBar, GestureInputs.MiddleClick),
        S("titleBar.doubleClick", GestureAreas.TitleBar, GestureInputs.DoubleLeft),
        S("titleBar.wheel", GestureAreas.TitleBar, GestureInputs.Wheel),

        S("explorer.middleClick", GestureAreas.Explorer, GestureInputs.MiddleClick),
        S("explorer.doubleClick", GestureAreas.Explorer, GestureInputs.DoubleLeft),
        // Round 4 C3: modifier + double-click on an item.
        S("explorer.altDoubleClick", GestureAreas.Explorer, GestureInputs.DoubleLeft, GestureModifiers.Alt),
        S("explorer.ctrlDoubleClick", GestureAreas.Explorer, GestureInputs.DoubleLeft, GestureModifiers.Ctrl),
        S("explorer.shiftDoubleClick", GestureAreas.Explorer, GestureInputs.DoubleLeft, GestureModifiers.Shift),
        S("explorer.backButton", GestureAreas.Explorer, GestureInputs.BackButton),
        S("explorer.forwardButton", GestureAreas.Explorer, GestureInputs.ForwardButton),
        S("explorer.wheel", GestureAreas.Explorer, GestureInputs.Wheel)
    };

    /// <summary>
    /// What "Default" does for each slot, shown in brackets as "Default (...)". These describe
    /// Glide's built-in behaviour, which equals the behaviour of the folded 4.7 mouse toggles at
    /// their stock values; a user who had changed one of those toggles gets an explicit binding
    /// through the one-time settings migration instead (see <see cref="GestureLegacyMigration"/>).
    /// </summary>
    public static IReadOnlyDictionary<string, string> DefaultBehavior { get; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["windowed.leftClickImage"] = "nothing",
        ["windowed.rightClickImage"] = "context menu",
        ["windowed.middleClickImage"] = "nothing",
        ["windowed.doubleLeftImage"] = "enter fullscreen",
        ["windowed.doubleRightImage"] = "nothing",
        ["windowed.backButtonImage"] = "not used by Glide",
        ["windowed.forwardButtonImage"] = "not used by Glide",
        ["drag.leftImage"] = "draw a selection",
        ["drag.rightImage"] = "per right-drag mode",
        ["drag.middleImage"] = "nothing",
        ["wheel.windowed"] = "previous / next",
        ["wheel.windowedCtrl"] = "zoom",
        ["wheel.windowedShift"] = "as wheel",
        ["wheel.windowedAlt"] = "as wheel",
        ["wheel.rightHeld"] = "as wheel",
        ["fullscreen.leftClickImage"] = "next image",
        ["fullscreen.rightClickImage"] = "previous image",
        ["fullscreen.middleClickImage"] = "nothing",
        ["fullscreen.doubleLeftImage"] = "nothing",
        ["fullscreen.backButtonImage"] = "not used by Glide",
        ["fullscreen.forwardButtonImage"] = "not used by Glide",
        ["wheel.fullscreen"] = "previous / next",
        ["wheel.fullscreenCtrl"] = "zoom",
        ["wheel.fullscreenShift"] = "as wheel",
        ["wheel.fullscreenAlt"] = "as wheel",
        ["background.doubleLeft"] = "enter fullscreen",
        ["drag.leftEmpty"] = "move the window",
        ["drag.rightEmpty"] = "context menu",
        ["selection.leftClick"] = "zoom into the selection",
        ["selection.rightClick"] = "zoom out",
        ["slideshow.leftClick"] = "pause / resume",
        ["slideshow.rightClick"] = "stop while playing",
        ["slideshow.middleClick"] = "as without a slideshow",
        ["slideshow.wheel"] = "as without a slideshow",
        ["overlay.middleClick"] = "reset overlay zoom",
        ["overlay.doubleClick"] = "reset overlay zoom",
        ["overlay.rightDrag"] = "pan zoomed overlay",
        ["overlay.wheel"] = "zoom overlay",
        ["overlay.ctrlWheel"] = "zoom overlay",
        ["tabs.rightClick"] = "tab menu",
        ["tabs.middleClick"] = "close tab",
        ["tabs.doubleClick"] = "close tab",
        ["tabs.backButton"] = "not used by Glide",
        ["tabs.forwardButton"] = "not used by Glide",
        ["tabs.wheel"] = "scroll the tab strip",
        ["titleBar.rightClick"] = "menu in fullscreen / overlay mode",
        ["titleBar.middleClick"] = "new tab",
        ["titleBar.doubleClick"] = "maximize / restore",
        ["titleBar.wheel"] = "nothing",
        ["explorer.middleClick"] = "open in new tab",
        ["explorer.doubleClick"] = "open in current tab",
        ["explorer.altDoubleClick"] = "properties",
        ["explorer.ctrlDoubleClick"] = "select only",
        ["explorer.shiftDoubleClick"] = "select only",
        ["explorer.backButton"] = "back",
        ["explorer.forwardButton"] = "forward",
        ["explorer.wheel"] = "scroll the list"
    };

    /// <summary>Top 3-4 actions per slot, shown as "Suggested" chips. Filtered against the slot's valid actions.</summary>
    private static readonly Dictionary<string, string[]> SuggestedBySlot = new(StringComparer.OrdinalIgnoreCase)
    {
        ["windowed.leftClickImage"] = [Next, ToggleFitActual, Fit],
        ["windowed.rightClickImage"] = [ContextMenu, Previous, ToggleFitActual],
        ["windowed.middleClickImage"] = [ToggleFitActual, Fullscreen, Actual],
        ["windowed.doubleLeftImage"] = [Fullscreen, ToggleFitActual, Actual],
        ["windowed.doubleRightImage"] = [RotateRight, Previous, Fit],
        ["windowed.backButtonImage"] = [NavigateBack, Previous, FirstImage],
        ["windowed.forwardButtonImage"] = [NavigateForward, Next, LastImage],
        ["fullscreen.backButtonImage"] = [NavigateBack, Previous, FirstImage],
        ["fullscreen.forwardButtonImage"] = [NavigateForward, Next, LastImage],
        ["explorer.backButton"] = [NavigateBack, "explorer.up", NavigateForward],
        ["explorer.forwardButton"] = [NavigateForward, NavigateBack, "explorer.up"],
        ["tabs.backButton"] = [NavigateBack, CloseTab, ReopenClosedTab],
        ["tabs.forwardButton"] = [NavigateForward, NewTab, DuplicateTab],
        ["fullscreen.leftClickImage"] = [Next, PassThrough, ToggleSlideshow],
        ["fullscreen.rightClickImage"] = [Previous, ContextMenu, PassThrough],
        ["fullscreen.middleClickImage"] = [ToggleFitActual, Fullscreen, ToggleSlideshow],
        ["fullscreen.doubleLeftImage"] = [Fullscreen, ToggleFitActual, ToggleSlideshow],
        ["background.doubleLeft"] = [Fullscreen, Fit, NewTab],
        ["selection.leftClick"] = [ClearSelection, CopyImage, PassThrough],
        ["selection.rightClick"] = [ContextMenu, ClearSelection, PassThrough],
        ["slideshow.leftClick"] = [Next, ToggleSlideshow, StopSlideshow, PassThrough],
        ["slideshow.rightClick"] = [Previous, ToggleSlideshow, StopSlideshow, PassThrough],
        ["slideshow.middleClick"] = [ToggleSlideshow, StopSlideshow, Fullscreen],
        ["overlay.middleClick"] = [ResetOverlayZoom, BringOverlayFront],
        ["overlay.doubleClick"] = [ResetOverlayZoom, BringOverlayFront],
        ["tabs.rightClick"] = [ContextMenu, CloseTab, DuplicateTab],
        ["tabs.middleClick"] = [CloseTab, DuplicateTab, DetachTab],
        ["tabs.doubleClick"] = [CloseTab, DuplicateTab, DetachTab],
        ["titleBar.rightClick"] = [ContextMenu, ToggleAlwaysOnTop, MinimizeWindow],
        ["titleBar.middleClick"] = [NewTab, ReopenClosedTab, MinimizeWindow],
        ["titleBar.doubleClick"] = [MaximizeRestore, Fullscreen, NewTab],
        ["explorer.middleClick"] = [OpenInNewTab, OpenInCurrentTab],
        ["explorer.doubleClick"] = [OpenInCurrentTab, OpenInNewTab],
        ["explorer.altDoubleClick"] = ["explorer.itemProperties", OpenInNewTab, OpenInCurrentTab],
        ["explorer.ctrlDoubleClick"] = [OpenInNewTab, OpenInCurrentTab, "explorer.itemProperties"],
        ["explorer.shiftDoubleClick"] = [OpenInNewTab, OpenInCurrentTab, "explorer.itemProperties"]
    };

    /// <summary>
    /// What a slot does when it is left at Default, for the slots whose Default runs a registry action (round 4 C3:
    /// Alt+double-click on an Explorer item shows its properties). Other slots: null (their owner's built-in behaviour).
    /// </summary>
    public static string? DefaultRunAction(string? slotId) =>
        string.Equals(slotId, "explorer.altDoubleClick", StringComparison.OrdinalIgnoreCase) ? "explorer.itemProperties" : null;

    /// <summary>The Explorer item double-click slot for the held keys: the plain row, or its Alt / Ctrl / Shift row.</summary>
    public static string ExplorerDoubleClickSlot(string? modifier) =>
        SlotFor(GestureAreas.Explorer, GestureInputs.DoubleLeft, modifier ?? GestureModifiers.None)?.Id ?? "explorer.doubleClick";

    private static readonly string[] SuggestedDrag = [Pan, CreateSelection, MoveWindow];
    private static readonly string[] SuggestedWheel = [ZoomWheel, BrowseWheel, PanWheelHorizontal, PanWheel];
    private static readonly string[] SuggestedChromeWheel = [TabWheel, BrowseWheel, ZoomWheel];

    /// <summary>The built-in slot of an area + input + held key, or null (for example the side buttons have no slot in the
    /// Overlay area).</summary>
    public static GestureSlotDefinition? SlotFor(string area, string input, string modifier = GestureModifiers.None) =>
        Slots.FirstOrDefault(s => string.Equals(s.Area, area, StringComparison.OrdinalIgnoreCase) && s.Input == input &&
                                  s.Modifier == GestureModifiers.Normalize(modifier));

    public static GestureSlotDefinition? FindSlot(string? slotId) =>
        slotId is null ? null : Slots.FirstOrDefault(x => string.Equals(x.Id, slotId, StringComparison.OrdinalIgnoreCase));

    public static GestureActionDefinition? FindAction(string? actionId) =>
        actionId is null ? null : Actions.FirstOrDefault(x => string.Equals(x.Id, actionId, StringComparison.OrdinalIgnoreCase));

    public static IReadOnlyList<GestureSlotDefinition> SlotsForArea(string area) =>
        Slots.Where(s => string.Equals(s.Area, area, StringComparison.OrdinalIgnoreCase)).ToArray();

    public static bool IsWheelSlot(string? slotId) =>
        FindSlot(slotId) is { } slot ? slot.Kind == GestureInputKind.Wheel
            : slotId is not null && slotId.Contains("wheel", StringComparison.OrdinalIgnoreCase);

    private static GestureInputKinds Flag(GestureInputKind kind) => kind switch
    {
        GestureInputKind.Click => GestureInputKinds.Click,
        GestureInputKind.DoubleClick => GestureInputKinds.DoubleClick,
        GestureInputKind.Drag => GestureInputKinds.Drag,
        _ => GestureInputKinds.Wheel
    };

    /// <summary>True when the action is valid for an input of this kind in this area.</summary>
    public static bool IsActionValidFor(GestureActionDefinition action, string area, GestureInputKind kind)
    {
        if (action.Destructive) return false;
        if (action.Id == Default) return true;
        if ((action.Inputs & Flag(kind)) == 0) return false;
        if (action.Areas is { } areas && !areas.Contains(area, StringComparer.OrdinalIgnoreCase)) return false;
        return true;
    }

    /// <summary>
    /// The actions a slot offers: only those valid for its input kind and area (wheel rows offer
    /// direction-aware wheel actions only, drag rows continuous actions only, click rows one-shot
    /// commands only). "Ignore here" is offered only on the click slots whose default claims the
    /// press (fullscreen click navigation, selection zoom, slideshow left/right click).
    /// </summary>
    public static IReadOnlyList<GestureActionDefinition> ActionsForSlot(string? slotId)
    {
        var slot = FindSlot(slotId);
        if (slot is null)
        {
            // Unknown (legacy) slot ids keep the historical wheel / non-wheel split.
            var wheel = slotId is not null && slotId.Contains("wheel", StringComparison.OrdinalIgnoreCase);
            return Actions.Where(a => a.Id is Default or Nothing || (wheel ? a.Inputs == GestureInputKinds.Wheel : (a.Inputs & GestureInputKinds.OneShot) != 0)).ToArray();
        }
        return Actions.Where(a => IsActionValidFor(a, slot.Area, slot.Kind) &&
                                  (a.Id != PassThrough || PassThroughSlots.Contains(slot.Id)) &&
                                  IsAllowedForSpecialSlot(slot, a.Id)).ToArray();
    }

    private static readonly HashSet<string> PassThroughSlots = new(StringComparer.OrdinalIgnoreCase)
    {
        "fullscreen.leftClickImage", "fullscreen.rightClickImage", "selection.leftClick", "selection.rightClick",
        "slideshow.leftClick", "slideshow.rightClick"
    };

    private static bool IsAllowedForSpecialSlot(GestureSlotDefinition slot, string actionId)
    {
        // The overlay right-drag pans overlay content or does nothing; no other drag applies there.
        if (slot.Id == "overlay.rightDrag") return actionId is Default or Nothing;
        // Explorer open actions only on the item clicks (Enter follows the double-click row).
        // They act on the item under the pointer, so the side buttons (which do not select an item) never offer them.
        if (actionId is OpenInCurrentTab or OpenInNewTab)
            return slot.Area == GestureAreas.Explorer && slot.Kind != GestureInputKind.Wheel && slot.Input is not (GestureInputs.BackButton or GestureInputs.ForwardButton);
        return true;
    }

    /// <summary>Suggested chips for a slot (valid actions only; never Default / Do nothing).</summary>
    public static IReadOnlyList<GestureActionDefinition> SuggestedForSlot(string? slotId)
    {
        var slot = FindSlot(slotId);
        if (slot is null) return Array.Empty<GestureActionDefinition>();
        string[] ids = SuggestedBySlot.TryGetValue(slot.Id, out var configured) ? configured
            : slot.Kind == GestureInputKind.Drag ? SuggestedDrag
            : slot.Kind == GestureInputKind.Wheel ? (slot.Area is GestureAreas.Tabs or GestureAreas.TitleBar or GestureAreas.Explorer or GestureAreas.Overlay ? SuggestedChromeWheel : SuggestedWheel)
            : [Next, Previous, ToggleFitActual, Fullscreen];
        var valid = ActionsForSlot(slot.Id);
        if (slot.Area == GestureAreas.Overlay && slot.Kind == GestureInputKind.Wheel) ids = [ZoomWheel, ZoomWheelReversed];
        return ids.Select(id => valid.FirstOrDefault(a => a.Id == id)).Where(a => a is not null).Select(a => a!)
            .Distinct().Take(4).ToArray();
    }

    /// <summary>True when <paramref name="actionId"/> is offered by the slot's dropdown.</summary>
    public static bool IsOffered(string slotId, string? actionId) =>
        ActionsForSlot(slotId).Any(a => string.Equals(a.Id, actionId, StringComparison.OrdinalIgnoreCase));

    /// <summary>Migrate a stored binding to the item the slot can offer. Legacy single-direction
    /// values on wheel slots become the merged direction-aware items ("Zoom in" and "Zoom out"
    /// both become "Zoom in/out"; "Next image"/"Previous image" both become "Previous/next image").
    /// Wheel-only items stored on non-wheel slots become Default. Any other stored action is kept
    /// exactly as saved, even when the slot no longer offers it in its dropdown, so a binding made
    /// before 4.8.0 keeps working.</summary>
    public static string NormalizeActionForSlot(string? slotId, string? actionId)
    {
        var normalized = NormalizeAction(actionId);
        if (IsWheelSlot(slotId))
        {
            return normalized switch
            {
                ZoomIn or ZoomOut or ZoomWheel => ZoomWheel,
                ZoomWheelReversed => ZoomWheelReversed,
                Next or Previous or BrowseWheel => BrowseWheel,
                BrowseWheelReversed => BrowseWheelReversed,
                _ => normalized
            };
        }
        if (FindAction(normalized) is { Inputs: GestureInputKinds.Wheel }) return Default;
        return normalized;
    }

    public static Dictionary<string, string> CreateDefaultMap() =>
        Slots.ToDictionary(x => x.Id, x => x.DefaultActionId, StringComparer.OrdinalIgnoreCase);

    public static string NormalizeAction(string? actionId) => FindAction(actionId)?.Id ?? Default;

    public static string ResolveAction(string slotId, IReadOnlyDictionary<string, string>? bindings)
    {
        if (bindings is not null && bindings.TryGetValue(slotId, out var action)) return NormalizeActionForSlot(slotId, action);
        return FindSlot(slotId)?.DefaultActionId ?? Default;
    }

    /// <summary>
    /// Resolves a slot for a physical input that happened while <paramref name="modifier"/> was held.
    /// A matching custom gesture (same area, input and held key; first match wins) overrides the
    /// built-in slot binding.
    /// </summary>
    public static string ResolveAction(string slotId, IReadOnlyDictionary<string, string>? bindings,
        IReadOnlyList<CustomGesture>? customGestures, string? modifier)
    {
        if (customGestures is { Count: > 0 } && FindSlot(slotId) is { } slot &&
            ResolveCustom(slot.Area, slot.Input, modifier, customGestures) is { } custom)
            return NormalizeActionForSlot(slotId, custom);
        return ResolveAction(slotId, bindings);
    }

    /// <summary>First custom gesture for this area + input + held key, honouring the per-area cap.</summary>
    public static string? ResolveCustom(string area, string input, string? modifier, IReadOnlyList<CustomGesture>? customGestures)
    {
        if (customGestures is null || customGestures.Count == 0) return null;
        var mod = GestureModifiers.Normalize(modifier);
        var seen = 0;
        foreach (var custom in customGestures)
        {
            if (custom is null || !string.Equals(custom.Area, area, StringComparison.OrdinalIgnoreCase)) continue;
            if (++seen > MaxCustomGesturesPerArea) break;
            if (!string.Equals(custom.Input, input, StringComparison.OrdinalIgnoreCase)) continue;
            if (GestureModifiers.Normalize(custom.Modifier) != mod) continue;
            var action = NormalizeAction(custom.Action);
            if (action == Default) continue;
            return action;
        }
        return null;
    }

    /// <summary>Inputs a custom gesture can use in an area: the inputs of the area's built-in rows
    /// (the right-button + wheel chord is excluded because the hold key would be ambiguous).</summary>
    public static IReadOnlyList<string> CustomInputsForArea(string area) =>
        GestureInputs.All.Where(input => input != GestureInputs.RightWheel &&
            Slots.Any(s => string.Equals(s.Area, area, StringComparison.OrdinalIgnoreCase) && s.Input == input)).ToArray();

    /// <summary>Actions a custom gesture may use: the valid actions of the matching built-in row, minus Default.</summary>
    public static IReadOnlyList<GestureActionDefinition> ActionsForCustom(string area, string input)
    {
        var slot = Slots.FirstOrDefault(s => string.Equals(s.Area, area, StringComparison.OrdinalIgnoreCase) && s.Input == input);
        return slot is null ? Array.Empty<GestureActionDefinition>() : ActionsForSlot(slot.Id).Where(a => a.Id != Default).ToArray();
    }

    /// <summary>Drops invalid entries and keeps at most <see cref="MaxCustomGesturesPerArea"/> per area.</summary>
    public static List<CustomGesture> NormalizeCustomGestures(IEnumerable<CustomGesture>? customGestures)
    {
        var result = new List<CustomGesture>();
        if (customGestures is null) return result;
        foreach (var custom in customGestures)
        {
            if (custom is null) continue;
            var area = Areas.FirstOrDefault(a => string.Equals(a.Id, custom.Area, StringComparison.OrdinalIgnoreCase))?.Id;
            if (area is null) continue;
            var input = CustomInputsForArea(area).FirstOrDefault(i => string.Equals(i, custom.Input, StringComparison.OrdinalIgnoreCase));
            if (input is null) continue;
            if (result.Count(c => c.Area == area) >= MaxCustomGesturesPerArea) continue;
            var action = NormalizeAction(custom.Action);
            if (action == Default) action = Nothing;
            result.Add(new CustomGesture { Area = area, Input = input, Modifier = GestureModifiers.Normalize(custom.Modifier), Action = action });
        }
        return result;
    }

    public static GlideCommand CommandForAction(string actionId) => FindAction(actionId)?.Command ?? GlideCommand.None;
}
