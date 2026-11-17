using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Glide.App.Platform;
using Glide.Core.Data;
using Glide.Core.Reset;

namespace Glide.App.Settings;

/// <summary>
/// Settings &gt; Windows &amp; developer &gt; Reset Glide. The card only chooses what to wipe; the work is
/// <see cref="ResetCoordinator"/> (Glide.Core), the confirmation is <see cref="ResetConfirmWindow"/>.
/// The tick boxes are Action entries of the settings catalogue ("reset.*"): searchable, but never counted as changed
/// settings, never part of Apply / profiles, and not bindable to hotkeys (SettingActionCatalog.Excluded).
/// They are not written in XAML: <see cref="DataCategorySelector"/> builds them from the shared
/// <see cref="GlideDataCategoryCatalog"/> (every category that can be reset), exactly like the Back up and restore card.
/// </summary>
public partial class SettingsWindow
{
    private readonly Dictionary<ResetItemId, CheckBox> _resetChecks = new();
    private DataCategorySelector _resetSelector = null!;
    private Button _resetGoButton = null!;

    /// <summary>Tests: scratch folders, a fake registry and a fake process host instead of the real machine.</summary>
    internal Func<ResetServices>? ResetServicesOverride { get; set; }

    /// <summary>Tests: replaces App.ExitApplication (the real one ends the process).</summary>
    internal Action? ExitApplicationForReset { get; set; }

    /// <summary>Raised with the confirmation window just before it is shown (tests drive its buttons).</summary>
    internal event Action<ResetConfirmWindow>? ResetDialogOpened;

    /// <summary>Raised with the report window when a reset could not run or finished with problems.</summary>
    internal event Action<ResetResultWindow>? ResetResultOpened;

    /// <summary>
    /// Builds the Reset Glide tick boxes and bar into ResetCategoryHost (before the Settings-search map is built, which
    /// registers each tick box under its "reset.*" id).
    /// </summary>
    private void BuildResetSelector()
    {
        _resetSelector = new DataCategorySelector("Reset", GlideDataCapabilities.Reset, GlideDataCategoryCatalog.With(GlideDataCapabilities.Reset));
        _resetGoButton = new Button { Name = "ResetGoButton", Content = "Reset selected...", IsEnabled = false, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right };
        _resetGoButton.Classes.Add("dangerAction");
        _resetGoButton.Click += ResetGoClicked;
        _resetSelector.AddAction(_resetGoButton);
        ResetCategoryHost.Children.Add(_resetSelector);
        foreach (var (id, check) in _resetSelector.Checks) _resetChecks[id] = check;
    }

    private void InitializeResetCard()
    {
        _resetSelector.SelectionChanged += UpdateResetBar;
        UpdateResetBar();
    }

    /// <summary>The ticked items in catalogue order.</summary>
    internal IReadOnlyList<ResetItemId> SelectedResetItems() =>
        ResetCatalog.Items.Where(x => _resetChecks[x.Id].IsChecked == true).Select(x => x.Id).ToArray();

    internal void UpdateResetBar()
    {
        var count = SelectedResetItems().Count;
        _resetSelector.CountText.Text = DataCategorySelector.CountLabel(count);
        _resetGoButton.Content = count == 0 ? "Reset selected..." : $"Reset {count} selected...";
        _resetGoButton.IsEnabled = count > 0;
    }

    /// <summary>CardId of the Reset Glide card (Windows &amp; developer).</summary>
    internal const string ResetCardId = "windows-reset";

    /// <summary>Footer "Reset Glide" button: Restore defaults only resets options, everything else is on the Reset Glide card.</summary>
    private void OpenResetGlideClicked(object? sender, RoutedEventArgs e) => OpenResetGlideCard();

    /// <summary>
    /// Shows Windows &amp; developer with the Reset Glide card expanded and scrolled into view, and puts keyboard focus on its
    /// first tick box. A search in progress is cleared first (the category filter only narrows searches, so the page shows).
    /// </summary>
    internal void OpenResetGlideCard()
    {
        if (!_cardById.TryGetValue(ResetCardId, out var card) || !_pageOfCard.TryGetValue(card, out var page)) return;
        card.IsExpanded = true;
        ShowCategory(page, clearSearch: true, revealTarget: card);
        HighlightRevealedRow(card.Header);
        if (_resetSelector.FirstCheck is { } first) FocusRevealedEditor(first, card);
    }

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

        _resetGoButton.IsEnabled = false;
        _resetSelector.CountText.Text = "Resetting...";
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
