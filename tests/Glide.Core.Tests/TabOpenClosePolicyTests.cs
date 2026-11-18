using Glide.App.Settings;
using Glide.Core.Settings;
using Glide.Core.Workspace;

namespace Glide.Core.Tests;

/// <summary>5.0.6: the pure rules behind "New tabs open", "Open new tabs in the", their "Key to open … instead" and "After closing a tab, activate".</summary>
[Trait("Area", "Tabs")]
public sealed class TabOpenClosePolicyTests
{
    [Theory]
    // position, switch, position key, foreground key, held shift / ctrl / alt -> at end, activate
    [InlineData(NewTabPositions.NextToCurrent, false, "Alt", "Shift", false, false, false, false, false)] // nothing held: the settings
    [InlineData(NewTabPositions.NextToCurrent, false, "Alt", "Shift", true, false, false, false, true)]   // Shift: the other side
    [InlineData(NewTabPositions.NextToCurrent, false, "Alt", "Shift", false, false, true, true, false)]   // Alt: the other position
    [InlineData(NewTabPositions.NextToCurrent, false, "Alt", "Shift", true, false, true, true, true)]
    [InlineData(NewTabPositions.AtEnd, true, "Alt", "Shift", true, false, false, true, false)]            // Shift: stay in the background
    [InlineData(NewTabPositions.AtEnd, true, "Alt", "Shift", false, false, true, false, true)]            // Alt: next to the current tab
    [InlineData(NewTabPositions.AtEnd, true, "Ctrl", "None", false, true, false, false, true)]            // Ctrl chosen for position
    [InlineData(NewTabPositions.AtEnd, true, "Ctrl", "None", true, false, true, true, true)]              // keys not chosen do nothing
    [InlineData(NewTabPositions.NextToCurrent, true, "Shift", "Shift", true, false, false, true, false)]  // one key for both flips both
    public void Settings_give_the_default_and_the_chosen_keys_flip_them(string position, bool switchTo, string positionKey, string foregroundKey,
        bool shift, bool ctrl, bool alt, bool atEnd, bool activate)
    {
        Assert.Equal(new NewTabOpening(atEnd, activate), NewTabPolicy.Resolve(position, switchTo, positionKey, foregroundKey, shift, ctrl, alt));
    }

    [Theory]
    [InlineData(NewTabPositions.NextToCurrent, false)]
    [InlineData(NewTabPositions.NextToCurrent, true)]
    [InlineData(NewTabPositions.AtEnd, false)]
    [InlineData(NewTabPositions.AtEnd, true)]
    public void With_both_keys_None_held_keys_change_nothing(string position, bool switchTo)
    {
        var expected = new NewTabOpening(position == NewTabPositions.AtEnd, switchTo);
        foreach (var (shift, ctrl, alt) in new[] { (false, false, false), (true, false, false), (false, true, false), (false, false, true), (true, true, true) })
            Assert.Equal(expected, NewTabPolicy.Resolve(position, switchTo, NewTabKeys.None, NewTabKeys.None, shift, ctrl, alt));
    }

    [Fact]
    public void Round3_18_the_key_labels_name_the_other_choice_and_never_say_at_at()
    {
        Assert.Equal("Key to open at the end instead", NewTabPolicy.PositionKeyLabel(NewTabPositions.NextToCurrent));
        Assert.Equal("Key to open next to the current tab instead", NewTabPolicy.PositionKeyLabel(NewTabPositions.AtEnd));
        Assert.Equal("Key to open in the background instead", NewTabPolicy.ForegroundKeyLabel(true));
        Assert.Equal("Key to open in the foreground instead", NewTabPolicy.ForegroundKeyLabel(false));
        // #19: "New tabs open" + "at the end" reads once; the old label "New tabs open at" + "At the end" said "at at".
        var position = SettingsCatalog.All.Single(s => s.Id == "tabs.newTabPosition");
        foreach (var option in NewTabPositions.All)
            Assert.DoesNotContain("at at", (position.Label + " " + option).ToLowerInvariant());
    }

    [Fact]
    public void Round3_18_the_old_Shift_and_Alt_checkbox_maps_to_the_keys_it_used()
    {
        Assert.Equal((NewTabKeys.Shift, NewTabKeys.Alt), NewTabPolicy.MigrateLegacyModifiers(true)); // Shift = position, Alt = foreground/background
        Assert.Equal((NewTabKeys.None, NewTabKeys.None), NewTabPolicy.MigrateLegacyModifiers(false));
        Assert.Equal("Ctrl", NewTabKeys.Normalize("ctrl"));
        Assert.Equal("None", NewTabKeys.Normalize("Win"));
        Assert.True(NewTabSides.ToSwitch("foreground"));
        Assert.False(NewTabSides.ToSwitch("Background"));
    }

    [Fact]
    public void Ctrl_T_and_plus_always_open_at_the_end_and_switch()
    {
        Assert.Equal(new NewTabOpening(AtEnd: true, Activate: true), NewTabPolicy.ForNewTabCommand());
    }

    [Fact]
    public void Unknown_positions_fall_back_to_next_to_the_current_tab()
    {
        Assert.Equal(NewTabPositions.NextToCurrent, NewTabPositions.Normalize(null));
        Assert.Equal(NewTabPositions.NextToCurrent, NewTabPositions.Normalize("somewhere"));
        Assert.Equal(NewTabPositions.AtEnd, NewTabPositions.Normalize("at the END"));
    }

    [Fact]
    public void The_new_tab_settings_and_their_keys_are_catalogued_so_search_finds_them()
    {
        Assert.DoesNotContain(SettingsCatalog.All, s => s.Id == "tabs.newTabModifiers");
        Assert.Equal("New tabs open", SettingsCatalog.All.Single(s => s.Id == "tabs.newTabPosition").Label);
        var side = SettingsCatalog.All.Single(s => s.Id == "tabs.switchToNewTabs");
        Assert.Equal("Open new tabs in the", side.Label);
        Assert.Equal(SettingKind.Choice, side.Kind);
        Assert.Equal(NewTabSides.Foreground, side.DefaultValue); // foreground by default since 5.0.6
        foreach (var id in new[] { "tabs.newTabPositionKey", "tabs.newTabForegroundKey" })
        {
            var setting = SettingsCatalog.All.Single(s => s.Id == id);
            Assert.Equal(SettingKind.Choice, setting.Kind);
            Assert.Equal(NewTabKeys.None, setting.DefaultValue);
            Assert.Contains("shift and alt change how a new tab opens", setting.SearchTerms);
            Assert.Equal(NewTabKeys.All, SettingActionCatalog.Choices[id]);
        }
        foreach (var id in new[] { "tabs.newTabPosition", "tabs.switchToNewTabs", "tabs.newTabPositionKey", "tabs.newTabForegroundKey" })
            Assert.Equal("Tabs & status bar", SettingsCatalog.All.Single(s => s.Id == id).Category);
        Assert.Equal(NewTabPositions.All, SettingActionCatalog.Choices["tabs.newTabPosition"]);
        Assert.Equal(NewTabSides.All, SettingActionCatalog.Choices["tabs.switchToNewTabs"]);
        Assert.Equal(TabCloseActivation.All, SettingActionCatalog.Choices["tabs.afterCloseActivate"]);
    }

    // ---- schema 18 migration of "Shift and Alt change how a new tab opens" -----------------------------------------

    [Theory]
    [InlineData("{\"SettingsSchemaVersion\":17,\"NewTabModifiers\":true}", "Shift", "Alt")]
    [InlineData("{\"SettingsSchemaVersion\":17,\"NewTabModifiers\":false}", "None", "None")]
    [InlineData("{\"SettingsSchemaVersion\":13}", "None", "None")]
    [InlineData("{}", "None", "None")]
    // An older file never had the new keys; the old checkbox decides.
    [InlineData("{\"SettingsSchemaVersion\":17,\"NewTabModifiers\":true,\"NewTabPositionKey\":\"Ctrl\"}", "Shift", "Alt")]
    // From schema 18 the stored keys are kept (a stray old checkbox is ignored).
    [InlineData("{\"SettingsSchemaVersion\":18,\"NewTabPositionKey\":\"Ctrl\",\"NewTabForegroundKey\":\"alt\",\"NewTabModifiers\":false}", "Ctrl", "Alt")]
    [InlineData("{\"SettingsSchemaVersion\":18,\"NewTabPositionKey\":\"Win\"}", "None", "None")]
    public void Schema_18_migrates_the_old_Shift_and_Alt_checkbox(string json, string positionKey, string foregroundKey)
    {
        var loaded = SettingsStore.MigrateFromJson(json, isSettingsFile: false);
        Assert.Equal(positionKey, loaded.NewTabPositionKey);
        Assert.Equal(foregroundKey, loaded.NewTabForegroundKey);
        Assert.Equal(SettingsStore.CurrentSettingsSchema, loaded.SettingsSchemaVersion);
    }

    [Fact]
    public void A_5_0_6_file_keeps_its_position_under_the_new_wording()
    {
        var loaded = SettingsStore.MigrateFromJson("{\"SettingsSchemaVersion\":17,\"NewTabPosition\":\"At the end\",\"SwitchToNewTabs\":false}", isSettingsFile: false);
        Assert.Equal(NewTabPositions.AtEnd, loaded.NewTabPosition);
        Assert.False(loaded.SwitchToNewTabs);
    }

    private static WorkspaceState Workspace(int tabs, int active)
    {
        var workspace = new WorkspaceState(createHome: false);
        workspace.Reset(Enumerable.Range(0, tabs).Select(_ => (TabState)new HomeTabState(Guid.NewGuid())).ToArray(), active);
        return workspace;
    }

    [Fact]
    public void Background_tabs_opened_one_after_another_keep_their_order_next_to_the_current_tab()
    {
        var workspace = Workspace(3, 1);
        var opener = workspace.Active!.Id;
        var a = workspace.OpenTab(new HomeTabState(Guid.NewGuid()), new NewTabOpening(false, false));
        var b = workspace.OpenTab(new HomeTabState(Guid.NewGuid()), new NewTabOpening(false, false));
        Assert.Equal(opener, workspace.Active!.Id);
        Assert.Equal(2, workspace.Tabs.ToList().IndexOf(a));
        Assert.Equal(3, workspace.Tabs.ToList().IndexOf(b));
        // After switching tabs the run ends: the next one goes right next to the (new) current tab again.
        workspace.Select(workspace.Tabs[0].Id);
        var c = workspace.OpenTab(new HomeTabState(Guid.NewGuid()), new NewTabOpening(false, false));
        Assert.Equal(1, workspace.Tabs.ToList().IndexOf(c));
    }

    [Fact]
    public void At_the_end_and_activate_put_the_tab_last_and_in_front()
    {
        var workspace = Workspace(3, 0);
        var tab = workspace.OpenTab(new HomeTabState(Guid.NewGuid()), new NewTabOpening(true, true));
        Assert.Equal(3, workspace.ActiveIndex);
        Assert.Same(tab, workspace.Active);
    }

    [Theory]
    [InlineData(TabCloseActivation.MatchCycleOrder, "Tab order", TabCloseActivationMode.Right)]
    [InlineData(TabCloseActivation.MatchCycleOrder, "Most recently used", TabCloseActivationMode.PreviouslyUsed)]
    [InlineData(null, "Most recently used", TabCloseActivationMode.PreviouslyUsed)]
    [InlineData(null, "Tab order", TabCloseActivationMode.PreviouslyUsed)]          // the default: Previously used tab
    [InlineData("nonsense", "Tab order", TabCloseActivationMode.PreviouslyUsed)]
    [InlineData(TabCloseActivation.PreviouslyUsed, "Tab order", TabCloseActivationMode.PreviouslyUsed)]
    [InlineData(TabCloseActivation.Right, "Most recently used", TabCloseActivationMode.Right)]
    [InlineData(TabCloseActivation.Left, "Most recently used", TabCloseActivationMode.Left)]
    public void Match_Ctrl_Tab_order_follows_the_cycle_setting_and_explicit_choices_win(string? setting, string cycle, TabCloseActivationMode expected)
    {
        Assert.Equal(expected, TabCloseActivation.Resolve(setting, cycle));
    }

    [Fact]
    public void After_closing_a_tab_activate_defaults_to_the_previously_used_tab()
    {
        Assert.Equal(TabCloseActivation.PreviouslyUsed, TabCloseActivation.Default);
        Assert.Equal(TabCloseActivation.PreviouslyUsed, TabCloseActivation.Normalize(null));
        Assert.Equal(TabCloseActivation.PreviouslyUsed, SettingsCatalog.All.Single(s => s.Id == "tabs.afterCloseActivate").DefaultValue);
        Assert.Contains(TabCloseActivation.MatchCycleOrder, TabCloseActivation.All); // still an option
    }

    [Fact]
    public void Closing_the_active_tab_activates_the_tab_the_rule_picks()
    {
        // [t0, t1, t2, t3], t2 active, used before: t0 then t3.
        WorkspaceState Setup(out Guid[] ids)
        {
            var w = Workspace(4, 2);
            ids = w.Tabs.Select(t => t.Id).ToArray();
            return w;
        }

        var right = Setup(out var ids);
        right.Close(ids[2], ensureHome: false, TabCloseActivationMode.Right, null);
        Assert.Equal(ids[3], right.Active!.Id);

        var left = Setup(out ids);
        left.Close(ids[2], ensureHome: false, TabCloseActivationMode.Left, null);
        Assert.Equal(ids[1], left.Active!.Id);

        var mru = Setup(out ids);
        mru.Close(ids[2], ensureHome: false, TabCloseActivationMode.PreviouslyUsed, [ids[2], ids[0], ids[3]]);
        Assert.Equal(ids[0], mru.Active!.Id);

        // Without usable history the previously-used rule falls back to the tab to the right.
        var none = Setup(out ids);
        none.Close(ids[2], ensureHome: false, TabCloseActivationMode.PreviouslyUsed, [Guid.NewGuid()]);
        Assert.Equal(ids[3], none.Active!.Id);

        // Closing a background tab never changes the active tab.
        var background = Setup(out ids);
        background.Close(ids[0], ensureHome: false, TabCloseActivationMode.PreviouslyUsed, [ids[3]]);
        Assert.Equal(ids[2], background.Active!.Id);
    }

    [Fact]
    public void Edge_tabs_fall_back_inside_the_strip()
    {
        var first = Workspace(3, 0);
        var ids = first.Tabs.Select(t => t.Id).ToArray();
        first.Close(ids[0], ensureHome: false, TabCloseActivationMode.Left, null);
        Assert.Equal(ids[1], first.Active!.Id);

        var last = Workspace(3, 2);
        ids = last.Tabs.Select(t => t.Id).ToArray();
        last.Close(ids[2], ensureHome: false, TabCloseActivationMode.Right, null);
        Assert.Equal(ids[1], last.Active!.Id);
    }
}
