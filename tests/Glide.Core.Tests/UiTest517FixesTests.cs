using Glide.Core.Commands;

namespace Glide.Core.Tests;

/// <summary>Source-level guards for the fixes from the 5.1.7 desktop UI test (docs/feedback/ui-test-5.1.7-desktop.md).</summary>
[Trait("Area", "Regression")]
public sealed class UiTest517FixesTests
{
    /// <summary>Bug 4: the tag sidebar action has a default key, and no other hotkey or fixed shortcut uses it.</summary>
    [Fact]
    public void Tag_sidebar_has_a_default_hotkey_nothing_else_uses()
    {
        var action = AppActionRegistry.Find("tags.toggleSidebar")!;
        var key = Assert.Single(HotkeyCatalog.All.Single(h => h.ActionId == "tags.toggleSidebar").DefaultShortcuts);
        Assert.Equal("Ctrl+Shift+G", key);
        Assert.NotNull(action);
        var others = HotkeyCatalog.All.Where(h => h.ActionId != "tags.toggleSidebar")
            .Where(h => h.DefaultShortcuts.Any(s => string.Equals(s, key, StringComparison.OrdinalIgnoreCase))).Select(h => h.Id)
            .Concat(HotkeyCatalog.FixedShortcuts.Where(f => f.Shortcut.Split(" / ").Any(s => string.Equals(s.Trim(), key, StringComparison.OrdinalIgnoreCase))).Select(f => f.Id));
        Assert.Empty(others);
        Assert.Empty(AppActionRegistry.Validate());
    }
    /// <summary>Bug 10: a huge X offset is clamped to a few pixels inside the picture area, never flush against the window edge.</summary>
    [Theory]
    [InlineData("Top right", 5000, 0)]
    [InlineData("Top right", -5000, 0)]
    [InlineData("Top left", 5000, 0)]
    [InlineData("Top center", -5000, 0)]
    [InlineData("Bottom right", 5000, 5000)]
    [InlineData("Bottom left", -5000, -5000)]
    public void A_clamped_text_overlay_keeps_a_gap_from_every_edge(string anchor, int x, int y)
    {
        const double areaW = 1200, areaH = 800, w = 140, h = 40, side = 15, top = 28, bottom = 40, inset = 4;
        var (cx, cy) = Glide.Core.Settings.TextOverlayLayout.ClampOffset(anchor, areaW, areaH, w, h, x, y, side, top, bottom, inset);
        var px = anchor.Contains("left") ? side : anchor.Contains("center") ? (areaW - w) / 2 : areaW - side - w;
        var py = anchor.StartsWith("Bottom") ? areaH - bottom - h : top;
        var left = px + cx; var topEdge = py + cy;
        Assert.InRange(left, inset - 0.5, areaW - w - inset + 0.5);
        Assert.InRange(topEdge, inset - 0.5, areaH - h - inset + 0.5);
        // Without the inset (the old behaviour) the box could touch the edge: that is what cut the last glyph.
        var (ox, _) = Glide.Core.Settings.TextOverlayLayout.ClampOffset(anchor, areaW, areaH, w, h, x, y, side, top, bottom);
        Assert.True(px + ox <= areaW - w + 0.5 && px + ox >= -0.5);
    }

    [Fact]
    public void The_clamp_inset_gives_way_when_the_overlay_nearly_fills_the_area()
    {
        Assert.Equal(0, Glide.Core.Settings.TextOverlayLayout.ClampInside(-50, 100, 100, 4));
        Assert.Equal(1, Glide.Core.Settings.TextOverlayLayout.ClampInside(-50, 102, 100, 4));
        Assert.Equal(4, Glide.Core.Settings.TextOverlayLayout.ClampInside(-50, 300, 100, 4));
        Assert.Equal(196, Glide.Core.Settings.TextOverlayLayout.ClampInside(500, 300, 100, 4));
    }

    /// <summary>Bug 10: the window applies the inset to the saved offset and to Alt+drag.</summary>
    [Fact]
    public void The_window_passes_the_clamp_inset_when_it_places_and_drops_the_overlay()
    {
        var placement = SourceText.Read("src/Glide.App/MainWindow.TextOverlayPlacement.cs");
        var drag = SourceText.Read("src/Glide.App/MainWindow.TextOverlayDrag.cs");
        Assert.Contains("TextOverlayBottomInset(), TextOverlayClampInset)", placement);
        Assert.Contains("TextOverlayBottomInset(), TextOverlayClampInset)", drag);
    }

    /// <summary>Bug 9: the Text overlay template description lists the documented short tokens and none of the old long names.</summary>
    [Fact]
    public void The_template_setting_description_lists_the_short_tokens_only()
    {
        var description = Glide.Core.Settings.SettingsCatalog.All.Single(s => s.Id == "overlay.pictureTemplate").Description;
        foreach (var (token, _) in Glide.Core.Status.TextTemplate.Tokens) Assert.Contains(token, description);
        foreach (var old in new[] { "{extension}", "{width}", "{height}", "{dimensions}", "{megapixels}", "{filesizebytes}", "{filename}", "{datemodified}", "{datetaken}" })
            Assert.DoesNotContain(old, description);
        Assert.Contains("{modified:yyyy/MM/dd HH:mm}", description);
    }
}
