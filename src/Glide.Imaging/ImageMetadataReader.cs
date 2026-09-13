using System.Globalization;
using System.IO.Compression;
using System.Text;
using Glide.Core;

namespace Glide.Imaging;

public sealed record ImageMetadata(
    string? Make = null, string? Model = null, DateTimeOffset? Captured = null,
    double? ExposureSeconds = null, double? Aperture = null, int? Iso = null,
    double? FocalLengthMm = null, int? Orientation = null, int? FrameCount = null,
    int Width = 0, int Height = 0)
{
    public bool HasExif => Make is not null || Model is not null || Captured is not null || ExposureSeconds is not null || Aperture is not null || Iso is not null || FocalLengthMm is not null || Orientation is not null;
    public string ToDisplayText() => string.Join("\n", new[] {
        Make is null ? null : $"Camera: {Make}{(Model is null ? "" : " " + Model)}",
        Captured is null ? null : $"Captured: {Captured:yyyy-MM-dd HH:mm:ss}",
        ExposureSeconds is null ? null : $"Exposure: {ExposureSeconds:g4}s",
        Aperture is null ? null : $"Aperture: f/{Aperture:g4}", Iso is null ? null : $"ISO: {Iso}",
        FocalLengthMm is null ? null : $"Focal length: {FocalLengthMm:g4} mm", Orientation is null ? null : $"Orientation: {Orientation}",
        FrameCount is null ? null : $"Frames/pages: {FrameCount}"
    }.Where(x => x is not null).Cast<string>());
}

/// <summary>Lazy, bounded EXIF reader. Metadata errors are isolated and never block first paint.</summary>
public static class ImageMetadataReader
{
    public static Task<ImageMetadata> ReadAsync(string path, CancellationToken token = default) => Task.Run(() => Read(path, token), token);
    public static ImageMetadata Read(string path, CancellationToken token = default)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 64 * 1024, FileOptions.SequentialScan);
            if (stream.Length > 64 * 1024 * 1024) return new ImageMetadata();
            var data = new byte[checked((int)stream.Length)]; stream.ReadExactly(data);
            token.ThrowIfCancellationRequested();
            var ext = ImageFormatRegistry.GetLongestExtension(path).ToLowerInvariant();
            if (ext is ".jpg" or ".jpeg" or ".jpe") return ParseJpeg(data);
            if (ext == ".png") return ParsePng(data);
            if (ext is ".tif" or ".tiff") return ParseTiff(data, 0, data.Length);
            if (ext == ".gif") return new ImageMetadata(FrameCount: CountGifFrames(data));
            if (ext == ".webp") return ParseWebp(data);
            return new ImageMetadata();
        }
        catch (OperationCanceledException) { throw; }
        catch { return new ImageMetadata(); }
    }

    private static ImageMetadata ParseJpeg(byte[] data)
    {
        if (data.Length < 4 || data[0] != 0xff || data[1] != 0xd8) return new ImageMetadata();
        for (var i = 2; i + 4 < data.Length;)
        {
            if (data[i] != 0xff) { i++; continue; }
            while (i < data.Length && data[i] == 0xff) i++;
            if (i >= data.Length) break;
            var marker = data[i++]; if (marker is 0xd8 or 0xd9) continue;
            if (i + 2 > data.Length) break; var len = (data[i] << 8) | data[i + 1];
            if (len < 2 || i + len > data.Length) break;
            if (marker == 0xe1 && len >= 8 && Encoding.ASCII.GetString(data, i + 2, 6) == "Exif\0\0") return ParseTiff(data[(i + 8)..(i + len)], 0, len - 8);
            i += len;
        }
        return new ImageMetadata();
    }

    private static ImageMetadata ParsePng(byte[] data)
    {
        if (data.Length < 24 || data[0] != 137 || data[1] != 80 || data[2] != 78 || data[3] != 71) return new ImageMetadata();
        var width = ToInt(ReadBe(data, 16)); var height = ToInt(ReadBe(data, 20)); string? make = null, model = null, date = null; var frames = 1;
        for (var i = 8; i + 12 <= data.Length;)
        {
            var len = checked((int)ReadBe(data, i)); if (len < 0 || i + 12 + len > data.Length) break; var type = Encoding.ASCII.GetString(data, i + 4, 4);
            if (type == "acTL" && len >= 4) frames = Math.Max(1, ToInt(ReadBe(data, i + 8)));
            if (type == "eXIf" && len >= 8) { var exif = ParseTiff(data[(i + 8)..(i + 8 + len)], 0, len); return exif with { Width = width, Height = height, FrameCount = frames }; }
            if (type is "tEXt" or "zTXt" or "iTXt")
            {
                var raw = ParsePngText(data, i + 8, len, type);
                if (raw is not null) { var split = raw.Value.Key.ToLowerInvariant(); var value = raw.Value.Value; if (split.Contains("date") || split.Contains("time")) date ??= value; else if (split.Contains("author") || split.Contains("make")) make ??= value; else if (split.Contains("model")) model ??= value; }
            }
            i += 12 + len;
        }
        return new ImageMetadata(make, model, ParseDate(date), FrameCount: frames, Width: width, Height: height);
    }
    private static (string Key, string Value)? ParsePngText(byte[] data, int offset, int length, string type)
    {
        if (length <= 0 || offset < 0 || offset > data.Length - length) return null;
        var payload = data.AsSpan(offset, length);
        var nul = payload.IndexOf((byte)0); if (nul <= 0) return null;
        var key = Encoding.Latin1.GetString(payload[..nul]);
        if (type == "tEXt") return (key, Encoding.Latin1.GetString(payload[(nul + 1)..]));
        if (type == "zTXt")
        {
            if (nul + 2 > payload.Length || payload[nul + 1] != 0) return null;
            try { using var input = new MemoryStream(payload[(nul + 2)..].ToArray()); using var zlib = new ZLibStream(input, CompressionMode.Decompress); using var output = new MemoryStream(); zlib.CopyTo(output); if (output.Length > 1024 * 1024) return null; return (key, Encoding.Latin1.GetString(output.ToArray())); }
            catch { return null; }
        }
        // iTXt: keyword, compression flag, compression method, language tag, translated keyword, text.
        var p = nul + 1; if (p + 2 > payload.Length) return null; var compressed = payload[p++] != 0; if (payload[p++] != 0) return null;
        var langEnd = payload[p..].IndexOf((byte)0); if (langEnd < 0) return null; p += langEnd + 1;
        var translatedEnd = payload[p..].IndexOf((byte)0); if (translatedEnd < 0) return null; p += translatedEnd + 1;
        try
        {
            var text = payload[p..].ToArray();
            if (compressed) using (var input = new MemoryStream(text)) using (var zlib = new ZLibStream(input, CompressionMode.Decompress)) using (var output = new MemoryStream()) { zlib.CopyTo(output); if (output.Length > 1024 * 1024) return null; text = output.ToArray(); }
            return (key, Encoding.UTF8.GetString(text));
        }
        catch { return null; }
    }
    private static uint ReadBe(byte[] data, int offset) => (uint)(data[offset] << 24 | data[offset + 1] << 16 | data[offset + 2] << 8 | data[offset + 3]);
    private static int ToInt(uint value) => value > int.MaxValue ? int.MaxValue : (int)value;

    private static ImageMetadata ParseTiff(byte[] b, int start, int length)
    {
        if (length < 8) return new ImageMetadata(); bool little = b[start] == 'I' && b[start + 1] == 'I';
        ushort U(int o) => little ? BitConverter.ToUInt16(b, o) : (ushort)((b[o] << 8) | b[o + 1]);
        uint D(int o) => little ? BitConverter.ToUInt32(b, o) : (uint)(b[o] << 24 | b[o + 1] << 16 | b[o + 2] << 8 | b[o + 3]);
        if (U(start + 2) != 42) return new ImageMetadata(); var ifd = checked(start + (int)D(start + 4)); if (ifd < start || ifd + 2 > b.Length) return new ImageMetadata();
        var count = Math.Min(U(ifd), (ushort)256); string? make = null, model = null, date = null, originalDate = null, digitizedDate = null; double? exposure = null, aperture = null, focal = null; int? iso = null, orient = null; int width = 0, height = 0; uint exifIfd = 0; var pages = 1;
        for (var n = 0; n < count; n++) { var e = ifd + 2 + n * 12; if (e + 12 > b.Length) break; var tag = U(e); var type = U(e + 2); var c = D(e + 4); var value = e + 8; var size = checked((long)c * TypeSize(type)); if (size > 4) value = checked(start + (int)D(value)); if (value < start || value + Math.Min(size, 4096) > b.Length) continue;
            string? S() { var raw = b.AsSpan(value, (int)Math.Min(size, 4096)); return Encoding.ASCII.GetString(raw).TrimEnd('\0', ' '); }
            double R() { if (type != 5 || size < 8) return double.NaN; var a = D(value); var z = D(value + 4); return z == 0 ? double.NaN : (double)a / z; }
            switch (tag) { case 0x0100: width = (int)D(value); break; case 0x0101: height = (int)D(value); break; case 0x010f: make = S(); break; case 0x0110: model = S(); break; case 0x0112: orient = U(value); break; case 0x0132: date = S(); break; case 0x8769: exifIfd = D(value); break; case 0x9003: originalDate = S(); break; case 0x9004: digitizedDate = S(); break; case 0x829a: exposure = R(); break; case 0x829d: aperture = R(); break; case 0x8827: iso = type == 3 ? U(value) : (int)D(value); break; case 0x920a: focal = R(); break; }
        }
        // EXIF camera fields normally live in the sub-IFD referenced by 0x8769.
        if (exifIfd > 0 && exifIfd < b.Length - 2)
        {
            var sub = checked(start + (int)exifIfd);
            if (sub >= start && sub + 2 <= b.Length)
            {
                var subCount = Math.Min(U(sub), (ushort)256);
                for (var n = 0; n < subCount; n++) { var e = sub + 2 + n * 12; if (e + 12 > b.Length) break; var tag = U(e); var type = U(e + 2); var c = D(e + 4); var value = e + 8; var size = checked((long)c * TypeSize(type)); if (size > 4) value = checked(start + (int)D(value)); if (value < start || value + Math.Min(size, 4096) > b.Length) continue; string S() => Encoding.ASCII.GetString(b, value, (int)Math.Min(size, 4096)).TrimEnd('\0', ' '); double R() { if (type != 5 || size < 8) return double.NaN; var a = D(value); var z = D(value + 4); return z == 0 ? double.NaN : (double)a / z; } switch (tag) { case 0x9003: originalDate ??= S(); break; case 0x9004: digitizedDate ??= S(); break; case 0x829a: exposure ??= R(); break; case 0x829d: aperture ??= R(); break; case 0x8827: iso ??= type == 3 ? U(value) : (int)D(value); break; case 0x920a: focal ??= R(); break; } }
            }
        }
        // Follow the next-IFD chain for multi-page TIFFs (bounded to avoid hostile files).
        var next = ifd + 2 + count * 12; if (next + 4 <= b.Length) { var cursor = D(next); for (var i = 1; i < 256 && cursor > 0 && start + cursor + 2 <= b.Length; i++) { pages++; var p = checked(start + (int)cursor); var pc = Math.Min(U(p), (ushort)256); var no = p + 2 + pc * 12; if (no + 4 > b.Length) break; cursor = D(no); } }
        DateTimeOffset? captured = ParseDate(originalDate) ?? ParseDate(digitizedDate) ?? ParseDate(date);
        return new ImageMetadata(make, model, captured, exposure, aperture, iso, focal, orient, pages, width, height);
    }
    private static int CountGifFrames(byte[] data)
    {
        if (data.Length < 13 || data[0] != 'G' || data[1] != 'I' || data[2] != 'F') return 0;
        var i = 13; var packed = data[10];
        if ((packed & 0x80) != 0) i += 3 * (1 << ((packed & 7) + 1));
        var count = 0; var valid = false;
        while (i < data.Length)
        {
            var marker = data[i++];
            if (marker == 0x3b) { valid = true; break; }
            if (marker == 0x2c)
            {
                if (i + 9 > data.Length) break;
                var imagePacked = data[i + 8]; i += 9;
                if ((imagePacked & 0x80) != 0) i += 3 * (1 << ((imagePacked & 7) + 1));
                if (i >= data.Length) break;
                i++; // LZW minimum code size
                if (!SkipGifSubBlocks(data, ref i)) break;
                count++;
            }
            else if (marker == 0x21)
            {
                if (i >= data.Length) break;
                i++; // extension label
                if (!SkipGifSubBlocks(data, ref i)) break;
            }
            else break;
            if (i > data.Length) break;
        }
        return valid ? count : 0;
    }
    private static ImageMetadata ParseWebp(byte[] data)
    {
        if (data.Length < 16 || Encoding.ASCII.GetString(data, 0, 4) != "RIFF" || Encoding.ASCII.GetString(data, 8, 4) != "WEBP") return new ImageMetadata();
        var frames = 0; var width = 0; var height = 0; var hasImagePayload = false; var i = 12;
        while (i + 8 <= data.Length)
        {
            var chunk = Encoding.ASCII.GetString(data, i, 4); var size = BitConverter.ToInt32(data, i + 4); if (size < 0 || size > data.Length - i - 8) break;
            if (chunk == "ANMF") { frames++; hasImagePayload = true; }
            if (chunk == "VP8X" && size >= 10) { hasImagePayload = true; width = 1 + data[i + 8 + 4] + (data[i + 8 + 5] << 8) + (data[i + 8 + 6] << 16); height = 1 + data[i + 8 + 7] + (data[i + 8 + 8] << 8) + (data[i + 8 + 9] << 16); }
            // VP8L stores 14-bit width/height values immediately after its 0x2f signature.
            // Keep this bounded and independent of the bitstream decoder: metadata must remain
            // lazy and must never make a malformed file unsafe to inspect.
            if (chunk == "VP8L" && size >= 5 && data[i + 8] == 0x2f)
            {
                hasImagePayload = true;
                var bits = (uint)(data[i + 9] | (data[i + 10] << 8) | (data[i + 11] << 16) | (data[i + 12] << 24));
                width = 1 + (int)(bits & 0x3fff);
                height = 1 + (int)((bits >> 14) & 0x3fff);
            }
            if (chunk == "VP8 " && size >= 10 && data[i + 8 + 3] == 0x9d && data[i + 8 + 4] == 0x01 && data[i + 8 + 5] == 0x2a) { hasImagePayload = true; width = BitConverter.ToUInt16(data, i + 8 + 6) & 0x3fff; height = BitConverter.ToUInt16(data, i + 8 + 8) & 0x3fff; }
            i += 8 + size + (size & 1);
        }
        return new ImageMetadata(FrameCount: frames > 0 ? frames : hasImagePayload ? 1 : 0, Width: width, Height: height);
    }
    private static bool SkipGifSubBlocks(byte[] data, ref int offset)
    {
        while (offset < data.Length) { var length = data[offset++]; if (length == 0) return true; if (length > data.Length - offset) return false; offset += length; }
        return false;
    }
    private static DateTimeOffset? ParseDate(string? value) => DateTimeOffset.TryParseExact(value, "yyyy:MM:dd HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var dt) ? dt : null;
    private static int TypeSize(ushort type) => type switch { 1 or 2 or 6 or 7 => 1, 3 or 8 => 2, 4 or 9 => 4, 5 or 10 => 8, 11 => 4, 12 => 8, _ => 1 };
}
