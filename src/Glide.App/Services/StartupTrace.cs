using System.Text;
using Glide.App.Settings;
using Glide.Core;

namespace Glide.App.Services;

/// <summary>
/// Always-on, tiny launch log (<c>startup-trace.log</c> next to glide.settings.json). Every Glide process writes
/// one line per launch decision: how it was started, whether it forwarded to a running Glide (and which kind of
/// request), won the single-instance election, showed its first window, began shutting down, and why it exited.
/// It exists because a launch that ends in a forward or an early exit is otherwise invisible: no window, no
/// crash log, no event-log entry (docs/audits/silent-exit-investigation.md). Lines carry the process id, so a
/// launcher that forwarded can be matched with the owner process that received (or failed to serve) its request.
///
/// Cost: one small append per decision (a handful per launch); the file is trimmed to the most recent lines by
/// the elected owner. Every write is best effort and never throws.
/// </summary>
internal static class StartupTrace
{
    internal const string FileName = "startup-trace.log";
    private const long TrimThresholdBytes = 96 * 1024;
    private const int KeepLines = 600;
    private static readonly object Gate = new();
    private static string? _pathOverrideForTests;
    private static string? _cachedPath;

    /// <summary>The last append failure (diagnostics/tests only).</summary>
    internal static string? LastWriteFailure { get; private set; }

    /// <summary>Test seam: redirect the log (null restores the settings-directory location).</summary>
    internal static string? PathOverrideForTests
    {
        get => _pathOverrideForTests;
        set { lock (Gate) { _pathOverrideForTests = value; _cachedPath = null; } }
    }

    internal static string LogPath
    {
        get
        {
            lock (Gate)
            {
                if (_pathOverrideForTests is { Length: > 0 } overridePath) return overridePath;
                return _cachedPath ??= Path.Combine(SettingsStore.GetSettingsDirectory(), FileName);
            }
        }
    }

    internal static void Write(string evt, string? detail = null)
    {
        try
        {
            var line = FormatLine(DateTime.UtcNow, Environment.ProcessId, AppVersion.DiagnosticTag, evt, detail);
            var bytes = Encoding.UTF8.GetBytes(line);
            var path = LogPath;
            // Several Glide processes (a launcher and the owner) can append at the same moment.
            // Contention is short (another process's single append, or an AV scan of the just-written file),
            // so a few short retries cover it; the worst case adds ~0.2 s to a process that is exiting.
            // FileMode.Append is seek-to-end + write, not an atomic append: two processes appending at
            // the same moment overwrite each other's line (seen in the launcher/owner forward handshake).
            // A per-file named mutex serializes the appends across Glide processes.
            using var mutex = CreateAppendMutex(path);
            var locked = false;
            try { locked = mutex?.WaitOne(TimeSpan.FromMilliseconds(250)) ?? false; }
            catch (AbandonedMutexException) { locked = true; }
            try
            {
                for (var attempt = 0; attempt < 20; attempt++)
                {
                    try
                    {
                        using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
                        stream.Write(bytes, 0, bytes.Length);
                        return;
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        LastWriteFailure = ex.GetType().Name + ": " + ex.Message;
                        Thread.Sleep(10);
                    }
                }
            }
            finally
            {
                if (locked) { try { mutex!.ReleaseMutex(); } catch { /* not owned */ } }
            }
        }
        catch { /* the launch log must never affect launching */ }
    }

    private static Mutex? CreateAppendMutex(string path)
    {
        try
        {
            var hash = System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(path).ToUpperInvariant()));
            return new Mutex(false, @"Local\Glide.StartupTrace." + Convert.ToHexString(hash, 0, 8));
        }
        catch { return null; }
    }

    internal static string FormatLine(DateTime utc, int pid, string version, string evt, string? detail)
    {
        var clean = string.IsNullOrEmpty(detail) ? string.Empty : " " + detail.Replace('\r', ' ').Replace('\n', ' ');
        return $"{utc:yyyy-MM-ddTHH:mm:ss.fffZ} pid={pid} v{version} {evt}{clean}{Environment.NewLine}";
    }

    /// <summary>Keeps the log bounded. Called once by the elected owner, off the launch hot path.</summary>
    internal static void TrimIfLarge()
    {
        try
        {
            var path = LogPath;
            var info = new FileInfo(path);
            if (!info.Exists || info.Length <= TrimThresholdBytes) return;
            using var mutex = CreateAppendMutex(path);
            var locked = false;
            try { locked = mutex?.WaitOne(TimeSpan.FromMilliseconds(500)) ?? false; }
            catch (AbandonedMutexException) { locked = true; }
            try { TrimLocked(path); }
            finally { if (locked) { try { mutex!.ReleaseMutex(); } catch { /* not owned */ } } }
        }
        catch { /* trimming is best effort; a concurrent writer simply wins */ }
    }

    private static void TrimLocked(string path)
    {
        try
        {
            string[] lines;
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var reader = new StreamReader(stream, Encoding.UTF8))
                lines = reader.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries);
            var kept = lines.Skip(Math.Max(0, lines.Length - KeepLines)).Select(l => l.TrimEnd('\r'));
            var temp = path + ".tmp";
            File.WriteAllText(temp, string.Join(Environment.NewLine, kept) + Environment.NewLine, new UTF8Encoding(false));
            File.Move(temp, path, overwrite: true);
        }
        catch { /* trimming is best effort; a concurrent writer simply wins */ }
    }
}
