using System.IO.Compression;
using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.Animation;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Glide.App;
using Glide.App.Controls;
using Glide.App.Explorer;
using Glide.App.Platform;
using Glide.App.Settings;
using Glide.Core.Workspace;

namespace Glide.Explorer.Tests;

/// <summary>
/// Builds the real MainWindow with an Explorer tab over a temp folder tree and drives it with the
/// headless input helpers. Everything that could touch the machine (settings file, clipboard,
/// Recycle Bin, dialogs, explorer.exe, shell properties, known folders) is replaced by a seam.
/// </summary>
internal sealed class ExplorerHarness : IDisposable
{
    public MainWindow Window { get; }
    public string Root { get; }
    public string SettingsRoot { get; }
    public MemoryExplorerFileClipboard Clipboard { get; } = new();
    public List<(string Title, string Message, string Accept)> Confirmations { get; } = new();
    public bool ConfirmAnswer { get; set; } = true;
    public List<(string Title, string Message)> Errors { get; } = new();
    public List<(string Path, bool Directory)> Revealed { get; } = new();
    public List<string> OpenWithCalls { get; } = new();
    public List<string> ShellOpened { get; } = new();
    public List<string> Properties { get; } = new();
    public List<(string Path, bool Directory)> Recycled { get; } = new();

    private readonly string? _previousSettingsOverride;
    private readonly Func<string, bool, bool>? _previousRecycler;
    private readonly IExplorerFileClipboard _previousClipboard;
    private readonly Func<string, bool>? _previousProperties;

    public const string ImageA = "a.png";
    public const string ImageB = "b.png";
    public const string ImageC = "c.png";

    private ExplorerHarness(Action<string>? populate, bool theme)
    {
        _previousSettingsOverride = SettingsStore.SettingsDirectoryOverrideForTests.Value;
        SettingsRoot = Path.Combine(Path.GetTempPath(), "Glide.ExplorerTab.Settings", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(SettingsRoot);
        SettingsStore.SettingsDirectoryOverrideForTests.Value = SettingsRoot;
        Root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "Glide.ExplorerTab", Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(Root);
        (populate ?? DefaultTree)(Root);

        if (theme) EnsureThemes();

        _previousClipboard = ExplorerFileClipboard.Current;
        ExplorerFileClipboard.Current = Clipboard;
        _previousRecycler = null;
        ExplorerFileOperations.Recycler = (path, directory) =>
        {
            Recycled.Add((path, directory));
            if (directory) Directory.Delete(path, true); else File.Delete(path);
            return true;
        };
        _previousProperties = WindowsShellProperties.Launcher;
        WindowsShellProperties.Launcher = path => { Properties.Add(path); return true; };
        ExplorerHost.Confirm = (title, message, accept) => { Confirmations.Add((title, message, accept)); return Task.FromResult(ConfirmAnswer); };
        ExplorerHost.ShowError = (title, message) => { Errors.Add((title, message)); return Task.CompletedTask; };
        ExplorerHost.RevealInWindows = (path, directory) => Revealed.Add((path, directory));
        ExplorerHost.OpenWith = path => OpenWithCalls.Add(path);
        ExplorerHost.OpenWithShell = path => ShellOpened.Add(path);
        ExplorerHost.LastCopiedText = null;
        ExplorerLocations.QuickAccessOverride = () => new[]
        {
            new ExplorerLocation("Desktop", Path.Combine(Root, "Alpha"), "Desktop"),
            new ExplorerLocation("Pictures", Path.Combine(Root, "Beta"), "Image")
        };
        ExplorerLocations.DrivesOverride = () => new[] { new ExplorerLocation("Test drive (T:)", Path.Combine(Root, "Gamma"), "Drive") };

        Window = new MainWindow { Width = 1200, Height = 800 };
        Set("_settings", new GlideSettingsState());
    }

    /// <summary>
    /// Runs the body on the headless UI thread and awaits it COMPLETELY. (AvaloniaHeadlessFixture.RunOnUIThreadAsync
    /// binds to Dispatch&lt;Task&gt; and only waits for the first synchronous segment of an async body.)
    /// </summary>
    public static async Task Run(Func<ExplorerHarness, Task> body, Action<string>? populate = null, bool theme = true, bool startExplorer = true)
    {
                var session = HeadlessUnitTestSession.GetOrStartForAssembly(typeof(ExplorerTestApplication).Assembly);
        // Clicks within the double-click time of the previous test's last click would be counted as double-clicks.
        var wait = 560 - (Environment.TickCount64 - s_lastTestEndTick);
        if (wait > 0) await Task.Delay((int)wait);
        // Safety net: an intermittent stall of the app-owned MainWindow constructor on the headless platform (seen roughly
        // once in a dozen runs) would otherwise hang the whole test run. Turn it into a prompt, explicit crash instead.
        using var watchdog = new Timer(_ => Environment.FailFast("Explorer headless test did not finish within 150 s (stalled UI thread)."),
            null, TimeSpan.FromSeconds(150), Timeout.InfiniteTimeSpan);
        await session.Dispatch<object?>(async () =>
        {
            // Continuations after awaits must come back to the UI thread, as they do in the real app.
            AvaloniaSynchronizationContext.InstallIfNeeded();
            // The tooltip guard's wheel quiet period (600 ms, static) is longer than the pause above (560 ms): a previous test's
            // last wheel tick used to cancel the first tooltip of this one (HoverToolTipTests, intermittently in a full run).
            Glide.App.Services.ToolTipGuard.ResetWheelQuietPeriodForTests();
            var harness = new ExplorerHarness(populate, theme);
            try
            {
                harness.Window.Show();
                // Let the window finish activating (a queued job restores keyboard focus) before any input is simulated.
                for (var i = 0; i < 20 && !harness.Window.IsActive; i++) { harness.Pump(); await Task.Delay(25); }
                // The headless platform runs its queued activation job (which restores keyboard focus) with the first input.
                harness.Window.MouseMove(new Point(2, 2));
                harness.Window.KeyPress(Avalonia.Input.Key.F24, RawInputModifiers.None);
                harness.Window.KeyRelease(Avalonia.Input.Key.F24, RawInputModifiers.None);
                harness.Pump();
                if (startExplorer) await harness.OpenExplorerAsync(harness.Root);
                await body(harness);
            }
            finally { harness.Dispose(); }
            return null;
        }, CancellationToken.None);
    }

    // ------------------------------------------------------------------------------------------
    // Tree and files
    // ------------------------------------------------------------------------------------------

    public static void DefaultTree(string root)
    {
        Directory.CreateDirectory(Path.Combine(root, "Alpha", "Deep"));
        Directory.CreateDirectory(Path.Combine(root, "Beta"));
        Directory.CreateDirectory(Path.Combine(root, "Gamma"));
        File.WriteAllBytes(Path.Combine(root, "Alpha", "inner.png"), Png(24, 16, 200, 60, 60));
        File.WriteAllBytes(Path.Combine(root, "Alpha", "Deep", "deep.png"), Png(24, 16, 60, 200, 60));
        File.WriteAllBytes(Path.Combine(root, ImageA), Png(48, 32, 220, 40, 40));
        File.WriteAllBytes(Path.Combine(root, ImageB), Png(48, 32, 40, 220, 40));
        File.WriteAllBytes(Path.Combine(root, ImageC), Png(48, 32, 40, 40, 220));
        File.WriteAllText(Path.Combine(root, "notes.txt"), "hello");
        var hidden = Path.Combine(root, "secret.png");
        File.WriteAllBytes(hidden, Png(8, 8, 1, 2, 3));
        File.SetAttributes(hidden, FileAttributes.Hidden);
        var ini = Path.Combine(root, "desktop.ini");
        File.WriteAllText(ini, "[.ShellClassInfo]");
        File.SetAttributes(ini, FileAttributes.Hidden | FileAttributes.System);
    }

    public static void ManyFilesTree(string root)
    {
        Directory.CreateDirectory(Path.Combine(root, "Sub"));
        var png = Png(16, 16, 10, 120, 200);
        for (var i = 0; i < 300; i++) File.WriteAllBytes(Path.Combine(root, $"img{i:D3}.png"), png);
    }

    /// <summary>A real, decodable PNG of one solid colour.</summary>
    public static byte[] Png(int width, int height, byte r, byte g, byte b) => Png(width, height, (_, _) => (r, g, b));

    /// <summary>A real, decodable PNG whose pixels come from <paramref name="pixel"/> (x, y).</summary>
    public static byte[] Png(int width, int height, Func<int, int, (byte R, byte G, byte B)> pixel)
    {
        using var raw = new MemoryStream();
        for (var y = 0; y < height; y++)
        {
            raw.WriteByte(0);
            for (var x = 0; x < width; x++) { var (r, g, b) = pixel(x, y); raw.WriteByte(r); raw.WriteByte(g); raw.WriteByte(b); }
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

    // ------------------------------------------------------------------------------------------
    // Themes (FluentTheme + Glide styles) so controls have templates, like the real app
    // ------------------------------------------------------------------------------------------

    // The shared window and the themes live for the whole Explorer test collection: creating a MainWindow per
    // test made the (app-owned) window construction hang now and then when dozens were built back to back.
    private static long s_lastTestEndTick;
    private static MainWindow? s_window;
    private static readonly List<IStyle> s_styles = new();

    private static void EnsureThemes()
    {
        RemoveThemes();
        var app = Application.Current!;
        var fluent = new FluentTheme();
        var uri = new Uri("avares://Glide/Styles/GlideTheme.axaml");
        var glide = new Avalonia.Markup.Xaml.Styling.StyleInclude(uri) { Source = uri };
        // Transitions subscribe to the compositor clock, which the headless test platform ties to a different thread
        // than the test's UI thread ("Call from invalid thread"). Tests do not need animations.
        app.Styles.Add(fluent);
        app.Styles.Add(glide);
        s_styles.Add(fluent);
        s_styles.Add(glide);

        app.RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Dark;
    }

    // ONE MainWindow serves the whole run: constructing dozens of windows back to back made the (app-owned) constructor
    // hang now and then, and a clean reset between tests is cheap.
    private static MainWindow SharedWindow() => s_window ??= new MainWindow { Width = 1200, Height = 800 };

    private static void RemoveThemes()
    {
        foreach (var style in s_styles) Application.Current?.Styles.Remove(style);
        s_styles.Clear();
    }

    /// <summary>Called by the collection fixture when the Explorer tests are done.</summary>
    public static void Shutdown()
    {
                var session = HeadlessUnitTestSession.GetOrStartForAssembly(typeof(ExplorerTestApplication).Assembly);
        session.Dispatch<object?>(() =>
        {
            try { s_window?.Close(); } catch { }
            s_window = null;
            RemoveThemes();
            return null!;
        }, CancellationToken.None).GetAwaiter().GetResult();
    }

    /// <summary>Puts the shared window back into a clean "just started" state with fresh settings.</summary>
    private void ResetWindow()
    {
        Set("_settings", new GlideSettingsState());
        Window.TabHistories.Clear();
        Call("CloseActiveOwnedMenu", new object?[] { null });
        Get<Dictionary<Guid, string>>("_browserHighlightTargets").Clear();
        ((System.Collections.IDictionary)Get<object>("_explorerTabViews")).Clear();
        SelectionSet.Clear();
        Get<HashSet<string>>("_explorerCutPaths").Clear();
        Set("_explorerLoadedFolder", null);
        Set("_explorerDisplayedTabId", Guid.Empty);
        Set("_explorerFilterText", "");
        Set("_explorerPendingSelection", null);
        Set("_explorerPendingRename", null);
        Set("_explorerRenamePath", null);
        Set("_explorerSortKey", ExplorerSortKey.Name);
        Set("_explorerSortAscending", true);
        Set("_explorerFoldersFirst", true);
        Set("_browserCurrentIndex", -1);
        Set("_browserSelectionAnchor", -1);
        Set("_browserViewMode", ExplorerViewModes.Large);
        if (Get<object?>("_browserSurface") is BrowserSurface surface)
        {
            surface.AddressBar.CancelEdit();
            surface.FilterBox.Text = "";
            surface.ViewModeCombo.SelectedIndex = ExplorerViewModes.Large;
        }
        Workspace.Reset(new TabState[] { new HomeTabState(Guid.NewGuid()) }, 0);
        Call("RebuildTabStrip");
        Window.Width = 1200;
        Window.Height = 800;
        Window.WindowState = WindowState.Normal;
        Pump();
    }

    // ------------------------------------------------------------------------------------------
    // Reflection access to the window's private state
    // ------------------------------------------------------------------------------------------

    public T Get<T>(string name)
    {
        var f = typeof(MainWindow).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance);
        if (f is not null) return (T)f.GetValue(Window)!;
        var p = typeof(MainWindow).GetProperty(name, BindingFlags.NonPublic | BindingFlags.Instance) ?? throw new MissingMemberException(name);
        return (T)p.GetValue(Window)!;
    }

    public void Set(string name, object? value) =>
        typeof(MainWindow).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(Window, value);

    public object? Call(string name, params object?[] args)
    {
        var m = typeof(MainWindow).GetMethods(BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance)
            .First(x => x.Name == name && x.GetParameters().Length == args.Length);
        return m.Invoke(Window, args);
    }

    public WorkspaceState Workspace => Get<WorkspaceState>("_workspace");
    public GlideSettingsState Settings => Get<GlideSettingsState>("_settings");
    public BrowserSurface Surface => Get<BrowserSurface>("_browserSurface");
    public ContentControl BrowserView => Window.FindControl<ContentControl>("BrowserView")!;
    public HashSet<string> SelectionSet => Get<HashSet<string>>("_browserSelection");
    public int CurrentIndex => Get<int>("_browserCurrentIndex");
    public IReadOnlyList<string> Names => ((System.Collections.IEnumerable)Get<object>("_browserEntries")).Cast<ExplorerEntry>().Select(e => e.Name).ToList();
    public IReadOnlyList<ExplorerEntry> Entries => ((System.Collections.IEnumerable)Get<object>("_browserEntries")).Cast<ExplorerEntry>().ToList();
    public IReadOnlyList<string> SelectedNames => SelectionSet.Select(Path.GetFileName).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList()!;
    public string CurrentFolder => ((BrowserTabState)Workspace.Active!).Folder;
    public Task RefreshTask => Get<Task>("_explorerRefreshTask");
    public ContextMenu? ActiveMenu => Get<ContextMenu?>("_activeOwnedMenu");

    // ------------------------------------------------------------------------------------------
    // Driving
    // ------------------------------------------------------------------------------------------

    public void Pump()
    {
        // MinValue: also run the low-priority jobs (window activation, thumbnails) that a plain RunJobs() skips.
        Dispatcher.UIThread.RunJobs(DispatcherPriority.SystemIdle);
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs(DispatcherPriority.SystemIdle);
    }

    /// <summary>Pumps the UI and polls (shared <see cref="Wait"/> helper); fails the test naming <paramref name="what"/>.</summary>
    public async Task WaitUntil(Func<bool> condition, string what = "condition", int timeoutMs = 8000)
    {
        if (await Wait.TryUntilAsync(condition, timeoutMs, Pump, pollMs: 15)) return;
        Pump();
        Assert.True(condition(), $"Timed out waiting for: {what}");
    }

    /// <summary>Lets the pending refresh/relayout settle and the window lay out.</summary>
    public async Task SettleAsync()
    {
        await RefreshTask;
        Pump();
        for (var i = 0; i < 40; i++)
        {
            await Task.Delay(15);
            Pump();
            Window.UpdateLayout();
            Pump();
            // The list is settled once every entry that fits has a realized tile.
            if (Names.Count == 0 || Tile(Names[0]) is not null) break;
        }
    }

    public async Task OpenExplorerAsync(string folder)
    {
        Workspace.AddBrowser(folder);
        Call("RebuildTabStrip");
        var t = (Task)Call("ActivateWorkspaceAsync")!;
        await t;
        await SettleAsync();
    }

    public Border? Tile(string name)
    {
        var entry = Entries.FirstOrDefault(e => string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase));
        return entry is null ? null
            : Surface.Repeater.Children.OfType<Border>().FirstOrDefault(t => ReferenceEquals(t.Tag, entry));
    }

    public Point TileCenter(string name, double dx = 0)
    {
        var tile = Tile(name) ?? throw new InvalidOperationException($"Tile '{name}' is not realized. Names: {string.Join(", ", Names)}");
        return tile.TranslatePoint(new Point(tile.Bounds.Width / 2 + dx, tile.Bounds.Height / 2), Window) ?? throw new InvalidOperationException("tile has no window position");
    }

    // Successive clicks at the very same spot within the double-click time count as a double-click. Tests that mean
    // separate clicks therefore alternate the click position a little (like a real hand does).
    private int _clickSerial;
    private double NextJitter() => (_clickSerial++ % 2 == 0) ? -14 : 14;

    public Point EmptySpotInList()
    {
        // Bottom-right area of the scroll viewer, well away from any tile.
        var scroll = Surface.ScrollViewer;
        return scroll.TranslatePoint(new Point(scroll.Bounds.Width - 40, scroll.Bounds.Height - 30), Window)!.Value;
    }

    public void Click(Point point, MouseButton button = MouseButton.Left, RawInputModifiers modifiers = RawInputModifiers.None)
    {
        Window.MouseMove(point, modifiers);
        Window.MouseDown(point, button, modifiers);
        Window.MouseUp(point, button, modifiers);
        Pump();
    }

    public void Click(string name, MouseButton button = MouseButton.Left, RawInputModifiers modifiers = RawInputModifiers.None) =>
        Click(TileCenter(name, NextJitter()), button, modifiers);

    public void DoubleClick(string name)
    {
        var point = TileCenter(name, NextJitter());
        Window.MouseMove(point);
        Window.MouseDown(point, MouseButton.Left);
        Window.MouseUp(point, MouseButton.Left);
        Window.MouseDown(point, MouseButton.Left);
        Window.MouseUp(point, MouseButton.Left);
        Pump();
    }

    public void Key(Key key, RawInputModifiers modifiers = RawInputModifiers.None)
    {
        Window.KeyPress(key, modifiers);
        Window.KeyRelease(key, modifiers);
        Pump();
    }

    public void Type(string text)
    {
        Window.KeyTextInput(text);
        Pump();
    }

    public void ClickButton(Button button)
    {
        var point = button.TranslatePoint(new Point(button.Bounds.Width / 2, button.Bounds.Height / 2), Window)!.Value;
        Click(point);
    }

    public async Task NavigateAsync(string folder)
    {
        Call("NavigateBrowserTo", folder, true, null);
        await SettleAsync();
    }

    /// <summary>Writes the rendered window to PNG when GLIDE_EXPLORER_RENDER_DIR is set (visual review aid).</summary>
    public string? Render(string name)
    {
        var dir = Environment.GetEnvironmentVariable("GLIDE_EXPLORER_RENDER_DIR");
        if (string.IsNullOrWhiteSpace(dir)) return null;
        Directory.CreateDirectory(dir);
        Pump();
        Window.UpdateLayout();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        var frame = Window.CaptureRenderedFrame();
        if (frame is null) return null;
        var path = Path.Combine(dir, name + ".png");
        frame.Save(path);
        return path;
    }

    public void FocusList()
    {
        Surface.ScrollViewer.Focus();
        Pump();
    }

    public void Dispose()
    {
        s_lastTestEndTick = Environment.TickCount64;
        try { Window.Close(); } catch { }
        RemoveThemes();
        ExplorerFileClipboard.Current = _previousClipboard;
        ExplorerFileOperations.Recycler = null!;
        WindowsShellProperties.Launcher = _previousProperties;
        ExplorerHost.Confirm = null; ExplorerHost.ShowError = null; ExplorerHost.RevealInWindows = null;
        ExplorerHost.OpenWith = null; ExplorerHost.OpenWithShell = null;
        ExplorerLocations.QuickAccessOverride = null; ExplorerLocations.DrivesOverride = null;
        SettingsStore.SettingsDirectoryOverrideForTests.Value = _previousSettingsOverride;
        TempDir.TryDelete(Root);
        TempDir.TryDelete(SettingsRoot);
    }
}

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ExplorerHeadlessCollection : ICollectionFixture<ExplorerHeadlessFixture>
{
    public const string Name = "Explorer headless";
}

public sealed class ExplorerHeadlessFixture : IDisposable
{
    public void Dispose() { try { ExplorerHarness.Shutdown(); } catch { } }
}
