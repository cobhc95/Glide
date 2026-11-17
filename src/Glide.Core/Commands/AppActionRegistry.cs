using Glide.Core.Settings;

namespace Glide.Core.Commands;

/// <summary>What an entry of the master action list is.</summary>
public enum AppActionKind
{
    /// <summary>Runs a <see cref="GlideCommand"/> through the main window's command dispatcher.</summary>
    Command,
    /// <summary>Flips a catalogued boolean setting through the normal settings pipeline.</summary>
    SettingToggle,
    /// <summary>Moves a catalogued choice setting to its next option through the normal settings pipeline.</summary>
    SettingCycle,
    /// <summary>A pointer-only behaviour (pan, draw a selection, direction-aware wheel...). Offered to gestures only.</summary>
    GestureOnly,
    /// <summary>A fixed, non-editable shortcut owned by a specific surface (Explorer list keys, slideshow Space...).
    /// Listed so it is visible and searchable in the hotkey list, never rebindable.</summary>
    FixedShortcut
}

/// <summary>Which input surfaces may trigger an action.</summary>
[Flags]
public enum AppActionInputs
{
    None = 0,
    Hotkey = 1,
    Gesture = 2,
    TitleBarButton = 4,
    StatusBarButton = 8,
    ContextMenu = 16,
    All = Hotkey | Gesture | TitleBarButton | StatusBarButton | ContextMenu
}

/// <summary>Where an action is meaningful (documentation, gesture areas and the hotkey scope note).</summary>
[Flags]
public enum AppActionContexts
{
    None = 0,
    Viewer = 1,
    Home = 2,
    Explorer = 4,
    Overlay = 8,
    Everywhere = Viewer | Home | Explorer | Overlay
}

/// <summary>One editable row of the hotkey list. Most actions have exactly one, whose id equals the action id;
/// a few (Next / Previous image) keep several legacy rows so saved bindings load unchanged.</summary>
public sealed record AppHotkeySlot(string Id, string Label, string[] DefaultShortcuts);

/// <summary>How an action appears in the gesture action picker. <see cref="Id"/> is the id saved in gesture
/// bindings (legacy short ids such as "next" are kept so older settings files load unchanged).</summary>
public sealed record AppGestureInfo(string Id, string Label, string Category, int Order)
{
    public GestureInputKinds Inputs { get; init; } = GestureInputKinds.OneShot;
    public IReadOnlyList<string>? Areas { get; init; }
    public bool NeedsImage { get; init; }
    public bool NeedsSelection { get; init; }
    public bool DirectionAware { get; init; }
}

/// <summary>
/// One entry of the master list of everything Glide can do. Hotkeys, gestures, Settings search, the title-bar
/// button catalogue and the menu builders all read from <see cref="AppActionRegistry"/>; a new feature is offered
/// everywhere it makes sense by adding one entry here and one handler in MainWindow.Commands.
/// </summary>
public sealed record AppAction(string Id, string Label, string Category, GlideCommand Command)
{
    public AppActionKind Kind { get; init; } = AppActionKind.Command;
    public string Description { get; init; } = string.Empty;
    public string[] SearchTerms { get; init; } = [];
    public AppActionInputs Inputs { get; init; } = AppActionInputs.Hotkey | AppActionInputs.Gesture | AppActionInputs.ContextMenu;
    public AppActionContexts Contexts { get; init; } = AppActionContexts.Everywhere;
    public IReadOnlyList<AppHotkeySlot> HotkeySlots { get; init; } = [];
    public AppGestureInfo? Gesture { get; init; }
    /// <summary>For setting actions: the <see cref="SettingsCatalog"/> id.</summary>
    public string? SettingId { get; init; }
    /// <summary>For fixed shortcuts: the key(s) as shown in the hotkey list.</summary>
    public string? FixedShortcut { get; init; }
    /// <summary>This entry performs exactly the same operation as another registry action (kept for its own
    /// gesture id / title-bar button / menu wording); it gets no separate hotkey row.</summary>
    public string? AliasOf { get; init; }
    /// <summary>Destructive actions (delete file, close Glide) are never offered to gestures.</summary>
    public bool Destructive { get; init; }
    /// <summary>Vector icon name; actions with an icon can be placed on the title bar.</summary>
    public string? Icon { get; init; }

    public bool IsHotkeyBindable => HotkeySlots.Count > 0;
    public bool IsSettingAction => Kind is AppActionKind.SettingToggle or AppActionKind.SettingCycle;
}

/// <summary>
/// The single source of truth for user-invokable functions (Glide 5.0.x action registry). AOT-safe: a plain
/// static table, no reflection. Order matters: the hotkey list (and therefore the input router's priority)
/// follows the order of <see cref="All"/>; gesture pickers follow <see cref="AppGestureInfo.Order"/>.
/// </summary>
public static class AppActionRegistry
{
    public const string SettingsCategory = "Settings";

    private static readonly string[] ViewerAreas = [GestureAreas.Windowed, GestureAreas.Fullscreen, GestureAreas.Background, GestureAreas.Selection, GestureAreas.Slideshow];
    private static readonly string[] ImageAreas = [GestureAreas.Windowed, GestureAreas.Fullscreen, GestureAreas.Background, GestureAreas.Selection];
    private static readonly string[] ViewerAndChrome = [.. ViewerAreas, GestureAreas.Tabs, GestureAreas.TitleBar];
    private static readonly string[] Everywhere = [.. ViewerAndChrome, GestureAreas.Explorer];
    private static readonly string[] ViewerAndOverlay = [.. ImageAreas, GestureAreas.Overlay];
    private static readonly string[] ExplorerAndChrome = [GestureAreas.Explorer, GestureAreas.Tabs, GestureAreas.TitleBar];

    private static AppHotkeySlot[] Slot(string id, string label, params string[] keys) => [new(id, label, keys)];

    private const AppActionInputs NoGesture = AppActionInputs.Hotkey | AppActionInputs.ContextMenu | AppActionInputs.TitleBarButton | AppActionInputs.StatusBarButton;

    // ---- builders ------------------------------------------------------------------------------------------

    /// <summary>A command with one hotkey row whose id is the action id.</summary>
    private static AppAction Cmd(string id, string label, string category, GlideCommand command, string[] keys, string[]? terms = null) =>
        new(id, label, category, command) { HotkeySlots = Slot(id, label, keys), SearchTerms = terms ?? [] };

    private static AppGestureInfo G(string id, string label, string category, int order, IReadOnlyList<string>? areas = null,
        bool image = false, GestureInputKinds inputs = GestureInputKinds.OneShot, bool selection = false, bool direction = false) =>
        new(id, label, category, order) { Areas = areas, NeedsImage = image, Inputs = inputs, NeedsSelection = selection, DirectionAware = direction };

    private static AppAction GestureOnly(string gestureId, string label, string category, int order, IReadOnlyList<string>? areas = null,
        bool image = false, GestureInputKinds inputs = GestureInputKinds.OneShot, bool direction = false, string description = "") =>
        new("gesture." + gestureId, label, category, GlideCommand.None)
        {
            Kind = AppActionKind.GestureOnly,
            Inputs = AppActionInputs.Gesture,
            Description = description,
            Gesture = G(gestureId, label, category, order, areas, image, inputs, direction: direction)
        };

    private static AppAction Fixed(string id, string keys, string label, AppActionContexts context, string description, params string[] terms) =>
        new("fixed." + id, label, context == AppActionContexts.Explorer ? "Explorer (fixed)" : "Viewer (fixed)", GlideCommand.None)
        {
            Kind = AppActionKind.FixedShortcut,
            Inputs = AppActionInputs.None,
            Contexts = context,
            FixedShortcut = keys,
            Description = description,
            SearchTerms = terms
        };

    // ---- the list ------------------------------------------------------------------------------------------

    private static AppAction[] CommandActions() =>
    [
        // The first 75 hotkey rows are the pre-registry HotkeyCatalog, in its exact order, ids, labels,
        // categories and defaults (so saved bindings, presets and the router's priority are unchanged).
        Cmd("file.open", "Open file", "File", GlideCommand.OpenFile, ["O", "Ctrl+O"]) with { Icon = "Open", Gesture = G("file.open", "Open file", GestureActionCategories.File, 1000, Everywhere) },
        Cmd("file.openFolder", "Open folder", "File", GlideCommand.OpenFolder, ["Ctrl+Shift+O"]),
        Cmd("app.settings", "Open Settings", "Application", GlideCommand.Settings, ["Ctrl+,"]) with { Icon = "Settings", Gesture = G(GestureCatalog.Settings, "Open Settings", GestureActionCategories.Window, 13, Everywhere) },
        Cmd("folder.refresh", "Refresh current folder", "File", GlideCommand.RefreshFolder, ["F5", "U", "Ctrl+R"]),
        Cmd("workspace.home", "Open / select Home tab", "Workspace", GlideCommand.Home, []) with { Icon = "Home" },
        Cmd("folder.previous", "Previous sibling folder", "Navigation", GlideCommand.PreviousFolder, ["Shift+Left"]) with { Icon = "FolderPrevious", Gesture = G(GestureCatalog.PreviousFolder, "Previous folder", GestureActionCategories.Navigation, 29, ViewerAndChrome, image: true) },
        Cmd("folder.next", "Next sibling folder", "Navigation", GlideCommand.NextFolder, ["Shift+Right"]) with { Icon = "FolderNext", Gesture = G(GestureCatalog.NextFolder, "Next folder", GestureActionCategories.Navigation, 28, ViewerAndChrome, image: true) },
        // This action used to do nothing (its command handler was an empty stub). It now behaves
        // like the title-bar Show in Explorer button and follows the "Show in Explorer button opens" setting.
        Cmd("folder.exploreParent", "Explore/Browse containing folder", "Navigation", GlideCommand.ExploreParentFolder, ["Ctrl+E"],
            ["explore", "browse", "folder", "reveal", "show in explorer", "containing", "locate", "open containing folder", "parent folder", "file explorer", "explorer tab"]),
        Cmd("slideshow.startPause", "Start / pause / resume slideshow", "Slideshow", GlideCommand.StartPauseSlideshow, ["Shift+A", "S"]) with { Icon = "Play", Gesture = G(GestureCatalog.ToggleSlideshow, "Start / pause slideshow", GestureActionCategories.Slideshow, 44, ViewerAreas, image: true) },
        Cmd("slideshow.stop", "Stop slideshow session", "Slideshow", GlideCommand.StopSlideshow, []) with { Icon = "Stop", Gesture = G(GestureCatalog.StopSlideshow, "Stop slideshow", GestureActionCategories.Slideshow, 45, ViewerAreas) },
        Cmd("view.info", "Toggle image information", "Viewing", GlideCommand.ToggleImageInfo, ["I"]) with { Icon = "Info", Gesture = G(GestureCatalog.ToggleInfo, "Toggle image information", GestureActionCategories.View, 12, [.. ViewerAreas, GestureAreas.TitleBar], image: true) },
        Cmd("view.rotateLeft", "Rotate view left", "Viewing", GlideCommand.RotateLeft, ["L"]) with { Icon = "RotateLeft", Gesture = G(GestureCatalog.RotateLeft, "Rotate left", GestureActionCategories.View, 31, ViewerAreas, image: true) },
        Cmd("view.rotateRight", "Rotate view right", "Viewing", GlideCommand.RotateRight, ["R"]) with { Icon = "RotateRight", Gesture = G(GestureCatalog.RotateRight, "Rotate right", GestureActionCategories.View, 32, ViewerAreas, image: true) },
        Cmd("view.flipHorizontal", "Flip view horizontally", "Viewing", GlideCommand.FlipHorizontal, ["H"]) with { Icon = "FlipHorizontal", Gesture = G(GestureCatalog.FlipHorizontal, "Flip horizontally", GestureActionCategories.View, 33, ViewerAreas, image: true) },
        Cmd("view.flipVertical", "Flip view vertically", "Viewing", GlideCommand.FlipVertical, ["V"]) with { Icon = "FlipVertical", Gesture = G(GestureCatalog.FlipVertical, "Flip vertically", GestureActionCategories.View, 34, ViewerAreas, image: true) },
        new("nav.next", "Next image", "Navigation", GlideCommand.NextImage)
        {
            Icon = "Next",
            HotkeySlots =
            [
                new("nav.nextSpace", "Next image (Space)", ["Space"]),
                new("nav.nextRight", "Next image (Right Arrow)", ["Right"]),
                new("nav.nextPage", "Next image (Page Down)", ["PageDown"])
            ],
            Gesture = G(GestureCatalog.Next, "Next image", GestureActionCategories.Navigation, 2, ViewerAndChrome, image: true)
        },
        new("nav.previous", "Previous image", "Navigation", GlideCommand.PreviousImage)
        {
            Icon = "Previous",
            HotkeySlots =
            [
                new("nav.previousBack", "Previous image (extra key)", []),
                new("nav.previousLeft", "Previous image (Left Arrow)", ["Left"]),
                new("nav.previousPage", "Previous image (Page Up)", ["PageUp"])
            ],
            Gesture = G(GestureCatalog.Previous, "Previous image", GestureActionCategories.Navigation, 3, ViewerAndChrome, image: true)
        },
        Cmd("nav.first", "First image", "Navigation", GlideCommand.FirstImage, ["Ctrl+Home", "Home"]) with { Icon = "First", Gesture = G(GestureCatalog.FirstImage, "First image", GestureActionCategories.Navigation, 24, ViewerAndChrome, image: true) },
        Cmd("nav.last", "Last image", "Navigation", GlideCommand.LastImage, ["Ctrl+End", "End"]) with { Icon = "Last", Gesture = G(GestureCatalog.LastImage, "Last image", GestureActionCategories.Navigation, 25, ViewerAndChrome, image: true) },
        Cmd("view.fullscreen", "Toggle fullscreen", "Viewing", GlideCommand.ToggleFullscreen, ["F", "F11", "Enter"]) with { Icon = "Fullscreen", Gesture = G(GestureCatalog.Fullscreen, "Toggle fullscreen", GestureActionCategories.Window, 9, Everywhere) },
        Cmd("view.fit", "Fit image", "Viewing", GlideCommand.FitImage, ["Shift+W"]) with { Icon = "Fit", Gesture = G(GestureCatalog.Fit, "Fit image", GestureActionCategories.View, 6, ViewerAreas, image: true) },
        Cmd("view.fitWidth", "Fit width", "Viewing", GlideCommand.FitWidth, ["2"]) with { Icon = "FitWidth" },
        Cmd("view.fitHeight", "Fit height", "Viewing", GlideCommand.FitHeight, ["1"]) with { Icon = "FitHeight" },
        Cmd("view.fitWindow", "Fit to window (enlarge or shrink)", "Viewing", GlideCommand.FitWindow, ["4"]) with { Icon = "FitWindow" },
        Cmd("view.actual", "Actual size / 100%", "Viewing", GlideCommand.ActualSize, ["3", "Ctrl+H"]) with { Icon = "Actual", Gesture = G(GestureCatalog.Actual, "100% / actual size", GestureActionCategories.View, 7, ViewerAreas, image: true) },
        Cmd("view.toggleFitActual", "Toggle Fit / 100%", "Viewing", GlideCommand.ToggleFitActual, []) with { Gesture = G(GestureCatalog.ToggleFitActual, "Toggle Fit / 100%", GestureActionCategories.View, 8, ViewerAreas, image: true) },
        Cmd("view.zoomIn", "Zoom in", "Viewing", GlideCommand.ZoomIn, ["+", "="]) with { Icon = "ZoomIn", Gesture = G(GestureCatalog.ZoomIn, "Zoom in", GestureActionCategories.View, 4, ViewerAreas, image: true) },
        Cmd("view.zoomOut", "Zoom out", "Viewing", GlideCommand.ZoomOut, ["-"]) with { Icon = "ZoomOut", Gesture = G(GestureCatalog.ZoomOut, "Zoom out", GestureActionCategories.View, 5, ViewerAreas, image: true) },
        // Animated GIF playback. Space already means Next image / slideshow pause, so P toggles.
        Cmd("gif.playPause", "Pause / play animated GIF", "Viewing", GlideCommand.GifPlayPause, ["P"]) with { Icon = "Pause" },
        Cmd("gif.previousFrame", "Previous GIF frame", "Viewing", GlideCommand.GifPreviousFrame, [","]) with { Icon = "FramePrevious" },
        Cmd("gif.nextFrame", "Next GIF frame", "Viewing", GlideCommand.GifNextFrame, ["."]) with { Icon = "FrameNext" },
        Cmd("selection.clear", "Clear selection", "Selection", GlideCommand.ClearSelection, ["Esc"]) with { Gesture = G(GestureCatalog.ClearSelection, "Clear selection", GestureActionCategories.Selection, 11, ViewerAreas, selection: true) },
        Cmd("tab.new", "New tab", "Tabs", GlideCommand.NewTab, ["Ctrl+T"]) with { Icon = "Plus", Gesture = G(GestureCatalog.NewTab, "New tab", GestureActionCategories.Tabs, 35, Everywhere) },
        Cmd("tab.close", "Close tab", "Tabs", GlideCommand.CloseTab, ["Ctrl+W", "Ctrl+F4"]) with { Gesture = G(GestureCatalog.CloseTab, "Close tab", GestureActionCategories.Tabs, 36, Everywhere) },
        Cmd("tab.restore", "Restore closed tab", "Tabs", GlideCommand.RestoreClosedTab, ["Ctrl+Shift+T"]) with { Icon = "History", Gesture = G(GestureCatalog.ReopenClosedTab, "Reopen closed tab", GestureActionCategories.Tabs, 37, Everywhere) },
        Cmd("tab.duplicate", "Duplicate tab", "Tabs", GlideCommand.DuplicateTab, ["Ctrl+Shift+K"]) with { Gesture = G(GestureCatalog.DuplicateTab, "Duplicate tab", GestureActionCategories.Tabs, 38, [GestureAreas.Tabs]) },
        Cmd("window.newSameImage", "New window with current picture", "Application", GlideCommand.NewWindowSameImage, ["Ctrl+N"]),
        Cmd("window.duplicateSession", "Duplicate current window/session", "Application", GlideCommand.DuplicateSessionWindow, ["Ctrl+Shift+N"]),
        Cmd("tab.next", "Next tab", "Tabs", GlideCommand.NextTab, ["Ctrl+Tab", "Ctrl+PageDown"]),
        Cmd("tab.previous", "Previous tab", "Tabs", GlideCommand.PreviousTab, ["Ctrl+Shift+Tab", "Ctrl+PageUp"]),
        Cmd("tab.select1", "Select tab 1", "Tabs", GlideCommand.SelectTab1, ["Ctrl+1"]),
        Cmd("tab.select2", "Select tab 2", "Tabs", GlideCommand.SelectTab2, ["Ctrl+2"]),
        Cmd("tab.select3", "Select tab 3", "Tabs", GlideCommand.SelectTab3, ["Ctrl+3"]),
        Cmd("tab.select4", "Select tab 4", "Tabs", GlideCommand.SelectTab4, ["Ctrl+4"]),
        Cmd("tab.select5", "Select tab 5", "Tabs", GlideCommand.SelectTab5, ["Ctrl+5"]),
        Cmd("tab.select6", "Select tab 6", "Tabs", GlideCommand.SelectTab6, ["Ctrl+6"]),
        Cmd("tab.select7", "Select tab 7", "Tabs", GlideCommand.SelectTab7, ["Ctrl+7"]),
        Cmd("tab.select8", "Select tab 8", "Tabs", GlideCommand.SelectTab8, ["Ctrl+8"]),
        Cmd("tab.selectLast", "Select last tab", "Tabs", GlideCommand.SelectLastTab, ["Ctrl+9"]),
        Cmd("tab.detach", "Move active tab to new Glide window", "Tabs", GlideCommand.DetachTab, []) with { Gesture = G(GestureCatalog.DetachTab, "Move tab to a new window", GestureActionCategories.Tabs, 39, [GestureAreas.Tabs]) },
        Cmd("window.close", "Close Glide window", "Application", GlideCommand.CloseWindow, ["Ctrl+Shift+W"]) with { Destructive = true },
        Cmd("window.alwaysTop", "Toggle Always On Top", "Application", GlideCommand.ToggleAlwaysOnTop, [],
            ["always on top", "topmost", "pin", "above other windows"]) with { Icon = "AlwaysOnTop", Gesture = G(GestureCatalog.ToggleAlwaysOnTop, "Toggle always on top", GestureActionCategories.Window, 41, Everywhere) },
        // Default Ctrl+Shift+B ("status Bar"): the earlier default Alt+Shift+S is the Windows input-language switch
        // chord. Saved files still on the old default move once (HotkeyDefaultMigrations, settings schema 8).
        Cmd("status.toggle", "Show / hide status surface", "Application", GlideCommand.ToggleStatusSurface, ["Ctrl+Shift+B"],
            ["status", "status bar", "collapse", "expand", "minimise", "minimize", "restore", "show status bar", "hide status bar"]) with { Icon = "Status", Gesture = G(GestureCatalog.ToggleStatusBar, "Show / hide status bar", GestureActionCategories.Window, 40, Everywhere) },
        Cmd("window.transparency", "Show window transparency control", "Application", GlideCommand.ToggleTransparency, [],
            ["transparency", "opacity", "see through", "translucent"]) with { Icon = "WindowOpacity" },
        Cmd("view.contextMenu", "Open viewer context menu", "Viewing", GlideCommand.ShowContextMenu, []) with { Gesture = G(GestureCatalog.ContextMenu, "Open context menu", GestureActionCategories.General, 10, ViewerAndChrome) },
        Cmd("overlay.add", "Add Window-in-Window overlay", "Overlays", GlideCommand.AddOverlay, ["Shift+O"]) with { Icon = "OverlayAdd" },
        Cmd("overlay.saveLayout", "Save overlay layout", "Overlays", GlideCommand.SaveOverlayLayout, ["Ctrl+Shift+S"]) with { Icon = "OverlaySave" },
        Cmd("overlay.loadLayout", "Load overlay layout", "Overlays", GlideCommand.LoadOverlayLayout, ["Ctrl+Shift+L"]) with { Icon = "OverlayLoad" },
        Cmd("file.copyImage", "Copy image / active selection pixels", "File", GlideCommand.CopyImage, ["Ctrl+C", "Ctrl+Insert"]) with { Icon = "Copy", Gesture = G(GestureCatalog.CopyImage, "Copy image to clipboard", GestureActionCategories.File, 46, ViewerAreas, image: true) },
        Cmd("file.copyFile", "Copy underlying image file", "File", GlideCommand.CopyFile, []),
        Cmd("file.copyName", "Copy file name", "File", GlideCommand.CopyFileName, []),
        Cmd("file.copyFolder", "Copy folder path", "File", GlideCommand.CopyFolderPath, []),
        Cmd("file.copyPath", "Copy full path", "File", GlideCommand.CopyFullPath, []),
        // Rename / Print / Save keep the 4.8.0 gesture rule: they open a dialog or overwrite a file, so they are never
        // offered to a pointer gesture (a stray click must not print or overwrite). Hotkey, menu and title bar only.
        Cmd("file.rename", "Rename current file", "File", GlideCommand.RenameFile, ["F2", "F6"]) with { Icon = "Rename", Inputs = NoGesture },
        Cmd("file.delete", "Delete current file to Recycle Bin", "File", GlideCommand.DeleteFile, ["Delete"]) with { Destructive = true },
        Cmd("file.print", "Print current image", "File", GlideCommand.Print, ["Ctrl+P"]) with { Icon = "Print", Inputs = NoGesture },
        Cmd("file.save", "Save (overwrite with current rotation/flip)", "File", GlideCommand.SaveImage, ["Ctrl+S"]) with { Icon = "SaveFile", Inputs = NoGesture },
        // Ctrl+Shift+S is already the canonical overlay-layout save shortcut.
        Cmd("file.saveAs", "Save as…", "File", GlideCommand.SaveImageAs, ["Ctrl+Alt+S"]) with { Icon = "SaveAs" },
        Cmd("external.1", "Open in external program 1", "External", GlideCommand.ExternalProgram1, ["Shift+1"]),
        Cmd("external.2", "Open in external program 2", "External", GlideCommand.ExternalProgram2, ["Shift+2"]),
        Cmd("external.3", "Open in external program 3", "External", GlideCommand.ExternalProgram3, ["Shift+3"]),

        // ---- 5.0.x: commands that existed only as a gesture, button or menu item (missing from the hotkey list) ----
        Cmd("window.maximizeRestore", "Maximize / restore window", "Application", GlideCommand.MaximizeRestoreWindow, [],
            ["maximize", "maximise", "restore", "window size"]) with { Icon = "Maximize", Gesture = G(GestureCatalog.MaximizeRestore, "Maximize / restore window", GestureActionCategories.Window, 42, [GestureAreas.TitleBar]) },
        Cmd("window.minimize", "Minimize window", "Application", GlideCommand.MinimizeWindow, [],
            ["minimize", "minimise", "taskbar"]) with { Icon = "Minimize", Gesture = G(GestureCatalog.MinimizeWindow, "Minimize window", GestureActionCategories.Window, 43, [GestureAreas.TitleBar]) },
        Cmd("nav.jumpForward10", "Jump forward 10 images", "Navigation", GlideCommand.JumpForward10, []) with { Gesture = G(GestureCatalog.JumpForward10, "Jump forward 10 images", GestureActionCategories.Navigation, 26, ViewerAreas, image: true) },
        Cmd("nav.jumpBack10", "Jump back 10 images", "Navigation", GlideCommand.JumpBack10, []) with { Gesture = G(GestureCatalog.JumpBack10, "Jump back 10 images", GestureActionCategories.Navigation, 27, ViewerAreas, image: true) },
        Cmd("view.zoom200", "Zoom to 200%", "Viewing", GlideCommand.ZoomTo200, []) with { Gesture = G(GestureCatalog.Zoom200, "Zoom to 200%", GestureActionCategories.View, 30, ViewerAreas, image: true) },
        Cmd("selection.export", "Export selected region…", "Selection", GlideCommand.ExportSelection, [], ["export", "crop", "save selection", "region"]) with { Icon = "Crop" },
        Cmd("overlay.resetZoom", "Reset selected overlay zoom", "Overlays", GlideCommand.ResetSelectedOverlayZoom, []) with { Gesture = G(GestureCatalog.ResetOverlayZoom, "Reset selected overlay zoom", GestureActionCategories.Overlay, 17, ViewerAndOverlay) },
        Cmd("overlay.bringFront", "Bring selected overlay to front", "Overlays", GlideCommand.BringSelectedOverlayFront, []) with { Icon = "BringToFront", Gesture = G(GestureCatalog.BringOverlayFront, "Bring selected overlay to front", GestureActionCategories.Overlay, 18, ViewerAndOverlay) },
        Cmd("overlay.clear", "Clear all overlays", "Overlays", GlideCommand.ClearOverlays, [], ["remove overlays", "close overlays", "window in window"]),
        Cmd("overlay.toggleMode", "Enter / exit Overlay (Window-in-Window) mode", "Overlays", GlideCommand.ToggleOverlayMode, [],
            ["overlay mode", "window in window", "frameless", "transparent window", "exit overlay"]) with { Icon = "Overlays" },
        Cmd("status.close", "Close (hide) the status bar", "Application", GlideCommand.CloseStatusBar, [], ["status", "status bar", "hide", "close"]),
        Cmd("tab.newExplorer", "New Explorer tab (browse folders)", "Tabs", GlideCommand.NewExplorerTab, [], ["explorer", "browse", "folder", "files"]) with { Icon = "Browser" },
        Cmd("tab.newHome", "New Welcome tab", "Tabs", GlideCommand.NewHomeTab, [], ["welcome", "home", "start page"]),
        Cmd("tab.openFirstImage", "Open first image in folder", "Tabs", GlideCommand.OpenFirstImageInFolder, [], ["first image", "open folder", "explorer"]) with { Contexts = AppActionContexts.Explorer },
        // Since the 5.0.0 unified tab history these run exactly the same Back / Forward as nav.back / nav.forward (only in an
        // Explorer tab). Kept as their own rows so saved hotkey and gesture bindings keep working.
        Cmd("explorer.back", "Explorer: back", "Explorer", GlideCommand.BrowserBack, [], ["explorer", "back", "history"]) with { Contexts = AppActionContexts.Explorer, Description = "Same as Back, from an Explorer tab.", Gesture = G("explorer.back", "Explorer: back", GestureActionCategories.Explorer, 1100, ExplorerAndChrome) },
        Cmd("explorer.forward", "Explorer: forward", "Explorer", GlideCommand.BrowserForward, [], ["explorer", "forward", "history"]) with { Contexts = AppActionContexts.Explorer, Description = "Same as Forward, from an Explorer tab.", Gesture = G("explorer.forward", "Explorer: forward", GestureActionCategories.Explorer, 1101, ExplorerAndChrome) },
        Cmd("explorer.up", "Explorer: up one folder", "Explorer", GlideCommand.BrowserUp, [], ["explorer", "up", "parent folder"]) with { Contexts = AppActionContexts.Explorer, Gesture = G("explorer.up", "Explorer: up one folder", GestureActionCategories.Explorer, 1102, ExplorerAndChrome) },
        Cmd("explorer.showInWindows", "Show in Windows File Explorer", "Explorer", GlideCommand.ShowFolderInWindowsExplorer, [],
            ["windows explorer", "file explorer", "reveal", "open folder", "show this folder", "select file"]) with { Contexts = AppActionContexts.Explorer, Description = "Opens the folder in Windows File Explorer with the selected item selected." },
        Cmd("explorer.showInAlternative", "Show in alternative file manager", "Explorer", GlideCommand.ShowInAlternativeFileManager, [],
            ["file manager", "alternative", "external", "total commander", "directory opus", "reveal", "open folder"]) with { Contexts = AppActionContexts.Explorer, Description = "Opens the folder or selected item in the alternative file manager set in Settings (Explorer)." },
        Cmd("titlebar.customize", "Customize title-bar buttons…", "Application", GlideCommand.CustomizeTitleBar, [], ["title bar", "toolbar", "buttons", "customize"]),
        Cmd("statusbar.customize", "Customize status-bar buttons…", "Application", GlideCommand.CustomizeStatusBar, [], ["status bar", "status", "buttons", "reorder", "customize", "customise"]),
        Cmd("navigation.toggleLoopFolder", "Loop this folder (this tab)", "Navigation", GlideCommand.ToggleLoopFolder, [], ["loop", "repeat", "wrap around", "end of folder", "last image", "first image", "this tab", "status bar"]) with { Icon = "Loop", Description = "Turns looping on or off for the current tab: at the end (or start) of the folder Next / Previous wraps around inside the folder, whatever \"When the last image is reached\" says. Also available as a status-bar button." },
        Cmd("tabs.tooltipLarger", "Tab tooltip text larger", "Tabs", GlideCommand.TabToolTipLarger, [], ["tab", "tooltip", "font", "font size", "text size", "larger", "bigger", "increase"]),
        Cmd("tabs.tooltipSmaller", "Tab tooltip text smaller", "Tabs", GlideCommand.TabToolTipSmaller, [], ["tab", "tooltip", "font", "font size", "text size", "smaller", "decrease"]),
        Cmd("window.resetTransparency", "Reset window transparency to 100%", "Application", GlideCommand.ResetTransparency, [], ["transparency", "opacity", "reset"]),
        Cmd("home.clearRecent", "Clear recent history", "Workspace", GlideCommand.ClearRecentHistory, [], ["recent", "history", "clear", "welcome"]) with { Destructive = true },
        Cmd("home.cycleRecentSize", "Cycle Recent list size (Welcome page)", "Workspace", GlideCommand.CycleRecentHistorySize, [], ["recent", "thumbnails", "size", "compact", "large", "welcome"]),
        // The viewer menu's "Exit program" and the tray icon's "Exit Glide": closes every window and ends the process.
        // Unassigned by default; "Close Glide window" keeps closing only the current window.
        Cmd("app.exit", "Exit Glide", "Application", GlideCommand.ExitApplication, [],
            ["exit", "quit", "close glide", "close all windows", "shut down", "end program", "exit program", "tray", "speed boost"]) with { Destructive = true },

        // 5.0.0 browser-style Back / Forward through the active tab's history (docs/NAVIGATION-HISTORY.md): Explorer folders,
        // an image opened from them in the same tab, Home. Default title-bar buttons and the default of the mouse side buttons.
        // Backspace belongs to the composite action nav.backOrPrevious ("Back, or Previous image"), not to Back itself: Back is
        // strict (never falls back to Previous image). The legacy row nav.previousBack ("Previous image") keeps its id for saved
        // files and presets but has no default key; HotkeyDefaultMigrations.MigrateBackspaceToBackOrPrevious moves an untouched
        // old default once (settings schema 11).
        Cmd("nav.back", "Back", "Navigation", GlideCommand.NavigateBack, ["Alt+Left"],
            ["back", "go back", "previous page", "history", "navigate", "undo navigation", "browser back", "return to explorer", "mouse back button"]) with
        {
            Icon = "Back",
            Inputs = AppActionInputs.All,
            Description = "Goes back in this tab's history, like a web browser: from an image opened from the Explorer back to that folder with the image selected, to the previous folder, or to Home.",
            Gesture = G("nav.back", "Back", GestureActionCategories.Navigation, 56, Everywhere)
        },
        Cmd("nav.backOrPrevious", "Back, or Previous image", "Navigation", GlideCommand.BackOrPrevious, ["Backspace"],
            ["back", "previous", "backspace", "history", "previous image", "go back", "explorer backspace", "return to explorer"]) with
        {
            Inputs = AppActionInputs.Hotkey,
            Description = "Backspace by default. Goes Back in this tab's history (an image opened from the Explorer returns to that folder with the image selected) when there is a step to go back to; otherwise, in an image tab, shows the previous image; otherwise does nothing. Never fires while typing in a text box.",
        },
        Cmd("nav.forward", "Forward", "Navigation", GlideCommand.NavigateForward, ["Shift+Backspace", "Alt+Right"],
            ["forward", "go forward", "next page", "history", "navigate", "redo navigation", "browser forward", "mouse forward button"]) with
        {
            Icon = "Forward",
            Inputs = AppActionInputs.All,
            Description = "Goes forward again in this tab's history (undoes Back), like a web browser.",
            Gesture = G("nav.forward", "Forward", GestureActionCategories.Navigation, 57, Everywhere)
        },

        // 5.0.1 update checker: the same "Check now" as Settings > General & startup > Updates. It only asks GitHub and, when a newer
        // version exists, shows the update prompt (which can only open the release page); nothing is ever downloaded or installed.
        Cmd("app.checkUpdates", "Check for updates now", "Application", GlideCommand.CheckForUpdates, [],
            ["update", "updates", "check for updates", "new version", "upgrade", "latest", "release", "github", "version", "check now"]) with
        {
            Inputs = AppActionInputs.Hotkey,
            Description = "Asks GitHub whether a newer version of Glide exists (one small request, no personal data) and shows the answer: in Settings > Updates when Settings is open, otherwise as the update prompt or a short message. Nothing is downloaded or installed."
        },

        // 5.0.4 Back up and restore (Settings > Windows & developer): open that card and start its Export / Import flow. Both
        // open dialogs (save / open file, then the import choice and confirmation), so they are hotkey / menu only, never
        // gestures; import replaces data, so it is marked destructive too.
        Cmd("app.exportBackup", "Export Glide backup…", "Application", GlideCommand.ExportGlideBackup, [],
            ["backup", "back up", "export", "save settings", "transfer", "migrate", "move to new pc", "new computer", "settings file", "profile", "glidebackup"]) with
        {
            Inputs = NoGesture,
            Description = "Opens Settings > Windows & developer > Back up and restore and saves the ticked items (settings, hotkeys, profiles, window positions, recent files, last session) to one .glidebackup file."
        },
        Cmd("app.importBackup", "Import Glide backup…", "Application", GlideCommand.ImportGlideBackup, [],
            ["backup", "back up", "restore", "import", "load settings", "transfer", "migrate", "move to new pc", "new computer", "settings file", "profile", "glidebackup"]) with
        {
            Inputs = NoGesture,
            Destructive = true,
            Description = "Opens Settings > Windows & developer > Back up and restore and imports a .glidebackup file: you choose what to import and confirm first."
        },

        // Same operation as folder.exploreParent (both run the title-bar Show in Explorer action). Kept as its own
        // entry for the legacy gesture id "showInExplorer", the title-bar button file.locate and menu wording.
        new("file.showInExplorer", "Show in Explorer (select current image)", "File", GlideCommand.OpenContainingFolder)
        {
            AliasOf = "folder.exploreParent",
            Icon = "FolderLocate",
            Inputs = AppActionInputs.Gesture | AppActionInputs.TitleBarButton | AppActionInputs.ContextMenu,
            Gesture = G(GestureCatalog.ShowInExplorer, "Show in File Explorer", GestureActionCategories.File, 47, ViewerAreas, image: true)
        },
    ];

    /// <summary>Pointer-only actions: kept in their historical gesture order.</summary>
    private static AppAction[] GestureOnlyActions() =>
    [
        new("gesture.default", "Use Glide/default behavior", GestureActionCategories.General, GlideCommand.None) { Kind = AppActionKind.GestureOnly, Inputs = AppActionInputs.Gesture, Gesture = G(GestureCatalog.Default, "Use Glide/default behavior", GestureActionCategories.General, 0, inputs: GestureInputKinds.All) },
        new("gesture.nothing", "Do nothing", GestureActionCategories.General, GlideCommand.None) { Kind = AppActionKind.GestureOnly, Inputs = AppActionInputs.Gesture, Gesture = G(GestureCatalog.Nothing, "Do nothing", GestureActionCategories.General, 1, inputs: GestureInputKinds.All) },
        GestureOnly(GestureCatalog.Pan, "Pan image", GestureActionCategories.View, 14, ImageAreas, image: true, inputs: GestureInputKinds.Drag),
        GestureOnly(GestureCatalog.CreateSelection, "Create selection", GestureActionCategories.Selection, 15, ImageAreas, image: true, inputs: GestureInputKinds.Drag),
        GestureOnly(GestureCatalog.MoveWindow, "Move native window", GestureActionCategories.Window, 16, [GestureAreas.Windowed, GestureAreas.Background, GestureAreas.Selection], inputs: GestureInputKinds.Drag),
        GestureOnly(GestureCatalog.ZoomWheel, "Zoom in/out", GestureActionCategories.View, 19, [.. ViewerAreas, GestureAreas.Overlay, GestureAreas.TitleBar], image: true, inputs: GestureInputKinds.Wheel, direction: true),
        GestureOnly(GestureCatalog.ZoomWheelReversed, "Zoom in/out (reversed)", GestureActionCategories.View, 20, [.. ViewerAreas, GestureAreas.Overlay, GestureAreas.TitleBar], image: true, inputs: GestureInputKinds.Wheel, direction: true),
        GestureOnly(GestureCatalog.BrowseWheel, "Previous/next image", GestureActionCategories.Navigation, 21, [.. ViewerAreas, GestureAreas.TitleBar], image: true, inputs: GestureInputKinds.Wheel, direction: true),
        GestureOnly(GestureCatalog.BrowseWheelReversed, "Previous/next image (reversed)", GestureActionCategories.Navigation, 22, [.. ViewerAreas, GestureAreas.TitleBar], image: true, inputs: GestureInputKinds.Wheel, direction: true),
        new("gesture.passThrough", "Ignore here (normal click / drag)", GestureActionCategories.General, GlideCommand.None) { Kind = AppActionKind.GestureOnly, Inputs = AppActionInputs.Gesture, Gesture = G(GestureCatalog.PassThrough, "Ignore here (normal click / drag)", GestureActionCategories.General, 23, [GestureAreas.Fullscreen, GestureAreas.Selection, GestureAreas.Slideshow], inputs: GestureInputKinds.Click) },
        GestureOnly(GestureCatalog.OpenInCurrentTab, "Open in current tab", GestureActionCategories.Explorer, 48, [GestureAreas.Explorer]),
        GestureOnly(GestureCatalog.OpenInNewTab, "Open in new tab", GestureActionCategories.Explorer, 49, [GestureAreas.Explorer]),
        GestureOnly(GestureCatalog.PanWheel, "Pan up / down", GestureActionCategories.View, 50, ViewerAreas, image: true, inputs: GestureInputKinds.Wheel, direction: true),
        GestureOnly(GestureCatalog.PanWheelHorizontal, "Pan left / right (sideways)", GestureActionCategories.View, 51, ViewerAreas, image: true, inputs: GestureInputKinds.Wheel, direction: true),
        GestureOnly(GestureCatalog.JumpWheel, "Jump 10 images back / forward", GestureActionCategories.Navigation, 52, ViewerAreas, image: true, inputs: GestureInputKinds.Wheel, direction: true),
        GestureOnly(GestureCatalog.FolderWheel, "Previous / next folder", GestureActionCategories.Navigation, 53, [.. ViewerAreas, GestureAreas.TitleBar], image: true, inputs: GestureInputKinds.Wheel, direction: true),
        GestureOnly(GestureCatalog.RotateWheel, "Rotate left / right", GestureActionCategories.View, 54, ViewerAreas, image: true, inputs: GestureInputKinds.Wheel, direction: true),
        GestureOnly(GestureCatalog.TabWheel, "Previous / next tab", GestureActionCategories.Tabs, 55, Everywhere, inputs: GestureInputKinds.Wheel, direction: true),
    ];

    /// <summary>
    /// Fixed shortcuts owned by one surface. They are listed (read-only) so the hotkey list and Settings search
    /// show every key Glide reacts to; the code that handles them is allow-listed by the source guard test.
    /// </summary>
    private static AppAction[] FixedShortcutActions() =>
    [
        Fixed("slideshow.spacePause", "Space", "Pause / resume the running slideshow", AppActionContexts.Viewer,
            "While a slideshow is active Space always pauses or resumes it, whatever Space is bound to otherwise.", "space", "slideshow", "pause", "resume"),
        Fixed("overlay.keyboardZoom", "Num + / Num - / Ctrl+= / Ctrl+-", "Zoom the selected overlay", AppActionContexts.Overlay,
            "With an overlay selected and 'Keyboard zoom for selected overlay' on, these keys zoom the overlay instead of the image.", "overlay", "zoom", "plus", "minus"),
        Fixed("overlay.areaCancel", "Esc", "Cancel drawing an overlay animation area", AppActionContexts.Overlay,
            "While drawing an overlay animation area, Esc cancels it.", "overlay", "area", "escape", "cancel"),
        Fixed("explorer.filter", "Ctrl+F", "Explorer: focus the filter box", AppActionContexts.Explorer, "Moves the keyboard to the Explorer filter box.", "filter", "search", "find"),
        Fixed("explorer.address", "Ctrl+L / Alt+D / F4", "Explorer: edit the address bar", AppActionContexts.Explorer, "Edits the folder path in the Explorer address bar.", "address", "path", "location"),
        // 5.0.0: Alt+Left / Alt+Right are no longer fixed Explorer keys; they are the editable defaults of Back / Forward
        // (nav.back / nav.forward), which also step across an image opened from the Explorer tab. Backspace in the Explorer list
        // is Back through the registry action nav.back (no separate fixed key).
        Fixed("explorer.up", "Alt+Up", "Explorer: up one folder", AppActionContexts.Explorer, "Opens the parent folder.", "up", "parent", "alt+up"),
        Fixed("explorer.properties", "Alt+Enter", "Explorer: properties", AppActionContexts.Explorer, "Shows Windows properties for the selection or the folder.", "properties", "details"),
        Fixed("explorer.selectAll", "Ctrl+A", "Explorer: select all", AppActionContexts.Explorer, "Selects every item in the folder.", "select all"),
        Fixed("explorer.copy", "Ctrl+C", "Explorer: copy files", AppActionContexts.Explorer, "Copies the selected files to the clipboard.", "copy", "clipboard"),
        Fixed("explorer.cut", "Ctrl+X", "Explorer: cut files", AppActionContexts.Explorer, "Cuts the selected files to the clipboard.", "cut", "move", "clipboard"),
        Fixed("explorer.paste", "Ctrl+V", "Explorer: paste files", AppActionContexts.Explorer, "Pastes files from the clipboard into this folder.", "paste", "clipboard"),
        Fixed("explorer.newFolder", "Ctrl+Shift+N", "Explorer: new folder", AppActionContexts.Explorer, "Creates a new folder.", "new folder", "create"),
        Fixed("explorer.refresh", "F5 / Ctrl+R", "Explorer: refresh", AppActionContexts.Explorer, "Reloads the folder.", "refresh", "reload"),
        Fixed("explorer.toggleItem", "Ctrl+Space", "Explorer: toggle the focused item", AppActionContexts.Explorer, "Adds or removes the focused item from the selection.", "select", "toggle"),
        Fixed("explorer.rename", "F2", "Explorer: rename", AppActionContexts.Explorer, "Renames the selected item in place.", "rename"),
        Fixed("explorer.delete", "Delete / Shift+Delete", "Explorer: delete (Shift: permanently)", AppActionContexts.Explorer, "Deletes the selection to the Recycle Bin, or permanently with Shift.", "delete", "recycle bin", "permanent"),
        Fixed("explorer.menu", "Menu key / Shift+F10", "Explorer: context menu", AppActionContexts.Explorer, "Opens the context menu for the selection.", "context menu", "right click"),
        Fixed("explorer.open", "Enter", "Explorer: open the selected item", AppActionContexts.Explorer, "Opens the selected image or folder.", "open", "enter"),
        Fixed("explorer.move", "Arrows / Home / End / Page Up / Page Down (Shift extends, Ctrl moves focus only)", "Explorer: move the selection", AppActionContexts.Explorer, "Keyboard navigation in the file list.", "arrows", "navigate", "select"),
        Fixed("explorer.typeAhead", "Letters", "Explorer: jump to an item by typing its name", AppActionContexts.Explorer, "Type the first letters of a name to select it.", "type ahead", "jump", "letters"),
    ];

    /// <summary>One generated action per bindable Toggle / Choice setting (see <see cref="SettingActionCatalog"/>).</summary>
    private static IEnumerable<AppAction> SettingActions()
    {
        var order = 2000;
        foreach (var setting in SettingsCatalog.All)
        {
            if (!SettingActionCatalog.IsBindable(setting)) continue;
            var toggle = setting.Kind == SettingKind.Toggle;
            var id = "setting." + setting.Id;
            var label = (toggle ? "Toggle: " : "Cycle: ") + setting.Label;
            var terms = setting.SearchTerms.Concat([toggle ? "toggle" : "cycle", "setting"])
                .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            yield return new AppAction(id, label, SettingsCategory, GlideCommand.ChangeSetting)
            {
                Kind = toggle ? AppActionKind.SettingToggle : AppActionKind.SettingCycle,
                SettingId = setting.Id,
                Description = (toggle ? "Turns this setting on or off: " : "Moves this setting to its next option: ") + setting.Description,
                SearchTerms = terms,
                Inputs = AppActionInputs.Hotkey | AppActionInputs.Gesture | AppActionInputs.ContextMenu,
                HotkeySlots = Slot(id, label),
                Gesture = G(id, label, GestureActionCategories.Settings, order++, Everywhere)
            };
        }
    }

    /// <summary>The master list.</summary>
    public static IReadOnlyList<AppAction> All { get; } = BuildAll();

    /// <summary>
    /// Actions that operate on the image shown by the viewer (its file, pixels, view, folder or overlays). They only
    /// run while the viewer is showing: from an Explorer or Home tab the viewer still holds the image of another tab,
    /// and before 5.0.x a key pressed with focus outside the Explorer list deleted / renamed / rotated that hidden image.
    /// </summary>
    // A method, not a static field: BuildAll runs from the static initializer of All, declared above.
    private static HashSet<string> ViewerOnlyIds() => new(StringComparer.OrdinalIgnoreCase)
    {
        "folder.previous", "folder.next", "navigation.toggleLoopFolder", "slideshow.startPause", "view.info", "view.rotateLeft", "view.rotateRight",
        "view.flipHorizontal", "view.flipVertical", "nav.next", "nav.previous", "nav.first", "nav.last", "view.fit",
        "view.fitWidth", "view.fitHeight", "view.fitWindow", "view.actual", "view.toggleFitActual", "view.zoomIn",
        "view.zoomOut", "gif.playPause", "gif.previousFrame", "gif.nextFrame", "file.copyImage", "file.copyFile",
        "file.copyName", "file.copyFolder", "file.copyPath", "file.rename", "file.delete", "file.print", "file.save",
        // external.1-3 are not listed: they work in image AND Explorer tabs (in an Explorer tab on the selected image, see MainWindow.LaunchExternal).
        "file.saveAs", "nav.jumpForward10", "nav.jumpBack10", "view.zoom200",
        "selection.export", "overlay.resetZoom", "overlay.bringFront", "overlay.saveLayout", "overlay.clear",
    };

    private static AppAction[] BuildAll()
    {
        var commands = CommandActions();
        var viewerOnly = ViewerOnlyIds();
        for (var i = 0; i < commands.Length; i++)
            if (viewerOnly.Contains(commands[i].Id)) commands[i] = commands[i] with { Contexts = AppActionContexts.Viewer };
        // Commands that never had a gesture are offered to gesture clicks automatically (unless destructive),
        // so a new feature reaches the gesture picker without touching GestureCatalog.
        var order = 1000;
        for (var i = 0; i < commands.Length; i++)
        {
            var action = commands[i];
            if (action.Gesture is not null || action.Destructive || action.Kind != AppActionKind.Command) continue;
            if ((action.Inputs & AppActionInputs.Gesture) == 0) continue;
            var areas = action.Contexts == AppActionContexts.Explorer ? ExplorerAndChrome
                : action.Category is "Viewing" or "Selection" or "Navigation" or "Slideshow" or "Overlays" ? ViewerAndChrome
                : Everywhere;
            commands[i] = action with { Gesture = G(action.Id, action.Label, GestureCategoryFor(action.Category), order++, areas) };
        }
        return [.. commands, .. GestureOnlyActions(), .. SettingActions(), .. FixedShortcutActions()];
    }

    /// <summary>Maps a hotkey-list category to the gesture picker group.</summary>
    public static string GestureCategoryFor(string hotkeyCategory) => hotkeyCategory switch
    {
        "File" or "External" => GestureActionCategories.File,
        "Application" => GestureActionCategories.Window,
        "Workspace" => GestureActionCategories.General,
        "Navigation" => GestureActionCategories.Navigation,
        "Slideshow" => GestureActionCategories.Slideshow,
        "Viewing" => GestureActionCategories.View,
        "Selection" => GestureActionCategories.Selection,
        "Tabs" => GestureActionCategories.Tabs,
        "Overlays" => GestureActionCategories.Overlay,
        "Explorer" => GestureActionCategories.Explorer,
        SettingsCategory => GestureActionCategories.Settings,
        _ => GestureActionCategories.General
    };

    private static readonly Dictionary<string, AppAction> ById = All.ToDictionary(a => a.Id, StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, AppAction> ByHotkeySlot = All.SelectMany(a => a.HotkeySlots.Select(s => (s.Id, a)))
        .ToDictionary(x => x.Id, x => x.a, StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, AppAction> ByGestureId = All.Where(a => a.Gesture is not null)
        .ToDictionary(a => a.Gesture!.Id, StringComparer.OrdinalIgnoreCase);

    public static AppAction? Find(string? id) => id is not null && ById.TryGetValue(id, out var action) ? action : null;

    /// <summary>The action that owns a hotkey row (for example "nav.nextSpace" belongs to "nav.next").</summary>
    public static AppAction? FindByHotkeySlot(string? slotId) => slotId is not null && ByHotkeySlot.TryGetValue(slotId, out var action) ? action : null;

    /// <summary>The action a saved gesture binding names (legacy short ids such as "next" included).</summary>
    public static AppAction? FindByGestureId(string? gestureId) => gestureId is not null && ByGestureId.TryGetValue(gestureId, out var action) ? action : null;

    /// <summary>The primary (non-alias) command action for a semantic command; null for None / ChangeSetting.</summary>
    public static AppAction? ForCommand(GlideCommand command) =>
        command is GlideCommand.None or GlideCommand.ChangeSetting ? null
            : All.FirstOrDefault(a => a.Command == command && a.Kind == AppActionKind.Command && a.AliasOf is null)
              ?? All.FirstOrDefault(a => a.Command == command && a.Kind == AppActionKind.Command);

    /// <summary>The generated toggle / cycle action of a setting, or null when the setting is not bindable.</summary>
    public static AppAction? ForSetting(string settingId) => Find("setting." + settingId);

    /// <summary>Registry contract checks; the guard tests and the audit script fail on any issue.</summary>
    public static IReadOnlyList<string> Validate()
    {
        var issues = new List<string>();
        issues.AddRange(All.GroupBy(a => a.Id, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1).Select(g => "duplicate action id: " + g.Key));
        issues.AddRange(All.SelectMany(a => a.HotkeySlots).GroupBy(s => s.Id, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1).Select(g => "duplicate hotkey row id: " + g.Key));
        issues.AddRange(All.Where(a => a.Gesture is not null).GroupBy(a => a.Gesture!.Id, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1).Select(g => "duplicate gesture id: " + g.Key));
        foreach (var command in Enum.GetValues<GlideCommand>())
        {
            if (command is GlideCommand.None or GlideCommand.ChangeSetting) continue;
            if (ForCommand(command) is null) issues.Add("GlideCommand without a registry entry: " + command);
        }
        foreach (var action in All)
        {
            if (string.IsNullOrWhiteSpace(action.Label)) issues.Add("action without label: " + action.Id);
            if (string.IsNullOrWhiteSpace(action.Category)) issues.Add("action without category: " + action.Id);
            if (action.Kind == AppActionKind.Command && action.Command == GlideCommand.None) issues.Add("command action without command: " + action.Id);
            if (action.Kind == AppActionKind.Command && action.AliasOf is null && !action.IsHotkeyBindable && (action.Inputs & AppActionInputs.Hotkey) != 0)
                issues.Add("bindable command without a hotkey row: " + action.Id);
            if (action.AliasOf is not null && Find(action.AliasOf) is not { } target) issues.Add($"alias of unknown action: {action.Id} -> {action.AliasOf}");
            else if (action.AliasOf is not null && Find(action.AliasOf)!.Command != action.Command && Find(action.AliasOf)!.Command != GlideCommand.None &&
                     !(action.Command == GlideCommand.OpenContainingFolder && Find(action.AliasOf)!.Command == GlideCommand.ExploreParentFolder))
                issues.Add($"alias runs a different command: {action.Id}");
            if (action.IsSettingAction && (action.SettingId is null || !SettingsCatalog.All.Any(s => string.Equals(s.Id, action.SettingId, StringComparison.OrdinalIgnoreCase))))
                issues.Add("setting action for unknown setting: " + action.Id);
            if (action.Kind == AppActionKind.FixedShortcut && string.IsNullOrWhiteSpace(action.FixedShortcut)) issues.Add("fixed shortcut without keys: " + action.Id);
            if (action.Destructive && action.Gesture is not null) issues.Add("destructive action offered to gestures: " + action.Id);
            if (action.Gesture is { } g && g.Category is var category && !GestureActionCategories.Order.Contains(category))
                issues.Add($"gesture category not shown by the picker: {action.Id} ({category})");
            foreach (var slot in action.HotkeySlots)
                foreach (var key in slot.DefaultShortcuts)
                    if (!ShortcutText.IsCanonical(key)) issues.Add($"default shortcut is not canonical (the router compares canonically, but the list shows the raw text): {slot.Id} '{key}' -> '{ShortcutText.Canonicalize(key)}'");
        }
        foreach (var setting in SettingsCatalog.All)
        {
            if (setting.Kind is not (SettingKind.Toggle or SettingKind.Choice) || setting.FuturePhase is not null) continue;
            if (SettingActionCatalog.Excluded.ContainsKey(setting.Id)) continue;
            if (ForSetting(setting.Id) is null)
                issues.Add($"bindable setting without an action or an exclusion reason: {setting.Id}");
        }
        foreach (var excluded in SettingActionCatalog.Excluded.Keys)
            if (!SettingsCatalog.All.Any(s => string.Equals(s.Id, excluded, StringComparison.OrdinalIgnoreCase)))
                issues.Add("exclusion names an unknown setting: " + excluded);
        return issues;
    }
}
