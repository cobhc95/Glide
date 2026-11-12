using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Rendering.SceneGraph;
using Glide.Core;

namespace Glide.App.Controls;

/// <summary>Navigation-bar state a renderer needs to draw a video overlay's controls.</summary>
public readonly record struct OverlayVideoChrome(double Progress, bool Playing, bool Muted, double Volume = 1);

/// <summary>
/// Draws the video navigation bar (play/pause, seek track, mute) for the managed and compositor
/// overlay renderers from <see cref="OverlayVideoChromeLayout"/>. Only rectangles, lines and
/// ellipses are used because the compositor's immediate context has no geometry fill; the native
/// retained-window renderer rasterizes the same shapes itself.
/// </summary>
internal static class OverlayVideoChromePainter
{
    private interface ICanvas
    {
        void Rect(Rect rect, Color? fill, Color? stroke, double radius);
        void Line(Point a, Point b, Color color, double thickness);
        void Ellipse(Point center, double radius, Color fill);
    }

    private sealed class ManagedCanvas(DrawingContext context) : ICanvas
    {
        public void Rect(Rect rect, Color? fill, Color? stroke, double radius) =>
            context.DrawRectangle(fill is { } f ? new SolidColorBrush(f) : null, stroke is { } s ? new Pen(new SolidColorBrush(s), 1.2) : null, rect, radius, radius);
        public void Line(Point a, Point b, Color color, double thickness) => context.DrawLine(new Pen(new SolidColorBrush(color), thickness), a, b);
        public void Ellipse(Point center, double radius, Color fill) => context.DrawEllipse(new SolidColorBrush(fill), null, center, radius, radius);
    }

    private sealed class ImmediateCanvas(ImmediateDrawingContext context) : ICanvas
    {
        public void Rect(Rect rect, Color? fill, Color? stroke, double radius) =>
            context.DrawRectangle(fill is { } f ? new ImmutableSolidColorBrush(f) : null, stroke is { } s ? new ImmutablePen(new ImmutableSolidColorBrush(s), 1.2) : null, rect, radius, radius);
        public void Line(Point a, Point b, Color color, double thickness) => context.DrawLine(new ImmutablePen(new ImmutableSolidColorBrush(color), thickness), a, b);
        public void Ellipse(Point center, double radius, Color fill) => context.DrawEllipse(new ImmutableSolidColorBrush(fill), null, center, radius, radius);
    }

    internal static readonly Color Glass = Color.FromArgb(205, 45, 50, 58);
    internal static readonly Color Track = Color.FromArgb(110, 255, 255, 255);

    public static void Draw(DrawingContext context, Rect overlay, OverlayVideoChrome video, Color accent) =>
        Draw(new ManagedCanvas(context), overlay, video, accent);

    public static void Draw(ImmediateDrawingContext context, Rect overlay, OverlayVideoChrome video, Color accent) =>
        Draw(new ImmediateCanvas(context), overlay, video, accent);

    private static void Draw(ICanvas canvas, Rect overlay, OverlayVideoChrome video, Color accent)
    {
        Rect Host(ChromeRect r) => new(overlay.X + r.X, overlay.Y + r.Y, r.Width, r.Height);
        var w = overlay.Width; var h = overlay.Height;
        var white = Colors.White;

        canvas.Rect(Host(OverlayVideoChromeLayout.NavigationBar(w, h)), Glass, null, 8);

        var play = Host(OverlayVideoChromeLayout.PlayPauseButton(w, h));
        canvas.Rect(play, null, accent, 7);
        var c = play.Center;
        if (video.Playing)
        {
            canvas.Line(new Point(c.X - 4, c.Y - 7), new Point(c.X - 4, c.Y + 7), white, 3);
            canvas.Line(new Point(c.X + 4, c.Y - 7), new Point(c.X + 4, c.Y + 7), white, 3);
        }
        else
        {
            // Right-pointing triangle filled with vertical strokes.
            for (var dx = 0.0; dx <= 12; dx += 1)
            {
                var half = 7 * (1 - dx / 12.0);
                canvas.Line(new Point(c.X - 5 + dx, c.Y - half), new Point(c.X - 5 + dx, c.Y + half), white, 1.2);
            }
        }

        if (OverlayVideoChromeLayout.PreviousButton(w, h) is { } previousRect)
            DrawSkip(canvas, Host(previousRect), forward: false, accent);
        if (OverlayVideoChromeLayout.NextButton(w, h) is { } nextRect)
            DrawSkip(canvas, Host(nextRect), forward: true, accent);

        if (OverlayVideoChromeLayout.VolumeTrack(w, h) is { } volumeRect)
        {
            var volume = Host(volumeRect);
            var vy = volume.Center.Y;
            var level = video.Muted ? 0 : Math.Clamp(video.Volume, 0, 1);
            var levelX = volume.X + volume.Width * level;
            canvas.Line(new Point(volume.X, vy), new Point(volume.Right, vy), Track, 3);
            if (levelX > volume.X) canvas.Line(new Point(volume.X, vy), new Point(levelX, vy), accent, 3);
            canvas.Ellipse(new Point(levelX, vy), 5, accent);
        }

        var track = Host(OverlayVideoChromeLayout.SeekTrack(w, h));
        var y = track.Center.Y;
        var progressX = track.X + track.Width * Math.Clamp(video.Progress, 0, 1);
        canvas.Line(new Point(track.X, y), new Point(track.Right, y), Track, 3);
        if (progressX > track.X) canvas.Line(new Point(track.X, y), new Point(progressX, y), accent, 3);
        canvas.Ellipse(new Point(progressX, y), 6, accent);

        var mute = Host(OverlayVideoChromeLayout.MuteButton(w, h));
        canvas.Rect(mute, null, accent, 7);
        var m = mute.Center;
        // Speaker body and cone.
        canvas.Rect(new Rect(m.X - 8, m.Y - 3, 4, 6), white, null, 0);
        for (var dx = 0.0; dx <= 5; dx += 1)
            canvas.Line(new Point(m.X - 4 + dx, m.Y - 3 - dx), new Point(m.X - 4 + dx, m.Y + 3 + dx), white, 1.2);
        if (video.Muted || video.Volume <= 0.001)
        {
            canvas.Line(new Point(m.X + 3, m.Y - 4), new Point(m.X + 9, m.Y + 4), white, 1.8);
            canvas.Line(new Point(m.X + 9, m.Y - 4), new Point(m.X + 3, m.Y + 4), white, 1.8);
        }
        else
        {
            canvas.Line(new Point(m.X + 4, m.Y - 3), new Point(m.X + 4, m.Y + 3), white, 1.6);
            if (video.Volume > 0.5) canvas.Line(new Point(m.X + 7, m.Y - 6), new Point(m.X + 7, m.Y + 6), white, 1.6);
        }
    }

    /// <summary>Previous (|◀) or next (▶|) video button.</summary>
    private static void DrawSkip(ICanvas canvas, Rect button, bool forward, Color accent)
    {
        var white = Colors.White;
        canvas.Rect(button, null, accent, 7);
        var c = button.Center;
        var direction = forward ? 1 : -1;
        // Triangle pointing in the skip direction, filled with vertical strokes, then the end bar.
        for (var dx = 0.0; dx <= 8; dx += 1)
        {
            var half = 6 * (1 - dx / 8.0);
            var x = c.X - direction * 5 + direction * dx;
            canvas.Line(new Point(x, c.Y - half), new Point(x, c.Y + half), white, 1.2);
        }
        canvas.Line(new Point(c.X + direction * 5, c.Y - 6), new Point(c.X + direction * 5, c.Y + 6), white, 2.2);
    }
}
