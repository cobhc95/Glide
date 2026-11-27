using System.Text;
using System.Text.RegularExpressions;

namespace Glide.Core.Renaming;

/// <summary>What the dialog reads from a file, once, to fill date and size tokens (or to sort). Any part may be unknown.</summary>
public sealed record RenameFileFacts(DateTime? Taken, DateTime? Modified, int Width, int Height)
{
    public static RenameFileFacts Empty { get; } = new(null, null, 0, 0);
}

/// <summary>
/// The rebuilt batch rename dialog's engine: a <see cref="RenameRecipe"/> and the files in, a checked
/// <see cref="BatchRenamePlan"/> out. Pure: dates and sizes come from <c>facts</c> (read elsewhere, off the UI thread), the disk is
/// only asked through <c>pathExists</c>. Items come in numbering order. An item whose template needs a date or size it doesn't have
/// is <em>skipped</em> (left as it is, not a problem). An item with a problem (illegal name, a name taken by a file outside the batch
/// or by another new name) keeps its old name in the plan, so the others can never run into a name it would still hold.
/// A template that is only old-style text (<c>holiday*</c>, <c>foo/bar</c>) is read by <see cref="BatchRenameExpression"/> as before.
/// </summary>
public static class RenamePlanner
{
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(500);

    private sealed record Computed(string NewName, IReadOnlyList<RenameSegment>? Segments, string? SkipReason, string? Failure);

    public static BatchRenamePlan Plan(IReadOnlyList<BatchRenameSource> sources, RenameRecipe recipe, Func<BatchRenameSource, RenameFileFacts?> facts, Func<string, bool> pathExists)
    {
        var r = recipe.Normalized();
        if (RenameTemplate.IsLegacyPattern(r.Template))
            return BatchRenamePlanner.Plan(sources, r.Template, r.UseRegex, r.MatchCase, pathExists);

        Regex? regex = null;
        if (r.Find.Length > 0 && r.UseRegex)
        {
            try { regex = new Regex(r.Find, RegexOptions.CultureInvariant | (r.MatchCase ? RegexOptions.None : RegexOptions.IgnoreCase), RegexTimeout); }
            catch (ArgumentException ex)
            {
                var items = sources.Select(s => new BatchRenameItem(s, BatchRenamePlanner.NameOf(s), BatchRenamePlanner.NameOf(s), BatchRenameProblem.None, null)).ToArray();
                return new BatchRenamePlan(items, "Not a valid regular expression: " + FirstLine(ex.Message));
            }
        }

        var ordered = Order(sources, r.Sort, facts);
        var pieces = RenameTemplate.Parse(r.Template);
        var computed = new Dictionary<BatchRenameSource, Computed>(ordered.Count);
        long counter = 0;
        foreach (var source in ordered)
        {
            var info = facts(source) ?? RenameFileFacts.Empty;
            var name = BatchRenamePlanner.NameOf(source);
            var missing = RenameTemplate.MissingReason(pieces, info);
            if (missing is not null)
            {
                computed[source] = new Computed(name, null, missing, null);
                continue;
            }
            try { computed[source] = Compute(source, name, pieces, r, regex, info, r.NumberStart + counter++); }
            catch (RegexMatchTimeoutException) { computed[source] = new Computed(name, null, null, "The regular expression took too long on \"" + name + "\"."); }
        }

        // Items with a problem hold their old name, then the plan is checked again: a new name that is a held item's old name is a
        // conflict too. Each round adds at least one held item, so it ends.
        var held = new HashSet<BatchRenameSource>();
        var flagged = new Dictionary<BatchRenameSource, BatchRenameItem>();
        BatchRenamePlan plan;
        while (true)
        {
            plan = BatchRenamePlanner.PlanNames(ordered, (s, _) => held.Contains(s) ? BatchRenamePlanner.NameOf(s) : computed[s].NewName, pathExists);
            var fresh = plan.Items.Where(i => i.HasProblem && !held.Contains(i.Source)).ToList();
            if (fresh.Count == 0) break;
            foreach (var item in fresh)
            {
                held.Add(item.Source);
                flagged[item.Source] = item;
            }
        }

        var result = new List<BatchRenameItem>(plan.Items.Count);
        foreach (var item in plan.Items)
        {
            var c = computed[item.Source];
            if (c.Failure is not null) result.Add(item with { Problem = BatchRenameProblem.ExpressionFailed, Message = c.Failure });
            else if (flagged.TryGetValue(item.Source, out var problem)) result.Add(problem);
            else result.Add(item with { Segments = item.Changed ? c.Segments : null, SkipReason = c.SkipReason });
        }
        return new BatchRenamePlan(result, null);
    }

    private static Computed Compute(BatchRenameSource source, string name, IReadOnlyList<RenamePiece> pieces, RenameRecipe r, Regex? regex, RenameFileFacts info, long number)
    {
        var (stem, extension) = BatchRenamePlanner.Split(name, source.IsDirectory);

        // Remove acts on the old name, so it shapes what the Old name piece gives.
        var old = stem;
        if (r.RemoveFirst > 0) old = old.Length > r.RemoveFirst ? old[r.RemoveFirst..] : "";
        if (r.RemoveLast > 0) old = old.Length > r.RemoveLast ? old[..^r.RemoveLast] : "";
        if (r.RemoveDigits) old = new string(old.Where(c => c is < '0' or > '9').ToArray());

        var folder = System.IO.Path.GetFileName(System.IO.Path.GetDirectoryName(source.Path) ?? "");
        if (folder.Length == 0) folder = FileNameRules.StripInvalid(System.IO.Path.GetDirectoryName(source.Path) ?? "");
        var (text, ranges) = RenameTemplate.Render(pieces, new RenameTemplate.Values(old, number, r.NumberDigits, info, folder, r.DateFormat));

        var tracked = new Tracked(text, ranges);
        if (r.Find.Length > 0) tracked.Edit(FindEdits(tracked.Text, r, regex));
        if (r.RemoveSpaces) tracked.Edit(SpaceEdits(tracked.Text));
        tracked.Text = RenameCaseRules.Apply(tracked.Text, r.Case);

        if (tracked.Text.Trim().Length == 0 && !source.IsDirectory) return new Computed("", null, null, null);
        var ext = r.Extension switch
        {
            RenameExtension.Lower => extension.ToLowerInvariant(),
            RenameExtension.Upper => extension.ToUpperInvariant(),
            _ => extension
        };
        var segments = tracked.Segments();
        if (ext.Length > 0) segments = [.. segments, new RenameSegment(ext, false)];
        return new Computed(tracked.Text + ext, segments, null, null);
    }

    private static List<(int Index, int Length, string With)> FindEdits(string text, RenameRecipe r, Regex? regex)
    {
        var edits = new List<(int, int, string)>();
        if (regex is not null)
        {
            foreach (Match m in regex.Matches(text))
                if (m.Length > 0 || m.Index < text.Length) edits.Add((m.Index, m.Length, m.Result(r.Replace)));
            return edits;
        }
        var comparison = r.MatchCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        for (var at = text.IndexOf(r.Find, comparison); at >= 0; at = at + r.Find.Length >= text.Length ? -1 : text.IndexOf(r.Find, at + r.Find.Length, comparison))
            edits.Add((at, r.Find.Length, r.Replace));
        return edits;
    }

    /// <summary>Runs of spaces become one space and spaces at either end go.</summary>
    private static List<(int Index, int Length, string With)> SpaceEdits(string text)
    {
        var edits = new List<(int, int, string)>();
        for (var i = 0; i < text.Length;)
        {
            if (text[i] != ' ') { i++; continue; }
            var end = i;
            while (end < text.Length && text[end] == ' ') end++;
            var atEdge = i == 0 || end == text.Length;
            if (atEdge) edits.Add((i, end - i, ""));
            else if (end - i > 1) edits.Add((i, end - i, " "));
            i = end;
        }
        return edits;
    }

    private static List<BatchRenameSource> Order(IReadOnlyList<BatchRenameSource> sources, RenameSort sort, Func<BatchRenameSource, RenameFileFacts?> facts)
    {
        if (sort == RenameSort.CurrentOrder) return sources.ToList();
        var natural = NaturalStringComparer.Instance;
        string Name(BatchRenameSource s) => BatchRenamePlanner.NameOf(s);
        DateTime Key(BatchRenameSource s)
        {
            var f = facts(s);
            return (sort == RenameSort.DateTaken ? f?.Taken ?? f?.Modified : f?.Modified) ?? DateTime.MinValue;
        }
        return sort == RenameSort.Name
            ? sources.OrderBy(Name, natural).ToList()
            : sources.OrderBy(Key).ThenBy(Name, natural).ToList();
    }

    private static string FirstLine(string message)
    {
        var line = message.Split('\n', 2)[0].Trim();
        return line.Length > 120 ? line[..120] : line;
    }

    /// <summary>A text with the character ranges that came from tokens; edits keep those ranges in step.</summary>
    private sealed class Tracked(string text, List<(int Start, int End)> tokens)
    {
        public string Text { get; set; } = text;

        /// <summary>Replaces non-overlapping ranges (ascending) and moves the token ranges with them.</summary>
        public void Edit(List<(int Index, int Length, string With)> edits)
        {
            if (edits.Count == 0) return;
            var sb = new StringBuilder(Text.Length);
            var newStarts = new int[edits.Count];
            var last = 0;
            for (var i = 0; i < edits.Count; i++)
            {
                var (index, length, with) = edits[i];
                sb.Append(Text, last, index - last);
                newStarts[i] = sb.Length;
                sb.Append(with);
                last = index + length;
            }
            sb.Append(Text, last, Text.Length - last);

            int Map(int pos, bool isEnd)
            {
                var delta = 0;
                for (var i = 0; i < edits.Count; i++)
                {
                    var (index, length, with) = edits[i];
                    if (index + length <= pos) { delta += with.Length - length; continue; }
                    if (index < pos) return isEnd ? newStarts[i] + with.Length : newStarts[i];   // the edit straddles this edge
                    break;
                }
                return pos + delta;
            }
            for (var i = 0; i < tokens.Count; i++) tokens[i] = (Map(tokens[i].Start, false), Map(tokens[i].End, true));
            Text = sb.ToString();
        }

        public List<RenameSegment> Segments()
        {
            var segments = new List<RenameSegment>();
            var at = 0;
            foreach (var (start, end) in tokens)
            {
                var s = Math.Clamp(start, at, Text.Length);
                var e = Math.Clamp(end, s, Text.Length);
                if (s > at) segments.Add(new RenameSegment(Text[at..s], false));
                if (e > s) segments.Add(new RenameSegment(Text[s..e], true));
                at = e;
            }
            if (at < Text.Length) segments.Add(new RenameSegment(Text[at..], false));
            return segments;
        }
    }
}

/// <summary>The "Capitals" box: how the letters of a name are cased. Every mapping keeps the length of the text.</summary>
public static class RenameCaseRules
{
    public static string Apply(string text, RenameCase mode)
    {
        if (string.IsNullOrEmpty(text) || mode == RenameCase.Keep) return text;
        switch (mode)
        {
            case RenameCase.Lower: return text.ToLowerInvariant();
            case RenameCase.Upper: return text.ToUpperInvariant();
        }
        var chars = text.ToLowerInvariant().ToCharArray();
        if (mode == RenameCase.Sentence)
        {
            for (var i = 0; i < chars.Length; i++)
                if (char.IsLetter(chars[i])) { chars[i] = char.ToUpperInvariant(chars[i]); break; }
            return new string(chars);
        }
        // Title Case: a letter that starts a word. Letters and digits (and an apostrophe) continue a word, so "4k" and "don't" stay as they are.
        for (var i = 0; i < chars.Length; i++)
        {
            if (!char.IsLetter(chars[i])) continue;
            var starts = i == 0 || !(char.IsLetterOrDigit(chars[i - 1]) || chars[i - 1] == '\'');
            if (starts) chars[i] = char.ToUpperInvariant(chars[i]);
        }
        return new string(chars);
    }
}
