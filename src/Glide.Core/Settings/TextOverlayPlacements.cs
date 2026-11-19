namespace Glide.Core.Settings;

/// <summary>Where the text overlay sits while the vertical (side) tab pane is showing (5.1.2). Master list for the setting.</summary>
public static class TextOverlayPlacements
{
    public const string OverPicture = "Over the picture";
    public const string TabBarTop = "Top of the tab bar";
    public const string TabBarBottom = "Bottom of the tab bar";
    public const string TitleBar = "Title bar";

    public static readonly string[] All = [OverPicture, TabBarTop, TabBarBottom, TitleBar];

    public static string Normalize(string? value) =>
        All.FirstOrDefault(v => string.Equals(v, value?.Trim(), StringComparison.OrdinalIgnoreCase)) ?? OverPicture;
}
