using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Controls.Primitives.PopupPositioning;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Glide.App.Services;
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
    // Diagnostic-only: open the Print window for one image, capture it, then exit.
    public static string? CapturePrintDialogImage { get; set; }
    public static string? CapturePrintDialogOutput { get; set; }
    public static Task<Glide.App.Settings.GlideSettingsState>? StartupSettingsTask { get; set; }
    public static Glide.App.Settings.SettingsStore.FirstFramePolicy? StartupFirstFramePolicy { get; set; }
    public static bool StartHidden { get; set; }
    public static TrayIcon? TrayIconInstance { get; private set; }
    private static readonly object SettingsGate = new();
    private static Glide.App.Settings.GlideSettingsState? _currentSettings;
    private static int _pendingWindowCreation;
    private static int _standbyRetryScheduled;

    /// <summary>
    /// Rehydrates the render window when the process-level broker owns accepted work during
    /// Speed Boost standby. The interlocked guard coalesces concurrent forwarded batches.
    /// </summary>
    internal static void EnsureMainWindowForPendingExternalRequest()
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(EnsureMainWindowForPendingExternalRequest);
            return;
        }
        // This method can be reached by a delayed standby-close retry.  The original request may
        // already have been consumed by a receiver which became available in the meantime.  Never
        // resurrect a Glide window from that stale retry.
        if (!ExternalLaunchBroker.HasPendingExternalRequests())
            return;
        if (Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop)
            return;
        // Speed Boost closes the render window while keeping the process and broker alive. During
        // the short standby-close interval the old HWND is intentionally hidden but is still the
        // broker owner. Creating a replacement in that gap produced the invisible Alt-Tab/empty
        // window reported by users. Wait for the owner to finish its internal close instead.
        if (desktop.MainWindow is MainWindow existing)
        {
            if (existing.IsStandbyClosePending)
            {
                if (Interlocked.Exchange(ref _standbyRetryScheduled, 1) == 0)
                {
                    Dispatcher.UIThread.Post(async () =>
                    {
                        try { await Task.Delay(50).ConfigureAwait(true); }
                        finally
                        {
                            Volatile.Write(ref _standbyRetryScheduled, 0);
                            if (ExternalLaunchBroker.HasPendingExternalRequests())
                                EnsureMainWindowForPendingExternalRequest();
                        }
                    }, DispatcherPriority.Background);
                }
                return;
            }
            if (existing.IsVisible) return;
        }
        if (Interlocked.Exchange(ref _pendingWindowCreation, 1) != 0) return;
        try
        {
            // Re-check after winning the creation gate. A visible receiver may have drained the
            // queue between the first check and this point.
            if (!ExternalLaunchBroker.HasPendingExternalRequests())
            {
                Volatile.Write(ref _pendingWindowCreation, 0);
                return;
            }
            var window = new MainWindow();
            // This window exists only to receive work already accepted by the resident broker.
            // Treat it as an explicit external launch so the normal standalone Home destination
            // is not created before the queued image/folder request is dispatched.
            window.PrepareForPendingExternalOpen();
            desktop.MainWindow = window;
            window.Show();
            window.Activate();
        }
        catch
        {
            Volatile.Write(ref _pendingWindowCreation, 0);
            throw;
        }
        Volatile.Write(ref _pendingWindowCreation, 0);
    }

    public static Glide.App.Settings.GlideSettingsState? TryGetStartupSettings()
    {
        lock (SettingsGate)
            if (_currentSettings is not null) return _currentSettings.CloneState();
        var task = StartupSettingsTask;
        // Block-free by contract: Result is only touched when IsCompletedSuccessfully is already
        // true, so a still-running background load returns null instead of stalling the UI thread.
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
        RegisterToolTipPlacement();
    }

    /// <summary>
    /// Avalonia opens tooltips at the pointer by default, so the popup lands on top of the hovered
    /// toolbar button: the button can no longer be clicked and, because the pointer is now over the
    /// popup, moving onto a neighbouring button never closes it. Anchor every tooltip to its control
    /// instead, opening away from the window edge the control sits on (above for the bottom status
    /// bar, below for the title/tab bar), so it never covers the control row.
    /// </summary>
    private static void RegisterToolTipPlacement()
    {
        ToolTip.ToolTipOpeningEvent.AddClassHandler<Control>((control, _) =>
        {
            if (TopLevel.GetTopLevel(control) is not { } top) return;
            var origin = control.TranslatePoint(default, top);
            if (origin is null) return;
            var centerY = origin.Value.Y + control.Bounds.Height / 2;
            var lowerHalf = centerY > top.Bounds.Height / 2;
            ToolTip.SetPlacement(control, lowerHalf ? PlacementMode.Top : PlacementMode.Bottom);
            ToolTip.SetHorizontalOffset(control, 0);
            ToolTip.SetVerticalOffset(control, lowerHalf ? -6 : 6);
        }, handledEventsToo: true);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        GlidePerformanceTrace.Mark("framework_init_start");
        // Exceptions escaping UI callbacks (including the many async void event handlers) would
        // otherwise terminate the whole viewer. Log them and keep running; only genuinely
        // unrecoverable process states are allowed to propagate.
        Dispatcher.UIThread.UnhandledException += (_, e) =>
        {
            var fatal = e.Exception is OutOfMemoryException or InsufficientExecutionStackException;
            Glide.Diagnostics.Runtime.LiveDiagnosticTrace.WriteException("ui", "dispatcher_unhandled_exception", e.Exception, new { handled = !fatal });
            if (!fatal) e.Handled = true;
        };
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
                    // Background startup must not leave a live Avalonia composition target merely
                    // hidden. Enter the same dormant-window lifecycle used by Speed Boost close.
                    window.EnterSpeedBoostStandby(persistSession: false);
                };
            }

            // Automatically make Glide available in Windows' Open With list for every recognised
            // extension, without the user having to configure it. Deferred to background priority so
            // it can never delay first paint, and best-effort so a locked-down registry cannot fail
            // startup.
            ScheduleFileAssociationRegistration();
        }
        base.OnFrameworkInitializationCompleted();
        GlidePerformanceTrace.Mark("framework_init_end");
    }

    private static void ScheduleFileAssociationRegistration()
    {
        if (!OperatingSystem.IsWindows()) return;
        // Diagnostic/benchmark harnesses must never mutate the user's real Open With registration.
        if (CapturePrintDialogImage is not null ||
            BenchmarkExitAfterFirstFrame ||
            NotifyFirstFrameEventName is not null ||
            AutoExportDiagnostics ||
            !string.IsNullOrWhiteSpace(AutoExportDiagnosticsFolder))
            return;
        Dispatcher.UIThread.Post(
            () => _ = Task.Run(Platform.WindowsFileAssociationRegistration.EnsureRegisteredQuietly),
            DispatcherPriority.Background);
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
                IsVisible = ShouldShowTrayIcon(settings)
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
        TrayIconInstance.IsVisible = ShouldShowTrayIcon(settings);
    }

    /// <summary>Tray icon mode, honouring the legacy ShowTrayIcon=false switch as "Never".</summary>
    internal static string EffectiveTrayIconMode(Settings.GlideSettingsState settings)
    {
        if (!settings.ShowTrayIcon) return "Never";
        return settings.SpeedBoostTrayIcon switch
        {
            "Only while Glide is closed" or "Never" => settings.SpeedBoostTrayIcon,
            _ => "Always"
        };
    }

    private static bool ShouldShowTrayIcon(Settings.GlideSettingsState settings)
    {
        if (!settings.SpeedBoostEnabled) return false;
        return EffectiveTrayIconMode(settings) switch
        {
            "Never" => false,
            // Only while no Glide window is on screen, i.e. the warm instance is in standby.
            "Only while Glide is closed" => !Platform.GlideWindowRegistry.Snapshot().Any(window => window.IsVisible),
            _ => true
        };
    }

    /// <summary>
    /// True when the Speed Boost infrastructure (tray host + resident broker) is running. Standby no
    /// longer requires the icon to be visible: the user may hide it, and launching Glide again wakes
    /// the warm instance through the broker. The tray host exists only on Windows.
    /// </summary>
    internal static bool IsTrayIconAvailable => TrayIconInstance is not null;

    public static void ShowMainWindow()
    {
        if (Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            if (desktop.MainWindow is MainWindow window && window.IsVisible)
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
        if (Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            if (desktop.MainWindow is not MainWindow existing || !existing.IsVisible)
            {
                var replacement = new MainWindow();
                desktop.MainWindow = replacement;
                replacement.Show();
            }
            if (desktop.MainWindow is not MainWindow window) return;
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
