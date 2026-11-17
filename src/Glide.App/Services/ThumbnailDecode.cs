using Avalonia.Media.Imaging;
using Glide.Imaging;

namespace Glide.App.Services;

/// <summary>
/// The small scaled decode shared by the Explorer tab's thumbnails and the tab hover previews: Avalonia's own decoder
/// reading the file sequentially (JPEG decodes directly at the reduced size). Callers decide which formats qualify and
/// run it off the UI thread.
/// </summary>
internal static class ThumbnailDecode
{
    // Flows into Task.Run, so a test's hook never sees decodes from tests running in parallel.
    private static readonly AsyncLocal<Action<string>?> s_decodingForTests = new();

    /// <summary>Test seam: called with the path just before Skia decodes the pixels.</summary>
    internal static Action<string>? DecodingForTests
    {
        get => s_decodingForTests.Value;
        set => s_decodingForTests.Value = value;
    }

    /// <summary>
    /// The picture scaled to <paramref name="width"/>, or null when the frame Skia would allocate is beyond the pixel cap
    /// or does not fit the free memory (the main loader's <see cref="SkiaDecodeGuard"/> check: PNG, BMP and GIF decode at
    /// full size before scaling, so a small file can declare a multi-gigabyte frame). Callers then show no picture.
    /// </summary>
    public static Bitmap? DecodeToWidth(string path, int width)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
            64 * 1024, FileOptions.SequentialScan);
        try { SkiaDecodeGuard.EnsureAllowed(stream, width); }
        catch (Exception ex) when (SkiaDecodeGuard.IsRefusal(ex)) { return null; }
        DecodingForTests?.Invoke(path);
        return Bitmap.DecodeToWidth(stream, width, BitmapInterpolationMode.LowQuality);
    }
}
