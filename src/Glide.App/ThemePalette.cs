using Avalonia.Media;

namespace Glide.App;

/// <summary>
/// Single source for accent/neutral/warning colour literals. <c>GlideTheme.axaml</c> remains the
/// XAML master for brushes; C# code reads these constants instead of inline hex (4.5.2 audit).
/// Previously <c>#38A9F5</c> vs <c>#36B8F4</c> drifted across overlay/viewport/dialog code.
/// </summary>
public static class ThemePalette
{
    public const string GlideBlue = "#38A9F5";
    public const string OverlayBlue = "#36B8F4";
    public const string NavAccent = "#39A9E5";
    public const string OverlayAccent = "#AE94FF";
    public const string MutedDark = "#AAB5C2";
    public const string MutedLight = "#56616B";
    public const string Muted2Dark = "#7E8995";
    public const string Border = "#343C42";
    public const string Warning = "#E5B53B";
    public const string WarningWash = "#1AE5B53B";
    public const string FallbackBackground = "#101214";
    public const string CloseDanger = "#C42B1C";
    public const string CloseDangerPressed = "#A92317";

    public static string ResolveAccentHex(string? choice) => choice switch
    {
        "Cyan" => "#22C7E8",
        "Teal" => "#30C7B5",
        "Emerald" => "#32C97D",
        "Lime" => "#8CCF45",
        "Violet" => "#A98CFF",
        "Indigo" => "#6E83FF",
        "Magenta" => "#D36CE6",
        "Rose" => "#EE6C9A",
        "Crimson" => "#D94B61",
        "Orange" => "#F08A45",
        "Amber" => "#E0A54A",
        _ => GlideBlue,
    };

    public static string ResolveAccentSoftHex(string? choice) => choice switch
    {
        "Teal" => "#234842",
        "Violet" => "#44395E",
        "Amber" => "#4B3D28",
        _ => "#27435E",
    };

    public static string ResolveGlowHex(string? choice, string? accentChoice) => choice switch
    {
        "Ice blue" => "#78D6FF",
        "Cyan" => "#3BDAF5",
        "Teal" => "#4DDBC9",
        "Emerald" => "#55D99A",
        "Violet" => "#BCA7FF",
        "Magenta" => "#E28AF0",
        "Rose" => "#FF8DB0",
        "Amber" => "#F4C66A",
        "White" => "#FFFFFF",
        _ => ResolveAccentHex(accentChoice),
    };

    /// <summary>Normalizes user-supplied hex (adds '#', validates via parse) or returns fallback.</summary>
    public static string NormalizeHex(string? value, string fallback)
    {
        if (string.IsNullOrWhiteSpace(value)) return fallback;
        var text = value.Trim(); if (!text.StartsWith('#')) text = "#" + text;
        try { _ = Color.Parse(text); return text; } catch { return fallback; }
    }
}
