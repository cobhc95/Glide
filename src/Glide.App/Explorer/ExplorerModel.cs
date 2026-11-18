using Glide.Imaging;
using Glide.Core;

namespace Glide.App.Explorer;

internal enum ExplorerSortKey { Name, Modified, Type, Size, Width, Height, Pixels }

/// <summary>The sort fields in menu order with their labels: the Sort by menu, the toolbar Sort button and the Details headers.</summary>
internal static class ExplorerSortFields
{
    /// <summary>The main sort fields, in the order of the Sort by list and the Details columns.</summary>
    public static readonly (ExplorerSortKey Key, string Label)[] All =
    {
        (ExplorerSortKey.Name, "Name"), (ExplorerSortKey.Modified, "Date modified"), (ExplorerSortKey.Type, "Type"), (ExplorerSortKey.Size, "File size")
    };

    /// <summary>Image-dimension sort fields (the Sort by list's "Dimensions" submenu); they have no Details column.</summary>
    public static readonly (ExplorerSortKey Key, string Label)[] Dimensions =
    {
        (ExplorerSortKey.Width, "Width"), (ExplorerSortKey.Height, "Height"), (ExplorerSortKey.Pixels, "Total pixels")
    };

    public const string Ascending = "\u25B2", Descending = "\u25BC";

    /// <summary>The direction triangle: ▲ ascending, ▼ descending.</summary>
    public static string Triangle(bool ascending) => ascending ? Ascending : Descending;

    public static bool IsDimension(ExplorerSortKey key) => key is ExplorerSortKey.Width or ExplorerSortKey.Height or ExplorerSortKey.Pixels;

    public static string Label(ExplorerSortKey key) => All.Concat(Dimensions).First(f => f.Key == key).Label;

    /// <summary>Details column header text (the File size column keeps its short "Size" header).</summary>
    public static string ColumnLabel(ExplorerSortKey key) => key == ExplorerSortKey.Size ? "Size" : Label(key);

    /// <summary>The Sort button's tooltip: "Sort: Date modified, descending, folders first. Click to change."</summary>
    public static string Describe(ExplorerSortKey key, bool ascending, bool foldersFirst) =>
        $"Sort: {Label(key)}, {(ascending ? "ascending" : "descending")}{(foldersFirst ? ", folders first" : "")}. Click to change.";
}

/// <summary>The six Explorer-tab views. Index order is the order of the view combo box.</summary>
internal static class ExplorerViewModes
{
    public const int ExtraLarge = 0, Large = 1, Medium = 2, Small = 3, List = 4, Details = 5;

    /// <summary>Stored names (the "Explorer default view" setting value and saved profiles). Never shown in the toolbar.</summary>
    public static readonly string[] Names =
    {
        "Extra large thumbnails", "Large thumbnails", "Medium thumbnails", "Small thumbnails", "List", "Details"
    };

    /// <summary>Display labels (view box, View menu), short enough for the closed view box. Same order as <see cref="Names"/>.</summary>
    public static readonly string[] Labels =
    {
        "Thumbnails (XL)", "Thumbnails (L)", "Thumbnails (M)", "Thumbnails (S)", "List", "Details"
    };

    public static int IndexOf(string? name)
    {
        // Saved before 5.0.3 as "... icons".
        if (name is not null && name.EndsWith(" icons", StringComparison.OrdinalIgnoreCase)) name = name[..^" icons".Length] + " thumbnails";
        for (var i = 0; i < Names.Length; i++)
            if (string.Equals(Names[i], name, StringComparison.OrdinalIgnoreCase)) return i;
        return Large;
    }

    /// <summary>Tiles with a thumbnail area and a caption underneath.</summary>
    public static bool IsIconMode(int mode) => mode is ExtraLarge or Large or Medium;

    /// <summary>
    /// Tile footprint (width, height) in device-independent pixels. <paramref name="infoLines"/> is the number of extra
    /// info lines under thumbnails (round 3 #6, <see cref="ExplorerTileInfo"/>); only icon-mode tiles show them.
    /// </summary>
    public static (double Width, double Height) TileSize(int mode, int infoLines = 0) => mode switch
    {
        ExtraLarge => (IconTileWidth(ExtraLarge), IconTileHeight(ExtraLarge, infoLines)),
        Large => (IconTileWidth(Large), IconTileHeight(Large, infoLines)),
        Medium => (IconTileWidth(Medium), IconTileHeight(Medium, infoLines)),
        Small => (250, 40),
        List => (240, 28),
        _ => (double.NaN, 28)
    };

    /// <summary>Caption line height and line count of icon-mode tiles: the tile reserves room for every caption line.</summary>
    public const double CaptionLineHeight = 17;
    public const int CaptionLines = 2;
    /// <summary>Height of one extra info line under the caption (date / resolution / file size, round 3 #6).</summary>
    public const double InfoLineHeight = 15;
    /// <summary>Gap between the preview and the caption.</summary>
    public const double CaptionSpacing = 6;
    /// <summary>Tile padding (7 each side) plus border (1 each side) of icon-mode tiles.</summary>
    public const double IconTileChrome = 16;
    /// <summary>Width of the selection outline drawn on the edge of the selection fill.</summary>
    public const double SelectionOutlineThickness = 3;
    /// <summary>How far the selection fill/outline extend into the tile padding (icon tiles: padding 7, compact rows: 5,2).</summary>
    public static readonly Avalonia.Thickness IconOutlineReach = new(SelectionOutlineThickness);
    public static readonly Avalonia.Thickness CompactOutlineReach = new(SelectionOutlineThickness, 2);

    /// <summary>
    /// Icon-mode tile height: preview + gap + all caption lines + chrome. Before 5.0.7 the heights were fixed numbers
    /// that left room for only ~1.5 caption lines, so a wrapped second line spilled below the selection fill.
    /// </summary>
    public static double IconTileHeight(int mode, int infoLines = 0) =>
        IconTopInset(mode) + PreviewSize(mode).Height + CaptionSpacing + CaptionLineHeight * CaptionLines
        + InfoLineHeight * Math.Clamp(infoLines, 0, ExplorerTileInfo.MaxLines) + IconTileChrome;

    private static double IconTileWidth(int mode) => mode switch { ExtraLarge => 260, Large => 176, _ => 126 };

    /// <summary>
    /// Space above the thumbnail inside an icon tile: equal to the space on its left and right, so with the selection fill
    /// reaching the same distance out on every side, the brown band above the thumbnail is as thick as the side bands.
    /// </summary>
    public static double IconTopInset(int mode) => (IconTileWidth(mode) - IconTileChrome - PreviewSize(mode).Width) / 2;

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

/// <summary>
/// Round 3 #6: the optional extra info lines under Explorer thumbnails (off by default; each one chosen in Settings).
/// One line per chosen item, in this order: date modified, resolution (pixel size), file size. Every icon tile of the
/// view reserves the same lines (a folder's resolution and size lines stay empty) so the grid stays uniform and the
/// text always sits inside the selection fill.
/// </summary>
internal readonly record struct ExplorerTileInfo(bool Date, bool Resolution, bool FileSize)
{
    public const int MaxLines = 3;

    public int Lines => (Date ? 1 : 0) + (Resolution ? 1 : 0) + (FileSize ? 1 : 0);

    public static ExplorerTileInfo From(Glide.Core.Settings.GlideSettingsState settings) =>
        new(settings.ExplorerInfoDate, settings.ExplorerInfoResolution, settings.ExplorerInfoFileSize);

    /// <summary>The lines for <paramref name="entry"/>; <paramref name="pixels"/> is its pixel size when known.</summary>
    public IReadOnlyList<string> Text(ExplorerEntry entry, ImageDimensions? pixels)
    {
        var lines = new List<string>(Lines);
        if (Date) lines.Add(ExplorerModel.FormatDate(entry.Modified));
        if (Resolution) lines.Add(pixels is { IsValid: true } p ? $"{p.Width} \u00d7 {p.Height}" : "");
        if (FileSize) lines.Add(entry.IsDirectory ? "" : ExplorerModel.FormatSize(entry.Size));
        return lines;
    }
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

    private ImageDimensions? _dimensions;
    private bool _dimensionsProbed;

    /// <summary>The picture's pixel size from its header, read once on first use ((0,0) for folders and unreadable files).</summary>
    public ImageDimensions PixelDimensions
    {
        get
        {
            if (!_dimensionsProbed)
            {
                _dimensions = IsImage ? ExplorerModel.ProbePixelSize(Path) : null;
                _dimensionsProbed = true;
            }
            return _dimensions ?? default;
        }
    }

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
            ExplorerSortKey.Width => (a, b) => { var c = a.PixelDimensions.Width.CompareTo(b.PixelDimensions.Width); return c != 0 ? c : ByName(a, b); },
            ExplorerSortKey.Height => (a, b) => { var c = a.PixelDimensions.Height.CompareTo(b.PixelDimensions.Height); return c != 0 ? c : ByName(a, b); },
            ExplorerSortKey.Pixels => (a, b) => { var c = Pixels(a).CompareTo(Pixels(b)); return c != 0 ? c : ByName(a, b); },
            ExplorerSortKey.Type => (a, b) => { var c = string.Compare(a.TypeName, b.TypeName, StringComparison.OrdinalIgnoreCase); return c != 0 ? c : ByName(a, b); },
            _ => ByName
        };
        list.Sort((a, b) =>
        {
            if (foldersFirst && a.IsDirectory != b.IsDirectory) return a.IsDirectory ? -1 : 1;
            // Folders have no size: keep them in name order for the Size column regardless of direction.
            if ((key == ExplorerSortKey.Size || ExplorerSortFields.IsDimension(key)) && a.IsDirectory && b.IsDirectory) return ByName(a, b);
            var c = core(a, b);
            return ascending ? c : -c;
        });
        return list;
    }

    private static long Pixels(ExplorerEntry e) => (long)e.PixelDimensions.Width * e.PixelDimensions.Height;

    /// <summary>Background thread: reads the pixel size of every picture so a dimension sort never reads headers on the UI thread.</summary>
    public static void ProbeDimensions(IEnumerable<ExplorerEntry> entries, CancellationToken token)
    {
        foreach (var entry in entries)
        {
            token.ThrowIfCancellationRequested();
            _ = entry.PixelDimensions;
        }
    }

    /// <summary>A picture's pixel size from its header (built-in probe, header probe, then the Windows codecs); null when unknown.</summary>
    public static ImageDimensions? ProbePixelSize(string path)
    {
        try
        {
            if (Glide.Imaging.BuiltInRasterDecoder.TryProbeDimensions(path, out var raster) && raster.IsValid) return raster;
            if (Glide.Imaging.ImageHeaderProbe.TryProbe(path, out var header) && header.IsValid) return header;
            if (Glide.Imaging.NativeImageProbe.TryProbe(path, out var native) && native.Width > 0 && native.Height > 0)
                return new ImageDimensions((int)Math.Min(native.Width, (uint)int.MaxValue), (int)Math.Min(native.Height, (uint)int.MaxValue));
        }
        catch (IOException) { /* unreadable: unknown size */ }
        catch (UnauthorizedAccessException) { /* unreadable: unknown size */ }
        return null;
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

    public static string FormatDate(DateTime value) => value.ToString("g", System.Globalization.CultureInfo.CurrentCulture);
}

/// <summary>
/// The list shown by the Explorer ItemsRepeater. Observable so an in-place reload (round 3 #15) can remove and insert
/// single entries: the repeater then only adds / drops those tiles and shifts the rest, instead of re-creating every
/// tile (a dark flash and a thumbnail reload). Replacing the whole list is still how a different folder or a new
/// sort / view is shown.
/// </summary>
internal sealed class ExplorerEntryList : System.Collections.ObjectModel.ObservableCollection<ExplorerEntry>
{
    public ExplorerEntryList() { }
    public ExplorerEntryList(IEnumerable<ExplorerEntry> entries) : base(entries) { }

    public int FindIndex(Predicate<ExplorerEntry> match)
    {
        for (var i = 0; i < Count; i++)
            if (match(this[i])) return i;
        return -1;
    }

    /// <summary>Same entry as listed before: same name (case included), kind, size and date. A changed file is re-shown.</summary>
    public static bool SameListing(ExplorerEntry a, ExplorerEntry b) =>
        a.IsDirectory == b.IsDirectory && a.Size == b.Size && a.Modified == b.Modified && string.Equals(a.Name, b.Name, StringComparison.Ordinal);
}
