using System.Text.Json;
using Glide.Core;
using Glide.Core.Reset;
using Glide.Diagnostics;

namespace Glide.App;

/// <summary>
/// Headless command-line modes (installer registration, fixture generation, print-to-PDF acceptance,
/// diagnostics and launch measurement). Each runs before Avalonia initialisation and ends the process.
/// Split out of <c>Program.Main</c> so the GUI startup path reads on its own; behaviour unchanged.
/// </summary>
internal static class CommandLineTools
{
    /// <summary>Runs a tool mode when <paramref name="args"/> requests one.</summary>
    /// <returns>True when a tool ran; the process should then exit with <paramref name="exitCode"/>.</returns>
    public static bool TryRun(string[] args, out int exitCode)
    {
        exitCode = 0;

        // Settings > Windows & developer > Reset Glide: Glide closes itself and starts this mode, which waits for the
        // old process to exit, wipes the chosen data (nothing can rewrite it meanwhile) and starts Glide again.
        if (ResetHelperArguments.TryParse(args, out var resetArguments) && resetArguments is not null)
        {
            exitCode = Platform.ResetServicesFactory.RunHelper(resetArguments);
            return true;
        }

        // Machine-wide Windows Open With integration, invoked by the installer so a fresh install is
        // immediately associated with every recognised extension. Runs before Avalonia; no window.
        if (args.Length >= 1 && args[0].Equals("--register-file-associations", StringComparison.OrdinalIgnoreCase))
        {
            var ok = Platform.WindowsFileAssociationRegistration.RegisterMachineWide(out var message);
            Console.WriteLine(message);
            exitCode = ok ? 0 : 1;
            return true;
        }
        if (args.Length >= 1 && args[0].Equals("--unregister-file-associations", StringComparison.OrdinalIgnoreCase))
        {
            var ok = Platform.WindowsFileAssociationRegistration.UnregisterMachineWide(out var message);
            Console.WriteLine(message);
            exitCode = ok ? 0 : 1;
            return true;
        }

        // Machine-wide Explorer thumbnail provider registration, invoked by the installer.
        if (args.Length >= 1 && args[0].Equals("--register-thumbnails", StringComparison.OrdinalIgnoreCase))
        {
            var extensions = Platform.WindowsThumbnailRegistration.ExtensionsFor(Platform.ThumbnailProviderMode.Recommended);
            var ok = Platform.WindowsThumbnailRegistration.Register(perUser: false, extensions, out var message);
            Console.WriteLine(message);
            exitCode = ok ? 0 : 1;
            return true;
        }
        if (args.Length >= 1 && args[0].Equals("--unregister-thumbnails", StringComparison.OrdinalIgnoreCase))
        {
            var ok = Platform.WindowsThumbnailRegistration.Unregister(perUser: false, ImageFormatRegistry.Extensions, out var message);
            Console.WriteLine(message);
            exitCode = ok ? 0 : 1;
            return true;
        }

        // Build-time fixture generation also runs before Avalonia initialization.
        if (args.Length >= 2 && args[0].Equals("--write-diagnostic-fixtures", StringComparison.OrdinalIgnoreCase))
        {
            var issues = ImageDiagnosticFixtures.Validate();
            if (issues.Count > 0)
            {
                foreach (var issue in issues) Console.Error.WriteLine(issue);
                exitCode = 2;
                return true;
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
            exitCode = 0;
            return true;
        }

        // Image-printing acceptance harness. Runs before Avalonia: GDI+ decodes the oriented image and
        // the shared PrintService geometry/render path spools to a file (Microsoft Print to PDF, etc.).
        if (args.Length >= 3 && args[0].Equals("--print-to-pdf", StringComparison.OrdinalIgnoreCase))
        {
            var image = args[1];
            var output = args[2];
            var printer = "Microsoft Print to PDF";
            var scaling = "Best fit";
            var landscape = false;
            var keepAspect = true;
            var autoRotate = true;
            var hAlign = "Centre";
            var vAlign = "Centre";
            var color = true;
            short copies = 1;
            double ml = 50, mt = 50, mr = 50, mb = 50, custom = 100;
            for (var i = 3; i + 1 < args.Length; i += 2)
            {
                var (key, value) = (args[i], args[i + 1]);
                if (key.Equals("--printer", StringComparison.OrdinalIgnoreCase)) printer = value;
                else if (key.Equals("--scaling", StringComparison.OrdinalIgnoreCase)) scaling = value;
                else if (key.Equals("--landscape", StringComparison.OrdinalIgnoreCase)) landscape = value == "1";
                else if (key.Equals("--keep-aspect", StringComparison.OrdinalIgnoreCase)) keepAspect = value == "1";
                else if (key.Equals("--auto-rotate", StringComparison.OrdinalIgnoreCase)) autoRotate = value == "1";
                else if (key.Equals("--h", StringComparison.OrdinalIgnoreCase)) hAlign = value;
                else if (key.Equals("--v", StringComparison.OrdinalIgnoreCase)) vAlign = value;
                else if (key.Equals("--color", StringComparison.OrdinalIgnoreCase)) color = value == "1";
                else if (key.Equals("--copies", StringComparison.OrdinalIgnoreCase) && short.TryParse(value, out var c)) copies = c;
                else if (key.Equals("--margin", StringComparison.OrdinalIgnoreCase) && double.TryParse(value, out var m)) { ml = mt = mr = mb = m; }
                else if (key.Equals("--scale", StringComparison.OrdinalIgnoreCase) && double.TryParse(value, out var s)) custom = s;
            }
            exitCode = Printing.PrintAcceptance.PrintToFileAsync(image, output, printer, scaling, landscape,
                ml, mt, mr, mb, keepAspect, autoRotate, hAlign, vAlign, custom, copies, color, Console.Out)
                .GetAwaiter().GetResult();
            return true;
        }

        // Diagnostics run before Avalonia initialization so broken UI startup can still be diagnosed.
        if (args.Length >= 2 && args[0].Equals("--diagnostics", StringComparison.OrdinalIgnoreCase))
        {
            if (args[1].Equals("self-test", StringComparison.OrdinalIgnoreCase))
                { exitCode = DiagnosticRunner.SelfTest(Console.Out); return true; }

            if (args[1].Equals("export", StringComparison.OrdinalIgnoreCase))
            {
                var folder = args.Length >= 3 ? args[2] : "Glide Diagnostics";
                exitCode = DiagnosticRunner.Export(folder, Console.Out);
                return true;
            }

            if (args[1].Equals("measure-launch", StringComparison.OrdinalIgnoreCase) ||
                args[1].Equals("cold-launch", StringComparison.OrdinalIgnoreCase))
            {
                var target = args.Length >= 3 && !args[2].StartsWith("--") ? args[2] : null;
                var runs = args.Length >= 4 && int.TryParse(args[3], out var parsedRuns) ? parsedRuns :
                           (args.Length >= 3 && int.TryParse(args[2], out var r2) ? r2 : 3);
                exitCode = DiagnosticRunner.MeasureLaunch(target, runs, Console.Out);
                return true;
            }
        }
        if (args.Length >= 1 && (args[0].Equals("--measure-launch", StringComparison.OrdinalIgnoreCase) ||
                                args[0].Equals("--measure-cold-launch", StringComparison.OrdinalIgnoreCase)))
        {
            var target = args.Length >= 2 && !args[1].StartsWith("--") ? args[1] : null;
            var runs = args.Length >= 3 && int.TryParse(args[2], out var parsedRuns) ? parsedRuns :
                       (args.Length >= 2 && int.TryParse(args[1], out var r2) ? r2 : 3);
            exitCode = DiagnosticRunner.MeasureLaunch(target, runs, Console.Out);
            return true;
        }

        return false;
    }
}
