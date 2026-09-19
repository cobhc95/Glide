namespace Glide.Core.Tests;

public sealed class StandbyReceiverCreationContractTests
{
    [Fact]
    public void Delayed_standby_retry_cannot_create_a_window_after_the_queue_is_drained()
    {
        var app = ReadSource("src", "Glide.App", "App.axaml.cs");
        var broker = ReadSource("src", "Glide.App", "Services", "ExternalLaunchBroker.cs");

        Assert.Contains("internal static bool HasPendingExternalRequests()", broker, StringComparison.Ordinal);
        Assert.True(Count(app, "ExternalLaunchBroker.HasPendingExternalRequests()") >= 3,
            "The entry point, delayed retry and post-gate creation path must all re-check broker work.");
        Assert.Contains("if (!ExternalLaunchBroker.HasPendingExternalRequests())\n            {\n                Volatile.Write(ref _pendingWindowCreation, 0);\n                return;\n            }\n            var window = new MainWindow();",
            app.Replace("\r\n", "\n"), StringComparison.Ordinal);
    }

    private static int Count(string text, string value)
    {
        var count = 0;
        for (var index = 0; (index = text.IndexOf(value, index, StringComparison.Ordinal)) >= 0; index += value.Length)
            count++;
        return count;
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
