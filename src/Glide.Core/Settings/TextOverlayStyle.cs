namespace Glide.Core.Settings;

/// <summary>
/// Round 4 B5: the text overlay's look where it sits. Over the picture it uses the "Text overlay ..." settings; in the
/// title bar and the vertical tab bar each value can be its own or "the same as over the picture" (blank template, size 0,
/// "Same as over the picture"). The title bar always shows a single line.
/// </summary>
public readonly record struct TextOverlayStyle(string Template, int FontSize, string Color, bool Border, bool SingleLine)
{
    public const string Same = "Same as over the picture";
    public static readonly string[] ColorChoices = [Same, "White", "Soft gray", "Accent", "Amber"];
    public static readonly string[] BorderChoices = [Same, "On", "Off"];
    public const string SameAsText = "Same as text";
    public static readonly string[] SeparatorColorChoices = [SameAsText, "White", "Soft gray", "Accent", "Amber"];

    /// <summary>A stored choice, or the first (default) option when it is not one of <paramref name="choices"/>.</summary>
    public static string NormalizeChoice(string? value, IReadOnlyList<string> choices)
    {
        foreach (var c in choices) if (string.Equals(c, value?.Trim(), StringComparison.OrdinalIgnoreCase)) return c;
        return choices[0];
    }

    public static TextOverlayStyle For(GlideSettingsState s, string? placement)
    {
        var picture = new TextOverlayStyle(s.PictureCounterTemplate, s.PictureCounterFontSize, s.PictureCounterColor, s.PictureCounterBorder, false);
        if (TextOverlayPlacements.IsTitleBar(placement))
            return Merge(picture, s.TextOverlayTitleBarTemplate, s.TextOverlayTitleBarFontSize, s.TextOverlayTitleBarColor, s.TextOverlayTitleBarBorder) with { SingleLine = true };
        if (TextOverlayPlacements.IsTabBar(placement))
            return Merge(picture, s.TextOverlayTabBarTemplate, s.TextOverlayTabBarFontSize, s.TextOverlayTabBarColor, s.TextOverlayTabBarBorder);
        return picture;
    }

    private static TextOverlayStyle Merge(TextOverlayStyle picture, string? template, int fontSize, string? color, string? border) => new(
        string.IsNullOrWhiteSpace(template) ? picture.Template : template,
        fontSize > 0 ? fontSize : picture.FontSize,
        string.IsNullOrWhiteSpace(color) || color == Same ? picture.Color : color,
        border switch { "On" => true, "Off" => false, _ => picture.Border },
        false);
}
