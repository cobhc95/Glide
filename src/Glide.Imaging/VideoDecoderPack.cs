using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace Glide.Imaging;

/// <summary>
/// The optional FFmpeg decoder, which is not shipped with Glide. It is offered only when Windows can't
/// play a video the user opened, and the archive must match a pinned SHA-256 before anything is unpacked.
/// </summary>
public static partial class VideoDecoderPack
{
    public const long PackBytes = 51_097_900;
    private const string PackVersion = "9.0.2";
    private const string PackSha256 = "7CF1FE8F67853D410FE24F62468291C7B6ADD3FF5320BB312D782DFFB904F08D";
    private const string PackUrl = "https://github.com/cobhc95/Glide/releases/download/ffmpeg-9.0.2/glide-ffmpeg-9.0.2-win64.zip";
    private static readonly string[] Libraries = { "avutil-61.dll", "swresample-7.dll", "swscale-10.dll", "avcodec-63.dll", "avformat-63.dll" };
    private static readonly string[] PackFiles = { "avutil-61.dll", "swresample-7.dll", "swscale-10.dll", "avcodec-63.dll", "avformat-63.dll", "LICENSE.txt", "NOTICE.txt" };

    // Program Files isn't writable without elevation, so downloads go to the per-user folder.
    private static string InstallDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Glide", "ffmpeg-" + PackVersion);

    /// <summary>True when FFmpeg is usable now; also points the native bridge at a downloaded copy.</summary>
    public static bool TryActivate()
    {
        // A copy beside the app (developer builds and tests) is what the native bridge loads by default.
        if (HasLibraries(Path.Combine(AppContext.BaseDirectory, "ffmpeg"))) return true;
        if (!HasLibraries(InstallDirectory)) return false;
        try { return GlideVideoSetFfmpegDirectoryW(InstallDirectory + Path.DirectorySeparatorChar) >= 0; }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException) { return false; }
    }

    /// <summary>Downloads, verifies and installs the pack. Throws on failure or cancellation.</summary>
    public static async Task InstallAsync(IProgress<double>? progress, CancellationToken cancellationToken)
    {
        // The hash stays pinned, so the override used for testing can't change what gets installed.
        var url = Environment.GetEnvironmentVariable("GLIDE_FFMPEG_PACK_URL") is { Length: > 0 } custom ? custom : PackUrl;
        var parent = Path.GetDirectoryName(InstallDirectory)!;
        Directory.CreateDirectory(parent);
        var work = Path.Combine(parent, "ffmpeg-download-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        try
        {
            var archive = Path.Combine(work, "pack.zip");
            using (var client = new HttpClient { Timeout = Timeout.InfiniteTimeSpan })
            using (var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false))
            {
                response.EnsureSuccessStatusCode();
                await using var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                await using var target = File.Create(archive);
                var buffer = new byte[1 << 16];
                long total = 0;
                int read;
                while ((read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
                {
                    total += read;
                    if (total > PackBytes) throw new InvalidDataException("The download is larger than expected.");
                    await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                    progress?.Report((double)total / PackBytes);
                }
            }

            await using (var stream = new FileStream(archive, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete))
            {
                var hash = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false));
                if (!hash.Equals(PackSha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("The download failed its integrity check.");
            }

            var unpacked = Path.Combine(work, "unpacked");
            Directory.CreateDirectory(unpacked);
            using (var zip = new ZipArchive(new FileStream(archive, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete), ZipArchiveMode.Read))
            {
                foreach (var name in PackFiles)
                {
                    var entry = zip.GetEntry(name) ?? throw new InvalidDataException("The download is incomplete.");
                    entry.ExtractToFile(Path.Combine(unpacked, name));
                }
            }

            if (Directory.Exists(InstallDirectory)) Directory.Delete(InstallDirectory, recursive: true);
            Directory.Move(unpacked, InstallDirectory);
        }
        finally
        {
            try { Directory.Delete(work, recursive: true); } catch { }
        }
    }

    private static bool HasLibraries(string directory) => Libraries.All(x => File.Exists(Path.Combine(directory, x)));

    [LibraryImport("Glide.Native", EntryPoint = "GlideVideoSetFfmpegDirectoryW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int GlideVideoSetFfmpegDirectoryW(string directory);
}
