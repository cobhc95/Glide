namespace Glide.Core.Settings;

/// <summary>Typed effect ownership for the user-facing settings schema.</summary>
public enum SettingApplySemantics { LivePreview, CommitOnly, Action, FutureDisabled }
public enum SettingEditorKind { FromCatalog, DynamicAction, DisabledFuture }
public enum SettingEvidenceStatus { Unknown, StaticContract, LiveVerified }
public enum SettingEffectOwner
{
    None, General, Appearance, Navigation, Viewing, WindowChrome, Fullscreen, Input,
    Performance, StatusBar, Slideshow, TabsWorkspace, FolderNavigation, Overlay,
    Profiles, WindowIntegration, Developer, FutureDisabled
}

public sealed record SettingEffectBinding(
    string SettingId,
    SettingEffectOwner EffectOwner,
    SettingEditorKind EditorKind,
    SettingApplySemantics ApplySemantics,
    string EvidenceId,
    SettingEvidenceStatus EvidenceStatus = SettingEvidenceStatus.Unknown);

/// <summary>
/// The single typed map from SettingsCatalog IDs to editor/action and runtime-effect ownership. Stage C: the rows are
/// derived from the catalogue, so a new setting needs no row here. Rules:
/// - owner: <see cref="OwnerByPrefix"/> (the id's first segment), unless the id is in <see cref="OwnerExceptions"/>;
/// - editor: Action settings are <see cref="SettingEditorKind.DynamicAction"/>, every other kind
///   <see cref="SettingEditorKind.FromCatalog"/>;
/// - apply semantics: Action settings are <see cref="SettingApplySemantics.Action"/> (gesture rows are live:
///   <see cref="SettingApplySemantics.LivePreview"/>); others follow <see cref="LivePreviewPrefixes"/> unless listed in
///   <see cref="SemanticsExceptions"/>;
/// - evidence id: "settings.effect." + id; a future-phase catalogue row is FutureDisabled throughout.
/// Only edit this file when a new setting's owner or live/commit behaviour differs from its prefix (or for a new prefix).
/// MainWindow routes applied setting changes by <see cref="SettingEffectBinding.EffectOwner"/> (Glide.App SettingsDelta).
/// </summary>
public static class SettingEffectRegistry
{
    // Stage B: no validating static constructor (a catalogue error used to surface as a TypeInitializationException
    // wherever the type was first touched). Validate() runs in tests (SettingsCatalogTests, GlideCatalogsTests) and in
    // diagnostics; GlideCatalogs.Build() fixes the construction order.

    /// <summary>Default effect owner per id prefix (the part before the first dot).</summary>
    public static IReadOnlyDictionary<string, SettingEffectOwner> OwnerByPrefix { get; } = new Dictionary<string, SettingEffectOwner>(StringComparer.OrdinalIgnoreCase)
    {
        ["search"] = SettingEffectOwner.General, ["updates"] = SettingEffectOwner.General, ["general"] = SettingEffectOwner.General,
        ["settings"] = SettingEffectOwner.General,
        ["windows"] = SettingEffectOwner.WindowIntegration,
        ["navigation"] = SettingEffectOwner.Navigation,
        ["folderNav"] = SettingEffectOwner.FolderNavigation,
        ["appearance"] = SettingEffectOwner.Appearance,
        ["status"] = SettingEffectOwner.StatusBar,
        ["view"] = SettingEffectOwner.Viewing, ["keyboard"] = SettingEffectOwner.Viewing,
        ["window"] = SettingEffectOwner.WindowChrome, ["titlebar"] = SettingEffectOwner.WindowChrome, ["caption"] = SettingEffectOwner.WindowChrome,
        ["fullscreen"] = SettingEffectOwner.Fullscreen,
        ["tabs"] = SettingEffectOwner.TabsWorkspace, ["explorer"] = SettingEffectOwner.TabsWorkspace, ["tags"] = SettingEffectOwner.TabsWorkspace,
        ["escape"] = SettingEffectOwner.Input, ["mouse"] = SettingEffectOwner.Input, ["selection"] = SettingEffectOwner.Input,
        ["hotkeys"] = SettingEffectOwner.Input, ["gesture"] = SettingEffectOwner.Input,
        ["developer"] = SettingEffectOwner.Developer, ["backup"] = SettingEffectOwner.Developer, ["reset"] = SettingEffectOwner.Developer,
        ["performance"] = SettingEffectOwner.Performance,
        ["slideshow"] = SettingEffectOwner.Slideshow,
        ["overlay"] = SettingEffectOwner.Overlay,
        ["profiles"] = SettingEffectOwner.Profiles,
    };

    /// <summary>Settings owned by a different subsystem than their prefix says.</summary>
    public static IReadOnlyDictionary<string, SettingEffectOwner> OwnerExceptions { get; } = new Dictionary<string, SettingEffectOwner>(StringComparer.OrdinalIgnoreCase)
    {
        ["navigation.hierarchicalFolders"] = SettingEffectOwner.FolderNavigation,
        ["navigation.confirmHierarchicalBoundary"] = SettingEffectOwner.FolderNavigation,
        ["navigation.hierarchicalPreviousEntry"] = SettingEffectOwner.FolderNavigation,
        ["window.centerOnDisplay"] = SettingEffectOwner.WindowIntegration,
        ["performance.startWithWindowsInBackground"] = SettingEffectOwner.WindowIntegration,
        ["windows.alwaysOnTopMode"] = SettingEffectOwner.WindowChrome,
    };

    /// <summary>Prefixes whose non-action settings preview live by default; every other prefix is commit-only by default.</summary>
    public static IReadOnlySet<string> LivePreviewPrefixes { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "updates", "appearance", "status", "view", "fullscreen", "settings", "keyboard", "escape", "mouse", "selection", "developer", "overlay", "gesture"
    };

    /// <summary>Non-action settings whose apply semantics differ from their prefix default.</summary>
    public static IReadOnlyDictionary<string, SettingApplySemantics> SemanticsExceptions { get; } = new Dictionary<string, SettingApplySemantics>(StringComparer.OrdinalIgnoreCase)
    {
        ["navigation.sortOrder"] = SettingApplySemantics.LivePreview,
        ["navigation.sortDescending"] = SettingApplySemantics.LivePreview,
        ["folderNav.showCounter"] = SettingApplySemantics.LivePreview,
        ["folderNav.counterGrandparent"] = SettingApplySemantics.LivePreview,
        ["windows.alwaysOnTop"] = SettingApplySemantics.LivePreview,
        ["windows.alwaysOnTopMode"] = SettingApplySemantics.LivePreview,
        ["window.showInExplorerTarget"] = SettingApplySemantics.LivePreview,
        ["tabs.tooltipTextSize"] = SettingApplySemantics.LivePreview,
        ["tabs.position"] = SettingApplySemantics.LivePreview,
        ["tabs.sideAutoHide"] = SettingApplySemantics.LivePreview,
        ["tabs.topAutoHide"] = SettingApplySemantics.LivePreview,
        ["view.animateGifs"] = SettingApplySemantics.CommitOnly,
        ["developer.reverseSelectionZoomOut"] = SettingApplySemantics.CommitOnly,
        ["developer.startupDiagnostics"] = SettingApplySemantics.CommitOnly,
        ["overlay.wholeAppAlwaysStart"] = SettingApplySemantics.CommitOnly,
        ["overlay.defaultDirectory"] = SettingApplySemantics.CommitOnly,
    };

    public static IReadOnlyList<SettingEffectBinding> All { get; } = SettingsCatalog.All.Select(Derive).ToArray();

    private static readonly Dictionary<string, SettingEffectBinding> ById =
        All.GroupBy(x => x.SettingId, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

    /// <summary>The id's first segment ("status" for "status.zoom").</summary>
    public static string PrefixOf(string settingId)
    {
        var dot = settingId.IndexOf('.', StringComparison.Ordinal);
        return dot < 0 ? settingId : settingId[..dot];
    }

    private static SettingEffectBinding Derive(SettingDefinition setting)
    {
        var id = setting.Id;
        var evidence = "settings.effect." + id;
        if (setting.FuturePhase is not null)
            return new(id, SettingEffectOwner.FutureDisabled, SettingEditorKind.DisabledFuture, SettingApplySemantics.FutureDisabled, evidence);
        var prefix = PrefixOf(id);
        var owner = OwnerExceptions.TryGetValue(id, out var o) ? o
            : OwnerByPrefix.TryGetValue(prefix, out var p) ? p : SettingEffectOwner.None;
        var isAction = setting.Kind == SettingKind.Action;
        var editor = isAction ? SettingEditorKind.DynamicAction : SettingEditorKind.FromCatalog;
        SettingApplySemantics semantics;
        if (isAction) semantics = string.Equals(prefix, "gesture", StringComparison.OrdinalIgnoreCase) ? SettingApplySemantics.LivePreview : SettingApplySemantics.Action;
        else if (SemanticsExceptions.TryGetValue(id, out var s)) semantics = s;
        else semantics = LivePreviewPrefixes.Contains(prefix) ? SettingApplySemantics.LivePreview : SettingApplySemantics.CommitOnly;
        return new(id, owner, editor, semantics, evidence);
    }

    public static IReadOnlyList<string> Validate()
    {
        var issues = new List<string>();
        var duplicateIds = All.GroupBy(x => x.SettingId, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() != 1).Select(g => g.Key);
        issues.AddRange(duplicateIds.Select(id => $"registry duplicate: {id}"));
        var catalogIds = SettingsCatalog.All.Select(x => x.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        issues.AddRange(OwnerExceptions.Keys.Concat(SemanticsExceptions.Keys).Where(id => !catalogIds.Contains(id)).Select(id => $"registry exception for unknown setting: {id}"));
        issues.AddRange(All.Where(x => !catalogIds.Contains(x.SettingId)).Select(x => $"registry unknown: {x.SettingId}"));
        issues.AddRange(catalogIds.Where(id => !All.Any(x => string.Equals(x.SettingId, id, StringComparison.OrdinalIgnoreCase))).Select(id => $"registry missing: {id}"));
        foreach (var row in All)
        {
            var catalog = SettingsCatalog.All.FirstOrDefault(x => string.Equals(x.Id, row.SettingId, StringComparison.OrdinalIgnoreCase));
            if (catalog is null) continue;
            if (catalog.FuturePhase is not null)
            {
                if (row.ApplySemantics != SettingApplySemantics.FutureDisabled || row.EditorKind != SettingEditorKind.DisabledFuture || row.EffectOwner != SettingEffectOwner.FutureDisabled)
                    issues.Add($"future setting is not disabled: {row.SettingId}");
            }
            else
            {
                if (row.EffectOwner is SettingEffectOwner.None or SettingEffectOwner.FutureDisabled)
                    issues.Add($"active setting has no effect owner: {row.SettingId}");
                if (row.EditorKind == SettingEditorKind.DisabledFuture || row.ApplySemantics == SettingApplySemantics.FutureDisabled)
                    issues.Add($"active setting is disabled: {row.SettingId}");
            }
            if (string.IsNullOrWhiteSpace(row.EvidenceId)) issues.Add($"missing evidence id: {row.SettingId}");
        }
        return issues;
    }

    public static SettingEffectBinding For(string settingId) =>
        ById.TryGetValue(settingId, out var binding) ? binding : throw new InvalidOperationException($"Unknown setting id: {settingId}");

    /// <summary>The binding for <paramref name="settingId"/>, or null when the id is not catalogued.</summary>
    public static SettingEffectBinding? Find(string settingId) => ById.GetValueOrDefault(settingId);
}
