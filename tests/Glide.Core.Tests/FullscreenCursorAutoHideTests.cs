using Glide.Core;
using Xunit;

namespace Glide.Core.Tests;

public sealed class FullscreenCursorAutoHideTests
{
    private static readonly DateTime T0 = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private static DateTime Ms(double ms) => T0.AddMilliseconds(ms);

    private static FullscreenCursorAutoHide Hidden()
    {
        var p = new FullscreenCursorAutoHide();
        p.Activate(T0);
        p.OnPointerMoved(T0, 100, 100);
        Assert.True(p.Tick(Ms(3000)));
        Assert.True(p.IsHidden);
        return p;
    }

    [Fact]
    public void Constants_MatchSpec()
    {
        Assert.Equal(TimeSpan.FromSeconds(3), FullscreenCursorAutoHide.HideDelay);
        Assert.Equal(TimeSpan.FromSeconds(1), FullscreenCursorAutoHide.RevealSustain);
    }

    [Fact]
    public void HidesAfterIdle_NotBefore()
    {
        var p = new FullscreenCursorAutoHide();
        p.Activate(T0);
        Assert.False(p.Tick(Ms(2900)));
        Assert.False(p.IsHidden);
        Assert.True(p.Tick(Ms(3000)));
        Assert.True(p.IsHidden);
    }

    [Fact]
    public void MovementWhileVisible_RestartsCountdown()
    {
        var p = new FullscreenCursorAutoHide();
        p.Activate(T0);
        p.OnPointerMoved(Ms(0), 0, 0);
        p.OnPointerMoved(Ms(2500), 50, 0);
        Assert.False(p.Tick(Ms(4000)));
        Assert.True(p.Tick(Ms(5500)));
    }

    [Fact]
    public void ShortBump_DoesNotReveal()
    {
        var p = Hidden();
        for (var t = 3100; t <= 3500; t += 50) p.OnPointerMoved(Ms(t), t, 100);
        p.Tick(Ms(4000));
        Assert.True(p.IsHidden);
    }

    [Fact]
    public void Jitter_DoesNotReveal()
    {
        var p = Hidden();
        for (var t = 3100; t <= 6000; t += 20) p.OnPointerMoved(Ms(t), 100 + (t / 20 % 2), 100);
        Assert.True(p.IsHidden);
    }

    [Fact]
    public void SustainedMovement_Reveals()
    {
        var p = Hidden();
        var changed = false;
        for (var t = 3100; t <= 4200 && !changed; t += 50) changed = p.OnPointerMoved(Ms(t), t, 100);
        Assert.True(changed);
        Assert.False(p.IsHidden);
    }

    [Fact]
    public void MovementWithGaps_RestartsStreak()
    {
        var p = Hidden();
        // 600ms of motion, a 500ms pause, then 600ms more: never 1s continuous.
        for (var t = 3100; t <= 3700; t += 50) p.OnPointerMoved(Ms(t), t, 100);
        for (var t = 4200; t <= 4800; t += 50) p.OnPointerMoved(Ms(t), t, 100);
        Assert.True(p.IsHidden);
    }

    [Fact]
    public void Suppression_ShowsAndBlocksHiding()
    {
        var p = Hidden();
        Assert.True(p.SetSuppressed(true, Ms(3100)));
        Assert.False(p.IsHidden);
        Assert.False(p.Tick(Ms(10000)));
        Assert.False(p.IsHidden);
        p.SetSuppressed(false, Ms(10000));
        Assert.True(p.Tick(Ms(13000)));
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
