using Avalonia;
using Avalonia.Controls;

namespace Glide.App.Controls;

/// <summary>
/// Layout of the tab hover tooltip card (5.0.6, approved mockup docs/mockups/tab-hover-preview-5.0.6.md): the preview
/// runs edge to edge across the top, scaled to the card width and cropped rather than letterboxed (at most
/// <see cref="MaxPictureHeight"/> tall), with the text block under it. With a preview the card is
/// <see cref="PreferredPictureWidth"/> wide (wider only when the one-line mouse hint needs it); without one it hugs its
/// text, up to <see cref="MaxTextWidth"/>. No text is ever trimmed: the name and the folder path wrap and the card
/// grows as tall as they need.
/// </summary>
internal sealed class TabHoverCard : Panel
{
    public const double LandscapeWidth = 260;
    public const double MaxPictureHeight = 220;
    public const double MinPortraitWidth = 160;
    public const double MaxTextWidth = 420;

    private double _pictureAspect;

    public TabHoverCard(Border pictureHost, Control text, TextBlock hint)
    {
        PictureHost = pictureHost;
        Text = text;
        Hint = hint;
        Children.Add(pictureHost);
        Children.Add(text);
    }

    /// <summary>The preview slot (hidden while there is no preview).</summary>
    public Border PictureHost { get; }

    /// <summary>The name / path / details / hint block.</summary>
    public Control Text { get; }

    /// <summary>The one-line mouse hint inside <see cref="Text"/>; it never wraps, so it can widen the card.</summary>
    public TextBlock Hint { get; }

    /// <summary>Width / height of the preview; 0 while there is none.</summary>
    public double PictureAspect
    {
        get => _pictureAspect;
        set
        {
            if (_pictureAspect.Equals(value)) return;
            _pictureAspect = value;
            InvalidateMeasure();
        }
    }

    /// <summary>About 260 DIP for a landscape preview; a portrait one is narrower so it stays under the height cap.</summary>
    internal static double PreferredPictureWidth(double aspect) =>
        aspect >= 1 ? LandscapeWidth : Math.Max(MinPortraitWidth, MaxPictureHeight * aspect);

    private bool ShowsPicture => PictureHost.IsVisible && _pictureAspect > 0;

    private double PictureHeight(double width) => ShowsPicture ? Math.Min(width / _pictureAspect, MaxPictureHeight) : 0;

    protected override Size MeasureOverride(Size availableSize)
    {
        var horizontalMargin = Text.Margin.Left + Text.Margin.Right;
        Hint.Measure(Size.Infinity);
        var hintWidth = Hint.DesiredSize.Width + horizontalMargin;
        double width;
        if (ShowsPicture) width = Math.Max(PreferredPictureWidth(_pictureAspect), hintWidth);
        else
        {
            Text.Measure(new Size(MaxTextWidth + horizontalMargin, double.PositiveInfinity));
            width = Math.Max(Text.DesiredSize.Width, hintWidth);
        }
        width = Math.Ceiling(width);
        Text.Measure(new Size(width, double.PositiveInfinity));
        var pictureHeight = PictureHeight(width);
        PictureHost.Measure(new Size(width, pictureHeight));
        return new Size(width, pictureHeight + Text.DesiredSize.Height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var pictureHeight = PictureHeight(finalSize.Width);
        PictureHost.Arrange(new Rect(0, 0, finalSize.Width, pictureHeight));
        Text.Arrange(new Rect(0, pictureHeight, finalSize.Width, Math.Max(0, finalSize.Height - pictureHeight)));
        return finalSize;
    }
}
