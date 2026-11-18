using Avalonia;
using Glide.App.Controls;
using Glide.App.Platform;
using P = Glide.App.Platform.BrowserFrameHitTestPolicy;

namespace Glide.Core.Tests;

/// <summary>
/// The WM_NCHITTEST decision of the browser-style frame (WindowsBrowserFrameController), as a pure function with
/// injected rectangles and DPI scaling. 5.0.5 top-edge click report: every interactive title-bar control touching the
/// top edge must be HTCLIENT from its first pixel in every window state, while (a) the outermost Normal-state pixel
/// still resizes, (b) empty title bar defers (Avalonia/DefWindowProc: HTCLIENT, then Glide's drag/double-click),
/// (c) caption buttons keep native HT* codes for Snap Layouts, (d) ordinary moves never run the Avalonia hit test.
/// </summary>
[Trait("Area", "Explorer")]
public sealed class BrowserFrameHitTestPolicyTests
{
    private static readonly double[] Scales = { 1.0, 1.25, 1.5, 1.75, 2.0 };

    private static FrameHitTestInput Input(FrameWindowMode mode, double scale, PixelRect? window = null, int? monitorTop = null)
    {
        var w = window ?? new PixelRect(100, 50, 1600, 900);
        // Caption buttons: 3 x 46 DIP at the top-right, 44 DIP tall; title actions just left of them.
        var cw = (int)Math.Ceiling(46 * scale);
        var ch = (int)Math.Ceiling(44 * scale);
        var close = new PixelRect(w.Right - cw, w.Y, cw, ch);
        var max = new PixelRect(close.X - cw, w.Y, cw, ch);
        var min = new PixelRect(max.X - cw, w.Y, cw, ch);
        var actions = new PixelRect(min.X - (int)Math.Ceiling(120 * scale), w.Y + (int)Math.Round(4 * scale), (int)Math.Ceiling(114 * scale), (int)Math.Ceiling(36 * scale));
        return new FrameHitTestInput(mode, w, monitorTop ?? w.Y, scale, actions, close, max, min);
    }

    private static Func<int, int, bool> Always(bool value) => (_, _) => value;

    [Theory]
    [InlineData(1.0)]
    [InlineData(1.25)]
    [InlineData(1.5)]
    [InlineData(1.75)]
    public void Maximized_top_row_over_a_control_is_client_from_the_first_pixel(double scale)
    {
        var input = Input(FrameWindowMode.Maximized, scale);
        for (var row = 0; row < P.TopBand(scale); row++)
        {
            var result = P.Decide(input, 400, input.Window.Y + row, Always(true));
            Assert.Equal(P.HtClient, result.Code);
        }
    }

    [Fact]
    public void Maximized_empty_title_bar_defers_so_it_keeps_dragging()
    {
        var input = Input(FrameWindowMode.Maximized, 1.5);
        Assert.Equal(P.Defer, P.Decide(input, 700, input.Window.Y, Always(false)).Code);
    }

    [Theory]
    [InlineData(1.0)]
    [InlineData(1.25)]
    [InlineData(1.5)]
    [InlineData(1.75)]
    public void Normal_outermost_pixel_resizes_even_above_a_control_and_the_rest_of_the_band_belongs_to_it(double scale)
    {
        var input = Input(FrameWindowMode.Normal, scale);
        var top = input.Window.Y;
        Assert.Equal(P.HtTop, P.Decide(input, 400, top, Always(true)).Code);
        for (var row = P.TopPriorityPixels; row < P.ResizeEdge(scale); row++)
        {
            Assert.Equal(P.HtClient, P.Decide(input, 400, top + row, Always(true)).Code);
            Assert.Equal(P.HtTop, P.Decide(input, 400, top + row, Always(false)).Code);
        }
        // Corners of the outermost row resize diagonally.
        Assert.Equal(P.HtTopLeft, P.Decide(input, input.Window.X + 2, top, Always(true)).Code);
        Assert.Equal(P.HtTopRight, P.Decide(input, input.Window.Right - 2, top, Always(true)).Code);
    }

    [Fact]
    public void Normal_side_and_bottom_edges_still_resize()
    {
        var input = Input(FrameWindowMode.Normal, 1.5);
        var w = input.Window;
        Assert.Equal(P.HtLeft, P.Decide(input, w.X, w.Y + 300, Always(true)).Code);
        Assert.Equal(P.HtRight, P.Decide(input, w.Right - 1, w.Y + 300, Always(true)).Code);
        Assert.Equal(P.HtBottom, P.Decide(input, w.X + 300, w.Bottom - 1, Always(true)).Code);
        Assert.Equal(P.HtBottomLeft, P.Decide(input, w.X, w.Bottom - 1, Always(true)).Code);
        Assert.Equal(P.HtBottomRight, P.Decide(input, w.Right - 1, w.Bottom - 1, Always(true)).Code);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Caption_buttons_keep_native_codes_for_snap_layouts(bool maximized)
    {
        var mode = maximized ? FrameWindowMode.Maximized : FrameWindowMode.Normal;
        foreach (var scale in Scales)
        {
            var input = Input(mode, scale);
            var row = input.Window.Y + P.ResizeEdge(scale) + 2; // below any Normal resize band
            Assert.Equal(P.HtClose, P.Decide(input, input.Close!.Value.X + 5, row, Always(true)).Code);
            Assert.Equal(P.HtMaxButton, P.Decide(input, input.Maximize!.Value.X + 5, row, Always(true)).Code);
            Assert.Equal(P.HtMinButton, P.Decide(input, input.Minimize!.Value.X + 5, row, Always(true)).Code);
            if (mode == FrameWindowMode.Maximized)
            {
                // Maximized: the very top row of the caption buttons is still the button (Fitts's law).
                Assert.Equal(P.HtMaxButton, P.Decide(input, input.Maximize!.Value.X + 5, input.Window.Y, Always(false)).Code);
            }
        }
    }

    [Fact]
    public void Title_action_strip_wins_over_an_overlapping_stale_caption_rectangle()
    {
        var input = Input(FrameWindowMode.Maximized, 1.0);
        var actions = input.ProtectedClient!.Value;
        var stale = input with { Close = actions };
        Assert.Equal(P.HtClient, P.Decide(stale, actions.X + 3, actions.Y + 3, Always(false)).Code);
    }

    [Fact]
    public void Fullscreen_corner_is_close_and_everything_else_is_client()
    {
        foreach (var scale in Scales)
        {
            var input = new FrameHitTestInput(FrameWindowMode.FullScreen, new PixelRect(0, 0, 2560, 1440), 0, scale);
            Assert.Equal(P.HtClose, P.Decide(input, 2559, 0, Always(false)).Code);
            Assert.Equal(P.HtClient, P.Decide(input, 400, 0, Always(false)).Code);
            Assert.Equal(P.HtClient, P.Decide(input, 2559, P.FullscreenCloseHeight(scale), Always(false)).Code);
        }
    }

    [Fact]
    public void A_maximized_window_above_its_monitor_measures_the_band_from_the_monitor_top()
    {
        // Frame-thickness overhang: window rect starts 8 px above the monitor.
        var input = Input(FrameWindowMode.Maximized, 1.0, new PixelRect(-8, -8, 1936, 1056), monitorTop: 0);
        Assert.True(P.IsInTopBand(input, 0));
        Assert.True(P.IsInTopBand(input, P.TopBand(1.0) - 1));
        Assert.False(P.IsInTopBand(input, P.TopBand(1.0)));
        Assert.Equal(P.HtClient, P.Decide(input, 400, 0, Always(true)).Code);
    }

    [Fact]
    public void The_avalonia_probe_runs_at_most_once_and_never_below_the_top_band()
    {
        var calls = 0;
        bool Probe(int x, int y) { calls++; return true; }

        var normal = Input(FrameWindowMode.Normal, 1.0);
        P.Decide(normal, 400, normal.Window.Y + 2, Probe);
        Assert.Equal(1, calls);                         // resize band and top-band rule share one probe

        calls = 0;
        var maximized = Input(FrameWindowMode.Maximized, 1.0);
        P.Decide(maximized, 400, maximized.Window.Y + 300, Probe);
        P.Decide(normal, 400, normal.Window.Y + 300, Probe);
        Assert.Equal(0, calls);                         // ordinary mouse moves pay nothing

        calls = 0;
        P.Decide(new FrameHitTestInput(FrameWindowMode.FullScreen, maximized.Window, 0, 1.0), 400, 0, Probe);
        Assert.Equal(0, calls);
    }

    [Theory]
    [InlineData(1.25, 36, 45)]
    [InlineData(1.5, 46, 69)]
    [InlineData(1.75, 46, 81)]   // 80.5 -> 81: the partially covered far pixel belongs to the control
    public void Screen_rectangles_round_their_far_edge_outward(double scale, double dip, int expected)
    {
        var rect = P.ScreenRect(new PixelPoint(10, 0), new Size(dip, 44), scale)!.Value;
        Assert.Equal(expected, rect.Width);
        Assert.Equal(10, rect.X);
        Assert.Null(P.ScreenRect(new PixelPoint(0, 0), new Size(0, 44), scale));
    }

    [Theory]
    [InlineData(0, 0, true)]        // the top-left corner pixel a rounded background would miss
    [InlineData(0.4, 0, true)]
    [InlineData(36, 0, true)]       // inclusive far edge, like Avalonia's ClipToBounds test
    [InlineData(18, 44, true)]
    [InlineData(-0.01, 10, false)]
    [InlineData(10, -0.01, false)]
    [InlineData(36.01, 0, false)]
    public void Edge_hit_button_targets_its_full_rectangle(double x, double y, bool expected) =>
        Assert.Equal(expected, EdgeHitButton.IsRectangularHit(new Point(x, y), new Size(36, 44)));
}
