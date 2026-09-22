using Glide.App.Printing;

namespace Glide.Core.Tests;

/// <summary>
/// Geometry contract for image printing. The same <see cref="PrintLayoutEngine.Compute"/>
/// drives the Avalonia preview and the GDI spool path, so these tests guard WYSIWYG unity.
/// All dimensions are hundredths of an inch unless noted.
/// </summary>
public sealed class PrintLayoutEngineTests
{
    // A4 portrait sheet, printable rect inset 0.5 in / 12.7 mm on every side (typical laser).
    private static readonly PrintPaperGeometry A4 = new(827, 1169, 50, 50, 727, 1069);

    private static PrintLayoutInput Base(
        int imgW = 1200, int imgH = 800,
        double dpi = 96,
        PrintScalingMode mode = PrintScalingMode.BestFit,
        double ml = 50, double mt = 50, double mr = 50, double mb = 50,
        bool keepAspect = true, bool autoRotate = true,
        PrintHAlign h = PrintHAlign.Centre, PrintVAlign v = PrintVAlign.Centre,
        bool landscape = false, double custom = 100, bool rotate = false) => new(
        imgW, imgH, dpi, dpi, A4, landscape, ml, mt, mr, mb,
        mode, custom, keepAspect, rotate, h, v);

    [Fact]
    public void BestFit_landscape_photo_fills_width_and_centres_vertically()
    {
        var layout = PrintLayoutEngine.Compute(Base(autoRotate: false));
        // Content box: 727 x 1069. Image 3:2 → width-limited.
        Assert.False(layout.Rotated);
        Assert.False(layout.Clipped);
        Assert.Equal("", layout.Warning);
        Assert.Equal(50, layout.DestX, 0.01);
        Assert.Equal(727, layout.DestWidth, 0.01);
        var expectedH = 727.0 * 800 / 1200;
        Assert.Equal(expectedH, layout.DestHeight, 0.01);
        Assert.Equal(50 + (1069 - expectedH) / 2, layout.DestY, 0.01);
        Assert.Equal(0, layout.SrcX); Assert.Equal(0, layout.SrcY);
        Assert.Equal(1, layout.SrcWidth); Assert.Equal(1, layout.SrcHeight);
    }

    [Fact]
    public void BestFit_auto_rotate_picks_the_larger_fit()
    {
        // Portrait image on portrait sheet already matches: no rotation.
        var straight = PrintLayoutEngine.Compute(Base(800, 1200, rotate: true));
        Assert.False(straight.Rotated);
        // Landscape image on portrait sheet fits larger rotated.
        var landscape = PrintLayoutEngine.Compute(Base(1200, 800, rotate: true));
        Assert.True(landscape.Rotated);
        // Very wide panorama on portrait sheet: rotated and height-limited.
        var pano = PrintLayoutEngine.Compute(Base(3000, 500, rotate: true));
        Assert.True(pano.Rotated);
        Assert.Equal(1069, pano.DestHeight, 0.01);
    }

    [Fact]
    public void FillPage_covers_box_and_crops_with_alignment()
    {
        // Square image on portrait box: crop left/right, keep full height.
        var centre = PrintLayoutEngine.Compute(Base(1000, 1000, mode: PrintScalingMode.FillPage));
        Assert.True(centre.Clipped);
        Assert.Equal(727, centre.DestWidth, 0.01);
        Assert.Equal(1069, centre.DestHeight, 0.01);
        Assert.True(centre.SrcWidth < 1);
        Assert.Equal(1, centre.SrcHeight);
        var left = PrintLayoutEngine.Compute(Base(1000, 1000, mode: PrintScalingMode.FillPage, h: PrintHAlign.Left));
        var right = PrintLayoutEngine.Compute(Base(1000, 1000, mode: PrintScalingMode.FillPage, h: PrintHAlign.Right));
        Assert.Equal(0, left.SrcX);
        Assert.Equal(1 - left.SrcWidth, right.SrcX, 0.0001);
        Assert.Equal((1 - centre.SrcWidth) / 2, centre.SrcX, 0.0001);
    }

    [Fact]
    public void ActualSize_uses_dpi_and_clips_when_larger_than_box()
    {
        // 1200x800 @ 300 dpi = 4x2.667 in → fits.
        var fit = PrintLayoutEngine.Compute(Base(mode: PrintScalingMode.ActualSize, dpi: 300));
        Assert.False(fit.Clipped);
        Assert.Equal(400, fit.DestWidth, 0.01);
        Assert.Equal(266.67, fit.DestHeight, 0.1);
        // Same pixels at 96 dpi = 12.5x8.33 in → clipped, centred window.
        var clip = PrintLayoutEngine.Compute(Base(mode: PrintScalingMode.ActualSize, dpi: 96));
        Assert.True(clip.Clipped);
        Assert.Equal(1250, clip.DestWidth, 0.01);
        Assert.Contains("larger than the printable area", clip.Warning);
        // Centre alignment picks the middle window of the overflow.
        Assert.Equal(50 + (727 - 1250) / 2.0, clip.DestX, 0.01);
        // All nine alignments are defined (no NaN) even when clipped.
        foreach (var h in Enum.GetValues<PrintHAlign>())
            foreach (var v in Enum.GetValues<PrintVAlign>())
            {
                var layout = PrintLayoutEngine.Compute(Base(mode: PrintScalingMode.ActualSize, dpi: 96, h: h, v: v));
                Assert.True(double.IsFinite(layout.DestX) && double.IsFinite(layout.DestY));
            }
    }

    [Fact]
    public void Absent_or_nonsense_dpi_falls_back_to_96()
    {
        var zero = PrintLayoutEngine.Compute(Base(mode: PrintScalingMode.ActualSize, dpi: 0));
        var expected = PrintLayoutEngine.Compute(Base(mode: PrintScalingMode.ActualSize, dpi: 96));
        Assert.Equal(expected.DestWidth, zero.DestWidth, 0.01);
        var huge = PrintLayoutEngine.Compute(Base(mode: PrintScalingMode.ActualSize, dpi: 100000));
        Assert.Equal(expected.DestWidth, huge.DestWidth, 0.01);
    }

    [Fact]
    public void CustomScale_is_a_factor_of_actual_size()
    {
        var half = PrintLayoutEngine.Compute(Base(mode: PrintScalingMode.CustomScale, dpi: 300, custom: 50));
        Assert.Equal(200, half.DestWidth, 0.01);
        Assert.Equal(0.5, half.ScaleUsed, 0.0001);
        var dbl = PrintLayoutEngine.Compute(Base(mode: PrintScalingMode.CustomScale, dpi: 300, custom: 200));
        Assert.Equal(800, dbl.DestWidth, 0.01);
    }

    [Fact]
    public void Zero_margins_use_printable_edge_not_sheet_edge()
    {
        var layout = PrintLayoutEngine.Compute(Base(ml: 0, mt: 0, mr: 0, mb: 0));
        // Dest must stay inside the printable rect (50,50,727,1069), never bleed to 0.
        Assert.True(layout.DestX >= 50 - 1e-9);
        Assert.True(layout.DestY >= 50 - 1e-9);
        Assert.True(layout.DestX + layout.DestWidth <= 50 + 727 + 1e-9);
        Assert.True(layout.DestY + layout.DestHeight <= 50 + 1069 + 1e-9);
    }

    [Fact]
    public void Asymmetric_margins_shift_the_content_box()
    {
        var layout = PrintLayoutEngine.Compute(Base(ml: 100, mt: 50, mr: 50, mb: 200));
        // Box: x 100..777, y 50..969 → 677 x 919.
        Assert.Equal(100, layout.DestX, 0.01); // width-limited 3:2
        Assert.Equal(677, layout.DestWidth, 0.01);
    }

    [Fact]
    public void Landscape_swaps_the_sheet()
    {
        var portrait = PrintLayoutEngine.Compute(Base(1200, 800));
        var landscape = PrintLayoutEngine.Compute(Base(1200, 800, landscape: true));
        // Landscape box is wider than tall; the 3:2 image fits larger.
        Assert.True(landscape.DestWidth > portrait.DestWidth);
    }

    [Fact]
    public void Stretch_ignores_aspect_and_warns()
    {
        var layout = PrintLayoutEngine.Compute(Base(mode: PrintScalingMode.Stretch));
        Assert.Equal(727, layout.DestWidth, 0.01);
        Assert.Equal(1069, layout.DestHeight, 0.01);
        Assert.Contains("Aspect ratio", layout.Warning);
    }

    [Fact]
    public void Tiny_image_at_actual_size_warns_it_will_look_small()
    {
        var layout = PrintLayoutEngine.Compute(Base(32, 32, mode: PrintScalingMode.ActualSize, dpi: 96));
        Assert.Contains("tiny", layout.Warning);
    }

    [Fact]
    public void BestFit_never_overflows_the_content_box()
    {
        var rnd = new Random(42);
        for (var i = 0; i < 200; i++)
        {
            var w = rnd.Next(1, 9000); var h = rnd.Next(1, 9000);
            var dpi = new[] { 0, 72, 96, 150, 300, 600 }[rnd.Next(6)];
            var layout = PrintLayoutEngine.Compute(Base(w, h, dpi));
            Assert.True(layout.DestWidth <= 727 + 1e-6);
            Assert.True(layout.DestHeight <= 1069 + 1e-6);
            Assert.True(layout.DestX >= 50 - 1e-6 && layout.DestY >= 50 - 1e-6);
        }
    }
}
