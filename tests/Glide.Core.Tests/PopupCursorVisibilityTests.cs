using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Avalonia.Markup.Xaml.Styling;
using Glide.App;
using Glide.App.Services;
using Glide.App.Settings;
using Glide.Core.Settings;
using Glide.Core.Workspace;

namespace Glide.Core.Tests;

/// <summary>
/// Contract: the mouse pointer is never invisible over a menu, sub-menu, drop-down or flyout.
/// Root cause of the reported bug: <c>InputElement.Cursor</c> is inherited, every popup is a separate top-level whose
/// inheritance parent is the window it was opened from, and MainWindow hides the pointer by setting
/// <c>Cursor = None</c> on itself (fullscreen idle hiding) - so every popup inherited "no cursor". The idle timer also
/// kept running while the pointer was inside an open menu (the main window gets no pointer events there).
/// </summary>
// Source-text contract kept: this asserts on production source (UI code-behind, native/installer files or a repo-wide scan) that cannot be run headlessly; behavioural coverage lives in the other test files.
[Collection(AvaloniaHeadlessCollection.Name)]
public sealed class PopupCursorVisibilityTests
{
    private static readonly DateTime T0 = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly Cursor Hidden = new(StandardCursorType.None);

    public PopupCursorVisibilityTests(AvaloniaHeadlessFixture fixture) { _ = fixture; }

    // ---- pure policy -----------------------------------------------------------------------------------------

    [Fact]
    public void Idle_hiding_is_lifted_while_a_popup_is_open_and_never_hides_over_the_menu()
    {
        var p = new FullscreenCursorAutoHide();
        p.Activate(T0);
        p.OnPointerMoved(T0, 100, 100);
        Assert.False(p.SetPopupOpen(false, T0));
        p.SetPopupOpen(true, T0.AddSeconds(1));
        // The pointer is inside the popup: the main window gets no events and the idle clock runs on.
        Assert.False(p.Tick(T0.AddSeconds(30)));
        Assert.False(p.IsHidden);
    }

    [Fact]
    public void A_popup_opening_over_an_already_hidden_cursor_reveals_it_at_once()
    {
        var p = new FullscreenCursorAutoHide();
        p.Activate(T0);
        p.OnPointerMoved(T0, 100, 100);
        Assert.True(p.Tick(T0.AddSeconds(3)));
        Assert.True(p.IsHidden);
        Assert.True(p.SetPopupOpen(true, T0.AddSeconds(4)));
        Assert.False(p.IsHidden);
    }

    [Fact]
    public void Closing_the_popup_restarts_the_idle_countdown_so_hiding_over_the_image_still_works()
    {
        var p = new FullscreenCursorAutoHide();
        p.Activate(T0);
        p.OnPointerMoved(T0, 100, 100);
        p.SetPopupOpen(true, T0.AddSeconds(1));
        p.Tick(T0.AddSeconds(60));
        Assert.False(p.SetPopupOpen(false, T0.AddSeconds(60)));
        Assert.False(p.Tick(T0.AddSeconds(62.9)));       // countdown restarted at close, not hidden instantly
        Assert.True(p.Tick(T0.AddSeconds(63)));           // the feature itself still works with no menu open
        Assert.True(p.IsHidden);
    }

    [Fact]
    public void Popup_flag_is_independent_of_pointer_driven_suppression()
    {
        var p = new FullscreenCursorAutoHide();
        p.Activate(T0);
        p.OnPointerMoved(T0, 100, 100);
        p.SetPopupOpen(true, T0);
        p.SetSuppressed(true, T0.AddSeconds(1));
        p.SetSuppressed(false, T0.AddSeconds(2));         // a later pointer move over the image must not re-arm hiding
        Assert.False(p.Tick(T0.AddSeconds(20)));
        Assert.True(p.IsPopupOpen);
    }

    // ---- headless popups: the inherited cursor of every surface ---------------------------------------------------

    [Fact]
    public async Task Context_menu_and_sub_menu_items_never_inherit_a_hidden_window_cursor()
    {
        await OnThemedWindow(window =>
        {
            window.Cursor = Hidden;                        // exactly what fullscreen idle hiding does
            var sub = new MenuItem { Header = "Size" };
            var tiny = new MenuItem { Header = "Tiny" };
            sub.Items.Add(tiny);
            sub.Items.Add(new MenuItem { Header = "Small" });
            var top = new MenuItem { Header = "Auto-hide" };
            var menu = new ContextMenu { ItemsSource = new object[] { top, new Separator(), sub } };
            menu.Open(window);
            Dispatcher.UIThread.RunJobs();
            sub.IsSubMenuOpen = true;
            Dispatcher.UIThread.RunJobs();

            AssertVisibleCursor(menu, "ContextMenu");
            AssertVisibleCursor(top, "top-level MenuItem");
            AssertVisibleCursor(sub, "sub-menu header");
            AssertVisibleCursor(tiny, "sub-menu item");
            foreach (var popupRoot in OpenPopupRoots(menu, sub)) AssertVisibleCursor(popupRoot, "PopupRoot");
            menu.Close();
        });
    }

    [Fact]
    public async Task Combo_box_dropdown_items_never_inherit_a_hidden_window_cursor()
    {
        await OnThemedWindow(window =>
        {
            var combo = new ComboBox { ItemsSource = new[] { "One", "Two" }, SelectedIndex = 0 };
            window.Content = combo;
            window.Cursor = Hidden;
            window.UpdateLayout();
            combo.IsDropDownOpen = true;
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            var items = new List<ComboBoxItem>();
            for (var i = 0; i < 2; i++) if (combo.ContainerFromIndex(i) is ComboBoxItem container) items.Add(container);
            Assert.NotEmpty(items);
            foreach (var item in items) AssertVisibleCursor(item, "ComboBoxItem");
            combo.IsDropDownOpen = false;
        });
    }

    [Fact]
    public async Task A_control_that_picks_its_own_cursor_inside_a_popup_keeps_it()
    {
        await OnThemedWindow(window =>
        {
            var box = new TextBox { Cursor = new Cursor(StandardCursorType.Ibeam) };
            var menu = new ContextMenu { ItemsSource = new object[] { new MenuItem { Header = box } } };
            window.Cursor = Hidden;
            menu.Open(window);
            Dispatcher.UIThread.RunJobs();
            Assert.NotNull(box.Cursor);
            Assert.False(ReferenceEquals(box.Cursor, PopupCursorGuard.Arrow));
            menu.Close();
        });
    }

    [Fact]
    public async Task Open_popup_registry_reports_owner_and_notifies()
    {
        await OnThemedWindow(window =>
        {
            Assert.False(PopupCursorGuard.HasOpenPopup(window));
            var raised = 0;
            void Changed() => raised++;
            PopupCursorGuard.OpenPopupsChanged += Changed;
            try
            {
                var menu = new ContextMenu { ItemsSource = new object[] { new MenuItem { Header = "x" } } };
                menu.Open(window);
                Dispatcher.UIThread.RunJobs();
                Assert.True(PopupCursorGuard.HasOpenPopup(window));
                menu.Close();
                Dispatcher.UIThread.RunJobs();
                Assert.False(PopupCursorGuard.HasOpenPopup(window));
                Assert.True(raised >= 2, "open and close both notify");
            }
            finally { PopupCursorGuard.OpenPopupsChanged -= Changed; }
        });
    }

    // ---- the real MainWindow ----------------------------------------------------------------------------------------

    [Fact]
    public async Task MainWindow_lifts_idle_cursor_hiding_while_its_menu_is_open_and_restores_it_afterwards()
    {
        await OnMainWindow(window =>
        {
            var policy = (FullscreenCursorAutoHide)Get(window, "_cursorAutoHide")!;
            var settings = (GlideSettingsState)Get(window, "_settings")!;
            settings.HideCursorFullscreen = true;
            policy.Activate(DateTime.UtcNow.AddSeconds(-10));
            policy.OnPointerMoved(DateTime.UtcNow.AddSeconds(-10), 10, 10);
            Assert.True(policy.Tick(DateTime.UtcNow));              // idle for 10s: hidden over the image
            Call(window, "ApplyFullscreenCursorVisibility");
            Assert.True(policy.IsHidden);
            Assert.NotNull(window.Cursor);                          // the window carries the hidden cursor

            var menu = (ContextMenu)Call(window, "BuildStatusSurfaceContextMenu")!;
            menu.Open(window);
            Dispatcher.UIThread.RunJobs();
            Assert.False(policy.IsHidden);
            Assert.True(policy.IsPopupOpen);
            Assert.Null(window.Cursor);                             // hidden cursor removed: popups resolve the arrow
            Assert.False(policy.Tick(DateTime.UtcNow.AddSeconds(60)), "never re-hides while the menu is open");

            menu.Close();
            Dispatcher.UIThread.RunJobs();
            Assert.False(policy.IsPopupOpen);
            Assert.False(policy.IsHidden);
            Assert.True(policy.Tick(DateTime.UtcNow.AddSeconds(4)), "idle hiding over the image works again afterwards");
        });
    }

    public static IEnumerable<object[]> MenuBuilders() => new[]
    {
        new object[] { "status bar (BuildStatusSurfaceContextMenu)" },
        new object[] { "viewer (BuildViewerContextMenu)" },
        new object[] { "overlay-only (BuildWholeAppOverlayOnlyContextMenu)" },
        new object[] { "tab (BuildTabContextMenu)" },
        new object[] { "new-tab button (BuildNewTabMenu)" },
        new object[] { "title bar: Show in File Explorer (BuildTitleActionContextMenu file.locate)" },
        new object[] { "title bar: Always on top (BuildTitleActionContextMenu window.alwaysOnTop)" },
        new object[] { "explorer background (BuildExplorerContextMenu null)" },
        new object[] { "explorer history dropdown (BuildExplorerHistoryMenu)" },
    };

    [Theory]
    [MemberData(nameof(MenuBuilders))]
    public async Task Every_menu_the_app_builds_resolves_a_visible_cursor_for_itself_and_every_item_and_sub_item(string which)
    {
        await OnMainWindow(window =>
        {
            window.Cursor = Hidden;
            var menu = which switch
            {
                var s when s.StartsWith("status bar") => (ContextMenu)Call(window, "BuildStatusSurfaceContextMenu")!,
                var s when s.StartsWith("viewer") => (ContextMenu)Call(window, "BuildViewerContextMenu")!,
                var s when s.StartsWith("overlay-only") => (ContextMenu)Call(window, "BuildWholeAppOverlayOnlyContextMenu")!,
                var s when s.StartsWith("tab (") => (ContextMenu)Call(window, "BuildTabContextMenu", new ImageTabState(Guid.NewGuid(), @"C:\x\a.jpg"))!,
                var s when s.StartsWith("new-tab") => (ContextMenu)Call(window, "BuildNewTabMenu")!,
                var s when s.Contains("file.locate") => (ContextMenu)Call(window, "BuildTitleActionContextMenu", "file.locate")!,
                var s when s.Contains("window.alwaysOnTop") => (ContextMenu)Call(window, "BuildTitleActionContextMenu", "window.alwaysOnTop")!,
                var s when s.StartsWith("explorer background") => (ContextMenu)Call(window, "BuildExplorerContextMenu", new object?[] { null })!,
                _ => (ContextMenu)Call(window, "BuildExplorerHistoryMenu")!,
            };
            menu.Open(window);
            Dispatcher.UIThread.RunJobs();
            try
            {
                AssertVisibleCursor(menu, which);
                var count = 0;
                foreach (var item in AllItems(menu)) { AssertVisibleCursor(item, which + " > " + item.Header); count++; }
                Assert.True(count > 0, which + " builds no items");
            }
            finally { menu.Close(); }
        });
    }

    [Fact]
    public void Application_start_and_main_window_register_the_central_guard_and_no_other_path_hides_the_pointer()
    {
        var app = ReadSource("src", "Glide.App", "App.axaml.cs");
        Assert.Contains("PopupCursorGuard.Register();", app, StringComparison.Ordinal);
        var window = ReadSource("src", "Glide.App", "MainWindow.axaml.cs");
        Assert.Contains("AttachPopupCursorGuard();", window, StringComparison.Ordinal);
        Assert.Contains("SyncPopupCursorState();", window, StringComparison.Ordinal);
        // No code path may hide the pointer by any means other than the policy-driven window cursor.
        foreach (var file in Directory.EnumerateFiles(SourceRoot("src", "Glide.App"), "*.cs", SearchOption.AllDirectories))
        {
            var text = SourceText.Read(file);
            if (!text.Contains("StandardCursorType.None", StringComparison.Ordinal)) continue;
            Assert.True(Path.GetFileName(file) == "MainWindow.axaml.cs", $"{Path.GetFileName(file)} introduces another hidden-cursor path; route it through FullscreenCursorAutoHide so popups are exempt.");
        }
    }

    // ---- helpers ----------------------------------------------------------------------------------------------------

    private static void AssertVisibleCursor(InputElement element, string what)
    {
        Assert.True(!ReferenceEquals(element.Cursor, Hidden), $"{what}: pointer would be invisible (inherited the hidden window cursor)");
    }

    private static IEnumerable<MenuItem> AllItems(ItemsControl parent)
    {
        foreach (var item in parent.Items.OfType<MenuItem>())
        {
            yield return item;
            foreach (var child in AllItems(item)) yield return child;
        }
    }

    private static IEnumerable<InputElement> OpenPopupRoots(params Control[] anchors)
    {
        foreach (var anchor in anchors)
            if (anchor.GetVisualRoot() is PopupRoot root) yield return root;
            else if (anchor.FindAncestorOfType<PopupRoot>() is { } r) yield return r;
    }

    private static async Task OnThemedWindow(Action<Window> body)
    {
        await AvaloniaHeadlessFixture.RunOnUIThreadAsync(() =>
        {
            PopupCursorGuard.Register();
            PopupCursorGuard.InstallStyles();
            var window = new Window { Width = 400, Height = 300 };
            window.Styles.Insert(0, new FluentTheme());
            window.Styles.Insert(1, new StyleInclude(new Uri("avares://Glide/")) { Source = new Uri("avares://Glide/Styles/GlideTheme.axaml") });
            window.Show();
            Dispatcher.UIThread.RunJobs();
            try { body(window); }
            finally { window.Close(); }
            return Task.CompletedTask;
        });
    }

    private static async Task OnMainWindow(Action<MainWindow> body)
    {
        var root = Path.Combine(Path.GetTempPath(), "Glide.Settings.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        await AvaloniaHeadlessFixture.RunOnUIThreadAsync(() =>
        {
            var previousStore = SettingsStore.SettingsDirectoryOverrideForTests.Value;
            SettingsStore.SettingsDirectoryOverrideForTests.Value = root;
            var window = new MainWindow();
            try
            {
                window.Styles.Insert(0, new FluentTheme());
                window.Styles.Insert(1, new StyleInclude(new Uri("avares://Glide/")) { Source = new Uri("avares://Glide/Styles/GlideTheme.axaml") });
                Set(window, "_settings", new GlideSettingsState { ShowStatusSurface = true });
                window.Width = 1000; window.Height = 700;
                window.Show();
                Dispatcher.UIThread.RunJobs();
                // Headless windows have no native popup implementation: popups need the window template's overlay layer.
                if (window.TryFindResource(typeof(Window), window.ActualThemeVariant, out var theme) && theme is Avalonia.Styling.ControlTheme controlTheme)
                    window.Theme = controlTheme;      // the constructor ran before the theme existed: re-template so the overlay layer exists
                window.ApplyTemplate();
                window.UpdateLayout();
                Dispatcher.UIThread.RunJobs();
                body(window);
            }
            finally
            {
                window.Close();
                SettingsStore.SettingsDirectoryOverrideForTests.Value = previousStore;
            }
            return Task.CompletedTask;
        });
        try { Directory.Delete(root, true); } catch { }
    }

    private const BindingFlags Flags = BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance;

    private static object? Call(MainWindow window, string method, params object?[] args)
    {
        var info = typeof(MainWindow).GetMethod(method, Flags);
        Assert.NotNull(info);
        return info!.Invoke(window, args);
    }

    private static object? Get(MainWindow window, string field)
    {
        var info = typeof(MainWindow).GetField(field, Flags);
        Assert.NotNull(info);
        return info!.GetValue(window);
    }

    private static void Set(MainWindow window, string field, object? value)
    {
        var info = typeof(MainWindow).GetField(field, Flags);
        Assert.NotNull(info);
        info!.SetValue(window, value);
    }

    private static string SourceRoot(params string[] segments)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(new[] { directory.FullName }.Concat(segments).ToArray());
            if (Directory.Exists(candidate)) return candidate;
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException(Path.Combine(segments));
    }

    private static string ReadSource(params string[] segments)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(new[] { directory.FullName }.Concat(segments).ToArray());
            if (File.Exists(candidate)) return SourceText.Read(candidate);
            directory = directory.Parent;
        }
        throw new FileNotFoundException(Path.Combine(segments));
    }
}
