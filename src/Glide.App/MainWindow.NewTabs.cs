using Avalonia.Input;
using Glide.App.Settings;
using Glide.Core;
using Glide.Core.Workspace;

namespace Glide.App;

/// <summary>
/// 5.0.6: where tabs opened "in a new tab" go and whether Glide switches to them ("New tabs open", "Open new
/// tabs in the", and each one's "Key to open … instead" flips it for one tab (round 3 #18):
/// <see cref="NewTabPolicy"/>; Ctrl+T and "+" follow only the position), and which tab becomes active after closing the active
/// tab ("After closing a tab, activate": <see cref="TabCloseActivation"/>).
/// </summary>
public partial class MainWindow
{
    private NewTabOpening ResolveNewTabOpening(KeyModifiers modifiers) =>
        NewTabPolicy.Resolve(_settings.NewTabPosition, _settings.SwitchToNewTabs, _settings.NewTabPositionKey, _settings.NewTabForegroundKey,
            shift: modifiers.HasFlag(KeyModifiers.Shift), ctrl: modifiers.HasFlag(KeyModifiers.Control), alt: modifiers.HasFlag(KeyModifiers.Alt));

    /// <summary>5.1.0: an Explorer middle-click has its own side and position settings; the held-key flips still apply.</summary>
    private NewTabOpening ResolveMiddleClickOpening(KeyModifiers modifiers) =>
        NewTabPolicy.ResolveMiddleClick(_settings.MiddleClickNewTabSide, _settings.MiddleClickNewTabPosition, _settings.NewTabPosition,
            _settings.NewTabPositionKey, _settings.NewTabForegroundKey,
            shift: modifiers.HasFlag(KeyModifiers.Shift), ctrl: modifiers.HasFlag(KeyModifiers.Control), alt: modifiers.HasFlag(KeyModifiers.Alt));

    /// <summary>Adds <paramref name="tab"/> as a tab opened from the current one: placed and activated as the settings and held keys say.</summary>
    private Task OpenInNewTabAsync(TabState tab, KeyModifiers modifiers, string source) =>
        OpenInNewTabAsync(tab, ResolveNewTabOpening(modifiers), source);

    /// <summary>Adds <paramref name="tab"/> placed and activated exactly as <paramref name="opening"/> says.</summary>
    private async Task OpenInNewTabAsync(TabState tab, NewTabOpening opening, string source)
    {
        if (opening.Activate) CaptureActiveTabRuntimeState();
        _workspace.OpenTab(tab, opening);
        _diagnostics.Write("tabs", "opened_in_new_tab", new { tab.Id, tab.Kind, source, atEnd = opening.AtEnd, activate = opening.Activate });
        if (opening.Activate) await ActivateWorkspaceAsync();
        else RebuildTabStrip();
    }

    private Task OpenImageInNewTabAsync(string path, KeyModifiers modifiers, string source) =>
        OpenImageInNewTabAsync(path, ResolveNewTabOpening(modifiers), source);

    private async Task OpenImageInNewTabAsync(string path, NewTabOpening opening, string source)
    {
        if (!File.Exists(path) || !ImageNavigator.IsSupported(path)) return;
        await OpenInNewTabAsync(new ImageTabState(Guid.NewGuid(), Path.GetFullPath(path)), opening, source);
        if (_settings.RecentHistoryEnabled) RecentHistoryStore.RecordFile(path, _settings.HistorySize);
    }

    private Task OpenFolderInNewTabAsync(string folder, KeyModifiers modifiers, string source) =>
        OpenFolderInNewTabAsync(folder, ResolveNewTabOpening(modifiers), source);

    private Task OpenFolderInNewTabAsync(string folder, NewTabOpening opening, string source) =>
        OpenInNewTabAsync(new BrowserTabState(Guid.NewGuid(), Path.GetFullPath(folder)), opening, source);

    /// <summary>
    /// Adds a plain new tab (Ctrl+T, "+", its menu, a title-bar middle-click) at the far right end and makes it
    /// the active tab (<see cref="NewTabPolicy.ForNewTabCommand"/>); the caller then activates the workspace.
    /// </summary>
    private TabState AddNewTab(TabState tab) =>
        _workspace.OpenTab(tab, NewTabPolicy.ForNewTabCommand());

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
        return _tabStrip.Mru.ToArray();
    }
}
