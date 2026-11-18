using Glide.Core.Commands;
using Glide.Core.Data;
using Glide.Core.Reset;
using Glide.Core.Settings;

namespace Glide.Core;

/// <summary>
/// The one place that states the build order of the Core catalogues and validates them together (Stage B, audit C15).
/// <para>
/// Order (each step only reads the steps before it):
/// 1. <see cref="GestureCatalog.Slots"/> (gesture rows, plain data)
/// 2. <see cref="SettingActionCatalog"/> (which settings get generated actions, plain data)
/// 3. <see cref="SettingsCatalog.All"/> (static rows + one row per gesture slot)
/// 4. <see cref="AppActionRegistry.All"/> (commands + generated "Toggle:/Cycle:" setting actions), then
///    <see cref="HotkeyCatalog"/> (a projection of the registry)
/// 5. <see cref="SettingEffectRegistry.All"/> (one binding per settings row)
/// 6. <see cref="GlideSettingsPropertyGroups"/> and <see cref="GlideDataCategoryCatalog.All"/> (Core.Data, reads nothing above)
/// 7. <see cref="ResetCatalog.Items"/> (reads Core.Data one way)
/// </para>
/// No catalogue validates itself in a static constructor any more: <see cref="Validate"/> runs in tests and diagnostics, so a
/// catalogue mistake is a failing test, never a TypeInitializationException at first use in the app.
/// </summary>
public static class GlideCatalogs
{
    /// <summary>Builds every catalogue in the documented order (idempotent; each list is built once per process).</summary>
    public static void Build()
    {
        _ = GestureCatalog.Slots;
        _ = SettingActionCatalog.Excluded;
        _ = SettingsCatalog.All;
        _ = AppActionRegistry.All;
        _ = HotkeyCatalog.All;
        _ = SettingEffectRegistry.All;
        _ = GlideSettingsPropertyGroups.AllSettingsKeptProperties;
        _ = GlideDataCategoryCatalog.All;
        _ = GlideDataCategoryCatalog.AllSettingsBackupExcludedProperties;
        _ = ResetCatalog.Items;
    }

    /// <summary>Every catalogue contract issue (empty = consistent).</summary>
    public static IReadOnlyList<string> Validate()
    {
        Build();
        return AppActionRegistry.Validate().Concat(SettingEffectRegistry.Validate()).ToArray();
    }
}
