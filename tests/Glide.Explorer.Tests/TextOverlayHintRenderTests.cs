using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;
using Glide.App.Services;
using Glide.Core.Settings;
using Glide.Core.Workspace;

namespace Glide.Explorer.Tests;

/// <summary>
/// The "Alt+drag to move" hint never appeared on the real desktop in four attempts although the headless tests said it was
/// "shown" (a flag and a parent). These tests assert what the user would see: the hint is a Border of the root grid, visible,
/// sized, inside the window, opaque, light on dark, topmost, and its pixels really are in the rendered frame.
/// </summary>
[Collection(ExplorerHeadlessCollection.Name)]
[Trait("Area", "Explorer")]
public sealed class TextOverlayHintRenderTests
{
    private static async Task<Border> OpenPictureWithOverlayAsync(ExplorerHarness h, int delayMs)
    {
        ToolTipGuard.Register();
        h.DoubleClick("a.png");
        await h.WaitUntil(() => h.Workspace.Active is ImageTabState, "image opened in the tab");
        await h.WaitUntil(() => h.Window.FindControl<Grid>("ImageView")!.IsVisible, "the picture view is showing");
        var host = h.Window.FindControl<Border>("CornerCounterHost")!;
        h.Window.FindControl<TextBlock>("CornerCounterText")!.Text = "[2/17]";
        host.IsVisible = true;
        h.Settings.PictureCounterEnabled = true;
        TooltipAreas.SetDelayMs(h.Settings, TooltipAreas.Main, delayMs);
        await h.SettleAsync();
        for (var i = 0; i < 3; i++) { h.Pump(); await Task.Delay(20); }
        return host;
    }

    /// <summary>The pointer comes from the dark area beside the picture onto the overlay and rests there, jittering a pixel like a hand.</summary>
    private static async Task RestOnOverlayAsync(ExplorerHarness h, Border host)
    {
        var centre = host.TranslatePoint(new Point(host.Bounds.Width / 2, host.Bounds.Height / 2), h.Window)!.Value;
        var start = new Point(centre.X - 120, centre.Y + 120);
        h.Window.MouseMove(start);
        h.Pump();
        for (var i = 1; i <= 10; i++) { h.Window.MouseMove(start + (centre - start) * (i / 10.0)); h.Pump(); await Task.Delay(8); }
        var deadline = Environment.TickCount64 + 2500;
        var jitter = 0;
        while (Environment.TickCount64 < deadline && !h.Window.IsTextOverlayHintShown)
        {
            h.Window.MouseMove(new Point(centre.X + (jitter++ % 2), centre.Y));
            h.Pump();
            await Task.Delay(30);
        }
        Assert.True(h.Window.IsTextOverlayHintShown, "the hint did not show after the pointer rested on the overlay");
        // Let layout and a render frame run, as they do on screen.
        for (var i = 0; i < 4; i++) { h.Window.UpdateLayout(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(); h.Pump(); await Task.Delay(15); }
    }

    private static double Luminance(Color c)
    {
        static double Channel(byte v) { var s = v / 255.0; return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4); }
        return 0.2126 * Channel(c.R) + 0.7152 * Channel(c.G) + 0.0722 * Channel(c.B);
    }

    private static (byte B, byte G, byte R, byte A) PixelAt(WriteableBitmap bitmap, int x, int y)
    {
        using var fb = bitmap.Lock();
        var offset = y * fb.RowBytes + x * 4;
        var b0 = Marshal.ReadByte(fb.Address, offset); var b1 = Marshal.ReadByte(fb.Address, offset + 1);
        var b2 = Marshal.ReadByte(fb.Address, offset + 2); var b3 = Marshal.ReadByte(fb.Address, offset + 3);
        // The headless frame is RGBA or BGRA depending on the platform's preferred format.
        return fb.Format == Avalonia.Platform.PixelFormat.Bgra8888 ? (b0, b1, b2, b3) : (b2, b1, b0, b3);
    }

    [Fact]
    public async Task The_hint_is_a_visible_opaque_topmost_label_of_the_root_grid_inside_the_window()
    {
        await ExplorerHarness.Run(async h =>
        {
            var host = await OpenPictureWithOverlayAsync(h, 150);
            await RestOnOverlayAsync(h, host);

            var root = h.Window.FindControl<Grid>("MainRoot")!;
            var hint = h.Window.FindControl<Border>("TextOverlayHint")!;
            var label = (TextBlock)hint.Child!;

            // It is the root grid's own child (no overlay layer), above every other child, never hit-testable.
            Assert.Same(root, hint.Parent);
            Assert.Equal(hint.ZIndex, root.Children.Max(c => c.ZIndex));
            Assert.False(hint.IsHitTestVisible);
            Assert.Empty(h.Window.GetVisualDescendants().OfType<Avalonia.Controls.Primitives.OverlayLayer>().SelectMany(l => l.Children).OfType<Border>().Where(b => b.Name == "TextOverlayHint"));

            // Visible and really laid out, with the text.
            Assert.True(hint.IsVisible && hint.IsEffectivelyVisible, "the hint is not visible");
            Assert.True(hint.Bounds.Width >= 100 && hint.Bounds.Height >= 18, $"the hint has no real size: {hint.Bounds}");
            Assert.Equal(MainWindowToolTipText(), label.Text);
            var opacity = 1.0;
            for (Visual? v = hint; v is not null; v = v.GetVisualParent()) opacity *= v.Opacity;
            Assert.Equal(1.0, opacity, 3);

            // Inside the window's client area, entirely, and next to (not over) the overlay.
            var origin = hint.TranslatePoint(new Point(0, 0), h.Window)!.Value;
            var box = new Rect(origin, hint.Bounds.Size);
            var rootBox = new Rect(root.TranslatePoint(new Point(0, 0), h.Window)!.Value, root.Bounds.Size);
            Assert.True(rootBox.Contains(box), $"the hint {box} is outside the window's content {rootBox}");
            Assert.True(new Rect(h.Window.Bounds.Size).Contains(box), $"the hint {box} is outside the window {h.Window.Bounds}");
            var hostBox = new Rect(host.TranslatePoint(new Point(0, 0), h.Window)!.Value, host.Bounds.Size);
            Assert.False(box.Intersects(hostBox), $"the hint {box} covers the overlay {hostBox}");

            // Opaque dark background, light text with real contrast (no theme resource that could resolve to nothing).
            var background = Assert.IsAssignableFrom<ISolidColorBrush>(hint.Background);
            Assert.Equal(255, background.Color.A);
            Assert.Equal(1.0, background.Opacity, 3);
            var foreground = Assert.IsAssignableFrom<ISolidColorBrush>(label.Foreground);
            Assert.Equal(255, foreground.Color.A);
            var (light, dark) = (Math.Max(Luminance(foreground.Color), Luminance(background.Color)), Math.Min(Luminance(foreground.Color), Luminance(background.Color)));
            Assert.True((light + 0.05) / (dark + 0.05) >= 7, $"text {foreground.Color} on {background.Color} has too little contrast");
            Assert.True(Luminance(background.Color) < 0.05, $"the background {background.Color} is not dark");
            Assert.NotNull(hint.BorderBrush);
        });
    }

    private static string MainWindowToolTipText() => Glide.App.MainWindow.TextOverlayToolTip;

    [Fact]
    public async Task The_hint_is_painted_in_the_rendered_frame()
    {
        await ExplorerHarness.Run(async h =>
        {
            var host = await OpenPictureWithOverlayAsync(h, 150);
            var hint = h.Window.FindControl<Border>("TextOverlayHint")!;
            var background = Assert.IsAssignableFrom<ISolidColorBrush>(hint.Background).Color;

            await RestOnOverlayAsync(h, host);
            var origin = hint.TranslatePoint(new Point(0, 0), h.Window)!.Value;
            var scale = h.Window.RenderScaling;
            using var frame = h.Window.CaptureRenderedFrame();
            Assert.NotNull(frame);
            // The strip of the hint's left padding (inside the border, left of the text) is the background colour; so is the strip under the text.
            var x = (int)Math.Round((origin.X + 4) * scale);
            var yMid = (int)Math.Round((origin.Y + hint.Bounds.Height / 2) * scale);
            var yBottom = (int)Math.Round((origin.Y + hint.Bounds.Height - 3) * scale);
            foreach (var y in new[] { yMid, yBottom })
            {
                var (b, g, r, a) = PixelAt(frame!, x, y);
                Assert.True(Math.Abs(r - background.R) <= 3 && Math.Abs(g - background.G) <= 3 && Math.Abs(b - background.B) <= 3 && a == 255,
                    $"pixel ({x},{y}) is #{a:X2}{r:X2}{g:X2}{b:X2}, not the hint's background {background}: the hint is not in the rendered frame");
            }
            // And there is light text on it: at least a few near-white pixels inside the hint's box.
            var lightPixels = 0;
            var left = (int)Math.Round(origin.X * scale); var top = (int)Math.Round(origin.Y * scale);
            var width = (int)Math.Round(hint.Bounds.Width * scale); var height = (int)Math.Round(hint.Bounds.Height * scale);
            for (var yy = top; yy < top + height; yy++)
                for (var xx = left; xx < left + width; xx++)
                {
                    var (b, g, r, _) = PixelAt(frame!, xx, yy);
                    if (r > 180 && g > 180 && b > 180) lightPixels++;
                }
            Assert.True(lightPixels >= 20, $"only {lightPixels} light text pixels inside the hint box {hint.Bounds}");
        });
    }

    [Fact]
    public async Task The_hint_shows_with_a_pointer_that_rests_perfectly_still_and_nothing_else_moves()
    {
        await ExplorerHarness.Run(async h =>
        {
            var host = await OpenPictureWithOverlayAsync(h, 120);
            var centre = host.TranslatePoint(new Point(host.Bounds.Width / 2, host.Bounds.Height / 2), h.Window)!.Value;
            h.Window.MouseMove(new Point(centre.X - 90, centre.Y + 90));
            h.Pump();
            h.Window.MouseMove(centre);   // the last move: from here on the pointer never moves again
            h.Pump();
            Assert.False(h.Window.IsTextOverlayHintShown);
            await h.WaitUntil(() => h.Window.IsTextOverlayHintShown, "the hint shows for a pointer at rest (timer only)");
        });
    }

    [Fact]
    public async Task GLIDE_TRACE_HINT_logs_every_step_of_the_hint()
    {
        var lines = new List<string>();
        UiTrace.SinkForTests = line => { lock (lines) lines.Add(line); };
        UiTrace.SetForTests(hint: true, input: null);
        try
        {
            await ExplorerHarness.Run(async h =>
            {
                var host = await OpenPictureWithOverlayAsync(h, 100);
                await RestOnOverlayAsync(h, host);
                var leave = new Point(host.TranslatePoint(new Point(0, 0), h.Window)!.Value.X - 200, 300);
                h.Window.MouseMove(leave);
                h.Pump();
            });
        }
        finally { UiTrace.SetForTests(null, null); UiTrace.SinkForTests = null; }

        string[] snapshot; lock (lines) snapshot = lines.ToArray();
        string Find(string prefix) => snapshot.FirstOrDefault(l => l.StartsWith(prefix, StringComparison.Ordinal)) ?? throw new Xunit.Sdk.XunitException($"no trace line {prefix}; got:\n{string.Join("\n", snapshot.Take(40))}");
        Assert.Contains("onOverlay=True", Find("hint.hover"));
        Assert.Contains("intervalMs=", Find("hint.timer_start"));
        var tick = snapshot.FirstOrDefault(l => l.StartsWith("hint.tick", StringComparison.Ordinal));
        var show = Find("hint.show");
        Assert.Contains("result=True", show);
        Assert.Contains("reason=shown", show);
        var placed = Find("hint.placed ");
        foreach (var part in new[] { "layer=MainRoot", "desired=", "margin=", "bounds=", "inRoot=", "isVisible=True", "effectiveOpacity=1", "background=#ff1f2227", "parents=Grid#MainRoot" })
            Assert.Contains(part, placed, StringComparison.OrdinalIgnoreCase);
        Assert.NotNull(Find("hint.placed_after_layout"));
        Assert.NotNull(Find("hint.hidden"));
        _ = tick;   // the timer tick is logged whenever the timer (rather than a pointer move) showed the hint
    }
}
