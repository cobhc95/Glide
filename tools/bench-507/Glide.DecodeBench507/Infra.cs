using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
using System.Text;

namespace Glide.DecodeBench507;

internal static class Stats
{
    public static double Median(IReadOnlyList<double> v) => Percentile(v, 0.5, interpolate: true);

    /// <summary>Nearest-rank percentile (p95 of 7 samples = the maximum; reported as such).</summary>
    public static double Percentile(IReadOnlyList<double> values, double p, bool interpolate = false)
    {
        if (values.Count == 0) return double.NaN;
        var s = values.OrderBy(x => x).ToArray();
        if (interpolate && p == 0.5)
            return s.Length % 2 == 1 ? s[s.Length / 2] : (s[s.Length / 2 - 1] + s[s.Length / 2]) / 2;
        var i = (int)Math.Ceiling(p * s.Length) - 1;
        return s[Math.Clamp(i, 0, s.Length - 1)];
    }

    public static double StdDev(IReadOnlyList<double> v)
    {
        if (v.Count < 2) return 0;
        var m = v.Average();
        return Math.Sqrt(v.Sum(x => (x - m) * (x - m)) / (v.Count - 1));
    }

    public static string F(double v) => double.IsNaN(v) ? "" : v.ToString(v < 10 ? "0.00" : "0.0", CultureInfo.InvariantCulture);
}

/// <summary>Samples process private bytes (managed + native) on a 1 ms cadence; same method as tools\perf.</summary>
internal sealed class MemorySampler : IDisposable
{
    private readonly Thread _thread;
    private volatile bool _stop;
    private long _peak;
    public long Baseline { get; }

    public MemorySampler()
    {
        Baseline = PrivateBytes();
        _peak = Baseline;
        _thread = new Thread(() =>
        {
            while (!_stop)
            {
                var now = PrivateBytes();
                if (now > Interlocked.Read(ref _peak)) Interlocked.Exchange(ref _peak, now);
                Thread.Sleep(1);
            }
        }) { IsBackground = true, Priority = ThreadPriority.AboveNormal, Name = "mem-sampler" };
        _thread.Start();
    }

    public double PeakDeltaMb => (Interlocked.Read(ref _peak) - Baseline) / 1048576d;

    public void Dispose()
    {
        _stop = true;
        _thread.Join();
        var final = PrivateBytes();
        if (final > _peak) _peak = final;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Pmc
    {
        public uint cb, PageFaultCount;
        public nuint PeakWorkingSetSize, WorkingSetSize, QuotaPeakPagedPoolUsage, QuotaPagedPoolUsage,
            QuotaPeakNonPagedPoolUsage, QuotaNonPagedPoolUsage, PagefileUsage, PeakPagefileUsage, PrivateUsage;
    }

    [DllImport("psapi.dll")] private static extern bool GetProcessMemoryInfo(IntPtr p, out Pmc c, uint size);
    [DllImport("kernel32.dll")] private static extern IntPtr GetCurrentProcess();

    public static long PrivateBytes()
    {
        var c = new Pmc { cb = (uint)Marshal.SizeOf<Pmc>() };
        return GetProcessMemoryInfo(GetCurrentProcess(), out c, c.cb) ? (long)c.PrivateUsage : 0;
    }

    public static double PeakWorkingSetMb()
    {
        var c = new Pmc { cb = (uint)Marshal.SizeOf<Pmc>() };
        return GetProcessMemoryInfo(GetCurrentProcess(), out c, c.cb) ? c.PeakWorkingSetSize / 1048576d : double.NaN;
    }
}

/// <summary>
/// Waits until the rest of the machine is quiet before a measurement: system-wide busy CPU (all logical
/// processors, minus this process's own CPU) must stay below the threshold for one sampling window.
/// The user is working on this PC, so a busy machine is expected; the gate waits (up to a timeout) and
/// every sample records the busy percentage it started with so noisy samples can be filtered later.
/// </summary>
internal static class IdleGate
{
    [StructLayout(LayoutKind.Sequential)] private struct FileTime { public uint Low, High; public readonly ulong V => ((ulong)High << 32) | Low; }
    [DllImport("kernel32.dll")] private static extern bool GetSystemTimes(out FileTime idle, out FileTime kernel, out FileTime user);

    public static double SampleBusyPercent(int windowMs)
    {
        var self = Process.GetCurrentProcess();
        GetSystemTimes(out var i0, out var k0, out var u0);
        var s0 = self.TotalProcessorTime;
        Thread.Sleep(windowMs);
        GetSystemTimes(out var i1, out var k1, out var u1);
        self.Refresh();
        var s1 = self.TotalProcessorTime;
        double idle = i1.V - i0.V, total = (k1.V - k0.V) + (u1.V - u0.V); // kernel time includes idle time
        if (total <= 0) return 0;
        var busy = total - idle - (s1 - s0).Ticks; // 100 ns units on both sides
        return Math.Max(0, 100.0 * busy / total);
    }

    /// <summary>Process names that mean "someone is building or testing": the gate waits while any exists.</summary>
    public static string[] BlockingProcesses { get; set; } = Array.Empty<string>();

    private static string? BlockingProcess()
    {
        foreach (var name in BlockingProcesses)
        {
            var found = Process.GetProcessesByName(name);
            try { if (found.Length > 0) return name; }
            finally { foreach (var p in found) p.Dispose(); }
        }
        return null;
    }

    /// <summary>Returns (busy% of the last window, ms waited, timedOut).</summary>
    public static (double Busy, double WaitedMs, bool TimedOut) WaitForIdle(double thresholdPercent, int windowMs, int timeoutMs)
    {
        var sw = Stopwatch.StartNew();
        var announced = false;
        while (true)
        {
            var blocker = BlockingProcess();
            if (blocker is not null)
            {
                if (!announced) { Console.Error.Write($"(paused: {blocker} running) "); announced = true; }
                if (sw.ElapsedMilliseconds >= timeoutMs) return (100, sw.Elapsed.TotalMilliseconds, true);
                Thread.Sleep(5000);
                continue;
            }
            var busy = SampleBusyPercent(windowMs);
            if (busy <= thresholdPercent) return (busy, sw.Elapsed.TotalMilliseconds, false);
            if (sw.ElapsedMilliseconds >= timeoutMs) return (busy, sw.Elapsed.TotalMilliseconds, true);
            Thread.Sleep(Math.Min(2000, windowMs * 2));
        }
    }
}

internal static class ProcessTuning
{
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetProcessInformation(IntPtr process, int infoClass, ref PowerThrottlingState info, int size);
    [DllImport("kernel32.dll")] private static extern IntPtr GetCurrentProcess();

    [StructLayout(LayoutKind.Sequential)]
    private struct PowerThrottlingState { public uint Version, ControlMask, StateMask; }

    /// <summary>High priority, optional affinity, and opt out of EcoQoS/power throttling (process-local only).</summary>
    public static string Apply(string priority, string? affinityHex)
    {
        var p = Process.GetCurrentProcess();
        var notes = new List<string>();
        try
        {
            p.PriorityClass = priority.ToLowerInvariant() switch
            {
                "normal" => ProcessPriorityClass.Normal,
                "abovenormal" => ProcessPriorityClass.AboveNormal,
                "realtime" => ProcessPriorityClass.High, // never RealTime on a user's desktop
                _ => ProcessPriorityClass.High
            };
            notes.Add("priority=" + p.PriorityClass);
        }
        catch (Exception ex) { notes.Add("priority-failed:" + ex.GetType().Name); }
        if (!string.IsNullOrWhiteSpace(affinityHex))
        {
            try
            {
                p.ProcessorAffinity = (IntPtr)Convert.ToInt64(affinityHex.Replace("0x", "", StringComparison.OrdinalIgnoreCase), 16);
                notes.Add("affinity=0x" + ((long)p.ProcessorAffinity).ToString("X"));
            }
            catch (Exception ex) { notes.Add("affinity-failed:" + ex.GetType().Name); }
        }
        else notes.Add("affinity=all");
        try
        {
            // ProcessPowerThrottling = 4; PROCESS_POWER_THROTTLING_EXECUTION_SPEED = 1; StateMask 0 = never throttle.
            var state = new PowerThrottlingState { Version = 1, ControlMask = 1, StateMask = 0 };
            notes.Add(SetProcessInformation(GetCurrentProcess(), 4, ref state, Marshal.SizeOf<PowerThrottlingState>()) ? "ecoqos=off" : "ecoqos=unchanged");
        }
        catch { notes.Add("ecoqos=unchanged"); }
        Thread.CurrentThread.Priority = ThreadPriority.Highest;
        return string.Join(' ', notes);
    }
}

internal static class MachineInfo
{
    public static string Describe()
    {
        var sb = new StringBuilder();
        string cpu = "?";
        try { cpu = Microsoft.Win32.Registry.GetValue(@"HKEY_LOCAL_MACHINE\HARDWARE\DESCRIPTION\System\CentralProcessor\0", "ProcessorNameString", "?")?.ToString()?.Trim() ?? "?"; } catch { }
        sb.Append($"CPU: {cpu}; logical processors {Environment.ProcessorCount}; ");
        sb.Append($"RAM: {GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / 1073741824d:0.0} GB; ");
        sb.Append($"OS: {Environment.OSVersion.VersionString}; runtime {RuntimeInformation.FrameworkDescription}");
        return sb.ToString();
    }
}

/// <summary>A BGRA8 premultiplied surface in native memory (Glide's presentation format).</summary>
internal sealed unsafe class Surface : IDisposable
{
    public IntPtr Ptr { get; private set; }
    public int Width { get; }
    public int Height { get; }
    public int Stride { get; }

    public Surface(int width, int height)
    {
        Width = width; Height = height; Stride = width * 4;
        Ptr = (IntPtr)NativeMemory.AlignedAlloc((nuint)((long)Stride * height), 64);
        if (Ptr == IntPtr.Zero) throw new OutOfMemoryException();
    }

    /// <summary>Cheap correctness fingerprint: 4096 sampled pixels (also forces the pages to be touched).</summary>
    public ulong Checksum()
    {
        ulong h = 1469598103934665603;
        var p = (byte*)Ptr;
        for (var gy = 0; gy < 64; gy++)
        for (var gx = 0; gx < 64; gx++)
        {
            var x = (int)((long)gx * (Width - 1) / 63);
            var y = (int)((long)gy * (Height - 1) / 63);
            h = (h ^ *(uint*)(p + (long)y * Stride + x * 4L)) * 1099511628211;
        }
        return h;
    }

    public void Dispose()
    {
        if (Ptr != IntPtr.Zero) NativeMemory.AlignedFree((void*)Ptr);
        Ptr = IntPtr.Zero;
    }
}

/// <summary>Pixel conversions some engines need to reach BGRA premul. Timed separately (convert_ms).</summary>
internal static unsafe class Convert32
{
    /// <summary>RGB24 -> BGRA (opaque) with an SSSE3 shuffle, rows in parallel when large.</summary>
    public static void RgbToBgra(byte* src, int srcStride, Surface dst)
    {
        var rows = dst.Height;
        void Row(int y)
        {
            var s = src + (long)y * srcStride;
            var d = (byte*)dst.Ptr + (long)y * dst.Stride;
            var x = 0;
            if (Ssse3.IsSupported)
            {
                var mask = Vector128.Create((byte)2, 1, 0, 0x80, 5, 4, 3, 0x80, 8, 7, 6, 0x80, 11, 10, 9, 0x80);
                var alpha = Vector128.Create(0xFF000000u).AsByte();
                for (; x + 6 <= dst.Width; x += 4) // reads 16 bytes for 12 used: stop 2 pixels early
                {
                    var v = Sse2.LoadVector128(s + x * 3);
                    Sse2.Store(d + x * 4, Sse2.Or(Ssse3.Shuffle(v, mask), alpha));
                }
            }
            for (; x < dst.Width; x++)
            {
                d[x * 4] = s[x * 3 + 2]; d[x * 4 + 1] = s[x * 3 + 1]; d[x * 4 + 2] = s[x * 3]; d[x * 4 + 3] = 255;
            }
        }
        if ((long)dst.Width * rows >= 4_000_000) Parallel.For(0, rows, Row); else for (var y = 0; y < rows; y++) Row(y);
    }

    /// <summary>RGBA32 straight -> BGRA premultiplied.</summary>
    public static void RgbaToBgraPremul(byte* src, int srcStride, Surface dst)
    {
        void Row(int y)
        {
            var s = src + (long)y * srcStride;
            var d = (byte*)dst.Ptr + (long)y * dst.Stride;
            for (var x = 0; x < dst.Width; x++)
            {
                uint r = s[x * 4], g = s[x * 4 + 1], b = s[x * 4 + 2], a = s[x * 4 + 3];
                if (a != 255) { r = (r * a + 127) / 255; g = (g * a + 127) / 255; b = (b * a + 127) / 255; }
                d[x * 4] = (byte)b; d[x * 4 + 1] = (byte)g; d[x * 4 + 2] = (byte)r; d[x * 4 + 3] = (byte)a;
            }
        }
        if ((long)dst.Width * dst.Height >= 4_000_000) Parallel.For(0, dst.Height, Row); else for (var y = 0; y < dst.Height; y++) Row(y);
    }

    public static void GrayToBgra(byte* src, int srcStride, Surface dst)
    {
        for (var y = 0; y < dst.Height; y++)
        {
            var s = src + (long)y * srcStride;
            var d = (uint*)((byte*)dst.Ptr + (long)y * dst.Stride);
            for (var x = 0; x < dst.Width; x++) { uint g = s[x]; d[x] = 0xFF000000u | (g << 16) | (g << 8) | g; }
        }
    }
}

/// <summary>Box fit of a source into the viewport box (same rule Glide's bounded decode uses).</summary>
internal static class Fit
{
    public static (int W, int H) Box(int w, int h, int boxW, int boxH)
    {
        if (w <= boxW && h <= boxH) return (w, h);
        var r = Math.Min(boxW / (double)w, boxH / (double)h);
        return (Math.Max(1, (int)Math.Floor(w * r)), Math.Max(1, (int)Math.Floor(h * r)));
    }
}

internal static class Args
{
    public static string? Get(string[] a, string key)
    {
        for (var i = 0; i < a.Length - 1; i++) if (a[i].Equals(key, StringComparison.OrdinalIgnoreCase)) return a[i + 1];
        return null;
    }
    public static int Int(string[] a, string key, int fallback) => int.TryParse(Get(a, key), out var v) ? v : fallback;
    public static double Dbl(string[] a, string key, double fallback) => double.TryParse(Get(a, key), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : fallback;
    public static bool Has(string[] a, string key) => a.Any(x => x.Equals(key, StringComparison.OrdinalIgnoreCase));
    public static string[] List(string[] a, string key, string fallback) => (Get(a, key) ?? fallback).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}

internal static class Csv
{
    public static string Esc(object? v)
    {
        var s = v switch { null => "", double d => double.IsNaN(d) ? "" : d.ToString("0.###", CultureInfo.InvariantCulture), _ => Convert.ToString(v, CultureInfo.InvariantCulture) ?? "" };
        return s.IndexOfAny(new[] { ',', '"', '\n', '\r' }) >= 0 ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;
    }
    public static string Line(params object?[] values) => string.Join(',', values.Select(Esc));

    public static List<Dictionary<string, string>> Read(string path)
    {
        var lines = File.ReadAllLines(path).Where(l => l.Length > 0).ToArray();
        if (lines.Length == 0) return new();
        var header = Split(lines[0]).Select(h => h.Trim().ToLowerInvariant()).ToArray();
        return lines.Skip(1).Select(l =>
        {
            var cells = Split(l);
            var row = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < header.Length; i++) row[header[i]] = i < cells.Count ? cells[i] : "";
            return row;
        }).ToList();
    }

    private static List<string> Split(string line)
    {
        var cells = new List<string>(); var sb = new StringBuilder(); var q = false;
        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (q) { if (c == '"') { if (i + 1 < line.Length && line[i + 1] == '"') { sb.Append('"'); i++; } else q = false; } else sb.Append(c); }
            else if (c == '"') q = true;
            else if (c == ',') { cells.Add(sb.ToString()); sb.Clear(); }
            else sb.Append(c);
        }
        cells.Add(sb.ToString());
        return cells;
    }
}
