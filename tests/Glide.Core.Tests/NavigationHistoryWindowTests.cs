using System.IO.Compression;
using System.Reflection;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using Glide.App;
using Glide.App.Settings;
using Glide.Core.Commands;
using Glide.Core.Navigation;
using Glide.Core.Workspace;

namespace Glide.Core.Tests;

/// <summary>
/// Back / Forward on a real headless MainWindow (5.0.0): the title-bar buttons, real key presses through the window's
/// key handlers, Home to image and back, a new tab without history, the title-bar default and its one-time migration.
/// The Explorer side (selection / scroll restore, Explorer keys and text boxes) is in Glide.Explorer.Tests.
/// </summary>
[Collection(AvaloniaHeadlessCollection.Name)]
public sealed class NavigationHistoryWindowTests
{
    public NavigationHistoryWindowTests(AvaloniaHeadlessFixture fixture) { _ = fixture; }

    // ---- title-bar catalogue, default and migration ------------------------------------------------------------------

    [Fact]
    public void The_default_title_bar_has_Back_and_Forward_and_not_the_image_arrows()
    {
        Assert.Equal(new[] { "nav.back", "nav.forward" }, TitleBarButtonCatalog.DefaultButtonIds.Take(2));
        Assert.DoesNotContain("image.previous", TitleBarButtonCatalog.DefaultButtonIds);
        Assert.DoesNotContain("image.next", TitleBarButtonCatalog.DefaultButtonIds);
        Assert.Equal(TitleBarButtonCatalog.PreviousDefaultButtonIds, TitleBarButtonCatalog.DefaultButtonIds.Skip(2));
        Assert.Equal(TitleBarButtonCatalog.DefaultButtonIds, new GlideSettingsState().TitleBarButtons);
    }

    [Fact]
    public void Back_and_Forward_are_in_the_customiser_catalogue_and_the_image_arrows_stay_optional()
    {
        var back = TitleBarButtonCatalog.Find("nav.back")!;
        var forward = TitleBarButtonCatalog.Find("nav.forward")!;
        Assert.Equal(("Back", "Back", GlideCommand.NavigateBack), (back.Label, back.IconKind, back.Command));
        Assert.Equal(("Forward", "Forward", GlideCommand.NavigateForward), (forward.Label, forward.IconKind, forward.Command));
        Assert.Equal("Images and tabs", TitleBarButtonCatalog.CategoryOf("nav.back"));
        Assert.Equal("Images and tabs", TitleBarButtonCatalog.CategoryOf("nav.forward"));
        Assert.NotNull(TitleBarButtonCatalog.Find("image.previous"));
        Assert.NotNull(TitleBarButtonCatalog.Find("image.next"));
        // Back / Forward lead the "Images and tabs" group (before Home, New tab and the image arrows).
        var group = TitleBarButtonCatalog.All.Where(d => TitleBarButtonCatalog.CategoryOf(d.Id) == "Images and tabs").Select(d => d.Id).ToList();
        Assert.Equal(new[] { "nav.back", "nav.forward", "home" }, group.Take(3));
        Assert.Single(TitleBarButtonCatalog.All, d => d.Id == "nav.back");
    }

    [Fact]
    public void A_stored_previous_default_layout_migrates_once_and_a_customised_layout_is_kept()
    {
        WithIsolatedStore(() =>
        {
            void Write(int schema, params string[] buttons) => File.WriteAllText(SettingsStore.GetSettingsPath(),
                "{\"SettingsSchemaVersion\":" + schema + ",\"TitleBarButtons\":[" + string.Join(",", buttons.Select(b => "\"" + b + "\"")) + "]}");

            // The untouched 5.0 default gains Back / Forward.
            Write(8, TitleBarButtonCatalog.PreviousDefaultButtonIds);
            Assert.Equal(TitleBarButtonCatalog.DefaultButtonIds, SettingsStore.Load().TitleBarButtons);

            // A customised layout (even one holding the image arrows) is not touched.
            Write(8, "image.previous", "image.next", "file.open", "settings");
            Assert.Equal(new[] { "image.previous", "image.next", "file.open", "settings" }, SettingsStore.Load().TitleBarButtons);

            // A file saved at schema 9 that holds the old list is the user's choice (they removed Back / Forward): kept.
            Write(9, TitleBarButtonCatalog.PreviousDefaultButtonIds);
            Assert.Equal(TitleBarButtonCatalog.PreviousDefaultButtonIds, SettingsStore.Load().TitleBarButtons);

            // Older historical defaults still move to the current default.
            Write(3, "folder.previous", "folder.next", "file.open", "window.alwaysOnTop");
            Assert.Equal(TitleBarButtonCatalog.DefaultButtonIds, SettingsStore.Load().TitleBarButtons);
        });
    }

    [Fact]
    public void A_saved_hotkey_map_without_Back_Forward_gets_them_unless_the_key_is_already_used()
    {
        WithIsolatedStore(() =>
        {
            File.WriteAllText(SettingsStore.GetSettingsPath(),
                "{\"SettingsSchemaVersion\":8,\"Hotkeys\":{\"view.fit\":[\"Alt+Right\"],\"nav.previousBack\":[\"Backspace\"]}}");
            var loaded = SettingsStore.Load();
            Assert.Equal(new[] { "Alt+Left" }, loaded.Hotkeys["nav.back"]);
            Assert.Equal(new[] { "Backspace" }, loaded.Hotkeys["nav.backOrPrevious"]);   // schema 11: Backspace moved from Previous image once
            Assert.Equal(new[] { "Shift+Backspace" }, loaded.Hotkeys["nav.forward"]);   // Alt+Right stays the user's Fit image
            Assert.Equal(new[] { "Alt+Right" }, loaded.Hotkeys["view.fit"]);
            Assert.Empty(loaded.Hotkeys["nav.previousBack"]);
            Assert.Equal(SettingsStore.CurrentSettingsSchema, loaded.SettingsSchemaVersion);
            Assert.Equal(11, SettingsStore.CurrentSettingsSchema);
        });
    }

    // ---- the real window ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task Title_bar_Back_Forward_are_dimmed_without_history_and_their_tooltips_read_the_live_binding()
    {
        await OnWindow(async (window, _) =>
        {
            Invoke(window, "RebuildTitleActionStrip");
            var back = TitleButton(window, "nav.back");
            var forward = TitleButton(window, "nav.forward");
            Assert.True(back.Classes.Contains("inactive"));
            Assert.True(forward.Classes.Contains("inactive"));
            Assert.True(back.Opacity < 0.5);
            Assert.True(back.IsEnabled, "never IsEnabled = false: a click must not fall through to the caption");
            Assert.Equal("Back (Alt+Left)", ToolTip.GetTip(back)?.ToString());
            Assert.Equal("Forward (Shift+Backspace / Alt+Right)", ToolTip.GetTip(forward)?.ToString());

            Settings(window).Hotkeys["nav.back"] = new List<string> { "F7" };
            Invoke(window, "RefreshDynamicTooltips");
            Assert.Equal("Back (F7)", ToolTip.GetTip(TitleButton(window, "nav.back"))?.ToString());
            Settings(window).Hotkeys["nav.back"] = new List<string>();
            Invoke(window, "RefreshDynamicTooltips");
            Assert.Contains("Unassigned", ToolTip.GetTip(TitleButton(window, "nav.back"))?.ToString());
            await Task.CompletedTask;
        });
    }

    [Fact]
    public async Task Home_to_an_image_in_the_same_tab_then_Back_returns_Home_and_Forward_returns_to_the_image()
    {
        var folder = TempFolder("a.png", "b.png");
        try
        {
            await OnWindow(async (window, _) =>
            {
                Invoke(window, "RebuildTitleActionStrip");
                await Activate(window);
                var tabId = Workspace(window).Active!.Id;
                var image = Path.Combine(folder, "a.png");
                await (Task)Invoke(window, "OpenAsImageTabAsync", image, false, false)!;
                await Settle();
                Assert.Equal(tabId, Workspace(window).Active!.Id);                       // same tab
                Assert.False(TitleButton(window, "nav.back").Classes.Contains("inactive"));
                Assert.True(TitleButton(window, "nav.forward").Classes.Contains("inactive"));

                await Press(window, Key.Left, RawInputModifiers.Alt);                      // Alt+Left = Back
                Assert.IsType<HomeTabState>(Workspace(window).Active);
                Assert.Equal(tabId, Workspace(window).Active!.Id);
                Assert.True(TitleButton(window, "nav.back").Classes.Contains("inactive"));
                Assert.False(TitleButton(window, "nav.forward").Classes.Contains("inactive"));

                await Press(window, Key.Right, RawInputModifiers.Alt);                     // Alt+Right = Forward
                Assert.Equal(image, Assert.IsType<ImageTabState>(Workspace(window).Active).Path);

                await Press(window, Key.Left, RawInputModifiers.Alt);
                Assert.IsType<HomeTabState>(Workspace(window).Active);
                await Press(window, Key.Back, RawInputModifiers.Shift);                    // Shift+Backspace = Forward
                Assert.IsType<ImageTabState>(Workspace(window).Active);

                // The title-bar button runs the same command.
                await ClickTitle(window, "nav.back");
                Assert.IsType<HomeTabState>(Workspace(window).Active);
                await ClickTitle(window, "nav.forward");
                Assert.IsType<ImageTabState>(Workspace(window).Active);
            });
        }
        finally { try { Directory.Delete(folder, true); } catch { } }
    }

    [Fact]
    public async Task Default_Backspace_reaches_the_composite_Left_reaches_Previous_image_and_Alt_Left_reaches_Back_in_an_image_tab()
    {
        await OnWindow(async (window, routed) =>
        {
            window.ActionInterceptorForTests = (_, _) => true;
            Workspace(window).Reset(new TabState[] { new ImageTabState(Guid.NewGuid(), Path.Combine(Path.GetTempPath(), "x.png")) }, 0);
            ((Control)GetField(window, "ImageView")!).IsVisible = true;
            foreach (var (key, mods, expected) in new (Key, RawInputModifiers, string)[]
            {
                (Key.Back, RawInputModifiers.None, "nav.backOrPrevious"),
                (Key.Left, RawInputModifiers.None, "nav.previous"),
                (Key.Left, RawInputModifiers.Alt, "nav.back"),
                (Key.Right, RawInputModifiers.Alt, "nav.forward"),
                (Key.Back, RawInputModifiers.Shift, "nav.forward"),
            })
            {
                routed.Clear();
                await Press(window, key, mods);
                Assert.True(routed.Count > 0 && routed[0].Id == expected, $"{mods}+{key}: {string.Join(",", routed.Select(a => a.Id))}");
            }
            // A user binding moves the key: Previous image on Backspace (after freeing it from Back) works through the same router.
            Settings(window).Hotkeys["nav.backOrPrevious"] = new List<string>();
            Settings(window).Hotkeys["nav.previousBack"] = new List<string> { "Backspace" };
            routed.Clear();
            await Press(window, Key.Back, RawInputModifiers.None);
            Assert.Equal("nav.previous", routed[0].Id);
        });
    }

    [Fact]
    public async Task An_image_opened_in_a_new_tab_starts_without_history()
    {
        var folder = TempFolder("a.png");
        try
        {
            await OnWindow(async (window, _) =>
            {
                Invoke(window, "RebuildTitleActionStrip");
                await Activate(window);
                var home = Workspace(window).Active!.Id;
                await (Task)Invoke(window, "OpenAsImageTabAsync", Path.Combine(folder, "a.png"), true, false)!;
                await Settle();
                var tab = Workspace(window).Active!;
                Assert.NotEqual(home, tab.Id);
                Assert.False(History(window, tab.Id).CanGoBack);
                Assert.True(TitleButton(window, "nav.back").Classes.Contains("inactive"));
                await Press(window, Key.Left, RawInputModifiers.Alt);
                Assert.Same(tab, Workspace(window).Active);
                Assert.IsType<ImageTabState>(Workspace(window).Active);
            });
        }
        finally { try { Directory.Delete(folder, true); } catch { } }
    }

    [Fact]
    public async Task Arrow_navigation_replaces_the_image_entry_and_a_missing_image_is_skipped_with_a_message()
    {
        var folder = TempFolder("a.png", "b.png");
        try
        {
            await OnWindow(async (window, _) =>
            {
                await Activate(window);
                var tabId = Workspace(window).Active!.Id;
                await (Task)Invoke(window, "OpenAsImageTabAsync", Path.Combine(folder, "a.png"), false, false)!;
                await Settle();
                // Next / previous image (and Open in the same tab) replace the image entry: still two entries.
                await (Task)Invoke(window, "OpenAsImageTabAsync", Path.Combine(folder, "b.png"), false, false)!;
                await Settle();
                var history = History(window, tabId);
                Assert.Equal(new[] { NavigationEntryKind.Home, NavigationEntryKind.Image }, history.Entries.Select(e => e.Kind));
                Assert.Equal(Path.Combine(folder, "b.png"), history.Current!.Path);

                await window.NavigateBackAsync();
                Assert.IsType<HomeTabState>(Workspace(window).Active);
                File.Delete(Path.Combine(folder, "b.png"));
                await window.NavigateForwardAsync();
                Assert.IsType<HomeTabState>(Workspace(window).Active);                 // nothing left to go forward to
                Assert.Equal("That image no longer exists.", window.LastHistoryNotice);
                Assert.False(History(window, tabId).CanGoForward);
            });
        }
        finally { try { Directory.Delete(folder, true); } catch { } }
    }

    [Fact]
    public async Task The_history_travels_with_a_tab_removed_for_transfer()
    {
        var folder = TempFolder("a.png");
        try
        {
            await OnWindow(async (window, _) =>
            {
                await Activate(window);
                var tabId = Workspace(window).Active!.Id;
                await (Task)Invoke(window, "OpenAsImageTabAsync", Path.Combine(folder, "a.png"), false, false)!;
                await Settle();
                Workspace(window).AddHome();   // keep the window non-empty
                var moved = (TabState)Invoke(window, "RemoveTabForTransfer", tabId)!;
                Assert.NotNull(moved.History);
                var restored = NavigationHistory.FromState(moved.History);
                Assert.True(restored.CanGoBack);
                Assert.Equal(NavigationEntryKind.Image, restored.Current!.Kind);
                // A window that receives it builds the tab's history from that copy.
                Assert.True(((NavigationHistory)Invoke(window, "HistoryFor", moved)!).CanGoBack);
            });
        }
        finally { try { Directory.Delete(folder, true); } catch { } }
    }

    // ---- helpers ----------------------------------------------------------------------------------------------------

    private static async Task Activate(MainWindow window)
    {
        await (Task)Invoke(window, "ActivateWorkspaceAsync")!;
        await Settle();
    }

    private static async Task ClickTitle(MainWindow window, string id)
    {
        TitleButton(window, id).RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        await Settle();
        await Settle();
    }

    private static Button TitleButton(MainWindow window, string id) =>
        ((Dictionary<string, Button>)GetField(window, "_titleBarButtons")!)[id];

    private static NavigationHistory History(MainWindow window, Guid tabId) =>
        ((Dictionary<Guid, NavigationHistory>)GetField(window, "_tabHistories")!)[tabId];

    private static string TempFolder(params string[] images)
    {
        var folder = Path.Combine(Path.GetTempPath(), "Glide.HistoryTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        foreach (var name in images) File.WriteAllBytes(Path.Combine(folder, name), Png(24, 16));
        return Path.GetFullPath(folder);
    }

    /// <summary>A real, decodable solid-colour PNG.</summary>
    internal static byte[] Png(int width, int height)
    {
        using var raw = new MemoryStream();
        for (var y = 0; y < height; y++)
        {
            raw.WriteByte(0);
            for (var x = 0; x < width; x++) { raw.WriteByte(90); raw.WriteByte(140); raw.WriteByte(200); }
        }
        using var compressed = new MemoryStream();
        using (var z = new ZLibStream(compressed, CompressionLevel.Fastest, leaveOpen: true)) z.Write(raw.ToArray());
        using var png = new MemoryStream();
        png.Write(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A });
        void Chunk(string type, byte[] data)
        {
            var len = BitConverter.GetBytes(data.Length); Array.Reverse(len); png.Write(len);
            var typeBytes = System.Text.Encoding.ASCII.GetBytes(type);
            png.Write(typeBytes); png.Write(data);
            var crc = Crc32(typeBytes.Concat(data).ToArray());
            var crcBytes = BitConverter.GetBytes(crc); Array.Reverse(crcBytes); png.Write(crcBytes);
        }
        var header = new byte[13];
        BitConverter.GetBytes(width).Reverse().ToArray().CopyTo(header, 0);
        BitConverter.GetBytes(height).Reverse().ToArray().CopyTo(header, 4);
        header[8] = 8; header[9] = 2;
        Chunk("IHDR", header);
        Chunk("IDAT", compressed.ToArray());
        Chunk("IEND", Array.Empty<byte>());
        return png.ToArray();
    }

    private static uint Crc32(byte[] data)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var b in data)
        {
            crc ^= b;
            for (var k = 0; k < 8; k++) crc = (crc & 1) != 0 ? 0xEDB88320u ^ (crc >> 1) : crc >> 1;
        }
        return ~crc;
    }

    private static async Task Press(MainWindow window, Key key, RawInputModifiers modifiers)
    {
        window.KeyPress(key, modifiers, PhysicalKey.None, null);
        await Settle();
        window.KeyRelease(key, modifiers, PhysicalKey.None, null);
        await Settle();
        await Settle();
    }

    private static async Task Settle()
    {
        for (var i = 0; i < 6; i++) { Dispatcher.UIThread.RunJobs(); await Task.Delay(8); }
    }

    private static void WithIsolatedStore(Action action)
    {
        var root = Path.Combine(Path.GetTempPath(), "Glide.Settings.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var previous = SettingsStore.SettingsDirectoryOverrideForTests.Value;
        SettingsStore.SettingsDirectoryOverrideForTests.Value = root;
        try { action(); }
        finally
        {
            SettingsStore.SettingsDirectoryOverrideForTests.Value = previous;
            try { Directory.Delete(root, true); } catch { }
        }
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
    private static WorkspaceState Workspace(MainWindow window) => (WorkspaceState)GetField(window, "_workspace")!;

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
