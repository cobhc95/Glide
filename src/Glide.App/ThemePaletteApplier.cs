using Avalonia;
using Avalonia.Media;
using Avalonia.Styling;

namespace Glide.App;

/// <summary>
/// Applies the user's theme, accent, glow and viewer-background choices to the application-wide
/// resource dictionary (the brushes <c>GlideTheme.axaml</c> declares). Moved out of
/// <c>MainWindow.Chrome.cs</c>: it never touched window state, only <see cref="Application.Current"/>.
/// Colour choice resolution stays in <see cref="ThemePalette"/>.
/// </summary>
internal static class ThemePaletteApplier
{
    public static void Apply(string? themeChoice, string accentChoice, string? glowChoice, int glowIntensityPercent, string customAccentHex, string customGlowHex, string backgroundChoice, string customBackgroundHex)
    {
        var resources = Application.Current?.Resources;
        if (resources is null) return;
        static SolidColorBrush B(string value) => new(Color.Parse(value));

        var theme = themeChoice is Glide.Core.Settings.SettingChoiceNames.Light or Glide.Core.Settings.SettingChoiceNames.Neutral ? themeChoice : Glide.Core.Settings.SettingChoiceNames.Dark;
        var accent = string.Equals(accentChoice, "Custom", StringComparison.OrdinalIgnoreCase) ? ThemePalette.NormalizeHex(customAccentHex, ThemePalette.GlideBlue) : ThemePalette.ResolveAccentHex(accentChoice);
        var accentColor = Color.Parse(accent);
        var glowHex = string.Equals(glowChoice, "Custom", StringComparison.OrdinalIgnoreCase) ? ThemePalette.NormalizeHex(customGlowHex, accent) : ThemePalette.ResolveGlowHex(glowChoice, accentChoice);
        var glowColor = Color.Parse(glowHex);
        var glowStrength = Math.Clamp(glowIntensityPercent, 0, 100) / 100.0;
        static Color Shift(Color c, double factor) => Color.FromArgb(c.A,
            (byte)Math.Clamp((int)Math.Round(c.R * factor), 0, 255),
            (byte)Math.Clamp((int)Math.Round(c.G * factor), 0, 255),
            (byte)Math.Clamp((int)Math.Round(c.B * factor), 0, 255));
        static SolidColorBrush A(Color c, byte alpha) => new(Color.FromArgb(alpha, c.R, c.G, c.B));
        byte GlowAlpha(int full) => (byte)Math.Clamp((int)Math.Round(full * glowStrength), 0, 255);

        resources["SystemAccentColor"] = accentColor;
        resources["SystemAccentColorLight1"] = Shift(accentColor, 1.12);
        resources["SystemAccentColorLight2"] = Shift(accentColor, 1.24);
        resources["SystemAccentColorLight3"] = Shift(accentColor, 1.36);
        resources["SystemAccentColorDark1"] = Shift(accentColor, 0.86);
        resources["SystemAccentColorDark2"] = Shift(accentColor, 0.72);
        resources["SystemAccentColorDark3"] = Shift(accentColor, 0.58);
        resources["BrushAccent"] = B(accent);
        resources["BrushNavAccent"] = B(accent);

        if (theme == "Light")
        {
            // Existing Light profile intentionally remains unchanged.
            resources["BrushWindow"] = B("#F4F5F6"); resources["BrushViewport"] = B("#E9EBED"); resources["BrushChrome"] = B("#EEF0F1");
            resources["BrushTabStrip"] = B("#E6E9EB"); resources["BrushSurface"] = B("#FFFFFF"); resources["BrushSurfaceRaised"] = B("#FFFFFF");
            resources["BrushSurfaceHover"] = B("#DDEAF5"); resources["BrushCard"] = B("#FFFFFF"); resources["BrushCardAccent"] = B("#D8E8F7");
            resources["BrushBorder"] = B("#BCC5CC"); resources["BrushBorderSoft"] = B("#D4DADF"); resources["BrushText"] = B("#171A1C");
            resources["BrushMuted"] = B("#56616B"); resources["BrushMuted2"] = B("#79838C"); resources["BrushSettingsRail"] = B("#E8EBED");
            resources["BrushChromeButton"] = B("#F3F3F3"); resources["BrushChromeButtonHover"] = B("#E5E5E5"); resources["BrushChromeButtonPressed"] = B("#DADADA"); resources["BrushChromeButtonBorder"] = B("#F3F3F3");
            resources["BrushTabHover"] = B("#E7E7E7"); resources["BrushTabActive"] = B("#FFFFFF"); resources["BrushTabCloseHover"] = B("#D0D0D0");
            resources["BrushStatusSurface"] = B("#EAF7F8F9"); resources["BrushStatusButton"] = B("#FFFFFF"); resources["BrushStatusButtonHover"] = B("#E4EFF7"); resources["BrushStatusButtonPressed"] = B("#D5E7F4");
            resources["BrushStatusBorder"] = B("#C8D0D6"); resources["BrushStatusButtonBorder"] = B("#C7D0D7"); resources["BrushStatusNavBorder"] = B("#8FBAD3");
            resources["BrushSettingsNavHover"] = B("#DDE5EB"); resources["BrushSettingsNavSelected"] = B("#CFE1F0");
            resources["BrushInput"] = B("#FFFFFF"); resources["BrushInputBorder"] = B("#AEB8C0"); resources["BrushSearch"] = B("#FFFFFF"); resources["BrushFooter"] = B("#ECEFF1");
            resources["BrushPrimaryAction"] = B("#2C76AA"); resources["BrushPrimaryActionHover"] = B("#3787BC"); resources["BrushSecondaryAction"] = B("#F8F9FA"); resources["BrushSecondaryActionHover"] = B("#E5EAEE");
            Application.Current!.RequestedThemeVariant = ThemeVariant.Light;
        }
        else if (theme == "Neutral")
        {
            resources["BrushWindow"] = B("#C4C7C9"); resources["BrushViewport"] = B("#B8BCC0"); resources["BrushChrome"] = B("#B8BBBD");
            resources["BrushTabStrip"] = B("#B7BABD"); resources["BrushSurface"] = B("#D4D6D8"); resources["BrushSurfaceRaised"] = B("#DADCDD");
            resources["BrushSurfaceHover"] = B("#C5D3DD"); resources["BrushCard"] = B("#D1D3D5"); resources["BrushCardAccent"] = B("#C5D4DF");
            resources["BrushBorder"] = B("#9EA4A9"); resources["BrushBorderSoft"] = B("#B4B9BD"); resources["BrushText"] = B("#202428");
            resources["BrushMuted"] = B("#525B62"); resources["BrushMuted2"] = B("#687177"); resources["BrushSettingsRail"] = B("#BEC1C4");
            resources["BrushChromeButton"] = B("#B8BBBD"); resources["BrushChromeButtonHover"] = B("#C6C9CB"); resources["BrushChromeButtonPressed"] = B("#AEB2B4"); resources["BrushChromeButtonBorder"] = B("#B8BBBD");
            resources["BrushTabHover"] = B("#C8CBCD"); resources["BrushTabActive"] = B("#D8DADB"); resources["BrushTabCloseHover"] = B("#B5B9BC");
            resources["BrushStatusSurface"] = B("#E6CDD0D2"); resources["BrushStatusButton"] = B("#D6D8D9"); resources["BrushStatusButtonHover"] = B("#C7D8E3"); resources["BrushStatusButtonPressed"] = B("#B9CDD9");
            resources["BrushStatusBorder"] = B("#A3AAAF"); resources["BrushStatusButtonBorder"] = B("#A7AEB3"); resources["BrushStatusNavBorder"] = B("#769CB4");
            resources["BrushSettingsNavHover"] = B("#C6CDD2"); resources["BrushSettingsNavSelected"] = B("#B9CCD9");
            resources["BrushInput"] = B("#D9DBDC"); resources["BrushInputBorder"] = B("#969EA4"); resources["BrushSearch"] = B("#D9DBDC"); resources["BrushFooter"] = B("#BEC2C5");
            resources["BrushPrimaryAction"] = B("#2C76AA"); resources["BrushPrimaryActionHover"] = B("#3787BC"); resources["BrushSecondaryAction"] = B("#D2D4D5"); resources["BrushSecondaryActionHover"] = B("#C4C8CB");
            Application.Current!.RequestedThemeVariant = ThemeVariant.Light;
        }
        else
        {
            // Existing Dark profile intentionally remains unchanged and remains the default.
            resources["BrushWindow"] = B("#181A1D"); resources["BrushViewport"] = B("#0E0F10"); resources["BrushChrome"] = B("#1A2224");
            resources["BrushTabStrip"] = B("#161616"); resources["BrushSurface"] = B("#1B1E23"); resources["BrushSurfaceRaised"] = B("#202329");
            resources["BrushSurfaceHover"] = B("#21364A"); resources["BrushCard"] = B("#191C1F"); resources["BrushCardAccent"] = B("#22384C");
            resources["BrushBorder"] = B("#343C42"); resources["BrushBorderSoft"] = B("#2A3035"); resources["BrushText"] = B("#F2F4F6");
            resources["BrushMuted"] = B("#AAB5C2"); resources["BrushMuted2"] = B("#7E8995"); resources["BrushSettingsRail"] = B("#1A1D20");
            resources["BrushChromeButton"] = B("#161616"); resources["BrushChromeButtonHover"] = B("#303030"); resources["BrushChromeButtonPressed"] = B("#3A3A3A"); resources["BrushChromeButtonBorder"] = B("#161616");
            resources["BrushTabHover"] = B("#292929"); resources["BrushTabActive"] = B("#333333"); resources["BrushTabCloseHover"] = B("#474747");
            resources["BrushStatusSurface"] = B("#D9202327"); resources["BrushStatusButton"] = B("#1B1F24"); resources["BrushStatusButtonHover"] = B("#23384A"); resources["BrushStatusButtonPressed"] = B("#2A455E");
            resources["BrushStatusBorder"] = B("#30373D"); resources["BrushStatusButtonBorder"] = B("#303840"); resources["BrushStatusNavBorder"] = B("#28536A");
            resources["BrushSettingsNavHover"] = B("#202A33"); resources["BrushSettingsNavSelected"] = B("#29465F");
            resources["BrushInput"] = B("#202226"); resources["BrushInputBorder"] = B("#30363C"); resources["BrushSearch"] = B("#202328"); resources["BrushFooter"] = B("#181B1E");
            resources["BrushPrimaryAction"] = B("#2C76AA"); resources["BrushPrimaryActionHover"] = B("#3787BC"); resources["BrushSecondaryAction"] = B("#1A1C20"); resources["BrushSecondaryActionHover"] = B("#242A30");
            Application.Current!.RequestedThemeVariant = ThemeVariant.Dark;
        }

        var bright = theme != "Dark";
        var background = backgroundChoice switch
        {
            "Black" => "#000000", "Dark gray" => "#202124", "Neutral gray" => "#777777", "Light gray" => "#D0D0D0", "White" => "#FFFFFF",
            "Custom" => ThemePalette.NormalizeHex(customBackgroundHex, ThemePalette.FallbackBackground), _ => null
        };
        if (background is not null) resources["BrushViewport"] = B(background);

        // Glow is deliberately limited to transient/selection surfaces. At the default Follow-accent
        // 100% setting, Dark/Light retain Alpha-5's established accent-derived interaction strengths.
        // At 0%, the ordinary theme hover surfaces remain intact rather than becoming transparent.
        resources["BrushAccentSoft"] = string.Equals(glowChoice, "Follow accent", StringComparison.OrdinalIgnoreCase) && glowStrength >= 0.999
            ? B(ThemePalette.ResolveAccentSoftHex(accentChoice))
            : A(glowColor, GlowAlpha(bright ? 54 : 66));
        if (glowStrength > 0)
        {
            resources["BrushSurfaceHover"] = A(glowColor, GlowAlpha(bright ? 30 : 34));
            resources["BrushCardAccent"] = A(glowColor, GlowAlpha(bright ? 34 : 38));
            resources["BrushStatusButtonHover"] = A(glowColor, GlowAlpha(bright ? 34 : 40));
            resources["BrushStatusButtonPressed"] = A(glowColor, GlowAlpha(bright ? 52 : 62));
            resources["BrushSettingsNavHover"] = A(glowColor, GlowAlpha(bright ? 28 : 32));
            resources["BrushSettingsNavSelected"] = A(glowColor, GlowAlpha(bright ? 48 : 58));
        }
        resources["BrushStatusNavBorder"] = A(accentColor, bright ? (byte)150 : (byte)135);
        resources["BrushPrimaryAction"] = B(accent);
        resources["BrushPrimaryActionHover"] = new SolidColorBrush(Shift(accentColor, bright ? 0.90 : 1.10));
    }
}
