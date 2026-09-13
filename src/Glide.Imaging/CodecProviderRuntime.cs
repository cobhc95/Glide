using Glide.Core;

namespace Glide.Imaging;

/// <summary>
/// Process-wide owner for optional codecs. Construction is lazy and cheap: it parses at most the
/// tiny build-generated index. DLL hashing, native loading and provider initialisation happen only
/// for the first file whose suffix actually needs that provider family.
/// </summary>
public sealed class CodecProviderRuntime : IDisposable
{
    private static readonly Lazy<CodecProviderRuntime> SharedRuntime = new(CreateDefault, LazyThreadSafetyMode.ExecutionAndPublication);
    private readonly object _gate = new();
    private readonly CodecProviderDecoderBackend _backend;
    private readonly CodecProviderIndex _index;
    private readonly Dictionary<string, ICodecProvider> _providersByExtension = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<ICodecProvider> _ownedProviders = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<string, Lazy<ICodecProvider?>> _providerLoads = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<CodecProviderArtifact> _runtimeArtifacts = new();
    private int _disposed;

    private CodecProviderRuntime(string directory)
    {
        ProviderDirectory = directory;
        _index = CodecProviderIndex.LoadGenerated(directory);
        _backend = new CodecProviderDecoderBackend(lazyResolver: ResolveProvider);
    }

    public string ProviderDirectory { get; }
    public CodecProviderDecoderBackend Backend => _backend;
    public bool GeneratedIndexPresent => _index.GeneratedIndexPresent;
    public static CodecProviderRuntime Shared => SharedRuntime.Value;
    public static bool IsSharedCreated => SharedRuntime.IsValueCreated;

    /// <summary>
    /// Snapshot only. Indexed entries are intentionally reported as deferred, not ABI-valid, until
    /// their DLL is selected, hashed and loaded. Reading this property never loads a provider.
    /// </summary>
    public IReadOnlyList<CodecProviderArtifact> Artifacts
    {
        get
        {
            ThrowIfDisposed();
            lock (_gate)
            {
                var indexed = _index.Entries.Select(entry => new CodecProviderArtifact(
                    entry.Id, entry.Version, entry.License,
                    Path.Combine(ProviderDirectory, entry.DllFileName), entry.Sha256,
                    false, "Indexed; verification and native load deferred until first use.", entry.Info));
                return indexed.Concat(_runtimeArtifacts).ToArray();
            }
        }
    }

    public static CodecProviderRuntime CreateDefault()
        => new(Path.Combine(AppContext.BaseDirectory, "codecs"));

    /// <summary>
    /// Resolve exactly one requested suffix. This is the only normal-viewer path that may hash/load
    /// an optional codec DLL. It is never called for Glide's ten preserved core suffixes.
    /// </summary>
    public ICodecProvider? ResolveProvider(string path)
    {
        ThrowIfDisposed();
        var extension = CodecCapabilityRegistry.Match(path);
        if (extension is null || ImageFormatRegistry.IsCoreFastPath(extension)) return null;

        Lazy<ICodecProvider?> load;
        lock (_gate)
        {
            if (_providersByExtension.TryGetValue(extension, out var existing)) return existing;
            if (!_providerLoads.TryGetValue(extension, out load!))
            {
                load = new Lazy<ICodecProvider?>(() => ResolveProviderCore(extension), LazyThreadSafetyMode.ExecutionAndPublication);
                _providerLoads[extension] = load;
            }
        }
        return load.Value;
    }


    private ICodecProvider? ResolveProviderCore(string extension)
    {
        var candidates = _index.Find(extension).ToList();
        if (candidates.Count == 0)
            candidates.AddRange(DiscoverDeferredManifestCandidates(extension));

        foreach (var candidate in candidates)
        {
            var provider = TryLoadCandidate(candidate);
            if (provider is null) continue;
            lock (_gate)
            {
                _ownedProviders.Add(provider);
                foreach (var ext in provider.Info.Extensions.Select(NormalizeExtension).Where(CodecCapabilityRegistry.IsDeclared))
                    _providersByExtension[ext] = provider;
            }
            CodecCapabilityRegistry.RegisterExternalProvider(provider.Info.Id, provider.Info.Extensions);
            return provider;
        }
        return null;
    }

    /// <summary>
    /// Explicit exhaustive verification for diagnostics/developer workflows. Never call this from
    /// MainWindow construction or ordinary image open.
    /// </summary>
    public IReadOnlyList<CodecProviderArtifact> VerifyAllProviders()
    {
        ThrowIfDisposed();
        var artifacts = CodecProviderInventory.Discover(ProviderDirectory);
        lock (_gate) _runtimeArtifacts.AddRange(artifacts);
        return artifacts;
    }

    public async Task<ImageMetadata?> ReadMetadataAsync(string path, CancellationToken cancellationToken = default)
    { ThrowIfDisposed(); return await _backend.ReadMetadataAsync(path, cancellationToken).ConfigureAwait(false); }

    public CodecProbeResult? Probe(string path, CancellationToken cancellationToken = default)
    { ThrowIfDisposed(); return _backend.Probe(path, cancellationToken); }

    public async Task<IReadOnlyList<CodecFrameInfo>?> ReadFramesAsync(string path, CancellationToken cancellationToken = default)
    { ThrowIfDisposed(); return await _backend.ReadFramesAsync(path, cancellationToken).ConfigureAwait(false); }

    private IEnumerable<CodecProviderIndexEntry> DiscoverDeferredManifestCandidates(string extension)
    {
        if (!Directory.Exists(ProviderDirectory)) yield break;
        IEnumerable<string> manifests;
        try { manifests = Directory.EnumerateFiles(ProviderDirectory, "*.glidecodec.json", SearchOption.TopDirectoryOnly).ToArray(); }
        catch { yield break; }

        foreach (var manifest in manifests)
        {
            if (!CodecProviderInventory.TryReadManifestMetadata(manifest, out var info, out var hash, out var dllPath, out _)) continue;
            if (!info.Extensions.Select(NormalizeExtension).Contains(extension, StringComparer.OrdinalIgnoreCase)) continue;
            yield return new CodecProviderIndexEntry(info.Id, info.Version, info.License,
                Path.GetFileName(dllPath), Path.GetFileName(manifest), hash, info);
        }
    }

    private ICodecProvider? TryLoadCandidate(CodecProviderIndexEntry candidate)
    {
        var safeDllName = Path.GetFileName(candidate.DllFileName);
        var dllPath = Path.Combine(ProviderDirectory, safeDllName);
        if (!File.Exists(dllPath))
        {
            Record(candidate, dllPath, "", false, "Indexed provider DLL is missing.");
            return null;
        }

        string hash;
        try { hash = CodecProviderInventory.ComputeSha256(dllPath); }
        catch (Exception ex)
        {
            Record(candidate, dllPath, "", false, "Selected provider hash failed: " + ex.Message);
            return null;
        }
        if (!string.Equals(hash, candidate.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            Record(candidate, dllPath, hash, false, "Selected provider hash does not match the generated index.");
            return null;
        }
        if (!CodecProviderInventory.TryReadManifest(dllPath, hash, out var manifestInfo, out var reason))
        {
            Record(candidate, dllPath, hash, false, reason);
            return null;
        }
        if (!Equivalent(manifestInfo, candidate.Info))
        {
            Record(candidate, dllPath, hash, false, "Generated index does not match the signed provider manifest.");
            return null;
        }
        if (!NativeCodecProvider.TryLoad(dllPath, manifestInfo, out var provider, out reason) || provider is null)
        {
            Record(candidate, dllPath, hash, false, reason);
            return null;
        }
        Record(candidate, dllPath, hash, true, reason);
        return provider;
    }

    private void Record(CodecProviderIndexEntry candidate, string path, string hash, bool valid, string reason)
    {
        lock (_gate) _runtimeArtifacts.Add(new CodecProviderArtifact(candidate.Id, candidate.Version, candidate.License, path, hash, valid, reason, candidate.Info));
    }

    private static bool Equivalent(CodecProviderInfo left, CodecProviderInfo right) =>
        left.AbiVersion == right.AbiVersion && left.StructSize == right.StructSize &&
        string.Equals(left.Id, right.Id, StringComparison.Ordinal) && string.Equals(left.Version, right.Version, StringComparison.Ordinal) &&
        string.Equals(left.License, right.License, StringComparison.Ordinal) && left.Preview == right.Preview && left.Full == right.Full &&
        left.Metadata == right.Metadata && left.Frames == right.Frames &&
        left.Extensions.Select(NormalizeExtension).OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
            .SequenceEqual(right.Extensions.Select(NormalizeExtension).OrderBy(x => x, StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase);

    private static string NormalizeExtension(string extension) => extension.StartsWith('.') ? extension.ToLowerInvariant() : "." + extension.ToLowerInvariant();
    private void ThrowIfDisposed() { if (Volatile.Read(ref _disposed) != 0) throw new ObjectDisposedException(nameof(CodecProviderRuntime)); }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _backend.Dispose();
        lock (_gate)
        {
            foreach (var provider in _ownedProviders) provider.Dispose();
            _ownedProviders.Clear();
            _providersByExtension.Clear();
            _providerLoads.Clear();
        }
        CodecCapabilityRegistry.ClearExternalProviders();
    }
}
