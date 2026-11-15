using Avalonia.Controls;
using Glide.App.Services;
using Glide.App.Settings;
using Glide.Core.Workspace;

namespace Glide.App;

/// <summary>
/// The title-bar "Show in Explorer" button (id file.locate, command OpenContainingFolder): reveal the
/// current image either in Glide's own Explorer tab (default, 4.8.0) or in Windows File Explorer.
/// The choice is the <c>ShowInExplorerTarget</c> setting and can be flipped from the button's context menu.
/// </summary>
public partial class MainWindow
{
    /// <summary>
    /// Launches Windows File Explorer with the file selected; returns whether it was started. This is a
    /// seam so tests never start explorer.exe: replace it, run the command, then restore the original.
    /// </summary>
    internal static Func<string, bool> WindowsExplorerLocateLauncher { get; set; } =
        path => new FileOperationController().TryOpenContainingFolder(path);

    private bool LocateOpensWindowsExplorer => ShowInExplorerTargets.IsWindowsExplorer(_settings.ShowInExplorerTarget);

    /// <summary>Title-bar / tooltip label for a button; the locate button's text follows the current choice.</summary>
    private string TitleActionLabel(string id, TitleBarButtonDefinition definition) =>
        id == "file.locate" ? TitleBarButtonCatalog.LocateLabel(_settings.ShowInExplorerTarget) : definition.Label;

    /// <summary>The image the locate command applies to: the active image tab's file, never a stale path.</summary>
    private string? CurrentLocatePath()
    {
        if (_workspace.Active is not ImageTabState image) return null;
        if (!string.IsNullOrWhiteSpace(_currentPath) && File.Exists(_currentPath)) return _currentPath;
        return File.Exists(image.Path) ? image.Path : null;
    }

    private async Task OpenContainingFolderAsync()
    {
        var path = CurrentLocatePath();
        if (LocateOpensWindowsExplorer)
        {
            if (string.IsNullOrWhiteSpace(path)) return;
            var opened = WindowsExplorerLocateLauncher(path);
            _diagnostics.Write("file", opened ? "open_containing_folder_external" : "open_containing_folder_failed", new { path });
            return;
        }
        await ShowPathInExplorerTabAsync(path);
    }

    /// <summary>
    /// Shows <paramref name="path"/> (a file, or a folder) in an Explorer tab of this window with the file
    /// selected and scrolled into view. Reuses the active Explorer tab when it already shows that folder,
    /// otherwise opens a new Explorer tab. With no usable path it opens the Explorer tab at the usual
    /// fresh-tab location (last location / Pictures) instead of failing silently.
    /// </summary>
    private async Task ShowPathInExplorerTabAsync(string? path)
    {
        string? file = null;
        string? folder = null;
        if (!string.IsNullOrWhiteSpace(path))
        {
            try
            {
                var full = Path.GetFullPath(path);
                if (File.Exists(full)) { file = full; folder = Path.GetDirectoryName(full); }
                else if (Directory.Exists(full)) folder = full;
            }
            catch { /* unusable path: file and folder stay null and the caller reports it */ }
        }
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
        {
            file = null;
            // Nothing to select. When an Explorer tab is already in front there is nothing more to do.
            if (_workspace.Active is BrowserTabState) return;
            folder = ResolveFreshExplorerFolder();
        }

        static string Normalize(string value) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(value));
        Guid tabId;
        var reuse = _workspace.Active is BrowserTabState active &&
                    string.Equals(Normalize(active.Folder), Normalize(folder), StringComparison.OrdinalIgnoreCase);
        if (reuse) tabId = _workspace.Active!.Id;
        else
        {
            tabId = _workspace.AddBrowser(folder).Id;
            RebuildTabStrip();
        }
        // Contract with the Explorer surface: once the tab is active it selects and scrolls to this path.
        if (file is not null) _browserHighlightTargets[tabId] = file;
        else _browserHighlightTargets.Remove(tabId);
        await ActivateWorkspaceAsync();
        _diagnostics.Write("file", "open_containing_folder_explorer_tab", new { path = file ?? folder, folder, reused = reuse });
    }

    private MenuItem BuildShowInExplorerTargetMenuItem(string text, string target, string icon)
    {
        var selected = string.Equals(ShowInExplorerTargets.Normalize(_settings.ShowInExplorerTarget), target, StringComparison.Ordinal);
        var item = Glide.App.Services.MenuActions.Tag(new MenuItem { Header = selected ? $"{text}  ✓" : text, Icon = MenuIcon(icon) }, "setting.window.showInExplorerTarget");
        item.Click += (_, _) => SetShowInExplorerTarget(target);
        return item;
    }

    private void SetShowInExplorerTarget(string target)
    {
        _settings.ShowInExplorerTarget = ShowInExplorerTargets.Normalize(target);
        SaveSettingsAndPublish();
        RebuildTitleActionStrip();
    }
}
