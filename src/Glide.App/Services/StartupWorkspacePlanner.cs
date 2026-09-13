using Glide.Core;
using Glide.Core.Workspace;

namespace Glide.App.Services;

/// <summary>
/// Builds the initial tab model for explicit launch paths without touching Avalonia controls.
/// The first accepted path is always the active tab so first-presentation work cannot be displaced
/// by dormant launch arguments. Additional paths remain model-only until the user selects them.
/// </summary>
public static class StartupWorkspacePlanner
{
    public sealed record Plan(IReadOnlyList<string> Paths, IReadOnlyList<TabState> Tabs);

    public static Plan Create(IEnumerable<string> paths)
    {
        var accepted = paths
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(Normalize)
            .Where(path => path is not null)
            .Select(path => path!)
            .Where(path => Directory.Exists(path) || (File.Exists(path) && ImageNavigator.IsSupported(path)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var tabs = accepted.Select(path => Directory.Exists(path)
                ? (TabState)new BrowserTabState(Guid.NewGuid(), path)
                : new ImageTabState(Guid.NewGuid(), path))
            .ToArray();
        return new Plan(accepted, tabs);
    }

    private static string? Normalize(string path)
    {
        try { return Path.GetFullPath(path); }
        catch { return null; }
    }
}
