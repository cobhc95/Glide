using Avalonia;

namespace Glide.App.Controls;

/// <summary>
/// Drift-motion math shared by the two animated-overlay renderers: <see cref="CompositorOverlayRenderer"/>
/// (in-window overlays, compositor thread) and <see cref="NativeOverlayWindowRenderer"/> (whole-app overlays,
/// its own render thread). Both must move items identically, so the steering, turning and edge clamping live
/// here once. Items steer away from a legal edge before reaching it instead of bouncing, turn at a bounded
/// angular rate, and are clamped at an edge with a small inward speed. Pure, allocation-free, thread-agnostic.
/// </summary>
internal static class OverlayDriftMotion
{
    /// <summary>Adds an inward push that grows as an item heading outward approaches an edge.</summary>
    public static void AddBoundarySteer(ref Vector steer, double distance, bool headingOutward, Vector inward, double margin)
    {
        if (!headingOutward || distance >= margin) return;
        var strength = 1 - Math.Clamp(distance / margin, 0, 1);
        steer += inward * (0.65 + strength * strength * 2.35);
    }

    /// <summary>Turns <paramref name="current"/> towards <paramref name="desired"/> by at most
    /// <paramref name="maxRadians"/>, keeping the current speed (minimum 1).</summary>
    public static Vector RotateTowards(Vector current, Vector desired, double maxRadians)
    {
        var speed = Math.Max(1, current.Length);
        if (desired.Length <= .001 || maxRadians <= 0) return current;
        var from = Math.Atan2(current.Y, current.X);
        var to = Math.Atan2(desired.Y, desired.X);
        var delta = Math.Atan2(Math.Sin(to - from), Math.Cos(to - from));
        var angle = from + Math.Clamp(delta, -maxRadians, maxRadians);
        return new Vector(Math.Cos(angle) * speed, Math.Sin(angle) * speed);
    }

    /// <summary>Clamps the position into <paramref name="bounds"/> without reflecting: at an edge the
    /// outward velocity component becomes a small inward one, and the original speed is preserved.</summary>
    public static Vector ClampWithoutBounce(Rect bounds, ref double x, ref double y, Vector velocity)
    {
        var speed = Math.Max(1, velocity.Length);
        var inward = Math.Max(1, speed * .18);
        if (bounds.Right <= bounds.Left) { x = bounds.Left; velocity = new Vector(0, velocity.Y); }
        else if (x <= bounds.Left) { x = bounds.Left; if (velocity.X < inward) velocity = new Vector(inward, velocity.Y); }
        else if (x >= bounds.Right) { x = bounds.Right; if (velocity.X > -inward) velocity = new Vector(-inward, velocity.Y); }

        if (bounds.Bottom <= bounds.Top) { y = bounds.Top; velocity = new Vector(velocity.X, 0); }
        else if (y <= bounds.Top) { y = bounds.Top; if (velocity.Y < inward) velocity = new Vector(velocity.X, inward); }
        else if (y >= bounds.Bottom) { y = bounds.Bottom; if (velocity.Y > -inward) velocity = new Vector(velocity.X, -inward); }

        if (velocity.Length > .001 && Math.Abs(velocity.Length - speed) > .001)
            velocity *= speed / velocity.Length;
        return velocity;
    }
}
