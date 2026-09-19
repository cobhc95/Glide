using Glide.Core;

namespace Glide.Core.Tests;

public sealed class OverlayAnimationPolicyTests
{
    private static readonly OverlayAnimationRect Host = new(10, 20, 800, 600);

    [Fact]
    public void SafeBoundsCoverAllSevenRegions()
    {
        var expected = new[]
        {
            new OverlayAnimationRect(10, 20, 700, 500),
            new OverlayAnimationRect(10, 20, 700, 200),
            new OverlayAnimationRect(10, 320, 700, 200),
            new OverlayAnimationRect(10, 20, 300, 200),
            new OverlayAnimationRect(410, 20, 300, 200),
            new OverlayAnimationRect(10, 320, 300, 200),
            new OverlayAnimationRect(410, 320, 300, 200)
        };

        var regions = Enum.GetValues<OverlayAnimationRegion>();
        Assert.Equal(7, regions.Length);
        for (var i = 0; i < regions.Length; i++)
            Assert.Equal(expected[i], OverlayAnimationPolicy.GetSafeBounds(Host, 100, 100, regions[i]));
    }

    [Fact]
    public void AdvanceReflectsAtRightEdge()
    {
        var state = new OverlayAnimationState(95, 30, 100, 0);

        var next = OverlayAnimationPolicy.Advance(state, new OverlayAnimationRect(0, 0, 100, 100), .1);

        Assert.Equal(95, next.X, 8);
        Assert.Equal(-100, next.VelocityX, 8);
    }

    [Fact]
    public void AdvanceReflectsAtLeftEdge()
    {
        var state = new OverlayAnimationState(5, 30, -100, 0);

        var next = OverlayAnimationPolicy.Advance(state, new OverlayAnimationRect(0, 0, 100, 100), .1);

        Assert.Equal(5, next.X, 8);
        Assert.Equal(100, next.VelocityX, 8);
    }

    [Theory]
    [InlineData(0, -100, 100)]
    [InlineData(100, 100, -100)]
    public void AdvanceTurnsInwardImmediatelyAtExactHorizontalEdge(double x, double velocityX, double expectedVelocityX)
    {
        var state = new OverlayAnimationState(x, 30, velocityX, 0);

        var next = OverlayAnimationPolicy.Advance(state, new OverlayAnimationRect(0, 0, 100, 100), .016);

        Assert.Equal(x, next.X, 8);
        Assert.Equal(expectedVelocityX, next.VelocityX, 8);
    }

    [Fact]
    public void LargeFrameDeltaIsCapped()
    {
        var state = new OverlayAnimationState(0, 0, 100, 0);
        var bounds = new OverlayAnimationRect(0, 0, 1000, 1000);

        var next = OverlayAnimationPolicy.Advance(state, bounds, 10);

        Assert.Equal(.1 * 100, next.X, 8);
    }

    [Fact]
    public void RandomTurnsStayWithinConfiguredAngle()
    {
        var random = new Random(42);
        const double maximum = .25;

        for (var i = 0; i < 1000; i++)
            Assert.InRange(OverlayAnimationPolicy.NextTurnAngleRadians(random, maximum), -maximum, maximum);
    }

    [Fact]
    public void SeedMakesTurnSequenceDeterministic()
    {
        var first = new OverlayAnimationSimulator(Host, 100, 100, seed: 17);
        var second = new OverlayAnimationSimulator(Host, 100, 100, seed: 17);

        for (var i = 0; i < 5; i++)
        {
            Assert.Equal(first.NextTurnAngleRadians(), second.NextTurnAngleRadians());
            Assert.Equal(first.Advance(.1), second.Advance(.1));
        }
    }
}
