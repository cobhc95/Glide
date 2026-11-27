using Avalonia;
using Glide.App.Controls;

namespace Glide.Core.Tests;

/// <summary>
/// 5.2.2 Overlay regressions after 66d295d: (1) the invisible part of an Overlay window (letterbox around a smaller picture) took clicks
/// meant for the windows underneath (the window region is cut to the visible frames); (2) re-entering Overlay mode could bring the window back far larger than the picture's frame;
/// (3) the resize handles are the pointer-driven resize again (the native sizing loop is gone), with the unpainted-step counter kept.
/// </summary>
[Trait("Area", "Overlay")]
public sealed class OverlayClickThroughAndRestoreTests
{
    private static readonly Size WindowDip = new(800, 600);

    private static IReadOnlyList<RegionRect>? Region(double scale, params Rect[] frames) =>
        OverlayInputRegion.Compute(frames, scale, (int)Math.Round(WindowDip.Width * scale), (int)Math.Round(WindowDip.Height * scale));

    [Theory]
    [InlineData(1.0)]
    [InlineData(1.25)]
    [InlineData(1.5)]
    [InlineData(2.0)]
    public void Region_never_claims_a_pixel_outside_the_visible_frame_and_keeps_every_pixel_of_it(double scale)
    {
        var picture = new Rect(150.3, 120.6, 240.2, 135.9); // a small, fractionally placed picture inside a bigger transparent window
        var region = Region(scale, picture);
        Assert.NotNull(region);
        var width = (int)Math.Round(WindowDip.Width * scale);
        var height = (int)Math.Round(WindowDip.Height * scale);
        for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
            {
                var centre = new Point((x + 0.5) / scale, (y + 0.5) / scale);
                var claimed = OverlayInputRegion.Contains(region, x, y);
                if (picture.Contains(centre)) Assert.True(claimed, $"visible pixel ({x},{y}) is not in the region");
                // One pixel of outward rounding at the picture's edge is allowed; anything further out must pass the click through.
                else if (!picture.Inflate(1.0 / scale).Contains(centre))
                    Assert.False(claimed, $"({x},{y}) is outside the picture but inside the region");
            }
    }

    [Fact]
    public void Region_is_the_union_of_the_picture_and_the_picture_in_picture_frames()
    {
        var picture = new Rect(100, 100, 200, 150);
        var pip = new Rect(500, 400, 150, 100);
        var region = Region(1.0, picture, pip);
        Assert.Equal(2, region!.Count);
        Assert.True(OverlayInputRegion.Contains(region, 150, 150));
        Assert.True(OverlayInputRegion.Contains(region, 520, 420));
        Assert.False(OverlayInputRegion.Contains(region, 400, 300)); // between them
        Assert.False(OverlayInputRegion.Contains(region, 799, 599));
    }

    [Fact]
    public void No_region_when_a_frame_fills_the_window_or_nothing_is_laid_out()
    {
        Assert.Null(Region(1.0, new Rect(0, 0, 800, 600)));
        Assert.Null(Region(1.5, new Rect(0, 0, 800, 600)));
        Assert.Null(Region(1.0, new Rect(0, 0, 800, 600), new Rect(10, 10, 20, 20))); // the filling picture wins
        Assert.Null(Region(1.0));
        Assert.Null(Region(1.0, new Rect(10, 10, 0, 50)));
        Assert.Null(OverlayInputRegion.Compute(new[] { new Rect(0, 0, 10, 10) }, 1.0, 0, 0));
        Assert.True(OverlayInputRegion.Contains(null, 12345, 5)); // no region = the whole window, as before
    }

    [Fact]
    public void Region_is_clipped_to_the_window_and_follows_dpi_scaling()
    {
        var overhang = Region(1.0, new Rect(700, 500, 400, 300))!.Single();
        Assert.Equal(new RegionRect(700, 500, 800, 600), overhang);
        var scaled = Region(1.5, new Rect(100, 100, 200, 100))!.Single();
        Assert.Equal(new RegionRect(150, 150, 450, 300), scaled);
        Assert.True(OverlayInputRegion.Same(Region(1.5, new Rect(100, 100, 200, 100)), new[] { scaled }));
        Assert.False(OverlayInputRegion.Same(Region(1.0, new Rect(100, 100, 200, 100)), new[] { scaled }));
    }

    [Fact]
    public void Main_window_cuts_its_hwnd_to_the_region_updates_it_with_the_layout_and_clears_it_on_exit()
    {
        var overlays = ReadSource("src", "Glide.App", "MainWindow.Overlays.cs");
        var update = overlays[overlays.IndexOf("private void UpdateWholeAppOverlayInputRegion()", StringComparison.Ordinal)..];
        update = update[..update.IndexOf("private void AlignWholeAppOverlayChromeToPixels", StringComparison.Ordinal)];
        // Only Overlay mode in a normal window restricts input; the picture, the picture-in-picture frames and DPI scaling feed it.
        Assert.Contains("_wholeAppOverlayMode && WindowState == WindowState.Normal", update, StringComparison.Ordinal);
        Assert.Contains("TryGetWholeAppOverlayPixelRect(out var picture)", update, StringComparison.Ordinal);
        Assert.Contains("overlays.FrameRects()", update, StringComparison.Ordinal);
        Assert.Contains("overlays.IsAnimating", update, StringComparison.Ordinal);
        Assert.Contains("RenderScaling", update, StringComparison.Ordinal);
        Assert.Contains("OverlayInputRegion.Compute(", update, StringComparison.Ordinal);
        Assert.Contains("Platform.OverlayWindowRegion.Apply(handle.Handle, wanted)", update, StringComparison.Ordinal);
        Assert.Contains("catch (Exception ex)", update, StringComparison.Ordinal);

        // Every chrome alignment (resize flush, zoom wheel, hover, SizeChanged, aspect toggle) refreshes the region too.
        var align = overlays[overlays.IndexOf("private void AlignWholeAppOverlayChromeToPixels()", StringComparison.Ordinal)..];
        align = align[..align.IndexOf("private bool IsPointerOverWholeAppOverlayPixels", StringComparison.Ordinal)];
        Assert.Contains("UpdateWholeAppOverlayInputRegion();", align, StringComparison.Ordinal);
        var flush = overlays[overlays.IndexOf("private void FlushWholeAppOverlayResize()", StringComparison.Ordinal)..];
        flush = flush[..flush.IndexOf("private void ApplyChromeSizingAfterWholeAppOverlayResize", StringComparison.Ordinal)];
        Assert.True(flush.IndexOf("ApplyWholeAppOverlayResizeBox(", StringComparison.Ordinal) < flush.IndexOf("AlignWholeAppOverlayChromeToPixels();", StringComparison.Ordinal));

        // Cleared when the mode ends.
        var exit = overlays[overlays.IndexOf("private void ExitWholeAppOverlayMode", StringComparison.Ordinal)..];
        exit = exit[..exit.IndexOf("if (_wholeAppOverlayControlsHost is { } overlayControls)", StringComparison.Ordinal)];
        Assert.True(exit.IndexOf("_wholeAppOverlayMode = false;", StringComparison.Ordinal) < exit.IndexOf("UpdateWholeAppOverlayInputRegion();", StringComparison.Ordinal));

        // Zoom / pan / fit and picture-in-picture changes arrive as events.
        Assert.Contains("QueueWholeAppOverlayRegionUpdate();", ReadSource("src", "Glide.App", "MainWindow.axaml.cs"), StringComparison.Ordinal);
        var startup = ReadSource("src", "Glide.App", "MainWindow.Startup.cs");
        Assert.Contains("overlays.LayoutChanged += QueueWholeAppOverlayRegionUpdate;", startup, StringComparison.Ordinal);
        Assert.Contains("overlays.OverlayCountChanged += QueueWholeAppOverlayRegionUpdate;", startup, StringComparison.Ordinal);

        var native = ReadSource("src", "Glide.App", "Platform", "OverlayWindowRegion.cs");
        Assert.Contains("SetWindowRgn(hwnd, IntPtr.Zero, true)", native, StringComparison.Ordinal); // cleared
        Assert.Contains("SetWindowRgn(hwnd, region, true)", native, StringComparison.Ordinal);
    }

    [Fact]
    public void Overlay_manager_reports_layout_changes_for_the_region()
    {
        var manager = ReadSource("src", "Glide.App", "Controls", "WindowInWindowOverlayManager.cs");
        Assert.Contains("public event Action? LayoutChanged;", manager, StringComparison.Ordinal);
        Assert.Contains("public bool IsAnimating => _animations.Values.Any(a => a.IsRunning);", manager, StringComparison.Ordinal);
        var invalidate = manager[manager.IndexOf("private void Invalidate()", StringComparison.Ordinal)..];
        Assert.Contains("LayoutChanged?.Invoke();", invalidate[..invalidate.IndexOf("UpdateAreaAdorner();", StringComparison.Ordinal)], StringComparison.Ordinal);
        Assert.True(CountOf(manager, "LayoutChanged?.Invoke();") >= 5); // invalidate, remove, animation start / stop / stop all
    }

    private static int CountOf(string text, string needle)
    {
        var count = 0;
        for (var i = text.IndexOf(needle, StringComparison.Ordinal); i >= 0; i = text.IndexOf(needle, i + needle.Length, StringComparison.Ordinal)) count++;
        return count;
    }

    [Fact]
    public void Picture_in_picture_windows_stay_click_through_and_no_larger_than_their_frame()
    {
        var native = ReadSource("src", "Glide.App", "Controls", "NativeOverlayWindowRenderer.cs");
        // Layered + transparent + disabled: the kernel skips the window for hit testing, whatever its alpha (glow pixels included).
        Assert.Contains("WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE", native, StringComparison.Ordinal);
        Assert.Contains("WS_POPUP | WS_DISABLED", native, StringComparison.Ordinal);
        Assert.Contains("message == WM_NCHITTEST ? new IntPtr(-1)", native, StringComparison.Ordinal);
        // The window is exactly the frame's size: no glow / handle margin around it.
        Assert.Contains("var width = Math.Max(1, (int)Math.Round(item.Width * scale)); var height = Math.Max(1, (int)Math.Round(item.Height * scale));", native, StringComparison.Ordinal);
        Assert.DoesNotContain("SetWindowLong", native, StringComparison.Ordinal); // nothing ever strips WS_EX_TRANSPARENT later
    }

    [Fact]
    public void Overlay_mode_pulls_its_frame_and_glow_inside_the_window()
    {
        // The window has no room outside its bounds: the frame is inset by the glow's reach, so nothing is drawn (or hit) beyond it.
        var spec = new LegacyOverlayChrome { OpacityValue = 1 }.BuildSpec(new Size(400, 300));
        Assert.Equal(new Rect(OverlayFrameGeometry.GlowReach, OverlayFrameGeometry.GlowReach, 400 - 2 * OverlayFrameGeometry.GlowReach, 300 - 2 * OverlayFrameGeometry.GlowReach), spec.Frame);
    }

    // ---- D: the size restored on re-entering Overlay mode ---------------------------------------------------------

    private static readonly PixelBox WorkArea = new(0, 0, 1920, 1040);

    [Fact]
    public void Restore_keeps_the_previous_size_when_it_fits()
    {
        Assert.Equal((390.0, 220.0), OverlayWindowSizing.RestoreSize(390, 220, WorkArea, 1.0));
    }

    [Fact]
    public void Restore_clamps_an_oversized_remembered_size_to_the_work_area_keeping_the_ratio()
    {
        var (w, h) = OverlayWindowSizing.RestoreSize(6000, 3000, WorkArea, 1.0)!.Value;
        Assert.True(w <= 1920 && h <= 1040);
        Assert.Equal(2.0, w / h, 3);
        // 150 % scaling: the work area is 1280 x 693 DIP.
        var (w2, h2) = OverlayWindowSizing.RestoreSize(5000, 4000, WorkArea, 1.5)!.Value;
        Assert.True(w2 <= 1920 / 1.5 + 1e-9 && h2 <= 1040 / 1.5 + 1e-9);
        Assert.Equal(1.25, w2 / h2, 3);
    }

    [Theory]
    [InlineData(double.NaN, 100)]
    [InlineData(100, double.PositiveInfinity)]
    [InlineData(0, 100)]
    [InlineData(-5, 100)]
    public void Restore_rejects_sizes_that_are_not_finite_positive_numbers(double width, double height) =>
        Assert.Null(OverlayWindowSizing.RestoreSize(width, height, WorkArea, 1.0));

    [Fact]
    public void Restore_never_goes_below_the_minimum_size()
    {
        var (w, h) = OverlayWindowSizing.RestoreSize(20, 10, WorkArea, 1.0)!.Value;
        Assert.True(w >= 80 && h >= 80);
    }

    [Fact]
    public void Entering_overlay_mode_restores_through_the_clamp_and_transient_sizes_are_not_remembered()
    {
        var chrome = ReadSource("src", "Glide.App", "MainWindow.ViewerChrome.cs");
        var apply = chrome[chrome.IndexOf("private void ApplyWholeAppOverlayPlacementForCurrentImage", StringComparison.Ordinal)..];
        apply = apply[..apply.IndexOf("private void ApplyTopmostState", StringComparison.Ordinal)];
        Assert.Contains("OverlayWindowSizing.RestoreSize(", apply, StringComparison.Ordinal);
        Assert.True(apply.IndexOf("OverlayWindowSizing.RestoreSize(", StringComparison.Ordinal) < apply.IndexOf("Width = Math.Max(80, width);", StringComparison.Ordinal));
        Assert.Contains("_wholeAppOverlayPlacementSettleTick = Environment.TickCount64", apply, StringComparison.Ordinal);

        var save = chrome[chrome.IndexOf("private void SaveCurrentWholeAppOverlayPlacement", StringComparison.Ordinal)..];
        save = save[..save.IndexOf("private void ApplyWholeAppOverlayPlacementForCurrentImage", StringComparison.Ordinal)];
        Assert.Contains("!force && (WholeAppOverlayResizing || Environment.TickCount64 < _wholeAppOverlayPlacementSettleTick)", save, StringComparison.Ordinal);

        var overlays = ReadSource("src", "Glide.App", "MainWindow.Overlays.cs");
        var exit = overlays[overlays.IndexOf("private void ExitWholeAppOverlayMode", StringComparison.Ordinal)..];
        Assert.Contains("SaveCurrentWholeAppOverlayPlacement(force: true);", exit[..exit.IndexOf("_wholeAppOverlayMode = false;", StringComparison.Ordinal)], StringComparison.Ordinal);
        Assert.Contains("SaveCurrentWholeAppOverlayPlacement(force: true);", overlays[overlays.IndexOf("private bool EndWholeAppOverlayResize", StringComparison.Ordinal)..], StringComparison.Ordinal);
    }

    // ---- A: the pointer-driven resize is back --------------------------------------------------------------------

    [Fact]
    public void Overlay_handles_resize_from_pointer_moves_once_per_frame_and_not_on_windows_sizing_loop()
    {
        var overlays = ReadSource("src", "Glide.App", "MainWindow.Overlays.cs");
        Assert.DoesNotContain("BeginResizeDrag", overlays, StringComparison.Ordinal);
        Assert.DoesNotContain("WM_SIZING", overlays, StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(Path.Combine(FindRepoRoot(), "src", "Glide.App", "Controls", "OverlayNativeResize.cs")));
        Assert.Contains("e.Pointer.Capture(hit);", overlays, StringComparison.Ordinal);
        Assert.Contains("RequestAnimationFrame(_ => { _wholeAppOverlayResizeFrameQueued = false; FlushWholeAppOverlayResize(); });", overlays, StringComparison.Ordinal);
        // The handle semantics of the previous build: width only, height only with the bottom edge fixed, corner ratio, crop, minimum, cap.
        Assert.Contains("OverlayResizeMath.Compute(session.Mode,", overlays, StringComparison.Ordinal);
        Assert.Contains("Math.Max(MinWidth, size.Width)", overlays, StringComparison.Ordinal);
        Assert.Contains("cropImage.Width - cropView.Width", overlays, StringComparison.Ordinal);
        Assert.Contains("size.TopShift", overlays, StringComparison.Ordinal);
    }

    [Fact]
    public void Resize_end_reports_the_unpainted_step_counters()
    {
        var overlays = ReadSource("src", "Glide.App", "MainWindow.Overlays.cs");
        var end = overlays[overlays.IndexOf("private bool EndWholeAppOverlayResize", StringComparison.Ordinal)..];
        foreach (var field in new[] { "\"overlay_window_resize_end\"", "sizeSteps", "paintedSteps", "unpaintedSteps", "unpaintedPercent" })
            Assert.Contains(field, end, StringComparison.Ordinal);
        Assert.Contains("Win32Properties.AddWndProcHookCallback(this, OverlayWndProcHook)", overlays, StringComparison.Ordinal);
    }

    [Fact]
    public void Sync_trace_counts_steps_that_were_never_painted_before_the_next_step()
    {
        var painted = new OverlayResizeSyncTrace();
        for (var i = 0; i < 5; i++) { painted.OnSize(); painted.OnPaint(); }
        Assert.Equal((5, 5, 0), (painted.Steps, painted.Paints, painted.UnpaintedSteps));
        Assert.Equal(0, painted.UnpaintedPercent);

        var starved = new OverlayResizeSyncTrace();
        for (var i = 0; i < 4; i++) starved.OnSize();
        starved.OnPaint();
        Assert.Equal((4, 1, 3), (starved.Steps, starved.Paints, starved.UnpaintedSteps));
        Assert.Equal(75, starved.UnpaintedPercent);
        starved.OnPaint();
        Assert.Equal(1, starved.Paints);
    }

    private static string FindRepoRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "AGENTS.md"))) return directory.FullName;
        throw new DirectoryNotFoundException("Could not locate the repository root.");
    }

    private static string ReadSource(params string[] segments) => SourceText.Read(Path.Combine(new[] { FindRepoRoot() }.Concat(segments).ToArray()));
}
