using System.Reflection;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Glide.App.Settings;

namespace Glide.Core.Tests;

/// <summary>
/// Stage C (R9): the plain toggles are loaded and read through <see cref="SettingAccessors.Values"/>, not by hand-written
/// LoadControls / ReadControls lines. These tests pin that every such toggle round-trips both ways and that the accessor
/// table agrees with the catalogue and the state defaults.
/// </summary>
[Collection(AvaloniaHeadlessCollection.Name)]
[Trait("Area", "Settings")]
public sealed class SettingBindingTableTests
{
    public SettingBindingTableTests(AvaloniaHeadlessFixture fixture) => _ = fixture;

    private static readonly HashSet<string> LegacyMappedIds = new(StringComparer.OrdinalIgnoreCase)
    {
        "general.newExplorerTabLastLocation", "general.newExplorerTabDefaultDirectory", "appearance.explorerTheme",
        "tabs.showBackButton", "tabs.showForwardButton", "folderNav.explore"
    };

    [Fact]
    public void Every_value_accessor_is_a_catalogued_setting_and_round_trips_on_a_state()
    {
        var catalog = SettingsCatalog.All.ToDictionary(s => s.Id, StringComparer.OrdinalIgnoreCase);
        foreach (var (id, accessor) in SettingAccessors.Values)
        {
            // A few legacy editors have a control-map id but no catalogue entry (kept for search/tooltips); they still load/read.
            Assert.True(catalog.ContainsKey(id) || LegacyMappedIds.Contains(id), $"accessor for unknown setting {id}");
            var state = new GlideSettingsState();
            object probe = accessor.Type switch
            {
                SettingAccessors.ValueKind.Bool => !(bool)accessor.Get(state)!,
                SettingAccessors.ValueKind.Int => (int)accessor.Get(state)! + 1,
                SettingAccessors.ValueKind.Double => (double)accessor.Get(state)! + 0.5,
                // tabs.switchToNewTabs is a choice stored as a bool: it only takes its listed options.
                _ when id == "tabs.switchToNewTabs" => Glide.Core.Settings.SettingActionCatalog.Choices[id].First(o => o != (string)accessor.Get(state)!),
                _ => accessor.Get(state) + "\u0001probe"
            };
            accessor.Set(state, probe);
            Assert.Equal(probe, accessor.Get(state));
            Assert.False(state.ContentEquals(new GlideSettingsState()), $"{id}: accessor wrote nothing");
        }
    }

    [Fact]
    public void Catalogue_defaults_match_the_state_defaults_for_every_toggle_accessor()
    {
        var defaults = new GlideSettingsState();
        var mismatches = new List<string>();
        foreach (var setting in SettingsCatalog.All)
        {
            if (!SettingAccessors.Values.TryGetValue(setting.Id, out var accessor) || accessor.Type != SettingAccessors.ValueKind.Bool) continue;
            if (setting.DefaultValue is bool expected && (bool)accessor.Get(defaults)! != expected) mismatches.Add(setting.Id);
        }
        Assert.Empty(mismatches);
    }

    [Fact]
    public void Every_mapped_toggle_loads_and_reads_through_the_table_in_both_directions()
    {
        AvaloniaHeadlessFixture.RunOnUIThread(() =>
        {
            var flipped = new GlideSettingsState();
            var bools = SettingAccessors.Values.Values.Where(a => a.Type == SettingAccessors.ValueKind.Bool).ToArray();
            foreach (var accessor in bools) accessor.Set(flipped, !(bool)accessor.Get(new GlideSettingsState())!);
            var window = new SettingsWindow(flipped, (_, _) => { });
            try
            {
                var controls = (Dictionary<string, Control>)typeof(SettingsWindow).GetField("_settingControls", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(window)!;
                var read = (GlideSettingsState)typeof(SettingsWindow).GetMethod("ReadControls", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(window, null)!;
                var checkedCount = 0;
                foreach (var accessor in bools)
                {
                    if (!controls.TryGetValue(accessor.Id, out var control) || control is not ToggleButton toggle) continue;
                    checkedCount++;
                    Assert.True((bool)accessor.Get(flipped)! == (toggle.IsChecked == true), $"{accessor.Id}: control not loaded from the state");
                    Assert.True((bool)accessor.Get(flipped)! == (bool)accessor.Get(read)!, $"{accessor.Id}: ReadControls lost the value");
                }
                Assert.True(checkedCount >= 120, $"only {checkedCount} table-driven toggles");
            }
            finally { window.Close(); }
        });
    }
}
