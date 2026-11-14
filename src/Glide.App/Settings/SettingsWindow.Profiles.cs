using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Glide.App.Services;

namespace Glide.App.Settings;

/// <summary>
/// Settings profiles: the saved-profile list, add/rename, save and load.
/// Split from SettingsWindow.axaml.cs; behaviour unchanged.
/// </summary>
public partial class SettingsWindow
{
    private void BuildProfileRows()
    {
        if (ProfileRowsHost is null) return;
        ProfileRowsHost.Children.Clear();
        foreach (var profile in ProfileStore.ListProfiles())
        {
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto"), ColumnSpacing = 8 };
            var name = new TextBox { Text = profile.Name, Tag = profile.Id, MinWidth = 210, Watermark = "Profile name" };
            name.LostFocus += ProfileNameLostFocus;
            name.KeyDown += (_, e) => { if (e.Key == Key.Enter) { CommitProfileRename(name); e.Handled = true; } };
            ToolTip.SetTip(name, "Rename this profile. Press Enter or click elsewhere to save the new name.");
            row.Children.Add(name);

            var save = new Button { Content = "Save...", Tag = profile.Id, MinWidth = 82 };
            save.Classes.Add("secondaryAction");
            save.Click += SaveProfileClicked;
            ToolTip.SetTip(save, "Save the current staged Glide settings to this profile and choose a portable profile file location.");
            Grid.SetColumn(save, 1); row.Children.Add(save);

            var load = new Button { Content = "Load...", Tag = profile.Id, MinWidth = 82 };
            load.Classes.Add("secondaryAction");
            load.Click += LoadProfileClicked;
            ToolTip.SetTip(load, "Choose a Glide profile file in Explorer, load it into this named profile, and stage its settings.");
            Grid.SetColumn(load, 2); row.Children.Add(load);
            ProfileRowsHost.Children.Add(row);
        }
    }

    private void ProfileNameLostFocus(object? sender, RoutedEventArgs e)
    {
        if (sender is TextBox box) CommitProfileRename(box);
    }

    private void CommitProfileRename(TextBox box)
    {
        if (box.Tag is not Guid id) return;
        box.Text = ProfileStore.Rename(id, box.Text ?? string.Empty);
    }

    private async void AddProfileClicked(object? sender, RoutedEventArgs e)
    {
        var name = await PromptProfileNameAsync();
        if (string.IsNullOrWhiteSpace(name)) return;
        ProfileStore.Add(name, ReadControls());
        BuildProfileRows();
        DirtyText.Text = $"Created profile ‘{name.Trim()}’.";
    }

    private async Task<string?> PromptProfileNameAsync()
    {
        var box = new TextBox { Watermark = "Profile name", MinWidth = 300, Text = $"Profile {ProfileStore.ListProfiles().Count + 1}" };
        var ok = new Button { Content = "Create", MinWidth = 90 };
        ok.Classes.Add("primaryAction");
        var cancel = new Button { Content = "Cancel", MinWidth = 90 };
        cancel.Classes.Add("secondaryAction");
        var root = new StackPanel { Margin = new Thickness(18), Spacing = 12 };
        root.Children.Add(new TextBlock { Text = "Name the new profile", FontSize = 18, FontWeight = FontWeight.SemiBold });
        root.Children.Add(box);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8 };
        actions.Children.Add(cancel); actions.Children.Add(ok); root.Children.Add(actions);
        var dialog = new Window
        {
            Title = "Add Glide profile",
            CanResize = false,
            SizeToContent = SizeToContent.WidthAndHeight,
            MinWidth = 390,
            MaxWidth = 560,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = root
        };
        try
        {
            if (Screens.ScreenFromWindow(this) is { } screen)
                dialog.MaxHeight = Math.Max(200, screen.WorkingArea.Height / Math.Max(1.0, RenderScaling) - 80);
        }
        catch { }
        PopupPlacementStore.Track(dialog, "profile-add");
        ok.Click += (_, _) => dialog.Close(box.Text?.Trim());
        cancel.Click += (_, _) => dialog.Close((string?)null);
        dialog.Opened += (_, _) => { box.Focus(); box.SelectAll(); };
        return await dialog.ShowDialog<string?>(this);
    }

    private async void SaveProfileClicked(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: Guid id }) return;
        var profile = ProfileStore.ListProfiles().FirstOrDefault(p => p.Id == id);
        if (profile is null) return;
        var state = ReadControls();
        ProfileStore.Save(id, state);
        var target = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = $"Save Glide profile — {profile.Name}",
            SuggestedFileName = SanitizeProfileFileName(profile.Name) + ".glideprofile.json",
            SuggestedStartLocation = await ResolveProfilePickerFolderAsync(),
            DefaultExtension = "json",
            FileTypeChoices = new[] { new FilePickerFileType("Glide profile") { Patterns = new[] { "*.glideprofile.json", "*.json" } } }
        });
        var path = target?.TryGetLocalPath();
        if (string.IsNullOrWhiteSpace(path)) { DirtyText.Text = $"Updated {profile.Name} internally."; return; }
        _workingLastProfileDirectory = Path.GetDirectoryName(path) ?? _workingLastProfileDirectory;
        try
        {
            var doc = new ProfileTransferDocument { Name = profile.Name, Settings = state.CloneState() };
            await File.WriteAllTextAsync(path, JsonSerializer.Serialize(doc, AppJsonContext.Default.ProfileTransferDocument));
            DirtyText.Text = $"Saved {profile.Name} to {Path.GetFileName(path)}";
        }
        catch (Exception ex) { DirtyText.Text = $"Profile save failed: {ex.Message}"; }
    }

    private async void LoadProfileClicked(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: Guid id }) return;
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Load Glide profile",
            AllowMultiple = false,
            SuggestedStartLocation = await ResolveProfilePickerFolderAsync(),
            FileTypeFilter = new[] { new FilePickerFileType("Glide profile") { Patterns = new[] { "*.glideprofile.json", "*.json" } } }
        });
        var path = files.FirstOrDefault()?.TryGetLocalPath();
        if (string.IsNullOrWhiteSpace(path)) return;
        _workingLastProfileDirectory = Path.GetDirectoryName(path) ?? _workingLastProfileDirectory;
        try
        {
            var current = ReadControls();
            var json = await ReadImportFileAsync(path);
            var doc = JsonSerializer.Deserialize(json, AppJsonContext.Default.ProfileTransferDocument);
            GlideSettingsState? state = doc?.Settings;
            string? importedName = doc?.Name;
            // Compatibility: accept a plain GlideSettingsState JSON file as a profile file too.
            state ??= JsonSerializer.Deserialize(json, AppJsonContext.Default.GlideSettingsState);
            if (state is null) throw new InvalidDataException("Profile file did not contain Glide settings.");
            NormalizeImportedState(state);
            var launchTargetsKept = KeepLocalLaunchTargets(state, current);
            var networkFoldersDropped = KeepLocalFolderPaths(state, current);
            ProfileStore.Replace(id, importedName, state);
            LoadControls(state);
            _apply(state.CloneState(), false);
            UpdateDirtyState(state);
            BuildProfileRows();
            DirtyText.Text = $"Loaded profile from {Path.GetFileName(path)}" + ImportNotes(launchTargetsKept, networkFoldersDropped);
        }
        catch (Exception ex) { DirtyText.Text = $"Profile load failed: {ex.Message}"; }
    }

    /// <summary>Largest settings/profile file accepted for import (real exports are a few hundred KB).</summary>
    internal const long MaxImportFileBytes = 16L * 1024 * 1024;

    internal const string LaunchTargetsKeptNote = " (external programs were not imported; set them in Settings)";

    internal const string NetworkFoldersDroppedNote = " (network folder paths were not imported)";

    /// <summary>Status-line suffix telling the user what an import deliberately left out.</summary>
    internal static string ImportNotes(bool launchTargetsKept, bool networkFoldersDropped)
        => (launchTargetsKept ? LaunchTargetsKeptNote : string.Empty) + (networkFoldersDropped ? NetworkFoldersDroppedNote : string.Empty);

    /// <summary>
    /// Security (R9): a shared profile or settings file must not make Glide open network locations on its
    /// own. Startup, new-tab, Explorer-tab, last-used and default-picker folders that name a network path
    /// (\\host\share, \\?\UNC\..., //host, file: URIs) are not imported: this PC's value is kept when it is
    /// local, otherwise the field is cleared. Opening such a folder at startup or in a file dialog makes
    /// Windows contact that host and can send the user's network credentials to it. Returns true when
    /// anything was dropped (so the caller can say so).
    /// </summary>
    internal static bool KeepLocalFolderPaths(GlideSettingsState imported, GlideSettingsState current)
    {
        var dropped = false;
        string Keep(string? importedValue, string? currentValue)
        {
            if (!IsNetworkPath(importedValue)) return importedValue ?? string.Empty;
            dropped = true;
            return IsNetworkPath(currentValue) ? string.Empty : currentValue ?? string.Empty;
        }

        imported.StartupCustomPath = Keep(imported.StartupCustomPath, current.StartupCustomPath);
        imported.NewTabCustomPath = Keep(imported.NewTabCustomPath, current.NewTabCustomPath);
        imported.NewExplorerTabDefaultDirectory = Keep(imported.NewExplorerTabDefaultDirectory, current.NewExplorerTabDefaultDirectory);
        imported.LastNewExplorerTabDirectory = Keep(imported.LastNewExplorerTabDirectory, current.LastNewExplorerTabDirectory);
        imported.LastOpenDirectory = Keep(imported.LastOpenDirectory, current.LastOpenDirectory);
        imported.LastOpenFileDirectory = Keep(imported.LastOpenFileDirectory, current.LastOpenFileDirectory);
        imported.LastOverlayDirectory = Keep(imported.LastOverlayDirectory, current.LastOverlayDirectory);
        imported.LastProfileDirectory = Keep(imported.LastProfileDirectory, current.LastProfileDirectory);
        imported.OpenFileDefaultDirectory = Keep(imported.OpenFileDefaultDirectory, current.OpenFileDefaultDirectory);
        imported.OverlayDefaultDirectory = Keep(imported.OverlayDefaultDirectory, current.OverlayDefaultDirectory);
        imported.ProfileDefaultDirectory = Keep(imported.ProfileDefaultDirectory, current.ProfileDefaultDirectory);
        return dropped;
    }

    /// <summary>True for UNC/network and device paths (\\host\share, \\?\..., //host) and file: URIs.</summary>
    internal static bool IsNetworkPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        var value = path.Trim().Trim('"').TrimStart();
        if (value.Length >= 2 && (value[0] == '\\' || value[0] == '/') && (value[1] == '\\' || value[1] == '/')) return true;
        return value.StartsWith("file:", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Reads an import file, refusing anything too large to be a Glide settings/profile file.</summary>
    internal static async Task<string> ReadImportFileAsync(string path)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096, useAsync: true);
        if (stream.Length > MaxImportFileBytes)
            throw new InvalidDataException("The file is too large to be a Glide settings file.");
        using var reader = new StreamReader(stream);
        return await reader.ReadToEndAsync();
    }

    /// <summary>
    /// Security: profile and settings files are meant to be shared, so an imported file must never
    /// decide which programs Glide launches. "Open in external program 1-3" (Shift+1..3 by default)
    /// would otherwise run whatever executable a downloaded profile named, with the current image as its
    /// argument. The imported state keeps this PC's external programs. Returns true when the file named
    /// different programs (so the caller can tell the user).
    /// </summary>
    internal static bool KeepLocalLaunchTargets(GlideSettingsState imported, GlideSettingsState current)
    {
        var differs =
            !string.Equals(imported.ExternalProgram1 ?? string.Empty, current.ExternalProgram1 ?? string.Empty, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(imported.ExternalProgram2 ?? string.Empty, current.ExternalProgram2 ?? string.Empty, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(imported.ExternalProgram3 ?? string.Empty, current.ExternalProgram3 ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        imported.ExternalProgram1 = current.ExternalProgram1 ?? string.Empty;
        imported.ExternalProgram2 = current.ExternalProgram2 ?? string.Empty;
        imported.ExternalProgram3 = current.ExternalProgram3 ?? string.Empty;
        return differs;
    }

    private async Task<IStorageFolder?> ResolveProfilePickerFolderAsync()
    {
        var configured = ProfileDefaultDirectoryBox.Text?.Trim();
        var directory = !string.IsNullOrWhiteSpace(configured) ? configured : _workingLastProfileDirectory;
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory)) return null;
        try { return await StorageProvider.TryGetFolderFromPathAsync(Path.GetFullPath(directory)); } catch { return null; }
    }

    private static string SanitizeProfileFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var clean = new string(name.Select(ch => invalid.Contains(ch) ? '_' : ch).ToArray()).Trim();
        return string.IsNullOrWhiteSpace(clean) ? "Glide-profile" : clean;
    }
}
