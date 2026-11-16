using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Glide.App.Explorer;

/// <summary>
/// The Explorer tab's two Windows-shell launches: open a file with its associated program and show a
/// path in Windows File Explorer. Each honours its <see cref="ExplorerHost"/> test seam first, so tests
/// never start a real process. Moved out of <c>MainWindow.Browser.cs</c> (window code no longer starts
/// processes itself); behaviour unchanged.
/// </summary>
internal static class ExplorerShell
{
    // Shell-launch hardening: reject embedded quotes/CR/LF so a crafted file name cannot break
    // out of quoting into extra explorer switches (explorer.exe needs /select,"path" quoted by hand, see
    // RevealArguments, so the path itself must be quote-free). Callers keep their existing
    // File.Exists/IsSupported gates; this only filters unsafe strings.
    private static bool IsSafeShellPath(string path) =>
        !string.IsNullOrWhiteSpace(path) && path.IndexOfAny(new[] { '"', '\r', '\n' }) < 0;

    /// <summary>Opens a file or folder with its associated Windows program.</summary>
    public static void Open(string path)
    {
        if (ExplorerHost.OpenWithShell is { } seam) { seam(path); return; }
        if (!OperatingSystem.IsWindows() || (!File.Exists(path) && !Directory.Exists(path)) || !IsSafeShellPath(path)) return;
        try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); } catch (Exception loggedEx) { Glide.Diagnostics.Runtime.LiveDiagnosticTrace.WriteException("explorer", "open_with_shell_failed", loggedEx); }
    }

    /// <summary>Shows a folder (<paramref name="directory"/>) or selects a file in Windows File Explorer.</summary>
    public static void Reveal(string path, bool directory)
    {
        if (ExplorerHost.RevealInWindows is { } seam) { seam(path, directory); return; }
        if (!OperatingSystem.IsWindows() || !IsSafeShellPath(path)) return;
        try
        {
            var psi = new ProcessStartInfo("explorer.exe") { UseShellExecute = true };
            psi.Arguments = RevealArguments(path, directory);
            Process.Start(psi);
        }
        catch (Exception loggedEx) { Glide.Diagnostics.Runtime.LiveDiagnosticTrace.WriteException("explorer", "reveal_in_explorer_failed", loggedEx); }
    }

    /// <summary>
    /// explorer.exe parses its own command line: "/select," must be followed by the quoted path
    /// (/select,"D:\a b\x.jpg"). ArgumentList would quote the WHOLE token ("/select,D:\a b\x.jpg"),
    /// which Explorer cannot parse, so it falls back to opening Documents. The caller has already
    /// rejected quotes and line breaks (<see cref="IsSafeShellPath"/>), so this cannot be broken out of.
    /// </summary>
    internal static string RevealArguments(string path, bool directory) =>
        directory ? "\"" + path + "\"" : "/select,\"" + path + "\"";

    /// <summary>
    /// Opens <paramref name="folder"/> in Windows File Explorer with <paramref name="selectFile"/> (a file or
    /// subfolder inside it) already selected, via SHOpenFolderAndSelectItems (reuses an open Explorer window for
    /// that folder). With no item it just opens the folder. Falls back to explorer.exe /select if the shell call fails.
    /// </summary>
    public static void RevealSelected(string folder, string? selectFile)
    {
        var target = string.IsNullOrWhiteSpace(selectFile) ? null : selectFile;
        if (ExplorerHost.RevealInWindows is { } seam) { seam(target ?? folder, target is null); return; }
        if (!OperatingSystem.IsWindows()) return;
        if (target is not null && TryShellSelect(folder, target)) return;
        Reveal(target ?? folder, directory: target is null);
    }

    private static bool TryShellSelect(string folder, string item)
    {
        IntPtr folderPidl = IntPtr.Zero, itemPidl = IntPtr.Zero;
        try
        {
            if (SHParseDisplayName(folder, IntPtr.Zero, out folderPidl, 0, out _) != 0 || folderPidl == IntPtr.Zero) return false;
            if (SHParseDisplayName(item, IntPtr.Zero, out itemPidl, 0, out _) != 0 || itemPidl == IntPtr.Zero) return false;
            var items = new[] { itemPidl };
            return SHOpenFolderAndSelectItems(folderPidl, 1, items, 0) == 0;
        }
        catch (Exception loggedEx)
        {
            Glide.Diagnostics.Runtime.LiveDiagnosticTrace.WriteException("explorer", "shell_select_failed", loggedEx);
            return false;
        }
        finally
        {
            if (itemPidl != IntPtr.Zero) ILFree(itemPidl);
            if (folderPidl != IntPtr.Zero) ILFree(folderPidl);
        }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHParseDisplayName(string name, IntPtr bindingContext, out IntPtr pidl, uint sfgaoIn, out uint sfgaoOut);
    [DllImport("shell32.dll")]
    private static extern int SHOpenFolderAndSelectItems(IntPtr pidlFolder, uint count, IntPtr[] items, uint flags);
    [DllImport("shell32.dll")]
    private static extern void ILFree(IntPtr pidl);

    /// <summary>
    /// The alternative file manager's command line: {path} (any case) replaced by <paramref name="path"/>.
    /// An empty template means "{path}" quoted; a template without {path} gets the quoted path appended.
    /// </summary>
    internal static string AlternativeArguments(string? template, string path)
    {
        if (string.IsNullOrWhiteSpace(template)) return "\"" + path + "\"";
        return template.Contains("{path}", StringComparison.OrdinalIgnoreCase)
            ? template.Replace("{path}", path, StringComparison.OrdinalIgnoreCase)
            : template.TrimEnd() + " \"" + path + "\"";
    }

    /// <summary>Starts the alternative file manager on <paramref name="path"/>; false when none is set or it cannot start.</summary>
    public static bool OpenInAlternative(string? exe, string? template, string path)
    {
        if (string.IsNullOrWhiteSpace(exe) || !IsSafeShellPath(path)) return false;
        exe = exe.Trim().Trim('"');
        var arguments = AlternativeArguments(template, path);
        if (ExplorerHost.LaunchAlternativeFileManager is { } seam) return seam(exe, arguments);
        try
        {
            Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true, Arguments = arguments });
            return true;
        }
        catch (Exception loggedEx)
        {
            Glide.Diagnostics.Runtime.LiveDiagnosticTrace.WriteException("explorer", "alternative_file_manager_failed", loggedEx);
            return false;
        }
    }
}
