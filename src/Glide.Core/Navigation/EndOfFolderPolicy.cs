namespace Glide.Core.Navigation;

/// <summary>
/// "When the last image is reached" (navigation.endOfFolder, 5.0.7): what Next does at the last image of a folder and,
/// mirrored, what Previous does at the first image. Directions are relative to the direction of travel: "next folder"
/// is the folder in the direction the user is moving, "previous folder" the one behind.
/// </summary>
public enum EndOfFolderAction
{
    DoNothing,
    LoopFolder,
    NextFolder,
    NextFolderNewTab,
    PreviousFolder,
    ShowHome,
    Ask
}

/// <summary>Stored names of <see cref="EndOfFolderAction"/> (the Settings combo text, in combo order) and conversions.</summary>
public static class EndOfFolderActions
{
    public const string DoNothing = "Do nothing";
    public const string LoopFolder = "Loop this folder";
    public const string NextFolder = "Move to the next folder";
    public const string NextFolderNewTab = "Move to the next folder in a new tab";
    public const string PreviousFolder = "Go back to the previous folder";
    public const string ShowHome = "Show Home";
    public const string Ask = "Ask me each time";

    /// <summary>The default (and the behaviour of the old "Automatically continue into sibling folders" checkbox when on).</summary>
    public const string Default = Ask;

    /// <summary>Every option in Settings combo order.</summary>
    public static IReadOnlyList<string> All { get; } = [DoNothing, LoopFolder, NextFolder, NextFolderNewTab, PreviousFolder, ShowHome, Ask];

    /// <summary>A stored name as the action; unknown / empty = the default (next folder).</summary>
    public static EndOfFolderAction Parse(string? name)
    {
        var trimmed = name?.Trim();
        for (var i = 0; i < All.Count; i++)
            if (string.Equals(All[i], trimmed, StringComparison.OrdinalIgnoreCase)) return (EndOfFolderAction)i;
        return EndOfFolderAction.Ask;
    }

    public static string NameOf(EndOfFolderAction action) => All[(int)action];

    /// <summary>A stored value as its canonical name; anything unknown becomes the default.</summary>
    public static string Normalize(string? name) => NameOf(Parse(name));

    /// <summary>Settings schema 15 migration of the old checkbox: on = next folder, off = do nothing.</summary>
    public static string FromLegacyContinueSiblingFolders(bool continueSiblingFolders) =>
        continueSiblingFolders ? NextFolder : DoNothing;

    /// <summary>Whether the folder options (branches, first image, wrap, skip empty, hidden) apply to this choice.</summary>
    public static bool UsesFolderOptions(EndOfFolderAction action) =>
        action is EndOfFolderAction.NextFolder or EndOfFolderAction.NextFolderNewTab or EndOfFolderAction.PreviousFolder or EndOfFolderAction.Ask;
}

public enum EndOfFolderStepKind
{
    /// <summary>Stay on the image and show the brief "End of folder" notice.</summary>
    Stay,
    /// <summary>Jump to the other end of the same folder.</summary>
    Loop,
    /// <summary>Open the sibling folder <see cref="EndOfFolderStep.FolderDirection"/> in this tab.</summary>
    Folder,
    /// <summary>Open the sibling folder <see cref="EndOfFolderStep.FolderDirection"/> in a new foreground tab.</summary>
    FolderInNewTab,
    Home,
    /// <summary>Show the "Ask me each time" picker.</summary>
    Ask
}

/// <param name="FolderDirection">+1 / -1 sibling direction for the folder steps, otherwise 0.</param>
public readonly record struct EndOfFolderStep(EndOfFolderStepKind Kind, int FolderDirection = 0)
{
    public static EndOfFolderStep Stay => new(EndOfFolderStepKind.Stay);
}

/// <summary>
/// The one decision of what happens when Next passes the last image (or Previous the first). Pure, so it is unit-tested
/// without a window; the main window only executes the returned step.
/// </summary>
public static class EndOfFolderPolicy
{
    /// <summary>
    /// The action in effect: a per-tab runtime "loop this folder" override wins over the setting. (The override itself
    /// lives with the tab; this is the single place that combines the two.)
    /// </summary>
    public static EndOfFolderAction Effective(EndOfFolderAction setting, bool loopOverride) =>
        loopOverride ? EndOfFolderAction.LoopFolder : setting;

    /// <param name="travelDirection">+1 = Next at the last image, -1 = Previous at the first image.</param>
    public static EndOfFolderStep Decide(EndOfFolderAction action, int travelDirection)
    {
        var dir = Math.Sign(travelDirection);
        if (dir == 0) return EndOfFolderStep.Stay;
        return action switch
        {
            EndOfFolderAction.LoopFolder => new(EndOfFolderStepKind.Loop),
            EndOfFolderAction.NextFolder => new(EndOfFolderStepKind.Folder, dir),
            EndOfFolderAction.NextFolderNewTab => new(EndOfFolderStepKind.FolderInNewTab, dir),
            EndOfFolderAction.PreviousFolder => new(EndOfFolderStepKind.Folder, -dir),
            EndOfFolderAction.ShowHome => new(EndOfFolderStepKind.Home),
            EndOfFolderAction.Ask => new(EndOfFolderStepKind.Ask),
            _ => EndOfFolderStep.Stay
        };
    }

    /// <summary>The image index a loop lands on: the first image going forward, the last going backward.</summary>
    public static int LoopTargetIndex(int travelDirection, int count) =>
        count <= 0 ? -1 : travelDirection > 0 ? 0 : count - 1;

    /// <summary>Whether held-key playback may carry on through this step (it never opens a tab, Home or the picker).</summary>
    public static bool ContinuesHeldNavigation(EndOfFolderStep step) =>
        step.Kind is EndOfFolderStepKind.Folder or EndOfFolderStepKind.Loop;
}
