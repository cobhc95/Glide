using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Glide.App.Controls;

/// <summary>
/// Minimal transparent render surface for the latency-critical selection rectangle.
/// Geometry is pushed into this control by ImageViewport in view coordinates, so Render performs
/// no viewport callbacks, transforms, hit testing or snapshot allocation.
/// </summary>
public sealed class ImageSelectionOverlay : Control
{
    private Rect? _selection;
    private Color _lastAccent;
    private IBrush _selectionFill = Brushes.Transparent;
    private IBrush _selectionStroke = Brushes.Transparent;

    public void SetSelection(Rect? selection, Color accentColor)
    {
        var accentChanged = accentColor != _lastAccent;
        if (!accentChanged && Nullable.Equals(_selection, selection)) return;
        _selection = selection;
        if (accentChanged) EnsureAccent(accentColor);
        InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        if (_selection is not { } selection || selection.Width <= 0 || selection.Height <= 0) return;

        context.FillRectangle(_selectionFill, selection);
        var scaling = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1.0;
        var line = Math.Max(0.7, 1.0 / Math.Max(1.0, scaling));
        context.DrawRectangle(null, new Pen(_selectionStroke, line), selection);
    }

    private void EnsureAccent(Color color)
    {
        _lastAccent = color;
        _selectionFill = new SolidColorBrush(Color.FromArgb(30, color.R, color.G, color.B));
        _selectionStroke = new SolidColorBrush(Color.FromArgb(245, color.R, color.G, color.B));
    }
}
