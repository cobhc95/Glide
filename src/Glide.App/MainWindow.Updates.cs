using Avalonia.Controls;
using Glide.App.Settings;
using Glide.App.Updates;
using Glide.Core.Updates;

namespace Glide.App;

/// <summary>
/// The update checker's window side (docs: README "Updates"): when it is a good moment to show the prompt, the prompt itself, the
/// "Don't remind me again" setting change and the "Check for updates now" action. The schedule, the check and the rules live in
/// <see cref="UpdateService"/> / Glide.Core.Updates; nothing here downloads or installs anything.
/// </summary>
public partial class MainWindow
{
    /// <summary>The Settings window currently open over this window (its Updates card shows the result of a check).</summary>
    private SettingsWindow? _openSettingsWindow;

    /// <summary>
    /// Why the update prompt cannot be shown over this window right now: it is not the visible, active window, it is fullscreen,
    /// a slideshow is active, another dialog (Settings, a confirmation...) is open, or the user is typing in a text box.
    /// </summary>
    internal UpdatePromptDeferral GetUpdatePromptDeferral()
    {
        var focused = TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement();
        var typing = focused is TextBox { IsReadOnly: false };
        var modalOpen = OwnedWindows.Any(w => w.IsVisible);
        var activeWindow = IsVisible && IsActive && WindowState != WindowState.Minimized;
        return UpdateSchedule.Defer(activeWindow, WindowState == WindowState.FullScreen, _slideshow?.IsActive == true, modalOpen, typing);
    }

    /// <summary>Shows the update prompt modally over this window; null when it could not be shown (the window is not visible).</summary>
    internal async Task<UpdatePromptResult?> ShowUpdatePromptAsync(UpdatePromptModel model)
    {
        try
        {
            var dialog = new UpdatePromptWindow(model) { Icon = Icon };
            _overlays?.SetModalSuppressed(true);
            try { await ShowOwnedDialogAsync(dialog); }
            finally { _overlays?.SetModalSuppressed(false); }
            return dialog.WasShown ? dialog.Outcome ?? new UpdatePromptResult(UpdatePromptAction.NotNow, false) : null;
        }
        catch (Exception ex)
        {
            _diagnostics.Write("updates", "prompt_failed", new { error = ex.GetType().Name });
            return null;
        }
    }

    /// <summary>"Don't remind me again": turns the setting on (or off) exactly like Settings > Apply (live, saved, published).</summary>
    internal void SetUpdateDontRemind(bool value)
    {
        if (_settings.UpdateDontRemind == value) return;
        var state = _settings.CloneState();
        state.UpdateDontRemind = value;
        ApplySettingsFromDialog(state, commit: true);
    }

    /// <summary>
    /// The registry action "Check for updates now" (app.checkUpdates): the same check as Settings > Updates > Check now. With Settings
    /// open the result shows in its status line; otherwise a newer version opens the update prompt and "up to date" or a failed check
    /// shows a short message.
    /// </summary>
    private async Task CheckForUpdatesNowAsync()
    {
        var service = UpdateService.Current;
        if (service is null)
        {
            _diagnostics.Write("updates", "service_unavailable", null);
            return;
        }
        var settingsOpen = _openSettingsWindow is not null;
        var outcome = await service.CheckNowAsync(promptIfAvailable: !settingsOpen);
        if (settingsOpen) return;
        switch (outcome.Status)
        {
            case UpdateCheckStatus.UpToDate:
                await ConfirmAsync("Glide is up to date", $"You have Glide {service.CurrentVersion}, the latest version.", "OK", showCancel: false);
                break;
            case UpdateCheckStatus.Failed:
                await ConfirmAsync("Check for updates", UpdateStatusText.CouldNotReachGitHub + ". Check your connection and try again.", "OK", showCancel: false);
                break;
        }
    }
}
