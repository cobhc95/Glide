using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Glide.App.Services;
using Glide.Core.Data;
using Glide.Core.Reset;
using Glide.Core.Windows;

namespace Glide.App.Settings;

/// <summary>
/// Settings &gt; Windows &amp; developer &gt; Back up and restore (directly above Reset Glide). The tick boxes are the same
/// data-driven list as Reset Glide's (<see cref="DataCategorySelector"/> over <see cref="GlideDataCategoryCatalog"/>, the
/// categories that can be backed up); "Export..." writes one .glidebackup file, "Import..." reads one, lists the categories
/// it holds (<see cref="BackupImportWindow"/>) and applies the chosen ones (<see cref="GlideBackupService"/>).
/// Like the Reset Glide tick boxes these are Action entries of the settings catalogue ("backup.*"): searchable, never
/// counted as changed settings, not bindable to hotkeys.
/// </summary>
public partial class SettingsWindow
{
    /// <summary>CardId of the Back up and restore card (Windows &amp; developer).</summary>
    internal const string BackupCardId = "windows-backup";

    private DataCategorySelector _backupSelector = null!;
    private Button _backupExportButton = null!;
    private Button _backupImportButton = null!;
    private bool _backupBusy;

    /// <summary>
    /// After a backup import staged "All settings" / "Hotkeys and mouse gestures": the staged state including the values
    /// that have no control in Settings. <see cref="ReadControls"/> starts from it instead of the committed state until the
    /// next commit, so OK / Apply saves the whole import and Cancel discards it.
    /// </summary>
    private GlideSettingsState? _backupStagedBase;

    /// <summary>Tests: the file Export writes to (instead of the save picker). Null result = cancelled.</summary>
    internal Func<Task<string?>>? BackupSavePathOverride { get; set; }

    /// <summary>Tests: the file Import reads (instead of the open picker). Null result = cancelled.</summary>
    internal Func<Task<string?>>? BackupOpenPathOverride { get; set; }

    /// <summary>Tests: where the "before import" safety backup goes (default Documents\Glide backups).</summary>
    internal string? BackupFolderOverride { get; set; }

    /// <summary>Tests: the running main window's values an import changes (default: the owner window).</summary>
    internal IGlideBackupLiveTarget? BackupLiveTargetOverride { get; set; }

    /// <summary>Tests: the monitors imported window positions are checked against.</summary>
    internal Func<(IReadOnlyList<ScreenWorkArea> Screens, ScreenWorkArea Primary)>? BackupScreensOverride { get; set; }

    /// <summary>Raised with the import dialog just before it is shown (tests drive its buttons).</summary>
    internal event Action<BackupImportWindow>? BackupImportDialogOpened;

    /// <summary>Builds the Back up and restore tick boxes and bar into BackupCategoryHost (before the Settings-search map).</summary>
    private void BuildBackupSelector()
    {
        _backupSelector = new DataCategorySelector("Backup", GlideDataCapabilities.Backup, GlideDataCategoryCatalog.With(GlideDataCapabilities.Backup));
        _backupImportButton = new Button { Name = "BackupImportButton", Content = "Import..." };
        _backupImportButton.Classes.Add("secondaryAction");
        _backupImportButton.Classes.Add("resetBar");
        _backupImportButton.Click += BackupImportClicked;
        AutomationProperties.SetName(_backupImportButton, "Import Glide backup");
        ToolTip.SetTip(_backupImportButton, "Choose a .glidebackup file, then pick which of the things it holds to import.");
        _backupExportButton = new Button { Name = "BackupExportButton", Content = "Export..." };
        _backupExportButton.Classes.Add("primaryAction");
        _backupExportButton.Classes.Add("resetBar");
        _backupExportButton.Click += BackupExportClicked;
        AutomationProperties.SetName(_backupExportButton, "Export Glide backup");
        ToolTip.SetTip(_backupExportButton, "Save the ticked items to one .glidebackup file.");
        _backupSelector.AddAction(_backupImportButton);
        _backupSelector.AddAction(_backupExportButton);
        BackupCategoryHost.Children.Add(_backupSelector);
    }

    private void InitializeBackupCard()
    {
        // A backup normally wants everything, so every category starts ticked (Reset Glide starts with nothing ticked).
        _backupSelector.SetAll(true);
        _backupSelector.SelectionChanged += UpdateBackupBar;
        UpdateBackupBar();
    }

    /// <summary>Registers both cards' tick boxes and the backup buttons in the Settings-search map (see BuildSettingControlMap).</summary>
    private void RegisterDataCategoryControls()
    {
        foreach (var selector in new[] { _resetSelector, _backupSelector })
            foreach (var category in selector.Categories)
                if (category.SettingIdFor(selector.Capability) is { } id) _settingControls[id] = selector.Checks[category.Id];
        _settingControls[GlideDataCategoryCatalog.BackupExportSettingId] = _backupExportButton;
        _settingControls[GlideDataCategoryCatalog.BackupImportSettingId] = _backupImportButton;
    }

    /// <summary>The ticked backup categories in catalogue order.</summary>
    internal IReadOnlyList<ResetItemId> SelectedBackupItems() => _backupSelector.Selected;

    internal void UpdateBackupBar()
    {
        var count = SelectedBackupItems().Count;
        _backupSelector.CountText.Text = DataCategorySelector.CountLabel(count);
        _backupExportButton.IsEnabled = count > 0 && !_backupBusy;
        _backupImportButton.IsEnabled = !_backupBusy;
    }

    private void SetBackupStatus(string text)
    {
        BackupResultText.Text = text;
        BackupResultText.IsVisible = !string.IsNullOrWhiteSpace(text);
    }

    /// <summary>
    /// Shows Windows &amp; developer with the Back up and restore card expanded and in view, focus on its first tick box
    /// (the same reveal as the Reset Glide card).
    /// </summary>
    internal void OpenBackupCard()
    {
        if (!_cardById.TryGetValue(BackupCardId, out var card) || !_pageOfCard.TryGetValue(card, out var page)) return;
        card.IsExpanded = true;
        ShowCategory(page, clearSearch: true, revealTarget: card);
        HighlightRevealedRow(card.Header);
        if (_backupSelector.FirstCheck is { } first) FocusRevealedEditor(first, card);
    }

    /// <summary>The "Export / Import Glide backup" commands (hotkeys): reveal the card, then run the flow.</summary>
    internal void StartBackupFromCommand(bool import)
    {
        OpenBackupCard();
        Dispatcher.UIThread.Post(() =>
        {
            if (import) BackupImportClicked(this, new RoutedEventArgs());
            else BackupExportClicked(this, new RoutedEventArgs());
        }, DispatcherPriority.Background);
    }

    private async void BackupExportClicked(object? sender, RoutedEventArgs e)
    {
        try { await RunBackupExportAsync(); }
        catch (Exception ex)
        {
            // async void: nothing above would catch this.
            _backupBusy = false;
            UpdateBackupBar();
            SetBackupStatus("Backup failed: " + ex.Message);
        }
    }

    private async void BackupImportClicked(object? sender, RoutedEventArgs e)
    {
        try { await RunBackupImportAsync(); }
        catch (Exception ex)
        {
            _backupBusy = false;
            UpdateBackupBar();
            SetBackupStatus("Import failed: " + ex.Message + " Nothing that was not already reported was changed.");
        }
    }

    /// <summary>Asks where to save, then writes the ticked categories to one .glidebackup file.</summary>
    internal async Task RunBackupExportAsync()
    {
        if (_backupBusy) return;
        var items = SelectedBackupItems();
        if (items.Count == 0)
        {
            SetBackupStatus("Tick at least one item to back up.");
            return;
        }
        var path = BackupSavePathOverride is { } pick ? await pick() : await PickBackupSavePathAsync();
        if (string.IsNullOrWhiteSpace(path)) return;
        if (string.IsNullOrEmpty(Path.GetExtension(path))) path += GlideBackupFormat.FileExtension;

        _backupBusy = true;
        UpdateBackupBar();
        _backupSelector.CountText.Text = "Exporting...";
        try
        {
            await GlideBackupService.ExportAsync(path, ReadControls(), items, DateTime.UtcNow);
            SetBackupStatus($"Backed up {Describe(items)} to {path}.");
        }
        catch (Exception ex) when (ex is GlideBackupException or IOException or UnauthorizedAccessException)
        {
            SetBackupStatus("Backup failed: " + ex.Message);
        }
        finally
        {
            _backupBusy = false;
            UpdateBackupBar();
        }
    }

    /// <summary>
    /// Asks for a .glidebackup file, validates it completely (nothing changes when it cannot be imported), lets the user
    /// choose among the categories it holds, then stages or applies them.
    /// </summary>
    internal async Task RunBackupImportAsync()
    {
        if (_backupBusy) return;
        var path = BackupOpenPathOverride is { } pick ? await pick() : await PickBackupOpenPathAsync();
        if (string.IsNullOrWhiteSpace(path)) return;

        _backupBusy = true;
        UpdateBackupBar();
        try
        {
            var current = ReadControls();
            GlideBackupService.ImportPlan plan;
            try { plan = await Task.Run(() => GlideBackupService.ReadImport(path, current)); }
            catch (GlideBackupException ex)
            {
                SetBackupStatus("Import failed: " + ex.Message + " Nothing was changed.");
                return;
            }

            var dialog = new BackupImportWindow(plan);
            BackupImportDialogOpened?.Invoke(dialog);
            var choice = await dialog.ShowDialog<BackupImportChoice?>(this);
            if (choice is null || choice.Categories.Count == 0)
            {
                SetBackupStatus("Import cancelled. Nothing was changed.");
                return;
            }

            // In-place categories: optionally keep a copy of what they replace, and stop if that copy cannot be made.
            var inPlace = choice.Categories.Where(x => !GlideBackupService.IsStaged(x)).ToArray();
            string? safety = null;
            if (inPlace.Length > 0 && choice.SaveBackupFirst)
            {
                var folder = BackupFolderOverride ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Glide backups");
                try { safety = await GlideBackupService.SaveSafetyBackupAsync(folder, ReadControls(), inPlace, DateTime.Now); }
                catch (Exception ex) when (ex is GlideBackupException or IOException or UnauthorizedAccessException)
                {
                    SetBackupStatus("Import stopped: the backup of your current data could not be saved (" + ex.Message + "). Nothing was changed.");
                    return;
                }
            }

            // Staged categories: into the Settings editors, previewed, saved by OK / Apply, discarded by Cancel.
            var staged = choice.Categories.Where(GlideBackupService.IsStaged).ToArray();
            if (staged.Length > 0)
            {
                var next = GlideBackupService.StageSettings(ReadControls(), plan, staged);
                _restoreDefaultsStaged = false;
                _backupStagedBase = next.CloneState();
                LoadControls(next);
                var preview = ReadControls();
                _apply(preview.CloneState(), false);
                UpdateDirtyState(preview);
            }

            // In-place categories: written now, each on its own.
            var screens = BackupScreensOverride?.Invoke() ?? WindowPlacementService.ScreensOf(this);
            var live = BackupLiveTargetOverride ?? Owner as IGlideBackupLiveTarget;
            var (applied, failed, notes) = await GlideBackupService.ApplyStoresAsync(plan, choice.Categories, live, screens.Screens, screens.Primary);
            if (applied.Contains(ResetItemId.LastSession))
            {
                // The remembered folders are part of the settings Settings commits: keep the imported ones in its base state.
                var names = GlideDataCategoryCatalog.SettingsPropertiesFor(ResetItemId.LastSession, ResetCatalog.SessionLocationProperties);
                _committed = CopySessionLocations(_committed, plan.Settings, names);
                if (_backupStagedBase is not null) _backupStagedBase = CopySessionLocations(_backupStagedBase, plan.Settings, names);
            }
            if (applied.Contains(ResetItemId.ProfilesAndPresets)) BuildProfileRows();

            var parts = new List<string>();
            if (staged.Length > 0) parts.Add($"Staged {Describe(staged)}: OK or Apply saves, Cancel discards.");
            if (applied.Count > 0) parts.Add($"Imported {Describe(applied)}.");
            parts.AddRange(notes);
            if (failed.Count > 0) parts.Add("Not imported: " + string.Join("; ", failed.Select(f => GlideDataCategoryCatalog.Get(f.Item1).Title + " (" + f.Item2 + ")")) + ".");
            var leftOut = GlideBackupService.Notes(plan);
            if (leftOut.Length > 0) parts.Add("Left out on purpose: " + leftOut + ".");
            if (safety is not null) parts.Add("Your previous data was saved to " + safety + ".");
            SetBackupStatus(string.Join(" ", parts));
            if (staged.Length > 0) DirtyText.Text = "Imported backup staged" + SettingsWindow.ImportNotes(plan.LaunchTargetsKept, plan.NetworkFoldersDropped);
        }
        finally
        {
            _backupBusy = false;
            UpdateBackupBar();
        }
    }

    private static GlideSettingsState CopySessionLocations(GlideSettingsState target, GlideSettingsState source, IReadOnlyList<string> names)
    {
        var copy = target.CloneState();
        var root = GlideBackupService.ToNode(copy);
        var from = GlideBackupService.ToNode(source);
        foreach (var name in names)
            if (from.ContainsKey(name)) root[name] = from[name]?.DeepClone();
        var result = GlideBackupService.FromNode(root);
        result.SettingsSchemaVersion = Math.Max(target.SettingsSchemaVersion, result.SettingsSchemaVersion);
        return result;
    }

    private static string Describe(IReadOnlyCollection<ResetItemId> items) =>
        string.Join(", ", items.Select(x => GlideDataCategoryCatalog.Get(x).Title));

    private async Task<string?> PickBackupSavePathAsync()
    {
        var target = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Export Glide backup",
            SuggestedFileName = GlideBackupService.SuggestedFileName(DateTime.Now),
            SuggestedStartLocation = await DocumentsFolderAsync(),
            DefaultExtension = "glidebackup",
            ShowOverwritePrompt = true,
            FileTypeChoices = new[] { new FilePickerFileType("Glide backup") { Patterns = new[] { "*" + GlideBackupFormat.FileExtension } } }
        });
        return target?.TryGetLocalPath();
    }

    private async Task<string?> PickBackupOpenPathAsync()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Import Glide backup",
            AllowMultiple = false,
            SuggestedStartLocation = await DocumentsFolderAsync(),
            FileTypeFilter = new[] { new FilePickerFileType("Glide backup") { Patterns = new[] { "*" + GlideBackupFormat.FileExtension } }, FilePickerFileTypes.All }
        });
        return files.FirstOrDefault()?.TryGetLocalPath();
    }

    private async Task<IStorageFolder?> DocumentsFolderAsync()
    {
        try
        {
            var documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            return string.IsNullOrWhiteSpace(documents) || !Directory.Exists(documents) ? null : await StorageProvider.TryGetFolderFromPathAsync(documents);
        }
        catch { return null; }
    }
}
