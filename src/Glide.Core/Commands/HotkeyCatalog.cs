namespace Glide.Core.Commands;

/// <summary>
/// Canonical keyboard-action catalogue. It is intentionally Avalonia-free so Settings, diagnostics
/// and tests can reason about shortcuts without constructing UI. Shortcut strings are normalized by
/// the UI/input adapter (for example Ctrl+Shift+T, F11, Right, Space).
/// </summary>
public sealed record HotkeyActionDefinition(
    string Id,
    string Label,
    string Category,
    GlideCommand Command,
    string[] DefaultShortcuts);

public static class HotkeyCatalog
{
    public static IReadOnlyList<HotkeyActionDefinition> All { get; } = new HotkeyActionDefinition[]
    {
        new("file.open", "Open file", "File", GlideCommand.OpenFile, ["O", "Ctrl+O"]),
        new("file.openFolder", "Open folder", "File", GlideCommand.OpenFolder, ["Ctrl+Shift+O"]),
        new("app.settings", "Open Settings", "Application", GlideCommand.Settings, ["Ctrl+,"]),
        new("folder.refresh", "Refresh current folder", "File", GlideCommand.RefreshFolder, ["F5", "U", "Ctrl+R"]),
        new("workspace.home", "Open / select Home tab", "Workspace", GlideCommand.Home, []),
        new("folder.previous", "Previous sibling folder", "Navigation", GlideCommand.PreviousFolder, ["Shift+Left"]),
        new("folder.next", "Next sibling folder", "Navigation", GlideCommand.NextFolder, ["Shift+Right"]),
        new("folder.exploreParent", "Explore containing folder", "Navigation", GlideCommand.ExploreParentFolder, []),
        new("slideshow.startPause", "Start / pause / resume slideshow", "Slideshow", GlideCommand.StartPauseSlideshow, ["Shift+A", "S"]),
        new("slideshow.stop", "Stop slideshow session", "Slideshow", GlideCommand.StopSlideshow, []),
        new("view.info", "Toggle image information", "Viewing", GlideCommand.ToggleImageInfo, ["I"]),
        new("view.rotateLeft", "Rotate view left", "Viewing", GlideCommand.RotateLeft, ["L"]),
        new("view.rotateRight", "Rotate view right", "Viewing", GlideCommand.RotateRight, ["R"]),
        new("view.flipHorizontal", "Flip view horizontally", "Viewing", GlideCommand.FlipHorizontal, ["H"]),
        new("view.flipVertical", "Flip view vertically", "Viewing", GlideCommand.FlipVertical, ["V"]),
        new("nav.nextSpace", "Next image (Space)", "Navigation", GlideCommand.NextImage, ["Space"]),
        new("nav.nextRight", "Next image (Right Arrow)", "Navigation", GlideCommand.NextImage, ["Right"]),
        new("nav.nextPage", "Next image (Page Down)", "Navigation", GlideCommand.NextImage, ["PageDown"]),
        new("nav.previousBack", "Previous image (Backspace)", "Navigation", GlideCommand.PreviousImage, ["Backspace"]),
        new("nav.previousLeft", "Previous image (Left Arrow)", "Navigation", GlideCommand.PreviousImage, ["Left"]),
        new("nav.previousPage", "Previous image (Page Up)", "Navigation", GlideCommand.PreviousImage, ["PageUp"]),
        new("nav.first", "First image", "Navigation", GlideCommand.FirstImage, ["Ctrl+Home", "Home"]),
        new("nav.last", "Last image", "Navigation", GlideCommand.LastImage, ["Ctrl+End", "End"]),
        new("view.fullscreen", "Toggle fullscreen", "Viewing", GlideCommand.ToggleFullscreen, ["F", "F11", "Enter"]),
        new("view.fit", "Fit image", "Viewing", GlideCommand.FitImage, ["Shift+W"]),
        new("view.fitWidth", "Fit width", "Viewing", GlideCommand.FitWidth, ["2"]),
        new("view.fitHeight", "Fit height", "Viewing", GlideCommand.FitHeight, ["1"]),
        new("view.actual", "Actual size / 100%", "Viewing", GlideCommand.ActualSize, ["3", "Ctrl+H"]),
        new("view.toggleFitActual", "Toggle Fit / 100%", "Viewing", GlideCommand.ToggleFitActual, []),
        new("view.zoomIn", "Zoom in", "Viewing", GlideCommand.ZoomIn, ["+", "="] ),
        new("view.zoomOut", "Zoom out", "Viewing", GlideCommand.ZoomOut, ["-"] ),
        new("selection.clear", "Clear selection", "Selection", GlideCommand.ClearSelection, ["Esc"]),
        new("tab.new", "New tab", "Tabs", GlideCommand.NewTab, ["Ctrl+T"]),
        new("tab.close", "Close tab", "Tabs", GlideCommand.CloseTab, ["Ctrl+W", "Ctrl+F4"]),
        new("tab.restore", "Restore closed tab", "Tabs", GlideCommand.RestoreClosedTab, ["Ctrl+Shift+T"]),
        new("tab.duplicate", "Duplicate tab", "Tabs", GlideCommand.DuplicateTab, ["Ctrl+Shift+K"]),
        new("window.newSameImage", "New window with current picture", "Application", GlideCommand.NewWindowSameImage, ["Ctrl+N"]),
        new("window.duplicateSession", "Duplicate current window/session", "Application", GlideCommand.DuplicateSessionWindow, ["Ctrl+Shift+N"]),
        new("tab.next", "Next tab", "Tabs", GlideCommand.NextTab, ["Ctrl+Tab", "Ctrl+PageDown"]),
        new("tab.previous", "Previous tab", "Tabs", GlideCommand.PreviousTab, ["Ctrl+Shift+Tab", "Ctrl+PageUp"]),
        new("tab.select1", "Select tab 1", "Tabs", GlideCommand.SelectTab1, ["Ctrl+1"]),
        new("tab.select2", "Select tab 2", "Tabs", GlideCommand.SelectTab2, ["Ctrl+2"]),
        new("tab.select3", "Select tab 3", "Tabs", GlideCommand.SelectTab3, ["Ctrl+3"]),
        new("tab.select4", "Select tab 4", "Tabs", GlideCommand.SelectTab4, ["Ctrl+4"]),
        new("tab.select5", "Select tab 5", "Tabs", GlideCommand.SelectTab5, ["Ctrl+5"]),
        new("tab.select6", "Select tab 6", "Tabs", GlideCommand.SelectTab6, ["Ctrl+6"]),
        new("tab.select7", "Select tab 7", "Tabs", GlideCommand.SelectTab7, ["Ctrl+7"]),
        new("tab.select8", "Select tab 8", "Tabs", GlideCommand.SelectTab8, ["Ctrl+8"]),
        new("tab.selectLast", "Select last tab", "Tabs", GlideCommand.SelectLastTab, ["Ctrl+9"]),
        new("tab.detach", "Move active tab to new Glide window", "Tabs", GlideCommand.DetachTab, []),
        new("window.close", "Close Glide window", "Application", GlideCommand.CloseWindow, ["Ctrl+Shift+W"]),
        new("window.alwaysTop", "Toggle Always On Top", "Application", GlideCommand.ToggleAlwaysOnTop, []),
        new("status.toggle", "Show / hide status surface", "Application", GlideCommand.ToggleStatusSurface, ["Alt+Shift+S"]),
        new("window.transparency", "Show window transparency control", "Application", GlideCommand.ToggleTransparency, []),
        new("view.contextMenu", "Open viewer context menu", "Viewing", GlideCommand.ShowContextMenu, []),
        new("overlay.add", "Add Window-in-Window overlay", "Overlays", GlideCommand.AddOverlay, ["Shift+O"]),
        new("overlay.saveLayout", "Save overlay layout", "Overlays", GlideCommand.SaveOverlayLayout, ["Ctrl+Shift+S"]),
        new("overlay.loadLayout", "Load overlay layout", "Overlays", GlideCommand.LoadOverlayLayout, ["Ctrl+Shift+L"]),
        new("file.copyImage", "Copy image / active selection pixels", "File", GlideCommand.CopyImage, ["Ctrl+C", "Ctrl+Insert"]),
        new("file.copyFile", "Copy underlying image file", "File", GlideCommand.CopyFile, []),
        new("file.copyName", "Copy file name", "File", GlideCommand.CopyFileName, []),
        new("file.copyFolder", "Copy folder path", "File", GlideCommand.CopyFolderPath, []),
        new("file.copyPath", "Copy full path", "File", GlideCommand.CopyFullPath, []),
        new("file.rename", "Rename current file", "File", GlideCommand.RenameFile, ["F2", "F6"]),
        new("file.delete", "Delete current file to Recycle Bin", "File", GlideCommand.DeleteFile, ["Delete"]),
        new("external.1", "Open in external program 1", "External", GlideCommand.ExternalProgram1, ["Shift+1"]),
        new("external.2", "Open in external program 2", "External", GlideCommand.ExternalProgram2, ["Shift+2"]),
        new("external.3", "Open in external program 3", "External", GlideCommand.ExternalProgram3, ["Shift+3"])
    };

    public static Dictionary<string, List<string>> CreateDefaultMap() =>
        All.ToDictionary(x => x.Id, x => x.DefaultShortcuts.ToList(), StringComparer.OrdinalIgnoreCase);

    public static IReadOnlyList<string> PresetNames { get; } =
        ["Glide default", "Windows Photos", "IrfanView", "nomacs", "FastStone", "XnView MP"];

    /// <summary>
    /// Returns a complete editable shortcut map for a named stock familiarity preset. Presets are
    /// opt-in starting points only: Glide default is canonical and remains the fresh-install state.
    /// AssignExclusive removes a borrowed shortcut from any other action before assigning it, so a
    /// compatibility profile never leaves ambiguous duplicate bindings behind.
    /// </summary>
    public static Dictionary<string, List<string>> CreatePresetMap(string? preset)
    {
        var map = CreateDefaultMap();
        if (string.IsNullOrWhiteSpace(preset) || string.Equals(preset, "Glide default", StringComparison.OrdinalIgnoreCase))
            return map;

        void AssignExclusive(string id, params string[] shortcuts)
        {
            var set = shortcuts.ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var key in map.Keys.ToArray())
                map[key].RemoveAll(x => set.Contains(x));
            map[id] = shortcuts.ToList();
        }

        void Replace(string id, params string[] shortcuts) => map[id] = shortcuts.ToList();

        if (string.Equals(preset, "Windows Photos", StringComparison.OrdinalIgnoreCase))
        {
            // Keep the Photos-style core deliberately small: arrows browse; F11 owns fullscreen.
            // Other Glide actions remain available and editable rather than inventing Photos keys.
            Replace("nav.nextSpace");
            Replace("nav.nextPage");
            Replace("nav.previousBack");
            Replace("nav.previousPage");
            Replace("nav.nextRight", "Right");
            Replace("nav.previousLeft", "Left");
            Replace("view.fullscreen", "F11");
            return map;
        }

        if (string.Equals(preset, "IrfanView", StringComparison.OrdinalIgnoreCase))
        {
            // IrfanView familiarity: F/5/6/1 are its long-standing fit/width/height/original-size
            // viewing keys; F11/Enter provide fullscreen familiarity.
            AssignExclusive("view.fullscreen", "F11", "Enter");
            AssignExclusive("view.fit", "F", "Shift+W");
            AssignExclusive("view.fitWidth", "5", "W");
            AssignExclusive("view.fitHeight", "6");
            AssignExclusive("view.actual", "Ctrl+H", "1");
            return map;
        }

        if (string.Equals(preset, "nomacs", StringComparison.OrdinalIgnoreCase))
        {
            // nomacs upstream defaults on Windows: arrows browse, Home/End first/last, PageUp/Down
            // skip, F11 fullscreen, Ctrl+0 reset/fit, Ctrl+1 100%, Ctrl+2 fit frame.
            AssignExclusive("view.fullscreen", "F11");
            AssignExclusive("view.fit", "Ctrl+0", "Ctrl+2");
            AssignExclusive("view.actual", "Ctrl+1");
            Replace("nav.nextSpace"); // Space is slideshow in nomacs, not next-image.
            AssignExclusive("slideshow.startPause", "Space");
            return map;
        }

        if (string.Equals(preset, "FastStone", StringComparison.OrdinalIgnoreCase))
        {
            // FastStone's documented viewer navigation already matches Glide's Space/Right/PageDown
            // and Backspace/Left/PageUp conventions; restrict fullscreen to its F11 convention.
            Replace("view.fullscreen", "F11");
            return map;
        }

        if (string.Equals(preset, "XnView MP", StringComparison.OrdinalIgnoreCase))
        {
            // XnView MP keeps arrows/Home/End/Page navigation familiar. Its 100% command is *; the
            // Avalonia key name emitted for the numeric-keypad multiply key is "Multiply".
            AssignExclusive("view.actual", "Multiply");
            Replace("view.fullscreen", "F11");
            return map;
        }

        // Unknown/future preset names are fail-safe: never silently alter Glide defaults.
        return map;
    }

    public static HotkeyActionDefinition? Find(string id) =>
        All.FirstOrDefault(x => string.Equals(x.Id, id, StringComparison.OrdinalIgnoreCase));

    public static IReadOnlyList<string> ShortcutsForCommand(
        GlideCommand command,
        IReadOnlyDictionary<string, List<string>> bindings)
    {
        return All
            .Where(action => action.Command == command)
            .SelectMany(action => bindings.TryGetValue(action.Id, out var assigned)
                ? (IEnumerable<string>)assigned
                : Array.Empty<string>())
            .Where(shortcut => !string.IsNullOrWhiteSpace(shortcut))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }
}
