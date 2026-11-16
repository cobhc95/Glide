namespace Glide.App.Explorer;

/// <summary>
/// Replaceable edges of the Explorer tab: everything that would otherwise show a dialog, start a
/// process or reach the real clipboard/Recycle Bin. Production wires the defaults in MainWindow;
/// tests replace them so nothing real is touched.
/// </summary>
internal static class ExplorerHost
{
    /// <summary>Asks the user to confirm a (destructive) action. (title, message, acceptText) => accepted.</summary>
    internal static Func<string, string, string, Task<bool>>? Confirm { get; set; }

    /// <summary>Shows an error message to the user. (title, message).</summary>
    internal static Func<string, string, Task>? ShowError { get; set; }

    /// <summary>Show a path in Windows File Explorer. (path, isDirectory).</summary>
    internal static Action<string, bool>? RevealInWindows { get; set; }

    /// <summary>Starts the alternative file manager. (exe, arguments) => started.</summary>
    internal static Func<string, string, bool>? LaunchAlternativeFileManager { get; set; }

    /// <summary>The "Open with..." chooser for a file.</summary>
    internal static Action<string>? OpenWith { get; set; }

    /// <summary>Opens a non-image file with its associated Windows program.</summary>
    internal static Action<string>? OpenWithShell { get; set; }

    /// <summary>The text most recently placed on the clipboard by "Copy path" (lets tests observe it without the OS clipboard).</summary>
    internal static string? LastCopiedText { get; set; }

    /// <summary>The key a drag is tagged with so Glide can tell its own Explorer drags from foreign ones.</summary>
    internal const string DragFormatName = "glide-explorer-paths";
}
