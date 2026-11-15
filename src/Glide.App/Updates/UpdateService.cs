using Glide.App.Settings;
using Glide.Core.Updates;

namespace Glide.App.Updates;

public enum UpdatePromptAction { NotNow, ViewOnGitHub }

/// <summary>What the user chose in the update prompt. <see cref="DontRemind"/> is the state of "Don't remind me again" at the moment a button was pressed.</summary>
public sealed record UpdatePromptResult(UpdatePromptAction Action, bool DontRemind);

/// <summary>What the update prompt shows. <see cref="ReleaseUrl"/> is already validated (https, github.com, inside the Glide repository).</summary>
public sealed record UpdatePromptModel(string CurrentVersion, string LatestVersion, string ReleasedText, string ReleaseUrl);

/// <summary>Everything the update service needs from the app around it, so it can be tested with a fake (no window, no network, no disk).</summary>
internal interface IUpdateHost
{
    /// <summary>The live settings (null while the start-up settings are still loading).</summary>
    GlideSettingsState? GetSettings();
    /// <summary>Persists only the update checker's memory.</summary>
    void SaveState(UpdateCheckState state);
    /// <summary>Turns "Don't remind me about updates" on or off through the normal settings pipeline.</summary>
    void SetDontRemind(bool value);
    /// <summary>Why the prompt cannot be shown right now (fullscreen, slideshow, another dialog, typing, no active window).</summary>
    UpdatePromptDeferral GetDeferral();
    /// <summary>Shows the prompt modally over the active Glide window; null when it could not be shown.</summary>
    Task<UpdatePromptResult?> ShowPromptAsync(UpdatePromptModel model);
    /// <summary>Opens a validated release address in the default browser.</summary>
    void OpenUrl(string url);
    void Log(string name, object? data = null);
}

/// <summary>A repeating timer (a dispatcher timer in the app, a fake in tests).</summary>
internal interface IUpdateTicker
{
    bool IsRunning { get; }
    /// <summary>Calls <paramref name="tick"/> after <paramref name="firstDelay"/> and then every <paramref name="interval"/>.</summary>
    void Start(TimeSpan firstDelay, TimeSpan interval, Func<Task> tick);
    void Stop();
}

/// <summary>
/// The update checker of a running Glide: a daily automatic check, the prompt, and the manual "Check now". One instance serves
/// every Glide window of the process (<see cref="Current"/>), so only one check and one prompt happen at a time.
/// <list type="bullet">
/// <item>An automatic check runs at most every 24 hours (<see cref="UpdateSchedule"/>), starting about 20 seconds after start-up and
/// then on a one-minute tick, so a long-running instance (Speed Boost standby) re-checks daily. A failed or offline check is silent
/// and retried after at least 6 hours.</item>
/// <item>The prompt is offered when a newer stable version is known, prompts are on and it was not shown for that version in the
/// last 20 hours; while the screen is not a good moment (fullscreen, slideshow, another dialog, typing, no active window) it waits
/// and is retried every minute.</item>
/// <item>The service only ever opens the release page; nothing is downloaded or installed.</item>
/// </list>
/// </summary>
internal sealed class UpdateService
{
    /// <summary>The process-wide service; null until production start-up installs it (so tests and designers never start a checker).</summary>
    public static UpdateService? Current { get; private set; }

    /// <summary>Installs (or with null removes) the process-wide service. Production start-up only; tests set their own and remove it again.</summary>
    internal static void Install(UpdateService? service) => Current = service;

    /// <summary>The first automatic check after enabling daily checks in Settings while Glide runs.</summary>
    public static readonly TimeSpan EnabledCheckDelay = TimeSpan.FromSeconds(3);

    private readonly IUpdateHost _host;
    private readonly UpdateChecker _checker;
    private readonly IUpdateTicker _ticker;
    private readonly Func<DateTime> _utcNow;
    private UpdateCheckState _state = new();
    private bool _seeded;
    private bool _ticking;
    private bool _prompting;
    private bool _started;
    private Task<UpdateCheckOutcome>? _inFlight;
    private bool _checking;
    private UpdateCheckStatus? _lastManualOutcome;

    internal UpdateService(IUpdateHost host, UpdateChecker checker, IUpdateTicker ticker, Func<DateTime>? utcNow = null)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
        _checker = checker ?? throw new ArgumentNullException(nameof(checker));
        _ticker = ticker ?? throw new ArgumentNullException(nameof(ticker));
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
    }

    /// <summary>Raised on the UI thread whenever the status line would change (check started / finished, prompt shown).</summary>
    public event Action? StatusChanged;

    public UpdateVersion CurrentVersion => _checker.CurrentVersion;
    public bool IsChecking => _checking;
    public bool IsPrompting => _prompting;
    public bool TickerRunning => _ticker.IsRunning;

    /// <summary>A copy of the live memory (tests and the Settings card).</summary>
    public UpdateCheckState Snapshot() { EnsureSeeded(); return _state.Clone(); }

    /// <summary>The status line and the release address for the Settings card.</summary>
    public UpdateStatusInfo GetStatus() { EnsureSeeded(); return UpdateStatusText.Describe(_state, _checker.CurrentVersion, _utcNow(), IsChecking); }

    /// <summary>The validated release page of the latest known version (the repository's latest-release page when unknown).</summary>
    public string ReleaseUrl { get { EnsureSeeded(); return UpdateUrlPolicy.ResolveReleaseUrl(_state.LatestKnownUrl); } }

    public bool UpdateAvailable { get { EnsureSeeded(); return UpdateSchedule.IsUpdateAvailable(_state, _checker.CurrentVersion); } }

    /// <summary>
    /// Copies the live memory into <paramref name="settings"/> (called right before the main window saves, like the window
    /// placement store), so an older in-memory copy can never write back a stale last-check time.
    /// </summary>
    public void ApplyTo(GlideSettingsState settings)
    {
        if (settings is null || !_seeded) return;
        settings.WriteUpdateState(_state);
    }

    private void EnsureSeeded()
    {
        if (_seeded) return;
        if (_host.GetSettings() is { } settings)
        {
            _state = settings.ReadUpdateState();
            _seeded = true;
        }
    }

    /// <summary>Starts the schedule: the first automatic check about 20 seconds from now, if daily checks are on.</summary>
    public void Start()
    {
        _started = true;
        _ticker.Start(UpdateSchedule.StartupDelay, UpdateSchedule.TickInterval, TickAsync);
    }

    /// <summary>
    /// Settings changed (applied or previewed): turning daily checks on starts the timer (a check soon), turning them off stops it
    /// at once. "Don't remind me" is read live by every tick, so it needs no action here.
    /// </summary>
    public void SettingsChanged(GlideSettingsState settings)
    {
        if (!_started) return;
        if (!settings.UpdateCheckDaily)
        {
            if (_ticker.IsRunning) _ticker.Stop();
            return;
        }
        if (!_ticker.IsRunning) _ticker.Start(EnabledCheckDelay, UpdateSchedule.TickInterval, TickAsync);
    }

    /// <summary>One scheduled step: a check when due, then the prompt when it is time and a good moment. Never throws.</summary>
    public async Task TickAsync()
    {
        if (_ticking) return;
        _ticking = true;
        try
        {
            var settings = _host.GetSettings();
            if (settings is null) return;
            EnsureSeeded();
            if (!settings.UpdateCheckDaily)
            {
                // Daily checks are off: no timer is needed until Settings turns them back on (SettingsChanged restarts it).
                if (_ticker.IsRunning) _ticker.Stop();
                return;
            }
            if (UpdateSchedule.IsCheckDue(_state, _utcNow()))
                await RunCheckAsync().ConfigureAwait(true);
            await MaybePromptAsync(settings, force: false).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _host.Log("tick_failed", new { error = ex.GetType().Name });
        }
        finally
        {
            _ticking = false;
        }
    }

    /// <summary>
    /// "Check now" (Settings button, the Check for updates now action): runs a check at once, whether or not daily checks are on.
    /// When <paramref name="promptIfAvailable"/> is set and a newer version exists, the update prompt is shown too (even if prompts
    /// are off, because the user asked). Joins a check that is already running.
    /// </summary>
    public async Task<UpdateCheckOutcome> CheckNowAsync(bool promptIfAvailable = false)
    {
        EnsureSeeded();
        var outcome = await RunCheckAsync().ConfigureAwait(true);
        _lastManualOutcome = outcome.Status;
        if (promptIfAvailable && outcome.Status == UpdateCheckStatus.UpdateAvailable && _host.GetSettings() is { } settings)
            await MaybePromptAsync(settings, force: true).ConfigureAwait(true);
        return outcome;
    }

    /// <summary>The outcome of the last <see cref="CheckNowAsync"/> (diagnostics and tests).</summary>
    public UpdateCheckStatus? LastManualOutcome => _lastManualOutcome;

    private async Task<UpdateCheckOutcome> RunCheckAsync()
    {
        if (_inFlight is { IsCompleted: false } running) return await running.ConfigureAwait(true);
        var task = RunCheckCoreAsync();
        _inFlight = task;
        return await task.ConfigureAwait(true);
    }

    private async Task<UpdateCheckOutcome> RunCheckCoreAsync()
    {
        _checking = true;
        StatusChanged?.Invoke();
        UpdateCheckOutcome outcome;
        try
        {
            // The request itself runs on the thread pool (Task.Run): the UI thread is only touched again to record the answer.
            outcome = await Task.Run(() => _checker.CheckAsync()).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            outcome = new UpdateCheckOutcome(UpdateCheckStatus.Failed, FailureReason: "error:" + ex.GetType().Name);
        }
        finally
        {
            _checking = false;
        }
        if (outcome.Status is UpdateCheckStatus.UpToDate or UpdateCheckStatus.UpdateAvailable or UpdateCheckStatus.Failed)
        {
            _state.Apply(outcome, _utcNow());
            _host.SaveState(_state.Clone());
        }
        _host.Log("check_finished", new { status = outcome.Status.ToString(), reason = outcome.FailureReason });
        StatusChanged?.Invoke();
        return outcome;
    }

    private async Task MaybePromptAsync(GlideSettingsState settings, bool force)
    {
        if (_prompting) return;
        var now = _utcNow();
        var current = _checker.CurrentVersion;
        if (force ? !UpdateSchedule.IsUpdateAvailable(_state, current)
                  : !UpdateSchedule.ShouldPrompt(_state, current, settings.UpdateCheckDaily, settings.UpdateDontRemind, now))
            return;
        if (!force)
        {
            var deferral = _host.GetDeferral();
            if (deferral != UpdatePromptDeferral.None)
            {
                _host.Log("prompt_deferred", new { reason = deferral.ToString() });
                return;
            }
        }

        _prompting = true;
        var before = _state.Clone();
        try
        {
            var latest = _state.LatestKnown!;
            // Recorded before the window opens, so even a crash or a force-quit while it is open does not ask again within the day.
            _state.MarkPrompted(latest.ToString(), now);
            _host.SaveState(_state.Clone());
            StatusChanged?.Invoke();
            var model = new UpdatePromptModel(current.ToString(), latest.ToString(),
                UpdateStatusText.ReleasedAgo(_state.LatestKnownPublishedUtc, now), UpdateUrlPolicy.ResolveReleaseUrl(_state.LatestKnownUrl));
            var result = await _host.ShowPromptAsync(model).ConfigureAwait(true);
            if (result is null)
            {
                // It could not be shown (no visible window): not a prompt that happened, so it is tried again.
                _state.LastPromptedVersion = before.LastPromptedVersion;
                _state.LastPromptedUtc = before.LastPromptedUtc;
                _host.SaveState(_state.Clone());
                _host.Log("prompt_not_shown");
                return;
            }
            if (result.DontRemind) _host.SetDontRemind(true);
            if (result.Action == UpdatePromptAction.ViewOnGitHub) _host.OpenUrl(model.ReleaseUrl);
            _host.Log("prompt_closed", new { action = result.Action.ToString(), dontRemind = result.DontRemind });
        }
        finally
        {
            _prompting = false;
            StatusChanged?.Invoke();
        }
    }
}
