using Avalonia;
using Avalonia.Input;
using Glide.App.Settings;
using Glide.Core.Workspace;

namespace Glide.Explorer.Tests;

/// <summary>
/// "Open last session" for Explorer tabs: the browsed folder, selected item and scroll are saved
/// continuously (debounced, atomic) and come back after a crash or restart, even when things moved.
/// </summary>
[Collection(ExplorerHeadlessCollection.Name)]
[Trait("Area", "Explorer")]
public sealed class ExplorerSessionPersistenceTests
{
    private static readonly TimeSpan Quick = TimeSpan.FromMilliseconds(150);

    /// <summary>Turns continuous saving on with a short debounce for the duration of the body.</summary>
    private static async Task WithAutosave(Func<ExplorerHarness, Task> body, TimeSpan? delay = null, Action<string>? populate = null, bool startExplorer = true)
    {
        var previous = WorkspaceSessionStore.AutosaveDelay;
        WorkspaceSessionStore.AutosaveDelay = delay ?? Quick;
        try
        {
            await ExplorerHarness.Run(async h =>
            {
                h.Settings.StartupAction = "Open last session";
                await body(h);
            }, populate, startExplorer: startExplorer);
        }
        finally { WorkspaceSessionStore.AutosaveDelay = previous; }
    }

    private static string SessionFile(ExplorerHarness h) => Path.Combine(h.SettingsRoot, "glide.workspace-session.json");

    private static BrowserTabState LoadedBrowser(int index = -1)
    {
        var loaded = WorkspaceSessionStore.Load() ?? throw new InvalidOperationException("no session file");
        return Assert.IsType<BrowserTabState>(loaded.Tabs[index < 0 ? loaded.ActiveIndex : index]);
    }

    private static bool SavedMatches(Func<BrowserTabState, bool> predicate)
    {
        try { return WorkspaceSessionStore.Load() is { } loaded && loaded.Tabs.OfType<BrowserTabState>().Any(predicate); }
        catch { return false; }
    }

    private static async Task Saved(ExplorerHarness h, Func<BrowserTabState, bool> predicate, string what)
    {
        try { await h.WaitUntil(() => SavedMatches(predicate), what); }
        catch (Exception ex)
        {
            var text = File.Exists(SessionFile(h)) ? File.ReadAllText(SessionFile(h)) : "<no session file>";
            throw new InvalidOperationException(ex.Message + " | session file: " + text + " | writes=" + WorkspaceSessionStore.WriteCount);
        }
    }

    /// <summary>A fresh process: no per-tab view state and no history in the window, only what the session file holds.</summary>
    private static async Task RelaunchFromSessionFileAsync(ExplorerHarness h)
    {
        var loaded = WorkspaceSessionStore.Load() ?? throw new InvalidOperationException("no session file to restore");
        ((System.Collections.IDictionary)h.Get<object>("_explorerTabViews")).Clear();
        h.Window.TabHistories.Clear();
        h.Set("_explorerDisplayedTabId", Guid.Empty);
        h.Set("_explorerLoadedFolder", null);
        h.SelectionSet.Clear();
        h.Set("_browserCurrentIndex", -1);
        h.Workspace.Reset(loaded.Tabs.ToArray(), loaded.ActiveIndex);
        await (Task)h.Call("ActivateWorkspaceAsync")!;
        await h.SettleAsync();
    }

    private static void WriteSession(ExplorerHarness h, params TabState[] tabs) =>
        WorkspaceSessionStore.Save(tabs, Math.Max(0, tabs.Length - 1));

    // ---- saving ------------------------------------------------------------------------------------

    [Fact]
    public async Task Navigating_and_selecting_saves_folder_and_selected_file_without_closing()
    {
        await WithAutosave(async h =>
        {
            Assert.False(File.Exists(SessionFile(h)));
            h.DoubleClick("Alpha");
            await h.SettleAsync();
            var alpha = Path.Combine(h.Root, "Alpha");
            h.Click("inner.png");
            Assert.Equal(new[] { "inner.png" }, h.SelectedNames);

            await Saved(h, b => b.Folder == alpha && b.View?.SelectedPath == Path.Combine(alpha, "inner.png"), "session file has folder + file");
            var browser = LoadedBrowser();
            Assert.Equal(new[] { h.Root, alpha }, browser.Navigation!.History.ToArray());
        });
    }

    [Fact]
    public async Task Rapid_changes_coalesce_into_one_write_after_the_quiet_period()
    {
        await WithAutosave(async h =>
        {
            var before = WorkspaceSessionStore.WriteCount;
            foreach (var name in new[] { "a.png", "b.png", "c.png", "a.png", "b.png", "c.png" }) h.Click(name);
            // Still inside the quiet period: nothing has been written yet.
            Assert.Equal(before, WorkspaceSessionStore.WriteCount);
            Assert.False(File.Exists(SessionFile(h)));

            await h.WaitUntil(() => WorkspaceSessionStore.WriteCount > before, "debounced write");
            await Task.Delay(700); // several more debounce periods: no further writes without further changes
            h.Pump();
            Assert.Equal(before + 1, WorkspaceSessionStore.WriteCount);
            Assert.Equal(Path.Combine(h.Root, "c.png"), LoadedBrowser().View!.SelectedPath);
        }, delay: TimeSpan.FromMilliseconds(500));
    }

    [Fact]
    public async Task A_burst_that_never_pauses_is_still_written_within_the_maximum_wait()
    {
        await WithAutosave(async h =>
        {
            var before = WorkspaceSessionStore.WriteCount;
            var start = Environment.TickCount64;
            var names = new[] { "a.png", "b.png", "c.png" };
            for (var i = 0; WorkspaceSessionStore.WriteCount == before && Environment.TickCount64 - start < 9000; i++)
            {
                h.Click(names[i % 3]);
                await Task.Delay(40); // always shorter than the quiet period
            }
            Assert.True(WorkspaceSessionStore.WriteCount > before, "a continuous burst must not postpone the save forever");
        }, delay: TimeSpan.FromMilliseconds(700));
    }

    [Fact]
    public async Task Unchanged_state_does_not_rewrite_the_file_and_other_startup_modes_never_write()
    {
        await WithAutosave(async h =>
        {
            h.Click("a.png");
            await h.WaitUntil(() => File.Exists(SessionFile(h)), "first write");
            var writes = WorkspaceSessionStore.WriteCount;
            h.Call("NoteExplorerSessionChanged"); // a change event that did not change anything
            await Task.Delay(500);
            h.Pump();
            Assert.Equal(writes, WorkspaceSessionStore.WriteCount);

            h.Settings.StartupAction = "Welcome tab"; // the session is not used: Explorer activity must not touch disk
            h.Click("b.png");
            await Task.Delay(500);
            h.Pump();
            Assert.Equal(writes, WorkspaceSessionStore.WriteCount);
            Assert.Equal(Path.Combine(h.Root, "a.png"), LoadedBrowser().View!.SelectedPath);
        });
    }

    [Fact]
    public async Task Image_and_home_tabs_do_not_trigger_continuous_saves()
    {
        await WithAutosave(async h =>
        {
            var home = h.Workspace.Tabs.OfType<HomeTabState>().First();
            h.Workspace.Select(home.Id);
            var before = WorkspaceSessionStore.WriteCount;
            h.Call("NoteExplorerSessionChanged");
            await Task.Delay(500);
            h.Pump();
            Assert.Equal(before, WorkspaceSessionStore.WriteCount);
            Assert.False(File.Exists(SessionFile(h)));
        });
    }

    [Fact]
    public async Task Scroll_position_is_saved_and_changes_trigger_a_save()
    {
        await WithAutosave(async h =>
        {
            h.Surface.ScrollViewer.Offset = new Vector(0, 300);
            await h.SettleAsync();
            await Saved(h, b => b.View is { ScrollY: > 100 }, "scroll saved");
            Assert.InRange(LoadedBrowser().View!.ScrollY, 100, 400);
        }, populate: ExplorerHarness.ManyFilesTree);
    }

    [Fact]
    public async Task Several_explorer_tabs_are_all_saved_with_their_own_folder_and_selection()
    {
        await WithAutosave(async h =>
        {
            var first = ((BrowserTabState)h.Workspace.Active!).Id;
            h.Click("b.png");
            var deep = Path.Combine(h.Root, "Alpha", "Deep");
            await h.OpenExplorerAsync(deep);
            h.Click("deep.png");

            await Saved(h, b => b.Folder == deep && b.View?.SelectedPath == Path.Combine(deep, "deep.png"), "second tab in the session file");
            await Saved(h, b => b.Folder == h.Root && b.View?.SelectedPath == Path.Combine(h.Root, "b.png"), "first tab in the session file");
            var loaded = WorkspaceSessionStore.Load()!.Value;
            Assert.Equal(2, loaded.Tabs.OfType<BrowserTabState>().Count());
            Assert.Equal(deep, ((BrowserTabState)loaded.Tabs[loaded.ActiveIndex]).Folder);
            Assert.NotEqual(first, h.Workspace.Active!.Id);
        });
    }

    // ---- restoring ---------------------------------------------------------------------------------

    [Fact]
    public async Task Simulated_crash_after_the_debounce_restores_folder_and_selected_file()
    {
        await WithAutosave(async h =>
        {
            h.DoubleClick("Alpha");
            await h.SettleAsync();
            h.Click("inner.png");
            var alpha = Path.Combine(h.Root, "Alpha");
            await Saved(h, b => b.Folder == alpha && b.View?.SelectedPath == Path.Combine(alpha, "inner.png"), "saved");

            // The process is killed here: no Closing handler, no explicit save. Only the file survives.
            await RelaunchFromSessionFileAsync(h);

            Assert.Equal(alpha, h.CurrentFolder);
            Assert.Equal(new[] { "inner.png" }, h.SelectedNames);
            Assert.Equal(h.CurrentIndex, h.Names.ToList().IndexOf("inner.png"));
            Assert.True(h.Surface.BackButton.IsEnabled, "history was restored as well");
        });
    }

    [Fact]
    public async Task A_crash_inside_the_quiet_period_keeps_the_previous_save_and_loses_only_the_last_change()
    {
        await WithAutosave(async h =>
        {
            h.Click("a.png");
            await Saved(h, b => b.View?.SelectedPath == Path.Combine(h.Root, "a.png"), "first state saved");
            h.Click("c.png"); // killed before the debounce fires: the earlier, complete file is what is left
            var crashed = File.ReadAllText(SessionFile(h));
            Assert.Contains("a.png", crashed);
            await RelaunchFromSessionFileAsync(h);
            Assert.Equal(new[] { "a.png" }, h.SelectedNames);
        }, delay: TimeSpan.FromMilliseconds(900));
    }

    [Fact]
    public async Task Scroll_position_and_selection_come_back_for_a_long_folder()
    {
        await WithAutosave(async h =>
        {
            h.Surface.ScrollViewer.Offset = new Vector(0, 400);
            await h.SettleAsync();
            h.Click(h.Names.First(n => h.Tile(n) is { } t && t.TranslatePoint(new Point(0, 0), h.Surface.ScrollViewer)?.Y > 150));
            var selected = h.SelectedNames.Single();
            await Saved(h, b => b.View?.SelectedPath is { } p && Path.GetFileName(p) == selected && b.View.ScrollY > 100, "saved");
            var scrolled = h.Surface.ScrollViewer.Offset.Y;

            await RelaunchFromSessionFileAsync(h);
            await h.WaitUntil(() => Math.Abs(h.Surface.ScrollViewer.Offset.Y - scrolled) < 3, "scroll restored");
            Assert.Equal(new[] { selected }, h.SelectedNames);
        }, populate: ExplorerHarness.ManyFilesTree);
    }

    [Fact]
    public async Task A_saved_file_far_down_is_scrolled_into_view_even_without_a_saved_scroll()
    {
        await WithAutosave(async h =>
        {
            var file = Path.Combine(h.Root, "img280.png");
            WriteSession(h, new BrowserTabState(Guid.NewGuid(), h.Root) { View = new BrowserTabViewState(file, 0) });
            await RelaunchFromSessionFileAsync(h);
            Assert.Equal(new[] { "img280.png" }, h.SelectedNames);
            await h.WaitUntil(() => h.Surface.ScrollViewer.Offset.Y > 100, "selected file scrolled into view");
        }, populate: ExplorerHarness.ManyFilesTree, startExplorer: false);
    }

    [Fact]
    public async Task Missing_folder_opens_the_nearest_existing_parent_with_nothing_selected()
    {
        await WithAutosave(async h =>
        {
            var gone = Path.Combine(h.Root, "Alpha", "Deep", "Removed", "Deeper");
            WriteSession(h, new BrowserTabState(Guid.NewGuid(), gone)
            {
                View = new BrowserTabViewState(Path.Combine(gone, "x.png"), 250),
                Navigation = new BrowserNavigationState(new[] { gone }, 0, new string?[] { Path.Combine(gone, "x.png") })
            });
            await RelaunchFromSessionFileAsync(h);

            var deep = Path.Combine(h.Root, "Alpha", "Deep");
            Assert.Equal(deep, h.CurrentFolder);
            Assert.Equal(new[] { "deep.png" }, h.Names);
            Assert.Empty(h.SelectedNames);
            Assert.Equal(0, h.Surface.ScrollViewer.Offset.Y);
            var view = ((BrowserTabState)h.Workspace.Active!).View; // the stale view was dropped with the folder
            Assert.True(view is null || view is { SelectedPath: null, ScrollY: 0 }, $"stale view kept: {view}");
        }, startExplorer: false);
    }

    [Fact]
    public async Task Missing_everything_falls_back_to_the_usual_start_folder()
    {
        await WithAutosave(async h =>
        {
            var start = (string)h.Call("ResolveFreshExplorerFolder")!;
            Assert.Equal(start, (string)h.Call("ResolveExplorerRestoreFolder", "\0 not a path \0", false)!);
            Assert.Equal(h.Root, (string)h.Call("ResolveExplorerRestoreFolder", Path.Combine(h.Root, "nope", "nada"), true)!);
            Assert.Equal(Path.Combine(h.Root, "Alpha"), (string)h.Call("ResolveExplorerRestoreFolder", Path.Combine(h.Root, "Alpha"), true)!);
            await Task.CompletedTask;
        });
    }

    [Fact]
    public async Task Missing_file_opens_the_folder_with_nothing_selected_even_if_an_old_highlight_remains()
    {
        await WithAutosave(async h =>
        {
            var alpha = Path.Combine(h.Root, "Alpha");
            WriteSession(h, new BrowserTabState(Guid.NewGuid(), alpha)
            {
                View = new BrowserTabViewState(Path.Combine(alpha, "deleted-meanwhile.png"), 0),
                Navigation = new BrowserNavigationState(new[] { alpha }, 0, new string?[] { Path.Combine(alpha, "inner.png") })
            });
            await RelaunchFromSessionFileAsync(h);
            Assert.Equal(alpha, h.CurrentFolder);
            Assert.Equal(new[] { "Deep", "inner.png" }, h.Names);
            Assert.Empty(h.SelectedNames);
        }, startExplorer: false);
    }

    [Fact]
    public async Task Session_file_from_before_this_feature_still_restores_like_it_used_to()
    {
        await WithAutosave(async h =>
        {
            var alpha = Path.Combine(h.Root, "Alpha");
            string Json(string p) => p.Replace("\\", "\\\\");
            File.WriteAllText(SessionFile(h), $$"""
                { "Schema": 1, "ActiveIndex": 1, "Tabs": [
                    { "Kind": "Home", "Path": "", "Active": false },
                    { "Kind": "Browser", "Path": "{{Json(alpha)}}", "Active": true,
                      "BrowserNavigation": { "History": [ "{{Json(h.Root)}}", "{{Json(alpha)}}" ], "Index": 1,
                                             "Highlights": [ "{{Json(Path.Combine(h.Root, "Alpha"))}}", "{{Json(Path.Combine(alpha, "inner.png"))}}" ] } } ] }
                """);
            Assert.Null(LoadedBrowser().View);
            await RelaunchFromSessionFileAsync(h);
            Assert.Equal(alpha, h.CurrentFolder);
            Assert.Equal(new[] { "inner.png" }, h.SelectedNames); // the history highlight, as before
            Assert.True(h.Surface.BackButton.IsEnabled);
        }, startExplorer: false);
    }

    [Fact]
    public async Task Several_explorer_tabs_each_reopen_with_their_own_folder_and_selection()
    {
        await WithAutosave(async h =>
        {
            var alpha = Path.Combine(h.Root, "Alpha");
            var deep = Path.Combine(alpha, "Deep");
            WriteSession(h,
                new HomeTabState(Guid.NewGuid()),
                new BrowserTabState(Guid.NewGuid(), h.Root) { View = new BrowserTabViewState(Path.Combine(h.Root, "b.png"), 0) },
                new BrowserTabState(Guid.NewGuid(), Path.Combine(h.Root, "removed", "folder")) { View = new BrowserTabViewState(null, 0) },
                new BrowserTabState(Guid.NewGuid(), deep) { View = new BrowserTabViewState(Path.Combine(deep, "deep.png"), 0) });
            await RelaunchFromSessionFileAsync(h); // the last tab is active

            Assert.Equal(deep, h.CurrentFolder);
            Assert.Equal(new[] { "deep.png" }, h.SelectedNames);

            h.Workspace.Select(h.Workspace.Tabs[1].Id);
            await (Task)h.Call("ActivateWorkspaceAsync")!;
            await h.SettleAsync();
            Assert.Equal(h.Root, h.CurrentFolder);
            Assert.Equal(new[] { "b.png" }, h.SelectedNames);

            h.Workspace.Select(h.Workspace.Tabs[2].Id);
            await (Task)h.Call("ActivateWorkspaceAsync")!;
            await h.SettleAsync();
            Assert.Equal(h.Root, h.CurrentFolder); // the removed folder's nearest existing parent
            Assert.Empty(h.SelectedNames);

            h.Workspace.Select(h.Workspace.Tabs[3].Id);
            await (Task)h.Call("ActivateWorkspaceAsync")!;
            await h.SettleAsync();
            Assert.Equal(deep, h.CurrentFolder);
            Assert.Equal(new[] { "deep.png" }, h.SelectedNames);
        }, startExplorer: false);
    }

    [Fact]
    public async Task Show_in_Explorer_from_an_image_tab_is_remembered_by_the_autosave_and_by_a_clean_close()
    {
        await WithAutosave(async h =>
        {
            var alpha = Path.Combine(h.Root, "Alpha");
            var file = Path.Combine(alpha, "inner.png");
            h.Workspace.AddImage(file); // the image tab whose title-bar "Show in File Explorer" button is used
            await (Task)h.Call("ShowPathInExplorerTabAsync", file)!;
            await h.SettleAsync();
            Assert.Equal(alpha, h.CurrentFolder);
            Assert.Equal(new[] { "inner.png" }, h.SelectedNames);

            // 1) Crash after the debounce: only the autosave file exists.
            await Saved(h, b => b.Folder == alpha && b.View?.SelectedPath == file, "locate result autosaved");
            await RelaunchFromSessionFileAsync(h);
            Assert.Equal(alpha, h.CurrentFolder);
            Assert.Equal(new[] { "inner.png" }, h.SelectedNames);

            // 2) Clean close (what the Closing handler does) before the debounce could fire.
            h.Call("CancelPendingExplorerSessionSave");
            h.Call("CaptureActiveTabRuntimeState");
            WorkspaceSessionStore.Save(h.Workspace.Tabs, h.Workspace.ActiveIndex);
            await RelaunchFromSessionFileAsync(h);
            Assert.Equal(alpha, h.CurrentFolder);
            Assert.Equal(new[] { "inner.png" }, h.SelectedNames);
        }, startExplorer: false);
    }

    [Fact]
    public async Task Clean_close_still_writes_the_session_and_cancels_the_pending_debounced_write()
    {
        await WithAutosave(async h =>
        {
            h.Click("b.png");
            h.Call("CancelPendingExplorerSessionSave");
            h.Call("CaptureActiveTabRuntimeState");
            WorkspaceSessionStore.Save(h.Workspace.Tabs, h.Workspace.ActiveIndex);
            Assert.Equal(Path.Combine(h.Root, "b.png"), LoadedBrowser().View!.SelectedPath);
            await Task.CompletedTask;
        }, delay: TimeSpan.FromMilliseconds(500));
    }
}
