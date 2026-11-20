namespace Glide.Core.Settings;

/// <summary>
/// "Text overlay position" master lists (5.1.3). Each tab layout keeps its own position and alignment: tabs along the top
/// (<see cref="TopTabsPositions"/>: the six picture positions and the title bar left/center/right) and tabs at the side
/// (<see cref="All"/>: those plus the vertical tab pane top/bottom). Glide uses the pair matching the current tab layout.
/// Before 5.1.3 the bar placements were a separate "Text overlay with vertical tabs" choice; settings schema 22 maps
/// older files (see <see cref="MigrateLegacy"/>).
/// </summary>
public static class TextOverlayPlacements
{
    public const string TopLeft = "Top left";
    public const string TopCenter = "Top center";
    public const string TopRight = "Top right";
    public const string BottomLeft = "Bottom left";
    public const string BottomCenter = "Bottom center";
    public const string BottomRight = "Bottom right";
    public const string TitleBarLeft = "Title bar – left";
    public const string TitleBarCenter = "Title bar – center";
    public const string TitleBarRight = "Title bar – right";
    public const string TabBarTop = "Vertical tabs – top";
    public const string TabBarBottom = "Vertical tabs – bottom";
    /// <summary>
    /// 5.1.4: a free spot over the picture, set by Alt+dragging the overlay (or the offset boxes). Stored per card as a
    /// picture anchor plus an offset (<see cref="TextOverlayOffsets.GetCustom"/>), so it follows window resizes and a
    /// bottom anchor stays above the status bar. Every other position is an exact preset with no offset.
    /// </summary>
    public const string Custom = "Custom";

    public const string Default = TopRight;
    public const string DefaultAlignment = "Automatic";

    public static readonly string[] PicturePositions = [TopLeft, TopCenter, TopRight, BottomLeft, BottomCenter, BottomRight];
    /// <summary>"Tabs along the top" card: no vertical-tab positions.</summary>
    public static readonly string[] TopTabsPositions = [.. PicturePositions, TitleBarLeft, TitleBarCenter, TitleBarRight, Custom];
    /// <summary>"Tabs at the side" card: every position.</summary>
    public static readonly string[] All = [.. PicturePositions, TitleBarLeft, TitleBarCenter, TitleBarRight, TabBarTop, TabBarBottom, Custom];
    public static readonly string[] Alignments = [DefaultAlignment, "Left", "Center", "Right"];

    public static bool IsTitleBar(string? value) => value is TitleBarLeft or TitleBarCenter or TitleBarRight;
    public static bool IsTabBar(string? value) => value is TabBarTop or TabBarBottom;
    public static bool IsOverPicture(string? value) => !IsTitleBar(value) && !IsTabBar(value);

    /// <summary>Exact match (case/space/dash-insensitive: "Title bar - left" works too); anything else is <see cref="Default"/>.</summary>
    public static string Normalize(string? value)
    {
        var key = Key(value);
        return All.FirstOrDefault(v => Key(v) == key) ?? Default;
    }

    /// <summary>A "Tabs along the top" position: as <see cref="Normalize"/>, but a vertical-tabs value becomes <see cref="Default"/>.</summary>
    public static string NormalizeTopTabs(string? value)
    {
        var position = Normalize(value);
        return IsTabBar(position) ? Default : position;
    }

    public static string NormalizeAlignment(string? value) =>
        Alignments.FirstOrDefault(a => string.Equals(a, value?.Trim(), StringComparison.OrdinalIgnoreCase)) ?? DefaultAlignment;

    /// <summary>True when the settings put the tabs at the side (the "Tabs at the side" card is the one in use).</summary>
    public static bool SideTabsLayout(GlideSettingsState s) =>
        s.TabsEnabled && string.Equals(s.TabStripPosition, "Side", StringComparison.OrdinalIgnoreCase);

    public static string Position(GlideSettingsState s, bool sideTabs) =>
        sideTabs ? Normalize(s.PictureCounterPositionSideTabs) : NormalizeTopTabs(s.PictureCounterPositionTopTabs);

    /// <summary>The picture anchor of the card's position: the preset itself, or the stored anchor of Custom.</summary>
    public static string PictureAnchor(GlideSettingsState s, bool sideTabs)
    {
        var position = Position(s, sideTabs);
        return position == Custom ? TextOverlayOffsets.GetCustom(s.PictureCounterOffsets, sideTabs).Anchor : position;
    }

    /// <summary>Offset from the picture anchor: the Custom offset, or zero for every preset (presets always reset).</summary>
    public static (int X, int Y) Offset(GlideSettingsState s, bool sideTabs)
    {
        if (Position(s, sideTabs) != Custom) return (0, 0);
        var c = TextOverlayOffsets.GetCustom(s.PictureCounterOffsets, sideTabs);
        return (c.X, c.Y);
    }

    /// <summary>Sets the card's position to Custom at <paramref name="anchor"/> + offset (Alt+drag drop).</summary>
    public static void SetCustom(GlideSettingsState s, bool sideTabs, string anchor, int x, int y)
    {
        s.PictureCounterOffsets = TextOverlayOffsets.SetCustom(s.PictureCounterOffsets, sideTabs, anchor, x, y);
        if (sideTabs) s.PictureCounterPositionSideTabs = Custom; else s.PictureCounterPositionTopTabs = Custom;
    }

    /// <summary>
    /// The offset settings edit the Custom position: on Custom they change its offset; on a picture preset a non-zero value
    /// turns the card into Custom anchored at that preset; on a bar position (or zero on a preset) nothing changes.
    /// </summary>
    public static void SetOffset(GlideSettingsState s, bool sideTabs, int? x, int? y)
    {
        var position = Position(s, sideTabs);
        var current = Offset(s, sideTabs);
        var nx = x ?? current.X; var ny = y ?? current.Y;
        if (position == Custom) SetCustom(s, sideTabs, PictureAnchor(s, sideTabs), nx, ny);
        else if (IsOverPicture(position) && (nx != 0 || ny != 0)) SetCustom(s, sideTabs, position, nx, ny);
    }

    /// <summary>
    /// Settings schema 23 (5.1.4): offsets used to be remembered per preset. A card whose preset had a non-zero offset becomes
    /// Custom at that same spot; every other per-preset offset is dropped (presets always reset).
    /// </summary>
    public static void MigratePerPresetOffsets(GlideSettingsState s)
    {
        var old = s.PictureCounterOffsets;
        var map = TextOverlayOffsets.OnlyCustom(old);
        foreach (var side in new[] { false, true })
        {
            var position = Position(s, side);
            if (position == Custom || !IsOverPicture(position)) continue;
            var (x, y) = TextOverlayOffsets.Get(old, position);
            if (x == 0 && y == 0) continue;
            map = TextOverlayOffsets.SetCustom(map, side, position, x, y);
            if (side) s.PictureCounterPositionSideTabs = Custom; else s.PictureCounterPositionTopTabs = Custom;
        }
        s.PictureCounterOffsets = map;
    }

    public static string Alignment(GlideSettingsState s, bool sideTabs) =>
        NormalizeAlignment(sideTabs ? s.PictureCounterAlignmentSideTabs : s.PictureCounterAlignmentTopTabs);

    /// <summary>
    /// Fills both cards from an older settings file. Schema ≤20: one picture position and alignment plus a separate
    /// "Text overlay with vertical tabs" placement (<paramref name="legacySideTabsPlacement"/>) that only applied with side
    /// tabs. Schema 21: one position (picture, title bar or vertical tabs) with an alignment per area.
    /// </summary>
    public static void MigrateLegacy(GlideSettingsState s, int sourceSchema, string? position, string? alignment,
        string? legacySideTabsPlacement, string? titleBarAlignment, string? tabBarAlignment)
    {
        var pos = Normalize(position);
        var align = NormalizeAlignment(alignment);
        if (sourceSchema <= 20)
        {
            // The old position was always a picture position; the side-tabs placement applied only with side tabs.
            pos = IsOverPicture(pos) ? pos : Default;
            s.PictureCounterPositionTopTabs = pos;
            s.PictureCounterAlignmentTopTabs = align;
            s.PictureCounterPositionSideTabs = legacySideTabsPlacement?.Trim().ToLowerInvariant() switch
            {
                "top of the tab bar" => TabBarTop,
                "bottom of the tab bar" => TabBarBottom,
                "title bar" => TitleBarCenter,
                _ => pos
            };
            s.PictureCounterAlignmentSideTabs = align;
            return;
        }

        // Schema 21: a title-bar position applied with both layouts; a vertical-tabs one only with side tabs (with
        // tabs along the top it fell back to Top right).
        if (IsTabBar(pos))
        {
            s.PictureCounterPositionSideTabs = pos;
            s.PictureCounterAlignmentSideTabs = NormalizeAlignment(tabBarAlignment ?? alignment);
            s.PictureCounterPositionTopTabs = Default;
            s.PictureCounterAlignmentTopTabs = align;
            return;
        }
        var areaAlign = IsTitleBar(pos) ? NormalizeAlignment(titleBarAlignment ?? alignment) : align;
        s.PictureCounterPositionTopTabs = pos;
        s.PictureCounterPositionSideTabs = pos;
        s.PictureCounterAlignmentTopTabs = areaAlign;
        s.PictureCounterAlignmentSideTabs = areaAlign;
    }

    private static string Key(string? value) =>
        string.Join(' ', (value ?? string.Empty).Replace('–', '-').Replace('-', ' ')
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)).ToLowerInvariant();
}
