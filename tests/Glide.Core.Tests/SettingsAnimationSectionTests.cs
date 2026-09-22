namespace Glide.Core.Tests;

/// <summary>
/// The overlay animation controls used to live in their own Settings tab. They only affect
/// Window-in-Window overlays, so they now live inside the Window in Window (Overlays) section and
/// the standalone Animation tab/category is gone.
/// </summary>
public sealed class SettingsAnimationSectionTests
{
    [Fact]
    public void Animation_tab_is_removed_and_controls_live_in_the_window_in_window_section()
    {
        var axaml = ReadSource("src", "Glide.App", "Settings", "SettingsWindow.axaml").Replace("\r\n", "\n");

        Assert.DoesNotContain("x:Name=\"AnimationPanel\"", axaml, StringComparison.Ordinal);
        Assert.DoesNotContain("Tag=\"Animation\"", axaml, StringComparison.Ordinal);

        var overlaysStart = axaml.IndexOf("x:Name=\"OverlaysPanel\"", StringComparison.Ordinal);
        Assert.True(overlaysStart >= 0, "The Window in Window (Overlays) panel must exist.");
        var overlaysEnd = axaml.IndexOf("</StackPanel>", overlaysStart, StringComparison.Ordinal);
        Assert.True(overlaysEnd > overlaysStart, "The Overlays panel must be a closed panel.");
        var overlays = axaml.Substring(overlaysStart, overlaysEnd - overlaysStart);

        Assert.Contains("OverlayAnimationRegionCombo", overlays, StringComparison.Ordinal);
        Assert.Contains("OverlayAnimationSpeedBox", overlays, StringComparison.Ordinal);
        Assert.Contains("OverlayAnimationRefreshRateCombo", overlays, StringComparison.Ordinal);
    }

    [Fact]
    public void Animation_settings_are_categorised_under_window_in_window()
    {
        var catalog = ReadSource("src", "Glide.Core", "Settings", "SettingsCatalog.cs");
        Assert.DoesNotContain("\"Animation\"", catalog, StringComparison.Ordinal);

        foreach (var id in new[]
        {
            "overlay.animationRegion",
            "overlay.animationSpeedDipsPerSecond",
            "overlay.animationTurnIntervalMs",
            "overlay.animationTurnAngleDegrees",
            "overlay.animationPauseWhileInteracting",
            "overlay.animationAvoidOverlap",
            "overlay.animationRefreshRateHz"
        })
        {
            var marker = $"new(\"{id}\", \"Window in Window\"";
            Assert.Contains(marker, catalog, StringComparison.Ordinal);
        }
    }

    private static string ReadSource(params string[] segments)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(new[] { directory.FullName }.Concat(segments).ToArray());
            if (File.Exists(candidate)) return File.ReadAllText(candidate);
            directory = directory.Parent;
        }
        throw new FileNotFoundException("Could not locate source file.", Path.Combine(segments));
    }
}
