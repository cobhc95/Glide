using Avalonia;
using Avalonia.Controls;

namespace Glide.App.Settings;

/// <summary>
/// Default and minimum Settings window size, and fitting it to small displays. The defaults are
/// deliberately compact (the window used to open at 1040 x 760, larger than a 1366 x 768 laptop
/// panel at 125-150 % scaling can show). The size is never persisted - Settings, like every Glide
/// dialog, opens at the default and centred over its owner - so there is no saved size to migrate.
/// </summary>
public partial class SettingsWindow
{
    /// <summary>First-launch (and every-launch) client width in device-independent pixels.</summary>
    public const double DefaultWidth = 930;
    /// <summary>First-launch (and every-launch) client height in device-independent pixels.</summary>
    public const double DefaultHeight = 630;
    /// <summary>Smallest client width the layout is designed for.</summary>
    public const double MinimumWidth = 720;
    /// <summary>Smallest client height the layout is designed for.</summary>
    public const double MinimumHeight = 480;
    /// <summary>The window never opens larger than this share of the screen's usable (working) area.</summary>
    public const double MaxWorkingAreaShare = 0.9;
    /// <summary>The fixed (non-resizable) navigation column: the longest label plus its padding and glyph.</summary>
    public const double NavigationColumnWidth = 224;

    /// <summary>Default and minimum client size for a usable area of the given size (DIPs).</summary>
    internal static (Size Size, Size Minimum) ComputeInitialSize(double workingWidth, double workingHeight)
    {
        var maxWidth = Math.Max(1, Math.Floor(workingWidth * MaxWorkingAreaShare));
        var maxHeight = Math.Max(1, Math.Floor(workingHeight * MaxWorkingAreaShare));
        var size = new Size(Math.Min(DefaultWidth, maxWidth), Math.Min(DefaultHeight, maxHeight));
        var minimum = new Size(Math.Min(MinimumWidth, size.Width), Math.Min(MinimumHeight, size.Height));
        return (size, minimum);
    }

    /// <summary>
    /// Top-left (device pixels) of a window of <paramref name="size"/> centred on <paramref name="ownerCentre"/>
    /// and shifted so that it lies fully inside <paramref name="working"/>.
    /// </summary>
    internal static PixelPoint PlaceWithin(PixelRect working, PixelPoint ownerCentre, PixelSize size)
    {
        var x = ownerCentre.X - size.Width / 2;
        var y = ownerCentre.Y - size.Height / 2;
        x = Math.Min(x, working.Right - size.Width);
        y = Math.Min(y, working.Bottom - size.Height);
        x = Math.Max(x, working.X);
        y = Math.Max(y, working.Y);
        return new PixelPoint(x, y);
    }

    private void FitToScreenOnOpen()
    {
        // A remembered size was already validated against the screens in RestoreSavedPlacement.
        if (_placementRestored) return;
        try
        {
            var screen = Screens.ScreenFromWindow(this)
                         ?? (Owner is WindowBase owner ? Screens.ScreenFromWindow(owner) : null)
                         ?? Screens.Primary;
            if (screen is not null) FitToWorkingArea(screen.WorkingArea, screen.Scaling);
        }
        catch
        {
            // Sizing is a convenience; the XAML defaults are already compact.
        }
    }

    /// <summary>
    /// Clamps the window to <paramref name="working"/> (device pixels at <paramref name="scaling"/>): never larger
    /// than 90 % of it, and when it had to shrink or sits partly off-screen it is re-centred over its owner and
    /// kept fully inside the working area.
    /// </summary>
    internal void FitToWorkingArea(PixelRect working, double scaling)
    {
        var scale = scaling > 0 ? scaling : 1;
        var (size, minimum) = ComputeInitialSize(working.Width / scale, working.Height / scale);
        MinWidth = minimum.Width;
        MinHeight = minimum.Height;
        var shrunk = Width > size.Width + 0.5 || Height > size.Height + 0.5;
        if (Width > size.Width) Width = size.Width;
        if (Height > size.Height) Height = size.Height;

        // Outer (frame) size in pixels for the size the window is about to have.
        var frame = FrameSize ?? ClientSize;
        var chromeW = Math.Max(0, frame.Width - ClientSize.Width);
        var chromeH = Math.Max(0, frame.Height - ClientSize.Height);
        var outer = new PixelSize(
            (int)Math.Ceiling((Math.Min(double.IsNaN(Width) ? size.Width : Width, size.Width) + chromeW) * scale),
            (int)Math.Ceiling((Math.Min(double.IsNaN(Height) ? size.Height : Height, size.Height) + chromeH) * scale));
        var here = new PixelRect(Position, outer);
        var inside = here.X >= working.X && here.Y >= working.Y && here.Right <= working.Right && here.Bottom <= working.Bottom;
        if (!shrunk && inside) return;

        var centre = new PixelPoint(here.X + here.Width / 2, here.Y + here.Height / 2);
        if (Owner is Window owner && owner.IsVisible)
        {
            var ownerFrame = owner.FrameSize ?? owner.ClientSize;
            centre = new PixelPoint(
                owner.Position.X + (int)(ownerFrame.Width * owner.RenderScaling / 2),
                owner.Position.Y + (int)(ownerFrame.Height * owner.RenderScaling / 2));
        }
        Position = PlaceWithin(working, centre, outer);
    }
}
