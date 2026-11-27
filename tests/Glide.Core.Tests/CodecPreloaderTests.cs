using Glide.Imaging;

namespace Glide.Core.Tests;

/// <summary>
/// 5.2.0 codec preload: a listing that contains HEIC / AVIF / JPEG XL / JPEG 2000 warms the codec plug-in on a background
/// thread, once per suffix; folders without those formats (and SVG, which the plug-in does not serve) load nothing.
/// </summary>
[Collection(AvaloniaHeadlessCollection.Name)]
[Trait("Area", "Decode")]
public sealed class CodecPreloaderTests : IDisposable
{
    private readonly List<string> _resolved = new();

    public CodecPreloaderTests(AvaloniaHeadlessFixture fixture)
    {
        _ = fixture;
        CodecPreloader.ResetForTests();
        CodecPreloader.RunInlineForTests = true;
        CodecPreloader.ResolverForTests = path => { lock (_resolved) _resolved.Add(path); return null; };
    }

    public void Dispose()
    {
        CodecPreloader.ResolverForTests = null;
        CodecPreloader.RunInlineForTests = false;
        CodecPreloader.ResetForTests();
    }

    [Theory]
    [InlineData("photo.heic")] [InlineData("photo.HEIF")] [InlineData("a.avif")] [InlineData("a.jxl")] [InlineData("a.jp2")] [InlineData("a.j2k")] [InlineData(@"C:\x\y\z.avifs")]
    public void A_plugin_format_needs_the_provider(string name) => Assert.True(CodecPreloader.NeedsProvider(name));

    [Theory]
    [InlineData("photo.jpg")] [InlineData("photo.png")] [InlineData("a.webp")] [InlineData("a.svg")] [InlineData("a.svgz")] [InlineData("a.psd")] [InlineData("a.dng")]
    [InlineData("readme.txt")] [InlineData("noextension")] [InlineData("")]
    public void Other_formats_do_not(string name) => Assert.False(CodecPreloader.NeedsProvider(name));

    [Fact]
    public void A_listing_with_plugin_formats_warms_each_suffix_once()
    {
        var started = CodecPreloader.NotifyListing(new[] { "a.jpg", "b.heic", "c.heic", "d.avif", "e.png", "f.jxl", "g.jp2" });
        Assert.Equal(new[] { ".heic", ".avif", ".jxl", ".jp2" }, started);
        Assert.Equal(4, _resolved.Count);
        Assert.All(_resolved, probe => Assert.StartsWith("preload.", probe));
        Assert.Equal(4, CodecPreloader.ResolvedCount);

        // The next folder (or the same one reloaded) asks nothing more for suffixes already warmed.
        Assert.Empty(CodecPreloader.NotifyListing(new[] { "h.heic", "i.avif" }));
        Assert.Equal(4, _resolved.Count);
        Assert.Equal(new[] { ".heif" }, CodecPreloader.NotifyListing(new[] { "j.heif" }));
    }

    [Fact]
    public void A_listing_without_plugin_formats_loads_nothing()
    {
        Assert.Empty(CodecPreloader.NotifyListing(new[] { "a.jpg", "b.png", "c.svg", "d.psd", "e.tif", "readme.txt", "" }));
        Assert.Empty(_resolved);
        Assert.Equal(0, CodecPreloader.ResolvedCount);
    }

    [Fact]
    public void A_resolver_that_throws_never_escapes_the_warm_up()
    {
        CodecPreloader.ResolverForTests = _ => throw new InvalidOperationException("damaged plug-in");
        var exception = Record.Exception(() => CodecPreloader.NotifyListing(new[] { "a.heic" }));
        Assert.Null(exception);
        Assert.Equal(0, CodecPreloader.ResolvedCount);
    }

    [Fact]
    public void The_warm_up_runs_off_the_calling_thread_by_default()
    {
        CodecPreloader.RunInlineForTests = false;
        using var done = new ManualResetEventSlim(false);
        var callerThread = Environment.CurrentManagedThreadId;
        var workerThread = -1;
        CodecPreloader.ResolverForTests = _ => { workerThread = Environment.CurrentManagedThreadId; done.Set(); return null; };
        CodecPreloader.NotifyListing(new[] { "a.jxl" });
        Assert.True(done.Wait(TimeSpan.FromSeconds(10)));
        Assert.NotEqual(callerThread, workerThread);
    }

    [Fact]
    public void The_real_plugin_resolves_through_the_preload_and_a_later_open_finds_it_loaded()
    {
        if (!File.Exists(Path.Combine(AppContext.BaseDirectory, "codecs", "glidecodecs.dll"))) return; // no plug-in in this build output
        CodecPreloader.ResolverForTests = null; // the real runtime
        var runtime = CodecProviderRuntime.Shared;
        try
        {
            Assert.Equal(new[] { ".heic" }, CodecPreloader.NotifyListing(new[] { "preview.heic" }));
            Assert.Equal(1, CodecPreloader.ResolvedCount);
            // The provider was hashed and loaded by the preload; the open's own lookup returns the same live instance.
            var first = runtime.ResolveProvider("a.heic");
            Assert.NotNull(first);
            Assert.Same(first, runtime.ResolveProvider("b.heic"));
            Assert.Same(first, runtime.ResolveProvider("c.avif")); // one plug-in serves the whole family
        }
        finally { CodecCapabilityRegistry.ClearExternalProviders(); }
    }
}
