namespace Glide.App.Printing;

/// <summary>
/// Reads embedded image DPI for Actual-size printing. Supports JFIF density,
/// EXIF X/YResolution + ResolutionUnit, PNG pHYs, TIFF resolution tags and BMP
/// PelsPerMeter. Bounded and fail-safe: anything absent or nonsensical falls back
/// to the documented 96 DPI default (IrfanView uses 72; 96 matches Glide's decode
/// pipeline which hard-codes 96 dpi vectors everywhere).
/// </summary>
public static class PrintDpiReader
{
    public const double DefaultDpi = 96.0;
    public const double MinSaneDpi = 10.0;
    public const double MaxSaneDpi = 2400.0;

    public static double NormalizeDpi(double dpi) =>
        double.IsFinite(dpi) && dpi >= MinSaneDpi && dpi <= MaxSaneDpi ? dpi : DefaultDpi;

    public static (double Dx, double Dy, string Source) GetDpi(string path)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                return (DefaultDpi, DefaultDpi, "default");
            var ext = Path.GetExtension(path).ToLowerInvariant();
            return ext switch
            {
                ".jpg" or ".jpeg" or ".jpe" or ".jfif" => ReadJpegDpi(path),
                ".png" => ReadPngDpi(path),
                ".tif" or ".tiff" => ReadTiffDpi(path),
                ".bmp" or ".dib" => ReadBmpDpi(path),
                _ => (DefaultDpi, DefaultDpi, "default")
            };
        }
        catch { return (DefaultDpi, DefaultDpi, "default"); }
    }

    private static (double, double, string) Valid(double dx, double dy, string source) =>
        dx >= MinSaneDpi && dx <= MaxSaneDpi && dy >= MinSaneDpi && dy <= MaxSaneDpi
            ? (dx, dy, source) : (DefaultDpi, DefaultDpi, "default");

    private static (double, double, string) ReadJpegDpi(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 64 * 1024, FileOptions.SequentialScan);
        Span<byte> soI = stackalloc byte[2];
        if (fs.Read(soI) != 2 || soI[0] != 0xFF || soI[1] != 0xD8)
            return (DefaultDpi, DefaultDpi, "default");
        Span<byte> lenBuf = stackalloc byte[2];
        while (true)
        {
            int b;
            do { b = fs.ReadByte(); if (b < 0) return (DefaultDpi, DefaultDpi, "default"); } while (b != 0xFF);
            do { b = fs.ReadByte(); if (b < 0) return (DefaultDpi, DefaultDpi, "default"); } while (b == 0xFF);
            var marker = (byte)b;
            if (marker is 0xD9 or 0xDA) break;
            if (marker is >= 0xD0 and <= 0xD7 or 0x01) continue;
            if (fs.Read(lenBuf) != 2) break;
            var len = (lenBuf[0] << 8) | lenBuf[1];
            if (len < 2) break;
            var payload = len - 2;
            if (marker == 0xE0 && payload >= 14)
            {
                Span<byte> jfif = stackalloc byte[14];
                var got = fs.Read(jfif);
                payload -= got;
                if (got == 14 && jfif[0] == (byte)'J' && jfif[1] == (byte)'F' && jfif[2] == (byte)'I' && jfif[3] == (byte)'F')
                {
                    var units = jfif[7];
                    var xd = (jfif[8] << 8) | jfif[9];
                    var yd = (jfif[10] << 8) | jfif[11];
                    if (units == 1 && xd > 0 && yd > 0) return Valid(xd, yd, "jfif");
                    if (units == 2 && xd > 0 && yd > 0) return Valid(xd * 2.54, yd * 2.54, "jfif-dpcm");
                }
                Skip(fs, payload);
            }
            else if (marker == 0xE1 && payload > 0)
            {
                var buf = new byte[Math.Min(payload, 128 * 1024)];
                var read = 0;
                while (read < buf.Length) { var r = fs.Read(buf, read, buf.Length - read); if (r <= 0) break; read += r; }
                Skip(fs, payload - read);
                if (read > 14 && buf[0] == (byte)'E' && buf[1] == (byte)'x' && buf[2] == (byte)'i' && buf[3] == (byte)'f')
                {
                    var dpi = ReadExifResolution(buf, 6, read - 6);
                    if (dpi is not null) return Valid(dpi.Dx, dpi.Dy, "exif");
                }
            }
            else Skip(fs, payload);
            if (payload > 512 * 1024) break; // only header markers matter
        }
        return (DefaultDpi, DefaultDpi, "default");
    }

    private static (double, double, string) ReadPngDpi(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 4096, FileOptions.SequentialScan);
        Span<byte> sig = stackalloc byte[8];
        if (fs.Read(sig) != 8) return (DefaultDpi, DefaultDpi, "default");
        Span<byte> hdr = stackalloc byte[8];
        while (fs.Read(hdr) == 8)
        {
            var len = (hdr[0] << 24) | (hdr[1] << 16) | (hdr[2] << 8) | hdr[3];
            var type = System.Text.Encoding.ASCII.GetString(hdr.Slice(4, 4));
            if (len < 0 || len > 16 * 1024 * 1024) break;
            if (type == "pHYs" && len == 9)
            {
                Span<byte> phys = stackalloc byte[9];
                if (fs.Read(phys) == 9 && phys[8] == 1)
                {
                    var ppx = (uint)(phys[0] << 24 | phys[1] << 16 | phys[2] << 8 | phys[3]);
                    var ppy = (uint)(phys[4] << 24 | phys[5] << 16 | phys[6] << 8 | phys[7]);
                    if (ppx > 0 && ppy > 0) return Valid(ppx * 0.0254, ppy * 0.0254, "png-phys");
                }
                break;
            }
            fs.Seek(len + 4, SeekOrigin.Current);
            if (type == "IDAT") break;
        }
        return (DefaultDpi, DefaultDpi, "default");
    }

    private static (double, double, string) ReadTiffDpi(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 64 * 1024, FileOptions.SequentialScan);
        if (fs.Length > 32 * 1024 * 1024) return (DefaultDpi, DefaultDpi, "default");
        var buf = new byte[fs.Length];
        fs.ReadExactly(buf);
        var dpi = ReadExifResolution(buf, 0, buf.Length);
        return dpi is not null ? Valid(dpi.Dx, dpi.Dy, "tiff") : (DefaultDpi, DefaultDpi, "default");
    }

    private static (double, double, string) ReadBmpDpi(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 4096, FileOptions.SequentialScan);
        Span<byte> hdr = stackalloc byte[54];
        if (fs.Read(hdr) != 54) return (DefaultDpi, DefaultDpi, "default");
        if (hdr[0] != (byte)'B' || hdr[1] != (byte)'M') return (DefaultDpi, DefaultDpi, "default");
        var ppmX = BitConverter.ToInt32(hdr.Slice(38, 4));
        var ppmY = BitConverter.ToInt32(hdr.Slice(42, 4));
        if (ppmX > 0 && ppmY > 0) return Valid(ppmX * 0.0254, ppmY * 0.0254, "bmp");
        return (DefaultDpi, DefaultDpi, "default");
    }

    private record Resolution(double Dx, double Dy);

    private static Resolution? ReadExifResolution(byte[] b, int start, int length)
    {
        try
        {
            if (length < 8) return null;
            bool little = b[start] == (byte)'I' && b[start + 1] == (byte)'I';
            if (!little && !(b[start] == (byte)'M' && b[start + 1] == (byte)'M')) return null;
            ushort U(int o) => little ? BitConverter.ToUInt16(b, o) : (ushort)((b[o] << 8) | b[o + 1]);
            uint D(int o) => little ? BitConverter.ToUInt32(b, o) : (uint)((b[o] << 24) | (b[o] << 16) | (b[o + 2] << 8) | b[o + 3]);
            if (U(start + 2) != 42) return null;
            var ifd = start + checked((int)D(start + 4));
            if (ifd < start || ifd + 2 > start + length) return null;
            var count = Math.Min(U(ifd), (ushort)64);
            double? dx = null, dy = null;
            int unit = 2;
            for (var n = 0; n < count; n++)
            {
                var e = ifd + 2 + n * 12;
                if (e + 12 > start + length) break;
                var tag = U(e);
                if (tag is not (0x011A or 0x011B or 0x0128)) continue;
                var type = U(e + 2);
                var valueOff = e + 8;
                var dataOff = type == 5 ? start + checked((int)D(valueOff)) : valueOff;
                if (tag == 0x0128) { unit = U(type == 3 ? valueOff : dataOff); continue; }
                if (type != 5 || dataOff + 8 > start + length) continue;
                var num = D(dataOff); var den = D(dataOff + 4);
                if (den == 0) continue;
                var v = (double)num / den;
                if (tag == 0x011A) dx = v; else dy = v;
            }
            if (dx is null || dy is null) return null;
            if (unit == 3) { dx *= 2.54; dy *= 2.54; }
            else if (unit != 2) return null;
            return new Resolution(dx.Value, dy.Value);
        }
        catch { return null; }
    }

    private static void Skip(Stream s, long n)
    {
        if (n <= 0) return;
        if (s.CanSeek) s.Seek(n, SeekOrigin.Current);
        else { Span<byte> tmp = stackalloc byte[4096]; while (n > 0) { var r = s.Read(tmp[..(int)Math.Min(tmp.Length, n)]); if (r <= 0) break; n -= r; } }
    }
}
