namespace Glide.Core.Workspace;

/// <summary>Values of the "New tabs open" setting (5.0.6; lower-case wording since round 3 #18/#19).</summary>
public static class NewTabPositions
{
    public const string AtEnd = "at the end";
    public const string NextToCurrent = "next to the current tab";
    public const string Default = NextToCurrent;

    /// <summary>In the Settings combo order.</summary>
    public static IReadOnlyList<string> All { get; } = [AtEnd, NextToCurrent];

    /// <summary>Case-insensitive, so files saved with the 5.0.6 wording ("At the end") keep their choice.</summary>
    public static string Normalize(string? value) =>
        string.Equals(value?.Trim(), AtEnd, StringComparison.OrdinalIgnoreCase) ? AtEnd : NextToCurrent;
}

/// <summary>Values of the "Open new tabs in the" setting (stored as the SwitchToNewTabs flag).</summary>
public static class NewTabSides
{
    public const string Foreground = "foreground";
    public const string Background = "background";

    /// <summary>In the Settings combo order.</summary>
    public static IReadOnlyList<string> All { get; } = [Foreground, Background];

    public static string FromSwitch(bool switchToNewTabs) => switchToNewTabs ? Foreground : Background;

    public static bool ToSwitch(string? value) => !string.Equals(value?.Trim(), Background, StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Values of "Duplicated tabs open [next to the current tab / at the end]" (round 4 D1; replaced the 5.1.0 middle-click
/// position setting, Explorer middle-click now follows "New tabs open").
/// </summary>
public static class DuplicateTabPositions
{
    public const string NextToCurrent = NewTabPositions.NextToCurrent;
    public const string AtEnd = NewTabPositions.AtEnd;
    public const string Default = NextToCurrent;

    /// <summary>In the Settings combo order.</summary>
    public static IReadOnlyList<string> All { get; } = [NextToCurrent, AtEnd];

    public static string Normalize(string? value) =>
        string.Equals(value?.Trim(), AtEnd, StringComparison.OrdinalIgnoreCase) ? AtEnd : NextToCurrent;
}

/// <summary>Values of "... in the [foreground / background]" for duplicated tabs (round 4 D1): foreground by default.</summary>
public static class DuplicateTabSides
{
    public const string Foreground = NewTabSides.Foreground;
    public const string Background = NewTabSides.Background;
    public const string Default = Foreground;

    /// <summary>In the Settings combo order.</summary>
    public static IReadOnlyList<string> All { get; } = [Foreground, Background];

    public static string Normalize(string? value) =>
        string.Equals(value?.Trim(), Background, StringComparison.OrdinalIgnoreCase) ? Background : Foreground;
}

/// <summary>The keys that can be held to open one new tab the other way ("Key to open … instead").</summary>
public static class NewTabKeys
{
    public const string None = "None";
    public const string Shift = "Shift";
    public const string Ctrl = "Ctrl";
    public const string Alt = "Alt";
    public const string Default = None;

    /// <summary>In the Settings combo order.</summary>
    public static IReadOnlyList<string> All { get; } = [None, Shift, Ctrl, Alt];

    public static string Normalize(string? value) =>
        All.FirstOrDefault(k => string.Equals(k, value?.Trim(), StringComparison.OrdinalIgnoreCase)) ?? None;

    /// <summary>True when <paramref name="key"/> is held (alone or with other keys). None is never held.</summary>
    public static bool IsHeld(string? key, bool shift, bool ctrl, bool alt) => Normalize(key) switch
    {
        Shift => shift,
        Ctrl => ctrl,
        Alt => alt,
        _ => false
    };
}

/// <summary>Where one "open in new tab" puts the tab and whether it becomes the active tab.</summary>
public readonly record struct NewTabOpening(bool AtEnd, bool Activate);

/// <summary>
/// Opening something "in a new tab" from inside Glide (Explorer middle-click (round 4 D1: no separate settings), a double-click set to open new tabs,
/// the Explorer and overlay "Open in new tab" menu items). "New tabs open [at the end / next to the current tab]" and
/// "Open new tabs in the [foreground / background]" give the default. Each has its own "Key to open … instead"
/// (None / Shift / Ctrl / Alt, None by default): holding that key while opening does the other thing for that one tab.
/// Before round 3 #18 one checkbox, "Shift and Alt change how a new tab opens", turned on a fixed scheme (Alt = the other
/// position, Shift = the other of foreground / background); SettingsStore migrates it to those two keys.
/// A custom mouse gesture bound to the same click and held key takes precedence over this scheme.
/// A plain new tab (Ctrl+T, the "+" button and its menu, a title-bar middle-click) uses only "New tabs open":
/// see <see cref="ForNewTabCommand"/>.
/// </summary>
public static class NewTabPolicy
{
    /// <summary>Settings label of the position key; it names the other position, so it flips with "New tabs open".</summary>
    public static string PositionKeyLabel(string? position) =>
        NewTabPositions.Normalize(position) == NewTabPositions.AtEnd ? "Key to open next to the current tab instead" : "Key to open at the end instead";

    /// <summary>Settings label of the foreground / background key; it flips with "Open new tabs in the".</summary>
    public static string ForegroundKeyLabel(bool switchToNewTabs) =>
        switchToNewTabs ? "Key to open in the background instead" : "Key to open in the foreground instead";

    /// <summary>Resolves one opening from the two settings, their keys and the keys held right now.</summary>
    public static NewTabOpening Resolve(string? position, bool switchToNewTabs, string? positionKey, string? foregroundKey,
        bool shift, bool ctrl, bool alt) =>
        new(AtEnd: (NewTabPositions.Normalize(position) == NewTabPositions.AtEnd) ^ NewTabKeys.IsHeld(positionKey, shift, ctrl, alt),
            Activate: switchToNewTabs ^ NewTabKeys.IsHeld(foregroundKey, shift, ctrl, alt));

    /// <summary>
    /// Round 4 D1: "Duplicate tab" places and activates the copy as "Duplicated tabs open [position] in the [side]" says
    /// (next to the current tab, foreground by default). Held keys do not apply.
    /// </summary>
    public static NewTabOpening ForDuplicate(string? position, string? side) =>
        new(AtEnd: DuplicateTabPositions.Normalize(position) == DuplicateTabPositions.AtEnd,
            Activate: DuplicateTabSides.Normalize(side) == DuplicateTabSides.Foreground);

    /// <summary>Round 4 D2: both keys set to the same key (not None) is allowed; holding it then inverts both.</summary>
    public static bool KeysShared(string? positionKey, string? foregroundKey) =>
        NewTabKeys.Normalize(positionKey) is var k && k != NewTabKeys.None && k == NewTabKeys.Normalize(foregroundKey);

    /// <summary>Round 4 D5: the hint shown after "Open in new tab" in context menus, e.g. "Shift: background"; empty with no keys.</summary>
    public static string MenuHint(string? position, bool switchToNewTabs, string? positionKey, string? foregroundKey)
    {
        var pk = NewTabKeys.Normalize(positionKey);
        var fk = NewTabKeys.Normalize(foregroundKey);
        var otherPosition = NewTabPositions.Normalize(position) == NewTabPositions.AtEnd ? "next to current" : "at end";
        var otherSide = switchToNewTabs ? "background" : "foreground";
        var parts = new List<string>();
        if (pk != NewTabKeys.None && pk == fk) parts.Add($"{pk}: {otherSide}, {otherPosition}");
        else
        {
            if (fk != NewTabKeys.None) parts.Add($"{fk}: {otherSide}");
            if (pk != NewTabKeys.None) parts.Add($"{pk}: {otherPosition}");
        }
        return string.Join("; ", parts);
    }

    /// <summary>
    /// A plain new tab (Ctrl+T, the "+" button and its menu, a title-bar middle-click): like a web browser, always at the
    /// far right end and brought to the front (5.1.0). "New tabs open", "Open new tabs in the" and held keys are for
    /// pictures/folders opened in new tabs and do not apply (Ctrl+Shift+T stays Reopen closed tab).
    /// </summary>
    public static NewTabOpening ForNewTabCommand() => new(AtEnd: true, Activate: true);

    /// <summary>
    /// Round 3 #18 migration of the retired "Shift and Alt change how a new tab opens" checkbox: on maps to the user-approved scheme
    /// (Shift = the other position, Alt = the other of foreground / background), off means no key.
    /// </summary>
    public static (string PositionKey, string ForegroundKey) MigrateLegacyModifiers(bool legacyModifiersOn) =>
        legacyModifiersOn ? (NewTabKeys.Shift, NewTabKeys.Alt) : (NewTabKeys.None, NewTabKeys.None);
}
