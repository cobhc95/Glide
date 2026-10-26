using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media.Imaging;
using Glide.Core;
using Glide.Imaging;

namespace Glide.App.Services;

/// <summary>
/// Save / Save As: bakes the viewer's rotation/flip into the full-resolution pixels and writes
/// through a temp file in the destination folder, then atomically replaces the target so a
/// failed encode can never corrupt the original.
/// </summary>
public static class ImageSaveService
{
    public static async Task SaveAsync(Bitmap source, ImageEditTransform transform, string destination,
        float jpegQuality = 0.92f, CancellationToken token = default)
    {
        var ext = Path.GetExtension(destination);
        if (!NativeImageEncoder.CanEncode(ext))
            throw new NotSupportedException($"Glide cannot write '{ext}' files. Choose JPEG, PNG, BMP or TIFF.");
        var width = source.PixelSize.Width;
        var height = source.PixelSize.Height;
        if (width <= 0 || height <= 0) throw new InvalidOperationException("No image pixels to save.");

        // Copy pixels on the caller's thread; the bitmap may be released by the viewer afterwards.
        var stride = checked(width * 4);
        var raw = new byte[checked(stride * height)];
        var handle = GCHandle.Alloc(raw, GCHandleType.Pinned);
        try { source.CopyPixels(new PixelRect(0, 0, width, height), handle.AddrOfPinnedObject(), raw.Length, stride); }
        finally { handle.Free(); }

        await Task.Run(() => EncodeAndReplace(raw, width, height, transform, destination, jpegQuality, token), token).ConfigureAwait(false);
    }

    /// <summary>Pure-buffer path (premultiplied BGRA, tightly packed); used by Save and by tests.</summary>
    public static void EncodeAndReplace(byte[] bgra, int width, int height, ImageEditTransform transform,
        string destination, float jpegQuality, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        var ext = Path.GetExtension(destination);
        var pixels = transform.Apply(bgra, width, height, out var w, out var h);
        var temp = ImageSavePolicy.TempPathFor(destination);
        try
        {
            var handle = GCHandle.Alloc(pixels, GCHandleType.Pinned);
            bool ok;
            try
            {
                ok = NativeImageEncoder.TryEncode(temp, checked((uint)w), checked((uint)h), checked((uint)(w * 4)),
                    handle.AddrOfPinnedObject(), ext, Math.Clamp(jpegQuality, 0.05f, 1f));
            }
            finally { handle.Free(); }
            if (!ok || !File.Exists(temp) || new FileInfo(temp).Length == 0)
                throw new IOException($"Encoding {ext} failed (Windows WIC bridge unavailable or rejected the image).");
            token.ThrowIfCancellationRequested();
            if (File.Exists(destination)) File.Replace(temp, destination, null, ignoreMetadataErrors: true);
            else File.Move(temp, destination);
        }
        finally
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch { }
        }
    }
}
