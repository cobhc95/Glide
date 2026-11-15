using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Glide.Imaging;

namespace Glide.Core.Tests;

/// <summary>
/// Regression tests for the 5.0 security pass (R2-R10 of the 4.8.0 audit) and the 512 MP decoded-pixel cap.
/// Every "large image" here is a synthetic header or a synthetic memory snapshot: no test allocates a
/// full-size frame, and the fake decoder backend only ever creates tiny bitmaps.
/// </summary>
// Source-text contract kept: this asserts on production source (UI code-behind, native/installer files or a repo-wide scan) that cannot be run headlessly; behavioural coverage lives in the other test files.
[Collection(AvaloniaHeadlessCollection.Name)]
public sealed class SecurityHardening50Tests : IDisposable
{
    private const long GB = 1024L * 1024 * 1024;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "glide-sec50-" + Guid.NewGuid().ToString("N"));

    public SecurityHardening50Tests(AvaloniaHeadlessFixture fixture)
    {
        _ = fixture;
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        DecodeMemoryGuard.SnapshotOverrideForTests = null;
        VideoDecoderPack.ResetVerificationForTests();
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    // ============================================================================ helpers ========

    private static uint Crc32(ReadOnlySpan<byte> data)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var b in data)
        {
            crc ^= b;
            for (var k = 0; k < 8; k++) crc = (crc & 1) != 0 ? 0xEDB88320u ^ (crc >> 1) : crc >> 1;
        }
        return ~crc;
    }

    private static void Chunk(List<byte> png, string type, byte[] data)
    {
        var length = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(length, data.Length);
        png.AddRange(length);
        var typed = System.Text.Encoding.ASCII.GetBytes(type).Concat(data).ToArray();
        png.AddRange(typed);
        var crc = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(crc, Crc32(typed));
        png.AddRange(crc);
    }

    /// <summary>A structurally valid PNG (correct CRCs) that declares <paramref name="width"/> x <paramref name="height"/> but holds almost no data.</summary>
    private static byte[] DeclaredPng(int width, int height)
    {
        var png = new List<byte> { 137, 80, 78, 71, 13, 10, 26, 10 };
        var ihdr = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(0, 4), width);
        BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(4, 4), height);
        ihdr[8] = 8; ihdr[9] = 6; // 8-bit RGBA
        Chunk(png, "IHDR", ihdr);
        Chunk(png, "IDAT", new byte[] { 0x78, 0x9C, 0x63, 0x00, 0x00, 0x00, 0x01, 0x00, 0x01 });
        Chunk(png, "IEND", Array.Empty<byte>());
        return png.ToArray();
    }

    /// <summary>BMP header (BITMAPINFOHEADER, 32 bpp) declaring a frame, with no pixel data.</summary>
    private static byte[] DeclaredBmp(int width, int height)
    {
        var bmp = new byte[54 + 16];
        bmp[0] = (byte)'B'; bmp[1] = (byte)'M';
        BinaryPrimitives.WriteInt32LittleEndian(bmp.AsSpan(2), bmp.Length);
        BinaryPrimitives.WriteInt32LittleEndian(bmp.AsSpan(10), 54);
        BinaryPrimitives.WriteInt32LittleEndian(bmp.AsSpan(14), 40);
        BinaryPrimitives.WriteInt32LittleEndian(bmp.AsSpan(18), width);
        BinaryPrimitives.WriteInt32LittleEndian(bmp.AsSpan(22), height);
        BinaryPrimitives.WriteInt16LittleEndian(bmp.AsSpan(26), 1);
        BinaryPrimitives.WriteInt16LittleEndian(bmp.AsSpan(28), 32);
        return bmp;
    }

    private string Write(string name, byte[] bytes)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    private static void UseMemory(long availablePhysical, long availableCommit)
        => DecodeMemoryGuard.SnapshotOverrideForTests = () => new DecodeMemoryGuard.MemorySnapshot((ulong)availablePhysical, (ulong)availableCommit);

    /// <summary>Never decodes anything: returns tiny bitmaps and counts what the coordinator asked for.</summary>
    private sealed class FakeBackend : IImageDecoderBackend
    {
        public int FullDecodes;
        public int PreviewDecodes;
        public bool ThrowOutOfMemoryOnFull;

        private static Bitmap Tiny(int width, int height)
            => new WriteableBitmap(new PixelSize(width, height), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);

        public Bitmap DecodeFull(Stream stream)
        {
            Interlocked.Increment(ref FullDecodes);
            if (ThrowOutOfMemoryOnFull) throw new OutOfMemoryException("synthetic allocation failure");
            return Tiny(64, 64);
        }

        public Bitmap DecodePreview(Stream stream, ImageDimensions source, int longestSide, BitmapInterpolationMode interpolationMode)
        {
            Interlocked.Increment(ref PreviewDecodes);
            return Tiny(32, 32);
        }
    }

    private static ImagePerformancePolicy Policy() => ImagePerformancePolicy.ForProfile("Balanced") with
    {
        PredictivePrefetch = false,
        BackgroundRefinement = true,
        RefinementDelayMs = 5,
    };

    // ================================================== A. 512 MP cap and the memory guard ========

    [Fact]
    public void Pixel_cap_is_512_megapixels_in_every_managed_check()
    {
        Assert.Equal(512_000_000L, DecodeMemoryGuard.MaxTotalPixels);
        Assert.Equal(DecodeMemoryGuard.MaxTotalPixels, ImageHeaderProbe.MaxTotalPixels);

        Assert.True(ImageHeaderProbe.TryProbe(DeclaredPng(25_000, 20_000), out var accepted)); // 500 MP
        Assert.Equal(new ImageDimensions(25_000, 20_000), accepted);
        Assert.False(ImageHeaderProbe.TryProbe(DeclaredPng(30_000, 20_000), out _));           // 600 MP

        Assert.False(DecodeMemoryGuard.ExceedsPixelCap(new ImageDimensions(32_000, 16_000)));
        Assert.True(DecodeMemoryGuard.ExceedsPixelCap(new ImageDimensions(32_000, 16_001)));
        Assert.True(DecodeMemoryGuard.ExceedsPixelCap(new ImageDimensions(65_536, 10)));

        // The native bridge enforces the same number (checked against the scratch build by Native_bridge_* below).
        var native = File.ReadAllText(RepositoryFile("native", "Glide.Native", "GlideNative.cpp"));
        Assert.Contains("GlideMaxDecodePixels = 512'000'000ull", native, StringComparison.Ordinal);
        var builtIn = File.ReadAllText(RepositoryFile("src", "Glide.Imaging", "BuiltInRasterDecoder.cs"));
        Assert.Contains("MaxTotalPixels = DecodeMemoryGuard.MaxTotalPixels", builtIn, StringComparison.Ordinal);
    }

    [Fact]
    public void Memory_guard_rules_with_synthetic_snapshots()
    {
        var frame512 = DecodeMemoryGuard.EstimatedFrameBytes(new ImageDimensions(32_000, 16_000)); // 2.048 GB
        Assert.Equal(2_048_000_000L, frame512);

        // Small frames are always allowed, even with almost no free memory.
        Assert.True(DecodeMemoryGuard.FitsAvailableMemory(200L * 1024 * 1024, new(64L * 1024 * 1024, 64L * 1024 * 1024)));
        // A 512 MP frame needs at most half of the available memory.
        Assert.True(DecodeMemoryGuard.FitsAvailableMemory(frame512, new(8 * (ulong)GB, 16 * (ulong)GB)));
        Assert.False(DecodeMemoryGuard.FitsAvailableMemory(frame512, new(3 * (ulong)GB, 16 * (ulong)GB)));
        // The commit the process can still allocate counts as well.
        Assert.False(DecodeMemoryGuard.FitsAvailableMemory(frame512, new(16 * (ulong)GB, 3 * (ulong)GB)));
        // Unknown memory: only the pixel cap applies.
        Assert.True(DecodeMemoryGuard.FitsAvailableMemory(frame512, null));

        UseMemory(64 * GB, 64 * GB);
        Assert.True(DecodeMemoryGuard.AllowsFullDecode(new ImageDimensions(32_000, 16_000)));
        Assert.False(DecodeMemoryGuard.AllowsFullDecode(new ImageDimensions(32_000, 16_001)));
        UseMemory(2 * GB, 2 * GB);
        Assert.False(DecodeMemoryGuard.AllowsFullDecode(new ImageDimensions(32_000, 16_000)));
        Assert.True(DecodeMemoryGuard.AllowsFullDecode(new ImageDimensions(6000, 4000)));
    }

    [Fact]
    public async Task Full_request_beyond_free_memory_keeps_the_preview_instead_of_decoding_full()
    {
        var path = Write("big.png", DeclaredPng(20_000, 20_000)); // 400 MP: inside the cap, 1.6 GB decoded
        UseMemory(1 * GB, 4 * GB);
        var backend = new FakeBackend();
        using var loader = new ImageLoadCoordinator(backend) { Policy = Policy() };

        var result = await loader.LoadFullForegroundAsync(path, cacheResult: false);

        Assert.NotNull(result);
        Assert.Equal(0, backend.FullDecodes);
        Assert.Equal(1, backend.PreviewDecodes);
        Assert.True(result!.IsPreview);
        Assert.Equal((20_000, 20_000), (result.SourceWidth, result.SourceHeight));
        loader.ReleaseBitmap(result.Bitmap);
    }

    [Fact]
    public async Task Full_request_with_enough_memory_still_decodes_the_full_frame()
    {
        var path = Write("big-ok.png", DeclaredPng(20_000, 20_000));
        UseMemory(64 * GB, 64 * GB);
        var backend = new FakeBackend();
        using var loader = new ImageLoadCoordinator(backend) { Policy = Policy() };

        var result = await loader.LoadFullForegroundAsync(path, cacheResult: false);

        Assert.NotNull(result);
        Assert.Equal(1, backend.FullDecodes);
        Assert.False(result!.IsPreview);
        loader.ReleaseBitmap(result.Bitmap);
    }

    [Fact]
    public async Task Allocation_failure_on_the_full_frame_falls_back_to_the_preview()
    {
        var path = Write("oom.png", DeclaredPng(20_000, 20_000));
        UseMemory(64 * GB, 64 * GB); // the guard lets it through; the allocation itself then fails
        var backend = new FakeBackend { ThrowOutOfMemoryOnFull = true };
        using var loader = new ImageLoadCoordinator(backend) { Policy = Policy() };

        var result = await loader.LoadFullForegroundAsync(path, cacheResult: false);

        Assert.NotNull(result);
        Assert.Equal(1, backend.FullDecodes);
        Assert.Equal(1, backend.PreviewDecodes);
        Assert.True(result!.IsPreview);
        loader.ReleaseBitmap(result.Bitmap);
    }

    [Fact]
    public async Task Preview_first_still_works_and_refinement_is_skipped_when_the_full_frame_would_not_fit()
    {
        var path = Write("refine.png", DeclaredPng(20_000, 20_000));
        UseMemory(1 * GB, 4 * GB);
        var backend = new FakeBackend();
        using var loader = new ImageLoadCoordinator(backend) { Policy = Policy() };

        var first = await loader.LoadForegroundAsync(path, loader.Policy);
        Assert.NotNull(first);
        Assert.True(first!.IsPreview);
        Assert.Equal(0, backend.FullDecodes);
        var decodes = backend.PreviewDecodes;

        var refined = await loader.RefineForegroundAsync(first);

        Assert.Null(refined);
        Assert.Equal(decodes, backend.PreviewDecodes);
        Assert.Equal(0, backend.FullDecodes);
        loader.ReleaseBitmap(first.Bitmap);

        // Control: with enough memory the same refinement does run (a bounded decode, as before 5.0).
        UseMemory(64 * GB, 64 * GB);
        var control = new FakeBackend();
        using var controlLoader = new ImageLoadCoordinator(control) { Policy = Policy() };
        var controlFirst = await controlLoader.LoadForegroundAsync(path, controlLoader.Policy);
        Assert.NotNull(controlFirst);
        var before = control.PreviewDecodes + control.FullDecodes;
        var controlRefined = await controlLoader.RefineForegroundAsync(controlFirst!);
        Assert.True(control.PreviewDecodes + control.FullDecodes > before, "refinement never ran with ample memory");
        if (controlRefined is not null) controlLoader.ReleaseBitmap(controlRefined.Bitmap);
        controlLoader.ReleaseBitmap(controlFirst!.Bitmap);
    }

    [Fact]
    public void Startup_preloader_never_full_decodes_a_frame_that_does_not_fit_free_memory()
    {
        // 12000 x 12000 (576 MB decoded) is inside the pixel cap; with 512 MB free it must not be decoded at
        // startup (the foreground path shows its preview instead).
        var path = Write("preload-big.png", DeclaredPng(12_000, 12_000));
        UseMemory(512L * 1024 * 1024, 4 * GB);

        using var result = StartupImagePreloader.PreloadForTests(path, 0, 0);

        Assert.Null(result);
    }

    [Fact]
    public void Startup_preloader_hands_over_bytes_only_for_a_known_size()
    {
        // Unknown format/size: the consumer would decode the bytes at full size without any check (R3).
        var path = Write("unknown.dds", new byte[] { 0x44, 0x44, 0x53, 0x20, 1, 2, 3, 4, 5, 6, 7, 8 });

        using var result = StartupImagePreloader.PreloadForTests(path, 0, 0);

        Assert.True(result is null || result.PreloadedBytes is null, "bytes of an unknown-size image were handed over");
    }

    [Fact]
    public void Skia_fallback_reads_the_size_and_refuses_before_allocating()
    {
        using (var overCap = new MemoryStream(DeclaredBmp(30_000, 20_000))) // 600 MP
        {
            overCap.Position = 0;
            Assert.True(SkiaDecodeGuard.TryReadOutputSize(overCap, 0, out var size));
            Assert.Equal(new ImageDimensions(30_000, 20_000), size);
            Assert.Equal(0, overCap.Position);
            var refused = Assert.ThrowsAny<Exception>(() => SkiaDecodeGuard.EnsureAllowed(overCap, 0));
            Assert.True(SkiaDecodeGuard.IsRefusal(refused));
            Assert.Equal(0, overCap.Position);
        }

        // Inside the cap but beyond free memory: refused as an allocation failure (the loader keeps the preview).
        UseMemory(512L * 1024 * 1024, 4 * GB);
        using (var big = new MemoryStream(DeclaredBmp(12_000, 12_000)))
        {
            Assert.Throws<InsufficientMemoryException>(() => new AvaloniaImageDecoderBackend().DecodeFull(big, null));
        }

        // A normal frame passes.
        UseMemory(64 * GB, 64 * GB);
        using var small = new MemoryStream(DeclaredBmp(4000, 3000));
        SkiaDecodeGuard.EnsureAllowed(small, 0);
    }

    [Fact]
    public void Psd_without_its_pixel_data_is_refused_before_any_plane_is_allocated()
    {
        // 10000 x 10000 RGB, raw: needs 300 MB of channel data, the file has none.
        var psd = new byte[26 + 4 + 4 + 4 + 2];
        "8BPS"u8.CopyTo(psd);
        BinaryPrimitives.WriteUInt16BigEndian(psd.AsSpan(4), 1);         // version
        BinaryPrimitives.WriteUInt16BigEndian(psd.AsSpan(12), 3);        // channels
        BinaryPrimitives.WriteUInt32BigEndian(psd.AsSpan(14), 10_000);   // height
        BinaryPrimitives.WriteUInt32BigEndian(psd.AsSpan(18), 10_000);   // width
        BinaryPrimitives.WriteUInt16BigEndian(psd.AsSpan(22), 8);        // depth
        BinaryPrimitives.WriteUInt16BigEndian(psd.AsSpan(24), 3);        // RGB
        // colour mode, image resources, layer section: all empty; compression 0 (raw)
        var path = Write("hollow.psd", psd);

        var before = GC.GetAllocatedBytesForCurrentThread();
        var decoded = BuiltInRasterDecoder.TryDecode(path, out var bitmap);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.False(decoded);
        Assert.Null(bitmap);
        Assert.True(allocated < 32L * 1024 * 1024, $"allocated {allocated:N0} bytes for a 38-byte file");

        // RLE: a byte-count table that promises more data than the file holds is refused the same way.
        var header = new BuiltInRasterDecoder.PsdHeader(false, 3, 10_000, 10_000, 8, 3);
        var rle = new byte[100 + 3 * 10_000 * 2];
        for (var i = 0; i < 3 * 10_000; i++) BinaryPrimitives.WriteUInt16BigEndian(rle.AsSpan(100 + i * 2), 200);
        Assert.False(BuiltInRasterDecoder.PsdHasPixelData(rle, header, 1, 3, 10_000, 100, 100 + 3 * 10_000 * 2));
        // ...and rows too short to expand to a full row (PackBits expands at most 64:1).
        for (var i = 0; i < 3 * 10_000; i++) BinaryPrimitives.WriteUInt16BigEndian(rle.AsSpan(100 + i * 2), 0);
        Assert.False(BuiltInRasterDecoder.PsdHasPixelData(rle, header, 1, 3, 10_000, 100, 100 + 3 * 10_000 * 2));
    }

    // ===================================================== C. native bridge cap (C++) ==============

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate ulong GetMaxDecodePixelsFn();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl, CharSet = CharSet.Unicode)]
    private delegate int DecodeImageFn([MarshalAs(UnmanagedType.LPWStr)] string path, uint maxLongestSide, out NativeImage image);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl, CharSet = CharSet.Unicode)]
    private delegate int DecodeIntoFn([MarshalAs(UnmanagedType.LPWStr)] string path, IntPtr allocate, IntPtr context);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr AllocateFn(IntPtr context, uint width, uint height, IntPtr stride);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void FreeFn(IntPtr data);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeImage { public uint Width, Height, Stride; public ulong BufferBytes; public IntPtr Data; }

    [Fact]
    public void Native_bridge_refuses_an_over_cap_frame_before_allocating()
    {
        if (!OperatingSystem.IsWindows()) return;
        var dll = Environment.GetEnvironmentVariable("GLIDE_TEST_NATIVE_DLL");
        if (string.IsNullOrWhiteSpace(dll)) dll = Path.Combine(AppContext.BaseDirectory, "Glide.Native.dll");
        if (!File.Exists(dll)) return;
        var module = NativeLibrary.Load(dll);
        // Bridges built before 5.0 have no cap (and no export); build.cmd rebuilds the bridge before testing.
        if (!NativeLibrary.TryGetExport(module, "GlideGetMaxDecodePixels", out var getMax)) return;
        Assert.Equal(512_000_000ul, Marshal.GetDelegateForFunctionPointer<GetMaxDecodePixelsFn>(getMax)());

        var decode = Marshal.GetDelegateForFunctionPointer<DecodeImageFn>(NativeLibrary.GetExport(module, "GlideDecodeImageW"));
        var decodeInto = Marshal.GetDelegateForFunctionPointer<DecodeIntoFn>(NativeLibrary.GetExport(module, "GlideDecodeImageIntoW"));
        var free = Marshal.GetDelegateForFunctionPointer<FreeFn>(NativeLibrary.GetExport(module, "GlideFreeImageBuffer"));

        var allocations = new List<(uint Width, uint Height, IntPtr Memory)>();
        AllocateFn allocate = (_, width, height, stride) =>
        {
            var memory = Marshal.AllocHGlobal((nint)((long)width * height * 4));
            Marshal.WriteInt32(stride, (int)(width * 4));
            allocations.Add((width, height, memory));
            return memory;
        };
        var allocatePointer = Marshal.GetFunctionPointerForDelegate(allocate);
        try
        {
            // 30000 x 20000 = 600 MP; no managed probe is involved here, WIC reads the size itself.
            var overCap = Write("native-over-cap.png", DeclaredPng(30_000, 20_000));
            Assert.Equal(0, decodeInto(overCap, allocatePointer, IntPtr.Zero));
            Assert.Empty(allocations);
            Assert.Equal(0, decode(overCap, 0, out var image));
            Assert.Equal(IntPtr.Zero, image.Data);

            // A bounded (preview) request for the same file only ever produces its reduced frame.
            if (decode(overCap, 512, out var preview) == 1)
            {
                Assert.True(Math.Max(preview.Width, preview.Height) <= 512);
                free(preview.Data);
            }

            // A normal image still decodes into the caller's surface.
            var pixel = Write("native-pixel.png", Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8DwHwAFBQIAX8jx0gAAAABJRU5ErkJggg=="));
            Assert.Equal(1, decodeInto(pixel, allocatePointer, IntPtr.Zero));
            Assert.Single(allocations);
            Assert.Equal((1u, 1u), (allocations[0].Width, allocations[0].Height));
        }
        finally
        {
            foreach (var allocation in allocations) Marshal.FreeHGlobal(allocation.Memory);
            GC.KeepAlive(allocate);
        }
    }

    [Fact]
    public void Native_projects_are_built_with_exploit_mitigations()
    {
        foreach (var project in new[] { "Glide.Native", "Glide.ShellThumbnail" })
        {
            var cmake = File.ReadAllText(RepositoryFile("native", project, "CMakeLists.txt"));
            foreach (var flag in new[] { "/guard:cf", "/sdl", "/CETCOMPAT", "/DYNAMICBASE", "/NXCOMPAT", "/Qspectre" })
                Assert.Contains(flag, cmake, StringComparison.Ordinal);
        }
        var provider = File.ReadAllText(RepositoryFile("native", "Glide.ShellThumbnail", "GlideThumbnailProvider.cpp"));
        Assert.Contains("cx = glide::thumb::ClampRequestedEdge(cx);", provider, StringComparison.Ordinal);
    }

    // ============================================== B. installer: custom folder permissions ========

    [Fact]
    public void Installer_locks_down_a_custom_install_folder_before_files_and_before_running_glide()
    {
        var script = File.ReadAllText(RepositoryFile("installer", "Glide.iss"));
        Assert.Contains("/inheritance:r /grant:r *S-1-5-18:(OI)(CI)F *S-1-5-32-544:(OI)(CI)F", script, StringComparison.Ordinal);
        Assert.Contains("*S-1-5-32-545:(OI)(CI)RX", script, StringComparison.Ordinal);
        Assert.Contains("/setowner *S-1-5-32-544", script, StringComparison.Ordinal);
        // Program Files is already safe; the user's own profile (per-user installs) keeps working.
        Assert.Contains("not IsProgramFilesFolder(Dir) and not IsUserProfileFolder(Dir)", script, StringComparison.Ordinal);
        Assert.Contains("ExpandConstant('{localappdata}')", script, StringComparison.Ordinal);

        var step = script.IndexOf("procedure CurStepChanged", StringComparison.Ordinal);
        var install = script.IndexOf("CurStep = ssInstall", step, StringComparison.Ordinal);
        var post = script.IndexOf("CurStep = ssPostInstall", step, StringComparison.Ordinal);
        var lockAfterPost = script.IndexOf("LockDownInstallFolder(AppDir, True)", post, StringComparison.Ordinal);
        var register = script.IndexOf("RegisterGlideAssociations;", post, StringComparison.Ordinal);
        Assert.True(install > step && script.IndexOf("LockDownInstallFolder(AppDir, False)", install, StringComparison.Ordinal) < post);
        Assert.True(lockAfterPost > post && lockAfterPost < register, "the folder must be locked down before Glide.exe runs elevated");
    }

    // ================================================== D. FFmpeg pack verified at load time ========

    private (string Directory, VideoDecoderPack.PinnedLibrary[] Pins) FakePack()
    {
        var directory = Path.Combine(_dir, "ffmpeg-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var pins = new List<VideoDecoderPack.PinnedLibrary>();
        foreach (var (name, size) in new[] { ("avutil-test.dll", 3000), ("avcodec-test.dll", 5000) })
        {
            var bytes = new byte[size];
            new Random(size).NextBytes(bytes);
            File.WriteAllBytes(Path.Combine(directory, name), bytes);
            pins.Add(new(name, size, Convert.ToHexString(SHA256.HashData(bytes))));
        }
        return (directory, pins.ToArray());
    }

    [Fact]
    public void Downloaded_ffmpeg_libraries_are_hashed_in_every_process_and_held_until_exit()
    {
        var (directory, pins) = FakePack();
        try
        {
            Assert.True(VideoDecoderPack.TryVerifyLibraries(directory, pins));
            Assert.Equal(2, VideoDecoderPack.LastVerificationHashedFiles);
            // Nothing is written beside the libraries any more (5.0.1: the forgeable stamp is gone).
            Assert.False(File.Exists(Path.Combine(directory, VideoDecoderPack.LegacyVerifiedStampFileName)));
            // While verified, nobody can rewrite, delete or swap a library.
            var library = Path.Combine(directory, pins[0].Name);
            Assert.ThrowsAny<IOException>(() => new FileStream(library, FileMode.Open, FileAccess.Write, FileShare.ReadWrite).Dispose());
            Assert.ThrowsAny<Exception>(() => File.Delete(library));
            Assert.ThrowsAny<Exception>(() => File.Move(library, library + ".old"));
            Assert.True(File.Exists(library));

            // Within the same process the verified set is reused (cheap)...
            Assert.True(VideoDecoderPack.TryVerifyLibraries(directory, pins));

            // ...but the next process (simulated) hashes every library again.
            VideoDecoderPack.ResetVerificationForTests();
            Assert.True(VideoDecoderPack.TryVerifyLibraries(directory, pins));
            Assert.Equal(2, VideoDecoderPack.LastVerificationHashedFiles);
        }
        finally { VideoDecoderPack.ResetVerificationForTests(); }
    }

    [Fact]
    public void A_forged_legacy_stamp_never_lets_a_tampered_library_through_and_is_removed()
    {
        var (directory, pins) = FakePack();
        try
        {
            // Tamper with a library (same size), then forge a 5.0.0-style stamp that "vouches" for exactly this
            // file: name, size, write/creation time and the pinned hash.
            var target = Path.Combine(directory, pins[1].Name);
            var bytes = File.ReadAllBytes(target);
            bytes[100] ^= 0xFF;
            File.WriteAllBytes(target, bytes);
            var stamp = Path.Combine(directory, VideoDecoderPack.LegacyVerifiedStampFileName);
            File.WriteAllLines(stamp, pins.Select(pin =>
            {
                var info = new FileInfo(Path.Combine(directory, pin.Name));
                return string.Join('|', pin.Name, info.Length, info.LastWriteTimeUtc.Ticks, info.CreationTimeUtc.Ticks, pin.Sha256.ToUpperInvariant());
            }));

            Assert.False(VideoDecoderPack.TryVerifyLibraries(directory, pins));
            Assert.False(File.Exists(stamp));

            // Valid files still load, and a stamp beside them changes nothing.
            bytes[100] ^= 0xFF;
            File.WriteAllBytes(target, bytes);
            File.WriteAllText(stamp, "anything");
            Assert.True(VideoDecoderPack.TryVerifyLibraries(directory, pins));
            Assert.Equal(2, VideoDecoderPack.LastVerificationHashedFiles);
            Assert.False(File.Exists(stamp));
        }
        finally { VideoDecoderPack.ResetVerificationForTests(); }
    }

    [Fact]
    public void Hashing_the_real_ffmpeg_pack_stays_well_under_a_second()
    {
        // Measured against the real pinned DLLs when they are on this PC (developer PCs, build.cmd).
        var bin = Path.Combine(RepositoryRoot(), "native", "third_party", "ffmpeg", "bin");
        if (!VideoDecoderPack.PinnedLibraries.All(pin => File.Exists(Path.Combine(bin, pin.Name)))) return;
        try
        {
            // Warm the file cache once (the first activation after a reboot reads from disk; that is I/O, not hashing).
            Assert.True(VideoDecoderPack.TryVerifyLibraries(bin, VideoDecoderPack.PinnedLibraries));
            VideoDecoderPack.ResetVerificationForTests();
            Assert.True(VideoDecoderPack.TryVerifyLibraries(bin, VideoDecoderPack.PinnedLibraries));
            Assert.Equal(5, VideoDecoderPack.LastVerificationHashedFiles);
            Assert.True(VideoDecoderPack.LastVerificationDuration < TimeSpan.FromSeconds(1),
                $"hashing the pack took {VideoDecoderPack.LastVerificationDuration.TotalMilliseconds:F0} ms");
        }
        finally { VideoDecoderPack.ResetVerificationForTests(); }
    }

    [Fact]
    public void Modified_missing_or_resized_ffmpeg_libraries_are_refused()
    {
        var (directory, pins) = FakePack();
        try
        {
            Assert.True(VideoDecoderPack.TryVerifyLibraries(directory, pins));
            VideoDecoderPack.ResetVerificationForTests();

            // Same size, different content and a new write time: the hash fails.
            var target = Path.Combine(directory, pins[1].Name);
            var bytes = File.ReadAllBytes(target);
            bytes[100] ^= 0xFF;
            File.WriteAllBytes(target, bytes);
            File.SetLastWriteTimeUtc(target, DateTime.UtcNow.AddMinutes(5));
            Assert.False(VideoDecoderPack.TryVerifyLibraries(directory, pins));

            File.WriteAllBytes(target, bytes.Take(10).ToArray());
            Assert.False(VideoDecoderPack.TryVerifyLibraries(directory, pins));

            File.Delete(target);
            Assert.False(VideoDecoderPack.TryVerifyLibraries(directory, pins));
        }
        finally { VideoDecoderPack.ResetVerificationForTests(); }
    }

    [Fact]
    public void Pinned_ffmpeg_hashes_match_the_published_pack_libraries()
    {
        Assert.Equal(5, VideoDecoderPack.PinnedLibraries.Length);
        Assert.All(VideoDecoderPack.PinnedLibraries, pin => Assert.Equal(64, pin.Sha256.Length));
        // The pack is built from native\third_party\ffmpeg\bin (tools\package-ffmpeg-pack.ps1); when the
        // fetched FFmpeg is present (developer PCs, build.cmd) the pinned values must match it.
        var bin = Path.Combine(RepositoryRoot(), "native", "third_party", "ffmpeg", "bin");
        if (!Directory.Exists(bin)) return;
        foreach (var pin in VideoDecoderPack.PinnedLibraries)
        {
            var file = Path.Combine(bin, pin.Name);
            Assert.True(File.Exists(file), pin.Name);
            Assert.Equal(pin.Bytes, new FileInfo(file).Length);
            using var stream = File.OpenRead(file);
            Assert.Equal(pin.Sha256, Convert.ToHexString(SHA256.HashData(stream)));
        }
    }

    [Fact]
    public void Ffmpeg_download_override_must_be_https_or_this_pc()
    {
        var pinned = VideoDecoderPack.ResolvePackUrl(null);
        Assert.StartsWith("https://github.com/", pinned, StringComparison.Ordinal);
        Assert.Equal(pinned, VideoDecoderPack.ResolvePackUrl("http://example.com/pack.zip"));
        Assert.Equal(pinned, VideoDecoderPack.ResolvePackUrl("file://server/share/pack.zip"));
        Assert.Equal("https://example.com/pack.zip", VideoDecoderPack.ResolvePackUrl("https://example.com/pack.zip"));
        Assert.Equal("http://127.0.0.1:8080/pack.zip", VideoDecoderPack.ResolvePackUrl("http://127.0.0.1:8080/pack.zip"));
    }

    // ===================================================== E. per-user single-instance names ========

    [Fact]
    public void Single_instance_pipe_and_presence_names_are_scoped_to_the_user()
    {
        var user = Glide.App.Services.ExternalLaunchBroker.CurrentUserKey();
        var pipe = Glide.App.Services.ExternalLaunchBroker.PipeName;
        Assert.Equal(ProtocolNames.UserScoped(ProtocolNames.PipeName, user), pipe);
        Assert.StartsWith(ProtocolNames.PipeName + ".", pipe, StringComparison.Ordinal);
        Assert.DoesNotContain(user, pipe, StringComparison.OrdinalIgnoreCase); // a hash, not the raw SID
        Assert.NotEqual(pipe, ProtocolNames.UserScoped(ProtocolNames.PipeName, "S-1-5-21-1-2-3-1001"));
        Assert.Equal(ProtocolNames.UserScoped("x", "S-1-5-21-1"), ProtocolNames.UserScoped("x", "s-1-5-21-1"));
        if (OperatingSystem.IsWindows()) Assert.StartsWith("S-1-", user, StringComparison.Ordinal);

        // Both ends use the same scoped name, and CurrentUserOnly stays on.
        var broker = File.ReadAllText(RepositoryFile("src", "Glide.App", "Services", "ExternalLaunchBroker.cs"));
        Assert.Contains("new NamedPipeClientStream(\".\", PipeName, PipeDirection.InOut, ClientPipeOptions)", broker, StringComparison.Ordinal);
        Assert.Contains("PipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, ServerPipeOptions)", broker, StringComparison.Ordinal);
        Assert.DoesNotContain("const string PipeName", broker, StringComparison.Ordinal);
        Assert.True(Glide.App.Services.ExternalLaunchBroker.ClientPipeOptions.HasFlag(System.IO.Pipes.PipeOptions.CurrentUserOnly));
        Assert.True(Glide.App.Services.ExternalLaunchBroker.ServerPipeOptions.HasFlag(System.IO.Pipes.PipeOptions.CurrentUserOnly));
    }

    // ============================================================== H. local signing script ========

    [Fact]
    public void Local_signing_never_trusts_its_certificate_unless_asked()
    {
        var script = File.ReadAllText(RepositoryFile("tools", "sign-local.ps1"));
        Assert.Contains("[switch]$TrustOnThisPc", script, StringComparison.Ordinal);
        Assert.Contains("[switch]$RemoveLocalTrust", script, StringComparison.Ordinal);
        Assert.Contains("GLIDE_SIGN_CERT_THUMBPRINT", script, StringComparison.Ordinal);
        var trustBlock = script.IndexOf("if ($TrustOnThisPc) {", StringComparison.Ordinal);
        Assert.True(trustBlock > 0);
        // Every import into a trust store happens inside the explicit opt-in block.
        var import = script.IndexOf("Import-Certificate", StringComparison.Ordinal);
        Assert.True(import > trustBlock, "certificates are imported into trust stores outside the -TrustOnThisPc block");
        Assert.Equal(import, script.LastIndexOf("Import-Certificate", StringComparison.Ordinal));
        Assert.True(File.Exists(RepositoryFile("docs", "code-signing.md")));
    }

    // ======================================================= I. imports drop network folders ========

    [Theory]
    [InlineData(@"\\attacker\share", true)]
    [InlineData(@"\\?\UNC\attacker\share\x", true)]
    [InlineData(@"//attacker/share", true)]
    [InlineData("  \\\\host\\pics", true)]
    [InlineData("\"\\\\host\\pics\"", true)]
    [InlineData("file://host/share", true)]
    [InlineData(@"C:\Users\me\Pictures", false)]
    [InlineData(@"D:\", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Network_paths_are_recognised(string? path, bool network)
        => Assert.Equal(network, Glide.App.Settings.SettingsWindow.IsNetworkPath(path));

    [Fact]
    public void Imported_files_never_bring_network_folders()
    {
        var current = new Glide.App.Settings.GlideSettingsState
        {
            StartupCustomPath = @"C:\Pictures",
            OpenFileDefaultDirectory = @"\\old\share", // this PC's own value is a network path: cleared, not kept
            LastOpenDirectory = @"D:\Photos",
        };
        var imported = new Glide.App.Settings.GlideSettingsState
        {
            StartupCustomPath = @"\\attacker\share\pics",
            NewTabCustomPath = @"\\attacker\x",
            NewExplorerTabDefaultDirectory = @"E:\Shared",
            LastOpenDirectory = @"//attacker/y",
            OpenFileDefaultDirectory = @"\\attacker\z",
            OverlayDefaultDirectory = @"C:\Overlay",
            ProfileDefaultDirectory = "file://attacker/p",
            LastNewExplorerTabDirectory = @"\\?\UNC\attacker\q",
        };

        Assert.True(Glide.App.Settings.SettingsWindow.KeepLocalFolderPaths(imported, current));
        Assert.Equal(@"C:\Pictures", imported.StartupCustomPath);
        Assert.Equal("", imported.NewTabCustomPath);
        Assert.Equal(@"E:\Shared", imported.NewExplorerTabDefaultDirectory);
        Assert.Equal(@"D:\Photos", imported.LastOpenDirectory);
        Assert.Equal("", imported.OpenFileDefaultDirectory);
        Assert.Equal(@"C:\Overlay", imported.OverlayDefaultDirectory);
        Assert.Equal("", imported.ProfileDefaultDirectory);
        Assert.Equal("", imported.LastNewExplorerTabDirectory);

        var local = new Glide.App.Settings.GlideSettingsState { StartupCustomPath = @"C:\Local" };
        Assert.False(Glide.App.Settings.SettingsWindow.KeepLocalFolderPaths(local, current));
        Assert.Equal(@"C:\Local", local.StartupCustomPath);

        Assert.Equal(" (network folder paths were not imported)", Glide.App.Settings.SettingsWindow.ImportNotes(false, true));
        Assert.EndsWith(Glide.App.Settings.SettingsWindow.NetworkFoldersDroppedNote,
            Glide.App.Settings.SettingsWindow.ImportNotes(true, true), StringComparison.Ordinal);
        Assert.Equal("", Glide.App.Settings.SettingsWindow.ImportNotes(false, false));

        // Both import paths (profile load and settings import) apply it and report it.
        var profiles = File.ReadAllText(RepositoryFile("src", "Glide.App", "Settings", "SettingsWindow.Profiles.cs"));
        var settings = File.ReadAllText(RepositoryFile("src", "Glide.App", "Settings", "SettingsWindow.axaml.cs"));
        Assert.Contains("KeepLocalFolderPaths(state, current)", profiles, StringComparison.Ordinal);
        Assert.Contains("KeepLocalFolderPaths(imported, current)", settings, StringComparison.Ordinal);
        Assert.Contains("ImportNotes(launchTargetsKept, networkFoldersDropped)", settings, StringComparison.Ordinal);
    }

    // ============================================================ J. dead security code removed ========

    [Fact]
    public void Unused_network_fetcher_and_disk_cache_are_gone()
    {
        var core = typeof(ProtocolNames).Assembly;
        Assert.Null(core.GetType("Glide.Core.Security.SecureImageFetcher"));
        Assert.Null(core.GetType("Glide.Core.Security.SecureDiskCache"));
    }

    // ==================================================================== repository helpers ========

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Glide.sln"))) directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException("Glide.sln not found above the test output.");
    }

    private static string RepositoryFile(params string[] segments) => Path.Combine(new[] { RepositoryRoot() }.Concat(segments).ToArray());
}
