using System.Text;
using System.Text.RegularExpressions;

namespace Glide.Core.Renaming;

/// <summary>How the one input field of the batch rename dialog was read.</summary>
public enum BatchRenameMode
{
    /// <summary>Nothing typed: every name stays as it is.</summary>
    Empty,
    /// <summary>Plain text, where <c>*</c> stands for the current name without its extension.</summary>
    Template,
    /// <summary><c>find/replace</c> (several separated by <c>|</c>), plain text.</summary>
    Replace,
    /// <summary><c>find/replace</c> where <c>find</c> is a .NET regular expression and <c>replace</c> may use <c>$1</c>.</summary>
    Regex
}

/// <summary>
/// The single-field rename language of the batch rename dialog (GitHub issue #4). Parsed once, applied to every name.
/// Only the part before the extension is ever changed, so extensions are preserved.
/// <list type="bullet">
/// <item><c>foo*bar</c>: prefix / suffix. <c>*</c> is the current name without extension ("Filename.jpg" gives "fooFilenamebar.jpg"). Several <c>*</c> are allowed.</item>
/// <item><c>holiday</c> (no <c>*</c>): every name becomes that text; with more than one item " (1)", " (2)" ... is appended.</item>
/// <item><c>foo/bar</c>: replace the text foo with bar. <c>a/b|c/d</c> applies several replacements in order. <c>/</c> and <c>|</c> are illegal in file names, so they never need escaping. An empty replacement deletes the text.</item>
/// <item>Regular expression on: the same <c>find/replace</c> split at the first <c>/</c>, find is a .NET regex, <c>$1</c> and <c>${name}</c> work in replace. A regex without <c>/</c> deletes what it matches.</item>
/// </list>
/// Plain replacements and regexes ignore case unless <c>matchCase</c> is set.
/// </summary>
public sealed class BatchRenameExpression
{
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(500);

    private readonly string _template = "";
    private readonly (string Find, string Replace)[] _replacements = [];
    private readonly Regex? _regex;
    private readonly string _regexReplacement = "";
    private readonly StringComparison _comparison;

    private BatchRenameExpression(BatchRenameMode mode, string? error, StringComparison comparison, string template = "",
        (string Find, string Replace)[]? replacements = null, Regex? regex = null, string regexReplacement = "", bool needsCounter = false)
    {
        Mode = mode;
        Error = error;
        _comparison = comparison;
        _template = template;
        _replacements = replacements ?? [];
        _regex = regex;
        _regexReplacement = regexReplacement;
        NeedsCounter = needsCounter;
    }

    public BatchRenameMode Mode { get; }

    /// <summary>A user-facing reason the expression can't be used (an invalid regex, an empty "find"), or null.</summary>
    public string? Error { get; }

    public bool IsEmpty => Mode == BatchRenameMode.Empty;

    /// <summary>True when every item gets the same base text and so needs a " (n)" counter (a template without <c>*</c>).</summary>
    public bool NeedsCounter { get; }

    public static BatchRenameExpression Parse(string? text, bool regex = false, bool matchCase = false)
    {
        var comparison = matchCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        if (string.IsNullOrEmpty(text)) return new BatchRenameExpression(BatchRenameMode.Empty, null, comparison);

        if (regex)
        {
            var slash = text.IndexOf('/');
            var find = slash < 0 ? text : text[..slash];
            var replacement = slash < 0 ? "" : text[(slash + 1)..];
            if (find.Length == 0) return new BatchRenameExpression(BatchRenameMode.Regex, "Type the regular expression to find, then / and the replacement.", comparison);
            try
            {
                var options = RegexOptions.CultureInvariant | (matchCase ? RegexOptions.None : RegexOptions.IgnoreCase);
                return new BatchRenameExpression(BatchRenameMode.Regex, null, comparison, regex: new Regex(find, options, RegexTimeout), regexReplacement: replacement);
            }
            catch (ArgumentException ex)
            {
                return new BatchRenameExpression(BatchRenameMode.Regex, "Not a valid regular expression: " + FirstLine(ex.Message), comparison);
            }
        }

        if (text.Contains('/'))
        {
            var rules = new List<(string Find, string Replace)>();
            foreach (var part in text.Split('|'))
            {
                if (part.Length == 0) continue;
                var slash = part.IndexOf('/');
                if (slash < 0) return new BatchRenameExpression(BatchRenameMode.Replace, $"\"{part}\" has no / : write find/replace.", comparison);
                var find = part[..slash];
                var replacement = part[(slash + 1)..];
                if (find.Length == 0) return new BatchRenameExpression(BatchRenameMode.Replace, "Type the text to find before the /.", comparison);
                if (replacement.Contains('/')) return new BatchRenameExpression(BatchRenameMode.Replace, "Only one / per replacement (find/replace). Separate several with |.", comparison);
                rules.Add((find, replacement));
            }
            if (rules.Count == 0) return new BatchRenameExpression(BatchRenameMode.Empty, null, comparison);
            return new BatchRenameExpression(BatchRenameMode.Replace, null, comparison, replacements: rules.ToArray());
        }

        return new BatchRenameExpression(BatchRenameMode.Template, null, comparison, template: text, needsCounter: !text.Contains('*'));
    }

    /// <summary>
    /// The new name without extension for one item. <paramref name="index"/> is the item's 0-based position among
    /// <paramref name="count"/> items (it only matters for a template without <c>*</c>). Returns false with
    /// <paramref name="error"/> when the expression failed on this name (a regex that timed out).
    /// </summary>
    public bool TryApply(string stem, int index, int count, out string result, out string? error)
    {
        error = null;
        result = stem;
        if (Error is not null) { error = Error; return false; }
        switch (Mode)
        {
            case BatchRenameMode.Empty:
                return true;
            case BatchRenameMode.Template:
            {
                var text = _template.Replace("*", stem, StringComparison.Ordinal);
                result = NeedsCounter && count > 1 ? $"{text} ({index + 1})" : text;
                return true;
            }
            case BatchRenameMode.Replace:
            {
                var current = stem;
                foreach (var (find, replace) in _replacements) current = current.Replace(find, replace, _comparison);
                result = current;
                return true;
            }
            default:
                try { result = _regex!.Replace(stem, _regexReplacement); return true; }
                catch (RegexMatchTimeoutException) { error = "The regular expression took too long on \"" + stem + "\"."; return false; }
        }
    }

    private static string FirstLine(string message)
    {
        var line = message.Split('\n', 2)[0].Trim();
        return line.Length > 120 ? line[..120] : line;
    }

    /// <summary>One-screen help shown in the dialog.</summary>
    public static string HelpText => new StringBuilder()
        .Append("*  is the current name without extension:  foo*bar  turns Filename.jpg into fooFilenamebar.jpg\n")
        .Append("Text without *  names every file that text, with (1), (2) ... added\n")
        .Append("foo/bar  replaces foo with bar;  a/b|c/d  replaces several (the extension is never changed)\n")
        .Append("Regular expression:  (\\d+)/img_$1  (find, then /, then the replacement)")
        .ToString();
}
