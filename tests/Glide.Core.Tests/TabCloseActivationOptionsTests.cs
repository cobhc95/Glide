using Glide.Core.Settings;
using Glide.Core.Workspace;

namespace Glide.Core.Tests;

/// <summary>5.1.0: "After closing a tab, activate" gained First tab, Last tab and Custom… ("Custom tab to show").</summary>
[Trait("Area", "Tabs")]
public sealed class TabCloseActivationOptionsTests
{
    private static (WorkspaceState W, Guid[] Ids) Tabs(int count)
    {
        var w = new WorkspaceState(createHome: false);
        var ids = Enumerable.Range(0, count).Select(_ => w.AddHome().Id).ToArray();
        return (w, ids);
    }

    [Fact]
    public void Old_values_keep_their_place_and_new_ones_are_appended()
    {
        Assert.Equal(["Match Ctrl+Tab order", "Previously used tab", "Tab to the right", "Tab to the left", "First tab", "Last tab", "Custom\u2026"],
            TabCloseActivation.All);
        Assert.Equal(TabCloseActivation.All, SettingActionCatalog.Choices["tabs.afterCloseActivate"]);
        Assert.Equal(TabCloseCustomTargets.All, SettingActionCatalog.Choices["tabs.afterCloseCustomTarget"]);
        Assert.Equal(TabCloseActivation.PreviouslyUsed, TabCloseActivation.Default);
        Assert.Equal(TabCloseCustomTargets.Home, new GlideSettingsState().AfterTabCloseCustomTarget);
        Assert.Equal(TabCloseActivation.Custom, TabCloseActivation.Normalize("custom..."));
    }

    [Theory]
    [InlineData("First tab", TabCloseActivationMode.First)]
    [InlineData("Last tab", TabCloseActivationMode.Last)]
    [InlineData("Custom\u2026", TabCloseActivationMode.Custom)]
    [InlineData("Tab to the left", TabCloseActivationMode.Left)]
    public void Settings_values_resolve_to_modes(string value, TabCloseActivationMode mode) =>
        Assert.Equal(mode, TabCloseActivation.Resolve(value, TabCycleOrders.TabOrder));

    [Theory]
    // closed index -> expected original index of the activated tab (5 tabs, the closed one is active)
    [InlineData(TabCloseActivationMode.First, 0, 1)]
    [InlineData(TabCloseActivationMode.First, 2, 0)]
    [InlineData(TabCloseActivationMode.Last, 4, 3)]
    [InlineData(TabCloseActivationMode.Last, 1, 4)]
    [InlineData(TabCloseActivationMode.Right, 4, 3)]
    [InlineData(TabCloseActivationMode.Left, 0, 1)]
    public void First_and_last_pick_the_ends_also_when_closing_an_end(TabCloseActivationMode mode, int closed, int expected)
    {
        var (w, ids) = Tabs(5);
        w.Select(ids[closed]);
        w.Close(ids[closed], false, mode, null);
        Assert.Equal(ids[expected], w.Active!.Id);
    }

    [Fact]
    public void Custom_falls_back_to_the_previously_used_tab()
    {
        var (w, ids) = Tabs(4);
        w.Select(ids[1]);
        w.Close(ids[1], false, TabCloseActivationMode.Custom, [ids[1], ids[3], ids[0]]);
        Assert.Equal(ids[3], w.Active!.Id);
    }

    [Theory]
    [InlineData(TabCloseActivationMode.First)]
    [InlineData(TabCloseActivationMode.Last)]
    [InlineData(TabCloseActivationMode.Custom)]
    public void Closing_the_only_tab_leaves_no_active_tab(TabCloseActivationMode mode)
    {
        var (w, ids) = Tabs(1);
        w.Close(ids[0], false, mode, null);
        Assert.Empty(w.Tabs);
        Assert.Null(w.Active);
    }

    [Fact]
    public void Custom_targets_make_the_chosen_tab_or_fall_back()
    {
        var picture = new ImageTabState(Guid.NewGuid(), Path.Combine(Path.GetTempPath(), "pics", "a.png"));
        var home = new HomeTabState(Guid.NewGuid());
        bool Exists(string _) => true;
        bool Missing(string _) => false;

        Assert.IsType<HomeTabState>(TabCloseCustomTargets.Create("Home", picture, null, Exists));
        Assert.IsType<HomeTabState>(TabCloseCustomTargets.Create("??", picture, null, Exists)); // unknown -> Home

        var explorer = Assert.IsType<BrowserTabState>(TabCloseCustomTargets.Create(TabCloseCustomTargets.NewExplorer, home, @"C:\Last", Exists));
        Assert.Equal(@"C:\Last", explorer.Folder);
        Assert.Null(TabCloseCustomTargets.Create(TabCloseCustomTargets.NewExplorer, home, null, Exists));
        Assert.Null(TabCloseCustomTargets.Create(TabCloseCustomTargets.NewExplorer, home, @"C:\Gone", Missing));

        var folder = Assert.IsType<BrowserTabState>(TabCloseCustomTargets.Create(TabCloseCustomTargets.PictureFolder, picture, null, Exists));
        Assert.Equal(Path.GetDirectoryName(picture.Path), folder.Folder);
        Assert.Null(TabCloseCustomTargets.Create(TabCloseCustomTargets.PictureFolder, home, null, Exists)); // not a picture
        Assert.Null(TabCloseCustomTargets.Create(TabCloseCustomTargets.PictureFolder, picture, null, Missing)); // folder gone
    }
}
