using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using SkiaSharp;

namespace Glide.DecodeBench507;

internal enum Mode { Preview, Full }

/// <summary>Preview = the viewport-bounded first frame (time-to-first-pixel); Full = source resolution.</summary>
internal sealed record Request(string Path, Mode Mode, int BoxW, int BoxH, int Threads);

internal sealed class Outcome
{
    public bool Ok;
    public int W, H;
    public string Route = "";
    public string? Error;
    public double ConvertMs;
    public ulong Checksum;
    public static Outcome Fail(string error, string route = "") => new() { Error = error, Route = route };
}

/// <summary>
/// One decoder under test. Prepare/Cleanup are untimed; Run is the timed region and must leave a complete
/// BGRA8 premultiplied frame (Glide's presentation format) so every engine pays for the same output.
/// </summary>
internal interface IEngine : IDisposable
{
    string Name { get; }
    string Library { get; }
    string? LoadError { get; }
    bool Supports(string ext);
    object? Prepare(Request r) => null;
    Outcome Run(Request r, object? state);
    void Cleanup(object? state) { }
}

internal static class Ext
{
    public static readonly HashSet<string> Jpeg = new(StringComparer.OrdinalIgnoreCase) { ".jpg", ".jpeg", ".jpe", ".jfif" };
    public static readonly HashSet<string> Raw = new(StringComparer.OrdinalIgnoreCase)
    {
        ".dng", ".cr2", ".cr3", ".crw", ".nef", ".nrw", ".arw", ".srf", ".sr2", ".raf", ".orf", ".ori", ".rw2", ".rwl", ".pef", ".ptx",
        ".3fr", ".fff", ".iiq", ".cap", ".eip", ".mef", ".mos", ".mrw", ".x3f", ".erf", ".kdc", ".dcr", ".k25", ".bay", ".srw", ".rwz",
        ".gpr", ".mdc", ".raw", ".dcs", ".drf", ".dsc", ".r2d", ".rw1"
    };
    public static string Of(string path)
    {
        var name = Path.GetFileName(path).ToLowerInvariant();
        foreach (var two in new[] { ".hdr.gz", ".nii.gz", ".fts.gz" }) if (name.EndsWith(two, StringComparison.Ordinal)) return two;
        return Path.GetExtension(name);
    }
}

/// <summary>Paths of the alternative codec binaries (tools\bench-507\engines.json).</summary>
internal sealed class EngineConfig
{
    public string CodecRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "Glide-bench-codecs");
    public string? GlideNative, GlideNativeTuned, TurboJpeg, Vips, VipsAll, LibRaw;
    public List<CliSpec> Cli = new();

    public sealed record CliSpec(string Name, string Exe, string Args, string[] Ext, string Library);

    public string? Resolve(string? p) => string.IsNullOrWhiteSpace(p) ? null : Path.IsPathRooted(p) ? p : Path.Combine(CodecRoot, p);

    public static EngineConfig Load(string? path)
    {
        var cfg = new EngineConfig();
        path ??= Path.Combine(AppContext.BaseDirectory, "engines.json");
        if (!File.Exists(path)) return cfg;
        using var doc = JsonDocument.Parse(File.ReadAllText(path), new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        var root = doc.RootElement;
        string? S(string k) => root.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        cfg.CodecRoot = S("codecRoot") ?? cfg.CodecRoot;
        cfg.GlideNative = S("glideNative"); cfg.GlideNativeTuned = S("glideNativeTuned"); cfg.TurboJpeg = S("turbojpeg"); cfg.Vips = S("vips"); cfg.VipsAll = S("vipsAll"); cfg.LibRaw = S("libraw");
        if (root.TryGetProperty("cli", out var cli))
            foreach (var e in cli.EnumerateArray())
                cfg.Cli.Add(new CliSpec(e.GetProperty("name").GetString()!, e.GetProperty("exe").GetString()!, e.GetProperty("args").GetString()!,
                    e.GetProperty("ext").EnumerateArray().Select(x => x.GetString()!).ToArray(),
                    e.TryGetProperty("library", out var lib) ? lib.GetString() ?? "" : ""));
        return cfg;
    }
}

internal static class EngineFactory
{
    public static readonly string[] All =
    {
        "glide", "wic", "wic-tuned", "skia", "turbojpeg", "turbojpeg-fast", "turbojpeg-bands", "turbojpeg-mmap", "vips", "vips-all", "libraw"
    };

    public static IEngine Create(string name, EngineConfig cfg, int threads)
    {
        var lower = name.ToLowerInvariant();
        return lower switch
        {
#if GLIDE_REFS
            "glide" => new GlideEngine(),
#else
            "glide" => new MissingEngine(name, "built with -p:GlideRefs=false"),
#endif
            "wic" => new WicEngine("wic", cfg.Resolve(cfg.GlideNative) ?? Path.Combine(AppContext.BaseDirectory, "Glide.Native.dll")),
            "wic-tuned" => cfg.Resolve(cfg.GlideNativeTuned) is { } tuned ? new WicEngine("wic-tuned", tuned) : new MissingEngine(name, "glideNativeTuned not set"),
            "skia" => new SkiaEngine(),
            "turbojpeg" => new TurboJpegEngine(name, cfg.Resolve(cfg.TurboJpeg), fast: false, bands: false, mmap: false),
            "turbojpeg-fast" => new TurboJpegEngine(name, cfg.Resolve(cfg.TurboJpeg), fast: true, bands: false, mmap: false),
            "turbojpeg-bands" => new TurboJpegEngine(name, cfg.Resolve(cfg.TurboJpeg), fast: false, bands: true, mmap: false),
            "turbojpeg-mmap" => new TurboJpegEngine(name, cfg.Resolve(cfg.TurboJpeg), fast: false, bands: false, mmap: true),
            "vips" => new VipsEngine(name, cfg.Resolve(cfg.Vips), threads),
            "vips-all" => new VipsEngine(name, cfg.Resolve(cfg.VipsAll), threads),
            "libraw" => new LibRawEngine(cfg.Resolve(cfg.LibRaw), cfg.Resolve(cfg.TurboJpeg)),
            _ => cfg.Cli.FirstOrDefault(c => c.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) is { } spec
                ? new CliEngine(spec, cfg)
                : new MissingEngine(name, "unknown engine")
        };
    }
}

internal sealed class MissingEngine(string name, string why) : IEngine
{
    public string Name => name;
    public string Library => "-";
    public string? LoadError => why;
    public bool Supports(string ext) => false;
    public Outcome Run(Request r, object? state) => Outcome.Fail(why);
    public void Dispose() { }
}

// =====================================================================================================
// WIC through Glide's own native bridge (Glide.Native.dll exports): the current production codec path
// without the managed coordinator. Point "glideNative" in engines.json at a tuned build to A/B it.
// =====================================================================================================
internal sealed unsafe class WicEngine : IEngine
{
    [StructLayout(LayoutKind.Sequential)]
    private struct NativeImg { public uint Width, Height, Stride; public ulong BufferBytes; public IntPtr Data; }

    private readonly IntPtr _lib;
    private readonly delegate* unmanaged[Cdecl]<char*, uint, uint, int, NativeImg*, int> _bounded;
    private readonly delegate* unmanaged[Cdecl]<char*, uint, NativeImg*, int> _decode;
    private readonly delegate* unmanaged[Cdecl]<char*, IntPtr, IntPtr, int> _into;
    private readonly delegate* unmanaged[Cdecl]<IntPtr, void> _free;

    public string Name { get; }
    public string Library { get; }
    public string? LoadError { get; }

    public WicEngine(string name, string dllPath)
    {
        Name = name;
        Library = "Windows WIC via " + Path.GetFileName(dllPath) + " (OS codecs; 0 bytes added)";
        try
        {
            _lib = NativeLibrary.Load(dllPath);
            _bounded = (delegate* unmanaged[Cdecl]<char*, uint, uint, int, NativeImg*, int>)NativeLibrary.GetExport(_lib, "GlideDecodeImageBoundedExW");
            _decode = (delegate* unmanaged[Cdecl]<char*, uint, NativeImg*, int>)NativeLibrary.GetExport(_lib, "GlideDecodeImageW");
            _into = (delegate* unmanaged[Cdecl]<char*, IntPtr, IntPtr, int>)NativeLibrary.GetExport(_lib, "GlideDecodeImageIntoW");
            _free = (delegate* unmanaged[Cdecl]<IntPtr, void>)NativeLibrary.GetExport(_lib, "GlideFreeImageBuffer");
        }
        catch (Exception ex) { LoadError = ex.Message; }
    }

    // Built-in WIC codecs plus the suffixes Store extensions add (WebP, HEIF/AV1, JPEG XL, RAW): whether this
    // PC has them is what the run records. Other suffixes never reach WIC first in Glide, so they are skipped.
    private static readonly HashSet<string> Exts = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".jpe", ".jfif", ".png", ".bmp", ".dib", ".gif", ".tif", ".tiff", ".ico", ".jxr", ".wdp", ".hdp", ".dds",
        ".webp", ".heic", ".heif", ".avif", ".jxl"
    };
    public bool Supports(string ext) => LoadError is null && (Exts.Contains(ext) || Ext.Raw.Contains(ext));

    private sealed class Target { public Surface? Surface; }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvCdecl) })]
    private static IntPtr Allocate(IntPtr context, uint width, uint height, uint* stride)
    {
        try
        {
            var t = (Target)GCHandle.FromIntPtr(context).Target!;
            t.Surface = new Surface((int)width, (int)height);
            *stride = (uint)t.Surface.Stride;
            return t.Surface.Ptr;
        }
        catch { return IntPtr.Zero; }
    }

    public Outcome Run(Request r, object? state)
    {
        if (LoadError is not null) return Outcome.Fail(LoadError);
        fixed (char* path = r.Path)
        {
            if (r.Mode == Mode.Full)
            {
                // Production order: decode straight into the presentation surface; status 2 = EXIF rotation -> buffered.
                var target = new Target();
                var handle = GCHandle.Alloc(target);
                int status;
                try { status = _into(path, (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, uint, uint, uint*, IntPtr>)&Allocate, GCHandle.ToIntPtr(handle)); }
                finally { handle.Free(); }
                if (status == 1 && target.Surface is { } s)
                {
                    var o = new Outcome { Ok = true, W = s.Width, H = s.Height, Route = "wic-into", Checksum = s.Checksum() };
                    s.Dispose();
                    return o;
                }
                target.Surface?.Dispose();
                if (status != 2) return Outcome.Fail("wic decode failed", "wic-into");
                NativeImg img;
                return Copy(_decode(path, 0, &img), img, "wic-buffered-oriented");
            }
            else
            {
                NativeImg img;
                return Copy(_bounded(path, (uint)r.BoxW, (uint)r.BoxH, 1, &img), img, "wic-bounded");
            }
        }
    }

    /// <summary>Same copy into a fresh surface that NativeImageDecoder.TryCreateBitmap performs.</summary>
    private Outcome Copy(int status, NativeImg img, string route)
    {
        if (img.Data == IntPtr.Zero) return Outcome.Fail("wic decode failed", route);
        try
        {
            if (status != 1) return Outcome.Fail("wic status " + status, route);
            using var s = new Surface((int)img.Width, (int)img.Height);
            for (var y = 0; y < s.Height; y++)
                Buffer.MemoryCopy((byte*)img.Data + (long)y * img.Stride, (byte*)s.Ptr + (long)y * s.Stride, s.Stride, s.Stride);
            return new Outcome { Ok = true, W = s.Width, H = s.Height, Route = route, Checksum = s.Checksum() };
        }
        finally { _free(img.Data); }
    }

    public void Dispose() { } // never unload: WIC/COM state may still reference the module
}

// =====================================================================================================
// Skia (SkiaSharp 2.88.9 = what Glide already ships): libjpeg-turbo, libpng, libwebp, giflib, wuffs-gif?
// Zero bytes added. Scaled decode where the codec supports it (JPEG n/8, WebP any), else full + resize.
// =====================================================================================================
internal sealed unsafe class SkiaEngine : IEngine
{
    private static readonly HashSet<string> Exts = new(StringComparer.OrdinalIgnoreCase)
    { ".jpg", ".jpeg", ".jpe", ".jfif", ".png", ".apng", ".webp", ".gif", ".bmp", ".dib", ".ico", ".wbmp" };

    public string Name => "skia";
    public string Library => "SkiaSharp 2.88.9 libSkiaSharp.dll (already shipped; BSD-3 + libjpeg-turbo/libpng/libwebp)";
    public string? LoadError => null;
    public bool Supports(string ext) => Exts.Contains(ext);

    public Outcome Run(Request r, object? state)
    {
        using var codec = SKCodec.Create(r.Path);
        if (codec is null) return Outcome.Fail("SKCodec.Create returned null");
        var info = codec.Info;
        var (tw, th) = r.Mode == Mode.Full ? (info.Width, info.Height) : Fit.Box(info.Width, info.Height, r.BoxW, r.BoxH);
        var size = new SKSizeI(info.Width, info.Height);
        if (tw < info.Width)
        {
            for (var n = 1; n <= 8; n++)
            {
                var sc = codec.GetScaledDimensions(n / 8f);
                if (sc.Width >= tw && sc.Height >= th && sc.Width <= info.Width) { size = sc; break; }
            }
        }
        var route = size.Width == info.Width ? (tw < info.Width ? "skia-full+resize" : "skia-full") : "skia-scaled";
        var decode = new Surface(size.Width, size.Height);
        try
        {
            var res = codec.GetPixels(new SKImageInfo(size.Width, size.Height, SKColorType.Bgra8888, SKAlphaType.Premul), decode.Ptr, decode.Stride, new SKCodecOptions(0));
            if (res is not (SKCodecResult.Success or SKCodecResult.IncompleteInput)) return Outcome.Fail("skia " + res, route);
            if (route != "skia-full+resize")
                return new Outcome { Ok = true, W = size.Width, H = size.Height, Route = route, Checksum = decode.Checksum() };
            var sw = Stopwatch.GetTimestamp();
            using var dst = new Surface(tw, th);
            using var srcMap = new SKPixmap(new SKImageInfo(size.Width, size.Height, SKColorType.Bgra8888, SKAlphaType.Premul), decode.Ptr, decode.Stride);
            using var dstMap = new SKPixmap(new SKImageInfo(tw, th, SKColorType.Bgra8888, SKAlphaType.Premul), dst.Ptr, dst.Stride);
            srcMap.ScalePixels(dstMap, SKFilterQuality.Medium);
            return new Outcome { Ok = true, W = tw, H = th, Route = route, ConvertMs = Stopwatch.GetElapsedTime(sw).TotalMilliseconds, Checksum = dst.Checksum() };
        }
        finally { decode.Dispose(); }
    }

    public void Dispose() { }
}

// =====================================================================================================
// libjpeg-turbo 3.x TurboJPEG API (turbojpeg.dll from the official 3.2.1 vc-x64 package).
// Variants: accurate (default), -fast (FASTDCT + FASTUPSAMPLE), -bands (full decode split into horizontal
// crop bands decoded in parallel; entropy decoding above each band is repeated), -mmap (memory-mapped input).
// =====================================================================================================
internal sealed unsafe class TurboJpegEngine : IEngine
{
    [StructLayout(LayoutKind.Sequential)] internal struct ScalingFactor { public int Num, Denom; }
    [StructLayout(LayoutKind.Sequential)] private struct Region { public int X, Y, W, H; }

    private const int TjInitDecompress = 1, ParamJpegWidth = 5, ParamJpegHeight = 6, ParamFastUpsample = 9, ParamFastDct = 10, ParamMaxPixels = 24;
    private const int PfBgra = 8;

    private readonly delegate* unmanaged[Cdecl]<int, IntPtr> _init;
    private readonly delegate* unmanaged[Cdecl]<IntPtr, void> _destroy;
    private readonly delegate* unmanaged[Cdecl]<IntPtr, int, int, int> _set;
    private readonly delegate* unmanaged[Cdecl]<IntPtr, int, int> _get;
    private readonly delegate* unmanaged[Cdecl]<IntPtr, byte*, nuint, int> _header;
    private readonly delegate* unmanaged[Cdecl]<IntPtr, ScalingFactor, int> _setScale;
    private readonly delegate* unmanaged[Cdecl]<IntPtr, Region, int> _setCrop;
    private readonly delegate* unmanaged[Cdecl]<IntPtr, byte*, nuint, byte*, int, int, int> _decompress;
    private readonly delegate* unmanaged[Cdecl]<IntPtr, byte*> _error;
    private readonly ScalingFactor[] _factors = Array.Empty<ScalingFactor>();
    private readonly bool _fast, _bands, _mmap;

    public string Name { get; }
    public string Library { get; }
    public string? LoadError { get; }

    public TurboJpegEngine(string name, string? dll, bool fast, bool bands, bool mmap)
    {
        Name = name; _fast = fast; _bands = bands; _mmap = mmap;
        Library = "libjpeg-turbo TurboJPEG 3 (" + (dll is null ? "not configured" : Path.GetFileName(dll)) + "; IJG + BSD-3 + zlib licences)";
        if (dll is null || !File.Exists(dll)) { LoadError = "turbojpeg.dll not found: " + dll; return; }
        try
        {
            var lib = NativeLibrary.Load(dll);
            IntPtr Get(string n) => NativeLibrary.GetExport(lib, n);
            _init = (delegate* unmanaged[Cdecl]<int, IntPtr>)Get("tj3Init");
            _destroy = (delegate* unmanaged[Cdecl]<IntPtr, void>)Get("tj3Destroy");
            _set = (delegate* unmanaged[Cdecl]<IntPtr, int, int, int>)Get("tj3Set");
            _get = (delegate* unmanaged[Cdecl]<IntPtr, int, int>)Get("tj3Get");
            _header = (delegate* unmanaged[Cdecl]<IntPtr, byte*, nuint, int>)Get("tj3DecompressHeader");
            _setScale = (delegate* unmanaged[Cdecl]<IntPtr, ScalingFactor, int>)Get("tj3SetScalingFactor");
            _setCrop = (delegate* unmanaged[Cdecl]<IntPtr, Region, int>)Get("tj3SetCroppingRegion");
            _decompress = (delegate* unmanaged[Cdecl]<IntPtr, byte*, nuint, byte*, int, int, int>)Get("tj3Decompress8");
            _error = (delegate* unmanaged[Cdecl]<IntPtr, byte*>)Get("tj3GetErrorStr");
            var getFactors = (delegate* unmanaged[Cdecl]<int*, ScalingFactor*>)Get("tj3GetScalingFactors");
            int n;
            var f = getFactors(&n);
            _factors = new ReadOnlySpan<ScalingFactor>(f, n).ToArray();
        }
        catch (Exception ex) { LoadError = ex.Message; }
    }

    public bool Supports(string ext) => LoadError is null && Ext.Jpeg.Contains(ext);

    private sealed class Input : IDisposable
    {
        public byte* Ptr; public long Length;
        private System.IO.MemoryMappedFiles.MemoryMappedFile? _map;
        private System.IO.MemoryMappedFiles.MemoryMappedViewAccessor? _view;
        public static Input Read(string path)
        {
            using var h = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, FileOptions.SequentialScan);
            var len = RandomAccess.GetLength(h);
            var p = (byte*)NativeMemory.Alloc((nuint)len);
            var read = 0L;
            while (read < len) read += RandomAccess.Read(h, new Span<byte>(p + read, (int)Math.Min(int.MaxValue, len - read)), read);
            return new Input { Ptr = p, Length = len };
        }
        public static Input Map(string path)
        {
            var map = System.IO.MemoryMappedFiles.MemoryMappedFile.CreateFromFile(path, FileMode.Open, null, 0, System.IO.MemoryMappedFiles.MemoryMappedFileAccess.Read);
            var view = map.CreateViewAccessor(0, 0, System.IO.MemoryMappedFiles.MemoryMappedFileAccess.Read);
            byte* p = null;
            view.SafeMemoryMappedViewHandle.AcquirePointer(ref p);
            return new Input { Ptr = p + view.PointerOffset, Length = new FileInfo(path).Length, _map = map, _view = view };
        }
        public void Dispose()
        {
            if (_view is not null) { _view.SafeMemoryMappedViewHandle.ReleasePointer(); _view.Dispose(); _map!.Dispose(); }
            else if (Ptr != null) NativeMemory.Free(Ptr);
            Ptr = null;
        }
    }

    public Outcome Run(Request r, object? state)
    {
        if (LoadError is not null) return Outcome.Fail(LoadError);
        using var input = _mmap ? Input.Map(r.Path) : Input.Read(r.Path);
        return DecodeBuffer(input.Ptr, (nuint)input.Length, r.Mode, r.BoxW, r.BoxH, r.Threads, out _);
    }

    /// <summary>Also used by LibRawEngine for embedded RAW previews.</summary>
    internal Outcome DecodeBuffer(byte* data, nuint length, Mode mode, int boxW, int boxH, int threads, out string scaleNote)
    {
        scaleNote = "1/1";
        var h = _init(TjInitDecompress);
        if (h == IntPtr.Zero) return Outcome.Fail("tj3Init failed");
        try
        {
            Configure(h);
            if (_header(h, data, length) != 0) return Outcome.Fail(Err(h));
            int w = _get(h, ParamJpegWidth), hh = _get(h, ParamJpegHeight);
            var sf = new ScalingFactor { Num = 1, Denom = 1 };
            if (mode == Mode.Preview)
            {
                var (tw, th) = Fit.Box(w, hh, boxW, boxH);
                foreach (var f in _factors.Where(f => f.Num <= f.Denom).OrderBy(f => f.Num / (double)f.Denom))
                    if (Scaled(w, f) >= tw && Scaled(hh, f) >= th) { sf = f; break; }
                if (_setScale(h, sf) != 0) return Outcome.Fail(Err(h));
                scaleNote = $"{sf.Num}/{sf.Denom}";
            }
            int ow = Scaled(w, sf), oh = Scaled(hh, sf);
            using var s = new Surface(ow, oh);
            var route = Name + (mode == Mode.Preview ? "-scaled-" + scaleNote : "");
            if (_bands && mode == Mode.Full && threads > 1 && oh >= 512)
            {
                var bands = Math.Min(threads, 8);
                var bandH = (oh + bands - 1) / bands;
                bandH = (bandH + 15) / 16 * 16; // whole iMCU rows: no band re-decodes another band's rows
                var errors = new string?[bands];
                var dp = data; var dl = length; var sp = (byte*)s.Ptr; var stride = s.Stride;
                Parallel.For(0, bands, new ParallelOptions { MaxDegreeOfParallelism = bands }, b =>
                {
                    var y0 = b * bandH;
                    if (y0 >= oh) return;
                    var bh = Math.Min(bandH, oh - y0);
                    var hb = _init(TjInitDecompress);
                    try
                    {
                        Configure(hb);
                        if (_header(hb, dp, dl) != 0 || _setCrop(hb, new Region { X = 0, Y = y0, W = 0, H = bh }) != 0 ||
                            _decompress(hb, dp, dl, sp + (long)y0 * stride, stride, PfBgra) != 0) errors[b] = Err(hb);
                    }
                    finally { _destroy(hb); }
                });
                var e = errors.FirstOrDefault(x => x is not null);
                if (e is not null) return Outcome.Fail(e, route + "-bands");
                return new Outcome { Ok = true, W = ow, H = oh, Route = route + $"-bands{bands}", Checksum = s.Checksum() };
            }
            if (_decompress(h, data, length, (byte*)s.Ptr, s.Stride, PfBgra) != 0) return Outcome.Fail(Err(h), route);
            return new Outcome { Ok = true, W = ow, H = oh, Route = route, Checksum = s.Checksum() };
        }
        finally { _destroy(h); }
    }

    private void Configure(IntPtr h)
    {
        _set(h, ParamMaxPixels, 0);
        if (_fast) { _set(h, ParamFastDct, 1); _set(h, ParamFastUpsample, 1); }
    }

    private static int Scaled(int d, ScalingFactor f) => (d * f.Num + f.Denom - 1) / f.Denom;
    private string Err(IntPtr h) => Marshal.PtrToStringAnsi((IntPtr)_error(h)) ?? "tj error";
    public void Dispose() { }
}

// =====================================================================================================
// libvips (LGPL-2.1+). "vips" = NetVips.Native.win-x64 single statically linked libvips-42.dll ("web"
// format set: mozjpeg, spng/libpng, libtiff, libwebp 1.6, libheif+aom, cgif). "vips-all" = the official
// multi-DLL "all" bundle (adds JXL, OpenJPEG, LibRaw, OpenEXR, ...). Preview = vips_thumbnail
// (shrink-on-load + lanczos to the box); Full = new_from_file(access=sequential) + write_to_memory.
// The operation cache is disabled so repetitions really decode.
// =====================================================================================================
internal sealed unsafe class VipsEngine : IEngine
{
    private readonly delegate* unmanaged[Cdecl]<byte*, byte*, IntPtr, int> _thumbnail; // varargs: declared per call below
    private readonly IntPtr _thumbPtr, _newFromFilePtr;
    private readonly delegate* unmanaged[Cdecl]<IntPtr, nuint*, IntPtr> _writeToMemory;
    private readonly delegate* unmanaged[Cdecl]<IntPtr, int> _getWidth, _getHeight, _getBands, _getFormat;
    private readonly delegate* unmanaged[Cdecl]<IntPtr, void> _unref, _gfree;
    private readonly delegate* unmanaged[Cdecl]<byte*> _errorBuffer;
    private readonly delegate* unmanaged[Cdecl]<void> _errorClear;

    public string Name { get; }
    public string Library { get; }
    public string? LoadError { get; }
    private readonly HashSet<string> _exts;

    public VipsEngine(string name, string? dll, int threads)
    {
        Name = name;
        Library = "libvips 8.18.7 (" + (dll is null ? "not configured" : Path.GetFileName(Path.GetDirectoryName(dll) ?? "")) + "; LGPL-2.1+)";
        _exts = new(StringComparer.OrdinalIgnoreCase) { ".jpg", ".jpeg", ".jpe", ".jfif", ".png", ".webp", ".tif", ".tiff", ".gif", ".heic", ".heif", ".avif", ".hdr" };
        if (name == "vips-all") foreach (var e in new[] { ".jxl", ".jp2", ".j2k", ".exr", ".fits", ".svg" }.Concat(Ext.Raw)) _exts.Add(e);
        if (dll is null || !File.Exists(dll)) { LoadError = "libvips not found: " + dll; return; }
        try
        {
            var dir = Path.GetDirectoryName(dll)!;
            // vips-all loads optional modules (vips-jxl.dll, ...) with plain LoadLibrary: their dependencies
            // live in the bundle's bin folder, so put it on this process's PATH (process-local).
            Environment.SetEnvironmentVariable("PATH", dir + ";" + Environment.GetEnvironmentVariable("PATH"));
            var lib = NativeLibrary.Load(dll);
            IntPtr Exp(string n)
            {
                if (NativeLibrary.TryGetExport(lib, n, out var p)) return p;
                foreach (var dep in new[] { "libgobject-2.0-0.dll", "libglib-2.0-0.dll" })
                {
                    var dp = Path.Combine(dir, dep);
                    if (File.Exists(dp) && NativeLibrary.TryGetExport(NativeLibrary.Load(dp), n, out p)) return p;
                }
                throw new EntryPointNotFoundException(n);
            }
            var init = (delegate* unmanaged[Cdecl]<byte*, int>)Exp("vips_init");
            fixed (byte* argv0 = "glide-bench\0"u8) if (init(argv0) != 0) throw new InvalidOperationException("vips_init failed");
            ((delegate* unmanaged[Cdecl]<int, void>)Exp("vips_cache_set_max"))(0);
            ((delegate* unmanaged[Cdecl]<int, void>)Exp("vips_concurrency_set"))(Math.Max(1, threads));
            _thumbPtr = Exp("vips_thumbnail");
            _newFromFilePtr = Exp("vips_image_new_from_file");
            _writeToMemory = (delegate* unmanaged[Cdecl]<IntPtr, nuint*, IntPtr>)Exp("vips_image_write_to_memory");
            _getWidth = (delegate* unmanaged[Cdecl]<IntPtr, int>)Exp("vips_image_get_width");
            _getHeight = (delegate* unmanaged[Cdecl]<IntPtr, int>)Exp("vips_image_get_height");
            _getBands = (delegate* unmanaged[Cdecl]<IntPtr, int>)Exp("vips_image_get_bands");
            _getFormat = (delegate* unmanaged[Cdecl]<IntPtr, int>)Exp("vips_image_get_format");
            _unref = (delegate* unmanaged[Cdecl]<IntPtr, void>)Exp("g_object_unref");
            _gfree = (delegate* unmanaged[Cdecl]<IntPtr, void>)Exp("g_free");
            _errorBuffer = (delegate* unmanaged[Cdecl]<byte*>)Exp("vips_error_buffer");
            _errorClear = (delegate* unmanaged[Cdecl]<void>)Exp("vips_error_clear");
        }
        catch (Exception ex) { LoadError = ex.Message; }
    }

    public bool Supports(string ext) => LoadError is null && _exts.Contains(ext);

    public Outcome Run(Request r, object? state)
    {
        if (LoadError is not null) return Outcome.Fail(LoadError);
        var path = Encoding.UTF8.GetBytes(r.Path + "\0");
        IntPtr img = IntPtr.Zero;
        fixed (byte* p = path)
        fixed (byte* kHeight = "height\0"u8)
        fixed (byte* kSize = "size\0"u8)
        fixed (byte* kAccess = "access\0"u8)
        {
            if (r.Mode == Mode.Preview)
            {
                // int vips_thumbnail(const char *filename, VipsImage **out, int width, ...): win-x64 passes
                // integer/pointer varargs exactly like fixed arguments. size=VIPS_SIZE_DOWN (1).
                var f = (delegate* unmanaged[Cdecl]<byte*, IntPtr*, int, byte*, int, byte*, int, IntPtr, int>)_thumbPtr;
                if (f(p, &img, r.BoxW, kHeight, r.BoxH, kSize, 1, IntPtr.Zero) != 0) return Fail("vips-thumbnail");
            }
            else
            {
                var f = (delegate* unmanaged[Cdecl]<byte*, byte*, int, IntPtr, IntPtr>)_newFromFilePtr;
                img = f(p, kAccess, 1 /* VIPS_ACCESS_SEQUENTIAL */, IntPtr.Zero);
                if (img == IntPtr.Zero) return Fail("vips-load");
            }
        }
        try
        {
            nuint size;
            var mem = _writeToMemory(img, &size); // forces the decode
            if (mem == IntPtr.Zero) return Fail("vips-write");
            try
            {
                int w = _getWidth(img), h = _getHeight(img), bands = _getBands(img), fmt = _getFormat(img);
                if (fmt != 0) return Outcome.Fail("non-8-bit vips output (format " + fmt + ")", "vips");
                var sw = Stopwatch.GetTimestamp();
                using var s = new Surface(w, h);
                switch (bands)
                {
                    case 3: Convert32.RgbToBgra((byte*)mem, w * 3, s); break;
                    case 4: Convert32.RgbaToBgraPremul((byte*)mem, w * 4, s); break;
                    case 1: Convert32.GrayToBgra((byte*)mem, w, s); break;
                    default: return Outcome.Fail("unsupported band count " + bands, "vips");
                }
                var conv = Stopwatch.GetElapsedTime(sw).TotalMilliseconds;
                return new Outcome { Ok = true, W = w, H = h, Route = Name + (r.Mode == Mode.Preview ? "-thumbnail" : "-load"), ConvertMs = conv, Checksum = s.Checksum() };
            }
            finally { _gfree(mem); }
        }
        finally { _unref(img); }
    }

    private Outcome Fail(string route)
    {
        var e = Marshal.PtrToStringUTF8((IntPtr)_errorBuffer()) ?? "vips error";
        _errorClear();
        return Outcome.Fail(e.Trim(), route);
    }

    public void Dispose() { }
}

// =====================================================================================================
// LibRaw (LGPL-2.1 or CDDL-1.0): embedded preview extraction (unpack_thumb) + JPEG decode with TurboJPEG
// (scaled for the preview), i.e. the same job Glide's managed RawPreviewDecoder + Skia do today.
// =====================================================================================================
internal sealed unsafe class LibRawEngine : IEngine
{
    private readonly delegate* unmanaged[Cdecl]<uint, IntPtr> _init;
    private readonly delegate* unmanaged[Cdecl]<IntPtr, char*, int> _openW;
    private readonly delegate* unmanaged[Cdecl]<IntPtr, int> _unpackThumb;
    private readonly delegate* unmanaged[Cdecl]<IntPtr, int*, IntPtr> _makeThumb;
    private readonly delegate* unmanaged[Cdecl]<IntPtr, void> _clearMem, _close;
    private readonly TurboJpegEngine? _jpeg;

    public string Name => "libraw";
    public string Library => "LibRaw 0.22.3 libraw.dll (LGPL-2.1 or CDDL-1.0) + " + (_jpeg?.LoadError is null && _jpeg is not null ? "TurboJPEG" : "Skia");
    public string? LoadError { get; }

    public LibRawEngine(string? dll, string? turbo)
    {
        if (turbo is not null && File.Exists(turbo)) _jpeg = new TurboJpegEngine("libraw", turbo, false, false, false);
        if (dll is null || !File.Exists(dll)) { LoadError = "libraw.dll not found: " + dll; return; }
        try
        {
            var lib = NativeLibrary.Load(dll);
            _init = (delegate* unmanaged[Cdecl]<uint, IntPtr>)NativeLibrary.GetExport(lib, "libraw_init");
            _openW = (delegate* unmanaged[Cdecl]<IntPtr, char*, int>)NativeLibrary.GetExport(lib, "libraw_open_wfile");
            _unpackThumb = (delegate* unmanaged[Cdecl]<IntPtr, int>)NativeLibrary.GetExport(lib, "libraw_unpack_thumb");
            _makeThumb = (delegate* unmanaged[Cdecl]<IntPtr, int*, IntPtr>)NativeLibrary.GetExport(lib, "libraw_dcraw_make_mem_thumb");
            _clearMem = (delegate* unmanaged[Cdecl]<IntPtr, void>)NativeLibrary.GetExport(lib, "libraw_dcraw_clear_mem");
            _close = (delegate* unmanaged[Cdecl]<IntPtr, void>)NativeLibrary.GetExport(lib, "libraw_close");
        }
        catch (Exception ex) { LoadError = ex.Message; }
    }

    public bool Supports(string ext) => LoadError is null && Ext.Raw.Contains(ext);

    public Outcome Run(Request r, object? state)
    {
        if (LoadError is not null) return Outcome.Fail(LoadError);
        var lr = _init(0);
        if (lr == IntPtr.Zero) return Outcome.Fail("libraw_init failed");
        try
        {
            int rc;
            fixed (char* p = r.Path) rc = _openW(lr, p);
            if (rc != 0) return Outcome.Fail("libraw_open " + rc, "libraw");
            if ((rc = _unpackThumb(lr)) != 0) return Outcome.Fail("libraw_unpack_thumb " + rc, "libraw");
            int err;
            var img = _makeThumb(lr, &err);
            if (img == IntPtr.Zero) return Outcome.Fail("make_mem_thumb " + err, "libraw");
            try
            {
                var type = *(int*)img;
                ushort h = *(ushort*)(img + 4), w = *(ushort*)(img + 6), colors = *(ushort*)(img + 8), bits = *(ushort*)(img + 10);
                var size = *(uint*)(img + 12);
                var data = (byte*)(img + 16);
                if (type == 1) // LIBRAW_IMAGE_JPEG
                {
                    if (_jpeg is not null && _jpeg.LoadError is null)
                    {
                        var o = _jpeg.DecodeBuffer(data, size, r.Mode, r.BoxW, r.BoxH, 1, out var sc);
                        o.Route = "libraw-thumb+turbojpeg-" + sc;
                        return o;
                    }
                    using var skData = SKData.Create((IntPtr)data, (int)size);
                    using var codec = SKCodec.Create(skData);
                    if (codec is null) return Outcome.Fail("embedded JPEG not decodable", "libraw");
                    using var s = new Surface(codec.Info.Width, codec.Info.Height);
                    var res = codec.GetPixels(new SKImageInfo(s.Width, s.Height, SKColorType.Bgra8888, SKAlphaType.Premul), s.Ptr);
                    return res == SKCodecResult.Success
                        ? new Outcome { Ok = true, W = s.Width, H = s.Height, Route = "libraw-thumb+skia", Checksum = s.Checksum() }
                        : Outcome.Fail("skia " + res, "libraw-thumb+skia");
                }
                if (type == 2 && bits == 8 && colors == 3)
                {
                    var sw = Stopwatch.GetTimestamp();
                    using var s = new Surface(w, h);
                    Convert32.RgbToBgra(data, w * 3, s);
                    return new Outcome { Ok = true, W = w, H = h, Route = "libraw-thumb-bitmap", ConvertMs = Stopwatch.GetElapsedTime(sw).TotalMilliseconds, Checksum = s.Checksum() };
                }
                return Outcome.Fail($"thumbnail type {type} bits {bits} colors {colors}", "libraw");
            }
            finally { _clearMem(img); }
        }
        finally { _close(lr); }
    }

    public void Dispose() => _jpeg?.Dispose();
}

// =====================================================================================================
// External decoder executables (djxl, dwebp, avifdec, opj_decompress, djpeg ...): wall time including
// process start-up and any output write, so only use them for a coarse "is the library faster" signal.
// Template tokens: {in} {out} {threads}. {out} is a temp file deleted afterwards.
// =====================================================================================================
internal sealed class CliEngine(EngineConfig.CliSpec spec, EngineConfig cfg) : IEngine
{
    private readonly string _exe = cfg.Resolve(spec.Exe)!;
    public string Name => spec.Name;
    public string Library => spec.Library + " (CLI wall time incl. process start)";
    public string? LoadError => File.Exists(_exe) ? null : "missing " + _exe;
    public bool Supports(string ext) => LoadError is null && spec.Ext.Contains(ext, StringComparer.OrdinalIgnoreCase);

    // Base name without extension; templates append their own ("{out}.y4m"). Cleanup deletes base*.
    public object? Prepare(Request r) => Path.Combine(Path.GetTempPath(), "glide-bench-" + Guid.NewGuid().ToString("N"));

    public Outcome Run(Request r, object? state)
    {
        var outFile = (string)state!;
        var args = spec.Args.Replace("{in}", r.Path).Replace("{out}", outFile).Replace("{threads}", r.Threads.ToString());
        var psi = new ProcessStartInfo(_exe, args) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = Path.GetTempPath() };
        using var p = Process.Start(psi)!;
        p.StandardOutput.ReadToEndAsync(); var err = p.StandardError.ReadToEndAsync();
        if (!p.WaitForExit(120_000)) { try { p.Kill(true); } catch { } return Outcome.Fail("timeout", "cli"); }
        return p.ExitCode == 0 ? new Outcome { Ok = true, Route = "cli-wall" } : Outcome.Fail("exit " + p.ExitCode + ": " + err.Result.Trim(), "cli");
    }

    public void Cleanup(object? state)
    {
        var f = (string)state!;
        foreach (var candidate in Directory.GetFiles(Path.GetDirectoryName(f)!, Path.GetFileNameWithoutExtension(f) + "*"))
            try { File.Delete(candidate); } catch { }
    }

    public void Dispose() { }
}
