using Avalonia;
using Glide.App.Controls;

namespace Glide.Core.Tests;

/// <summary>
/// Overlay mode resize glitches (5.2.2): a ghost / double frame while dragging a handle, the picture past the border, the chrome squashed
/// or outside the window at small sizes, and a frame that was not drawn after an edge crop. Source-order and geometry tests; nothing
/// here starts Glide.exe. (Frames smaller than the full chrome needs, such as the 80 x 60 minimum, leave the secondary controls out in
/// Overlay mode only, so that size is no longer part of the "both hosts draw identical chrome" theory.)
/// </summary>
[Trait("Area", "Overlay")]
public sealed class OverlayResizeStabilityTests
{
    private static string Method(string text, string start, string end)
    {
        var from = text.IndexOf(start, StringComparison.Ordinal);
        Assert.True(from >= 0, start);
        var rest = text[from..];
        var to = rest.IndexOf(end, start.Length, StringComparison.Ordinal);
        Assert.True(to > 0, end);
        return rest[..to];
    }

    private static void InOrder(string text, params string[] needles)
    {
        var last = -1;
        foreach (var needle in needles)
        {
            var at = text.IndexOf(needle, last + 1, StringComparison.Ordinal);
            Assert.True(at > last, $"'{needle}' is missing or out of order");
            last = at;
        }
    }

    // ---- order of operations in the frame callback ------------------------------------------------------------

    [Fact]
    public void Frame_callback_runs_crop_then_size_then_layout_then_chrome_then_layout()
    {
        var overlays = ReadSource("src", "Glide.App", "MainWindow.Overlays.cs");
        var flush = Method(overlays, "private void FlushWholeAppOverlayResize()", "private void ApplyChromeSizingAfterWholeAppOverlayResize");
        InOrder(flush,
            "Viewport.UpdateEdgeCrop(",
            "ApplyWholeAppOverlayResizeBox(",
            "UpdateLayout();",                          // the viewport has its new bounds before anything reads the picture rect
            "AlignWholeAppOverlayChromeToPixels();",    // chrome / border from that picture rect
            "UpdateLayout();");                         // the chrome host is arranged in the same frame
        Assert.Equal(2, CountOf(flush, "UpdateLayout();"));
        // Nothing in the callback touches the window region: it is suspended for the drag.
        Assert.DoesNotContain("SetWindowRgn", flush, StringComparison.Ordinal);
        Assert.DoesNotContain("OverlayWindowRegion", flush, StringComparison.Ordinal);
    }

    [Fact]
    public void Chrome_host_is_the_picture_rectangle_read_after_layout_and_clipped_to_the_window()
    {
        var overlays = ReadSource("src", "Glide.App", "MainWindow.Overlays.cs");
        var align = Method(overlays, "private void AlignWholeAppOverlayChromeToPixels()", "private bool IsPointerOverWholeAppOverlayPixels");
        InOrder(align,
            "TryGetWholeAppOverlayPixelRect(out var rect)",
            "OverlayFrameGeometry.HostRect(rect, WorkspaceLayer.Bounds.Size)",
            "controls.Width = rect.Width;",
            "controls.Height = rect.Height;",
            "controls.Margin = new Thickness(rect.X, rect.Y, 0, 0);",
            "UpdateWholeAppOverlayInputRegion();");
        // The border and handles follow the picture rect, and so does the drag strip.
        Assert.Contains("RefreshWholeAppOverlayDragStrip();", align, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0, 0, 400, 300, 400, 300)]       // picture fills the window
    [InlineData(40, 30, 320, 180, 400, 300)]     // letterboxed picture
    [InlineData(-30, -20, 500, 400, 400, 300)]   // picture larger than the window (zoomed, cropped): clipped
    [InlineData(300, 200, 400, 300, 400, 300)]   // overhangs right and bottom
    public void Border_rect_equals_the_picture_rect_clipped_to_the_window(double x, double y, double w, double h, double ww, double wh)
    {
        var picture = new Rect(x, y, w, h);
        var host = OverlayFrameGeometry.HostRect(picture, new Size(ww, wh));
        var window = new Rect(0, 0, ww, wh);
        Assert.Equal(picture.Intersect(window), host);
        Assert.True(host.X >= 0 && host.Y >= 0 && host.Right <= ww + 1e-9 && host.Bottom <= wh + 1e-9);
        // The frame is drawn on that rect, pulled in by the glow reach and nowhere else.
        var spec = OverlayFrameGeometry.BuildSpec(new Rect(default, host.Size), 1, glow: true, frameInset: OverlayFrameGeometry.GlowReach, adaptToSize: true);
        Assert.Equal(new Rect(default, host.Size).Deflate(OverlayFrameGeometry.GlowReach), spec.Frame);
    }

    // ---- region suspended during a drag, re-applied after ------------------------------------------------------

    [Fact]
    public void Region_is_suspended_for_the_drag_and_applied_again_after_it()
    {
        var overlays = ReadSource("src", "Glide.App", "MainWindow.Overlays.cs");
        var update = Method(overlays, "private void UpdateWholeAppOverlayInputRegion()", "private void AlignWholeAppOverlayChromeToPixels");
        Assert.Contains("_wholeAppOverlayMode && WindowState == WindowState.Normal && !WholeAppOverlayResizing", update, StringComparison.Ordinal);
        // Not queued while a drag runs either (the end of the drag aligns once).
        var queue = Method(overlays, "private void QueueWholeAppOverlayRegionUpdate()", "private void UpdateWholeAppOverlayInputRegion");
        Assert.Contains("|| WholeAppOverlayResizing) return;", queue, StringComparison.Ordinal);

        // Begin: the session exists first (so "resizing" is true), then the region is cleared once.
        var begin = Method(overlays, "private void BeginWholeAppOverlayResize(", "private bool _overlayWndProcHookInstalled");
        InOrder(begin, "_wholeAppOverlayResize = new WholeAppOverlayResizeSession(", "Viewport.BeginResizeQuality();", "UpdateWholeAppOverlayInputRegion();", "e.Pointer.Capture(hit);");

        // End: the last frame, then the session ends, then the chrome and region of the final layout.
        var end = Method(overlays, "private bool EndWholeAppOverlayResize", "/// <summary>Sets the whole-app Overlay opacity");
        InOrder(end, "FlushWholeAppOverlayResize();", "_wholeAppOverlayResize = null;", "Viewport.EndResizeQuality();", "UpdateLayout();", "AlignWholeAppOverlayChromeToPixels();",
            "PublishViewportDemand();", "EnsurePresentedFrameSufficient(\"overlay_resize_end\");", "\"overlay_window_resize_end\"");
    }

    [Fact]
    public void A_step_never_runs_the_per_step_work_that_made_the_drag_refresh_everything()
    {
        var window = ReadSource("src", "Glide.App", "MainWindow.axaml.cs");
        var sizeChanged = Method(window, "SizeChanged += (_, _) =>", "PositionChanged +=");
        // The guard comes before the input reset / interaction cancel (CancelInteraction ends the viewport's cheap drag sampling).
        var guard = sizeChanged.IndexOf("if (WholeAppOverlayResizing) return;", StringComparison.Ordinal);
        Assert.True(guard >= 0 && guard < sizeChanged.IndexOf("Viewport.CancelInteraction();", StringComparison.Ordinal));
        Assert.True(guard < sizeChanged.IndexOf("ScheduleResizeInputReset();", StringComparison.Ordinal));

        // The presented-frame check (a possible full decode) is held back during the drag, and counted.
        var nav = ReadSource("src", "Glide.App", "MainWindow.Navigation.cs");
        var ensure = Method(nav, "private void EnsurePresentedFrameSufficient(string reason)", "internal const int MaxSufficiencyRecoveryAttempts");
        InOrder(ensure, "_wholeAppOverlayResize is { } overlayResize", "OnRefineDeferred()", "PresentedFrameWidths()", "SchedulePreviewQualityRecovery(");

        // The viewport draws its retained bitmap with cheap sampling during the drag and once at full quality after it.
        var viewport = ReadSource("src", "Glide.App", "Controls", "ImageViewport.cs");
        Assert.Contains("public void BeginResizeQuality()", viewport, StringComparison.Ordinal);
        Assert.Contains("public void EndResizeQuality()", viewport, StringComparison.Ordinal);
        Assert.Contains("RenderCount++;", viewport, StringComparison.Ordinal);
    }

    [Fact]
    public void Resize_end_trace_counts_renders_decodes_region_applies_and_chrome_aligns()
    {
        var overlays = ReadSource("src", "Glide.App", "MainWindow.Overlays.cs");
        var end = Method(overlays, "private bool EndWholeAppOverlayResize", "/// <summary>Sets the whole-app Overlay opacity");
        foreach (var field in new[] { "viewportRenders", "presentRequests", "bitmapSwaps", "deferredRefineChecks", "fullRendersOrDecodes", "regionApplies", "chromeAligns", "frames" })
            Assert.Contains(field, end, StringComparison.Ordinal);

        var trace = new OverlayResizeSyncTrace();
        for (var i = 0; i < 6; i++) { trace.OnFlush(); trace.OnChromeAligned(); trace.OnRefineDeferred(); }
        trace.OnRegionApplied();
        Assert.Equal((6, 6, 6, 1), (trace.Flushes, trace.ChromeAligns, trace.DeferredRefines, trace.RegionApplies));
        Assert.Equal(0, OverlayResizeSyncTrace.CountDelta(10, 10));
        Assert.Equal(3, OverlayResizeSyncTrace.CountDelta(13, 10));
        Assert.Equal(0, OverlayResizeSyncTrace.CountDelta(9, 10));
    }

    // ---- chrome inside the window and non-overlapping at every size ------------------------------------------

    public static IEnumerable<object[]> Sizes()
    {
        foreach (var w in new[] { 80.0, 100, 119, 120, 131, 132, 160, 250, 400, 1000 })
            foreach (var h in new[] { 60.0, 71, 72, 100, 115, 116, 149, 150, 300, 800 })
                yield return new object[] { w, h };
    }

    private static List<(string Name, Rect Rect)> VisibleControls(double w, double h)
    {
        var size = new Size(w, h);
        var frame = new Rect(default, size);
        var parts = OverlayFrameGeometry.PartsFor(size);
        var list = new List<(string, Rect)>();
        if (parts.Close) list.Add(("close", OverlayFrameGeometry.Close(frame)));
        if (parts.Grip) list.Add(("grip", OverlayFrameGeometry.GripVisual(frame)));
        if (parts.WidthHandle) list.Add(("widthHandle", OverlayFrameGeometry.WidthHandle(frame)));
        if (parts.HeightHandle) list.Add(("heightHandle", OverlayFrameGeometry.HeightHandle(frame)));
        if (parts.Slider) list.Add(("slider", OverlayFrameGeometry.Slider(frame)));
        if (parts.Zoom) { list.Add(("zoomOut", OverlayFrameGeometry.ZoomOut(frame))); list.Add(("zoomIn", OverlayFrameGeometry.ZoomIn(frame))); }
        return list;
    }

    [Theory]
    [MemberData(nameof(Sizes))]
    public void Every_visible_control_lies_inside_the_window_and_none_overlap(double w, double h)
    {
        var controls = VisibleControls(w, h);
        foreach (var (name, rect) in controls)
            Assert.True(rect.X >= -0.001 && rect.Y >= -0.001 && rect.Right <= w + 0.001 && rect.Bottom <= h + 0.001, $"{name} {rect} leaves {w}x{h}");
        for (var i = 0; i < controls.Count; i++)
            for (var j = i + 1; j < controls.Count; j++)
                Assert.False(controls[i].Rect.Intersects(controls[j].Rect), $"{controls[i].Name} {controls[i].Rect} overlaps {controls[j].Name} {controls[j].Rect} at {w}x{h}");
    }

    [Fact]
    public void At_the_minimum_size_close_stays_and_the_secondary_controls_are_left_out()
    {
        var parts = OverlayFrameGeometry.PartsFor(new Size(OverlayResizeMath.MinimumWidth, OverlayResizeMath.MinimumHeight));
        Assert.True(parts.Close);
        Assert.False(parts.Slider);
        Assert.False(parts.Zoom);
        Assert.False(parts.WidthHandle);
        Assert.False(parts.HeightHandle);
        Assert.Equal(OverlayChromeParts.All, OverlayFrameGeometry.PartsFor(new Size(400, 300)));
        // A frame too small for even the close button draws no chrome at all rather than a squashed one.
        Assert.Equal(new OverlayChromeParts(false, false, false, false, false, false), OverlayFrameGeometry.PartsFor(new Size(30, 30)));
    }

    [Fact]
    public void Overlay_mode_chrome_draws_only_the_controls_that_fit_while_picture_in_picture_keeps_all()
    {
        var overlayMode = OverlayFrameGeometry.BuildSpec(new Rect(0, 0, 80, 60), 1, glow: true, frameInset: OverlayFrameGeometry.GlowReach, adaptToSize: true);
        Assert.True(overlayMode.ShowClose);
        Assert.False(overlayMode.ShowSlider);
        Assert.False(overlayMode.ShowZoom);
        Assert.Null(overlayMode.ResizeGrip);
        Assert.Null(overlayMode.WidthHandle);
        Assert.Null(overlayMode.HeightHandle);
        var pip = OverlayFrameGeometry.BuildSpec(new Rect(0, 0, 80, 60), 1, glow: true);
        Assert.True(pip.ShowClose && pip.ShowSlider && pip.ShowZoom);
        Assert.NotNull(pip.ResizeGrip);
        Assert.NotNull(pip.WidthHandle);
        Assert.NotNull(pip.HeightHandle);
    }

    [Fact]
    public void Host_places_hit_areas_from_the_fitting_controls_and_keeps_a_dragged_handle_alive()
    {
        var overlays = ReadSource("src", "Glide.App", "MainWindow.Overlays.cs");
        var placing = Method(overlays, "host.SizeChanged += (_, _) =>", "WorkspaceLayer.Children.Add(host);");
        Assert.Contains("OverlayFrameGeometry.PartsFor(size)", placing, StringComparison.Ordinal);
        Assert.Contains("Place(gripHit, OverlayFrameGeometry.GripHit(frame), parts.Grip);", placing, StringComparison.Ordinal);
        Assert.Contains("Place(widthHit, OverlayFrameGeometry.WidthHandle(frame), parts.WidthHandle);", placing, StringComparison.Ordinal);
        Assert.Contains("Place(heightHit, OverlayFrameGeometry.HeightHandle(frame), parts.HeightHandle);", placing, StringComparison.Ordinal);
        Assert.DoesNotContain("gripHit.IsVisible", placing, StringComparison.Ordinal);   // a handle in use keeps its capture
    }

    // ---- handle semantics are unchanged ------------------------------------------------------------------------

    [Fact]
    public void Handle_semantics_width_only_height_only_corner_and_stretch_are_unchanged()
    {
        var width = OverlayResizeMath.Compute(OverlayResizeMode.WidthOnly, 400, 300, -150, 90, preserveAspect: true);
        Assert.Equal((250.0, 300.0, 0.0), (width.Width, width.Height, width.TopShift));
        var height = OverlayResizeMath.Compute(OverlayResizeMode.HeightOnly, 400, 300, 80, -100, preserveAspect: true, maxHeight: 350);
        Assert.Equal(400, height.Width);
        Assert.Equal(350, height.Height);                       // growth cap
        Assert.Equal(-50, height.TopShift);                      // the bottom edge stays: the top moves
        var corner = OverlayResizeMath.Compute(OverlayResizeMode.Proportional, 400, 300, 100, 10, preserveAspect: true);
        Assert.Equal(400.0 / 300.0, corner.Width / corner.Height, 6);
        var stretch = OverlayResizeMath.Compute(OverlayResizeMode.Proportional, 400, 300, 100, 10, preserveAspect: false);
        Assert.Equal((500.0, 310.0), (stretch.Width, stretch.Height));
        var minimum = OverlayResizeMath.Compute(OverlayResizeMode.Proportional, 400, 300, -1000, -1000, preserveAspect: false);
        Assert.Equal((OverlayResizeMath.MinimumWidth, OverlayResizeMath.MinimumHeight), (minimum.Width, minimum.Height));
    }

    // ---- fixed transparent stage HWND during a resize drag -----------------------------------------------------

    [Fact]
    public void Stage_box_keeps_origin_fixed_for_proportional_and_width_only_and_expands_upward_only_for_height_growth()
    {
        var origin = new PixelPoint(300, 200);
        var start = new Size(400, 300);

        var corner = OverlayResizeStage.Compute(OverlayResizeMode.Proportional, origin, start, 1920, 1040, 1.5);
        Assert.True(corner.Active);
        Assert.Equal(origin, corner.Origin);
        Assert.Equal(new Size(1920, 1040), corner.Size);
        Assert.Equal(new Thickness(0), corner.ContentMargin(origin, 1.5));

        var widthUncropped = OverlayResizeStage.Compute(OverlayResizeMode.WidthOnly, origin, start, 400, 1040, 1.0);
        Assert.True(widthUncropped.Active);
        Assert.Equal(origin, widthUncropped.Origin);
        Assert.Equal(start, widthUncropped.Size);

        var widthCropped = OverlayResizeStage.Compute(OverlayResizeMode.WidthOnly, origin, start, 600, 1040, 1.0);
        Assert.True(widthCropped.Active);
        Assert.Equal(origin, widthCropped.Origin);
        Assert.Equal(new Size(600, 300), widthCropped.Size);

        // HeightOnly when uncropped can only shrink downward: stage matches start exactly (no initial HWND move).
        var heightUncropped = OverlayResizeStage.Compute(OverlayResizeMode.HeightOnly, origin, start, 1920, 300, 1.5);
        Assert.True(heightUncropped.Active);
        Assert.Equal(origin, heightUncropped.Origin);
        Assert.Equal(start, heightUncropped.Size);
        Assert.Equal(new Thickness(0, 100, 0, 0), heightUncropped.ContentMargin(new PixelPoint(300, 350), 1.5));

        // HeightOnly when cropped/stretch can grow upward: stage expands up by the maximum upward growth.
        var heightGrow = OverlayResizeStage.Compute(OverlayResizeMode.HeightOnly, origin, start, 1920, 420, 1.5);
        Assert.True(heightGrow.Active);
        Assert.Equal(new PixelPoint(300, 20), heightGrow.Origin); // 120 DIP * 1.5 = 180 px above Y=200
        Assert.Equal(new Size(400, 420), heightGrow.Size);
        Assert.Equal(new Thickness(0, 120, 0, 0), heightGrow.ContentMargin(origin, 1.5));

        // Non-finite screen bounds (headless tests without a monitor) deactivate staging cleanly.
        var headless = OverlayResizeStage.Compute(OverlayResizeMode.Proportional, origin, start, double.PositiveInfinity, double.PositiveInfinity, 1.0);
        Assert.False(headless.Active);
    }

    [Fact]
    public void Staged_resize_updates_ImageView_in_place_during_drag_and_commits_final_HWND_once_on_end()
    {
        var overlays = ReadSource("src", "Glide.App", "MainWindow.Overlays.cs");
        var flush = Method(overlays, "private void FlushWholeAppOverlayResize()", "private void ApplyChromeSizingAfterWholeAppOverlayResize");
        InOrder(flush,
            "if (session.Stage.Active)",
            "ImageView.Width = p.Width;",
            "ImageView.Height = p.Height;",
            "ImageView.Margin = session.Stage.ContentMargin(p.Origin, p.Scale);",
            "UpdateLayout();",
            "AlignWholeAppOverlayChromeToPixels();",
            "UpdateLayout();");

        var end = Method(overlays, "private bool EndWholeAppOverlayResize", "/// <summary>Sets the whole-app Overlay opacity");
        InOrder(end,
            "FlushWholeAppOverlayResize();",
            "if (session.Stage.Active)",
            "ImageView.Width = session.SavedImageWidth;",
            "ImageView.Margin = session.SavedImageMargin;",
            "ApplyWholeAppOverlayResizeBox(session.CurrentOrigin, session.CurrentSize.Width, session.CurrentSize.Height, session.Scale",
            "_wholeAppOverlayResize = null;");

        var exit = Method(overlays, "private void ExitWholeAppOverlayMode()", "private void RememberNormalPlacementAfterOverlay");
        Assert.Contains("if (_wholeAppOverlayResize is { } activeResize) EndWholeAppOverlayResize(activeResize.Pointer);", exit, StringComparison.Ordinal);
    }

    private static int CountOf(string text, string needle)
    {
        var count = 0;
        for (var i = text.IndexOf(needle, StringComparison.Ordinal); i >= 0; i = text.IndexOf(needle, i + needle.Length, StringComparison.Ordinal)) count++;
        return count;
    }

    private static string FindRepoRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "AGENTS.md"))) return directory.FullName;
        throw new DirectoryNotFoundException("Could not locate the repository root.");
    }

    private static string ReadSource(params string[] segments) => SourceText.Read(Path.Combine(new[] { FindRepoRoot() }.Concat(segments).ToArray()));
}
