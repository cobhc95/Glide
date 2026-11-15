using Glide.Core;

namespace Glide.App.Explorer;

internal enum ExplorerSortKey { Name, Modified, Type, Size }

/// <summary>The six Explorer-tab views. Index order is the order of the view combo box.</summary>
internal static class ExplorerViewModes
{
    public const int ExtraLarge = 0, Large = 1, Medium = 2, Small = 3, List = 4, Details = 5;

    public static readonly string[] Names =
    {
        "Extra large icons", "Large icons", "Medium icons", "Small icons", "List", "Details"
    };

    public static int IndexOf(string? name)
    {
        for (var i = 0; i < Names.Length; i++)
            if (string.Equals(Names[i], name, StringComparison.OrdinalIgnoreCase)) return i;
        return Large;
    }

    /// <summary>Tiles with a thumbnail area and a caption underneath.</summary>
    public static bool IsIconMode(int mode) => mode is ExtraLarge or Large or Medium;

    /// <summary>Tile footprint (width, height) in device-independent pixels.</summary>
    public static (double Width, double Height) TileSize(int mode) => mode switch
    {
        ExtraLarge => (260, 252),
        Large => (176, 188),
        Medium => (126, 146),
        Small => (250, 40),
        List => (240, 28),
        _ => (double.NaN, 28)
    };

    /// <summary>Thumbnail preview box (width, height) for icon modes; Small uses a tiny square.</summary>
    public static (int Width, int Height) PreviewSize(int mode) => mode switch
    {
        ExtraLarge => (236, 200),
        Large => (152, 136),
        Medium => (104, 98),
        Small => (30, 30),
        _ => (0, 0)
    };
}

internal sealed class ExplorerEntry
{
    public ExplorerEntry(string path, bool isDirectory, long size, DateTime modified)
    {
        Path = path;
        IsDirectory = isDirectory;
        Size = size;
        Modified = modified;
        Name = System.IO.Path.GetFileName(path);
        if (string.IsNullOrEmpty(Name)) Name = path;
        Extension = isDirectory ? "" : System.IO.Path.GetExtension(path);
        IsImage = !isDirectory && ImageNavigator.IsSupported(path);
        TypeName = isDirectory ? "File folder"
            : Extension.Length == 0 ? "File"
            : IsImage ? $"{Extension.TrimStart('.').ToUpperInvariant()} image"
            : $"{Extension.TrimStart('.').ToUpperInvariant()} file";
    }

    public string Path { get; }
    public string Name { get; }
    public bool IsDirectory { get; }
    public long Size { get; }
    public DateTime Modified { get; }
    public string Extension { get; }
    public bool IsImage { get; }
    public string TypeName { get; }

    public override string ToString() => Path;
}

internal static class ExplorerModel
{
    /// <summary>
    /// Lists a folder. Hidden/system entries are skipped unless asked for; when only images are
    /// wanted, non-image files are skipped (folders always stay so the user can still navigate).
    /// </summary>
    public static List<ExplorerEntry> Enumerate(string folder, bool showHidden, bool onlyImages, CancellationToken token)
    {
        var result = new List<ExplorerEntry>();
        var info = new DirectoryInfo(folder);
        foreach (var item in info.EnumerateFileSystemInfos())
        {
            token.ThrowIfCancellationRequested();
            try
            {
                var attributes = item.Attributes;
                if (!showHidden && (attributes & (FileAttributes.Hidden | FileAttributes.System)) != 0) continue;
                var directory = (attributes & FileAttributes.Directory) != 0;
                if (directory)
                {
                    result.Add(new ExplorerEntry(item.FullName, true, 0, item.LastWriteTime));
                    continue;
                }
                var entry = new ExplorerEntry(item.FullName, false, item is FileInfo file ? file.Length : 0, item.LastWriteTime);
                if (onlyImages && !entry.IsImage) continue;
                result.Add(entry);
            }
            catch (IOException) { /* an inaccessible entry is skipped in the listing */ }
            catch (UnauthorizedAccessException) { /* an inaccessible entry is skipped in the listing */ }
        }
        return result;
    }

    public static List<ExplorerEntry> Sort(IEnumerable<ExplorerEntry> entries, ExplorerSortKey key, bool ascending, bool foldersFirst)
    {
        var list = entries.ToList();
        var natural = NaturalStringComparer.Instance;
        int ByName(ExplorerEntry a, ExplorerEntry b) => natural.Compare(a.Name, b.Name);
        Comparison<ExplorerEntry> core = key switch
        {
            ExplorerSortKey.Modified => (a, b) => { var c = a.Modified.CompareTo(b.Modified); return c != 0 ? c : ByName(a, b); },
            ExplorerSortKey.Size => (a, b) => { var c = a.Size.CompareTo(b.Size); return c != 0 ? c : ByName(a, b); },
            ExplorerSortKey.Type => (a, b) => { var c = string.Compare(a.TypeName, b.TypeName, StringComparison.OrdinalIgnoreCase); return c != 0 ? c : ByName(a, b); },
            _ => ByName
        };
        list.Sort((a, b) =>
        {
            if (foldersFirst && a.IsDirectory != b.IsDirectory) return a.IsDirectory ? -1 : 1;
            // Folders have no size: keep them in name order for the Size column regardless of direction.
            if (key == ExplorerSortKey.Size && a.IsDirectory && b.IsDirectory) return ByName(a, b);
            var c = core(a, b);
            return ascending ? c : -c;
        });
        return list;
    }

    public static List<ExplorerEntry> Filter(IEnumerable<ExplorerEntry> entries, string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return entries.ToList();
        var needle = text.Trim();
        return entries.Where(e => e.Name.Contains(needle, StringComparison.OrdinalIgnoreCase)).ToList();
    }

    public static string FormatSize(long bytes)
    {
        if (bytes < 0) return "";
        if (bytes < 1024) return $"{bytes} B";
        var kb = (bytes + 1023) / 1024;
        if (kb < 1024) return $"{kb:N0} KB";
        var mb = bytes / 1024d / 1024d;
        if (mb < 1024) return $"{mb:0.#} MB";
        return $"{mb / 1024d:0.##} GB";
    }

    public static string FormatDate(DateTime value) => value.ToString("g");
}
