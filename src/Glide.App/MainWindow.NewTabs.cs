using Avalonia.Input;
using Glide.App.Settings;
using Glide.Core;
using Glide.Core.Workspace;

namespace Glide.App;

/// <summary>
/// 5.0.6: where tabs opened "in a new tab" go and whether Glide switches to them ("New tabs open at", "Open new
/// tabs in the foreground", and Shift / Alt flip them when "Shift and Alt change how a new tab opens" is on:
/// <see cref="NewTabPolicy"/>; Ctrl+T and "+" follow only the position), and which tab becomes active after closing the active
/// tab ("After closing a tab, activate": <see cref="TabCloseActivation"/>).
/// </summary>
public partial class MainWindow
{
    private NewTabOpening ResolveNewTabOpening(KeyModifiers modifiers) =>
        NewTabPolicy.Resolve(_settings.NewTabPosition, _settings.SwitchToNewTabs,
            shift: modifiers.HasFlag(KeyModifiers.Shift), alt: modifiers.HasFlag(KeyModifiers.Alt), modifiersEnabled: _settings.NewTabModifiers);

    /// <summary>Adds <paramref name="tab"/> as a tab opened from the current one: placed and activated as the settings and held keys say.</summary>
    private async Task OpenInNewTabAsync(TabState tab, KeyModifiers modifiers, string source)
    {
        var opening = ResolveNewTabOpening(modifiers);
        if (opening.Activate) CaptureActiveTabRuntimeState();
        _workspace.OpenTab(tab, opening);
        _diagnostics.Write("tabs", "opened_in_new_tab", new { tab.Id, tab.Kind, source, atEnd = opening.AtEnd, activate = opening.Activate });
        if (opening.Activate) await ActivateWorkspaceAsync();
        else RebuildTabStrip();
    }

    private async Task OpenImageInNewTabAsync(string path, KeyModifiers modifiers, string source)
    {
        if (!File.Exists(path) || !ImageNavigator.IsSupported(path)) return;
        await OpenInNewTabAsync(new ImageTabState(Guid.NewGuid(), Path.GetFullPath(path)), modifiers, source);
        if (_settings.RecentHistoryEnabled) RecentHistoryStore.RecordFile(path, _settings.HistorySize);
    }

    private Task OpenFolderInNewTabAsync(string folder, KeyModifiers modifiers, string source) =>
        OpenInNewTabAsync(new BrowserTabState(Guid.NewGuid(), Path.GetFullPath(folder)), modifiers, source);

    /// <summary>
    /// Adds a plain new tab (Ctrl+T, "+", its menu, a title-bar middle-click) where "New tabs open at" says and makes it
    /// the active tab (<see cref="NewTabPolicy.ForNewTabCommand"/>); the caller then activates the workspace.
    /// </summary>
    private TabState AddNewTab(TabState tab) =>
        _workspace.OpenTab(tab, NewTabPolicy.ForNewTabCommand(_settings.NewTabPosition));

    private TabState AddNewHomeTab() => AddNewTab(new HomeTabState(Guid.NewGuid()));
    private TabState AddNewImageTab(string path) => AddNewTab(new ImageTabState(Guid.NewGuid(), Path.GetFullPath(path)));
    private TabState AddNewBrowserTab(string folder) => AddNewTab(new BrowserTabState(Guid.NewGuid(), Path.GetFullPath(folder)));

    /// <summary>The tab-close rule in effect ("Match Ctrl+Tab order" follows the Ctrl+Tab switching order).</summary>
    private TabCloseActivationMode TabCloseActivationMode =>
        TabCloseActivation.Resolve(_settings.AfterTabCloseActivate, _settings.TabCycleOrder);

    /// <summary>Most recently used tab ids, most recent first, with a finished Ctrl+Tab cycle committed.</summary>
    private IReadOnlyList<Guid> RecentlyUsedTabs()
    {
        EndTabMruCycle();
        TouchTabMru();
        return _tabMru.ToArray();
    }
}
