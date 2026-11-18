using Glide.Core.Status;

namespace Glide.Core.Tests;

[Trait("Area", "StatusBar")]
public sealed class StatusChromePolicyTests
{
    private static StatusChromeInputs Base => new(
        WholeAppOverlay: false, ShowStatusSurface: true, AutoHide: false, Collapsed: false, SessionClosed: false,
        Fullscreen: false, FullscreenAlwaysOn: false, HoverRevealed: false, ImageVisible: true, HomeVisible: false);

    [Fact]
    public void Windowed_expanded_bar_shows_on_the_visible_surface_only()
    {
        Assert.Equal(new StatusChromeVisibility(true, false, false), StatusChromePolicy.Decide(Base));
        Assert.Equal(new StatusChromeVisibility(false, true, false), StatusChromePolicy.Decide(Base with { ImageVisible = false, HomeVisible = true }));
    }

    [Fact]
    public void Windowed_collapse_keeps_the_bar_as_its_own_expand_button_and_no_restore_arrow()
        => Assert.Equal(new StatusChromeVisibility(true, false, false), StatusChromePolicy.Decide(Base with { Collapsed = true }));

    [Fact]
    public void Closed_bar_and_whole_app_overlay_hide_everything()
    {
        Assert.Equal(default, StatusChromePolicy.Decide(Base with { SessionClosed = true }));
        Assert.Equal(default, StatusChromePolicy.Decide(Base with { WholeAppOverlay = true, Fullscreen = true }));
        Assert.Equal(default, StatusChromePolicy.Decide(Base with { ShowStatusSurface = false }));
    }

    [Fact]
    public void Fullscreen_suppresses_the_bar_and_leaves_the_corner_restore_button()
    {
        Assert.Equal(new StatusChromeVisibility(false, false, true), StatusChromePolicy.Decide(Base with { Fullscreen = true }));
        Assert.Equal(new StatusChromeVisibility(true, false, false), StatusChromePolicy.Decide(Base with { Fullscreen = true, FullscreenAlwaysOn = true }));
        // always-on but collapsed: no bar, restore button again
        Assert.Equal(new StatusChromeVisibility(false, false, true), StatusChromePolicy.Decide(Base with { Fullscreen = true, FullscreenAlwaysOn = true, Collapsed = true }));
        // home surface never shows the bar in fullscreen without auto-hide
        Assert.Equal(new StatusChromeVisibility(false, false, false), StatusChromePolicy.Decide(Base with { Fullscreen = true, FullscreenAlwaysOn = true, ImageVisible = false, HomeVisible = true }));
    }

    [Fact]
    public void Auto_hide_replaces_collapse_and_shows_only_when_revealed()
    {
        Assert.Equal(default, StatusChromePolicy.Decide(Base with { AutoHide = true, Collapsed = true }));
        Assert.Equal(new StatusChromeVisibility(true, false, false), StatusChromePolicy.Decide(Base with { AutoHide = true, Collapsed = true, HoverRevealed = true }));
        Assert.Equal(new StatusChromeVisibility(false, true, false), StatusChromePolicy.Decide(Base with { AutoHide = true, Fullscreen = true, HoverRevealed = true, ImageVisible = false, HomeVisible = true }));
        Assert.False(StatusChromePolicy.CollapsedNow(collapsed: true, autoHide: true));
    }

    [Fact]
    public void Collapsed_look_is_compact_in_the_corner_and_ghost_otherwise()
    {
        Assert.Equal((true, true, false), StatusChromePolicy.CollapsedLook(false, true, true, false, false, false, false, collapsedToCorner: true));
        Assert.Equal((true, false, true), StatusChromePolicy.CollapsedLook(false, true, true, false, false, false, false, collapsedToCorner: false));
        Assert.Equal((false, false, false), StatusChromePolicy.CollapsedLook(previewReveal: true, true, true, false, false, false, false, true));
        Assert.Equal((false, false, false), StatusChromePolicy.CollapsedLook(false, true, true, hoverRevealed: true, false, false, false, true));
        Assert.Equal((false, false, false), StatusChromePolicy.CollapsedLook(false, true, true, false, false, fullscreen: true, false, true));
    }
}
