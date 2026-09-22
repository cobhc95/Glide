using Glide.App.Printing;

namespace Glide.Core.Tests;

/// <summary>
/// Embedded-DPI contract for Actual-size printing: real metadata wins, anything absent
/// or nonsensical falls back to the documented 96 DPI default (matching Glide's decode
/// pipeline, which hard-codes 96 dpi vectors).
/// </summary>
public sealed class PrintDpiReaderTests
{
    [Fact]
    public void NormalizeDpi_accepts_sane_values_and_rejects_the_rest()
    {
        Assert.Equal(300, PrintDpiReader.NormalizeDpi(300));
        Assert.Equal(96, PrintDpiReader.NormalizeDpi(0));
        Assert.Equal(96, PrintDpiReader.NormalizeDpi(-72));
        Assert.Equal(96, PrintDpiReader.NormalizeDpi(double.NaN));
        Assert.Equal(96, PrintDpiReader.NormalizeDpi(double.PositiveInfinity));
        Assert.Equal(96, PrintDpiReader.NormalizeDpi(9.9));
        Assert.Equal(96, PrintDpiReader.NormalizeDpi(2400.1));
        Assert.Equal(10, PrintDpiReader.NormalizeDpi(10));
        Assert.Equal(2400, PrintDpiReader.NormalizeDpi(2400));
    }

    [Fact]
    public void Missing_file_falls_back_to_default()
    {
        var (dx, dy, source) = PrintDpiReader.GetDpi(Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".jpg"));
        Assert.Equal(96, dx);
        Assert.Equal(96, dy);
        Assert.Equal("default", source);
    }

    [Fact]
    public void Minimal_jfif_with_density_reports_jfif_dpi()
    {
        var path = WriteTemp(".jpg", BuildJpegWithJfif(200, 200));
        try
        {
            var (dx, dy, source) = PrintDpiReader.GetDpi(path);
            Assert.Equal(200, dx);
            Assert.Equal(200, dy);
            Assert.Equal("jfif", source);
        }
        finally { TryDelete(path); }
    }

    [Fact]
    public void Png_phys_chunk_converts_pixels_per_metre_to_dpi()
    {
        // 3780 ppm ≈ 96 dpi (3780 * 0.0254 = 96.012).
        var path = WriteTemp(".png", BuildPngWithPhys(3780, 3780));
        try
        {
            var (dx, dy, source) = PrintDpiReader.GetDpi(path);
            Assert.Equal(96.01, dx, 0.02);
            Assert.Equal(96.01, dy, 0.02);
            Assert.Equal("png-phys", source);
        }
        finally { TryDelete(path); }
    }

    [Fact]
    public void Plain_png_without_phys_falls_back_to_default()
    {
        var path = WriteTemp(".png", BuildPngWithPhys(0, 0, includePhys: false));
        try
        {
            var (dx, dy, source) = PrintDpiReader.GetDpi(path);
            Assert.Equal(96, dx);
            Assert.Equal("default", source);
        }
        finally { TryDelete(path); }
    }

    [Fact]
    public void Absurd_embedded_dpi_is_rejected()
    {
        var path = WriteTemp(".jpg", BuildJpegWithJfif(9999, 9999));
        try
        {
            var (dx, dy, source) = PrintDpiReader.GetDpi(path);
            Assert.Equal(96, dx);
            Assert.Equal(96, dy);
            Assert.Equal("default", source);
        }
        finally { TryDelete(path); }
    }

    // --- minimal file builders ---

    private static byte[] BuildJpegWithJfif(int xd, int yd)
    {
        using var ms = new MemoryStream();
        ms.WriteByte(0xFF); ms.WriteByte(0xD8);
        ms.WriteByte(0xFF); ms.WriteByte(0xE0);
        ms.WriteByte(0x00); ms.WriteByte(0x10); // length 16
        ms.Write("JFIF\0"u8);
        ms.WriteByte(1); ms.WriteByte(1); // version
        ms.WriteByte(1); // units = dpi
        ms.WriteByte((byte)(xd >> 8)); ms.WriteByte((byte)(xd & 0xFF));
        ms.WriteByte((byte)(yd >> 8)); ms.WriteByte((byte)(yd & 0xFF));
        ms.WriteByte(0); ms.WriteByte(0); // thumbnail
        ms.WriteByte(0xFF); ms.WriteByte(0xD9);
        return ms.ToArray();
    }

    private static byte[] BuildPngWithPhys(uint ppx, uint ppy, bool includePhys = true)
    {
        using var ms = new MemoryStream();
        ms.Write(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });
        WriteChunk(ms, "IHDR", new byte[] { 0, 0, 0, 8, 0, 0, 0, 8, 8, 2, 0, 0, 0 });
        if (includePhys)
        {
            ms.WriteByte(0); ms.WriteByte(0); ms.WriteByte(0); ms.WriteByte(9);
            ms.Write("pHYs"u8);
            ms.WriteByte((byte)(ppx >> 24)); ms.WriteByte((byte)(ppx >> 16)); ms.WriteByte((byte)(ppx >> 8)); ms.WriteByte((byte)ppx);
            ms.WriteByte((byte)(ppy >> 24)); ms.WriteByte((byte)(ppy >> 16)); ms.WriteByte((byte)(ppy >> 8)); ms.WriteByte((byte)ppy);
            ms.WriteByte(1);
            ms.Write(new byte[] { 0, 0, 0, 0 }); // crc placeholder (reader ignores crc)
        }
        WriteChunk(ms, "IEND", Array.Empty<byte>());
        return ms.ToArray();
    }

    private static void WriteChunk(MemoryStream ms, string type, byte[] data)
    {
        var len = data.Length;
        ms.WriteByte((byte)(len >> 24)); ms.WriteByte((byte)(len >> 16)); ms.WriteByte((byte)(len >> 8)); ms.WriteByte((byte)len);
        ms.Write(System.Text.Encoding.ASCII.GetBytes(type));
        ms.Write(data, 0, data.Length);
        ms.Write(new byte[] { 0, 0, 0, 0 });
    }

    private static string WriteTemp(string ext, byte[] bytes)
    {
        var path = Path.Combine(Path.GetTempPath(), "glide-print-dpi-" + Guid.NewGuid().ToString("N") + ext);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch { }
    }
}
