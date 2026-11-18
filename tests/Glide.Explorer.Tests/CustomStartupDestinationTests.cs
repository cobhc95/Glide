using Glide.App;
using Glide.App.Controls;
using Glide.App.Settings;
using Glide.Core.Workspace;

namespace Glide.Explorer.Tests;

/// <summary>
/// "When Glide opens: Custom" and "New tab opens: Custom / Explorer tab" with a custom path (5.0.0 fix).
/// A custom FOLDER opens as an Explorer tab, a custom image FILE opens as an image tab, a missing or
/// unusable path falls back to the Welcome page (or the usual Explorer start folder) with a short,
/// non-blocking message, and "Open last session" / "Welcome tab" are unaffected by the custom path.
/// </summary>
[Collection(ExplorerHeadlessCollection.Name)]
[Trait("Area", "Explorer")]
public sealed class CustomStartupDestinationTests
{
    private static string Beta(ExplorerHarness h) => Path.Combine(h.Root, "Beta");
    private static string Gamma(ExplorerHarness h) => Path.Combine(h.Root, "Gamma");
    private static string ImageA(ExplorerHarness h) => Path.Combine(h.Root, ExplorerHarness.ImageA);

    private static IReadOnlyDictionary<Guid, string> Notices(ExplorerHarness h) => h.Get<Dictionary<Guid, string>>("_tabNotices");

    private static void ApplyStartup(ExplorerHarness h) => h.Call("ApplyConfiguredStartupWorkspace");

    private static async Task ActivateAsync(ExplorerHarness h)
    {
        h.Call("RebuildTabStrip");
        await (Task)h.Call("ActivateWorkspaceAsync")!;
        await h.SettleAsync();
    }

    // ---- startup: When Glide opens = Custom ------------------------------------------------------

    [Fact]
    public async Task Startup_custom_folder_opens_that_folder_in_an_Explorer_tab()
    {
        await ExplorerHarness.Run(async h =>
        {
            h.Settings.StartupAction = "Custom";
            h.Settings.StartupCustomPath = Beta(h);
            h.Settings.HomePageMode = "Welcome page";
            ApplyStartup(h);

            var tab = Assert.IsType<BrowserTabState>(h.Workspace.Active);
            Assert.Equal(Path.GetFullPath(Beta(h)), tab.Folder);
            Assert.Single(h.Workspace.Tabs);

            await ActivateAsync(h);
            Assert.Equal(Path.GetFullPath(Beta(h)), h.Surface.AddressBar.Path.TrimEnd('\\'));
            Assert.Empty(Notices(h));
        }, startExplorer: false);
    }

    [Fact]
    public async Task Startup_custom_folder_with_trailing_slash_and_quotes_still_resolves()
    {
        await ExplorerHarness.Run(h =>
        {
            h.Settings.StartupAction = "Custom";
            h.Settings.StartupCustomPath = "\"" + Gamma(h) + Path.DirectorySeparatorChar + "\"";
            ApplyStartup(h);
            var tab = Assert.IsType<BrowserTabState>(h.Workspace.Active);
            Assert.Equal(Path.GetFullPath(Gamma(h)).TrimEnd('\\'), tab.Folder.TrimEnd('\\'));
            return Task.CompletedTask;
        }, startExplorer: false);
    }

    [Fact]
    public async Task Startup_custom_image_file_opens_that_image()
    {
        await ExplorerHarness.Run(h =>
        {
            h.Settings.StartupAction = "Custom";
            h.Settings.StartupCustomPath = ImageA(h);
            ApplyStartup(h);
            var tab = Assert.IsType<ImageTabState>(h.Workspace.Active);
            Assert.Equal(Path.GetFullPath(ImageA(h)), tab.Path);
            Assert.Empty(Notices(h));
            return Task.CompletedTask;
        }, startExplorer: false);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("unsupported")]
    [InlineData("empty")]
    public async Task Startup_custom_path_that_cannot_be_used_falls_back_to_Welcome_with_a_message(string problem)
    {
        await ExplorerHarness.Run(async h =>
        {
            h.Settings.StartupAction = "Custom";
            h.Settings.StartupCustomPath = problem switch
            {
                "missing" => Path.Combine(h.Root, "NoSuchFolder"),
                "unsupported" => Path.Combine(h.Root, "notes.txt"),
                _ => ""
            };
            ApplyStartup(h);

            var home = Assert.IsType<HomeTabState>(h.Workspace.Active);
            Assert.True(Notices(h).TryGetValue(home.Id, out var notice), "a fallback notice is recorded");
            Assert.False(string.IsNullOrWhiteSpace(notice));

            await ActivateAsync(h);
            var homeSurface = h.Get<HomeSurface>("_homeSurface");
            Assert.Contains(notice!, homeSurface.SubtitleText.Text, StringComparison.Ordinal);
            Assert.Contains("Welcome page", homeSurface.SubtitleText.Text, StringComparison.Ordinal);
        }, startExplorer: false);
    }

    // ---- interaction with the other startup actions -----------------------------------------------

    [Fact]
    public async Task Welcome_tab_startup_ignores_a_stored_custom_path()
    {
        await ExplorerHarness.Run(h =>
        {
            h.Settings.StartupAction = "Welcome tab";
            h.Settings.StartupCustomPath = Beta(h);
            ApplyStartup(h);
            var home = Assert.IsType<HomeTabState>(h.Workspace.Active);
            Assert.Empty(Notices(h));
            Assert.Single(h.Workspace.Tabs);
            Assert.NotEqual(Guid.Empty, home.Id);
            return Task.CompletedTask;
        }, startExplorer: false);
    }

    [Fact]
    public async Task Open_last_session_wins_over_a_stored_custom_path_and_restores_the_saved_tabs()
    {
        await ExplorerHarness.Run(h =>
        {
            h.Settings.StartupAction = "Open last session";
            h.Settings.StartupCustomPath = Beta(h);
            WorkspaceSessionStore.Save(new TabState[] { new BrowserTabState(Guid.NewGuid(), Gamma(h)) }, 0);
            ApplyStartup(h);

            var tab = Assert.IsType<BrowserTabState>(h.Workspace.Active);
            Assert.Equal(Path.GetFullPath(Gamma(h)), tab.Folder);
            Assert.Empty(Notices(h));
            return Task.CompletedTask;
        }, startExplorer: false);
    }

    [Fact]
    public async Task Open_last_session_without_a_saved_session_opens_Welcome_not_the_custom_path()
    {
        await ExplorerHarness.Run(h =>
        {
            h.Settings.StartupAction = "Open last session";
            h.Settings.StartupCustomPath = Beta(h);
            var file = Path.Combine(h.SettingsRoot, "glide.workspace-session.json");
            if (File.Exists(file)) File.Delete(file);
            ApplyStartup(h);

            Assert.IsType<HomeTabState>(h.Workspace.Active);
            Assert.Empty(Notices(h));
            return Task.CompletedTask;
        }, startExplorer: false);
    }

    // ---- new tab ----------------------------------------------------------------------------------

    [Fact]
    public async Task New_tab_custom_folder_opens_an_Explorer_tab_on_that_folder()
    {
        await ExplorerHarness.Run(async h =>
        {
            h.Settings.NewTabAction = "Custom";
            h.Settings.NewTabCustomPath = Beta(h);
            var tab = Assert.IsType<BrowserTabState>(h.Call("CreateConfiguredNewTabDestination"));
            Assert.Equal(Path.GetFullPath(Beta(h)), tab.Folder);
            Assert.Same(tab, h.Workspace.Active);
            await ActivateAsync(h);
            Assert.Equal(Path.GetFullPath(Beta(h)), h.Surface.AddressBar.Path.TrimEnd('\\'));
        }, startExplorer: false);
    }

    [Fact]
    public async Task New_tab_custom_image_file_opens_that_image()
    {
        await ExplorerHarness.Run(h =>
        {
            h.Settings.NewTabAction = "Custom";
            h.Settings.NewTabCustomPath = ImageA(h);
            var tab = Assert.IsType<ImageTabState>(h.Call("CreateConfiguredNewTabDestination"));
            Assert.Equal(Path.GetFullPath(ImageA(h)), tab.Path);
            return Task.CompletedTask;
        }, startExplorer: false);
    }

    [Fact]
    public async Task New_tab_custom_missing_path_opens_Welcome_with_a_message()
    {
        await ExplorerHarness.Run(h =>
        {
            h.Settings.NewTabAction = "Custom";
            h.Settings.NewTabCustomPath = Path.Combine(h.Root, "gone");
            var tab = Assert.IsType<HomeTabState>(h.Call("CreateConfiguredNewTabDestination"));
            Assert.True(Notices(h).TryGetValue(tab.Id, out var notice));
            Assert.Contains("not found", notice, StringComparison.OrdinalIgnoreCase);
            return Task.CompletedTask;
        }, startExplorer: false);
    }

    /// <summary>The owner's report: Explorer tab + New-tab custom path, yet the tab opened at the last-used folder.</summary>
    [Fact]
    public async Task New_tab_Explorer_mode_opens_the_custom_folder_even_when_a_last_location_and_default_directory_exist()
    {
        await ExplorerHarness.Run(async h =>
        {
            h.Settings.NewTabAction = "Explorer tab";
            h.Settings.NewTabCustomPath = Beta(h);
            h.Settings.NewExplorerTabsUseLastLocation = true;
            h.Settings.LastNewExplorerTabDirectory = Gamma(h);
            h.Settings.NewExplorerTabDefaultDirectory = Path.Combine(h.Root, "Alpha");

            var tab = Assert.IsType<BrowserTabState>(h.Call("CreateConfiguredNewTabDestination"));
            Assert.Equal(Path.GetFullPath(Beta(h)), tab.Folder);
            await ActivateAsync(h);
            Assert.Equal(Path.GetFullPath(Beta(h)), h.Surface.AddressBar.Path.TrimEnd('\\'));
            Assert.Empty(Notices(h));
        }, startExplorer: false);
    }

    [Fact]
    public async Task New_tab_Explorer_mode_without_a_custom_path_still_uses_the_last_location()
    {
        await ExplorerHarness.Run(h =>
        {
            h.Settings.NewTabAction = "Explorer tab";
            h.Settings.NewTabCustomPath = "";
            h.Settings.NewExplorerTabsUseLastLocation = true;
            h.Settings.LastNewExplorerTabDirectory = Gamma(h);
            var tab = Assert.IsType<BrowserTabState>(h.Call("CreateConfiguredNewTabDestination"));
            Assert.Equal(Path.GetFullPath(Gamma(h)), tab.Folder);
            Assert.Empty(Notices(h));
            return Task.CompletedTask;
        }, startExplorer: false);
    }

    [Fact]
    public async Task New_tab_Explorer_mode_with_an_image_custom_path_opens_the_folder_of_that_image()
    {
        await ExplorerHarness.Run(h =>
        {
            h.Settings.NewTabAction = "Explorer tab";
            h.Settings.NewTabCustomPath = Path.Combine(h.Root, "Alpha", "inner.png");
            h.Settings.LastNewExplorerTabDirectory = Gamma(h);
            var tab = Assert.IsType<BrowserTabState>(h.Call("CreateConfiguredNewTabDestination"));
            Assert.Equal(Path.GetFullPath(Path.Combine(h.Root, "Alpha")), tab.Folder);
            return Task.CompletedTask;
        }, startExplorer: false);
    }

    [Fact]
    public async Task New_tab_Explorer_mode_with_a_missing_custom_folder_falls_back_to_the_usual_folder_and_says_so()
    {
        await ExplorerHarness.Run(async h =>
        {
            h.Settings.NewTabAction = "Explorer tab";
            h.Settings.NewTabCustomPath = Path.Combine(h.Root, "NoSuchFolder");
            h.Settings.NewExplorerTabsUseLastLocation = true;
            h.Settings.LastNewExplorerTabDirectory = Gamma(h);

            var tab = Assert.IsType<BrowserTabState>(h.Call("CreateConfiguredNewTabDestination"));
            Assert.Equal(Path.GetFullPath(Gamma(h)), tab.Folder);
            Assert.True(Notices(h).ContainsKey(tab.Id));

            await ActivateAsync(h);
            Assert.Contains("not found", h.Surface.MessageText.Text, StringComparison.OrdinalIgnoreCase);
            Assert.False(Notices(h).ContainsKey(tab.Id), "the notice is shown once");
        }, startExplorer: false);
    }

    [Fact]
    public async Task New_tab_Welcome_mode_ignores_a_stored_custom_path()
    {
        await ExplorerHarness.Run(h =>
        {
            h.Settings.NewTabAction = "Welcome tab";
            h.Settings.NewTabCustomPath = Beta(h);
            Assert.IsType<HomeTabState>(h.Call("CreateConfiguredNewTabDestination"));
            Assert.Empty(Notices(h));
            return Task.CompletedTask;
        }, startExplorer: false);
    }
}
