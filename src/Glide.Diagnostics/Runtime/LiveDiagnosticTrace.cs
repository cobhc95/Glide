using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace Glide.Diagnostics.Runtime;

/// <summary>
/// Always-on, crash-oriented breadcrumb trace. Unlike the in-memory diagnostic queue this is flushed
/// on every line so a native/renderer hard exit still leaves the events immediately preceding it.
/// The fixed Latest file lives in the user's Downloads folder even for installed builds.
/// </summary>
public static class LiveDiagnosticTrace
{
    private const long MaxBytes = 2 * 1024 * 1024;
    private static readonly object Gate = new();
    private static readonly Stopwatch Lifetime = Stopwatch.StartNew();
    private static StreamWriter? _writer;
    private static string? _path;
    private static string _version = "unknown";
    private static int _initialized;

    public static string? Path => Volatile.Read(ref _path);

    public static void Initialize(string version)
    {
        if (Interlocked.Exchange(ref _initialized, 1) != 0) return;
        _version = string.IsNullOrWhiteSpace(version) ? "unknown" : version;
        try
        {
            var downloads = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
            Directory.CreateDirectory(downloads);
            _path = System.IO.Path.Combine(downloads, "Glide-Diagnostic-Latest.txt");
            PreservePreviousAbnormalTrace(downloads);
            OpenWriter(overwrite: true);
            Write("process", "trace_started", new
            {
                version = _version,
                pid = Environment.ProcessId,
                process = Environment.ProcessPath,
                os = Environment.OSVersion.ToString(),
                framework = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
                commandLine = Environment.CommandLine
            });
        }
        catch
        {
            // Diagnostics must never prevent Glide from launching.
        }
    }

    public static void Write(string subsystem, string name, object? data = null)
    {
        try
        {
            lock (Gate)
            {
                if (_writer is null) return;
                if (_writer.BaseStream.Length >= MaxBytes)
                {
                    OpenWriter(overwrite: true);
                    WriteLineUnsafe("process", "trace_rotated", new { maxBytes = MaxBytes });
                }
                WriteLineUnsafe(subsystem, name, data);
            }
        }
        catch
        {
            // Never let logging affect the code path being diagnosed.
        }
    }

    public static void WriteException(string subsystem, string name, Exception ex, object? data = null)
    {
        Write(subsystem, name, new
        {
            context = data,
            exception = ex.GetType().FullName,
            ex.Message,
            ex.HResult,
            stack = ex.ToString()
        });
    }

    public static void MarkCleanExit(int exitCode = 0)
    {
        Write("process", "clean_exit", new { exitCode });
        lock (Gate)
        {
            try { _writer?.Flush(); } catch { }
        }
    }

    private static void PreservePreviousAbnormalTrace(string downloads)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(_path) || !File.Exists(_path)) return;
            // A normal shutdown appends clean_exit. If it is absent, keep the prior file before the
            // next owner process replaces Latest. This also protects evidence across Speed Boost or
            // an immediate user relaunch after a hard native/render crash.
            var tail = ReadTail(_path, 64 * 1024);
            if (tail.Contains("process.clean_exit", StringComparison.Ordinal)) return;
            var previous = System.IO.Path.Combine(downloads, "Glide-Diagnostic-Previous-AbnormalExit.txt");
            File.Copy(_path, previous, overwrite: true);
        }
        catch { }
    }

    private static string ReadTail(string path, int maxBytes)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var length = stream.Length;
            if (length > maxBytes) stream.Seek(-maxBytes, SeekOrigin.End);
            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            return reader.ReadToEnd();
        }
        catch { return string.Empty; }
    }

    private static void OpenWriter(bool overwrite)
    {
        if (string.IsNullOrWhiteSpace(_path)) return;
        try { _writer?.Dispose(); } catch { }
        var stream = new FileStream(_path, overwrite ? FileMode.Create : FileMode.Append,
            FileAccess.Write, FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.None);
        _writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true };
        _writer.WriteLine($"Glide live diagnostic trace | version {_version} | PID {Environment.ProcessId} | started {DateTimeOffset.Now:O}");
        _writer.WriteLine("If Glide disappears, drag this file back into ChatGPT. The final lines are the most important.");
    }

    private static void WriteLineUnsafe(string subsystem, string name, object? data)
    {
        var payload = data is null ? "" : " | " + SafeSerialize(data);
        _writer!.WriteLine($"{DateTimeOffset.Now:O} | +{Lifetime.Elapsed.TotalMilliseconds:F1}ms | T{Environment.CurrentManagedThreadId} | {subsystem}.{name}{payload}");
    }

    private static string SafeSerialize(object data)
    {
        try { return JsonSerializer.Serialize(data); }
        catch { return data.ToString() ?? "<unserializable>"; }
    }
}
