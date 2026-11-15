using Glide.App.Settings;
using Glide.App.Updates;
using Glide.Core.Updates;
using static Glide.Core.Tests.UpdateTestSupport;

namespace Glide.Core.Tests;

/// <summary>
/// The update service end to end on fakes (feed, host, ticker, clock): schedule, failure back-off, prompt rules and deferral,
/// "Don't remind me", live settings, manual Check now, persistence. No window and no network.
/// </summary>
[Collection("UpdateService process-wide state")]
public sealed class UpdateServiceTests
{
    private static string Json(string tag = "v5.0.2", string published = "2026-11-13T12:00:00Z", string? url = "https://github.com/cobhc95/Glide/releases/tag/v5.0.2") =>
        ReleaseJson(tag, url, published);

    // ============================================================ schedule ========

    [Fact]
    public void Start_schedules_the_first_check_20_seconds_out_then_every_minute()
    {
        var rig = new UpdateRig();
        Assert.False(rig.Ticker.IsRunning);
        rig.Service.Start();
        Assert.True(rig.Ticker.IsRunning);
        Assert.Equal(TimeSpan.FromSeconds(20), rig.Ticker.FirstDelay);
        Assert.Equal(TimeSpan.FromMinutes(1), rig.Ticker.Interval);
        Assert.Equal(0, rig.Feed.Calls);   // nothing is requested by starting
    }

    [Fact]
    public async Task The_first_tick_checks_once_and_a_second_tick_the_same_day_does_not()
    {
        var rig = new UpdateRig();
        rig.Feed.ThenJson(Json("v5.0.1"));
        await rig.Service.TickAsync();
        Assert.Equal(1, rig.Feed.Calls);
        rig.Now = rig.Now.AddMinutes(1);
        await rig.Service.TickAsync();
        rig.Now = rig.Now.AddHours(23);
        await rig.Service.TickAsync();
        Assert.Equal(1, rig.Feed.Calls);
        Assert.Empty(rig.Host.Prompts);
        Assert.Equal(T0, rig.Service.Snapshot().LastCheckedUtc);
    }

    [Fact]
    public async Task A_long_running_instance_checks_again_after_24_hours()
    {
        var rig = new UpdateRig();
        rig.Feed.ThenJson(Json("v5.0.1"));
        await rig.Service.TickAsync();
        rig.Now = rig.Now.AddHours(24).AddMinutes(1);
        await rig.Service.TickAsync();
        Assert.Equal(2, rig.Feed.Calls);
        Assert.Equal(rig.Now, rig.Service.Snapshot().LastCheckedUtc);
    }

    [Fact]
    public async Task Offline_is_silent_and_retried_after_the_back_off_not_in_a_loop()
    {
        var rig = new UpdateRig();
        rig.Feed.ThenFail();
        for (var i = 0; i < 20; i++)
        {
            await rig.Service.TickAsync();
            rig.Now = rig.Now.AddMinutes(1);
        }
        Assert.Equal(1, rig.Feed.Calls);                 // 20 minutes of ticks: one attempt
        Assert.Empty(rig.Host.Prompts);                  // silent
        Assert.True(rig.Service.Snapshot().LastAttemptFailed);
        Assert.Null(rig.Service.Snapshot().LastCheckedUtc);   // the day is not marked as checked

        rig.Now = T0.AddHours(5);
        await rig.Service.TickAsync();
        Assert.Equal(1, rig.Feed.Calls);
        rig.Now = T0.AddHours(6).AddMinutes(1);
        rig.Feed.ThenJson(Json());
        await rig.Service.TickAsync();
        Assert.Equal(2, rig.Feed.Calls);
        Assert.False(rig.Service.Snapshot().LastAttemptFailed);
        Assert.NotNull(rig.Service.Snapshot().LastCheckedUtc);
    }

    [Fact]
    public async Task A_bad_answer_is_treated_like_offline()
    {
        var rig = new UpdateRig();
        rig.Feed.ThenJson("not json");
        await rig.Service.TickAsync();
        var state = rig.Service.Snapshot();
        Assert.True(state.LastAttemptFailed);
        Assert.Equal("Couldn't reach GitHub", rig.Service.GetStatus().ResultLine);
        Assert.Empty(rig.Host.Prompts);
    }

    [Fact]
    public async Task Every_finished_check_is_persisted_without_touching_other_settings()
    {
        var rig = new UpdateRig();
        rig.Feed.ThenJson(Json());
        await rig.Service.TickAsync();
        Assert.Contains(rig.Host.Saved, s => s.LastCheckedUtc == T0 && s.LatestKnownVersion == "5.0.2");
    }

    [Fact]
    public async Task A_state_from_the_settings_file_is_honoured_within_24_hours()
    {
        var rig = new UpdateRig();
        rig.Host.Settings!.UpdateLastCheckedUtc = T0.AddHours(-3);
        rig.Host.Settings.UpdateLatestKnownVersion = "5.0.1";
        await rig.Service.TickAsync();
        Assert.Equal(0, rig.Feed.Calls);
        Assert.Equal("You're up to date (5.0.1)", rig.Service.GetStatus().ResultLine);
    }

    [Fact]
    public async Task The_tick_waits_while_the_start_up_settings_are_still_loading()
    {
        var rig = new UpdateRig();
        rig.Host.Settings = null;
        await rig.Service.TickAsync();
        Assert.Equal(0, rig.Feed.Calls);
        rig.Host.Settings = new GlideSettingsState();
        rig.Feed.ThenJson(Json("v5.0.1"));
        await rig.Service.TickAsync();
        Assert.Equal(1, rig.Feed.Calls);
    }

    // ============================================================ prompt ========

    [Fact]
    public async Task A_newer_version_opens_the_prompt_once_with_the_right_content()
    {
        var rig = new UpdateRig();
        rig.Now = new DateTime(2026, 11, 15, 12, 0, 0, DateTimeKind.Utc);
        rig.Feed.ThenJson(Json(published: "2026-11-13T12:00:00Z"));
        await rig.Service.TickAsync();
        var prompt = Assert.Single(rig.Host.Prompts);
        Assert.Equal("5.0.1", prompt.CurrentVersion);
        Assert.Equal("5.0.2", prompt.LatestVersion);
        Assert.Equal("Released 2 days ago on GitHub", prompt.ReleasedText);
        Assert.Equal("https://github.com/cobhc95/Glide/releases/tag/v5.0.2", prompt.ReleaseUrl);
        Assert.Empty(rig.Host.Opened);        // Not now: nothing is opened
        Assert.Empty(rig.Host.DontRemindSet);

        // Shown once per day: more ticks the same day stay quiet, even though the user pressed Not now.
        for (var i = 0; i < 5; i++) { rig.Now = rig.Now.AddMinutes(30); await rig.Service.TickAsync(); }
        Assert.Single(rig.Host.Prompts);
    }

    [Fact]
    public async Task The_prompt_is_recorded_before_it_opens_so_a_crash_cannot_repeat_it()
    {
        var rig = new UpdateRig();
        rig.Feed.ThenJson(Json());
        UpdateCheckState? duringPrompt = null;
        rig.Host.OnPrompt = _ => { duringPrompt = rig.Host.Saved.Last(); return Task.FromResult<UpdatePromptResult?>(new UpdatePromptResult(UpdatePromptAction.NotNow, false)); };
        await rig.Service.TickAsync();
        Assert.NotNull(duringPrompt);
        Assert.Equal("5.0.2", duringPrompt!.LastPromptedVersion);
        Assert.Equal(T0, duringPrompt.LastPromptedUtc);
    }

    [Fact]
    public async Task Not_now_asks_again_after_the_next_day_and_a_new_check()
    {
        var rig = new UpdateRig();
        rig.Feed.ThenJson(Json());
        await rig.Service.TickAsync();
        Assert.Single(rig.Host.Prompts);

        rig.Now = T0.AddHours(21);            // not 24 h yet: no new request, but a day-old prompt may repeat
        await rig.Service.TickAsync();
        Assert.Equal(1, rig.Feed.Calls);
        Assert.Equal(2, rig.Host.Prompts.Count);

        rig.Now = T0.AddHours(46);            // the next daily check runs and the prompt follows
        await rig.Service.TickAsync();
        Assert.Equal(2, rig.Feed.Calls);
        Assert.Equal(3, rig.Host.Prompts.Count);
    }

    [Fact]
    public async Task View_on_GitHub_opens_the_validated_release_page_and_nothing_else()
    {
        var rig = new UpdateRig();
        rig.Feed.ThenJson(Json());
        rig.Host.OnPrompt = _ => Task.FromResult<UpdatePromptResult?>(new UpdatePromptResult(UpdatePromptAction.ViewOnGitHub, false));
        await rig.Service.TickAsync();
        Assert.Equal(new[] { "https://github.com/cobhc95/Glide/releases/tag/v5.0.2" }, rig.Host.Opened);
        Assert.Empty(rig.Host.DontRemindSet);
    }

    [Theory]
    [InlineData("https://evil.com/cobhc95/Glide/releases/tag/v5.0.2")]
    [InlineData("http://github.com/cobhc95/Glide/releases/tag/v5.0.2")]
    [InlineData("https://github.com.evil.com/cobhc95/Glide/releases/tag/v5.0.2")]
    [InlineData("https://github.com/someone/else/releases")]
    public async Task A_hostile_release_address_opens_the_repository_page_instead(string hostile)
    {
        var rig = new UpdateRig();
        rig.Feed.ThenJson(Json(url: hostile));
        rig.Host.OnPrompt = _ => Task.FromResult<UpdatePromptResult?>(new UpdatePromptResult(UpdatePromptAction.ViewOnGitHub, false));
        await rig.Service.TickAsync();
        Assert.Equal(new[] { UpdateUrlPolicy.FallbackReleaseUrl }, rig.Host.Opened);
        Assert.Equal(UpdateUrlPolicy.FallbackReleaseUrl, rig.Service.ReleaseUrl);
    }

    [Fact]
    public async Task A_missing_release_address_opens_the_repository_page()
    {
        var rig = new UpdateRig();
        rig.Feed.ThenJson(Json(url: null));
        rig.Host.OnPrompt = _ => Task.FromResult<UpdatePromptResult?>(new UpdatePromptResult(UpdatePromptAction.ViewOnGitHub, false));
        await rig.Service.TickAsync();
        Assert.Equal(new[] { UpdateUrlPolicy.FallbackReleaseUrl }, rig.Host.Opened);
    }

    [Theory]
    [InlineData(UpdatePromptAction.NotNow)]
    [InlineData(UpdatePromptAction.ViewOnGitHub)]
    public async Task Dont_remind_me_again_turns_the_setting_on_with_either_button_and_ends_the_prompts(UpdatePromptAction action)
    {
        var rig = new UpdateRig();
        rig.Feed.ThenJson(Json());
        rig.Host.OnPrompt = _ => Task.FromResult<UpdatePromptResult?>(new UpdatePromptResult(action, true));
        await rig.Service.TickAsync();
        Assert.Equal(new[] { true }, rig.Host.DontRemindSet);
        Assert.True(rig.Host.Settings!.UpdateDontRemind);

        // Forever: even days later, with new checks, no more prompts; the status still shows the update.
        rig.Now = T0.AddDays(3);
        await rig.Service.TickAsync();
        rig.Now = T0.AddDays(10);
        await rig.Service.TickAsync();
        Assert.Single(rig.Host.Prompts);
        Assert.True(rig.Feed.Calls >= 2);                    // checking goes on (the card stays current)
        Assert.True(rig.Service.GetStatus().UpdateAvailable);
        Assert.Equal("Glide 5.0.2 is available", rig.Service.GetStatus().ResultLine);
    }

    [Fact]
    public async Task With_prompts_off_from_the_start_there_is_never_a_window()
    {
        var rig = new UpdateRig();
        rig.Host.Settings!.UpdateDontRemind = true;
        rig.Feed.ThenJson(Json());
        await rig.Service.TickAsync();
        Assert.Empty(rig.Host.Prompts);
        Assert.Equal(1, rig.Feed.Calls);
        Assert.True(rig.Service.UpdateAvailable);
    }

    [Fact]
    public async Task Turning_dont_remind_off_again_brings_the_prompt_back()
    {
        var rig = new UpdateRig();
        rig.Host.Settings!.UpdateDontRemind = true;
        rig.Feed.ThenJson(Json());
        await rig.Service.TickAsync();
        Assert.Empty(rig.Host.Prompts);
        rig.Host.Settings.UpdateDontRemind = false;        // live: read at every tick
        rig.Now = rig.Now.AddMinutes(1);
        await rig.Service.TickAsync();
        Assert.Single(rig.Host.Prompts);
    }

    [Theory]
    [InlineData(UpdatePromptDeferral.Fullscreen)]
    [InlineData(UpdatePromptDeferral.Slideshow)]
    [InlineData(UpdatePromptDeferral.ModalDialog)]
    [InlineData(UpdatePromptDeferral.Typing)]
    [InlineData(UpdatePromptDeferral.NoWindow)]
    public async Task The_prompt_waits_for_a_good_moment_and_is_not_lost(UpdatePromptDeferral reason)
    {
        var rig = new UpdateRig();
        rig.Feed.ThenJson(Json());
        rig.Host.Deferral = reason;
        for (var i = 0; i < 4; i++) { await rig.Service.TickAsync(); rig.Now = rig.Now.AddMinutes(1); }
        Assert.Empty(rig.Host.Prompts);
        Assert.Contains("prompt_deferred", rig.Host.Logs);
        Assert.Equal(string.Empty, rig.Service.Snapshot().LastPromptedVersion);     // deferral is not a prompt

        rig.Host.Deferral = UpdatePromptDeferral.None;
        await rig.Service.TickAsync();
        Assert.Single(rig.Host.Prompts);
        Assert.Equal(1, rig.Feed.Calls);                                            // the wait never repeated the request
    }

    [Fact]
    public async Task A_prompt_that_could_not_be_shown_is_tried_again()
    {
        var rig = new UpdateRig();
        rig.Feed.ThenJson(Json());
        rig.Host.OnPrompt = _ => Task.FromResult<UpdatePromptResult?>(null);
        await rig.Service.TickAsync();
        Assert.Single(rig.Host.Prompts);
        Assert.Equal(string.Empty, rig.Service.Snapshot().LastPromptedVersion);
        rig.Host.OnPrompt = _ => Task.FromResult<UpdatePromptResult?>(new UpdatePromptResult(UpdatePromptAction.NotNow, false));
        rig.Now = rig.Now.AddMinutes(1);
        await rig.Service.TickAsync();
        Assert.Equal(2, rig.Host.Prompts.Count);
    }

    [Fact]
    public async Task Only_one_prompt_is_open_at_a_time()
    {
        var rig = new UpdateRig();
        rig.Feed.ThenJson(Json());
        var release = new TaskCompletionSource<UpdatePromptResult?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.Host.OnPrompt = _ => { entered.TrySetResult(true); return release.Task; };
        var first = rig.Service.TickAsync();
        await entered.Task;
        // While the first prompt is open (another window's timer fires, or the user asks for a check): nothing more is shown.
        await rig.Service.TickAsync();
        await rig.Service.CheckNowAsync(promptIfAvailable: true);
        Assert.Single(rig.Host.Prompts);
        Assert.True(rig.Service.IsPrompting);
        release.SetResult(new UpdatePromptResult(UpdatePromptAction.NotNow, false));
        await first;
        Assert.False(rig.Service.IsPrompting);
    }

    [Fact]
    public async Task No_prompt_when_the_running_version_is_already_the_latest()
    {
        var rig = new UpdateRig("5.0.2");
        rig.Feed.ThenJson(Json("v5.0.2"));
        await rig.Service.TickAsync();
        Assert.Empty(rig.Host.Prompts);
        Assert.Equal("You're up to date (5.0.2)", rig.Service.GetStatus().ResultLine);
    }

    // ============================================================ live settings ========

    [Fact]
    public async Task With_daily_checks_off_a_tick_does_nothing()
    {
        var rig = new UpdateRig();
        rig.Host.Settings!.UpdateCheckDaily = false;
        rig.Feed.ThenJson(Json());
        await rig.Service.TickAsync();
        Assert.Equal(0, rig.Feed.Calls);
        Assert.Empty(rig.Host.Prompts);
    }

    [Fact]
    public void Turning_daily_checks_off_stops_the_timer_and_on_starts_it_soon()
    {
        var rig = new UpdateRig();
        rig.Service.Start();
        Assert.True(rig.Ticker.IsRunning);

        rig.Service.SettingsChanged(new GlideSettingsState { UpdateCheckDaily = false });
        Assert.False(rig.Ticker.IsRunning);
        Assert.Equal(1, rig.Ticker.Stops);

        rig.Service.SettingsChanged(new GlideSettingsState { UpdateCheckDaily = true });
        Assert.True(rig.Ticker.IsRunning);
        Assert.Equal(UpdateService.EnabledCheckDelay, rig.Ticker.FirstDelay);
        Assert.True(rig.Ticker.FirstDelay <= TimeSpan.FromSeconds(10));
        Assert.Equal(2, rig.Ticker.Starts);

        // Applying unchanged settings does not restart the timer.
        rig.Service.SettingsChanged(new GlideSettingsState { UpdateCheckDaily = true });
        Assert.Equal(2, rig.Ticker.Starts);
    }

    [Fact]
    public void Settings_changes_before_start_up_do_not_start_a_timer()
    {
        var rig = new UpdateRig();
        rig.Service.SettingsChanged(new GlideSettingsState { UpdateCheckDaily = true });
        Assert.False(rig.Ticker.IsRunning);
    }

    [Fact]
    public async Task Enabling_daily_checks_in_settings_leads_to_a_check_on_the_next_tick()
    {
        var rig = new UpdateRig();
        rig.Host.Settings!.UpdateCheckDaily = false;
        rig.Service.Start();
        rig.Feed.ThenJson(Json("v5.0.1"));
        await rig.Service.TickAsync();
        Assert.Equal(0, rig.Feed.Calls);
        Assert.False(rig.Ticker.IsRunning);                  // nothing to do while it is off: the timer stops itself
        rig.Host.Settings.UpdateCheckDaily = true;
        rig.Service.SettingsChanged(rig.Host.Settings);
        await rig.Ticker.Tick!();
        Assert.Equal(1, rig.Feed.Calls);
    }

    // ============================================================ Check now ========

    [Fact]
    public async Task Check_now_works_with_daily_checks_off_and_updates_the_status()
    {
        var rig = new UpdateRig();
        rig.Host.Settings!.UpdateCheckDaily = false;
        rig.Feed.ThenJson(Json());
        var raised = 0;
        rig.Service.StatusChanged += () => raised++;
        var outcome = await rig.Service.CheckNowAsync();
        Assert.Equal(UpdateCheckStatus.UpdateAvailable, outcome.Status);
        Assert.Equal(1, rig.Feed.Calls);
        Assert.Empty(rig.Host.Prompts);                    // the Settings card shows it; no window unless asked for
        Assert.True(raised >= 2);                          // "Checking..." then the result
        Assert.Equal("Glide 5.0.2 is available", rig.Service.GetStatus().ResultLine);
        Assert.Equal(UpdateCheckStatus.UpdateAvailable, rig.Service.LastManualOutcome);
    }

    [Fact]
    public async Task Check_now_shows_checking_while_it_runs()
    {
        var rig = new UpdateRig();
        rig.Feed.Hold = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.Feed.ThenJson(Json("v5.0.1"));
        var states = new List<UpdateStatusKind>();
        rig.Service.StatusChanged += () => states.Add(rig.Service.GetStatus().Kind);
        var running = rig.Service.CheckNowAsync();
        await rig.Feed.Entered.Task;
        Assert.True(rig.Service.IsChecking);
        Assert.Equal(UpdateStatusKind.Checking, rig.Service.GetStatus().Kind);
        rig.Feed.Hold.SetResult(true);
        await running;
        Assert.False(rig.Service.IsChecking);
        Assert.Equal(UpdateStatusKind.Checking, states.First());
        Assert.Equal(UpdateStatusKind.UpToDate, states.Last());
    }

    [Fact]
    public async Task Check_now_while_a_check_runs_joins_it_instead_of_asking_twice()
    {
        var rig = new UpdateRig();
        rig.Feed.Hold = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.Feed.ThenJson(Json("v5.0.1"));
        var tick = rig.Service.TickAsync();
        await rig.Feed.Entered.Task;
        var manual = rig.Service.CheckNowAsync();
        rig.Feed.Hold.SetResult(true);
        await Task.WhenAll(tick, manual);
        Assert.Equal(1, rig.Feed.Calls);
        Assert.Equal(UpdateCheckStatus.UpToDate, (await manual).Status);
    }

    [Fact]
    public async Task Check_now_can_open_the_prompt_even_when_prompts_are_off_because_the_user_asked()
    {
        var rig = new UpdateRig();
        rig.Host.Settings!.UpdateDontRemind = true;
        rig.Host.Deferral = UpdatePromptDeferral.Typing;     // an explicit request is not deferred
        rig.Feed.ThenJson(Json());
        await rig.Service.CheckNowAsync(promptIfAvailable: true);
        Assert.Single(rig.Host.Prompts);
    }

    [Fact]
    public async Task Check_now_reports_failure_and_up_to_date_and_never_prompts_for_them()
    {
        var rig = new UpdateRig();
        rig.Feed.ThenFail();
        Assert.Equal(UpdateCheckStatus.Failed, (await rig.Service.CheckNowAsync(promptIfAvailable: true)).Status);
        Assert.Equal("Couldn't reach GitHub", rig.Service.GetStatus().ResultLine);
        rig.Feed.ThenJson(Json("v5.0.1"));
        Assert.Equal(UpdateCheckStatus.UpToDate, (await rig.Service.CheckNowAsync(promptIfAvailable: true)).Status);
        Assert.Equal("You're up to date (5.0.1)", rig.Service.GetStatus().ResultLine);
        Assert.Empty(rig.Host.Prompts);
    }

    [Fact]
    public async Task Another_window_or_process_checking_means_busy_not_a_second_request()
    {
        var gate = new SemaphoreSlim(1, 1);
        var slow = new FakeUpdateFeed { Hold = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously) }.ThenJson(Json("v5.0.1"));
        var a = new UpdateService(new FakeUpdateHost(), new UpdateChecker(slow, V("5.0.1"), gate), new FakeTicker(), () => T0);
        var otherFeed = new FakeUpdateFeed().ThenJson(Json("v5.0.1"));
        var b = new UpdateService(new FakeUpdateHost(), new UpdateChecker(otherFeed, V("5.0.1"), gate), new FakeTicker(), () => T0);
        var first = a.CheckNowAsync();
        await slow.Entered.Task;
        var second = await b.CheckNowAsync();
        Assert.Equal(UpdateCheckStatus.Busy, second.Status);
        Assert.Equal(0, otherFeed.Calls);
        slow.Hold!.SetResult(true);
        await first;
    }

    // ============================================================ memory in the settings ========

    [Fact]
    public async Task The_live_memory_is_copied_into_settings_before_a_full_save()
    {
        var rig = new UpdateRig();
        var settings = new GlideSettingsState();
        rig.Service.ApplyTo(settings);                       // not seeded yet: nothing to copy, nothing overwritten
        Assert.Null(settings.UpdateLastCheckedUtc);

        rig.Feed.ThenJson(Json());
        await rig.Service.TickAsync();
        rig.Service.ApplyTo(settings);
        Assert.Equal(T0, settings.UpdateLastCheckedUtc);
        Assert.Equal("5.0.2", settings.UpdateLatestKnownVersion);
        Assert.Equal("5.0.2", settings.UpdateLastPromptedVersion);

        // A stale snapshot (an open Settings window) cannot take it back.
        var stale = new GlideSettingsState { UpdateCheckDaily = false };
        rig.Service.ApplyTo(stale);
        Assert.Equal(T0, stale.UpdateLastCheckedUtc);
        Assert.False(stale.UpdateCheckDaily);                // user settings are never touched
    }

    [Fact]
    public async Task A_service_exception_never_escapes_a_tick()
    {
        var rig = new UpdateRig();
        rig.Host.OnPrompt = _ => throw new InvalidOperationException("window failed");
        rig.Feed.ThenJson(Json());
        await rig.Service.TickAsync();                       // must not throw
        Assert.Contains("tick_failed", rig.Host.Logs);
        Assert.False(rig.Service.IsPrompting);
        await rig.Service.TickAsync();                       // and the service keeps working
    }

    [Fact]
    public void Install_replaces_and_clears_the_process_wide_service()
    {
        var before = UpdateService.Current;
        try
        {
            var rig = new UpdateRig();
            UpdateService.Install(rig.Service);
            Assert.Same(rig.Service, UpdateService.Current);
            UpdateService.Install(null);
            Assert.Null(UpdateService.Current);
        }
        finally { UpdateService.Install(before); }
    }
}
