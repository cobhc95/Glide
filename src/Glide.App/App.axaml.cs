using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
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
    public static Task<Glide.Core.Settings.GlideSettingsState>? StartupSettingsTask { get; set; }
    public static Glide.App.Settings.SettingsStore.FirstFramePolicy? StartupFirstFramePolicy { get; set; }
    public static bool StartHidden { get; set; }
    public static TrayIcon? TrayIconInstance { get; private set; }
    private static int _pendingWindowCreation;
    private static int _standbyRetryScheduled;
    private static int _shutdownBegun;
    private static int _anyWindowOpened;

    /// <summary>True once the application has started to exit (tray/menu Exit, whole-app Overlay X, OS
    /// session end, or the UI lifetime ending). A window closing during shutdown must not enter Speed Boost
    /// standby, and the launch broker no longer acknowledges launches.</summary>
    internal static bool IsShuttingDown => Volatile.Read(ref _shutdownBegun) != 0;

    /// <summary>True once any Glide window of this process has opened (startup-trace exit reason).</summary>
    internal static bool AnyWindowEverOpened => Volatile.Read(ref _anyWindowOpened) != 0;

    internal static void NoteWindowOpened()
    {
        Volatile.Write(ref _speedBoostResident, 0);
        if (Interlocked.Exchange(ref _anyWindowOpened, 1) == 0)
            StartupTrace.Write("first_window_opened");
    }

    private static int _speedBoostResident;

    /// <summary>The last window went into Speed Boost standby: the process stays resident on purpose.</summary>
    internal static void NoteSpeedBoostStandbyEntered() => Volatile.Write(ref _speedBoostResident, 1);

    /// <summary>
    /// Issue #5: the desktop lifetime is OnExplicitShutdown, so a process whose last window closed without entering
    /// Speed Boost standby (a follower process, or Speed Boost off) used to keep running with no window, and with the
    /// tray icon. Called after a window closed without standby: once no window of this process is left (checked after
    /// the close has settled, so a window being replaced, e.g. by a tab detach, keeps the process), the process exits.
    /// </summary>
    internal static void ExitIfNoWindowsRemain()
    {
        if (Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime) return;
        Dispatcher.UIThread.Post(() =>
        {
            if (Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop) return;
            if (!ShouldExitWithoutWindows(IsShuttingDown, Volatile.Read(ref _speedBoostResident) != 0,
                    Platform.GlideWindowRegistry.Snapshot().Count, desktop.Windows.Count,
                    Volatile.Read(ref _pendingWindowCreation) != 0 || ExternalLaunchBroker.HasPendingExternalRequests()))
                return;
            StartupTrace.Write("last_window_closed");
            ShutdownApplication("last_window_closed");
        }, DispatcherPriority.Background);
    }

    /// <summary>Pure half of <see cref="ExitIfNoWindowsRemain"/>.</summary>
    internal static bool ShouldExitWithoutWindows(bool shuttingDown, bool speedBoostResident, int glideWindows, int openTopLevels, bool windowPending)
        => !shuttingDown && !speedBoostResident && glideWindows == 0 && openTopLevels == 0 && !windowPending;

    /// <summary>
    /// The first step of every application exit. Stops the external-launch broker (releases the presence object
    /// and the pipe) BEFORE windows are torn down, so a Glide launched while this process is dying elects itself
    /// instead of forwarding to a process that acknowledges the request and then exits without showing anything
    /// (the 5.0.7 silent-exit report; docs/audits/silent-exit-investigation.md). Idempotent.
    /// </summary>
    internal static void BeginApplicationShutdown(string reason)
    {
        if (Interlocked.Exchange(ref _shutdownBegun, 1) != 0) return;
        StartupTrace.Write("shutdown_begin", "reason=" + reason);
        try { ExternalLaunchBroker.Stop(); }
        catch (Exception ex) { StartupTrace.Write("broker_stop_failed", ex.GetType().Name); }
    }

    /// <summary>Begins application shutdown (see <see cref="BeginApplicationShutdown"/>) and ends the desktop lifetime.</summary>
    internal static void ShutdownApplication(string reason)
    {
        BeginApplicationShutdown(reason);
        if (Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            desktop.Shutdown();
    }

    /// <summary>Test seam: resets the process-wide shutdown latch.</summary>
    internal static void ResetShutdownStateForTests() => Volatile.Write(ref _shutdownBegun, 0);

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
            StartupTrace.Write("standby_window_construct_start");
            var window = new MainWindow();
            StartupTrace.Write("standby_window_rehydrate");
            // This window exists only to receive work already accepted by the resident broker.
            // Treat it as an explicit external launch so the normal standalone Home destination
            // is not created before the queued image/folder request is dispatched.
            window.PrepareForPendingExternalOpen();
            desktop.MainWindow = window;
            window.Show();
            window.Activate();
        }
        catch (Exception ex)
        {
            StartupTrace.Write("standby_window_rehydrate_failed", ex.GetType().Name + ": " + ex.Message);
            Volatile.Write(ref _pendingWindowCreation, 0);
            throw;
        }
        Volatile.Write(ref _pendingWindowCreation, 0);
    }

    public static Glide.Core.Settings.GlideSettingsState? TryGetStartupSettings()
    {
        if (Glide.App.Settings.SettingsService.Current is { } live) return live;
        var task = StartupSettingsTask;
        // Block-free by contract: Result is only touched when IsCompletedSuccessfully is already
        // true, so a still-running background load returns null instead of stalling the UI thread.
        return task is { IsCompletedSuccessfully: true } ? task.Result.CloneState() : null;
    }

    /// <summary>Makes <paramref name="settings"/> the process-wide live settings (Stage C: forwards to SettingsService).</summary>
    public static void PublishCurrentSettings(Glide.Core.Settings.GlideSettingsState settings) => Glide.App.Settings.SettingsService.Publish(settings);

    public override void Initialize()
    {
        GlidePerformanceTrace.Mark("avalonia_xaml_start");
        AvaloniaXamlLoader.Load(this);
        GlidePerformanceTrace.Mark("avalonia_xaml_end");
        ToolTipGuard.Register();
        DialogEscape.Register();
        PopupCursorGuard.Register();
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
            // OS session end / programmatic TryShutdown, and the final Exit, both stop the broker first.
            desktop.ShutdownRequested += (_, e) => { if (!e.Cancel) BeginApplicationShutdown("shutdown_requested"); };
            desktop.Exit += (_, _) => BeginApplicationShutdown("lifetime_exit");
            InitializeTrayIcon();
            StartSettingsFileWatcher();

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
                    // Only the single-instance owner may stay resident (issue #5); a background start
                    // that is not the owner has nothing to do and exits.
                    if (ExternalLaunchBroker.IsPresenceOwner)
                        window.EnterSpeedBoostStandby(persistSession: false);
                    else
                        ShutdownApplication("background_not_owner");
                };
            }

            // Update checker (5.0.1): a daily check of GitHub's latest release, started about 20 seconds after the window is shown.
            // Never in benchmark / diagnostics-export modes, and never in tests (they do not run this method).
            ScheduleUpdateChecker(window);

            // Automatically make Glide available in Windows' Open With list for every recognised
            // extension, without the user having to configure it. Deferred to background priority so
            // it can never delay first paint, and best-effort so a locked-down registry cannot fail
            // startup.
            ScheduleFileAssociationRegistration();
        }
        base.OnFrameworkInitializationCompleted();
        GlidePerformanceTrace.Mark("framework_init_end");
    }

    private static void ScheduleUpdateChecker(MainWindow window)
    {
        if (CapturePrintDialogImage is not null ||
            BenchmarkExitAfterFirstFrame ||
            NotifyFirstFrameEventName is not null ||
            AutoExportDiagnostics ||
            !string.IsNullOrWhiteSpace(AutoExportDiagnosticsFolder))
            return;
        window.Opened += (_, _) => Updates.UpdateProduction.Start();
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
            () =>
            {
                _ = Task.Run(Platform.WindowsFileAssociationRegistration.EnsureRegisteredQuietly);
                _ = Task.Run(Platform.ThumbnailSettingsStore.EnsureMigrationApplied);
            },
            DispatcherPriority.Background);
    }

    public static void InitializeTrayIcon()
    {
        if (!OperatingSystem.IsWindows()) return;
        try
        {
            if (TrayIconInstance is not null) return;
            // Issue #5: while the settings file is still loading the icon starts hidden (it used to start visible
            // with the defaults, i.e. "Always", whatever the user chose); it is updated once the settings are live.
            var settings = TryGetStartupSettings();
            var icon = new TrayIcon
            {
                ToolTipText = "Glide Image Viewer (Speed Boost)",
                IsVisible = settings is not null && ShouldShowTrayIcon(settings)
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
            catch { /* tray icon is optional; the tray falls back to no custom icon */ }

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
            // Every publish (startup adoption, Settings preview / OK, standby, another process' change picked up by
            // the settings-file watcher) re-evaluates the icon, so no path can leave it out of date.
            Glide.App.Settings.SettingsService.Changed += (_, e) => UpdateTrayIconState(e.Current);
        }
        catch { /* tray icon is optional; the app runs without it */ }
    }

    public static void UpdateTrayIconState(GlideSettingsState? settings = null)
    {
        if (TrayIconInstance is null) return;
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => UpdateTrayIconState(settings));
            return;
        }
        settings ??= TryGetStartupSettings();
        if (settings is null) return;
        TrayIconInstance.IsVisible = ShouldShowTrayIcon(settings);
    }

    /// <summary>Held for the process lifetime (a collected watcher stops raising events).</summary>
    internal static FileSystemWatcher? SettingsFileWatcher { get; private set; }
    private static Timer? _settingsReloadTimer;

    /// <summary>
    /// Issue #5: a resident Speed Boost process kept the settings it had when it started, so a change saved by another
    /// Glide process (for example the tray icon set to "Never") never reached it. Watch the settings file; when it
    /// changes, reload it and make it live while this process has no visible window (standby: nobody is editing here),
    /// or else only re-evaluate the tray icon from it.
    /// </summary>
    private static void StartSettingsFileWatcher()
    {
        if (!OperatingSystem.IsWindows() || CapturePrintDialogImage is not null || BenchmarkExitAfterFirstFrame ||
            NotifyFirstFrameEventName is not null || AutoExportDiagnostics || !string.IsNullOrWhiteSpace(AutoExportDiagnosticsFolder))
            return;
        try
        {
            var path = Glide.App.Settings.SettingsStore.GetSettingsPath();
            var directory = Path.GetDirectoryName(path);
            if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory)) return;
            _settingsReloadTimer = new Timer(_ => ReloadSettingsFromDisk(), null, Timeout.Infinite, Timeout.Infinite);
            var watcher = new FileSystemWatcher(directory, Path.GetFileName(path))
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size
            };
            FileSystemEventHandler changed = (_, _) => _settingsReloadTimer?.Change(400, Timeout.Infinite);
            watcher.Changed += changed;
            watcher.Created += changed;
            watcher.Renamed += (_, _) => _settingsReloadTimer?.Change(400, Timeout.Infinite);
            watcher.EnableRaisingEvents = true;
            SettingsFileWatcher = watcher;
        }
        catch { /* best effort: without the watcher only this process' own changes apply live */ }
    }

    private static void ReloadSettingsFromDisk()
    {
        if (IsShuttingDown) return;
        GlideSettingsState loaded;
        // Not SettingsStore.Load: it falls back to the defaults on a read error, and a file caught mid-write must
        // never make the defaults live. A failed read is simply skipped (the write's own event follows).
        try
        {
            var path = Glide.App.Settings.SettingsStore.GetSettingsPath();
            if (!File.Exists(path)) return;
            loaded = Glide.App.Settings.SettingsStore.MigrateFromJson(File.ReadAllText(path), isSettingsFile: false);
        }
        catch { return; }
        Dispatcher.UIThread.Post(() =>
        {
            if (IsShuttingDown) return;
            if (!Platform.GlideWindowRegistry.Snapshot().Any(window => window.IsVisible))
                Glide.App.Settings.SettingsService.Publish(loaded); // re-evaluates the tray icon (Changed)
            else
                UpdateTrayIconState(loaded);
        });
    }

    /// <summary>Tray icon mode, honouring the legacy ShowTrayIcon=false switch as "Never".</summary>
    internal static string EffectiveTrayIconMode(GlideSettingsState settings)
    {
        if (!settings.ShowTrayIcon) return "Never";
        return settings.SpeedBoostTrayIcon switch
        {
            "Only while Glide is closed" or "Never" => settings.SpeedBoostTrayIcon,
            _ => "Always"
        };
    }

    private static bool ShouldShowTrayIcon(GlideSettingsState settings)
        => ShouldShowTrayIconPolicy(settings, ExternalLaunchBroker.IsPresenceOwner,
            () => Platform.GlideWindowRegistry.Snapshot().Any(window => window.IsVisible));

    /// <summary>
    /// The tray icon decision. Issue #5: only the single-instance owner (the one process that can stay resident in Speed
    /// Boost) shows it; a follower process showed a second icon that stayed after its window closed.
    /// </summary>
    internal static bool ShouldShowTrayIconPolicy(GlideSettingsState settings, bool ownsLaunchBroker, Func<bool> anyWindowVisible)
    {
        if (!settings.SpeedBoostEnabled || !ownsLaunchBroker) return false;
        return EffectiveTrayIconMode(settings) switch
        {
            "Never" => false,
            // Only while no Glide window is on screen, i.e. the warm instance is in standby.
            "Only while Glide is closed" => !anyWindowVisible(),
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
                ShutdownApplication("tray_exit_standby");
            }
        }
    }
}
