namespace Glide.Imaging.Encoders;

/// <summary>
/// JPEG 2000 and WebP writing through the bundled codec provider (codecs\glidecodecs.dll, ABI v2 encode
/// extension). The provider is resolved, hash-verified and loaded only when one of these formats is first needed
/// (building the Save as list or saving), exactly like opening a JPEG 2000 file does.
/// </summary>
internal static class CodecProviderEncoder
{
    private static readonly Lazy<NativeCodecProvider?> Provider = new(Resolve, LazyThreadSafetyMode.ExecutionAndPublication);
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, bool> Supported = new(StringComparer.Ordinal);

    private static NativeCodecProvider? Resolve()
    {
        try
        {
            // The bundled provider owns .jp2; routing by that suffix loads it through the normal verified path.
            return CodecProviderRuntime.Shared.ResolveProvider("glide-encoder-probe.jp2") as NativeCodecProvider;
        }
        catch { return null; }
    }

    public static bool IsAvailable(string codecName)
        => Supported.GetOrAdd(codecName, name => Provider.Value?.CanEncode(name) == true);

    public static bool TryEncode(string codecName, StraightImage image, string path, float quality)
        => IsAvailable(codecName) && Provider.Value!.Encode(path, image.Bgra, image.Width, image.Height, codecName, quality);
}
