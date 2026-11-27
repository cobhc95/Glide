using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Glide.Core.Status;

namespace Glide.Core.Renaming;

/// <summary>The pieces the batch rename dialog's "New name" box can hold besides typed text.</summary>
public enum RenameToken { Name, Number, Taken, Modified, Folder, Size, Width, Height }

/// <summary>How a token is spelled in a template (<c>{key}</c>), and how the dialog names it on a chip and inside the box.</summary>
public sealed record RenameTokenInfo(RenameToken Token, string Key, string ChipLabel, string BoxLabel);

/// <summary>One piece of a template: typed text, or a token with an optional per-token format (<c>{taken:yyyyMMdd}</c>).</summary>
public readonly record struct RenamePiece(RenameToken? Token, string Text, string? Format = null)
{
    public bool IsToken => Token is not null;
    public static RenamePiece Literal(string text) => new(null, text);
    public static RenamePiece Of(RenameToken token, string? format = null) => new(token, "", format);
}

/// <summary>A part of a finished name: plain text, or text that came from a token (the dialog highlights those).</summary>
public sealed record RenameSegment(string Text, bool IsToken);

/// <summary>
/// The "New name" template of the batch rename dialog, kept as one string: typed text with <c>{token}</c> pieces.
/// It uses the same <c>{token}</c> spelling and date formats as <see cref="TextTemplate"/> (<c>{taken}</c>, <c>{modified}</c>,
/// <c>{folder}</c>, <c>{w}</c>, <c>{h}</c>, <c>{taken:yyyyMMdd}</c>), plus <c>{name}</c> (the old name without extension),
/// <c>{n}</c> (the running number) and <c>{size}</c> (<c>{w}x{h}</c>). A literal brace is written <c>{{</c> or <c>}}</c>, so a name
/// typed in the box can never be mistaken for a token. The default is <c>{name}</c>: every name stays as it is.
/// </summary>
public static class RenameTemplate
{
    public const string Default = "{name}";

    /// <summary>The chips of the "Add:" row, in order.</summary>
    public static readonly IReadOnlyList<RenameTokenInfo> Tokens =
    [
        new(RenameToken.Name, "name", "Old name", "Old name"),
        new(RenameToken.Number, "n", "Number", "Number"),
        new(RenameToken.Taken, "taken", "Date taken", "Date taken"),
        new(RenameToken.Modified, "modified", "Date modified", "Date modified"),
        new(RenameToken.Folder, "folder", "Folder name", "Folder name"),
        new(RenameToken.Size, "size", "Size (W×H)", "Size"),
    ];

    private static readonly RenameTokenInfo WidthInfo = new(RenameToken.Width, "w", "Width", "Width");
    private static readonly RenameTokenInfo HeightInfo = new(RenameToken.Height, "h", "Height", "Height");

    private static readonly Dictionary<string, RenameToken> ByKey = new(StringComparer.OrdinalIgnoreCase)
    {
        ["name"] = RenameToken.Name, ["stem"] = RenameToken.Name,
        ["n"] = RenameToken.Number, ["number"] = RenameToken.Number,
        ["taken"] = RenameToken.Taken, ["datetaken"] = RenameToken.Taken,
        ["modified"] = RenameToken.Modified, ["datemodified"] = RenameToken.Modified,
        ["folder"] = RenameToken.Folder,
        ["size"] = RenameToken.Size, ["res"] = RenameToken.Size,
        ["w"] = RenameToken.Width, ["width"] = RenameToken.Width,
        ["h"] = RenameToken.Height, ["height"] = RenameToken.Height,
    };

    private static readonly Regex TokenAt = new(@"\G\{([A-Za-z]+)(?::([^{}]*))?\}", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static RenameTokenInfo Info(RenameToken token) =>
        Tokens.FirstOrDefault(t => t.Token == token) ?? (token == RenameToken.Width ? WidthInfo : HeightInfo);

    /// <summary>Splits a template into typed text and tokens. Anything that is not a known token stays text, as typed.</summary>
    public static IReadOnlyList<RenamePiece> Parse(string? template)
    {
        var pieces = new List<RenamePiece>();
        var text = new StringBuilder();
        var s = template ?? "";
        void Flush()
        {
            if (text.Length == 0) return;
            pieces.Add(RenamePiece.Literal(text.ToString()));
            text.Clear();
        }
        for (var i = 0; i < s.Length;)
        {
            var c = s[i];
            if (c == '{')
            {
                if (i + 1 < s.Length && s[i + 1] == '{') { text.Append('{'); i += 2; continue; }
                var m = TokenAt.Match(s, i);
                if (m.Success && ByKey.TryGetValue(m.Groups[1].Value, out var token))
                {
                    Flush();
                    pieces.Add(RenamePiece.Of(token, m.Groups[2].Success ? m.Groups[2].Value : null));
                    i += m.Length;
                    continue;
                }
            }
            else if (c == '}' && i + 1 < s.Length && s[i + 1] == '}') { text.Append('}'); i += 2; continue; }
            text.Append(c);
            i++;
        }
        Flush();
        return pieces;
    }

    /// <summary>The template string for these pieces (typed braces are doubled).</summary>
    public static string Serialize(IEnumerable<RenamePiece> pieces)
    {
        var sb = new StringBuilder();
        foreach (var piece in pieces)
        {
            if (piece.IsToken)
            {
                sb.Append('{').Append(Info(piece.Token!.Value).Key);
                if (!string.IsNullOrEmpty(piece.Format)) sb.Append(':').Append(piece.Format);
                sb.Append('}');
            }
            else sb.Append(piece.Text.Replace("{", "{{", StringComparison.Ordinal).Replace("}", "}}", StringComparison.Ordinal));
        }
        return sb.ToString();
    }

    /// <summary>The template string for a token added by a chip (<c>{taken}</c>).</summary>
    public static string TokenText(RenameToken token) => Serialize([RenamePiece.Of(token)]);

    public static bool Uses(string? template, RenameToken token) => Parse(template).Any(p => p.Token == token);

    /// <summary>
    /// The hidden fallback: text with a <c>*</c> or <c>/</c> (both illegal in file names, so never part of a real name) and no
    /// token is read in the old single-field language (<c>holiday*</c>, <c>foo/bar</c>, <c>a/b|c/d</c>; see <see cref="BatchRenameExpression"/>).
    /// </summary>
    public static bool IsLegacyPattern(string? template) =>
        !string.IsNullOrEmpty(template) && (template.Contains('*') || template.Contains('/')) && !Parse(template).Any(p => p.IsToken);

    /// <summary>Why this item can't get a name from the template (no date taken ...), or null when everything the template uses is known.</summary>
    public static string? MissingReason(IReadOnlyList<RenamePiece> pieces, RenameFileFacts facts)
    {
        foreach (var piece in pieces)
        {
            switch (piece.Token)
            {
                case RenameToken.Taken when facts.Taken is null: return "no date taken";
                case RenameToken.Modified when facts.Modified is null: return "no date modified";
                case RenameToken.Size or RenameToken.Width or RenameToken.Height when facts.Width <= 0 || facts.Height <= 0: return "no picture size";
            }
        }
        return null;
    }

    /// <summary>What a template shows for one item.</summary>
    public sealed record Values(string OldName, long Number, int NumberDigits, RenameFileFacts Facts, string Folder, string DateFormat);

    /// <summary>The text of the template for one item and the character ranges (start, end exclusive) that came from tokens.</summary>
    public static (string Text, List<(int Start, int End)> TokenRanges) Render(IReadOnlyList<RenamePiece> pieces, Values values)
    {
        var sb = new StringBuilder();
        var ranges = new List<(int, int)>();
        foreach (var piece in pieces)
        {
            if (!piece.IsToken) { sb.Append(piece.Text); continue; }
            var start = sb.Length;
            sb.Append(TokenValue(piece, values));
            if (sb.Length > start) ranges.Add((start, sb.Length));
        }
        return (sb.ToString(), ranges);
    }

    private static string TokenValue(RenamePiece piece, Values v) => piece.Token switch
    {
        RenameToken.Name => v.OldName,
        RenameToken.Number => v.Number.ToString("D" + Math.Clamp(v.NumberDigits, 1, 9), CultureInfo.InvariantCulture),
        RenameToken.Taken => FormatDate(v.Facts.Taken, piece.Format ?? v.DateFormat),
        RenameToken.Modified => FormatDate(v.Facts.Modified, piece.Format ?? v.DateFormat),
        RenameToken.Folder => v.Folder,
        RenameToken.Size => $"{v.Facts.Width}x{v.Facts.Height}",
        RenameToken.Width => v.Facts.Width.ToString(CultureInfo.InvariantCulture),
        RenameToken.Height => v.Facts.Height.ToString(CultureInfo.InvariantCulture),
        _ => "",
    };

    /// <summary>
    /// A date in a .NET custom format (Excel-style YYYY / DD are accepted, like <see cref="TextTemplate.FormatDate"/>), always in
    /// the invariant culture so a name never depends on the calendar or language of the machine. A bad format uses <see cref="RenameRecipe.DefaultDateFormat"/>.
    /// </summary>
    public static string FormatDate(DateTime? date, string? format)
    {
        if (date is not { } d) return "";
        var f = TextTemplate.NormalizeDateFormat(string.IsNullOrWhiteSpace(format) ? RenameRecipe.DefaultDateFormat : format);
        try { return d.ToString(f, CultureInfo.InvariantCulture); }
        catch (FormatException) { return d.ToString(RenameRecipe.DefaultDateFormat, CultureInfo.InvariantCulture); }
    }
}
