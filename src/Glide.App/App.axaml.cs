using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Glide.Core;

namespace Glide.App;

public partial class App : Application
{
    public static string? StartupPath { get; set; }
    public static IReadOnlyList<string> StartupPaths { get; set; } = Array.Empty<string>();
    public static bool BenchmarkExitAfterFirstFrame { get; set; }
    public static string? NotifyFirstFrameEventName { get; set; }
    public static bool AutoExportDiagnostics { get; set; }
    public static string? AutoExportDiagnosticsFolder { get; set; }
    public static Task<Glide.App.Settings.GlideSettingsState>? StartupSettingsTask { get; set; }
    public static Glide.App.Settings.SettingsStore.FirstFramePolicy? StartupFirstFramePolicy { get; set; }
    public static bool StartHidden { get; set; }
    public static TrayIcon? TrayIconInstance { get; private set; }
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
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            InitializeTrayIcon();

            var paths = StartupPaths.Count > 0 ? StartupPaths : (!string.IsNullOrWhiteSpace(StartupPath) ? new[] { StartupPath! } : Array.Empty<string>());
            var hasExplicitOpen = paths.Count > 0;

            GlidePerformanceTrace.Mark("main_window_ctor_start");
            var window = new MainWindow();
            GlidePerformanceTrace.Mark("main_window_ctor_end");
            desktop.MainWindow = window;

            if (!StartHidden || hasExplicitOpen)
            {
                window.QueueOpenPaths(paths);
                window.TryPrepareEarlyStartupPresentation();
            }
            else
            {
                // Started in background mode (--background): keep hidden and standby-warmed
                window.WindowState = WindowState.Minimized;
                window.ShowInTaskbar = false;
                window.Opacity = 0;
                window.Opened += (_, _) =>
                {
                    window.Hide();
                    window.Opacity = 1.0;
                    window.ShowInTaskbar = true;
                    window.WindowState = WindowState.Normal;
                    try { GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true); } catch { }
                };
            }
        }
        base.OnFrameworkInitializationCompleted();
        GlidePerformanceTrace.Mark("framework_init_end");
    }

    public static void InitializeTrayIcon()
    {
        if (!OperatingSystem.IsWindows()) return;
        try
        {
            if (TrayIconInstance is not null) return;
            var settings = TryGetStartupSettings() ?? new Settings.GlideSettingsState();
            var icon = new TrayIcon
            {
                ToolTipText = "Glide Image Viewer (Speed Boost)",
                IsVisible = settings.SpeedBoostEnabled && settings.ShowTrayIcon
            };

            try
            {
                var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "Glide.ico");
                if (File.Exists(iconPath))
                {
                    icon.Icon = new WindowIcon(iconPath);
                }
                else
                {
                    icon.Icon = new WindowIcon(Avalonia.Platform.AssetLoader.Open(new Uri("avares://Glide/Assets/Glide.ico")));
                }
            }
            catch { }

            var menu = new NativeMenu();
            var openItem = new NativeMenuItem("Open Glide");
            openItem.Click += (_, _) => ShowMainWindow();

            var settingsItem = new NativeMenuItem("Settings");
            settingsItem.Click += (_, _) => ShowSettings();

            var exitItem = new NativeMenuItem("Exit Glide");
            exitItem.Click += (_, _) => ExitApplication();

            menu.Add(openItem);
            menu.Add(settingsItem);
            menu.Add(new NativeMenuItemSeparator());
            menu.Add(exitItem);

            icon.Menu = menu;
            icon.Clicked += (_, _) => ShowMainWindow();

            var icons = new TrayIcons { icon };
            TrayIcon.SetIcons(Current!, icons);
            TrayIconInstance = icon;
        }
        catch { }
    }

    public static void UpdateTrayIconState(Settings.GlideSettingsState? settings = null)
    {
        if (TrayIconInstance is null) return;
        settings ??= TryGetStartupSettings();
        if (settings is null) return;
        TrayIconInstance.IsVisible = settings.SpeedBoostEnabled && settings.ShowTrayIcon;
    }

    public static void ShowMainWindow()
    {
        if (Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            if (desktop.MainWindow is MainWindow window)
            {
                window.ExitStandby();
                window.RestoreFromMinimizedIfNeeded();
                window.Activate();
            }
            else
            {
                var newWindow = new MainWindow();
                desktop.MainWindow = newWindow;
                newWindow.Show();
                newWindow.Activate();
            }
        }
    }

    public static void ShowSettings()
    {
        if (Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop &&
            desktop.MainWindow is MainWindow window)
        {
            if (!window.IsVisible) window.Show();
            window.RestoreFromMinimizedIfNeeded();
            window.Activate();
            window.OpenSettingsDialog();
        }
    }

    public static void ExitApplication()
    {
        if (Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            if (desktop.MainWindow is MainWindow window)
            {
                window.ForceExit();
            }
            else
            {
                desktop.Shutdown();
            }
        }
    }
}
