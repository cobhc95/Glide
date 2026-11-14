using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Glide.App.Platform;
using Glide.Core.Reset;

namespace Glide.App.Settings;

/// <summary>
/// Settings &gt; Windows &amp; developer &gt; Reset Glide. The card only chooses what to wipe; the work is
/// <see cref="ResetCoordinator"/> (Glide.Core), the confirmation is <see cref="ResetConfirmWindow"/>.
/// The tick boxes are Action entries of the settings catalogue ("reset.*"): searchable, but never counted as changed
/// settings, never part of Apply / profiles, and not bindable to hotkeys (SettingActionCatalog.Excluded).
/// </summary>
public partial class SettingsWindow
{
    private readonly Dictionary<ResetItemId, CheckBox> _resetChecks = new();

    /// <summary>Tests: scratch folders, a fake registry and a fake process host instead of the real machine.</summary>
    internal Func<ResetServices>? ResetServicesOverride { get; set; }

    /// <summary>Tests: replaces App.ExitApplication (the real one ends the process).</summary>
    internal Action? ExitApplicationForReset { get; set; }

    /// <summary>Raised with the confirmation window just before it is shown (tests drive its buttons).</summary>
    internal event Action<ResetConfirmWindow>? ResetDialogOpened;

    /// <summary>Raised with the report window when a reset could not run or finished with problems.</summary>
    internal event Action<ResetResultWindow>? ResetResultOpened;

    private void InitializeResetCard()
    {
        _resetChecks[ResetItemId.AllSettings] = ResetAllSettingsCheck;
        _resetChecks[ResetItemId.HotkeysAndGestures] = ResetHotkeysCheck;
        _resetChecks[ResetItemId.ProfilesAndPresets] = ResetProfilesCheck;
        _resetChecks[ResetItemId.WindowPlacements] = ResetWindowPlacementCheck;
        _resetChecks[ResetItemId.RecentHistory] = ResetRecentCheck;
        _resetChecks[ResetItemId.LastSession] = ResetSessionCheck;
        _resetChecks[ResetItemId.Caches] = ResetCachesCheck;
        _resetChecks[ResetItemId.CodecPack] = ResetCodecPackCheck;
        _resetChecks[ResetItemId.RegistryEntries] = ResetRegistryCheck;
        _resetChecks[ResetItemId.StartupEntries] = ResetStartupCheck;

        foreach (var info in ResetCatalog.Items)
        {
            var check = _resetChecks[info.Id];
            var label = new StackPanel();
            var title = new TextBlock { Text = info.Title };
            title.Classes.Add("resetItemTitle");
            var hint = new TextBlock { Text = info.Description, Margin = new Avalonia.Thickness(0, 1, 0, 0) };
            hint.Classes.Add("resetItemHint");
            label.Children.Add(title);
            label.Children.Add(hint);
            check.Content = label;
            // The label is a panel (title + hint), so give assistive technology the plain title.
            AutomationProperties.SetName(check, info.Title);
            check.IsCheckedChanged += (_, _) => UpdateResetBar();
        }
        UpdateResetBar();
    }

    /// <summary>The ticked items in catalogue order.</summary>
    internal IReadOnlyList<ResetItemId> SelectedResetItems() =>
        ResetCatalog.Items.Where(x => _resetChecks[x.Id].IsChecked == true).Select(x => x.Id).ToArray();

    internal void UpdateResetBar()
    {
        var count = SelectedResetItems().Count;
        ResetCountText.Text = count == 0 ? "Nothing selected" : $"{count} selected";
        ResetGoButton.Content = count == 0 ? "Reset selected..." : $"Reset {count} selected...";
        ResetGoButton.IsEnabled = count > 0;
    }

    private void SetAllResetChecks(bool value)
    {
        foreach (var check in _resetChecks.Values) check.IsChecked = value;
        UpdateResetBar();
    }

    private void ResetSelectAllClicked(object? sender, RoutedEventArgs e) => SetAllResetChecks(true);
    private void ResetClearClicked(object? sender, RoutedEventArgs e) => SetAllResetChecks(false);
    private async void ResetGoClicked(object? sender, RoutedEventArgs e)
    {
        try { await RunResetFlowAsync(); }
        catch (Exception ex)
        {
            // async void: nothing above would catch this, and a half-run reset must never look like success.
            UpdateResetBar();
            SetResetStatus("Reset Glide could not run: " + ex.Message);
        }
    }

    /// <summary>Confirm, back up, wipe. Returns after the confirmation was cancelled, the wipe finished, or Glide was told to exit.</summary>
    internal async Task RunResetFlowAsync()
    {
        var items = SelectedResetItems();
        if (items.Count == 0) return;
        var services = ResetServicesOverride?.Invoke() ?? ResetServicesFactory.CreateDefault();
        var plan = ResetPlanner.Plan(services.Environment, items);

        var dialog = new ResetConfirmWindow(plan);
        ResetDialogOpened?.Invoke(dialog);
        var confirmation = await dialog.ShowDialog<ResetConfirmation?>(this);
        if (confirmation is null) return; // Cancel, Esc or the close button: nothing happens

        ResetGoButton.IsEnabled = false;
        ResetCountText.Text = "Resetting...";
        var result = await Task.Run(() => ResetCoordinator.Run(new ResetRequest(items, confirmation.SaveBackup), services));
        await ShowResetResultAsync(result);
    }

    private async Task ShowResetResultAsync(ResetResult result)
    {
        switch (result.Outcome)
        {
            case ResetOutcome.BackupFailed:
            case ResetOutcome.HelperFailed:
                UpdateResetBar();
                SetResetStatus(result.Error ?? "Nothing was reset.");
                await ShowResetReportAsync("Nothing was reset", result.Error ?? "Glide could not start the reset.", string.Empty);
                return;

            case ResetOutcome.RestartScheduled:
                // Close Settings like Cancel does (the dialog was confirmed, so unsaved Settings edits go with the data),
                // then end Glide. The helper process waits for the exit, wipes, and starts Glide again.
                _closeWithoutRollback = true;
                Close();
                RollbackPreviewDeferred();
                var exit = ExitApplicationForReset ?? App.ExitApplication;
                Dispatcher.UIThread.Post(exit, DispatcherPriority.Background);
                return;

            default:
                var report = result.Report!;
                // Items that went through are done; items that failed stay ticked so the user can try again.
                foreach (var item in report.Items.Where(x => x.Succeeded)) _resetChecks[item.Item.Id].IsChecked = false;
                UpdateResetBar();
                var backup = result.BackupFolder is null ? string.Empty : $" A backup was saved to {result.BackupFolder}.";
                if (report.AllSucceeded)
                {
                    SetResetStatus("Done. " + report.Describe().Replace("\r\n", "; ").Replace("\n", "; ") + "." + backup);
                    if (result.Plan.Includes(ResetItemId.RegistryEntries))
                        WindowsIntegrationStatusText.Text = "Glide's per-user Open With, file-type and thumbnail registry entries were removed (Reset Glide). Use \"Add Glide to Open With\" to register again.";
                }
                else
                {
                    SetResetStatus("Some items could not be reset - see the report." + backup);
                    await ShowResetReportAsync("Some items could not be reset", "These items are unchanged or only partly reset:" + backup, report.Describe());
                }
                return;
        }
    }

    private void SetResetStatus(string text)
    {
        ResetResultText.Text = text;
        ResetResultText.IsVisible = !string.IsNullOrWhiteSpace(text);
    }

    private async Task ShowResetReportAsync(string title, string summary, string details)
    {
        var window = new ResetResultWindow(title, summary, details);
        ResetResultOpened?.Invoke(window);
        await window.ShowDialog(this);
    }
}
