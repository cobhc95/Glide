using System;
using System.IO;
using System.Text;
using Glide.Diagnostics.Runtime;
using Xunit;

namespace Glide.Core.Tests;

public sealed class LiveDiagnosticTraceTests
{
    [Fact]
    public void Write_and_flush_records_breadcrumbs()
    {
        LiveDiagnosticTrace.Initialize("test-version");
        LiveDiagnosticTrace.Write("unit_test", "event_alpha", new { key = "value" });
        LiveDiagnosticTrace.WriteException("unit_test", "event_ex", new InvalidOperationException("simulated"), new { step = 1 });
        LiveDiagnosticTrace.Flush();

        var path = LiveDiagnosticTrace.Path;
        Assert.NotNull(path);
        Assert.True(File.Exists(path));

        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var content = reader.ReadToEnd();
        Assert.Contains("unit_test.event_alpha", content);
        Assert.Contains("unit_test.event_ex", content);
        Assert.Contains("simulated", content);
    }
}
