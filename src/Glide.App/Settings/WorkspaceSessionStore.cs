using System.Text.Json;
using System.Text.Json.Serialization;
using Glide.Core.Workspace;
using Glide.Core;

namespace Glide.App.Settings;

/// <summary>
/// Small typed workspace snapshot used only when StartupAction == "Open last session".
/// Invalid/missing paths are discarded during restore so a stale session can never block startup.
/// </summary>
public static class WorkspaceSessionStore
{
    private const int SchemaVersion = 1;
    private const string FileName = "glide.workspace-session.json";

    internal sealed class SessionDocument
    {
        public int Schema { get; set; } = SchemaVersion;
        public int ActiveIndex { get; set; }
        public List<SessionTab> Tabs { get; set; } = new();
    }

    internal sealed class SessionTab
    {
        public string Kind { get; set; } = "Home";
        public string Path { get; set; } = string.Empty;
        public bool Active { get; set; }
        public ImageTabViewState? ImageView { get; set; }
        public BrowserNavigationState? BrowserNavigation { get; set; }

        // Optional (4.8.0): the item selected in an Explorer tab's folder and its scroll offset. Older
        // files simply lack them; older Glide versions ignore them.
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? SelectedPath { get; set; }
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public double? ScrollY { get; set; }
    }

    /// <summary>
    /// A serialized session plus the file it belongs to, captured on the calling (UI) thread so the
    /// slower file write can run elsewhere without touching live tab state.
    /// </summary>
    internal sealed record SessionSnapshot(string Path, string Json, long Sequence);

    /// <summary>Quiet time after the last Explorer change before the session is written (about a second; tests shorten it).</summary>
    internal static TimeSpan AutosaveDelay = TimeSpan.FromSeconds(1);

    private static readonly object WriteLock = new();
    private static long _sequence;
    private static long _lastWrittenSequence;
    private static string? _lastWrittenPath;
    private static string? _lastWrittenJson;
    private static int _writeCount;

    /// <summary>Number of times the session file was actually written by this process (tests and diagnostics).</summary>
    internal static int WriteCount => Volatile.Read(ref _writeCount);

    internal static string SessionPathIn(string settingsDirectory) => Path.Combine(settingsDirectory, FileName);

    private static string SessionPath => SessionPathIn(SettingsStore.GetSettingsDirectory());

    public static void Save(IReadOnlyList<TabState> tabs, int activeIndex)
    {
        if (GlidePerformanceTrace.Enabled) GlidePerformanceTrace.Mark("workspace_session_write_start", $"tabs={tabs.Count}");
        try
        {
            var snapshot = Snapshot(tabs, activeIndex);
            if (snapshot is null) return;
            Write(snapshot);
            if (GlidePerformanceTrace.Enabled) GlidePerformanceTrace.Mark("workspace_session_write_end", $"tabs={tabs.Count}");
        }
        catch { if (GlidePerformanceTrace.Enabled) GlidePerformanceTrace.Mark("workspace_session_write_failed"); /* Session persistence is best-effort and must never prevent closing. */ }
    }

    /// <summary>Serializes the tabs now. A later snapshot always supersedes an earlier one, even if it is written first.</summary>
    internal static SessionSnapshot? Snapshot(IReadOnlyList<TabState> tabs, int activeIndex, string? settingsDirectory = null)
    {
        try
        {
            var doc = new SessionDocument { ActiveIndex = Math.Max(0, activeIndex) };
            for (var index = 0; index < tabs.Count; index++)
            {
                var tab = tabs[index];
                var active = index == activeIndex;
                switch (tab)
                {
                    case HomeTabState:
                        doc.Tabs.Add(new SessionTab { Kind = "Home", Active = active });
                        break;
                    case ImageTabState image:
                        doc.Tabs.Add(new SessionTab { Kind = "Image", Path = image.Path, ImageView = image.ViewState, Active = active });
                        break;
                    case BrowserTabState browser:
                        doc.Tabs.Add(new SessionTab
                        {
                            Kind = "Browser", Path = browser.Folder, BrowserNavigation = browser.Navigation, Active = active,
                            SelectedPath = string.IsNullOrWhiteSpace(browser.View?.SelectedPath) ? null : browser.View!.SelectedPath,
                            // Written whenever the tab's view is known (even 0), so a restore can tell "nothing
                            // selected, not scrolled" from an older file that carried no view at all.
                            ScrollY = browser.View is { } view ? (double.IsFinite(view.ScrollY) && view.ScrollY > 0 ? Math.Round(view.ScrollY, 1) : 0) : null
                        });
                        break;
                }
            }
            return new SessionSnapshot(settingsDirectory is null ? SessionPath : SessionPathIn(settingsDirectory), JsonSerializer.Serialize(doc, AppJsonContext.Default.SessionDocument),
                Interlocked.Increment(ref _sequence));
        }
        catch { return null; }
    }

    /// <summary>
    /// True after Back up and restore imported a last session in this process: the imported file must survive until the next
    /// start, so this process stops writing its own session (autosave and close) from then on.
    /// </summary>
    internal static bool ImportedSessionPending => Volatile.Read(ref _importedSessionPending) != 0;
    private static int _importedSessionPending;

    /// <summary>Tests: forget an imported session.</summary>
    internal static void ResetImportedSessionForTests() => Volatile.Write(ref _importedSessionPending, 0);

    /// <summary>The schema glide.workspace-session.json is written with (a backup import refuses newer ones).</summary>
    internal const int CurrentSchema = SchemaVersion;

    /// <summary>Back up and restore: the stored last session (null when there is none or it is unreadable).</summary>
    internal static SessionDocument? ReadDocumentForBackup()
    {
        try
        {
            var path = SessionPath;
            if (!File.Exists(path)) return null;
            var doc = JsonSerializer.Deserialize(File.ReadAllText(path), AppJsonContext.Default.SessionDocument);
            return doc is { Schema: SchemaVersion } ? doc : null;
        }
        catch { return null; }
    }

    /// <summary>
    /// Back up and restore: writes an imported (validated) session as the last session, atomically, and keeps this process
    /// from overwriting it (see <see cref="ImportedSessionPending"/>). It opens on the next start when the startup action is
    /// "Open last session". Throws when the file could not be written.
    /// </summary>
    internal static void WriteImported(SessionDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        document.Schema = SchemaVersion;
        var json = JsonSerializer.Serialize(document, AppJsonContext.Default.SessionDocument);
        lock (WriteLock)
        {
            var path = SessionPath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temp = path + "." + Environment.ProcessId + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllText(temp, json);
                File.Move(temp, path, true);
                Volatile.Write(ref _importedSessionPending, 1);
                _lastWrittenPath = path;
                _lastWrittenJson = json;
            }
            finally
            {
                try { if (File.Exists(temp)) File.Delete(temp); } catch { /* best-effort temp cleanup */ }
            }
        }
    }

    /// <summary>
    /// Atomic write: a temp file in the same folder, then a rename over the session file, so a crash
    /// mid-write leaves either the previous or the new file, never a truncated one. Skips a write that
    /// would not change the file and a snapshot that a newer one has already replaced.
    /// </summary>
    internal static void Write(SessionSnapshot snapshot)
    {
        lock (WriteLock)
        {
            // An imported last session waits for the next start; this process must not replace it.
            if (ImportedSessionPending) return;
            if (snapshot.Sequence < _lastWrittenSequence) return;
            if (string.Equals(_lastWrittenPath, snapshot.Path, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(_lastWrittenJson, snapshot.Json, StringComparison.Ordinal) && File.Exists(snapshot.Path))
            {
                _lastWrittenSequence = snapshot.Sequence;
                return;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(snapshot.Path)!);
            var temp = snapshot.Path + "." + Environment.ProcessId + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllText(temp, snapshot.Json);
                File.Move(temp, snapshot.Path, true);
                _lastWrittenSequence = snapshot.Sequence;
                _lastWrittenPath = snapshot.Path;
                _lastWrittenJson = snapshot.Json;
                Interlocked.Increment(ref _writeCount);
            }
            finally
            {
                try { if (File.Exists(temp)) File.Delete(temp); } catch { /* best-effort temp cleanup; the write outcome is already decided */ }
            }
        }
    }

    public static (IReadOnlyList<TabState> Tabs, int ActiveIndex)? Load()
    {
        if (GlidePerformanceTrace.Enabled) GlidePerformanceTrace.Mark("workspace_session_read_start");
        try
        {
            var path = SessionPath;
            if (!File.Exists(path)) return null;
            var doc = JsonSerializer.Deserialize(File.ReadAllText(path), AppJsonContext.Default.SessionDocument);
            if (doc is null || doc.Schema != SchemaVersion) return null;
            var tabs = new List<TabState>();
            var restoredActiveIndex = -1;
            foreach (var item in doc.Tabs ?? new())
            {
                TabState? restored = null;
                if (string.Equals(item.Kind, "Home", StringComparison.OrdinalIgnoreCase))
                    restored = new HomeTabState(Guid.NewGuid());
                else if (string.Equals(item.Kind, "Image", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(item.Path))
                    restored = new ImageTabState(Guid.NewGuid(), Path.GetFullPath(item.Path)) { ViewState = item.ImageView };
                else if (string.Equals(item.Kind, "Browser", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(item.Path))
                    restored = new BrowserTabState(Guid.NewGuid(), Path.GetFullPath(item.Path)) { Navigation = item.BrowserNavigation, View = RestoredView(item) };

                if (restored is null) continue;
                tabs.Add(restored);
                if (item.Active) restoredActiveIndex = tabs.Count - 1;
            }
            if (tabs.Count == 0) return null;
            if (restoredActiveIndex < 0) restoredActiveIndex = Math.Clamp(doc.ActiveIndex, 0, tabs.Count - 1); // schema-1 compatibility
            if (GlidePerformanceTrace.Enabled) GlidePerformanceTrace.Mark("workspace_session_read_end", $"tabs={tabs.Count};active={restoredActiveIndex}");
            return (tabs, restoredActiveIndex);
        }
        catch { if (GlidePerformanceTrace.Enabled) GlidePerformanceTrace.Mark("workspace_session_read_failed"); return null; }
    }

    private static BrowserTabViewState? RestoredView(SessionTab item)
    {
        string? selected = null;
        try { if (!string.IsNullOrWhiteSpace(item.SelectedPath)) selected = Path.GetFullPath(item.SelectedPath); }
        catch { /* a damaged value only loses the selection, never the session */ }
        if (selected is null && item.ScrollY is null) return null; // an older file: no view was recorded
        var scroll = item.ScrollY is { } y && double.IsFinite(y) && y > 0 ? y : 0;
        return new BrowserTabViewState(selected, scroll);
    }
}
