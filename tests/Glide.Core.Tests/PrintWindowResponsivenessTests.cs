using System.Diagnostics;
using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using Glide.App.Printing;
using Glide.App.Services;

namespace Glide.Core.Tests;

/// <summary>
/// Fake printers for the Print window: no driver, no spooler, nothing is ever printed. Every query is counted and can
/// be slowed down or held at a gate to imitate a slow (network / Remote Desktop redirected) driver.
/// </summary>
internal sealed class FakePrintQueries : IPrintQueries
{
    public readonly Dictionary<string, PrintPrinterCapabilities> Printers = new(StringComparer.OrdinalIgnoreCase);
    public string? Default;
    public int DelayMs;
    public Dictionary<string, int> DelayPerPrinterMs = new(StringComparer.OrdinalIgnoreCase);
    public ManualResetEventSlim? Gate;
    public readonly List<string> CapabilityCalls = new();
    public readonly List<string> GeometryCalls = new();

    public static PrintPrinterCapabilities Caps(string name, params (string Paper, int W, int H)[] papers) => new(
        name,
        papers.Select(p => new PrintPaperOption(p.Paper, p.W, p.H, System.Drawing.Printing.PaperKind.Custom)).ToArray(),
        Array.Empty<PrintSourceOption>(),
        papers[0].Paper, "", true, true);

    public static FakePrintQueries Standard()
    {
        var fake = new FakePrintQueries { Default = "Fake PDF" };
        fake.Printers["Fake PDF"] = Caps("Fake PDF", ("A4", 827, 1169), ("Letter", 850, 1100), ("A3", 1169, 1654));
        fake.Printers["Fake Laser"] = Caps("Fake Laser", ("Letter", 850, 1100), ("Legal", 850, 1400));
        fake.Printers["Fake Network"] = Caps("Fake Network", ("A4", 827, 1169));
        return fake;
    }

    private void Wait(string printer)
    {
        Gate?.Wait(TimeSpan.FromSeconds(20));
        var delay = DelayPerPrinterMs.TryGetValue(printer, out var d) ? d : DelayMs;
        if (delay > 0) Thread.Sleep(delay);
    }

    public IReadOnlyList<string> GetPrinterNames() => Printers.Keys.ToList();
    public string? GetDefaultPrinterName() => Default;

    public PrintPrinterCapabilities GetCapabilities(string printerName)
    {
        lock (CapabilityCalls) CapabilityCalls.Add(printerName);
        Wait(printerName);
        return Printers.TryGetValue(printerName, out var caps) ? caps : throw new PrintNoPrinterException($"Printer '{printerName}' is not available.");
    }

    /// <summary>Printable rect: 0.17 in left/right, 0.2 in top, 0.25 in bottom (asymmetric on purpose).</summary>
    public static PrintPaperGeometry GeometryFor(PrintPaperOption paper) =>
        new(paper.WidthHundredths, paper.HeightHundredths, 17, 20, paper.WidthHundredths - 34, paper.HeightHundredths - 45);

    public PrintPaperGeometry GetPaperGeometry(string printerName, string paperName, string sourceName)
    {
        lock (GeometryCalls) GeometryCalls.Add(printerName + "/" + paperName);
        Wait(printerName);
        var caps = Printers[printerName];
        var paper = caps.Papers.FirstOrDefault(p => p.Name == paperName) ?? caps.Papers[0];
        return GeometryFor(paper);
    }
}

/// <summary>
/// The Print window answers every option instantly: driver reads are cached, off the UI thread, coalesced (latest
/// wins) and never repeated for orientation / scaling / margin / alignment / colour changes; the preview shows a
/// clear "reading" / "Updating preview…" state; preview geometry is exactly what the spool path computes.
/// </summary>
[Collection(AvaloniaHeadlessCollection.Name)]
[Trait("Area", "Print")]
public sealed class PrintWindowResponsivenessTests : IDisposable
{
    private readonly string _settingsDir = Path.Combine(Path.GetTempPath(), "Glide.PrintWindowTests", Guid.NewGuid().ToString("N"));

    public PrintWindowResponsivenessTests(AvaloniaHeadlessFixture fixture)
    {
        _ = fixture;
        Directory.CreateDirectory(_settingsDir);
        PrintSettingsStore.DirectoryOverrideForTests = _settingsDir;
        WindowPlacementService.ResetForTests();
        WindowPlacementService.PersistOverrideForTests = _ => { };
    }

    public void Dispose()
    {
        PrintSettingsStore.DirectoryOverrideForTests = null;
        WindowPlacementService.PersistOverrideForTests = null;
        WindowPlacementService.ResetForTests();
        TempDir.TryDelete(_settingsDir);
    }

    internal static WriteableBitmap MakeBitmap(int w, int h)
    {
        var bitmap = new WriteableBitmap(new PixelSize(w, h), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);
        using var fb = bitmap.Lock();
        var row = new byte[w * 4];
        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                row[x * 4] = (byte)(x * 255 / Math.Max(1, w - 1));
                row[x * 4 + 1] = (byte)(y * 255 / Math.Max(1, h - 1));
                row[x * 4 + 2] = (byte)((x + y) % 256);
                row[x * 4 + 3] = 255;
            }
            System.Runtime.InteropServices.Marshal.Copy(row, 0, fb.Address + y * fb.RowBytes, row.Length);
        }
        return bitmap;
    }

    internal sealed class Harness
    {
        public required PrintWindow Window;
        public required Bitmap Bitmap;
        public T F<T>(string name) where T : class =>
            (T)typeof(PrintWindow).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(Window)!;
        public PrintPreviewControl Preview => F<PrintPreviewControl>("_preview");
        public ComboBox Printer => F<ComboBox>("_printerCombo");
        public ComboBox Paper => F<ComboBox>("_paperCombo");
        public RadioButton Landscape => F<RadioButton>("_landscapeRadio");
        public RadioButton Portrait => F<RadioButton>("_portraitRadio");
        public ComboBox Scaling => F<ComboBox>("_scalingCombo");
        public ComboBox Colour => F<ComboBox>("_colorCombo");
        public ComboBox HAlign => F<ComboBox>("_hAlignCombo");
        public NumericUpDown MarginLeft => F<NumericUpDown>("_marginLeft");
        public NumericUpDown Copies => F<NumericUpDown>("_copiesBox");
        public Button Print => F<Button>("_printButton");
        public TextBlock Status => F<TextBlock>("_statusText");
    }

    internal static Harness Open(PrintQueryCache cache, int w = 4000, int h = 3000, bool show = true)
    {
        var bitmap = MakeBitmap(Math.Min(w, 400), Math.Min(h, 300));
        var args = new PrintWindowArgs("C:\\pictures\\test.jpg", bitmap, w, h, 300, 300,
            _ => throw new InvalidOperationException("Tests never print."));
        var window = new PrintWindow(args, cache);
        if (show) window.Show();
        return new Harness { Window = window, Bitmap = bitmap };
    }

    internal static async Task<bool> WaitUntil(Func<bool> condition, int timeoutMs = 10000)
    {
        var sw = Stopwatch.StartNew();
        while (!condition())
        {
            if (sw.ElapsedMilliseconds > timeoutMs) return false;
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(5);
        }
        return true;
    }

    internal static Task<bool> Ready(Harness h) => WaitUntil(() => h.Preview.Paper is not null && h.Print.IsEnabled);

    [Fact]
    public async Task Orientation_and_layout_options_update_the_preview_instantly_without_any_driver_call()
    {
        await AvaloniaHeadlessFixture.RunOnUIThreadAsync(async () =>
        {
            var fake = FakePrintQueries.Standard();
            var cache = new PrintQueryCache(fake);
            var h = Open(cache);
            try
            {
                Assert.True(await Ready(h));
                Assert.Single(fake.CapabilityCalls);
                Assert.Single(fake.GeometryCalls);
                fake.DelayMs = 3000; // a driver call now would be obvious

                h.Landscape.IsChecked = true;
                Assert.True(h.Preview.Landscape, "landscape must show in the same UI turn as the click");
                Assert.True(h.Preview.Layout!.DestWidth > 0);
                var landscapeLayout = h.Preview.Layout;
                h.Portrait.IsChecked = true;
                Assert.False(h.Preview.Landscape);
                Assert.NotEqual(landscapeLayout, h.Preview.Layout);

                foreach (var mode in new[] { "Fill page", "Stretch", "Actual size", "Custom scale", "Best fit" })
                {
                    var before = h.Preview.Layout;
                    h.Scaling.SelectedItem = mode;
                    Assert.NotNull(h.Preview.Layout);
                    Assert.False(ReferenceEquals(before, h.Preview.Layout), mode);
                }
                h.MarginLeft.Value = 1.5m;
                Assert.Equal(h.Window.BuildLayoutInput(h.Preview.Paper!).MarginLeftHundredths, h.Preview.MarginLeft);
                h.HAlign.SelectedItem = "Left";
                h.Copies.Value = 3;
                h.Colour.SelectedItem = "Grayscale";
                Dispatcher.UIThread.RunJobs();

                Assert.Single(fake.CapabilityCalls);
                Assert.Single(fake.GeometryCalls);
                Assert.True(h.Print.IsEnabled);
                Assert.Null(h.Preview.BusyText);
            }
            finally { h.Window.Close(); }
        });
    }

    [Fact]
    public async Task While_the_driver_is_slow_the_window_shows_a_reading_state_and_layout_options_stay_usable()
    {
        await AvaloniaHeadlessFixture.RunOnUIThreadAsync(async () =>
        {
            var fake = FakePrintQueries.Standard();
            using var gate = new ManualResetEventSlim(false);
            fake.Gate = gate;
            var h = Open(new PrintQueryCache(fake));
            try
            {
                Assert.True(await WaitUntil(() => fake.CapabilityCalls.Count == 1));
                Assert.Null(h.Preview.Paper);
                Assert.Equal("Reading printer settings…", h.Preview.PlaceholderText);
                Assert.Equal("Reading printer settings…", h.Status.Text);
                Assert.False(h.Print.IsEnabled);
                Assert.True(h.Landscape.IsEnabled && h.Scaling.IsEnabled && h.MarginLeft.IsEnabled, "layout options never wait for the driver");
                h.Landscape.IsChecked = true; // a click while reading is kept
                gate.Set();
                Assert.True(await Ready(h));
                Assert.True(h.Preview.Landscape);
                Assert.Equal("", h.Status.Text);
                Assert.Null(h.Preview.BusyText);
            }
            finally { gate.Set(); h.Window.Close(); }
        });
    }

    [Fact]
    public async Task A_paper_change_draws_the_new_sheet_at_once_and_refines_with_the_driver_geometry()
    {
        await AvaloniaHeadlessFixture.RunOnUIThreadAsync(async () =>
        {
            var fake = FakePrintQueries.Standard();
            var cache = new PrintQueryCache(fake);
            var h = Open(cache);
            try
            {
                Assert.True(await Ready(h));
                using var gate = new ManualResetEventSlim(false);
                fake.Gate = gate;
                h.Paper.SelectedItem = "A3";
                // Instant: the provisional A3 sheet (exact size from the paper list) with the "Updating preview…" badge.
                Assert.Equal(1169, h.Preview.Paper!.PaperWidthHundredths);
                Assert.Equal(1654, h.Preview.Paper.PaperHeightHundredths);
                Assert.Equal("Updating preview…", h.Preview.BusyText);
                Assert.False(h.Print.IsEnabled, "Print waits for the driver's real printable area");
                gate.Set();
                Assert.True(await Ready(h));
                Assert.Equal(FakePrintQueries.GeometryFor(new PrintPaperOption("A3", 1169, 1654, default)), h.Preview.Paper);
                Assert.Null(h.Preview.BusyText);
                Assert.Single(fake.CapabilityCalls); // a paper change never re-reads the printer
                Assert.Equal(["Fake PDF/A4", "Fake PDF/A3"], fake.GeometryCalls);

                // Back to A4: cached, instant, no driver call.
                h.Paper.SelectedItem = "A4";
                Assert.True(h.Print.IsEnabled);
                Assert.Equal(827, h.Preview.Paper!.PaperWidthHundredths);
                Assert.Equal(2, fake.GeometryCalls.Count);
            }
            finally { fake.Gate?.Set(); h.Window.Close(); }
        });
    }

    [Fact]
    public async Task Rapid_printer_changes_are_coalesced_and_the_latest_selection_wins()
    {
        await AvaloniaHeadlessFixture.RunOnUIThreadAsync(async () =>
        {
            var fake = FakePrintQueries.Standard();
            var h = Open(new PrintQueryCache(fake));
            try
            {
                Assert.True(await Ready(h));
                fake.DelayPerPrinterMs["Fake Laser"] = 600;
                // Scroll through two printers within the debounce window: only the last one is read.
                h.Printer.SelectedItem = "Fake Network";
                h.Printer.SelectedItem = "Fake Laser";
                Assert.False(h.Print.IsEnabled);
                Assert.True(await WaitUntil(() => h.Print.IsEnabled && h.Preview.Paper!.PaperWidthHundredths == 850));
                Assert.Equal(["Fake PDF", "Fake Laser"], fake.CapabilityCalls);

                // A slow answer that arrives after a newer selection is dropped.
                fake.DelayPerPrinterMs["Fake Network"] = 900;
                h.Printer.SelectedItem = "Fake Network";
                await Task.Delay(PrintWindow.SelectionDebounceMs + 60); // its driver read has started
                h.Printer.SelectedItem = "Fake Laser";                  // cached: instant
                Assert.True(h.Print.IsEnabled);
                Assert.Equal("Fake Laser", h.Printer.SelectedItem);
                await Task.Delay(1200);
                Dispatcher.UIThread.RunJobs();
                Assert.Equal("Fake Laser", h.Printer.SelectedItem);
                Assert.Equal(850, h.Preview.Paper!.PaperWidthHundredths); // still Laser's Letter, not Network's A4
                Assert.Contains("Letter", h.Paper.ItemsSource!.Cast<string>());
            }
            finally { h.Window.Close(); }
        });
    }

    [Fact]
    public async Task Capabilities_and_geometry_are_cached_so_a_second_print_window_is_instant()
    {
        await AvaloniaHeadlessFixture.RunOnUIThreadAsync(async () =>
        {
            var fake = FakePrintQueries.Standard();
            var cache = new PrintQueryCache(fake);
            var first = Open(cache);
            Assert.True(await Ready(first));
            first.Window.Close();
            fake.DelayMs = 2000;
            var second = Open(cache);
            try
            {
                var sw = Stopwatch.StartNew();
                Assert.True(await Ready(second));
                Assert.True(sw.ElapsedMilliseconds < 1500, $"{sw.ElapsedMilliseconds} ms");
                Assert.Single(fake.CapabilityCalls);
                Assert.Single(fake.GeometryCalls);
                Assert.Equal(2, cache.PrinterListQueries); // the cheap printer list is re-read so new printers show up
            }
            finally { second.Window.Close(); }
        });
    }

    [Fact]
    public void Cache_shares_in_flight_reads_expires_and_forgets_failures_and_invalidated_printers()
    {
        var fake = FakePrintQueries.Standard();
        fake.DelayMs = 100;
        long now = 0;
        var cache = new PrintQueryCache(fake, TimeSpan.FromSeconds(10), () => now);
        var a = cache.GetCapabilitiesAsync("Fake PDF");
        var b = cache.GetCapabilitiesAsync("Fake PDF");
        Assert.Same(a, b);
        a.Wait();
        Assert.Single(fake.CapabilityCalls);
        Assert.True(cache.TryGetCachedCapabilities("Fake PDF", out _));
        now += Stopwatch.Frequency * 11; // past the time to live
        Assert.False(cache.TryGetCachedCapabilities("Fake PDF", out _));
        cache.GetCapabilitiesAsync("Fake PDF").Wait();
        Assert.Equal(2, fake.CapabilityCalls.Count);

        Assert.ThrowsAny<Exception>(() => cache.GetCapabilitiesAsync("Missing").Wait());
        Thread.Sleep(50);
        Assert.ThrowsAny<Exception>(() => cache.GetCapabilitiesAsync("Missing").Wait());
        Assert.Equal(2, fake.CapabilityCalls.Count(c => c == "Missing")); // a failure is retried, not remembered

        cache.GetGeometryAsync("Fake PDF", "A4", "").Wait();
        Assert.True(cache.TryGetCachedGeometry("Fake PDF", "A4", "", out _));
        cache.Invalidate("Fake PDF");
        Assert.False(cache.TryGetCachedGeometry("Fake PDF", "A4", "", out _));
        Assert.False(cache.TryGetCachedCapabilities("Fake PDF", out _));
    }

    /// <summary>The preview's page geometry is exactly what PrintService.PrintAsync computes for the same request.</summary>
    [Fact]
    public async Task Preview_geometry_equals_the_spool_geometry_for_every_orientation_and_scaling()
    {
        await AvaloniaHeadlessFixture.RunOnUIThreadAsync(async () =>
        {
            var fake = FakePrintQueries.Standard();
            var h = Open(new PrintQueryCache(fake), 6000, 2500);
            try
            {
                Assert.True(await Ready(h));
                foreach (var landscape in new[] { false, true })
                foreach (var mode in new[] { "Best fit", "Fill page", "Actual size", "Custom scale", "Stretch" })
                foreach (var align in new[] { "Left", "Centre", "Right" })
                {
                    if (landscape) h.Landscape.IsChecked = true; else h.Portrait.IsChecked = true;
                    h.Scaling.SelectedItem = mode;
                    h.HAlign.SelectedItem = align;
                    var request = h.Window.BuildJobRequest("Fake PDF", 6000, 2500);
                    Assert.Equal(landscape, request.Landscape);
                    // What PrintService.PrintAsync does with the request and the live driver geometry (portrait, as the window reads it).
                    var live = FakePrintQueries.GeometryFor(fake.Printers[request.PrinterName].Papers.First(p => p.Name == request.PaperName));
                    var spool = PrintLayoutEngine.Compute(new PrintLayoutInput(
                        request.ImageWidthPx, request.ImageHeightPx, request.DpiX, request.DpiY,
                        live, request.Landscape,
                        request.MarginLeftHundredths, request.MarginTopHundredths,
                        request.MarginRightHundredths, request.MarginBottomHundredths,
                        request.ScalingMode, request.CustomScalePercent, request.KeepAspectRatio,
                        request.AutoRotate, request.HAlign, request.VAlign));
                    Assert.Equal(spool, h.Preview.Layout);
                    Assert.Equal(live, h.Preview.Paper);
                    Assert.Equal(landscape, h.Preview.Landscape);
                }
            }
            finally { h.Window.Close(); }
        });
    }

    [Fact]
    public void Preview_proxy_is_screen_sized_and_grayscale_is_unchanged_by_parallel_conversion()
    {
        AvaloniaHeadlessFixture.RunOnUIThread(() =>
        {
            using var big = MakeBitmap(4000, 1000);
            using var proxy = PrintPreviewControl.CreateProxy(big)!;
            Assert.Equal(PrintPreviewControl.ProxyLongestSide, proxy.PixelSize.Width);
            Assert.Equal(512, proxy.PixelSize.Height);
            using var small = MakeBitmap(800, 600);
            Assert.Null(PrintPreviewControl.CreateProxy(small)); // already small: drawn as is

            using var gray = PrintPreviewControl.CreateGrayscale(small)!;
            var src = Pixels(small);
            var dst = Pixels(gray);
            for (var i = 0; i < src.Length; i += 4)
            {
                var expected = (byte)((src[i + 2] * 299 + src[i + 1] * 587 + src[i] * 114 + 500) / 1000);
                if (dst[i] != expected || dst[i + 1] != expected || dst[i + 2] != expected || dst[i + 3] != src[i + 3])
                    Assert.Fail($"pixel {i / 4}: expected {expected}, got {dst[i]},{dst[i + 1]},{dst[i + 2]}");
            }
        });
    }

    private static byte[] Pixels(Bitmap bitmap)
    {
        var w = bitmap.PixelSize.Width;
        var h = bitmap.PixelSize.Height;
        var bytes = new byte[w * h * 4];
        using var target = new WriteableBitmap(bitmap.PixelSize, new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Unpremul);
        using (var fb = target.Lock())
        {
            bitmap.CopyPixels(fb, AlphaFormat.Unpremul);
            System.Runtime.InteropServices.Marshal.Copy(fb.Address, bytes, 0, bytes.Length);
        }
        return bytes;
    }

    [Fact]
    public void Provisional_sheet_has_the_exact_paper_size_and_sane_insets()
    {
        var a3 = new PrintPaperOption("A3", 1169, 1654, default);
        var fresh = PrintPreviewGeometry.Provisional(a3, null);
        Assert.Equal((1169d, 1654d), (fresh.PaperWidthHundredths, fresh.PaperHeightHundredths));
        Assert.Equal(25, fresh.PrintableX);
        var borrowed = PrintPreviewGeometry.Provisional(a3, new PrintPaperGeometry(827, 1169, 17, 20, 793, 1124));
        Assert.Equal((17d, 20d), (borrowed.PrintableX, borrowed.PrintableY));
        Assert.Equal(1169 - 34, borrowed.PrintableWidth);
        Assert.Equal(1654 - 45, borrowed.PrintableHeight);
        var tiny = PrintPreviewGeometry.Provisional(new PrintPaperOption("Stamp", 60, 60, default), new PrintPaperGeometry(827, 1169, 40, 40, 747, 1089));
        Assert.True(tiny.PrintableWidth > 0 && tiny.PrintableHeight > 0);
    }
}
