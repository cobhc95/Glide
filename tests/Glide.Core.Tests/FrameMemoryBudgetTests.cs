using Glide.Core.Settings;

namespace Glide.Core.Tests;

/// <summary>5.1.1 RAM work: shrunk / capped frames must never be smaller than the physical fit size.</summary>
[Trait("Area", "Decode")]
public sealed class FrameMemoryBudgetTests
{
    [Theory]
    [InlineData(6720, 4480, 4480, 2300)] // 30 MP landscape, maximized at 175%
    [InlineData(4480, 6720, 4480, 2300)] // 30 MP portrait
    [InlineData(6720, 4480, 1920, 1080)]
    [InlineData(4000, 4000, 2560, 1440)]
    public void ShrinkTarget_NeverBelowFit_InEitherOrientation(int fw, int fh, int bw, int bh)
    {
        var (w, h) = FrameMemoryBudget.ShrinkTarget(fw, fh, bw, bh);
        Assert.True(w > 0 && h > 0);
        var upright = DisplayFrameSufficiency.FitNeeded(bw, bh, 1.0, fw, fh);
        var turned = DisplayFrameSufficiency.FitNeeded(bh, bw, 1.0, fw, fh);
        Assert.False(DisplayFrameSufficiency.IsUndersized(w, upright.Width));
        Assert.False(DisplayFrameSufficiency.IsUndersized(h, upright.Height));
        Assert.False(DisplayFrameSufficiency.IsUndersized(w, turned.Width));
        Assert.False(DisplayFrameSufficiency.IsUndersized(h, turned.Height));
        Assert.True(w >= upright.Width && h >= upright.Height && w >= turned.Width && h >= turned.Height);
        Assert.True(w <= fw && h <= fh);
    }

    [Fact]
    public void ShrinkTarget_SavesMemoryOnLargePhoto()
    {
        var (w, h) = FrameMemoryBudget.ShrinkTarget(6720, 4480, 4480, 2300);
        Assert.True((long)w * h < 6720L * 4480 * FrameMemoryBudget.MinShrinkSavingRatio);
    }

    [Theory]
    [InlineData(3840, 2160, 4480, 2300)] // 4K frame already below the box: keep it
    [InlineData(1000, 800, 1920, 1080)]
    [InlineData(6720, 4480, 0, 0)]       // box unknown: never shrink
    [InlineData(0, 0, 1920, 1080)]
    public void ShrinkTarget_KeepsFrameWhenNotWorthIt(int fw, int fh, int bw, int bh) =>
        Assert.Equal((0, 0), FrameMemoryBudget.ShrinkTarget(fw, fh, bw, bh));

    [Theory]
    [InlineData(5, 12)]
    [InlineData(15, 32)]
    [InlineData(0, 4)]
    public void NeighbourFrameLimit_CoversPrefetchWindowOnBothSides(int depth, int expected)
    {
        Assert.Equal(expected, FrameMemoryBudget.NeighbourFrameLimit(depth));
        Assert.True(FrameMemoryBudget.NeighbourFrameLimit(depth) >= 2 * depth + 1);
    }
}
