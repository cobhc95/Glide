using System.Text;
using Glide.Imaging;
using Xunit;

namespace Glide.Core.Tests;

public sealed class ImageMetadataReaderTests
{
    [Fact]
    public void Read_ExtractsJpegExifFromMarkerStreamWithoutReadingEntireFile()
    {
        var tempFile = Path.Combine(Path.GetTempPath(), $"glide_test_{Guid.NewGuid():N}.jpg");
        try
        {
            using (var fs = File.Create(tempFile))
            {
                // Write SOI
                fs.Write(new byte[] { 0xFF, 0xD8 });

                // Write APP0 (JFIF) dummy segment - length 16 (including 2 length bytes)
                fs.Write(new byte[] { 0xFF, 0xE0, 0x00, 0x10 });
                fs.Write(Encoding.ASCII.GetBytes("JFIF\0\x01\x01\0\0\x01\0\x01\0\0"));

                // Write APP1 (EXIF) segment
                // Build a minimal valid TIFF header with Make="GlideCamera" and Model="GlidePro"
                using var exifPayload = new MemoryStream();
                exifPayload.Write(Encoding.ASCII.GetBytes("Exif\0\0")); // 6 bytes

                // TIFF Header: Little Endian ('II'), answer 42 (0x002A), IFD offset 8
                exifPayload.Write(new byte[] { (byte)'I', (byte)'I', 0x2A, 0x00, 0x08, 0x00, 0x00, 0x00 });

                // IFD0: 2 entries (Make 0x010F, Model 0x0110)
                exifPayload.Write(BitConverter.GetBytes((ushort)2));

                // Make entry: Tag 0x010F, Type 2 (ASCII), Count 12, Value offset
                var makeStr = "GlideCamera\0";
                var modelStr = "GlidePro419\0";
                var valueOffset = 8 + 2 + 2 * 12 + 4; // After IFD + next pointer

                exifPayload.Write(BitConverter.GetBytes((ushort)0x010F));
                exifPayload.Write(BitConverter.GetBytes((ushort)2));
                exifPayload.Write(BitConverter.GetBytes((uint)makeStr.Length));
                exifPayload.Write(BitConverter.GetBytes((uint)valueOffset));

                // Model entry: Tag 0x0110, Type 2 (ASCII), Count 12, Value offset
                exifPayload.Write(BitConverter.GetBytes((ushort)0x0110));
                exifPayload.Write(BitConverter.GetBytes((ushort)2));
                exifPayload.Write(BitConverter.GetBytes((uint)modelStr.Length));
                exifPayload.Write(BitConverter.GetBytes((uint)(valueOffset + makeStr.Length)));

                // Next IFD offset: 0
                exifPayload.Write(BitConverter.GetBytes((uint)0));

                // Write values
                exifPayload.Write(Encoding.ASCII.GetBytes(makeStr));
                exifPayload.Write(Encoding.ASCII.GetBytes(modelStr));

                var payloadBytes = exifPayload.ToArray();
                var app1Len = (ushort)(payloadBytes.Length + 2);
                fs.Write(new byte[] { 0xFF, 0xE1, (byte)(app1Len >> 8), (byte)(app1Len & 0xFF) });
                fs.Write(payloadBytes);

                // Write SOS marker (Start of Scan) - marker scanning should stop here
                fs.Write(new byte[] { 0xFF, 0xDA, 0x00, 0x0C, 0x03, 0x01, 0x00, 0x02, 0x11, 0x03, 0x11, 0x00, 0x3F, 0x00 });

                // Append 5 MB of dummy compressed image data (which should never be read or allocated)
                var dummyBuffer = new byte[1024 * 1024];
                for (int i = 0; i < 5; i++)
                {
                    fs.Write(dummyBuffer);
                }

                // Write EOI
                fs.Write(new byte[] { 0xFF, 0xD9 });
            }

            var metadata = ImageMetadataReader.Read(tempFile);
            Assert.NotNull(metadata);
            Assert.True(metadata.HasExif);
            Assert.Equal("GlideCamera", metadata.Make);
            Assert.Equal("GlidePro419", metadata.Model);
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }

    [Fact]
    public void Read_HandlesMalformedOrTruncatedJpegGracefully()
    {
        var tempFile = Path.Combine(Path.GetTempPath(), $"glide_test_corrupt_{Guid.NewGuid():N}.jpg");
        try
        {
            File.WriteAllBytes(tempFile, new byte[] { 0xFF, 0xD8, 0xFF, 0xE1, 0xFF, 0xFF });
            var metadata = ImageMetadataReader.Read(tempFile);
            Assert.NotNull(metadata);
            Assert.False(metadata.HasExif);
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }

    [Fact]
    public void Read_ThrowsOnCancellation()
    {
        var tempFile = Path.Combine(Path.GetTempPath(), $"glide_test_cancel_{Guid.NewGuid():N}.jpg");
        try
        {
            File.WriteAllBytes(tempFile, new byte[] { 0xFF, 0xD8, 0xFF, 0xE1, 0x00, 0x10, 0x00, 0x00 });
            using var cts = new CancellationTokenSource();
            cts.Cancel();
            Assert.Throws<OperationCanceledException>(() => ImageMetadataReader.Read(tempFile, cts.Token));
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }
}
