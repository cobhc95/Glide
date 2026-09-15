namespace Glide.Core.Workspace;

public enum TabKind { Home, Image, Browser }

/// <summary>
/// Typed workspace state. Browser is defined now even though its live shell implementation
/// arrives later, preventing future work from requiring a tab-model rewrite.
/// </summary>
public abstract record TabState(Guid Id, string Title, TabKind Kind);
public sealed record HomeTabState(Guid Id) : TabState(Id, "Home", TabKind.Home);
public sealed record ImageTabState(Guid Id, string Path) : TabState(Id, System.IO.Path.GetFileName(Path), TabKind.Image)
{
    public ImageTabViewState? ViewState { get; init; }
}

public sealed record BrowserTabState(Guid Id, string Folder) : TabState(Id, System.IO.Path.GetFileName(Folder), TabKind.Browser)
{
    public BrowserNavigationState? Navigation { get; init; }
}

/// <summary>Framework-free image view state that travels with a tab between Glide windows.</summary>
public sealed record ImageTabViewState(string Mode, double Zoom, double PanX, double PanY);

/// <summary>Typed Explorer history that travels with a browser tab.</summary>
public sealed record BrowserNavigationState(IReadOnlyList<string> History, int Index);
