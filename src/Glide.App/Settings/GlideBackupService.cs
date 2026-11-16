using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Glide.App.Services;
using Glide.Core.Data;
using Glide.Core.Reset;
using Glide.Core.Windows;
using Glide.Core.Workspace;

namespace Glide.App.Settings;

/// <summary>
/// The live values of the running main window an import changes right away (its own geometry, the per-image overlay
/// placements and the remembered folders). The main window implements it; the Settings window finds it through its owner.
/// </summary>
internal interface IGlideBackupLiveTarget
{
    /// <summary>
    /// Applies the main window's size and position from <paramref name="imported"/> when they are safe on this PC's monitors
    /// (IsSafeSavedWindowPosition), and the overlay placements that are; returns what was skipped.
    /// </summary>
    IReadOnlyList<string> ApplyImportedWindowPlacements(GlideSettingsState imported);

    /// <summary>Applies the remembered folders of "Last session" (already stripped of network paths) to the running settings.</summary>
    void ApplyImportedSessionLocations(GlideSettingsState imported);
}

/// <summary>
/// Settings &gt; Windows &amp; developer &gt; Back up and restore: builds a .glidebackup file from the chosen categories and
/// turns a chosen file back into validated, sanitised data. The archive container (size caps, zip-bomb and path guards,
/// manifest and checksums) is <see cref="GlideBackupArchive"/>; this class owns what is inside, using the same
/// source-generated JSON contexts as the stores and the same safety rules as Settings &gt; Profiles import:
/// <list type="bullet">
/// <item>settings values are migrated through the normal loader (<see cref="SettingsStore.MigrateFromJson"/>), a newer
/// schema than this Glide knows is refused, and the schema version never goes down;</item>
/// <item>external programs are never imported (<see cref="SettingsWindow.KeepLocalLaunchTargets"/>) and network folders are
/// dropped (<see cref="SettingsWindow.KeepLocalFolderPaths"/>), in settings and in every imported profile; recent files and
/// session tabs that name a network or relative path are dropped too;</item>
/// <item>the Windows startup switches, the update checker's memory, registry entries and caches are never
/// part of a backup, so an import can never change startup or registry state;</item>
/// <item>window geometry from another PC is checked against this PC's monitors (<see cref="WindowPlacementResolver"/> for the
/// per-window store, IsSafeSavedWindowPosition for the main window and overlays).</item>
/// </list>
/// "All settings" and "Hotkeys and mouse gestures" are staged in the Settings window (OK / Apply saves, Cancel discards);
/// the other categories live in their own stores and are written right away, after the import dialog's confirmation,
/// through a temporary file and a replace.
/// </summary>
internal static class GlideBackupService
{
    /// <summary>Most saved profiles one backup may hold.</summary>
    internal const int MaxProfiles = 100;
    /// <summary>Most session tabs one backup may hold.</summary>
    internal const int MaxSessionTabs = 200;
    /// <summary>Most recent-file rows read from a backup (the list itself keeps at most 32).</summary>
    internal const int MaxHistoryRows = 256;
    /// <summary>Most remembered window / overlay placements read from a backup.</summary>
    internal const int MaxPlacements = 128;

    /// <summary>Categories staged in the Settings window (OK / Apply saves them, Cancel discards them).</summary>
    internal static bool IsStaged(ResetItemId id) => id is ResetItemId.AllSettings or ResetItemId.HotkeysAndGestures;

    internal static string AppVersion()
    {
        var informational = typeof(GlideBackupService).Assembly
            .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
            .OfType<System.Reflection.AssemblyInformationalVersionAttribute>().FirstOrDefault()?.InformationalVersion;
        var version = string.IsNullOrWhiteSpace(informational) ? Glide.Core.AppVersion.Fallback : informational;
        var plus = version.IndexOf('+');
        return plus > 0 ? version[..plus] : version;
    }

    internal static string SuggestedFileName(DateTime now) =>
        "Glide backup " + now.ToString("yyyy-MM-dd HHmm", CultureInfo.InvariantCulture) + GlideBackupFormat.FileExtension;

    // ================================================================ export

    /// <summary>Everything an export reads, gathered first so the archive is built from one consistent snapshot.</summary>
    internal sealed record ExportSources(
        GlideSettingsState Settings,
        Dictionary<string, WindowPlacementState> WindowStore,
        ProfileStore.ProfileDocument? Profiles,
        IReadOnlyList<RecentHistoryStore.Entry>? History,
        WorkspaceSessionStore.SessionDocument? Session);

    /// <summary>
    /// Reads the data of <paramref name="categories"/>: settings values from <paramref name="settings"/> (the Settings
    /// window's staged state, like Export settings), the live per-window placement store, and the stores' files.
    /// </summary>
    internal static async Task<ExportSources> GatherAsync(GlideSettingsState settings, IReadOnlyCollection<ResetItemId> categories)
    {
        var state = settings.CloneState();
        var windowStore = categories.Contains(ResetItemId.WindowPlacements) ? WindowPlacementService.Snapshot() : new(StringComparer.OrdinalIgnoreCase);
        var profiles = categories.Contains(ResetItemId.ProfilesAndPresets) ? ProfileStore.ReadDocumentForBackup() : null;
        var history = categories.Contains(ResetItemId.RecentHistory) ? await RecentHistoryStore.SnapshotForBackupAsync().ConfigureAwait(false) : null;
        var session = categories.Contains(ResetItemId.LastSession) ? WorkspaceSessionStore.ReadDocumentForBackup() : null;
        return new ExportSources(state, windowStore, profiles, history, session);
    }

    /// <summary>The JSON text of each chosen category's archive entry.</summary>
    internal static Dictionary<ResetItemId, string> BuildEntries(ExportSources sources, IReadOnlyCollection<ResetItemId> categories)
    {
        var entries = new Dictionary<ResetItemId, string>();
        var settings = sources.Settings.CloneState();
        if (categories.Contains(ResetItemId.WindowPlacements) && sources.WindowStore.Count > 0)
            settings.WindowPlacements = GlideSettingsState.ClonePlacements(sources.WindowStore);
        var root = ToNode(settings);
        var schema = Math.Max(settings.SettingsSchemaVersion, SettingsStore.CurrentSettingsSchema);
        foreach (var category in GlideDataCategoryCatalog.With(GlideDataCapabilities.Backup))
        {
            if (!categories.Contains(category.Id)) continue;
            switch (category.Id)
            {
                case ResetItemId.AllSettings:
                case ResetItemId.HotkeysAndGestures:
                case ResetItemId.WindowPlacements:
                case ResetItemId.LastSession:
                {
                    var entry = new JsonObject { [ResetCatalog.SettingsSchemaVersionProperty] = schema };
                    foreach (var name in GlideDataCategoryCatalog.SettingsPropertiesFor(category.Id, root.Select(x => x.Key)))
                        if (FindName(root, name) is { } key) entry[key] = root[key]?.DeepClone();
                    if (category.Id == ResetItemId.LastSession && sources.Session is { } session)
                        entry["Session"] = JsonSerializer.SerializeToNode(session, AppJsonContext.Default.SessionDocument);
                    entries[category.Id] = entry.ToJsonString(AppJsonContext.Indented);
                    break;
                }
                case ResetItemId.ProfilesAndPresets:
                    entries[category.Id] = JsonSerializer.Serialize(sources.Profiles ?? new ProfileStore.ProfileDocument(), AppJsonContext.Default.ProfileDocument);
                    break;
                case ResetItemId.RecentHistory:
                    entries[category.Id] = JsonSerializer.Serialize(new RecentHistoryStore.Document { Items = (sources.History ?? []).ToList() }, AppJsonContext.Default.Document);
                    break;
            }
        }
        return entries;
    }

    /// <summary>Writes a backup of <paramref name="categories"/> to <paramref name="path"/> (temp file + replace).</summary>
    internal static async Task ExportAsync(string path, GlideSettingsState settings, IReadOnlyCollection<ResetItemId> categories, DateTime nowUtc)
    {
        var sources = await GatherAsync(settings, categories).ConfigureAwait(true);
        var entries = BuildEntries(sources, categories);
        var bytes = GlideBackupArchive.Build(entries, AppVersion(), SettingsStore.CurrentSettingsSchema, nowUtc);
        await Task.Run(() => GlideBackupArchive.WriteFileAtomic(path, bytes)).ConfigureAwait(true);
    }

    // ================================================================ import: read and validate

    /// <summary>A validated backup, ready to stage or apply. Nothing has been changed yet.</summary>
    internal sealed class ImportPlan
    {
        public required string SourcePath { get; init; }
        public required GlideBackupManifest Manifest { get; init; }
        /// <summary>Categories present in the file, in catalogue order.</summary>
        public required IReadOnlyList<ResetItemId> Categories { get; init; }
        /// <summary>The current settings with every imported settings category's values in place (migrated and sanitised).</summary>
        public required GlideSettingsState Settings { get; init; }
        public ProfileStore.ProfileDocument? Profiles { get; init; }
        public List<RecentHistoryStore.Entry>? History { get; init; }
        public WorkspaceSessionStore.SessionDocument? Session { get; init; }
        /// <summary>The file named other external programs; this PC's are kept.</summary>
        public bool LaunchTargetsKept { get; init; }
        /// <summary>Network folders were left out (settings, profiles).</summary>
        public bool NetworkFoldersDropped { get; init; }
        /// <summary>Recent files, session tabs or Explorer histories left out because they named a network or relative path.</summary>
        public int NetworkEntriesDropped { get; init; }
        public IReadOnlyList<string> IgnoredCategories { get; init; } = [];
    }

    /// <summary>Reads, validates and sanitises a backup. Throws <see cref="GlideBackupException"/> (nothing changed) when it cannot be imported.</summary>
    internal static ImportPlan ReadImport(string path, GlideSettingsState current)
    {
        var contents = GlideBackupArchive.ReadFile(path);
        return Plan(path, contents, current);
    }

    internal static ImportPlan Plan(string path, GlideBackupContents contents, GlideSettingsState current)
    {
        ArgumentNullException.ThrowIfNull(contents);
        ArgumentNullException.ThrowIfNull(current);
        var baseline = current.CloneState();
        var merged = ToNode(baseline);
        var importedSettingsNames = new List<string>();
        WorkspaceSessionStore.SessionDocument? session = null;
        ProfileStore.ProfileDocument? profiles = null;
        List<RecentHistoryStore.Entry>? history = null;
        var launchKept = false;
        var networkDropped = false;
        var networkEntries = 0;

        foreach (var id in contents.Categories)
        {
            var title = GlideDataCategoryCatalog.Get(id).Title;
            var text = contents.Entries[id];
            switch (id)
            {
                case ResetItemId.AllSettings:
                case ResetItemId.HotkeysAndGestures:
                case ResetItemId.WindowPlacements:
                case ResetItemId.LastSession:
                {
                    var entry = ParseObject(text, title);
                    var migrated = MigrateSettingsEntry(entry, baseline, id, title);
                    var source = ToNode(migrated);
                    foreach (var name in GlideDataCategoryCatalog.SettingsPropertiesFor(id, source.Select(x => x.Key)))
                    {
                        if (FindName(source, name) is not { } key) continue;
                        // Only values the backup holds: a property this backup's Glide did not have keeps this PC's value.
                        if (FindName(entry, name) is null) continue;
                        merged[FindName(merged, name) ?? key] = source[key]?.DeepClone();
                        importedSettingsNames.Add(key);
                    }
                    if (id == ResetItemId.LastSession && FindName(entry, "Session") is { } sessionKey && entry[sessionKey] is { } sessionNode)
                    {
                        var (doc, dropped) = ReadSession(sessionNode, title);
                        session = doc;
                        networkEntries += dropped;
                    }
                    else if (id == ResetItemId.LastSession)
                    {
                        // A backup made when there was no stored session: importing it clears the last session.
                        session = new WorkspaceSessionStore.SessionDocument();
                    }
                    break;
                }
                case ResetItemId.ProfilesAndPresets:
                {
                    var (doc, kept, dropped) = ReadProfiles(text, baseline, title);
                    profiles = doc;
                    launchKept |= kept;
                    networkDropped |= dropped;
                    break;
                }
                case ResetItemId.RecentHistory:
                {
                    var (items, dropped) = ReadHistory(text, title);
                    history = items;
                    networkEntries += dropped;
                    break;
                }
            }
        }

        var settings = FromNode(merged);
        settings.SettingsSchemaVersion = Math.Max(baseline.SettingsSchemaVersion, Math.Max(settings.SettingsSchemaVersion, SettingsStore.CurrentSettingsSchema));
        // Same rules as Settings > Profiles import, applied to the imported values only (not to this PC's own values).
        var before = ToNode(settings);
        // External programs always stay this PC's (an imported file never decides what Glide runs).
        launchKept |= SettingsWindow.KeepLocalLaunchTargets(settings, baseline);
        var sanitized = settings.CloneState();
        SettingsWindow.KeepLocalFolderPaths(sanitized, baseline);
        var after = ToNode(sanitized);
        var restore = ToNode(settings);
        foreach (var name in importedSettingsNames.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (FindName(after, name) is not { } key) continue;
            // Only a network path that was replaced counts (the rule also turns a missing value into an empty one).
            if (!JsonNode.DeepEquals(before[key], after[key]) && before[key] is JsonValue value &&
                value.TryGetValue<string>(out var text) && SettingsWindow.IsNetworkPath(text))
                networkDropped = true;
            restore[key] = after[key]?.DeepClone();
        }
        settings = FromNode(restore);
        settings.SettingsSchemaVersion = Math.Max(baseline.SettingsSchemaVersion, Math.Max(settings.SettingsSchemaVersion, SettingsStore.CurrentSettingsSchema));

        return new ImportPlan
        {
            SourcePath = path,
            Manifest = contents.Manifest,
            Categories = contents.Categories,
            Settings = settings,
            Profiles = profiles,
            History = history,
            Session = session,
            LaunchTargetsKept = launchKept,
            NetworkFoldersDropped = networkDropped,
            NetworkEntriesDropped = networkEntries,
            IgnoredCategories = contents.IgnoredCategories,
        };
    }

    private static JsonObject ParseObject(string text, string title)
    {
        try
        {
            return JsonNode.Parse(text, documentOptions: new JsonDocumentOptions { MaxDepth = 64 }) as JsonObject
                   ?? throw new GlideBackupException($"This backup is damaged ({title} is not valid).");
        }
        catch (JsonException ex) { throw new GlideBackupException($"This backup is damaged ({title} is not valid JSON).", ex); }
    }

    private static int SchemaOf(JsonObject entry, string title)
    {
        if (FindName(entry, ResetCatalog.SettingsSchemaVersionProperty) is not { } key) return 0;
        if (entry[key] is not JsonValue value || !value.TryGetValue<int>(out var schema) || schema < 0)
            throw new GlideBackupException($"This backup is damaged ({title} has no valid settings version).");
        if (schema > SettingsStore.CurrentSettingsSchema)
            throw new GlideBackupException($"This backup was made by a newer version of Glide (settings version {schema}; this Glide reads up to {SettingsStore.CurrentSettingsSchema}). Update Glide first.");
        return schema;
    }

    /// <summary>
    /// The entry's values laid over this PC's settings, stamped with the entry's schema and run through the normal loader,
    /// so an older backup migrates exactly like an older glide.settings.json.
    /// </summary>
    private static GlideSettingsState MigrateSettingsEntry(JsonObject entry, GlideSettingsState baseline, ResetItemId id, string title)
    {
        var schema = SchemaOf(entry, title);
        var root = ToNode(baseline);
        foreach (var name in GlideDataCategoryCatalog.SettingsPropertiesFor(id, root.Select(x => x.Key)))
        {
            if (FindName(entry, name) is not { } source) continue;
            root[FindName(root, name) ?? name] = entry[source]?.DeepClone();
        }
        root[FindName(root, ResetCatalog.SettingsSchemaVersionProperty) ?? ResetCatalog.SettingsSchemaVersionProperty] = schema;
        try { return SettingsStore.MigrateFromJson(root.ToJsonString(), isSettingsFile: false); }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException or NotSupportedException or ArgumentException)
        {
            throw new GlideBackupException($"This backup is damaged ({title} holds values Glide cannot read).", ex);
        }
    }

    private static (ProfileStore.ProfileDocument Document, bool LaunchKept, bool NetworkDropped) ReadProfiles(string text, GlideSettingsState current, string title)
    {
        ProfileStore.ProfileDocument? doc;
        try { doc = JsonSerializer.Deserialize(text, AppJsonContext.Default.ProfileDocument); }
        catch (JsonException ex) { throw new GlideBackupException($"This backup is damaged ({title} is not valid).", ex); }
        if (doc is null) throw new GlideBackupException($"This backup is damaged ({title} is empty).");
        if (doc.Schema > ProfileStore.CurrentSchema)
            throw new GlideBackupException($"This backup was made by a newer version of Glide ({title} version {doc.Schema}). Update Glide first.");
        doc.Profiles ??= new();
        // Schema 1 kept three fixed slots: the same one-time conversion as the store's own loader.
        if (doc.Schema <= 1 || (doc.Profiles.Count == 0 && doc.Slots is { Count: > 0 }))
            foreach (var pair in doc.Slots ?? new())
            {
                if (pair.Value is null) continue;
                var number = pair.Key.StartsWith("profile", StringComparison.OrdinalIgnoreCase) ? pair.Key[7..] : pair.Key;
                doc.Profiles.Add(new ProfileStore.StoredProfile { Id = Guid.NewGuid(), Name = $"Profile {number}", Settings = pair.Value });
            }
        doc.Slots = null;
        if (doc.Profiles.Count > MaxProfiles)
            throw new GlideBackupException($"This backup holds too many profiles ({doc.Profiles.Count}; at most {MaxProfiles}).");

        var launchKept = false;
        var networkDropped = false;
        var ids = new HashSet<Guid>();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var clean = new List<ProfileStore.StoredProfile>();
        foreach (var profile in doc.Profiles)
        {
            if (profile?.Settings is null) continue;
            if (profile.Settings.SettingsSchemaVersion > SettingsStore.CurrentSettingsSchema)
                throw new GlideBackupException($"This backup was made by a newer version of Glide (a profile uses settings version {profile.Settings.SettingsSchemaVersion}). Update Glide first.");
            GlideSettingsState settings;
            try
            {
                // Through the normal loader, like a settings file of that age.
                settings = SettingsStore.MigrateFromJson(JsonSerializer.Serialize(profile.Settings, AppJsonContext.Default.GlideSettingsState), isSettingsFile: false);
            }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException or NotSupportedException or ArgumentException)
            {
                throw new GlideBackupException($"This backup is damaged (a profile holds values Glide cannot read).", ex);
            }
            launchKept |= SettingsWindow.KeepLocalLaunchTargets(settings, current);
            networkDropped |= SettingsWindow.KeepLocalFolderPaths(settings, current);
            var name = (profile.Name ?? string.Empty).Trim();
            if (name.Length > 100) name = name[..100].Trim();
            if (name.Length == 0 || name.Any(char.IsControl)) name = $"Profile {clean.Count + 1}";
            var unique = name;
            for (var n = 2; !names.Add(unique); n++) unique = $"{name} {n}";
            var id = profile.Id == Guid.Empty || !ids.Add(profile.Id) ? Guid.NewGuid() : profile.Id;
            ids.Add(id);
            clean.Add(new ProfileStore.StoredProfile { Id = id, Name = unique, Settings = settings });
        }
        return (new ProfileStore.ProfileDocument { Schema = ProfileStore.CurrentSchema, Profiles = clean }, launchKept, networkDropped);
    }

    private static (List<RecentHistoryStore.Entry> Items, int Dropped) ReadHistory(string text, string title)
    {
        RecentHistoryStore.Document? doc;
        try { doc = JsonSerializer.Deserialize(text, AppJsonContext.Default.Document); }
        catch (JsonException ex) { throw new GlideBackupException($"This backup is damaged ({title} is not valid).", ex); }
        if (doc is null) throw new GlideBackupException($"This backup is damaged ({title} is empty).");
        if (doc.Schema > 1) throw new GlideBackupException($"This backup was made by a newer version of Glide ({title} version {doc.Schema}). Update Glide first.");
        var rows = doc.Items ?? new();
        if (rows.Count > MaxHistoryRows) throw new GlideBackupException($"This backup is damaged ({title} has too many rows).");
        var dropped = 0;
        var items = new List<RecentHistoryStore.Entry>();
        foreach (var row in rows)
        {
            if (row is null) continue;
            var kind = string.Equals(row.Kind, "folder", StringComparison.OrdinalIgnoreCase) ? "folder"
                : string.Equals(row.Kind, "file", StringComparison.OrdinalIgnoreCase) ? "file" : null;
            if (kind is null) continue;
            // Never a network location: the Home list probes every entry on start, which would contact that host.
            if (SafeLocalPath(row.Path) is not { } local) { dropped++; continue; }
            items.Add(new RecentHistoryStore.Entry(kind, local, row.LastOpenedUtc));
        }
        return (items.OrderByDescending(x => x.LastOpenedUtc).Take(32).ToList(), dropped);
    }

    private static (WorkspaceSessionStore.SessionDocument Document, int Dropped) ReadSession(JsonNode node, string title)
    {
        WorkspaceSessionStore.SessionDocument? doc;
        try { doc = node.Deserialize(AppJsonContext.Default.SessionDocument); }
        catch (JsonException ex) { throw new GlideBackupException($"This backup is damaged ({title} is not valid).", ex); }
        if (doc is null) return (new WorkspaceSessionStore.SessionDocument(), 0);
        if (doc.Schema > WorkspaceSessionStore.CurrentSchema)
            throw new GlideBackupException($"This backup was made by a newer version of Glide ({title} version {doc.Schema}). Update Glide first.");
        var tabs = doc.Tabs ?? new();
        if (tabs.Count > MaxSessionTabs) throw new GlideBackupException($"This backup is damaged ({title} has too many tabs).");
        var dropped = 0;
        var clean = new List<WorkspaceSessionStore.SessionTab>();
        foreach (var tab in tabs)
        {
            if (tab is null) continue;
            if (string.Equals(tab.Kind, "Home", StringComparison.OrdinalIgnoreCase))
            {
                clean.Add(new WorkspaceSessionStore.SessionTab { Kind = "Home", Active = tab.Active });
                continue;
            }
            var kind = string.Equals(tab.Kind, "Image", StringComparison.OrdinalIgnoreCase) ? "Image"
                : string.Equals(tab.Kind, "Browser", StringComparison.OrdinalIgnoreCase) ? "Browser" : null;
            if (kind is null) continue;
            // Restoring a tab opens its file or folder: a network path would make Windows contact that host on start.
            if (SafeLocalPath(tab.Path) is not { } local) { dropped++; continue; }
            var clean1 = new WorkspaceSessionStore.SessionTab { Kind = kind, Path = local, Active = tab.Active };
            if (kind == "Image" && tab.ImageView is { } view && double.IsFinite(view.Zoom) && double.IsFinite(view.PanX) && double.IsFinite(view.PanY) && view.Zoom > 0)
                clean1.ImageView = view;
            if (kind == "Browser")
            {
                clean1.BrowserNavigation = SafeNavigation(tab.BrowserNavigation, ref dropped);
                clean1.SelectedPath = tab.SelectedPath is null ? null : SafeLocalPath(tab.SelectedPath);
                clean1.ScrollY = tab.ScrollY is not { } y ? null : double.IsFinite(y) && y > 0 ? y : 0;
            }
            clean.Add(clean1);
        }
        var active = clean.FindIndex(x => x.Active);
        return (new WorkspaceSessionStore.SessionDocument
        {
            Schema = WorkspaceSessionStore.CurrentSchema,
            Tabs = clean,
            ActiveIndex = active >= 0 ? active : Math.Clamp(doc.ActiveIndex, 0, Math.Max(0, clean.Count - 1)),
        }, dropped);
    }

    private static BrowserNavigationState? SafeNavigation(BrowserNavigationState? navigation, ref int dropped)
    {
        if (navigation?.History is not { } history) return null;
        if (history.Count == 0 || history.Count > 256 || navigation.Index < 0 || navigation.Index >= history.Count) return null;
        if (history.Any(x => SafeLocalPath(x) is null)) { dropped++; return null; }
        if (navigation.Highlights is { } highlights && (highlights.Count > 256 || highlights.Any(x => x is not null && SafeLocalPath(x) is null)))
            return navigation with { Highlights = null };
        return navigation;
    }

    /// <summary>A full local path (never network, device or relative), or null.</summary>
    internal static string? SafeLocalPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Length > 4096 || SettingsWindow.IsNetworkPath(path)) return null;
        try
        {
            if (!Path.IsPathFullyQualified(path)) return null;
            var full = Path.GetFullPath(path);
            return SettingsWindow.IsNetworkPath(full) ? null : full;
        }
        catch { return null; }
    }

    // ================================================================ import: stage and apply

    /// <summary>
    /// <paramref name="target"/> with the values of the staged categories in <paramref name="chosen"/> taken from the plan
    /// ("All settings": every option; "Hotkeys and mouse gestures": the bindings and custom presets). Everything else is
    /// <paramref name="target"/>'s; the schema version never goes down.
    /// </summary>
    internal static GlideSettingsState StageSettings(GlideSettingsState target, ImportPlan plan, IEnumerable<ResetItemId> chosen)
    {
        var root = ToNode(target);
        var source = ToNode(plan.Settings);
        foreach (var id in chosen.Where(IsStaged).Where(plan.Categories.Contains))
            foreach (var name in GlideDataCategoryCatalog.SettingsPropertiesFor(id, source.Select(x => x.Key)))
                if (FindName(source, name) is { } key) root[FindName(root, name) ?? key] = source[key]?.DeepClone();
        var staged = FromNode(root);
        staged.SettingsSchemaVersion = Math.Max(target.SettingsSchemaVersion, staged.SettingsSchemaVersion);
        return staged;
    }

    /// <summary>
    /// The per-window placements of the plan that are usable on <paramref name="screens"/>: each is validated like a window's
    /// own restore (monitor gone or off-screen: the position is dropped and the window opens at its default place; size
    /// clamped to the monitor). Garbage values are dropped.
    /// </summary>
    internal static Dictionary<string, WindowPlacementState> ValidatePlacements(
        IReadOnlyDictionary<string, WindowPlacementState>? placements, IReadOnlyList<ScreenWorkArea> screens, ScreenWorkArea primary)
    {
        var result = new Dictionary<string, WindowPlacementState>(StringComparer.OrdinalIgnoreCase);
        if (placements is null) return result;
        foreach (var (key, value) in placements.Take(MaxPlacements))
        {
            if (string.IsNullOrWhiteSpace(key) || key.Length > 128 || value is null) continue;
            if (!double.IsFinite(value.Width) || !double.IsFinite(value.Height) || value.Width < 0 || value.Height < 0 || value.Width > 20000 || value.Height > 20000) continue;
            var hasPosition = value.X != int.MinValue && value.Y != int.MinValue;
            if (hasPosition && (Math.Abs((long)value.X) >= 50000 || Math.Abs((long)value.Y) >= 50000)) hasPosition = false;
            var saved = new SavedWindowPlacement(hasPosition ? value.X : int.MinValue, hasPosition ? value.Y : int.MinValue, value.Width, value.Height, value.Maximized);
            var mode = saved.HasSize ? WindowPlacementMode.SizeAndPosition : WindowPlacementMode.PositionOnly;
            if (WindowPlacementResolver.Resolve(saved, screens, primary, mode, 0, 0, 400, 200) is not { } resolved) continue;
            var placement = new WindowPlacementState
            {
                X = resolved.HasPosition && hasPosition ? resolved.X!.Value : int.MinValue,
                Y = resolved.HasPosition && hasPosition ? resolved.Y!.Value : int.MinValue,
                Width = resolved.Width ?? 0,
                Height = resolved.Height ?? 0,
                Maximized = resolved.Maximized,
            };
            // Position-only dialogs with no usable position have nothing left to remember.
            if (placement.X == int.MinValue && !placement.HasSize) continue;
            result[key] = placement;
        }
        return result;
    }

    /// <summary>What an import did.</summary>
    internal sealed record ImportOutcome(
        IReadOnlyList<ResetItemId> Staged,
        IReadOnlyList<ResetItemId> Applied,
        IReadOnlyList<(ResetItemId Category, string Error)> Failed,
        IReadOnlyList<string> Notes,
        string? SafetyBackupPath);

    /// <summary>
    /// Writes the categories that are not staged: profiles, recent files, last session (file and remembered folders) and window
    /// placements. Each is written on its own (temp file + replace); one that fails is reported and the others still apply.
    /// </summary>
    internal static async Task<(List<ResetItemId> Applied, List<(ResetItemId, string)> Failed, List<string> Notes)> ApplyStoresAsync(
        ImportPlan plan, IReadOnlyCollection<ResetItemId> chosen, IGlideBackupLiveTarget? live,
        IReadOnlyList<ScreenWorkArea> screens, ScreenWorkArea primary)
    {
        var applied = new List<ResetItemId>();
        var failed = new List<(ResetItemId, string)>();
        var notes = new List<string>();
        foreach (var id in plan.Categories.Where(chosen.Contains).Where(x => !IsStaged(x)))
        {
            try
            {
                switch (id)
                {
                    case ResetItemId.ProfilesAndPresets:
                        ProfileStore.ImportDocument(plan.Profiles ?? new ProfileStore.ProfileDocument());
                        break;
                    case ResetItemId.RecentHistory:
                        await RecentHistoryStore.ReplaceAllAsync(plan.History ?? []).ConfigureAwait(true);
                        break;
                    case ResetItemId.LastSession:
                        WorkspaceSessionStore.WriteImported(plan.Session ?? new WorkspaceSessionStore.SessionDocument());
                        live?.ApplyImportedSessionLocations(plan.Settings);
                        notes.Add("The imported last session opens the next time Glide starts with \"Open last session\".");
                        break;
                    case ResetItemId.WindowPlacements:
                    {
                        var store = ValidatePlacements(plan.Settings.WindowPlacements, screens, primary);
                        var skipped = (plan.Settings.WindowPlacements?.Count ?? 0) - store.Count;
                        WindowPlacementService.Import(store);
                        if (live is not null) notes.AddRange(live.ApplyImportedWindowPlacements(plan.Settings));
                        if (skipped > 0) notes.Add($"{skipped} window position{(skipped == 1 ? "" : "s")} did not fit this PC's screens and {(skipped == 1 ? "was" : "were")} left out.");
                        break;
                    }
                }
                applied.Add(id);
            }
            catch (Exception ex)
            {
                failed.Add((id, ex.Message));
            }
        }
        return (applied, failed, notes);
    }

    /// <summary>
    /// Before an import replaces data in place: a backup of what is there now, of the same categories, in
    /// <paramref name="folder"/> (Documents\Glide backups). Returns its path.
    /// </summary>
    internal static async Task<string> SaveSafetyBackupAsync(string folder, GlideSettingsState current, IReadOnlyCollection<ResetItemId> categories, DateTime now)
    {
        Directory.CreateDirectory(folder);
        var name = "Glide backup before import " + now.ToString("yyyy-MM-dd HHmmss", CultureInfo.InvariantCulture);
        var path = Path.Combine(folder, name + GlideBackupFormat.FileExtension);
        for (var n = 2; File.Exists(path); n++) path = Path.Combine(folder, $"{name} ({n}){GlideBackupFormat.FileExtension}");
        await ExportAsync(path, current, categories, now.ToUniversalTime()).ConfigureAwait(true);
        return path;
    }

    /// <summary>One-line summary of what the import left out on purpose (status line and dialog).</summary>
    internal static string Notes(ImportPlan plan)
    {
        var parts = new List<string>();
        if (plan.LaunchTargetsKept) parts.Add("external programs were not imported (set them in Settings)");
        if (plan.NetworkFoldersDropped) parts.Add("network folder paths were not imported");
        if (plan.NetworkEntriesDropped > 0) parts.Add($"{plan.NetworkEntriesDropped} network or relative location{(plan.NetworkEntriesDropped == 1 ? " was" : "s were")} left out");
        if (plan.IgnoredCategories.Count > 0) parts.Add("skipped what this Glide cannot import: " + string.Join(", ", plan.IgnoredCategories));
        return string.Join("; ", parts);
    }

    // ================================================================ JSON helpers (source-generated context only: NativeAOT safe)

    internal static JsonObject ToNode(GlideSettingsState state) =>
        JsonSerializer.SerializeToNode(state, AppJsonContext.Default.GlideSettingsState) as JsonObject
        ?? throw new InvalidOperationException("Settings did not serialise to a JSON object.");

    internal static GlideSettingsState FromNode(JsonObject root)
    {
        var state = root.Deserialize(AppJsonContext.Default.GlideSettingsState) ?? new GlideSettingsState();
        state.OverlayWindowPlacements = new Dictionary<string, OverlayWindowPlacementState>(state.OverlayWindowPlacements ?? new(), StringComparer.OrdinalIgnoreCase);
        return state.CloneState();
    }

    // Glide's settings are read case-insensitively, so a differently cased key is the same value.
    private static string? FindName(JsonObject obj, string name)
    {
        if (obj.ContainsKey(name)) return name;
        foreach (var pair in obj)
            if (string.Equals(pair.Key, name, StringComparison.OrdinalIgnoreCase)) return pair.Key;
        return null;
    }
}
