using System.Reflection;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Glide.App;
using Glide.App.Settings;
using Glide.Core.Commands;
using Glide.Core.Workspace;

namespace Glide.Core.Tests;

/// <summary>
/// The title-bar "Show in Explorer" button (file.locate): right-click menu choice between Glide's own
/// Explorer tab (default) and Windows File Explorer, persisted as the ShowInExplorerTarget setting.
/// Nothing here launches explorer.exe (the launcher seam is replaced) or writes the real settings file.
/// </summary>
[Collection(AvaloniaHeadlessCollection.Name)]
[Trait("Area", "Explorer")]
public sealed class TitleBarLocateButtonTests
{
    private const string TabHeader = "Show in Glide's Explorer tab";
    private const string WindowsHeader = "Show in Windows File Explorer";
    private const string OtherHeader = "Show in other file manager…";

    public TitleBarLocateButtonTests(AvaloniaHeadlessFixture fixture) { _ = fixture; }

    // ---- settings model / migration (no UI) ---------------------------------------------------------

    [Fact]
    public void Default_is_Glides_own_Explorer_tab()
    {
        var state = new GlideSettingsState();
        Assert.Equal("Glide's Explorer tab", state.ShowInExplorerTarget);
        Assert.False(ShowInExplorerTargets.IsWindowsExplorer(state.ShowInExplorerTarget));
        Assert.Equal("Large thumbnails", state.ExplorerDefaultView);
        Assert.False(state.ExplorerShowHiddenFiles);
        Assert.True(state.ExplorerShowOnlyImages);
    }

    [Theory]
    [InlineData(null, "Glide's Explorer tab")]
    [InlineData("", "Glide's Explorer tab")]
    [InlineData("Glide's Explorer tab", "Glide's Explorer tab")]
    [InlineData("Windows File Explorer", "Windows File Explorer")]
    [InlineData("windows file explorer", "Windows File Explorer")]
    // Legacy hidden values: "Windows Explorer" was the never-chosen stock default of every old file.
    [InlineData("Windows Explorer", "Glide's Explorer tab")]
    [InlineData("Internal browser", "Glide's Explorer tab")]
    [InlineData("something unexpected", "Glide's Explorer tab")]
    public void Saved_values_map_to_the_new_meaning(string? saved, string expected) =>
        Assert.Equal(expected, ShowInExplorerTargets.Normalize(saved));

    [Fact]
    public void Loading_a_file_applies_the_migration_and_keeps_an_explicit_Windows_choice()
    {
        WithIsolatedStore(root =>
        {
            // Older file: only the hidden legacy value, no ShowInExplorerTarget -> new default.
            File.WriteAllText(Path.Combine(root, "glide.settings.json"), "{\"NavigateToFolderBehavior\":\"Windows Explorer\"}");
            Assert.Equal(ShowInExplorerTargets.GlideTab, SettingsStore.Load().ShowInExplorerTarget);

            File.WriteAllText(Path.Combine(root, "glide.settings.json"), "{\"NavigateToFolderBehavior\":\"Internal browser\"}");
            Assert.Equal(ShowInExplorerTargets.GlideTab, SettingsStore.Load().ShowInExplorerTarget);

            // An explicit choice of Windows File Explorer survives a save/load round trip.
            var state = new GlideSettingsState { ShowInExplorerTarget = ShowInExplorerTargets.WindowsExplorer };
            SettingsStore.Save(state);
            Assert.Equal(ShowInExplorerTargets.WindowsExplorer, SettingsStore.Load().ShowInExplorerTarget);

            // Garbage view / target values fall back to the defaults.
            File.WriteAllText(Path.Combine(root, "glide.settings.json"), "{\"ShowInExplorerTarget\":\"x\",\"ExplorerDefaultView\":\"nope\"}");
            var loaded = SettingsStore.Load();
            Assert.Equal(ShowInExplorerTargets.GlideTab, loaded.ShowInExplorerTarget);
            Assert.Equal("Large thumbnails", loaded.ExplorerDefaultView);
        });
    }

    [Fact]
    public void Explorer_view_and_filter_settings_round_trip()
    {
        WithIsolatedStore(_ =>
        {
            SettingsStore.Save(new GlideSettingsState { ExplorerDefaultView = "Details", ExplorerShowHiddenFiles = true, ExplorerShowOnlyImages = false });
            var loaded = SettingsStore.Load();
            Assert.Equal("Details", loaded.ExplorerDefaultView);
            Assert.True(loaded.ExplorerShowHiddenFiles);
            Assert.False(loaded.ExplorerShowOnlyImages);
        });
    }

    [Fact]
    public void Tooltip_label_follows_the_choice()
    {
        Assert.Equal("Show in Explorer tab (select current image)", TitleBarButtonCatalog.LocateLabel(ShowInExplorerTargets.GlideTab));
        Assert.Equal("Show in File Explorer (select current image)", TitleBarButtonCatalog.LocateLabel(ShowInExplorerTargets.WindowsExplorer));
        Assert.Equal(TitleBarButtonCatalog.LocateLabel(ShowInExplorerTargets.GlideTab), TitleBarButtonCatalog.Find("file.locate")!.Label);
    }

    // ---- context menu -------------------------------------------------------------------------------

    [Fact]
    public async Task Menu_offers_both_choices_with_the_default_ticked_in_front_of_the_standard_items()
    {
        await OnWindow(window =>
        {
            var headers = MenuHeaders(window);
            Assert.Equal(TabHeader + "  ✓", headers[0]);
            Assert.Equal(WindowsHeader, headers[1]);
            Assert.Equal(OtherHeader, headers[2]);
            Assert.Equal("-", headers[3]);
            Assert.Equal("Move left", headers[4]);
            Assert.Equal("Move right", headers[5]);
            Assert.Equal("Remove from title bar", headers[6]);
            Assert.Contains("Customize title-bar buttons…", headers);
            Assert.Equal(1, headers.Count(h => h.EndsWith("✓", StringComparison.Ordinal)));
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task Choosing_each_entry_persists_the_setting_and_moves_the_tick()
    {
        await OnWindow(window =>
        {
            Click(window, WindowsHeader);
            Assert.Equal(ShowInExplorerTargets.WindowsExplorer, Settings(window).ShowInExplorerTarget);
            var headers = MenuHeaders(window);
            Assert.Equal(TabHeader, headers[0]);
            Assert.Equal(WindowsHeader + "  ✓", headers[1]);
            Assert.Equal(1, headers.Count(h => h.EndsWith("✓", StringComparison.Ordinal)));

            Click(window, TabHeader);
            Assert.Equal(ShowInExplorerTargets.GlideTab, Settings(window).ShowInExplorerTarget);
            headers = MenuHeaders(window);
            Assert.Equal(TabHeader + "  ✓", headers[0]);
            Assert.Equal(WindowsHeader, headers[1]);
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task Other_title_bar_buttons_do_not_get_the_locate_entries()
    {
        await OnWindow(window =>
        {
            var menu = (ContextMenu)Invoke(window, "BuildTitleActionContextMenu", "file.open")!;
            var headers = menu.ItemsSource!.Cast<object>().OfType<MenuItem>().Select(i => i.Header?.ToString() ?? "").ToList();
            Assert.DoesNotContain(headers, h => h.Contains("Explorer", StringComparison.Ordinal));
            return Task.CompletedTask;
        });
    }

    // ---- behaviour ----------------------------------------------------------------------------------

    [Fact]
    public async Task Default_choice_opens_an_Explorer_tab_on_the_images_folder_with_the_file_marked()
    {
        var (folder, file) = TempImage();
        try
        {
            var launched = new List<string>();
            await OnWindow(async window =>
            {
                ShowImageTab(window, file);
                var imageTabId = Workspace(window).Active!.Id;
                var before = Workspace(window).Tabs.Count;

                await Command(window);

                var active = Assert.IsType<BrowserTabState>(Workspace(window).Active);
                Assert.Equal(Path.GetFullPath(folder), active.Folder);
                Assert.NotEqual(imageTabId, active.Id);
                Assert.Equal(before + 1, Workspace(window).Tabs.Count);
                Assert.True(IsMarkedForSelection(window, active.Id, file), "the image must be marked for selection in the new Explorer tab");

                // Second press: the active Explorer tab already shows that folder, so it is reused.
                await Command(window);
                Assert.Equal(before + 1, Workspace(window).Tabs.Count);
                Assert.Equal(active.Id, Workspace(window).Active!.Id);
            }, launched);
            Assert.Empty(launched);
        }
        finally { TempDir.TryDelete(folder); }
    }

    // ---- "Explore/Browse containing folder" hotkey action (folder.exploreParent) ----------------------------------------
    // Root cause of "does nothing": GlideCommand.ExploreParentFolder was mapped to an empty stub in the command
    // dispatcher, so the hotkey resolved to the command but nothing ran. It now runs the same operation as the title-bar
    // Show in Explorer button and follows the same "Show in Explorer button opens" setting.

    [Fact]
    public void The_action_is_renamed_and_carries_the_search_terms()
    {
        var action = HotkeyCatalog.Find("folder.exploreParent")!;
        Assert.Equal("Explore/Browse containing folder", action.Label);
        Assert.Equal(GlideCommand.ExploreParentFolder, action.Command);
        foreach (var term in new[] { "explore", "browse", "folder", "reveal", "show in explorer", "containing", "locate" })
            Assert.Contains(term, action.SearchTerms!);
    }

    [Fact]
    public async Task Explore_containing_folder_command_opens_the_Explorer_tab_like_the_title_bar_button()
    {
        var (folder, file) = TempImage();
        try
        {
            var launched = new List<string>();
            await OnWindow(async window =>
            {
                ShowImageTab(window, file);
                var imageTabId = Workspace(window).Active!.Id;
                var before = Workspace(window).Tabs.Count;

                await Command(window, GlideCommand.ExploreParentFolder);

                var active = Assert.IsType<BrowserTabState>(Workspace(window).Active);
                Assert.Equal(Path.GetFullPath(folder), active.Folder);
                Assert.NotEqual(imageTabId, active.Id);
                Assert.Equal(before + 1, Workspace(window).Tabs.Count);
                Assert.True(IsMarkedForSelection(window, active.Id, file));
            }, launched);
            Assert.Empty(launched);
        }
        finally { TempDir.TryDelete(folder); }
    }

    [Fact]
    public async Task Explore_containing_folder_command_follows_the_Windows_File_Explorer_choice()
    {
        var (folder, file) = TempImage();
        try
        {
            var launched = new List<string>();
            await OnWindow(async window =>
            {
                Settings(window).ShowInExplorerTarget = ShowInExplorerTargets.WindowsExplorer;
                ShowImageTab(window, file);
                var before = Workspace(window).Tabs.Count;
                var activeId = Workspace(window).Active!.Id;

                await Command(window, GlideCommand.ExploreParentFolder);

                Assert.Equal(new[] { file }, launched);
                Assert.Equal(before, Workspace(window).Tabs.Count);
                Assert.Equal(activeId, Workspace(window).Active!.Id);
            }, launched);
        }
        finally { TempDir.TryDelete(folder); }
    }

    [Fact]
    public async Task A_key_assigned_to_Explore_containing_folder_runs_it_through_the_real_key_handler()
    {
        var (folder, file) = TempImage();
        try
        {
            await OnWindow(async window =>
            {
                Settings(window).Hotkeys["folder.exploreParent"] = new List<string> { "Ctrl+Alt+E" };
                // Show first and let the startup activation settle: it resets the workspace, so the image tab must be
                // set up afterwards. (Before the headless fixture awaited async bodies this test never got this far.)
                window.Show();
                for (var i = 0; i < 10; i++) { Dispatcher.UIThread.RunJobs(); await Task.Delay(10); }
                ShowImageTab(window, file);

                // 1. The shortcut resolves to the command (hotkey table -> InputRouter).
                var router = (Glide.Input.InputRouter)GetField(window, "_inputRouter")!;
                Assert.Equal(GlideCommand.ExploreParentFolder, router.ResolveShortcut("Ctrl+Alt+E", Settings(window).Hotkeys));

                // 2. A real key press on the window runs it (OnKeyDown -> ExecuteCommandAsync -> dispatcher).
                window.KeyPress(Key.E, RawInputModifiers.Control | RawInputModifiers.Alt, PhysicalKey.E, "e");
                await Wait.UntilAsync(() => Workspace(window).Active is BrowserTabState, "Explorer tab opened", pump: () => Dispatcher.UIThread.RunJobs());
                var active = Assert.IsType<BrowserTabState>(Workspace(window).Active);
                Assert.Equal(Path.GetFullPath(folder), active.Folder);
            }, new List<string>());
        }
        finally { TempDir.TryDelete(folder); }
    }

    [Fact]
    public async Task No_command_in_the_production_dispatcher_is_an_empty_stub_for_a_hotkey_action()
    {
        // Every hotkey action's command must be mapped by the real MainWindow dispatcher. (The coverage test only
        // proves the mapping exists; the behaviour tests above prove this one actually does something.)
        await OnWindow(window =>
        {
            var mapped = (IReadOnlyCollection<GlideCommand>)Invoke(window, "ProductionMappedCommandsForTests")!;
            // Generated setting toggles (ChangeSetting) run through ExecuteActionAsync, not the per-command table.
            foreach (var action in HotkeyCatalog.All.Where(a => a.Command != GlideCommand.ChangeSetting))
                Assert.Contains(action.Command, mapped);
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task Windows_choice_launches_explorer_through_the_seam_and_leaves_tabs_alone()
    {
        var (folder, file) = TempImage();
        try
        {
            var launched = new List<string>();
            await OnWindow(async window =>
            {
                Settings(window).ShowInExplorerTarget = ShowInExplorerTargets.WindowsExplorer;
                ShowImageTab(window, file);
                var before = Workspace(window).Tabs.Count;
                var activeId = Workspace(window).Active!.Id;

                await Command(window);

                Assert.Equal(new[] { file }, launched);
                Assert.Equal(before, Workspace(window).Tabs.Count);
                Assert.Equal(activeId, Workspace(window).Active!.Id);
                Assert.IsType<ImageTabState>(Workspace(window).Active);
            }, launched);
        }
        finally { TempDir.TryDelete(folder); }
    }

    [Fact]
    public async Task Without_a_current_image_the_Explorer_tab_opens_at_the_usual_start_location()
    {
        var launched = new List<string>();
        await OnWindow(async window =>
        {
            Workspace(window).Reset(new TabState[] { new HomeTabState(Guid.NewGuid()) }, 0);
            SetField(window, "_currentPath", null);

            await Command(window);

            var active = Assert.IsType<BrowserTabState>(Workspace(window).Active);
            Assert.True(Directory.Exists(active.Folder));
            Assert.Empty(launched);
        }, launched);
    }

    [Fact]
    public async Task ShowPathInExplorerTabAsync_accepts_a_folder_path()
    {
        var (folder, _) = TempImage();
        try
        {
            await OnWindow(async window =>
            {
                Workspace(window).Reset(new TabState[] { new HomeTabState(Guid.NewGuid()) }, 0);
                await (Task)Invoke(window, "ShowPathInExplorerTabAsync", folder)!;
                var active = Assert.IsType<BrowserTabState>(Workspace(window).Active);
                Assert.Equal(Path.GetFullPath(folder), active.Folder);
            }, new List<string>());
        }
        finally { TempDir.TryDelete(folder); }
    }

    [Fact]
    public async Task ShowPathInExplorerTabAsync_reuses_an_inactive_Explorer_tab_already_showing_the_folder()
    {
        var (folder, file) = TempImage();
        try
        {
            await OnWindow(async window =>
            {
                var explorer = new BrowserTabState(Guid.NewGuid(), Path.GetFullPath(folder));
                var image = new ImageTabState(Guid.NewGuid(), file);
                Workspace(window).Reset(new TabState[] { explorer, new HomeTabState(Guid.NewGuid()), image }, 2);
                await (Task)Invoke(window, "ShowPathInExplorerTabAsync", file)!;
                Assert.Equal(3, Workspace(window).Tabs.Count);
                Assert.Equal(explorer.Id, Workspace(window).Active!.Id);
                var targets = (System.Collections.IDictionary)typeof(MainWindow)
                    .GetField("_browserHighlightTargets", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
                // Either still pending or already consumed by the surface; it must never point at another tab.
                Assert.True(!targets.Contains(explorer.Id) || Equals(targets[explorer.Id], file));
            }, new List<string>());
        }
        finally { TempDir.TryDelete(folder); }
    }

    [Fact]
    public async Task ShowPathInExplorerTabAsync_opens_a_new_tab_when_no_Explorer_tab_shows_the_folder()
    {
        var (folder, file) = TempImage();
        var other = Path.Combine(folder, "sub");
        Directory.CreateDirectory(other);
        try
        {
            await OnWindow(async window =>
            {
                Workspace(window).Reset(new TabState[] { new BrowserTabState(Guid.NewGuid(), other), new ImageTabState(Guid.NewGuid(), file) }, 1);
                await (Task)Invoke(window, "ShowPathInExplorerTabAsync", file)!;
                Assert.Equal(3, Workspace(window).Tabs.Count);
                Assert.Equal(Path.GetFullPath(folder), Assert.IsType<BrowserTabState>(Workspace(window).Active).Folder);
            }, new List<string>());
        }
        finally { TempDir.TryDelete(folder); }
    }

    // ---- alternative file manager entry -------------------------------------------------------------

    [Fact]
    public async Task Alternative_entry_reads_Set_up_without_a_program_and_names_the_program_once_set()
    {
        await OnWindow(window =>
        {
            var menu = (ContextMenu)Invoke(window, "BuildTitleActionContextMenu", "file.locate")!;
            var other = menu.ItemsSource!.Cast<object>().OfType<MenuItem>().ElementAt(2);
            var panel = Assert.IsType<StackPanel>(other.Header);
            var texts = panel.Children.OfType<TextBlock>().ToList();
            Assert.Equal(OtherHeader, texts[0].Text);
            Assert.Equal("Set up", texts[1].Text);
            Assert.True(texts[1].Opacity < 1, "the Set up hint is muted");

            Settings(window).AlternativeFileManagerPath = @"""C:\Tools\TotalCmd64.exe""";
            var headers = MenuHeaders(window);
            Assert.Equal(WindowsHeader, headers[1]);
            Assert.Equal("Show in TotalCmd64", headers[2]);
            Assert.Equal("-", headers[3]);
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task Unset_alternative_entry_opens_Settings_at_the_path_setting_with_its_card_expanded()
    {
        await OnWindow(async window =>
        {
            window.Show();
            for (var i = 0; i < 10; i++) { Dispatcher.UIThread.RunJobs(); await Task.Delay(10); }
            Click(window, OtherHeader);
            await AssertSettingsRevealsAlternativePath(window);
        });
    }

    [Fact]
    public async Task Unset_alternative_hotkey_action_also_opens_Settings_at_the_path_setting()
    {
        await OnWindow(async window =>
        {
            window.Show();
            for (var i = 0; i < 10; i++) { Dispatcher.UIThread.RunJobs(); await Task.Delay(10); }
            await Command(window, GlideCommand.ShowInAlternativeFileManager);
            await AssertSettingsRevealsAlternativePath(window);
        });
    }

    private static async Task AssertSettingsRevealsAlternativePath(MainWindow window)
    {
        SettingsWindow? settings = null;
        Control? highlighted = null;
        for (var i = 0; i < 200; i++)
        {
            Dispatcher.UIThread.RunJobs();
            settings = GetField(window, "_openSettingsWindow") as SettingsWindow;
            highlighted = settings is null ? null : SettingsField<Control?>(settings, "_revealHighlightTarget");
            if (highlighted is not null) break;
            await Task.Delay(20);
        }
        Assert.NotNull(settings);
        try
        {
            Assert.NotNull(highlighted);
            var card = SettingsField<Dictionary<string, Glide.App.Controls.SettingsCard>>(settings!, "_settingCard")["explorer.altFileManagerPath"];
            Assert.True(card.IsExpanded, "the card holding the path setting is expanded");
            var page = SettingsField<Dictionary<Glide.App.Controls.SettingsCard, string>>(settings!, "_pageOfCard")[card];
            Assert.Equal(page, SettingsField<string>(settings!, "_activeCategory"));
            var box = settings!.FindControl<TextBox>("AlternativeFileManagerPathBox")!;
            Assert.True(ReferenceEquals(highlighted, box) || highlighted!.GetLogicalDescendants().Contains(box), "the highlighted row holds the path box");
        }
        finally { settings!.Close(); Dispatcher.UIThread.RunJobs(); }
    }

    [Fact]
    public async Task Set_alternative_entry_launches_it_through_the_seam_and_becomes_the_ticked_target()
    {
        var (folder, file) = TempImage();
        var calls = new List<(string Exe, string Args)>();
        var previous = Glide.App.Explorer.ExplorerHost.LaunchAlternativeFileManager;
        Glide.App.Explorer.ExplorerHost.LaunchAlternativeFileManager = (exe, args) => { calls.Add((exe, args)); return true; };
        try
        {
            var launched = new List<string>();
            await OnWindow(async window =>
            {
                Settings(window).AlternativeFileManagerPath = @"C:\Tools\TotalCmd64.exe";
                Settings(window).AlternativeFileManagerArguments = "/O \"{path}\"";
                ShowImageTab(window, file);

                Click(window, "Show in TotalCmd64");
                Assert.Equal(new[] { (@"C:\Tools\TotalCmd64.exe", $"/O \"{file}\"") }, calls);
                Assert.Equal(ShowInExplorerTargets.Alternative, Settings(window).ShowInExplorerTarget);
                var headers = MenuHeaders(window);
                Assert.Equal("Show in TotalCmd64  ✓", headers[2]);
                Assert.Equal(1, headers.Count(h => h.EndsWith("✓", StringComparison.Ordinal)));

                // The button's left-click (the locate command) now opens the alternative too.
                await Command(window);
                Assert.Equal(2, calls.Count);
            }, launched);
            Assert.Empty(launched);
        }
        finally
        {
            Glide.App.Explorer.ExplorerHost.LaunchAlternativeFileManager = previous;
            TempDir.TryDelete(folder);
        }
    }

    private static T SettingsField<T>(SettingsWindow settings, string name) =>
        (T)typeof(SettingsWindow).GetField(name, Flags)!.GetValue(settings)!;

    // ---- helpers ------------------------------------------------------------------------------------

    private static (string Folder, string File) TempImage()
    {
        var folder = Path.Combine(Path.GetTempPath(), "Glide.LocateTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var file = Path.GetFullPath(Path.Combine(folder, "picture.png"));
        File.WriteAllBytes(file, new byte[] { 0x89, 0x50, 0x4E, 0x47 });
        File.WriteAllBytes(Path.Combine(folder, "other.png"), new byte[] { 0x89, 0x50, 0x4E, 0x47 });
        return (folder, file);
    }

    private static async Task OnWindow(Func<MainWindow, Task> body, List<string>? launched = null)
    {
        var root = Path.Combine(Path.GetTempPath(), "Glide.Settings.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        await AvaloniaHeadlessFixture.RunOnUIThreadAsync(async () =>
        {
            var previousStore = SettingsStore.SettingsDirectoryOverrideForTests.Value;
            SettingsStore.SettingsDirectoryOverrideForTests.Value = root;
            var previousLauncher = MainWindow.WindowsExplorerLocateLauncher;
            MainWindow.WindowsExplorerLocateLauncher = path => { launched?.Add(path); return true; };
            var window = new MainWindow();
            try
            {
                // Work on a pristine in-memory state; never the user's settings.
                SetField(window, "_settings", new GlideSettingsState { ExplorerLocateOpensIn = Glide.Core.Navigation.ExplorerLocateTargets.ExistingOrNewTab });
                await body(window);
            }
            finally
            {
                window.Close();
                MainWindow.WindowsExplorerLocateLauncher = previousLauncher;
                SettingsStore.SettingsDirectoryOverrideForTests.Value = previousStore;
            }
        });
        TempDir.TryDelete(root);
    }

    private static void WithIsolatedStore(Action<string> action)
    {
        var root = Path.Combine(Path.GetTempPath(), "Glide.Settings.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var previous = SettingsStore.SettingsDirectoryOverrideForTests.Value;
        SettingsStore.SettingsDirectoryOverrideForTests.Value = root;
        try { action(root); }
        finally
        {
            SettingsStore.SettingsDirectoryOverrideForTests.Value = previous;
            TempDir.TryDelete(root);
        }
    }

    private static void ShowImageTab(MainWindow window, string file)
    {
        Workspace(window).Reset(new TabState[] { new ImageTabState(Guid.NewGuid(), file) }, 0);
        SetField(window, "_currentPath", file);
    }

    private static Task Command(MainWindow window) => Command(window, GlideCommand.OpenContainingFolder);
    private static Task Command(MainWindow window, GlideCommand command) => (Task)Invoke(window, "ExecuteCommandAsync", command)!;

    private static GlideSettingsState Settings(MainWindow window) => (GlideSettingsState)GetField(window, "_settings")!;
    private static WorkspaceState Workspace(MainWindow window) => (WorkspaceState)GetField(window, "_workspace")!;

    private static List<string> MenuHeaders(MainWindow window)
    {
        var menu = (ContextMenu)Invoke(window, "BuildTitleActionContextMenu", "file.locate")!;
        return menu.ItemsSource!.Cast<object>().OfType<MenuItem>().Select(HeaderText).ToList();
    }

    /// <summary>A plain string header, or the first line of a header panel (text plus a muted hint such as "Set up").</summary>
    private static string HeaderText(MenuItem item) => item.Header switch
    {
        StackPanel panel => panel.Children.OfType<TextBlock>().First().Text ?? "",
        var other => other?.ToString() ?? ""
    };

    /// <summary>Raises Click on the freshly built menu's item (the same path a user click takes).</summary>
    private static void Click(MainWindow window, string headerStartsWith)
    {
        var menu = (ContextMenu)Invoke(window, "BuildTitleActionContextMenu", "file.locate")!;
        var item = menu.ItemsSource!.Cast<object>().OfType<MenuItem>().First(i => HeaderText(i).StartsWith(headerStartsWith, StringComparison.Ordinal));
        item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
    }

    /// <summary>
    /// The Explorer surface selects and scrolls to the path left in _browserHighlightTargets[tabId]; once it
    /// has done so the path is also in its selection set. Either form counts as "marked".
    /// </summary>
    private static bool IsMarkedForSelection(MainWindow window, Guid tabId, string file)
    {
        if (GetField(window, "_browserHighlightTargets") is Dictionary<Guid, string> targets &&
            targets.TryGetValue(tabId, out var target) && string.Equals(target, file, StringComparison.OrdinalIgnoreCase))
            return true;
        return GetField(window, "_browserSelection") is IEnumerable<string> selection &&
               selection.Contains(file, StringComparer.OrdinalIgnoreCase);
    }

    private const BindingFlags Flags = BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance;

    private static object? Invoke(MainWindow window, string method, params object?[] args)
    {
        var info = typeof(MainWindow).GetMethod(method, Flags);
        Assert.NotNull(info);
        return info!.Invoke(window, args);
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
