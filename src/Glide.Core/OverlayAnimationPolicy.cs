namespace Glide.Core;

/// <summary>The seven selectable areas in which an overlay may animate.</summary>
public enum OverlayAnimationRegion
{
    Anywhere,
    TopHalf,
    BottomHalf,
    TopLeftQuarter,
    TopRightQuarter,
    BottomLeftQuarter,
    BottomRightQuarter
}

/// <summary>A framework-free rectangle, expressed in host coordinates.</summary>
public readonly record struct OverlayAnimationRect(double X, double Y, double Width, double Height)
{
    public double Right => X + Width;
    public double Bottom => Y + Height;
}

/// <summary>Position and velocity of an animated overlay.</summary>
public readonly record struct OverlayAnimationState(double X, double Y, double VelocityX, double VelocityY);

/// <summary>Tunable limits for the deterministic animation policy.</summary>
public sealed record OverlayAnimationOptions
{
    public const double DefaultMaxFrameDeltaSeconds = 0.1;
    public const double DefaultMaxTurnAngleRadians = Math.PI / 6;
    public const double DefaultTurnIntervalSeconds = 0.65;

    public double MaxFrameDeltaSeconds { get; init; } = DefaultMaxFrameDeltaSeconds;
    public double MaxTurnAngleRadians { get; init; } = DefaultMaxTurnAngleRadians;
    public double TurnIntervalSeconds { get; init; } = DefaultTurnIntervalSeconds;
}

/// <summary>
/// Pure overlay animation behavior. It has no dependency on a UI or graphics framework.
/// </summary>
public static class OverlayAnimationPolicy
{
    public const double DefaultMaxFrameDeltaSeconds = OverlayAnimationOptions.DefaultMaxFrameDeltaSeconds;
    public const double DefaultMaxTurnAngleRadians = OverlayAnimationOptions.DefaultMaxTurnAngleRadians;

    /// <summary>
    /// Returns the legal top-left position rectangle for an overlay in the requested host region.
    /// If the overlay is larger than the region, the corresponding position dimension is zero.
    /// </summary>
    public static OverlayAnimationRect GetSafeBounds(
        OverlayAnimationRect host,
        double overlayWidth,
        double overlayHeight,
        OverlayAnimationRegion region = OverlayAnimationRegion.Anywhere)
    {
        var hostX = FiniteOrZero(host.X);
        var hostY = FiniteOrZero(host.Y);
        var hostWidth = NonNegative(host.Width);
        var hostHeight = NonNegative(host.Height);
        var width = Math.Min(NonNegative(overlayWidth), hostWidth);
        var height = Math.Min(NonNegative(overlayHeight), hostHeight);

        var regionX = hostX;
        var regionY = hostY;
        var regionWidth = hostWidth;
        var regionHeight = hostHeight;
        var halfWidth = hostWidth / 2;
        var halfHeight = hostHeight / 2;

        switch (region)
        {
            case OverlayAnimationRegion.TopHalf:
                regionHeight = halfHeight;
                break;
            case OverlayAnimationRegion.BottomHalf:
                regionY += halfHeight;
                regionHeight -= halfHeight;
                break;
            case OverlayAnimationRegion.TopLeftQuarter:
                regionWidth = halfWidth;
                regionHeight = halfHeight;
                break;
            case OverlayAnimationRegion.TopRightQuarter:
                regionX += halfWidth;
                regionWidth -= halfWidth;
                regionHeight = halfHeight;
                break;
            case OverlayAnimationRegion.BottomLeftQuarter:
                regionWidth = halfWidth;
                regionY += halfHeight;
                regionHeight -= halfHeight;
                break;
            case OverlayAnimationRegion.BottomRightQuarter:
                regionX += halfWidth;
                regionY += halfHeight;
                regionWidth -= halfWidth;
                regionHeight -= halfHeight;
                break;
            case OverlayAnimationRegion.Anywhere:
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(region), region, "Unknown animation region.");
        }

        return new OverlayAnimationRect(
            regionX,
            regionY,
            Math.Max(0, regionWidth - width),
            Math.Max(0, regionHeight - height));
    }

    /// <summary>Integrates position for a capped frame delta and reflects at each edge.</summary>
    public static OverlayAnimationState Advance(
        OverlayAnimationState state,
        OverlayAnimationRect safeBounds,
        double elapsedSeconds,
        double maxFrameDeltaSeconds = DefaultMaxFrameDeltaSeconds)
    {
        var delta = CapDelta(elapsedSeconds, maxFrameDeltaSeconds);
        var bounds = NormalizeBounds(safeBounds);
        var x = AdvanceAxis(state.X, state.VelocityX, delta, bounds.X, bounds.Right, out var velocityX);
        var y = AdvanceAxis(state.Y, state.VelocityY, delta, bounds.Y, bounds.Bottom, out var velocityY);
        return new OverlayAnimationState(x, y, velocityX, velocityY);
    }

    /// <summary>Returns a random signed turn in the inclusive range ±<paramref name="maxAngleRadians"/>.</summary>
    public static double NextTurnAngleRadians(Random random, double maxAngleRadians = DefaultMaxTurnAngleRadians)
    {
        ArgumentNullException.ThrowIfNull(random);
        var maximum = Math.Abs(FiniteOrZero(maxAngleRadians));
        return (random.NextDouble() * 2 - 1) * maximum;
    }

    /// <summary>Rotates a velocity vector by a bounded angle while preserving its speed.</summary>
    public static OverlayAnimationState ApplyTurn(OverlayAnimationState state, double angleRadians)
    {
        if (!double.IsFinite(angleRadians)) return state;
        var speed = Math.Sqrt(state.VelocityX * state.VelocityX + state.VelocityY * state.VelocityY);
        if (speed <= double.Epsilon) return state;

        var angle = Math.Atan2(state.VelocityY, state.VelocityX) + angleRadians;
        return state with
        {
            VelocityX = Math.Cos(angle) * speed,
            VelocityY = Math.Sin(angle) * speed
        };
    }

    internal static double CapDelta(double elapsedSeconds, double maxFrameDeltaSeconds)
    {
        if (!double.IsFinite(elapsedSeconds) || elapsedSeconds <= 0) return 0;
        var maximum = double.IsFinite(maxFrameDeltaSeconds) && maxFrameDeltaSeconds > 0
            ? maxFrameDeltaSeconds
            : DefaultMaxFrameDeltaSeconds;
        return Math.Min(elapsedSeconds, maximum);
    }

    private static double AdvanceAxis(double position, double velocity, double delta, double minimum, double maximum, out double nextVelocity)
    {
        if (maximum <= minimum)
        {
            nextVelocity = velocity;
            return minimum;
        }

        var start = Math.Clamp(FiniteOrZero(position), minimum, maximum);
        if (delta <= 0 || !double.IsFinite(velocity))
        {
            nextVelocity = double.IsFinite(velocity) ? velocity : 0;
            return start;
        }

        var speed = Math.Abs(velocity);
        if (speed <= double.Epsilon)
        {
            nextVelocity = 0;
            return start;
        }

        // Turn inward immediately at an exact edge instead of spending another frame trying
        // to travel outside the legal area. The UI layer may then randomize the inward heading.
        if (start <= minimum && velocity < 0)
        {
            nextVelocity = speed;
            return minimum;
        }
        if (start >= maximum && velocity > 0)
        {
            nextVelocity = -speed;
            return maximum;
        }

        var phase = (start - minimum) + velocity * delta;
        var cycle = (maximum - minimum) * 2;
        var wrapped = phase % cycle;
        if (wrapped < 0) wrapped += cycle;

        var direction = velocity >= 0 ? 1 : -1;
        if (wrapped <= maximum - minimum)
        {
            nextVelocity = speed * direction;
            return minimum + wrapped;
        }

        nextVelocity = -speed * direction;
        return maximum - (wrapped - (maximum - minimum));
    }

    private static OverlayAnimationRect NormalizeBounds(OverlayAnimationRect bounds) => new(
        FiniteOrZero(bounds.X),
        FiniteOrZero(bounds.Y),
        NonNegative(bounds.Width),
        NonNegative(bounds.Height));

    private static double FiniteOrZero(double value) => double.IsFinite(value) ? value : 0;
    private static double NonNegative(double value) => double.IsFinite(value) ? Math.Max(0, value) : 0;
}

/// <summary>Stateful convenience wrapper around the pure animation policy.</summary>
public sealed class OverlayAnimationSimulator
{
    private readonly Random _random;
    private readonly OverlayAnimationOptions _options;
    private double _secondsUntilTurn;

    public OverlayAnimationSimulator(
        OverlayAnimationRect host,
        double overlayWidth,
        double overlayHeight,
        OverlayAnimationRegion region = OverlayAnimationRegion.Anywhere,
        OverlayAnimationState? initialState = null,
        OverlayAnimationOptions? options = null,
        Random? random = null,
        int? seed = null)
    {
        if (random is not null && seed is not null)
            throw new ArgumentException("Provide either random or seed, not both.");

        _random = random ?? (seed.HasValue ? new Random(seed.Value) : Random.Shared);
        _options = NormalizeOptions(options ?? new OverlayAnimationOptions());
        Bounds = OverlayAnimationPolicy.GetSafeBounds(host, overlayWidth, overlayHeight, region);
        State = initialState ?? new OverlayAnimationState(
            Bounds.X + Bounds.Width / 2,
            Bounds.Y + Bounds.Height / 2,
            0,
            0);
        State = ClampState(State);
        _secondsUntilTurn = _options.TurnIntervalSeconds;
    }

    public OverlayAnimationRect Bounds { get; }
    public OverlayAnimationState State { get; private set; }

    public double NextTurnAngleRadians() => OverlayAnimationPolicy.NextTurnAngleRadians(_random, _options.MaxTurnAngleRadians);

    public OverlayAnimationState Advance(double elapsedSeconds)
    {
        var remaining = OverlayAnimationPolicy.CapDelta(elapsedSeconds, _options.MaxFrameDeltaSeconds);
        while (remaining > 0)
        {
            var segment = Math.Min(remaining, _secondsUntilTurn);
            State = OverlayAnimationPolicy.Advance(State, Bounds, segment, segment);
            remaining -= segment;
            _secondsUntilTurn -= segment;

            if (_secondsUntilTurn <= double.Epsilon)
            {
                State = OverlayAnimationPolicy.ApplyTurn(State, NextTurnAngleRadians());
                _secondsUntilTurn = _options.TurnIntervalSeconds;
            }
        }

        return State;
    }

    private OverlayAnimationState ClampState(OverlayAnimationState state) => new(
        Math.Clamp(double.IsFinite(state.X) ? state.X : Bounds.X, Bounds.X, Bounds.Right),
        Math.Clamp(double.IsFinite(state.Y) ? state.Y : Bounds.Y, Bounds.Y, Bounds.Bottom),
        double.IsFinite(state.VelocityX) ? state.VelocityX : 0,
        double.IsFinite(state.VelocityY) ? state.VelocityY : 0);

    private static OverlayAnimationOptions NormalizeOptions(OverlayAnimationOptions options) => options with
    {
        MaxFrameDeltaSeconds = double.IsFinite(options.MaxFrameDeltaSeconds) && options.MaxFrameDeltaSeconds > 0
            ? options.MaxFrameDeltaSeconds : OverlayAnimationOptions.DefaultMaxFrameDeltaSeconds,
        MaxTurnAngleRadians = Math.Abs(double.IsFinite(options.MaxTurnAngleRadians) ? options.MaxTurnAngleRadians : 0),
        TurnIntervalSeconds = double.IsFinite(options.TurnIntervalSeconds) && options.TurnIntervalSeconds > 0
            ? options.TurnIntervalSeconds : OverlayAnimationOptions.DefaultTurnIntervalSeconds
    };
}
