using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media.Imaging;
using Glide.Core;
using Glide.Imaging;
using Glide.Imaging.Encoders;

namespace Glide.App.Services;

/// <summary>
/// Save / Save As: bakes the viewer's rotation/flip into the full-resolution pixels and writes
/// through a temp file in the destination folder, then atomically replaces the target so a
/// failed encode can never corrupt the original. The format follows the destination's extension
/// (or <c>format</c> when the Save as dialog resolved one); see <see cref="SaveFormatCatalog"/>.
/// </summary>
public static class ImageSaveService
{
    public static async Task SaveAsync(Bitmap source, ImageEditTransform transform, string destination,
        float jpegQuality = ImageEncodeOptions.DefaultQuality, CancellationToken token = default, string? sourcePath = null, SaveFormat? format = null)
    {
        format ??= ResolveFormat(destination);
        var width = source.PixelSize.Width;
        var height = source.PixelSize.Height;
        if (width <= 0 || height <= 0) throw new InvalidOperationException("No image pixels to save.");

        // Copy pixels on the caller's thread; the bitmap may be released by the viewer afterwards.
        var stride = checked(width * 4);
        var raw = new byte[checked(stride * height)];
        var handle = GCHandle.Alloc(raw, GCHandleType.Pinned);
        try { source.CopyPixels(new PixelRect(0, 0, width, height), handle.AddrOfPinnedObject(), raw.Length, stride); }
        finally { handle.Free(); }

        await Task.Run(() => EncodeAndReplace(raw, width, height, transform, destination, jpegQuality, token, sourcePath, format), token).ConfigureAwait(false);
    }

    /// <summary>The writable format for <paramref name="destination"/>'s extension, or a clear refusal.</summary>
    public static SaveFormat ResolveFormat(string destination)
    {
        var ext = Path.GetExtension(destination);
        var format = SaveFormatCatalog.FromExtension(ext);
        if (format is null || !SaveFormatAvailability.IsWritable(format))
            throw new NotSupportedException(format is null
                ? $"Glide cannot write '{ext}' files. Choose JPEG, PNG or another type from the Save as list."
                : $"This PC cannot write {format.Name} files" + (format.NeedsWindowsExtension ? " (the Windows codec extension with an encoder is not installed)." : "."));
        return format;
    }

    /// <summary>
    /// Pure-buffer path (premultiplied BGRA, tightly packed); used by Save and by tests. When
    /// <paramref name="sourcePath"/> is the destination itself and it holds more than one page/frame, nothing is
    /// written (the encoder writes one frame, so the others would be lost): last line of defence behind the UI checks.
    /// Metadata (ICC, EXIF, XMP) is copied from <paramref name="sourcePath"/> where the format can hold it.
    /// </summary>
    public static void EncodeAndReplace(byte[] bgra, int width, int height, ImageEditTransform transform,
        string destination, float jpegQuality, CancellationToken token = default, string? sourcePath = null, SaveFormat? format = null)
    {
        token.ThrowIfCancellationRequested();
        if (sourcePath is not null && ImageSavePolicy.IsSameFile(sourcePath, destination) && File.Exists(destination))
        {
            var frames = ImageFrameProbe.CountFrames(destination);
            if (ImageSavePolicy.BlocksInPlaceSave(frames))
                throw new MultiFrameSaveRefusedException(ImageSavePolicy.MultiFrameInPlaceMessage(destination, frames), frames);
        }
        format ??= ResolveFormat(destination);
        var pixels = transform.Apply(bgra, width, height, out var w, out var h);
        var options = new ImageEncodeOptions
        {
            Quality = jpegQuality,
            MetadataSource = sourcePath is not null && File.Exists(sourcePath) ? sourcePath : null,
            Metadata = SaveMetadataScope.All,
            // A lossless WebP stays lossless (Save over it, or a copy of it).
            Lossless = format.Id == SaveFormatCatalog.WebP.Id && WebPContainer.IsLossless(sourcePath),
            Name = Path.GetFileNameWithoutExtension(destination),
        };
        WriteAtomically(destination, token, temp => ImageFileEncoder.Encode(format, pixels, w, h, temp, options));
    }

    /// <summary>Encodes into a temp file next to <paramref name="destination"/>, then replaces it (or moves into place).</summary>
    internal static void WriteAtomically(string destination, CancellationToken token, Action<string> encode, string? backup = null, string? temp = null)
    {
        temp ??= ImageSavePolicy.TempPathFor(destination);
        try
        {
            encode(temp);
            if (!File.Exists(temp) || new FileInfo(temp).Length == 0)
                throw new IOException($"Encoding {Path.GetExtension(destination)} produced no data.");
            token.ThrowIfCancellationRequested();
            if (File.Exists(destination)) File.Replace(temp, destination, backup, ignoreMetadataErrors: true);
            else File.Move(temp, destination);
            Glide.App.Platform.ShellItemNotifier.FileContentChanged(destination);
        }
        finally
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch { /* best-effort temp cleanup; the save result is already decided */ }
        }
    }
}

/// <summary>Save refused because the destination is the multi-frame original (see <see cref="ImageSavePolicy.BlocksInPlaceSave"/>).</summary>
public sealed class MultiFrameSaveRefusedException(string message, int frameCount) : InvalidOperationException(message)
{
    public int FrameCount { get; } = frameCount;
}
