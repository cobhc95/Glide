namespace Glide.Core.Tests;

/// <summary>
/// Guards the reported "multiple Glide windows do not appear as separate taskbar buttons" and
/// "minimizing makes the window vanish" regressions. Every top-level Glide window must own its own
/// taskbar button; the old single-representative suppression must not come back.
/// </summary>
public sealed class TaskbarWindowContractTests
{
    [Fact]
    public void Secondary_windows_keep_their_own_taskbar_button()
    {
        var body = MethodBody("src", "Glide.App", "MainWindow.axaml.cs", "private static void ShowSecondaryWindow");

        Assert.Contains("child.ShowInTaskbar = true", body, StringComparison.Ordinal);
        Assert.Contains("ApplyDistinctTaskbarIdentity", body, StringComparison.Ordinal);
        Assert.DoesNotContain("ShowInTaskbar = false", body, StringComparison.Ordinal);
    }

    [Fact]
    public void Registry_no_longer_suppresses_taskbar_buttons()
    {
        var registry = ReadSource("src", "Glide.App", "Platform", "GlideWindowRegistry.cs").Replace("\r\n", "\n");

        // The registry must never toggle ShowInTaskbar again: doing so hid secondary windows and
        // could strand a minimized window without a taskbar button.
        Assert.DoesNotContain("ShowInTaskbar", registry, StringComparison.Ordinal);
    }

    [Fact]
    public void Restoring_from_standby_restores_the_taskbar_button()
    {
        var body = MethodBody("src", "Glide.App", "MainWindow.axaml.cs", "internal void ExitStandby()");

        Assert.Contains("ShowInTaskbar = true", body, StringComparison.Ordinal);
    }

    [Fact]
    public void Taskbar_identity_rejects_missing_input_without_throwing()
    {
        Assert.False(Glide.App.Platform.WindowsTaskbarIdentity.TrySetWindowAppUserModelId(IntPtr.Zero, "cobhc95.Glide.Window.x"));
        Assert.False(Glide.App.Platform.WindowsTaskbarIdentity.TrySetWindowAppUserModelId(new IntPtr(1), string.Empty));
    }

    private static string MethodBody(params string[] segments)
    {
        var path = segments[..^1];
        var signature = segments[^1];
        var source = ReadSource(path).Replace("\r\n", "\n");
        var start = source.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Could not locate '{signature}'.");
        var end = source.IndexOf("\n    }", start, StringComparison.Ordinal);
        Assert.True(end > start, $"Could not locate the end of '{signature}'.");
        return source.Substring(start, end - start);
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
