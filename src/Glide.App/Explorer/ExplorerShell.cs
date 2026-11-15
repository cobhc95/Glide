using System.Diagnostics;

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
}
