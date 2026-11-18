using System.Text.RegularExpressions;
using Avalonia.Controls;
using Glide.App.Controls;
using Glide.Core.Settings;

namespace Glide.Core.Tests;

/// <summary>5.1.0 Tooltips: one master list of areas, settings that match it, and no literal per-control delays left.</summary>
[Collection(AvaloniaHeadlessCollection.Name)]
[Trait("Area", "Settings")]
public sealed class TooltipSettingsTests
{
    public TooltipSettingsTests(AvaloniaHeadlessFixture fixture) => _ = fixture;

    [Fact]
    public void Every_area_has_a_catalogue_setting_with_the_same_default_and_the_state_matches()
    {
        var state = new GlideSettingsState();
        foreach (var area in TooltipAreas.All)
        {
            var entry = SettingsCatalog.All.Single(e => e.Id == area.SettingId);
            Assert.Equal(SettingKind.Number, entry.Kind);
            Assert.Equal(area.DefaultMs, Convert.ToInt32(entry.DefaultValue));
            Assert.Equal(area.DefaultMs, TooltipAreas.DelayMs(state, area.Id));
            Assert.Contains("tooltip", entry.SearchTerms);
            Assert.Contains("delay", entry.SearchTerms);
        }
        Assert.Equal(1500, TooltipAreas.Find(TooltipAreas.Explorer).DefaultMs);
        Assert.True(state.ExplorerItemInfoTooltip);
        Assert.Equal(true, SettingsCatalog.All.Single(e => e.Id == TooltipAreas.ExplorerInfoTooltipSettingId).DefaultValue);
    }

    [Fact]
    public void Delays_are_clamped_and_written_per_area()
    {
        var state = new GlideSettingsState();
        TooltipAreas.SetDelayMs(state, TooltipAreas.Overlay, 99999);
        TooltipAreas.SetDelayMs(state, TooltipAreas.Settings, -5);
        Assert.Equal(TooltipAreas.MaxMs, TooltipAreas.DelayMs(state, TooltipAreas.Overlay));
        Assert.Equal(0, TooltipAreas.DelayMs(state, TooltipAreas.Settings));
        Assert.Equal(600, TooltipAreas.DelayMs(state, TooltipAreas.Main));
    }

    [Fact]
    public void An_area_style_sets_the_delay_on_every_control_below_and_can_be_replaced() => AvaloniaHeadlessFixture.RunOnUIThread(() =>
    {
        var child = new Button();
        var root = new Window { Content = new StackPanel { Children = { child } } };
        root.Show();
        TooltipDelayStyle.Apply(root, 1500);
        root.UpdateLayout(); // styles reach the controls on the next layout pass
        Assert.Equal(1500, ToolTip.GetShowDelay(child));
        TooltipDelayStyle.Apply(root, 300);
        root.UpdateLayout();
        Assert.Equal(300, ToolTip.GetShowDelay(child));
        Assert.Equal(300, TooltipDelayStyle.AppliedMs(root));
        root.Close();
    });

    [Fact]
    public void No_control_sets_its_own_tooltip_delay()
    {
        var dir = FindSrc();
        var offenders = Directory.EnumerateFiles(dir, "*.cs", SearchOption.AllDirectories)
            .Where(f => Regex.IsMatch(File.ReadAllText(f), @"ToolTip\.SetShowDelay\("))
            .Select(Path.GetFileName).ToList();
        Assert.Empty(offenders);
    }

    private static string FindSrc()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "src", "Glide.App"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return Path.Combine(dir!.FullName, "src", "Glide.App");
    }
}
