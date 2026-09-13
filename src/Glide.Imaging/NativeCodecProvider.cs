using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace Glide.Imaging;

/// <summary>
/// Decoder-only native provider implementation. The module is intentionally kept loaded for the
/// lifetime of the process: native buffers and callbacks can never outlive an unloaded module.
/// Provider contexts are destroyed during Dispose; unloading is left to process teardown.
/// </summary>
internal sealed class NativeCodecProvider : ICodecSurfaceProvider, IDisposable
{
    private readonly nint _module;
    private readonly nint _instance;
    private readonly DestroyDelegate _destroy;
    private readonly ProbeDelegate _probe;
    private readonly FreeBufferDelegate _freeBuffer;
    private readonly DecodeDelegate? _preview;
    private readonly DecodeDelegate? _full;
    private readonly JsonDelegate? _metadata;
    private readonly JsonDelegate? _frames;
    private readonly SurfaceDecodeDelegate? _previewSurface;
    private readonly SurfaceDecodeDelegate? _fullSurface;
    private int _disposed;

    public CodecProviderInfo Info { get; }

    private NativeCodecProvider(nint module, nint instance, CodecProviderInfo info,
        DestroyDelegate destroy, ProbeDelegate probe, FreeBufferDelegate freeBuffer,
        DecodeDelegate? preview, DecodeDelegate? full, JsonDelegate? metadata, JsonDelegate? frames,
        SurfaceDecodeDelegate? previewSurface, SurfaceDecodeDelegate? fullSurface)
    {
        _module = module; _instance = instance; Info = info; _destroy = destroy; _probe = probe; _freeBuffer = freeBuffer;
        _preview = preview; _full = full; _metadata = metadata; _frames = frames;
        _previewSurface = previewSurface; _fullSurface = fullSurface;
    }

    public static bool TryLoad(string path, CodecProviderInfo expected, out NativeCodecProvider? provider, out string reason)
    {
        provider = null;
        reason = "Provider rejected.";
        nint module = 0;
        try
        {
            module = NativeLibrary.Load(path);
            var getInfo = GetExport<GetInfoDelegate>(module, "GlideCodecProvider_GetInfo");
            var infoPtr = getInfo();
            if (infoPtr == 0) { reason = "ABI info export returned null."; return false; }
            var header = Marshal.PtrToStructure<NativeCodecProviderHeader>(infoPtr);
            if (!CodecProviderAbi.IsSupportedVersion(header.AbiVersion) || header.StructSize < CodecProviderAbi.MinimumStructSize)
            { reason = "Native ABI header failed version/size validation."; return false; }
            var native = Marshal.PtrToStructure<NativeCodecProviderInfo>(infoPtr);
            if (native.ExtensionCount == 0 || native.ExtensionCount > 512 || native.Extensions == 0)
            { reason = "Native ABI extension table is missing or exceeds the bounded limit."; return false; }
            var actual = ReadInfo(native);
            if (!CodecProviderAbi.IsSafe(actual)) { reason = "Native ABI info failed version/size/identity/capability validation."; return false; }
            if (!Equivalent(actual, expected)) { reason = "Native ABI info does not match the signed manifest."; return false; }
            if (!CodecProviderAbi.CapabilitiesMatch(actual, native.Capabilities)) { reason = "Native capability flags do not match ABI fields."; return false; }

            var create = GetExport<CreateDelegate>(module, "GlideCodecProvider_Create");
            var destroy = GetExport<DestroyDelegate>(module, "GlideCodecProvider_Destroy");
            var probe = GetExport<ProbeDelegate>(module, "GlideCodecProvider_Probe");
            var free = GetExport<FreeBufferDelegate>(module, "GlideCodecProvider_FreeBuffer");
            DecodeDelegate? preview = null;
            DecodeDelegate? full = null;
            SurfaceDecodeDelegate? previewSurface = null;
            SurfaceDecodeDelegate? fullSurface = null;
            if (actual.AbiVersion == CodecProviderAbi.LegacyVersion)
            {
                preview = actual.Preview ? GetExport<DecodeDelegate>(module, "GlideCodecProvider_DecodePreview") : null;
                full = actual.Full ? GetExport<DecodeDelegate>(module, "GlideCodecProvider_DecodeFull") : null;
            }
            else
            {
                previewSurface = actual.Preview ? GetExport<SurfaceDecodeDelegate>(module, "GlideCodecProvider_DecodePreviewSurface") : null;
                fullSurface = actual.Full ? GetExport<SurfaceDecodeDelegate>(module, "GlideCodecProvider_DecodeFullSurface") : null;
            }
            JsonDelegate? metadata = actual.Metadata ? GetExport<JsonDelegate>(module, "GlideCodecProvider_ReadMetadata") : null;
            JsonDelegate? frames = actual.Frames ? GetExport<JsonDelegate>(module, "GlideCodecProvider_ReadFrames") : null;
            var instance = create();
            if (instance == 0) { reason = "Provider context creation failed."; return false; }
            provider = new NativeCodecProvider(module, instance, actual, destroy, probe, free, preview, full, metadata, frames, previewSurface, fullSurface);
            reason = actual.AbiVersion == CodecProviderAbi.CurrentVersion
                ? "Verified native ABI v2 direct-surface provider, manifest identity, capabilities, and SHA-256-selected artifact."
                : "Verified legacy native ABI v1 provider, manifest identity, capabilities, and SHA-256-selected artifact.";
            return true;

        }
        catch (Exception ex) when (ex is DllNotFoundException or BadImageFormatException or EntryPointNotFoundException or MarshalDirectiveException or SEHException)
        {
            reason = "Native ABI load failed: " + ex.Message;
            return false;
        }
        catch (Exception ex)
        {
            reason = "Provider validation failed: " + ex.Message;
            return false;
        }
        finally
        {
            // A rejected module has no managed owner and is safe to unload. Accepted modules are
            // deliberately process-lifetime-owned by the runtime and never reach this branch.
            if (provider is null && module != 0) NativeLibrary.Free(module);
        }
    }

    public CodecProbeResult Probe(string path, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfDisposed();
        using var utf8 = Utf8(path);
        try
        {
            var status = _probe(_instance, utf8.Pointer, out var result);
            var frames = result.FrameCount > 1_000_000 ? 1_000_000 : (int)result.FrameCount;
            return status == 0
                ? new CodecProbeResult(false, default, 0, false, "Provider probe returned failure.")
                : new CodecProbeResult(result.Supported != 0, new ImageDimensions(CheckedDimension(result.Width), CheckedDimension(result.Height)), frames, result.Animated != 0, null);
        }
        catch (Exception ex) when (ex is SEHException or AccessViolationException)
        {
            return new CodecProbeResult(false, default, 0, false, "Provider probe faulted: " + ex.Message);
        }
    }

    public Task<Stream?> DecodePreviewAsync(string path, int longestSide, CancellationToken cancellationToken = default)
        => DecodeAsync(_preview, path, longestSide, cancellationToken);

    public Task<Stream?> DecodeFullAsync(string path, CancellationToken cancellationToken = default)
        => DecodeAsync(_full, path, 0, cancellationToken);

    private async Task<Stream?> DecodeAsync(DecodeDelegate? decode, string path, int longestSide, CancellationToken token)
    {
        if (decode is null) return null;
        return await Task.Run(() =>
        {
            token.ThrowIfCancellationRequested(); ThrowIfDisposed();
            using var utf8 = Utf8(path);
            var status = decode(_instance, utf8.Pointer, checked((uint)Math.Max(0, longestSide)), out var native);
            if (native.Data == 0) return null;
            try
            {
                if (status == 0 || native.Size == 0) return null;
                token.ThrowIfCancellationRequested();
                var payloadSize = native.Size.ToUInt64();
                if (payloadSize > 512UL * 1024 * 1024 || payloadSize > int.MaxValue) throw new InvalidDataException("Provider output exceeds the managed stream limit.");
                var bytes = new byte[checked((int)payloadSize)];
                Marshal.Copy(native.Data, bytes, 0, bytes.Length);
                return (Stream?)new MemoryStream(bytes, writable: false);
            }
            finally { _freeBuffer(native.Data, native.Size); }
        }, token).ConfigureAwait(false);
    }


    public Task<CodecPixelSurface?> DecodePreviewSurfaceAsync(string path, int longestSide, CancellationToken cancellationToken = default)
        => DecodeSurfaceAsync(_previewSurface, path, longestSide, cancellationToken);

    public Task<CodecPixelSurface?> DecodeFullSurfaceAsync(string path, CancellationToken cancellationToken = default)
        => DecodeSurfaceAsync(_fullSurface, path, 0, cancellationToken);

    private async Task<CodecPixelSurface?> DecodeSurfaceAsync(SurfaceDecodeDelegate? decode, string path, int longestSide, CancellationToken token)
    {
        if (decode is null) return null;
        return await Task.Run(() =>
        {
            token.ThrowIfCancellationRequested(); ThrowIfDisposed();
            using var utf8 = Utf8(path);
            var status = decode(_instance, utf8.Pointer, checked((uint)Math.Max(0, longestSide)), out var native);
            if (native.Data == 0) return null;
            if (status == 0) { _freeBuffer(native.Data, native.Size); return null; }
            try
            {
                token.ThrowIfCancellationRequested();
                if (native.PixelFormat != 1 || native.AlphaMode != 1)
                    throw new InvalidDataException("ABI v2 provider must return premultiplied BGRA8.");
                var width = CheckedDimension(native.Width);
                var height = CheckedDimension(native.Height);
                var stride = checked((int)native.Stride);
                return new CodecPixelSurface(native.Data, native.Size, width, height, stride,
                    (data, size) => _freeBuffer(data, size));
            }
            catch
            {
                _freeBuffer(native.Data, native.Size);
                throw;
            }
        }, token).ConfigureAwait(false);
    }

    public Task<ImageMetadata> ReadMetadataAsync(string path, CancellationToken cancellationToken = default)
        => ReadJsonAsync(_metadata, path, new ImageMetadata(), cancellationToken);

    public Task<IReadOnlyList<CodecFrameInfo>> ReadFramesAsync(string path, CancellationToken cancellationToken = default)
        => ReadJsonAsync<IReadOnlyList<CodecFrameInfo>>(_frames, path, Array.Empty<CodecFrameInfo>(), cancellationToken);

    private async Task<T> ReadJsonAsync<T>(JsonDelegate? read, string path, T fallback, CancellationToken token)
    {
        if (read is null) return fallback;
        return await Task.Run(() =>
        {
            token.ThrowIfCancellationRequested(); ThrowIfDisposed();
            using var utf8 = Utf8(path);
            var status = read(_instance, utf8.Pointer, out var native);
            if (native.Data == 0) return fallback;
            try
            {
                if (status == 0 || native.Size == 0) return fallback;
                token.ThrowIfCancellationRequested();
                var payloadSize = native.Size.ToUInt64();
                if (payloadSize > 64UL * 1024 * 1024 || payloadSize > int.MaxValue) throw new InvalidDataException("Provider metadata exceeds the managed limit.");
                var bytes = new byte[checked((int)payloadSize)]; Marshal.Copy(native.Data, bytes, 0, bytes.Length);
                return JsonSerializer.Deserialize<T>(bytes, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? fallback;
            }
            finally { _freeBuffer(native.Data, native.Size); }
        }, token).ConfigureAwait(false);
    }

    private void ThrowIfDisposed() { if (Volatile.Read(ref _disposed) != 0) throw new ObjectDisposedException(nameof(NativeCodecProvider)); }
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        try { _destroy(_instance); } catch { }
        // _module intentionally remains loaded until process teardown. See class summary.
    }

    private static T GetExport<T>(nint module, string name) where T : Delegate
    {
        if (!NativeLibrary.TryGetExport(module, name, out var address) || address == 0)
            throw new EntryPointNotFoundException(name);
        return Marshal.GetDelegateForFunctionPointer<T>(address);
    }

    private static CodecProviderInfo ReadInfo(NativeCodecProviderInfo native)
    {
        var extensions = new List<string>();
        for (var i = 0; i < native.ExtensionCount && i < 512; i++)
        {
            var pointer = Marshal.ReadIntPtr(native.Extensions, i * IntPtr.Size);
            var extension = ReadUtf8Bounded(pointer);
            extensions.Add(extension);
        }
        var flags = native.Capabilities;
        return new CodecProviderInfo(native.AbiVersion, native.StructSize, String(native.Id), String(native.Version), String(native.License), extensions,
            (flags & CodecProviderAbi.SupportsPreview) != 0, (flags & CodecProviderAbi.SupportsFull) != 0,
            (flags & CodecProviderAbi.SupportsMetadata) != 0, (flags & CodecProviderAbi.SupportsFrames) != 0);
    }

    private static bool Equivalent(CodecProviderInfo left, CodecProviderInfo right) =>
        left.AbiVersion == right.AbiVersion && left.StructSize == right.StructSize &&
        string.Equals(left.Id, right.Id, StringComparison.Ordinal) && string.Equals(left.Version, right.Version, StringComparison.Ordinal) &&
        string.Equals(left.License, right.License, StringComparison.Ordinal) && left.Preview == right.Preview && left.Full == right.Full &&
        left.Metadata == right.Metadata && left.Frames == right.Frames &&
        left.Extensions.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).SequenceEqual(right.Extensions.OrderBy(x => x, StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase);

    private static string String(nint pointer) => ReadUtf8Bounded(pointer);
    private static string ReadUtf8Bounded(nint pointer, int maxBytes = 256)
    {
        if (pointer == 0) return "";
        var bytes = new List<byte>(Math.Min(maxBytes, 64));
        for (var i = 0; i < maxBytes; i++)
        {
            var value = Marshal.ReadByte(pointer, i);
            if (value == 0) break;
            bytes.Add(value);
        }
        return Encoding.UTF8.GetString(bytes.ToArray());
    }
    private static int CheckedDimension(uint value) => value > int.MaxValue ? 0 : (int)value;
    private static Utf8Buffer Utf8(string value) => new(value);

    [StructLayout(LayoutKind.Sequential)] private struct NativeCodecProviderHeader { public uint AbiVersion, StructSize; }
    [StructLayout(LayoutKind.Sequential)] private struct NativeCodecProviderInfo
    {
        public uint AbiVersion, StructSize; public nint Id, Version, License, Extensions; public uint ExtensionCount, Capabilities;
    }
    [StructLayout(LayoutKind.Sequential)] private struct NativeBuffer { public nint Data; public nuint Size; }
    [StructLayout(LayoutKind.Sequential)] private struct NativeProbe { public uint Supported, Width, Height, FrameCount, Animated; }
    [StructLayout(LayoutKind.Sequential)] private struct NativeSurface { public nint Data; public nuint Size; public uint Width, Height, Stride, PixelFormat, AlphaMode; }
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate nint GetInfoDelegate();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate nint CreateDelegate();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void DestroyDelegate(nint instance);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int ProbeDelegate(nint instance, nint path, out NativeProbe result);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int DecodeDelegate(nint instance, nint path, uint longestSide, out NativeBuffer result);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int SurfaceDecodeDelegate(nint instance, nint path, uint longestSide, out NativeSurface result);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int JsonDelegate(nint instance, nint path, out NativeBuffer result);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void FreeBufferDelegate(nint data, nuint size);

    private readonly struct Utf8Buffer : IDisposable
    {
        public readonly nint Pointer;
        public Utf8Buffer(string value)
        {
            var bytes = Encoding.UTF8.GetBytes(value + "\0"); Pointer = Marshal.AllocHGlobal(bytes.Length); Marshal.Copy(bytes, 0, Pointer, bytes.Length);
        }
        public void Dispose() => Marshal.FreeHGlobal(Pointer);
    }
}
