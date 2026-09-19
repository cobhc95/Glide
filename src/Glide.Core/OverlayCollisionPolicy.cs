namespace Glide.Core;

/// <summary>The axis used to separate two colliding overlay rectangles.</summary>
public enum OverlayCollisionAxis
{
    None,
    Horizontal,
    Vertical
}

/// <summary>A framework-free overlay rectangle and its current velocity.</summary>
public readonly record struct OverlayCollisionBody(
    OverlayAnimationRect Rect,
    double VelocityX,
    double VelocityY)
{
    public OverlayCollisionBody WithRect(OverlayAnimationRect rect) => this with { Rect = rect };
}

/// <summary>The result of resolving one pair of overlay bodies for a frame.</summary>
public readonly record struct OverlayCollisionResolution(
    bool Collided,
    OverlayCollisionAxis Axis,
    OverlayCollisionBody First,
    OverlayCollisionBody Second);

/// <summary>
/// Pure deterministic AABB collision handling for moving overlays. It has no dependency on
/// Avalonia, a dispatcher, a timer, or any other UI framework.
/// </summary>
public static class OverlayCollisionPolicy
{
    private const double Epsilon = 1e-9;
    private const double MinimumOutwardComponent = 0.25;

    /// <summary>
    /// Resolves the current or proposed positions of two bodies. Proposed positions are used
    /// when they overlap; otherwise an overlap already present at the current positions is
    /// resolved there. Exact edge or corner contact is not overlap and is left unchanged.
    /// </summary>
    public static OverlayCollisionResolution Resolve(
        OverlayCollisionBody firstCurrent,
        OverlayCollisionBody firstProposed,
        OverlayCollisionBody secondCurrent,
        OverlayCollisionBody secondProposed)
    {
        var currentFirst = Sanitize(firstCurrent);
        var currentSecond = Sanitize(secondCurrent);
        var proposedFirst = Sanitize(firstProposed);
        var proposedSecond = Sanitize(secondProposed);

        var currentOverlap = GetOverlap(currentFirst.Rect, currentSecond.Rect, out _, out _);
        var proposedOverlap = GetOverlap(proposedFirst.Rect, proposedSecond.Rect, out _, out _);
        if (!proposedOverlap && !currentOverlap)
        {
            return new OverlayCollisionResolution(
                false,
                OverlayCollisionAxis.None,
                proposedFirst,
                proposedSecond);
        }

        var first = proposedOverlap ? proposedFirst : currentFirst;
        var second = proposedOverlap ? proposedSecond : currentSecond;
        GetOverlap(first.Rect, second.Rect, out var overlapX, out var overlapY);

        var axis = SelectAxis(overlapX, overlapY, first, second);
        var normalX = 0d;
        var normalY = 0d;
        if (axis == OverlayCollisionAxis.Horizontal)
            normalX = SeparationSign(
                first.Rect.X + first.Rect.Width / 2,
                second.Rect.X + second.Rect.Width / 2,
                second.VelocityX - first.VelocityX,
                currentFirst.Rect.X + currentFirst.Rect.Width / 2,
                currentSecond.Rect.X + currentSecond.Rect.Width / 2);
        else
            normalY = SeparationSign(
                first.Rect.Y + first.Rect.Height / 2,
                second.Rect.Y + second.Rect.Height / 2,
                second.VelocityY - first.VelocityY,
                currentFirst.Rect.Y + currentFirst.Rect.Height / 2,
                currentSecond.Rect.Y + currentSecond.Rect.Height / 2);

        var separation = (axis == OverlayCollisionAxis.Horizontal ? overlapX : overlapY) / 2 + Epsilon;
        if (axis == OverlayCollisionAxis.Horizontal)
        {
            first = first.WithRect(first.Rect with { X = first.Rect.X - normalX * separation });
            second = second.WithRect(second.Rect with { X = second.Rect.X + normalX * separation });
        }
        else
        {
            first = first.WithRect(first.Rect with { Y = first.Rect.Y - normalY * separation });
            second = second.WithRect(second.Rect with { Y = second.Rect.Y + normalY * separation });
        }

        var firstAwayX = -normalX;
        var firstAwayY = -normalY;
        var secondAwayX = normalX;
        var secondAwayY = normalY;
        first = first with
        {
            VelocityX = PointAway(first.VelocityX, first.VelocityY, firstAwayX, firstAwayY, out var firstAwayVelocityY),
            VelocityY = firstAwayVelocityY
        };
        second = second with
        {
            VelocityX = PointAway(second.VelocityX, second.VelocityY, secondAwayX, secondAwayY, out var secondAwayVelocityY),
            VelocityY = secondAwayVelocityY
        };

        return new OverlayCollisionResolution(true, axis, first, second);
    }

    /// <summary>
    /// Advances both bodies by a frame delta and resolves any current or proposed overlap.
    /// Velocities are interpreted as device-independent units per second.
    /// </summary>
    public static OverlayCollisionResolution Resolve(
        OverlayCollisionBody first,
        OverlayCollisionBody second,
        double elapsedSeconds,
        double maxFrameDeltaSeconds = OverlayAnimationPolicy.DefaultMaxFrameDeltaSeconds)
    {
        var delta = OverlayAnimationPolicy.CapDelta(elapsedSeconds, maxFrameDeltaSeconds);
        var firstCurrent = Sanitize(first);
        var secondCurrent = Sanitize(second);
        var firstProposed = firstCurrent.WithRect(Translate(firstCurrent.Rect, firstCurrent.VelocityX * delta, firstCurrent.VelocityY * delta));
        var secondProposed = secondCurrent.WithRect(Translate(secondCurrent.Rect, secondCurrent.VelocityX * delta, secondCurrent.VelocityY * delta));
        return Resolve(firstCurrent, firstProposed, secondCurrent, secondProposed);
    }

    private static OverlayCollisionAxis SelectAxis(
        double overlapX,
        double overlapY,
        OverlayCollisionBody first,
        OverlayCollisionBody second)
    {
        if (overlapX < overlapY - Epsilon) return OverlayCollisionAxis.Horizontal;
        if (overlapY < overlapX - Epsilon) return OverlayCollisionAxis.Vertical;

        var relativeX = Math.Abs(second.VelocityX - first.VelocityX);
        var relativeY = Math.Abs(second.VelocityY - first.VelocityY);
        return relativeX >= relativeY ? OverlayCollisionAxis.Horizontal : OverlayCollisionAxis.Vertical;
    }

    private static double SeparationSign(
        double firstCenter,
        double secondCenter,
        double relativeVelocity,
        double fallbackFirstCenter,
        double fallbackSecondCenter)
    {
        if (secondCenter > firstCenter + Epsilon) return 1;
        if (secondCenter < firstCenter - Epsilon) return -1;
        if (fallbackSecondCenter > fallbackFirstCenter + Epsilon) return 1;
        if (fallbackSecondCenter < fallbackFirstCenter - Epsilon) return -1;
        if (relativeVelocity > Epsilon) return 1;
        if (relativeVelocity < -Epsilon) return -1;
        return 1;
    }

    private static double PointAway(
        double velocityX,
        double velocityY,
        double awayX,
        double awayY,
        out double resolvedY)
    {
        var x = FiniteOrZero(velocityX);
        var y = FiniteOrZero(velocityY);
        var speed = Math.Sqrt(x * x + y * y);
        if (speed <= Epsilon)
        {
            resolvedY = 0;
            return 0;
        }

        var directionX = x / speed;
        var directionY = y / speed;
        var outward = directionX * awayX + directionY * awayY;
        if (outward > Epsilon)
        {
            resolvedY = y;
            return x;
        }

        // Reflect the offending component. For a tangent or exactly stationary heading,
        // add a small deterministic outward component so the next frame cannot re-enter.
        var tangentX = directionX - outward * awayX;
        var tangentY = directionY - outward * awayY;
        var outwardComponent = Math.Max(Math.Abs(outward), MinimumOutwardComponent);
        var bouncedX = tangentX + outwardComponent * awayX;
        var bouncedY = tangentY + outwardComponent * awayY;
        var bouncedLength = Math.Sqrt(bouncedX * bouncedX + bouncedY * bouncedY);
        if (bouncedLength <= Epsilon)
        {
            bouncedX = awayX;
            bouncedY = awayY;
            bouncedLength = 1;
        }

        resolvedY = bouncedY / bouncedLength * speed;
        return bouncedX / bouncedLength * speed;
    }

    private static bool GetOverlap(
        OverlayAnimationRect first,
        OverlayAnimationRect second,
        out double overlapX,
        out double overlapY)
    {
        var a = NormalizeRect(first);
        var b = NormalizeRect(second);
        overlapX = Math.Min(a.Right, b.Right) - Math.Max(a.X, b.X);
        overlapY = Math.Min(a.Bottom, b.Bottom) - Math.Max(a.Y, b.Y);
        return overlapX > Epsilon && overlapY > Epsilon;
    }

    private static OverlayCollisionBody Sanitize(OverlayCollisionBody body) => body with
    {
        Rect = NormalizeRect(body.Rect),
        VelocityX = FiniteOrZero(body.VelocityX),
        VelocityY = FiniteOrZero(body.VelocityY)
    };

    private static OverlayAnimationRect Translate(OverlayAnimationRect rect, double x, double y) => rect with
    {
        X = rect.X + FiniteOrZero(x),
        Y = rect.Y + FiniteOrZero(y)
    };

    private static OverlayAnimationRect NormalizeRect(OverlayAnimationRect rect) => new(
        FiniteOrZero(rect.X),
        FiniteOrZero(rect.Y),
        NonNegative(rect.Width),
        NonNegative(rect.Height));

    private static double FiniteOrZero(double value) => double.IsFinite(value) ? value : 0;
    private static double NonNegative(double value) => double.IsFinite(value) ? Math.Max(0, value) : 0;
}
