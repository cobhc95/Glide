using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Glide.App.Settings;

namespace Glide.App;

/// <summary>
/// Back up and restore (Settings &gt; Windows &amp; developer): the "Export / Import Glide backup" commands and the main
/// window's own values an import changes right away (<see cref="IGlideBackupLiveTarget"/>). Every imported geometry passes the
/// same monitor check as a remembered placement at start-up (<see cref="IsSafeSavedWindowPosition"/>).
/// </summary>
public partial class MainWindow : IGlideBackupLiveTarget
{
    /// <summary>Run once on the next Settings window this main window opens (the backup commands open Settings first).</summary>
    private Action<SettingsWindow>? _settingsOpenedHook;

    /// <summary>"Export Glide backup..." / "Import Glide backup...": Settings at the Back up and restore card, then its flow.</summary>
    private Task OpenBackupFromCommandAsync(bool import)
    {
        if (_openSettingsWindow is { } open)
        {
            open.Activate();
            open.StartBackupFromCommand(import);
            return Task.CompletedTask;
        }
        _settingsOpenedHook = window => window.StartBackupFromCommand(import);
        SettingsClicked(this, new RoutedEventArgs());
        return Task.CompletedTask;
    }

    /// <summary>Hands the pending command hook to a Settings window that is about to open (SettingsClicked).</summary>
    private void AttachSettingsOpenedHook(SettingsWindow window)
    {
        if (_settingsOpenedHook is not { } hook) return;
        _settingsOpenedHook = null;
        window.Opened += (_, _) => hook(window);
    }

    IReadOnlyList<string> IGlideBackupLiveTarget.ApplyImportedWindowPlacements(GlideSettingsState imported)
    {
        var notes = new List<string>();
        var scale = RenderScaling > 0 ? RenderScaling : 1.0;
        var width = imported.WindowWidth;
        var height = imported.WindowHeight;
        if (!double.IsFinite(width) || !double.IsFinite(height) || width < 400 || height < 300 || width > 20000 || height > 20000)
        {
            // Nothing usable was remembered on the other PC: this window stays as it is.
        }
        else if (_wholeAppOverlayMode || WindowState == WindowState.FullScreen)
        {
            notes.Add("The main window keeps its place while it is full screen or in Overlay mode.");
        }
        else
        {
            if (WindowState != WindowState.Normal) WindowState = WindowState.Normal;
            // Never larger than the screen the window is on now.
            if ((Screens.ScreenFromWindow(this) ?? Screens.Primary) is { } screen)
            {
                width = Math.Min(width, screen.WorkingArea.Width / scale);
                height = Math.Min(height, screen.WorkingArea.Height / scale);
            }
            Width = width;
            Height = height;
            var position = new PixelPoint(imported.WindowX, imported.WindowY);
            if (IsSafeSavedWindowPosition(position, width, height, scale)) Position = position;
            else notes.Add("The main window's saved position is not on this PC's screens, so only its size was used.");
            _lastNormalWindowPosition = Position;
            _lastNormalWindowSize = new Size(width, height);
            _normalPlacementKnown = true;
            _settings.WindowX = Position.X;
            _settings.WindowY = Position.Y;
            _settings.WindowWidth = width;
            _settings.WindowHeight = height;
            _settings.WindowWasMaximized = imported.WindowWasMaximized;
            if (imported.WindowWasMaximized) WindowState = WindowState.Maximized;
        }

        // Per-image Overlay-mode placements: only those that fit this PC's screens, keyed by a local path.
        var kept = 0;
        var skipped = 0;
        _settings.OverlayWindowPlacements ??= new Dictionary<string, OverlayWindowPlacementState>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in (imported.OverlayWindowPlacements ?? new()).OrderByDescending(x => x.Value?.LastUsedUtcTicks ?? 0).Take(GlideBackupService.MaxPlacements))
        {
            if (value is null || GlideBackupService.SafeLocalPath(key) is not { } local ||
                !double.IsFinite(value.Width) || !double.IsFinite(value.Height) || value.Width < 80 || value.Height < 80 || value.Width > 20000 || value.Height > 20000 ||
                !IsSafeSavedWindowPosition(new PixelPoint(value.X, value.Y), value.Width, value.Height, scale))
            {
                skipped++;
                continue;
            }
            _settings.OverlayWindowPlacements[local] = value with { };
            kept++;
        }
        if (_settings.OverlayWindowPlacements.Count > 128)
            foreach (var stale in _settings.OverlayWindowPlacements.OrderBy(kv => kv.Value.LastUsedUtcTicks).Take(_settings.OverlayWindowPlacements.Count - 128).Select(kv => kv.Key).ToArray())
                _settings.OverlayWindowPlacements.Remove(stale);
        if (skipped > 0) notes.Add($"{skipped} Overlay-mode placement{(skipped == 1 ? "" : "s")} did not fit this PC's screens and {(skipped == 1 ? "was" : "were")} left out.");
        App.PublishCurrentSettings(_settings);
        return notes;
    }

    void IGlideBackupLiveTarget.ApplyImportedSessionLocations(GlideSettingsState imported)
    {
        _settings.LastNewExplorerTabDirectory = imported.LastNewExplorerTabDirectory ?? string.Empty;
        _settings.LastOpenDirectory = imported.LastOpenDirectory ?? string.Empty;
        _settings.LastOpenFileDirectory = imported.LastOpenFileDirectory ?? string.Empty;
        _settings.LastOverlayDirectory = imported.LastOverlayDirectory ?? string.Empty;
        App.PublishCurrentSettings(_settings);
    }
}
