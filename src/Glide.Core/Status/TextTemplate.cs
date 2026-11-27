using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Glide.Core.Status;

/// <summary>What a template can show about one file (the text overlay, or the info under an Explorer thumbnail).</summary>
public readonly record struct TemplateValues(
    string? Path,
    int Index = 0,
    int Count = 0,
    int PixelWidth = 0,
    int PixelHeight = 0,
    long FileSize = 0,
    int? ZoomPercent = null,
    DateTime? Modified = null,
    DateTime? Taken = null,
    bool IsFolder = false);

/// <summary>One piece of rendered template text: plain text or a separator (drawn in the separator colour when set).</summary>
public readonly record struct TemplateSegment(string Text, bool IsSeparator);

/// <summary>
/// Round 4 (B6, B7, C1): the one <c>{token}</c> template engine shared by the text overlay and the Explorer thumbnail info.
/// Short tokens are documented (<see cref="Tokens"/>); the older long names still work as silent aliases. Date tokens take an
/// optional format after a colon: <c>{modified:yyyy/MM/dd HH:mm}</c>, <c>{taken:ddd}</c> (.NET custom format; Excel-style
/// upper-case YYYY / DD / DDD are accepted). <c>{sep}</c> is the chosen separator.
/// </summary>
public static class TextTemplate
{
    public const string DefaultDateFormat = "yyyy-MM-dd HH:mm";

    /// <summary>The documented tokens and what they show (Settings help, in columns).</summary>
    public static readonly (string Token, string Meaning)[] Tokens =
    {
        ("{index}", "position in folder"), ("{total}", "pictures in folder"),
        ("{file}", "file name"), ("{stem}", "name only"),
        ("{ext}", "extension"), ("{format}", "format (JPG)"),
        ("{folder}", "folder name"), ("{folderpath}", "folder path"),
        ("{path}", "full path"), ("{w}", "width (px)"),
        ("{h}", "height (px)"), ("{res}", "width×height"),
        ("{MP}", "megapixels"), ("{filesize}", "size (2.4 MB)"),
        ("{filesizeb}", "size in bytes"), ("{zoom}", "zoom (100%)"),
        ("{modified}", "date modified"), ("{taken}", "date taken (EXIF)"),
        ("{sep}", "separator"),
    };

    /// <summary>Older names: they keep working but are not shown in the help.</summary>
    public static readonly IReadOnlyDictionary<string, string> Aliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["filename"] = "file", ["extension"] = "ext", ["width"] = "w", ["height"] = "h", ["dimensions"] = "res",
        ["megapixels"] = "mp", ["filesizebytes"] = "filesizeb", ["datemodified"] = "modified", ["datetaken"] = "taken"
    };

    /// <summary>Separator choices (Settings combo order); "None" leaves only the spacing.</summary>
    public static readonly string[] Separators = { "•", "▪", "|", "–", "/", "None" };
    public const string DefaultSeparator = "•";

    /// <summary>What <c>{sep}</c> becomes: the symbol with two spaces each side ("None": two spaces).</summary>
    public static string SeparatorText(string? choice) =>
        string.IsNullOrWhiteSpace(choice) || string.Equals(choice, "None", StringComparison.OrdinalIgnoreCase) ? "  " : $"  {choice.Trim()}  ";

    private static readonly Regex TokenPattern = new(@"\{([A-Za-z]+)(?::([^{}]*))?\}", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>True when <paramref name="template"/> uses <paramref name="token"/> (or one of its aliases), e.g. "taken".</summary>
    public static bool Uses(string? template, string token)
    {
        if (string.IsNullOrEmpty(template)) return false;
        foreach (Match m in TokenPattern.Matches(template))
            if (string.Equals(Canonical(m.Groups[1].Value), token, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    public static string Render(string? template, in TemplateValues values, string? separator = DefaultSeparator) =>
        string.Concat(Segments(template, values, separator).Select(s => s.Text));

    /// <summary>The rendered template split into text and separators (<c>{sep}</c>), for a separate separator colour.</summary>
    public static IReadOnlyList<TemplateSegment> Segments(string? template, in TemplateValues values, string? separator = DefaultSeparator)
    {
        var result = new List<TemplateSegment>();
        if (string.IsNullOrEmpty(template)) return result;
        var text = new StringBuilder();
        var last = 0;
        var v = values;
        // A "part" is the text between two {sep}. One whose tokens all rendered empty (a folder has no size or pixels) is dropped
        // whole, with the literal text around those tokens ("{w}x{h}" for a folder is nothing, not a lone "x").
        var hasSeparator = TokenPattern.Matches(template).Any(m => Canonical(m.Groups[1].Value) == "sep");
        var partTokens = 0;
        var partFilled = 0;
        void EndPart()
        {
            // Without any {sep} a single token keeps its literal text ("[{taken}]" with no date stays "[]").
            if (partTokens > 0 && partFilled == 0 && (hasSeparator || partTokens > 1)) text.Clear();
            if (text.Length > 0) result.Add(new(text.ToString(), false));
            text.Clear();
            partTokens = partFilled = 0;
        }
        foreach (Match m in TokenPattern.Matches(template))
        {
            text.Append(template, last, m.Index - last);
            last = m.Index + m.Length;
            var name = Canonical(m.Groups[1].Value);
            if (name == "sep")
            {
                EndPart();
                result.Add(new(SeparatorText(separator), true));
                continue;
            }
            var value = Value(name, m.Groups[2].Success ? m.Groups[2].Value : null, v);
            partTokens++;
            if (value is null || value.Length > 0) partFilled++;
            text.Append(value ?? m.Value);   // unknown tokens stay as typed
        }
        text.Append(template, last, template.Length - last);
        EndPart();
        return CollapseEmptyParts(result);
    }

    /// <summary>
    /// A part that rendered empty (a folder has no size or pixels) takes its separator with it: "{res}{sep}{filesize}" for a
    /// folder is nothing, not a lone "/"; "{a}{sep}{b}{sep}{c}" with an empty {b} reads "a sep c". A separator is kept only
    /// between two non-blank parts, so none is left at either end; blank text next to a dropped separator goes too.
    /// </summary>
    private static List<TemplateSegment> CollapseEmptyParts(List<TemplateSegment> segments)
    {
        var output = new List<TemplateSegment>(segments.Count);
        TemplateSegment? pending = null;
        var anyContent = false;
        foreach (var segment in segments)
        {
            if (segment.IsSeparator) { if (anyContent) pending = segment; continue; }
            if (string.IsNullOrWhiteSpace(segment.Text))
            {
                // Spacing after real content and not next to a separator stays (it is the template's own spacing).
                if (anyContent && pending is null) output.Add(segment);
                continue;
            }
            if (pending is { } separator) { output.Add(separator); pending = null; }
            output.Add(segment);
            anyContent = true;
        }
        return output;
    }

    private static string Canonical(string name)
    {
        var lower = name.ToLowerInvariant();
        return Aliases.TryGetValue(lower, out var canonical) ? canonical : lower;
    }

    private static string? Value(string name, string? format, in TemplateValues v)
    {
        var culture = CultureInfo.CurrentCulture;
        var path = v.Path ?? string.Empty;
        var empty = string.IsNullOrWhiteSpace(path);
        var w = Math.Max(0, v.PixelWidth);
        var h = Math.Max(0, v.PixelHeight);
        switch (name)
        {
            case "index": return v.Count > 0 ? (v.Index + 1).ToString(culture) : "0";
            case "total": return v.Count.ToString(culture);
            case "file": return empty ? string.Empty : System.IO.Path.GetFileName(path);
            case "stem": return empty ? string.Empty : System.IO.Path.GetFileNameWithoutExtension(path);
            case "ext": return empty || v.IsFolder ? string.Empty : System.IO.Path.GetExtension(path).TrimStart('.');
            case "format": return empty || v.IsFolder ? string.Empty : CodecCapabilityRegistry.GetLongestExtension(path).TrimStart('.').ToUpperInvariant();
            case "folder": return FolderName(path);
            case "folderpath": return ViewerStatusFormatter.FolderPathOf(path);
            case "path": return path;
            case "w": return w > 0 ? w.ToString(culture) : v.IsFolder ? string.Empty : "0";
            case "h": return h > 0 ? h.ToString(culture) : v.IsFolder ? string.Empty : "0";
            case "res": return w > 0 && h > 0 ? $"{w}×{h}" : string.Empty;
            case "mp": return w > 0 && h > 0 ? (w * (double)h / 1_000_000d).ToString("0.##", culture) : v.IsFolder ? string.Empty : "0";
            case "filesize": return v.IsFolder ? string.Empty : v.FileSize > 0 ? ViewerStatusFormatter.FormatFileSize(v.FileSize) : "0 B";
            case "filesizeb": return v.IsFolder ? string.Empty : Math.Max(0, v.FileSize).ToString(culture);
            case "zoom": return v.ZoomPercent is { } z ? $"{z}%" : string.Empty;
            case "modified": return FormatDate(v.Modified, format);
            case "taken": return FormatDate(v.Taken, format);
            default: return null;
        }
    }

    private static string FolderName(string path)
    {
        var folderPath = ViewerStatusFormatter.FolderPathOf(path);
        if (string.IsNullOrWhiteSpace(folderPath)) return string.Empty;
        var folder = System.IO.Path.GetFileName(folderPath.TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar));
        return string.IsNullOrWhiteSpace(folder) ? folderPath : folder;
    }

    /// <summary>A date in a .NET custom format; Excel-style YYYY / DD / DDD / DDDD are accepted. Bad formats fall back to the default.</summary>
    public static string FormatDate(DateTime? date, string? format)
    {
        if (date is not { } d) return string.Empty;
        var f = NormalizeDateFormat(format);
        try { return d.ToString(f, CultureInfo.CurrentCulture); }
        catch (FormatException) { return d.ToString(DefaultDateFormat, CultureInfo.CurrentCulture); }
    }

    /// <summary>Excel-style upper-case year and day letters become .NET's lower-case ones (Y and D mean nothing else there).</summary>
    public static string NormalizeDateFormat(string? format)
    {
        if (string.IsNullOrWhiteSpace(format)) return DefaultDateFormat;
        var sb = new StringBuilder(format.Length);
        var quoted = false;
        foreach (var c in format)
        {
            if (c is '\'' or '"') quoted = !quoted;
            sb.Append(!quoted && c is 'Y' or 'D' ? char.ToLowerInvariant(c) : c);
        }
        // A single custom letter ("d", "h") would be read as a standard format: prefix '%' to keep it custom.
        var s = sb.ToString();
        return s.Length == 1 ? "%" + s : s;
    }
}
