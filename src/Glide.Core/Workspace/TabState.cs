namespace Glide.Core.Workspace;

public enum TabKind { Home, Image, Browser }

/// <summary>
/// Typed workspace state. Browser is defined now even though its live shell implementation
/// arrives later, preventing future work from requiring a tab-model rewrite.
/// </summary>
public abstract record TabState(Guid Id, string Title, TabKind Kind)
{
    /// <summary>
    /// The tab's Back / Forward history while it travels to another Glide window (tab drag / detach). The owning window
    /// keeps the live history itself; this copy is only set when the tab is handed over and is never saved in the
    /// session file.
    /// </summary>
    public Glide.Core.Navigation.NavigationHistoryState? History { get; init; }
}
public sealed record HomeTabState(Guid Id) : TabState(Id, "Home", TabKind.Home);
public sealed record ImageTabState(Guid Id, string Path) : TabState(Id, System.IO.Path.GetFileName(Path), TabKind.Image)
{
    public ImageTabViewState? ViewState { get; init; }
}

public sealed record BrowserTabState(Guid Id, string Folder) : TabState(Id, System.IO.Path.GetFileName(Folder), TabKind.Browser)
{
    public BrowserNavigationState? Navigation { get; init; }

    /// <summary>
    /// Selection and scroll of the folder currently in <see cref="Folder"/>; null when unknown. It is
    /// dropped whenever the tab moves to a different folder (see WorkspaceState.ReplaceActiveBrowserFolder).
    /// </summary>
    public BrowserTabViewState? View { get; init; }
}

/// <summary>The item selected in an Explorer tab's folder and how far its list is scrolled (pixels).</summary>
public sealed record BrowserTabViewState(string? SelectedPath, double ScrollY);

/// <summary>Framework-free image view state that travels with a tab between Glide windows.</summary>
public sealed record ImageTabViewState(string Mode, double Zoom, double PanX, double PanY);

/// <summary>Typed Explorer history that travels with a browser tab.</summary>
public sealed record BrowserNavigationState(IReadOnlyList<string> History, int Index, IReadOnlyList<string?>? Highlights = null);
