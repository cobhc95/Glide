using System.Security.Cryptography;
using System.Text.Json;

namespace Glide.Imaging;

public sealed record CodecProviderArtifact(string Id, string Version, string License, string Path, string Sha256, bool AbiValid, string Reason, CodecProviderInfo? Info = null);

/// <summary>Discovery and verification for decoder-only providers. Invalid entries are isolated.</summary>
public static class CodecProviderInventory
{
    public static IReadOnlyList<CodecProviderArtifact> Discover(string directory)
    {
        if (!Directory.Exists(directory)) return Array.Empty<CodecProviderArtifact>();
        var result = new List<CodecProviderArtifact>();
        foreach (var file in Directory.EnumerateFiles(directory, "*.dll", SearchOption.TopDirectoryOnly).OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                var hash = ComputeSha256(file);
                if (!TryReadManifest(file, hash, out var info, out var reason))
                {
                    result.Add(new CodecProviderArtifact(Path.GetFileNameWithoutExtension(file), "unknown", "unknown", file, hash, false, reason));
                    continue;
                }
                if (!NativeCodecProvider.TryLoad(file, info, out var provider, out reason) || provider is null)
                {
                    result.Add(new CodecProviderArtifact(info.Id, info.Version, info.License, file, hash, false, reason, info));
                    continue;
                }
                provider.Dispose();
                result.Add(new CodecProviderArtifact(info.Id, info.Version, info.License, file, hash, true, "Verified native exports and manifest/hash.", info));
            }
            catch (Exception ex) { result.Add(new CodecProviderArtifact(Path.GetFileNameWithoutExtension(file), "unknown", "unknown", file, "", false, "Provider inspection failed: " + ex.Message)); }
        }
        return result;
    }

    internal static IReadOnlyList<NativeCodecProvider> LoadVerified(string directory, Action<CodecProviderArtifact>? report = null)
    {
        var loaded = new List<NativeCodecProvider>();
        IEnumerable<string> files = Directory.Exists(directory)
            ? Directory.EnumerateFiles(directory, "*.dll", SearchOption.TopDirectoryOnly).OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
            : Array.Empty<string>();
        foreach (var file in files)
        {
            var hash = SafeHash(file);
            if (!TryReadManifest(file, hash, out var info, out var reason))
            {
                report?.Invoke(new CodecProviderArtifact(Path.GetFileNameWithoutExtension(file), "unknown", "unknown", file, hash, false, reason));
                continue;
            }
            if (!NativeCodecProvider.TryLoad(file, info, out var provider, out reason) || provider is null)
            {
                report?.Invoke(new CodecProviderArtifact(info.Id, info.Version, info.License, file, hash, false, reason, info));
                continue;
            }
            loaded.Add(provider);
            report?.Invoke(new CodecProviderArtifact(info.Id, info.Version, info.License, file, hash, true, "Loaded and verified native provider.", info));
        }
        return loaded;
    }

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
            dllPath = Path.ChangeExtension(manifestPath, ".dll");
            if (!File.Exists(dllPath)) { reason = "Provider DLL matching the manifest is missing."; return false; }
            reason = "Manifest metadata is structurally valid; DLL hash/load remains deferred.";
            return true;
        }
        catch (Exception ex) { reason = "Malformed provider manifest: " + ex.Message; return false; }
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
        using var stream = File.OpenRead(path); using var sha = SHA256.Create();
        return Convert.ToHexString(sha.ComputeHash(stream)).ToLowerInvariant();
    }
    private static string SafeHash(string path) { try { return ComputeSha256(path); } catch { return ""; } }
}
