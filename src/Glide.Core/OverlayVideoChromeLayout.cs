namespace Glide.Core;

/// <summary>Axis-aligned rectangle in overlay-local DIPs (origin = overlay top-left).</summary>
public readonly record struct ChromeRect(double X, double Y, double Width, double Height)
{
    public double Right => X + Width;
    public double Bottom => Y + Height;
    public double CenterX => X + Width / 2;
    public double CenterY => Y + Height / 2;
    public bool Contains(double x, double y) => x >= X && x <= Right && y >= Y && y <= Bottom;
    public bool Intersects(ChromeRect other) => X < other.Right && other.X < Right && Y < other.Bottom && other.Y < Bottom;
}

/// <summary>
/// Single geometry source for overlay chrome that differs between image and video overlays. The
/// managed renderer, the compositor renderer, the native retained-window renderer and the
/// manager's hit testing all read these rectangles, so drawn controls and clickable areas cannot
/// drift apart. Video overlays add a bottom navigation bar; the zoom buttons move above it and the
/// opacity slider is centred in the space that remains.
///
/// The bar adapts to the overlay's width so the seek track always stays usable:
/// narrow = play · seek · mute; medium adds previous/next video; wide also adds a volume slider.
/// </summary>
public static class OverlayVideoChromeLayout
{
    public const double NavigationBarHeight = 32;
    public const double NavigationBarInset = 8;
    /// <summary>Space reserved on the right so the bar never covers the resize corner.</summary>
    public const double ResizeCornerReserve = 40;
    /// <summary>Bar widths at which previous/next buttons, then the volume slider, appear.</summary>
    public const double PreviousNextMinimumBarWidth = 230;
    public const double VolumeMinimumBarWidth = 330;
    private const double ButtonSize = 28;
    private const double ButtonGap = 2;
    private const double VolumeTrackWidth = 64;

    public static ChromeRect NavigationBar(double width, double height) =>
        new(NavigationBarInset, height - NavigationBarInset - NavigationBarHeight,
            Math.Max(ButtonSize * 2, width - NavigationBarInset - ResizeCornerReserve), NavigationBarHeight);

    public static bool HasPreviousNext(double width, double height) => NavigationBar(width, height).Width >= PreviousNextMinimumBarWidth;
    public static bool HasVolumeSlider(double width, double height) => NavigationBar(width, height).Width >= VolumeMinimumBarWidth;

    /// <summary>Previous-video button, or null when the bar is too narrow (use the menu instead).</summary>
    public static ChromeRect? PreviousButton(double width, double height)
    {
        if (!HasPreviousNext(width, height)) return null;
        var bar = NavigationBar(width, height);
        return new ChromeRect(bar.X + 2, bar.Y + 2, ButtonSize, ButtonSize);
    }

    public static ChromeRect PlayPauseButton(double width, double height)
    {
        var bar = NavigationBar(width, height);
        var left = PreviousButton(width, height) is { } previous ? previous.Right + ButtonGap : bar.X + 2;
        return new ChromeRect(left, bar.Y + 2, ButtonSize, ButtonSize);
    }

    /// <summary>Next-video button, or null when the bar is too narrow (use the menu instead).</summary>
    public static ChromeRect? NextButton(double width, double height)
    {
        if (!HasPreviousNext(width, height)) return null;
        var play = PlayPauseButton(width, height);
        return new ChromeRect(play.Right + ButtonGap, play.Y, ButtonSize, ButtonSize);
    }

    public static ChromeRect MuteButton(double width, double height)
    {
        var bar = NavigationBar(width, height);
        return new ChromeRect(bar.Right - 2 - ButtonSize, bar.Y + 2, ButtonSize, ButtonSize);
    }

    /// <summary>Hit area of the volume slider (drawn along its vertical centre), or null when narrow.</summary>
    public static ChromeRect? VolumeTrack(double width, double height)
    {
        if (!HasVolumeSlider(width, height)) return null;
        var mute = MuteButton(width, height);
        var bar = NavigationBar(width, height);
        return new ChromeRect(mute.X - 6 - VolumeTrackWidth, bar.Y + 4, VolumeTrackWidth, bar.Height - 8);
    }

    /// <summary>Hit area of the seek track; the drawn track runs along its vertical centre.</summary>
    public static ChromeRect SeekTrack(double width, double height)
    {
        var leftControl = NextButton(width, height) ?? PlayPauseButton(width, height);
        var rightControl = VolumeTrack(width, height) ?? MuteButton(width, height);
        var left = leftControl.Right + 8;
        var right = Math.Max(left + 1, rightControl.X - 8);
        var bar = NavigationBar(width, height);
        return new ChromeRect(left, bar.Y + 4, right - left, bar.Height - 8);
    }

    /// <summary>Seek fraction [0, 1] for an overlay-local x coordinate.</summary>
    public static double SeekFraction(double width, double height, double localX) =>
        Fraction(SeekTrack(width, height), localX);

    /// <summary>Volume [0, 1] for an overlay-local x coordinate (0 when there is no slider).</summary>
    public static double VolumeFraction(double width, double height, double localX) =>
        VolumeTrack(width, height) is { } track ? Fraction(track, localX) : 0;

    private static double Fraction(ChromeRect track, double localX) =>
        Math.Clamp((localX - track.X) / Math.Max(1, track.Width), 0, 1);

    public static ChromeRect ZoomOut(double width, double height, bool video) =>
        new(Math.Max(4, width / 2 - 36), ZoomTop(height, video), 30, 30);

    public static ChromeRect ZoomIn(double width, double height, bool video) =>
        new(Math.Max(4, width / 2 + 6), ZoomTop(height, video), 30, 30);

    public static ChromeRect OpacitySlider(double width, double height, bool video)
    {
        if (!video)
        {
            var h = Math.Min(130.0, Math.Max(70.0, height - 80.0));
            return new ChromeRect(8, (height - h) / 2, 22, h);
        }
        var available = NavigationBar(width, height).Y - 4;
        var sliderHeight = Math.Min(130.0, Math.Max(40.0, available - 40.0));
        return new ChromeRect(8, Math.Max(0, (available - sliderHeight) / 2), 22, sliderHeight);
    }

    private static double ZoomTop(double height, bool video) =>
        video ? Math.Max(4, height - NavigationBarInset - NavigationBarHeight - 38) : Math.Max(4, height - 38);
}
