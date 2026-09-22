using Avalonia;
using Glide.Core;
using Glide.Diagnostics;
using Glide.Diagnostics.Runtime;
using Glide.App.Services;
using Glide.App.Settings;
using System.Text.Json;

namespace Glide.App;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        // Pre-warm thread pool to eliminate cold-launch worker thread dispatch delays
        ThreadPool.SetMinThreads(Math.Max(8, Environment.ProcessorCount), 8);

        // Build-time fixture generation also runs before Avalonia initialization.
        if (args.Length >= 2 && args[0].Equals("--write-diagnostic-fixtures", StringComparison.OrdinalIgnoreCase))
        {
            var issues = ImageDiagnosticFixtures.Validate();
            if (issues.Count > 0)
            {
                foreach (var issue in issues) Console.Error.WriteLine(issue);
                return 2;
            }
            var written = ImageDiagnosticFixtures.WriteTo(args[1]);
            Console.WriteLine($"Diagnostic fixtures written: {Path.GetFullPath(args[1])}");
            Console.WriteLine($"  Core real fixtures: {written.Count}");
            Console.WriteLine($"  Routing probes: {ImageFormatRegistry.Extensions.Count}");
            var corpusStatusPath = Path.Combine(args[1], "performance_corpus_status.json");
            var corpusDirectory = Path.Combine(args[1], "navigation-stress-240");
            var corpusFiles = Directory.Exists(corpusDirectory)
                ? Directory.EnumerateFiles(corpusDirectory, "browse_*.jpg").Count()
                : 0;
            var corpusGenerated = false;
            var corpusStatus = "SKIP";
            var corpusReason = "Performance corpus status was not written.";
            try
            {
                using var statusDocument = JsonDocument.Parse(File.ReadAllText(corpusStatusPath));
                var root = statusDocument.RootElement;
                corpusGenerated = root.TryGetProperty("generated", out var generated) && generated.GetBoolean();
                corpusStatus = root.TryGetProperty("status", out var status) ? status.GetString() ?? "UNKNOWN" : "UNKNOWN";
                corpusReason = root.TryGetProperty("reason", out var reason) ? reason.GetString() ?? "" : "";
            }
            catch (Exception ex) { corpusReason = "Unable to read corpus status: " + ex.Message; }
            if (corpusGenerated)
                Console.WriteLine($"  Navigation stress JPEGs: {corpusFiles} generated browse files");
            else
                Console.WriteLine($"  Navigation stress JPEGs: {corpusStatus} ({corpusReason})");
            Console.WriteLine("  Legacy real-format corpus: staged when diagnostic_corpus is present");
            return 0;
        }

        // Diagnostics run before Avalonia initialization so broken UI startup can still be diagnosed.
        if (args.Length >= 2 && args[0].Equals("--diagnostics", StringComparison.OrdinalIgnoreCase))
        {
            if (args[1].Equals("self-test", StringComparison.OrdinalIgnoreCase))
                return DiagnosticRunner.SelfTest(Console.Out);

            if (args[1].Equals("export", StringComparison.OrdinalIgnoreCase))
            {
                var folder = args.Length >= 3 ? args[2] : "Glide Diagnostics";
                return DiagnosticRunner.Export(folder, Console.Out);
            }

            if (args[1].Equals("measure-launch", StringComparison.OrdinalIgnoreCase) ||
                args[1].Equals("cold-launch", StringComparison.OrdinalIgnoreCase))
            {
                var target = args.Length >= 3 && !args[2].StartsWith("--") ? args[2] : null;
                var runs = args.Length >= 4 && int.TryParse(args[3], out var parsedRuns) ? parsedRuns :
                           (args.Length >= 3 && int.TryParse(args[2], out var r2) ? r2 : 3);
                return DiagnosticRunner.MeasureLaunch(target, runs, Console.Out);
            }
        }
        if (args.Length >= 1 && (args[0].Equals("--measure-launch", StringComparison.OrdinalIgnoreCase) ||
                                args[0].Equals("--measure-cold-launch", StringComparison.OrdinalIgnoreCase)))
        {
            var target = args.Length >= 2 && !args[1].StartsWith("--") ? args[1] : null;
            var runs = args.Length >= 3 && int.TryParse(args[2], out var parsedRuns) ? parsedRuns :
                       (args.Length >= 2 && int.TryParse(args[1], out var r2) ? r2 : 3);
            return DiagnosticRunner.MeasureLaunch(target, runs, Console.Out);
        }

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
            appArgs.Add(args[i]);
        }
        GlidePerformanceTrace.Mark("process_entry");
        try
        {
            if (App.StartHidden && ExternalLaunchBroker.ExistingProcessPresent())
                return 0;

            App.StartupPaths = appArgs.Where(a => File.Exists(a) || Directory.Exists(a)).ToArray();
            if (App.StartupPaths.Count > 0 && File.Exists(App.StartupPaths[0]) && ImageNavigator.IsSupported(App.StartupPaths[0]))
            {
                Glide.Imaging.StartupImagePreloader.Start(App.StartupPaths[0]);
            }

            var firstFramePolicy = SettingsStore.LoadFirstFramePolicy();
            App.StartupFirstFramePolicy = firstFramePolicy;
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
                LiveDiagnosticTrace.Initialize("4.2.4-diagnostic");
            AppDomain.CurrentDomain.UnhandledException += (_, eventArgs) =>
            {
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


    private static void WriteStartupCrashLog(Exception ex)
    {
        try
        {
            var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Glide");
            Directory.CreateDirectory(root);
            var path = Path.Combine(root, "startup-crash.log");
            File.WriteAllText(path, $"Glide fatal startup failure\nUTC: {DateTime.UtcNow:O}\n\n{ex}");
        }
        catch { }
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
            .UsePlatformDetect()
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
