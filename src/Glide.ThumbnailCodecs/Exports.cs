using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using Glide.Imaging;

namespace Glide.ThumbnailCodecs;

/// <summary>
/// Optional-codec ABI v2 exports (native/Glide.CodecProvider.ABI.h) over Glide's bundled managed decoders.
/// Every export is total: it catches everything, so no managed exception can reach the native caller
/// (Explorer's thumbnail host). Nothing runs at load time; the provider table is built on first use.
/// Output is premultiplied BGRA8, as the ABI requires.
/// </summary>
public static unsafe class Exports
{
    private const uint AbiVersion = 2;
    private const uint SupportsPreview = 1u << 0;
    private const uint SupportsFull = 1u << 1;
    private const uint PixelFormatBgra8 = 1;
    private const uint AlphaPremultiplied = 1;
    private const uint InstanceMagic = 0x474C4D47; // "GLMG"

    /// <summary>
    /// Suffixes this library decodes for thumbnails: the managed families the native provider has no decoder
    /// for, plus PSD/PSB/PDD (the native compact decoder covers raw/RLE; ZIP-compressed documents land here).
    /// Keep in step with CodecCapabilityRegistry's built-in set (ThumbnailCoverageTests checks it).
    /// </summary>
    internal static readonly string[] Extensions =
    {
        ".exr", ".pfm", ".hdr.gz", ".dcm", ".dicom", ".ima", ".nii", ".nii.gz", ".fits", ".fit", ".fts", ".fts.gz",
        ".ora", ".kra", ".pdn", ".xcf", ".ktx", ".ktx2", ".pvr", ".astc", ".psd", ".psb", ".pdd",
        ".dng", ".cr2", ".cr3", ".crw", ".nef", ".nrw", ".arw", ".srf", ".sr2", ".raf", ".orf", ".ori", ".rw2", ".rwl",
        ".pef", ".ptx", ".3fr", ".fff", ".iiq", ".cap", ".eip", ".mef", ".mos", ".mrw", ".x3f", ".erf", ".kdc", ".dcr",
        ".k25", ".bay", ".srw", ".rwz", ".gpr", ".mdc", ".raw", ".dcs", ".drf", ".dsc", ".r2d", ".rw1",
    };

    [StructLayout(LayoutKind.Sequential)]
    private struct ProviderInfo
    {
        public uint AbiVersion;
        public uint StructSize;
        public byte* Id;
        public byte* Version;
        public byte* License;
        public byte** Extensions;
        public uint ExtensionCount;
        public uint Capabilities;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProbeResult
    {
        public uint Supported;
        public uint Width;
        public uint Height;
        public uint FrameCount;
        public uint Animated;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Surface
    {
        public void* Data;
        public nuint Size;
        public uint Width;
        public uint Height;
        public uint Stride;
        public uint PixelFormat;
        public uint AlphaMode;
    }

    private static ProviderInfo* s_info;
    private static readonly object InfoGate = new();

    [UnmanagedCallersOnly(EntryPoint = "GlideCodecProvider_GetInfo", CallConvs = new[] { typeof(CallConvCdecl) })]
    private static ProviderInfo* GetInfo()
    {
        try
        {
            if (s_info is not null) return s_info;
            lock (InfoGate)
            {
                if (s_info is not null) return s_info;
                var info = (ProviderInfo*)NativeMemory.AllocZeroed((nuint)sizeof(ProviderInfo));
                var table = (byte**)NativeMemory.AllocZeroed((nuint)(sizeof(byte*) * Extensions.Length));
                for (var i = 0; i < Extensions.Length; i++) table[i] = Utf8(Extensions[i]);
                info->AbiVersion = AbiVersion;
                info->StructSize = (uint)sizeof(ProviderInfo);
                info->Id = Utf8("glide.managed");
                info->Version = Utf8(typeof(Exports).Assembly.GetName().Version?.ToString(3) ?? "0.0.0");
                info->License = Utf8("MIT");
                info->Extensions = table;
                info->ExtensionCount = (uint)Extensions.Length;
                info->Capabilities = SupportsPreview | SupportsFull;
                s_info = info; // published once, never freed (process lifetime, like the module itself)
                return info;
            }
        }
        catch
        {
            return null;
        }
    }

    [UnmanagedCallersOnly(EntryPoint = "GlideCodecProvider_Create", CallConvs = new[] { typeof(CallConvCdecl) })]
    private static void* Create()
    {
        try
        {
            var instance = (uint*)NativeMemory.Alloc(sizeof(uint));
            *instance = InstanceMagic;
            return instance;
        }
        catch { return null; }
    }

    [UnmanagedCallersOnly(EntryPoint = "GlideCodecProvider_Destroy", CallConvs = new[] { typeof(CallConvCdecl) })]
    private static void Destroy(void* instance)
    {
        try
        {
            if (!Valid(instance)) return;
            *(uint*)instance = 0;
            NativeMemory.Free(instance);
        }
        catch { /* never unwind into the host */ }
    }

    [UnmanagedCallersOnly(EntryPoint = "GlideCodecProvider_Probe", CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int Probe(void* instance, byte* utf8Path, ProbeResult* result)
    {
        try
        {
            if (result is null) return 0;
            *result = default;
            if (!Valid(instance) || !TryPath(utf8Path, out var path)) return 0;
            if (!BuiltInRasterDecoder.TryProbeDimensions(path, out var dimensions) || !dimensions.IsValid) return 0;
            result->Supported = 1;
            result->Width = (uint)dimensions.Width;
            result->Height = (uint)dimensions.Height;
            result->FrameCount = 1;
            return 1;
        }
        catch { return 0; }
    }

    [UnmanagedCallersOnly(EntryPoint = "GlideCodecProvider_DecodePreviewSurface", CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int DecodePreviewSurface(void* instance, byte* utf8Path, uint longestSide, Surface* result)
        => Decode(instance, utf8Path, longestSide == 0 ? 0 : (int)Math.Min(longestSide, 32_768u), result);

    [UnmanagedCallersOnly(EntryPoint = "GlideCodecProvider_DecodeFullSurface", CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int DecodeFullSurface(void* instance, byte* utf8Path, uint ignored, Surface* result)
        => Decode(instance, utf8Path, 0, result);

    [UnmanagedCallersOnly(EntryPoint = "GlideCodecProvider_FreeBuffer", CallConvs = new[] { typeof(CallConvCdecl) })]
    private static void FreeBuffer(void* data, nuint size)
    {
        try { if (data is not null) NativeMemory.Free(data); }
        catch { /* never unwind into the host */ }
    }

    /// <summary>
    /// Registers the host's encoded-image decoder (WIC in Glide.ShellThumbnail) used for embedded JPEG/PNG.
    /// Not part of the generic ABI; a host that never calls it still gets every format without embedded
    /// JPEG/PNG data.
    /// </summary>
    [UnmanagedCallersOnly(EntryPoint = "GlideManaged_SetHostDecoder", CallConvs = new[] { typeof(CallConvCdecl) })]
    private static void SetHostDecoder(delegate* unmanaged[Cdecl]<byte*, nuint, uint, int, ManagedCodecSupport.HostImage*, int> decode,
                                       delegate* unmanaged[Cdecl]<void*, void> free)
    {
        try { ManagedCodecSupport.SetHost(decode, free); }
        catch { /* never unwind into the host */ }
    }

    /// <summary>
    /// Inflates a gzip member (SVGZ for the host's Direct2D SVG renderer; Windows exposes no inflate API).
    /// The output is capped at <paramref name="maxOutput"/> (and 64 MiB) and released with
    /// GlideCodecProvider_FreeBuffer. Returns 1 on success.
    /// </summary>
    [UnmanagedCallersOnly(EntryPoint = "GlideManaged_Gunzip", CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int Gunzip(byte* data, nuint size, nuint maxOutput, byte** output, nuint* outputSize)
    {
        try
        {
            if (output is null || outputSize is null) return 0;
            *output = null;
            *outputSize = 0;
            if (data is null || size == 0 || size > int.MaxValue) return 0;
            var limit = (long)Math.Min((ulong)maxOutput, 64UL * 1024 * 1024);
            var source = new ReadOnlySpan<byte>(data, (int)size);
            if (!ManagedCodecSupport.IsGzip(source) || !ManagedCodecSupport.TryGunzip(source, limit, out var inflated) ||
                inflated.Length == 0 || inflated.Length > limit)
                return 0;
            var memory = (byte*)NativeMemory.Alloc((nuint)inflated.Length);
            inflated.AsSpan().CopyTo(new Span<byte>(memory, inflated.Length));
            *output = memory;
            *outputSize = (nuint)inflated.Length;
            return 1;
        }
        catch
        {
            return 0;
        }
    }

    private static int Decode(void* instance, byte* utf8Path, int longestSide, Surface* result)
    {
        try
        {
            if (result is null) return 0;
            *result = default;
            if (!Valid(instance) || !TryPath(utf8Path, out var path)) return 0;
            var ok = longestSide > 0
                ? BuiltInRasterDecoder.TryDecodeBoundedPixels(path, longestSide, out var image)
                : BuiltInRasterDecoder.TryDecodeFullPixels(path, out image);
            if (!ok || image.Bgra is null || image.Width <= 0 || image.Height <= 0) return 0;
            var bytes = (long)image.Width * image.Height * 4;
            if (image.Bgra.Length < bytes) return 0;

            var output = (byte*)NativeMemory.Alloc((nuint)bytes);
            var source = image.Bgra;
            // Straight -> premultiplied BGRA (the ABI's only surface format).
            for (long i = 0; i < bytes; i += 4)
            {
                var a = source[i + 3];
                if (a == 255)
                {
                    output[i] = source[i]; output[i + 1] = source[i + 1]; output[i + 2] = source[i + 2]; output[i + 3] = 255;
                }
                else
                {
                    output[i] = (byte)((source[i] * a + 127) / 255);
                    output[i + 1] = (byte)((source[i + 1] * a + 127) / 255);
                    output[i + 2] = (byte)((source[i + 2] * a + 127) / 255);
                    output[i + 3] = a;
                }
            }
            result->Data = output;
            result->Size = (nuint)bytes;
            result->Width = (uint)image.Width;
            result->Height = (uint)image.Height;
            result->Stride = (uint)image.Width * 4;
            result->PixelFormat = PixelFormatBgra8;
            result->AlphaMode = AlphaPremultiplied;
            return 1;
        }
        catch
        {
            if (result is not null) *result = default;
            return 0;
        }
    }

    private static bool Valid(void* instance) => instance is not null && *(uint*)instance == InstanceMagic;

    private static bool TryPath(byte* utf8Path, out string path)
    {
        path = string.Empty;
        if (utf8Path is null) return false;
        var length = 0;
        while (length < 32_768 && utf8Path[length] != 0) length++;
        if (length == 0 || length >= 32_768) return false;
        path = Encoding.UTF8.GetString(utf8Path, length);
        return true;
    }

    private static byte* Utf8(string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        var memory = (byte*)NativeMemory.AllocZeroed((nuint)bytes.Length + 1);
        bytes.AsSpan().CopyTo(new Span<byte>(memory, bytes.Length));
        return memory;
    }
}
