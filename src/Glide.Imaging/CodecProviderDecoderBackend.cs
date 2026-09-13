using Avalonia.Media.Imaging;
using Glide.Core;

namespace Glide.Imaging;

/// <summary>
/// Provider adapter with a preserved fast fallback. Optional providers are resolved only on the
/// first non-core suffix that actually needs them; constructing this backend performs no directory
/// enumeration, hashing, native load, or provider initialisation.
/// </summary>
public sealed class CodecProviderDecoderBackend : IImageDecoderBackend, IPathOptimizedImageDecoderBackend, IDisposable
{
    private readonly IImageDecoderBackend _fallback;
    private readonly Func<string, ICodecProvider?>? _lazyResolver;
    private readonly object _providerGate = new();
    private readonly Dictionary<string, ICodecProvider> _providers = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<ICodecProvider> _ownedProviders = new(ReferenceEqualityComparer.Instance);
    private readonly HashSet<string> _resolverAttempted = new(StringComparer.OrdinalIgnoreCase);
    private int _disposed;

    public event Action<string, string>? ProviderFailure;

    public CodecProviderDecoderBackend(IImageDecoderBackend? fallback = null, Func<string, ICodecProvider?>? lazyResolver = null)
    {
        _fallback = fallback ?? new AvaloniaImageDecoderBackend();
        _lazyResolver = lazyResolver;
    }

    public IReadOnlyList<CodecProviderInfo> Providers
    {
        get { lock (_providerGate) return _providers.Values.Distinct<ICodecProvider>(ReferenceEqualityComparer.Instance).Select(x => x.Info).ToArray(); }
    }

    public bool Register(ICodecProvider provider) => Register(provider, ownsProvider: true);

    internal bool Register(ICodecProvider provider, bool ownsProvider)
    {
        ThrowIfDisposed();
        if (!CodecProviderAbi.IsSafe(provider.Info)) return false;
        var extensions = provider.Info.Extensions.Select(NormalizeExtension)
            .Where(x => CodecCapabilityRegistry.IsDeclared(x))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (extensions.Length == 0) return false;
        lock (_providerGate)
        {
            foreach (var ext in extensions) _providers[ext] = provider;
            if (ownsProvider) _ownedProviders.Add(provider);
        }
        CodecCapabilityRegistry.RegisterExternalProvider(provider.Info.Id, extensions);
        return true;
    }

    public Bitmap DecodeFull(Stream stream) => _fallback.DecodeFull(stream);
    public Bitmap DecodePreview(Stream stream, ImageDimensions source, int longestSide, BitmapInterpolationMode interpolationMode)
        => _fallback.DecodePreview(stream, source, longestSide, interpolationMode);
    public Bitmap DecodeFull(Stream stream, string? sourcePath) => _fallback.DecodeFull(stream, sourcePath);
    public Bitmap DecodePreview(Stream stream, ImageDimensions source, int longestSide, BitmapInterpolationMode mode, string? sourcePath)
        => _fallback.DecodePreview(stream, source, longestSide, mode, sourcePath);

    public bool SupportsPathPreview(string path)
        => _fallback is IPathOptimizedImageDecoderBackend pathBackend && pathBackend.SupportsPathPreview(path);

    public Task<PathPreviewDecodeResult?> TryDecodePreviewFromPathAsync(
        string path, ImageDimensions source, int maxWidth, int maxHeight, bool progressiveColorFirstPreview,
        CancellationToken cancellationToken = default)
        => _fallback is IPathOptimizedImageDecoderBackend pathBackend
            ? pathBackend.TryDecodePreviewFromPathAsync(path, source, maxWidth, maxHeight, progressiveColorFirstPreview, cancellationToken)
            : Task.FromResult<PathPreviewDecodeResult?>(null);

    public async Task<Bitmap> DecodeFullAsync(Stream stream, string? sourcePath, CancellationToken cancellationToken = default)
    {
        if (sourcePath is not null && TryProvider(sourcePath, out var provider) && provider.Info.Full)
        {
            try
            {
                if (provider is ICodecSurfaceProvider surfaceProvider)
                {
                    using var surface = await surfaceProvider.DecodeFullSurfaceAsync(sourcePath, cancellationToken).ConfigureAwait(false);
                    if (surface is not null) return CodecSurfaceBitmapFactory.Create(surface);
                }
                await using var bytes = await provider.DecodeFullAsync(sourcePath, cancellationToken).ConfigureAwait(false);
                if (bytes is not null) return await _fallback.DecodeFullAsync(bytes, sourcePath, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { ProviderFailure?.Invoke(provider.Info.Id, "full decode: " + ex.Message); }
        }
        return await _fallback.DecodeFullAsync(stream, sourcePath, cancellationToken).ConfigureAwait(false);
    }

    public async Task<Bitmap> DecodePreviewAsync(Stream stream, ImageDimensions source, int longestSide, BitmapInterpolationMode mode, string? sourcePath, CancellationToken cancellationToken = default)
    {
        if (sourcePath is not null && TryProvider(sourcePath, out var provider) && provider.Info.Preview)
        {
            try
            {
                if (provider is ICodecSurfaceProvider surfaceProvider)
                {
                    using var surface = await surfaceProvider.DecodePreviewSurfaceAsync(sourcePath, longestSide, cancellationToken).ConfigureAwait(false);
                    if (surface is not null) return CodecSurfaceBitmapFactory.Create(surface);
                }
                await using var bytes = await provider.DecodePreviewAsync(sourcePath, longestSide, cancellationToken).ConfigureAwait(false);
                if (bytes is not null) return await _fallback.DecodePreviewAsync(bytes, source, longestSide, mode, sourcePath, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { ProviderFailure?.Invoke(provider.Info.Id, "preview decode: " + ex.Message); }
        }
        return await _fallback.DecodePreviewAsync(stream, source, longestSide, mode, sourcePath, cancellationToken).ConfigureAwait(false);
    }

    private bool TryProvider(string path, out ICodecProvider provider)
    {
        ThrowIfDisposed();
        var match = CodecCapabilityRegistry.Match(path);
        if (match is null) { provider = null!; return false; }

        lock (_providerGate)
        {
            if (_providers.TryGetValue(match, out provider!)) return true;
        }

        // Never wake the optional-codec runtime for the preserved 10-format core or formats that
        // Windows/Avalonia can route natively (JPEG aliases, JXR/HDP/WDP, DDS, etc.). If a provider
        // for one of these extensions is already live it was returned above; otherwise the platform
        // path wins without provider-index/hash/native-load overhead.
        var capability = CodecCapabilityRegistry.Describe(match);
        if (ImageFormatRegistry.IsCoreFastPath(match) || capability.Tier == CodecTier.NativeOs || _lazyResolver is null)
        {
            provider = null!;
            return false;
        }

        lock (_providerGate)
        {
            if (_providers.TryGetValue(match, out provider!)) return true;
            if (!_resolverAttempted.Add(match)) { provider = null!; return false; }
        }

        ICodecProvider? resolved = null;
        try { resolved = _lazyResolver(path); }
        catch (Exception ex) { ProviderFailure?.Invoke("resolver", match + ": " + ex.Message); }
        if (resolved is null) { provider = null!; return false; }
        if (!Register(resolved, ownsProvider: false)) { provider = null!; return false; }

        lock (_providerGate) return _providers.TryGetValue(match, out provider!);
    }

    public async Task<ImageMetadata?> ReadMetadataAsync(string path, CancellationToken cancellationToken = default)
    {
        if (!TryProvider(path, out var provider) || !provider.Info.Metadata) return null;
        try { return await provider.ReadMetadataAsync(path, cancellationToken).ConfigureAwait(false); }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { ProviderFailure?.Invoke(provider.Info.Id, "metadata: " + ex.Message); return null; }
    }

    public CodecProbeResult? Probe(string path, CancellationToken cancellationToken = default)
    {
        if (!TryProvider(path, out var provider)) return null;
        try { return provider.Probe(path, cancellationToken); }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { ProviderFailure?.Invoke(provider.Info.Id, "probe: " + ex.Message); return new CodecProbeResult(false, default, 0, false, ex.Message); }
    }

    public async Task<IReadOnlyList<CodecFrameInfo>?> ReadFramesAsync(string path, CancellationToken cancellationToken = default)
    {
        if (!TryProvider(path, out var provider) || !provider.Info.Frames) return null;
        try { return await provider.ReadFramesAsync(path, cancellationToken).ConfigureAwait(false); }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { ProviderFailure?.Invoke(provider.Info.Id, "frames: " + ex.Message); return null; }
    }

    private static string NormalizeExtension(string extension) => extension.StartsWith('.') ? extension.ToLowerInvariant() : "." + extension.ToLowerInvariant();
    private void ThrowIfDisposed() { if (Volatile.Read(ref _disposed) != 0) throw new ObjectDisposedException(nameof(CodecProviderDecoderBackend)); }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        lock (_providerGate)
        {
            foreach (var provider in _ownedProviders) provider.Dispose();
            _ownedProviders.Clear();
            _providers.Clear();
            _resolverAttempted.Clear();
        }
        // External-provider registration is process-wide because the runtime may be shared by
        // multiple windows. Do not clear it when one window/backend closes.
        if (_fallback is IDisposable disposableFallback) disposableFallback.Dispose();
    }
}
