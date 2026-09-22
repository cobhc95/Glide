using Glide.App.Printing;

namespace Glide.Core.Tests;

/// <summary>
/// Print-layout preferences persist across sessions without touching printer-specific
/// capabilities (paper/source names are never stored).
/// </summary>
public sealed class PrintSettingsStoreTests
{
    [Fact]
    public void Defaults_match_the_documented_stock_policy()
    {
        var settings = new PrintDocumentSettings();
        Assert.Equal("Best fit", settings.ScalingMode);
        Assert.True(settings.KeepAspectRatio);
        Assert.True(settings.AutoRotate);
        Assert.Equal("Centre", settings.HAlign);
        Assert.Equal("Centre", settings.VAlign);
        Assert.Equal("Portrait", settings.Orientation);
        Assert.Equal(50, settings.MarginLeft);
        Assert.Equal(100, settings.CustomScalePercent);
        Assert.Equal(1, settings.Copies);
    }

    [Fact]
    public void Roundtrip_preserves_layout_prefs_and_never_stores_printer_names()
    {
        var dir = Path.Combine(Path.GetTempPath(), "glide-print-settings-" + Guid.NewGuid().ToString("N"));
        PrintSettingsStore.DirectoryOverrideForTests = dir;
        try
        {
            var saved = new PrintDocumentSettings
            {
                ScalingMode = "Fill page",
                KeepAspectRatio = false,
                AutoRotate = false,
                HAlign = "Right",
                VAlign = "Bottom",
                Orientation = "Landscape",
                MarginLeft = 10, MarginTop = 20, MarginRight = 30, MarginBottom = 40,
                CustomScalePercent = 150,
                Copies = 3
            };
            PrintSettingsStore.Save(saved);
            var loaded = PrintSettingsStore.Load();
            Assert.Equal("Fill page", loaded.ScalingMode);
            Assert.False(loaded.KeepAspectRatio);
            Assert.False(loaded.AutoRotate);
            Assert.Equal("Right", loaded.HAlign);
            Assert.Equal("Bottom", loaded.VAlign);
            Assert.Equal("Landscape", loaded.Orientation);
            Assert.Equal(10, loaded.MarginLeft);
            Assert.Equal(150, loaded.CustomScalePercent);
            Assert.Equal(3, loaded.Copies);
            var json = File.ReadAllText(Path.Combine(dir, "print.settings.json"));
            Assert.DoesNotContain("PaperName", json, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("PrinterName", json, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            PrintSettingsStore.DirectoryOverrideForTests = null;
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    [Fact]
    public void Corrupt_or_unknown_values_fall_back_safely()
    {
        var dir = Path.Combine(Path.GetTempPath(), "glide-print-settings-" + Guid.NewGuid().ToString("N"));
        PrintSettingsStore.DirectoryOverrideForTests = dir;
        try
        {
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "print.settings.json"),
                "{ \"ScalingMode\": \"Bogus\", \"Copies\": 500, \"MarginLeft\": -40, \"CustomScalePercent\": 0 }");
            var loaded = PrintSettingsStore.Load();
            Assert.Equal("Best fit", loaded.ScalingMode);
            Assert.Equal(99, loaded.Copies);
            Assert.Equal(0, loaded.MarginLeft);
            Assert.Equal(1, loaded.CustomScalePercent);
        }
        finally
        {
            PrintSettingsStore.DirectoryOverrideForTests = null;
            try { Directory.Delete(dir, true); } catch { }
        }
    }
}
