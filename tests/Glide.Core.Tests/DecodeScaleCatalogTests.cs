using Glide.Core.Settings;

namespace Glide.Core.Tests;

/// <summary>5.1.0: the preset -> per-format decode scale / full-refine master table.</summary>
[Trait("Area", "Decode")]
public sealed class DecodeScaleCatalogTests
{
    [Fact]
    public void EveryPresetHasEveryFamily_WithScalesInRange()
    {
        foreach (var preset in DecodeScaleCatalog.Presets)
            foreach (var family in Enum.GetValues<DecodeFormatFamily>())
            {
                var row = DecodeScaleCatalog.Get(preset, family);
                Assert.InRange(row.FirstViewScale, DecodeScaleCatalog.MinScale, DecodeScaleCatalog.MaxScale);
                Assert.InRange(row.PrefetchScale, DecodeScaleCatalog.MinScale, row.FirstViewScale);
            }
    }

    [Fact]
    public void MaximumQuality_IsViewportExact_AndNeverRefinesFullImmediately()
    {
        var row = DecodeScaleCatalog.Get("Maximum quality", DecodeFormatFamily.Jpeg);
        Assert.Equal(1.0, row.FirstViewScale);
        Assert.Equal(FullRefinePolicy.Idle, row.Refine);
        Assert.Equal(FullRefinePolicy.OnZoom, DecodeScaleCatalog.Get("Maximum speed", DecodeFormatFamily.Jpeg).Refine);
    }

    [Fact]
    public void Overrides_ReplacePresetValues_ZeroKeepsPreset()
    {
        var keep = DecodeScaleCatalog.Resolve("Balanced", DecodeFormatFamily.Wic, 0, 0, "Preset default");
        Assert.Equal(DecodeScaleCatalog.Get("Balanced", DecodeFormatFamily.Wic), keep);
        var custom = DecodeScaleCatalog.Resolve("Balanced", DecodeFormatFamily.Wic, 40, 500, "Immediately");
        Assert.Equal(0.4, custom.FirstViewScale, 3);
        Assert.Equal(1.0, custom.PrefetchScale, 3);
        Assert.Equal(FullRefinePolicy.Immediate, custom.Refine);
    }

    [Theory]
    [InlineData("a.JPG", DecodeFormatFamily.Jpeg)]
    [InlineData("a.tiff", DecodeFormatFamily.Wic)]
    [InlineData("a.heic", DecodeFormatFamily.Heif)]
    [InlineData("a.avif", DecodeFormatFamily.Heif)]
    [InlineData("a.cr3", DecodeFormatFamily.Raw)]
    [InlineData("a.psd", DecodeFormatFamily.Other)]
    public void FamilyOf_MapsExtensions(string path, DecodeFormatFamily expected) =>
        Assert.Equal(expected, DecodeScaleCatalog.FamilyOf(path));
}
