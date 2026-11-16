using System.Security.Cryptography;
using System.Text.Json;

namespace Glide.Imaging;

public sealed record CodecProviderArtifact(string Id, string Version, string License, string Path, string Sha256, bool AbiValid, string Reason, CodecProviderInfo? Info = null);

/// <summary>Manifest reading and SHA-256 verification for decoder-only providers (loading lives in CodecProviderRuntime).</summary>
public static class CodecProviderInventory
{
    /// <summary>
    /// Reads only manifest metadata and intentionally does not hash or load the DLL. Used solely on
    /// first use when a developer build has no generated index. This must never run on app startup.
    /// </summary>
    internal static bool TryReadManifestMetadata(string manifestPath, out CodecProviderInfo info, out string declaredHash, out string dllPath, out string reason)
    {
        info = default;
        declaredHash = "";
        dllPath = "";
        reason = "Malformed provider manifest.";
        try
        {
            if (!File.Exists(manifestPath)) { reason = "Provider manifest missing."; return false; }
            using var doc = JsonDocument.Parse(File.ReadAllText(manifestPath));
            var root = doc.RootElement;
            declaredHash = (root.TryGetProperty("sha256", out var hash) ? hash.GetString() : null)?.ToLowerInvariant() ?? "";
            if (declaredHash.Length != 64) { reason = "Provider manifest SHA-256 is missing or malformed."; return false; }
            var extensions = root.GetProperty("extensions").EnumerateArray().Select(x => x.GetString() ?? "").ToArray();
            info = new CodecProviderInfo(root.GetProperty("abiVersion").GetUInt32(), root.GetProperty("structSize").GetUInt32(), root.GetProperty("id").GetString() ?? "", root.GetProperty("version").GetString() ?? "", root.GetProperty("license").GetString() ?? "", extensions,
                root.GetProperty("preview").GetBoolean(), root.GetProperty("full").GetBoolean(), root.GetProperty("metadata").GetBoolean(), root.GetProperty("frames").GetBoolean());
            if (!CodecProviderAbi.IsSafe(info)) { reason = "Manifest ABI/version/size/license/capability validation failed."; return false; }
            dllPath = DllPathForManifest(manifestPath);
            if (!File.Exists(dllPath)) { reason = "Provider DLL matching the manifest is missing."; return false; }
            reason = "Manifest metadata is structurally valid; DLL hash/load remains deferred.";
            return true;
        }
        catch (Exception ex) { reason = "Malformed provider manifest: " + ex.Message; return false; }
    }

    /// <summary>
    /// Pairs <c>name.glidecodec.json</c> with <c>name.dll</c> (the inverse of
    /// <c>Path.ChangeExtension(dll, ".glidecodec.json")</c>). A plain ChangeExtension on the manifest
    /// would only strip ".json" and look for <c>name.glidecodec.dll</c>, so no provider could ever match.
    /// </summary>
    internal static string DllPathForManifest(string manifestPath)
    {
        const string suffix = ".glidecodec.json";
        return manifestPath.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)
            ? manifestPath[..^suffix.Length] + ".dll"
            : Path.ChangeExtension(manifestPath, ".dll");
    }

    public static bool TryReadManifest(string dllPath, string sha256, out CodecProviderInfo info, out string reason)
    {
        info = default; reason = "No provider manifest; ignored.";
        var manifestPath = Path.ChangeExtension(dllPath, ".glidecodec.json");
        if (!TryReadManifestMetadata(manifestPath, out info, out var declaredHash, out _, out reason)) return false;
        if (!string.Equals(declaredHash, sha256, StringComparison.OrdinalIgnoreCase))
        { reason = "Provider SHA-256 does not match the manifest."; return false; }
        return true;
    }

    public static string ComputeSha256(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 64 * 1024, FileOptions.SequentialScan);
        using var sha = SHA256.Create();
        return Convert.ToHexString(sha.ComputeHash(stream)).ToLowerInvariant();
    }
}
