using System.Diagnostics;
using System.Reflection;
using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Glide.App;
using Glide.App.Controls;
using Glide.App.Settings;
using Glide.Core.Workspace;
using Glide.Imaging;

namespace Glide.PerfBench;

public sealed class BenchApp : Application
{
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<BenchApp>()
            .UseSkia()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
}

/// <summary>
/// UI snappiness on Avalonia's headless platform with the real Fluent + Glide theme (CPU Skia raster,
/// no GPU, no DWM). Times are wall-clock on the UI thread including layout and one forced render.
/// They are comparable before/after; absolute values differ from a desktop session.
/// </summary>
internal static class UiBench
{
    private static void Pump()
    {
        Dispatcher.UIThread.RunJobs(DispatcherPriority.SystemIdle);
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs(DispatcherPriority.SystemIdle);
    }

    private static void EnsureThemes()
    {
        var app = Application.Current!;
        if (app.Styles.Count > 0) return;
        app.Styles.Add(new FluentTheme());
        var uri = new Uri("avares://Glide/Styles/GlideTheme.axaml");
        app.Styles.Add(new Avalonia.Markup.Xaml.Styling.StyleInclude(uri) { Source = uri });
        app.RequestedThemeVariant = ThemeVariant.Dark;
    }

    private static T Get<T>(object target, string name)
    {
        var type = target.GetType();
        var f = type.GetField(name, BindingFlags.NonPublic | BindingFlags.Instance);
        if (f is not null) return (T)f.GetValue(target)!;
        var p = type.GetProperty(name, BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance) ?? throw new MissingMemberException(name);
        return (T)p.GetValue(target)!;
    }

    private static void Set(object target, string name, object? value) =>
        target.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(target, value);

    private static object? Call(object target, string name, params object?[] args)
    {
        var m = target.GetType().GetMethods(BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance)
            .First(x => x.Name == name && x.GetParameters().Length == args.Length);
        return m.Invoke(target, args);
    }

    private static async Task WaitUntil(Func<bool> condition, int timeoutMs = 20000, string what = "condition", Func<string>? state = null)
    {
        var start = Environment.TickCount64;
        while (Environment.TickCount64 - start < timeoutMs)
        {
            Pump();
            if (condition()) return;
            await Task.Delay(2);
        }
        throw new TimeoutException("UI condition not reached: " + what + " " + state?.Invoke());
    }

    public static int Run(string[] args)
    {
        var data = Args.Get(args, "--data") ?? throw new ArgumentException("--data required");
        var outDir = Args.Get(args, "--out") ?? Path.Combine(data, "..", "perf-results");
        var label = Args.Get(args, "--label") ?? "run";
        var repeats = Args.GetInt(args, "--repeats", 7);
        var settingsDir = Path.Combine(outDir, "isolated-settings-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(settingsDir);
        Environment.SetEnvironmentVariable("GLIDE_SETTINGS_DIRECTORY", settingsDir);
        SetSettingsOverride(settingsDir);

        var tracePath = Args.Get(args, "--trace");
        if (tracePath is not null) Glide.Core.GlidePerformanceTrace.Configure(tracePath);
        var session = HeadlessUnitTestSession.StartNew(typeof(BenchApp));
        using var watchdog = new Timer(_ => Environment.FailFast("UI bench stalled for 10 minutes."), null, TimeSpan.FromMinutes(10), Timeout.InfiniteTimeSpan);
        var report = new Report("ui");
        var results = new JsonObject();
        session.Dispatch<object?>(async () =>
        {
            AvaloniaSynchronizationContext.InstallIfNeeded();
            SetSettingsOverride(settingsDir);
            EnsureThemes();
            await RunUiAsync(data, repeats, results).ConfigureAwait(true);
            return null;
        }, CancellationToken.None).GetAwaiter().GetResult();

        var md = report.Markdown;
        md.AppendLine($"## UI snappiness ({label}) - headless Avalonia, first = cold (JIT), warm = median of later runs");
        md.AppendLine();
        md.AppendLine("| Metric | First ms | Warm median ms | Warm p95 ms |");
        md.AppendLine("|---|---:|---:|---:|");
        foreach (var (key, value) in results)
        {
            var arr = value!.AsArray().Select(v => v!.GetValue<double>()).ToArray();
            var warm = arr.Skip(1).ToArray();
            md.AppendLine($"| {key} | {Stats.Ms(arr.FirstOrDefault(double.NaN))} | {Stats.Ms(Stats.Median(warm.Length > 0 ? warm : arr))} | {Stats.Ms(Stats.Percentile(warm.Length > 0 ? warm : arr, 0.95))} |");
        }
        if (tracePath is not null) Glide.Core.GlidePerformanceTrace.Flush();
        report.Root["results"] = results;
        report.Save(outDir, $"ui-{label}");
        try { Directory.Delete(settingsDir, true); } catch { }
        return 0;
    }

    private static void SetSettingsOverride(string dir)
    {
        // Internal test seam (AsyncLocal/ThreadLocal) - set via reflection; GLIDE_SETTINGS_DIRECTORY is also set.
        var member = typeof(SettingsStore).GetField("SettingsDirectoryOverrideForTests", BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Public)
            ?.GetValue(null);
        member?.GetType().GetProperty("Value")?.SetValue(member, dir);
    }

    private static string ThumbState(BrowserSurface surface)
    {
        var images = surface.Repeater.Children.SelectMany(c => c.GetVisualDescendants().OfType<Image>()).ToArray();
        var missing = images.Where(i => i.Source is null).Take(5).Select(i => Path.GetFileName(i.Tag as string) + ":" + (surface.Repeater.GetElementIndex(i.FindAncestorOfType<Border>(true) is { } b && b.Parent is ItemsRepeater ? b : (Control)i)));
        var realizedIndices = surface.Repeater.Children.Select(ch => surface.Repeater.GetElementIndex(ch)).OrderBy(x => x).ToArray();
        return $"children={surface.Repeater.Children.Count} images={images.Length} tagged={images.Count(i => i.Tag is string)} loaded={images.Count(i => i.Source is not null)} visible={images.Count(i => i.IsEffectivelyVisible)} missing=[{string.Join(",", missing)}] indices={string.Join(",", realizedIndices)}";
    }

    private static void Add(JsonObject results, string key, double ms)
    {
        if (results[key] is not JsonArray arr) results[key] = arr = new JsonArray();
        arr.Add(ms);
    }

    private static async Task RunUiAsync(string data, int repeats, JsonObject results)
    {
        // --- MainWindow construction (cold path for every new window / tab tear-off) ---
        MainWindow? window = null;
        for (var i = 0; i < Math.Min(repeats, 4); i++)
        {
            GC.Collect(); GC.WaitForPendingFinalizers();
            var t = Stopwatch.GetTimestamp();
            var w = new MainWindow { Width = 1600, Height = 1000 };
            Add(results, "MainWindow ctor", Timing.Ms(t));
            t = Stopwatch.GetTimestamp();
            w.Show();
            Pump();
            w.UpdateLayout();
            Pump();
            Add(results, "MainWindow show + first layout/render", Timing.Ms(t));
            if (window is null) window = w; else w.Close();
            Pump();
        }
        window!.Activate();
        Pump();
        Set(window, "_settings", new GlideSettingsState());

        // --- Settings window ---
        for (var i = 0; i < repeats; i++)
        {
            GC.Collect(); GC.WaitForPendingFinalizers();
            var t = Stopwatch.GetTimestamp();
            var settings = new SettingsWindow(new GlideSettingsState(), (_, _) => { });
            Add(results, "Settings ctor", Timing.Ms(t));
            var t2 = Stopwatch.GetTimestamp();
            settings.Show();
            Pump();
            settings.UpdateLayout();
            Pump();
            Add(results, "Settings show + first layout/render", Timing.Ms(t2));
            Add(results, "Settings open total", Timing.Ms(t));
            // Page switch inside Settings (Performance page, then Mouse & keyboard with the hotkey list).
            foreach (var page in new[] { "Performance", "Mouse" })
            {
                var tp = Stopwatch.GetTimestamp();
                Call(settings, "ShowCategory", page, true, false, null);
                Pump(); settings.UpdateLayout(); Pump();
                Add(results, $"Settings page switch -> {page}", Timing.Ms(tp));
            }
            settings.Close();
            Pump();
        }

        // --- Viewer context menu (rebuilt on every right click) + first sub-menu ---
        var viewport = Get<ImageViewport>(window, "Viewport");
        var image = Path.Combine(data, "single", "jpeg-base-4k.jpg");
        if (File.Exists(image)) viewport.SwapPresentedFrame(new Bitmap(image), 3840, 2160, 1, image);
        Set(window, "_currentPath", image);
        for (var i = 0; i < repeats; i++)
        {
            var t = Stopwatch.GetTimestamp();
            var menu = (ContextMenu)Call(window, "BuildViewerContextMenu")!;
            Add(results, "Context menu build", Timing.Ms(t));
            var t2 = Stopwatch.GetTimestamp();
            menu.Open(viewport);
            Pump();
            Add(results, "Context menu open (popup laid out)", Timing.Ms(t2));
            var sub = menu.Items.OfType<MenuItem>().FirstOrDefault(m => m.ItemCount > 0);
            if (sub is not null)
            {
                var t3 = Stopwatch.GetTimestamp();
                sub.IsSubMenuOpen = true;
                Pump();
                Add(results, "Sub-menu open", Timing.Ms(t3));
                sub.IsSubMenuOpen = false;
            }
            menu.Close();
            Pump();
            Add(results, "Context menu total (build + open + sub-menu)", Timing.Ms(t));
        }

        // --- Explorer tab over a 5000-file folder, then tab switches ---
        var big = Path.Combine(data, "folders", "folder-5000-small");
        var mid = Path.Combine(data, "folders", "folder-500-4k");
        if (Directory.Exists(big) && Directory.Exists(mid))
        {
            var workspace = Get<WorkspaceState>(window, "_workspace");
            for (var i = 0; i < Math.Min(repeats, 4); i++)
            {
                workspace.Reset(new TabState[] { new HomeTabState(Guid.NewGuid()) }, 0);
                Call(window, "RebuildTabStrip");
                Pump();
                GC.Collect(); GC.WaitForPendingFinalizers();
                var t = Stopwatch.GetTimestamp();
                workspace.AddBrowser(big);
                Call(window, "RebuildTabStrip");
                await ((Task)Call(window, "ActivateWorkspaceAsync")!).ConfigureAwait(true);
                var refresh = Get<Task?>(window, "_explorerRefreshTask");
                if (refresh is not null) await refresh.ConfigureAwait(true);
                var surface = Get<BrowserSurface>(window, "_browserSurface");
                await WaitUntil(() => surface.Repeater.Children.Count > 0, 20000, "tiles realized").ConfigureAwait(true);
                Add(results, "Explorer tab open 5000 files (listing + first tiles)", Timing.Ms(t));
                await WaitUntil(() =>
                {
                    var images = surface.Repeater.Children.Where(ch => surface.Repeater.GetElementIndex(ch) >= 0).SelectMany(c => c.GetVisualDescendants().OfType<Image>()).Where(im => im.Tag is string).ToArray();
                    return images.Length > 0 && images.All(im => im.Source is not null);
                }, 60000, "thumbnails", () => ThumbState(surface)).ConfigureAwait(true);
                Add(results, "Explorer 5000 files: visible thumbnails all loaded", Timing.Ms(t));

                // Scroll to the middle and time until those thumbnails are in.
                var ts = Stopwatch.GetTimestamp();
                surface.ScrollViewer.Offset = new Vector(0, surface.ScrollViewer.Extent.Height / 2);
                Pump();
                await WaitUntil(() =>
                {
                    var images = surface.Repeater.Children.Where(ch => surface.Repeater.GetElementIndex(ch) >= 0).SelectMany(c => c.GetVisualDescendants().OfType<Image>()).Where(im => im.Tag is string).ToArray();
                    return images.Length > 0 && images.All(im => im.Source is not null);
                }, 60000, "scrolled thumbnails", () => ThumbState(surface)).ConfigureAwait(true);
                Add(results, "Explorer 5000 files: jump-scroll to middle, thumbnails loaded", Timing.Ms(ts));

                workspace.AddBrowser(mid);
                Call(window, "RebuildTabStrip");
                await ((Task)Call(window, "ActivateWorkspaceAsync")!).ConfigureAwait(true);
                Pump();
                for (var s = 0; s < 4; s++)
                {
                    var target = s % 2 == 0 ? 1 : 2;
                    var tt = Stopwatch.GetTimestamp();
                    workspace.Select(workspace.Tabs[target].Id);
                    Call(window, "RebuildTabStrip");
                    await ((Task)Call(window, "ActivateWorkspaceAsync")!).ConfigureAwait(true);
                    var r = Get<Task?>(window, "_explorerRefreshTask");
                    if (r is not null) await r.ConfigureAwait(true);
                    Pump();
                    Add(results, "Tab switch between Explorer tabs (5000 <-> 500 files)", Timing.Ms(tt));
                }
            }
        }
        window.Close();
        Pump();
    }
}
