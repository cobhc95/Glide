using Glide.Core.Commands;
using Glide.Core.Settings;
using Glide.Input;

namespace Glide.Core.Tests;

/// <summary>
/// Guard rails for the master action registry (5.0.x): every command, hotkey row, gesture action, setting toggle
/// and fixed shortcut comes from AppActionRegistry, and the historical ids / defaults keep loading unchanged.
/// </summary>
[Trait("Area", "Hotkeys")]
public sealed class AppActionRegistryTests
{
    [Fact]
    public void Registry_contract_has_no_violations()
    {
        Assert.Empty(AppActionRegistry.Validate());
    }

    [Fact]
    public void Every_semantic_command_has_exactly_one_primary_registry_action()
    {
        foreach (var command in Enum.GetValues<GlideCommand>())
        {
            if (command is GlideCommand.None or GlideCommand.ChangeSetting) continue;
            var action = AppActionRegistry.ForCommand(command);
            Assert.True(action is not null, $"{command} has no registry action");
            Assert.Equal(command, action!.Command);
            // OpenContainingFolder is the documented duplicate of ExploreParentFolder: its only entry is an alias.
            if (action.AliasOf is not null) Assert.Equal(GlideCommand.OpenContainingFolder, command);
            var primaries = AppActionRegistry.All.Count(a => a.Command == command && a.Kind == AppActionKind.Command && a.AliasOf is null);
            Assert.True(primaries == (command == GlideCommand.OpenContainingFolder ? 0 : 1), $"{command}: {primaries} primary actions");
        }
    }

    /// <summary>The pre-registry HotkeyCatalog, verbatim (id, label, category, command, defaults). Saved maps,
    /// custom presets and the router's first-match priority depend on these rows staying exactly as they were.</summary>
    private static readonly (string Id, string Label, string Category, GlideCommand Command, string[] Defaults)[] LegacyHotkeys =
    [
        ("file.open", "Open file", "File", GlideCommand.OpenFile, ["O", "Ctrl+O"]),
        ("file.openFolder", "Open folder", "File", GlideCommand.OpenFolder, ["Ctrl+Shift+O"]),
        ("app.settings", "Open Settings", "Application", GlideCommand.Settings, ["Ctrl+,"]),
        ("folder.refresh", "Refresh current folder", "File", GlideCommand.RefreshFolder, ["F5", "U", "Ctrl+R"]),
        ("workspace.home", "Open / select Home tab", "Workspace", GlideCommand.Home, []),
        ("folder.previous", "Previous sibling folder", "Navigation", GlideCommand.PreviousFolder, ["Shift+Left"]),
        ("folder.next", "Next sibling folder", "Navigation", GlideCommand.NextFolder, ["Shift+Right"]),
        ("folder.exploreParent", "Explore/Browse containing folder", "Navigation", GlideCommand.ExploreParentFolder, []),
        ("slideshow.startPause", "Start / pause / resume slideshow", "Slideshow", GlideCommand.StartPauseSlideshow, ["Shift+A", "S"]),
        ("slideshow.stop", "Stop slideshow session", "Slideshow", GlideCommand.StopSlideshow, []),
        ("view.info", "Toggle image information", "Viewing", GlideCommand.ToggleImageInfo, ["I"]),
        ("view.rotateLeft", "Rotate view left", "Viewing", GlideCommand.RotateLeft, ["L"]),
        ("view.rotateRight", "Rotate view right", "Viewing", GlideCommand.RotateRight, ["R"]),
        ("view.flipHorizontal", "Flip view horizontally", "Viewing", GlideCommand.FlipHorizontal, ["H"]),
        ("view.flipVertical", "Flip view vertically", "Viewing", GlideCommand.FlipVertical, ["V"]),
        ("nav.nextSpace", "Next image (Space)", "Navigation", GlideCommand.NextImage, ["Space"]),
        ("nav.nextRight", "Next image (Right Arrow)", "Navigation", GlideCommand.NextImage, ["Right"]),
        ("nav.nextPage", "Next image (Page Down)", "Navigation", GlideCommand.NextImage, ["PageDown"]),
        ("nav.previousBack", "Previous image (extra key)", "Navigation", GlideCommand.PreviousImage, []),   // 5.0.0 owner decision: relabelled, Backspace moved to Back
        ("nav.previousLeft", "Previous image (Left Arrow)", "Navigation", GlideCommand.PreviousImage, ["Left"]),
        ("nav.previousPage", "Previous image (Page Up)", "Navigation", GlideCommand.PreviousImage, ["PageUp"]),
        ("nav.first", "First image", "Navigation", GlideCommand.FirstImage, ["Ctrl+Home", "Home"]),
        ("nav.last", "Last image", "Navigation", GlideCommand.LastImage, ["Ctrl+End", "End"]),
        ("view.fullscreen", "Toggle fullscreen", "Viewing", GlideCommand.ToggleFullscreen, ["F", "F11", "Enter"]),
        ("view.fit", "Fit image", "Viewing", GlideCommand.FitImage, ["Shift+W"]),
        ("view.fitWidth", "Fit width", "Viewing", GlideCommand.FitWidth, ["2"]),
        ("view.fitHeight", "Fit height", "Viewing", GlideCommand.FitHeight, ["1"]),
        ("view.fitWindow", "Fit to window (enlarge or shrink)", "Viewing", GlideCommand.FitWindow, ["4"]),
        ("view.actual", "Actual size / 100%", "Viewing", GlideCommand.ActualSize, ["3", "Ctrl+H"]),
        ("view.toggleFitActual", "Toggle Fit / 100%", "Viewing", GlideCommand.ToggleFitActual, []),
        ("view.zoomIn", "Zoom in", "Viewing", GlideCommand.ZoomIn, ["+", "="]),
        ("view.zoomOut", "Zoom out", "Viewing", GlideCommand.ZoomOut, ["-"]),
        ("gif.playPause", "Pause / play animated GIF", "Viewing", GlideCommand.GifPlayPause, ["P"]),
        ("gif.previousFrame", "Previous GIF frame", "Viewing", GlideCommand.GifPreviousFrame, [","]),
        ("gif.nextFrame", "Next GIF frame", "Viewing", GlideCommand.GifNextFrame, ["."]),
        ("selection.clear", "Clear selection", "Selection", GlideCommand.ClearSelection, ["Esc"]),
        ("tab.new", "New tab", "Tabs", GlideCommand.NewTab, ["Ctrl+T"]),
        ("tab.close", "Close tab", "Tabs", GlideCommand.CloseTab, ["Ctrl+W", "Ctrl+F4"]),
        ("tab.restore", "Restore closed tab", "Tabs", GlideCommand.RestoreClosedTab, ["Ctrl+Shift+T"]),
        ("tab.duplicate", "Duplicate tab", "Tabs", GlideCommand.DuplicateTab, ["Ctrl+Shift+K"]),
        ("window.newSameImage", "New window with current picture", "Application", GlideCommand.NewWindowSameImage, ["Ctrl+N"]),
        ("window.duplicateSession", "Duplicate current window/session", "Application", GlideCommand.DuplicateSessionWindow, ["Ctrl+Shift+N"]),
        ("tab.next", "Next tab", "Tabs", GlideCommand.NextTab, ["Ctrl+Tab", "Ctrl+PageDown"]),
        ("tab.previous", "Previous tab", "Tabs", GlideCommand.PreviousTab, ["Ctrl+Shift+Tab", "Ctrl+PageUp"]),
        ("tab.select1", "Select tab 1", "Tabs", GlideCommand.SelectTab1, ["Ctrl+1"]),
        ("tab.select2", "Select tab 2", "Tabs", GlideCommand.SelectTab2, ["Ctrl+2"]),
        ("tab.select3", "Select tab 3", "Tabs", GlideCommand.SelectTab3, ["Ctrl+3"]),
        ("tab.select4", "Select tab 4", "Tabs", GlideCommand.SelectTab4, ["Ctrl+4"]),
        ("tab.select5", "Select tab 5", "Tabs", GlideCommand.SelectTab5, ["Ctrl+5"]),
        ("tab.select6", "Select tab 6", "Tabs", GlideCommand.SelectTab6, ["Ctrl+6"]),
        ("tab.select7", "Select tab 7", "Tabs", GlideCommand.SelectTab7, ["Ctrl+7"]),
        ("tab.select8", "Select tab 8", "Tabs", GlideCommand.SelectTab8, ["Ctrl+8"]),
        ("tab.selectLast", "Select last tab", "Tabs", GlideCommand.SelectLastTab, ["Ctrl+9"]),
        ("tab.detach", "Move active tab to new Glide window", "Tabs", GlideCommand.DetachTab, []),
        ("window.close", "Close Glide window", "Application", GlideCommand.CloseWindow, ["Ctrl+Shift+W"]),
        ("window.alwaysTop", "Toggle Always On Top", "Application", GlideCommand.ToggleAlwaysOnTop, []),
        ("status.toggle", "Show / hide status surface", "Application", GlideCommand.ToggleStatusSurface, ["Alt+Shift+S"]),
        ("window.transparency", "Show window transparency control", "Application", GlideCommand.ToggleTransparency, []),
        ("view.contextMenu", "Open viewer context menu", "Viewing", GlideCommand.ShowContextMenu, []),
        ("overlay.add", "Add Window-in-Window overlay", "Overlays", GlideCommand.AddOverlay, ["Shift+O"]),
        ("overlay.saveLayout", "Save overlay layout", "Overlays", GlideCommand.SaveOverlayLayout, ["Ctrl+Shift+S"]),
        ("overlay.loadLayout", "Load overlay layout", "Overlays", GlideCommand.LoadOverlayLayout, ["Ctrl+Shift+L"]),
        ("file.copyImage", "Copy image / active selection pixels", "File", GlideCommand.CopyImage, ["Ctrl+C", "Ctrl+Insert"]),
        ("file.copyFile", "Copy underlying image file", "File", GlideCommand.CopyFile, []),
        ("file.copyName", "Copy file name", "File", GlideCommand.CopyFileName, []),
        ("file.copyFolder", "Copy folder path", "File", GlideCommand.CopyFolderPath, []),
        ("file.copyPath", "Copy full path", "File", GlideCommand.CopyFullPath, []),
        ("file.rename", "Rename current file", "File", GlideCommand.RenameFile, ["F2", "F6"]),
        ("file.delete", "Delete current file to Recycle Bin", "File", GlideCommand.DeleteFile, ["Delete"]),
        ("file.print", "Print current image", "File", GlideCommand.Print, ["Ctrl+P"]),
        ("file.save", "Save (overwrite with current rotation/flip)", "File", GlideCommand.SaveImage, ["Ctrl+S"]),
        ("file.saveAs", "Save as…", "File", GlideCommand.SaveImageAs, ["Ctrl+Alt+S"]),
        ("external.1", "Open in external program 1", "External", GlideCommand.ExternalProgram1, ["Shift+1"]),
        ("external.2", "Open in external program 2", "External", GlideCommand.ExternalProgram2, ["Shift+2"]),
        ("external.3", "Open in external program 3", "External", GlideCommand.ExternalProgram3, ["Shift+3"]),
    ];

    [Fact]
    public void The_75_historical_hotkey_rows_are_unchanged_and_first_in_the_same_order()
    {
        Assert.Equal(75, LegacyHotkeys.Length);
        Assert.Equal(LegacyHotkeys.Select(x => x.Id), HotkeyCatalog.All.Take(75).Select(x => x.Id));
        Assert.Equal(LegacyHotkeys.Select(x => x.Id), HotkeyCatalog.LegacyIds);
        foreach (var legacy in LegacyHotkeys)
        {
            var row = HotkeyCatalog.Find(legacy.Id)!;
            Assert.Equal(legacy.Label, row.Label);
            Assert.Equal(legacy.Category, row.Category);
            Assert.Equal(legacy.Command, row.Command);
            // Same keys. One owner-approved exception (5.0.0 audit O7): "Show / hide status bar" moved from Alt+Shift+S (the
            // Windows input-language chord) to Ctrl+Shift+B; saved files on the old default are migrated (see StatusToggleDefaultTests).
            // Second exception (5.0.0): "Explore/Browse containing folder" got Ctrl+E (see AuditItemsTests).
            // Third exception (5.0.0 owner decision): Backspace moved from nav.previousBack to Back (nav.back); the row keeps its id.
            var expected = legacy.Id == "status.toggle" ? new[] { "Ctrl+Shift+B" }
                : legacy.Id == "nav.previousBack" ? Array.Empty<string>()
                : legacy.Id == "folder.exploreParent" ? new[] { "Ctrl+E" }
                : legacy.Defaults.Select(ShortcutText.Canonicalize).ToArray();
            Assert.Equal(expected, row.DefaultShortcuts);
        }
    }

    [Fact]
    public void The_56_historical_gesture_actions_keep_their_ids_labels_and_commands()
    {
        var expected = new Dictionary<string, GlideCommand>
        {
            ["next"] = GlideCommand.NextImage, ["previous"] = GlideCommand.PreviousImage, ["zoomIn"] = GlideCommand.ZoomIn,
            ["zoomOut"] = GlideCommand.ZoomOut, ["fit"] = GlideCommand.FitImage, ["actual"] = GlideCommand.ActualSize,
            ["toggleFitActual"] = GlideCommand.ToggleFitActual, ["fullscreen"] = GlideCommand.ToggleFullscreen,
            ["contextMenu"] = GlideCommand.ShowContextMenu, ["clearSelection"] = GlideCommand.ClearSelection,
            ["toggleInfo"] = GlideCommand.ToggleImageInfo, ["settings"] = GlideCommand.Settings,
            ["resetOverlayZoom"] = GlideCommand.ResetSelectedOverlayZoom, ["bringOverlayFront"] = GlideCommand.BringSelectedOverlayFront,
            ["firstImage"] = GlideCommand.FirstImage, ["lastImage"] = GlideCommand.LastImage, ["jumpForward10"] = GlideCommand.JumpForward10,
            ["jumpBack10"] = GlideCommand.JumpBack10, ["nextFolder"] = GlideCommand.NextFolder, ["previousFolder"] = GlideCommand.PreviousFolder,
            ["zoom200"] = GlideCommand.ZoomTo200, ["rotateLeft"] = GlideCommand.RotateLeft, ["rotateRight"] = GlideCommand.RotateRight,
            ["flipHorizontal"] = GlideCommand.FlipHorizontal, ["flipVertical"] = GlideCommand.FlipVertical, ["newTab"] = GlideCommand.NewTab,
            ["closeTab"] = GlideCommand.CloseTab, ["reopenClosedTab"] = GlideCommand.RestoreClosedTab, ["duplicateTab"] = GlideCommand.DuplicateTab,
            ["detachTab"] = GlideCommand.DetachTab, ["toggleStatusBar"] = GlideCommand.ToggleStatusSurface,
            ["toggleAlwaysOnTop"] = GlideCommand.ToggleAlwaysOnTop, ["maximizeRestore"] = GlideCommand.MaximizeRestoreWindow,
            ["minimizeWindow"] = GlideCommand.MinimizeWindow, ["toggleSlideshow"] = GlideCommand.StartPauseSlideshow,
            ["stopSlideshow"] = GlideCommand.StopSlideshow, ["copyImage"] = GlideCommand.CopyImage, ["showInExplorer"] = GlideCommand.OpenContainingFolder,
        };
        Assert.Equal(56, GestureCatalog.LegacyActionIds.Length);
        // The historical actions come first, in their historical order.
        Assert.Equal(GestureCatalog.LegacyActionIds, GestureCatalog.Actions.Take(56).Select(a => a.Id));
        foreach (var id in GestureCatalog.LegacyActionIds)
        {
            var action = GestureCatalog.FindAction(id);
            Assert.True(action is not null, id);
            Assert.Equal(expected.GetValueOrDefault(id, GlideCommand.None), action!.Command);
        }
        Assert.Equal("Next image", GestureCatalog.FindAction("next")!.Label);
        Assert.Equal("Show in File Explorer", GestureCatalog.FindAction("showInExplorer")!.Label);
    }

    [Fact]
    public void Every_default_shortcut_is_canonical_and_owned_by_one_row()
    {
        foreach (var row in HotkeyCatalog.All)
            foreach (var key in row.DefaultShortcuts)
                Assert.True(ShortcutText.IsCanonical(key), $"{row.Id}: '{key}' should be written '{ShortcutText.Canonicalize(key)}'");
        var collisions = HotkeyCatalog.All
            .SelectMany(row => row.DefaultShortcuts.Select(key => (row.Id, Key: ShortcutText.Canonicalize(key))))
            .GroupBy(x => x.Key, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Select(x => x.Id).Distinct().Count() > 1)
            .Select(g => g.Key)
            .ToArray();
        Assert.Empty(collisions);
    }

    [Theory]
    [InlineData("Alt+Shift+S", "Shift+Alt+S")]
    [InlineData("shift+alt+s", "Shift+Alt+S")]
    [InlineData("Alt+Ctrl+Shift+Left", "Ctrl+Shift+Alt+Left")]
    [InlineData("Control+escape", "Ctrl+Esc")]
    [InlineData("+", "+")]
    [InlineData("Ctrl++", "Ctrl++")]
    [InlineData("Ctrl+,", "Ctrl+,")]
    [InlineData("f11", "F11")]
    [InlineData("MouseX1", "MouseX1")]
    public void Shortcut_text_is_canonicalised(string raw, string canonical)
    {
        Assert.Equal(canonical, ShortcutText.Canonicalize(raw));
        Assert.True(ShortcutText.AreEqual(raw, canonical));
    }

    [Fact]
    public void Router_matches_a_binding_saved_with_another_modifier_order()
    {
        // The reported bug: settings files store "Alt+Shift+S"; the keyboard adapter produces "Shift+Alt+S".
        var saved = HotkeyCatalog.CreateDefaultMap();
        saved["status.toggle"] = new List<string> { "Alt+Shift+S" };
        var router = new InputRouter();
        Assert.Equal(GlideCommand.ToggleStatusSurface, router.ResolveShortcut("Shift+Alt+S", saved));
        Assert.Equal("status.toggle", router.ResolveShortcutAction("Shift+Alt+S", saved)!.Id);
        // The default moved to Ctrl+Shift+B; the old default only fires while a saved binding still says so.
        Assert.Equal(GlideCommand.ToggleStatusSurface, router.ResolveShortcut("Ctrl+Shift+B"));
        Assert.Equal(GlideCommand.None, router.ResolveShortcut("Shift+Alt+S"));
    }

    [Fact]
    public void Conflict_detection_sees_the_same_key_in_another_spelling()
    {
        var map = HotkeyCatalog.CreateDefaultMap();
        map["status.toggle"] = new List<string> { "Alt+Shift+S" };
        var conflict = HotkeyConflicts.Check(map, "Shift+Alt+S", "view.fit");
        Assert.Equal(HotkeyConflictKind.OtherAction, conflict.Kind);
        Assert.Contains(conflict.Owners, o => o.ActionId == "status.toggle");
        Assert.Contains("Show / hide status surface", HotkeyLookup.Describe(map, "Shift+Alt+S"));
    }

    [Fact]
    public void Every_toggle_and_choice_setting_is_bindable_or_excluded_with_a_reason()
    {
        foreach (var setting in SettingsCatalog.All.Where(s => s.Kind is SettingKind.Toggle or SettingKind.Choice && s.FuturePhase is null))
        {
            if (SettingActionCatalog.Excluded.TryGetValue(setting.Id, out var reason))
            {
                Assert.False(string.IsNullOrWhiteSpace(reason));
                Assert.Null(AppActionRegistry.ForSetting(setting.Id));
                continue;
            }
            var action = AppActionRegistry.ForSetting(setting.Id);
            Assert.True(action is not null, $"{setting.Id} has neither a generated action nor an exclusion");
            Assert.Equal(setting.Kind == SettingKind.Toggle ? AppActionKind.SettingToggle : AppActionKind.SettingCycle, action!.Kind);
            // Hotkey-bindable, gesture-bindable and searchable.
            Assert.NotNull(HotkeyCatalog.Find(action.Id));
            Assert.NotNull(GestureCatalog.FindAction(action.Id));
            Assert.Contains(setting.Label, action.Label);
        }
    }

    [Fact]
    public void Status_bar_auto_hide_and_reserve_fit_space_are_hotkey_bindable()
    {
        // Named in the owner request: show/hide status bar, always on top, auto-hide status bar, reserve fit space, slideshow.
        Assert.NotNull(HotkeyCatalog.Find("setting.status.autoHide"));
        Assert.NotNull(HotkeyCatalog.Find("setting.status.reserveFitSpace"));
        Assert.NotNull(HotkeyCatalog.Find("setting.status.visible"));
        Assert.NotNull(HotkeyCatalog.Find("window.alwaysTop"));
        Assert.NotNull(HotkeyCatalog.Find("setting.slideshow.loop"));
        Assert.NotNull(HotkeyCatalog.Find("setting.slideshow.shuffle"));
    }

    [Fact]
    public void Cycle_actions_wrap_and_skip_values_that_need_the_settings_window()
    {
        Assert.Equal("Neutral", SettingActionCatalog.NextChoice("appearance.theme", "Dark"));
        Assert.Equal("Dark", SettingActionCatalog.NextChoice("appearance.theme", "Light"));
        Assert.Equal("Glide blue", SettingActionCatalog.NextChoice("appearance.accent", "Amber")); // skips Custom
        Assert.Equal("Tiny", SettingActionCatalog.NextChoice("status.size", "unknown value"));
    }

    [Fact]
    public void Every_registry_action_is_searchable_and_listed_where_it_is_bindable()
    {
        var fixedIds = HotkeyCatalog.FixedShortcuts.Select(f => f.Id).ToHashSet();
        foreach (var action in AppActionRegistry.All)
        {
            Assert.False(string.IsNullOrWhiteSpace(action.Label), action.Id);
            Assert.False(string.IsNullOrWhiteSpace(action.Category), action.Id);
            switch (action.Kind)
            {
                case AppActionKind.FixedShortcut:
                    Assert.Contains(action.Id, fixedIds);
                    break;
                case AppActionKind.GestureOnly:
                    Assert.NotNull(GestureCatalog.FindAction(action.Gesture!.Id));
                    break;
                default:
                    if (action.AliasOf is not null) { Assert.NotNull(HotkeyCatalog.Find(AppActionRegistry.Find(action.AliasOf)!.HotkeySlots[0].Id)); break; }
                    Assert.True(action.IsHotkeyBindable, $"{action.Id} is not in the hotkey list");
                    foreach (var slot in action.HotkeySlots) Assert.NotNull(HotkeyCatalog.Find(slot.Id));
                    break;
            }
        }
    }

    [Fact]
    public void Destructive_actions_are_never_offered_to_gestures()
    {
        foreach (var action in AppActionRegistry.All.Where(a => a.Destructive))
            Assert.Null(action.Gesture);
        Assert.Null(GestureCatalog.FindAction("file.delete"));
        Assert.Null(GestureCatalog.FindAction("window.close"));
    }

    [Fact]
    public void New_commands_reach_the_gesture_picker_automatically()
    {
        // A command without a hand-written gesture entry is offered to click rows by the registry.
        var copyPath = GestureCatalog.FindAction("file.copyPath");
        Assert.NotNull(copyPath);
        Assert.Equal(GlideCommand.CopyFullPath, copyPath!.Command);
        Assert.True(GestureCatalog.IsOffered("windowed.middleClickImage", "file.copyPath"));
        Assert.True(GestureCatalog.IsOffered("windowed.middleClickImage", "setting.status.autoHide"));
        Assert.False(GestureCatalog.IsOffered("wheel.windowed", "file.copyPath"));
        // Commands that open a dialog or write a file keep the 4.8.0 rule: never on a stray click.
        Assert.Null(GestureCatalog.FindAction("file.print"));
        Assert.Null(GestureCatalog.FindAction("file.rename"));
        Assert.Null(GestureCatalog.FindAction("file.save"));
    }

    [Fact]
    public void Saved_gesture_bindings_with_legacy_ids_still_resolve()
    {
        var bindings = GestureCatalog.CreateDefaultMap();
        bindings["titleBar.middleClick"] = "maximizeRestore";
        bindings["windowed.middleClickImage"] = "showInExplorer";
        Assert.Equal(GlideCommand.MaximizeRestoreWindow, new InputRouter().ResolveGestureCommand("titleBar.middleClick", bindings));
        Assert.Equal(GlideCommand.OpenContainingFolder, new InputRouter().ResolveGestureCommand("windowed.middleClickImage", bindings));
    }

    [Fact]
    public void Fixed_shortcuts_are_found_by_search_hotkey()
    {
        var map = HotkeyCatalog.CreateDefaultMap();
        Assert.Contains(HotkeyLookup.FixedOwners("Ctrl+F"), f => f.Id == "fixed.explorer.filter");
        Assert.Contains("Explorer: focus the filter box", HotkeyLookup.Describe(map, "Ctrl+F"));
    }
}
