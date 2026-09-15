namespace Glide.App.Services;

public sealed record SiblingFolderNavigationOptions(
    bool IncludeHidden,
    bool Wrap,
    bool SkipEmpty,
    bool OpenFirstImage,
    bool HierarchicalTraversal = true);

public sealed record SiblingFolderNavigationResult(
    string Folder,
    string[] Images,
    string SelectedPath,
    bool CrossedAncestorBoundary = false,
    string? BoundaryFrom = null,
    string? BoundaryTo = null);

/// <summary>
/// Canonical picture-folder traversal. Direct sibling navigation remains the fast path. When enabled,
/// exhaustion of the current sibling set advances through the directory tree in a reversible lexical
/// depth-first order: Next ascends until a following branch exists; Previous enters the deepest trailing
/// branch of the previous sibling. Hidden/inaccessible/reparse directories are skipped safely.
/// </summary>
public static class SiblingFolderNavigationService
{
    private const int MaxTraversalSteps = 4096;

    public static Task<SiblingFolderNavigationResult?> FindAsync(
        string currentPath,
        int direction,
        SiblingFolderNavigationOptions options,
        bool forceFirstImage,
        Func<string, bool> isSupported,
        CancellationToken cancellationToken = default)
        => Task.Run(() => Find(currentPath, direction, options, forceFirstImage, isSupported, cancellationToken), cancellationToken);

    internal static SiblingFolderNavigationResult? Find(
        string currentPath,
        int direction,
        SiblingFolderNavigationOptions options,
        bool forceFirstImage,
        Func<string, bool> isSupported,
        CancellationToken cancellationToken)
    {
        if (direction == 0) return null;
        var currentFolder = Path.GetDirectoryName(currentPath);
        if (string.IsNullOrWhiteSpace(currentFolder) || !Directory.Exists(currentFolder)) return null;
        currentFolder = SafeFullPath(currentFolder) ?? currentFolder;

        // Preserve the historical direct-sibling behavior first.
        var direct = FindDirectSibling(currentFolder, direction, options, forceFirstImage, isSupported, cancellationToken);
        if (direct is not null) return direct;
        if (!options.HierarchicalTraversal) return null;

        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { currentFolder };
        var cursor = currentFolder;
        for (var step = 0; step < MaxTraversalSteps; step++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var candidate = direction > 0
                ? NextTreeDirectory(cursor, currentFolder, options, visited)
                : PreviousTreeDirectory(cursor, currentFolder, options, visited);
            if (candidate is null) return null;
            cursor = candidate;
            if (!visited.Add(cursor)) continue;

            var images = EnumerateImages(cursor, isSupported);
            if (images is null) continue;
            if (images.Length == 0)
            {
                if (options.SkipEmpty) continue;
                return null;
            }

            // Hierarchical transitions are directional edges by definition: Next enters at the
            // first image; Previous returns to the last image, preserving exact reversal.
            var selected = direction > 0 ? images[0] : images[^1];
            return new SiblingFolderNavigationResult(
                cursor, images, selected, true,
                Path.GetDirectoryName(currentFolder), Path.GetDirectoryName(cursor));
        }
        return null;
    }

    private static SiblingFolderNavigationResult? FindDirectSibling(
        string currentFolder, int direction, SiblingFolderNavigationOptions options, bool forceFirstImage,
        Func<string, bool> isSupported, CancellationToken token)
    {
        var parent = Directory.GetParent(currentFolder)?.FullName;
        if (parent is null || !Directory.Exists(parent)) return null;
        var siblings = EnumerateChildDirectories(parent, options, includePath: currentFolder);
        var index = Array.FindIndex(siblings, path => SamePath(path, currentFolder));
        if (index < 0) return null;

        for (var step = 1; step < siblings.Length; step++)
        {
            token.ThrowIfCancellationRequested();
            var next = index + direction * step;
            if (options.Wrap)
            {
                next %= siblings.Length;
                if (next < 0) next += siblings.Length;
            }
            else if (next < 0 || next >= siblings.Length) break;

            var candidateFolder = direction < 0 && options.HierarchicalTraversal
                ? DeepestTrailingDescendant(siblings[next], options, new HashSet<string>(StringComparer.OrdinalIgnoreCase) { currentFolder })
                : siblings[next];
            var images = EnumerateImages(candidateFolder, isSupported);
            if (images is null) continue;
            if (images.Length == 0 && options.SkipEmpty)
            {
                // Hierarchical Previous may need to walk back out of an empty trailing descendant;
                // defer that to the canonical tree walker rather than returning the branch root.
                if (direction < 0 && options.HierarchicalTraversal) break;
                continue;
            }
            if (images.Length == 0) return null;
            var crossed = direction < 0 && options.HierarchicalTraversal && !SamePath(candidateFolder, siblings[next]);
            var selected = crossed ? images[^1] : (forceFirstImage || direction > 0 || options.OpenFirstImage ? images[0] : images[^1]);
            return new SiblingFolderNavigationResult(candidateFolder, images, selected, crossed,
                crossed ? parent : null, crossed ? Path.GetDirectoryName(candidateFolder) : null);
        }
        return null;
    }

    // Next after sibling exhaustion: ascend until a next sibling exists; then visit that branch root.
    private static string? NextTreeDirectory(string cursor, string origin, SiblingFolderNavigationOptions options, HashSet<string> visited)
    {
        // Once a new branch root has been entered, visit its children before later siblings.
        if (!SamePath(cursor, origin))
        {
            var children = EnumerateChildDirectories(cursor, options, includePath: null).Where(p => !visited.Contains(p)).ToArray();
            if (children.Length > 0) return children[0];
        }
        var node = cursor;
        while (true)
        {
            var parent = Directory.GetParent(node)?.FullName;
            if (parent is null || SamePath(parent, node)) return null;
            var siblings = EnumerateChildDirectories(parent, options, includePath: node);
            var index = Array.FindIndex(siblings, p => SamePath(p, node));
            if (index >= 0 && index + 1 < siblings.Length) return siblings[index + 1];
            node = parent;
            if (visited.Contains(node) && !SamePath(node, origin)) return null;
        }
    }

    // Previous after sibling exhaustion: previous sibling's deepest trailing descendant is the exact
    // reverse of Next's branch-root transition. If no previous sibling exists, ascend one level.
    private static string? PreviousTreeDirectory(string cursor, string origin, SiblingFolderNavigationOptions options, HashSet<string> visited)
    {
        var node = cursor;
        while (true)
        {
            var parent = Directory.GetParent(node)?.FullName;
            if (parent is null || SamePath(parent, node)) return null;
            var siblings = EnumerateChildDirectories(parent, options, includePath: node);
            var index = Array.FindIndex(siblings, p => SamePath(p, node));
            if (index > 0) return DeepestTrailingDescendant(siblings[index - 1], options, visited);
            node = parent;
            if (visited.Contains(node) && !SamePath(node, origin)) return null;
        }
    }

    private static string DeepestTrailingDescendant(string folder, SiblingFolderNavigationOptions options, HashSet<string> visited)
    {
        var cursor = folder;
        for (var i = 0; i < 256; i++)
        {
            var children = EnumerateChildDirectories(cursor, options, includePath: null)
                .Where(p => !visited.Contains(p)).ToArray();
            if (children.Length == 0) break;
            cursor = children[^1];
        }
        return cursor;
    }

    private static string[] EnumerateChildDirectories(string parent, SiblingFolderNavigationOptions options, string? includePath)
    {
        try
        {
            return Directory.EnumerateDirectories(parent)
                .Select(path => SafeFullPath(path) ?? path)
                .Where(path => !IsReparseDirectory(path))
                .Where(path => options.IncludeHidden || (includePath is not null && SamePath(path, includePath)) || !IsHiddenDirectory(path))
                .OrderBy(path => Path.GetFileName(path), StringComparer.OrdinalIgnoreCase)
                .ThenBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        catch { return Array.Empty<string>(); }
    }

    private static string[]? EnumerateImages(string folder, Func<string, bool> isSupported)
    {
        try
        {
            return Directory.EnumerateFiles(folder)
                .Where(isSupported)
                .OrderBy(path => Path.GetFileName(path), StringComparer.OrdinalIgnoreCase)
                .ThenBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        catch { return null; }
    }

    private static bool SamePath(string a, string b) =>
        string.Equals(SafeFullPath(a)?.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            SafeFullPath(b)?.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), StringComparison.OrdinalIgnoreCase);

    private static string? SafeFullPath(string path) { try { return Path.GetFullPath(path); } catch { return null; } }

    private static bool IsReparseDirectory(string path)
    {
        try { return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0; }
        catch { return true; }
    }

    private static bool IsHiddenDirectory(string path)
    {
        try
        {
            var name = Path.GetFileName(path);
            if (!string.IsNullOrEmpty(name) && name.StartsWith(".", StringComparison.Ordinal)) return true;
            return (File.GetAttributes(path) & FileAttributes.Hidden) != 0;
        }
        catch { return false; }
    }
}
