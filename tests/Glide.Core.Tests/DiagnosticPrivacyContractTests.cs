namespace Glide.Core.Tests;

/// <summary>
/// Consumer privacy contract: a normal install must never write diagnostic text into the user's
/// Downloads folder. The crash breadcrumb trace is opt-in only, enabled by tools and crash
/// investigations with --diagnostic-trace or GLIDE_DIAGNOSTIC_TRACE=1.
/// </summary>
public sealed class DiagnosticPrivacyContractTests
{
    [Fact]
    public void Live_diagnostic_trace_is_opt_in_only()
    {
        var program = ReadSource("src", "Glide.App", "Program.cs");
        Assert.Contains("--diagnostic-trace", program, StringComparison.Ordinal);
        Assert.Contains("GLIDE_DIAGNOSTIC_TRACE", program, StringComparison.Ordinal);
        Assert.Contains("if (diagnosticTraceRequested)", program, StringComparison.Ordinal);

        var trace = ReadSource("src", "Glide.Diagnostics", "Runtime", "LiveDiagnosticTrace.cs");
        Assert.Contains("if (Volatile.Read(ref _initialized) == 0) return;", trace, StringComparison.Ordinal);
    }

    private static string ReadSource(params string[] segments)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(new[] { directory.FullName }.Concat(segments).ToArray());
            if (File.Exists(candidate)) return File.ReadAllText(candidate);
            directory = directory.Parent;
        }
        throw new FileNotFoundException("Could not locate source file.", Path.Combine(segments));
    }
}
