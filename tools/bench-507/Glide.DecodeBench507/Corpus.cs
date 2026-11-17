using System.Globalization;
using SkiaSharp;

namespace Glide.DecodeBench507;

internal sealed record CorpusFile(string Path, string Set, string Format, string Variant, int Width, int Height, long Bytes)
{
    public double Megapixels => Width > 0 && Height > 0 ? Width * (double)Height / 1e6 : double.NaN;
    public string Ext => DecodeBench507.Ext.Of(Path);
}

/// <summary>
/// Loads a corpus. Both sets carry a manifest.csv with a "file" column (path relative to the manifest):
///   synthetic (tools\bench-507\generate_synthetic.py): file,format,variant,content,width,height,bytes,seed
///   real      (Glide-bench-corpus): file,format,bytes,width,height,bit_depth_colour,real_or_converted,note,source_url
/// Without a manifest the folder is scanned and sizes come from a header probe (Skia, else unknown).
/// </summary>
internal static class Corpus
{
    public static List<CorpusFile> Load(string spec, int maxPerFormat, string? formatFilter, string? variantFilter)
    {
        // spec = "synthetic:<dir>" | "real:<dir>" | "<dir>"
        var set = "corpus";
        var dir = spec;
        var colon = spec.IndexOf(':');
        if (colon > 1) { set = spec[..colon]; dir = spec[(colon + 1)..]; }
        dir = Path.GetFullPath(dir);
        var manifest = Path.Combine(dir, "manifest.csv");
        var files = new List<CorpusFile>();
        if (File.Exists(manifest))
        {
            foreach (var row in Csv.Read(manifest))
            {
                var rel = row.GetValueOrDefault("file") ?? row.GetValueOrDefault("path") ?? row.GetValueOrDefault("relative_path") ?? "";
                if (rel.Length == 0) continue;
                var full = Path.IsPathRooted(rel) ? rel : Path.Combine(dir, rel);
                if (!File.Exists(full)) continue;
                var fmt = (row.GetValueOrDefault("format") is { Length: > 0 } f ? f : Ext.Of(full).TrimStart('.')).ToLowerInvariant();
                var variant = row.GetValueOrDefault("variant") is { Length: > 0 } v ? v
                    : row.GetValueOrDefault("real_or_converted") is { Length: > 0 } rc ? rc : "real";
                int.TryParse(row.GetValueOrDefault("width"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var w);
                int.TryParse(row.GetValueOrDefault("height"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var h);
                files.Add(new CorpusFile(full, set, fmt, variant, w, h, new FileInfo(full).Length));
            }
        }
        else
        {
            foreach (var full in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
            {
                var ext = Ext.Of(full);
                if (ext is ".csv" or ".txt" or ".json" or ".md" or ".tmp") continue;
                var (w, h) = Probe(full);
                files.Add(new CorpusFile(full, set, ext.TrimStart('.'), "scan", w, h, new FileInfo(full).Length));
            }
        }
        if (formatFilter is not null)
        {
            var wanted = formatFilter.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            files = files.Where(f => wanted.Any(x => f.Format.Equals(x.TrimStart('.'), StringComparison.OrdinalIgnoreCase))).ToList();
        }
        if (variantFilter is not null) // comma list: keep files whose variant contains any entry
        {
            var parts = variantFilter.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            files = files.Where(f => parts.Any(p => f.Variant.Contains(p, StringComparison.OrdinalIgnoreCase))).ToList();
        }
        if (maxPerFormat > 0)
        {
            // Deterministic spread per format: smallest, largest, then evenly spaced by size.
            files = files.GroupBy(f => f.Format).SelectMany(g =>
            {
                var sorted = g.OrderBy(f => f.Bytes).ThenBy(f => f.Path, StringComparer.OrdinalIgnoreCase).ToList();
                if (sorted.Count <= maxPerFormat) return sorted;
                var pick = new SortedSet<int>();
                for (var i = 0; i < maxPerFormat; i++) pick.Add((int)Math.Round(i * (sorted.Count - 1) / (double)(maxPerFormat - 1)));
                return pick.Select(i => sorted[i]);
            }).ToList();
        }
        return files.OrderBy(f => f.Format).ThenBy(f => f.Megapixels).ThenBy(f => f.Path, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static (int, int) Probe(string path)
    {
        try { using var c = SKCodec.Create(path); return c is null ? (0, 0) : (c.Info.Width, c.Info.Height); }
        catch { return (0, 0); }
    }
}
