using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Rendering.Composition;

namespace Glide.App.Controls;

/// <summary>
/// Single retained overlay surface. This intentionally mirrors legacy Glide's one-surface model:
/// every Window-in-Window image and its chrome are drawn in one Render pass, with no per-overlay
/// Border/Grid/Image visual tree, no layout during drag/pan, and no transform allocations.
/// </summary>
public sealed class LegacyOverlaySurface : Control
{
    internal WindowInWindowOverlayManager? Manager { get; set; }
    private CompositionCustomVisual? _compositorVisual;
    private NativeOverlayWindowRenderer? _nativeRenderer;
    private OverlayRenderSnapshot? _latestSnapshot;
    internal bool UsesCompositionRenderer => _nativeRenderer?.IsAvailable == true || _compositorVisual is not null;
    internal bool UsesNativeWindowRenderer => _nativeRenderer?.IsAvailable == true;

    public LegacyOverlaySurface()
    {
        ClipToBounds = true;
        Focusable = true;
        AttachedToVisualTree += (_, _) => EnsureCompositorVisual();
        DetachedFromVisualTree += (_, _) => _compositorVisual = null;
    }

    /// <summary>
    /// Publishes an immutable overlay frame to the compositor. Call this from the UI thread after
    /// the manager has produced a snapshot. The bitmap references remain caller-owned.
    /// </summary>
    public void PublishCompositorSnapshot(OverlayRenderSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        _latestSnapshot = snapshot;
        if (_nativeRenderer?.IsAvailable == true)
            _nativeRenderer.Publish(snapshot);
        else if (_compositorVisual is not null)
            _compositorVisual.SendHandlerMessage(snapshot);
        else
            InvalidateVisual();
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var arranged = base.ArrangeOverride(finalSize);
        if (_compositorVisual is not null)
            _compositorVisual.Size = new Vector(arranged.Width, arranged.Height);
        return arranged;
    }

    private void EnsureCompositorVisual()
    {
        if (_nativeRenderer?.IsAvailable == true) return;
        if (_compositorVisual is not null) return;
        try
        {
            var visual = ElementComposition.GetElementVisual(this);
            var compositor = visual?.Compositor;
            if (compositor is null) return;
            _compositorVisual = compositor.CreateCustomVisual(new CompositorOverlayVisualHandler());
            _compositorVisual.Size = new Vector(Bounds.Width, Bounds.Height);
            ElementComposition.SetElementChildVisual(this, _compositorVisual);
            if (_latestSnapshot is not null)
                _compositorVisual.SendHandlerMessage(_latestSnapshot);
            else
                Manager?.RefreshCompositionSnapshot();
        }
        catch
        {
            // Composition is optional. RenderSurface remains the complete fallback.
            _compositorVisual = null;
        }
    }

    internal void EnableNativeWindowRenderer(Window owner)
    {
        if (!OperatingSystem.IsWindows() || _nativeRenderer is not null) return;
        var renderer = new NativeOverlayWindowRenderer(owner);
        if (!renderer.IsAvailable) { renderer.Dispose(); return; }
        try { ElementComposition.SetElementChildVisual(this, null); } catch { }
        _compositorVisual = null;
        _nativeRenderer = renderer;
        if (_latestSnapshot is not null) renderer.Publish(_latestSnapshot);
    }

    internal void SetTopmost(bool topmost) => _nativeRenderer?.SetTopmost(topmost);
    internal void SetModalSuppressed(bool suppressed)
    {
        IsVisible = !suppressed;
        _nativeRenderer?.SetModalSuppressed(suppressed);
    }

    internal void DisableCompositionRenderer()
    {
        _nativeRenderer?.Dispose();
        _nativeRenderer = null;
        try { ElementComposition.SetElementChildVisual(this, null); } catch { }
        _compositorVisual = null;
        _latestSnapshot = null;
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        if (!UsesCompositionRenderer)
            Manager?.RenderSurface(context);
    }
}
