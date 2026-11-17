#if GLIDE_REFS
using Avalonia;
using Avalonia.Headless;
using Glide.Imaging;

namespace Glide.DecodeBench507;

public sealed class BenchApp : Application { }

/// <summary>
/// Glide's production open path: ImageLoadCoordinator over the same CodecProviderDecoderBackend MainWindow
/// builds (so AVIF/HEIC/JXL/JPEG 2000 reach glidecodecs.dll exactly as in the app), factory-default policy
/// (Maximum quality, 2400 px preview cap) for the configured viewport. A fresh coordinator per sample means
/// empty Glide caches; the backend (and any loaded codec provider) lives for the whole process, as in the app.
///   Preview = LoadForegroundAsync          -> what reaches the viewport first (decode-ready, not composited)
///   Full    = LoadFullForegroundAsync      -> what 100 % zoom / save / print need
/// </summary>
internal sealed class GlideEngine : IEngine
{
    private static readonly object InitGate = new();
    private static bool _avaloniaReady;
    private readonly CodecProviderDecoderBackend _backend;

    public string Name => "glide";
    public string Library => "Glide 5.0.7 production path (WIC / Skia / built-in managed / glidecodecs.dll)";
    public string? LoadError => null;

    public GlideEngine()
    {
        lock (InitGate)
        {
            if (!_avaloniaReady)
            {
                AppBuilder.Configure<BenchApp>().UseSkia()
                    .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
                    .SetupWithoutStarting();
                SynchronizationContext.SetSynchronizationContext(null);
                _avaloniaReady = true;
            }
        }
        _backend = new CodecProviderDecoderBackend(lazyResolver: path => CodecProviderRuntime.Shared.ResolveProvider(path));
    }

    public bool Supports(string ext) => true; // Glide routes every suffix; failures are recorded per file.

    private static ImagePerformancePolicy Policy(int boxW, int boxH)
    {
        // Mirrors tools\perf BenchPolicies.AppDefault (MainWindow.BuildPerformancePolicy with factory defaults).
        var bounds = ImagePerformanceGovernor.ChoosePreviewBounds(2400, boxW, boxH);
        return (ImagePerformancePolicy.ForProfile("Maximum quality") with
        {
            DecoderScaledFirstFrame = true,
            PreviewLongestSide = 2400,
            PreviewMaxWidth = bounds.Width,
            PreviewMaxHeight = bounds.Height,
            ProgressiveColorFirstPreview = true,
            BackgroundRefinement = true,
            RefinementDelayMs = 5,
            SequentialForegroundReads = true,
            PredictivePrefetch = false,
            PrefetchDepth = 15,
            FullNeighbourPredecodeCount = 1,
            CompressedCacheItems = 20,
            CompressedCacheMegabytes = 999,
            DecodedCacheMegabytes = 999
        }).Normalize();
    }

    private sealed record State(ImageLoadCoordinator Loader, ImagePerformancePolicy Policy);

    public object? Prepare(Request r)
    {
        var policy = Policy(r.BoxW, r.BoxH);
        var loader = new ImageLoadCoordinator(_backend) { Policy = policy };
        loader.SetViewportDemand(r.Path, new ImageViewportDemand(r.BoxW, r.BoxH));
        return new State(loader, policy);
    }

    public Outcome Run(Request r, object? state)
    {
        var s = (State)state!;
        var result = (r.Mode == Mode.Preview
                ? s.Loader.LoadForegroundAsync(r.Path, s.Policy)
                : s.Loader.LoadFullForegroundAsync(r.Path, CancellationToken.None, cacheResult: false))
            .GetAwaiter().GetResult();
        if (result is null) return Outcome.Fail("null result");
        var o = new Outcome { Ok = true, W = result.Bitmap.PixelSize.Width, H = result.Bitmap.PixelSize.Height, Route = result.DecodeRoute + (result.IsPreview ? "/preview" : "/full") };
        s.Loader.ReleaseBitmap(result.Bitmap);
        return o;
    }

    public void Cleanup(object? state) => ((State)state!).Loader.Dispose();

    public void Dispose() => _backend.Dispose();
}
#endif
