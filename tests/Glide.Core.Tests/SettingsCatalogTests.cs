using Glide.Core.Settings;
using Glide.App.Settings;
using Xunit;

namespace Glide.Core.Tests;

public sealed class SettingsCatalogTests
{
    [Fact]
    public void SettingIds_AreUnique()
    {
        var duplicate = SettingsCatalog.All.GroupBy(x => x.Id, StringComparer.OrdinalIgnoreCase).FirstOrDefault(g => g.Count() > 1);
        Assert.Null(duplicate);
    }

    [Fact]
    public void EverySetting_IsSearchableAndDocumented()
    {
        Assert.All(SettingsCatalog.All, setting =>
        {
            Assert.False(string.IsNullOrWhiteSpace(setting.Id));
            Assert.False(string.IsNullOrWhiteSpace(setting.Category));
            Assert.False(string.IsNullOrWhiteSpace(setting.Label));
            Assert.False(string.IsNullOrWhiteSpace(setting.Description));
            Assert.NotEmpty(setting.SearchTerms);
        });
    }

    [Fact]
    public void Glide30_catalog_has_166_current_and_no_future_entries()
    {
        Assert.Equal(166, SettingsCatalog.All.Count);
        Assert.Equal(166, SettingsCatalog.All.Count(x => x.FuturePhase is null));
        Assert.Empty(SettingsCatalog.All.Where(x => x.FuturePhase is not null));
        Assert.Contains(SettingsCatalog.All, x => x.Id == "keyboard.tabFocusNavigation" && x.FuturePhase is null && x.DefaultValue is bool tabNav && !tabNav);
        Assert.Contains(SettingsCatalog.All, x => x.Id == "performance.compressedCacheMb" && x.FuturePhase is null);
        Assert.Contains(SettingsCatalog.All, x => x.Id == "performance.decodedCacheMb" && x.FuturePhase is null);
        Assert.Contains(SettingsCatalog.All, x => x.Id == "performance.sequentialReads" && x.FuturePhase is null);
        Assert.Contains(SettingsCatalog.All, x => x.Id == "overlay.wholeAppAlwaysStart" && x.FuturePhase is null && x.DefaultValue is bool enabled && !enabled);
        var progressive = Assert.Single(SettingsCatalog.All, x => x.Id == "performance.progressiveColor");
        Assert.Null(progressive.FuturePhase);
        Assert.True((bool)progressive.DefaultValue);
        Assert.Contains("black-and-white", progressive.Description, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(SettingsCatalog.All, x => x.Id == "appearance.glowColor" && x.FuturePhase is null);
        Assert.Contains(SettingsCatalog.All, x => x.Id == "performance.prefetchSiblingFolders" && x.FuturePhase is null);
        Assert.Contains(SettingsCatalog.All, x => x.Id == "developer.statusGlobalScale" && x.FuturePhase is null);
        Assert.Contains(SettingsCatalog.All, x => x.Id == "developer.overlayAbsoluteCoordinates" && x.FuturePhase is null);
    }

    [Fact]
    public void Parity_settings_include_hidden_sibling_policy_and_scaling_quality()
    {
        Assert.Contains(SettingsCatalog.All, setting => setting.Id.Equals("folderNav.includeHidden", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(SettingsCatalog.All, setting => setting.Id.Equals("performance.initialQuality", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void SettingsState_semantic_equality_ignores_clone_collection_identity()
    {
        var committed = new GlideSettingsState();
        var staged = committed.CloneState();

        Assert.True(committed.ContentEquals(staged));

        staged.TitleBarButtons = staged.TitleBarButtons.ToList();
        staged.Hotkeys = staged.Hotkeys.ToDictionary(x => x.Key, x => x.Value.ToList(), StringComparer.OrdinalIgnoreCase);
        Assert.True(committed.ContentEquals(staged));
    }

    [Fact]
    public void SettingsState_semantic_equality_detects_real_content_changes()
    {
        var committed = new GlideSettingsState();

        var scalarChange = committed.CloneState();
        scalarChange.ShowHomeTips = !scalarChange.ShowHomeTips;
        Assert.False(committed.ContentEquals(scalarChange));

        var hotkeyChange = committed.CloneState();
        var hotkey = hotkeyChange.Hotkeys.First();
        hotkeyChange.Hotkeys[hotkey.Key] = [.. hotkey.Value, "Ctrl+Alt+Shift+F12"];
        Assert.False(committed.ContentEquals(hotkeyChange));

        var gestureChange = committed.CloneState();
        var gesture = gestureChange.Gestures.First();
        gestureChange.Gestures[gesture.Key] = gesture.Value + "__changed";
        Assert.False(committed.ContentEquals(gestureChange));

        var titleBarChange = committed.CloneState();
        titleBarChange.TitleBarButtons = titleBarChange.TitleBarButtons.Skip(1).ToList();
        Assert.False(committed.ContentEquals(titleBarChange));

        var normalizedUnknownTitleBarId = committed.CloneState();
        normalizedUnknownTitleBarId.TitleBarButtons = [.. normalizedUnknownTitleBarId.TitleBarButtons, "__unknown_invalid_id"];
        Assert.True(committed.ContentEquals(normalizedUnknownTitleBarId));
    }
}

public sealed class SettingEffectRegistryTests
{
    [Fact]
    public void Registry_is_complete_unique_and_well_formed()
    {
        var issues = SettingEffectRegistry.Validate();

        Assert.Empty(issues);
        Assert.Equal(SettingsCatalog.All.Count, SettingEffectRegistry.All.Count);
        Assert.Equal(SettingsCatalog.All.Count, SettingEffectRegistry.All.Select(x => x.SettingId).Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public void Every_active_setting_has_owner_and_editor_or_action()
    {
        var active = SettingEffectRegistry.All.Where(row => SettingsCatalog.All.Single(x => x.Id.Equals(row.SettingId, StringComparison.OrdinalIgnoreCase)).FuturePhase is null);

        Assert.All(active, row =>
        {
            Assert.NotEqual(SettingEffectOwner.None, row.EffectOwner);
            Assert.NotEqual(SettingEffectOwner.FutureDisabled, row.EffectOwner);
            Assert.NotEqual(SettingEditorKind.DisabledFuture, row.EditorKind);
            Assert.NotEqual(SettingApplySemantics.FutureDisabled, row.ApplySemantics);
            Assert.False(string.IsNullOrWhiteSpace(row.EvidenceId));
        });
    }

    [Fact]
    public void Future_settings_are_explicitly_disabled()
    {
        var future = SettingsCatalog.All.Where(x => x.FuturePhase is not null).ToArray();

        Assert.Empty(future);
    }

    [Fact]
    public void Dynamic_entries_are_actions_with_evidence()
    {
        var dynamicIds = new[]
        {
            "developer.diagnostics", "escape.resetRememberedClose", "hotkeys.registry",
            "mouse.gestureMatrix", "profiles.export", "profiles.import", "profiles.presets",
            "titlebar.customize", "windows.fileAssociations"
        };

        Assert.All(dynamicIds, id =>
        {
            var row = SettingEffectRegistry.For(id);
            var expectedFuture = SettingsCatalog.All.Single(x => x.Id == id).FuturePhase is not null;
            Assert.Equal(expectedFuture ? SettingEditorKind.DisabledFuture : SettingEditorKind.DynamicAction, row.EditorKind);
        });
        Assert.All(dynamicIds.Where(id => SettingsCatalog.All.Single(x => x.Id == id).FuturePhase is null), id =>
        {
            var row = SettingEffectRegistry.For(id);
            Assert.Equal(SettingEditorKind.DynamicAction, row.EditorKind);
            Assert.Equal(SettingApplySemantics.Action, row.ApplySemantics);
        });
    }

    [Fact]
    public void Registry_evidence_defaults_to_unknown_until_runtime_proof_exists()
    {
        Assert.All(SettingEffectRegistry.All, row => Assert.Equal(SettingEvidenceStatus.Unknown, row.EvidenceStatus));
    }
}

