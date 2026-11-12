using System.Text.Json;
using System.Text.Json.Serialization;
using Glide.Imaging;

namespace Glide.Diagnostics;

/// <summary>Compile-time JSON metadata for diagnostic exports (NativeAOT has no reflection serializer).</summary>
[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(DiagnosticSnapshot))]
[JsonSerializable(typeof(IReadOnlyList<DiagnosticCheck>))]
[JsonSerializable(typeof(IReadOnlyList<CodecProviderArtifact>))]
internal sealed partial class DiagnosticsJsonContext : JsonSerializerContext
{
    /// <summary>For building reports out of JsonNode values.</summary>
    internal static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };
}
