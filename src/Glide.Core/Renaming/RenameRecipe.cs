using System.Text.Json;

namespace Glide.Core.Renaming;

public enum RenameCase { Keep, Lower, Upper, Title, Sentence }

public enum RenameExtension { Keep, Lower, Upper }

/// <summary>The order the numbers are given in.</summary>
public enum RenameSort { CurrentOrder, Name, DateTaken, DateModified }

/// <summary>What the batch rename dialog's date format picker offers (the sample date is 14 May 2026, 15:30).</summary>
public static class RenameDateFormats
{
    public static readonly DateTime Sample = new(2026, 5, 14, 15, 30, 0);

    /// <summary>The picker's examples, in order. A format that is not one of these is a "Custom" one.</summary>
    public static readonly string[] Presets = ["yyyy-MM-dd", "dd-MM-yyyy", "yyyyMMdd", "yyyy-MM-dd HH.mm"];

    public static string Example(string format) => RenameTemplate.FormatDate(Sample, format);
}

/// <summary>
/// Everything the batch rename dialog's boxes say, as plain data: the "New name" template, Replace text, Numbers, Capitals and
/// the "More options". The order of work for one name is: Remove (on the old name), the template, Replace text, extra spaces,
/// Capitals, then the extension option. A recipe is what a "Saved:" preset stores.
/// </summary>
public sealed record RenameRecipe
{
    public const string DefaultDateFormat = "yyyy-MM-dd";
    public const int MaxRemove = 255;

    public string Template { get; init; } = RenameTemplate.Default;
    public string Find { get; init; } = "";
    public string Replace { get; init; } = "";
    public bool MatchCase { get; init; }
    public bool UseRegex { get; init; }
    public int NumberStart { get; init; } = 1;
    /// <summary>How many digits a number has at least: 1 is "1", 2 is "01", 3 is "001", 4 is "0001".</summary>
    public int NumberDigits { get; init; } = 1;
    public RenameCase Case { get; init; } = RenameCase.Keep;
    public string DateFormat { get; init; } = DefaultDateFormat;
    public int RemoveFirst { get; init; }
    public int RemoveLast { get; init; }
    public bool RemoveSpaces { get; init; }
    public bool RemoveDigits { get; init; }
    public RenameExtension Extension { get; init; } = RenameExtension.Keep;
    public RenameSort Sort { get; init; } = RenameSort.CurrentOrder;

    public bool NeedsTaken => Sort == RenameSort.DateTaken || RenameTemplate.Uses(Template, RenameToken.Taken);
    public bool NeedsModified => Sort == RenameSort.DateModified || RenameTemplate.Uses(Template, RenameToken.Modified);
    public bool NeedsSize => RenameTemplate.Uses(Template, RenameToken.Size) || RenameTemplate.Uses(Template, RenameToken.Width) || RenameTemplate.Uses(Template, RenameToken.Height);

    /// <summary>The same recipe with every number brought into range and blank text replaced by its default.</summary>
    public RenameRecipe Normalized() => this with
    {
        Template = Template ?? RenameTemplate.Default,
        Find = Find ?? "",
        Replace = Replace ?? "",
        NumberStart = Math.Clamp(NumberStart, 0, 999_999_999),
        NumberDigits = Math.Clamp(NumberDigits, 1, 9),
        DateFormat = string.IsNullOrWhiteSpace(DateFormat) ? DefaultDateFormat : DateFormat,
        RemoveFirst = Math.Clamp(RemoveFirst, 0, MaxRemove),
        RemoveLast = Math.Clamp(RemoveLast, 0, MaxRemove),
    };
}

/// <summary>One named recipe in the "Saved:" list.</summary>
public sealed record RenamePreset(string Name, RenameRecipe Recipe);

/// <summary>
/// The "Saved:" presets of the batch rename dialog, kept as one JSON string in the settings (<c>BatchRenamePresets</c>) so Reset,
/// backup and profiles carry them. Written and read without reflection (the app can be published ahead of time). Unknown or
/// damaged entries are skipped, never an error.
/// </summary>
public static class RenamePresets
{
    public const int Capacity = 50;
    public const int MaxNameLength = 60;

    public static IReadOnlyList<RenamePreset> Parse(string? json)
    {
        var list = new List<RenamePreset>();
        if (string.IsNullOrWhiteSpace(json)) return list;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object || !doc.RootElement.TryGetProperty("presets", out var array) || array.ValueKind != JsonValueKind.Array) return list;
            foreach (var item in array.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) continue;
                var name = CleanName(Str(item, "name"));
                if (name.Length == 0 || list.Any(p => NameEquals(p.Name, name))) continue;
                list.Add(new RenamePreset(name, ReadRecipe(item)));
                if (list.Count == Capacity) break;
            }
        }
        catch (JsonException) { /* a damaged value: no presets */ }
        return list;
    }

    public static string Serialize(IEnumerable<RenamePreset> presets)
    {
        using var stream = new MemoryStream();
        using (var w = new Utf8JsonWriter(stream))
        {
            w.WriteStartObject();
            w.WriteNumber("version", 1);
            w.WriteStartArray("presets");
            foreach (var preset in presets.Take(Capacity))
            {
                var r = preset.Recipe.Normalized();
                w.WriteStartObject();
                w.WriteString("name", CleanName(preset.Name));
                w.WriteString("template", r.Template);
                w.WriteString("find", r.Find);
                w.WriteString("replace", r.Replace);
                w.WriteBoolean("matchCase", r.MatchCase);
                w.WriteBoolean("regex", r.UseRegex);
                w.WriteNumber("start", r.NumberStart);
                w.WriteNumber("digits", r.NumberDigits);
                w.WriteString("case", r.Case.ToString());
                w.WriteString("dateFormat", r.DateFormat);
                w.WriteNumber("removeFirst", r.RemoveFirst);
                w.WriteNumber("removeLast", r.RemoveLast);
                w.WriteBoolean("removeSpaces", r.RemoveSpaces);
                w.WriteBoolean("removeDigits", r.RemoveDigits);
                w.WriteString("extension", r.Extension.ToString());
                w.WriteString("sort", r.Sort.ToString());
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteEndObject();
        }
        return System.Text.Encoding.UTF8.GetString(stream.ToArray());
    }

    /// <summary>The list with <paramref name="recipe"/> saved under <paramref name="name"/>: an existing preset of that name (ignoring case) is replaced in place, a new one goes first.</summary>
    public static IReadOnlyList<RenamePreset> Save(IReadOnlyList<RenamePreset> presets, string? name, RenameRecipe recipe)
    {
        var clean = CleanName(name);
        if (clean.Length == 0) return presets;
        var entry = new RenamePreset(clean, recipe.Normalized());
        var index = presets.ToList().FindIndex(p => NameEquals(p.Name, clean));
        var result = presets.ToList();
        if (index >= 0) result[index] = entry;
        else result.Insert(0, entry);
        return result.Take(Capacity).ToArray();
    }

    public static IReadOnlyList<RenamePreset> Delete(IReadOnlyList<RenamePreset> presets, string? name) =>
        presets.Where(p => !NameEquals(p.Name, CleanName(name))).ToArray();

    public static RenamePreset? Find(IReadOnlyList<RenamePreset> presets, string? name) =>
        presets.FirstOrDefault(p => NameEquals(p.Name, CleanName(name)));

    /// <summary>A preset name: one line, trimmed, at most <see cref="MaxNameLength"/> characters.</summary>
    public static string CleanName(string? name)
    {
        var text = (name ?? "").Replace('\r', ' ').Replace('\n', ' ').Trim();
        return text.Length > MaxNameLength ? text[..MaxNameLength].TrimEnd() : text;
    }

    private static bool NameEquals(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private static RenameRecipe ReadRecipe(JsonElement e) => new RenameRecipe
    {
        Template = Str(e, "template", RenameTemplate.Default),
        Find = Str(e, "find"),
        Replace = Str(e, "replace"),
        MatchCase = Bool(e, "matchCase"),
        UseRegex = Bool(e, "regex"),
        NumberStart = Int(e, "start", 1),
        NumberDigits = Int(e, "digits", 1),
        Case = EnumValue<RenameCase>(e, "case"),
        DateFormat = Str(e, "dateFormat", RenameRecipe.DefaultDateFormat),
        RemoveFirst = Int(e, "removeFirst", 0),
        RemoveLast = Int(e, "removeLast", 0),
        RemoveSpaces = Bool(e, "removeSpaces"),
        RemoveDigits = Bool(e, "removeDigits"),
        Extension = EnumValue<RenameExtension>(e, "extension"),
        Sort = EnumValue<RenameSort>(e, "sort"),
    }.Normalized();

    private static string Str(JsonElement e, string name, string fallback = "") =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? fallback : fallback;

    private static bool Bool(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

    private static int Int(JsonElement e, string name, int fallback) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n) ? n : fallback;

    private static T EnumValue<T>(JsonElement e, string name) where T : struct, Enum =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String && System.Enum.TryParse<T>(v.GetString(), true, out var value) ? value : default;
}

/// <summary>One level of undo for the batch rename dialog: the last batch, turned around.</summary>
public static class RenameUndo
{
    private static readonly StringComparer PathComparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    /// <summary>The moves that put every item back: each (from, to) becomes (to, from).</summary>
    public static IReadOnlyList<(string From, string To)> Invert(IReadOnlyList<(string From, string To)> done) =>
        done.Select(m => (m.To, m.From)).ToArray();

    /// <summary>
    /// Null when the last batch can still be undone, otherwise why not. Every item must still have the name the batch gave it, and
    /// the old names must be free unless another item of the batch held them (a swap or a chain). The executor re-checks all of it
    /// and changes nothing if anything has moved since.
    /// </summary>
    public static string? Check(IReadOnlyList<(string From, string To)> done, Func<string, bool> pathExists)
    {
        if (done.Count == 0) return "There is nothing to undo.";
        var current = new HashSet<string>(done.Select(m => m.To), PathComparer);
        foreach (var (from, to) in done)
        {
            if (!pathExists(to)) return $"\"{System.IO.Path.GetFileName(to)}\" is no longer there, so the last rename can't be undone.";
            if (!current.Contains(from) && pathExists(from)) return $"\"{System.IO.Path.GetFileName(from)}\" exists again, so the last rename can't be undone.";
        }
        return null;
    }
}
