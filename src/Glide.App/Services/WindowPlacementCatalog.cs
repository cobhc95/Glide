using Glide.Core.Windows;

namespace Glide.App.Services;

/// <summary>
/// Inventory of every top-level window Glide can open and what it remembers (PositionOnly dialogs remember nothing
/// since 5.0.6 and always open centred on their owner; their keys stay registered). Keys are stable ids used in
/// glide.settings.json ("WindowPlacements"); never rename one (a renamed key forgets the user's placement).
/// A guard-rail test (WindowPlacementGuardTests) fails when a new Window subclass or a new ad-hoc <c>new Window</c>
/// dialog is not registered here and tracked with <see cref="WindowPlacementService.Track"/>, or allow-listed below
/// with a reason.
/// </summary>
public static class WindowPlacementCatalog
{
    public sealed record Entry(string Key, string Window, WindowPlacementMode Mode, string Where);

    public static IReadOnlyList<Entry> Entries { get; } =
    [
        new("print", "Print window (PrintWindow)", WindowPlacementMode.SizeAndPosition, "Printing/PrintWindow.cs"),
        new("settings", "Settings window (SettingsWindow; own Cancel-aware code, same store and resolver)", WindowPlacementMode.SizeAndPosition, "Settings/SettingsWindow.Placement.cs"),
        new("titlebar-customizer", "Customize title-bar buttons (TitleBarCustomizerWindow)", WindowPlacementMode.SizeAndPosition, "Settings/TitleBarCustomizerWindow.cs"),
        new("statusbar-customizer", "Customize status-bar buttons (StatusBarCustomizerWindow)", WindowPlacementMode.SizeAndPosition, "Settings/StatusBarCustomizerWindow.cs"),
        new("diagnostics-results", "Glide Diagnostics results", WindowPlacementMode.SizeAndPosition, "Settings/SettingsWindow.Diagnostics.cs"),
        new("settings-discard-confirm", "Discard Settings changes? (content-sized height)", WindowPlacementMode.PositionOnly, "Settings/SettingsWindow.axaml.cs"),
        new("reset-confirm", "Reset Glide confirmation (ResetConfirmWindow)", WindowPlacementMode.PositionOnly, "Settings/ResetDialogs.cs"),
        new("reset-result", "Reset Glide report (ResetResultWindow)", WindowPlacementMode.PositionOnly, "Settings/ResetDialogs.cs"),
        new("backup-import", "Import Glide backup (BackupImportWindow)", WindowPlacementMode.PositionOnly, "Settings/BackupDialogs.cs"),
        new("hotkey-preset-name", "Save hotkey preset (name prompt)", WindowPlacementMode.PositionOnly, "Settings/SettingsWindow.Hotkeys.cs"),
        new("hotkey-confirm", "Hotkey preset / conflict confirmation", WindowPlacementMode.PositionOnly, "Settings/SettingsWindow.Hotkeys.cs"),
        new("profile-add", "Add profile (name prompt)", WindowPlacementMode.PositionOnly, "Settings/SettingsWindow.Profiles.cs"),
        new("confirm", "Main-window confirmations and errors (save/overwrite, Explorer delete and file-operation errors)", WindowPlacementMode.PositionOnly, "MainWindow.Commands.cs (ConfirmAsync)"),
        new("slideshow-start", "Start slideshow", WindowPlacementMode.PositionOnly, "MainWindow.Slideshow.cs"),
        new("folder-boundary-confirm", "Continue to nearby folder?", WindowPlacementMode.PositionOnly, "MainWindow.Navigation.cs"),
        new("multi-tab-close-confirm", "Close Glide tabs?", WindowPlacementMode.PositionOnly, "MainWindow.Sessions.cs"),
        new("batch-rename", "Rename several items (BatchRenameDialog)", WindowPlacementMode.SizeAndPosition, "Controls/BatchRenameDialog.cs"),
        new("rename-image", "Rename image", WindowPlacementMode.PositionOnly, "MainWindow.Sessions.cs"),
        new("overlay-video-unavailable", "Can't play this video", WindowPlacementMode.PositionOnly, "MainWindow.Tabs.cs"),
        new("escape-close-confirm", "Close Glide? (Escape)", WindowPlacementMode.PositionOnly, "MainWindow.Tabs.cs"),
        new("update-prompt", "A new version of Glide is available (UpdatePromptWindow)", WindowPlacementMode.PositionOnly, "Updates/UpdatePromptWindow.cs"),
    ];

    /// <summary>
    /// Window subclasses that deliberately do not use <see cref="WindowPlacementService.Track"/>, with the reason.
    /// Everything else deriving from Avalonia's Window in Glide.App must be tracked.
    /// </summary>
    public static IReadOnlyDictionary<string, string> AllowList { get; } = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["Glide.App.MainWindow"] =
            "The main window (and detached-tab / new-window copies of it) has its own placement: WindowX/Y/Width/Height/" +
            "WindowWasMaximized with Remember window placement, fullscreen restore, and per-image overlay placements " +
            "(OverlayWindowPlacements). Window-in-Window overlays are drawn inside it, not separate top-level windows.",
        ["Glide.App.Settings.SettingsWindow"] =
            "Uses its own Cancel-rollback aware code (SettingsWindow.Placement.cs) on the same resolver " +
            "(Glide.Core WindowPlacementResolver) and the same store key \"settings\" (older SettingsWindow* keys still read).",
    };

    public static Entry? Find(string key) => Entries.FirstOrDefault(e => string.Equals(e.Key, key, StringComparison.OrdinalIgnoreCase));
}
