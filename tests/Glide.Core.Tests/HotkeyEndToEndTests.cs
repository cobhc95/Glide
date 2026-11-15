using Avalonia;
using System.Reflection;
using System.Text.RegularExpressions;
using Avalonia.Controls;
using Avalonia.Headless;
using Glide.App.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Glide.App;
using Glide.App.Services;
using Glide.App.Settings;
using Glide.Core.Commands;
using Glide.Core.Workspace;

namespace Glide.Core.Tests;

/// <summary>
/// End-to-end hotkey and registry wiring on a real headless MainWindow: a physical key press goes through the
/// window's own key handlers -> canonical shortcut text -> hotkey map -> registry action -> execution. Started by
/// the "Alt+Shift+S does nothing" report (the default was stored as "Alt+Shift+S" but the keyboard adapter
/// produces "Shift+Alt+S", and the router compared the strings literally).
/// </summary>
// Source-text contract kept: this asserts on production source (UI code-behind, native/installer files or a repo-wide scan) that cannot be run headlessly; behavioural coverage lives in the other test files.
[Collection(AvaloniaHeadlessCollection.Name)]
public sealed class HotkeyEndToEndTests
{
    public HotkeyEndToEndTests(AvaloniaHeadlessFixture fixture) { _ = fixture; }

    // ---- the reported bug -----------------------------------------------------------------------------------

    [Fact]
    public async Task Ctrl_Shift_B_collapses_and_expands_the_visible_status_bar()
    {
        // 5.0.0 audit O7: the default moved from Alt+Shift+S (Windows input-language chord) to Ctrl+Shift+B.
        await OnWindow(async (window, routed) =>
        {
            Assert.False(Settings(window).StatusAutoHide);
            Assert.True(Settings(window).ShowStatusSurface);
            Assert.False(Collapsed(window));

            await Press(window, Key.B, RawInputModifiers.Control | RawInputModifiers.Shift);
            Assert.Equal("status.toggle", Assert.Single(routed).Id);
            Assert.True(Collapsed(window), "Ctrl+Shift+B collapses the bar");

            await Press(window, Key.B, RawInputModifiers.Control | RawInputModifiers.Shift);
            Assert.False(Collapsed(window), "Ctrl+Shift+B expands it again");
        });
    }

    [Fact]
    public async Task The_old_default_Alt_Shift_S_no_longer_fires_until_the_user_binds_it()
    {
        await OnWindow(async (window, routed) =>
        {
            await Press(window, Key.S, RawInputModifiers.Alt | RawInputModifiers.Shift);
            Assert.DoesNotContain(routed, a => a.Id == "status.toggle");
            Assert.False(Collapsed(window));

            // A user binding (saved override) of the old key keeps working on the real key.
            Settings(window).Hotkeys["status.toggle"] = new List<string> { "Alt+Shift+S" };
            await Press(window, Key.S, RawInputModifiers.Alt | RawInputModifiers.Shift);
            Assert.Contains(routed, a => a.Id == "status.toggle");
            Assert.True(Collapsed(window));
            // ... and the new default stops firing once the user rebound the action.
            routed.Clear();
            await Press(window, Key.B, RawInputModifiers.Control | RawInputModifiers.Shift);
            Assert.DoesNotContain(routed, a => a.Id == "status.toggle");
        });
    }

    [Fact]
    public async Task The_status_bar_tooltips_show_the_live_binding_not_a_hard_coded_key()
    {
        await OnWindow((window, _) =>
        {
            string Tip(string field) => ToolTip.GetTip((Control)GetField(window, field)!)?.ToString() ?? string.Empty;
            Invoke(window, "UpdateCollapseButtonGlyphs");
            Assert.Contains("Ctrl+Shift+B", Tip("StatusCollapseButton"));
            Settings(window).Hotkeys["status.toggle"] = new List<string> { "F9" };
            Invoke(window, "UpdateCollapseButtonGlyphs");
            Assert.Contains("(F9)", Tip("StatusCollapseButton"));
            Assert.DoesNotContain("Ctrl+Shift+B", Tip("StatusCollapseButton"));
            Settings(window).Hotkeys["status.toggle"] = new List<string>();
            Invoke(window, "RefreshDynamicTooltips");
            Assert.Contains("Unassigned", Tip("StatusRestoreButton"));
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task A_settings_file_that_stored_Alt_Shift_S_still_fires_on_the_real_key()
    {
        await OnWindow(async (window, routed) =>
        {
            Settings(window).Hotkeys["status.toggle"] = new List<string> { "Alt+Shift+S" }; // a saved override in the older file spelling
            await Press(window, Key.S, RawInputModifiers.Shift | RawInputModifiers.Alt);
            Assert.Contains(routed, a => a.Id == "status.toggle");
            Assert.True(Collapsed(window));
        });
    }

    [Fact]
    public async Task Status_toggle_shows_a_closed_status_bar_again()
    {
        await OnWindow(async (window, _) =>
        {
            Settings(window).ShowStatusSurface = false;
            await Press(window, Key.B, RawInputModifiers.Control | RawInputModifiers.Shift);
            Assert.True(Settings(window).ShowStatusSurface);
            Assert.False(Collapsed(window));
        });
    }

    [Fact]
    public async Task Ctrl_E_in_an_image_tab_opens_the_Explorer_tab_with_the_image_and_Windows_choice_uses_the_launcher()
    {
        var folder = Path.Combine(Path.GetTempPath(), "Glide.CtrlE.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var file = Path.GetFullPath(Path.Combine(folder, "picture.png"));
        File.WriteAllBytes(file, new byte[] { 0x89, 0x50, 0x4E, 0x47 });
        File.WriteAllBytes(Path.Combine(folder, "other.png"), new byte[] { 0x89, 0x50, 0x4E, 0x47 });
        try
        {
            await OnWindow(async (window, routed) =>
            {
                var launched = new List<string>();
                MainWindow.WindowsExplorerLocateLauncher = path => { launched.Add(path); return true; };
                Assert.Equal(new[] { "Ctrl+E" }, Settings(window).Hotkeys["folder.exploreParent"]);
                // Tooltips and menus read the live binding (the title-bar button is an alias of this action).
                Assert.Equal("Ctrl+E", (string)Invoke(window, "ShortcutForAction", "file.showInExplorer")!);
                Assert.Equal("Ctrl+E", (string)Invoke(window, "ShortcutForAction", "folder.exploreParent")!);
                Settings(window).Hotkeys["folder.exploreParent"] = new List<string> { "F9" };
                Assert.Equal("F9", (string)Invoke(window, "ShortcutForAction", "file.showInExplorer")!);
                Settings(window).Hotkeys["folder.exploreParent"] = new List<string> { "Ctrl+E" };

                var workspace = (WorkspaceState)GetField(window, "_workspace")!;
                workspace.Reset(new TabState[] { new ImageTabState(Guid.NewGuid(), file) }, 0);
                SetField(window, "_currentPath", file);
                var imageTabId = workspace.Active!.Id;

                // 1. Glide's own Explorer tab (default): a real Ctrl+E press opens it on the image's folder.
                await Press(window, Key.E, RawInputModifiers.Control);
                for (var i = 0; i < 100 && workspace.Active is not BrowserTabState; i++) { Dispatcher.UIThread.RunJobs(); await Task.Delay(20); }
                Assert.Contains(routed, a => a.Id == "folder.exploreParent");
                var active = Assert.IsType<BrowserTabState>(workspace.Active);
                Assert.Equal(Path.GetFullPath(folder), active.Folder);
                Assert.NotEqual(imageTabId, active.Id);
                Assert.Empty(launched);

                // 2. Windows File Explorer choice: the launcher seam receives the image; no new tab.
                workspace.Reset(new TabState[] { new ImageTabState(Guid.NewGuid(), file) }, 0);
                SetField(window, "_currentPath", file);
                Settings(window).ShowInExplorerTarget = ShowInExplorerTargets.WindowsExplorer;
                var tabs = workspace.Tabs.Count;
                await Press(window, Key.E, RawInputModifiers.Control);
                for (var i = 0; i < 100 && launched.Count == 0; i++) { Dispatcher.UIThread.RunJobs(); await Task.Delay(20); }
                Assert.Equal(new[] { file }, launched);
                Assert.Equal(tabs, workspace.Tabs.Count);
            });
        }
        finally { try { Directory.Delete(folder, true); } catch { } }
    }

    // ---- every default hotkey reaches its action through the real key handler -----------------------------

    [Fact]
    public async Task Every_default_hotkey_reaches_its_registry_action_through_the_window_key_handlers()
    {
        var failures = new List<string>();
        var tested = 0;
        await OnWindow(async (window, routed) =>
        {
            Intercept(window);
            foreach (var row in HotkeyCatalog.All)
            {
                var expected = AppActionRegistry.FindByHotkeySlot(row.Id)!;
                foreach (var shortcut in row.DefaultShortcuts)
                {
                    if (ToKey(shortcut) is not { } press) { failures.Add($"{row.Id}: cannot simulate '{shortcut}'"); continue; }
                    routed.Clear();
                    ResetWorkspace(window);
                    await Press(window, press.Key, press.Modifiers);
                    tested++;
                    if (routed.Count == 0) failures.Add($"{row.Id}: '{shortcut}' did not reach any action");
                    else if (routed[0].Id != expected.Id) failures.Add($"{row.Id}: '{shortcut}' reached {routed[0].Id}, expected {expected.Id}");
                }
            }
        });
        var expectedCount = HotkeyCatalog.All.Sum(row => row.DefaultShortcuts.Length);
        Assert.Equal(expectedCount, tested);
        Assert.True(tested >= 75, $"only {tested} default shortcuts were pressed");
        Assert.True(failures.Count == 0, string.Join("\n", failures));
    }

    [Fact]
    public async Task Enter_and_Space_still_run_their_hotkeys_after_a_status_bar_button_was_clicked()
    {
        // A clicked Button keeps keyboard focus and used to swallow Enter / Space (Space re-clicked the button).
        await OnWindow(async (window, routed) =>
        {
            Intercept(window);
            var fit = (Button)GetField(window, "StatusFitImageButton")!;
            fit.Focus();
            Pump();
            await Press(window, Key.Enter, RawInputModifiers.None);
            Assert.Equal("view.fullscreen", Assert.Single(routed).Id);
            routed.Clear();
            fit.Focus();
            Pump();
            await Press(window, Key.Space, RawInputModifiers.None);
            Assert.Equal("nav.next", Assert.Single(routed).Id);
        });
    }

    [Fact]
    public async Task Ctrl_Tab_follows_the_hotkey_map_instead_of_being_hardwired()
    {
        await OnWindow(async (window, routed) =>
        {
            Intercept(window);
            Settings(window).Hotkeys["tab.next"] = new List<string> { "Ctrl+PageDown" };
            Settings(window).Hotkeys["view.fit"] = new List<string> { "Ctrl+Tab" };
            await Press(window, Key.Tab, RawInputModifiers.Control);
            Assert.Equal("view.fit", Assert.Single(routed).Id);
        });
    }

    [Fact]
    public async Task Image_hotkeys_never_act_on_a_hidden_image_from_an_Explorer_or_Home_tab()
    {
        // Before the registry fix, with keyboard focus outside the Explorer list (tab strip, title bar...), Delete in
        // an Explorer tab recycled the image last shown in ANOTHER tab, F2 renamed it, R rotated the hidden viewport.
        // Viewer-only actions (AppActionContexts.Viewer) now run only while the image viewer is showing.
        var root = Path.Combine(Path.GetTempPath(), "Glide.HotkeyExplorer", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "Folder"));
        var hidden = Path.Combine(root, "hidden.png");
        File.WriteAllBytes(hidden, new byte[] { 0x89, 0x50, 0x4E, 0x47 });
        try
        {
            await OnWindow(async (window, routed) =>
            {
                SetField(window, "_currentPath", hidden); // the image of another (inactive) tab
                await (Task)Invoke(window, "OpenExplorerTabAsync", root)!;
                await Settle();
                var executed = new List<string>();
                window.ActionInterceptorForTests = (action, _) => { executed.Add(action.Id); return true; };
                foreach (var (key, mods) in new (Key, RawInputModifiers)[]
                {
                    (Key.Delete, RawInputModifiers.None), (Key.F2, RawInputModifiers.None), (Key.R, RawInputModifiers.None),
                    (Key.C, RawInputModifiers.Control), (Key.P, RawInputModifiers.Control),
                })
                    await Press(window, key, mods);
                Assert.Empty(executed.Where(id => AppActionRegistry.Find(id)!.Contexts == Glide.Core.Commands.AppActionContexts.Viewer));
                Assert.True(File.Exists(hidden));
                // Global actions still work from the Explorer tab.
                await Press(window, Key.B, RawInputModifiers.Control | RawInputModifiers.Shift);
                Assert.Contains("status.toggle", executed);
            });
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }

    // ---- generated setting actions ----------------------------------------------------------------------------

    [Fact]
    public async Task A_hotkey_bound_to_a_setting_toggle_flips_it_applies_it_and_saves_it()
    {
        await OnWindow(async (window, routed) =>
        {
            SetField(window, "_deferredStartupSettingsAdopted", true);
            Settings(window).Hotkeys["setting.status.autoHide"] = new List<string> { "Ctrl+Alt+H" };
            await Press(window, Key.H, RawInputModifiers.Control | RawInputModifiers.Alt);
            Assert.Equal("setting.status.autoHide", Assert.Single(routed).Id);
            Assert.True(Settings(window).StatusAutoHide, "the setting flipped on the live state");
            Assert.True(SettingsStore.Load().StatusAutoHide, "and was persisted like Settings > Apply");
            await Press(window, Key.H, RawInputModifiers.Control | RawInputModifiers.Alt);
            Assert.False(Settings(window).StatusAutoHide);
        });
    }

    [Fact]
    public async Task A_cycle_action_moves_a_choice_setting_to_its_next_option()
    {
        await OnWindow(async (window, _) =>
        {
            Settings(window).StatusBarSize = "Small";
            await Execute(window, "setting.status.size");
            Assert.Equal("Medium", Settings(window).StatusBarSize);
            Settings(window).StatusBarAlignment = "Right";
            await Execute(window, "setting.status.alignment");
            Assert.Equal("Center", Settings(window).StatusBarAlignment);
        });
    }

    [Fact]
    public async Task A_gesture_bound_to_a_setting_toggle_flips_it()
    {
        await OnWindow(async (window, _) =>
        {
            var before = Settings(window).StatusReserveFitSpace;
            await (Task)Invoke(window, "ExecuteGestureActionAsync", "setting.status.reserveFitSpace", 0, null)!;
            Assert.Equal(!before, Settings(window).StatusReserveFitSpace);
        });
    }

    [Fact]
    public void Every_bindable_setting_has_a_typed_accessor_of_the_right_kind()
    {
        foreach (var action in AppActionRegistry.All.Where(a => a.IsSettingAction))
        {
            var accessor = SettingAccessors.Find(action.SettingId!);
            Assert.True(accessor is not null, $"{action.SettingId} has no accessor");
            if (action.Kind == AppActionKind.SettingToggle) Assert.NotNull(accessor!.GetBool);
            else Assert.NotNull(accessor!.GetText);
        }
    }

    [Fact]
    public void Every_choice_setting_option_list_round_trips_through_its_accessor()
    {
        foreach (var (id, options) in Glide.Core.Settings.SettingActionCatalog.Choices)
        {
            if (AppActionRegistry.ForSetting(id) is null) continue;
            var accessor = SettingAccessors.Find(id)!;
            var defaults = new GlideSettingsState();
            Assert.Contains(accessor.GetText!(defaults), options, StringComparer.OrdinalIgnoreCase);
        }
    }

    // ---- 5.0.0 audit items O2 / O3 / O4 / O5 ----------------------------------------------------------------

    private static Control ImageViewHost(MainWindow window) => (Control)GetField(window, "ImageView")!;
    private static ImageViewport ViewportOf(MainWindow window) => (ImageViewport)GetField(window, "Viewport")!;

    private static async Task ShowViewerWithBitmap(MainWindow window)
    {
        ImageViewHost(window).IsVisible = true;
        ViewportOf(window).Bitmap = new Avalonia.Media.Imaging.WriteableBitmap(new Avalonia.PixelSize(400, 300),
            new Avalonia.Vector(96, 96), Avalonia.Platform.PixelFormat.Bgra8888, Avalonia.Platform.AlphaFormat.Premul);
        await Settle();
        window.UpdateLayout();
        RenderTick(window);
        ViewportOf(window).ActualSize();
        await Settle();
    }

    [Fact]
    public async Task Zoom_keys_use_the_Viewport_zoom_step_setting_live_and_the_default_keeps_15_percent()
    {
        await OnWindow(async (window, _) =>
        {
            await ShowViewerWithBitmap(window);
            Assert.Equal(15, Settings(window).ViewportZoomStepPercent);
            await Press(window, Key.OemPlus, RawInputModifiers.None);          // "=" : zoom in
            Assert.Equal(115, ViewportOf(window).ZoomPercent);
            await Press(window, Key.OemMinus, RawInputModifiers.None);         // "-" : zoom out (reciprocal)
            Assert.Equal(100, ViewportOf(window).ZoomPercent);

            // Changing the setting applies live (same pipeline as Settings > Apply) with no restart.
            var state = Settings(window).CloneState();
            state.ViewportZoomStepPercent = 40;
            Invoke(window, "ApplySettingsFromDialog", state, true);
            Assert.Equal(40, ViewportOf(window).ZoomStepPercent);
            ViewportOf(window).ActualSize();
            await Press(window, Key.OemPlus, RawInputModifiers.None);
            Assert.Equal(140, ViewportOf(window).ZoomPercent);
            await Press(window, Key.OemMinus, RawInputModifiers.None);
            Assert.Equal(100, ViewportOf(window).ZoomPercent);

            // The registry action (status bar buttons and menus use the same step).
            await Execute(window, "view.zoomIn");
            Assert.Equal(140, ViewportOf(window).ZoomPercent);
        });
    }

    [Fact]
    public async Task Wheel_zoom_step_is_the_default_ratio_of_the_zoom_step_and_clamps_are_unaffected()
    {
        await OnWindow(async (window, _) =>
        {
            await ShowViewerWithBitmap(window);
            var viewport = ViewportOf(window);
            Assert.Equal(1.12, viewport.WheelZoomFactor);   // the historical 12% at the default
            Assert.Equal(1.15, viewport.KeyZoomFactor);     // the historical 15% at the default
            var state = Settings(window).CloneState();
            state.ViewportZoomStepPercent = 30;
            Invoke(window, "ApplySettingsFromDialog", state, true);
            Assert.Equal(1.30, viewport.KeyZoomFactor, 10);
            Assert.Equal(1.24, viewport.WheelZoomFactor, 10);  // 30 * 12 / 15 = 24%

            // Min / max zoom stay at 5% and 3200% whatever the step.
            state.ViewportZoomStepPercent = 100;
            Invoke(window, "ApplySettingsFromDialog", state, true);
            for (var i = 0; i < 20; i++) viewport.ZoomStep(true);
            Assert.Equal(3200, viewport.ZoomPercent);
            for (var i = 0; i < 40; i++) viewport.ZoomStep(false);
            Assert.Equal(5, viewport.ZoomPercent);
        });
    }

    [Fact]
    public async Task A_real_ctrl_wheel_zooms_by_the_wheel_ratio_of_the_step()
    {
        await OnWindow(async (window, _) =>
        {
            await ShowViewerWithBitmap(window);
            var viewport = ViewportOf(window);
            RaiseWheel(window, viewport, 1, KeyModifiers.Control);
            await Settle();
            Assert.Equal(112, viewport.ZoomPercent);            // 12% at the default step
            var state = Settings(window).CloneState();
            state.ViewportZoomStepPercent = 30;
            Invoke(window, "ApplySettingsFromDialog", state, true);
            viewport.ActualSize();
            RaiseWheel(window, viewport, 1, KeyModifiers.Control);
            await Settle();
            Assert.Equal(124, viewport.ZoomPercent);            // 30 * 12 / 15 = 24%
        });
    }

    // The headless platform has no rendered scene to hit-test, so pointer events are raised on the target element
    // directly: they then travel the real routing (the window's tunnel handlers, the viewport's own handlers).
    private static void RaiseWheel(MainWindow window, Avalonia.Visual target, double deltaY, KeyModifiers modifiers)
    {
        var props = new PointerPointProperties(RawInputModifiers.None, PointerUpdateKind.Other);
        var args = new PointerWheelEventArgs(target, new Avalonia.Input.Pointer(Avalonia.Input.Pointer.GetNextFreeId(), PointerType.Mouse, true), window,
            new Avalonia.Point(100, 100), 0, props, modifiers, new Avalonia.Vector(0, deltaY)) { RoutedEvent = InputElement.PointerWheelChangedEvent };
        ((Avalonia.Interactivity.Interactive)target).RaiseEvent(args);
    }

    private static PointerPressedEventArgs RaiseSideButton(MainWindow window, Avalonia.Visual target, bool back, KeyModifiers modifiers = KeyModifiers.None)
    {
        var props = new PointerPointProperties(back ? RawInputModifiers.XButton1MouseButton : RawInputModifiers.XButton2MouseButton,
            back ? PointerUpdateKind.XButton1Pressed : PointerUpdateKind.XButton2Pressed);
        var args = new PointerPressedEventArgs(target, new Avalonia.Input.Pointer(Avalonia.Input.Pointer.GetNextFreeId(), PointerType.Mouse, true), window,
            new Avalonia.Point(100, 100), 0, props, modifiers) { RoutedEvent = InputElement.PointerPressedEvent };
        ((Avalonia.Interactivity.Interactive)target).RaiseEvent(args);
        return args;
    }

    [Theory]
    [InlineData("Only the current image", "view.fit", "Fit image")]
    [InlineData("This session", "view.fit", "Fit image")]
    [InlineData("This session and future sessions", "view.fit", "Fit image")]
    [InlineData("Only the current image", "view.fitWidth", "Fit width")]
    [InlineData("This session", "view.fitWidth", "Fit width")]
    [InlineData("This session and future sessions", "view.fitWidth", "Fit width")]
    [InlineData("Only the current image", "view.fitHeight", "Fit height")]
    [InlineData("This session", "view.fitHeight", "Fit height")]
    [InlineData("This session and future sessions", "view.fitHeight", "Fit height")]
    [InlineData("Only the current image", "view.fitWindow", "Fit to window")]
    [InlineData("This session", "view.fitWindow", "Fit to window")]
    [InlineData("This session and future sessions", "view.fitWindow", "Fit to window")]
    [InlineData("Only the current image", "view.actual", "100%")]
    [InlineData("This session", "view.actual", "100%")]
    [InlineData("This session and future sessions", "view.actual", "100%")]
    public async Task Fit_hotkeys_follow_the_Fit_control_scope_like_the_status_bar_buttons(string scope, string actionId, string viewMode)
    {
        await OnWindow(async (window, routed) =>
        {
            SetField(window, "_deferredStartupSettingsAdopted", true);
            ImageViewHost(window).IsVisible = true;
            var initial = viewMode == "Fit image" ? "Fit width" : "Fit image";
            Settings(window).StatusFitScope = scope;
            Settings(window).DefaultViewMode = initial;
            SetField(window, "_sessionDefaultViewMode", null);
            SettingsStore.Save(Settings(window));
            var row = AppActionRegistry.Find(actionId)!.HotkeySlots[0];
            var press = ToKey(row.DefaultShortcuts[0])!.Value;
            await Press(window, press.Key, press.Modifiers);
            Assert.Equal(actionId, routed[0].Id);

            var session = (string?)GetField(window, "_sessionDefaultViewMode");
            if (scope == "Only the current image")
            {
                Assert.True(string.IsNullOrEmpty(session), "the session default must not change");
                Assert.Equal(initial, Settings(window).DefaultViewMode);
                Assert.Equal(initial, SettingsStore.Load().DefaultViewMode);
            }
            else
            {
                Assert.Equal(viewMode, session);
                Assert.Equal(scope == "This session" ? initial : viewMode, Settings(window).DefaultViewMode);
                Assert.Equal(scope == "This session" ? initial : viewMode, SettingsStore.Load().DefaultViewMode);
            }
        });
    }

    [Fact]
    public async Task The_Fit_hotkey_and_the_status_bar_button_end_in_the_same_state()
    {
        await OnWindow(async (window, _) =>
        {
            ImageViewHost(window).IsVisible = true;
            Settings(window).StatusFitScope = "This session";
            SetField(window, "_sessionDefaultViewMode", null);
            Invoke(window, "FitWidthClicked", null, new Avalonia.Interactivity.RoutedEventArgs());
            var viaButton = (string?)GetField(window, "_sessionDefaultViewMode");
            SetField(window, "_sessionDefaultViewMode", null);
            await Press(window, Key.D2, RawInputModifiers.None);
            Assert.Equal(viaButton, (string?)GetField(window, "_sessionDefaultViewMode"));
            Assert.Equal("Fit width", viaButton);
        });
    }

    [Fact]
    public async Task Exit_Glide_is_a_labelled_registry_action_and_runs_the_exit_path()
    {
        var action = AppActionRegistry.Find("app.exit")!;
        Assert.Equal("Exit Glide", action.Label);
        Assert.Equal(GlideCommand.ExitApplication, action.Command);
        Assert.True(action.Destructive, "never a gesture");
        Assert.Contains(HotkeyCatalog.All, row => row.Id == "app.exit" && row.DefaultShortcuts.Length == 0);
        // "Close Glide window" keeps closing only the window.
        Assert.Equal("Close Glide window", AppActionRegistry.Find("window.close")!.Label);
        await OnWindow(async (window, _) =>
        {
            var exits = 0;
            window.ApplicationExitForTests = () => exits++;
            await Execute(window, "app.exit");
            Assert.Equal(1, exits);
            // A hotkey bound to it does the same through the real key handler.
            Settings(window).Hotkeys["app.exit"] = new List<string> { "Ctrl+Alt+Q" };
            await Press(window, Key.Q, RawInputModifiers.Control | RawInputModifiers.Alt);
            Assert.Equal(2, exits);
            // The viewer context menu's item is that action.
            var menu = (ContextMenu)Invoke(window, "BuildViewerContextMenu")!;
            var item = Assert.Single(Leaves(menu.ItemsSource), i => MenuActions.GetActionId(i) == "app.exit");
            Assert.StartsWith("Exit Glide", item.Header?.ToString());
        });
    }

    [Fact]
    public async Task Exit_Glide_asks_like_closing_the_window_when_several_image_tabs_are_open()
    {
        await OnWindow(async (window, _) =>
        {
            var exits = 0;
            window.ApplicationExitForTests = () => exits++;
            var workspace = (WorkspaceState)GetField(window, "_workspace")!;
            workspace.Reset(new TabState[] { new ImageTabState(Guid.NewGuid(), "a.png"), new ImageTabState(Guid.NewGuid(), "b.png") }, 0);
            Settings(window).ConfirmCloseMultipleTabs = true;
            Settings(window).RememberedMultiTabCloseChoice = "Cancel";
            await window.ExitGlideAsync();
            Assert.Equal(0, exits);                     // cancelled: nothing closes
            Settings(window).RememberedMultiTabCloseChoice = "Close all";
            await window.ExitGlideAsync();
            Assert.Equal(1, exits);
            Settings(window).RememberedMultiTabCloseChoice = "Cancel";
            Settings(window).ConfirmCloseMultipleTabs = false;   // confirmation switched off: no question at all
            await window.ExitGlideAsync();
            Assert.Equal(2, exits);
        });
    }

    [Fact]
    public async Task Exit_Glide_without_the_seam_closes_the_window_through_ForceExit_and_blocks_speed_boost_standby()
    {
        await OnWindow(async (window, _) =>
        {
            Settings(window).SpeedBoostEnabled = true;
            await window.ExitGlideAsync();
            Assert.True((bool)GetField(window, "_forceProcessExit")!, "ForceExit marks the window so the Closing handler never enters Speed Boost standby");
            Assert.False(window.IsVisible);
        });
        // The other windows of the process are marked the same way before the application shuts down.
        var source = File.ReadAllText(FindSource("MainWindow.Sessions.cs"));
        var body = source[source.IndexOf("internal async Task ExitGlideAsync", StringComparison.Ordinal)..];
        Assert.Contains("other.PrepareForApplicationExit()", body, StringComparison.Ordinal);
        Assert.Contains("ForceExit();", body, StringComparison.Ordinal);
    }

    private static string FindSource(string fileName)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "src", "Glide.App", fileName);
            if (File.Exists(candidate)) return candidate;
            directory = directory.Parent;
        }
        throw new FileNotFoundException(fileName);
    }

    [Fact]
    public async Task Mouse_side_buttons_go_Back_and_Forward_by_default_and_a_bound_action_overrides_in_the_viewer_and_tab_strip()
    {
        await OnWindow(async (window, _) =>
        {
            ImageViewHost(window).IsVisible = true;
            await Settle();
            var viewport = ViewportOf(window);
            var strip = (Control)GetField(window, "TabStripScroll")!;

            async Task<bool> Click(Avalonia.Visual target, bool back)
            {
                var handled = RaiseSideButton(window, target, back).Handled;
                await Settle();
                return handled;
            }

            // Default (5.0.0): the press is claimed and goes Back / Forward. A fresh tab has no history, so nothing changes.
            Assert.True(await Click(viewport, true));
            Assert.True(await Click(viewport, false));
            Assert.True(await Click(strip, true));
            Assert.False(Collapsed(window));

            // Bound: toggleStatusBar is observable without images.
            Settings(window).Gestures["windowed.backButtonImage"] = GestureCatalog.ToggleStatusBar;
            Assert.True(await Click(viewport, true));
            Assert.True(Collapsed(window), "XButton1 over the windowed image ran its gesture");
            Assert.True(await Click(viewport, false));
            Assert.True(Collapsed(window), "XButton2 is a different slot and still goes Forward");
            Settings(window).Gestures["windowed.forwardButtonImage"] = GestureCatalog.ToggleStatusBar;
            Assert.True(await Click(viewport, false));
            Assert.False(Collapsed(window));

            Settings(window).Gestures["tabs.forwardButton"] = GestureCatalog.ToggleStatusBar;
            Assert.True(await Click(strip, false));
            Assert.True(Collapsed(window), "XButton2 over the tab strip ran the Tabs-area gesture");
            Assert.True(await Click(strip, true));
            Assert.True(Collapsed(window), "tab back button still goes Back");

            // "Do nothing" is a real choice: the press is swallowed and nothing runs.
            Settings(window).Gestures["windowed.backButtonImage"] = GestureCatalog.Nothing;
            Assert.True(await Click(viewport, true));
            Assert.True(Collapsed(window));

            // A custom gesture with a held key overrides the row for that key only.
            Settings(window).CustomGestures.Add(new CustomGesture { Area = GestureAreas.Windowed, Input = GestureInputs.BackButton, Modifier = GestureModifiers.Ctrl, Action = GestureCatalog.ToggleStatusBar });
            Settings(window).Gestures["windowed.backButtonImage"] = GestureCatalog.Default;
            Assert.True(RaiseSideButton(window, viewport, true).Handled);                  // Default: Back
            await Settle();
            Assert.True(Collapsed(window));
            Assert.True(RaiseSideButton(window, viewport, true, KeyModifiers.Control).Handled);
            await Settle();
            Assert.False(Collapsed(window), "Ctrl + back button ran the custom gesture");
        });
    }

    [Fact]
    public async Task Side_buttons_map_to_the_slot_of_the_area_under_the_pointer_and_go_Back_in_the_explorer_by_default()
    {
        var root = Path.Combine(Path.GetTempPath(), "Glide.SideButtons", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await OnWindow(async (window, _) =>
            {
                var viewport = ViewportOf(window);
                ImageViewHost(window).IsVisible = true;
                Guid? tab;
                Assert.Equal("windowed.backButtonImage", window.SideButtonSlotAt(viewport, true, out tab)!.Id);
                Assert.Equal("windowed.forwardButtonImage", window.SideButtonSlotAt(viewport, false, out tab)!.Id);
                window.WindowState = WindowState.FullScreen;
                Assert.Equal("fullscreen.backButtonImage", window.SideButtonSlotAt(viewport, true, out tab)!.Id);
                window.WindowState = WindowState.Normal;
                Assert.Equal("tabs.backButton", window.SideButtonSlotAt((Control)GetField(window, "TabStripScroll")!, true, out tab)!.Id);
                Assert.Null(window.SideButtonSlotAt(window, true, out tab));      // elsewhere: no slot, the press falls through

                await (Task)Invoke(window, "OpenExplorerTabAsync", root)!;
                await Settle();
                window.UpdateLayout();
                RenderTick(window);
                var browserView = (Control)GetField(window, "BrowserView")!;
                Assert.True(browserView.IsVisible);
                Assert.Equal("explorer.backButton", window.SideButtonSlotAt(browserView, true, out tab)!.Id);
                Assert.Equal("explorer.forwardButton", window.SideButtonSlotAt(browserView, false, out tab)!.Id);
                // Default (5.0.0): Back. A new Explorer tab has nothing to go back to, so the folder stays.
                Assert.True(RaiseSideButton(window, browserView, true).Handled);
                await Settle();
                Assert.False(Collapsed(window));
                Assert.Equal(root, ((BrowserTabState)((WorkspaceState)GetField(window, "_workspace")!).Active!).Folder);
                Settings(window).Gestures["explorer.backButton"] = GestureCatalog.ToggleStatusBar;
                Assert.True(RaiseSideButton(window, browserView, true).Handled);
                await Settle();
                Assert.True(Collapsed(window));
            });
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }

    // ---- registry coverage of menus, buttons and the dispatcher ------------------------------------------------

    [Fact]
    public async Task Every_context_menu_item_is_a_registry_action_or_an_explicit_context_item()
    {
        var root = Path.Combine(Path.GetTempPath(), "Glide.MenuCoverage", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "Sub"));
        File.WriteAllBytes(Path.Combine(root, "a.png"), new byte[] { 0x89, 0x50, 0x4E, 0x47 });
        try
        {
            await OnWindow(async (window, _) =>
            {
                var menus = new List<(string Name, ContextMenu Menu)>
                {
                    ("viewer", (ContextMenu)Invoke(window, "BuildViewerContextMenu")!),
                    ("status", window.BuildStatusSurfaceContextMenu()),
                    ("fullscreen chrome", window.BuildFullscreenChromeContextMenu()),
                    ("new tab", (ContextMenu)Invoke(window, "BuildNewTabMenu")!),
                    ("overlay only", (ContextMenu)Invoke(window, "BuildWholeAppOverlayOnlyContextMenu")!),
                };
                foreach (var button in TitleBarButtonCatalog.All)
                    menus.Add(("title " + button.Id, (ContextMenu)Invoke(window, "BuildTitleActionContextMenu", button.Id)!));
                foreach (TabState tab in new TabState[] { new HomeTabState(Guid.NewGuid()), new ImageTabState(Guid.NewGuid(), Path.Combine(root, "a.png")), new BrowserTabState(Guid.NewGuid(), root) })
                    menus.Add(("tab " + tab.Kind, (ContextMenu)Invoke(window, "BuildTabContextMenu", tab)!));

                // Explorer: background, one item, multi-selection and the history dropdown.
                await (Task)Invoke(window, "OpenExplorerTabAsync", root)!;
                await Settle();
                menus.Add(("explorer background", window.BuildExplorerContextMenu(null)));
                var entries = ((System.Collections.IEnumerable)GetField(window, "_browserEntries")!).Cast<Glide.App.Explorer.ExplorerEntry>().ToList();
                Assert.NotEmpty(entries);
                menus.Add(("explorer item", window.BuildExplorerContextMenu(entries[0])));
                Invoke(window, "SelectAllBrowserEntries");
                menus.Add(("explorer multi", window.BuildExplorerContextMenu(entries[0])));
                menus.Add(("explorer history", window.BuildExplorerHistoryMenu()));

                var problems = new List<string>();
                foreach (var (name, menu) in menus)
                    foreach (var item in Leaves(menu.ItemsSource))
                    {
                        var tag = MenuActions.GetActionId(item);
                        if (!MenuActions.IsValidTag(tag)) problems.Add($"{name}: '{item.Header}' -> {tag ?? "(untagged)"}");
                    }
                Assert.True(problems.Count == 0, string.Join("\n", problems));
            });
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }

    [Fact]
    public async Task Every_status_bar_button_maps_to_a_registry_action()
    {
        await OnWindow((window, _) =>
        {
            var map = window.StatusButtonActionIds();
            var surface = (Control)GetField(window, "ViewerStatusSurface")!;
            var buttons = Avalonia.LogicalTree.LogicalExtensions.GetLogicalDescendants(surface).OfType<Button>()
                .Concat(surface.GetVisualDescendants().OfType<Button>())
                .Append((Button)GetField(window, "StatusRestoreButton")!)
                .Distinct().ToList();
            Assert.True(buttons.Count >= 20, $"found only {buttons.Count} status-bar buttons");
            foreach (var button in buttons)
            {
                Assert.True(map.TryGetValue(button, out var tag), $"status-bar button {button.Name} is not mapped to a registry action");
                Assert.True(MenuActions.IsValidTag(tag), $"{button.Name} -> {tag}");
            }
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task Every_title_bar_button_runs_a_mapped_registry_command()
    {
        await OnWindow((window, _) =>
        {
            var mapped = (IReadOnlyCollection<GlideCommand>)Invoke(window, "ProductionMappedCommandsForTests")!;
            foreach (var button in TitleBarButtonCatalog.All)
            {
                var action = AppActionRegistry.Find(button.ActionId);
                Assert.True(action is not null, button.Id);
                Assert.Equal(action!.Command, button.Command);
                Assert.Contains(button.Command, mapped);
            }
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task The_production_dispatcher_maps_every_registry_command()
    {
        await OnWindow((window, _) =>
        {
            var mapped = (IReadOnlyCollection<GlideCommand>)Invoke(window, "ProductionMappedCommandsForTests")!;
            foreach (var action in AppActionRegistry.All.Where(a => a.Kind == AppActionKind.Command))
                Assert.Contains(action.Command, mapped);
            return Task.CompletedTask;
        });
    }

    [Fact]
    public void No_command_handler_is_a_bare_placeholder()
    {
        // "Does nothing" bugs were empty dispatcher lambdas. A handler may return Task.CompletedTask after doing work,
        // but never consist of nothing else.
        var source = SourceText.Read("src/Glide.App/MainWindow.Commands.cs");
        var placeholders = Regex.Matches(source, @"\[GlideCommand\.(\w+)\]\s*=\s*\(\)\s*=>\s*(Task\.CompletedTask|\{\s*(return\s+)?Task\.CompletedTask;?\s*\})");
        Assert.True(placeholders.Count == 0, "placeholder handlers: " + string.Join(", ", placeholders.Select(m => m.Groups[1].Value)));
    }

    /// <summary>
    /// Keyboard handling outside the router must stay on this allow-list: text editing, dialogs, Settings capture
    /// and surface-owned keys that are listed in the registry as fixed shortcuts.
    /// </summary>
    [Fact]
    public void Hardcoded_key_handling_only_exists_in_allow_listed_places()
    {
        var allowed = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["MainWindow.Commands.cs"] = "the router itself: OnKeyDown/RouteHotkeyAsync, the Ctrl+Tab tunnel claim, Space-pauses-slideshow (fixed.slideshow.spacePause), Home repeat guard, held-arrow release",
            ["MainWindow.axaml.cs"] = "registers the router handlers; tracks Ctrl for the Ctrl+Tab MRU cycle",
            ["MainWindow.ExplorerInput.cs"] = "Explorer list keys, all listed as fixed.explorer.* registry entries",
            ["MainWindow.ExplorerMenus.cs"] = "shows the fixed Explorer shortcuts (read from the registry) in its menu",
            ["MainWindow.ExplorerOps.cs"] = "inline rename text box: Enter / Escape",
            ["MainWindow.Pickers.cs"] = "overlay keyboard zoom (fixed.overlay.keyboardZoom)",
            ["MainWindow.Sessions.cs"] = "rename dialog: Enter / Escape",
            ["MainWindow.Slideshow.cs"] = "slideshow interval dialog keys",
            ["MainWindow.Browser.cs"] = "Explorer filter text box: Esc / Down / Enter move between the box and the list",
            ["MainWindow.Navigation.cs"] = "folder-boundary prompt: remembers a held arrow until it is released",
            ["Services/KeyboardShortcutText.cs"] = "the canonical key-to-text adapter",
            ["Services/DialogEscape.cs"] = "Esc cancels every dialog / secondary window (one class handler; never the main viewer window)",
            ["Controls/ExplorerAddressBar.cs"] = "address bar text editing",
            ["Controls/BrowserSurface.axaml.cs"] = "forwards the filter box keys",
            ["Controls/WindowInWindowOverlayManager.cs"] = "Esc cancels drawing an overlay area (fixed.overlay.areaCancel)",
            ["Settings/SettingsWindow.axaml.cs"] = "Settings window keys",
            ["Settings/SettingsWindow.Hotkeys.cs"] = "shortcut capture and Search hotkey",
            ["Settings/SettingsWindow.Profiles.cs"] = "profile rename text box",
            ["Settings/SettingsWindow.Gestures.cs"] = "gesture panel search box",
            ["Settings/GestureActionPicker.cs"] = "picker search box navigation",
            ["Settings/SettingsWindow.Responsive.cs"] = "Settings window keys",
            ["Controls/SettingsCard.cs"] = "card header Enter / Space",
            ["Printing/PrintWindow.cs"] = "print dialog keys",
            ["Settings/TitleBarCustomizerWindow.cs"] = "customizer list keys",
            ["Settings/ResetDialogs.cs"] = "Reset Glide confirmation: Esc cancels (Cancel is focused; Enter never starts the destructive button)",
            ["Updates/UpdatePromptWindow.cs"] = "update prompt dialog: Esc = Not now (Not now is focused; Enter never opens the browser unless View on GitHub is focused)",
        };
        var root = Path.Combine(SourceText.RepoRoot, "src", "Glide.App");
        var pattern = new Regex(@"\bKey\.(?!Modifiers)[A-Z][A-Za-z0-9]*\b(?!\s*\()|KeyGesture|KeyDownEvent|\.KeyDown\s*\+=|KeyBinding");
        var offenders = new List<string>();
        foreach (var file in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar)) continue;
            var relative = Path.GetRelativePath(root, file).Replace('\\', '/');
            if (allowed.ContainsKey(relative)) continue;
            var text = File.ReadAllText(file);
            if (pattern.IsMatch(text)) offenders.Add(relative + ": " + pattern.Match(text).Value);
        }
        Assert.True(offenders.Count == 0, "hardcoded key handling outside the router (route it through the hotkey map or allow-list it with a reason):\n" + string.Join("\n", offenders));
    }

    // ---- helpers ------------------------------------------------------------------------------------------------

    private static IEnumerable<MenuItem> Leaves(System.Collections.IEnumerable? items)
    {
        if (items is null) yield break;
        foreach (var item in items.OfType<MenuItem>())
        {
            if (item.Header as string == "-") continue;
            var children = item.ItemsSource ?? (item.Items.Count > 0 ? item.Items : null);
            if (children is not null && children.Cast<object>().Any())
            {
                foreach (var child in Leaves(children)) yield return child;
                continue;
            }
            yield return item;
        }
    }

    private static (Key Key, RawInputModifiers Modifiers)? ToKey(string shortcut)
    {
        var canonical = ShortcutText.Canonicalize(shortcut);
        var modifiers = RawInputModifiers.None;
        string keyText;
        if (canonical == "+") keyText = "+";
        else if (canonical.EndsWith("++", StringComparison.Ordinal)) { keyText = "+"; canonical = canonical[..^2] + "+X"; }
        else keyText = canonical[(canonical.LastIndexOf('+') + 1)..];
        foreach (var part in canonical.Split('+').SkipLast(1))
            modifiers |= part switch { "Ctrl" => RawInputModifiers.Control, "Shift" => RawInputModifiers.Shift, "Alt" => RawInputModifiers.Alt, _ => RawInputModifiers.None };
        Key? key = keyText switch
        {
            "+" => Key.Add, "=" => Key.OemPlus, "-" => Key.OemMinus, "," => Key.OemComma, "." => Key.OemPeriod,
            "Esc" => Key.Escape, "Backspace" => Key.Back, "Space" => Key.Space, "Enter" => Key.Enter, "Tab" => Key.Tab,
            _ when keyText.Length == 1 && char.IsDigit(keyText[0]) => Key.D0 + (keyText[0] - '0'),
            _ when Enum.TryParse<Key>(keyText, out var parsed) => parsed,
            _ => null
        };
        return key is null ? null : (key.Value, modifiers);
    }

    private static async Task Press(MainWindow window, Key key, RawInputModifiers modifiers)
    {
        window.KeyPress(key, modifiers, PhysicalKey.None, null);
        await Settle();
        window.KeyRelease(key, modifiers, PhysicalKey.None, null);
        await Settle();
    }

    private static Task Execute(MainWindow window, string actionId) =>
        window.ExecuteActionAsync(AppActionRegistry.Find(actionId)!, "test");

    private static void Intercept(MainWindow window) => window.ActionInterceptorForTests = (_, _) => true;

    private static void RenderTick(MainWindow window)
    {
        // Hit testing (pointer routing) needs the composition scene of the last rendered frame.
        for (var i = 0; i < 3; i++)
        {
            Dispatcher.UIThread.RunJobs(DispatcherPriority.SystemIdle);
            Avalonia.Headless.AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs(DispatcherPriority.SystemIdle);
            using (window.CaptureRenderedFrame()) { }
        }
    }

    private static void Pump() => Dispatcher.UIThread.RunJobs();

    private static async Task Settle()
    {
        for (var i = 0; i < 4; i++) { Dispatcher.UIThread.RunJobs(); await Task.Delay(5); }
    }

    private static void ResetWorkspace(MainWindow window)
    {
        var workspace = (WorkspaceState)GetField(window, "_workspace")!;
        if (workspace.Active is not HomeTabState) workspace.Reset(new TabState[] { new HomeTabState(Guid.NewGuid()) }, 0);
        if (window.WindowState != WindowState.Normal) window.WindowState = WindowState.Normal;
    }

    private static async Task OnWindow(Func<MainWindow, List<AppAction>, Task> body)
    {
        var root = Path.Combine(Path.GetTempPath(), "Glide.Settings.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        await AvaloniaHeadlessFixture.RunOnUIThreadAsync(async () =>
        {
            var previousStore = SettingsStore.SettingsDirectoryOverrideForTests.Value;
            SettingsStore.SettingsDirectoryOverrideForTests.Value = root;
            var previousLauncher = MainWindow.WindowsExplorerLocateLauncher;
            MainWindow.WindowsExplorerLocateLauncher = _ => true;
            var window = new MainWindow();
            var routed = new List<AppAction>();
            try
            {
                SetField(window, "_settings", new GlideSettingsState());
                window.Show();
                await Settle();
                ((WorkspaceState)GetField(window, "_workspace")!).Reset(new TabState[] { new HomeTabState(Guid.NewGuid()) }, 0);
                window.ActionRouted += (action, _) => routed.Add(action);
                await body(window, routed);
            }
            finally
            {
                window.ActionInterceptorForTests = (_, _) => true;
                window.Close();
                MainWindow.WindowsExplorerLocateLauncher = previousLauncher;
                SettingsStore.SettingsDirectoryOverrideForTests.Value = previousStore;
            }
        });
        try { Directory.Delete(root, true); } catch { }
    }

    private static GlideSettingsState Settings(MainWindow window) => (GlideSettingsState)GetField(window, "_settings")!;
    private static bool Collapsed(MainWindow window) => (bool)GetField(window, "_statusCollapsed")!;

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
