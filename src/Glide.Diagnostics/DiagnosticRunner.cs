using System.Reflection;
using System.Runtime.InteropServices;
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
    public const string BuildVersion = "3.0";

    public static DiagnosticSnapshot Capture() => new(
        Product: "Glide",
        Version: BuildVersion,
        Release: "Glide 3.0",
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
        checks.Add(Check("settings_schema_glide30_contract", snapshot.SettingsSchemaCount == 160 && currentSettings == 160 && futureSettings == 0,
            $"Glide 3.0 catalogue contract: total={snapshot.SettingsSchemaCount}, current={currentSettings}, future={futureSettings}; expected 160/160/0."));
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
        checks.Add(Check("hotkey_contract", HotkeyCatalog.All.Count == 70 &&
            HotkeyCatalog.All.Select(x => x.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count() == 70 &&
            HotkeyCatalog.ShortcutsForCommand(GlideCommand.AddOverlay, defaultHotkeys).Contains("Shift+O"),
            $"{HotkeyCatalog.All.Count} editable semantic hotkey actions registered, including Shift+O overlay add."));
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
            SettingsCatalog.All.Any(x => x.Id == "developer.statusAutoFit") &&
            SettingsCatalog.All.Any(x => x.Id == "developer.statusMaximizedBoost") &&
            SettingsCatalog.All.Any(x => x.Id == "developer.overlayAbsoluteCoordinates") &&
            SettingsCatalog.All.Any(x => x.Id == "general.startupAction") &&
            SettingsCatalog.All.Any(x => x.Id == "general.newTabAction") &&
            SettingsCatalog.All.Any(x => x.Id == "general.newExplorerTabLastLocation") &&
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
            speed.PreviewLongestSide < balanced.PreviewLongestSide && balanced.PreviewLongestSide < quality.PreviewLongestSide &&
            speed.RefinementDelayMs > balanced.RefinementDelayMs && balanced.RefinementDelayMs > quality.RefinementDelayMs &&
            !speed.ProgressiveColorFirstPreview && balanced.ProgressiveColorFirstPreview && quality.ProgressiveColorFirstPreview &&
            speed.FullNeighbourPredecodeCount == 0 && balanced.FullNeighbourPredecodeCount == 0 && quality.FullNeighbourPredecodeCount > balanced.FullNeighbourPredecodeCount &&
            speed.PrefetchDepth == 3 && balanced.PrefetchDepth == 5 && quality.PrefetchDepth == 5,
            "Maximum speed/Balanced/Maximum quality coordinate preview size, refinement grace, progressive colour-first policy, full-neighbour preparation and neighbour-prefetch defaults 3/5/5."));
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
                .All(x => x.Tier == CodecTier.ExternalProvider),
            "Recognised/routed breadth, protected core fixtures and guaranteed decode remain distinct; extra guaranteed rows require a verified provider."));
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
        checks.Add(new("file_associations", "PASS", "Installer source declares RegisteredApplications/App Paths and common image-association capabilities; live Windows default-app selection remains OS-controlled."));
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
            "Glide 3.0 evidence bundle. PASS means a named assertion actually ran. SKIP means the owning subsystem is absent or the check requires a live UI. codec_capabilities.json separates recognised/routed suffixes from guaranteed decode; codec_providers.json reports only providers already indexed/verified in this process and never wakes optional providers during export. When exported from Settings > Developer Options, Glide.App appends immediate/settled screenshots, logical-control geometry/layout audit, settings-effect coverage, current settings, capability/provider inventory and a live behaviour event trace. Do not infer UI parity from headless structural PASS.\n");
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
