using System.Text.Json;
using Glide.App.Settings;
using Glide.Core.Commands;

namespace Glide.Core.Tests;

/// <summary>Core-side guards for the failures the real-desktop retest of 5.1.7 found (docs/feedback/ui-test-5.1.7-desktop.md).</summary>
[Trait("Area", "Regression")]
public sealed class UiTest517RetestCoreTests
{
    private static void WithIsolatedStore(Action action)
    {
        var dir = Path.Combine(Path.GetTempPath(), "glide-retest517-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var previous = SettingsStore.SettingsDirectoryOverrideForTests.Value;
        SettingsStore.SettingsDirectoryOverrideForTests.Value = dir;
        try { action(); }
        finally
        {
            SettingsStore.SettingsDirectoryOverrideForTests.Value = previous;
            TempDir.TryDelete(dir);
        }
    }

    // ------------------------------------------------------------------ B: a default added after the row was saved empty

    [Fact]
    public void The_real_settings_file_with_an_empty_tag_sidebar_row_gets_Ctrl_Shift_G_and_the_menu_shows_it()
    {
        WithIsolatedStore(() =>
        {
            // What the user's glide.settings.json holds: written by a build where the action had no default key.
            var path = SettingsStore.GetSettingsPath();
            var node = System.Text.Json.Nodes.JsonNode.Parse(JsonSerializer.Serialize(new GlideSettingsState { SettingsSchemaVersion = 24 }, Glide.App.AppJsonContext.Default.GlideSettingsState))!;
            node["Hotkeys"]!["tags.toggleSidebar"] = new System.Text.Json.Nodes.JsonArray();
            File.WriteAllText(path, node.ToJsonString());
            Assert.Contains("\"tags.toggleSidebar\":[]", File.ReadAllText(path));

            var loaded = SettingsStore.Load();
            Assert.Equal(new[] { "Ctrl+Shift+G" }, loaded.Hotkeys["tags.toggleSidebar"]);
            // The menu text and the key lookup read the same map, so the shortcut now shows and fires.
            Assert.Equal(new[] { "Ctrl+Shift+G" }, HotkeyCatalog.ShortcutsForAction("tags.toggleSidebar", loaded.Hotkeys).ToArray());

            // The user removes it afterwards (saved at the current schema): that choice stays.
            loaded.Hotkeys["tags.toggleSidebar"] = [];
            SettingsStore.Save(loaded);
            var again = SettingsStore.Load();
            Assert.Empty(again.Hotkeys["tags.toggleSidebar"]);
        });
    }

    [Fact]
    public void A_tag_sidebar_row_the_user_bound_or_whose_key_is_taken_is_left_alone_and_a_schema_25_removal_stays()
    {
        const string id = HotkeyDefaultMigrations.TagSidebarId;
        var mine = new Dictionary<string, List<string>> { [id] = ["Ctrl+Alt+T"] };
        Assert.False(HotkeyDefaultMigrations.MigrateTagSidebar(mine, 24));
        Assert.Equal(["Ctrl+Alt+T"], mine[id]);
        var taken = new Dictionary<string, List<string>> { [id] = [], ["view.fit"] = ["ctrl+shift+g"] };
        Assert.False(HotkeyDefaultMigrations.MigrateTagSidebar(taken, 24));
        Assert.Empty(taken[id]);
        var removed = new Dictionary<string, List<string>> { [id] = [] };
        Assert.False(HotkeyDefaultMigrations.MigrateTagSidebar(removed, HotkeyDefaultMigrations.TagSidebarDefaultSchema));
        Assert.Empty(removed[id]);
        var missing = new Dictionary<string, List<string>>();
        Assert.True(HotkeyDefaultMigrations.MigrateTagSidebar(missing, 24));
        Assert.Equal(["Ctrl+Shift+G"], missing[id]);
    }

    // ------------------------------------------------------------------ E / F: offsets are stored as typed and described readably

    [Fact]
    public void Offsets_are_stored_exactly_as_typed_and_only_limited_when_drawn()
    {
        Assert.Equal((5000, -12345), Glide.Core.Settings.TextOverlayOffsets.Get(Glide.Core.Settings.TextOverlayOffsets.Set(null, "Top right", 5000, -12345), "Top right"));
        var custom = Glide.Core.Settings.TextOverlayOffsets.SetCustom(null, false, "Top right", 100000, 0);
        Assert.Equal(("Top right", 100000, 0), Glide.Core.Settings.TextOverlayOffsets.GetCustom(custom, false));
        // Drawn: a 5000 px offset is limited to the picture area (a few px inside its edge), without touching the saved value.
        var (x, y) = Glide.Core.Settings.TextOverlayLayout.ClampOffset("Top right", 1200, 700, 80, 24, 5000, 0, 15, 28, 0, 4);
        Assert.True(x <= 1200 - 80 - 4 - (1200 - 15 - 80), $"clamped x {x}");
        Assert.Equal(0, y);
    }

    [Fact]
    public void The_unsaved_changes_list_shows_text_overlay_offsets_readably()
    {
        var committed = new GlideSettingsState();
        var staged = committed.CloneState();
        Glide.Core.Settings.TextOverlayPlacements.SetCustom(staged, false, "Top right", 500, 0);
        var changes = SettingsChangeSet.Compute(committed, staged);
        var offset = Assert.Single(changes, c => c.Label == "Text overlay offset (px)");
        Assert.Equal("Tabs along the top: X 0, Y 0", offset.Before);
        Assert.Equal("Tabs along the top: X 500, Y 0", offset.After);
        foreach (var change in changes)
        {
            Assert.DoesNotContain("@", change.After);
            Assert.DoesNotContain("Custom top tabs", change.Before + change.After);
            Assert.DoesNotContain("Picture counter offsets", change.Label);
        }
    }

    [Fact]
    public void Compound_settings_are_described_by_their_labels_not_their_stored_ids()
    {
        var committed = new GlideSettingsState();
        var staged = committed.CloneState();
        staged.StatusBarGroups = "zoom,fit,a:navigation.toggleLoopFolder";
        staged.TitleBarButtons = ["nav.back", "settings"];
        var changes = SettingsChangeSet.Compute(committed, staged);
        var status = Assert.Single(changes, c => c.Label == "Status bar buttons");
        Assert.Equal("Default layout", status.Before);
        Assert.Equal("Zoom in / out, Fit modes, Loop this folder (this tab)", status.After);
        var title = Assert.Single(changes, c => c.Label == "Title bar buttons");
        Assert.DoesNotContain("nav.back", title.After);
        Assert.Contains("Settings", title.After);
    }
}
