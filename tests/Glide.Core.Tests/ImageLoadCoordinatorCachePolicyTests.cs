using Xunit;
using Glide.Core;
using Glide.Imaging;

namespace Glide.Core.Tests;

public sealed class ImageLoadCoordinatorCachePolicyTests
{
    [Fact]
    public void SpeculativeCompressedLimitFollowsPolicyBudget()
    {
        var policy = ImagePerformancePolicy.ForProfile("Balanced") with { CompressedCacheMegabytes = 32 };
        var limit = ImagePerformanceGovernor.GetMaxSpeculativeCompressedFileBytes(policy);
        Assert.Equal(8L * 1024 * 1024, limit);

        var larger = policy with { CompressedCacheMegabytes = 2048 };
        Assert.True(ImagePerformanceGovernor.GetMaxSpeculativeCompressedFileBytes(larger) >= limit);
    }

    [Fact]
    public async Task WarmSkipsFileAboveSpeculativeLimitWithoutManagedCacheEntry()
    {
        var path = Path.Combine(Path.GetTempPath(), "glide-large-warm-" + Guid.NewGuid() + ".jpg");
        try
        {
            using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
                stream.SetLength(9L * 1024 * 1024);
            using var loader = new ImageLoadCoordinator { Policy = ImagePerformancePolicy.ForProfile("Balanced") with { CompressedCacheMegabytes = 32 } };
            await loader.WarmAsync(new[] { path });
            var snapshot = loader.GetCacheSnapshot();
            Assert.Equal(0, snapshot.CompressedItems);
            Assert.Equal(0, snapshot.CompressedBytes);
        }
        finally { try { File.Delete(path); } catch { } }
    }


    [Fact]
    public void PerformanceProfilesKeepBalancedColourFirstButLetMaximumSpeedOptIntoLevelZero()
    {
        var speed = ImagePerformancePolicy.ForProfile("Maximum speed");
        var balanced = ImagePerformancePolicy.ForProfile("Balanced");
        var quality = ImagePerformancePolicy.ForProfile("Maximum quality");

        Assert.False(speed.ProgressiveColorFirstPreview);
        Assert.True(balanced.ProgressiveColorFirstPreview);
        Assert.True(quality.ProgressiveColorFirstPreview);

        // The setting remains an ordinary policy override even after selecting Maximum speed.
        Assert.True((speed with { ProgressiveColorFirstPreview = true }).ProgressiveColorFirstPreview);
    }

    [Fact]
    public void PerformanceProfilesUseRequestedNeighbourPrefetchDepths()
    {
        var speed = ImagePerformancePolicy.ForProfile("Maximum speed");
        var balanced = ImagePerformancePolicy.ForProfile("Balanced");
        var quality = ImagePerformancePolicy.ForProfile("Maximum quality");

        Assert.Equal(3, speed.PrefetchDepth);
        Assert.Equal(5, balanced.PrefetchDepth);
        Assert.Equal(5, quality.PrefetchDepth);
    }

    [Fact]
    public void PerformanceTraceFlushesCoordinatorEventSchema()
    {
        var path = Path.Combine(Path.GetTempPath(), "glide-trace-" + Guid.NewGuid() + ".tsv");
        try
        {
            GlidePerformanceTrace.Configure(path);
            GlidePerformanceTrace.Mark("compressed_cache_skip_large", "bytes=1;limit=0");
            GlidePerformanceTrace.Flush();
            var text = File.ReadAllText(path);
            Assert.Contains("elapsed_ms\tprocess_id\tthread_id\tevent\tdetail", text);
            Assert.Contains("compressed_cache_skip_large", text);
        }
        finally { try { File.Delete(path); } catch { } }
    }
}

// 2.5 regression contracts for aspect-aware first-paint sizing. These deliberately avoid
// hard-coding a hardware tier: the governor may select different overscan on different test PCs.
public sealed class ImageFirstPaintBoundsTests
{
    [Fact]
    public void PreviewBoundsRespectConfiguredLongestSideAndViewportAspect()
    {
        var bounds = ImagePerformanceGovernor.ChoosePreviewBounds(3000, 1536, 887);
        Assert.InRange(bounds.Width, 1536, 3000);
        Assert.InRange(bounds.Height, 887, 3000);
        Assert.True(Math.Max(bounds.Width, bounds.Height) <= 3000);
        var viewportAspect = 1536d / 887d;
        var previewAspect = bounds.Width / (double)bounds.Height;
        Assert.InRange(previewAspect / viewportAspect, 0.995, 1.005);
    }

    [Fact]
    public void PreviewBoundsDoNotTurnPortraitDemandIntoConfiguredSquare()
    {
        var bounds = ImagePerformanceGovernor.ChoosePreviewBounds(3000, 900, 1500);
        Assert.True(bounds.Width < bounds.Height);
        Assert.True(bounds.Width < 3000);
        Assert.True(bounds.Height <= 3000);
    }
}
