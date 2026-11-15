using Avalonia;
using Glide.Core;
using Glide.Diagnostics.Runtime;
using Glide.App.Services;
using Glide.App.Settings;

namespace Glide.App;

internal static partial class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        // Security (DLL planting): a file-association or command-line launch can run Glide with the opened
        // image's folder (often Downloads) as its current directory. Remove the current directory from
        // the DLL search order so a
        // planted DLL there can never be loaded. Glide's own and Avalonia's natives load from the app folder.
        if (OperatingSystem.IsWindows())
        {
            try { SetDllDirectoryW(string.Empty); } catch { /* DLL search hardening is best effort */ }
        }

        // Pre-warm thread pool to eliminate cold-launch worker thread dispatch delays
        ThreadPool.SetMinThreads(Math.Max(8, Environment.ProcessorCount), 8);

        // Installer, build and diagnostics command-line modes run before Avalonia and exit.
        if (CommandLineTools.TryRun(args, out var toolExitCode)) return toolExitCode;

        // Crash breadcrumbs are opt-in only: a consumer install must never drop diagnostic text into
        // the user's Downloads folder. Crash investigations and benchmarks enable the trace with
        // --diagnostic-trace or GLIDE_DIAGNOSTIC_TRACE=1.
        var diagnosticTraceRequested =
            string.Equals(Environment.GetEnvironmentVariable("GLIDE_DIAGNOSTIC_TRACE"), "1", StringComparison.Ordinal);

        // Optional benchmark trace. Strip our private switch before passing arguments to Avalonia.
        // Example: Glide.exe --perf-trace "C:\temp\glide-start.tsv" image.jpg
        var appArgs = new List<string>(args.Length);
        var forceNewInstance = false;
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i].Equals("--diagnostic-trace", StringComparison.OrdinalIgnoreCase))
            {
                diagnosticTraceRequested = true;
                continue;
            }
            if (args[i].Equals("--perf-trace", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                GlidePerformanceTrace.Configure(args[++i]);
                continue;
            }
            if (args[i].Equals("--benchmark-exit-after-first-frame", StringComparison.OrdinalIgnoreCase))
            {
                App.BenchmarkExitAfterFirstFrame = true;
                continue;
            }
            if (args[i].Equals("--notify-first-frame", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                App.NotifyFirstFrameEventName = args[++i];
                continue;
            }
            if (args[i].Equals("--background", StringComparison.OrdinalIgnoreCase))
            {
                App.StartHidden = true;
                continue;
            }
            if (args[i].Equals("--force-new-instance", StringComparison.OrdinalIgnoreCase))
            {
                forceNewInstance = true;
                continue;
            }
            if (args[i].Equals("--run-diagnostics-export", StringComparison.OrdinalIgnoreCase))
            {
                App.AutoExportDiagnostics = true;
                if (i + 1 < args.Length && !args[i + 1].StartsWith("--"))
                    App.AutoExportDiagnosticsFolder = args[++i];
                continue;
            }
            if (args[i].Equals("--capture-print-dialog", StringComparison.OrdinalIgnoreCase) && i + 2 < args.Length)
            {
                App.CapturePrintDialogImage = args[++i];
                App.CapturePrintDialogOutput = args[++i];
                continue;
            }
            appArgs.Add(args[i]);
        }
        GlidePerformanceTrace.Mark("process_entry");
        try
        {
            if (App.StartHidden && ExternalLaunchBroker.ExistingProcessPresent())
                return 0;

            App.StartupPaths = appArgs.Where(a => File.Exists(a) || Directory.Exists(a)).ToArray();
            // The compact first-frame policy (a few ms) is read before the preloader starts so the
            // overlapped decode targets the same first-frame box the viewer will ask for.
            var firstFramePolicy = SettingsStore.LoadFirstFramePolicy();
            App.StartupFirstFramePolicy = firstFramePolicy;
            if (App.StartupPaths.Count > 0 && File.Exists(App.StartupPaths[0]) && ImageNavigator.IsSupported(App.StartupPaths[0]))
            {
                var (boxWidth, boxHeight) = StartupPreviewBox(firstFramePolicy);
                Glide.Imaging.StartupImagePreloader.Start(App.StartupPaths[0], boxWidth, boxHeight);
            }
            var launchPolicy = new SettingsStore.LaunchPolicy(firstFramePolicy.ReuseSingleInstance, firstFramePolicy.ExternalOpenBehavior);

            // Image launches prefer an already-running Glide by default. The launch-only parser above
            // reads only the compact scalar first-frame policy, avoiding hotkey/gesture/profile migration on a forwarding
            // process that is about to exit anyway.
            var benchmarkForcesColdProcess = string.Equals(Environment.GetEnvironmentVariable("GLIDE_BENCHMARK_DISABLE_REUSE"), "1", StringComparison.Ordinal);
            // The external-open behavior is handled by the elected UI owner. In particular,
            // "Open new window" still needs to reach that owner so it can create a real child
            // window; starting a helper process here used to make that process lose the broker
            // election and exit before it ever opened the image.
            var shouldReuse = !forceNewInstance && !benchmarkForcesColdProcess && !App.BenchmarkExitAfterFirstFrame && launchPolicy.ReuseSingleInstance;
            // Prepare one immutable request batch for the entire launch attempt. The same request IDs
            // are reused by both forwarding attempts so a lost acknowledgement cannot turn the second
            // attempt into a duplicate open under fresh IDs.
            var forwardBatch = shouldReuse
                ? (App.StartupPaths.Count > 0 ? ExternalLaunchBroker.PrepareForwardBatch(App.StartupPaths) : (!App.StartHidden ? ExternalLaunchBroker.PrepareActivateBatch() : null))
                : null;
            if (forwardBatch is not null && ExternalLaunchBroker.TryForwardToExisting(forwardBatch))
                return 0;

            // Presence is claimed only after the forwarding attempt, so this cold process never
            // mistakes its own kernel object for an already-running Glide instance.
            var electedBrokerOwner = ExternalLaunchBroker.ClaimProcessPresence();
            if (!electedBrokerOwner && !forceNewInstance)
            {
                if (forwardBatch is not null &&
                    ExternalLaunchBroker.TryForwardToExisting(forwardBatch, TimeSpan.FromSeconds(2)))
                    return 0;

                // A presence object that survived the bounded handshake still belongs to a live
                // owner. Starting Avalonia here creates the duplicate cold process regression. The
                // owner will either accept a later retry or release the object as it exits.
                if (forwardBatch is not null && ExternalLaunchBroker.ExistingProcessPresent())
                    return 0;

                // The owner released between the handshake and the probe. Make one final election;
                // if another launcher won it, terminate this forwarding helper rather than creating
                // an unowned second UI process.
                electedBrokerOwner = ExternalLaunchBroker.ClaimProcessPresence();
                if (!electedBrokerOwner)
                    return 0;
            }

            // This process is now the elected UI/broker owner. Start the crash breadcrumb file only
            // here so short-lived forwarding helper processes cannot overwrite the live owner trace.
            if (diagnosticTraceRequested)
                LiveDiagnosticTrace.Initialize(AppVersion.DiagnosticTag);
            AppDomain.CurrentDomain.UnhandledException += (_, eventArgs) =>
            {
                AppendCrashLog("unhandled_exception", eventArgs.ExceptionObject?.ToString());
                if (eventArgs.ExceptionObject is Exception fatal)
                    LiveDiagnosticTrace.WriteException("process", "unhandled_exception", fatal, new { eventArgs.IsTerminating });
                else
                    LiveDiagnosticTrace.Write("process", "unhandled_non_exception", new { eventArgs.IsTerminating, value = eventArgs.ExceptionObject?.ToString() });
            };
            TaskScheduler.UnobservedTaskException += (_, eventArgs) =>
                LiveDiagnosticTrace.WriteException("task", "unobserved_exception", eventArgs.Exception);
            AppDomain.CurrentDomain.ProcessExit += (_, _) =>
                LiveDiagnosticTrace.Write("process", "process_exit_event");

            // Do not start storage warm-up before Avalonia. The real Glide shell now owns perceived
            // startup latency; file speculation begins only after that shell has had a chance to paint.
            // Only a process that is actually going to create a UI pays for full settings hydration.
            // Forward-only helper processes exit without racing an unnecessary JSON read/deserialise.
            App.StartupSettingsTask = Task.Run(SettingsStore.Load);
            GlidePerformanceTrace.Mark("avalonia_build_start");
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(appArgs.ToArray());
            LiveDiagnosticTrace.MarkCleanExit(0);
            return 0;
        }
        catch (Exception ex)
        {
            // A GUI-subsystem executable can otherwise disappear without leaving anything the user
            // can copy back to an agent. Always persist fatal startup failures outside the build tree.
            LiveDiagnosticTrace.WriteException("process", "main_catch", ex);
            WriteStartupCrashLog(ex);
            Console.Error.WriteLine(ex);
            return 1;
        }
        finally
        {
            GlidePerformanceTrace.Mark("process_exit");
            GlidePerformanceTrace.Flush();
        }
    }


    [System.Runtime.InteropServices.LibraryImport("user32.dll")]
    private static partial int GetSystemMetrics(int index);

    [System.Runtime.InteropServices.LibraryImport("kernel32.dll", StringMarshalling = System.Runtime.InteropServices.StringMarshalling.Utf16)]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static partial bool SetDllDirectoryW(string? pathName);

    /// <summary>
    /// First-frame box for the cold-start preloader: the primary screen in physical pixels (the
    /// manifest makes the process DPI aware, so these are device pixels), bounded by the user's preview
    /// size exactly like MainWindow.BuildPerformancePolicy. (0,0) = decode at full resolution, used when
    /// the decoder-scaled first frame is switched off.
    /// </summary>
    internal static (int Width, int Height) StartupPreviewBox(SettingsStore.FirstFramePolicy policy)
    {
        if (!policy.AdaptiveFastPreview || !OperatingSystem.IsWindows()) return (0, 0);
        try
        {
            var width = GetSystemMetrics(0);  // SM_CXSCREEN
            var height = GetSystemMetrics(1); // SM_CYSCREEN
            if (width <= 0 || height <= 0) return (0, 0);
            return Glide.Imaging.ImagePerformanceGovernor.ChoosePreviewBounds(policy.RapidPreviewLongestSide, width, height);
        }
        catch { return (0, 0); }
    }

    internal static void AppendCrashLog(string kind, string? detail)
    {
        try
        {
            var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Glide");
            Directory.CreateDirectory(root);
            File.AppendAllText(Path.Combine(root, "crash.log"), $"[{DateTime.UtcNow:O}] {kind} v{AppVersion.DiagnosticTag}{Environment.NewLine}{detail}{Environment.NewLine}{Environment.NewLine}");
        }
        catch { /* the crash logger cannot log its own failure */ }
    }

    private static void WriteStartupCrashLog(Exception ex)
    {
        try
        {
            var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Glide");
            Directory.CreateDirectory(root);
            var path = Path.Combine(root, "startup-crash.log");
            File.WriteAllText(path, $"Glide fatal startup failure\nUTC: {DateTime.UtcNow:O}\n\n{ex}");
        }
        catch { /* the crash logger cannot log its own failure */ }
    }

    // Glide routinely displays images whose decoded BGRA textures are far larger than Avalonia's
    // default ~28 MiB Skia GPU resource cache. Once a texture exceeds/pressures that cache, Skia can
    // evict and re-upload it during otherwise lightweight overlay repaints; that makes selection drag
    // latency scale with SOURCE image dimensions. Keep a desktop-class cache so the settled full-size
    // image remains GPU-resident while the selection/compositor layer changes.
    //
    // 512 MiB intentionally covers a ~100 MP 32-bpp frame (~381 MiB) plus normal UI resources. This is
    // a cache ceiling, not an eager allocation. Do not reduce this back to Avalonia's default without
    // re-running the large-image selection benchmark on Windows.
    private const long GlideGpuResourceCacheBytes = 512L * 1024 * 1024;

    public static AppBuilder BuildAvaloniaApp()
    {
        var builder = AppBuilder.Configure<App>()
            .UseWin32()
            .UseSkia()
            .With(new SkiaOptions
            {
                MaxGpuResourceSizeBytes = GlideGpuResourceCacheBytes
            })
            .With(new Win32PlatformOptions
            {
                CompositionMode = new[]
                {
                    Win32CompositionMode.RedirectionSurface
                }
            });
#if DEBUG
        // Release cold launch must not pay trace-listener/log plumbing costs.
        builder = builder.LogToTrace();
#endif
        return builder;
    }
}
