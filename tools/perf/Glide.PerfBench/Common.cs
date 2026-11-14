using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Glide.Imaging;

namespace Glide.PerfBench;

internal static class Stats
{
    public static double Median(IReadOnlyCollection<double> values)
    {
        if (values.Count == 0) return double.NaN;
        var sorted = values.OrderBy(v => v).ToArray();
        var mid = sorted.Length / 2;
        return sorted.Length % 2 == 1 ? sorted[mid] : (sorted[mid - 1] + sorted[mid]) / 2;
    }

    public static double Percentile(IReadOnlyCollection<double> values, double p)
    {
        if (values.Count == 0) return double.NaN;
        var sorted = values.OrderBy(v => v).ToArray();
        var index = (int)Math.Ceiling(p * sorted.Length) - 1;
        return sorted[Math.Clamp(index, 0, sorted.Length - 1)];
    }

    public static string Ms(double value) => double.IsNaN(value) ? "n/a" : value < 10 ? value.ToString("0.00") : value.ToString("0.0");
}

/// <summary>Samples process private bytes (managed + native, includes Skia/WIC buffers) on a 1 ms cadence.</summary>
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

    public long PeakBytes => Interlocked.Read(ref _peak);
    public double PeakDeltaMb => (PeakBytes - Baseline) / 1024d / 1024d;
    public double PeakMb => PeakBytes / 1024d / 1024d;

    public void Dispose()
    {
        _stop = true;
        _thread.Join();
        var final = PrivateBytes();
        if (final > _peak) _peak = final;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessMemoryCountersEx
    {
        public uint cb;
        public uint PageFaultCount;
        public nuint PeakWorkingSetSize, WorkingSetSize, QuotaPeakPagedPoolUsage, QuotaPagedPoolUsage,
            QuotaPeakNonPagedPoolUsage, QuotaNonPagedPoolUsage, PagefileUsage, PeakPagefileUsage, PrivateUsage;
    }

    [DllImport("psapi.dll", SetLastError = true)]
    private static extern bool GetProcessMemoryInfo(IntPtr process, out ProcessMemoryCountersEx counters, uint size);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    public static long PrivateBytes()
    {
        var counters = new ProcessMemoryCountersEx { cb = (uint)Marshal.SizeOf<ProcessMemoryCountersEx>() };
        return GetProcessMemoryInfo(GetCurrentProcess(), out counters, counters.cb) ? (long)counters.PrivateUsage : 0;
    }
}

internal static class BenchPolicies
{
    // Matches MainWindow.BuildPerformancePolicy with factory-default settings (Maximum quality profile,
    // RapidPreviewLongestSide 2400, PrefetchDepth 15, 20 items, 999 MB caches, FullNeighbourPredecodeCount 1)
    // for a maximised 2560x1440 window (viewport ~2560x1369 physical pixels at 100 % scaling).
    public const int ViewportWidth = 2560;
    public const int ViewportHeight = 1369;

    public static ImagePerformancePolicy AppDefault(bool prefetch = true)
    {
        var profile = ImagePerformancePolicy.ForProfile("Maximum quality");
        var bounds = ImagePerformanceGovernor.ChoosePreviewBounds(2400, ViewportWidth, ViewportHeight);
        return (profile with
        {
            DecoderScaledFirstFrame = true,
            PreviewLongestSide = 2400,
            PreviewMaxWidth = bounds.Width,
            PreviewMaxHeight = bounds.Height,
            ProgressiveColorFirstPreview = true,
            BackgroundRefinement = true,
            RefinementDelayMs = 5,
            SequentialForegroundReads = true,
            PredictivePrefetch = prefetch,
            PrefetchDepth = 15,
            FullNeighbourPredecodeCount = 1,
            CompressedCacheItems = 20,
            CompressedCacheMegabytes = 999,
            DecodedCacheMegabytes = 999
        }).Normalize();
    }

    /// <summary>MainWindow.BuildRapidBrowsePolicy with defaults (RapidBrowsePreviewLongestSide 3500).</summary>
    public static ImagePerformancePolicy AppRapid()
    {
        var normal = AppDefault();
        var rapidCap = 3500;
        var bounds = ImagePerformanceGovernor.ChoosePreviewBounds(rapidCap, ViewportWidth, ViewportHeight);
        return (normal with
        {
            DecoderScaledFirstFrame = true,
            PreviewLongestSide = rapidCap,
            PreviewMaxWidth = Math.Min(rapidCap, bounds.Width),
            PreviewMaxHeight = Math.Min(rapidCap, bounds.Height),
            PreviewInterpolation = Avalonia.Media.Imaging.BitmapInterpolationMode.HighQuality,
            BackgroundRefinement = false,
            PredictivePrefetch = true,
            PrefetchDepth = Math.Max(4, Math.Min(8, normal.PrefetchDepth)),
            FullNeighbourPredecodeCount = 0
        }).Normalize();
    }
}

internal sealed class Report
{
    private readonly JsonObject _root = new();
    private readonly StringBuilder _markdown = new();

    public Report(string mode)
    {
        _root["mode"] = mode;
        _root["machine"] = new JsonObject
        {
            ["logicalProcessors"] = Environment.ProcessorCount,
            ["os"] = Environment.OSVersion.VersionString,
            ["runtime"] = RuntimeInformation.FrameworkDescription,
            ["hardwareTier"] = ImagePerformanceGovernor.Current.Tier.ToString(),
            ["utc"] = DateTime.UtcNow.ToString("O")
        };
    }

    public JsonObject Root => _root;

    /// <summary>JSON has no NaN/Infinity: replace non-finite numbers with null before writing.</summary>
    private static void Sanitize(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var key in obj.Select(p => p.Key).ToArray())
                {
                    if (obj[key] is JsonValue v && v.TryGetValue<double>(out var d) && !double.IsFinite(d)) obj[key] = null;
                    else Sanitize(obj[key]);
                }
                break;
            case JsonArray arr:
                for (var i = 0; i < arr.Count; i++)
                {
                    if (arr[i] is JsonValue v && v.TryGetValue<double>(out var d) && !double.IsFinite(d)) arr[i] = null;
                    else Sanitize(arr[i]);
                }
                break;
        }
    }
    public StringBuilder Markdown => _markdown;

    public void Save(string outDir, string name)
    {
        Directory.CreateDirectory(outDir);
        File.WriteAllText(Path.Combine(outDir, name + ".md"), _markdown.ToString());
        Sanitize(_root);
        File.WriteAllText(Path.Combine(outDir, name + ".json"), _root.ToJsonString(new JsonSerializerOptions { WriteIndented = true, NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals, TypeInfoResolver = new System.Text.Json.Serialization.Metadata.DefaultJsonTypeInfoResolver() }));
        File.WriteAllText(Path.Combine(outDir, name + ".md"), _markdown.ToString());
        Console.WriteLine(_markdown.ToString());
    }
}

internal static class Args
{
    public static string? Get(string[] args, string key)
    {
        for (var i = 0; i < args.Length - 1; i++)
            if (args[i].Equals(key, StringComparison.OrdinalIgnoreCase)) return args[i + 1];
        return null;
    }

    public static int GetInt(string[] args, string key, int fallback) => int.TryParse(Get(args, key), out var v) ? v : fallback;
    public static bool Has(string[] args, string key) => args.Any(a => a.Equals(key, StringComparison.OrdinalIgnoreCase));
}

internal static class Timing
{
    public static double Ms(long startTimestamp) => Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds;
}

/// <summary>
/// Version-tolerant ownership helpers so one bench source runs against the baseline build (no presentation
/// leases: "dispose unless cached") and the optimised build (ImageLoadCoordinator.ReleaseBitmap).
/// </summary>
internal static class Ownership
{
    private static readonly System.Reflection.MethodInfo? ReleaseMethod =
        typeof(ImageLoadCoordinator).GetMethod("ReleaseBitmap", new[] { typeof(Avalonia.Media.Imaging.Bitmap) });
    private static readonly System.Reflection.MethodInfo? LeaseMethod =
        typeof(ImageLoadCoordinator).GetMethod("GetLeaseCount", new[] { typeof(Avalonia.Media.Imaging.Bitmap) });

    public static bool HasLeases => ReleaseMethod is not null;

    public static void Release(ImageLoadCoordinator loader, Avalonia.Media.Imaging.Bitmap? bitmap)
    {
        if (bitmap is null) return;
        if (ReleaseMethod is not null) { ReleaseMethod.Invoke(loader, new object[] { bitmap }); return; }
        if (!loader.IsBitmapCached(bitmap)) bitmap.Dispose();
    }

    public static int Leases(ImageLoadCoordinator loader, Avalonia.Media.Imaging.Bitmap? bitmap) =>
        bitmap is null || LeaseMethod is null ? 0 : (int)LeaseMethod.Invoke(loader, new object[] { bitmap })!;
}