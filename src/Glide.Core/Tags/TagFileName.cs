using System.Text.RegularExpressions;
using Glide.Core.Renaming;

namespace Glide.Core.Tags;

/// <summary>How many of the selected files carry a tag.</summary>
public enum TagPresence { None, Some, All }

/// <summary>A file name taken apart: the name, its tags (without the prefix, in file order) and the extension with its dot.</summary>
public sealed record TagParts(string BaseName, IReadOnlyList<string> Tags, string Extension);

/// <summary>
/// Adds, removes and reads tags in file names ("DCIM0078 {parents;#mom}.jpg"). Pure string work, no file system:
/// the caller renames. Tags always come out in the order of the configured groups (a tag added between two existing ones
/// re-sorts the block), are never written twice (compared ignoring case) and tags that are not in any group
/// are kept, after the known ones, in the order they had.
/// </summary>
public static class TagFileName
{
    public static TagParts Parse(string fileName, TagOptions options, bool isDirectory = false)
    {
        var (stem, extension) = BatchRenamePlanner.Split(fileName, isDirectory);
        Match match;
        try { match = options.Pattern.Match(stem); }
        catch (RegexMatchTimeoutException) { return new TagParts(stem, [], extension); }
        if (!match.Success) return new TagParts(stem, [], extension);

        var tags = new List<string>();
        foreach (var part in match.Groups["tags"].Value.Split(options.Separator))
        {
            var tag = part.Trim();
            if (options.Prefix.Length > 0 && tag.StartsWith(options.Prefix, StringComparison.Ordinal)) tag = tag[options.Prefix.Length..].Trim();
            if (tag.Length > 0 && !tags.Contains(tag, StringComparer.OrdinalIgnoreCase)) tags.Add(tag);
        }
        return new TagParts(match.Groups["name"].Value, tags, extension);
    }

    public static IReadOnlyList<string> GetTags(string fileName, TagOptions options, bool isDirectory = false) => Parse(fileName, options, isDirectory).Tags;

    public static bool HasTag(string fileName, string tag, TagOptions options, bool isDirectory = false) =>
        Parse(fileName, options, isDirectory).Tags.Contains(tag, StringComparer.OrdinalIgnoreCase);

    /// <summary>The file name with <paramref name="tags"/> written in the configured format (no tags: just name and extension).</summary>
    public static string Compose(TagParts parts, IEnumerable<string> tags, TagOptions options)
    {
        var sorted = Sort(tags, options);
        if (sorted.Count == 0) return parts.BaseName + parts.Extension;
        var block = options.Open + string.Join(options.Separator, sorted.Select(t => options.Prefix + t)) + options.Close;
        return options.Format.Replace("{name}", parts.BaseName, StringComparison.Ordinal).Replace("{tags}", block, StringComparison.Ordinal) + parts.Extension;
    }

    /// <summary>Adds <paramref name="tagsToAdd"/> (and their group tags where the group asks for it) and removes <paramref name="tagsToRemove"/> (and group tags left alone).</summary>
    public static string Edit(string fileName, IEnumerable<string> tagsToAdd, IEnumerable<string> tagsToRemove, TagOptions options, bool isDirectory = false)
    {
        var parts = Parse(fileName, options, isDirectory);
        var tags = new List<string>(parts.Tags);

        foreach (var tag in tagsToRemove)
        {
            tags.RemoveAll(t => string.Equals(t, tag, StringComparison.OrdinalIgnoreCase));
            if (GroupOf(tag, options) is { IncludeGroupTag: true } group && !group.Tags.Any(t => tags.Contains(t, StringComparer.OrdinalIgnoreCase)))
                tags.RemoveAll(t => string.Equals(t, group.Name, StringComparison.OrdinalIgnoreCase));
        }
        foreach (var tag in tagsToAdd)
        {
            AddOnce(tags, tag);
            if (GroupOf(tag, options) is { IncludeGroupTag: true } group) AddOnce(tags, group.Name);
        }

        // Nothing to add or remove (the tags were already there, or were never there): leave the name exactly as it is.
        var same = tags.Count == parts.Tags.Count && tags.All(t => parts.Tags.Contains(t, StringComparer.OrdinalIgnoreCase));
        return same ? fileName : Compose(parts, tags, options);
    }

    public static string AddTags(string fileName, IEnumerable<string> tags, TagOptions options, bool isDirectory = false) => Edit(fileName, tags, [], options, isDirectory);

    public static string RemoveTags(string fileName, IEnumerable<string> tags, TagOptions options, bool isDirectory = false) => Edit(fileName, [], tags, options, isDirectory);

    /// <summary>For the sidebar's checkbox: none, some or all of <paramref name="fileNames"/> carry <paramref name="tag"/>.</summary>
    public static TagPresence PresenceIn(IReadOnlyList<string> fileNames, string tag, TagOptions options)
    {
        if (fileNames.Count == 0) return TagPresence.None;
        var with = fileNames.Count(n => HasTag(n, tag, options));
        return with == 0 ? TagPresence.None : with == fileNames.Count ? TagPresence.All : TagPresence.Some;
    }

    /// <summary>The group (the first, if a tag is listed twice) that lists <paramref name="tag"/>; null for a tag nobody lists.</summary>
    public static TagGroup? GroupOf(string tag, TagOptions options) =>
        options.Groups.FirstOrDefault(g => g.Tags.Contains(tag, StringComparer.OrdinalIgnoreCase));

    /// <summary>Group order, then within a group its own tag first and then its tags in list order; unknown tags last in their old order.</summary>
    public static IReadOnlyList<string> Sort(IEnumerable<string> tags, TagOptions options)
    {
        var known = new List<(int Group, int Position, string Tag)>();
        var unknown = new List<string>();
        foreach (var tag in tags)
        {
            if (known.Any(k => string.Equals(k.Tag, tag, StringComparison.OrdinalIgnoreCase)) || unknown.Contains(tag, StringComparer.OrdinalIgnoreCase)) continue;
            var placed = false;
            for (var g = 0; g < options.Groups.Count && !placed; g++)
            {
                var group = options.Groups[g];
                if (string.Equals(group.Name, tag, StringComparison.OrdinalIgnoreCase) && group.IncludeGroupTag) { known.Add((g, -1, group.Name)); placed = true; }
                else
                {
                    var index = IndexOf(group.Tags, tag);
                    if (index >= 0) { known.Add((g, index, group.Tags[index])); placed = true; }
                }
            }
            if (!placed) unknown.Add(tag);
        }
        return known.OrderBy(k => k.Group).ThenBy(k => k.Position).Select(k => k.Tag).Concat(unknown).ToArray();
    }

    private static int IndexOf(IReadOnlyList<string> list, string value)
    {
        for (var i = 0; i < list.Count; i++)
            if (string.Equals(list[i], value, StringComparison.OrdinalIgnoreCase)) return i;
        return -1;
    }

    private static void AddOnce(List<string> tags, string tag)
    {
        if (!tags.Contains(tag, StringComparer.OrdinalIgnoreCase)) tags.Add(tag);
    }
}
