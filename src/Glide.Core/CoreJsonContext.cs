using System.Text.Json.Serialization;

namespace Glide.Core;

/// <summary>On-disk overlay layout. Property names are the persisted JSON names.</summary>
internal sealed record OverlayLayoutDocument(int version, OverlayState[] overlays);

/// <summary>Compile-time JSON metadata: Glide ships as NativeAOT, which has no reflection-based serializer.</summary>
[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(OverlayLayoutDocument))]
[JsonSerializable(typeof(OverlayState[]))]
internal sealed partial class CoreJsonContext : JsonSerializerContext;
