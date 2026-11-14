using System.Diagnostics;
using System.Reflection;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Glide.App.Printing;

namespace Glide.Core.Tests;

/// <summary>
/// Opt-in timing harness for the Print window (set GLIDE_PRINT_PERF=1, optional GLIDE_PRINT_PERF_OUT=file and
/// GLIDE_PRINT_PERF_IMAGES=dir). It only QUERIES the installed printers (names, capabilities, paper geometry) and
/// renders the preview headless; it never starts a print job. Without the variable every test returns at once.
/// </summary>
[Collection(AvaloniaHeadlessCollection.Name)]
public sealed class PrintPreviewPerfHarness
{
    public PrintPreviewPerfHarness(AvaloniaHeadlessFixture fixture) => _ = fixture;

    private static bool Enabled => Environment.GetEnvironmentVariable("GLIDE_PRINT_PERF") == "1";

    private static readonly StringBuilder Log = new();

    private static void Write(string line)
    {
        lock (Log) Log.AppendLine(line);
        var output = Environment.GetEnvironmentVariable("GLIDE_PRINT_PERF_OUT");
        if (!string.IsNullOrWhiteSpace(output))
        {
            try { File.AppendAllText(output, line + Environment.NewLine); } catch { }
        }
    }

    private static IEnumerable<(string Label, string Path)> Images()
    {
        var dir = Environment.GetEnvironmentVariable("GLIDE_PRINT_PERF_IMAGES") ?? "";
        foreach (var (label, name) in new[] { ("4K", "jpeg-base-4k.jpg"), ("12MP", "jpeg-base-12mp.jpg"), ("50MP", "jpeg-base-50mp.jpg") })
        {
            var path = Path.Combine(dir, name);
            if (File.Exists(path)) yield return (label, path);
        }
    }

    [Fact]
    public void Driver_query_costs()
    {
        if (!Enabled || !OperatingSystem.IsWindows()) return;
        var sw = Stopwatch.StartNew();
        var names = PrintService.GetPrinterNames();
        Write($"driver GetPrinterNames {sw.Elapsed.TotalMilliseconds:F1} ms ({names.Count} printers)");
        for (var i = 0; i < 2; i++)
        {
            sw.Restart();
            var def = PrintService.GetDefaultPrinterName();
            Write($"driver GetDefaultPrinterName#{i} {sw.Elapsed.TotalMilliseconds:F1} ms ({def})");
        }
        foreach (var printer in names)
        {
            for (var i = 0; i < 2; i++)
            {
                try
                {
                    sw.Restart();
                    var caps = PrintService.GetCapabilities(printer);
                    var capsMs = sw.Elapsed.TotalMilliseconds;
                    sw.Restart();
                    var geo = PrintService.GetPaperGeometry(printer, caps.DefaultPaperName, caps.DefaultSourceName, landscape: false);
                    var geoMs = sw.Elapsed.TotalMilliseconds;
                    sw.Restart();
                    _ = PrintService.GetPaperGeometry(printer, caps.DefaultPaperName, caps.DefaultSourceName, landscape: true);
                    var geoLandMs = sw.Elapsed.TotalMilliseconds;
                    Write($"driver '{printer}'#{i} GetCapabilities {capsMs:F1} ms ({caps.Papers.Count} papers), GetPaperGeometry {geoMs:F1} ms, landscape {geoLandMs:F1} ms ({geo.PaperWidthHundredths}x{geo.PaperHeightHundredths})");
                }
                catch (Exception ex) { Write($"driver '{printer}' failed: {ex.Message}"); }
            }
        }
    }

    private static Bitmap LoadFull(string path)
    {
        using var stream = File.OpenRead(path);
        return new Bitmap(stream);
    }

    [Fact]
    public async Task Preview_render_costs()
    {
        if (!Enabled) return;
        foreach (var (label, path) in Images())
        {
            await AvaloniaHeadlessFixture.RunOnUIThreadAsync(() =>
            {
                var sw = Stopwatch.StartNew();
                using var bitmap = LoadFull(path);
                Write($"render {label} decode {sw.Elapsed.TotalMilliseconds:F0} ms ({bitmap.PixelSize.Width}x{bitmap.PixelSize.Height})");
                var paper = new PrintPaperGeometry(827, 1169, 25, 25, 777, 1119);
                var control = new PrintPreviewControl { Width = 560, Height = 600, Paper = paper, PreviewBitmap = bitmap };
                var host = new Window { Width = 600, Height = 640, Content = control };
                host.Show();
                try
                {
                    foreach (var landscape in new[] { false, true, false, true })
                    {
                        control.Landscape = landscape;
                        control.Layout = PrintLayoutEngine.Compute(new PrintLayoutInput(bitmap.PixelSize.Width, bitmap.PixelSize.Height, 96, 96, paper, landscape,
                            50, 50, 50, 50, PrintScalingMode.BestFit, 100, true, true, PrintHAlign.Centre, PrintVAlign.Centre));
                        sw.Restart();
                        host.UpdateLayout();
                        using var frame = new RenderTargetBitmap(new PixelSize(600, 640));
                        frame.Render(host);
                        Write($"render {label} frame(full bitmap) landscape={landscape} {sw.Elapsed.TotalMilliseconds:F1} ms");
                    }
                    sw.Restart();
                    using (var gray = PrintPreviewControl.CreateGrayscale(bitmap))
                        Write($"render {label} CreateGrayscale(full) {sw.Elapsed.TotalMilliseconds:F0} ms");
                    sw.Restart();
                    using var proxy = PrintPreviewControl.CreateProxy(bitmap);
                    Write($"render {label} CreateProxy {sw.Elapsed.TotalMilliseconds:F0} ms ({proxy?.PixelSize.Width}x{proxy?.PixelSize.Height})");
                    if (proxy is not null)
                    {
                        control.PreviewBitmap = proxy;
                        foreach (var landscape in new[] { false, true, false, true })
                        {
                            control.Landscape = landscape;
                            control.Layout = PrintLayoutEngine.Compute(new PrintLayoutInput(bitmap.PixelSize.Width, bitmap.PixelSize.Height, 96, 96, paper, landscape,
                                50, 50, 50, 50, PrintScalingMode.BestFit, 100, true, true, PrintHAlign.Centre, PrintVAlign.Centre));
                            sw.Restart();
                            host.UpdateLayout();
                            using var frame = new RenderTargetBitmap(new PixelSize(600, 640));
                            frame.Render(host);
                            Write($"render {label} frame(proxy) landscape={landscape} {sw.Elapsed.TotalMilliseconds:F1} ms");
                        }
                        control.PreviewBitmap = bitmap;
                    }
                    if (proxy is not null)
                    {
                        sw.Restart();
                        using var grayProxy = PrintPreviewControl.CreateGrayscale(proxy);
                        Write($"render {label} CreateGrayscale(proxy) {sw.Elapsed.TotalMilliseconds:F0} ms");
                    }
                }
                finally { host.Close(); }
                return Task.CompletedTask;
            });
        }
    }

    private static T? Field<T>(object target, string name) where T : class =>
        target.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(target) as T;

    private static async Task<double> WaitUntil(Func<bool> condition, int timeoutMs = 30000)
    {
        var sw = Stopwatch.StartNew();
        while (!condition())
        {
            if (sw.ElapsedMilliseconds > timeoutMs) return double.NaN;
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(2);
        }
        return sw.Elapsed.TotalMilliseconds;
    }

    [Fact]
    public async Task Window_flow_costs()
    {
        if (!Enabled || !OperatingSystem.IsWindows()) return;
        var settingsDir = Path.Combine(Path.GetTempPath(), "Glide.PrintPerf", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(settingsDir);
        PrintSettingsStore.DirectoryOverrideForTests = settingsDir;
        try
        {
            foreach (var (label, path) in Images())
            {
                await AvaloniaHeadlessFixture.RunOnUIThreadAsync(async () =>
                {
                    using var bitmap = LoadFull(path);
                    var args = new PrintWindowArgs(path, bitmap, bitmap.PixelSize.Width, bitmap.PixelSize.Height, 96, 96,
                        _ => throw new InvalidOperationException("The perf harness never prints."));
                    var window = new PrintWindow(args);
                    window.Trace = (name, ms) => Write($"window {label} trace {name} {ms:F0} ms");
                    var preview = Field<PrintPreviewControl>(window, "_preview")!;
                    var landscapeRadio = Field<RadioButton>(window, "_landscapeRadio")!;
                    var portraitRadio = Field<RadioButton>(window, "_portraitRadio")!;
                    var scaling = Field<ComboBox>(window, "_scalingCombo")!;
                    var printerCombo = Field<ComboBox>(window, "_printerCombo")!;
                    var sw = Stopwatch.StartNew();
                    window.Show();
                    var first = await WaitUntil(() => preview.Paper is not null && preview.Layout is not null);
                    using (window.CaptureRenderedFrame()) { }
                    Write($"window {label} open->first preview {first:F0} ms (incl. first frame {sw.Elapsed.TotalMilliseconds:F0} ms)");

                    // Let the printer settle so the timings below measure the option change only.
                    await WaitUntil(() => printerCombo.SelectedItem is not null, 30000);
                    await Task.Delay(300);
                    Dispatcher.UIThread.RunJobs();

                    for (var i = 0; i < 3; i++)
                    {
                        var wantLandscape = preview.Landscape == false;
                        sw.Restart();
                        if (wantLandscape) landscapeRadio.IsChecked = true; else portraitRadio.IsChecked = true;
                        var instant = preview.Landscape == wantLandscape;
                        var updated = await WaitUntil(() => preview.Landscape == wantLandscape);
                        sw.Restart();
                        using (window.CaptureRenderedFrame()) { }
                        Write($"window {label} orientation->{(wantLandscape ? "landscape" : "portrait")} geometry {(instant ? "instant" : $"{updated:F0} ms")}, frame {sw.Elapsed.TotalMilliseconds:F0} ms");
                        // Let any late refinement finish before the next click.
                        await Task.Delay(50);
                        await WaitUntil(() => false, 400);
                    }

                    foreach (var mode in new[] { "Fill page", "Stretch", "Best fit" })
                    {
                        var before = preview.Layout;
                        sw.Restart();
                        scaling.SelectedItem = mode;
                        var changed = await WaitUntil(() => !ReferenceEquals(preview.Layout, before), 10000);
                        sw.Restart();
                        using (window.CaptureRenderedFrame()) { }
                        Write($"window {label} scaling->{mode} layout {changed:F1} ms, frame {sw.Elapsed.TotalMilliseconds:F0} ms");
                    }

                    var paperCombo = Field<ComboBox>(window, "_paperCombo")!;
                    var printButton = Field<Button>(window, "_printButton")!;
                    if (paperCombo.ItemsSource is IEnumerable<string> papers)
                    {
                        foreach (var other in papers.Where(p => !Equals(p, paperCombo.SelectedItem)).Take(2).ToList())
                        {
                            var beforePaper = preview.Paper;
                            sw.Restart();
                            paperCombo.SelectedItem = other;
                            var immediate = !ReferenceEquals(preview.Paper, beforePaper);
                            var final = await WaitUntil(() => printButton.IsEnabled && !ReferenceEquals(preview.Paper, beforePaper), 30000);
                            Write($"window {label} paper->'{other}' outline {(immediate ? "instant" : "not instant")}, final geometry {final:F0} ms");
                        }
                    }

                    if (printerCombo.ItemsSource is IEnumerable<string> printers)
                    {
                        foreach (var other in printers.ToList())
                        {
                            if (Equals(other, printerCombo.SelectedItem)) continue;
                            var beforePaper = preview.Paper;
                            sw.Restart();
                            printerCombo.SelectedItem = other;
                            await Task.Delay(1);
                            var switched = await WaitUntil(() => printButton.IsEnabled && !ReferenceEquals(preview.Paper, beforePaper), 60000);
                            Write($"window {label} printer->'{other}' final preview {switched:F0} ms");
                        }
                    }
                    window.Close();
                });
            }
        }
        finally
        {
            PrintSettingsStore.DirectoryOverrideForTests = null;
            try { Directory.Delete(settingsDir, true); } catch { }
        }
    }
}
