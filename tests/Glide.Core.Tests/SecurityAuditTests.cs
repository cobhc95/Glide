using System.Buffers.Binary;
using Glide.Imaging;

namespace Glide.Core.Tests;

// Source-text contract kept: this asserts on production source (UI code-behind, native/installer files or a repo-wide scan) that cannot be run headlessly; behavioural coverage lives in the other test files.
[Trait("Area", "Security")]
public sealed class SecurityAuditTests : IDisposable
{
    private readonly string _tempCacheDir;

    public SecurityAuditTests()
    {
        _tempCacheDir = Path.Combine(Path.GetTempPath(), "GlideSecurityAuditTest-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempCacheDir);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempCacheDir))
                Directory.Delete(_tempCacheDir, recursive: true);
        }
        catch { }
    }

    #region Decompression Bomb / Dimension Defenses

    [Fact]
    public void ImageHeaderProbe_Rejects_Extreme_Dimensions_Decompression_Bomb()
    {
        // Craft a mock PNG header declaring a 100,000 x 100,000 dimension bomb
        var pngBomb = new byte[32];
        // PNG Signature
        new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }.CopyTo(pngBomb, 0);
        // IHDR chunk length (13 bytes)
        BinaryPrimitives.WriteInt32BigEndian(pngBomb.AsSpan(8, 4), 13);
        // IHDR chunk type "IHDR"
        "IHDR"u8.CopyTo(pngBomb.AsSpan(12, 4));
        // Width: 100,000 px, Height: 100,000 px (10 Billion pixels = 40 GB uncompressed)
        BinaryPrimitives.WriteInt32BigEndian(pngBomb.AsSpan(16, 4), 100_000);
        BinaryPrimitives.WriteInt32BigEndian(pngBomb.AsSpan(20, 4), 100_000);

        var probed = ImageHeaderProbe.TryProbe(pngBomb, out var dimensions);

        Assert.False(probed, "Decompression bomb declaring 100,000x100,000 pixels must be rejected by ImageHeaderProbe.");
    }

    [Fact]
    public void ImageHeaderProbe_Rejects_Dimensions_Exceeding_MaxDimension()
    {
        // Craft a PNG declaring 70,000 x 1,000 (exceeds MaxDimension of 65,535)
        var pngBomb = new byte[32];
        new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }.CopyTo(pngBomb, 0);
        BinaryPrimitives.WriteInt32BigEndian(pngBomb.AsSpan(8, 4), 13);
        "IHDR"u8.CopyTo(pngBomb.AsSpan(12, 4));
        BinaryPrimitives.WriteInt32BigEndian(pngBomb.AsSpan(16, 4), 70_000);
        BinaryPrimitives.WriteInt32BigEndian(pngBomb.AsSpan(20, 4), 1_000);

        var probed = ImageHeaderProbe.TryProbe(pngBomb, out var dimensions);

        Assert.False(probed, "Dimensions exceeding MaxDimension (65,535) must be rejected.");
    }

    [Fact]
    public void ImageHeaderProbe_Accepts_Legitimate_HighResolution_Image()
    {
        // Craft a PNG declaring 8K (7680 x 4320 = ~33 MP, well within the 512 MP cap)
        var png8K = new byte[32];
        new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }.CopyTo(png8K, 0);
        BinaryPrimitives.WriteInt32BigEndian(png8K.AsSpan(8, 4), 13);
        "IHDR"u8.CopyTo(png8K.AsSpan(12, 4));
        BinaryPrimitives.WriteInt32BigEndian(png8K.AsSpan(16, 4), 7680);
        BinaryPrimitives.WriteInt32BigEndian(png8K.AsSpan(20, 4), 4320);

        var probed = ImageHeaderProbe.TryProbe(png8K, out var dimensions);

        Assert.True(probed, "Valid 8K image must be accepted.");
        Assert.Equal(7680, dimensions.Width);
        Assert.Equal(4320, dimensions.Height);
    }

    [Fact]
    public void ImageLoadCoordinator_IsDecompressionBomb_Identifies_Hostile_Dimensions()
    {
        Assert.True(ImageLoadCoordinator.IsDecompressionBomb(new ImageDimensions(70_000, 100)));
        Assert.True(ImageLoadCoordinator.IsDecompressionBomb(new ImageDimensions(30_000, 20_000))); // 600 MP > 512 MP
        Assert.True(ImageLoadCoordinator.IsDecompressionBomb(new ImageDimensions(32_000, 16_001))); // just over 512 MP
        Assert.False(ImageLoadCoordinator.IsDecompressionBomb(new ImageDimensions(32_000, 16_000))); // exactly 512 MP
        Assert.False(ImageLoadCoordinator.IsDecompressionBomb(new ImageDimensions(20_000, 20_000))); // 400 MP (refused before 5.0)
        Assert.False(ImageLoadCoordinator.IsDecompressionBomb(new ImageDimensions(16_000, 9_000))); // 144 MP
        Assert.False(ImageLoadCoordinator.IsDecompressionBomb(new ImageDimensions(3840, 2160))); // 4K UHD
        Assert.Equal(512_000_000L, ImageHeaderProbe.MaxTotalPixels);
    }

    [Fact]
    public void NativeCompressedBuffer_Rejects_Negative_Or_Excessive_Lengths()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new NativeCompressedBuffer(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new NativeCompressedBuffer(-1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new NativeCompressedBuffer(NativeCompressedBuffer.MaxAllowedBufferBytes + 1));
    }

    #endregion

    #region 2026-11 security audit regressions

    private static byte[] PngHeader(int width, int height)
    {
        var png = new byte[33];
        new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }.CopyTo(png, 0);
        BinaryPrimitives.WriteInt32BigEndian(png.AsSpan(8, 4), 13);
        "IHDR"u8.CopyTo(png.AsSpan(12, 4));
        BinaryPrimitives.WriteInt32BigEndian(png.AsSpan(16, 4), width);
        BinaryPrimitives.WriteInt32BigEndian(png.AsSpan(20, 4), height);
        png[24] = 8; png[25] = 6; // 8-bit RGBA
        return png;
    }

    [Fact]
    public void Startup_preloader_never_full_decodes_or_hands_over_an_over_cap_image()
    {
        // 30000 x 30000 = 900 MP: refused by the header probe, but the preloader used to ignore that and
        // decode at full resolution (or hand the bytes to a consumer that decodes without a size check).
        var path = Path.Combine(_tempCacheDir, "bomb.png");
        File.WriteAllBytes(path, PngHeader(30_000, 30_000));

        using var result = Glide.Imaging.StartupImagePreloader.PreloadForTests(path, 0, 0);

        Assert.Null(result);
    }

    [Fact]
    public void Startup_preloader_still_preloads_a_normal_image()
    {
        var path = Path.Combine(_tempCacheDir, "pixel.png");
        File.WriteAllBytes(path, Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8DwHwAFBQIAX8jx0gAAAABJRU5ErkJggg=="));

        using var result = Glide.Imaging.StartupImagePreloader.PreloadForTests(path, 0, 0);

        Assert.NotNull(result);
        Assert.True(result!.NativeStatus == 1 || result.PreloadedBytes is { Length: > 0 });
    }

    /// <summary>1x1 global-palette GIF built from raw image descriptors (each frame's LZW data is one pixel).</summary>
    private static byte[] TinyGif(IEnumerable<(int Width, int Height)> frames)
    {
        var bytes = new List<byte>();
        bytes.AddRange("GIF89a"u8.ToArray());
        bytes.AddRange(new byte[] { 1, 0, 1, 0, 0x80, 0, 0 });      // 1x1 screen, 2-colour global palette
        bytes.AddRange(new byte[] { 0xFF, 0, 0, 0, 0xFF, 0 });      // red, green
        foreach (var (width, height) in frames)
        {
            bytes.Add(0x2C);
            bytes.AddRange(new byte[] { 0, 0, 0, 0, (byte)width, (byte)(width >> 8), (byte)height, (byte)(height >> 8), 0 });
            bytes.Add(2);                                            // LZW minimum code size
            bytes.AddRange(new byte[] { 2, 0x44, 0x01, 0 });         // clear, index 0, end (3-bit codes)
        }
        bytes.Add(0x3B);
        return bytes.ToArray();
    }

    [Fact]
    public void Gif_decoder_skips_a_hostile_oversized_frame_without_allocating_it()
    {
        // A ~60-byte file declaring a 65535 x 32767 frame used to allocate a 2 GB index buffer and run a
        // billion-iteration draw loop on the player thread.
        var gif = TinyGif(new[] { (1, 1), (65535, 32767), (1, 1), (1, 1) });

        var before = GC.GetAllocatedBytesForCurrentThread();
        var frames = 0;
        var size = GifAnimationDecoder.DecodeFrames(gif, (_, _, _, _) => { frames++; return true; });
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal((1, 1), size);
        Assert.Equal(3, frames);
        Assert.True(allocated < 64L * 1024 * 1024, $"decoder allocated {allocated:N0} bytes for a 1x1 animation");
    }

    [Fact]
    public void Gif_decoder_caps_the_number_of_frames()
    {
        var gif = TinyGif(Enumerable.Repeat((1, 1), GifAnimationDecoder.MaxFrames + 5));

        var frames = 0;
        var size = GifAnimationDecoder.DecodeFrames(gif, (_, _, _, _) => { frames++; return true; });

        Assert.Equal((1, 1), size);
        Assert.Equal(GifAnimationDecoder.MaxFrames, frames);
    }

    [Fact]
    public void External_open_pipe_is_current_user_only_on_both_ends()
    {
        Assert.True(Glide.App.Services.ExternalLaunchBroker.ClientPipeOptions.HasFlag(System.IO.Pipes.PipeOptions.CurrentUserOnly));
        Assert.True(Glide.App.Services.ExternalLaunchBroker.ServerPipeOptions.HasFlag(System.IO.Pipes.PipeOptions.CurrentUserOnly));
        Assert.True(Glide.App.Services.ExternalLaunchBroker.ClientPipeOptions.HasFlag(System.IO.Pipes.PipeOptions.Asynchronous));
    }

    [Fact]
    public void Clipboard_paste_ignores_relative_paths()
    {
        var absolute = Path.Combine(_tempCacheDir, "pasted.txt");
        File.WriteAllText(absolute, "x");
        var relativeName = "glide-relative-" + Guid.NewGuid().ToString("N") + ".txt";
        var relativeFull = Path.Combine(Environment.CurrentDirectory, relativeName);
        File.WriteAllText(relativeFull, "x");
        try
        {
            var usable = Glide.App.Explorer.WindowsExplorerFileClipboard.UsablePastePaths(
                new[] { relativeName, @".\" + relativeName, absolute, Path.Combine(_tempCacheDir, "missing.txt") });
            Assert.Equal(new[] { absolute }, usable);
        }
        finally { try { File.Delete(relativeFull); } catch { } }
    }

    [Fact]
    public async Task Permanent_delete_of_a_read_only_junction_never_touches_the_target()
    {
        if (!OperatingSystem.IsWindows()) return;
        var target = Path.Combine(_tempCacheDir, "target");
        Directory.CreateDirectory(target);
        var kept = Path.Combine(target, "kept.txt");
        File.WriteAllText(kept, "keep me");
        File.SetAttributes(kept, FileAttributes.ReadOnly);
        var junction = Path.Combine(_tempCacheDir, "link");
        using (var mklink = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe")
               {
                   ArgumentList = { "/c", "mklink", "/J", junction, target },
                   UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
               })!)
        {
            await mklink.WaitForExitAsync();
            if (mklink.ExitCode != 0 || !Directory.Exists(junction)) return; // junctions unavailable here
        }
        File.SetAttributes(junction, File.GetAttributes(junction) | FileAttributes.ReadOnly);
        try
        {
            var result = await Glide.App.Explorer.ExplorerFileOperations.DeleteAsync(new[] { junction }, permanent: true);

            // Whether or not Windows lets the read-only link itself go, the target is never touched:
            // its content stays and no read-only flag inside it is cleared by the delete's retry path.
            Assert.True(File.Exists(kept), "the junction's target content must survive");
            Assert.True((File.GetAttributes(kept) & FileAttributes.ReadOnly) != 0,
                "deleting a link must not clear read-only flags inside its target");
            Assert.True(result.Deleted.Count + result.Errors.Count == 1);
        }
        finally
        {
            try { File.SetAttributes(kept, FileAttributes.Normal); } catch { }
            try
            {
                if (Directory.Exists(junction))
                {
                    File.SetAttributes(junction, File.GetAttributes(junction) & ~FileAttributes.ReadOnly);
                    Directory.Delete(junction);
                }
            }
            catch { }
        }
    }

    [Fact]
    public void Installer_never_trusts_hkcu_for_the_install_directory_and_launches_unelevated()
    {
        var script = ReadRepositoryFile("installer", "Glide.iss");
        Assert.DoesNotContain("ExistingAppPath(HKCU", script, StringComparison.Ordinal);
        Assert.Matches(@"Description: ""Launch Glide""; Flags: [^\r\n]*\brunasoriginaluser\b", script);
    }

    [Fact]
    public void Startup_removes_the_current_directory_from_the_dll_search_order()
    {
        var program = ReadRepositoryFile("src", "Glide.App", "Program.cs");
        var main = program.IndexOf("public static int Main(", StringComparison.Ordinal);
        var call = program.IndexOf("SetDllDirectoryW(string.Empty)", StringComparison.Ordinal);
        var firstWork = program.IndexOf("CommandLineTools.TryRun(", StringComparison.Ordinal);
        Assert.True(main >= 0 && call > main && call < firstWork, "SetDllDirectoryW(\"\") must run first in Main");
    }

    private static string ReadRepositoryFile(params string[] segments)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(new[] { directory.FullName }.Concat(segments).ToArray());
            if (File.Exists(candidate)) return File.ReadAllText(candidate);
            directory = directory.Parent;
        }
        throw new FileNotFoundException("Could not locate repository file.", Path.Combine(segments));
    }

    #endregion
}

/// <summary>Settings/profile import must never decide which executables Glide launches.</summary>
[Collection(AvaloniaHeadlessCollection.Name)]
[Trait("Area", "Security")]
public sealed class SettingsImportSecurityTests
{
    public SettingsImportSecurityTests(AvaloniaHeadlessFixture fixture) { _ = fixture; }

    [Fact]
    public void Imported_files_keep_this_pcs_external_programs()
    {
        var current = new Glide.Core.Settings.GlideSettingsState { ExternalProgram1 = @"C:\Tools\Editor.exe" };
        var imported = new Glide.Core.Settings.GlideSettingsState
        {
            ExternalProgram1 = @"C:\Windows\System32\mshta.exe",
            ExternalProgram2 = @"\\attacker\share\run.exe",
            ExternalProgram3 = "",
        };

        Assert.True(Glide.App.Settings.SettingsWindow.KeepLocalLaunchTargets(imported, current));
        Assert.Equal(@"C:\Tools\Editor.exe", imported.ExternalProgram1);
        Assert.Equal("", imported.ExternalProgram2);
        Assert.Equal("", imported.ExternalProgram3);

        var same = new Glide.Core.Settings.GlideSettingsState { ExternalProgram1 = @"c:\tools\editor.exe" };
        Assert.False(Glide.App.Settings.SettingsWindow.KeepLocalLaunchTargets(same, current));
    }

    [Fact]
    public async Task Oversized_import_files_are_refused_before_reading()
    {
        var path = Path.Combine(Path.GetTempPath(), "glide-import-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            using (var stream = new FileStream(path, FileMode.CreateNew))
                stream.SetLength(Glide.App.Settings.SettingsWindow.MaxImportFileBytes + 1);
            await Assert.ThrowsAsync<InvalidDataException>(() => Glide.App.Settings.SettingsWindow.ReadImportFileAsync(path));

            await File.WriteAllTextAsync(path, "{\"Schema\":1}");
            Assert.Equal("{\"Schema\":1}", await Glide.App.Settings.SettingsWindow.ReadImportFileAsync(path));
        }
        finally { try { File.Delete(path); } catch { } }
    }
}
