using Glide.Core;

namespace Glide.Diagnostics;

/// <summary>
/// Deterministic real encoded fixtures used by diagnostics to prove every currently advertised
/// registered extension is actually exercised. Aliases receive separate files with the same valid codec payload.
/// </summary>
public static class ImageDiagnosticFixtures
{
    private static readonly IReadOnlyDictionary<string, string> Base64ByCodec = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["jpg"] = "/9j/4AAQSkZJRgABAQAAAQABAAD/2wBDAAYEBAUEBAYFBQUGBgYHCQ4JCQgICRINDQoOFRIWFhUSFBQXGiEcFxgfGRQUHScdHyIjJSUlFhwpLCgkKyEkJST/2wBDAQYGBgkICREJCREkGBQYJCQkJCQkJCQkJCQkJCQkJCQkJCQkJCQkJCQkJCQkJCQkJCQkJCQkJCQkJCQkJCQkJCT/wAARCAASABgDASIAAhEBAxEB/8QAHwAAAQUBAQEBAQEAAAAAAAAAAAECAwQFBgcICQoL/8QAtRAAAgEDAwIEAwUFBAQAAAF9AQIDAAQRBRIhMUEGE1FhByJxFDKBkaEII0KxwRVS0fAkM2JyggkKFhcYGRolJicoKSo0NTY3ODk6Q0RFRkdISUpTVFVWV1hZWmNkZWZnaGlqc3R1dnd4eXqDhIWGh4iJipKTlJWWl5iZmqKjpKWmp6ipqrKztLW2t7i5usLDxMXGx8jJytLT1NXW19jZ2uHi4+Tl5ufo6erx8vP09fb3+Pn6/8QAHwEAAwEBAQEBAQEBAQAAAAAAAAECAwQFBgcICQoL/8QAtREAAgECBAQDBAcFBAQAAQJ3AAECAxEEBSExBhJBUQdhcRMiMoEIFEKRobHBCSMzUvAVYnLRChYkNOEl8RcYGRomJygpKjU2Nzg5OkNERUZHSElKU1RVVldYWVpjZGVmZ2hpanN0dXZ3eHl6goOEhYaHiImKkpOUlZaXmJmaoqOkpaanqKmqsrO0tba3uLm6wsPExcbHyMnK0tPU1dbX2Nna4uPk5ebn6Onq8vP09fb3+Pn6/9oADAMBAAIRAxEAPwDxzS/BvT93+lddpfg3p+7/AEr0fS/BvT93+ldbp/hBYk3ugVR1JFduCxUYR5pOyW7PAyLi/b3jzfT/AAgsSb3QKoHJIor1mDw01264jKxjouP1NFfNY/xMqwquGXxi4LrJPXzVmrLt16+R+zZdxKnRXPLU+SNLjTj5F/KtudFxANowcnGPpRRX7LxB/wAiev6L/wBKR8hw1/EibWlxpx8i/lRRRX5VQ+A/bcF/CR//2Q==",
        ["png"] = "iVBORw0KGgoAAAANSUhEUgAAABgAAAASCAYAAABB7B6eAAAAT0lEQVR4nO2RsQkAMQwDL5BGpMpcXve931cGD2CTxoUaFSfQLeA7yA4ixau6zZWBSHGo6za3Dw7yPFAOB1kMtMDjojZ4DIzkkTySR/JryT8wL2eKcZtxEQAAAABJRU5ErkJggg==",
        ["bmp"] = "Qk1GBQAAAAAAADYAAAAoAAAAGAAAABIAAAABABgAAAAAABAFAADEDgAAxA4AAAAAAAAAAAAAmSEAoiELqyEWtCEhvSEsxiE3zyFC2CFN4SFY6iFj8yFu/CF5BSGEDiGPFyGaICGlKSGwMiG7OyHGRCHRTSHcViHnXyHyaCH9kBAAmRALohAWqxAhtBAsvRA3xhBCzxBN2BBY4RBj6hBu8xB5/BCEBRCPDhCaFxClIBCwKRC7MhDGOxDRRBDcTRDnVhDyXxD9h/8AkP8Lmf8Wov8hq/8stP83vf9Cxv9Nz/9Y2P9j4f9u6v958/+E/P+PBf+aDv+lF/+wIP+7Kf/GMv/RO//cRP/nTf/yVv/9fu4Ah+4LkO4Wme4hou4sq+43tO5Cve5Nxu5Yz+5j2O5u4e556u6E8+6P/O6aBe6lDu6wF+67IO7GKe7RMu7cO+7nRO7yTe79dd0Aft0Lh90WkN0hmd0sot03q91CtN1Nvd1Yxt1jz91u2N154d2E6t2P892a/N2lBd2wDt27F93GIN3RKd3cMt3nO93yRN39bMwAdcwLfswWh8whkMwsmcw3osxCq8xNtMxYvcxjxsxuz8x52MyE4cyP6sya88yl/MywBcy7DszGF8zRIMzcKcznMszyO8z9Y7sAbLsLdbsWfrshh7sskLs3mbtCortNq7tYtLtjvbtuxrt5z7uE2LuP4bua6rul87uw/Lu7BbvGDrvRF7vcILvnKbvyMrv9WqoAY6oLbKoWdaohfqosh6o3kKpCmapNoqpYq6pjtKpuvap5xqqEz6qP2Kqa4aql6qqw86q7/KrGBarRDqrcF6rnIKryKar9UZkAWpkLY5kWbJkhdZksfpk3h5lCkJlNmZlYopljq5lutJl5vZmExpmPz5ma2Jml4Zmw6pm785nG/JnRBZncDpnnF5nyIJn9SIgAUYgLWogWY4ghbIgsdYg3fohCh4hNkIhYmYhjoohuq4h5tIiEvYiPxoiaz4il2Iiw4Yi76ojG84jR/IjcBYjnDojyF4j9P3cASHcLUXcWWnchY3csbHc3dXdCfndNh3dYkHdjmXduond5q3eEtHePvXeaxnelz3ew2He74XfG6nfR83fc/HfnBXfyDnf9NmYAP2YLSGYWUWYhWmYsY2Y3bGZCdWZNfmZYh2ZjkGZumWZ5omaEq2aPtGaavWalxmawz2a72GbG4WbR6mbc82bn/GbyBWb9LVUANlULP1UWSFUhUVUsWlU3Y1VCbFVNdVVYflVjh1VukFV5mVWEolWPq1WatFWlvVWwxlW7z1XG2FXR4VXc6lXn81Xy/FX9JEQALUQLNkQWP0QhSEQsUUQ3WkRCY0RNbERYdURjfkRuh0R5kESEmUSPokSaq0SltESwvUS7xkTGz0TR2ETc4UTn6kTy80T9GzMAJDMLLTMWNjMhPzMsSDM3UTNCWjNNYzNYbDNjdTNufjN5hzOEkDOPmTOaojOlqzOwtDO7vTPGxjPRzzPc2DPn4TPy6jP9EiIAGyILJCIWLSIhNiIsPyI3SCJCUSJNWiJYYyJjbCJudSJ5fiKEhyKPkCKamSKloiKwqyK7tCLGvSLRxiLczyLn2CLy4SL9CREAEhELGxEWJBEhLREsNhE3PxFCSBFNURFYWhFjYxFubBF5dRGEfhGPhxGakBGlmRGwohG7qxHGtBHRvRHcxhHnzxHy2BH9AAAACQALEgAWGwAhJAAsLQA3NgBCPwBNSABYUQBjWgBuYwB5bACEdQCPfgCahwClkACwmQC7ogDGqwDRtADcvQDnxgDyzwD9",
        ["gif"] = "R0lGODdhGAASAIcAAOefEeTgMrXpENZd4eGhDtbUILCZ4bDuDtERtNQcveot2Nhr6qpdvbCZ4csYO/cq4fduBaVVtLtVxqXd/MYQMv0haNwQRP13Dsaq/P3dRAsREgu7bAvMdSyIbCyZdU0Qz01VbE1mdW4ibG4h824zdW7dz48AdY+qz4/u87AhKbB3z7C789FEz9GI89HuKfIQVvIRz/JV8/K7KfL/TRAADQUQlAUiFhAhpgtELRBVOgVmOhB3TAWITBCqZwXdeRD/lCwRLSwQtDEiOiYzOjFETCZVTDF3ZyaqeTHMlCbdlDHupib/pkcAOlIRTEciTEch01JEZ0d3eVKZlEeqlFK7pkfMpk3dtFL/ynMRZ3MQ7mhEeXNmlGh3lHOIpmiZpm6qtHPMymju04kReY8hDpQzlIlElJRVpolmpo93tJSZyom705Td7on/97UAlLUQJKoRlLUipqozprBEtLVmyqqI07Wq7qrM97DuDssAptERtNYhSNYzystV09Z37suZ99G7DtbdJMv/LfcAyuwhWuwi0/dE7uxm9/KIDveqJOzMLffuSAAAABYQohYiJAAhmQAzGxYzLQszJAtELQBVLRZmSAB3PxaIWguZWgCZURaZYwCqWhbdhwDufgvuhxbukAD/hyEAGzcALSwAJCwRLSwQtCEiLSEhtCwhvTchxjczSCFEPzdVWixmWiFmUTdmYyF3WjeqhyG7fiy7hze7kCHMhzfdoiHumTf/tFgASEIRP1giWlgh4U0zWkIzUVgzY0JEWlh3h0KIfk2Ih1iIkEKZh1iqokK7mVjMtE3dtELutE3uvVjuxkL/vW4AWmMAUXkAY2MRWmMQ4XlEh2NVfm5Vh3lVkGNmh3l3omOImXmZtG6qtGO7tG67vXm7xmPMvXnu4WP/2G7/4Xn/6poQDo8QBZoRh4QQ/I8hDoQifo8ih5oikIQzh5pEooRVmZpmtI93tISItI+IvZqIxoSZvZq74YTM2I/M4ZrM6oTd4Zr/BaUAh6UQF7sRoqUimbsztLBEtCwAAAAAGAASAEAI/wAX0aABSlQoJkxwOWv2zIRDfW3a4MGDIIEgQTx4WOrAMZiwYdi6dIEXTx4dOgZaqDxE4FCNGoyCkAryoWa0LFnMkRu3z40bCg4svBiKQxIOVUSI/IICRYsWaWXKsJPzTw6LqwMKFeLAlRYSJFWqHPMGBkw9e/fs2BEQoECBRIkC2LDRqJQQIU6c6BLBF106df3gwKmogBChBzp0UGrFylWIx9a2bDlzxh2DOXMWDFhgyBAETT16HDkCa8qUYl+0fTnB2kCdOhgAEACACBGnTp5sKVGSTNmyMGG+oRh+58AdF8gDKFKkoTmQUUByNWkCDQsWMWLKvXnDLw+CPDDCP92KBGnIkFS9ePkiwX4dGTJx4vhLsGePggcKJuXIUaTIKhAATkNNNe2YYUYEDEjABx8DxOBgJTvs8IoRRkQRBTBccHENGu+gocKHA/TRxwIQXIDJJZl4oCIxUkjhhRfZzJNGGg0Y0IAffsgGAAAb9BiLLLMYQwUV23DTjRpq0LPCkn8Q8IcMUPrgwyZJJFGLFchYUcKW+KyxxgQCHFAAIIAUEEAGn/zwwxJL3MLMFVeAE444bLCRzwECCBBIIAHM4KcjN9xgyimoPPHELiMkOsY5Y6TgqAN66DHIIBUEBAA7",
        ["tif"] = "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",
        ["webp"] = "UklGRlgBAABXRUJQVlA4WAoAAAAQAAAAFwAAEQAAQUxQSB4AAAABD/Al2oiIQBZgAj62qaTQmUMEEf2vQmjdAtXlJQBWUDggFAEAAJAHAJ0BKhgAEgA+bS6VRqQioiEoCqiADYlsN0nqs7FfaHGABvIsMHl4x+Jv63cwZqz2gtAEWAbwd+1QKcbW4a6U89vzwAD+/ndbo4TatzuiTVIRXcWMaUBlxwOmMbmcn5cPFiNpa7xWHmd93mAXi7zX7a7TiDR7E20epp45hP6wsfRLRxyYZ9qAh0LkqOfKUj8LV/hv+Z5uILXeKw4EkGTP8YFv+DbI6DY9qab9Yp9d+M1aqcv29egu3+gJ//qg+7/5jGTAi1E3Y+eGnTIbkchYVtBM292JGL47/60Rfid/cI822/+aIvxO/uEwY6sCygvFYFuCLdmUnfJ4RSwp9FGnhvu2k0+TJhaTT5MAMLxnqwAAAA==",
        ["ico"] = "AAABAAEAGBIAAAAAIACIAAAAFgAAAIlQTkcNChoKAAAADUlIRFIAAAAYAAAAEggGAAAAQewengAAAE9JREFUeJztkbEJADEMAy+QRqTKXF73vd9XBg9gk8aFGhUn0C3gO8gOIsWrus2VgUhxqOs2tw8O8jxQDgdZDLTA46I2eAyM5JE8kkfya8k/MC9ninGbcREAAAAASUVORK5CYII=",
    };

    private static readonly IReadOnlyDictionary<string, string> CodecByExtension = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        [".jpg"] = "jpg",
        [".jpeg"] = "jpg",
        [".jpe"] = "jpg",
        [".png"] = "png",
        [".bmp"] = "bmp",
        [".gif"] = "gif",
        [".tif"] = "tif",
        [".tiff"] = "tif",
        [".webp"] = "webp",
        [".ico"] = "ico",
    };

    public static IReadOnlyList<string> Extensions => ImageFormatRegistry.CoreFastPathExtensions;

    public static IReadOnlyDictionary<string, byte[]> CreatePayloads()
    {
        var result = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        foreach (var extension in Extensions)
        {
            if (!CodecByExtension.TryGetValue(extension, out var codec) || !Base64ByCodec.TryGetValue(codec, out var base64))
                throw new InvalidOperationException($"Missing diagnostic fixture payload for {extension}.");
            result[extension] = Convert.FromBase64String(base64);
        }
        return result;
    }

    public static IReadOnlyList<string> Validate()
    {
        var issues = new List<string>();
        var payloads = CreatePayloads();
        if (payloads.Count != Extensions.Count)
            issues.Add($"Fixture count {payloads.Count} does not match core fast-path count {Extensions.Count}.");
        foreach (var ext in Extensions)
        {
            if (!payloads.TryGetValue(ext, out var bytes) || bytes.Length < 32)
            {
                issues.Add($"{ext}: missing or implausibly short payload.");
                continue;
            }
            if (!HasExpectedSignature(ext, bytes))
                issues.Add($"{ext}: fixture payload does not have the expected encoded-image signature.");
        }
        return issues;
    }

    private static bool HasExpectedSignature(string extension, byte[] bytes)
    {
        static bool Starts(byte[] value, params byte[] prefix) => value.Length >= prefix.Length && prefix.Select((b, i) => value[i] == b).All(x => x);
        return extension.ToLowerInvariant() switch
        {
            ".jpg" or ".jpeg" or ".jpe" => Starts(bytes, 0xFF, 0xD8, 0xFF),
            ".png" => Starts(bytes, 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A),
            ".bmp" => Starts(bytes, 0x42, 0x4D),
            ".gif" => Starts(bytes, 0x47, 0x49, 0x46, 0x38),
            ".tif" or ".tiff" => Starts(bytes, 0x49, 0x49, 0x2A, 0x00) || Starts(bytes, 0x4D, 0x4D, 0x00, 0x2A),
            ".webp" => bytes.Length >= 12 && Starts(bytes, 0x52, 0x49, 0x46, 0x46) && bytes[8] == 0x57 && bytes[9] == 0x45 && bytes[10] == 0x42 && bytes[11] == 0x50,
            ".ico" => Starts(bytes, 0x00, 0x00, 0x01, 0x00),
            _ => false
        };
    }

    public static IReadOnlyList<string> WriteTo(string directory)
    {
        Directory.CreateDirectory(directory);
        var written = new List<string>();
        foreach (var pair in CreatePayloads())
        {
            var fileName = "fixture" + pair.Key;
            var path = Path.Combine(directory, fileName);
            File.WriteAllBytes(path, pair.Value);
            written.Add(path);
        }
        File.WriteAllLines(Path.Combine(directory, "fixture_manifest.tsv"),
            new[] { "extension\tfile\tbytes" }.Concat(written.Select(path =>
                $"{Path.GetExtension(path).ToLowerInvariant()}\t{Path.GetFileName(path)}\t{new FileInfo(path).Length}")));

        // Every routed suffix gets a physical probe so build/diagnostic packages can verify discovery,
        // longest-suffix matching and provider routing. These are intentionally NOT fake image files:
        // unsupported codecs must never pass merely because a JPEG was renamed to another extension.
        var routing = Path.Combine(directory, "routing-probes-196");
        Directory.CreateDirectory(routing);
        var routingRows = new List<string> { "extension\tfile\texpected_match" };
        var index = 0;
        foreach (var extension in ImageFormatRegistry.Extensions)
        {
            var name = $"route_{++index:000}" + extension;
            File.WriteAllText(Path.Combine(routing, name), $"GLIDE_ROUTING_PROBE\n{extension}\n");
            routingRows.Add($"{extension}\t{name}\t{ImageFormatRegistry.GetLongestExtension(name)}");
        }
        File.WriteAllLines(Path.Combine(routing, "routing_manifest.tsv"), routingRows);

        // Create hundreds of genuine JPEGs for realistic folder-navigation/prefetch testing. Only
        // three WIC encodes are needed; copies preserve valid JPEG bitstreams while producing a
        // realistic directory enumeration/cache workload. Sizes include multi-megapixel material.
        var performanceCorpusGenerated = WriteNavigationStressCorpus(directory, 240);
        // This file exists even when generation is skipped, so automated consumers never infer
        // that an absent navigation CSV means zero latency or zero files.
        var performanceTarget = Path.Combine(directory, "navigation-stress-240");
        var performanceResult = Path.Combine(performanceTarget, "live_navigation_results.csv");
        if (!File.Exists(performanceResult))
        {
            File.WriteAllLines(performanceResult, performanceCorpusGenerated
                ? new[] { "status,result", "PENDING,Awaiting live MainWindow diagnostic navigation" }
                : new[] { "status,result", "SKIP,Performance corpus was not generated on this platform" });
        }
        File.WriteAllText(Path.Combine(directory, "performance_corpus_status.json"),
            System.Text.Json.JsonSerializer.Serialize(new
            {
                corpus = "navigation-stress-240",
                generated = performanceCorpusGenerated,
                status = performanceCorpusGenerated ? "PENDING_LIVE_RUN" : "SKIP_PLATFORM_DEPENDENCY",
                evidence = performanceResult
            }, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));

        // If the authoritative legacy real-format corpus is packaged beside the source, stage it too.
        // This retains actual AVIF/WebP/TIFF/GIF/etc payloads instead of synthesising false support.
        var legacySource = FindCorpusDirectory("diagnostic_corpus");
        var legacyTarget = Path.Combine(directory, "legacy-real-format-corpus");
        if (Directory.Exists(legacySource))
        {
            Directory.CreateDirectory(legacyTarget);
            foreach (var source in Directory.EnumerateFiles(legacySource))
                File.Copy(source, Path.Combine(legacyTarget, Path.GetFileName(source)), true);
        }
        return written;
    }
    private static bool WriteNavigationStressCorpus(string directory, int count)
    {
        var target = Path.Combine(directory, "navigation-stress-240");
        Directory.CreateDirectory(target);
        var masters = new List<string>();
        var encodeAvailable = true;
        foreach (var (width, height, label) in new[] { (960, 640, "small"), (1920, 1280, "medium"), (3000, 2000, "large") })
        {
            var master = Path.Combine(target, $"_master_{label}.jpg");
            var bytes = new byte[checked(width * height * 4)];
            for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
            {
                var i = (y * width + x) * 4;
                bytes[i] = (byte)(x * 255 / Math.Max(1, width - 1));
                bytes[i + 1] = (byte)(y * 255 / Math.Max(1, height - 1));
                bytes[i + 2] = (byte)((x + y) & 255);
                bytes[i + 3] = 255;
            }
            var handle = System.Runtime.InteropServices.Marshal.AllocHGlobal(bytes.Length);
            try
            {
                System.Runtime.InteropServices.Marshal.Copy(bytes, 0, handle, bytes.Length);
                if (!Glide.Imaging.NativeImageEncoder.TryEncode(master, (uint)width, (uint)height, (uint)(width * 4), handle, ".jpg"))
                {
                    // Never substitute a tiny fixture here. This directory is a performance corpus,
                    // so a fallback payload would falsely label its dimensions and invalidate timing
                    // evidence. Windows/WIC generation is an explicit certification prerequisite.
                    encodeAvailable = false;
                    break;
                }
            }
            finally { System.Runtime.InteropServices.Marshal.FreeHGlobal(handle); }
            masters.Add(master);
        }
        if (!encodeAvailable)
        {
            foreach (var file in Directory.EnumerateFiles(target)) File.Delete(file);
            File.WriteAllLines(Path.Combine(target, "navigation_manifest.tsv"), new[]
            {
                "status\tSKIP",
                "reason\tNative WIC JPEG encoding is unavailable; no synthetic performance corpus was generated.",
                "requirement\tGenerate this corpus on Windows before using navigation timings as evidence."
            });
            return false;
        }
        var rows = new List<string> { "index\tfile\tclass\tbytes" };
        for (var i = 0; i < count; i++)
        {
            var cls = i % masters.Count;
            var file = Path.Combine(target, $"browse_{i + 1:0000}_{Path.GetFileNameWithoutExtension(masters[cls]).Replace("_master_", "")}.jpg");
            File.Copy(masters[cls], file, true);
            rows.Add($"{i + 1}\t{Path.GetFileName(file)}\t{cls}\t{new FileInfo(file).Length}");
        }
        File.WriteAllLines(Path.Combine(target, "navigation_manifest.tsv"), rows);
        return true;
    }

    private static string? FindCorpusDirectory(string name)
    {
        foreach (var start in new[] { Environment.CurrentDirectory, AppContext.BaseDirectory })
        {
            var current = new DirectoryInfo(start);
            while (current is not null)
            {
                var candidate = Path.Combine(current.FullName, name);
                if (Directory.Exists(candidate)) return candidate;
                current = current.Parent;
            }
        }
        return null;
    }

}
