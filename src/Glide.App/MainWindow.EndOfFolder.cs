using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Glide.App.Controls;
using Glide.App.Services;
using Glide.Core;
using Glide.Core.Commands;
using Glide.Core.Navigation;
using Glide.Core.Workspace;

namespace Glide.App;

/// <summary>
/// 5.0.7 "When the last image is reached" (navigation.endOfFolder): what Next at the last image (and Previous at the
/// first) does. <see cref="EndOfFolderPolicy"/> decides; this file only executes the step, shows the "Ask me each time"
/// picker and owns the per-tab "Loop this folder" override (status-bar button / navigation.toggleLoopFolder).
/// </summary>
public partial class MainWindow
{
    private const string LoopFolderActionId = "navigation.toggleLoopFolder";

    /// <summary>Tabs whose "Loop this folder" override is on (runtime only, never saved).</summary>
    private readonly HashSet<Guid> _loopFolderTabs = new();
    private EndOfFolderPickerCard? _endOfFolderPicker;
    private bool _endOfFolderPickerSawKeyUp;

    private bool LoopFolderOverride => _workspace.Active is { } tab && _loopFolderTabs.Contains(tab.Id);

    /// <summary>What reaching the end in direction <paramref name="delta"/> does now (setting + this tab's loop override).</summary>
    private EndOfFolderStep EndOfFolderStepFor(int delta) =>
        EndOfFolderPolicy.Decide(
            EndOfFolderPolicy.Effective(EndOfFolderActions.Parse(NavigationOptionsView.EndOfFolderAction), LoopFolderOverride),
            Math.Sign(delta));

    private Task<bool> ExecuteEndOfFolderStepSerializedAsync(EndOfFolderStep step, int direction) =>
        RunSerializedNavigationResultAsync(() => ExecuteEndOfFolderStepAsync(step, direction));

    /// <summary>Executes an end-of-folder step. The caller holds the navigation gate. True when the view moved.</summary>
    private async Task<bool> ExecuteEndOfFolderStepAsync(EndOfFolderStep step, int direction, bool rapidPreviewOnly = false)
    {
        if (_navigator.Count == 0 || direction == 0 || _navigationBoundaryPromptActive) return false;
        // A posted step can be stale: only act while still at that end of the folder.
        var atBoundary = direction > 0 ? _navigator.Index >= _navigator.Count - 1 : _navigator.Index <= 0;
        if (!atBoundary) return false;
        _diagnostics.Write("navigation", "end_of_folder", new { step = step.Kind.ToString(), step.FolderDirection, direction, loopOverride = LoopFolderOverride });
        switch (step.Kind)
        {
            case EndOfFolderStepKind.Loop:
                if (_navigator.Count <= 1)
                {
                    if (!rapidPreviewOnly) ShowNavigationBoundaryNotice(direction);
                    return false;
                }
                _navigator.SelectIndex(EndOfFolderPolicy.LoopTargetIndex(direction, _navigator.Count));
                await PresentCurrentAsync(rapidPreviewOnly: rapidPreviewOnly, forceFull: !rapidPreviewOnly);
                return true;
            case EndOfFolderStepKind.Folder:
                return await TryNavigateSiblingFolderAsync(step.FolderDirection, rapidPreviewOnly: rapidPreviewOnly);
            case EndOfFolderStepKind.FolderInNewTab:
            {
                if (await FindSiblingFolderAsync(step.FolderDirection) is not { } result) return false;
                var opening = ResolveNewTabOpening(KeyModifiers.None) with { Activate = true };
                var tab = new ImageTabState(Guid.NewGuid(), Path.GetFullPath(result.SelectedPath));
                // Opening and activating a tab loads it through the navigation gate this step holds: run it after.
                Dispatcher.UIThread.Post(async () => await OpenInNewTabAsync(tab, opening, "end_of_folder"));
                return true;
            }
            case EndOfFolderStepKind.Home:
                await NavigateHomeCoreAsync();
                return true;
            case EndOfFolderStepKind.Ask:
                return await AskEndOfFolderAsync(direction);
            default:
                if (!rapidPreviewOnly) ShowNavigationBoundaryNotice(direction);
                return false;
        }
    }

    /// <summary>Shows the "Ask me each time" card and runs the picked step. A navigation barrier while open.</summary>
    private async Task<bool> AskEndOfFolderAsync(int direction)
    {
        var current = _navigator.Current;
        if (current is null || _endOfFolderPicker is not null) return false;
        string? nextName = null;
        try
        {
            var next = await SiblingFolderNavigationService.FindAsync(current, direction, SiblingFolderOptions(direction), false, ImageNavigator.IsSupported);
            nextName = next is null ? null : Path.GetFileName(next.Folder.TrimEnd(Path.DirectorySeparatorChar));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _diagnostics.Write("navigation", "end_of_folder_lookup_failed", new { error = ex.GetType().Name });
        }
        catch (OperationCanceledException) { return false; }

        _navigationBoundaryPromptActive = true;
        InterruptRapidNavigationForBlockingUi("end_of_folder_picker");
        var folderName = Path.GetFileName(Path.GetDirectoryName(current)) is { Length: > 0 } name ? name : current;
        var position = $"{_navigator.Index + 1} of {_navigator.Count}";
        var card = new EndOfFolderPickerCard(new EndOfFolderPicker(direction), folderName, position, nextName);
        var picked = new TaskCompletionSource<(EndOfFolderAction Action, bool Remember)>();
        card.Picked += (action, remember) => picked.TrySetResult((action, remember));
        (EndOfFolderAction Action, bool Remember) choice;
        _endOfFolderPicker = card;
        _endOfFolderPickerSawKeyUp = false;
        EndOfFolderPickerHost.Children.Clear();
        EndOfFolderPickerHost.Children.Add(card);
        EndOfFolderPickerHost.IsVisible = true;
        EndOfFolderPickerHost.PointerPressed += EndOfFolderPickerHostPressed;
        AddHandler(InputElement.KeyUpEvent, EndOfFolderPickerKeyUp, RoutingStrategies.Tunnel, true);
        try
        {
            choice = await picked.Task;
        }
        finally
        {
            RemoveHandler(InputElement.KeyUpEvent, EndOfFolderPickerKeyUp);
            EndOfFolderPickerHost.PointerPressed -= EndOfFolderPickerHostPressed;
            EndOfFolderPickerHost.IsVisible = false;
            EndOfFolderPickerHost.Children.Clear();
            _endOfFolderPicker = null;
            _navigationBoundaryPromptActive = false;
        }
        _diagnostics.Write("navigation", "end_of_folder_picked", new { action = choice.Action.ToString(), choice.Remember, direction });
        if (choice.Remember)
        {
            _settings.EndOfFolderAction = EndOfFolderActions.NameOf(choice.Action);
            SaveSettingsAndPublish();
        }
        return await ExecuteEndOfFolderStepAsync(EndOfFolderPolicy.Decide(choice.Action, direction), direction);
    }

    private void EndOfFolderPickerHostPressed(object? sender, PointerPressedEventArgs e)
    {
        e.Handled = true;
        _endOfFolderPicker?.Dismiss();
    }

    private void EndOfFolderPickerKeyUp(object? sender, KeyEventArgs e) => _endOfFolderPickerSawKeyUp = true;

    /// <summary>
    /// While the picker is open it owns the keyboard: its own keys, plus the navigation hotkey that reached the end
    /// (pressed again after a release, so a held key's auto-repeat never picks by accident). Every other key is swallowed.
    /// </summary>
    private void EndOfFolderPickerKeyDown(object? sender, KeyEventArgs e)
    {
        if (_endOfFolderPicker is not { } card) return;
        e.Handled = true;
        if (EndOfFolderPickerCard.Translate(e.Key, e.KeyModifiers) is { } key)
        {
            card.HandleKey(key.Key, key.Digit);
            return;
        }
        var action = _inputRouter.ResolveShortcutAction(KeyboardShortcutText.From(e), _settings.Hotkeys);
        var direction = action?.Command switch { GlideCommand.NextImage => 1, GlideCommand.PreviousImage => -1, _ => 0 };
        if (direction != 0 && direction == card.Picker.TravelDirection && _endOfFolderPickerSawKeyUp)
            card.HandleKey(EndOfFolderPickerKey.NavigateAgain);
    }

    private SiblingFolderNavigationOptions SiblingFolderOptions(int direction, bool forceDirectionalEdge = false) => new(
        NavigationOptionsView.FolderNavIncludeHidden,
        NavigationOptionsView.FolderNavWrap,
        NavigationOptionsView.FolderNavSkipEmpty,
        forceDirectionalEdge ? direction > 0 : NavigationOptionsView.FolderNavOpenFirstImage,
        NavigationOptionsView.HierarchicalFolderTraversal,
        NavigationOptionsView.FolderNavOrder,
        string.Equals(NavigationOptionsView.HierarchicalPreviousFolderEntry, "First image", StringComparison.OrdinalIgnoreCase));

    // ---- "Loop this folder" per-tab override -------------------------------------------------------------------

    private Task ToggleLoopFolderAsync()
    {
        if (_workspace.Active is not { } tab) return Task.CompletedTask;
        var on = !_loopFolderTabs.Remove(tab.Id);
        if (on) _loopFolderTabs.Add(tab.Id);
        _diagnostics.Write("navigation", "loop_folder_toggled", new { tab.Id, on });
        UpdateLoopFolderStatusButton();
        ShowViewerNotice(on ? "Loop this folder: on" : "Loop this folder: off");
        return Task.CompletedTask;
    }

    /// <summary>The status-bar loop button (when added) shows this tab's state: the active style when on, a dim icon when off.</summary>
    private void UpdateLoopFolderStatusButton()
    {
        var on = LoopFolderOverride;
        foreach (var (button, actionId) in _extraStatusButtons)
        {
            if (!string.Equals(actionId, LoopFolderActionId, StringComparison.OrdinalIgnoreCase)) continue;
            button.Classes.Set("statusToggleOn", on);
            button.Classes.Set("statusToggleOff", !on);
            var shortcut = ShortcutForAction(LoopFolderActionId);
            ToolTip.SetTip(button, $"Loop this folder: {(on ? "on" : "off")}{(string.IsNullOrWhiteSpace(shortcut) ? "" : $" ({shortcut})")}\n" +
                "Overrides â€˜When the last image is reachedâ€™ for this tab");
        }
    }
}
