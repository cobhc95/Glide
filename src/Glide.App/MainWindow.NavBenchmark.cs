using System.Diagnostics;
using System.Globalization;
using System.Text;
using Avalonia.Threading;
using Glide.Imaging;

namespace Glide.App;

/// <summary>
/// Benchmark-only scripted navigation (GLIDE_BENCH_NAV_OUT=&lt;tsv path&gt;). After the first frame and the
/// folder index are ready, drives the same entry points as the keyboard (tap, fast tap, held key, jump,
/// cold miss) inside the real app pipeline, records time-to-first-pixel (render fence) and refine
/// times per step plus decode/cache counters, CPU, memory and UI-thread lag per phase, writes a TSV
/// and closes the window. Inert unless the variable is set.
/// </summary>
public partial class MainWindow
{
    private static readonly string? NavBenchOut = Environment.GetEnvironmentVariable("GLIDE_BENCH_NAV_OUT");
    internal static bool NavBenchActive => NavBenchOut is not null;
    private readonly StringBuilder _navBenchRows = new();
    private string _navBenchPhase = "startup";
    private int _navBenchStep;
    private long _navBenchRequestTs;
    private readonly long _navBenchT0 = Stopwatch.GetTimestamp();
    private double _navBenchMaxLagMs;
    private bool _navBenchStarted;

    private double NavBenchNow() => Stopwatch.GetElapsedTime(_navBenchT0).TotalMilliseconds;

    private void NavBenchRow(string kind, string detail)
    {
        if (!NavBenchActive) return;
        var since = _navBenchRequestTs == 0 ? double.NaN : Stopwatch.GetElapsedTime(_navBenchRequestTs).TotalMilliseconds;
        lock (_navBenchRows)
            _navBenchRows.Append(CultureInfo.InvariantCulture, $"{NavBenchNow():F1}\t{_navBenchPhase}\t{_navBenchStep}\t{kind}\t{since:F1}\t{detail}\n");
    }

    /// <summary>Called by PresentCurrentAsync once the frame crossed the render fence.</summary>
    private void NavBenchPresented(string path, ImageLoadResult result, bool rapid, bool forceFull)
    {
        if (!NavBenchActive) return;
        NavBenchRow("present", $"idx={_navigator.Index};file={Path.GetFileName(path).GetHashCode() & 0xffff:x4};bmp={result.Bitmap.PixelSize.Width}x{result.Bitmap.PixelSize.Height};preview={result.IsPreview};prepared={result.PreparedFrameHit};route={result.DecodeRoute};rapid={rapid};forceFull={forceFull};loadMs={result.DecodeTime.TotalMilliseconds:F1}");
        if (!_navBenchStarted)
        {
            _navBenchStarted = true;
            _ = RunNavBenchmarkAsync();
        }
    }

    private void NavBenchRefined(string path, ImageLoadResult refined, string mode)
    {
        if (!NavBenchActive) return;
        NavBenchRow("refined", $"mode={mode};bmp={refined.Bitmap.PixelSize.Width}x{refined.Bitmap.PixelSize.Height};preview={refined.IsPreview};decodeMs={refined.DecodeTime.TotalMilliseconds:F1}");
    }

    private async Task RunNavBenchmarkAsync()
    {
        var lagCts = new CancellationTokenSource();
        _ = Task.Run(async () =>
        {
            // UI-thread lag probe: post a no-op at Input priority every 10 ms, track the worst delay.
            while (!lagCts.IsCancellationRequested)
            {
                var posted = Stopwatch.GetTimestamp();
                await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Input);
                var lag = Stopwatch.GetElapsedTime(posted).TotalMilliseconds;
                if (lag > _navBenchMaxLagMs) _navBenchMaxLagMs = lag;
                try { await Task.Delay(10, lagCts.Token); } catch { break; }
            }
        });
        try
        {
            for (var wait = 0; wait < 300 && !(_folderIndexReady && _folderIndexCanonicalReady && _navigator.Count > 2); wait++)
                await Task.Delay(100);
            NavBenchRow("index_ready", $"count={_navigator.Count};viewport={Viewport.Bounds.Width:F0}x{Viewport.Bounds.Height:F0}@{RenderScaling:F2}");
            var count = _navigator.Count;
            var span = Math.Min(count - 1, int.TryParse(Environment.GetEnvironmentVariable("GLIDE_BENCH_NAV_SPAN"), out var s) ? s : 9);
            await Phase("settle0", async () => await Task.Delay(3000));
            await Phase("tap_fwd", async () => { for (var i = 0; i < span; i++) await Tap(+1, 700); });
            await Phase("tap_back", async () => { for (var i = 0; i < span; i++) await Tap(-1, 700); });
            await Phase("fasttap_fwd", async () => { for (var i = 0; i < span; i++) await Tap(+1, 120); await Task.Delay(2000); });
            await Phase("fasttap_back", async () => { for (var i = 0; i < span; i++) await Tap(-1, 120); await Task.Delay(2000); });
            await Phase("hold_fwd", async () => await Hold(+1, span));
            await Phase("hold_back", async () => await Hold(-1, span));
            var rng = new Random(507);
            await Phase("jump", async () =>
            {
                for (var i = 0; i < 6; i++)
                {
                    var target = rng.Next(count);
                    if (target == _navigator.Index) target = (target + count / 2) % count;
                    _navBenchStep++;
                    _navBenchRequestTs = Stopwatch.GetTimestamp();
                    NavBenchRow("request", $"jump={target}");
                    await RunSerializedNavigationAsync(async () =>
                    {
                        _lastNavigationDirection = Math.Sign(target - _navigator.Index);
                        _navigator.SelectIndex(target);
                        await PresentCurrentAsync();
                    });
                    await Task.Delay(1000);
                }
            });
            await Phase("miss", async () =>
            {
                for (var i = 0; i < 6; i++)
                {
                    _loader.PurgeCaches();
                    await Task.Delay(200);
                    var dir = _navigator.Index < count - 1 ? +1 : -1;
                    _loader.PurgeCaches();
                    await Tap(dir, 1500);
                }
            });
            await Phase("idle_full", async () => await Tap(_navigator.Index > 0 ? -1 : +1, 4000));
        }
        catch (Exception ex) { NavBenchRow("error", ex.GetType().Name + ":" + ex.Message.Replace('\t', ' ').Replace('\n', ' ')); }
        finally
        {
            lagCts.Cancel();
            try
            {
                File.WriteAllText(NavBenchOut!, "t_ms\tphase\tstep\tkind\tsince_request_ms\tdetail\n" + _navBenchRows);
            }
            catch { /* benchmark output only */ }
            // Speed Boost may keep a closed window in standby: end the benchmark process outright.
            Environment.Exit(0);
        }
    }

    private async Task Phase(string name, Func<Task> body)
    {
        _navBenchPhase = name;
        _navBenchStep = 0;
        _navBenchMaxLagMs = 0;
        var proc = Process.GetCurrentProcess();
        var cpu0 = proc.TotalProcessorTime;
        var t0 = Stopwatch.GetTimestamp();
        var gc0 = GC.CollectionCount(2);
        long peakWs = 0;
        var memCts = new CancellationTokenSource();
        var memTask = Task.Run(async () =>
        {
            while (!memCts.IsCancellationRequested)
            {
                try { using var p = Process.GetCurrentProcess(); peakWs = Math.Max(peakWs, p.PrivateMemorySize64); } catch { }
                try { await Task.Delay(50, memCts.Token); } catch { break; }
            }
        });
        NavBenchRow("phase_start", ImageLoadCounters.Snapshot());
        await body();
        memCts.Cancel();
        await memTask;
        proc.Refresh();
        var wall = Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
        var cpu = (proc.TotalProcessorTime - cpu0).TotalMilliseconds;
        var snap = _loader.GetCacheSnapshot();
        NavBenchRow("phase_end", $"wallMs={wall:F0};cpuMs={cpu:F0};cpuCores={cpu / Math.Max(1, wall):F2};peakPrivateMB={peakWs / 1048576};privateMB={proc.PrivateMemorySize64 / 1048576};gc2={GC.CollectionCount(2) - gc0};maxUiLagMs={_navBenchMaxLagMs:F1};decodedItems={snap.DecodedItems};decodedMB={snap.DecodedBytes / 1048576};{ImageLoadCounters.Snapshot()}");
    }

    private async Task Tap(int delta, int afterMs)
    {
        _navBenchStep++;
        _navBenchRequestTs = Stopwatch.GetTimestamp();
        NavBenchRow("request", $"delta={delta}");
        await NavigateKeyboardAsync(delta);
        await Task.Delay(afterMs);
    }

    private async Task Hold(int direction, int steps)
    {
        // Mirrors RunHeldNavigationAsync: one normal step, the 180 ms hold threshold, then rapid steps
        // as fast as frames are presented (120 Hz cap), then the key-up settle.
        _navBenchStep++;
        _navBenchRequestTs = Stopwatch.GetTimestamp();
        var holdStart = _navBenchRequestTs;
        NavBenchRow("request", $"hold_first={direction}");
        await NavigateKeyboardAsync(direction);
        await Task.Delay(180);
        var minimumFrameTime = TimeSpan.FromSeconds(1.0 / 120.0);
        for (var i = 1; i < steps; i++)
        {
            var started = Stopwatch.GetTimestamp();
            _navBenchStep++;
            _navBenchRequestTs = started;
            NavBenchRow("request", $"hold_step={direction}");
            if (!await NavigateRapidStepAsync(direction, CancellationToken.None)) break;
            var remaining = minimumFrameTime - Stopwatch.GetElapsedTime(started);
            if (remaining > TimeSpan.Zero) await Task.Delay(remaining);
        }
        _navBenchStep++;
        _navBenchRequestTs = Stopwatch.GetTimestamp();
        NavBenchRow("request", $"hold_release;holdMs={Stopwatch.GetElapsedTime(holdStart).TotalMilliseconds:F1}");
        ScheduleRapidNavigationSettle();
        await Task.Delay(2500);
    }
}
