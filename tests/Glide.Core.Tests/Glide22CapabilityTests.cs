using Glide.App.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Media;
using Avalonia;
using Avalonia.Platform;
using Glide.App.Services;
using Glide.Core;
using Glide.Core.Commands;
using Glide.Diagnostics;
using Glide.Imaging;
using System.Runtime.InteropServices;
using Xunit;

namespace Glide.Core.Tests;

[Collection(AvaloniaHeadlessCollection.Name)]
public sealed class Glide22CapabilityTests
{
    [Fact]
    public void LegacyRegistryContainsExactly196DistinctSuffixes()
    {
        Assert.Equal(196, CodecCapabilityRegistry.Extensions.Count);
        Assert.Equal(196, CodecCapabilityRegistry.Extensions.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Equal(196, CodecCapabilityRegistry.All.Count);
    }


    [Fact]
    public void FullRegistryRoutes196WhileCoreFastPathRemainsExactlyTen()
    {
        Assert.Equal(196, ImageFormatRegistry.Extensions.Count);
        Assert.Equal(10, ImageFormatRegistry.CoreFastPathExtensions.Count);
        Assert.Equal(".ome.tiff", ImageFormatRegistry.Match("slide.OME.TIFF"));
        Assert.Equal(".nii.gz", ImageFormatRegistry.Match("scan.NII.GZ"));
        Assert.Equal(".jpg", ImageFormatRegistry.Match("photo.JPG"));
        Assert.True(ImageFormatRegistry.IsSupported("drawing.dwg"));
        Assert.False(ImageFormatRegistry.IsCoreFastPath("drawing.dwg"));
    }

    [Fact]
    public void LazyProviderResolverNeverRunsForProtectedCoreSuffixes()
    {
        var resolutions = 0;
        using var backend = new CodecProviderDecoderBackend(lazyResolver: _ => { resolutions++; return new ProbeProvider(); });
        Assert.Null(backend.Probe("photo.jpg"));
        Assert.Null(backend.Probe("photo.png"));
        Assert.Equal(0, resolutions);
    }

    [Fact]
    public void LazyProviderResolverRunsOnlyOnFirstNonCoreSuffixRequest()
    {
        var resolutions = 0;
        var provider = new ProbeProvider();
        try
        {
            using var backend = new CodecProviderDecoderBackend(lazyResolver: _ => { resolutions++; return provider; });
            var first = backend.Probe("scan.nii.gz");
            var second = backend.Probe("other.nii.gz");
            Assert.True(first is { Supported: true });
            Assert.True(second is { Supported: true });
            Assert.Equal(1, resolutions);
        }
        finally
        {
            provider.Dispose();
            CodecCapabilityRegistry.ClearExternalProviders();
        }
    }

    [Fact]
    public void CapabilityTiersNeverPretendUnsupportedFormatsDecode()
    {
        Assert.Contains(CodecCapabilityRegistry.All, x => x.Tier == CodecTier.NativeOs && x.DecodeEnabled);
        Assert.Contains(CodecCapabilityRegistry.All, x => x.Tier == CodecTier.ModularNative && !x.DecodeEnabled);
        Assert.Contains(CodecCapabilityRegistry.All, x => x.Tier == CodecTier.RoutedOnDemand && !x.DecodeEnabled);
    }

    [Fact]
    public void CompoundSuffixMatcherIsAuthoritativeForProviderSelection()
    {
        Assert.Equal(".nii.gz", CodecCapabilityRegistry.Match("sample.NII.GZ"));
        try
        {
            CodecCapabilityRegistry.RegisterExternalProvider("test-provider", new[] { ".nii.gz" });
            var capability = CodecCapabilityRegistry.Describe("sample.nii.gz");
            Assert.Equal(CodecTier.ExternalProvider, capability.Tier);
            Assert.True(capability.DecodeEnabled);
            Assert.True(ImageFormatRegistry.IsSupported("sample.nii.gz"));
        }
        finally { CodecCapabilityRegistry.ClearExternalProviders(); }
    }

    [Fact]
    public void ProviderAbiRejectsManifestIdentityAndCapabilityGaps()
    {
        Assert.True(CodecProviderAbi.IsSafe(new CodecProviderInfo(2, CodecProviderAbi.MinimumStructSize, "x", "1", "MIT", new[] { ".x" }, true, true, true, true)));
        Assert.False(CodecProviderAbi.IsSafe(new CodecProviderInfo(3, CodecProviderAbi.MinimumStructSize, "x", "1", "MIT", new[] { ".x" }, true, true, true, true)));
        Assert.False(CodecProviderAbi.IsSafe(new CodecProviderInfo(1, CodecProviderAbi.MinimumStructSize - 1, "x", "1", "MIT", new[] { ".x" }, true, true, true, true)));
        Assert.False(CodecProviderAbi.IsSafe(new CodecProviderInfo(1, CodecProviderAbi.MinimumStructSize, "x", "1", "", new[] { ".x" }, true, true, true, true)));
        Assert.False(CodecProviderAbi.CapabilitiesMatch(new CodecProviderInfo(1, CodecProviderAbi.MinimumStructSize, "x", "1", "MIT", new[] { ".x" }, true, true, true, true), CodecProviderAbi.SupportsFull));
    }

    [Fact]
    public void ProviderAbiUsesArchitectureSizedInfoRecord()
    {
        Assert.Equal((uint)(16 + IntPtr.Size * 4), CodecProviderAbi.MinimumStructSize);
        Assert.True(CodecProviderAbi.IsSafe(new CodecProviderInfo(1, CodecProviderAbi.MinimumStructSize, "x", "1", "MIT", new[] { ".x" }, false, false, false, false)));
    }

    [Fact]
    public async Task ProviderProbeRoutesThroughBackendAsync()
    {
        using var backend = new CodecProviderDecoderBackend();
        Assert.True(backend.Register(new ProbeProvider()));
        var sync = backend.Probe("sample.nii.gz");
        Assert.NotNull(sync);
        Assert.True(sync.Value.Supported);
        Assert.Equal(new ImageDimensions(320, 200), sync.Value.Dimensions);
        var asyncProbe = await ((IImageDecoderBackend)backend).ProbeAsync("sample.nii.gz");
        Assert.NotNull(asyncProbe);
        Assert.Equal(new ImageDimensions(320, 200), asyncProbe.Value.Dimensions);
    }

    [Fact]
    public async Task ProviderMetadataFramesDecodeAndCancellationRouteThroughBackend()
    {
        var backend = new CodecProviderDecoderBackend();
        var provider = new ProbeProvider();
        Assert.True(backend.Register(provider));
        var path = Path.Combine(Path.GetTempPath(), "glide-provider-test-" + Guid.NewGuid() + ".nii.gz");
        try
        {
            File.WriteAllBytes(path, ImageDiagnosticFixtures.CreatePayloads()[".png"]);
            var metadata = await backend.ReadMetadataAsync(path);
            Assert.Equal("fixture", metadata?.Make);
            Assert.Equal(2, (await backend.ReadFramesAsync(path))?.Count);
            using var cts = new CancellationTokenSource(); cts.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => backend.DecodeFullAsync(Stream.Null, path, cts.Token));
        }
        finally { try { File.Delete(path); } catch { } backend.Dispose(); }
        Assert.True(provider.Disposed);
    }

    [Fact]
    public void ProviderManifestWithoutMatchingHashIsRejected()
    {
        var dll = Path.Combine(Path.GetTempPath(), "glide-provider-" + Guid.NewGuid() + ".dll");
        var manifest = Path.ChangeExtension(dll, ".glidecodec.json");
        try
        {
            File.WriteAllText(manifest, "{\"sha256\":\"wrong\",\"abiVersion\":1,\"structSize\":10,\"id\":\"x\",\"version\":\"1\",\"license\":\"MIT\",\"extensions\":[\".nii.gz\"],\"preview\":true,\"full\":true,\"metadata\":true,\"frames\":true}");
            Assert.False(CodecProviderInventory.TryReadManifest(dll, "actual", out _, out var reason));
            Assert.Contains("SHA-256", reason, StringComparison.OrdinalIgnoreCase);
        }
        finally { try { File.Delete(manifest); } catch { } }
    }

    [Fact]
    public void OverlayStateNormalizesUnsafeGeometryAndOpacity()
    {
        var state = new OverlayState(Guid.NewGuid(), "x.jpg", double.NaN, double.PositiveInfinity, 1, 2, 9, 0, ZIndex: 0).Normalize();
        Assert.Equal(0, state.X); Assert.Equal(0, state.Y);
        Assert.Equal(80, state.Width); Assert.Equal(60, state.Height);
        Assert.Equal(1, state.Opacity); Assert.Equal(.1, state.Zoom);
    }

    [Fact]
    public void OverlayLayoutStoreReadsVersionedEnvelopeAndRejectsCorruptInput()
    {
        var path = Path.Combine(Path.GetTempPath(), "glide-overlay-" + Guid.NewGuid() + ".json");
        try
        {
            var original = new OverlayState(Guid.NewGuid(), "x.jpg", 1, 2, 300, 200);
            OverlayLayoutStore.Save(path, new[] { original });
            OverlayLayoutStore.Save(path, new[] { original with { X = 7 } });
            Assert.Equal(original.Id, Assert.Single(OverlayLayoutStore.Load(path)).Id);
            File.WriteAllText(path, "{not-json");
            Assert.Equal(original.Id, Assert.Single(OverlayLayoutStore.Load(path)).Id);
        }
        finally { try { File.Delete(path); File.Delete(path + ".bak"); } catch { } }
    }

    [Theory]
    [InlineData(null, true, 1, OverlayGestureAction.ZoomIn)]
    [InlineData(null, true, -1, OverlayGestureAction.ZoomOut)]
    [InlineData("nothing", true, 1, OverlayGestureAction.None)]
    [InlineData("resetOverlayZoom", true, 1, OverlayGestureAction.ResetZoom)]
    [InlineData("bringOverlayFront", true, 1, OverlayGestureAction.BringToFront)]
    public void OverlayGestureResolutionIsExecutable(string? configured, bool enabled, double delta, OverlayGestureAction expected)
        => Assert.Equal(expected, OverlayGesturePolicy.Wheel(configured, enabled, delta));

    [Fact]
    public void OverlayClickResolutionAndDisabledDefaultAreExecutable()
    {
        Assert.Equal(OverlayGestureAction.ResetZoom, OverlayGesturePolicy.Click("resetOverlayZoom"));
        Assert.Equal(OverlayGestureAction.BringToFront, OverlayGesturePolicy.Click("bringOverlayFront"));
        Assert.Equal(OverlayGestureAction.None, OverlayGesturePolicy.Click("nothing"));
        Assert.Equal(OverlayGestureAction.None, OverlayGesturePolicy.Wheel(null, false, 1));
        // Explicit actions remain useful even when the default wheel zoom toggle is off.
        Assert.Equal(OverlayGestureAction.ResetZoom, OverlayGesturePolicy.Wheel("resetOverlayZoom", false, 1));
    }

    [Theory]
    [InlineData(0, 0, false)]
    [InlineData(3.99, 0, false)]
    [InlineData(4, 0, true)]
    [InlineData(0, -4, true)]
    [InlineData(double.NaN, 9, false)]
    public void TabOriginThresholdIsPureAndDeterministic(double dx, double dy, bool expected)
        => Assert.Equal(expected, TabDragPolicy.CrossedThreshold(dx, dy));

    [Fact]
    public void FileActionBoundaryRejectsMissingOsTargetsWithoutLaunching()
    {
        var operations = new FileOperationController();
        Assert.False(operations.TryOpenContainingFolder(Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".missing")));
        Assert.False(operations.TryLaunchExternal("missing-program.exe", "missing-image.png"));
    }

    [Fact]
    public async Task FileOperationControllerValidatesAndRenamesAtomicallyAtTheBoundary()
    {
        var folder = Path.Combine(Path.GetTempPath(), "glide-file-op-" + Guid.NewGuid());
        Directory.CreateDirectory(folder);
        var source = Path.Combine(folder, "before.png");
        File.WriteAllBytes(source, new byte[] { 1, 2, 3 });
        var operations = new FileOperationController();
        try
        {
            Assert.Null(operations.ValidateRename(source, "..\\escape.png"));
            Assert.Null(operations.ValidateRename(source, "before.png"));
            var renamed = await operations.RenameAsync(source, "after.png");
            Assert.Equal(Path.Combine(folder, "after.png"), renamed);
            Assert.False(File.Exists(source));
            Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(renamed!));
            using var cts = new CancellationTokenSource(); cts.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operations.RenameAsync(renamed!, "cancel.png", cts.Token));
        }
        finally { try { Directory.Delete(folder, true); } catch { } }
    }

    [Fact]
    public async Task CommandDispatcherExecutesMappedCommandAndIgnoresUnknown()
    {
        var count = 0;
        var dispatcher = new MainWindowCommandDispatcher(new Dictionary<GlideCommand, Func<Task>>
        {
            [GlideCommand.NextImage] = () => { count++; return Task.CompletedTask; }
        });
        Assert.True(dispatcher.CanExecute(GlideCommand.NextImage));
        await dispatcher.ExecuteAsync(GlideCommand.NextImage);
        await dispatcher.ExecuteAsync(GlideCommand.None);
        Assert.Equal(1, count);
    }

    [Fact]
    public void ValidDiagnosticFixturesProduceExpectedMetadata()
    {
        var folder = Path.Combine(Path.GetTempPath(), "glide-meta-fixtures-" + Guid.NewGuid());
        Directory.CreateDirectory(folder);
        try
        {
            var payloads = ImageDiagnosticFixtures.CreatePayloads();
            var png = Path.Combine(folder, "fixture.png"); File.WriteAllBytes(png, payloads[".png"]);
            var pngInfo = ImageMetadataReader.Read(png);
            Assert.Equal(24, pngInfo.Width); Assert.Equal(18, pngInfo.Height); Assert.Equal(1, pngInfo.FrameCount);
            var gif = Path.Combine(folder, "fixture.gif"); File.WriteAllBytes(gif, payloads[".gif"]);
            Assert.Equal(1, ImageMetadataReader.Read(gif).FrameCount);
        }
        finally { try { Directory.Delete(folder, true); } catch { } }
    }

    [Fact]
    public async Task SchedulerUsesAlreadyOpenHeaderBeforeOptionalBackendProbe()
    {
        var backend = new RecordingBackend(ImageDiagnosticFixtures.CreatePayloads()[".png"]);
        using var loader = new ImageLoadCoordinator(backend);
        var path = Path.Combine(Path.GetTempPath(), "glide-scheduler-" + Guid.NewGuid() + ".png");
        try
        {
            File.WriteAllBytes(path, backend.Payload);
            var result = await loader.LoadForegroundAsync(path);
            Assert.NotNull(result);
            result!.Bitmap.Dispose();
            Assert.Contains("decode", backend.Events);
            Assert.DoesNotContain("probe", backend.Events); // common header probe avoids provider/runtime wake-up
        }
        finally { try { File.Delete(path); } catch { } }
    }

    [Fact]
    public async Task SelectionExportWritesSelectedPixelsAndRejectsCancellation()
    {
        using var source = new WriteableBitmap(new PixelSize(2, 2), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Opaque);
        using (var frame = source.Lock())
        {
            var pixels = new byte[] { 1, 2, 3, 255, 11, 12, 13, 255, 21, 22, 23, 255, 31, 32, 33, 255 };
            Marshal.Copy(pixels, 0, frame.Address, pixels.Length);
        }
        var path = Path.Combine(Path.GetTempPath(), "glide-export-" + Guid.NewGuid() + ".png");
        try
        {
            await ImageSelectionExportService.ExportAsync(source, new PixelRect(1, 0, 1, 2), path);
            Assert.True(new FileInfo(path).Length > 0);
            using var decoded = new Bitmap(path);
            var bytes = new byte[8]; var memory = Marshal.AllocHGlobal(bytes.Length);
            try { decoded.CopyPixels(new PixelRect(0, 0, 1, 2), memory, bytes.Length, 4); Marshal.Copy(memory, bytes, 0, bytes.Length); }
            finally { Marshal.FreeHGlobal(memory); }
            Assert.Equal(new byte[] { 11, 12, 13, 255, 31, 32, 33, 255 }, bytes);
            // A second write replaces atomically and leaves a recoverable backup.
            await ImageSelectionExportService.ExportAsync(source, new PixelRect(0, 0, 1, 2), path);
            Assert.True(File.Exists(path + ".bak"));
            using var cts = new CancellationTokenSource(); cts.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ImageSelectionExportService.ExportAsync(source, new PixelRect(0, 0, 1, 1), path + ".cancel.png", cts.Token));
        }
        finally { try { File.Delete(path); File.Delete(path + ".bak"); File.Delete(path + ".cancel.png"); } catch { } }
    }

    [Theory]
    [InlineData("bad.jpg", "\xFF\xD8\xFF\xE1\x00\x20Exif\0\0")]
    [InlineData("bad.png", "\x89PNG\r\n\x1a\n\0\0\0\x10IHDR")]
    [InlineData("bad.gif", "GIF89a\0\0\0")]
    [InlineData("bad.webp", "RIFF\x01\0\0\0WEBP")]
    public void MetadataReaderRejectsMalformedFixtures(string name, string payload)
    {
        var path = Path.Combine(Path.GetTempPath(), "glide-meta-" + Guid.NewGuid() + Path.GetExtension(name));
        try { File.WriteAllBytes(path, System.Text.Encoding.Latin1.GetBytes(payload)); Assert.NotNull(ImageMetadataReader.Read(path)); }
        finally { try { File.Delete(path); } catch { } }
    }

    [Fact]
    public void MetadataReaderReturnsExpectedFixtureDimensionsAndFrames()
    {
        var root = Path.Combine(Path.GetTempPath(), "glide-meta-fixtures-" + Guid.NewGuid()); Directory.CreateDirectory(root);
        try
        {
            foreach (var pair in ImageDiagnosticFixtures.CreatePayloads()) File.WriteAllBytes(Path.Combine(root, "fixture" + pair.Key), pair.Value);
            var png = ImageMetadataReader.Read(Path.Combine(root, "fixture.png")); Assert.Equal(24, png.Width); Assert.Equal(18, png.Height); Assert.Equal(1, png.FrameCount);
            var tif = ImageMetadataReader.Read(Path.Combine(root, "fixture.tif")); Assert.Equal(24, tif.Width); Assert.Equal(18, tif.Height);
            var gif = ImageMetadataReader.Read(Path.Combine(root, "fixture.gif")); Assert.Equal(1, gif.FrameCount);
            var webp = ImageMetadataReader.Read(Path.Combine(root, "fixture.webp")); Assert.True(webp.Width > 0); Assert.True(webp.Height > 0); Assert.True(webp.FrameCount >= 1);
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }

    [Fact]
    public void MetadataReaderReportsWebpLosslessDimensionsAndGifMalformedAsZero()
    {
        var webp = Path.Combine(Path.GetTempPath(), "glide-meta-" + Guid.NewGuid() + ".webp");
        var gif = Path.Combine(Path.GetTempPath(), "glide-meta-" + Guid.NewGuid() + ".gif");
        try
        {
            // RIFF/WEBP + VP8L payload: 3x2 (lossless dimensions are minus-one packed).
            File.WriteAllBytes(webp, new byte[] { (byte)'R',(byte)'I',(byte)'F',(byte)'F', 0x0d,0,0,0,(byte)'W',(byte)'E',(byte)'B',(byte)'P',(byte)'V',(byte)'P',(byte)'8',(byte)'L', 5,0,0,0, 0x2f, 0x02, 0x40, 0x00, 0x00 });
            var metadata = ImageMetadataReader.Read(webp);
            Assert.Equal(3, metadata.Width); Assert.Equal(2, metadata.Height); Assert.Equal(1, metadata.FrameCount);
            File.WriteAllBytes(gif, new byte[] { (byte)'G',(byte)'I',(byte)'F',(byte)'8',(byte)'9',(byte)'a',0,0,0,0,0,0,0,0 });
            Assert.Equal(0, ImageMetadataReader.Read(gif).FrameCount);
        }
        finally { try { File.Delete(webp); File.Delete(gif); } catch { } }
    }

    private sealed class ProbeProvider : ICodecProvider
    {
        public bool Disposed { get; private set; }
        public CodecProviderInfo Info => new(1, CodecProviderAbi.MinimumStructSize, "probe-test", "1", "MIT", new[] { ".nii.gz" }, true, true, true, true);
        public CodecProbeResult Probe(string path, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return new CodecProbeResult(true, new ImageDimensions(320, 200), 1, false, null);
        }
        public Task<Stream?> DecodePreviewAsync(string path, int longestSide, CancellationToken cancellationToken = default) => Task.FromResult<Stream?>(new MemoryStream(new byte[] { 1 }));
        public Task<Stream?> DecodeFullAsync(string path, CancellationToken cancellationToken = default)
        { cancellationToken.ThrowIfCancellationRequested(); return Task.FromResult<Stream?>(new MemoryStream(new byte[] { 1 })); }
        public Task<ImageMetadata> ReadMetadataAsync(string path, CancellationToken cancellationToken = default) => Task.FromResult(new ImageMetadata(Make: "fixture"));
        public Task<IReadOnlyList<CodecFrameInfo>> ReadFramesAsync(string path, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<CodecFrameInfo>>(new[] { new CodecFrameInfo(0, 1, 1, null), new CodecFrameInfo(1, 1, 1, null) });
        public void Dispose() => Disposed = true;
    }

    private sealed class RecordingBackend : IImageDecoderBackend
    {
        public byte[] Payload { get; }
        public List<string> Events { get; } = new();
        public RecordingBackend(byte[] payload) => Payload = payload;
        public CodecProbeResult? Probe(string path, CancellationToken cancellationToken = default)
        { cancellationToken.ThrowIfCancellationRequested(); Events.Add("probe"); return new CodecProbeResult(true, new ImageDimensions(24, 18), 1, false, null); }
        public Bitmap DecodeFull(Stream stream) { Events.Add("decode"); return new Bitmap(stream); }
        public Bitmap DecodePreview(Stream stream, ImageDimensions source, int longestSide, BitmapInterpolationMode interpolationMode) { Events.Add("decode"); return new Bitmap(stream); }
    }

    [Fact]
    public void Shared_overlay_chrome_policy_keeps_internal_and_whole_window_bounds_consistent()
    {
        var size = new Avalonia.Size(400, 300);
        Assert.Equal(24, OverlayChromePolicy.ResizeRect(size).Width);
        Assert.Equal(OverlayChromePolicy.ResizeRect(size), LegacyOverlayChrome.ResizeRect(size));
        Assert.Equal(OverlayChromePolicy.CloseRect(size), LegacyOverlayChrome.CloseRect(size));
        Assert.Equal(0.10, OverlayChromePolicy.ClampItemOpacity(0));
        Assert.Equal(0.35, OverlayChromePolicy.ClampWholeWindowOpacity(0));
        Assert.False(OverlayChromePolicy.ShouldDismissChrome(new Avalonia.Point(390, 20), size));
        Assert.False(OverlayChromePolicy.ShouldDismissChrome(OverlayChromePolicy.SliderRect(size).Center, size));
        Assert.False(OverlayChromePolicy.ShouldDismissChrome(OverlayChromePolicy.ResizeRect(size).Center, size));
        Assert.True(OverlayChromePolicy.ShouldDismissChrome(new Avalonia.Point(200, 180), size));
    }
}
