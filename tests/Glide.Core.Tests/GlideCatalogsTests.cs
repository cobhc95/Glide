using Glide.Core.Data;
using Glide.Core.Reset;

namespace Glide.Core.Tests;

[Trait("Area", "Settings")]
public sealed class GlideCatalogsTests
{
    [Fact]
    public void Every_catalogue_builds_in_the_documented_order_and_validates()
    {
        GlideCatalogs.Build();
        Assert.Empty(GlideCatalogs.Validate());
    }

    [Fact]
    public void Reset_reads_the_data_property_groups_one_way()
    {
        // ResetCatalog keeps its old members as forwards; both names must give the same lists.
        Assert.Same(GlideSettingsPropertyGroups.AllSettingsKeptProperties, ResetCatalog.AllSettingsKeptProperties);
        Assert.Same(GlideSettingsPropertyGroups.HotkeyGestureProperties, ResetCatalog.HotkeyGestureProperties);
        Assert.Equal(GlideSettingsPropertyGroups.SettingsSchemaVersionProperty, ResetCatalog.SettingsSchemaVersionProperty);
        Assert.DoesNotContain("PerformanceUserCustomSaved", GlideDataCategoryCatalog.AllSettingsBackupExcludedProperties);
        Assert.Contains("Hotkeys", GlideDataCategoryCatalog.AllSettingsBackupExcludedProperties);
        Assert.Equal(GlideDataCategoryCatalog.With(GlideDataCapabilities.Reset).Select(x => x.Id), ResetCatalog.Items.Select(x => x.Id));
    }

    [Fact]
    public void Core_Data_does_not_depend_on_Core_Reset()
    {
        var data = typeof(GlideDataCategoryCatalog).Assembly.GetTypes().Where(t => t.Namespace == "Glide.Core.Data");
        foreach (var type in data)
        {
            var members = type.GetMembers(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.DeclaredOnly);
            foreach (var member in members)
            {
                var memberType = member switch
                {
                    System.Reflection.PropertyInfo p => p.PropertyType,
                    System.Reflection.FieldInfo f => f.FieldType,
                    System.Reflection.MethodInfo m => m.ReturnType,
                    _ => null,
                };
                Assert.False(memberType?.Namespace == "Glide.Core.Reset", $"{type.Name}.{member.Name} exposes a Glide.Core.Reset type");
            }
        }
    }
}
