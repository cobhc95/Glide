namespace Glide.Core.Workspace;

/// <summary>Values of the "New tabs open at" setting (5.0.6).</summary>
public static class NewTabPositions
{
    public const string NextToCurrent = "Next to the current tab";
    public const string AtEnd = "At the end";
    public const string Default = NextToCurrent;

    /// <summary>In the Settings combo order.</summary>
    public static IReadOnlyList<string> All { get; } = [NextToCurrent, AtEnd];

    public static string Normalize(string? value) =>
        string.Equals(value, AtEnd, StringComparison.OrdinalIgnoreCase) ? AtEnd : NextToCurrent;
}

/// <summary>Where one "open in new tab" puts the tab and whether it becomes the active tab.</summary>
public readonly record struct NewTabOpening(bool AtEnd, bool Activate);

/// <summary>
/// Opening something "in a new tab" from inside Glide (Explorer middle-click, a double-click set to open new tabs,
/// the Explorer and overlay "Open in new tab" menu items). The two settings give the default. Only with "Shift and Alt
/// change how a new tab opens" on (off by default), keys held while opening flip them for that one tab:
///   Shift (alone or with Ctrl)  - the opposite of "Open new tabs in the foreground";
///   Alt                         - the other "New tabs open at" position;
///   Ctrl alone                  - nothing (it toggles the Explorer selection).
/// A custom mouse gesture bound to the same click and held key takes precedence over this scheme.
/// A plain new tab (Ctrl+T, the "+" button and its menu, a title-bar middle-click) uses only "New tabs open at":
/// see <see cref="ForNewTabCommand"/>.
/// </summary>
public static class NewTabPolicy
{
    /// <summary>Label of the opt-in setting that turns the Shift / Alt scheme on.</summary>
    public const string ModifiersLabel = "Shift and Alt change how a new tab opens";

    /// <summary>The scheme, shown on the opt-in setting (its tooltip and Settings search description).</summary>
    public const string ModifierHint = "When on, hold Shift while opening a picture or folder in a new tab to do the opposite of Open new tabs in the foreground; hold Alt to use the other position.";

    /// <summary>Appended to "New tabs open at" and "Open new tabs in the foreground" so search leads to the opt-in.</summary>
    public const string ModifierPointer = "With \"" + ModifiersLabel + "\" on, hold Shift or Alt while opening to flip this for one tab.";

    /// <summary>
    /// <paramref name="modifiersEnabled"/> is the "Shift and Alt change how a new tab opens" setting: when it is off the
    /// held keys are ignored entirely.
    /// </summary>
    public static NewTabOpening Resolve(string? position, bool switchToNewTabs, bool shift, bool alt, bool modifiersEnabled)
    {
        if (!modifiersEnabled) shift = alt = false;
        return new(AtEnd: (NewTabPositions.Normalize(position) == NewTabPositions.AtEnd) ^ alt, Activate: switchToNewTabs ^ shift);
    }

    /// <summary>
    /// A plain new tab (Ctrl+T, the "+" button and its menu, a title-bar middle-click): placed where "New tabs open at"
    /// says and always brought to the front. "Open new tabs in the foreground" and held keys do not
    /// apply (Ctrl+Shift+T stays Reopen closed tab).
    /// </summary>
    public static NewTabOpening ForNewTabCommand(string? position) =>
        new(AtEnd: NewTabPositions.Normalize(position) == NewTabPositions.AtEnd, Activate: true);
}
