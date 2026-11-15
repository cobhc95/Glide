using System.IO.Compression;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Glide.App.Controls;
using Glide.App.Diagnostics;
using Glide.App.Platform;
using Glide.App.Services;
using Glide.Core.Settings;

namespace Glide.App.Settings;

/// <summary>
/// Diagnostics export (report folder + zip, settings page captures, performance
/// and image-fixture probes, control/effect map). Split from SettingsWindow.axaml.cs; behaviour unchanged.
/// </summary>
public partial class SettingsWindow
{
    private async void RunDiagnosticsClicked(object? sender, RoutedEventArgs e)
    {
        var writer = new StringWriter();
        Glide.Diagnostics.DiagnosticRunner.SelfTest(writer);
        var dialog = new Window
        {
            Width = 620,
            Height = 360,
            Title = "Glide Diagnostics",
            Icon = Icon,
            Content = new ScrollViewer { Content = new TextBlock { Text = writer.ToString(), Margin = new Thickness(18), TextWrapping = Avalonia.Media.TextWrapping.Wrap } }
        };
        PopupPlacementStore.Track(dialog, "diagnostics-results");
        await dialog.ShowDialog(this);
    }

    public async Task<string> ExportDiagnosticsAsync(string? targetDir = null, MainWindow? explicitOwner = null)
    {
        ExportDiagnosticsButton.IsEnabled = false;
        try
        {
            var customDir = targetDir ?? Environment.GetEnvironmentVariable("GLIDE_DIAGNOSTICS_DIR") ?? Environment.GetEnvironmentVariable("GLIDE_DIAGNOSTICS_EXPORT_DIR");
            var stamp = DateTime.Now.ToString("yyyy-MM-dd_HHmmss");
            string reportFolder;
            string zipPath;
            bool keepFolder = false;

            if (!string.IsNullOrWhiteSpace(customDir))
            {
                Directory.CreateDirectory(customDir);
                reportFolder = Path.Combine(customDir, $"Glide Diagnostics {stamp}");
                zipPath = reportFolder + ".zip";
                keepFolder = true;
            }
            else
            {
                var downloads = WindowsKnownFolders.Downloads();
                reportFolder = Path.Combine(downloads, $"Glide Diagnostics {stamp}");
                zipPath = reportFolder + ".zip";
            }

            Directory.CreateDirectory(reportFolder);
            var writer = new StringWriter();
            Glide.Diagnostics.DiagnosticRunner.Export(reportFolder, writer);

            await CaptureAllSettingsPagesAsync(reportFolder);
            UiDiagnosticCapture.TryCapture(this, Path.Combine(reportFolder, "settings_export_time.png"), out var settingsCaptureError);
            // Geometry/layout now audit the responsive Settings root. ScrollViewer off-viewport
            // descendants are intentionally excluded from outside-root failures.
            UiDiagnosticCapture.WriteGeometry(ReferenceRoot, Path.Combine(reportFolder, "settings_geometry.txt"));
            UiDiagnosticCapture.WriteLayoutAudit(ReferenceRoot, Path.Combine(reportFolder, "settings_layout_audit.txt"));
            UiDiagnosticCapture.WriteControlInventory(ReferenceRoot, Path.Combine(reportFolder, "settings_control_inventory.txt"));
            var effectiveOwner = (Owner as Control) ?? explicitOwner;
            if (effectiveOwner is Control owner)
            {
                UiDiagnosticCapture.TryCapture(owner, Path.Combine(reportFolder, "main_export_time.png"), out var mainCaptureError);
                UiDiagnosticCapture.WriteGeometry(owner, Path.Combine(reportFolder, "main_geometry.txt"));
                UiDiagnosticCapture.WriteLayoutAudit(owner, Path.Combine(reportFolder, "main_layout_audit.txt"));
                UiDiagnosticCapture.WriteControlInventory(owner, Path.Combine(reportFolder, "main_control_inventory.txt"));
                if (effectiveOwner is MainWindow mainWindow)
                {
                    mainWindow.WriteDiagnosticEvidence(reportFolder);
                    await mainWindow.CaptureDiagnosticVisualScenariosAsync(reportFolder);
                }
                if (!string.IsNullOrWhiteSpace(mainCaptureError)) File.WriteAllText(Path.Combine(reportFolder, "main_capture_warning.txt"), mainCaptureError);
            }
            if (!string.IsNullOrWhiteSpace(settingsCaptureError)) File.WriteAllText(Path.Combine(reportFolder, "settings_capture_warning.txt"), settingsCaptureError);
            UiDiagnosticCapture.WriteSettingsEffectCoverage(ReadControls(), Path.Combine(reportFolder, "settings_effect_coverage.txt"));
            WriteSettingsControlEffectMap(Path.Combine(reportFolder, "settings_control_effect_map.tsv"));
            await WritePerformanceEffectDiagnosticsAsync(reportFolder);
            await WriteImageFixtureDiagnosticsAsync(reportFolder);
            if (effectiveOwner is MainWindow liveMain)
            {
                // Exercise the actual visible viewer over the generated 240-image corpus. This is
                // intentionally after static/settings capture so navigation cannot contaminate those
                // baselines. The main window is restored to the user's prior image afterwards.
                await liveMain.RunLiveDiagnosticNavigationAsync(
                    Path.Combine(reportFolder, "generated_image_fixtures", "navigation-stress-240"), reportFolder);
                await liveMain.RunLargeImageFirstPaintDiagnosticAsync(
                    Path.Combine(reportFolder, "generated_image_fixtures", "legacy-real-format-corpus", "93_large_progressive_portrait_21mp.jpg"), reportFolder);
                // Refresh evidence after the live exercise. Previously behaviour_events.jsonl was copied
                // before navigation, so decode-route/native-Explorer events created by the most valuable
                // part of the audit could be absent from the exported ZIP.
                liveMain.WriteDiagnosticEvidence(reportFolder);
            }

            WriteOverallDiagnosticSummary(reportFolder);
            if (File.Exists(zipPath)) File.Delete(zipPath);
            ZipFile.CreateFromDirectory(reportFolder, zipPath, CompressionLevel.Fastest, includeBaseDirectory: false);
            if (!keepFolder)
            {
                try { Directory.Delete(reportFolder, true); } catch { /* best-effort cleanup; a locked or missing file is harmless */ }
            }
            DirtyText.Text = $"Diagnostics → {Path.GetFileName(zipPath)}";
            return reportFolder;
        }
        catch (Exception ex)
        {
            DirtyText.Text = "Diagnostic export failed";
            try
            {
                var targetErrDir = targetDir ?? Environment.GetEnvironmentVariable("GLIDE_DIAGNOSTICS_DIR") ?? Environment.GetEnvironmentVariable("GLIDE_DIAGNOSTICS_EXPORT_DIR") ?? WindowsKnownFolders.Downloads();
                Directory.CreateDirectory(targetErrDir);
                File.WriteAllText(Path.Combine(targetErrDir, "Glide Diagnostics Export Error.txt"), ex.ToString());
            }
            catch { /* writing the error note failed too; nothing further can be reported */ }
            throw;
        }
        finally
        {
            ExportDiagnosticsButton.IsEnabled = true;
        }
    }

    private async void ExportDiagnosticsClicked(object? sender, RoutedEventArgs e)
    {
        try
        {
            await ExportDiagnosticsAsync();
        }
        catch (Exception loggedEx) { Glide.Diagnostics.Runtime.LiveDiagnosticTrace.WriteException("diagnostics", "export_failed", loggedEx); }
    }

    private static void WriteOverallDiagnosticSummary(string reportFolder)
    {
        var failures = new List<string>();
        var warnings = new List<string>();
        foreach (var file in Directory.EnumerateFiles(reportFolder, "*.txt", SearchOption.AllDirectories))
        {
            var name = Path.GetRelativePath(reportFolder, file);
            if (name.Equals("summary.txt", StringComparison.OrdinalIgnoreCase) || name.Equals("overall_summary.txt", StringComparison.OrdinalIgnoreCase)) continue;
            foreach (var raw in File.ReadLines(file))
            {
                var line = raw.Trim();
                if (line.StartsWith("FAIL", StringComparison.OrdinalIgnoreCase) ||
                    (line.StartsWith("SUMMARY ", StringComparison.OrdinalIgnoreCase) && !line.Contains("FAIL=0", StringComparison.OrdinalIgnoreCase)))
                    failures.Add($"{name}: {line}");
                else if (line.StartsWith("WARN", StringComparison.OrdinalIgnoreCase)) warnings.Add($"{name}: {line}");
            }
        }
        var status = failures.Count == 0 ? "PASS" : "FAIL";
        var lines = new List<string>
        {
            $"OVERALL {status}",
            $"Nested failures={failures.Count} warnings={warnings.Count}",
            "Overall PASS requires both structural diagnostics and every generated nested suite/layout audit to contain no FAIL evidence."
        };
        lines.AddRange(failures.Take(200).Select(x => "FAIL " + x));
        lines.AddRange(warnings.Take(200).Select(x => "WARN " + x));
        File.WriteAllLines(Path.Combine(reportFolder, "overall_summary.txt"), lines);

        var structural = Path.Combine(reportFolder, "summary.txt");
        var previous = File.Exists(structural) ? File.ReadAllText(structural) : string.Empty;
        File.WriteAllText(structural, $"OVERALL {status} | nested FAIL={failures.Count} WARN={warnings.Count}{Environment.NewLine}" + previous);
    }

    private async Task WritePerformanceEffectDiagnosticsAsync(string reportFolder)
    {
        var temp = Path.Combine(reportFolder, "performance_fixtures");
        Directory.CreateDirectory(temp);
        var rows = new System.Text.Json.Nodes.JsonArray();
        var text = new List<string>();
        void Add(string id, bool pass, string detail)
        {
            rows.Add((System.Text.Json.Nodes.JsonNode)new System.Text.Json.Nodes.JsonObject { ["id"] = id, ["status"] = pass ? "PASS" : "FAIL", ["detail"] = detail });
            text.Add($"{(pass ? "PASS" : "FAIL")} {id}: {detail}");
        }

        try
        {
            var speed = Glide.Imaging.ImagePerformancePolicy.ForProfile("Maximum speed");
            var balanced = Glide.Imaging.ImagePerformancePolicy.ForProfile("Balanced");
            var quality = Glide.Imaging.ImagePerformancePolicy.ForProfile("Maximum quality");
            Add("profile_coordination",
                speed.PreviewLongestSide < balanced.PreviewLongestSide && balanced.PreviewLongestSide < quality.PreviewLongestSide &&
                speed.RefinementDelayMs > balanced.RefinementDelayMs && balanced.RefinementDelayMs > quality.RefinementDelayMs &&
                !speed.ProgressiveColorFirstPreview && balanced.ProgressiveColorFirstPreview && quality.ProgressiveColorFirstPreview &&
                speed.FullNeighbourPredecodeCount == 0 && balanced.FullNeighbourPredecodeCount == 0 && quality.FullNeighbourPredecodeCount > balanced.FullNeighbourPredecodeCount &&
                speed.PrefetchDepth == 3 && balanced.PrefetchDepth == 5 && quality.PrefetchDepth == 15 &&
                quality.CompressedCacheItems == 20 && quality.CompressedCacheMegabytes == 999 && quality.DecodedCacheMegabytes == 999,
                $"preview={speed.PreviewLongestSide}/{balanced.PreviewLongestSide}/{quality.PreviewLongestSide}, delay={speed.RefinementDelayMs}/{balanced.RefinementDelayMs}/{quality.RefinementDelayMs}, progressive-colour={speed.ProgressiveColorFirstPreview}/{balanced.ProgressiveColorFirstPreview}/{quality.ProgressiveColorFirstPreview}, full-neighbours={speed.FullNeighbourPredecodeCount}/{balanced.FullNeighbourPredecodeCount}/{quality.FullNeighbourPredecodeCount}, prefetch={speed.PrefetchDepth}/{balanced.PrefetchDepth}/{quality.PrefetchDepth}, cache={quality.CompressedCacheItems}/{quality.CompressedCacheMegabytes}/{quality.DecodedCacheMegabytes}");

            var plan = Glide.Imaging.ImageLoadCoordinator.BuildNeighbourPlan(new[] { "0", "1", "2", "3", "4" }, 2, 1, 2);
            Add("direction_aware_prefetch", plan.SequenceEqual(new[] { "3", "1", "4", "0" }), string.Join(" -> ", plan));

            var largeBmp = Path.Combine(temp, "large_1600x900.bmp");
            WriteDiagnosticBmp(largeBmp, 1600, 900);
            using (var loader = new Glide.Imaging.ImageLoadCoordinator())
            {
                loader.Policy = balanced with
                {
                    PreviewLongestSide = 480,
                    RefinementDelayMs = 5,
                    BackgroundRefinement = true,
                    PredictivePrefetch = false,
                    CompressedCacheMegabytes = 32,
                    DecodedCacheMegabytes = 64
                };
                var first = await loader.LoadForegroundAsync(largeBmp);
                if (first is null) Add("staged_first_frame", false, "Foreground decode returned null.");
                else
                {
                    var firstLongest = Math.Max(first.Bitmap.PixelSize.Width, first.Bitmap.PixelSize.Height);
                    Add("staged_first_frame", first.IsPreview && firstLongest <= 480 && first.SourceWidth == 1600 && first.SourceHeight == 900,
                        $"preview={first.Bitmap.PixelSize.Width}x{first.Bitmap.PixelSize.Height}, source={first.SourceWidth}x{first.SourceHeight}, isPreview={first.IsPreview}");
                    var bounded = await loader.RefineForegroundAsync(first);
                    Add("viewport_bounded_refinement", bounded is not null && bounded.IsPreview &&
                        Math.Max(bounded.Bitmap.PixelSize.Width, bounded.Bitmap.PixelSize.Height) > firstLongest &&
                        Math.Max(bounded.Bitmap.PixelSize.Width, bounded.Bitmap.PixelSize.Height) <= 960,
                        bounded is null ? "Bounded refinement returned null." : $"bounded={bounded.Bitmap.PixelSize.Width}x{bounded.Bitmap.PixelSize.Height}; contract=viewport-demand, not unconditional full decode");
                    bounded?.Bitmap.Dispose();

                    loader.SetViewportDemand(largeBmp, new Glide.Imaging.ImageViewportDemand(1600, 900, 1.0, SelectionZoom: false, ExactPixelDemand: true));
                    var refined = await loader.RefineForegroundAsync(first);
                    Add("full_resolution_refinement", refined is not null && !refined.IsPreview && refined.Bitmap.PixelSize.Width == 1600 && refined.Bitmap.PixelSize.Height == 900,
                        refined is null ? "Exact-pixel refinement returned null." : $"final={refined.Bitmap.PixelSize.Width}x{refined.Bitmap.PixelSize.Height}; demand=exact-pixels");
                    first.Bitmap.Dispose();
                    refined?.Bitmap.Dispose();
                }

                // Cache budget and purge use multiple independent identities so LRU eviction is exercised.
                var cacheFiles = new List<string>();
                for (var i = 0; i < 10; i++)
                {
                    var copy = Path.Combine(temp, $"cache_{i:00}.bmp");
                    File.Copy(largeBmp, copy, true);
                    File.SetLastWriteTimeUtc(copy, DateTime.UtcNow.AddSeconds(i));
                    cacheFiles.Add(copy);
                }
                loader.Policy = loader.Policy with { CompressedCacheItems = 64, CompressedCacheMegabytes = 32 };
                await loader.WarmAsync(cacheFiles);
                var warm = loader.GetCacheSnapshot();
                Add("compressed_cache_budget", warm.CompressedBytes <= 32L * 1024 * 1024 && warm.CompressedItems < cacheFiles.Count,
                    $"items={warm.CompressedItems}, bytes={warm.CompressedBytes}");
                loader.PurgeCaches();
                var purged = loader.GetCacheSnapshot();
                Add("cache_purge_epoch", purged.CompressedItems == 0 && purged.DecodedItems == 0 && purged.CompressedBytes == 0 && purged.DecodedBytes == 0,
                    $"compressed={purged.CompressedItems}/{purged.CompressedBytes}, decoded={purged.DecodedItems}/{purged.DecodedBytes}");

                loader.Policy = loader.Policy with { SequentialForegroundReads = false, DecoderScaledFirstFrame = false };
                var randomRead = await loader.LoadForegroundAsync(largeBmp);
                Add("foreground_read_mode", randomRead is not null && randomRead.Bitmap.PixelSize.Width == 1600, "Decode succeeds with sequential hint disabled; the setting is consumed by foreground stream creation.");
                randomRead?.Bitmap.Dispose();
            }
        }
        catch (Exception ex)
        {
            Add("performance_effect_suite", false, ex.GetType().Name + ": " + ex.Message);
        }

        File.WriteAllLines(Path.Combine(reportFolder, "performance_effect_results.txt"), text);
        File.WriteAllText(Path.Combine(reportFolder, "performance_effect_results.json"),
            rows.ToJsonString(AppJsonContext.Indented));
    }

    private static void WriteDiagnosticBmp(string path, int width, int height)
    {
        var rowBytes = ((width * 3 + 3) / 4) * 4;
        var pixelBytes = checked(rowBytes * height);
        var fileBytes = checked(54 + pixelBytes);
        using var stream = File.Create(path);
        using var writer = new BinaryWriter(stream);
        writer.Write((byte)'B'); writer.Write((byte)'M'); writer.Write(fileBytes);
        writer.Write(0); writer.Write(54);
        writer.Write(40); writer.Write(width); writer.Write(height); writer.Write((short)1); writer.Write((short)24);
        writer.Write(0); writer.Write(pixelBytes); writer.Write(2835); writer.Write(2835); writer.Write(0); writer.Write(0);
        var row = new byte[rowBytes];
        for (var x = 0; x < width; x++)
        {
            row[x * 3] = (byte)(x % 251);
            row[x * 3 + 1] = (byte)((x * 3) % 251);
            row[x * 3 + 2] = (byte)((x * 7) % 251);
        }
        for (var y = 0; y < height; y++) writer.Write(row);
    }

    private async Task WriteImageFixtureDiagnosticsAsync(string reportFolder)
    {
        var fixtureFolder = Path.Combine(reportFolder, "generated_image_fixtures");
        var validation = Glide.Diagnostics.ImageDiagnosticFixtures.Validate();
        if (validation.Count > 0)
        {
            File.WriteAllLines(Path.Combine(reportFolder, "image_fixture_validation_failures.txt"), validation);
            return;
        }

        var publishedFixtureFolder = Path.Combine(AppContext.BaseDirectory, "diagnostic-fixtures");
        var publishedFixtureFiles = Glide.Diagnostics.ImageDiagnosticFixtures.Extensions
            .Select(extension => Path.Combine(publishedFixtureFolder, "fixture" + extension))
            .ToArray();
        var fixtureSource = publishedFixtureFiles.All(File.Exists) ? "published-build" : "regenerated-development";
        if (fixtureSource == "published-build")
        {
            Directory.CreateDirectory(fixtureFolder);
            foreach (var source in Directory.EnumerateFiles(publishedFixtureFolder, "*", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(publishedFixtureFolder, source);
                var target = Path.Combine(fixtureFolder, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(source, target, overwrite: true);
            }
        }
        else
        {
            // Keep the generator's returned core-fixture inventory as an explicit artifact. The
            // generated corpus status is read below; diagnostics must never infer that a 240-file
            // corpus exists merely because its target directory name is present.
            _ = Glide.Diagnostics.ImageDiagnosticFixtures.WriteTo(fixtureFolder);
        }

        var performanceCorpusStatusPath = Path.Combine(fixtureFolder, "performance_corpus_status.json");
        var performanceCorpusGenerated = false;
        var performanceCorpusStatus = "SKIP";
        var performanceCorpusFileCount = 0;
        try
        {
            using var corpusStatus = JsonDocument.Parse(File.ReadAllText(performanceCorpusStatusPath));
            var root = corpusStatus.RootElement;
            performanceCorpusGenerated = root.TryGetProperty("generated", out var generated) && generated.GetBoolean();
            performanceCorpusStatus = root.TryGetProperty("status", out var status) ? status.GetString() ?? "SKIP" : "SKIP";
            var corpusFolder = Path.Combine(fixtureFolder, "navigation-stress-240");
            if (Directory.Exists(corpusFolder))
                performanceCorpusFileCount = Directory.EnumerateFiles(corpusFolder, "*.jpg", SearchOption.TopDirectoryOnly)
                    .Count(path => !Path.GetFileName(path).StartsWith("_master_", StringComparison.OrdinalIgnoreCase));
        }
        catch
        {
            performanceCorpusStatus = "SKIP_STATUS_UNAVAILABLE";
        }

        using var loader = new Glide.Imaging.ImageLoadCoordinator { WarmCacheItemLimit = 64, SequentialReadEnabled = true };
        var rows = new List<string> { "extension,file,bytes,width,height,cold_decode_ms,warm_decode_ms,warm_cache_hit,native_probe_ok,native_width,native_height,native_frames,status,error" };
        var jsonResults = new System.Text.Json.Nodes.JsonArray();
        foreach (var extension in Glide.Diagnostics.ImageDiagnosticFixtures.Extensions)
        {
            var path = Path.Combine(fixtureFolder, "fixture" + extension);
            var status = "PASS";
            var error = "";
            var width = 0; var height = 0; var coldMs = 0d; var warmMs = 0d; var cacheHit = false;
            var nativeProbeOk = false;
            uint nativeWidth = 0, nativeHeight = 0, nativeFrames = 0;
            try
            {
                loader.PurgeWarmCache();
                var cold = await loader.LoadForegroundAsync(path);
                if (cold is null) throw new InvalidOperationException("Cold fixture decode returned no result.");
                width = cold.Bitmap.PixelSize.Width; height = cold.Bitmap.PixelSize.Height; coldMs = cold.DecodeTime.TotalMilliseconds; cold.Bitmap.Dispose();
                await loader.WarmAsync(new[] { path });
                var warm = await loader.LoadForegroundAsync(path);
                if (warm is null) throw new InvalidOperationException("Warm fixture decode returned no result.");
                warmMs = warm.DecodeTime.TotalMilliseconds; cacheHit = warm.CacheHit; warm.Bitmap.Dispose();
                if (!cacheHit) { status = "FAIL"; error = "Warm cache was not used."; }

                nativeProbeOk = Glide.Imaging.NativeImageProbe.TryProbe(path, out var nativeInfo);
                if (nativeProbeOk)
                {
                    nativeWidth = nativeInfo.Width; nativeHeight = nativeInfo.Height; nativeFrames = nativeInfo.FrameCount;
                    if (nativeWidth != width || nativeHeight != height)
                    {
                        status = "FAIL";
                        error = $"Native WIC probe dimensions {nativeWidth}x{nativeHeight} disagree with Avalonia decode {width}x{height}.";
                    }
                }
            }
            catch (Exception ex)
            {
                status = "FAIL"; error = ex.GetType().Name + ": " + ex.Message;
            }
            static string Csv(string value) => "\"" + value.Replace("\"", "\"\"") + "\"";
            long fixtureBytes = 0;
            try { fixtureBytes = File.Exists(path) ? new FileInfo(path).Length : 0; }
            catch { /* fixture size is only a report column; 0 is acceptable */ }
            rows.Add(string.Join(",", Csv(extension), Csv(Path.GetFileName(path)), fixtureBytes, width, height,
                coldMs.ToString("F3", System.Globalization.CultureInfo.InvariantCulture), warmMs.ToString("F3", System.Globalization.CultureInfo.InvariantCulture),
                cacheHit ? "true" : "false", nativeProbeOk ? "true" : "false", nativeWidth, nativeHeight, nativeFrames, status, Csv(error)));
            jsonResults.Add((System.Text.Json.Nodes.JsonNode)new System.Text.Json.Nodes.JsonObject
            {
                ["extension"] = extension, ["file"] = Path.GetFileName(path), ["bytes"] = fixtureBytes, ["width"] = width, ["height"] = height,
                ["coldDecodeMs"] = Math.Round(coldMs, 3), ["warmDecodeMs"] = Math.Round(warmMs, 3), ["warmCacheHit"] = cacheHit,
                ["nativeWicProbe"] = new System.Text.Json.Nodes.JsonObject { ["available"] = nativeProbeOk, ["width"] = nativeWidth, ["height"] = nativeHeight, ["frames"] = nativeFrames },
                ["status"] = status, ["error"] = error
            });
        }
        File.WriteAllLines(Path.Combine(reportFolder, "image_fixture_decode_results.csv"), rows);
        File.WriteAllText(Path.Combine(reportFolder, "image_fixture_decode_results.json"),
            jsonResults.ToJsonString(AppJsonContext.Indented));
        File.WriteAllText(Path.Combine(reportFolder, "image_fixture_decode_summary.txt"),
            $"Routed extensions: {Glide.Core.ImageFormatRegistry.Extensions.Count}\nCore real-fixture extensions: {Glide.Diagnostics.ImageDiagnosticFixtures.Extensions.Count}\nFixture source: {fixtureSource}\nThe ten protected core suffixes have real encoded fixtures, cold/warm decode checks and native WIC probes. All 196 routed suffixes also have physical routing probes. Navigation corpus: status={performanceCorpusStatus}, generated={performanceCorpusGenerated}, genuine JPEG files={performanceCorpusFileCount}; the count is reported from performance_corpus_status.json and the generated directory, never assumed from its name. The packaged legacy real-format corpus is staged when available. A routing probe is deliberately not claimed as an independently decodable format.\nTIFF additionally records bounded native preview, full-resolution refinement dimensions and malformed-input rejection in tiff_wic_results.json.\nSee image_fixture_decode_results.csv, image_fixture_decode_results.json and tiff_wic_results.json.\n");

        var tiffResults = new System.Text.Json.Nodes.JsonObject();
        var tiffPath = Path.Combine(fixtureFolder, "fixture.tiff");
        if (File.Exists(tiffPath))
        {
            try
            {
                var previewOk = Glide.Imaging.NativeImageDecoder.TryDecode(tiffPath, 1, out var preview);
                var previewWidth = previewOk ? preview.PixelSize.Width : 0;
                var previewHeight = previewOk ? preview.PixelSize.Height : 0;
                if (previewOk) preview.Dispose();
                var fullOk = Glide.Imaging.NativeImageDecoder.TryDecode(tiffPath, 0, out var full);
                var fullWidth = fullOk ? full.PixelSize.Width : 0;
                var fullHeight = fullOk ? full.PixelSize.Height : 0;
                if (fullOk) full.Dispose();
                var malformedPath = Path.Combine(reportFolder, "malformed_tiff_fixture.tiff");
                File.WriteAllBytes(malformedPath, new byte[] { 0x49, 0x49, 0x2A, 0x00, 0x01, 0x00 });
                var malformedRejected = !Glide.Imaging.NativeImageDecoder.TryDecode(malformedPath, 1, out var malformed);
                if (!malformedRejected) malformed.Dispose();
                File.Delete(malformedPath);
                var bounded = previewOk && previewWidth > 0 && previewHeight > 0 && Math.Max(previewWidth, previewHeight) <= 1;
                var refined = fullOk && fullWidth > 0 && fullHeight > 0 && (fullWidth >= previewWidth && fullHeight >= previewHeight);
                // A missing native WIC bridge is a platform/dependency gate, not a malformed
                // fixture failure. Once WIC is available, malformed acceptance is a real FAIL.
                var wicUnavailable = !previewOk && !fullOk;
                tiffResults["status"] = wicUnavailable ? "SKIP" : (bounded && refined && malformedRejected ? "PASS" : "FAIL");
                tiffResults["availability"] = wicUnavailable ? "WIC_UNAVAILABLE" : "WIC_AVAILABLE";
                tiffResults["preview"] = new System.Text.Json.Nodes.JsonObject { ["available"] = previewOk, ["width"] = previewWidth, ["height"] = previewHeight, ["maxLongestSide"] = 1, ["bounded"] = bounded };
                tiffResults["fullResolution"] = new System.Text.Json.Nodes.JsonObject { ["available"] = fullOk, ["width"] = fullWidth, ["height"] = fullHeight, ["refined"] = refined };
                tiffResults["malformedRejected"] = wicUnavailable ? (bool?)null : malformedRejected;
            }
            catch (Exception ex)
            {
                tiffResults["status"] = "FAIL";
                tiffResults["error"] = ex.GetType().Name + ": " + ex.Message;
            }
        }
        else
        {
            tiffResults["status"] = "SKIP";
            tiffResults["availability"] = "FIXTURE_UNAVAILABLE";
        }
        File.WriteAllText(Path.Combine(reportFolder, "tiff_wic_results.json"), tiffResults.ToJsonString(AppJsonContext.Indented));
    }

    private async Task CaptureAllSettingsPagesAsync(string reportFolder)
    {
        var previousCategory = _activeCategory;
        var previousSearch = SearchBox.Text ?? string.Empty;
        var previousOffset = ContentScroll.Offset;
        var screenshotFiles = new List<string>();
        var captureWarnings = new List<string>();
        var scrollRows = new List<string> { "category,screenshot,offsetY,maxOffsetY,viewportHeight" };
        // Every page with every card expanded, so the screenshots show all settings; each page's
        // own open/closed state is restored after its captures.
        var keys = CategoryGroups.Select(g => g.Key).ToArray();
        foreach (var key in keys)
        {
            ShowCategory(key, clearSearch: false);
            var pageCards = _cardsByPage.GetValueOrDefault(key) ?? new List<SettingsCard>();
            var openState = pageCards.Select(c => c.IsExpanded).ToArray();
            try
            {
                foreach (var card in pageCards) card.IsExpanded = true;
                ContentScroll.Offset = new Vector(ContentScroll.Offset.X, 0);
                await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Render);

                var immediate = $"settings_{key.ToLowerInvariant()}_immediate.png";
                if (UiDiagnosticCapture.TryCapture(this, Path.Combine(reportFolder, immediate), out var immediateError))
                    screenshotFiles.Add(immediate);
                else captureWarnings.Add($"{immediate}: {immediateError}");

                await Task.Delay(180);
                await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Render);
                var settled = $"settings_{key.ToLowerInvariant()}_settled.png";
                if (UiDiagnosticCapture.TryCapture(this, Path.Combine(reportFolder, settled), out var settledError))
                {
                    screenshotFiles.Add(settled);
                    scrollRows.Add(ScrollEvidenceRow(key, settled));
                }
                else captureWarnings.Add($"{settled}: {settledError}");
                UiDiagnosticCapture.WriteGeometry(ReferenceRoot, Path.Combine(reportFolder, $"settings_{key.ToLowerInvariant()}_geometry.txt"));
                UiDiagnosticCapture.WriteLayoutAudit(ReferenceRoot, Path.Combine(reportFolder, $"settings_{key.ToLowerInvariant()}_layout_audit.txt"));

                // Long Settings pages are part of Glide's visual contract. Capture the entire scrollable
                // page in overlapping settled views rather than only proving that the top rendered.
                // This deliberately uses ScrollBarMaximum rather than guessing which categories are long.
                var maxY = Math.Max(0, ContentScroll.ScrollBarMaximum.Y);
                var viewportHeight = Math.Max(1, ContentScroll.Viewport.Height);
                if (maxY > 20)
                {
                    var step = Math.Max(120, viewportHeight * 0.82);
                    var positions = new List<double>();
                    for (var y = step; y < maxY - 8; y += step) positions.Add(Math.Min(y, maxY));
                    if (positions.Count == 0 || Math.Abs(positions[^1] - maxY) > 8) positions.Add(maxY);

                    var scrollIndex = 2;
                    foreach (var y in positions.DistinctBy(value => Math.Round(value, 1)))
                    {
                        ContentScroll.Offset = new Vector(ContentScroll.Offset.X, Math.Clamp(y, 0, maxY));
                        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Render);
                        await Task.Delay(120);
                        var scrolled = $"settings_{key.ToLowerInvariant()}_scroll{scrollIndex}_settled.png";
                        if (UiDiagnosticCapture.TryCapture(this, Path.Combine(reportFolder, scrolled), out var scrolledError))
                        {
                            screenshotFiles.Add(scrolled);
                            scrollRows.Add(ScrollEvidenceRow(key, scrolled));
                        }
                        else captureWarnings.Add($"{scrolled}: {scrolledError}");
                        scrollIndex++;
                    }
                }
            }
            finally
            {
                for (var i = 0; i < pageCards.Count; i++) pageCards[i].IsExpanded = openState[i];
                UpdateExpandCollapseButtons();
            }
        }

        // Responsive-shell fixtures: explicitly prove the former Viewbox letterboxing bug stays gone.
        var oldWidth = Width; var oldHeight = Height;
        var resizeScenarios = new (string Name, double Width, double Height)[]
        {
            ("compact", 760, 560), ("standard", 1040, 760), ("wide", 1320, 760), ("tall", 900, 1000)
        };
        foreach (var scenario in resizeScenarios)
        {
            Width = scenario.Width; Height = scenario.Height;
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Render);
            await Task.Delay(100);
            var file = $"settings_resize_{scenario.Name}.png";
            if (UiDiagnosticCapture.TryCapture(this, Path.Combine(reportFolder, file), out var error)) screenshotFiles.Add(file);
            else captureWarnings.Add($"{file}: {error}");
            UiDiagnosticCapture.WriteGeometry(ReferenceRoot, Path.Combine(reportFolder, $"settings_resize_{scenario.Name}_geometry.txt"));
            UiDiagnosticCapture.WriteLayoutAudit(ReferenceRoot, Path.Combine(reportFolder, $"settings_resize_{scenario.Name}_layout_audit.txt"));
        }
        Width = oldWidth; Height = oldHeight;
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Render);

        // Permanent search fixture: proves that Search is not merely a textual index.
        SearchBox.Text = "hotkey";
        // Search rendering is debounced for responsiveness; flush it so the screenshot is not empty.
        FlushPendingSearch();
        ContentScroll.Offset = new Vector(ContentScroll.Offset.X, 0);
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Render);
        await Task.Delay(120);
        const string searchShot = "settings_search_hotkey_settled.png";
        if (UiDiagnosticCapture.TryCapture(this, Path.Combine(reportFolder, searchShot), out var searchError))
        {
            screenshotFiles.Add(searchShot);
            scrollRows.Add(ScrollEvidenceRow("Search:hotkey", searchShot));
        }
        else captureWarnings.Add($"{searchShot}: {searchError}");

        if (string.IsNullOrWhiteSpace(previousSearch))
        {
            _loadingControls = true;
            SearchBox.Text = string.Empty;
            _loadingControls = false;
            ShowCategory(previousCategory, clearSearch: false);
            ContentScroll.Offset = new Vector(previousOffset.X, Math.Min(previousOffset.Y, Math.Max(0, ContentScroll.ScrollBarMaximum.Y)));
        }
        else
        {
            SearchBox.Text = previousSearch; // TextChanged schedules the debounced rebuild.
            FlushPendingSearch();            // Render it now so the window is left in a settled state.
            ContentScroll.Offset = new Vector(previousOffset.X, Math.Min(previousOffset.Y, Math.Max(0, ContentScroll.ScrollBarMaximum.Y)));
        }

        File.WriteAllLines(Path.Combine(reportFolder, "settings_scroll_capture_index.csv"), scrollRows);
        File.WriteAllLines(Path.Combine(reportFolder, "screenshot_index.txt"), new[]
        {
            "Glide UI diagnostic screenshots", "",
            "Every Settings category is captured immediate + settled at the top.",
            "Scrollable Settings categories additionally receive overlapping settled captures through the bottom of the page.",
            "Search is captured using the editable 'hotkey' fixture.",
            "Main-window immediate/settled/export-time and resize-scenario captures are also included.", ""
        }.Concat(screenshotFiles));

        static string Csv(string value) => "\"" + value.Replace("\"", "\"\"") + "\"";
        var visualRows = new List<string>
        {
            "scenario,expected,automated_result,screenshot,ai_review_required,notes"
        };
        foreach (var file in screenshotFiles)
        {
            var scenario = Path.GetFileNameWithoutExtension(file);
            visualRows.Add(string.Join(",", Csv(scenario), Csv("No clipping/overlap/stale paint; legacy Glide density and theme remain coherent"), Csv("CAPTURED"), Csv(file), "true", Csv("Review against bundled legacy reference screenshots")));
        }
        visualRows.Add(string.Join(",", Csv("main_current"), Csv("Main Glide chrome/content/status remain inside bounds"), Csv("CAPTURED"), Csv("main_export_time.png"), "true", Csv("See main_layout_audit.txt and main_geometry.txt")));
        File.WriteAllLines(Path.Combine(reportFolder, "combined_visual_results.csv"), visualRows);

        if (captureWarnings.Count > 0)
            File.WriteAllLines(Path.Combine(reportFolder, "screenshot_capture_warnings.txt"), captureWarnings);

        string ScrollEvidenceRow(string category, string screenshot)
        {
            var offset = ContentScroll.Offset.Y;
            var maximum = Math.Max(0, ContentScroll.ScrollBarMaximum.Y);
            var viewport = Math.Max(0, ContentScroll.Viewport.Height);
            return string.Join(",", Csv(category), Csv(screenshot), offset.ToString("F1", System.Globalization.CultureInfo.InvariantCulture), maximum.ToString("F1", System.Globalization.CultureInfo.InvariantCulture), viewport.ToString("F1", System.Globalization.CultureInfo.InvariantCulture));
        }
    }


    private void WriteSettingsControlEffectMap(string path)
    {
        var lines = new List<string>
        {
            "id\tcategory\tkind\tlabel\tfuturePhase\tcontrol\tcontrolType\tenabled\tstatus"
        };
        foreach (var definition in SettingsCatalog.All)
        {
            _settingControls.TryGetValue(definition.Id, out var control);
            var status = definition.FuturePhase is not null
                ? $"future phase {definition.FuturePhase}; intentionally disabled/hidden"
                : control is null
                    ? definition.Kind == SettingKind.Action ? "current semantic/action editor or generated UI" : "current setting without a direct named editor"
                    : control.IsEnabled ? "current enabled editor; value participates in Settings state/effect path"
                    : ConditionallyDisabledSettingIds.Contains(definition.Id) ? "current editor disabled by current state" : "current editor unexpectedly disabled";
            lines.Add(string.Join("\t",
                Sanitize(definition.Id), Sanitize(definition.Category), definition.Kind.ToString(), Sanitize(definition.Label), definition.FuturePhase?.ToString() ?? "",
                Sanitize(control?.Name ?? ""), control?.GetType().Name ?? "", (control?.IsEnabled ?? false).ToString(), Sanitize(status)));
        }
        File.WriteAllLines(path, lines);

        static string Sanitize(string value) => value.Replace("\t", " ").Replace("\r", " ").Replace("\n", " ");
    }
}
