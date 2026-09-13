using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace Glide.Core;

/// <summary>
/// Bounded, near-zero-I/O opt-in performance trace for cold-launch/decode benchmarking.
/// Disabled means a single volatile branch. Enabled events are timestamped into a fixed in-memory
/// buffer and written once after the UI lifetime exits, so measured first-frame latency is not
/// polluted by per-event filesystem writes.
/// </summary>
public static class GlidePerformanceTrace
{
    private readonly record struct TraceEvent(double ElapsedMs, int ProcessId, int ThreadId, string Name, string Detail);

    private const int MaxEvents = 512;
    private static long _processStartTimestamp;
    private static TraceEvent[]? _events;
    private static string? _path;
    private static int _enabled;
    private static int _nextIndex;

    /// <summary>Starts the opt-in benchmark clock without allocating the event buffer.</summary>
    public static void Touch()
    {
        if (Volatile.Read(ref _processStartTimestamp) == 0)
            Interlocked.CompareExchange(ref _processStartTimestamp, Stopwatch.GetTimestamp(), 0);
    }
    public static bool Enabled => Volatile.Read(ref _enabled) != 0;

    public static void Configure(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        Touch();
        _events ??= new TraceEvent[MaxEvents];
        _path = Path.GetFullPath(path);
        Volatile.Write(ref _enabled, 1);
        Mark("trace_enabled", Path.GetFileName(_path));
    }

    public static void Mark(string name, string? detail = null)
    {
        if (!Enabled) return;
        try
        {
            var index = Interlocked.Increment(ref _nextIndex) - 1;
            if ((uint)index >= MaxEvents) return;
            var events = _events;
            var start = Volatile.Read(ref _processStartTimestamp);
            if (events is null || start == 0) return;
            events[index] = new TraceEvent(
                Stopwatch.GetElapsedTime(start).TotalMilliseconds,
                Environment.ProcessId,
                Environment.CurrentManagedThreadId,
                Sanitize(name),
                Sanitize(detail ?? string.Empty));
        }
        catch
        {
            // Diagnostics must never affect the viewer.
        }
    }

    public static void Flush()
    {
        if (!Enabled) return;
        try
        {
            var path = _path;
            if (path is null) return;
            var folder = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(folder)) Directory.CreateDirectory(folder);
            var count = Math.Clamp(Volatile.Read(ref _nextIndex), 0, MaxEvents);
            using var writer = new StreamWriter(path, append: false, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            writer.WriteLine("elapsed_ms\tprocess_id\tthread_id\tevent\tdetail");
            for (var i = 0; i < count; i++)
            {
                var events = _events;
                if (events is null) return;
                var item = events[i];
                writer.Write(item.ElapsedMs.ToString("F3", CultureInfo.InvariantCulture)); writer.Write('\t');
                writer.Write(item.ProcessId.ToString(CultureInfo.InvariantCulture)); writer.Write('\t');
                writer.Write(item.ThreadId.ToString(CultureInfo.InvariantCulture)); writer.Write('\t');
                writer.Write(item.Name); writer.Write('\t'); writer.WriteLine(item.Detail);
            }
        }
        catch
        {
            // Benchmark tracing is never allowed to break application shutdown.
        }
    }

    private static string Sanitize(string value) => value.Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ');
}
