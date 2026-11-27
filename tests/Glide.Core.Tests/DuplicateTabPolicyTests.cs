using Glide.App.Settings;
using Glide.Core.Settings;
using Glide.Core.Workspace;

namespace Glide.Core.Tests;

/// <summary>
/// Round 4 D1/D2/D5/F2: the 5.1.0 middle-click side / position settings became "Duplicated tabs open [position] in the
/// [side]"; the same key for both new-tab keys inverts both; menus show the key hint.
/// </summary>
[Trait("Area", "Tabs")]
public sealed class DuplicateTabPolicyTests
{
    [Theory]
    [InlineData("next to the current tab", "foreground", false, true)]
    [InlineData("at the end", "foreground", true, true)]
    [InlineData("next to the current tab", "background", false, false)]
    [InlineData("at the end", "background", true, false)]
    [InlineData("??", "??", false, true)] // unknown values fall back to the defaults
    public void Duplicate_resolves_position_and_side(string position, string side, bool atEnd, bool activate) =>
        Assert.Equal(new NewTabOpening(atEnd, activate), NewTabPolicy.ForDuplicate(position, side));

    [Fact]
    public void Defaults_are_next_to_current_and_foreground_and_match_the_catalogue()
    {
        var state = new GlideSettingsState();
        Assert.Equal(DuplicateTabPositions.NextToCurrent, state.DuplicateTabPosition);
        Assert.Equal(DuplicateTabSides.Foreground, state.DuplicateTabSide);
        Assert.Equal(state.DuplicateTabPosition, SettingsCatalog.All.Single(e => e.Id == "tabs.duplicatePosition").DefaultValue);
        Assert.Equal(state.DuplicateTabSide, SettingsCatalog.All.Single(e => e.Id == "tabs.duplicateSide").DefaultValue);
        Assert.DoesNotContain(SettingsCatalog.All, e => e.Id.StartsWith("tabs.middleClick", StringComparison.Ordinal));
    }

    [Fact]
    public void Choice_lists_match_the_policy_values()
    {
        Assert.Equal(DuplicateTabPositions.All, SettingActionCatalog.Choices["tabs.duplicatePosition"]);
        Assert.Equal(DuplicateTabSides.All, SettingActionCatalog.Choices["tabs.duplicateSide"]);
    }

    [Theory]
    [InlineData(23, "at the end", "background", "next to the current tab", "foreground")]
    [InlineData(24, "at the end", "background", "at the end", "background")]
    [InlineData(24, "??", "??", "next to the current tab", "foreground")]
    public void Settings_files_are_migrated(int schema, string position, string side, string expectedPosition, string expectedSide) =>
        Assert.Equal((expectedPosition, expectedSide), SettingsStore.MigrateDuplicateTabSettings(schema, position, side));

    [Fact]
    public void Legacy_middle_click_keys_in_an_old_file_are_dropped_and_duplicate_defaults_apply()
    {
        var loaded = SettingsStore.MigrateFromJson(
            """{"SettingsSchemaVersion":23,"MiddleClickNewTabSide":"foreground","MiddleClickNewTabPosition":"at the end"}""", isSettingsFile: false);
        Assert.Equal(DuplicateTabPositions.NextToCurrent, loaded.DuplicateTabPosition);
        Assert.Equal(DuplicateTabSides.Foreground, loaded.DuplicateTabSide);
        Assert.Equal(SettingsStore.CurrentSettingsSchema, loaded.SettingsSchemaVersion);
    }

    [Theory]
    // position key, foreground key, shift / ctrl / alt -> at end, activate (New tabs open next to current, foreground)
    [InlineData("Shift", "Shift", true, false, false, true, false)] // D2: one key for both inverts both
    [InlineData("Shift", "Shift", false, false, false, false, true)]
    [InlineData("Shift", "Alt", true, false, false, true, true)]
    public void Same_key_for_both_inverts_both(string positionKey, string foregroundKey, bool shift, bool ctrl, bool alt, bool atEnd, bool activate)
    {
        Assert.Equal(new NewTabOpening(atEnd, activate),
            NewTabPolicy.Resolve(NewTabPositions.NextToCurrent, true, positionKey, foregroundKey, shift, ctrl, alt));
        Assert.Equal(positionKey == foregroundKey, NewTabPolicy.KeysShared(positionKey, foregroundKey));
    }

    [Fact]
    public void None_for_both_is_not_shared() => Assert.False(NewTabPolicy.KeysShared("None", "None"));

    [Theory]
    [InlineData("next to the current tab", true, "None", "None", "")]
    [InlineData("next to the current tab", true, "None", "Shift", "Shift: background")]
    [InlineData("at the end", false, "None", "Shift", "Shift: foreground")]
    [InlineData("next to the current tab", true, "Alt", "Shift", "Shift: background; Alt: at end")]
    [InlineData("at the end", true, "Ctrl", "Ctrl", "Ctrl: background, next to current")]
    public void Menu_hint_names_the_held_keys(string position, bool foreground, string positionKey, string foregroundKey, string expected) =>
        Assert.Equal(expected, NewTabPolicy.MenuHint(position, foreground, positionKey, foregroundKey));

    [Theory]
    [InlineData("next to the current tab", "foreground", 1, 1)]
    [InlineData("at the end", "foreground", 3, 3)]
    [InlineData("next to the current tab", "background", 1, 0)]
    [InlineData("at the end", "background", 3, 0)]
    public void Duplicate_tab_is_placed_and_activated_as_set(string position, string side, int expectedIndex, int expectedActive)
    {
        var w = new WorkspaceState(createHome: false);
        var a = w.AddImage(Path.Combine(Path.GetTempPath(), "a.png"));
        w.AddHome(); w.AddHome();
        Assert.True(w.Select(a.Id));
        var copy = w.DuplicateActive(NewTabPolicy.ForDuplicate(position, side))!;
        Assert.IsType<ImageTabState>(copy);
        Assert.Equal(expectedIndex, w.Tabs.ToList().FindIndex(t => t.Id == copy.Id));
        Assert.Equal(expectedActive, w.ActiveIndex);
    }
}
