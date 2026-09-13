using System.Text.Json;
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

    private sealed class SessionDocument
    {
        public int Schema { get; set; } = SchemaVersion;
        public int ActiveIndex { get; set; }
        public List<SessionTab> Tabs { get; set; } = new();
    }

    private sealed class SessionTab
    {
        public string Kind { get; set; } = "Home";
        public string Path { get; set; } = string.Empty;
        public bool Active { get; set; }
        public ImageTabViewState? ImageView { get; set; }
        public BrowserNavigationState? BrowserNavigation { get; set; }
    }

    public static void Save(IReadOnlyList<TabState> tabs, int activeIndex)
    {
        if (GlidePerformanceTrace.Enabled) GlidePerformanceTrace.Mark("workspace_session_write_start", $"tabs={tabs.Count}");
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
                        doc.Tabs.Add(new SessionTab { Kind = "Browser", Path = browser.Folder, BrowserNavigation = browser.Navigation, Active = active });
                        break;
                }
            }
            Directory.CreateDirectory(SettingsStore.GetSettingsDirectory());
            var path = Path.Combine(SettingsStore.GetSettingsDirectory(), FileName);
            var temp = path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(doc, Options()));
            File.Move(temp, path, true);
            if (GlidePerformanceTrace.Enabled) GlidePerformanceTrace.Mark("workspace_session_write_end", $"tabs={doc.Tabs.Count}");
        }
        catch { if (GlidePerformanceTrace.Enabled) GlidePerformanceTrace.Mark("workspace_session_write_failed"); /* Session persistence is best-effort and must never prevent closing. */ }
    }

    public static (IReadOnlyList<TabState> Tabs, int ActiveIndex)? Load()
    {
        if (GlidePerformanceTrace.Enabled) GlidePerformanceTrace.Mark("workspace_session_read_start");
        try
        {
            var path = Path.Combine(SettingsStore.GetSettingsDirectory(), FileName);
            if (!File.Exists(path)) return null;
            var doc = JsonSerializer.Deserialize<SessionDocument>(File.ReadAllText(path), Options());
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
                    restored = new BrowserTabState(Guid.NewGuid(), Path.GetFullPath(item.Path)) { Navigation = item.BrowserNavigation };

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

    private static JsonSerializerOptions Options() => new() { WriteIndented = true, PropertyNameCaseInsensitive = true };
}
