using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Glide.App;
using Glide.App.Settings;
using Glide.Core.Settings;
using Glide.Core.Workspace;

namespace Glide.Core.Tests;

/// <summary>
/// 5.0.2: in fullscreen the auto-hidden title/tab bar slid in OVER the Explorer tab's own navigation row (Back / Forward /
/// Up / address bar / filter), making it unusable. With an Explorer tab selected the bar now stays docked above that row,
/// exactly like windowed mode; image / Welcome tabs keep the normal edge-to-edge auto-hide.
/// </summary>
[Collection(AvaloniaHeadlessCollection.Name)]
public sealed class FullscreenExplorerTitleBarTests
{
    public FullscreenExplorerTitleBarTests(AvaloniaHeadlessFixture fixture) { _ = fixture; }

    private const string SettingId = "fullscreen.keepTitleBarForExplorer";

    // ---- pure decision ------------------------------------------------------------------------------------------

    [Theory]
    // keepTabBarOpen, autoHide, keepForExplorer, explorerRow -> mode
    [InlineData(false, true, true, true, FullscreenTitleBarMode.Docked)]     // the fix: Explorer active, default settings
    [InlineData(false, true, true, false, FullscreenTitleBarMode.AutoHide)]  // image / Welcome tab: unchanged auto-hide
    [InlineData(false, true, false, true, FullscreenTitleBarMode.AutoHide)]  // setting off: previous behaviour
    [InlineData(false, true, false, false, FullscreenTitleBarMode.AutoHide)]
    [InlineData(false, false, false, false, FullscreenTitleBarMode.Overlay)] // auto-hide off, image: edge-to-edge overlay as before
    [InlineData(false, false, true, false, FullscreenTitleBarMode.Overlay)]
    [InlineData(false, false, false, true, FullscreenTitleBarMode.Docked)]   // an always-shown overlay would permanently cover the row
    [InlineData(false, false, true, true, FullscreenTitleBarMode.Docked)]
    [InlineData(true, true, false, false, FullscreenTitleBarMode.Docked)]    // "Keep tab/title bar open" still pins everywhere
    [InlineData(true, false, false, true, FullscreenTitleBarMode.Docked)]
    [InlineData(true, true, true, true, FullscreenTitleBarMode.Docked)]
    public void Resolve_docks_the_bar_only_where_it_would_cover_a_navigation_row_or_is_pinned(
        bool keepTabBarOpen, bool autoHide, bool keepForExplorer, bool explorerRow, FullscreenTitleBarMode expected) =>
        Assert.Equal(expected, FullscreenTitleBarPolicy.Resolve(keepTabBarOpen, autoHide, keepForExplorer, explorerRow));

    [Theory]
    [InlineData(1.0, 18)]
    [InlineData(1.25, 14.4)]
    [InlineData(1.5, 14)]
    [InlineData(2.0, 14)]
    [InlineData(double.NaN, 18)]
    public void Reveal_edge_is_unchanged_without_a_navigation_row(double scaling, double expected) =>
        Assert.Equal(expected, FullscreenTitleBarPolicy.RevealEdgeFor(scaling, navigationRowVisible: false), 3);

    [Theory]
    [InlineData(1.0)]
    [InlineData(2.0)]
    public void Reveal_edge_is_narrow_above_a_navigation_row_so_its_controls_stay_clickable(double scaling)
    {
        var edge = FullscreenTitleBarPolicy.RevealEdgeFor(scaling, navigationRowVisible: true);
        Assert.Equal(FullscreenTitleBarPolicy.NavigationRowRevealEdge, edge);
        Assert.True(edge < FullscreenTitleBarPolicy.RevealEdge);
    }

    // ---- setting contract (AGENTS.md: catalogue, search, hotkey-bindable) ----------------------------------------

    [Fact]
    public void Setting_is_catalogued_searchable_bindable_and_on_by_default()
    {
        var setting = Assert.Single(SettingsCatalog.All, x => x.Id == SettingId);
        Assert.Equal("Window & navigation", setting.Category);
        Assert.Equal(SettingKind.Toggle, setting.Kind);
        Assert.True((bool)setting.DefaultValue);
        Assert.Null(setting.FuturePhase);
        foreach (var term in new[] { "fullscreen", "full screen", "title bar", "explorer", "address bar", "auto-hide" })
            Assert.Contains(term, setting.SearchTerms);
        Assert.True(SettingActionCatalog.IsBindable(setting));
        Assert.False(SettingActionCatalog.Excluded.ContainsKey(SettingId));
        Assert.Contains(SettingEffectRegistry.All, e => e.SettingId == SettingId);

        var state = new GlideSettingsState();
        Assert.True(state.FullscreenKeepTitleBarForExplorer);
        var accessor = SettingAccessors.Find(SettingId);
        Assert.NotNull(accessor);
        accessor!.SetBool!(state, false);
        Assert.False(state.FullscreenKeepTitleBarForExplorer);
        Assert.False(accessor.GetBool!(state));
    }

    // ---- real headless window -----------------------------------------------------------------------------------

    [Fact]
    public async Task Fullscreen_Explorer_tab_docks_the_title_bar_and_other_tabs_keep_auto_hide()
    {
        var root = Path.Combine(Path.GetTempPath(), "Glide.FullscreenExplorer", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "Sub"));
        try
        {
            await OnWindow(async window =>
            {
                window.WindowState = WindowState.FullScreen;
                await Settle();
                // Welcome tab: the ordinary edge-to-edge auto-hide.
                AssertAutoHidden(window, "Welcome tab");

                await (Task)Invoke(window, "OpenExplorerTabAsync", root)!;
                await Settle();
                Assert.True(((Control)GetField(window, "BrowserView")!).IsVisible);
                AssertDocked(window, "Explorer tab");
                // Pinned: moving the pointer away never hides it.
                Invoke(window, "UpdateFullscreenChromeHover", new Point(400, 600));
                Assert.True(Chrome(window).IsVisible);
                // The Explorer navigation row starts below the bar (nothing overlaps).
                window.UpdateLayout();
                var browserView = (Control)GetField(window, "BrowserView")!;
                var browserTop = browserView.TranslatePoint(default, window)!.Value.Y;
                var chromeBottom = Chrome(window).TranslatePoint(new Point(0, Chrome(window).Bounds.Height), window)!.Value.Y;
                Assert.True(browserTop >= chromeBottom - 0.5, $"Explorer view top {browserTop} is under the title bar (bottom {chromeBottom})");

                // Back to the Welcome tab: auto-hide again immediately; and pinned again on the way back.
                await (Task)Invoke(window, "SelectTabIndexAsync", 0)!;
                await Settle();
                AssertAutoHidden(window, "Welcome tab after Explorer");
                await (Task)Invoke(window, "SelectTabIndexAsync", 1)!;
                await Settle();
                AssertDocked(window, "Explorer tab again");

                // Setting off (as a hotkey would flip it): previous overlay auto-hide, with a narrow reveal edge.
                await (Task)Invoke(window, "ExecuteGestureActionAsync", "setting." + SettingId, 0, null)!;
                await Settle();
                Assert.False(Settings(window).FullscreenKeepTitleBarForExplorer);
                AssertAutoHidden(window, "Explorer tab, setting off");
                Invoke(window, "UpdateFullscreenChromeHover", new Point(400, 8));
                Assert.False(Chrome(window).IsVisible); // the upper part of the Explorer row stays clickable
                Invoke(window, "UpdateFullscreenChromeHover", new Point(400, 1));
                Assert.True(Chrome(window).IsVisible);  // the very top edge still reveals the bar
                Invoke(window, "UpdateFullscreenChromeHover", new Point(400, 600));
                Assert.False(Chrome(window).IsVisible);

                // Leaving fullscreen: ordinary windowed chrome whatever the tab.
                Settings(window).FullscreenKeepTitleBarForExplorer = true;
                window.WindowState = WindowState.Normal;
                await Settle();
                Assert.Equal(44, MainRoot(window).RowDefinitions[0].Height.Value);
                Assert.Equal(0, Grid.GetRow(Chrome(window)));
            });
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
        }
    }

    private static void AssertDocked(MainWindow window, string where)
    {
        Assert.True(MainRoot(window).RowDefinitions[0].Height.Value == 44, where + ": row 0 reserves the bar height");
        Assert.True(Grid.GetRow(Chrome(window)) == 0, where + ": bar sits in its own row");
        Assert.True(Chrome(window).IsVisible, where + ": bar is shown");
    }

    private static void AssertAutoHidden(MainWindow window, string where)
    {
        Assert.True(MainRoot(window).RowDefinitions[0].Height.Value == 0, where + ": edge-to-edge (no reserved row)");
        Assert.True(Grid.GetRow(Chrome(window)) == 1, where + ": bar overlays the view");
        Assert.False(Chrome(window).IsVisible, where + ": bar is auto-hidden");
    }

    private static Border Chrome(MainWindow window) => (Border)GetField(window, "ChromeBorder")!;
    private static Grid MainRoot(MainWindow window) => (Grid)GetField(window, "MainRoot")!;
    private static GlideSettingsState Settings(MainWindow window) => (GlideSettingsState)GetField(window, "_settings")!;

    private static async Task Settle()
    {
        for (var i = 0; i < 4; i++) { Dispatcher.UIThread.RunJobs(); await Task.Delay(5); }
    }

    private static async Task OnWindow(Func<MainWindow, Task> body)
    {
        var root = Path.Combine(Path.GetTempPath(), "Glide.Settings.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        await AvaloniaHeadlessFixture.RunOnUIThreadAsync(async () =>
        {
            var previousStore = SettingsStore.SettingsDirectoryOverrideForTests.Value;
            SettingsStore.SettingsDirectoryOverrideForTests.Value = root;
            var window = new MainWindow();
            try
            {
                SetField(window, "_settings", new GlideSettingsState());
                window.Show();
                await Settle();
                ((WorkspaceState)GetField(window, "_workspace")!).Reset(new TabState[] { new HomeTabState(Guid.NewGuid()) }, 0);
                await body(window);
            }
            finally
            {
                window.ActionInterceptorForTests = (_, _) => true;
                window.Close();
                SettingsStore.SettingsDirectoryOverrideForTests.Value = previousStore;
            }
        });
        try { Directory.Delete(root, true); } catch { }
    }

    private const BindingFlags Flags = BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance;

    private static object? Invoke(MainWindow window, string method, params object?[] args)
    {
        var candidates = typeof(MainWindow).GetMethods(Flags).Where(m => m.Name == method && m.GetParameters().Length == args.Length).ToArray();
        Assert.True(candidates.Length > 0, method);
        return candidates[0].Invoke(window, args);
    }

    private static object? GetField(MainWindow window, string name)
    {
        var field = typeof(MainWindow).GetField(name, Flags);
        Assert.NotNull(field);
        return field!.GetValue(window);
    }

    private static void SetField(MainWindow window, string name, object? value)
    {
        var field = typeof(MainWindow).GetField(name, Flags);
        Assert.NotNull(field);
        field!.SetValue(window, value);
    }
}
