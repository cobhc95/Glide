using System.Text.Json;
using System.Text.Json.Serialization;
using Glide.App.Platform;
using Glide.App.Printing;
using Glide.App.Settings;
using Glide.Imaging;

namespace Glide.App;

/// <summary>
/// Compile-time JSON metadata for everything Glide persists. Glide ships as NativeAOT, which has no
/// reflection-based serializer, so a type missing from this list cannot be saved or loaded.
/// </summary>
[JsonSourceGenerationOptions(WriteIndented = true, PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(GlideSettingsState))]
[JsonSerializable(typeof(SettingsStore.LaunchPolicyCache))]
[JsonSerializable(typeof(ProfileStore.ProfileDocument))]
[JsonSerializable(typeof(RecentHistoryStore.Document))]
[JsonSerializable(typeof(WorkspaceSessionStore.SessionDocument))]
[JsonSerializable(typeof(ThumbnailSettings))]
[JsonSerializable(typeof(PrintDocumentSettings))]
[JsonSerializable(typeof(SettingsWindow.SettingsTransferDocument))]
[JsonSerializable(typeof(SettingsWindow.ProfileTransferDocument))]
[JsonSerializable(typeof(IReadOnlyList<CodecProviderArtifact>))]
internal sealed partial class AppJsonContext : JsonSerializerContext
{
    /// <summary>For building diagnostic reports out of JsonNode values.</summary>
    internal static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };
}
