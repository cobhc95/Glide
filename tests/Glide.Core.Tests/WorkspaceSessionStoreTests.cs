using Glide.App.Settings;
using Glide.Core.Workspace;

namespace Glide.Core.Tests;

/// <summary>The "Open last session" file: optional Explorer selection/scroll fields, old files, atomic writes.</summary>
[Collection(StoreStaticsCollection.Name)]
[Trait("Area", "Tabs")]
public sealed class WorkspaceSessionStoreTests
{
    private const string FileName = "glide.workspace-session.json";

    private static void WithIsolatedStore(Action<string> action)
    {
        var dir = Path.Combine(Path.GetTempPath(), "glide-session-store-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var previous = SettingsStore.SettingsDirectoryOverrideForTests.Value;
        SettingsStore.SettingsDirectoryOverrideForTests.Value = dir;
        try { action(dir); }
        finally
        {
            SettingsStore.SettingsDirectoryOverrideForTests.Value = previous;
            TempDir.TryDelete(dir);
        }
    }

    private static string Folder(string name) => Path.Combine(Path.GetTempPath(), "glide-session-fixture", name);

    [Fact]
    public void Explorer_selection_and_scroll_round_trip()
    {
        WithIsolatedStore(_ =>
        {
            var folder = Folder("one");
            var file = Path.Combine(folder, "pic.png");
            var tabs = new TabState[]
            {
                new HomeTabState(Guid.NewGuid()),
                new BrowserTabState(Guid.NewGuid(), folder) { View = new BrowserTabViewState(file, 412.5) }
            };
            WorkspaceSessionStore.Save(tabs, 1);

            var loaded = WorkspaceSessionStore.Load();
            Assert.NotNull(loaded);
            var browser = Assert.IsType<BrowserTabState>(loaded!.Value.Tabs[1]);
            Assert.Equal(1, loaded.Value.ActiveIndex);
            Assert.Equal(folder, browser.Folder);
            Assert.Equal(new BrowserTabViewState(file, 412.5), browser.View);
        });
    }

    [Fact]
    public void Nothing_selected_and_not_scrolled_is_remembered_as_a_known_empty_view()
    {
        WithIsolatedStore(dir =>
        {
            WorkspaceSessionStore.Save(new TabState[] { new BrowserTabState(Guid.NewGuid(), Folder("two")) { View = new BrowserTabViewState(null, 0) } }, 0);
            var browser = Assert.IsType<BrowserTabState>(WorkspaceSessionStore.Load()!.Value.Tabs[0]);
            Assert.Equal(new BrowserTabViewState(null, 0), browser.View);
            Assert.Contains("\"ScrollY\"", File.ReadAllText(Path.Combine(dir, FileName)), StringComparison.OrdinalIgnoreCase);
        });
    }

    [Fact]
    public void Old_session_file_without_the_new_fields_still_loads()
    {
        WithIsolatedStore(dir =>
        {
            var folder = Folder("old").Replace("\\", "\\\\");
            File.WriteAllText(Path.Combine(dir, FileName), $$"""
                {
                  "Schema": 1,
                  "ActiveIndex": 1,
                  "Tabs": [
                    { "Kind": "Home", "Path": "", "Active": false },
                    { "Kind": "Image", "Path": "{{folder}}\\a.png", "Active": false },
                    { "Kind": "Browser", "Path": "{{folder}}", "Active": true,
                      "BrowserNavigation": { "History": [ "{{folder}}" ], "Index": 0, "Highlights": [ null ] } }
                  ]
                }
                """);

            var loaded = WorkspaceSessionStore.Load();
            Assert.NotNull(loaded);
            Assert.Equal(3, loaded!.Value.Tabs.Count);
            var browser = Assert.IsType<BrowserTabState>(loaded.Value.Tabs[2]);
            Assert.Equal(2, loaded.Value.ActiveIndex);
            Assert.Equal(Folder("old"), browser.Folder);
            Assert.Null(browser.View); // an older file recorded no selection or scroll
            Assert.Equal(0, browser.Navigation!.Index);
        });
    }

    [Fact]
    public void Unknown_future_fields_and_a_damaged_selection_never_lose_the_session()
    {
        WithIsolatedStore(dir =>
        {
            var folder = Folder("damaged").Replace("\\", "\\\\");
            File.WriteAllText(Path.Combine(dir, FileName), $$"""
                {
                  "Schema": 1, "ActiveIndex": 0, "SomethingNew": { "x": 1 },
                  "Tabs": [
                    { "Kind": "Browser", "Path": "{{folder}}", "Active": true, "SelectedPath": "bad\u0000path", "ScrollY": 12, "Future": true }
                  ]
                }
                """);
            var browser = Assert.IsType<BrowserTabState>(WorkspaceSessionStore.Load()!.Value.Tabs[0]);
            Assert.Equal(Folder("damaged"), browser.Folder);
            Assert.Equal(new BrowserTabViewState(null, 12), browser.View);
        });
    }

    [Fact]
    public void Non_explorer_tabs_do_not_gain_new_fields_in_the_file()
    {
        WithIsolatedStore(dir =>
        {
            WorkspaceSessionStore.Save(new TabState[] { new HomeTabState(Guid.NewGuid()), new ImageTabState(Guid.NewGuid(), Path.Combine(Folder("img"), "a.png")) }, 0);
            var text = File.ReadAllText(Path.Combine(dir, FileName));
            Assert.DoesNotContain("SelectedPath", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("ScrollY", text, StringComparison.OrdinalIgnoreCase);
        });
    }

    [Fact]
    public void Write_is_atomic_and_leaves_a_valid_file_without_temp_leftovers()
    {
        WithIsolatedStore(dir =>
        {
            var path = Path.Combine(dir, FileName);
            WorkspaceSessionStore.Save(new TabState[] { new BrowserTabState(Guid.NewGuid(), Folder("first")) }, 0);
            var first = File.ReadAllText(path);

            // A crash in the middle of an earlier write leaves a half-written temp file beside the real one.
            File.WriteAllText(path + ".4242.deadbeef.tmp", "{ \"Schema\": 1, \"Tabs\": [ { \"Kind\": \"Brow");
            var beforeCrashLoad = WorkspaceSessionStore.Load();
            Assert.Equal(Folder("first"), Assert.IsType<BrowserTabState>(beforeCrashLoad!.Value.Tabs[0]).Folder);
            File.Delete(path + ".4242.deadbeef.tmp");

            // Many overlapping saves (several windows / debounce flushes) still end in one complete document.
            Parallel.For(0, 60, i =>
                WorkspaceSessionStore.Save(new TabState[] { new BrowserTabState(Guid.NewGuid(), Folder("w" + i)) { View = new BrowserTabViewState(null, i + 1) } }, 0));

            Assert.NotEqual(first, File.ReadAllText(path));
            Assert.Empty(Directory.GetFiles(dir, "*.tmp"));
            Assert.NotNull(WorkspaceSessionStore.Load());
        });
    }

    [Fact]
    public void A_newer_snapshot_is_never_overwritten_by_an_older_one_written_late()
    {
        WithIsolatedStore(_ =>
        {
            var older = WorkspaceSessionStore.Snapshot(new TabState[] { new BrowserTabState(Guid.NewGuid(), Folder("older")) }, 0)!;
            var newer = WorkspaceSessionStore.Snapshot(new TabState[] { new BrowserTabState(Guid.NewGuid(), Folder("newer")) }, 0)!;
            WorkspaceSessionStore.Write(newer);
            WorkspaceSessionStore.Write(older); // e.g. a debounced background write finishing after the close-time save
            Assert.Equal(Folder("newer"), Assert.IsType<BrowserTabState>(WorkspaceSessionStore.Load()!.Value.Tabs[0]).Folder);
        });
    }

    [Fact]
    public void An_unchanged_session_is_not_rewritten()
    {
        WithIsolatedStore(dir =>
        {
            var tabs = new TabState[] { new BrowserTabState(Guid.NewGuid(), Folder("same")) { View = new BrowserTabViewState(null, 0) } };
            WorkspaceSessionStore.Save(tabs, 0);
            var writes = WorkspaceSessionStore.WriteCount;
            WorkspaceSessionStore.Save(tabs, 0);
            Assert.Equal(writes, WorkspaceSessionStore.WriteCount);
            File.Delete(Path.Combine(dir, FileName));
            WorkspaceSessionStore.Save(tabs, 0); // a deleted file is always recreated
            Assert.True(File.Exists(Path.Combine(dir, FileName)));
        });
    }

    [Fact]
    public void The_remembered_view_belongs_to_its_folder()
    {
        var workspace = new WorkspaceState(createHome: false);
        var tab = Assert.IsType<BrowserTabState>(workspace.AddBrowser(Folder("a")));
        Assert.True(workspace.ReplaceTab(tab with { View = new BrowserTabViewState(Path.Combine(Folder("a"), "x.png"), 50) }));

        Assert.True(workspace.ReplaceActiveBrowserFolder(Folder("a"))); // refresh of the same folder keeps it
        Assert.NotNull(Assert.IsType<BrowserTabState>(workspace.Active).View);

        Assert.True(workspace.ReplaceActiveBrowserFolder(Folder("b"))); // another folder drops it
        Assert.Null(Assert.IsType<BrowserTabState>(workspace.Active).View);
    }
}
