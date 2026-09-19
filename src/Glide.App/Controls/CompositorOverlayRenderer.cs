using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Media.Imaging;
using Avalonia.Rendering.Composition;
using Avalonia.Threading;

namespace Glide.App.Controls;

/// <summary>Which existing overlay chrome the compositor renderer should draw.</summary>
[Flags]
public enum OverlayChromeSelection
{
    None = 0,
    Controls = 1,
    Active = 2
}

/// <summary>
/// Immutable data sent from the UI-thread overlay manager to the compositor handler.
/// Bitmap ownership remains with the publisher; this type never disposes it.
/// </summary>
public sealed record OverlayRenderItem(
    Guid Id,
    Bitmap Bitmap,
    Rect SourceRect,
    Rect DestinationRect,
    double Opacity,
    int Z,
    OverlayChromeSelection Chrome,
    Vector Velocity,
    bool Running,
    Rect LegalBounds,
    int TargetHz,
    bool AvoidOverlap,
    long MotionRevision);

public readonly record struct OverlayCompositionMotion(Point Position, Vector Velocity);

/// <summary>Immutable compositor message containing the current overlay set.</summary>
public sealed class OverlayRenderSnapshot
{
    public IReadOnlyList<OverlayRenderItem> Items { get; }
    public int TargetHz { get; }
    public Color AccentColor { get; }
    public Action<IReadOnlyDictionary<Guid, OverlayCompositionMotion>>? PositionSink { get; }
    public PixelPoint HostScreenOrigin { get; }
    public double RenderScaling { get; }

    public OverlayRenderSnapshot(IEnumerable<OverlayRenderItem> items, int targetHz = 144, Color? accentColor = null,
        Action<IReadOnlyDictionary<Guid, OverlayCompositionMotion>>? positionSink = null,
        PixelPoint? hostScreenOrigin = null, double renderScaling = 1.0)
    {
        ArgumentNullException.ThrowIfNull(items);
        Items = Array.AsReadOnly(items.ToArray());
        TargetHz = Math.Clamp(targetHz, 1, 360);
        AccentColor = accentColor ?? Color.FromRgb(0x36, 0xB8, 0xF4);
        PositionSink = positionSink;
        HostScreenOrigin = hostScreenOrigin ?? default;
        RenderScaling = double.IsFinite(renderScaling) ? Math.Max(0.1, renderScaling) : 1.0;
    }
}

/// <summary>
/// Compositor-thread renderer for Window-in-Window overlays. It owns no bitmaps and only keeps
/// references supplied by immutable snapshots. The manager remains responsible for interaction,
/// hit testing, loading and lifetime.
/// </summary>
internal sealed class CompositorOverlayVisualHandler : CompositionCustomVisualHandler
{
    private sealed class Item
    {
        public required Guid Id;
        public required Bitmap Bitmap;
        public Rect SourceRect;
        public double X;
        public double Y;
        public double Width;
        public double Height;
        public double Opacity;
        public int Z;
        public OverlayChromeSelection Chrome;
        public Vector Velocity;
        public bool Running;
        public Rect LegalBounds;
        public int TargetHz;
        public bool AvoidOverlap;
        public long MotionRevision;
    }

    private readonly Dictionary<Guid, Item> _items = new();
    private readonly List<Item> _orderedItems = new();
    private TimeSpan? _lastCompositionTime;
    private TimeSpan? _lastRenderedTime;
    private bool _hasRunningItem;
    private Color _accentColor = Color.FromRgb(0x36, 0xB8, 0xF4);
    private Action<IReadOnlyDictionary<Guid, OverlayCompositionMotion>>? _positionSink;
    private IReadOnlyDictionary<Guid, OverlayCompositionMotion> _latestMotion = new Dictionary<Guid, OverlayCompositionMotion>();
    private int _positionPostPending;

    public override void OnMessage(object message)
    {
        if (message is not OverlayRenderSnapshot snapshot) return;
        var previouslyRunning = _hasRunningItem;
        _positionSink = snapshot.PositionSink;
        _accentColor = snapshot.AccentColor;

        var incomingIds = new HashSet<Guid>();
        foreach (var source in snapshot.Items)
        {
            incomingIds.Add(source.Id);
            if (!_items.TryGetValue(source.Id, out var item))
            {
                item = new Item { Id = source.Id, Bitmap = source.Bitmap };
                _items.Add(source.Id, item);
            }

            var wasRunning = item.Running;
            item.Bitmap = source.Bitmap;
            item.SourceRect = source.SourceRect;
            item.Width = Math.Max(0, source.DestinationRect.Width);
            item.Height = Math.Max(0, source.DestinationRect.Height);
            // The UI manager owns hit testing and interaction, so its latest destination is the
            // authoritative synchronization point. The compositor advances from here only between
            // UI snapshots (and keeps advancing if image presentation stalls the UI thread).
            if (!wasRunning || !source.Running)
            {
                item.X = source.DestinationRect.X;
                item.Y = source.DestinationRect.Y;
            }
            item.Opacity = Math.Clamp(source.Opacity, 0, 1);
            item.Z = source.Z;
            item.Chrome = source.Chrome;
            if (!wasRunning || !source.Running || item.MotionRevision != source.MotionRevision)
            {
                item.Velocity = source.Velocity;
                item.MotionRevision = source.MotionRevision;
            }
            item.Running = source.Running;
            item.LegalBounds = source.LegalBounds;
            item.TargetHz = Math.Clamp(source.TargetHz > 0 ? source.TargetHz : snapshot.TargetHz, 1, 360);
            item.AvoidOverlap = source.AvoidOverlap;
            Clamp(item);
        }

        foreach (var id in _items.Keys.Where(id => !incomingIds.Contains(id)).ToArray())
            _items.Remove(id);

        // Build ordering only when state is published, never on each animation/render frame.
        _orderedItems.Clear();
        _orderedItems.AddRange(_items.Values);
        _orderedItems.Sort(static (a, b) => a.Z.CompareTo(b.Z));
        _hasRunningItem = _orderedItems.Any(item => item.Running);
        // Do not reset the compositor clock for every UI snapshot. The manager publishes at its
        // own animation cadence; resetting here made every compositor delta zero and froze motion.
        if (!_hasRunningItem) _lastCompositionTime = null;
        else if (!previouslyRunning || !_lastCompositionTime.HasValue) _lastCompositionTime = CompositionNow;
        Invalidate();
        if (_hasRunningItem) RegisterForNextAnimationFrameUpdate();
    }

    private TimeSpan _lastMotionPublishTime;

    public override void OnAnimationFrameUpdate()
    {
        if (!_hasRunningItem) return;

        var now = CompositionNow;
        var delta = _lastCompositionTime.HasValue
            ? Math.Clamp((now - _lastCompositionTime.Value).TotalSeconds, 0, 0.25)
            : 0;
        _lastCompositionTime = now;
        if (delta > 0)
        {
            foreach (var item in _orderedItems)
                if (item.Running) Advance(item, delta);
            ResolveOverlaps(delta);
            PublishLatestMotion(now);
        }

        var targetHz = 144;
        foreach (var item in _orderedItems)
            if (item.Running && item.TargetHz > targetHz) targetHz = item.TargetHz;
        var frameInterval = TimeSpan.FromSeconds(1.0 / Math.Clamp(targetHz, 1, 360));
        if (!_lastRenderedTime.HasValue || now - _lastRenderedTime.Value >= frameInterval * .9)
        {
            _lastRenderedTime = now;
            Invalidate();
        }
        RegisterForNextAnimationFrameUpdate();
    }

    private void PublishLatestMotion(TimeSpan now)
    {
        if (_positionSink is null) return;
        if (now - _lastMotionPublishTime < TimeSpan.FromMilliseconds(50)) return;
        _lastMotionPublishTime = now;

        Volatile.Write(ref _latestMotion, _items.Values.ToDictionary(
            item => item.Id,
            item => new OverlayCompositionMotion(new Point(item.X, item.Y), item.Velocity)));
        if (Interlocked.Exchange(ref _positionPostPending, 1) != 0) return;
        Dispatcher.UIThread.Post(() =>
        {
            try { _positionSink?.Invoke(Volatile.Read(ref _latestMotion)); }
            finally { Interlocked.Exchange(ref _positionPostPending, 0); }
        }, DispatcherPriority.Background);
    }

    public override void OnRender(ImmediateDrawingContext drawingContext)
    {
        // CompositionNow is intentionally read on the compositor thread. Animation timing never
        // uses UI-thread timestamps or dispatcher callbacks.
        _ = CompositionNow;
        foreach (var item in _orderedItems)
        {
            var destination = new Rect(item.X, item.Y, item.Width, item.Height);
            using (drawingContext.PushOpacity(item.Opacity, destination))
            {
                try { drawingContext.DrawBitmap(item.Bitmap, item.SourceRect, destination); }
                catch (ObjectDisposedException) { /* The manager owns lifetime; never dispose here. */ }
            }
            if ((item.Chrome & OverlayChromeSelection.Controls) != 0)
                DrawChrome(drawingContext, item, _accentColor, (item.Chrome & OverlayChromeSelection.Active) != 0);
        }
    }

    public override Rect GetRenderBounds() => new(0, 0, EffectiveSize.X, EffectiveSize.Y);

    private static void Advance(Item item, double seconds)
    {
        var velocity = item.Velocity;
        var speed = Math.Max(1, velocity.Length);
        if (velocity.Length <= .001) velocity = new Vector(speed, 0);
        else velocity *= speed / velocity.Length;

        // Steer before reaching a legal edge rather than reflecting/bouncing at it. This keeps
        // direction changes continuous and preserves sub-pixel motion on high-refresh displays.
        var margin = Math.Clamp(speed * 0.9 + 30, 48, 360);
        var steer = new Vector();
        AddBoundarySteer(ref steer, item.X - item.LegalBounds.Left, velocity.X < 0, new Vector(speed, 0), margin);
        AddBoundarySteer(ref steer, item.LegalBounds.Right - item.X, velocity.X > 0, new Vector(-speed, 0), margin);
        AddBoundarySteer(ref steer, item.Y - item.LegalBounds.Top, velocity.Y < 0, new Vector(0, speed), margin);
        AddBoundarySteer(ref steer, item.LegalBounds.Bottom - item.Y, velocity.Y > 0, new Vector(0, -speed), margin);
        var desired = velocity + steer;
        if (desired.Length > .001) desired *= speed / desired.Length;
        velocity = RotateTowards(velocity, desired, Math.PI * 2.2 * Math.Clamp(seconds, 0, .05));

        item.X += velocity.X * seconds;
        item.Y += velocity.Y * seconds;
        ClampWithoutBounce(item, ref velocity);
        item.Velocity = velocity;
    }

    private static void AddBoundarySteer(ref Vector steer, double distance, bool headingOutward, Vector inward, double margin)
    {
        if (!headingOutward || distance >= margin) return;
        var strength = 1 - Math.Clamp(distance / margin, 0, 1);
        steer += inward * (0.65 + strength * strength * 2.35);
    }

    private static Vector RotateTowards(Vector current, Vector desired, double maxRadians)
    {
        var speed = Math.Max(1, current.Length);
        if (desired.Length <= .001 || maxRadians <= 0) return current;
        var from = Math.Atan2(current.Y, current.X);
        var to = Math.Atan2(desired.Y, desired.X);
        var delta = Math.Atan2(Math.Sin(to - from), Math.Cos(to - from));
        var angle = from + Math.Clamp(delta, -maxRadians, maxRadians);
        return new Vector(Math.Cos(angle) * speed, Math.Sin(angle) * speed);
    }

    private static void ClampWithoutBounce(Item item, ref Vector velocity)
    {
        var speed = Math.Max(1, velocity.Length);
        var inward = Math.Max(1, speed * .18);
        if (item.LegalBounds.Right <= item.LegalBounds.Left)
        {
            item.X = item.LegalBounds.Left;
            velocity = new Vector(0, velocity.Y);
        }
        else if (item.X <= item.LegalBounds.Left)
        {
            item.X = item.LegalBounds.Left;
            if (velocity.X < inward) velocity = new Vector(inward, velocity.Y);
        }
        else if (item.X >= item.LegalBounds.Right)
        {
            item.X = item.LegalBounds.Right;
            if (velocity.X > -inward) velocity = new Vector(-inward, velocity.Y);
        }

        if (item.LegalBounds.Bottom <= item.LegalBounds.Top)
        {
            item.Y = item.LegalBounds.Top;
            velocity = new Vector(velocity.X, 0);
        }
        else if (item.Y <= item.LegalBounds.Top)
        {
            item.Y = item.LegalBounds.Top;
            if (velocity.Y < inward) velocity = new Vector(velocity.X, inward);
        }
        else if (item.Y >= item.LegalBounds.Bottom)
        {
            item.Y = item.LegalBounds.Bottom;
            if (velocity.Y > -inward) velocity = new Vector(velocity.X, -inward);
        }

        if (velocity.Length > .001 && Math.Abs(velocity.Length - speed) > .001)
            velocity *= speed / velocity.Length;
    }

    private void ResolveOverlaps(double delta)
    {
        for (var firstIndex = 0; firstIndex < _orderedItems.Count - 1; firstIndex++)
        for (var secondIndex = firstIndex + 1; secondIndex < _orderedItems.Count; secondIndex++)
        {
            var first = _orderedItems[firstIndex];
            var second = _orderedItems[secondIndex];
            var firstMoves = first.Running && first.AvoidOverlap;
            var secondMoves = second.Running && second.AvoidOverlap;
            if (!firstMoves && !secondMoves) continue;

            var firstRect = new Rect(first.X, first.Y, first.Width, first.Height);
            var secondRect = new Rect(second.X, second.Y, second.Width, second.Height);
            var overlapX = Math.Min(firstRect.Right, secondRect.Right) - Math.Max(firstRect.Left, secondRect.Left);
            var overlapY = Math.Min(firstRect.Bottom, secondRect.Bottom) - Math.Max(firstRect.Top, secondRect.Top);
            if (overlapX <= 0 || overlapY <= 0) continue;

            var horizontal = overlapX <= overlapY;
            var direction = horizontal
                ? (firstRect.Center.X <= secondRect.Center.X ? -1d : 1d)
                : (firstRect.Center.Y <= secondRect.Center.Y ? -1d : 1d);
            var distance = (horizontal ? overlapX : overlapY) + .05;
            var divisor = firstMoves && secondMoves ? 2 : 1;
            var turn = Math.PI * 4.0 * Math.Clamp(delta, .002, .05);

            // Separate only the amount required to re-establish the no-overlap invariant, then turn
            // progressively away. This avoids the old restore/reflect oscillation at contact.
            if (firstMoves)
            {
                if (horizontal) first.X += direction * distance / divisor;
                else first.Y += direction * distance / divisor;
                first.Velocity = SteerAway(first.Velocity, horizontal, direction, turn);
                Clamp(first);
            }
            if (secondMoves)
            {
                if (horizontal) second.X -= direction * distance / divisor;
                else second.Y -= direction * distance / divisor;
                second.Velocity = SteerAway(second.Velocity, horizontal, -direction, turn);
                Clamp(second);
            }
        }
    }

    private static Vector SteerAway(Vector velocity, bool horizontal, double direction, double maxTurn)
    {
        var speed = Math.Max(1, velocity.Length);
        var target = horizontal
            ? new Vector(direction * speed, velocity.Y)
            : new Vector(velocity.X, direction * speed);
        if (target.Length > .001) target *= speed / target.Length;
        return RotateTowards(velocity, target, maxTurn);
    }

    private static void Clamp(Item item)
    {
        item.X = Math.Clamp(item.X, item.LegalBounds.Left, item.LegalBounds.Right);
        item.Y = Math.Clamp(item.Y, item.LegalBounds.Top, item.LegalBounds.Bottom);
    }

    private static void DrawChrome(ImmediateDrawingContext context, Item item, Color accentColor, bool showGlow)
    {
        var rect = new Rect(item.X, item.Y, item.Width, item.Height);
        var accent = new ImmutableSolidColorBrush(accentColor);
        var panel = new ImmutableSolidColorBrush(Color.FromArgb(225, 32, 36, 42));
        var glass = new ImmutableSolidColorBrush(Color.FromArgb(205, 45, 50, 58));
        var white = new ImmutableSolidColorBrush(Colors.White);
        if (showGlow)
            context.DrawRectangle(null, new ImmutablePen(accent, 1.35), rect, 7, 7);

        var close = new Rect(rect.Right - 34, rect.Top + 7, 27, 27);
        var closeCenter = close.Center;
        context.DrawEllipse(panel, null, closeCenter, 13, 13);
        context.DrawLine(new ImmutablePen(white, 1.8), new Point(closeCenter.X - 4, closeCenter.Y - 4), new Point(closeCenter.X + 4, closeCenter.Y + 4));
        context.DrawLine(new ImmutablePen(white, 1.8), new Point(closeCenter.X + 4, closeCenter.Y - 4), new Point(closeCenter.X - 4, closeCenter.Y + 4));

        var resize = new Rect(rect.Right - 28, rect.Bottom - 28, 24, 24);
        context.DrawRectangle(glass, null, resize, 5, 5);
        for (var k = 0; k < 3; k++)
            context.DrawLine(new ImmutablePen(accent, 1.25), new Point(rect.Right - 7 - k * 5, rect.Bottom - 5), new Point(rect.Right - 5, rect.Bottom - 7 - k * 5));

        // Right-middle horizontal resize control (↔) — fixed overlay coordinates.
        var widthHandle = new Rect(rect.Right - 22, rect.Y + rect.Height / 2 - 22, 22, 44);
        context.DrawRectangle(glass, new ImmutablePen(accent, 1.2), widthHandle, 6, 6);
        var wc = widthHandle.Center;
        context.DrawLine(new ImmutablePen(white, 2), new Point(wc.X - 7, wc.Y), new Point(wc.X + 7, wc.Y));
        context.DrawLine(new ImmutablePen(white, 2), new Point(wc.X - 7, wc.Y), new Point(wc.X - 3, wc.Y - 4));
        context.DrawLine(new ImmutablePen(white, 2), new Point(wc.X - 7, wc.Y), new Point(wc.X - 3, wc.Y + 4));
        context.DrawLine(new ImmutablePen(white, 2), new Point(wc.X + 7, wc.Y), new Point(wc.X + 3, wc.Y - 4));
        context.DrawLine(new ImmutablePen(white, 2), new Point(wc.X + 7, wc.Y), new Point(wc.X + 3, wc.Y + 4));

        // Top-middle vertical resize control (↕) — fixed overlay coordinates.
        var heightHandle = new Rect(rect.X + rect.Width / 2 - 22, rect.Y, 44, 22);
        context.DrawRectangle(glass, new ImmutablePen(accent, 1.2), heightHandle, 6, 6);
        var hc = heightHandle.Center;
        context.DrawLine(new ImmutablePen(white, 2), new Point(hc.X, hc.Y - 6), new Point(hc.X, hc.Y + 6));
        context.DrawLine(new ImmutablePen(white, 2), new Point(hc.X, hc.Y - 6), new Point(hc.X - 4, hc.Y - 2));
        context.DrawLine(new ImmutablePen(white, 2), new Point(hc.X, hc.Y - 6), new Point(hc.X + 4, hc.Y - 2));
        context.DrawLine(new ImmutablePen(white, 2), new Point(hc.X, hc.Y + 6), new Point(hc.X - 4, hc.Y + 2));
        context.DrawLine(new ImmutablePen(white, 2), new Point(hc.X, hc.Y + 6), new Point(hc.X + 4, hc.Y + 2));

        // Left-middle opacity slider — same geometry as manager/native renderer.
        var sliderHeight = Math.Min(130.0, Math.Max(70.0, rect.Height - 80.0));
        var slider = new Rect(rect.X + 8, rect.Y + (rect.Height - sliderHeight) / 2, 22, sliderHeight);
        context.DrawRectangle(glass, new ImmutablePen(accent, 1.0), slider, 6, 6);
        var sliderX = slider.Center.X;
        context.DrawLine(new ImmutablePen(white, 2), new Point(sliderX, slider.Top + 10), new Point(sliderX, slider.Bottom - 10));
        var knobY = slider.Bottom - 10 - (slider.Height - 20) * Math.Clamp(item.Opacity, .1, 1.0);
        context.DrawEllipse(accent, null, new Point(sliderX, knobY), 5, 5);

        var zoomOut = new Rect(rect.X + Math.Max(4, rect.Width / 2 - 36), rect.Bottom - 38, 30, 30);
        var zoomIn = new Rect(rect.X + Math.Max(4, rect.Width / 2 + 6), rect.Bottom - 38, 30, 30);
        DrawZoomButton(context, zoomOut, false, glass, accent, white);
        DrawZoomButton(context, zoomIn, true, glass, accent, white);
    }

    private static void DrawZoomButton(ImmediateDrawingContext context, Rect rect, bool plus, IImmutableBrush fill, IImmutableBrush accent, IImmutableBrush text)
    {
        context.DrawRectangle(fill, new ImmutablePen(accent, 1.2), rect, 7, 7);
        var center = rect.Center;
        context.DrawLine(new ImmutablePen(text, 2), new Point(center.X - 5, center.Y), new Point(center.X + 5, center.Y));
        if (plus) context.DrawLine(new ImmutablePen(text, 2), new Point(center.X, center.Y - 5), new Point(center.X, center.Y + 5));
    }
}
