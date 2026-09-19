using System.Runtime.InteropServices;

namespace Glide.App.Services;

public sealed record SiblingFolderNavigationOptions(
    bool IncludeHidden,
    bool Wrap,
    bool SkipEmpty,
    bool OpenFirstImage,
    bool HierarchicalTraversal = true,
    string FolderOrder = "Alphabetical",
    bool HierarchicalPreviousOpenFirstImage = true);

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
/// branch of the previous sibling. Hidden directories are optional; inaccessible folders fail locally, and reparse siblings are included without recursive link traversal.
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
            var selected = direction > 0 ? images[0] : (options.HierarchicalPreviousOpenFirstImage ? images[0] : images[^1]);
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
        if (parent is null) return null;

        // One canonical sibling snapshot is used for the entire move. Never recurse into an
        // adjacent sibling while deciding what the "next sibling" is: that made traversal
        // direction-dependent and caused folders to disappear depending on the starting point.
        var siblings = EnumerateChildDirectories(parent, options, includePath: currentFolder);
        var index = Array.FindIndex(siblings, path => SamePath(path, currentFolder));
        if (index < 0) return null;

        var sign = Math.Sign(direction);
        for (var step = 1; step < siblings.Length; step++)
        {
            token.ThrowIfCancellationRequested();

            var candidateIndex = index + sign * step;
            if (options.Wrap)
            {
                candidateIndex %= siblings.Length;
                if (candidateIndex < 0) candidateIndex += siblings.Length;
            }
            else if (candidateIndex < 0 || candidateIndex >= siblings.Length)
            {
                break;
            }

            var candidateFolder = siblings[candidateIndex];
            var images = EnumerateImages(candidateFolder, isSupported);
            if (images is null)
            {
                // An inaccessible folder is a local failure only. Continue to the next sibling;
                // never abandon the rest of the parent's sibling list.
                continue;
            }

            if (images.Length == 0)
            {
                if (options.HierarchicalTraversal && EnumerateChildDirectories(candidateFolder, options, includePath: null).Length > 0)
                    return null;
                if (options.SkipEmpty) continue;
                return null;
            }

            var selected = forceFirstImage || options.OpenFirstImage
                ? images[0]
                : images[^1];

            return new SiblingFolderNavigationResult(
                candidateFolder, images, selected, false,
                direction > 0 ? "direct-next-sibling" : "direct-previous-sibling");
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

    private static string? FindFirstImageFolderInBranch(
        string root, SiblingFolderNavigationOptions options, Func<string, bool> isSupported, CancellationToken token)
    {
        var pending = new Stack<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        pending.Push(root);
        var steps = 0;
        while (pending.Count > 0 && steps++ < MaxTraversalSteps)
        {
            token.ThrowIfCancellationRequested();
            var folder = pending.Pop();
            var normalized = SafeFullPath(folder) ?? folder;
            if (!seen.Add(normalized)) continue;

            var images = EnumerateImages(folder, isSupported);
            if (images is { Length: > 0 }) return folder;

            // Include linked/reparse directories as sibling candidates, but do not recurse through
            // them. This preserves legitimate linked folders without risking junction cycles.
            if (IsReparseDirectory(folder)) continue;

            var children = EnumerateChildDirectories(folder, options, includePath: null);
            for (var i = children.Length - 1; i >= 0; i--)
                pending.Push(children[i]);
        }
        return null;
    }

    private static string? FindLastImageFolderInBranch(
        string root, SiblingFolderNavigationOptions options, Func<string, bool> isSupported, CancellationToken token)
    {
        string? last = null;
        var steps = 0;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Visit(string folder)
        {
            if (steps++ >= MaxTraversalSteps) return;
            token.ThrowIfCancellationRequested();
            var normalized = SafeFullPath(folder) ?? folder;
            if (!seen.Add(normalized)) return;

            var images = EnumerateImages(folder, isSupported);
            if (images is { Length: > 0 }) last = folder;

            if (IsReparseDirectory(folder)) return;
            foreach (var child in EnumerateChildDirectories(folder, options, includePath: null))
                Visit(child);
        }

        Visit(root);
        return last;
    }

    private static string[] EnumerateChildDirectories(string parent, SiblingFolderNavigationOptions options, string? includePath)
    {
        try
        {
            var paths = Directory.EnumerateDirectories(parent)
                .Select(path => SafeFullPath(path) ?? path)
                .Where(path => options.IncludeHidden || (includePath is not null && SamePath(path, includePath)) || !IsHiddenDirectory(path))
                .ToArray();
            return OrderDirectories(paths, options.FolderOrder);
        }
        catch { return Array.Empty<string>(); }
    }

    private static string[] OrderDirectories(IEnumerable<string> paths, string? order)
    {
        var entries = paths.Select(path =>
        {
            try
            {
                var info = new DirectoryInfo(path);
                return (Path: path, Name: info.Name, Modified: info.LastWriteTimeUtc, Created: info.CreationTimeUtc);
            }
            catch
            {
                // Keep the candidate visible even when metadata is temporarily unavailable.
                return (Path: path, Name: Path.GetFileName(path), Modified: DateTime.MinValue, Created: DateTime.MinValue);
            }
        });

        return order switch
        {
            "Modified date (oldest first)" => entries
                .OrderBy(x => x.Modified)
                .ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
                .ThenBy(x => x.Path, StringComparer.OrdinalIgnoreCase)
                .Select(x => x.Path).ToArray(),
            "Creation date (oldest first)" => entries
                .OrderBy(x => x.Created)
                .ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
                .ThenBy(x => x.Path, StringComparer.OrdinalIgnoreCase)
                .Select(x => x.Path).ToArray(),
            _ => entries
                .OrderBy(x => x.Name, WindowsLogicalStringComparer.Instance)
                .ThenBy(x => x.Path, WindowsLogicalStringComparer.Instance)
                .Select(x => x.Path).ToArray()
        };
    }

    /// <summary>
    /// Matches Windows Explorer's logical/natural ordering on Windows (e.g. 2 before 10).
    /// A deterministic token-aware fallback is used outside Windows and if shell comparison fails.
    /// </summary>
    private sealed class WindowsLogicalStringComparer : IComparer<string>
    {
        public static WindowsLogicalStringComparer Instance { get; } = new();

        public int Compare(string? x, string? y)
        {
            x ??= string.Empty;
            y ??= string.Empty;
            if (ReferenceEquals(x, y)) return 0;

            if (OperatingSystem.IsWindows())
            {
                try
                {
                    var result = StrCmpLogicalW(x, y);
                    if (result != 0) return result;
                }
                catch { }
            }

            return NaturalCompare(x, y);
        }

        private static int NaturalCompare(string a, string b)
        {
            var ia = 0;
            var ib = 0;
            while (ia < a.Length && ib < b.Length)
            {
                var ca = a[ia];
                var cb = b[ib];
                if (char.IsDigit(ca) && char.IsDigit(cb))
                {
                    var sa = ia;
                    var sb = ib;
                    while (ia < a.Length && char.IsDigit(a[ia])) ia++;
                    while (ib < b.Length && char.IsDigit(b[ib])) ib++;

                    var za = sa;
                    var zb = sb;
                    while (za < ia && a[za] == '0') za++;
                    while (zb < ib && b[zb] == '0') zb++;

                    var lena = ia - za;
                    var lenb = ib - zb;
                    if (lena != lenb) return lena.CompareTo(lenb);

                    var numeric = string.Compare(a, za, b, zb, lena, StringComparison.Ordinal);
                    if (numeric != 0) return numeric;

                    var rawLenA = ia - sa;
                    var rawLenB = ib - sb;
                    if (rawLenA != rawLenB) return rawLenA.CompareTo(rawLenB);
                    continue;
                }

                var upperA = char.ToUpperInvariant(ca);
                var upperB = char.ToUpperInvariant(cb);
                if (upperA != upperB) return upperA.CompareTo(upperB);
                ia++;
                ib++;
            }

            if (ia != a.Length || ib != b.Length)
                return (a.Length - ia).CompareTo(b.Length - ib);

            return string.Compare(a, b, StringComparison.OrdinalIgnoreCase);
        }

        [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)]
        private static extern int StrCmpLogicalW(string psz1, string psz2);
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
