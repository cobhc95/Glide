using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace Glide.App.Diagnostics;

/// <summary>
/// Opt-in, bounded, allocation-free-on-hot-path trace for overlay/navigation stalls.
/// Events share Stopwatch's monotonic timebase. No per-frame formatting or I/O occurs.
/// </summary>
internal static class OverlayNavigationTrace
{
    internal enum Kind
    {
        BackendStarted, BackendUnavailable, LoopTick, GateWait, GateHeld,
        SyncWindows, EnsureSurface, SurfaceRecreated, UpdateLayeredWindow, SetWindowPos,
        MotionSample, MotionState, NavigationRequest, ImagePresented,
        RefinementStart, RefinementEnd, PrefetchStart, PrefetchEnd,
        MetadataRequested, MetadataBypassed, MetadataProviderStart, MetadataProviderEnd,
        MetadataFallbackStart, MetadataFallbackEnd, MetadataFramesStart, MetadataFramesEnd
    }

    private struct Event
    {
        public long Ticks;
        public int ThreadId;
        public Kind Kind;
        public Guid Id;
        public long A;
        public long B;
        public double X;
        public double Y;
        public double U;
        public double V;
        public string? Detail;
    }

    private const int Capacity = 65536;
    private static readonly Event[] Buffer = new Event[Capacity];
    private static readonly long Origin = Stopwatch.GetTimestamp();
    private static readonly bool IsEnabled = string.Equals(Environment.GetEnvironmentVariable("GLIDE_OVERLAY_DIAG"), "1", StringComparison.OrdinalIgnoreCase);
    private static int _next;

    static OverlayNavigationTrace()
    {
        if (IsEnabled) AppDomain.CurrentDomain.ProcessExit += (_, _) => Export();
    }

    internal static bool Enabled => IsEnabled;
    internal static long Now() => Stopwatch.GetTimestamp();

    internal static void Mark(Kind kind, Guid id = default, long a = 0, long b = 0,
        double x = 0, double y = 0, double u = 0, double v = 0, string? detail = null)
    {
        if (!IsEnabled) return;
        var index = Interlocked.Increment(ref _next) - 1;
        if ((uint)index >= Capacity) return;
        Buffer[index] = new Event
        {
            Ticks = Stopwatch.GetTimestamp(), ThreadId = Environment.CurrentManagedThreadId, Kind = kind,
            Id = id, A = a, B = b, X = x, Y = y, U = u, V = v, Detail = detail
        };
    }

    internal static void Duration(Kind kind, long startTicks, Guid id = default, long a = 0, long b = 0,
        double x = 0, double y = 0, double u = 0, double v = 0)
    {
        if (!IsEnabled) return;
        Mark(kind, id, a: Stopwatch.GetTimestamp() - startTicks, b: b, x: x, y: y, u: u, v: v);
    }

    internal static void Export()
    {
        if (!IsEnabled) return;
        try
        {
            var configured = Environment.GetEnvironmentVariable("GLIDE_OVERLAY_DIAG_PATH");
            var path = string.IsNullOrWhiteSpace(configured)
                ? Path.Combine(AppContext.BaseDirectory, "overlay-navigation-diagnostic.tsv")
                : Path.GetFullPath(configured);
            var folder = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(folder)) Directory.CreateDirectory(folder);
            var count = Math.Min(Math.Max(Volatile.Read(ref _next), 0), Capacity);
            using var writer = new StreamWriter(path, false, new UTF8Encoding(false));
            writer.WriteLine("ms\tthread\tevent\tid\ta\tb\tx\ty\tu\tv\tdetail");
            for (var i = 0; i < count; i++)
            {
                var e = Buffer[i];
                var ms = (e.Ticks - Origin) * 1000.0 / Stopwatch.Frequency;
                writer.Write(ms.ToString("F3", CultureInfo.InvariantCulture)); writer.Write('\t');
                writer.Write(e.ThreadId); writer.Write('\t'); writer.Write(e.Kind); writer.Write('\t');
                writer.Write(e.Id == Guid.Empty ? "" : e.Id.ToString("N")); writer.Write('\t');
                writer.Write(e.A); writer.Write('\t'); writer.Write(e.B); writer.Write('\t');
                writer.Write(e.X.ToString("F4", CultureInfo.InvariantCulture)); writer.Write('\t');
                writer.Write(e.Y.ToString("F4", CultureInfo.InvariantCulture)); writer.Write('\t');
                writer.Write(e.U.ToString("F4", CultureInfo.InvariantCulture)); writer.Write('\t');
                writer.Write(e.V.ToString("F4", CultureInfo.InvariantCulture)); writer.Write('\t');
                writer.WriteLine((e.Detail ?? "").Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' '));
            }
        }
        catch { }
    }
}
