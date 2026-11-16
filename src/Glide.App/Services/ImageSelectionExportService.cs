using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media.Imaging;
using Glide.Core;
using Glide.Imaging.Encoders;

namespace Glide.App.Services;

/// <summary>Non-destructive export of the viewport's image-coordinate selection, in any writable format.</summary>
public static class ImageSelectionExportService
{
    /// <param name="sourcePath">The original file: only its ICC colour profile is carried over (a crop is a new picture).</param>
    public static async Task ExportAsync(Bitmap source, PixelRect selection, string destination, CancellationToken token = default,
        SaveFormat? format = null, string? sourcePath = null)
    {
        if (selection.Width <= 0 || selection.Height <= 0) throw new ArgumentException("Selection is empty.", nameof(selection));
        if (selection.X < 0 || selection.Y < 0 || selection.Right > source.PixelSize.Width || selection.Bottom > source.PixelSize.Height)
            throw new ArgumentOutOfRangeException(nameof(selection), "Selection is outside the source image.");
        format ??= ImageSaveService.ResolveFormat(destination);
        await Task.Run(() =>
        {
            token.ThrowIfCancellationRequested();
            var rowBytes = checked(selection.Width * 4);
            var pixels = new byte[checked(rowBytes * selection.Height)];
            var handle = GCHandle.Alloc(pixels, GCHandleType.Pinned);
            try
            {
                for (var y = 0; y < selection.Height; y++)
                {
                    token.ThrowIfCancellationRequested();
                    var row = new PixelRect(selection.X, selection.Y + y, selection.Width, 1);
                    source.CopyPixels(row, IntPtr.Add(handle.AddrOfPinnedObject(), checked(y * rowBytes)), rowBytes, rowBytes);
                }
            }
            finally { handle.Free(); }
            var options = new ImageEncodeOptions
            {
                MetadataSource = sourcePath is not null && File.Exists(sourcePath) ? sourcePath : null,
                Metadata = SaveMetadataScope.IccOnly,
                Name = Path.GetFileNameWithoutExtension(destination),
            };
            var temp = destination + ".glide-export-" + Guid.NewGuid().ToString("N") + ".tmp";
            // A second export over the same name keeps the previous file as <name>.bak.
            ImageSaveService.WriteAtomically(destination, token,
                path => ImageFileEncoder.Encode(format, pixels, selection.Width, selection.Height, path, options),
                backup: destination + ".bak", temp: temp);
        }, token).ConfigureAwait(false);
    }
}
