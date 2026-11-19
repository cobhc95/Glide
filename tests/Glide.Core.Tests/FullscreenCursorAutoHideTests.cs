
namespace Glide.Core.Tests;

[Trait("Area", "Navigation")]
public sealed class FullscreenCursorAutoHideTests
{
    private static readonly DateTime T0 = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private static DateTime Ms(double ms) => T0.AddMilliseconds(ms);

    [Theory]
    [InlineData(true, false, false, false, true)]   // bare picture
    [InlineData(false, false, false, false, false)] // status bar / title bar / Explorer
    [InlineData(true, true, false, false, false)]   // control inside the viewport
    [InlineData(true, false, true, false, false)]   // Window-in-Window overlay or handle
    [InlineData(true, false, false, true, false)]   // popup / menu open
    public void MayHideAt_OnlyOverBarePicture(bool viewport, bool chrome, bool overlay, bool popup, bool expected) =>
        Assert.Equal(expected, FullscreenCursorAutoHide.MayHideAt(viewport, chrome, overlay, popup));

    private static FullscreenCursorAutoHide Hidden()
    {
        var p = new FullscreenCursorAutoHide();
        p.Activate(T0);
        p.OnPointerMoved(T0, 100, 100);
        Assert.True(p.Tick(Ms(2000)));
        Assert.True(p.IsHidden);
        return p;
    }

    [Fact]
    public void Defaults_MatchSpec()
    {
        Assert.Equal(TimeSpan.FromSeconds(2), FullscreenCursorAutoHide.DefaultHideDelay);
        Assert.Equal(TimeSpan.FromSeconds(2), new FullscreenCursorAutoHide().HideDelay);
        Assert.Equal(TimeSpan.FromSeconds(0.5), FullscreenCursorAutoHide.DelayFromSeconds(0));
        Assert.Equal(TimeSpan.FromSeconds(1.5), FullscreenCursorAutoHide.DelayFromSeconds(1.4));
        Assert.Equal(TimeSpan.FromSeconds(30), FullscreenCursorAutoHide.DelayFromSeconds(99));
    }

    [Fact]
    public void AlwaysHide_HidesAtOnce_StaysHiddenOnMove_ShowsWhenSuppressed()
    {
        var p = new FullscreenCursorAutoHide { AlwaysHide = true };
        p.Activate(T0);
        Assert.True(p.Tick(Ms(200)));
        Assert.False(p.OnPointerMoved(Ms(300), 10, 10));
        Assert.False(p.OnPointerMoved(Ms(400), 200, 200));
        Assert.True(p.IsHidden);
        Assert.True(p.SetSuppressed(true, Ms(500)));
        Assert.False(p.IsHidden);
        Assert.False(p.Tick(Ms(600)));
        p.SetSuppressed(false, Ms(700));
        Assert.True(p.Tick(Ms(800)));
    }

    [Fact]
    public void HidesAfterIdle_NotBefore()
    {
        var p = new FullscreenCursorAutoHide();
        p.Activate(T0);
        Assert.False(p.Tick(Ms(1900)));
        Assert.False(p.IsHidden);
        Assert.True(p.Tick(Ms(2000)));
        Assert.True(p.IsHidden);
    }

    [Fact]
    public void ConfiguredDelay_IsHonoured()
    {
        var p = new FullscreenCursorAutoHide { HideDelay = FullscreenCursorAutoHide.DelayFromSeconds(5) };
        p.Activate(T0);
        Assert.False(p.Tick(Ms(4900)));
        Assert.True(p.Tick(Ms(5000)));
    }

    [Fact]
    public void MovementWhileVisible_RestartsCountdown()
    {
        var p = new FullscreenCursorAutoHide();
        p.Activate(T0);
        p.OnPointerMoved(Ms(0), 0, 0);
        p.OnPointerMoved(Ms(1500), 50, 0);
        Assert.False(p.Tick(Ms(3000)));
        Assert.True(p.Tick(Ms(3500)));
    }

    [Fact]
    public void AnyRealMovement_RevealsInstantly()
    {
        var p = Hidden();
        Assert.True(p.OnPointerMoved(Ms(2100), 110, 100));
        Assert.False(p.IsHidden);
        Assert.False(p.Tick(Ms(4000)));      // countdown restarted at the move
        Assert.True(p.Tick(Ms(4100)));
    }

    [Fact]
    public void ClickOrWheel_RevealsInstantly()
    {
        var p = Hidden();
        Assert.True(p.OnPointerActivity(Ms(2100)));
        Assert.False(p.IsHidden);
        Assert.False(p.OnPointerActivity(Ms(2200)));   // already visible: no change
    }

    [Fact]
    public void Jitter_DoesNotReveal()
    {
        var p = Hidden();
        for (var t = 2100; t <= 6000; t += 20) p.OnPointerMoved(Ms(t), 100 + (t / 20 % 2), 100);
        Assert.True(p.IsHidden);
    }

    [Fact]
    public void Suppression_ShowsAndBlocksHiding()
    {
        var p = Hidden();
        Assert.True(p.SetSuppressed(true, Ms(3100)));   // drag / text box / chrome / outside the picture
        Assert.False(p.IsHidden);
        Assert.False(p.Tick(Ms(10000)));
        Assert.False(p.IsHidden);
        p.SetSuppressed(false, Ms(10000));
        Assert.False(p.Tick(Ms(11900)));
        Assert.True(p.Tick(Ms(12000)));
    }

    [Fact]
    public void Inactive_IgnoresEverything()
    {
        var p = new FullscreenCursorAutoHide();
        Assert.False(p.Tick(Ms(60000)));
        Assert.False(p.OnPointerActivity(Ms(60001)));
        Assert.False(p.IsHidden);
    }

    [Fact]
    public void Deactivate_RestoresAndStops()
    {
        var p = Hidden();
        p.Deactivate();
        Assert.False(p.IsHidden);
        Assert.False(p.Tick(Ms(20000)));
        Assert.False(p.OnPointerMoved(Ms(20001), 0, 0));
    }
}
