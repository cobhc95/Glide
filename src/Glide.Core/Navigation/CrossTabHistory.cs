namespace Glide.Core.Navigation;

/// <summary>
/// A Back / Forward step that leaves the tab: the image tab the user jumped from (Ctrl+E, Show in Explorer tab, Open
/// file location) or the Explorer tab Back returned from. <see cref="TabId"/> may be gone; <see cref="Path"/> (the image,
/// or the Explorer folder) and <see cref="SelectedPath"/> reopen it then.
/// </summary>
public sealed record CrossTabLink(Guid TabId, NavigationEntryKind Kind, string Path, string? SelectedPath = null);

/// <summary>The "Back after opening a picture's folder" setting (explorer.backReturnsToImage).</summary>
public static class ExplorerBackReturnModes
{
    public const string FoldersThenPicture = "Folders, then back to the picture";
    public const string StraightToPicture = "Straight back to the picture";
    public const string FoldersOnly = "Folders only";
    public const string Default = FoldersThenPicture;
    public static readonly string[] All = [FoldersThenPicture, StraightToPicture, FoldersOnly];

    public static string Normalize(string? value) =>
        All.FirstOrDefault(x => string.Equals(x, value?.Trim(), StringComparison.OrdinalIgnoreCase)) ?? Default;
}

/// <summary>The "Open a picture's folder in" setting (explorer.locateOpensIn): where Ctrl+E / Show in Explorer tab goes.</summary>
public static class ExplorerLocateTargets
{
    public const string SameTab = "Same tab";
    public const string ExistingOrNewTab = "Existing or new tab";
    public const string NewTab = "Always a new tab";
    public const string NewWindow = "New window";
    public const string Default = SameTab;
    public static readonly string[] All = [SameTab, ExistingOrNewTab, NewTab, NewWindow];

    public static string Normalize(string? value) =>
        All.FirstOrDefault(x => string.Equals(x, value?.Trim(), StringComparison.OrdinalIgnoreCase)) ?? Default;
}

/// <summary>What one Back / Forward does in a tab.</summary>
public enum CrossTabStepKind { None, InTab, ToOtherTab }

public readonly record struct CrossTabStep(CrossTabStepKind Kind, CrossTabLink? Link = null);

/// <summary>
/// Pure policy for chronological Back / Forward across a jump from an image tab to an Explorer tab. The Explorer entry
/// the user arrived at carries <see cref="NavigationEntry.ReturnTo"/> (the image); the image entry Back returned to
/// carries <see cref="NavigationEntry.ForwardTo"/> (the Explorer tab). Everything else is the tab's own history.
/// </summary>
public static class CrossTabHistory
{
    /// <summary>The image link Back can return to from this Explorer history, or null (mode and position decide).</summary>
    public static CrossTabLink? BackLink(NavigationHistory? history, string? mode)
    {
        if (history?.Current is null) return null;
        switch (ExplorerBackReturnModes.Normalize(mode))
        {
            case ExplorerBackReturnModes.FoldersOnly:
                return null;
            case ExplorerBackReturnModes.StraightToPicture:
                // The nearest arrival at or behind the current entry: folders browsed since are skipped.
                for (var i = history.Index; i >= 0; i--)
                    if (history.Entries[i].ReturnTo is { } link) return link;
                return null;
            default:
                return history.Current.ReturnTo;
        }
    }

    /// <summary>The index of the Explorer entry that carries <paramref name="link"/> (where Forward lands again).</summary>
    public static int ArrivalIndex(NavigationHistory history, CrossTabLink link)
    {
        for (var i = history.Index; i >= 0; i--)
            if (ReferenceEquals(history.Entries[i].ReturnTo, link) || history.Entries[i].ReturnTo == link) return i;
        return -1;
    }

    public static CrossTabLink? ForwardLink(NavigationHistory? history, string? mode) =>
        ExplorerBackReturnModes.Normalize(mode) == ExplorerBackReturnModes.FoldersOnly ? null : history?.Current?.ForwardTo;

    /// <summary>Back (direction &lt; 0) or Forward (&gt; 0): leave the tab, step inside it, or nothing.</summary>
    public static CrossTabStep Decide(NavigationHistory? history, int direction, string? mode)
    {
        if (history is null || direction == 0) return new(CrossTabStepKind.None);
        var link = direction < 0 ? BackLink(history, mode) : ForwardLink(history, mode);
        if (link is not null) return new(CrossTabStepKind.ToOtherTab, link);
        var inTab = direction < 0 ? history.CanGoBack : history.CanGoForward;
        return new(inTab ? CrossTabStepKind.InTab : CrossTabStepKind.None);
    }

    public static bool CanGo(NavigationHistory? history, int direction, string? mode) =>
        Decide(history, direction, mode).Kind != CrossTabStepKind.None;

    /// <summary>Marks the current Explorer entry as the arrival from <paramref name="image"/>.</summary>
    public static void MarkArrival(NavigationHistory history, CrossTabLink image)
    {
        if (history.Current is { Kind: NavigationEntryKind.Explorer }) history.UpdateCurrent(e => e with { ReturnTo = image });
    }

    /// <summary>Marks the current image entry so Forward returns to <paramref name="explorer"/>.</summary>
    public static void MarkReturned(NavigationHistory history, CrossTabLink explorer)
    {
        if (history.Current is { Kind: NavigationEntryKind.Image }) history.UpdateCurrent(e => e with { ForwardTo = explorer });
    }

    /// <summary>Tooltip text: "Back to 4K Wallpapers 1 (1).jpg" / "Forward to Wallpapers".</summary>
    public static string Describe(int direction, CrossTabLink link)
    {
        var name = System.IO.Path.GetFileName(System.IO.Path.TrimEndingDirectorySeparator(link.Path));
        if (string.IsNullOrEmpty(name)) name = link.Path;
        return (direction < 0 ? "Back to " : "Forward to ") + name;
    }
}
