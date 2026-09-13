using Avalonia;
using Glide.Core;
using Glide.Diagnostics;
using Glide.App.Services;
using Glide.App.Settings;
using System.Text.Json;

namespace Glide.App;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
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
        }

        // Optional benchmark trace. Strip our private switch before passing arguments to Avalonia.
        // Example: Glide.exe --perf-trace "C:\temp\glide-start.tsv" image.jpg
        var appArgs = new List<string>(args.Length);
        var forceNewInstance = false;
        for (var i = 0; i < args.Length; i++)
        {
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
            App.StartupPaths = appArgs.Where(a => File.Exists(a) || Directory.Exists(a)).ToArray();

            var firstFramePolicy = SettingsStore.LoadFirstFramePolicy();
            App.StartupFirstFramePolicy = firstFramePolicy;
            var launchPolicy = new SettingsStore.LaunchPolicy(firstFramePolicy.ReuseSingleInstance, firstFramePolicy.ExternalOpenBehavior);

            // Image launches prefer an already-running Glide by default. The launch-only parser above
            // reads only the compact scalar first-frame policy, avoiding hotkey/gesture/profile migration on a forwarding
            // process that is about to exit anyway.
            var benchmarkForcesColdProcess = string.Equals(Environment.GetEnvironmentVariable("GLIDE_BENCHMARK_DISABLE_REUSE"), "1", StringComparison.Ordinal);
            var shouldReuse = !forceNewInstance && !benchmarkForcesColdProcess && launchPolicy.ReuseSingleInstance &&
                !string.Equals(launchPolicy.ExternalOpenBehavior, "Open new window", StringComparison.OrdinalIgnoreCase);
            // Prepare one immutable request batch for the entire launch attempt. The same request IDs
            // are reused by both forwarding attempts so a lost acknowledgement cannot turn the second
            // attempt into a duplicate open under fresh IDs.
            var forwardBatch = App.StartupPaths.Count > 0 && shouldReuse
                ? ExternalLaunchBroker.PrepareForwardBatch(App.StartupPaths)
                : null;
            if (forwardBatch is not null && ExternalLaunchBroker.TryForwardToExisting(forwardBatch))
                return 0;

            // Presence is claimed only after the forwarding attempt, so this cold process never
            // mistakes its own kernel object for an already-running Glide instance.
            var electedBrokerOwner = ExternalLaunchBroker.ClaimProcessPresence();
            if (!electedBrokerOwner && forwardBatch is not null &&
                ExternalLaunchBroker.TryForwardToExisting(forwardBatch))
                return 0;

            // Do not start storage warm-up before Avalonia. The real Glide shell now owns perceived
            // startup latency; file speculation begins only after that shell has had a chance to paint.
            // Only a process that is actually going to create a UI pays for full settings hydration.
            // Forward-only helper processes exit without racing an unnecessary JSON read/deserialise.
            App.StartupSettingsTask = Task.Run(SettingsStore.Load);
            GlidePerformanceTrace.Mark("avalonia_build_start");
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(appArgs.ToArray());
            return 0;
        }
        catch (Exception ex)
        {
            // A GUI-subsystem executable can otherwise disappear without leaving anything the user
            // can copy back to an agent. Always persist fatal startup failures outside the build tree.
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
            File.WriteAllText(path, $"Glide 3.0 fatal startup failure\nUTC: {DateTime.UtcNow:O}\n\n{ex}");
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
            });
#if DEBUG
        // Release cold launch must not pay trace-listener/log plumbing costs.
        builder = builder.LogToTrace();
#endif
        return builder;
    }
}
