using System.Text.Json;

namespace Glide.Imaging;

/// <summary>
/// Tiny immutable routing index for optional codec providers. The generated JSON is cheap to parse
/// and contains no native code. Crucially this object is itself only constructed on the first
/// non-core file that needs an optional provider; ordinary Glide startup never touches it.
/// </summary>
public sealed record CodecProviderIndexEntry(
    string Id,
    string Version,
    string License,
    string DllFileName,
    string ManifestFileName,
    string Sha256,
    CodecProviderInfo Info);

public sealed class CodecProviderIndex
{
    public const int SchemaVersion = 1;
    public const string FileName = "codecs.index.json";

    private readonly Dictionary<string, List<CodecProviderIndexEntry>> _byExtension = new(StringComparer.OrdinalIgnoreCase);

    private CodecProviderIndex(string directory, bool generatedIndexPresent, IReadOnlyList<CodecProviderIndexEntry> entries)
    {
        Directory = directory;
        GeneratedIndexPresent = generatedIndexPresent;
        Entries = entries;
        foreach (var entry in entries)
        {
            foreach (var extension in entry.Info.Extensions)
            {
                var normalized = Normalize(extension);
                if (!_byExtension.TryGetValue(normalized, out var list))
                    _byExtension[normalized] = list = new List<CodecProviderIndexEntry>();
                list.Add(entry);
            }
        }
    }

    public string Directory { get; }
    public bool GeneratedIndexPresent { get; }
    public IReadOnlyList<CodecProviderIndexEntry> Entries { get; }

    public IReadOnlyList<CodecProviderIndexEntry> Find(string extensionOrPath)
    {
        var extension = Glide.Core.CodecCapabilityRegistry.Match(extensionOrPath) ?? Normalize(extensionOrPath);
        return _byExtension.TryGetValue(extension, out var list) ? list : Array.Empty<CodecProviderIndexEntry>();
    }

    public static CodecProviderIndex LoadGenerated(string directory)
    {
        var path = Path.Combine(directory, FileName);
        if (!File.Exists(path)) return new CodecProviderIndex(directory, false, Array.Empty<CodecProviderIndexEntry>());
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var root = document.RootElement;
            if (!root.TryGetProperty("schemaVersion", out var schema) || schema.GetInt32() != SchemaVersion)
                return new CodecProviderIndex(directory, true, Array.Empty<CodecProviderIndexEntry>());
            if (!root.TryGetProperty("providers", out var providers) || providers.ValueKind != JsonValueKind.Array)
                return new CodecProviderIndex(directory, true, Array.Empty<CodecProviderIndexEntry>());

            var entries = new List<CodecProviderIndexEntry>();
            foreach (var item in providers.EnumerateArray())
            {
                if (!TryReadEntry(item, out var entry)) continue;
                if (!CodecProviderAbi.IsSafe(entry.Info)) continue;
                if (entry.Info.Extensions.Any(x => !Glide.Core.CodecCapabilityRegistry.IsDeclared(x))) continue;
                entries.Add(entry);
            }
            return new CodecProviderIndex(directory, true, entries);
        }
        catch
        {
            // A corrupt optional index must never prevent Glide from launching. First-use manifest
            // discovery remains available through CodecProviderRuntime as a developer-build fallback.
            return new CodecProviderIndex(directory, true, Array.Empty<CodecProviderIndexEntry>());
        }
    }

    private static bool TryReadEntry(JsonElement item, out CodecProviderIndexEntry entry)
    {
        entry = default!;
        try
        {
            var extensions = item.GetProperty("extensions").EnumerateArray()
                .Select(x => Normalize(x.GetString() ?? ""))
                .Where(x => x.Length > 1)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var abi = item.GetProperty("abiVersion").GetUInt32();
            var structSize = item.GetProperty("structSize").GetUInt32();
            var id = item.GetProperty("id").GetString() ?? "";
            var version = item.GetProperty("version").GetString() ?? "";
            var license = item.GetProperty("license").GetString() ?? "";
            var info = new CodecProviderInfo(abi, structSize, id, version, license, extensions,
                item.GetProperty("preview").GetBoolean(), item.GetProperty("full").GetBoolean(),
                item.GetProperty("metadata").GetBoolean(), item.GetProperty("frames").GetBoolean());
            entry = new CodecProviderIndexEntry(id, version, license,
                item.GetProperty("dll").GetString() ?? "",
                item.GetProperty("manifest").GetString() ?? "",
                (item.GetProperty("sha256").GetString() ?? "").ToLowerInvariant(), info);
            return !string.IsNullOrWhiteSpace(entry.DllFileName) && entry.Sha256.Length == 64;
        }
        catch { return false; }
    }

    private static string Normalize(string extension) => extension.StartsWith('.') ? extension.ToLowerInvariant() : "." + extension.ToLowerInvariant();
}
