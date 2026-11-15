using Glide.Diagnostics;
using Glide.Imaging;

namespace Glide.Core.Tests;

[Collection(AvaloniaHeadlessCollection.Name)]
public sealed class FileHandleReleaseTests
{
    [Theory]
    [InlineData(".jpg")]
    [InlineData(".png")]
    [InlineData(".bmp")]
    [InlineData(".gif")]
    [InlineData(".tif")]
    [InlineData(".webp")]
    [InlineData(".ico")]
    public async Task ViewerDecodePipelineDoesNotKeepSourceFileLocked(string extension)
    {
        var payload = ImageDiagnosticFixtures.CreatePayloads()[extension];
        var dir = Path.Combine(Path.GetTempPath(), "glide-handle-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "image" + extension);
        File.WriteAllBytes(path, payload);
        try
        {
            using var loader = new ImageLoadCoordinator();
            // Some formats (notably TIFF) require the native WIC bridge, which a clean build only
            // copies into the published output; the decoder may be unavailable in the test host.
            // Whatever the decode outcome, the source must not remain locked afterwards.
            try { await loader.LoadForegroundAsync(path); }
            catch (Exception ex) when (ex is not OperationCanceledException) { }
            try { await loader.WarmAsync(new[] { path }); }
            catch (Exception ex) when (ex is not OperationCanceledException) { }

            // The image is still displayed (coordinator alive, caches populated). Deleting or
            // moving the source must still be permitted because every read-open is shared.
            var moved = Path.Combine(dir, "moved" + extension);
            File.Move(path, moved);
            File.Move(moved, path);
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    [Fact]
    public void MetadataReaderDoesNotKeepSourceFileLocked()
    {
        var payload = ImageDiagnosticFixtures.CreatePayloads()[".jpg"];
        var dir = Path.Combine(Path.GetTempPath(), "glide-handle-meta-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "image.jpg");
        File.WriteAllBytes(path, payload);
        try
        {
            _ = ImageMetadataReader.Read(path);
            var moved = Path.Combine(dir, "moved.jpg");
            File.Move(path, moved);
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    /// <summary>
    /// A viewed image must never become "in use in Glide". The viewer, metadata reader, print DPI
    /// reader and SVG loader all run asynchronously, so each must open the user's file with delete
    /// sharing; otherwise a background read that outlives the viewer (notably the Speed Boost warm
    /// process) blocks Explorer delete/move and reports the file as locked by Glide.
    /// </summary>
    // Source scan on purpose: it guards every file in src/ against a bare File.OpenRead / FileShare.Read, which no single runtime test can reach.
    [Fact]
    public void EveryUserImageReadOpenPermitsDeleteSharing()
    {
        var root = FindRepositoryRoot();
        var offenders = new List<string>();
        // Not user images: the downloaded FFmpeg DLLs are verified against pinned hashes and then held open
        // without write/delete sharing on purpose, so they cannot be swapped between the check and the load.
        var deliberate = new[] { Path.Combine("src", "Glide.Imaging", "VideoDecoderPack.cs") };
        foreach (var file in Directory.EnumerateFiles(Path.Combine(root, "src"), "*.cs", SearchOption.AllDirectories))
        {
            if (deliberate.Contains(Path.GetRelativePath(root, file), StringComparer.OrdinalIgnoreCase)) continue;
            var text = File.ReadAllText(file);
            // Bare FileShare.ReadWrite (no Delete) and File.OpenRead deny rename/delete by another process.
            if (text.Contains("FileShare.ReadWrite,", StringComparison.Ordinal) ||
                text.Contains("FileShare.Read,", StringComparison.Ordinal) ||
                text.Contains("File.OpenRead(", StringComparison.Ordinal))
            {
                offenders.Add(Path.GetRelativePath(root, file));
            }
        }
        Assert.Empty(offenders);
    }

    /// <summary>Svg.Skia must never be handed a path: a third-party loader may retain the handle.</summary>
    [Fact]
    public void SvgDecoderDoesNotKeepTheSourceFileLocked()
    {
        var dir = Path.Combine(Path.GetTempPath(), "glide-svg-handle-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "image.svg");
        File.WriteAllText(path, "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"16\" height=\"16\"><rect width=\"16\" height=\"16\" fill=\"red\"/></svg>");
        try
        {
            SvgDecoder.TryProbeDimensions(path, out _);
            if (SvgDecoder.TryDecode(path, 64, out var bitmap)) bitmap?.Dispose();
            File.Move(path, path + ".renamed"); // throws IOException if a handle without delete sharing remains
            File.Delete(path + ".renamed");
        }
        finally { try { Directory.Delete(dir, true); } catch { /* temp cleanup only */ } }
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, "src"))) return directory.FullName;
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException("Could not locate the repository root.");
    }
}
