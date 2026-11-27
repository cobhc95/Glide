using System.Collections.Concurrent;
using Glide.Core;

namespace Glide.Imaging;

/// <summary>
/// Warms the optional codec plug-in (glidecodecs.dll: HEIC, AVIF, JPEG XL, JPEG 2000) on a background thread as soon as a
/// folder listing shows one of those files (5.2.0). The first open of such a file used to pay the provider lookup, the
/// SHA-256 check of the 5.6 MB DLL, its load and its start-up itself, 63 - 106 ms more than the second open; now that work
/// happens while the user is still looking at the previous picture or the folder. Nothing is loaded for a folder without
/// those formats, and each suffix is resolved at most once per process.
/// </summary>
public static class CodecPreloader
{
    private static readonly ConcurrentDictionary<string, byte> Requested = new(StringComparer.OrdinalIgnoreCase);
    private static int s_resolved;

    /// <summary>Test seam: replaces the shared runtime's resolver (called on the background thread with a probe path).</summary>
    internal static Func<string, object?>? ResolverForTests { get; set; }

    /// <summary>Test seam: runs the warm-up inline instead of on a thread-pool thread.</summary>
    internal static bool RunInlineForTests { get; set; }

    /// <summary>How many warm-ups ran to completion in this process (diagnostics and tests).</summary>
    public static int ResolvedCount => Volatile.Read(ref s_resolved);

    /// <summary>True when the suffix of <paramref name="pathOrName"/> is served by an optional codec plug-in (not the SVG rasteriser).</summary>
    public static bool NeedsProvider(string? pathOrName)
    {
        if (string.IsNullOrWhiteSpace(pathOrName)) return false;
        var extension = ImageFormatRegistry.Match(pathOrName);
        return extension is not null && !ImageFormatRegistry.IsCoreFastPath(extension) &&
               extension is not (".svg" or ".svgz") &&
               CodecCapabilityRegistry.Describe(extension).Tier == CodecTier.ModularNative;
    }

    /// <summary>
    /// Looks at the names of a folder listing and starts the warm-up for each plug-in suffix found (one background task for
    /// all of them, run one after the other so the DLL is hashed and loaded once). Returns the suffixes newly requested.
    /// Cheap on a large folder: one extension match per name.
    /// </summary>
    public static IReadOnlyList<string> NotifyListing(IEnumerable<string> pathsOrNames)
    {
        List<string>? found = null;
        foreach (var name in pathsOrNames)
        {
            if (!NeedsProvider(name)) continue;
            var extension = ImageFormatRegistry.Match(name)!;
            if (!Requested.TryAdd(extension, 0)) continue;
            (found ??= new List<string>()).Add(extension);
        }
        if (found is null) return Array.Empty<string>();
        Start(found);
        return found;
    }

    private static readonly ConcurrentDictionary<string, byte> WarmedSamples = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Decodes a tiny built-in picture of the suffix's family once (a 32 x 16 file of 46 to 1601 bytes, written to a temporary
    /// file and deleted at once) so the plug-in's decoders and the managed hand-over to a bitmap are initialised before the
    /// first real file: loading the DLL alone left 20 - 80 ms of first-decode cost on the first open.
    /// </summary>
    private static void WarmDecode(string extension, ICodecProvider provider)
    {
        var sample = CodecWarmSamples.For(extension, out var sampleExtension);
        if (sample is null || !provider.Info.Preview || !WarmedSamples.TryAdd(sampleExtension, 0)) return;
        var temp = Path.Combine(Path.GetTempPath(), "glide-warm-" + Environment.ProcessId + "-" + Guid.NewGuid().ToString("N")[..8] + sampleExtension);
        try
        {
            File.WriteAllBytes(temp, sample);
            if (provider is ICodecSurfaceProvider surfaceProvider)
            {
                using var surface = surfaceProvider.DecodePreviewSurfaceAsync(temp, 16).GetAwaiter().GetResult();
                if (surface is not null)
                {
                    try { CodecSurfaceBitmapFactory.Create(surface).Dispose(); }
                    catch { /* no UI platform yet: the plug-in itself is warm */ }
                }
            }
            else
            {
                using var stream = provider.DecodePreviewAsync(temp, 16).GetAwaiter().GetResult();
            }
        }
        catch { /* best effort */ }
        finally
        {
            try { File.Delete(temp); } catch { /* a leftover 2 KB file in %TEMP% is harmless */ }
        }
    }

    /// <summary>Forgets what was requested (tests only).</summary>
    internal static void ResetForTests()
    {
        Requested.Clear();
        WarmedSamples.Clear();
        Volatile.Write(ref s_resolved, 0);
    }

    private static void Start(List<string> extensions)
    {
        void Work()
        {
            foreach (var extension in extensions)
            {
                try
                {
                    var probePath = "preload" + extension;
                    if (ResolverForTests is { } resolver) resolver(probePath);
                    else
                    {
                        var provider = CodecProviderRuntime.Shared.ResolveProvider(probePath);
                        if (provider is not null) WarmDecode(extension, provider);
                    }
                    Interlocked.Increment(ref s_resolved);
                }
                catch { /* the real open reports a missing or damaged plug-in */ }
            }
        }
        if (RunInlineForTests) Work();
        else _ = Task.Run(Work);
    }
}
