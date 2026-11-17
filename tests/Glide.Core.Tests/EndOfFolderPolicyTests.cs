using Glide.App.Settings;
using Glide.Core.Commands;
using Glide.Core.Navigation;
using Glide.Core.Settings;

namespace Glide.Core.Tests;

/// <summary>
/// 5.0.7 "When the last image is reached" (navigation.endOfFolder), its schema 15 migration, the "Ask me each time" picker
/// model and the per-tab "Loop this folder" action.
/// </summary>
public sealed class EndOfFolderPolicyTests
{
    // ---- the behaviour before 5.0.7, reproduced through the new policy ------------------------------------------

    [Theory]
    [InlineData(1)]
    [InlineData(-1)]
    public void Old_checkbox_on_still_continues_into_the_sibling_folder_in_the_direction_of_travel(int direction)
    {
        var action = EndOfFolderActions.Parse(EndOfFolderActions.FromLegacyContinueSiblingFolders(true));
        Assert.Equal(new EndOfFolderStep(EndOfFolderStepKind.Folder, direction), EndOfFolderPolicy.Decide(action, direction));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(-1)]
    public void Old_checkbox_off_still_stops_with_the_end_of_folder_notice(int direction)
    {
        var action = EndOfFolderActions.Parse(EndOfFolderActions.FromLegacyContinueSiblingFolders(false));
        Assert.Equal(EndOfFolderStepKind.Stay, EndOfFolderPolicy.Decide(action, direction).Kind);
    }

    [Fact]
    public void Default_is_move_to_the_next_folder()
    {
        Assert.Equal(EndOfFolderActions.NextFolder, EndOfFolderActions.Default);
        Assert.Equal(EndOfFolderActions.NextFolder, new GlideSettingsState().EndOfFolderAction);
        Assert.Equal(EndOfFolderActions.NextFolder, SettingsCatalog.All.Single(s => s.Id == "navigation.endOfFolder").DefaultValue);
    }

    // ---- the policy ---------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(EndOfFolderAction.DoNothing, 1, EndOfFolderStepKind.Stay, 0)]
    [InlineData(EndOfFolderAction.LoopFolder, 1, EndOfFolderStepKind.Loop, 0)]
    [InlineData(EndOfFolderAction.NextFolder, 1, EndOfFolderStepKind.Folder, 1)]
    [InlineData(EndOfFolderAction.NextFolderNewTab, 1, EndOfFolderStepKind.FolderInNewTab, 1)]
    [InlineData(EndOfFolderAction.PreviousFolder, 1, EndOfFolderStepKind.Folder, -1)]
    [InlineData(EndOfFolderAction.ShowHome, 1, EndOfFolderStepKind.Home, 0)]
    [InlineData(EndOfFolderAction.Ask, 1, EndOfFolderStepKind.Ask, 0)]
    // Mirrored for Previous at the first image: "next" is the folder in the direction of travel.
    [InlineData(EndOfFolderAction.NextFolder, -1, EndOfFolderStepKind.Folder, -1)]
    [InlineData(EndOfFolderAction.NextFolderNewTab, -1, EndOfFolderStepKind.FolderInNewTab, -1)]
    [InlineData(EndOfFolderAction.PreviousFolder, -1, EndOfFolderStepKind.Folder, 1)]
    [InlineData(EndOfFolderAction.LoopFolder, -1, EndOfFolderStepKind.Loop, 0)]
    public void Decide_maps_every_choice_to_one_step(EndOfFolderAction action, int direction, EndOfFolderStepKind kind, int folderDirection) =>
        Assert.Equal(new EndOfFolderStep(kind, folderDirection), EndOfFolderPolicy.Decide(action, direction));

    [Fact]
    public void No_direction_never_moves() =>
        Assert.Equal(EndOfFolderStepKind.Stay, EndOfFolderPolicy.Decide(EndOfFolderAction.NextFolder, 0).Kind);

    [Fact]
    public void Loop_lands_on_the_other_end_of_the_folder()
    {
        Assert.Equal(0, EndOfFolderPolicy.LoopTargetIndex(1, 142));
        Assert.Equal(141, EndOfFolderPolicy.LoopTargetIndex(-1, 142));
        Assert.Equal(-1, EndOfFolderPolicy.LoopTargetIndex(1, 0));
    }

    [Theory]
    [InlineData(EndOfFolderAction.DoNothing)]
    [InlineData(EndOfFolderAction.NextFolder)]
    [InlineData(EndOfFolderAction.ShowHome)]
    [InlineData(EndOfFolderAction.Ask)]
    public void The_per_tab_loop_override_wins_over_any_setting(EndOfFolderAction setting)
    {
        Assert.Equal(EndOfFolderAction.LoopFolder, EndOfFolderPolicy.Effective(setting, loopOverride: true));
        Assert.Equal(setting, EndOfFolderPolicy.Effective(setting, loopOverride: false));
    }

    [Fact]
    public void Held_keys_only_carry_on_through_folder_and_loop_steps()
    {
        Assert.True(EndOfFolderPolicy.ContinuesHeldNavigation(new(EndOfFolderStepKind.Folder, 1)));
        Assert.True(EndOfFolderPolicy.ContinuesHeldNavigation(new(EndOfFolderStepKind.Loop)));
        Assert.False(EndOfFolderPolicy.ContinuesHeldNavigation(new(EndOfFolderStepKind.FolderInNewTab, 1)));
        Assert.False(EndOfFolderPolicy.ContinuesHeldNavigation(new(EndOfFolderStepKind.Home)));
        Assert.False(EndOfFolderPolicy.ContinuesHeldNavigation(new(EndOfFolderStepKind.Ask)));
        Assert.False(EndOfFolderPolicy.ContinuesHeldNavigation(EndOfFolderStep.Stay));
    }

    [Fact]
    public void Folder_options_apply_only_to_the_folder_choices()
    {
        var withOptions = Enum.GetValues<EndOfFolderAction>().Where(EndOfFolderActions.UsesFolderOptions).ToArray();
        Assert.Equal([EndOfFolderAction.NextFolder, EndOfFolderAction.NextFolderNewTab, EndOfFolderAction.PreviousFolder, EndOfFolderAction.Ask], withOptions);
    }

    [Fact]
    public void Names_round_trip_in_settings_combo_order_and_junk_falls_back_to_the_default()
    {
        Assert.Equal(["Do nothing", "Loop this folder", "Move to the next folder", "Move to the next folder in a new tab",
            "Go back to the previous folder", "Show Home", "Ask me each time"], EndOfFolderActions.All);
        foreach (var action in Enum.GetValues<EndOfFolderAction>())
            Assert.Equal(action, EndOfFolderActions.Parse(EndOfFolderActions.NameOf(action)));
        Assert.Equal(EndOfFolderAction.LoopFolder, EndOfFolderActions.Parse("  loop THIS folder "));
        Assert.Equal(EndOfFolderActions.NextFolder, EndOfFolderActions.Normalize("banana"));
        Assert.Equal(EndOfFolderActions.NextFolder, EndOfFolderActions.Normalize(null));
        Assert.Equal(EndOfFolderActions.All, SettingActionCatalog.Choices["navigation.endOfFolder"]);
    }

    // ---- schema 15 migration ------------------------------------------------------------------------------------

    [Theory]
    [InlineData("{\"SettingsSchemaVersion\":14,\"ContinueSiblingFolders\":true}", EndOfFolderActions.NextFolder)]
    [InlineData("{\"SettingsSchemaVersion\":14,\"ContinueSiblingFolders\":false}", EndOfFolderActions.DoNothing)]
    [InlineData("{\"SettingsSchemaVersion\":9,\"ContinueSiblingFolders\":false}", EndOfFolderActions.DoNothing)]
    [InlineData("{\"SettingsSchemaVersion\":14}", EndOfFolderActions.NextFolder)]
    [InlineData("{}", EndOfFolderActions.NextFolder)]
    // An older file never had the new key; even if one appears there, the old checkbox decides.
    [InlineData("{\"SettingsSchemaVersion\":14,\"ContinueSiblingFolders\":false,\"EndOfFolderAction\":\"Show Home\"}", EndOfFolderActions.DoNothing)]
    // From schema 15 on the stored choice is kept (and a stray old key is ignored).
    [InlineData("{\"SettingsSchemaVersion\":15,\"EndOfFolderAction\":\"Ask me each time\"}", EndOfFolderActions.Ask)]
    [InlineData("{\"SettingsSchemaVersion\":15,\"EndOfFolderAction\":\"Do nothing\",\"ContinueSiblingFolders\":true}", EndOfFolderActions.DoNothing)]
    [InlineData("{\"SettingsSchemaVersion\":15,\"EndOfFolderAction\":\"nonsense\"}", EndOfFolderActions.NextFolder)]
    public void Schema_15_migrates_the_old_checkbox(string json, string expected)
    {
        var loaded = SettingsStore.MigrateFromJson(json, isSettingsFile: false);
        Assert.Equal(expected, loaded.EndOfFolderAction);
        Assert.Equal(SettingsStore.CurrentSettingsSchema, loaded.SettingsSchemaVersion);
    }

    [Fact]
    public void Migration_helper_matches_the_approved_mapping()
    {
        Assert.Equal(15, SettingsStore.EndOfFolderActionSchema);
        Assert.Equal(EndOfFolderActions.NextFolder, SettingsStore.MigrateEndOfFolderAction(14, true, null));
        Assert.Equal(EndOfFolderActions.DoNothing, SettingsStore.MigrateEndOfFolderAction(14, false, null));
        Assert.Equal(EndOfFolderActions.NextFolder, SettingsStore.MigrateEndOfFolderAction(14, null, null));
        Assert.Equal(EndOfFolderActions.ShowHome, SettingsStore.MigrateEndOfFolderAction(15, false, EndOfFolderActions.ShowHome));
    }

    // ---- the setting is wired like every other Choice -----------------------------------------------------------

    [Fact]
    public void The_setting_is_catalogued_bindable_and_has_an_accessor()
    {
        var setting = SettingsCatalog.All.Single(s => s.Id == "navigation.endOfFolder");
        Assert.Equal(SettingKind.Choice, setting.Kind);
        Assert.Contains("loop", setting.SearchTerms);
        Assert.DoesNotContain(SettingsCatalog.All, s => s.Id == "navigation.siblingFolders");
        Assert.True(SettingActionCatalog.IsBindable(setting));
        Assert.True(SettingAccessors.All.ContainsKey("navigation.endOfFolder"));
        Assert.Contains(SettingEffectRegistry.All, e => e.SettingId == "navigation.endOfFolder");
        var state = new GlideSettingsState { EndOfFolderAction = "loop this folder" };
        SettingAccessors.NormalizeDerived(state, "navigation.endOfFolder");
        Assert.Equal(EndOfFolderActions.LoopFolder, state.EndOfFolderAction);
    }

    // ---- "Ask me each time" picker --------------------------------------------------------------------------------

    [Fact]
    public void Picker_rows_follow_the_mockup_and_are_mirrored_for_previous()
    {
        var forward = new EndOfFolderPicker(1);
        Assert.Equal(["Next folder", "Next folder in new tab", "Previous folder", "Loop this folder", "Show Home", "Stay here"], forward.Rows.Select(r => r.Label));
        Assert.Equal(["1", "2", "3", "4", "5", "Esc"], forward.Rows.Select(r => r.KeyHint));
        Assert.Equal(0, forward.Highlighted);

        var backward = new EndOfFolderPicker(-1);
        Assert.Equal("Previous folder", backward.Rows[0].Label);
        Assert.Equal("Next folder", backward.Rows[2].Label);
        // The actions stay relative to the direction of travel, so Decide mirrors them.
        Assert.Equal(new EndOfFolderStep(EndOfFolderStepKind.Folder, -1), EndOfFolderPolicy.Decide(backward.Rows[0].Action, -1));
        Assert.Equal(new EndOfFolderStep(EndOfFolderStepKind.Folder, 1), EndOfFolderPolicy.Decide(backward.Rows[2].Action, -1));
    }

    [Fact]
    public void Arrows_move_the_highlight_and_wrap_and_Enter_picks_it()
    {
        var picker = new EndOfFolderPicker(1);
        Assert.True(picker.HandleKey(EndOfFolderPickerKey.Up));
        Assert.Equal(picker.Rows.Count - 1, picker.Highlighted);
        Assert.True(picker.HandleKey(EndOfFolderPickerKey.Down));
        Assert.True(picker.HandleKey(EndOfFolderPickerKey.Down));
        Assert.True(picker.HandleKey(EndOfFolderPickerKey.Down));
        Assert.Equal(2, picker.Highlighted);
        Assert.Null(picker.Result);
        Assert.True(picker.HandleKey(EndOfFolderPickerKey.Enter));
        Assert.Equal(EndOfFolderAction.PreviousFolder, picker.Result);
        // Once picked, further keys are not the picker's any more.
        Assert.False(picker.HandleKey(EndOfFolderPickerKey.Down));
        Assert.Equal(EndOfFolderAction.PreviousFolder, picker.Result);
    }

    [Theory]
    [InlineData(1, EndOfFolderAction.NextFolder)]
    [InlineData(2, EndOfFolderAction.NextFolderNewTab)]
    [InlineData(3, EndOfFolderAction.PreviousFolder)]
    [InlineData(4, EndOfFolderAction.LoopFolder)]
    [InlineData(5, EndOfFolderAction.ShowHome)]
    public void Number_keys_pick_directly(int digit, EndOfFolderAction expected)
    {
        var picker = new EndOfFolderPicker(1);
        Assert.True(picker.HandleKey(EndOfFolderPickerKey.Digit, digit));
        Assert.Equal(expected, picker.Result);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(6)]
    [InlineData(9)]
    public void Other_numbers_are_ignored(int digit)
    {
        var picker = new EndOfFolderPicker(1);
        Assert.False(picker.HandleKey(EndOfFolderPickerKey.Digit, digit));
        Assert.Null(picker.Result);
    }

    [Fact]
    public void Escape_and_clicking_outside_stay_here()
    {
        var escaped = new EndOfFolderPicker(1);
        Assert.True(escaped.HandleKey(EndOfFolderPickerKey.Escape));
        Assert.Equal(EndOfFolderAction.DoNothing, escaped.Result);
        Assert.Equal(EndOfFolderStepKind.Stay, EndOfFolderPolicy.Decide(escaped.Result!.Value, 1).Kind);

        var dismissed = new EndOfFolderPicker(-1);
        dismissed.Dismiss();
        Assert.Equal(EndOfFolderAction.DoNothing, dismissed.Result);
    }

    [Fact]
    public void Pressing_the_navigation_key_again_picks_the_highlighted_row()
    {
        var picker = new EndOfFolderPicker(1);
        Assert.True(picker.HandleKey(EndOfFolderPickerKey.NavigateAgain));
        Assert.Equal(EndOfFolderAction.NextFolder, picker.Result);

        var moved = new EndOfFolderPicker(-1);
        moved.HandleKey(EndOfFolderPickerKey.Down);
        moved.HandleKey(EndOfFolderPickerKey.Down);
        moved.HandleKey(EndOfFolderPickerKey.Down);
        moved.HandleKey(EndOfFolderPickerKey.NavigateAgain);
        Assert.Equal(EndOfFolderAction.LoopFolder, moved.Result);
    }

    [Fact]
    public void A_click_picks_its_row_and_only_the_first_pick_counts()
    {
        var picker = new EndOfFolderPicker(1);
        picker.Pick(4);
        picker.Pick(0);
        picker.Dismiss();
        Assert.Equal(EndOfFolderAction.ShowHome, picker.Result);
        Assert.Equal(4, picker.Highlighted);
    }

    // ---- "Loop this folder" action / status-bar button ----------------------------------------------------------

    [Fact]
    public void Loop_this_folder_is_a_registry_command_and_an_addable_status_bar_button()
    {
        var action = AppActionRegistry.Find("navigation.toggleLoopFolder");
        Assert.NotNull(action);
        Assert.Equal(GlideCommand.ToggleLoopFolder, action!.Command);
        Assert.Equal("Loop", action.Icon);
        Assert.Equal(AppActionContexts.Viewer, action.Contexts);
        var button = StatusBarGroupCatalog.FindAction(StatusBarGroupCatalog.ActionPrefix + "navigation.toggleLoopFolder");
        Assert.NotNull(button);
        // Shown by default, right after the navigation group; removable like every extra button.
        Assert.Equal(StatusBarGroupCatalog.LoopFolderButtonId, button!.Id);
        var defaults = StatusBarGroupCatalog.Parse("");
        Assert.Equal(defaults.ToList().IndexOf("nav") + 1, defaults.ToList().IndexOf(button.Id));
        Assert.Equal(string.Empty, StatusBarGroupCatalog.Format(defaults));
        Assert.DoesNotContain(button.Id, StatusBarGroupCatalog.Parse(string.Join(",", StatusBarGroupCatalog.DefaultOrder)));
    }

    // ---- schema 16 migration: the Loop this folder button joins the status bar once ----------------------------
    [Theory]
    [InlineData(15, "", "")]                                     // default layout picks it up by itself
    [InlineData(15, "none", "none")]                             // everything deliberately hidden stays hidden
    [InlineData(15, "zoom,nav,fit", "zoom,nav,a:navigation.toggleLoopFolder,fit")]
    [InlineData(15, "zoom,fit", "zoom,fit,a:navigation.toggleLoopFolder")]
    [InlineData(15, "nav,a:navigation.toggleLoopFolder,zoom", "nav,a:navigation.toggleLoopFolder,zoom")]
    [InlineData(16, "zoom,nav", "zoom,nav")]                     // removed after the migration: stays removed
    public void Schema_16_adds_the_loop_button_to_custom_status_bars_once(int schema, string stored, string expected)
    {
        Assert.Equal(16, SettingsStore.LoopFolderStatusButtonSchema);
        Assert.Equal(expected, SettingsStore.MigrateStatusBarGroups(schema, stored));
        var loaded = SettingsStore.MigrateFromJson("{\"SettingsSchemaVersion\":" + schema + ",\"StatusBarGroups\":\"" + stored + "\"}", isSettingsFile: false);
        Assert.Equal(expected, loaded.StatusBarGroups);
    }
}
