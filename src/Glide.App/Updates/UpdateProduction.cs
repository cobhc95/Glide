using Avalonia.Threading;
using Glide.App.Platform;
using Glide.App.Settings;
using Glide.Core;
using Glide.Core.Updates;
using Glide.Diagnostics.Runtime;

namespace Glide.App.Updates;

/// <summary>A dispatcher timer: first tick after <c>firstDelay</c>, then every <c>interval</c>.</summary>
internal sealed class DispatcherUpdateTicker : IUpdateTicker
{
    private DispatcherTimer? _timer;

    public bool IsRunning => _timer is not null;

    public void Start(TimeSpan firstDelay, TimeSpan interval, Func<Task> tick)
    {
        Stop();
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = firstDelay };
        timer.Tick += async (_, _) =>
        {
            timer.Interval = interval;
            try { await tick().ConfigureAwait(true); }
            catch { /* a tick never takes the app down */ }
        };
        _timer = timer;
        timer.Start();
    }

    public void Stop()
    {
        _timer?.Stop();
        _timer = null;
    }
}

/// <summary>The real <see cref="IUpdateHost"/>: the active Glide window, the settings file and the default browser.</summary>
internal sealed class MainWindowUpdateHost : IUpdateHost
{
    private MainWindow? _promptOwner;

    public GlideSettingsState? GetSettings() => App.TryGetStartupSettings();

    public void SaveState(UpdateCheckState state) => SettingsStore.SaveUpdateState(state);

    private static IEnumerable<MainWindow> Visible() =>
        GlideWindowRegistry.Snapshot().Where(w => w.IsVisible && w.WindowState != Avalonia.Controls.WindowState.Minimized);

    /// <summary>The Glide window the user is working in (the active one), or null when Glide is not the foreground application.</summary>
    private static MainWindow? ActiveWindow() => Visible().FirstOrDefault(w => w.IsActive);

    public void SetDontRemind(bool value)
    {
        var window = _promptOwner is { IsVisible: true } owner ? owner : ActiveWindow() ?? Visible().FirstOrDefault();
        window?.SetUpdateDontRemind(value);
    }

    public UpdatePromptDeferral GetDeferral() =>
        ActiveWindow() is { } window ? window.GetUpdatePromptDeferral() : UpdatePromptDeferral.NoWindow;

    public async Task<UpdatePromptResult?> ShowPromptAsync(UpdatePromptModel model)
    {
        // Automatic prompts only come here when a window is active; an explicit "Check for updates now" may find none active.
        var window = ActiveWindow() ?? Visible().FirstOrDefault();
        if (window is null) return null;
        _promptOwner = window;
        return await window.ShowUpdatePromptAsync(model).ConfigureAwait(true);
    }

    public void OpenUrl(string url) => UpdateBrowserLauncher.Open(url);

    public void Log(string name, object? data = null)
    {
        try { LiveDiagnosticTrace.Write("updates", name, data); } catch { /* trace write must never affect the update check */ }
    }
}

/// <summary>
/// Production start-up of the update checker. The ONLY place that builds the real GitHub feed, and it is called only from
/// <c>App.OnFrameworkInitializationCompleted</c>: tests and headless windows never create it, so no test can reach the network.
/// </summary>
internal static class UpdateProduction
{
    /// <summary>Creates and installs the process-wide service and starts its schedule (first check about 20 seconds from now).</summary>
    public static void Start()
    {
        if (UpdateService.Current is not null) return;
        var current = UpdateVersion.ParseOrNull(AppVersion.Short);
        if (current is null) return;
        var checker = new UpdateChecker(GitHubUpdateFeed.CreateProduction(), current);
        var service = new UpdateService(new MainWindowUpdateHost(), checker, new DispatcherUpdateTicker());
        UpdateService.Install(service);
        service.Start();
    }
}
