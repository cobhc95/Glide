using Avalonia.Interactivity;
using Avalonia.Threading;
using Glide.App.Updates;
using Glide.Core;
using Glide.Core.Updates;

namespace Glide.App.Settings;

/// <summary>
/// Settings > General &amp; startup > Updates: the two checkboxes are ordinary catalogued settings (updates.checkDaily and
/// updates.dontRemind); the status line, "View on GitHub" and "Check now" are not settings, so they never count as a changed
/// setting. They read the live <see cref="UpdateService"/> (null in designers and tests that do not install one: the card then
/// says "Not checked yet" and "Check now" is disabled).
/// </summary>
public partial class SettingsWindow
{
    private UpdateService? _updates;

    /// <summary>The process-wide update service the card talks to; set by the owner right after construction.</summary>
    internal UpdateService? Updates
    {
        get => _updates;
        set
        {
            if (ReferenceEquals(_updates, value)) return;
            if (_updates is not null) _updates.StatusChanged -= OnUpdateStatusChanged;
            _updates = value;
            if (_updates is not null) _updates.StatusChanged += OnUpdateStatusChanged;
            RefreshUpdateStatus();
        }
    }

    private void OnUpdateStatusChanged()
    {
        if (Dispatcher.UIThread.CheckAccess()) RefreshUpdateStatus();
        else Dispatcher.UIThread.Post(RefreshUpdateStatus);
    }

    /// <summary>Redraws the status line and the two buttons from the live service state.</summary>
    internal void RefreshUpdateStatus()
    {
        if (UpdateResultText is null) return;
        var info = _updates?.GetStatus() ??
                   UpdateStatusText.Describe(new UpdateCheckState(), UpdateVersion.ParseOrNull(AppVersion.Short) ?? new UpdateVersion(0, 0, 0, 0, false), DateTime.UtcNow);
        UpdateLastCheckedText.Text = info.LastCheckedLine;
        UpdateLastCheckedText.IsVisible = !string.IsNullOrEmpty(info.LastCheckedLine);
        UpdateResultText.Text = info.ResultLine;
        UpdateViewButton.IsVisible = info.UpdateAvailable;
        var checking = info.Kind == UpdateStatusKind.Checking;
        UpdateCheckNowButton.Content = checking ? "Checking..." : "Check now";
        UpdateCheckNowButton.IsEnabled = _updates is not null && !checking;
    }

    private async void UpdateCheckNowClicked(object? sender, RoutedEventArgs e)
    {
        if (_updates is null) return;
        try
        {
            await _updates.CheckNowAsync();
        }
        catch
        {
            // A check never takes Settings down; the status line shows "Couldn't reach GitHub" when it failed.
        }
        RefreshUpdateStatus();
    }

    private void UpdateViewClicked(object? sender, RoutedEventArgs e)
    {
        if (_updates is null) return;
        UpdateBrowserLauncher.Open(_updates.ReleaseUrl);
    }
}
