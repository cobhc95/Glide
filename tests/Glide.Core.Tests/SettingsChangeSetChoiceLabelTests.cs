using Glide.App.Settings;
using Glide.Core.Settings;

namespace Glide.Core.Tests;

/// <summary>Round 4 D4: the unsaved-changes list shows a Choice setting's own option labels, never "Off → On".</summary>
[Trait("Area", "Settings")]
public sealed class SettingsChangeSetChoiceLabelTests
{
    [Fact]
    public void Open_new_tabs_in_the_shows_foreground_and_background()
    {
        var a = new GlideSettingsState();
        var b = a.CloneState();
        b.SwitchToNewTabs = false;
        var change = Assert.Single(SettingsChangeSet.Compute(a, b));
        Assert.Equal("foreground", change.Before);
        Assert.Equal("background", change.After);
    }

    public static IEnumerable<object[]> ChoiceIds() =>
        SettingsCatalog.All.Where(e => e.Kind == SettingKind.Choice && SettingActionCatalog.Choices.ContainsKey(e.Id)
                && SettingAccessors.All.TryGetValue(e.Id, out var acc) && acc.GetText is not null)
            .Select(e => e.Id).Distinct().Select(id => new object[] { id });

    [Theory]
    [MemberData(nameof(ChoiceIds))]
    public void Every_choice_setting_lists_its_option_labels(string id)
    {
        var accessor = SettingAccessors.All[id];
        var a = new GlideSettingsState();
        var current = accessor.GetText!(a);
        foreach (var option in SettingActionCatalog.Choices[id])
        {
            var b = a.CloneState();
            accessor.SetText!(b, option);
            var after = accessor.GetText!(b);
            if (string.Equals(after, current, StringComparison.OrdinalIgnoreCase)) continue;
            var changes = SettingsChangeSet.Compute(a, b);
            Assert.NotEmpty(changes);
            Assert.Contains(changes, c => string.Equals(c.Before, current, StringComparison.OrdinalIgnoreCase)
                                          && string.Equals(c.After, after, StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(changes, c => c.Before is "On" or "Off" && c.After is "On" or "Off");
        }
    }
    /// <summary>5.1.7 UI test bug 3: the unsaved-changes prompt names the two "Key to open ... instead" settings as the Settings page does.</summary>
    [Theory]
    [InlineData("Next to the current tab", true, "Key to open at the end instead", "Key to open in the background instead")]
    [InlineData("At the end", true, "Key to open next to the current tab instead", "Key to open in the background instead")]
    [InlineData("Next to the current tab", false, "Key to open at the end instead", "Key to open in the foreground instead")]
    public void Key_to_open_instead_labels_match_the_Settings_page(string position, bool foreground, string positionLabel, string foregroundLabel)
    {
        var a = new GlideSettingsState { NewTabPosition = Glide.Core.Workspace.NewTabPositions.Normalize(position), SwitchToNewTabs = foreground };
        var b = a.CloneState();
        b.NewTabPositionKey = Glide.Core.Workspace.NewTabKeys.Shift;
        b.NewTabForegroundKey = Glide.Core.Workspace.NewTabKeys.Alt;
        var changes = SettingsChangeSet.Compute(a, b);
        Assert.Equal(positionLabel, changes.Single(c => c.Name.Contains("Position Key", StringComparison.OrdinalIgnoreCase)).Label);
        Assert.Equal(foregroundLabel, changes.Single(c => c.Name.Contains("Foreground Key", StringComparison.OrdinalIgnoreCase)).Label);
        // Never the catalogue's static wording, which the Settings page does not show.
        Assert.DoesNotContain(changes, c => c.Label.Contains("other position", StringComparison.OrdinalIgnoreCase) || c.Label.Contains("foreground or background", StringComparison.OrdinalIgnoreCase));
    }
}
