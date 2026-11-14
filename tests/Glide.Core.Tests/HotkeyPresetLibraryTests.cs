using System.Text.Json;
using Glide.App;
using Glide.App.Settings;
using Glide.Core.Commands;

namespace Glide.Core.Tests;

/// <summary>Custom hotkey presets: save, apply, rename and delete next to the built-ins; persistence and profile export/import.</summary>
public sealed class HotkeyPresetLibraryTests
{
    private static Dictionary<string, List<string>> Modified()
    {
        var map = HotkeyCatalog.CreateDefaultMap();
        map["view.zoomIn"] = new List<string> { "Ctrl+Alt+Z" };
        map["view.rotateLeft"] = new List<string>();
        return map;
    }

    [Fact]
    public void Save_stores_a_snapshot_that_later_edits_do_not_change()
    {
        var presets = new List<CustomHotkeyPreset>();
        var map = Modified();

        Assert.Equal(HotkeyPresetResult.Ok, HotkeyPresetLibrary.Save(presets, "  My   keys ", map, out var saved));
        Assert.Equal("My keys", saved);                     // trimmed, inner whitespace collapsed
        map["view.zoomIn"].Add("Ctrl+Q");                   // editing the live map afterwards...

        var resolved = HotkeyPresetLibrary.Resolve(presets, "my keys");   // ...is not part of the preset
        Assert.Equal(new[] { "Ctrl+Alt+Z" }, resolved["view.zoomIn"]);
        Assert.Empty(resolved["view.rotateLeft"]);
        Assert.Equal(HotkeyCatalog.All.Count, resolved.Count);
    }

    [Fact]
    public void Saving_under_an_existing_custom_name_overwrites_it_and_keeps_its_spelling()
    {
        var presets = new List<CustomHotkeyPreset>();
        HotkeyPresetLibrary.Save(presets, "Work", HotkeyCatalog.CreateDefaultMap(), out _);
        Assert.Equal(HotkeyPresetResult.Overwritten, HotkeyPresetLibrary.Save(presets, "WORK", Modified(), out var saved));
        Assert.Equal("Work", saved);
        Assert.Single(presets);
        Assert.Equal(new[] { "Ctrl+Alt+Z" }, HotkeyPresetLibrary.Resolve(presets, "work")["view.zoomIn"]);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Blank_names_are_rejected(string? name)
    {
        var presets = new List<CustomHotkeyPreset>();
        Assert.Equal(HotkeyPresetResult.InvalidName, HotkeyPresetLibrary.Save(presets, name, Modified(), out _));
        Assert.Empty(presets);
    }

    [Theory]
    [InlineData("Glide default")]
    [InlineData("windows photos")]
    [InlineData("IrfanView")]
    [InlineData("  nomacs ")]
    [InlineData("FastStone")]
    [InlineData("XnView MP")]
    public void Built_in_names_cannot_be_used_for_custom_presets(string name)
    {
        var presets = new List<CustomHotkeyPreset>();
        Assert.Equal(HotkeyPresetResult.BuiltInName, HotkeyPresetLibrary.Save(presets, name, Modified(), out _));
        Assert.Empty(presets);
        Assert.True(HotkeyPresetLibrary.IsBuiltIn(name));
    }

    [Fact]
    public void Built_in_presets_are_never_deletable_or_renamable()
    {
        var presets = new List<CustomHotkeyPreset>();
        HotkeyPresetLibrary.Save(presets, "Mine", Modified(), out _);
        foreach (var builtIn in HotkeyCatalog.PresetNames)
        {
            Assert.Equal(HotkeyPresetResult.BuiltInName, HotkeyPresetLibrary.Delete(presets, builtIn));
            Assert.Equal(HotkeyPresetResult.BuiltInName, HotkeyPresetLibrary.Rename(presets, builtIn, "Whatever", out _));
            // ...and a custom preset can't be renamed onto a built-in name.
            Assert.Equal(HotkeyPresetResult.BuiltInName, HotkeyPresetLibrary.Rename(presets, "Mine", builtIn, out _));
        }
        Assert.Single(presets);
        Assert.Equal("Mine", presets[0].Name);
    }

    [Fact]
    public void Rename_changes_only_the_name_and_refuses_clashes_and_blanks()
    {
        var presets = new List<CustomHotkeyPreset>();
        HotkeyPresetLibrary.Save(presets, "One", Modified(), out _);
        HotkeyPresetLibrary.Save(presets, "Two", HotkeyCatalog.CreateDefaultMap(), out _);

        Assert.Equal(HotkeyPresetResult.NameInUse, HotkeyPresetLibrary.Rename(presets, "One", "two", out _));
        Assert.Equal(HotkeyPresetResult.InvalidName, HotkeyPresetLibrary.Rename(presets, "One", "  ", out _));
        Assert.Equal(HotkeyPresetResult.NotFound, HotkeyPresetLibrary.Rename(presets, "Nope", "X", out _));
        Assert.Equal(HotkeyPresetResult.Ok, HotkeyPresetLibrary.Rename(presets, "One", "Uno", out var renamed));
        Assert.Equal("Uno", renamed);
        Assert.Equal(new[] { "Ctrl+Alt+Z" }, HotkeyPresetLibrary.Resolve(presets, "Uno")["view.zoomIn"]);
        // Case-only rename of itself is allowed.
        Assert.Equal(HotkeyPresetResult.Ok, HotkeyPresetLibrary.Rename(presets, "Uno", "UNO", out _));
    }

    [Fact]
    public void Delete_removes_only_the_named_custom_preset()
    {
        var presets = new List<CustomHotkeyPreset>();
        HotkeyPresetLibrary.Save(presets, "One", Modified(), out _);
        HotkeyPresetLibrary.Save(presets, "Two", Modified(), out _);
        Assert.Equal(HotkeyPresetResult.Ok, HotkeyPresetLibrary.Delete(presets, "one"));
        Assert.Equal(new[] { "Two" }, presets.Select(p => p.Name));
        Assert.Equal(HotkeyPresetResult.NotFound, HotkeyPresetLibrary.Delete(presets, "One"));
        Assert.Equal(HotkeyCatalog.PresetNames.Concat(new[] { "Two" }), HotkeyPresetLibrary.AllNames(presets));
    }

    [Fact]
    public void Applying_a_built_in_name_gives_the_built_in_map_and_a_custom_name_gives_the_saved_map()
    {
        var presets = new List<CustomHotkeyPreset>();
        HotkeyPresetLibrary.Save(presets, "Mine", Modified(), out _);
        var photos = HotkeyPresetLibrary.Resolve(presets, "Windows Photos");
        Assert.Equal(HotkeyCatalog.CreatePresetMap("Windows Photos")["view.fullscreen"], photos["view.fullscreen"]);
        Assert.Equal(new[] { "Ctrl+Alt+Z" }, HotkeyPresetLibrary.Resolve(presets, "Mine")["view.zoomIn"]);
        // Unknown names fail safe to the Glide defaults.
        Assert.Equal(HotkeyCatalog.CreateDefaultMap()["view.zoomIn"], HotkeyPresetLibrary.Resolve(presets, "nothing")["view.zoomIn"]);
    }

    [Fact]
    public void Actions_added_after_a_preset_was_saved_get_defaults_without_creating_duplicates()
    {
        var presets = new List<CustomHotkeyPreset>
        {
            // An "old" preset that predates most actions and has already given "F11" to Next tab.
            new() { Name = "Old", Hotkeys = new(StringComparer.OrdinalIgnoreCase) { ["tab.next"] = new() { "F11" } } }
        };
        var map = HotkeyPresetLibrary.Resolve(presets, "Old");
        Assert.Equal(HotkeyCatalog.All.Count, map.Count);
        Assert.Equal(new[] { "F11" }, map["tab.next"]);
        Assert.DoesNotContain("F11", map["view.fullscreen"]);          // its default F11 would have been a duplicate
        Assert.Contains("F", map["view.fullscreen"]);
        var dupes = map.SelectMany(kv => kv.Value).GroupBy(s => s, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1);
        Assert.Empty(dupes);
    }

    [Fact]
    public void Presets_survive_a_settings_save_and_load_through_the_source_generated_json_context()
    {
        WithIsolatedStore(() =>
        {
            var state = new GlideSettingsState();
            HotkeyPresetLibrary.Save(state.CustomHotkeyPresets, "Mine", Modified(), out _);
            SettingsStore.Save(state);

            Assert.Contains("CustomHotkeyPresets", File.ReadAllText(Path.Combine(SettingsStore.GetSettingsDirectory(), "glide.settings.json")));
            var loaded = SettingsStore.Load();
            var preset = Assert.Single(loaded.CustomHotkeyPresets);
            Assert.Equal("Mine", preset.Name);
            Assert.Equal(new[] { "Ctrl+Alt+Z" }, preset.Hotkeys["view.zoomIn"]);
            Assert.Empty(preset.Hotkeys["view.rotateLeft"]);
        });
    }

    [Fact]
    public void Older_settings_files_without_presets_still_load_with_an_empty_list()
    {
        WithIsolatedStore(() =>
        {
            File.WriteAllText(Path.Combine(SettingsStore.GetSettingsDirectory(), "glide.settings.json"),
                "{\"SettingsSchemaVersion\":7,\"StatusBarAlignment\":\"Right\"}");
            var loaded = SettingsStore.Load();
            Assert.Empty(loaded.CustomHotkeyPresets);
            Assert.Equal("Right", loaded.StatusBarAlignment);
            Assert.False(loaded.StatusAutoHide);
        });
    }

    [Fact]
    public void Corrupt_preset_entries_in_a_file_are_dropped_on_load_but_good_ones_stay()
    {
        WithIsolatedStore(() =>
        {
            File.WriteAllText(Path.Combine(SettingsStore.GetSettingsDirectory(), "glide.settings.json"),
                "{\"CustomHotkeyPresets\":[{\"Name\":\"\",\"Hotkeys\":{}},{\"Name\":\"IrfanView\",\"Hotkeys\":{}},{\"Name\":\"Good\",\"Hotkeys\":{\"view.zoomIn\":[\"Z\",\"\",\"z\"],\"unknown.action\":[\"Q\"]}},{\"Name\":\"good\",\"Hotkeys\":{}}]}");
            var loaded = SettingsStore.Load();
            var preset = Assert.Single(loaded.CustomHotkeyPresets);
            Assert.Equal("Good", preset.Name);
            Assert.Equal(new[] { "Z" }, preset.Hotkeys["view.zoomIn"]);
            Assert.False(preset.Hotkeys.ContainsKey("unknown.action"));
        });
    }

    [Fact]
    public void Profile_export_and_import_carry_the_custom_presets_with_no_reflection_serializer()
    {
        var state = new GlideSettingsState();
        HotkeyPresetLibrary.Save(state.CustomHotkeyPresets, "Travelling keys", Modified(), out _);

        var document = new SettingsWindow.ProfileTransferDocument { Name = "Profile", Settings = state.CloneState() };
        var json = JsonSerializer.Serialize(document, AppJsonContext.Default.ProfileTransferDocument);
        var imported = JsonSerializer.Deserialize(json, AppJsonContext.Default.ProfileTransferDocument)!.Settings;

        var preset = Assert.Single(imported.CustomHotkeyPresets);
        Assert.Equal("Travelling keys", preset.Name);
        Assert.Equal(new[] { "Ctrl+Alt+Z" }, preset.Hotkeys["view.zoomIn"]);

        // The whole-settings export document too.
        var full = JsonSerializer.Serialize(new SettingsWindow.SettingsTransferDocument { Settings = state.CloneState() }, AppJsonContext.Default.SettingsTransferDocument);
        Assert.Contains("Travelling keys", full);
    }

    [Fact]
    public void CloneState_deep_copies_presets_and_content_equality_notices_a_difference()
    {
        var state = new GlideSettingsState();
        HotkeyPresetLibrary.Save(state.CustomHotkeyPresets, "A", Modified(), out _);
        var clone = state.CloneState();
        Assert.True(state.ContentEquals(clone));

        clone.CustomHotkeyPresets[0].Hotkeys["view.zoomIn"].Add("Ctrl+Q");   // must not leak into the original
        Assert.DoesNotContain("Ctrl+Q", state.CustomHotkeyPresets[0].Hotkeys["view.zoomIn"]);
        Assert.False(state.ContentEquals(clone));

        var renamed = state.CloneState();
        renamed.CustomHotkeyPresets[0].Name = "B";
        Assert.False(state.ContentEquals(renamed));
        var changes = SettingsChangeSet.Compute(state, renamed);
        Assert.Contains(changes, c => c.Name == "Custom keyboard shortcut presets");
    }

    private static void WithIsolatedStore(Action action)
    {
        var root = Path.Combine(Path.GetTempPath(), "Glide.Settings.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var previous = SettingsStore.SettingsDirectoryOverrideForTests.Value;
        SettingsStore.SettingsDirectoryOverrideForTests.Value = root;
        try { action(); }
        finally
        {
            SettingsStore.SettingsDirectoryOverrideForTests.Value = previous;
            try { Directory.Delete(root, true); } catch { }
        }
    }
}
