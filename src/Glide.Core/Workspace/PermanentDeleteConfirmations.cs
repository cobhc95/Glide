namespace Glide.Core.Workspace;

/// <summary>
/// Round 4 G1: the image view's Shift+Delete (permanent delete) asks first, with "Don't ask again for this folder in this
/// tab". This remembers those (tab, folder) pairs for the session only: never saved; another tab or another folder asks again.
/// </summary>
public sealed class PermanentDeleteConfirmations
{
    private readonly Dictionary<Guid, HashSet<string>> _skip = new();

    private static string Key(string folder) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));

    /// <summary>True when the confirmation should be shown for deleting a file in <paramref name="folder"/> from tab <paramref name="tabId"/>.</summary>
    public bool ShouldAsk(Guid tabId, string? folder) =>
        string.IsNullOrWhiteSpace(folder) || !_skip.TryGetValue(tabId, out var folders) || !folders.Contains(Key(folder));

    public void DontAskAgain(Guid tabId, string? folder)
    {
        if (string.IsNullOrWhiteSpace(folder)) return;
        if (!_skip.TryGetValue(tabId, out var folders)) _skip[tabId] = folders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        folders.Add(Key(folder));
    }
}
