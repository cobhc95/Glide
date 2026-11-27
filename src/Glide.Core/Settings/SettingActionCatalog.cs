namespace Glide.Core.Settings;

/// <summary>
/// Which catalogued settings are hotkey / gesture bindable, and how. Every <see cref="SettingKind.Toggle"/>
/// and <see cref="SettingKind.Choice"/> entry of <see cref="SettingsCatalog"/> is bindable by default and
/// gets a generated "Toggle: ..." / "Cycle: ..." action in <see cref="Glide.Core.Commands.AppActionRegistry"/>,
/// unless it is listed in <see cref="Excluded"/> with a reason. A Choice needs its option list here (the same
/// strings the Settings combo box shows, in the same order), otherwise it must be excluded. The guard tests
/// and tools/audit-action-registry.py fail when a new Toggle/Choice setting is neither bindable nor excluded,
/// or when an option list drifts from the Settings window.
/// Number, Text and Action settings are never bindable: numbers and paths have no meaningful flip, and
/// Settings-window actions work on the Settings working copy.
/// </summary>
public static class SettingActionCatalog
{
    /// <summary>Choice option lists, in Settings combo-box order.</summary>
    public static IReadOnlyDictionary<string, string[]> Choices { get; } = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
    {
        ["navigation.endOfFolder"] = ["Do nothing", "Loop this folder", "Move to the next folder", "Move to the next folder in a new tab", "Go back to the previous folder", "Show Home", "Ask me each time"],
        ["navigation.hierarchicalPreviousEntry"] = ["First image", "Last image"],
        ["navigation.rateLimitInput"] = ["Mouse only", "Keyboard only", "Both"],
        ["navigation.rateLimitIndicatorPosition"] = ["Top left", "Top right", "Bottom left", "Bottom right", "Centre"],
        ["appearance.theme"] = ["Dark", "Neutral", "Light"],
        ["appearance.accent"] = ["Glide blue", "Cyan", "Teal", "Emerald", "Lime", "Violet", "Indigo", "Magenta", "Rose", "Crimson", "Orange", "Amber", "Custom"],
        ["appearance.glowColor"] = ["Follow accent", "Ice blue", "Cyan", "Teal", "Emerald", "Violet", "Magenta", "Rose", "Amber", "White", "Custom"],
        ["appearance.background"] = ["Follow theme", "Black", "Dark gray", "Neutral gray", "Light gray", "White", "Custom"],
        ["window.showInExplorerTarget"] = ["Glide's Explorer tab", "Windows File Explorer", "Alternative file manager"],
        ["windows.externalOpenBehavior"] = ["Open in new tab", "Open new window", "Overwrite existing tab"],
        ["windows.sameImageBehavior"] = ["Open new instance", "Refresh existing image", "Open new tab"],
        ["windows.alwaysOnTopMode"] = ["Soft", "Hard"],
        ["fullscreen.exitBehavior"] = ["Restore size and location", "Maximized", "Minimized"],
        ["fullscreen.hideCursorMode"] = ["After a delay", "Always while over the picture"],
        ["view.defaultMode"] = ["Fit image", "Fit width", "Fit height", "Fit to window", "100%"],
        ["mouse.leftWindowDrag"] = ["Smart (normal window only)", "Never move window", "Allow move when maximized"],
        ["mouse.rightDragPan"] = ["Smart (pan image / move window)", "Pan image only", "Move window", "Do nothing"],
        ["performance.interactivePanQuality"] = ["Fast", "Balanced", "Full quality"],
        ["status.size"] = ["Tiny", "Very small", "Small", "Medium", "Large", "Extra large"],
        ["status.alignment"] = ["Center", "Left", "Right"],
        ["status.fitScope"] = ["Only the current image", "This session", "This session and future sessions"],
        ["slideshow.direction"] = ["Forward", "Backward"],
        ["navigation.sortOrder"] = ["Name (like Explorer)", "Name (plain characters)", "Type", "Size", "Date modified", "Date created"],
        ["tabs.cycleOrder"] = ["Tab order", "Most recently used"],
        ["tabs.tooltipTextSize"] = ["Small", "Medium", "Large"],
        ["tabs.position"] = ["Top", "Side"],
        ["explorer.locateOpensIn"] = ["Same tab", "Existing or new tab", "Always a new tab", "New window"],
        ["explorer.backReturnsToImage"] = ["Folders, then back to the picture", "Straight back to the picture", "Folders only"],
        ["explorer.defaultView"] = ["Extra large thumbnails", "Large thumbnails", "Medium thumbnails", "Small thumbnails", "List", "Details"],
        ["explorer.scrollMode"] = ["One row per notch", "Rows per notch (fixed)", "Per view (1 / 2 / 3)", "Pixels per notch"],
        ["tabs.lastTabBehavior"] = ["Open home page", "Keep last tab open", "Close program"],
        ["tabs.homePage"] = ["Welcome page", "Recent pictures page", "Explorer page"],
        ["folderNav.order"] = ["Alphabetical", "Modified date (oldest first)", "Creation date (oldest first)"],
        ["overlay.picturePositionTopTabs"] = ["Top left", "Top center", "Top right", "Bottom left", "Bottom center", "Bottom right", "Title bar – left", "Title bar – center", "Title bar – right", "Custom"],   // = TextOverlayPlacements.TopTabsPositions (test-checked)
        ["overlay.picturePositionSideTabs"] = ["Top left", "Top center", "Top right", "Bottom left", "Bottom center", "Bottom right", "Title bar – left", "Title bar – center", "Title bar – right", "Vertical tabs – top", "Vertical tabs – bottom", "Custom"],   // = TextOverlayPlacements.All (test-checked)
        ["overlay.pictureColor"] = ["White", "Soft gray", "Accent", "Amber"],
        ["overlay.titleBarColor"] = ["Same as over the picture", "White", "Soft gray", "Accent", "Amber"],   // = TextOverlayStyle.ColorChoices
        ["overlay.titleBarBorder"] = ["Same as over the picture", "On", "Off"],   // = TextOverlayStyle.BorderChoices
        ["overlay.tabBarColor"] = ["Same as over the picture", "White", "Soft gray", "Accent", "Amber"],
        ["overlay.tabBarBorder"] = ["Same as over the picture", "On", "Off"],
        ["overlay.pictureSeparator"] = ["•", "▪", "|", "–", "/", "None"],   // = TextTemplate.Separators
        ["overlay.separatorColor"] = ["Same as text", "White", "Soft gray", "Accent", "Amber"],   // = TextOverlayStyle.SeparatorColorChoices
        ["overlay.pictureAlignmentTopTabs"] = ["Automatic", "Left", "Center", "Right"],
        ["overlay.pictureAlignmentSideTabs"] = ["Automatic", "Left", "Center", "Right"],
        ["overlay.animationRegion"] = ["Anywhere", "Top half", "Bottom half", "Top left quarter", "Top right quarter", "Bottom left quarter", "Bottom right quarter"],
        ["tabs.newTabPosition"] = ["at the end", "next to the current tab"],
        ["tabs.switchToNewTabs"] = ["foreground", "background"],
        ["tabs.newTabPositionKey"] = ["None", "Shift", "Ctrl", "Alt"],
        ["tabs.newTabForegroundKey"] = ["None", "Shift", "Ctrl", "Alt"],
        ["tabs.duplicatePosition"] = ["next to the current tab", "at the end"],
        ["tabs.duplicateSide"] = ["foreground", "background"],
        ["tabs.previewResizeModifier"] = ["Ctrl", "Shift", "Alt", "None"],
        ["tabs.afterCloseActivate"] = ["Match Ctrl+Tab order", "Previously used tab", "Tab to the right", "Tab to the left", "First tab", "Last tab", "Custom…"],
        ["tabs.afterCloseCustomTarget"] = ["Home", "New empty Explorer tab", "Explorer tab with the closed picture's folder"],
    };

    /// <summary>
    /// Option values a cycle action skips: they need extra input that only the Settings window can collect
    /// (a custom colour from the colour picker).
    /// </summary>
    public static IReadOnlySet<string> SkippedCycleValues { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Custom" };

    /// <summary>Toggle / Choice settings that deliberately get no generated action, with the reason.</summary>
    public static IReadOnlyDictionary<string, string> Excluded { get; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["windows.alwaysOnTop"] = "Covered by the existing action window.alwaysTop (Toggle Always On Top), which also handles Overlay mode.",
        ["general.singleInstance"] = "Process / Explorer-launch policy; only meaningful for the next launch, not as a live hotkey.",
        ["performance.speedBoost"] = "Controls background standby and the tray icon; applied by the Settings commit path only.",
        ["performance.speedBoostTrayIcon"] = "Tray-icon policy tied to Speed Boost standby; applied by the Settings commit path only.",
        ["performance.startWithWindowsInBackground"] = "Writes the Windows Run registry key; must stay an explicit Settings choice.",
        ["performance.initialQuality"] = "Choosing a profile stages a dozen dependent performance values inside the Settings window.",
        ["general.startupAction"] = "Its Custom option needs a path chosen in Settings; only meaningful at the next start.",
        ["general.newTabAction"] = "Its Custom option needs a path chosen in Settings.",
        ["caption.windowedMinimize"] = "Per-button caption configuration; blindly cycling 7 window actions could turn Minimize into Close Glide.",
        ["caption.windowedMaximize"] = "Per-button caption configuration; blindly cycling 7 window actions could turn Maximize into Close Glide.",
        ["caption.windowedClose"] = "Per-button caption configuration; cycling would change what the Close button does without the user seeing it.",
        ["caption.fullscreenMinimize"] = "Per-button caption configuration; see caption.windowedMinimize.",
        ["caption.fullscreenMaximize"] = "Per-button caption configuration; see caption.windowedMaximize.",
        ["caption.fullscreenClose"] = "Per-button caption configuration; see caption.windowedClose.",
        ["performance.thumbnailLanes"] = "A capacity choice for the thumbnail decoder (clamped by memory), not something to cycle from a hotkey.",
        ["performance.thumbnailPrepareFolder"] = "A background-work policy of the Explorer tab next to the lanes choice; not a view toggle.",
        ["explorer.thumbnailCache"] = "A disk-cache policy next to a size limit and a Clear button; turning it off from a hotkey would not delete anything and is not a view toggle.",
        ["windows.explorerThumbnails"] = "Shell thumbnail provider: applied by the thumbnail card's own Apply button, which re-registers the shell extension.",
        ["windows.thumbnailMode"] = "Shell thumbnail provider setting; applied by the thumbnail card's own Apply button (shell registration).",
        ["windows.thumbnailQuality"] = "Shell thumbnail provider setting; applied by the thumbnail card's own Apply button.",
        ["windows.thumbnailMaxSource"] = "Shell thumbnail provider setting; applied by the thumbnail card's own Apply button.",
        ["windows.thumbnailPreferEmbedded"] = "Shell thumbnail provider setting; applied by the thumbnail card's own Apply button.",
        ["overlay.animationRefreshRateHz"] = "Numeric frame-rate target stored as a number; not meaningful as a live toggle.",
        ["backup.card"] = "Opens the Back up and restore card; file dialogs and an import confirmation follow. The app.exportBackup / app.importBackup actions cover hotkeys.",
        ["backup.export"] = "Writes a file chosen in a save dialog; bindable through the app.exportBackup action instead.",
        ["backup.import"] = "Replaces data after a file dialog and a confirmation; bindable through the app.importBackup action instead.",
        ["backup.allSettings"] = "Chooses what Back up and restore exports; a selection inside the card, not a setting to flip from a hotkey.",
        ["backup.hotkeysGestures"] = "Chooses what Back up and restore exports; a selection inside the card, not a setting to flip from a hotkey.",
        ["backup.profiles"] = "Chooses what Back up and restore exports; a selection inside the card, not a setting to flip from a hotkey.",
        ["backup.windowPlacement"] = "Chooses what Back up and restore exports; a selection inside the card, not a setting to flip from a hotkey.",
        ["backup.recentHistory"] = "Chooses what Back up and restore exports; a selection inside the card, not a setting to flip from a hotkey.",
        ["backup.lastSession"] = "Chooses what Back up and restore exports; a selection inside the card, not a setting to flip from a hotkey.",
        ["reset.card"] = "Destructive data wipe (confirmation dialog, backup, restart); a hotkey or gesture must never be able to start it.",
        ["reset.allSettings"] = "Destructive data wipe (confirmation dialog, backup, restart); a hotkey or gesture must never be able to start it.",
        ["reset.hotkeysGestures"] = "Destructive data wipe (confirmation dialog, backup, restart); a hotkey or gesture must never be able to start it.",
        ["reset.profiles"] = "Destructive data wipe (confirmation dialog, backup, restart); a hotkey or gesture must never be able to start it.",
        ["reset.windowPlacement"] = "Destructive data wipe (confirmation dialog, backup, restart); a hotkey or gesture must never be able to start it.",
        ["reset.recentHistory"] = "Destructive data wipe (confirmation dialog, backup, restart); a hotkey or gesture must never be able to start it.",
        ["reset.lastSession"] = "Destructive data wipe (confirmation dialog, backup, restart); a hotkey or gesture must never be able to start it.",
        ["reset.caches"] = "Destructive data wipe (confirmation dialog, backup, restart); a hotkey or gesture must never be able to start it.",
        ["reset.registry"] = "Destructive data wipe (confirmation dialog, backup, restart); a hotkey or gesture must never be able to start it.",
        ["reset.startup"] = "Destructive data wipe (confirmation dialog, backup, restart); a hotkey or gesture must never be able to start it.",
    };

    /// <summary>True when the setting gets a generated Toggle/Cycle action.</summary>
    public static bool IsBindable(SettingDefinition setting) =>
        setting.FuturePhase is null && !Excluded.ContainsKey(setting.Id) &&
        (setting.Kind == SettingKind.Toggle || (setting.Kind == SettingKind.Choice && Choices.ContainsKey(setting.Id)));

    /// <summary>The value a cycle action moves to after <paramref name="current"/> (wraps, skips <see cref="SkippedCycleValues"/>).</summary>
    public static string NextChoice(string settingId, string? current)
    {
        if (!Choices.TryGetValue(settingId, out var options) || options.Length == 0) return current ?? string.Empty;
        var usable = options.Where(o => !SkippedCycleValues.Contains(o)).ToArray();
        if (usable.Length == 0) return current ?? string.Empty;
        var index = Array.FindIndex(usable, o => string.Equals(o, current, StringComparison.OrdinalIgnoreCase));
        return usable[(index + 1) % usable.Length];
    }
}
