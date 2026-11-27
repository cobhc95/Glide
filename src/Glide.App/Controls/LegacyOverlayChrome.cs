using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Glide.App.Controls;

/// <summary>
/// Overlay mode's chrome: the picture-in-picture chrome (frame, hover glow, close, opacity pill, zoom buttons, corner grip and
/// the two edge handles) drawn over the window's picture by the shared <see cref="OverlayFrameChromePainter"/> from
/// <see cref="OverlayFrameGeometry"/>. Hit targets are separate transparent controls in MainWindow.Overlays.cs.
/// </summary>
public sealed class LegacyOverlayChrome : Control
{
    public bool ChromeVisible { get; set; }
    public double OpacityValue { get; set; } = 1.0;
    public IBrush AccentBrush { get; set; } = new SolidColorBrush(Color.Parse("#36B8F4"));
    public IBrush ForegroundBrush { get; set; } = Brushes.White;

    public static Rect CloseRect(Size size) => OverlayChromePolicy.CloseRect(size);
    public static Rect ResizeRect(Size size) => OverlayChromePolicy.ResizeRect(size);
    public static Rect SliderRect(Size size) => OverlayChromePolicy.SliderRect(size);
    public static Rect ZoomOutRect(Size size) => OverlayChromePolicy.ZoomOutRect(size);
    public static Rect ZoomInRect(Size size) => OverlayChromePolicy.ZoomInRect(size);

    /// <summary>
    /// The same chrome a picture-in-picture overlay of this size draws. The only adaptation: a top-level window has no
    /// room outside its bounds, so the frame sits <see cref="OverlayFrameGeometry.GlowReach"/> inside and the glow fills the gap.
    /// </summary>
    internal OverlayFrameChromeSpec BuildSpec(Size size) =>
        OverlayFrameGeometry.BuildSpec(new Rect(default, size), OpacityValue, glow: true, frameInset: OverlayFrameGeometry.GlowReach, adaptToSize: true);

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        if (!ChromeVisible || Bounds.Width <= 1 || Bounds.Height <= 1) return;
        var accent = (AccentBrush as ISolidColorBrush)?.Color ?? Color.FromRgb(0x36, 0xB8, 0xF4);
        OverlayFrameChromePainter.Draw(context, BuildSpec(Bounds.Size), accent);
    }
}
