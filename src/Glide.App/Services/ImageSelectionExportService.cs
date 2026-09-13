using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Glide.Imaging;

namespace Glide.App.Services;

/// <summary>Non-destructive export of the viewport's image-coordinate selection.</summary>
public static class ImageSelectionExportService
{
    public static async Task ExportAsync(Bitmap source, PixelRect selection, string destination, CancellationToken token = default)
    {
        if (selection.Width <= 0 || selection.Height <= 0) throw new ArgumentException("Selection is empty.", nameof(selection));
        if (selection.X < 0 || selection.Y < 0 || selection.Right > source.PixelSize.Width || selection.Bottom > source.PixelSize.Height)
            throw new ArgumentOutOfRangeException(nameof(selection), "Selection is outside the source image.");
        var ext = Path.GetExtension(destination).ToLowerInvariant();
        if (ext is not ".png" and not ".jpg" and not ".jpeg" and not ".bmp") throw new NotSupportedException("Choose PNG, JPEG or BMP.");
        await Task.Run(() =>
        {
            token.ThrowIfCancellationRequested();
            var cropped = new WriteableBitmap(new PixelSize(selection.Width, selection.Height), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);
            var temp = destination + ".glide-export-" + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var frame = cropped.Lock())
                {
                    for (var y = 0; y < selection.Height; y++)
                    {
                        token.ThrowIfCancellationRequested();
                        var row = new PixelRect(selection.X, selection.Y + y, selection.Width, 1);
                        source.CopyPixels(row, IntPtr.Add(frame.Address, checked(y * frame.RowBytes)), frame.RowBytes, frame.RowBytes);
                    }
                }
                bool encoded;
                using (var frame = cropped.Lock())
                {
                    encoded = NativeImageEncoder.TryEncode(temp, checked((uint)selection.Width), checked((uint)selection.Height),
                        checked((uint)frame.RowBytes), frame.Address, ext);
                }
                if (!encoded)
                {
                    if (ext != ".png") throw new PlatformNotSupportedException("PNG/JPEG/BMP encoding requires the Windows WIC bridge.");
                    // WIC may have created or partially written the temp path before
                    // reporting failure. Remove it so Avalonia can create a clean PNG.
                    try { if (File.Exists(temp)) File.Delete(temp); } catch (IOException) { }
                    using var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                    cropped.Save(output);
                    output.Flush(true);
                }
                token.ThrowIfCancellationRequested();
                if (File.Exists(destination)) File.Replace(temp, destination, destination + ".bak", true);
                else File.Move(temp, destination);
            }
            finally { cropped.Dispose(); try { if (File.Exists(temp)) File.Delete(temp); } catch { } }
        }, token).ConfigureAwait(false);
    }

}
