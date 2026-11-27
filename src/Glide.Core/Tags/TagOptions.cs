using System.Text.RegularExpressions;
using Glide.Core.Renaming;
using Glide.Core.Settings;

namespace Glide.Core.Tags;

/// <summary>An ordered list of tags. With <see cref="IncludeGroupTag"/> the group's own name is added as a tag whenever one of its tags is added.</summary>
public sealed record TagGroup(string Name, bool IncludeGroupTag, IReadOnlyList<string> Tags);

/// <summary>
/// Everything that decides how tags are written into a file name: the groups (their order is the order tags appear in),
/// the prefix character, the brackets and separator of the tag block and the file name format.
/// Built from the settings with <see cref="From"/>; values that would make the names unreadable (empty brackets or separator,
/// a format without {name} and {tags}) fall back to the defaults.
/// </summary>
public sealed class TagOptions
{
    public const string DefaultFormat = "{name} {tags}";
    public const string DefaultOpen = "{";
    public const string DefaultClose = "}";
    public const string DefaultSeparator = ";";

    private Regex? _pattern;

    private TagOptions(IReadOnlyList<TagGroup> groups, string prefix, string open, string close, string separator, string format)
    {
        Groups = groups;
        Prefix = prefix;
        Open = open;
        Close = close;
        Separator = separator;
        Format = format;
    }

    public IReadOnlyList<TagGroup> Groups { get; }
    public string Prefix { get; }
    public string Open { get; }
    public string Close { get; }
    public string Separator { get; }
    public string Format { get; }

    public static TagOptions From(GlideSettingsState s) =>
        Create(s.TagGroups, s.TagPrefix, s.TagOpen, s.TagClose, s.TagSeparator, s.TagFilenameFormat);

    public static TagOptions Create(string? groupsText, string? prefix, string? open, string? close, string? separator, string? format)
    {
        var o = Clean(open, DefaultOpen);
        var c = Clean(close, DefaultClose);
        var sep = Clean(separator, DefaultSeparator);
        var p = FileNameRules.StripInvalid(prefix ?? "").Trim();
        var f = string.IsNullOrWhiteSpace(format) || CountOf(format, "{name}") != 1 || CountOf(format, "{tags}") != 1 || FileNameRules.StripInvalid(format) != format
            ? DefaultFormat : format;
        var groups = TagGroupCatalog.Parse(groupsText).Select(g => g with { Tags = g.Tags.Where(t => IsUsableTag(t, p, o, c, sep)).ToArray() })
            .Where(g => g.Name.Length > 0 && IsUsableTag(g.Name, p, o, c, sep)).ToArray();
        return new TagOptions(groups, p, o, c, sep, f);
    }

    /// <summary>A tag can't contain what separates or brackets tags, or a character a file name can't have.</summary>
    private static bool IsUsableTag(string tag, string prefix, string open, string close, string separator) =>
        tag.Length > 0 && tag == FileNameRules.StripInvalid(tag) && !tag.Contains(separator, StringComparison.Ordinal)
        && !tag.Contains(open, StringComparison.Ordinal) && !tag.Contains(close, StringComparison.Ordinal) && tag.Trim() == tag;

    public bool IsUsableTag(string tag) => IsUsableTag(tag, Prefix, Open, Close, Separator);

    private static string Clean(string? value, string fallback)
    {
        var stripped = FileNameRules.StripInvalid(value ?? "");
        return stripped.Length == 0 || stripped.Trim().Length == 0 ? fallback : stripped;
    }

    private static int CountOf(string text, string token)
    {
        var count = 0;
        for (var i = text.IndexOf(token, StringComparison.Ordinal); i >= 0; i = text.IndexOf(token, i + token.Length, StringComparison.Ordinal)) count++;
        return count;
    }

    /// <summary>
    /// The regular expression that reads "{name}{tags}" back out of a file name without extension, built from the format:
    /// literal text is escaped, {name} is lazy and {tags} is Open, anything but Close, Close.
    /// </summary>
    internal Regex Pattern => _pattern ??= BuildPattern();

    private Regex BuildPattern()
    {
        var nameAt = Format.IndexOf("{name}", StringComparison.Ordinal);
        var tagsAt = Format.IndexOf("{tags}", StringComparison.Ordinal);
        var tagsBlock = "(?:" + Regex.Escape(Open) + ")(?<tags>(?:(?!" + Regex.Escape(Close) + ").)*)(?:" + Regex.Escape(Close) + ")";
        const string name = "(?<name>.*?)";
        string pattern;
        if (nameAt < tagsAt)
            pattern = "^" + Regex.Escape(Format[..nameAt]) + name + Regex.Escape(Format[(nameAt + 6)..tagsAt]) + tagsBlock + Regex.Escape(Format[(tagsAt + 6)..]) + "$";
        else
            pattern = "^" + Regex.Escape(Format[..tagsAt]) + tagsBlock + Regex.Escape(Format[(tagsAt + 6)..nameAt]) + name + Regex.Escape(Format[(nameAt + 6)..]) + "$";
        return new Regex(pattern, RegexOptions.CultureInvariant | RegexOptions.Singleline, TimeSpan.FromMilliseconds(250));
    }
}

/// <summary>
/// Reads and writes the tag groups of the settings. One group per line: <c>Family: parents, mom, dad</c>; a leading
/// <c>+</c> turns on "include the group tag" for that group. The order of the lines and of the tags is the order tags are
/// written in a file name.
/// </summary>
public static class TagGroupCatalog
{
    public static IReadOnlyList<TagGroup> Parse(string? text)
    {
        var groups = new List<TagGroup>();
        if (string.IsNullOrWhiteSpace(text)) return groups;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;
            var include = line[0] == '+';
            if (include) line = line[1..].TrimStart();
            var colon = line.IndexOf(':');
            var name = (colon < 0 ? line : line[..colon]).Trim();
            if (name.Length == 0) continue;
            var tags = new List<string>();
            if (colon >= 0)
                foreach (var part in line[(colon + 1)..].Split(','))
                {
                    var tag = part.Trim();
                    if (tag.Length > 0 && !tags.Contains(tag, StringComparer.OrdinalIgnoreCase)) tags.Add(tag);
                }
            if (groups.Any(g => string.Equals(g.Name, name, StringComparison.OrdinalIgnoreCase))) continue;
            groups.Add(new TagGroup(name, include, tags));
        }
        return groups;
    }

    public static string Serialize(IEnumerable<TagGroup> groups) =>
        string.Join("\n", groups.Select(g => (g.IncludeGroupTag ? "+" : "") + g.Name + ": " + string.Join(", ", g.Tags)));
}
