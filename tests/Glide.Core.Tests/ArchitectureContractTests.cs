using Glide.Core.Settings;
using Xunit;

namespace Glide.Core.Tests;

/// <summary>
/// Stage C: the settings schema (GlideSettingsState and its catalogues) lives in Glide.Core, which must stay UI-free so a
/// pure Glide.Core.Tests / Glide.App.Tests split stays possible.
/// </summary>
[Trait("Area", "Settings")]
public sealed class ArchitectureContractTests
{
    [Fact]
    public void Glide_Core_references_neither_Avalonia_nor_Glide_App()
    {
        var references = typeof(GlideSettingsState).Assembly.GetReferencedAssemblies().Select(a => a.Name ?? string.Empty).ToArray();
        Assert.Equal("Glide.Core", typeof(GlideSettingsState).Assembly.GetName().Name);
        Assert.DoesNotContain(references, name => name.StartsWith("Avalonia", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(references, name => name.StartsWith("Glide.App", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Platform_availability_defaults_to_all_and_marks_the_windows_shell_settings()
    {
        var ids = SettingsCatalog.All.Select(s => s.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        Assert.All(SettingsCatalog.WindowsOnlyIds, id => Assert.Contains(id, ids));
        Assert.All(SettingsCatalog.All, s => Assert.Equal(SettingsCatalog.WindowsOnlyIds.Contains(s.Id) ? SettingPlatforms.Windows : SettingPlatforms.All, s.Platforms));
        Assert.Equal(SettingPlatforms.Windows, SettingsCatalog.All.Single(s => s.Id == "windows.fileAssociations").Platforms);
        Assert.Equal(SettingPlatforms.All, SettingsCatalog.All.Single(s => s.Id == "status.visible").Platforms);
    }

    [Fact]
    public void Settings_schema_types_live_in_Glide_Core()
    {
        foreach (var type in new[] { typeof(GlideSettingsState), typeof(WindowPlacementState), typeof(OverlayWindowPlacementState), typeof(TitleBarButtonCatalog), typeof(ShowInExplorerTargets) })
            Assert.Equal("Glide.Core", type.Assembly.GetName().Name);
    }
}
