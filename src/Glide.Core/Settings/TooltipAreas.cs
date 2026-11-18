namespace Glide.Core.Settings;

/// <summary>One area of Glide whose tooltips share a show delay (5.1.0 Tooltips settings).</summary>
public sealed record TooltipArea(string Id, string Label, string SettingId, int DefaultMs);

/// <summary>
/// The master list of tooltip areas (5.1.0): each has its own "tooltip delay" setting. The app applies the delays only
/// through this list (one style per area root, see Glide.App TooltipDelayStyle), so no control carries its own delay.
/// Defaults follow Windows: about 0.6 s for buttons and menus, 1.5 s for the Explorer item info tip (Windows Explorer
/// waits about that long before an item's info tip).
/// </summary>
public static class TooltipAreas
{
    public const string Settings = "settings";
    public const string Main = "main";
    public const string Overlay = "overlay";
    public const string Explorer = "explorer";

    public const int MinMs = 0;
    public const int MaxMs = 5000;
    public const int StepMs = 100;

    /// <summary>"Show info tooltip when hovering items" (Explorer tab).</summary>
    public const string ExplorerInfoTooltipSettingId = "explorer.itemInfoTooltip";

    public static IReadOnlyList<TooltipArea> All { get; } =
    [
        new(Settings, "Settings window", "appearance.tooltipDelaySettings", 600),
        new(Main, "Main interface (title bar, tabs, status bar)", "appearance.tooltipDelayMain", 600),
        new(Overlay, "Overlay mode", "appearance.tooltipDelayOverlay", 600),
        new(Explorer, "Explorer tab items", "appearance.tooltipDelayExplorer", 1500),
    ];

    public static TooltipArea Find(string areaId) =>
        All.FirstOrDefault(a => string.Equals(a.Id, areaId, StringComparison.OrdinalIgnoreCase)) ?? All[1];

    public static int Clamp(int ms) => Math.Clamp(ms, MinMs, MaxMs);

    /// <summary>The delay in effect for <paramref name="areaId"/> (clamped; the area's default for unknown ids).</summary>
    public static int DelayMs(GlideSettingsState settings, string areaId) => Clamp(Find(areaId).Id switch
    {
        Settings => settings.TooltipDelaySettingsMs,
        Overlay => settings.TooltipDelayOverlayMs,
        Explorer => settings.TooltipDelayExplorerMs,
        _ => settings.TooltipDelayMainMs,
    });

    /// <summary>Writes the delay of <paramref name="areaId"/> (used by the Settings window, which edits the list generically).</summary>
    public static void SetDelayMs(GlideSettingsState settings, string areaId, int ms)
    {
        ms = Clamp(ms);
        switch (Find(areaId).Id)
        {
            case Settings: settings.TooltipDelaySettingsMs = ms; break;
            case Overlay: settings.TooltipDelayOverlayMs = ms; break;
            case Explorer: settings.TooltipDelayExplorerMs = ms; break;
            default: settings.TooltipDelayMainMs = ms; break;
        }
    }
}
