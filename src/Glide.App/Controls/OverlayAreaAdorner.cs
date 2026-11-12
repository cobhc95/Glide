using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Glide.App.Controls;

/// <summary>
/// Adorner over the viewer for custom overlay animation areas. While the user draws an area it
/// dims the view, shows a hint and previews the rectangle; otherwise it outlines the custom area of
/// the overlay under the pointer. Never hit-testable: the overlay manager owns all input.
/// </summary>
internal sealed class OverlayAreaAdorner : Control
{
    private const string DrawHint = "Drag to draw the area this overlay can move in  ·  Esc to cancel";
    private Rect? _area;
    private bool _drawing;
    private Color _accent = Color.FromRgb(0x36, 0xB8, 0xF4);

    public OverlayAreaAdorner()
    {
        IsHitTestVisible = false;
        ClipToBounds = true;
    }

    /// <summary>Updates what is shown; returns without invalidating when nothing changed.</summary>
    public void Show(Rect? area, bool drawing, Color accent)
    {
        if (_area == area && _drawing == drawing && _accent == accent) return;
        _area = area;
        _drawing = drawing;
        _accent = accent;
        IsVisible = drawing || area is not null;
        InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        var bounds = new Rect(Bounds.Size);
        if (_drawing)
        {
            var dim = new SolidColorBrush(Color.FromArgb(70, 0, 0, 0));
            if (_area is { } cut)
            {
                // Dim everything outside the area being drawn.
                context.DrawRectangle(dim, null, new Rect(0, 0, bounds.Width, Math.Max(0, cut.Top)));
                context.DrawRectangle(dim, null, new Rect(0, cut.Bottom, bounds.Width, Math.Max(0, bounds.Height - cut.Bottom)));
                context.DrawRectangle(dim, null, new Rect(0, cut.Top, Math.Max(0, cut.Left), cut.Height));
                context.DrawRectangle(dim, null, new Rect(cut.Right, cut.Top, Math.Max(0, bounds.Width - cut.Right), cut.Height));
            }
            else context.DrawRectangle(dim, null, bounds);

            var text = new FormattedText(DrawHint, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, Typeface.Default, 13, Brushes.White);
            var pill = new Rect((bounds.Width - text.Width) / 2 - 14, 16, text.Width + 28, text.Height + 14);
            context.DrawRectangle(new SolidColorBrush(Color.FromArgb(225, 32, 36, 42)), new Pen(new SolidColorBrush(_accent), 1), pill, 8, 8);
            context.DrawText(text, new Point(pill.X + 14, pill.Y + 7));
        }

        if (_area is not { } area) return;
        context.DrawRectangle(new SolidColorBrush(Color.FromArgb((byte)(_drawing ? 28 : 18), _accent.R, _accent.G, _accent.B)),
            new Pen(new SolidColorBrush(_accent), 2, new DashStyle(new double[] { 4, 3 }, 0)), area, 6, 6);
    }
}
