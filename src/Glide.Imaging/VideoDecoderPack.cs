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

    /// <summary>One library of the pack with its pinned size and SHA-256.</summary>
    internal readonly record struct PinnedLibrary(string Name, long Bytes, string Sha256);

    /// <summary>
    /// Pinned SHA-256 of every DLL inside the pack (tools\package-ffmpeg-pack.ps1 prints them). The archive is
    /// checked against <see cref="PackSha256"/> at download; the unpacked DLLs live in a user-writable folder,
    /// so they are checked again before every activation (R4). Rebuilding the pack changes these values.
    /// </summary>
    internal static readonly PinnedLibrary[] PinnedLibraries =
    {
        new("avutil-61.dll", 3_025_408, "5D1CDAC282CDF0D04938595330C70671B87CB4EBB39BA0678A12F49CC8B4F058"),
        new("swresample-7.dll", 747_008, "ABF6658978A676BCA0868BF836F518CDCA27AC530E64493E360139C2CEE8DE72"),
        new("swscale-10.dll", 2_343_936, "AEF7F087E8CE69B1E7DB2255A5B46BF054B8368CE172D94C6B53326D3C82CD6E"),
        new("avcodec-63.dll", 91_810_816, "3FD8BE7AAE0594B118324169B91A868C3D75F57D7B7D60136A5AD5480EE3FA57"),
        new("avformat-63.dll", 23_003_136, "725FF43AD96F2A90413BE08CE88352A22795A355CEB5193515422A418839819D"),
    };

    /// <summary>
    /// Name of the "already verified" stamp that Glide 5.0.0 wrote beside the DLLs. It lived in the same
    /// user-writable folder, so anyone able to swap a DLL could also forge it: it is never read any more,
    /// and a leftover one is deleted.
    /// </summary>
    internal const string LegacyVerifiedStampFileName = ".glide-verified";

    private static readonly object VerifyGate = new();
    private static string? _verifiedDirectory;
    // Verified libraries stay open with FileShare.Read (no writer, no delete, no rename) for the life of the
    // process, so the files the native bridge maps are exactly the ones that were checked.
    private static List<FileStream>? _heldLibraries;

    /// <summary>Tests and diagnostics: files hashed by the last verification, and how long it took.</summary>
    internal static int LastVerificationHashedFiles { get; private set; }
    internal static TimeSpan LastVerificationDuration { get; private set; }

    // Program Files isn't writable without elevation, so downloads go to the per-user folder.
    private static string InstallDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Glide", "ffmpeg-" + PackVersion);

    /// <summary>
    /// <see cref="TryActivate"/> on a worker thread: the first activation in a process hashes the whole pack
    /// (~120 MB), which must not run on the UI thread. Later calls return at once.
    /// </summary>
    public static Task<bool> TryActivateAsync(CancellationToken cancellationToken = default)
        => Task.Run(TryActivate, cancellationToken);

    /// <summary>True when FFmpeg is usable now; also points the native bridge at a downloaded copy.</summary>
    public static bool TryActivate()
    {
        // A copy beside the app (developer builds and tests) is what the native bridge loads by default.
        if (HasLibraries(Path.Combine(AppContext.BaseDirectory, "ffmpeg"))) return true;
        if (!HasLibraries(InstallDirectory)) return false;
        // The downloaded copy is user-writable: load it only when every DLL still matches its pinned hash.
        // A modified or replaced DLL makes the pack count as not installed (Glide offers the download again).
        if (!TryVerifyLibraries(InstallDirectory, PinnedLibraries)) return false;
        try { return GlideVideoSetFfmpegDirectoryW(InstallDirectory + Path.DirectorySeparatorChar) >= 0; }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException) { return false; }
    }

    /// <summary>
    /// Checks every pinned library in <paramref name="directory"/> against its size and SHA-256. Nothing stored
    /// beside the files is trusted: every library is hashed again the first time a process needs the pack (the
    /// result is kept for that process only). Each file is opened without write/delete sharing before it is
    /// hashed and stays open for the life of the process, so what the native bridge loads is exactly what was
    /// checked.
    /// </summary>
    internal static bool TryVerifyLibraries(string directory, IReadOnlyList<PinnedLibrary> pins)
    {
        lock (VerifyGate)
        {
            directory = Path.GetFullPath(directory);
            if (_heldLibraries is not null && string.Equals(_verifiedDirectory, directory, StringComparison.OrdinalIgnoreCase))
                return true;

            RemoveLegacyStamp(directory);
            var clock = System.Diagnostics.Stopwatch.StartNew();
            var handles = new List<FileStream>(pins.Count);
            var verified = false;
            var hashed = 0;
            try
            {
                foreach (var pin in pins)
                {
                    var path = Path.Combine(directory, pin.Name);
                    var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, FileOptions.SequentialScan);
                    handles.Add(stream);
                    if (stream.Length != pin.Bytes) return false;
                    hashed++;
                    var hash = Convert.ToHexString(SHA256.HashData(stream));
                    if (!hash.Equals(pin.Sha256, StringComparison.OrdinalIgnoreCase)) return false;
                }
                ReleaseHeldLibrariesLocked();
                _heldLibraries = handles;
                _verifiedDirectory = directory;
                verified = true;
                return true;
            }
            catch { return false; }
            finally
            {
                LastVerificationHashedFiles = hashed;
                LastVerificationDuration = clock.Elapsed;
                if (!verified) foreach (var handle in handles) handle.Dispose();
            }
        }
    }

    /// <summary>Deletes the 5.0.0 "verified" stamp if one is left over; it is never read (best effort).</summary>
    private static void RemoveLegacyStamp(string directory)
    {
        try
        {
            var stamp = Path.Combine(directory, LegacyVerifiedStampFileName);
            if (File.Exists(stamp)) File.Delete(stamp);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private static void ReleaseHeldLibrariesLocked()
    {
        if (_heldLibraries is not null) foreach (var handle in _heldLibraries) handle.Dispose();
        _heldLibraries = null;
        _verifiedDirectory = null;
    }

    /// <summary>Tests: closes the verified-library handles so the next check runs again.</summary>
    internal static void ResetVerificationForTests()
    {
        lock (VerifyGate) ReleaseHeldLibrariesLocked();
    }

    /// <summary>
    /// The download URL: the pinned release asset, or the GLIDE_FFMPEG_PACK_URL test override when it is
    /// https (or plain http to this PC only). The archive hash stays pinned either way.
    /// </summary>
    internal static string ResolvePackUrl(string? custom)
    {
        if (string.IsNullOrWhiteSpace(custom) || !Uri.TryCreate(custom, UriKind.Absolute, out var uri)) return PackUrl;
        if (uri.Scheme == Uri.UriSchemeHttps) return uri.AbsoluteUri;
        if (uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback) return uri.AbsoluteUri;
        return PackUrl;
    }

    /// <summary>Downloads, verifies and installs the pack. Throws on failure or cancellation.</summary>
    public static async Task InstallAsync(IProgress<double>? progress, CancellationToken cancellationToken)
    {
        // The hash stays pinned, so the override used for testing can't change what gets installed.
        var url = ResolvePackUrl(Environment.GetEnvironmentVariable("GLIDE_FFMPEG_PACK_URL"));
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

            lock (VerifyGate) ReleaseHeldLibrariesLocked();
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
