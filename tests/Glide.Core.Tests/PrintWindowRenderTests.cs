using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Headless;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using Glide.App.Printing;
using Glide.App.Services;

namespace Glide.Core.Tests;

/// <summary>
/// Headless renders of the Print window over fake printers: the "reading printer settings" state, the ready preview,
/// an instant orientation change, the "Updating preview…" refine state and grayscale. Asserts the sheet is drawn;
/// set GLIDE_SAVE_RENDERS to a folder to keep the PNGs (and GLIDE_PRINT_PERF_IMAGES to use a real photo).
/// </summary>
[Collection(AvaloniaHeadlessCollection.Name)]
[Trait("Area", "Print")]
public sealed class PrintWindowRenderTests : IDisposable
{
    private readonly string _settingsDir = Path.Combine(Path.GetTempPath(), "Glide.PrintRender", Guid.NewGuid().ToString("N"));

    public PrintWindowRenderTests(AvaloniaHeadlessFixture fixture)
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

    private static int WhitePixels(Bitmap frame)
    {
        var w = frame.PixelSize.Width;
        var h = frame.PixelSize.Height;
        var pixels = new byte[w * h * 4];
        var handle = GCHandle.Alloc(pixels, GCHandleType.Pinned);
        try { frame.CopyPixels(new PixelRect(0, 0, w, h), handle.AddrOfPinnedObject(), pixels.Length, w * 4); }
        finally { handle.Free(); }
        var count = 0;
        for (var i = 0; i < pixels.Length; i += 4)
            if (pixels[i] > 245 && pixels[i + 1] > 245 && pixels[i + 2] > 245) count++;
        return count;
    }

    [Fact]
    public async Task Renders_reading_ready_landscape_refining_and_grayscale_states()
    {
        var save = Environment.GetEnvironmentVariable("GLIDE_SAVE_RENDERS");
        var photo = Path.Combine(Environment.GetEnvironmentVariable("GLIDE_PRINT_PERF_IMAGES") ?? "", "jpeg-base-4k.jpg");
        await AvaloniaHeadlessFixture.RunOnUIThreadAsync(async () =>
        {
            var fake = FakePrintQueries.Standard();
            using var gate = new ManualResetEventSlim(false);
            fake.Gate = gate;
            Bitmap source = File.Exists(photo) ? new Bitmap(photo) : PrintWindowResponsivenessTests.MakeBitmap(1600, 900);
            var args = new PrintWindowArgs("C:\\Pictures\\4K Wallpaper.jpg", source, source.PixelSize.Width, source.PixelSize.Height, 72, 72,
                _ => throw new InvalidOperationException("Tests never print."));
            var window = new PrintWindow(args, new PrintQueryCache(fake));
            window.RequestedThemeVariant = ThemeVariant.Dark;
            window.Styles.Insert(0, new FluentTheme());
            window.Styles.Insert(1, new StyleInclude(new Uri("avares://Glide/")) { Source = new Uri("avares://Glide/Styles/GlideTheme.axaml") });
            window.Show();
            var h = new PrintWindowResponsivenessTests.Harness { Window = window, Bitmap = source };
            var frames = new List<(string Name, int White)>();
            void Capture(string name)
            {
                Dispatcher.UIThread.RunJobs();
                window.UpdateLayout();
                using var frame = new RenderTargetBitmap(new PixelSize((int)Math.Ceiling(window.Bounds.Width), (int)Math.Ceiling(window.Bounds.Height)));
                frame.Render(window);
                frames.Add((name, WhitePixels(frame)));
                if (!string.IsNullOrWhiteSpace(save))
                {
                    Directory.CreateDirectory(save);
                    frame.Save(Path.Combine(save, $"print-window-{name}.png"));
                }
            }
            try
            {
                Assert.True(await PrintWindowResponsivenessTests.WaitUntil(() => fake.CapabilityCalls.Count == 1));
                Capture("1-reading-printer-settings");
                gate.Set();
                Assert.True(await PrintWindowResponsivenessTests.Ready(h));
                await Task.Delay(100);
                Capture("2-ready-portrait");
                h.Landscape.IsChecked = true;
                Capture("3-landscape-instant");
                fake.Gate = new ManualResetEventSlim(false);
                h.Paper.SelectedItem = "A3";
                Capture("4-paper-change-updating-preview");
                fake.Gate.Set();
                Assert.True(await PrintWindowResponsivenessTests.Ready(h));
                Capture("5-paper-refined");
                h.Colour.SelectedItem = "Grayscale";
                await Task.Delay(300);
                Capture("6-grayscale");
            }
            finally
            {
                fake.Gate?.Set();
                window.Close();
                source.Dispose();
            }
            // No sheet while reading; a white sheet in every later state.
            Assert.True(frames[0].White < frames[1].White, string.Join(", ", frames));
            Assert.All(frames.Skip(1), f => Assert.True(f.White > 20000, $"{f.Name}: {f.White} white pixels"));
        });
    }
}
