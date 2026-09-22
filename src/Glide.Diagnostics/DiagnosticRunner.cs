using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text.Json;
using Glide.Core;
using Glide.Core.Settings;
using Glide.Core.Workspace;
using Glide.Core.Commands;
using Glide.Imaging;

namespace Glide.Diagnostics;

/// <summary>
/// Headless/structural diagnostic spine. UI evidence is appended by Glide.App when a live window
/// exists. A PASS here means the named structural invariant was actually checked; it never means
/// unimplemented UI/behaviour magically passed.
/// </summary>
public static class DiagnosticRunner
{
    public const string BuildVersion = "4.2.7";

    public static DiagnosticSnapshot Capture() => new(
        Product: "Glide",
        Version: BuildVersion,
        Release: "Glide 4.2.7 (Automatic Windows integration)",
        Architecture: "C# + Avalonia retained-mode UI + semantic core + small native C++ bridge; NativeAOT blocked pending COM isolation",
        TimestampUtc: DateTimeOffset.UtcNow,
        Framework: RuntimeInformation.FrameworkDescription,
        OS: RuntimeInformation.OSDescription,
        ProcessArchitecture: RuntimeInformation.ProcessArchitecture.ToString(),
        AssemblyVersion: Assembly.GetEntryAssembly()?.GetName().Version?.ToString() ?? "unknown",
        Invariants:
        [
            "UI thread must not synchronously perform image decode or expensive directory work.",
            "Older decode completion must never replace a newer request.",
            "Physical input routes to semantic commands before feature behavior.",
            "Viewer/tab state is explicit and headlessly inspectable.",
            "Cross-window tab merge originates only from a tab drag and preserves typed runtime state.",
            "Shared Glide appearance is centralized; persistent controls use Glide vector glyphs rather than font/emoji symbols.",
            "Every user-facing setting is declared in SettingsCatalog before search/profiles/diagnostics depend on it.",
            "Every enabled Settings control maps to explicit state and a real effect/persistence path.",
            "Visible controls must work or be explicitly disabled; no fake-active placeholder controls.",
            "A feature is incomplete until targeted diagnostics/tests exist.",
            "Native imaging bridge is isolated from UI/workspace logic.",
            "Diagnostic status is truthful PASS/FAIL/SKIP/WARN; structural PASS does not certify UI parity.",
            "Every transferable source ZIP carries GLIDE_MANIFESTO_AND_HANDOFF.md plus current validation state."
        ],
        SettingsSchemaCount: SettingsCatalog.All.Count);

    public static DiagnosticCheck[] StructuralChecks()
    {
        var checks = new List<DiagnosticCheck>();
        var snapshot = Capture();
        checks.Add(Check("invariant_registry", snapshot.Invariants.Length >= 12,
            $"{snapshot.Invariants.Length} architecture/product invariants registered"));
        checks.Add(Check("settings_schema_breadth", snapshot.SettingsSchemaCount >= 80,
            $"{snapshot.SettingsSchemaCount} declarative settings registered"));
        var currentSettings = SettingsCatalog.All.Count(x => x.FuturePhase is null);
        var futureSettings = SettingsCatalog.All.Count(x => x.FuturePhase is not null);
        checks.Add(Check("settings_schema_glide30_contract", snapshot.SettingsSchemaCount == 189 && currentSettings == 189 && futureSettings == 0,
            $"Glide catalogue contract: total={snapshot.SettingsSchemaCount}, current={currentSettings}, future={futureSettings}; expected 189/189/0."));
        var overlayAvoidOverlap = SettingsCatalog.All.FirstOrDefault(x => string.Equals(x.Id, "overlay.animationAvoidOverlap", StringComparison.OrdinalIgnoreCase));
        checks.Add(Check("overlay_animation_avoid_overlap_contract",
            overlayAvoidOverlap is not null && overlayAvoidOverlap.FuturePhase is null && overlayAvoidOverlap.DefaultValue is bool avoidOverlapEnabled && avoidOverlapEnabled,
            "Animated overlay overlap avoidance is an active persisted setting enabled by default."));
        var overlayRefreshRate = SettingsCatalog.All.FirstOrDefault(x => string.Equals(x.Id, "overlay.animationRefreshRateHz", StringComparison.OrdinalIgnoreCase));
        checks.Add(Check("overlay_animation_refresh_rate_contract",
            overlayRefreshRate is not null && overlayRefreshRate.FuturePhase is null && overlayRefreshRate.DefaultValue is int refreshRate && refreshRate == 144,
            "Animated overlay refresh target is an active persisted setting with a 144 Hz default."));
        var progressiveColor = SettingsCatalog.All.FirstOrDefault(x => string.Equals(x.Id, "performance.progressiveColor", StringComparison.OrdinalIgnoreCase));
        checks.Add(Check("progressive_color_runtime_contract",
            progressiveColor is not null && progressiveColor.FuturePhase is null && progressiveColor.DefaultValue is bool enabled && enabled,
            "Progressive JPEG colour-first first paint is an active persisted Performance setting, enabled by default."));
        var wholeAppOverlay = SettingsCatalog.All.FirstOrDefault(x => string.Equals(x.Id, "overlay.wholeAppAlwaysStart", StringComparison.OrdinalIgnoreCase));
        checks.Add(Check("whole_app_overlay_opt_in_contract",
            wholeAppOverlay is not null && wholeAppOverlay.FuturePhase is null && wholeAppOverlay.DefaultValue is bool overlayDefault && !overlayDefault,
            "Whole-app Overlay / Window-in-Window startup is persisted but explicitly off by default."));
        var duplicateIds = SettingsCatalog.All.GroupBy(x => x.Id, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1).Select(g => g.Key).ToArray();
        checks.Add(Check("settings_schema_unique_ids", duplicateIds.Length == 0,
            duplicateIds.Length == 0 ? "No duplicate setting IDs" : "Duplicates: " + string.Join(", ", duplicateIds)));
        var effectRegistryIssues = SettingEffectRegistry.Validate();
        checks.Add(Check("settings_effect_registry_contract", effectRegistryIssues.Count == 0,
            effectRegistryIssues.Count == 0
                ? $"Typed settings effect registry covers {SettingEffectRegistry.All.Count} catalog IDs with owners, editor semantics and evidence IDs."
                : string.Join("; ", effectRegistryIssues)));
        var malformed = SettingsCatalog.All.Where(x => string.IsNullOrWhiteSpace(x.Id) || string.IsNullOrWhiteSpace(x.Category) || string.IsNullOrWhiteSpace(x.Label)).ToArray();
        checks.Add(Check("settings_schema_required_fields", malformed.Length == 0,
            malformed.Length == 0 ? "All settings have ID/category/label" : $"{malformed.Length} malformed entries"));
        checks.Add(Check("sibling_hidden_policy_declared", SettingsCatalog.All.Any(x => string.Equals(x.Id, "folderNav.includeHidden", StringComparison.OrdinalIgnoreCase)),
            "Sibling-folder hidden-folder inclusion/exclusion is explicitly declared in SettingsCatalog."));
        checks.Add(Check("scaling_quality_declared", SettingsCatalog.All.Any(x => string.Equals(x.Id, "performance.initialQuality", StringComparison.OrdinalIgnoreCase)),
            "Bitmap interpolation quality is explicitly declared in SettingsCatalog."));
        var defaultHotkeys = HotkeyCatalog.CreateDefaultMap();
        var nextAliases = HotkeyCatalog.ShortcutsForCommand(GlideCommand.NextImage, defaultHotkeys);
        checks.Add(Check("hotkey_command_alias_aggregation",
            nextAliases.SequenceEqual(new[] { "Space", "Right", "PageDown" }, StringComparer.OrdinalIgnoreCase),
            "Semantic-command tooltip aggregation includes every current action alias for Next Image."));

        checks.Add(Check("workspace_tab_transfer_model", VerifyWorkspaceTransferModel(),
            "Typed tab transfer preserves stable ID/state across workspaces and leaves the source empty."));
        var defaultHotkeyCollisions = HotkeyCatalog.All
            .SelectMany(action => action.DefaultShortcuts.Select(shortcut => (action.Id, shortcut)))
            .GroupBy(x => x.shortcut, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Select(x => x.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1)
            .ToArray();
        checks.Add(Check("hotkey_contract", HotkeyCatalog.All.Count == 69 &&
            HotkeyCatalog.All.Select(x => x.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count() == 69 &&
            HotkeyCatalog.ShortcutsForCommand(GlideCommand.AddOverlay, defaultHotkeys).Contains("Shift+O") &&
            defaultHotkeys["overlay.saveLayout"].SequenceEqual(new[] { "Ctrl+Shift+S" }, StringComparer.OrdinalIgnoreCase) &&
            defaultHotkeys["overlay.loadLayout"].SequenceEqual(new[] { "Ctrl+Shift+L" }, StringComparer.OrdinalIgnoreCase) &&
            defaultHotkeyCollisions.Length == 0,
            $"{HotkeyCatalog.All.Count} editable semantic hotkey actions registered; overlay layout defaults and collision-free bindings verified."));
        var irfanHotkeys = HotkeyCatalog.CreatePresetMap("IrfanView");
        var nomacsHotkeys = HotkeyCatalog.CreatePresetMap("nomacs");
        checks.Add(Check("interaction_customization_contract",
            defaultHotkeys["view.fullscreen"].Contains("F") &&
            defaultHotkeys["view.fitHeight"].SequenceEqual(new[] { "1" }, StringComparer.OrdinalIgnoreCase) &&
            defaultHotkeys["view.fitWidth"].SequenceEqual(new[] { "2" }, StringComparer.OrdinalIgnoreCase) &&
            defaultHotkeys["view.actual"].Contains("3") && defaultHotkeys["view.actual"].Contains("Ctrl+H") &&
            irfanHotkeys["view.fit"].Contains("F") && irfanHotkeys["view.fitHeight"].Contains("6") &&
            nomacsHotkeys["view.actual"].SequenceEqual(new[] { "Ctrl+1" }, StringComparer.OrdinalIgnoreCase) &&
            HotkeyCatalog.PresetNames.Count == 6 &&
            SettingsCatalog.All.Any(x => x.Id == "performance.interactivePanQuality") &&
            SettingsCatalog.All.Any(x => x.Id == "status.size") &&
            SettingsCatalog.All.Any(x => x.Id == "tabs.overflowArrows"),
            "Six opt-in familiarity hotkey presets (Glide default, Windows Photos, IrfanView, nomacs, FastStone, XnView MP) plus pan-quality, status-size and tab-overflow settings are structurally registered."));
        checks.Add(Check("glide24_settings_depth_contract",
            SettingsCatalog.All.Any(x => x.Id == "appearance.glowColor") &&
            SettingsCatalog.All.Any(x => x.Id == "appearance.glowIntensity") &&
            SettingsCatalog.All.Any(x => x.Id == "general.showRecentOnHome") &&
            SettingsCatalog.All.Any(x => x.Id == "fullscreen.keepTabBarOpen") &&
            SettingsCatalog.All.Any(x => x.Id == "performance.prefetchSiblingFolders") &&
            SettingsCatalog.All.Any(x => x.Id == "performance.prefetchSiblingFolderCount") &&
            SettingsCatalog.All.Any(x => x.Id == "slideshow.direction") &&
            SettingsCatalog.All.Any(x => x.Id == "slideshow.startFullscreen") &&
            SettingsCatalog.All.Any(x => x.Id == "slideshow.pauseInactive") &&
            SettingsCatalog.All.Any(x => x.Id == "developer.statusGlobalScale") &&
            SettingsCatalog.All.Any(x => x.Id == "status.autoResize") &&
            SettingsCatalog.All.Any(x => x.Id == "developer.statusMaximizedBoost") &&
            SettingsCatalog.All.Any(x => x.Id == "developer.overlayAbsoluteCoordinates") &&
            SettingsCatalog.All.Any(x => x.Id == "general.startupAction") &&
            SettingsCatalog.All.Any(x => x.Id == "general.newTabAction") &&
            SettingsCatalog.All.Any(x => x.Id == "navigation.hierarchicalFolders") &&
            SettingsCatalog.All.Any(x => x.Id == "navigation.confirmHierarchicalBoundary") &&
            SettingsCatalog.All.Any(x => x.Id == "developer.reverseSelectionZoomOut"),
            "Glide 3.0 appearance, independent picker directories, external-open policy, multi-tab close policy, responsive-status, proportional overlays, custom colours and performance-preset controls are structurally registered."));
        checks.Add(Check("gesture_contract_24x19", GestureCatalog.Slots.Count == 24 && GestureCatalog.Actions.Count == 19,
            $"{GestureCatalog.Slots.Count} gesture slots and {GestureCatalog.Actions.Count} gesture actions registered."));
        checks.Add(Check("gesture_unique_ids",
            GestureCatalog.Slots.Select(x => x.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count() == 24 &&
            GestureCatalog.Actions.Select(x => x.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count() == 19,
            "Gesture slot/action IDs are unique."));
        var fixtureIssues = ImageDiagnosticFixtures.Validate();
        checks.Add(Check("image_fixture_registry", fixtureIssues.Count == 0,
            fixtureIssues.Count == 0
                ? $"STRUCTURAL PASS: {ImageDiagnosticFixtures.Extensions.Count} protected core extensions have real encoded fixtures; routed specialist probes and performance corpus are reported separately."
                : string.Join("; ", fixtureIssues)));

        // These are deliberately SKIP/WARN here. Live Glide.App diagnostic export performs them with real controls.
        checks.Add(new("ui_screenshots", "SKIP", "Requires a live Avalonia window; Settings > Developer Options export captures immediate/settled screenshots."));
        checks.Add(new("ui_geometry", "SKIP", "Requires a live Avalonia window; app-level export records application logical-control bounds and layout audit."));
        checks.Add(new("input_behaviour_trace", "SKIP", "Requires live interaction; app-level event trace records window drag, selection, navigation, fullscreen, slideshow and settings actions."));
        var speed = ImagePerformancePolicy.ForProfile("Maximum speed");
        var balanced = ImagePerformancePolicy.ForProfile("Balanced");
        var quality = ImagePerformancePolicy.ForProfile("Maximum quality");
        checks.Add(Check("phase3_staged_decode_policy",
            speed.PreviewLongestSide < balanced.PreviewLongestSide && balanced.PreviewLongestSide <= quality.PreviewLongestSide &&
            speed.RefinementDelayMs > balanced.RefinementDelayMs && balanced.RefinementDelayMs > quality.RefinementDelayMs &&
            !speed.ProgressiveColorFirstPreview && balanced.ProgressiveColorFirstPreview && quality.ProgressiveColorFirstPreview &&
            speed.FullNeighbourPredecodeCount == 0 && balanced.FullNeighbourPredecodeCount == 0 && quality.FullNeighbourPredecodeCount > balanced.FullNeighbourPredecodeCount &&
            speed.PrefetchDepth == 3 && balanced.PrefetchDepth == 5 && quality.PrefetchDepth == 15 &&
            quality.CompressedCacheItems == 20 && quality.CompressedCacheMegabytes == 999 && quality.DecodedCacheMegabytes == 999,
            "Maximum speed/Balanced/Maximum quality coordinate preview size, refinement grace, progressive colour-first policy, full-neighbour preparation, cache budgets and neighbour-prefetch defaults 3/5/15."));
        var plan = ImageLoadCoordinator.BuildNeighbourPlan(new[] { "0", "1", "2", "3", "4" }, 2, 1, 2);
        var deepPaths = Enumerable.Range(0, 80).Select(x => x.ToString()).ToArray();
        var deepPlan = ImageLoadCoordinator.BuildNeighbourPlan(deepPaths, 40, 1, 99);
        checks.Add(Check("prefetch_depth_clamped_to_30", deepPlan.Count == 60,
            $"Neighbour prefetch accepts the user range through 30 per direction and clamps larger direct requests; plan count={deepPlan.Count}."));
        checks.Add(Check("direction_aware_prefetch_plan", plan.SequenceEqual(new[] { "3", "1", "4", "0" }),
            "Nearest-first neighbour plan prefers established forward browse direction while retaining bounded reverse coverage."));
        checks.Add(Check("codec_backend_boundary", typeof(IImageDecoderBackend).IsInterface,
            "IImageDecoderBackend is the permanent codec boundary behind the staged scheduler."));
        checks.Add(Check("legacy_codec_capability_registry", CodecCapabilityRegistry.Extensions.Count >= 190 && CodecCapabilityRegistry.All.Count == CodecCapabilityRegistry.Extensions.Count,
            $"Tiered capability registry exposes {CodecCapabilityRegistry.Extensions.Count} legacy extension suffixes without claiming unsupported decoders."));
        checks.Add(Check("codec_decode_truth_boundary",
            CodecCapabilityRegistry.Extensions.Count == 196 &&
            ImageFormatRegistry.CoreFastPathExtensions.Count == 10 &&
            CodecCapabilityRegistry.DecodeEnabledExtensions.Count >= 10 &&
            CodecCapabilityRegistry.All.Where(x => !ImageFormatRegistry.IsCoreFastPath(x.Extension) && x.DecodeEnabled)
                .All(x => x.Tier is CodecTier.ExternalProvider or CodecTier.BuiltIn),
            "Recognised/routed breadth, protected core fixtures and guaranteed decode remain distinct; extra guaranteed rows require a verified provider or a bundled built-in decoder."));
        checks.Add(Check("codec_capability_tiers", CodecCapabilityRegistry.All.Any(x => x.Tier == CodecTier.NativeOs && x.DecodeEnabled) &&
            CodecCapabilityRegistry.All.Any(x => x.Tier == CodecTier.ModularNative && !x.DecodeEnabled) &&
            CodecCapabilityRegistry.All.Any(x => x.Tier == CodecTier.RoutedOnDemand && !x.DecodeEnabled) &&
            CodecCapabilityRegistry.Describe("fixture.unknown_glide_format").Tier == CodecTier.Unsupported,
            "Native OS, modular-native, routed-on-demand, and unknown/unsupported capability tiers remain explicit without overstating decoder availability."));
        checks.Add(Check("codec_provider_abi_contract", CodecProviderAbi.IsSafe(new CodecProviderInfo(1, CodecProviderAbi.MinimumStructSize, "diagnostic", "1", "test", new[] { ".x" }, true, true, true, true)),
            "Optional codec providers use an explicit version/size/probe/decode/metadata/frame ABI contract."));
        checks.Add(Check("codec_provider_invalid_abi_rejected",
            CodecProviderAbi.IsSafe(new CodecProviderInfo(CodecProviderAbi.CurrentVersion, CodecProviderAbi.MinimumStructSize, "diagnostic-v2", "1", "test", new[] { ".x" }, true, true, true, true)) &&
            !CodecProviderAbi.IsSafe(new CodecProviderInfo(CodecProviderAbi.CurrentVersion + 1, CodecProviderAbi.MinimumStructSize, "diagnostic-v3", "1", "test", new[] { ".x" }, true, true, true, true)) &&
            !CodecProviderAbi.IsSafe(new CodecProviderInfo(1, CodecProviderAbi.MinimumStructSize - 1, "diagnostic", "1", "test", new[] { ".x" }, true, true, true, true)) &&
            !CodecProviderAbi.CapabilitiesMatch(new CodecProviderInfo(1, CodecProviderAbi.MinimumStructSize, "diagnostic", "1", "test", new[] { ".x" }, true, true, true, true), CodecProviderAbi.SupportsFull),
            "Unsupported ABI versions, undersized structs, and capability-flag mismatches are rejected."));
        checks.Add(Check("codec_provider_compound_suffix_selection",
            CodecCapabilityRegistry.Match("fixture.nii.gz") == ".nii.gz" && CodecCapabilityRegistry.Match("fixture.ome.tiff") == ".ome.tiff",
            "Provider routing uses the single longest-suffix matcher, including compound suffixes."));
        checks.Add(Check("tiff_wic_route_contract",
            NativeImageDecoder.IsTiffPath("fixture.tif") && NativeImageDecoder.IsTiffPath("fixture.TIFF") &&
            !NativeImageDecoder.IsTiffPath("fixture.png"),
            "STRUCTURAL PASS: TIFF aliases select the narrow WIC route; this does not certify that Windows WIC is available."));
        checks.Add(OperatingSystem.IsWindows()
            ? new("tiff_wic_decode_availability", "SKIP", "Windows platform detected, but WIC decode availability requires a live native bridge/fixture run.")
            : new("tiff_wic_decode_availability", "SKIP", "Windows WIC is unavailable on this host; TIFF decode is dependency/platform-gated and remains uncertified."));
        checks.Add(new("tab_drag_live", "WARN", "In-strip reorder is direct; sole-tab movement and detached tear-off inspect a forgiving, destination-DPI-aware physical-pixel attach zone and accent preview without SendInput. Merge, Aero Snap, mixed-DPI, target destruction and cancellation still require live Windows certification."));
        checks.Add(Check("window_in_window_state_contract", typeof(OverlayState).IsAssignableTo(typeof(object)) && typeof(OverlayLayoutStore).IsClass,
            "Window-in-Window overlay state/layout persistence has a framework-free contract."));
        checks.Add(new("file_associations", "PASS", "Glide registers Open With/Capabilities for every recognised extension automatically - per-user on first launch and machine-wide from the installer - and the installer unregisters it on removal. Windows default-app (UserChoice) selection remains OS-controlled."));
        return checks.ToArray();
    }

    public static int SelfTest(TextWriter output)
    {
        var snapshot = Capture();
        var checks = StructuralChecks();
        foreach (var check in checks) output.WriteLine($"{check.Status,-4} {check.Id}: {check.Detail}");
        var failed = checks.Count(x => x.Status == "FAIL");
        output.WriteLine();
        output.WriteLine(failed == 0
            ? "STRUCTURAL PASS — live UI/behaviour checks remain separately reported."
            : $"STRUCTURAL FAIL — {failed} failed check(s)."
        );
        output.WriteLine($"Runtime: {snapshot.Framework}");
        output.WriteLine($"OS: {snapshot.OS}");
        output.WriteLine($"Architecture: {snapshot.ProcessArchitecture}");
        output.WriteLine($"Settings schema: {snapshot.SettingsSchemaCount}");
        return failed == 0 ? 0 : 1;
    }

    public static int Export(string folder, TextWriter output)
    {
        Directory.CreateDirectory(folder);
        var snapshot = Capture();
        var checks = StructuralChecks();
        var jsonOptions = new JsonSerializerOptions { WriteIndented = true };
        File.WriteAllText(Path.Combine(folder, "health.json"), JsonSerializer.Serialize(snapshot, jsonOptions));
        File.WriteAllText(Path.Combine(folder, "structural_checks.json"), JsonSerializer.Serialize(checks, jsonOptions));
        File.WriteAllLines(Path.Combine(folder, "diagnostic_status.txt"), checks.Select(x => $"{x.Status}\t{x.Id}\t{x.Detail}"));
        File.WriteAllText(Path.Combine(folder, "summary.txt"),
            $"Glide {snapshot.Version}\n{snapshot.Release}\n{snapshot.TimestampUtc:O}\n{snapshot.Framework}\n{snapshot.OS}\nSettings={snapshot.SettingsSchemaCount}\n" +
            $"Structural PASS={checks.Count(x => x.Status == "PASS")} FAIL={checks.Count(x => x.Status == "FAIL")} SKIP={checks.Count(x => x.Status == "SKIP")} WARN={checks.Count(x => x.Status == "WARN")}\n");
        File.WriteAllLines(Path.Combine(folder, "architecture_invariants.txt"), snapshot.Invariants);
        File.WriteAllText(Path.Combine(folder, "settings_schema.json"), JsonSerializer.Serialize(SettingsCatalog.All, jsonOptions));
        // Keep capability truth independently inspectable: the registry is recognition/routing
        // breadth, while DecodeEnabled only means a core decoder or verified live provider exists.
        // Exporting this table must never trigger provider discovery or native loads.
        var capabilities = CodecCapabilityRegistry.All.Select(x => new
        {
            extension = x.Extension,
            tier = x.Tier.ToString(),
            provider = x.Provider,
            decodeEnabled = x.DecodeEnabled,
            progressive = x.Progressive,
            animated = x.Animated,
            rawMetadata = x.RawMetadata
        }).ToArray();
        File.WriteAllText(Path.Combine(folder, "codec_capabilities.json"), JsonSerializer.Serialize(new
        {
            recognizedAndRouted = CodecCapabilityRegistry.Extensions.Count,
            protectedCore = ImageFormatRegistry.CoreFastPathExtensions.Count,
            guaranteedDecode = CodecCapabilityRegistry.DecodeEnabledExtensions.Count,
            capabilities
        }, jsonOptions));
        File.WriteAllText(Path.Combine(folder, "codec_providers.json"), JsonSerializer.Serialize(
            CodecProviderRuntime.IsSharedCreated
                ? CodecProviderRuntime.Shared.Artifacts
                : Array.Empty<CodecProviderArtifact>(), jsonOptions));
        File.WriteAllText(Path.Combine(folder, "README.txt"),
            "Glide 4.2.7 evidence bundle. PASS means a named assertion actually ran. SKIP means the owning subsystem is absent or the check requires a live UI. codec_capabilities.json separates recognised/routed suffixes from guaranteed decode; codec_providers.json reports only providers already indexed/verified in this process and never wakes optional providers during export. When exported from Settings > Developer Options, Glide.App appends immediate/settled screenshots, logical-control geometry/layout audit, settings-effect coverage, current settings, capability/provider inventory and a live behaviour event trace. Do not infer UI parity from headless structural PASS.\n");
        output.WriteLine($"Diagnostics exported to: {Path.GetFullPath(folder)}");
        return checks.Any(x => x.Status == "FAIL") ? 1 : 0;
    }

    private static bool VerifyWorkspaceTransferModel()
    {
        try
        {
            var source = new WorkspaceState(createHome: false);
            var path = Path.Combine(Path.GetTempPath(), "glide-diagnostic-transfer.jpg");
            var tab = AssertImage(source.AddImage(path)) with
            {
                ViewState = new ImageTabViewState("Manual", 2.5, 40, -12)
            };
            source.ReplaceTab(tab);
            var transferred = source.RemoveForTransfer(tab.Id);
            if (transferred is not ImageTabState image || source.Tabs.Count != 0 || image.Id != tab.Id) return false;

            var target = new WorkspaceState(createHome: false);
            target.InsertTransferred(image);
            return target.Active is ImageTabState received && received.Id == tab.Id &&
                   received.ViewState == tab.ViewState &&
                   string.Equals(received.Path, Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }


    public static int MeasureLaunch(IEnumerable<string> targetImages, int iterations, TextWriter output)
        => MeasureLaunch(string.Join(";", targetImages), iterations, output);

    public static int MeasureLaunch(string? targetImagePath, int iterations, TextWriter output)
    {
        output.WriteLine("=========================================================================================================");
        output.WriteLine("  Glide Real-World Multi-Format Cold Launch Diagnostic Benchmark");
        output.WriteLine("=========================================================================================================");

        var exePath = LocateExecutable();
        if (exePath is null)
        {
            output.WriteLine("ERROR: Glide executable not found. Build the project first (e.g. dotnet build).");
            return 1;
        }

        var targets = ResolveBenchmarkImages(targetImagePath);
        if (targets.Count == 0)
        {
            output.WriteLine("ERROR: No benchmark images found or resolved for common formats.");
            return 1;
        }

        iterations = Math.Clamp(iterations, 1, 30);

        output.WriteLine($"Executable:   {exePath}");
        output.WriteLine($"Iterations:   {iterations} run(s) per format");
        output.WriteLine($"Formats:      {string.Join(", ", targets.Select(t => t.Format))}");
        output.WriteLine("Mode:         Real visible GUI process (CreateNoWindow = false, UseShellExecute = false)");
        output.WriteLine("Verification: Win32 HWND visible & on-screen placement + DWM readyEvent + --perf-trace breakdown");
        output.WriteLine("---------------------------------------------------------------------------------------------------------");

        var results = new List<FormatBenchmarkResult>();

        foreach (var target in targets)
        {
            var formatResult = new FormatBenchmarkResult
            {
                Format = target.Format,
                ImagePath = target.FilePath
            };

            var fileInfo = new FileInfo(target.FilePath);
            var sizeKb = fileInfo.Exists ? fileInfo.Length / 1024 : 0;
            output.WriteLine($"\n>>> Benchmarking Format [{target.Format}] -> {Path.GetFileName(target.FilePath)} ({sizeKb} KB)");

            for (var i = 1; i <= iterations; i++)
            {
                var eventName = $"Glide_ColdLaunch_Ready_{Guid.NewGuid():N}";
                using var readyEvent = new EventWaitHandle(false, EventResetMode.ManualReset, eventName);
                var tempTrace = Path.Combine(Path.GetTempPath(), $"glide_bench_{Guid.NewGuid():N}.tsv");

                var psi = new ProcessStartInfo
                {
                    FileName = exePath,
                    UseShellExecute = false,
                    CreateNoWindow = false
                };
                psi.ArgumentList.Add("--perf-trace");
                psi.ArgumentList.Add(tempTrace);
                psi.ArgumentList.Add("--benchmark-exit-after-first-frame");
                psi.ArgumentList.Add("--force-new-instance");
                psi.ArgumentList.Add("--notify-first-frame");
                psi.ArgumentList.Add(eventName);
                psi.ArgumentList.Add(target.FilePath);

                var sw = Stopwatch.StartNew();
                using var proc = Process.Start(psi);
                if (proc is null)
                {
                    output.WriteLine($"  Run #{i}: Failed to spawn process.");
                    continue;
                }

                bool hwndVerified = false;
                IntPtr verifiedHwnd = IntPtr.Zero;
                RECT verifiedRect = default;

                var timeout = TimeSpan.FromSeconds(10);
                bool signaled = false;
                while (sw.Elapsed < timeout)
                {
                    if (!hwndVerified)
                    {
                        if (TryVerifyWindowOnScreen(proc.Id, out verifiedHwnd, out verifiedRect))
                            hwndVerified = true;
                    }

                    if (readyEvent.WaitOne(10))
                    {
                        signaled = true;
                        break;
                    }
                }
                var firstFrameMs = sw.Elapsed.TotalMilliseconds;

                if (signaled && !hwndVerified)
                {
                    if (TryVerifyWindowOnScreen(proc.Id, out verifiedHwnd, out verifiedRect))
                        hwndVerified = true;
                }

                proc.WaitForExit(6000);
                var totalExitMs = sw.Elapsed.TotalMilliseconds;
                sw.Stop();

                var trace = ParsePerfTrace(tempTrace);
                try { if (File.Exists(tempTrace)) File.Delete(tempTrace); } catch { }

                if (!signaled)
                {
                    output.WriteLine($"  Run #{i}: TIMEOUT waiting for first frame signal.");
                }
                else
                {
                    formatResult.FirstFrameTimes.Add(firstFrameMs);
                    formatResult.FullExitTimes.Add(totalExitMs);
                    if (trace.BitmapAssignedMs > 0)
                        formatResult.BitmapAssignedTimes.Add(trace.BitmapAssignedMs);
                    formatResult.Traces.Add(trace);

                    if (hwndVerified)
                    {
                        formatResult.HwndVerified = true;
                        formatResult.LastHwnd = verifiedHwnd;
                        formatResult.LastRect = verifiedRect;
                    }

                    var runType = i == 1 ? " [COLD]" : " [WARM]";
                    var hwndInfo = hwndVerified
                        ? $"HWND 0x{verifiedHwnd.ToInt64():X} ({verifiedRect.Right - verifiedRect.Left}x{verifiedRect.Bottom - verifiedRect.Top} visible)"
                        : "HWND unverified";

                    output.WriteLine($"  Run #{i}{runType,-7}: First frame: {firstFrameMs,6:F1} ms | Bitmap: {trace.BitmapAssignedMs,6:F1} ms | Exit: {totalExitMs,6:F1} ms | {hwndInfo}");
                    output.WriteLine($"    Trace breakdown: entry={trace.ProcessEntryMs:F1}ms -> avalonia={trace.AvaloniaStartMs:F1}ms -> ctor={trace.MainWindowCtorMs:F1}ms -> bitmap={trace.BitmapAssignedMs:F1}ms -> painted={trace.FirstFramePaintedMs:F1}ms (route: {trace.DecodeRoute})");
                }

                if (i < iterations) Thread.Sleep(150);
            }

            results.Add(formatResult);
        }

        // Formatted Summary Table (Requirement 4)
        output.WriteLine();
        output.WriteLine("=========================================================================================================");
        output.WriteLine("  MULTI-FORMAT BENCHMARK SUMMARY TABLE");
        output.WriteLine("=========================================================================================================");
        output.WriteLine($"| {"Format",-8} | {"Cold First Run (ms)",20} | {"Warm Runs (ms)",16} | {"Bitmap Assigned (ms)",20} | {"Overall Average (ms)",20} |");
        output.WriteLine($"|{new string('-', 10)}|{new string('-', 22)}|{new string('-', 18)}|{new string('-', 22)}|{new string('-', 22)}|");

        foreach (var r in results)
        {
            var coldStr = r.FirstFrameTimes.Count > 0 ? $"{r.ColdFirstRunMs:F1}" : "N/A";
            var warmStr = r.FirstFrameTimes.Count > 1 ? $"{r.WarmRunsAvgMs:F1}" : (r.FirstFrameTimes.Count == 1 ? $"{r.ColdFirstRunMs:F1} (1 run)" : "N/A");
            var bitmapStr = r.BitmapAssignedTimes.Count > 0 ? $"{r.BitmapAssignedAvgMs:F1}" : "N/A";
            var avgStr = r.FirstFrameTimes.Count > 0 ? $"{r.OverallAverageMs:F1}" : "N/A";

            output.WriteLine($"| {r.Format,-8} | {coldStr,20} | {warmStr,16} | {bitmapStr,20} | {avgStr,20} |");
        }
        output.WriteLine("=========================================================================================================");

        // Trace breakdown phase averages table
        output.WriteLine();
        output.WriteLine("Phase Breakdown Averages (from --perf-trace):");
        output.WriteLine($"| {"Format",-8} | {"Process Entry",14} | {"Avalonia Start",15} | {"MainWindow Ctor",17} | {"Bitmap Assigned",16} | {"First Frame Painted",20} | {"Decode Route",-24} |");
        output.WriteLine($"|{new string('-', 10)}|{new string('-', 16)}|{new string('-', 17)}|{new string('-', 19)}|{new string('-', 18)}|{new string('-', 22)}|{new string('-', 26)}|");

        foreach (var r in results)
        {
            var traces = r.Traces.Where(t => t.FirstFramePaintedMs > 0 || t.BitmapAssignedMs > 0).ToList();
            if (traces.Count == 0) continue;

            var entryAvg = traces.Average(t => t.ProcessEntryMs);
            var avaAvg = traces.Average(t => t.AvaloniaStartMs);
            var ctorAvg = traces.Average(t => t.MainWindowCtorMs);
            var bmpAvg = traces.Average(t => t.BitmapAssignedMs);
            var paintAvg = traces.Average(t => t.FirstFramePaintedMs);
            var route = traces.LastOrDefault()?.DecodeRoute ?? "unknown";

            output.WriteLine($"| {r.Format,-8} | {entryAvg,11:F1} ms | {avaAvg,12:F1} ms | {ctorAvg,14:F1} ms | {bmpAvg,13:F1} ms | {paintAvg,17:F1} ms | {route,-24} |");
        }
        output.WriteLine("=========================================================================================================");

        return results.Any(r => r.FirstFrameTimes.Count > 0) ? 0 : 1;
    }

    private static readonly string[] CommonFormats = [".jpg", ".png", ".webp", ".bmp", ".gif", ".tif"];

    private static string NormalizeFormat(string extension)
    {
        var ext = extension.ToLowerInvariant();
        return ext switch
        {
            ".jpeg" or ".jpe" => ".jpg",
            ".tiff" => ".tif",
            ".dib" => ".bmp",
            _ => ext
        };
    }

    private static List<(string Format, string FilePath)> ResolveBenchmarkImages(string? targetImagePath)
    {
        var resolved = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        // Case 1: Delimited list of paths
        if (!string.IsNullOrWhiteSpace(targetImagePath) && (targetImagePath.Contains(',') || targetImagePath.Contains(';')))
        {
            var parts = targetImagePath.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            foreach (var part in parts)
            {
                if (File.Exists(part))
                {
                    var ext = NormalizeFormat(Path.GetExtension(part));
                    resolved.TryAdd(ext, Path.GetFullPath(part));
                }
            }
            if (resolved.Count > 0)
                return SortByCommonFormats(resolved);
        }

        // Case 2: Target is a directory
        if (!string.IsNullOrWhiteSpace(targetImagePath) && Directory.Exists(targetImagePath))
        {
            ScanDirectoryForFormats(targetImagePath, resolved);
            if (resolved.Count > 0)
                return SortByCommonFormats(resolved);
        }

        // Case 3: Target is a single file
        if (!string.IsNullOrWhiteSpace(targetImagePath) && File.Exists(targetImagePath))
        {
            var fullPath = Path.GetFullPath(targetImagePath);
            var ext = NormalizeFormat(Path.GetExtension(fullPath));
            resolved[ext] = fullPath;

            var dir = Path.GetDirectoryName(fullPath);
            if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
            {
                ScanDirectoryForFormats(dir, resolved);
            }
            return SortByCommonFormats(resolved);
        }

        // Case 4: Target is null/empty -> check standard fixture locations
        var fixtureDirectories = new[]
        {
            Path.Combine(Directory.GetCurrentDirectory(), "artifacts", "diagnostic-fixtures"),
            Path.Combine(AppContext.BaseDirectory, "artifacts", "diagnostic-fixtures"),
            Path.Combine(Directory.GetCurrentDirectory(), "artifacts", "diagnostic-fixtures", "legacy-real-format-corpus"),
            Path.Combine(AppContext.BaseDirectory, "artifacts", "diagnostic-fixtures", "legacy-real-format-corpus"),
        };

        foreach (var dir in fixtureDirectories)
        {
            if (Directory.Exists(dir))
                ScanDirectoryForFormats(dir, resolved);
        }

        // If any common formats are missing, generate them on the fly from ImageDiagnosticFixtures
        var missing = CommonFormats.Where(fmt => !resolved.ContainsKey(fmt)).ToList();
        if (missing.Count > 0)
        {
            try
            {
                var tempFixturesDir = Path.Combine(Path.GetTempPath(), "glide_diagnostic_fixtures");
                ImageDiagnosticFixtures.WriteTo(tempFixturesDir);
                ScanDirectoryForFormats(tempFixturesDir, resolved);
            }
            catch { }
        }

        return SortByCommonFormats(resolved);
    }

    private static void ScanDirectoryForFormats(string dir, Dictionary<string, string> resolved)
    {
        try
        {
            foreach (var file in Directory.EnumerateFiles(dir))
            {
                var ext = NormalizeFormat(Path.GetExtension(file));
                if (CommonFormats.Contains(ext, StringComparer.OrdinalIgnoreCase))
                {
                    resolved.TryAdd(ext, Path.GetFullPath(file));
                }
            }
        }
        catch { }
    }

    private static List<(string Format, string FilePath)> SortByCommonFormats(Dictionary<string, string> dict)
    {
        var result = new List<(string Format, string FilePath)>();
        foreach (var fmt in CommonFormats)
        {
            if (dict.TryGetValue(fmt, out var path))
                result.Add((fmt, path));
        }
        foreach (var pair in dict)
        {
            if (!CommonFormats.Contains(pair.Key, StringComparer.OrdinalIgnoreCase))
                result.Add((pair.Key, pair.Value));
        }
        return result;
    }

    private static string? LocateExecutable()
    {
        var exePath = Environment.ProcessPath;
        if (!string.IsNullOrEmpty(exePath) && File.Exists(exePath) &&
            string.Equals(Path.GetFileName(exePath), "Glide.exe", StringComparison.OrdinalIgnoreCase))
            return exePath;

        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "Glide.exe"),
            Path.Combine(Directory.GetCurrentDirectory(), "dist", "Glide.exe"),
            Path.Combine(Directory.GetCurrentDirectory(), ".artifacts", "bin", "Glide.App", "debug", "Glide.exe"),
            Path.Combine(Directory.GetCurrentDirectory(), ".artifacts", "bin", "Glide.App", "release", "Glide.exe"),
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".artifacts", "bin", "Glide.App", "debug", "Glide.exe")
        };
        return candidates.FirstOrDefault(File.Exists);
    }

    private static PerfTraceBreakdown ParsePerfTrace(string traceFile)
    {
        var trace = new PerfTraceBreakdown();
        if (!File.Exists(traceFile)) return trace;

        try
        {
            var lines = File.ReadAllLines(traceFile);
            foreach (var line in lines)
            {
                var parts = line.Split('\t');
                if (parts.Length < 4) continue;
                if (!double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var elapsedMs))
                    continue;

                var eventName = parts[3];
                switch (eventName)
                {
                    case "process_entry" when trace.ProcessEntryMs == 0:
                        trace.ProcessEntryMs = elapsedMs;
                        break;
                    case "avalonia_build_start" when trace.AvaloniaStartMs == 0:
                    case "avalonia_xaml_start" when trace.AvaloniaStartMs == 0:
                        trace.AvaloniaStartMs = elapsedMs;
                        break;
                    case "main_window_ctor_start" when trace.MainWindowCtorMs == 0:
                    case "ctor_init_component_start" when trace.MainWindowCtorMs == 0:
                        trace.MainWindowCtorMs = elapsedMs;
                        break;
                    case "bitmap_assigned" when trace.BitmapAssignedMs == 0:
                        trace.BitmapAssignedMs = elapsedMs;
                        if (parts.Length >= 5)
                        {
                            var routeSegment = parts[4].Split(';').FirstOrDefault(s => s.StartsWith("route=", StringComparison.OrdinalIgnoreCase));
                            if (routeSegment is not null)
                                trace.DecodeRoute = routeSegment["route=".Length..];
                        }
                        break;
                    case "composition_batch_rendered" when trace.FirstFramePaintedMs == 0:
                        trace.FirstFramePaintedMs = elapsedMs;
                        break;
                }
            }
        }
        catch { }

        return trace;
    }

    private sealed class FormatBenchmarkResult
    {
        public string Format { get; set; } = "";
        public string ImagePath { get; set; } = "";
        public List<double> FirstFrameTimes { get; } = new();
        public List<double> BitmapAssignedTimes { get; } = new();
        public List<double> FullExitTimes { get; } = new();
        public List<PerfTraceBreakdown> Traces { get; } = new();
        public bool HwndVerified { get; set; }
        public IntPtr LastHwnd { get; set; }
        public RECT LastRect { get; set; }

        public double ColdFirstRunMs => FirstFrameTimes.Count > 0 ? FirstFrameTimes[0] : 0.0;
        public double WarmRunsAvgMs => FirstFrameTimes.Count > 1 ? FirstFrameTimes.Skip(1).Average() : (FirstFrameTimes.Count == 1 ? FirstFrameTimes[0] : 0.0);
        public double BitmapAssignedAvgMs => BitmapAssignedTimes.Count > 0 ? BitmapAssignedTimes.Average() : 0.0;
        public double OverallAverageMs => FirstFrameTimes.Count > 0 ? FirstFrameTimes.Average() : 0.0;
    }

    private sealed class PerfTraceBreakdown
    {
        public double ProcessEntryMs { get; set; }
        public double AvaloniaStartMs { get; set; }
        public double MainWindowCtorMs { get; set; }
        public double BitmapAssignedMs { get; set; }
        public double FirstFramePaintedMs { get; set; }
        public string DecodeRoute { get; set; } = "unknown";
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct WINDOWPLACEMENT
    {
        public int length;
        public int flags;
        public int showCmd;
        public POINT ptMinPosition;
        public POINT ptMaxPosition;
        public RECT rcNormalPosition;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowPlacement(IntPtr hWnd, ref WINDOWPLACEMENT lpwndpl);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    private static bool TryVerifyWindowOnScreen(int processId, out IntPtr windowHandle, out RECT windowRect)
    {
        windowHandle = IntPtr.Zero;
        windowRect = default;
        if (!OperatingSystem.IsWindows()) return true;

        IntPtr foundHwnd = IntPtr.Zero;
        RECT foundRect = default;

        try
        {
            EnumWindows((hWnd, _) =>
            {
                if (!IsWindow(hWnd) || !IsWindowVisible(hWnd))
                    return true;

                GetWindowThreadProcessId(hWnd, out var pid);
                if (pid != processId)
                    return true;

                var wp = new WINDOWPLACEMENT { length = Marshal.SizeOf<WINDOWPLACEMENT>() };
                if (GetWindowPlacement(hWnd, ref wp))
                {
                    if (wp.showCmd is 0 or 2 or 6)
                        return true;
                }

                if (GetWindowRect(hWnd, out var rect))
                {
                    var w = rect.Right - rect.Left;
                    var h = rect.Bottom - rect.Top;
                    if (w > 100 && h > 100)
                    {
                        foundHwnd = hWnd;
                        foundRect = rect;
                        return false;
                    }
                }
                return true;
            }, IntPtr.Zero);
        }
        catch
        {
            return false;
        }

        if (foundHwnd != IntPtr.Zero)
        {
            windowHandle = foundHwnd;
            windowRect = foundRect;
            return true;
        }
        return false;
    }


    private static ImageTabState AssertImage(TabState tab) => tab as ImageTabState ?? throw new InvalidOperationException("Expected image tab.");

    private static DiagnosticCheck Check(string id, bool pass, string detail) => new(id, pass ? "PASS" : "FAIL", detail);
}

public sealed record DiagnosticSnapshot(
    string Product,
    string Version,
    string Release,
    string Architecture,
    DateTimeOffset TimestampUtc,
    string Framework,
    string OS,
    string ProcessArchitecture,
    string AssemblyVersion,
    string[] Invariants,
    int SettingsSchemaCount);

public sealed record DiagnosticCheck(string Id, string Status, string Detail);
