using Glide.Core.Settings;
using Glide.Core.Workspace;

namespace Glide.Core.Tests;

/// <summary>5.1.0: Explorer middle-click has its own side (background by default) and position (at the end by default).</summary>
[Trait("Area", "Tabs")]
public sealed class MiddleClickNewTabPolicyTests
{
    [Theory]
    // middle side, middle position, New tabs open, position key, foreground key, shift / ctrl / alt -> at end, activate
    [InlineData("background", "at the end", "next to the current tab", "None", "None", false, false, false, true, false)]
    [InlineData("foreground", "at the end", "next to the current tab", "None", "None", false, false, false, true, true)]
    [InlineData("background", "next to the current tab", "at the end", "None", "None", false, false, false, false, false)]
    [InlineData("background", "same as new tabs", "next to the current tab", "None", "None", false, false, false, false, false)]
    [InlineData("background", "same as new tabs", "at the end", "None", "None", false, false, false, true, false)]
    // Held-key flips still apply on top.
    [InlineData("background", "at the end", "next to the current tab", "Shift", "Alt", true, false, false, false, false)]
    [InlineData("background", "at the end", "next to the current tab", "Shift", "Alt", false, false, true, true, true)]
    [InlineData("foreground", "same as new tabs", "at the end", "Ctrl", "Ctrl", false, true, false, false, false)]
    // Unknown stored values fall back to the defaults (background, at the end).
    [InlineData("??", "??", "next to the current tab", "None", "None", false, false, false, true, false)]
    public void Middle_click_resolves_its_own_side_and_position(string side, string position, string newTabPosition, string positionKey,
        string foregroundKey, bool shift, bool ctrl, bool alt, bool atEnd, bool activate)
    {
        var opening = NewTabPolicy.ResolveMiddleClick(side, position, newTabPosition, positionKey, foregroundKey, shift, ctrl, alt);
        Assert.Equal(new NewTabOpening(atEnd, activate), opening);
    }

    [Fact]
    public void Defaults_are_background_and_at_the_end_and_match_the_catalogue()
    {
        var state = new GlideSettingsState();
        Assert.Equal(MiddleClickSides.Background, state.MiddleClickNewTabSide);
        Assert.Equal(MiddleClickPositions.AtEnd, state.MiddleClickNewTabPosition);
        Assert.Equal(state.MiddleClickNewTabSide, SettingsCatalog.All.Single(e => e.Id == "tabs.middleClickSide").DefaultValue);
        Assert.Equal(state.MiddleClickNewTabPosition, SettingsCatalog.All.Single(e => e.Id == "tabs.middleClickPosition").DefaultValue);
    }

    [Fact]
    public void Choice_lists_match_the_policy_values()
    {
        Assert.Equal(MiddleClickSides.All, SettingActionCatalog.Choices["tabs.middleClickSide"]);
        Assert.Equal(MiddleClickPositions.All, SettingActionCatalog.Choices["tabs.middleClickPosition"]);
    }
}
