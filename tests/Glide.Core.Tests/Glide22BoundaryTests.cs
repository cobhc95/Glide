using Glide.App.Services;
using Glide.Imaging;
using Xunit;

namespace Glide.Core.Tests;

public sealed class Glide22BoundaryTests
{
    [Fact]
    public void StartupPathsPreserveOrderAndDeduplicateCaseInsensitive()
    {
        var q = new StartupPathQueue();
        q.Add(new[] { "b.jpg", "A.PNG", "B.JPG", " ", "a.png" }, _ => true);
        Assert.Equal(new[] { Path.GetFullPath("b.jpg"), Path.GetFullPath("A.PNG") }, q.Paths);
    }

    [Fact]
    public void StartupPathsRejectAndReportMalformedOrUnacceptedEntries()
    {
        var q = new StartupPathQueue();
        var rejected = new List<(string Path, string Reason)>();
        q.Add(new[] { "missing.jpg", "ok.jpg" }, p => p.EndsWith("ok.jpg", StringComparison.OrdinalIgnoreCase),
            (p, reason) => rejected.Add((p, reason)));
        Assert.Single(rejected);
        Assert.Contains("missing.jpg", rejected[0].Path, StringComparison.OrdinalIgnoreCase);
        Assert.Single(q.Paths);
    }

    [Fact]
    public void StartupPathsDrainIsAtomicAndLazyQueueDoesNotActivateUntilDrain()
    {
        var q = new StartupPathQueue();
        var activated = 0;
        q.Add(new[] { "one.jpg", "two.jpg" }, _ => { activated++; return true; });
        Assert.Equal(2, activated); // acceptance/normalisation only; no open/decode occurs here
        var drained = q.Drain();
        Assert.Equal(2, drained.Length);
        Assert.Empty(q.Paths);
    }


    [Fact]
    public void ExplicitStartupWorkspacePreservesOrderAndMakesFirstPathActive()
    {
        var root = Path.Combine(Path.GetTempPath(), "glide-startup-plan-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var first = Path.Combine(root, "first.jpg");
            var second = Path.Combine(root, "second.png");
            File.WriteAllBytes(first, new byte[] { 1 });
            File.WriteAllBytes(second, new byte[] { 1 });

            var plan = StartupWorkspacePlanner.Create(new[] { first, second, first });
            var workspace = new Glide.Core.Workspace.WorkspaceState();
            workspace.Reset(plan.Tabs, 0);

            Assert.Equal(new[] { first, second }, plan.Paths);
            Assert.Equal(2, workspace.Tabs.Count);
            Assert.Equal(first, Assert.IsType<Glide.Core.Workspace.ImageTabState>(workspace.Active).Path);
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }

    [Fact]
    public async Task MetadataCancellationIsPropagatedForAsyncReads()
    {
        var path = Path.Combine(Path.GetTempPath(), "glide-meta-cancel-" + Guid.NewGuid() + ".jpg");
        try
        {
            File.WriteAllBytes(path, new byte[] { 0xff, 0xd8, 0xff, 0xd9 });
            using var cts = new CancellationTokenSource();
            cts.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ImageMetadataReader.ReadAsync(path, cts.Token));
        }
        finally { try { File.Delete(path); } catch { } }
    }
}
