using System.Xml.Linq;

namespace Glide.Core.Tests;

/// <summary>
/// The overlay animation controls used to live in their own Settings tab. They only affect
/// Window-in-Window overlays, so they live in the "Overlay animation" card of the Slideshow &amp;
/// overlays page and the standalone Animation tab/category is gone.
/// </summary>
// Source-text contract kept: this asserts on production source (UI code-behind, native/installer files or a repo-wide scan) that cannot be run headlessly; behavioural coverage lives in the other test files.
public sealed class SettingsAnimationSectionTests
{
    private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";

    [Fact]
    public void Animation_tab_is_removed_and_controls_live_in_the_overlay_animation_card()
    {
        var axaml = ReadSource("src", "Glide.App", "Settings", "SettingsWindow.axaml").Replace("\r\n", "\n");

        Assert.DoesNotContain("x:Name=\"AnimationPanel\"", axaml, StringComparison.Ordinal);
        Assert.DoesNotContain("Tag=\"Animation\"", axaml, StringComparison.Ordinal);

        var document = XDocument.Parse(axaml);
        var slideshowPanel = document.Descendants().SingleOrDefault(e => (string?)e.Attribute(X + "Name") == "SlideshowPanel");
        Assert.True(slideshowPanel is not null, "The Slideshow & overlays page (SlideshowPanel) must exist.");
        var card = slideshowPanel!.Descendants().SingleOrDefault(e => e.Name.LocalName == "SettingsCard" && (string?)e.Attribute("CardId") == "slideshow-animation");
        Assert.True(card is not null, "The Overlay animation card (CardId=slideshow-animation) must be on the Slideshow & overlays page.");

        var names = card!.Descendants().Select(e => (string?)e.Attribute(X + "Name")).Where(n => n is not null).ToHashSet(StringComparer.Ordinal);
        Assert.Contains("OverlayAnimationRegionCombo", names);
        Assert.Contains("OverlayAnimationSpeedBox", names);
        Assert.Contains("OverlayAnimationRefreshRateCombo", names);
    }

    [Fact]
    public void Animation_settings_are_categorised_under_slideshow_and_overlays()
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
            var marker = $"new(\"{id}\", \"Slideshow & overlays\"";
            Assert.Contains(marker, catalog, StringComparison.Ordinal);
        }
    }

    private static string ReadSource(params string[] segments)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(new[] { directory.FullName }.Concat(segments).ToArray());
            if (File.Exists(candidate)) return SourceText.Read(candidate);
            directory = directory.Parent;
        }
        throw new FileNotFoundException("Could not locate source file.", Path.Combine(segments));
    }
}
