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
    long MotionRevision,
    OverlayVideoChrome? Video = null);

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
        public OverlayVideoChrome? Video;
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
            item.Video = source.Video;
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
        OverlayDriftMotion.AddBoundarySteer(ref steer, item.X - item.LegalBounds.Left, velocity.X < 0, new Vector(speed, 0), margin);
        OverlayDriftMotion.AddBoundarySteer(ref steer, item.LegalBounds.Right - item.X, velocity.X > 0, new Vector(-speed, 0), margin);
        OverlayDriftMotion.AddBoundarySteer(ref steer, item.Y - item.LegalBounds.Top, velocity.Y < 0, new Vector(0, speed), margin);
        OverlayDriftMotion.AddBoundarySteer(ref steer, item.LegalBounds.Bottom - item.Y, velocity.Y > 0, new Vector(0, -speed), margin);
        var desired = velocity + steer;
        if (desired.Length > .001) desired *= speed / desired.Length;
        velocity = OverlayDriftMotion.RotateTowards(velocity, desired, Math.PI * 2.2 * Math.Clamp(seconds, 0, .05));

        item.X += velocity.X * seconds;
        item.Y += velocity.Y * seconds;
        velocity = OverlayDriftMotion.ClampWithoutBounce(item.LegalBounds, ref item.X, ref item.Y, velocity);
        item.Velocity = velocity;
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
        return OverlayDriftMotion.RotateTowards(velocity, target, maxTurn);
    }

    private static void Clamp(Item item)
    {
        item.X = Math.Clamp(item.X, item.LegalBounds.Left, item.LegalBounds.Right);
        item.Y = Math.Clamp(item.Y, item.LegalBounds.Top, item.LegalBounds.Bottom);
    }

    private static void DrawChrome(ImmediateDrawingContext context, Item item, Color accentColor, bool showGlow)
    {
        // One chrome look for picture-in-picture and Overlay mode (round 4 K1): see OverlayFrameChromePainter.
        var rect = new Rect(item.X, item.Y, item.Width, item.Height);
        var isVideo = item.Video is not null;
        // Frame and glow only while the overlay is active (hover or interaction).
        var spec = OverlayFrameGeometry.BuildSpec(rect, Math.Clamp(item.Opacity, .1, 1.0), glow: showGlow, video: isVideo, showFrame: showGlow);
        OverlayFrameChromePainter.Draw(context, spec, accentColor);
        if (item.Video is { } video) OverlayVideoChromePainter.Draw(context, rect, video, accentColor);
    }
}
