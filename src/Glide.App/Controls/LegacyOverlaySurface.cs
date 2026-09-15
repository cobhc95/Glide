using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Glide.App.Controls;

/// <summary>
/// Single retained overlay surface. This intentionally mirrors legacy Glide's one-surface model:
/// every Window-in-Window image and its chrome are drawn in one Render pass, with no per-overlay
/// Border/Grid/Image visual tree, no layout during drag/pan, and no transform allocations.
/// </summary>
public sealed class LegacyOverlaySurface : Control
{
    internal WindowInWindowOverlayManager? Manager { get; set; }

    public LegacyOverlaySurface()
    {
        ClipToBounds = true;
        Focusable = true;
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        Manager?.RenderSurface(context);
    }
}
