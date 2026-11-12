using System.Collections.Concurrent;
using System.Runtime.InteropServices;

namespace Glide.Core;

/// <summary>What the next / previous order of a folder's images is based on.</summary>
public enum ImageSortKey
{
    /// <summary>File name in Windows Explorer's natural order (2 before 10, digits before letters by value). The default.</summary>
    Name,
    /// <summary>File name compared character by character (the order older Glide versions used).</summary>
    NameOrdinal,
    /// <summary>File extension, then natural name.</summary>
    Type,
    Size,
    DateModified,
    DateCreated
}

/// <summary>A sort key and direction for the images of a folder.</summary>
public readonly record struct ImageSortSpec(ImageSortKey Key, bool Descending)
{
    public static ImageSortSpec Default => new(ImageSortKey.Name, false);
}

/// <summary>
/// The single source of truth for the order in which Glide walks a folder's images. Every place that
/// lists or inserts images (navigation, windowed enumeration, folder watching, browser listings) uses
/// <see cref="CreateComparer"/> so they can never disagree with each other, and the default order is the
/// same natural name order Windows Explorer uses.
/// </summary>
public static class ImageSortOrder
{
    public const string NameDisplay = "Name (like Explorer)";
    public const string NameOrdinalDisplay = "Name (plain characters)";
    public const string TypeDisplay = "Type";
    public const string SizeDisplay = "Size";
    public const string DateModifiedDisplay = "Date modified";
    public const string DateCreatedDisplay = "Date created";

    /// <summary>Display names in the order they are offered in Settings.</summary>
    public static readonly string[] DisplayNames =
        [NameDisplay, NameOrdinalDisplay, TypeDisplay, SizeDisplay, DateModifiedDisplay, DateCreatedDisplay];

    private static ImageSortSpec _current = ImageSortSpec.Default;

    /// <summary>The order currently chosen in Settings (process-wide; set when settings load or change).</summary>
    public static ImageSortSpec Current
    {
        get => _current;
        set => _current = value;
    }

    public static ImageSortKey ParseKey(string? displayName) => displayName?.Trim() switch
    {
        NameOrdinalDisplay => ImageSortKey.NameOrdinal,
        TypeDisplay => ImageSortKey.Type,
        SizeDisplay => ImageSortKey.Size,
        DateModifiedDisplay => ImageSortKey.DateModified,
        DateCreatedDisplay => ImageSortKey.DateCreated,
        _ => ImageSortKey.Name
    };

    public static string ToDisplayName(ImageSortKey key) => key switch
    {
        ImageSortKey.NameOrdinal => NameOrdinalDisplay,
        ImageSortKey.Type => TypeDisplay,
        ImageSortKey.Size => SizeDisplay,
        ImageSortKey.DateModified => DateModifiedDisplay,
        ImageSortKey.DateCreated => DateCreatedDisplay,
        _ => NameDisplay
    };

    /// <summary>Returns a known display name, falling back to the default for unknown or empty values.</summary>
    public static string NormalizeDisplayName(string? value) => ToDisplayName(ParseKey(value));

    public static IComparer<string> CreateComparer(ImageSortSpec spec) => new PathComparer(spec);

    public static IComparer<string> CreateComparer() => CreateComparer(Current);

    /// <summary>Sorts the paths with the comparer, reading each file's size or dates only once.</summary>
    public static string[] Sort(IEnumerable<string> paths, IComparer<string> comparer)
    {
        var array = paths.ToArray();
        Array.Sort(array, comparer);
        return array;
    }

    /// <summary>Compares two full file paths for <see cref="ImageSortSpec"/>. Total: ties fall back to name, then path.</summary>
    private sealed class PathComparer : IComparer<string>
    {
        private readonly ImageSortSpec _spec;
        private readonly ConcurrentDictionary<string, (long Size, long Modified, long Created)> _meta = new(StringComparer.OrdinalIgnoreCase);

        public PathComparer(ImageSortSpec spec) => _spec = spec;

        public int Compare(string? x, string? y)
        {
            if (ReferenceEquals(x, y)) return 0;
            x ??= string.Empty;
            y ??= string.Empty;
            var result = CompareForward(x, y);
            return _spec.Descending ? -result : result;
        }

        private int CompareForward(string x, string y)
        {
            var nameX = Path.GetFileName(x);
            var nameY = Path.GetFileName(y);
            var primary = _spec.Key switch
            {
                ImageSortKey.NameOrdinal => StringComparer.OrdinalIgnoreCase.Compare(nameX, nameY),
                ImageSortKey.Type => StringComparer.OrdinalIgnoreCase.Compare(Path.GetExtension(nameX), Path.GetExtension(nameY)),
                ImageSortKey.Size => Meta(x).Size.CompareTo(Meta(y).Size),
                ImageSortKey.DateModified => Meta(x).Modified.CompareTo(Meta(y).Modified),
                ImageSortKey.DateCreated => Meta(x).Created.CompareTo(Meta(y).Created),
                _ => NaturalStringComparer.Instance.Compare(nameX, nameY)
            };
            if (primary != 0) return primary;
            var byName = _spec.Key == ImageSortKey.NameOrdinal
                ? 0
                : NaturalStringComparer.Instance.Compare(nameX, nameY);
            if (byName != 0) return byName;
            var ordinalName = StringComparer.OrdinalIgnoreCase.Compare(nameX, nameY);
            return ordinalName != 0 ? ordinalName : StringComparer.Ordinal.Compare(x, y);
        }

        private (long Size, long Modified, long Created) Meta(string path)
            => _meta.GetOrAdd(path, static p =>
            {
                try
                {
                    var info = new FileInfo(p);
                    return info.Exists ? (info.Length, info.LastWriteTimeUtc.Ticks, info.CreationTimeUtc.Ticks) : (0, 0, 0);
                }
                catch { return (0, 0, 0); }
            });
    }
}

/// <summary>
/// Windows Explorer's logical ("natural") string order: runs of digits compare by numeric value, so
/// <c>2</c> sorts before <c>10</c> and <c>3m90</c> before <c>03325</c>. Uses the shell comparer on Windows
/// and a deterministic equivalent elsewhere or if the shell call fails.
/// </summary>
public sealed class NaturalStringComparer : IComparer<string>
{
    public static NaturalStringComparer Instance { get; } = new();

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
