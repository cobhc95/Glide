using Glide.Core.Settings;
using Glide.Core.Workspace;

namespace Glide.Core.Tests;

/// <summary>5.0.6: the pure rules behind "New tabs open at", "Open new tabs in the foreground", "Shift and Alt change how a new tab opens" and "After closing a tab, activate".</summary>
public sealed class TabOpenClosePolicyTests
{
    [Theory]
    // position, switch, shift, alt -> at end, activate (with "Shift and Alt change how a new tab opens" on)
    [InlineData(NewTabPositions.NextToCurrent, false, false, false, false, false)] // defaults = the behaviour before 5.0.6
    [InlineData(NewTabPositions.NextToCurrent, false, true, false, false, true)]   // Shift: switch to it
    [InlineData(NewTabPositions.NextToCurrent, false, false, true, true, false)]   // Alt: the other position
    [InlineData(NewTabPositions.NextToCurrent, false, true, true, true, true)]
    [InlineData(NewTabPositions.AtEnd, true, false, false, true, true)]
    [InlineData(NewTabPositions.AtEnd, true, true, false, true, false)]            // Shift: stay in the background
    [InlineData(NewTabPositions.AtEnd, true, false, true, false, true)]            // Alt: next to the current tab
    public void Settings_give_the_default_and_Shift_and_Alt_flip_them(string position, bool switchTo, bool shift, bool alt, bool atEnd, bool activate)
    {
        Assert.Equal(new NewTabOpening(atEnd, activate), NewTabPolicy.Resolve(position, switchTo, shift, alt, modifiersEnabled: true));
    }

    [Theory]
    [InlineData(NewTabPositions.NextToCurrent, false)]
    [InlineData(NewTabPositions.NextToCurrent, true)]
    [InlineData(NewTabPositions.AtEnd, false)]
    [InlineData(NewTabPositions.AtEnd, true)]
    public void With_the_modifier_setting_off_held_keys_change_nothing(string position, bool switchTo)
    {
        var expected = NewTabPolicy.Resolve(position, switchTo, shift: false, alt: false, modifiersEnabled: false);
        Assert.Equal(new NewTabOpening(position == NewTabPositions.AtEnd, switchTo), expected);
        foreach (var (shift, alt) in new[] { (true, false), (false, true), (true, true) })
            Assert.Equal(expected, NewTabPolicy.Resolve(position, switchTo, shift, alt, modifiersEnabled: false));
    }

    [Theory]
    [InlineData(NewTabPositions.NextToCurrent, false)]
    [InlineData(NewTabPositions.AtEnd, true)]
    [InlineData(null, false)]
    public void Ctrl_T_and_plus_follow_only_the_position_and_always_switch(string? position, bool atEnd)
    {
        Assert.Equal(new NewTabOpening(atEnd, Activate: true), NewTabPolicy.ForNewTabCommand(position));
    }

    [Fact]
    public void Unknown_positions_fall_back_to_next_to_the_current_tab()
    {
        Assert.Equal(NewTabPositions.NextToCurrent, NewTabPositions.Normalize(null));
        Assert.Equal(NewTabPositions.NextToCurrent, NewTabPositions.Normalize("somewhere"));
        Assert.Equal(NewTabPositions.AtEnd, NewTabPositions.Normalize("at the END"));
    }

    [Fact]
    public void The_new_tab_settings_describe_the_modifier_scheme_so_search_finds_it()
    {
        var modifiers = SettingsCatalog.All.Single(s => s.Id == "tabs.newTabModifiers");
        Assert.Equal(SettingKind.Toggle, modifiers.Kind);
        Assert.False((bool)modifiers.DefaultValue);
        Assert.Equal("Shift and Alt change how a new tab opens", modifiers.Label);
        Assert.Equal("Open new tabs in the foreground", SettingsCatalog.All.Single(s => s.Id == "tabs.switchToNewTabs").Label);
        Assert.True((bool)SettingsCatalog.All.Single(s => s.Id == "tabs.switchToNewTabs").DefaultValue); // on by default since 5.0.6
        Assert.Contains("On (default)", SettingsCatalog.All.Single(s => s.Id == "tabs.switchToNewTabs").Description);
        foreach (var id in new[] { "tabs.newTabPosition", "tabs.switchToNewTabs", "tabs.newTabModifiers" })
        {
            var setting = SettingsCatalog.All.Single(s => s.Id == id);
            Assert.Contains("Shift", setting.Description);
            Assert.Contains("Alt", setting.Description);
            Assert.Equal("Tabs & status bar", setting.Category);
        }
        Assert.Equal(NewTabPositions.All, SettingActionCatalog.Choices["tabs.newTabPosition"]);
        Assert.Equal(TabCloseActivation.All, SettingActionCatalog.Choices["tabs.afterCloseActivate"]);
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
