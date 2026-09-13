using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Glide.Core;

namespace Glide.App;

public partial class App : Application
{
    public static string? StartupPath { get; set; }
    public static IReadOnlyList<string> StartupPaths { get; set; } = Array.Empty<string>();
    public static bool BenchmarkExitAfterFirstFrame { get; set; }
    public static bool AutoExportDiagnostics { get; set; }
    public static string? AutoExportDiagnosticsFolder { get; set; }
    public static Task<Glide.App.Settings.GlideSettingsState>? StartupSettingsTask { get; set; }
    public static Glide.App.Settings.SettingsStore.FirstFramePolicy? StartupFirstFramePolicy { get; set; }
    private static readonly object SettingsGate = new();
    private static Glide.App.Settings.GlideSettingsState? _currentSettings;

    public static Glide.App.Settings.GlideSettingsState? TryGetStartupSettings()
    {
        lock (SettingsGate)
            if (_currentSettings is not null) return _currentSettings.CloneState();
        var task = StartupSettingsTask;
        return task is { IsCompletedSuccessfully: true } ? task.Result.CloneState() : null;
    }

    public static void PublishCurrentSettings(Glide.App.Settings.GlideSettingsState settings)
    {
        lock (SettingsGate) _currentSettings = settings.CloneState();
    }

    public override void Initialize()
    {
        GlidePerformanceTrace.Mark("avalonia_xaml_start");
        AvaloniaXamlLoader.Load(this);
        GlidePerformanceTrace.Mark("avalonia_xaml_end");
    }

    public override void OnFrameworkInitializationCompleted()
    {
        GlidePerformanceTrace.Mark("framework_init_start");
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            GlidePerformanceTrace.Mark("main_window_ctor_start");
            var window = new MainWindow();
            GlidePerformanceTrace.Mark("main_window_ctor_end");
            desktop.MainWindow = window;
            var paths = StartupPaths.Count > 0 ? StartupPaths : (!string.IsNullOrWhiteSpace(StartupPath) ? new[] { StartupPath! } : Array.Empty<string>());
            window.QueueOpenPaths(paths);
        }
        base.OnFrameworkInitializationCompleted();
        GlidePerformanceTrace.Mark("framework_init_end");
    }
}
