using Avalonia;
using Avalonia.Media;
using Glide.App.Controls;

namespace Glide.Core.Tests;

/// <summary>Round 4 K1: picture-in-picture overlays and Overlay mode draw one shared window chrome.</summary>
[Trait("Area", "Overlay")]
public sealed class OverlayFrameChromeSharingTests
{
    private sealed class RecordingCanvas : OverlayFrameChromePainter.ICanvas
    {
        public readonly List<(string Kind, Rect Rect, Color? Fill, Color? Stroke)> Ops = new();
        public void Rect(Rect rect, Color? fill, Color? stroke, double strokeThickness, double radius) => Ops.Add(("rect", rect, fill, stroke));
        public void Line(Point a, Point b, Color color, double thickness) => Ops.Add(("line", new Rect(a, b), color, null));
        public void Ellipse(Point center, double radius, Color fill) =>
            Ops.Add(("ellipse", new Rect(center.X - radius, center.Y - radius, radius * 2, radius * 2), fill, null));
    }

    private static readonly Color Accent = Color.FromRgb(0x36, 0xB8, 0xF4);

    private static RecordingCanvas Draw(OverlayFrameChromeSpec spec)
    {
        var canvas = new RecordingCanvas();
        OverlayFrameChromePainter.Draw(canvas, spec, Accent);
        return canvas;
    }

    private static OverlayFrameChromeSpec PictureInPicture(double w, double h) =>
        WindowInWindowOverlayManager.BuildChromeSpec(new OverlayState(Guid.NewGuid(), @"C:\i\a.png", 0, 0, w, h, Opacity: 1), glow: true);

    private static OverlayFrameChromeSpec OverlayMode(double w, double h) =>
        new LegacyOverlayChrome { OpacityValue = 1 }.BuildSpec(new Size(w, h));

    [Fact]
    public void Both_hosts_place_close_zoom_and_opacity_controls_in_the_same_spots()
    {
        var pip = PictureInPicture(400, 300);
        var mode = OverlayMode(400, 300);
        Assert.Equal(pip.Close, mode.Close);
        Assert.Equal(pip.ZoomOut, mode.ZoomOut);
        Assert.Equal(pip.ZoomIn, mode.ZoomIn);
        Assert.Equal(pip.Slider, mode.Slider);
        Assert.Equal(pip.ResizeGrip, OverlayChromePolicy.ResizeRect(new Size(400, 300)));
    }

    [Fact]
    public void Both_hosts_draw_the_same_close_button_and_zoom_buttons_with_the_shared_palette()
    {
        var pip = Draw(PictureInPicture(400, 300));
        var mode = Draw(OverlayMode(400, 300));
        foreach (var ops in new[] { pip, mode })
        {
            Assert.Contains(ops.Ops, o => o.Kind == "ellipse" && o.Fill == OverlayFrameChromePainter.Palette.Panel);
            Assert.Equal(2, ops.Ops.Count(o => o.Kind == "rect" && o.Fill == OverlayFrameChromePainter.Palette.Glass && o.Stroke == Accent && o.Rect.Width == 30 && o.Rect.Height == 30));
            Assert.Contains(ops.Ops, o => o.Kind == "rect" && o.Fill is null && o.Stroke == Accent); // frame
        }
        var pipClose = pip.Ops.First(o => o.Kind == "ellipse" && o.Fill == OverlayFrameChromePainter.Palette.Panel);
        var modeClose = mode.Ops.First(o => o.Kind == "ellipse" && o.Fill == OverlayFrameChromePainter.Palette.Panel);
        Assert.Equal(pipClose.Rect, modeClose.Rect);
    }

    [Fact]
    public void Hover_glow_never_fills_the_picture_in_either_host()
    {
        foreach (var spec in new[] { PictureInPicture(400, 300), OverlayMode(400, 300) })
        {
            var ops = Draw(spec);
            // No translucent fill covers the frame: only the accent border and an unfilled glow ring.
            Assert.DoesNotContain(ops.Ops, o => o.Kind == "rect" && o.Fill == OverlayFrameChromePainter.Palette.Glow);
            Assert.DoesNotContain(ops.Ops, o => o.Kind == "rect" && o.Fill is not null && o.Rect.Contains(spec.Frame.Center) && o.Rect.Width >= spec.Frame.Width);
        }
    }

    [Fact]
    public void Overlay_mode_resize_places_position_and_size_in_one_native_call()
    {
        var overlays = ReadSource("src", "Glide.App", "MainWindow.Overlays.cs");
        var update = overlays[overlays.IndexOf("private void UpdateWholeAppOverlayResize", StringComparison.Ordinal)..];
        update = update[..update.IndexOf("private void ApplyWholeAppOverlayResizeBox", StringComparison.Ordinal)];
        Assert.Contains("ApplyWholeAppOverlayResizeBox(", update, StringComparison.Ordinal);
        Assert.DoesNotContain(" Width = Math.Max(MinWidth", update, StringComparison.Ordinal);
        Assert.DoesNotContain("Position = ", update, StringComparison.Ordinal);
        Assert.Contains("Platform.Native.User32.SetWindowPos(", overlays, StringComparison.Ordinal);
    }

    [Fact]
    public void Overlay_mode_edge_handles_crop_like_picture_in_picture_and_do_not_refit()
    {
        var overlays = ReadSource("src", "Glide.App", "MainWindow.Overlays.cs");
        Assert.Contains("Viewport.BeginEdgeCrop(", overlays, StringComparison.Ordinal);
        Assert.Contains("Viewport.UpdateEdgeCrop(", overlays, StringComparison.Ordinal);
        Assert.Contains("Viewport.EndEdgeCrop()", overlays, StringComparison.Ordinal);
        var viewport = ReadSource("src", "Glide.App", "Controls", "ImageViewport.cs");
        var begin = viewport[viewport.IndexOf("public bool BeginEdgeCrop", StringComparison.Ordinal)..];
        begin = begin[..begin.IndexOf("public void UpdateEdgeCrop", StringComparison.Ordinal)];
        Assert.Contains("_fitMode = FitMode.Manual;", begin, StringComparison.Ordinal); // frozen scale: SizeChanged cannot refit
        // The size rules are the picture-in-picture ones: an edge handle changes one axis, whatever the aspect flag says.
        var width = OverlayResizeMath.Compute(OverlayResizeMode.WidthOnly, 400, 300, -100, 80, preserveAspect: true);
        Assert.Equal(300, width.Width); Assert.Equal(300, width.Height);
        var height = OverlayResizeMath.Compute(OverlayResizeMode.HeightOnly, 400, 300, 50, 100, preserveAspect: true);
        Assert.Equal(400, height.Width); Assert.Equal(200, height.Height); Assert.Equal(100, height.TopShift);
    }

    [Fact]
    public void Overlay_mode_has_no_chrome_of_its_own_beyond_the_picture_in_picture_one()
    {
        var mode = OverlayMode(400, 300);
        var pip = PictureInPicture(400, 300);
        // Same controls, glow and handles as a picture-in-picture overlay: no always-on-top pin, no minimum-opacity label.
        Assert.NotNull(mode.WidthHandle);
        Assert.NotNull(mode.HeightHandle);
        Assert.NotNull(mode.ResizeGrip);
        Assert.True(mode.Glow);
        Assert.Equal(pip.WidthHandle, mode.WidthHandle);
        Assert.Equal(pip.HeightHandle, mode.HeightHandle);
        Assert.Equal(pip.ResizeGrip, mode.ResizeGrip);
        Assert.Equal(pip.KnobY, mode.KnobY);
        var specType = typeof(OverlayFrameChromeSpec);
        Assert.Null(specType.GetProperty("Topmost"));
        Assert.Null(specType.GetProperty("SliderMinLabel"));
    }

    [Fact]
    public void Every_overlay_renderer_and_overlay_mode_draw_through_the_shared_painter()
    {
        foreach (var file in new[] { "WindowInWindowOverlayManager.cs", "CompositorOverlayRenderer.cs", "LegacyOverlayChrome.cs" })
        {
            var source = ReadSource("src", "Glide.App", "Controls", file);
            Assert.Contains("OverlayFrameChromePainter.Draw(context,", source, StringComparison.Ordinal);
            Assert.DoesNotContain("DrawZoomButton(", source, StringComparison.Ordinal);
        }
        Assert.Contains("OverlayFrameChromePainter.Palette.Glass", ReadSource("src", "Glide.App", "Controls", "NativeOverlayWindowRenderer.cs"), StringComparison.Ordinal);
        // Overlay mode still builds its chrome from LegacyOverlayChrome, plus its two additions: the drag strip and the context menu.
        var overlays = ReadSource("src", "Glide.App", "MainWindow.Overlays.cs");
        Assert.Contains("new LegacyOverlayChrome", overlays, StringComparison.Ordinal);
        Assert.Contains("EnsureWholeAppOverlayDragStrip", overlays, StringComparison.Ordinal);
        Assert.Contains("setting.overlay.wholeAppShowDragStrip", overlays, StringComparison.Ordinal);
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
        throw new FileNotFoundException("Could not locate source file.", Path.Combine(segments));
    }
}
