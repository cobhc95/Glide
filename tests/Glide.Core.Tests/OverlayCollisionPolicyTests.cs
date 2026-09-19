using Glide.Core;

namespace Glide.Core.Tests;

public sealed class OverlayCollisionPolicyTests
{
    [Fact]
    public void CurrentOverlapSeparatesHorizontallyAndPreservesSpeeds()
    {
        var first = Body(0, 0, 100, 100, 80, 0);
        var second = Body(80, 20, 100, 100, -40, 30);

        var result = OverlayCollisionPolicy.Resolve(first, first, second, second);

        Assert.True(result.Collided);
        Assert.Equal(OverlayCollisionAxis.Horizontal, result.Axis);
        Assert.False(Overlaps(result.First.Rect, result.Second.Rect));
        Assert.True(result.First.VelocityX < 0);
        Assert.True(result.Second.VelocityX > 0);
        Assert.Equal(Speed(first), Speed(result.First), 8);
        Assert.Equal(Speed(second), Speed(result.Second), 8);
    }

    [Fact]
    public void ProposedOverlapIsResolvedEvenWhenCurrentRectanglesAreSeparate()
    {
        var firstCurrent = Body(0, 0, 40, 40, 100, 0);
        var firstProposed = Body(45, 0, 40, 40, 100, 0);
        var secondCurrent = Body(90, 0, 40, 40, -100, 0);
        var secondProposed = Body(45, 0, 40, 40, -100, 0);

        var result = OverlayCollisionPolicy.Resolve(firstCurrent, firstProposed, secondCurrent, secondProposed);

        Assert.True(result.Collided);
        Assert.False(Overlaps(result.First.Rect, result.Second.Rect));
        Assert.True(result.First.Rect.X < result.Second.Rect.X);
        Assert.True(result.First.VelocityX < 0);
        Assert.True(result.Second.VelocityX > 0);
    }

    [Fact]
    public void ElapsedTimeOverloadDetectsPredictedOverlap()
    {
        var first = Body(0, 0, 50, 50, 100, 0);
        var second = Body(100, 0, 50, 50, -100, 0);

        var result = OverlayCollisionPolicy.Resolve(first, second, .4, .5);

        Assert.True(result.Collided);
        Assert.False(Overlaps(result.First.Rect, result.Second.Rect));
        Assert.True(result.First.VelocityX < 0);
        Assert.True(result.Second.VelocityX > 0);
    }

    [Fact]
    public void ExactContactDoesNotTriggerRepeatedCollision()
    {
        var first = Body(0, 0, 100, 100, 100, 0);
        var second = Body(100, 0, 100, 100, -100, 0);

        var result = OverlayCollisionPolicy.Resolve(first, first, second, second);

        Assert.False(result.Collided);
        Assert.Equal(first, result.First);
        Assert.Equal(second, result.Second);
    }

    [Fact]
    public void CornerTieBreakIsDeterministicAndSeparatesBodies()
    {
        var first = Body(0, 0, 100, 100, 10, 10);
        var second = Body(50, 50, 100, 100, -10, -10);

        var firstResult = OverlayCollisionPolicy.Resolve(first, first, second, second);
        var secondResult = OverlayCollisionPolicy.Resolve(first, first, second, second);

        Assert.Equal(firstResult, secondResult);
        Assert.Equal(OverlayCollisionAxis.Horizontal, firstResult.Axis);
        Assert.False(Overlaps(firstResult.First.Rect, firstResult.Second.Rect));
    }

    [Fact]
    public void ZeroMovementSeparatesButRemainsZeroAndStable()
    {
        var first = Body(0, 0, 100, 100, 0, 0);
        var second = Body(25, 25, 100, 100, 0, 0);

        var firstResult = OverlayCollisionPolicy.Resolve(first, first, second, second);
        var secondResult = OverlayCollisionPolicy.Resolve(firstResult.First, firstResult.First, firstResult.Second, firstResult.Second);

        Assert.True(firstResult.Collided);
        Assert.False(Overlaps(firstResult.First.Rect, firstResult.Second.Rect));
        Assert.Equal(0, firstResult.First.VelocityX);
        Assert.Equal(0, firstResult.First.VelocityY);
        Assert.Equal(0, firstResult.Second.VelocityX);
        Assert.Equal(0, firstResult.Second.VelocityY);
        Assert.False(secondResult.Collided);
    }

    private static OverlayCollisionBody Body(double x, double y, double width, double height, double velocityX, double velocityY) =>
        new(new OverlayAnimationRect(x, y, width, height), velocityX, velocityY);

    private static double Speed(OverlayCollisionBody body) =>
        Math.Sqrt(body.VelocityX * body.VelocityX + body.VelocityY * body.VelocityY);

    private static bool Overlaps(OverlayAnimationRect first, OverlayAnimationRect second) =>
        Math.Min(first.Right, second.Right) > Math.Max(first.X, second.X) &&
        Math.Min(first.Bottom, second.Bottom) > Math.Max(first.Y, second.Y);
}
