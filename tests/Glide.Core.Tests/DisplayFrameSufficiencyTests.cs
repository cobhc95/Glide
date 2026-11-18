using Glide.Core.Settings;

namespace Glide.Core.Tests;

/// <summary>5.1.0 regression: frames shown smaller than their physical draw size stayed pixelated.</summary>
[Trait("Area", "Decode")]
public sealed class DisplayFrameSufficiencyTests
{
    [Fact]
    public void DipToPhysical_UsesRenderScaling()
    {
        Assert.Equal(1750, DisplayFrameSufficiency.ToPhysical(1000, 1.75));
        Assert.Equal(1000, DisplayFrameSufficiency.ToPhysical(1000, 0.5)); // never below 1x
        Assert.Equal(0, DisplayFrameSufficiency.ToPhysical(0, 1.75));
    }

    [Fact]
    public void FitNeeded_IsPhysical_AndCappedAtSource()
    {
        // 2880x1800 source fitted in a 1037x646 DIP viewport at 175% -> ~1815 px wide, not ~1037.
        var fit = DisplayFrameSufficiency.FitNeeded(1037, 646, 1.75, 2880, 1800);
        Assert.InRange(fit.Width, 1800, 1820);
        Assert.Equal((800, 600), DisplayFrameSufficiency.FitNeeded(3000, 2000, 1.0, 800, 600));
        // Not laid out yet: only the source size is safe.
        Assert.Equal((2880, 1800), DisplayFrameSufficiency.FitNeeded(0, 0, 1.75, 2880, 1800));
    }

    [Fact]
    public void HalfOrQuarterScaledFrame_AtFit_IsUndersized_FullIsNot()
    {
        var needed = DisplayFrameSufficiency.FitNeeded(1037, 646, 1.75, 2880, 1800).Width;
        Assert.True(DisplayFrameSufficiency.IsUndersized(720, needed));
        Assert.True(DisplayFrameSufficiency.IsUndersized(1440, needed));
        Assert.False(DisplayFrameSufficiency.IsUndersized(2880, needed));
        Assert.False(DisplayFrameSufficiency.IsUndersized(needed - 10, needed)); // DCT rounding tolerance
    }

    [Fact]
    public void ManualZoom_NeedsShownWidth_CappedAtSource()
    {
        Assert.Equal(2880, DisplayFrameSufficiency.ShownNeededWidth(4000, 1.75, 2880));
        Assert.Equal(1750, DisplayFrameSufficiency.ShownNeededWidth(1000, 1.75, 2880));
    }

    [Fact]
    public void PrefetchBox_IsPhysicalFitSize_NotSmaller()
    {
        var (w, h) = DisplayFrameSufficiency.FitNeeded(1000, 600, 1.75, 6720, 4480);
        var policy = new Glide.Imaging.ImagePerformancePolicy { PreviewMaxWidth = 900, PreviewMaxHeight = 500, PrefetchBoxWidth = 1750, PrefetchBoxHeight = 1050 };
        Assert.Equal((1750, 1050), policy.PrefetchBox());
        Assert.False(DisplayFrameSufficiency.IsUndersized(w, 1575)); // fit width of a 3:2 photo in 1750x1050
        Assert.Equal((1575, 1050), (w, h));
    }

    [Fact]
    public void PrefetchFrame_HalfScaledProgressive_IsTooSmall_FitSizedIsNot()
    {
        // 3840x2160 wallpaper, maximized 175% viewport ~ 4480x2300 physical: fit ~ 4089 capped at source.
        Assert.True(DisplayFrameSufficiency.PrefetchFrameTooSmall(1920, 1080, 3840, 2160, 4480, 2300));
        Assert.False(DisplayFrameSufficiency.PrefetchFrameTooSmall(3840, 2160, 3840, 2160, 4480, 2300));
        // 2880x1800 in a 1815x1131 box: a 5/8 DCT frame (1800x1125) is within tolerance, 1/2 is not.
        Assert.False(DisplayFrameSufficiency.PrefetchFrameTooSmall(1800, 1125, 2880, 1800, 1815, 1131));
        Assert.True(DisplayFrameSufficiency.PrefetchFrameTooSmall(1440, 900, 2880, 1800, 1815, 1131));
    }

    [Fact]
    public void PrefetchFrame_UnknownBoxOrSource_NeverForcesFull()
    {
        Assert.False(DisplayFrameSufficiency.PrefetchFrameTooSmall(100, 100, 0, 0, 1000, 1000));
        Assert.False(DisplayFrameSufficiency.PrefetchFrameTooSmall(100, 100, 3840, 2160, 0, 0));
    }

    [Fact]
    public void PrefetchBox_FallsBackToPreviewBox()
    {
        var policy = new Glide.Imaging.ImagePerformancePolicy { PreviewMaxWidth = 900, PreviewMaxHeight = 500 };
        Assert.Equal((900, 500), policy.PrefetchBox());
    }

    [Fact]
    public void ProgressiveStagedRoute_IsNeverDetailComplete()
    {
        Assert.True(DisplayFrameSufficiency.IsDetailIncompleteRoute(DisplayFrameSufficiency.ProgressiveStagedRoute));
        Assert.False(DisplayFrameSufficiency.IsDetailIncompleteRoute("wic-native-scale"));
        Assert.False(DisplayFrameSufficiency.IsDetailIncompleteRoute("prepared-cache"));
        Assert.False(DisplayFrameSufficiency.IsDetailIncompleteRoute(null));
    }

    [Fact]
    public void PreparedCacheHit_KeepsDetailIncompleteFlag()
    {
        // A staged or reduced frame cached by prefetch/shrink-on-leave must not come back as a plain cache hit.
        Assert.True(DisplayFrameSufficiency.IsDetailIncompleteRoute(DisplayFrameSufficiency.PreparedHitRoute(detailComplete: false)));
        Assert.False(DisplayFrameSufficiency.IsDetailIncompleteRoute(DisplayFrameSufficiency.PreparedHitRoute(detailComplete: true)));
        Assert.True(DisplayFrameSufficiency.IsDetailIncompleteRoute(DisplayFrameSufficiency.ReducedFrameRoute));
    }

    [Fact]
    public void FitSizedButDetailIncompletePreview_NeedsRefinement()
    {
        // Pixel size matches the fit box: only the route says it is blurry.
        Assert.True(DisplayFrameSufficiency.NeedsRefinement(1920, 1920, isPreview: true, DisplayFrameSufficiency.ProgressiveStagedRoute));
        Assert.True(DisplayFrameSufficiency.NeedsRefinement(1920, 1920, isPreview: true, DisplayFrameSufficiency.PreparedHitRoute(false)));
        Assert.False(DisplayFrameSufficiency.NeedsRefinement(1920, 1920, isPreview: false, DisplayFrameSufficiency.ProgressiveStagedRoute)); // refined
        Assert.False(DisplayFrameSufficiency.NeedsRefinement(1920, 1920, isPreview: true, "prepared-cache"));
        Assert.True(DisplayFrameSufficiency.NeedsRefinement(960, 1920, isPreview: false, "stream"));
    }

    [Fact]
    public void RotatedSource_FitIsMeasuredInDisplayOrientation()
    {
        // EXIF 6/8: a 2880x1800 sensor frame shows as 1800x2880; in a 1920x1080 window the height limits it.
        Assert.Equal((675, 1080), DisplayFrameSufficiency.FitNeeded(1920, 1080, 1.0, 1800, 2880));
        Assert.True(DisplayFrameSufficiency.PrefetchFrameTooSmall(450, 720, 1800, 2880, 1920, 1080));
        Assert.False(DisplayFrameSufficiency.PrefetchFrameTooSmall(675, 1080, 1800, 2880, 1920, 1080));
    }

    private static byte[] Segment(byte marker, int payload, byte fill = 0)
    {
        var length = payload + 2;
        var seg = new byte[payload + 4];
        seg[0] = 0xFF; seg[1] = marker; seg[2] = (byte)(length >> 8); seg[3] = (byte)length;
        for (var i = 4; i < seg.Length; i++) seg[i] = fill;
        return seg;
    }

    private static byte[] Jpeg(params byte[][] segments) =>
        new byte[] { 0xFF, 0xD8 }.Concat(segments.SelectMany(s => s)).Concat(new byte[] { 0xFF, 0xDA, 0, 2 }).ToArray();

    [Fact]
    public void ProgressiveJpegProbe_ReadsMainFrameNotExifThumbnail()
    {
        // APP1 whose payload holds a baseline SOF0 (as an EXIF thumbnail would) is skipped by length.
        var exif = Segment(0xE1, 64);
        exif[10] = 0xFF; exif[11] = 0xC0;
        Assert.True(Glide.Imaging.ImageHeaderProbe.IsProgressiveJpeg(Jpeg(exif, Segment(0xE2, 20), Segment(0xC2, 15))));
        Assert.False(Glide.Imaging.ImageHeaderProbe.IsProgressiveJpeg(Jpeg(exif, Segment(0xC4, 20), Segment(0xC0, 15))));
        var progressiveThumb = Segment(0xE1, 64);
        progressiveThumb[10] = 0xFF; progressiveThumb[11] = 0xC2;
        Assert.False(Glide.Imaging.ImageHeaderProbe.IsProgressiveJpeg(Jpeg(progressiveThumb, Segment(0xC0, 15))));
        Assert.False(Glide.Imaging.ImageHeaderProbe.IsProgressiveJpeg(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0, 0, 0, 0 }));
        // Truncated before the SOF: not decided as progressive.
        Assert.False(Glide.Imaging.ImageHeaderProbe.IsProgressiveJpeg(Jpeg(Segment(0xE1, 64)).Take(30).ToArray()));
    }
}
