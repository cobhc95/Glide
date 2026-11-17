namespace Glide.Core.Navigation;

/// <summary>Keys the "Ask me each time" picker understands, already translated from the platform key.</summary>
public enum EndOfFolderPickerKey
{
    Up,
    Down,
    Enter,
    Escape,
    /// <summary>The navigation key that reached the end, pressed again (picks the highlighted row).</summary>
    NavigateAgain,
    /// <summary>A number key; pass its value as the digit.</summary>
    Digit
}

/// <summary>One picker row: the action it picks and its label for the direction of travel.</summary>
public sealed record EndOfFolderPickerRow(EndOfFolderAction Action, string Label, string Icon, string KeyHint);

/// <summary>
/// Model of the in-viewer "Ask me each time" card (5.0.7): rows, highlight and key handling. Pure, so the key handling is
/// unit-tested; the window draws it and feeds keys / clicks. Reached by Previous at the first image the folder rows are
/// mirrored (the folder in the direction of travel comes first and is labelled "Previous folder").
/// </summary>
public sealed class EndOfFolderPicker
{
    public EndOfFolderPicker(int travelDirection)
    {
        TravelDirection = travelDirection >= 0 ? 1 : -1;
        var forward = TravelDirection > 0;
        Rows =
        [
            new(EndOfFolderAction.NextFolder, forward ? "Next folder" : "Previous folder", forward ? "FolderNext" : "FolderPrevious", "1"),
            new(EndOfFolderAction.NextFolderNewTab, forward ? "Next folder in new tab" : "Previous folder in new tab", "Tabs", "2"),
            new(EndOfFolderAction.PreviousFolder, forward ? "Previous folder" : "Next folder", forward ? "FolderPrevious" : "FolderNext", "3"),
            new(EndOfFolderAction.LoopFolder, "Loop this folder", "Loop", "4"),
            new(EndOfFolderAction.ShowHome, "Show Home", "Home", "5"),
            new(EndOfFolderAction.DoNothing, "Stay here", "Close", "Esc")
        ];
    }

    public int TravelDirection { get; }
    public IReadOnlyList<EndOfFolderPickerRow> Rows { get; }
    public int Highlighted { get; private set; }

    /// <summary>The pick once made (Stay = <see cref="EndOfFolderAction.DoNothing"/>); null while still open.</summary>
    public EndOfFolderAction? Result { get; private set; }

    /// <summary>Handles a key; returns true when the key belongs to the picker (it is then consumed).</summary>
    public bool HandleKey(EndOfFolderPickerKey key, int digit = 0)
    {
        if (Result is not null) return false;
        switch (key)
        {
            case EndOfFolderPickerKey.Up: Highlighted = (Highlighted + Rows.Count - 1) % Rows.Count; return true;
            case EndOfFolderPickerKey.Down: Highlighted = (Highlighted + 1) % Rows.Count; return true;
            case EndOfFolderPickerKey.Enter:
            case EndOfFolderPickerKey.NavigateAgain: Pick(Highlighted); return true;
            case EndOfFolderPickerKey.Escape: Result = EndOfFolderAction.DoNothing; return true;
            case EndOfFolderPickerKey.Digit when digit >= 1 && digit <= Rows.Count - 1: Pick(digit - 1); return true;
            default: return false;
        }
    }

    /// <summary>A click on a row.</summary>
    public void Pick(int row)
    {
        if (Result is not null || row < 0 || row >= Rows.Count) return;
        Highlighted = row;
        Result = Rows[row].Action;
    }

    /// <summary>A click outside the card: the same as Stay here.</summary>
    public void Dismiss() => Result ??= EndOfFolderAction.DoNothing;

    public void Highlight(int row)
    {
        if (row >= 0 && row < Rows.Count) Highlighted = row;
    }
}
