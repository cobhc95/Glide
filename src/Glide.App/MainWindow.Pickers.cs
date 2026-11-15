using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Glide.App.Settings;
using Glide.Core;
using Glide.Core.Navigation;
using Glide.Core.Workspace;

namespace Glide.App;

/// <summary>Open/pick/drop dialogs and Explorer navigation entry points. Split from MainWindow (4.5.2).</summary>
public partial class MainWindow
{
    private async void OpenClicked(object? sender, RoutedEventArgs e)
    {
        var options = new FilePickerOpenOptions
        {
            Title = "Open image",
            AllowMultiple = false,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("Images")
                {
                    Patterns = CodecCapabilityRegistry.RoutedExtensions.Select(x => "*" + x).ToArray()
                }
            }
        };
        options.SuggestedStartLocation = await ResolvePickerFolderAsync(_settings.OpenFileDefaultDirectory, _settings.LastOpenFileDirectory);
        var files = await StorageProvider.OpenFilePickerAsync(options);
        var path = files.FirstOrDefault()?.TryGetLocalPath();
        if (!string.IsNullOrWhiteSpace(path))
        {
            _settings.LastOpenFileDirectory = Path.GetDirectoryName(path) ?? _settings.LastOpenFileDirectory;
            SaveSettingsAndPublish();
            await OpenAsImageTabAsync(path);
        }
    }

    private void TryScheduleAutomaticOverlayLayoutRestore()
    {
        if (_automaticOverlayLayoutRestoreScheduled || !_deferredStartupSettingsAdopted ||
            !_settings.OverlayPersistLayoutBetweenSessions || _windowLifetimeCts.IsCancellationRequested) return;
        _automaticOverlayLayoutRestoreScheduled = true;
        var layoutPath = AutomaticOverlayLayoutPath();
        Dispatcher.UIThread.Post(async () =>
        {
            if (_windowLifetimeCts.IsCancellationRequested || !IsEffectivelyVisible) return;
            try { await EnsureOverlays().LoadLayoutAsync(layoutPath, _windowLifetimeCts.Token); }
            catch (OperationCanceledException) { /* cancellation is the expected way to stop this work */ }
        }, DispatcherPriority.Loaded);
    }

    private static string AutomaticOverlayLayoutPath() =>
        Path.Combine(SettingsStore.GetSettingsDirectory(), "overlay-layout.json");

    private async void OverlayAddClicked(object? sender, RoutedEventArgs e)
    {
        var imagePatterns = CodecCapabilityRegistry.RoutedExtensions.Select(x => "*" + x).ToArray();
        var videoPatterns = VideoFormatRegistry.Extensions.Select(x => "*" + x).ToArray();
        var options = new FilePickerOpenOptions { Title = "Add Window-in-Window image or video", AllowMultiple = true,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("Images and videos") { Patterns = imagePatterns.Concat(videoPatterns).ToArray() },
                new FilePickerFileType("Supported image formats") { Patterns = imagePatterns },
                new FilePickerFileType("Videos") { Patterns = videoPatterns }
            } };
        options.SuggestedStartLocation = await ResolvePickerFolderAsync(_settings.OverlayDefaultDirectory, _settings.OverlayRememberFolder ? _settings.LastOverlayDirectory : null);
        var files = await StorageProvider.OpenFilePickerAsync(options);
        foreach (var file in files)
        {
            var path = file.TryGetLocalPath();
            if (!string.IsNullOrWhiteSpace(path)) { if (_settings.OverlayRememberFolder) { _settings.LastOverlayDirectory = Path.GetDirectoryName(path) ?? _settings.LastOverlayDirectory; SaveSettingsAndPublish(); } await EnsureOverlays().AddAsync(path); }
        }
    }

    private void OverlayClearClicked(object? sender, RoutedEventArgs e) => _overlays?.Clear();

    private async void OverlaySaveClicked(object? sender, RoutedEventArgs e)
    {
        if (_overlays?.HasOverlays != true) return;
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Save overlay layout",
            SuggestedFileName = "Glide Overlay Layout.glideoverlay",
            SuggestedStartLocation = await ResolvePickerFolderAsync(_settings.OverlayDefaultDirectory, _settings.OverlayRememberFolder ? _settings.LastOverlayDirectory : null),
            DefaultExtension = "glideoverlay",
            ShowOverwritePrompt = true,
            FileTypeChoices = new[] { new FilePickerFileType("Glide overlay layouts") { Patterns = new[] { "*.glideoverlay" } } }
        });
        var path = file?.TryGetLocalPath();
        if (string.IsNullOrWhiteSpace(path)) return;
        if (_settings.OverlayRememberFolder) { _settings.LastOverlayDirectory = Path.GetDirectoryName(path) ?? _settings.LastOverlayDirectory; SaveSettingsAndPublish(); }
        EnsureOverlays().SaveLayout(path);
        _diagnostics.Write("overlay", "layout_saved", new { path });
    }

    private async void OverlayLoadClicked(object? sender, RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Load overlay layout",
            AllowMultiple = false,
            SuggestedStartLocation = await ResolvePickerFolderAsync(_settings.OverlayDefaultDirectory, _settings.OverlayRememberFolder ? _settings.LastOverlayDirectory : null),
            FileTypeFilter = new[] { new FilePickerFileType("Glide overlay layouts") { Patterns = new[] { "*.glideoverlay" } } }
        });
        var path = files.FirstOrDefault()?.TryGetLocalPath();
        if (string.IsNullOrWhiteSpace(path)) return;
        if (_settings.OverlayRememberFolder) { _settings.LastOverlayDirectory = Path.GetDirectoryName(path) ?? _settings.LastOverlayDirectory; SaveSettingsAndPublish(); }
        await EnsureOverlays().LoadLayoutAsync(path);
        _diagnostics.Write("overlay", "layout_loaded", new { path });
    }

    private void UpdateOverlayStatusButtons()
    {
        if (OverlaySaveButton is null || OverlayClearButton is null) return;
        OverlaySaveButton.IsVisible = _overlays?.HasOverlays == true;
        OverlayClearButton.IsVisible = _overlays?.HasOverlays == true;
    }

    private async void OverlayKeyboardShortcut(object? sender, KeyEventArgs e)
    {
        if (!_settings.OverlayKeyboardZoom || !ImageView.IsVisible || _overlays?.HasSelection != true) return;
        if (e.Key == Key.Add || (e.Key == Key.OemPlus && (e.KeyModifiers & KeyModifiers.Control) != 0)) { EnsureOverlays().ZoomSelected(1 + Math.Clamp(_settings.OverlayZoomStepPercent, 1, 100) / 100.0); e.Handled = true; }
        else if (e.Key == Key.Subtract || (e.Key == Key.OemMinus && (e.KeyModifiers & KeyModifiers.Control) != 0)) { var step = 1 + Math.Clamp(_settings.OverlayZoomStepPercent, 1, 100) / 100.0; EnsureOverlays().ZoomSelected(1.0 / step); e.Handled = true; }
        await Task.CompletedTask;
    }

    private async void OpenFolderClicked(object? sender, RoutedEventArgs e)
    {
        var options = new FolderPickerOpenOptions
        {
            Title = "Open folder",
            AllowMultiple = false,
            SuggestedStartLocation = await ResolveRememberedPickerFolderAsync()
        };
        var folders = await StorageProvider.OpenFolderPickerAsync(options);
        var folder = folders.FirstOrDefault()?.TryGetLocalPath();
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder)) return;
        RememberPickerDirectory(folder);
        if (_settings.RecentHistoryEnabled) RecentHistoryStore.RecordFolder(folder, _settings.HistorySize);
        _workspace.AddBrowser(folder);
        RebuildTabStrip();
        await ActivateWorkspaceAsync();
    }

    private async Task<IStorageFolder?> ResolvePickerFolderAsync(string? configuredDirectory, string? lastDirectory)
    {
        var directory = !string.IsNullOrWhiteSpace(configuredDirectory) ? configuredDirectory : lastDirectory;
        if (string.IsNullOrWhiteSpace(directory)) return null;
        try
        {
            if (!Directory.Exists(directory)) return null;
            var full = Path.GetFullPath(directory);
            if (full.StartsWith(@"\\", StringComparison.Ordinal)) return null;
            return await StorageProvider.TryGetFolderFromPathAsync(full);
        }
        catch { return null; }
    }

    private async Task<IStorageFolder?> ResolveRememberedPickerFolderAsync()
    {
        if (!_settings.RememberLastOpenLocation || string.IsNullOrWhiteSpace(_settings.LastOpenDirectory)) return null;
        try
        {
            if (!Directory.Exists(_settings.LastOpenDirectory)) return null;
            // Avoid forcing a network location through the picker startup path: unavailable/slow
            // remembered locations must never make opening a local image feel hung.
            if (Path.GetFullPath(_settings.LastOpenDirectory).StartsWith(@"\\", StringComparison.Ordinal)) return null;
            return await StorageProvider.TryGetFolderFromPathAsync(_settings.LastOpenDirectory);
        }
        catch { return null; }
    }

    private void RememberPickerDirectory(string? directory)
    {
        if (!_settings.RememberLastOpenLocation || string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory)) return;
        _settings.LastOpenDirectory = Path.GetFullPath(directory);
        SaveSettingsAndPublish();
    }

    private void DragOverReceived(object? sender, DragEventArgs e)
    {
        // Drags that start inside the Explorer tab are the Explorer's own (move/copy between folders).
        if (ExplorerDragPaths(e) is not null) return;
        // Without an explicit effect Windows shows the "not allowed" cursor and never delivers Drop.
        e.DragEffects = e.DataTransfer.TryGetFiles() is { Length: > 0 } ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private async void DropReceived(object? sender, DragEventArgs e)
    {
        if (ExplorerDragPaths(e) is not null) return;
        // The main viewer shows images only; dropped videos open as Window-in-Window overlays.
        var droppedVideos = e.DataTransfer.TryGetFiles()?.Select(x => x.TryGetLocalPath())
            .Where(x => !string.IsNullOrWhiteSpace(x) && File.Exists(x) && VideoFormatRegistry.IsVideo(x)).ToArray();
        if (droppedVideos is { Length: > 0 })
        {
            foreach (var video in droppedVideos) await EnsureOverlays().AddAsync(video!);
            return;
        }
        var item = e.DataTransfer.TryGetFiles()?.FirstOrDefault();
        var path = item?.TryGetLocalPath();
        if (string.IsNullOrWhiteSpace(path)) return;
        if (Directory.Exists(path))
        {
            if (_settings.RecentHistoryEnabled) RecentHistoryStore.RecordFolder(path, _settings.HistorySize);
            RememberPickerDirectory(path);
            _workspace.AddBrowser(path);
            RebuildTabStrip();
            await ActivateWorkspaceAsync();
            return;
        }
        await OpenAsImageTabAsync(path);
    }

    private void NavigateBrowserTo(string folder, bool addHistory, string? highlightPath = null)
    {
        if (_workspace.Active is not BrowserTabState browser || !Directory.Exists(folder)) return;
        folder = Path.GetFullPath(folder);
        _settings.LastNewExplorerTabDirectory = folder;
        SaveSettingsAndPublish();
        var id = browser.Id;
        // One history per tab (MainWindow.History.cs): folders are entries next to an image opened from this tab and Home.
        var history = HistoryFor(browser);
        var highlight = NormalizeBrowserHighlight(folder, highlightPath);

        if (addHistory)
        {
            // Preserve the item the user was on (and the scroll) before leaving this folder. Going Back therefore
            // restores the correct child/file instead of carrying a stale selection from another directory.
            CaptureExplorerHistoryEntry(history, browser);
            // Like a browser: the Forward entries are dropped; the same folder again only updates its highlight.
            history.Push(NavigationEntry.Explorer(folder, highlight));
        }
        else if (history.Current is { Kind: NavigationEntryKind.Explorer } current && NavigationEntry.PathsEqual(current.Path, folder))
        {
            if (highlight is not null) history.UpdateCurrent(entry => entry with { SelectedPath = highlight });
        }
        else
        {
            // Shown without an explicit history step (first show of the tab, a refresh after the tab turned into an
            // Explorer view): recorded like any other location.
            history.Record(NavigationEntry.Explorer(folder, highlight));
        }

        _workspace.ReplaceActiveBrowserFolder(folder);
        if (_workspace.Active is BrowserTabState activeBrowser)
            _workspace.ReplaceTab(activeBrowser with { Navigation = BrowserNavigationFor(id) });

        RefreshManagedBrowser(folder);
        UpdateExplorerNavigationState(folder);
        Title = AppVersion.ExplorerTitle(folder);
        RebuildTabStrip();
        SelectBrowserHighlight(id);
        UpdateTabNavigationButtons();
        NoteExplorerSessionChanged();
        _diagnostics.Write("browser", "navigate", new
        {
            folder, addHistory, historyIndex = history.Index, historyCount = history.Count,
            highlight = CurrentBrowserHistoryHighlight(id)
        });
    }

    private static string? NormalizeBrowserHighlight(string folder, string? target)
    {
        if (string.IsNullOrWhiteSpace(target)) return null;
        try
        {
            var full = Path.GetFullPath(target);
            var parent = Directory.Exists(full) ? Directory.GetParent(full)?.FullName : Path.GetDirectoryName(full);
            return string.Equals(parent, folder, StringComparison.OrdinalIgnoreCase) ? full : null;
        }
        catch { return null; }
    }

    /// <summary>The item the active Explorer folder should select from the tab history (null: none remembered).</summary>
    private string? CurrentBrowserHistoryHighlight(Guid tabId)
    {
        if (!_tabHistories.TryGetValue(tabId, out var history) || history.Current is not { Kind: NavigationEntryKind.Explorer } current) return null;
        if (_workspace.Tabs.FirstOrDefault(t => t.Id == tabId) is BrowserTabState browser && !NavigationEntry.PathsEqual(current.Path, browser.Folder)) return null;
        return current.SelectedPath;
    }

    private void NativeExplorerFolderNavigated(string folder)
    {
        // IExplorerBrowser has its own in-surface navigation (double-click folder, breadcrumbs, etc.).
        // Keep Glide's typed BrowserTabState/history/address synchronized without navigating the host
        // again, otherwise a Shell navigation event would recursively issue another BrowseToIDList.
        if (!OperatingSystem.IsWindows() || _workspace.Active is not BrowserTabState browser || !Directory.Exists(folder)) return;
        folder = Path.GetFullPath(folder);
        _settings.LastNewExplorerTabDirectory = folder;
        SaveSettingsAndPublish();
        if (string.Equals(browser.Folder, folder, StringComparison.OrdinalIgnoreCase)) return;

        var history = HistoryFor(browser);
        CaptureExplorerHistoryEntry(history, browser);
        history.Push(NavigationEntry.Explorer(folder));
        _workspace.ReplaceActiveBrowserFolder(folder);
        if (_workspace.Active is BrowserTabState activeBrowser)
            _workspace.ReplaceTab(activeBrowser with { Navigation = BrowserNavigationFor(browser.Id) });

        UpdateExplorerNavigationState(folder);
        Title = AppVersion.ExplorerTitle(folder);
        RebuildTabStrip();
        SelectBrowserHighlight(browser.Id);
        UpdateTabNavigationButtons();
        _diagnostics.Write("browser", "native_navigation", new { folder, historyIndex = history.Index, historyCount = history.Count });
    }

    private async void NativeExplorerFileActivated(string path)
    {
        if (!File.Exists(path) || !ImageNavigator.IsSupported(path)) return;
        await OpenAsImageTabAsync(path);
    }
}
