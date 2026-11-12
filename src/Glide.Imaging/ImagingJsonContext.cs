using System.Text.Json.Serialization;

namespace Glide.Imaging;

/// <summary>Compile-time JSON metadata for native codec provider payloads (NativeAOT has no reflection serializer).</summary>
[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(ImageMetadata))]
[JsonSerializable(typeof(IReadOnlyList<CodecFrameInfo>))]
internal sealed partial class ImagingJsonContext : JsonSerializerContext;
