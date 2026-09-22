namespace Glide.Core.Tests;

/// <summary>
/// Guards the reported "I minimized Glide and the window vanished" regression. Minimize must stay a
/// genuine Windows minimize; Speed Boost standby may only be entered by closing the last window, and
/// only when a tray icon actually exists to bring the window back.
/// </summary>
public sealed class MinimizeBehaviorContractTests
{
    [Fact]
    public void Minimize_never_enters_speed_boost_standby()
    {
        var mainWindow = ReadSource("src", "Glide.App", "MainWindow.axaml.cs").Replace("\r\n", "\n");

        var branchStart = mainWindow.IndexOf("if (newState == WindowState.Minimized)", StringComparison.Ordinal);
        Assert.True(branchStart >= 0, "The WindowState.Minimized property-change branch must exist.");

        // Slice exactly to the branch terminator (the branch closes, then the PropertyChanged lambda).
        var terminator = mainWindow.IndexOf("\n            }\n        };", branchStart, StringComparison.Ordinal);
        Assert.True(terminator > branchStart, "The WindowState.Minimized branch must be a closed block.");
        var branch = mainWindow.Substring(branchStart, terminator - branchStart);
        Assert.DoesNotContain("EnterSpeedBoostStandby()", branch, StringComparison.Ordinal);
    }

    [Fact]
    public void Standby_requires_an_available_tray_icon()
    {
        var mainWindow = ReadSource("src", "Glide.App", "MainWindow.axaml.cs").Replace("\r\n", "\n");
        var app = ReadSource("src", "Glide.App", "App.axaml.cs").Replace("\r\n", "\n");

        Assert.Contains("_settings.SpeedBoostEnabled && App.IsTrayIconAvailable", mainWindow, StringComparison.Ordinal);
        Assert.Contains("internal static bool IsTrayIconAvailable", app, StringComparison.Ordinal);
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
