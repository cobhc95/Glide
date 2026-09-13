using System.Text.Json;
using Glide.Core;

namespace Glide.App.Settings;

/// <summary>
/// Dynamic named user profiles. Schema 2 migrates the old three fixed slots into named rows once,
/// while profile file import/export is handled explicitly by SettingsWindow through StorageProvider.
/// </summary>
public static class ProfileStore
{
    private const int SchemaVersion = 2;
    private const string FileName = "glide.profiles.json";

    public sealed record UserProfile(Guid Id, string Name, GlideSettingsState Settings);

    private sealed class ProfileDocument
    {
        public int Schema { get; set; } = SchemaVersion;
        public List<StoredProfile> Profiles { get; set; } = new();
        // v1 migration input only.
        public Dictionary<string, GlideSettingsState>? Slots { get; set; }
    }

    private sealed class StoredProfile
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public string Name { get; set; } = "Profile 1";
        public GlideSettingsState Settings { get; set; } = new();
    }

    public static IReadOnlyList<UserProfile> ListProfiles()
    {
        var document = LoadDocument();
        EnsureDefault(document);
        return document.Profiles.Select(p => new UserProfile(p.Id, p.Name, p.Settings.CloneState())).ToList();
    }

    public static UserProfile Add(string name, GlideSettingsState state)
    {
        var document = LoadDocument();
        var cleanName = UniqueName(document, string.IsNullOrWhiteSpace(name) ? "New profile" : name.Trim());
        var profile = new StoredProfile { Id = Guid.NewGuid(), Name = cleanName, Settings = state.CloneState() };
        document.Profiles.Add(profile);
        SaveDocument(document);
        return new UserProfile(profile.Id, profile.Name, profile.Settings.CloneState());
    }

    public static void Save(Guid id, GlideSettingsState state)
    {
        var document = LoadDocument();
        var profile = document.Profiles.FirstOrDefault(p => p.Id == id);
        if (profile is null) return;
        profile.Settings = state.CloneState();
        SaveDocument(document);
    }

    public static GlideSettingsState? Load(Guid id)
    {
        var profile = LoadDocument().Profiles.FirstOrDefault(p => p.Id == id);
        return profile?.Settings.CloneState();
    }

    public static string Rename(Guid id, string name)
    {
        var document = LoadDocument();
        var profile = document.Profiles.FirstOrDefault(p => p.Id == id);
        if (profile is null) return name;
        profile.Name = UniqueName(document, string.IsNullOrWhiteSpace(name) ? profile.Name : name.Trim(), id);
        SaveDocument(document);
        return profile.Name;
    }

    public static void Replace(Guid id, string? name, GlideSettingsState state)
    {
        var document = LoadDocument();
        var profile = document.Profiles.FirstOrDefault(p => p.Id == id);
        if (profile is null) return;
        if (!string.IsNullOrWhiteSpace(name)) profile.Name = UniqueName(document, name.Trim(), id);
        profile.Settings = state.CloneState();
        SaveDocument(document);
    }

    private static ProfileDocument LoadDocument()
    {
        if (GlidePerformanceTrace.Enabled) GlidePerformanceTrace.Mark("profile_read_start");
        try
        {
            var path = Path.Combine(SettingsStore.GetSettingsDirectory(), FileName);
            if (!File.Exists(path)) return NewDocument();
            var json = File.ReadAllText(path);
            var document = JsonSerializer.Deserialize<ProfileDocument>(json, Options()) ?? NewDocument();
            document.Profiles ??= new();
            if (document.Schema == 1 || (document.Profiles.Count == 0 && document.Slots is { Count: > 0 }))
            {
                foreach (var pair in document.Slots ?? new())
                {
                    var number = pair.Key.StartsWith("profile", StringComparison.OrdinalIgnoreCase) ? pair.Key[7..] : pair.Key;
                    document.Profiles.Add(new StoredProfile { Id = Guid.NewGuid(), Name = $"Profile {number}", Settings = pair.Value.CloneState() });
                }
                document.Schema = SchemaVersion;
                document.Slots = null;
                EnsureDefault(document);
                SaveDocument(document);
            }
            if (document.Schema != SchemaVersion) return NewDocument();
            EnsureDefault(document);
            if (GlidePerformanceTrace.Enabled) GlidePerformanceTrace.Mark("profile_read_end", $"count={document.Profiles.Count}");
            return document;
        }
        catch { if (GlidePerformanceTrace.Enabled) GlidePerformanceTrace.Mark("profile_read_failed"); return NewDocument(); }
    }

    private static ProfileDocument NewDocument()
    {
        var doc = new ProfileDocument();
        EnsureDefault(doc);
        return doc;
    }

    private static void EnsureDefault(ProfileDocument document)
    {
        if (document.Profiles.Count == 0)
            document.Profiles.Add(new StoredProfile { Id = Guid.NewGuid(), Name = "Profile 1", Settings = new GlideSettingsState() });
    }

    private static string UniqueName(ProfileDocument document, string requested, Guid? except = null)
    {
        var baseName = requested.Trim();
        if (!document.Profiles.Any(p => p.Id != except && string.Equals(p.Name, baseName, StringComparison.OrdinalIgnoreCase))) return baseName;
        for (var i = 2; i < 1000; i++)
        {
            var candidate = $"{baseName} {i}";
            if (!document.Profiles.Any(p => p.Id != except && string.Equals(p.Name, candidate, StringComparison.OrdinalIgnoreCase))) return candidate;
        }
        return $"{baseName} {Guid.NewGuid():N}"[..Math.Min(baseName.Length + 9, baseName.Length + 32)];
    }

    private static void SaveDocument(ProfileDocument document)
    {
        if (GlidePerformanceTrace.Enabled) GlidePerformanceTrace.Mark("profile_write_start", $"count={document.Profiles.Count}");
        try
        {
            var path = Path.Combine(SettingsStore.GetSettingsDirectory(), FileName);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temp = path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(document, Options()));
            File.Move(temp, path, true);
            if (GlidePerformanceTrace.Enabled) GlidePerformanceTrace.Mark("profile_write_end", $"count={document.Profiles.Count}");
        }
        catch { if (GlidePerformanceTrace.Enabled) GlidePerformanceTrace.Mark("profile_write_failed"); /* Profile persistence is non-fatal. */ }
    }

    private static JsonSerializerOptions Options() => new() { WriteIndented = true, PropertyNameCaseInsensitive = true };
}
