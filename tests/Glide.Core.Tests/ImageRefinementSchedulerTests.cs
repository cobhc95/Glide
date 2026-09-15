using Xunit;
using Glide.Imaging;

namespace Glide.Core.Tests;

public sealed class ImageRefinementSchedulerTests
{
    [Fact]
    public void RapidBrowseSuppressesDetail()
    {
        var scheduler = new ImageRefinementScheduler();
        var plan = scheduler.Plan(new ImageDimensions(12000, 8000), ImageViewportDemand.Unspecified,
            ImageNavigationActivity.RapidBrowse, ImagePerformancePolicy.ForProfile("Balanced"));
        Assert.False(plan.Allowed);
    }

    [Fact]
    public void FitDemandBoundsLargeImageToDisplayResolution()
    {
        var scheduler = new ImageRefinementScheduler();
        var demand = new ImageViewportDemand(1600, 900, 1);
        var plan = scheduler.Plan(new ImageDimensions(12000, 8000), demand,
            ImageNavigationActivity.NormalBrowse, ImagePerformancePolicy.ForProfile("Balanced"));
        Assert.True(plan.Allowed);
        Assert.False(plan.FullResolution);
        Assert.Equal(1600, plan.LongestSide);
    }

    [Fact]
    public void HundredPercentDemandCanRequestFullResolutionWhenWithinBudget()
    {
        var scheduler = new ImageRefinementScheduler();
        var demand = new ImageViewportDemand(4000, 3000, 1, ExactPixelDemand: true);
        var plan = scheduler.Plan(new ImageDimensions(4000, 3000), demand,
            ImageNavigationActivity.NormalBrowse, ImagePerformancePolicy.ForProfile("Balanced"));
        Assert.True(plan.FullResolution);
    }

    [Fact]
    public void SelectionZoomRequestsSourceDetailWithinBudget()
    {
        var scheduler = new ImageRefinementScheduler();
        var plan = scheduler.Plan(new ImageDimensions(8000, 6000), new ImageViewportDemand(900, 700, 1, SelectionZoom: true),
            ImageNavigationActivity.NormalBrowse, ImagePerformancePolicy.ForProfile("Balanced"));
        Assert.True(plan.FullResolution);
    }

    [Fact]
    public void BoundedTargetHonoursAspectRatioAndAvailableBytes()
    {
        var source = new ImageDimensions(12000, 8000);
        var target = ImageRefinementScheduler.BoundLongestSide(source, 12000, 16L * 1024 * 1024);
        Assert.InRange(target, 480, 12000);
        Assert.True((long)target * (long)Math.Ceiling(target * 8000d / 12000d) * 4 <= 16L * 1024 * 1024 + 50000);
    }
}
