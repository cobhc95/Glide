using Glide.Core.Updates;
using static Glide.Core.Tests.UpdateTestSupport;

namespace Glide.Core.Tests;

/// <summary>Pure logic of the update checker (Glide.Core.Updates): versions, release parsing, URL validation, schedule, prompt rules, status text.</summary>
[Trait("Area", "Update")]
public sealed class UpdateLogicTests
{
    // ================================================================ version comparison ========

    [Theory]
    [InlineData("5.0.1", "5.0.2", -1)]
    [InlineData("5.0.2", "5.0.1", 1)]
    [InlineData("5.0.2", "5.0.10", -1)]   // numeric, not text
    [InlineData("5.0.10", "5.0.2", 1)]
    [InlineData("5.0.1", "5.1.0", -1)]
    [InlineData("5.9.9", "6.0.0", -1)]
    [InlineData("5.0.1", "5.0.1", 0)]
    [InlineData("5.0.1", "v5.0.1", 0)]
    [InlineData("5.0.1", "V5.0.1", 0)]
    [InlineData("5.0.1", "5.0.1.0", 0)]   // missing parts are 0
    [InlineData("5.0", "5.0.0", 0)]
    [InlineData("5", "5.0.0.0", 0)]
    [InlineData("5.0.1.1", "5.0.1", 1)]   // 4-part versions
    [InlineData("5.0.1.2", "5.0.1.10", -1)]
    [InlineData("5.0.1-beta", "5.0.1", -1)]
    [InlineData("5.0.1+build7", "5.0.1", 0)]
    [InlineData("4.9.9", "5.0.0", -1)]
    public void Versions_compare_numerically(string left, string right, int expected)
    {
        Assert.Equal(expected, Math.Sign(V(left).CompareTo(V(right))));
        Assert.Equal(-expected, Math.Sign(V(right).CompareTo(V(left))));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("latest")]
    [InlineData("v")]
    [InlineData("v.")]
    [InlineData("5.")]
    [InlineData(".5")]
    [InlineData("5..1")]
    [InlineData("5.0.1.2.3")]            // five parts
    [InlineData("5.0.x")]
    [InlineData("-1.0.0")]
    [InlineData("5.-1.0")]
    [InlineData("5.0.1-")]
    [InlineData("99999999.0.0")]         // too large
    [InlineData("5.0.1; rm -rf")]
    [InlineData("５.０.１")]               // full-width digits are not digits here
    [InlineData("5.0.1 beta")]
    public void Junk_versions_do_not_parse(string text)
    {
        Assert.False(UpdateVersion.TryParse(text, out _));
        Assert.Null(UpdateVersion.ParseOrNull(text));
    }

    [Fact]
    public void Null_does_not_parse_and_versions_print_without_a_revision_when_it_is_zero()
    {
        Assert.False(UpdateVersion.TryParse(null, out _));
        Assert.Equal("5.0.1", V("v5.0.1").ToString());
        Assert.Equal("5.0.1.2", V("5.0.1.2").ToString());
        Assert.Equal("5.0.0", V("5").ToString());
        Assert.True(V("5.0.2-rc.1").IsPreRelease);
        Assert.False(V("5.0.2").IsPreRelease);
    }

    [Fact]
    public void The_running_version_of_this_build_parses()
    {
        Assert.True(UpdateVersion.TryParse(AppVersion.Short, out var running));
        Assert.True(running >= V("5.0.0"));
    }

    // ================================================================ response parsing ========

    [Fact]
    public void A_valid_release_parses_all_fields()
    {
        var result = UpdateReleaseParser.Parse(ReleaseJson());
        Assert.True(result.Ok);
        var release = result.Release!;
        Assert.Equal("v5.0.2", release.Tag);
        Assert.Equal(V("5.0.2"), release.Version);
        Assert.Equal("https://github.com/cobhc95/Glide/releases/tag/v5.0.2", release.HtmlUrl);
        Assert.Equal(new DateTimeOffset(2026, 11, 13, 10, 0, 0, TimeSpan.Zero), release.PublishedAt);
        Assert.False(release.IsDraft);
        Assert.False(release.IsPreRelease);
        Assert.True(release.IsStable);
    }

    [Fact]
    public void Prerelease_and_draft_flags_make_a_release_unstable()
    {
        var pre = UpdateReleaseParser.Parse(ReleaseJson(prerelease: true)).Release!;
        Assert.True(pre.IsPreRelease);
        Assert.False(pre.IsStable);
        var draft = UpdateReleaseParser.Parse(ReleaseJson(draft: true)).Release!;
        Assert.True(draft.IsDraft);
        Assert.False(draft.IsStable);
        // A pre-release tag is unstable even when GitHub forgot the flag.
        Assert.False(UpdateReleaseParser.Parse(ReleaseJson(tag: "v5.0.2-beta.1")).Release!.IsStable);
    }

    [Fact]
    public void Missing_optional_fields_still_parse()
    {
        var onlyTag = UpdateReleaseParser.Parse("{\"tag_name\":\"v5.0.2\"}");
        Assert.True(onlyTag.Ok);
        Assert.Null(onlyTag.Release!.HtmlUrl);
        Assert.Null(onlyTag.Release.PublishedAt);
        Assert.True(onlyTag.Release.IsStable);
        Assert.Null(UpdateReleaseParser.Parse(ReleaseJson(published: "not a date")).Release!.PublishedAt);
    }

    [Theory]
    [InlineData(null, UpdateParseStatus.Empty)]
    [InlineData("", UpdateParseStatus.Empty)]
    [InlineData("   ", UpdateParseStatus.Empty)]
    [InlineData("{", UpdateParseStatus.BadJson)]
    [InlineData("not json", UpdateParseStatus.BadJson)]
    [InlineData("[]", UpdateParseStatus.BadJson)]
    [InlineData("null", UpdateParseStatus.BadJson)]
    [InlineData("{\"tag_name\":5}", UpdateParseStatus.BadJson)]
    [InlineData("{\"tag_name\":\"v5.0.2\",\"draft\":\"yes\"}", UpdateParseStatus.BadJson)]
    [InlineData("{}", UpdateParseStatus.MissingTag)]
    [InlineData("{\"tag_name\":\"\"}", UpdateParseStatus.MissingTag)]
    [InlineData("{\"tag_name\":null}", UpdateParseStatus.MissingTag)]
    [InlineData("{\"tag_name\":\"nightly\"}", UpdateParseStatus.BadVersion)]
    [InlineData("{\"tag_name\":\"v5.0.2.1.1\"}", UpdateParseStatus.BadVersion)]
    public void Unusable_answers_are_rejected(string? json, UpdateParseStatus expected)
    {
        var result = UpdateReleaseParser.Parse(json);
        Assert.False(result.Ok);
        Assert.Equal(expected, result.Status);
        Assert.Null(result.Release);
    }

    [Fact]
    public void An_oversized_answer_is_rejected_unread()
    {
        var huge = "{\"tag_name\":\"v5.0.2\",\"body\":\"" + new string('x', UpdateReleaseParser.MaxResponseBytes) + "\"}";
        Assert.Equal(UpdateParseStatus.TooLarge, UpdateReleaseParser.Parse(huge).Status);
        Assert.Equal(256 * 1024, UpdateReleaseParser.MaxResponseBytes);
    }

    [Fact]
    public void Unknown_fields_and_big_but_legal_answers_are_fine()
    {
        var body = new string('x', 100_000);
        var result = UpdateReleaseParser.Parse("{\"tag_name\":\"v5.0.2\",\"body\":\"" + body + "\",\"author\":{\"login\":\"x\"},\"future\":[1,2,3]}");
        Assert.True(result.Ok);
    }

    // ================================================================ evaluation ========

    [Theory]
    [InlineData("v5.0.2", false, false, "5.0.1", true)]
    [InlineData("v5.0.10", false, false, "5.0.2", true)]
    [InlineData("v5.1.0", false, false, "5.0.1", true)]
    [InlineData("v6.0.0", false, false, "5.9.9", true)]
    [InlineData("v5.0.1", false, false, "5.0.1", false)]   // equal
    [InlineData("v5.0.0", false, false, "5.0.1", false)]   // older
    [InlineData("v5.0.2", true, false, "5.0.1", false)]    // draft
    [InlineData("v5.0.2", false, true, "5.0.1", false)]    // pre-release
    [InlineData("v5.0.2-rc.1", false, false, "5.0.1", false)]
    [InlineData("v5.0.1.1", false, false, "5.0.1", true)]  // 4-part
    public void Only_a_newer_stable_release_is_an_update(string tag, bool draft, bool pre, string running, bool expected)
    {
        var release = UpdateReleaseParser.Parse(ReleaseJson(tag: tag, draft: draft, prerelease: pre)).Release!;
        var outcome = UpdateChecker.Evaluate(release, V(running));
        Assert.Equal(expected ? UpdateCheckStatus.UpdateAvailable : UpdateCheckStatus.UpToDate, outcome.Status);
    }

    // ================================================================ URL validation ========

    [Theory]
    [InlineData("https://github.com/cobhc95/Glide/releases/tag/v5.0.2")]
    [InlineData("https://github.com/cobhc95/Glide/releases/latest")]
    [InlineData("https://GitHub.com/cobhc95/Glide/releases/tag/v5.0.2")]
    [InlineData("https://github.com/cobhc95/glide/releases/tag/v5.0.2")]
    [InlineData("https://github.com:443/cobhc95/Glide/releases/tag/v5.0.2")]
    [InlineData("https://github.com/cobhc95/Glide/releases/tag/v5.0.2?x=1#top")]
    public void A_release_page_of_the_repository_is_accepted(string url)
    {
        Assert.True(UpdateUrlPolicy.IsSafeReleaseUrl(url, out var uri));
        Assert.Equal("github.com", uri.Host);
        Assert.StartsWith("https://github.com/", UpdateUrlPolicy.ResolveReleaseUrl(url), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("http://github.com/cobhc95/Glide/releases/tag/v5.0.2")]                 // not https
    [InlineData("ftp://github.com/cobhc95/Glide/releases/tag/v5.0.2")]
    [InlineData("file:///C:/Windows/System32/calc.exe")]
    [InlineData("javascript:alert(1)")]
    [InlineData("https://evil.com/cobhc95/Glide/releases/tag/v5.0.2")]                  // wrong host
    [InlineData("https://github.com.evil.com/cobhc95/Glide/releases/tag/v5.0.2")]       // look-alike
    [InlineData("https://evilgithub.com/cobhc95/Glide/releases/tag/v5.0.2")]
    [InlineData("https://notgithub.com/cobhc95/Glide/releases/tag/v5.0.2")]
    [InlineData("https://api.github.com/cobhc95/Glide/releases/tag/v5.0.2")]           // another github host
    [InlineData("https://www.github.com/cobhc95/Glide/releases/tag/v5.0.2")]
    [InlineData("https://github.com@evil.com/cobhc95/Glide/releases/tag/v5.0.2")]       // user-info trick
    [InlineData("https://github.com:x@evil.com/cobhc95/Glide/")]
    [InlineData("https://user:pass@github.com/cobhc95/Glide/releases/tag/v5.0.2")]
    [InlineData("https://evil.com#@github.com/cobhc95/Glide/releases/tag/v5.0.2")]
    [InlineData("https://evil.com/@github.com/cobhc95/Glide/releases/tag/v5.0.2")]
    [InlineData("https://github.com:8443/cobhc95/Glide/releases/tag/v5.0.2")]           // port
    [InlineData("https://github.com/otheruser/Glide/releases/tag/v5.0.2")]              // path outside the repo
    [InlineData("https://github.com/cobhc95/Other/releases/tag/v5.0.2")]
    [InlineData("https://github.com/cobhc95/Glide")]                                    // no trailing slash: not inside
    [InlineData("https://github.com/cobhc95/GlideEvil/releases")]
    [InlineData("https://github.com/cobhc95/Glide/../evil/x")]                          // traversal
    [InlineData("https://github.com/cobhc95/Glide/%2e%2e/evil/x")]
    [InlineData("https://github.com/cobhc95/Glide/..%2fevil")]
    [InlineData("https://github.com\\@evil.com/cobhc95/Glide/")]
    [InlineData("https://github.com/cobhc95/Glide/releases tag")]                       // whitespace
    [InlineData("https://github.com/cobhc95/Glide/releases/\r\nHost: evil.com")]        // header injection
    [InlineData("//github.com/cobhc95/Glide/releases")]                                 // relative
    [InlineData("github.com/cobhc95/Glide/releases")]
    [InlineData("/cobhc95/Glide/releases")]
    [InlineData("")]
    [InlineData(null)]
    public void Anything_else_falls_back_to_the_repository_page(string? url)
    {
        Assert.False(UpdateUrlPolicy.IsSafeReleaseUrl(url, out _));
        Assert.Equal(UpdateUrlPolicy.FallbackReleaseUrl, UpdateUrlPolicy.ResolveReleaseUrl(url));
        Assert.Equal("https://github.com/cobhc95/Glide/releases/latest", UpdateUrlPolicy.FallbackReleaseUrl);
    }

    [Fact]
    public void An_overlong_address_is_rejected()
    {
        Assert.False(UpdateUrlPolicy.IsSafeReleaseUrl("https://github.com/cobhc95/Glide/" + new string('a', 3000), out _));
    }

    [Theory]
    [InlineData("https://api.github.com/repos/cobhc95/Glide/releases/latest", true)]
    [InlineData("https://github.com/cobhc95/Glide/releases/latest", true)]
    [InlineData("http://api.github.com/x", false)]
    [InlineData("https://evil.com/x", false)]
    [InlineData("https://api.github.com.evil.com/x", false)]
    [InlineData("https://user@api.github.com/x", false)]
    [InlineData("https://api.github.com:444/x", false)]
    [InlineData("https://objects.githubusercontent.com/x", false)]
    public void Redirects_are_followed_only_to_https_on_the_github_hosts(string url, bool allowed)
    {
        Assert.Equal(allowed, UpdateUrlPolicy.IsAllowedRedirect(new Uri(url)));
        Assert.False(UpdateUrlPolicy.IsAllowedRedirect(null));
        Assert.False(UpdateUrlPolicy.IsAllowedRedirect(new Uri("/relative", UriKind.Relative)));
        Assert.Equal("https://api.github.com/repos/cobhc95/Glide/releases/latest", UpdateUrlPolicy.LatestReleaseApiUrl);
    }

    // ================================================================ schedule ========

    private static UpdateCheckState Checked(DateTime at, bool failed = false, DateTime? attempt = null) =>
        new() { LastCheckedUtc = at, LastAttemptUtc = attempt ?? at, LastAttemptFailed = failed };

    [Fact]
    public void The_first_run_is_due()
    {
        Assert.True(UpdateSchedule.IsCheckDue(new UpdateCheckState(), T0));
    }

    [Fact]
    public void Within_24_hours_nothing_is_due_and_after_24_hours_it_is()
    {
        Assert.False(UpdateSchedule.IsCheckDue(Checked(T0.AddHours(-1)), T0));
        Assert.False(UpdateSchedule.IsCheckDue(Checked(T0.AddHours(-23).AddMinutes(-59)), T0));
        Assert.True(UpdateSchedule.IsCheckDue(Checked(T0.AddHours(-24)), T0));
        Assert.True(UpdateSchedule.IsCheckDue(Checked(T0.AddDays(-3)), T0));
    }

    [Fact]
    public void A_failed_attempt_backs_off_for_six_hours_and_is_never_a_tight_loop()
    {
        // Never checked, offline: the first attempt failed just now.
        var firstFail = new UpdateCheckState { LastAttemptUtc = T0, LastAttemptFailed = true };
        Assert.False(UpdateSchedule.IsCheckDue(firstFail, T0.AddMinutes(1)));
        Assert.False(UpdateSchedule.IsCheckDue(firstFail, T0.AddHours(5).AddMinutes(59)));
        Assert.True(UpdateSchedule.IsCheckDue(firstFail, T0.AddHours(6)));

        // A good check 30 hours ago, then a failed attempt 2 hours ago: retry only after the back-off.
        var failedLater = Checked(T0.AddHours(-30), failed: true, attempt: T0.AddHours(-2));
        Assert.False(UpdateSchedule.IsCheckDue(failedLater, T0));
        Assert.True(UpdateSchedule.IsCheckDue(failedLater, T0.AddHours(4)));

        // A failure does not mark the day as checked: LastChecked stays as it was.
        var state = Checked(T0.AddHours(-30));
        state.Apply(new UpdateCheckOutcome(UpdateCheckStatus.Failed), T0);
        Assert.Equal(T0.AddHours(-30), state.LastCheckedUtc);
        Assert.Equal(T0, state.LastAttemptUtc);
        Assert.True(state.LastAttemptFailed);
    }

    [Fact]
    public void A_clock_moved_backwards_never_blocks_the_check_for_days()
    {
        // Last check "tomorrow" (the clock was set back): treated as expired, not as "checked in the future".
        Assert.True(UpdateSchedule.IsCheckDue(Checked(T0.AddDays(1)), T0));
        Assert.True(UpdateSchedule.IsCheckDue(Checked(T0.AddDays(30), failed: true, attempt: T0.AddDays(30)), T0));
        // A tiny skew (NTP adjusting by seconds) is not a clock change.
        Assert.False(UpdateSchedule.IsCheckDue(Checked(T0.AddSeconds(30)), T0));
        Assert.False(UpdateSchedule.IsCheckDue(Checked(T0.AddMinutes(4)), T0));
    }

    [Fact]
    public void Outcomes_update_the_memory_correctly()
    {
        var state = new UpdateCheckState();
        var release = UpdateReleaseParser.Parse(ReleaseJson("v5.0.2")).Release!;
        state.Apply(UpdateChecker.Evaluate(release, V("5.0.1")), T0);
        Assert.Equal(T0, state.LastCheckedUtc);
        Assert.False(state.LastAttemptFailed);
        Assert.Equal("5.0.2", state.LatestKnownVersion);
        Assert.Equal(release.HtmlUrl, state.LatestKnownUrl);
        Assert.Equal(release.PublishedAt!.Value.UtcDateTime, state.LatestKnownPublishedUtc);
        Assert.True(UpdateSchedule.IsUpdateAvailable(state, V("5.0.1")));
        Assert.False(UpdateSchedule.IsUpdateAvailable(state, V("5.0.2")));   // after updating the offer is gone, no new check needed
        Assert.False(UpdateSchedule.IsUpdateAvailable(state, V("5.1.0")));

        // Busy / Unavailable change nothing.
        var before = state.Clone();
        state.Apply(new UpdateCheckOutcome(UpdateCheckStatus.Busy), T0.AddDays(2));
        state.Apply(new UpdateCheckOutcome(UpdateCheckStatus.Unavailable), T0.AddDays(2));
        Assert.True(state.SameAs(before));

        // A draft / pre-release answer is a successful check but never becomes "the latest".
        var pre = UpdateReleaseParser.Parse(ReleaseJson("v9.0.0", prerelease: true)).Release!;
        state.Apply(UpdateChecker.Evaluate(pre, V("5.0.1")), T0.AddDays(2));
        Assert.Equal("5.0.2", state.LatestKnownVersion);
        Assert.Equal(T0.AddDays(2), state.LastCheckedUtc);
    }

    [Fact]
    public void Junk_in_the_stored_latest_version_is_ignored()
    {
        var state = new UpdateCheckState { LatestKnownVersion = "garbage" };
        Assert.Null(state.LatestKnown);
        Assert.False(UpdateSchedule.IsUpdateAvailable(state, V("5.0.1")));
        state.LatestKnownVersion = "9.0.0-beta";
        Assert.False(UpdateSchedule.IsUpdateAvailable(state, V("5.0.1")));
    }

    // ================================================================ prompt rules ========

    private static UpdateCheckState Known(string latest, string? prompted = null, DateTime? promptedAt = null) =>
        new() { LastCheckedUtc = T0, LatestKnownVersion = latest, LastPromptedVersion = prompted ?? string.Empty, LastPromptedUtc = promptedAt };

    [Fact]
    public void The_prompt_is_offered_for_a_newer_known_version_that_was_never_prompted()
    {
        Assert.True(UpdateSchedule.ShouldPrompt(Known("5.0.2"), V("5.0.1"), checksEnabled: true, dontRemind: false, T0));
    }

    [Fact]
    public void No_prompt_without_an_update_or_when_checks_or_prompts_are_off()
    {
        Assert.False(UpdateSchedule.ShouldPrompt(Known("5.0.1"), V("5.0.1"), true, false, T0));
        Assert.False(UpdateSchedule.ShouldPrompt(Known("5.0.0"), V("5.0.1"), true, false, T0));
        Assert.False(UpdateSchedule.ShouldPrompt(new UpdateCheckState(), V("5.0.1"), true, false, T0));
        Assert.False(UpdateSchedule.ShouldPrompt(Known("5.0.2"), V("5.0.1"), checksEnabled: false, dontRemind: false, T0));
        Assert.False(UpdateSchedule.ShouldPrompt(Known("5.0.2"), V("5.0.1"), checksEnabled: true, dontRemind: true, T0));
    }

    [Fact]
    public void A_version_is_prompted_once_a_day()
    {
        var state = Known("5.0.2", "5.0.2", T0);
        Assert.False(UpdateSchedule.ShouldPrompt(state, V("5.0.1"), true, false, T0.AddMinutes(1)));
        Assert.False(UpdateSchedule.ShouldPrompt(state, V("5.0.1"), true, false, T0.AddHours(19)));
        Assert.True(UpdateSchedule.ShouldPrompt(state, V("5.0.1"), true, false, T0.AddHours(20)));
        Assert.True(UpdateSchedule.ShouldPrompt(state, V("5.0.1"), true, false, T0.AddHours(24)));
    }

    [Fact]
    public void A_newer_version_is_prompted_even_if_an_older_one_was_prompted_today()
    {
        var state = Known("5.0.3", "5.0.2", T0);
        Assert.True(UpdateSchedule.ShouldPrompt(state, V("5.0.1"), true, false, T0.AddMinutes(5)));
    }

    [Fact]
    public void A_clock_moved_back_does_not_silence_the_prompt_for_days()
    {
        var state = Known("5.0.2", "5.0.2", T0.AddDays(10));
        Assert.True(UpdateSchedule.ShouldPrompt(state, V("5.0.1"), true, false, T0));
    }

    [Fact]
    public void Marking_a_prompt_remembers_version_and_time()
    {
        var state = Known("5.0.2");
        state.MarkPrompted("5.0.2", T0);
        Assert.False(UpdateSchedule.ShouldPrompt(state, V("5.0.1"), true, false, T0.AddHours(1)));
    }

    [Theory]
    [InlineData(true, false, false, false, false, UpdatePromptDeferral.None)]
    [InlineData(false, false, false, false, false, UpdatePromptDeferral.NoWindow)]
    [InlineData(true, true, false, false, false, UpdatePromptDeferral.Fullscreen)]
    [InlineData(true, false, true, false, false, UpdatePromptDeferral.Slideshow)]
    [InlineData(true, false, false, true, false, UpdatePromptDeferral.ModalDialog)]
    [InlineData(true, false, false, false, true, UpdatePromptDeferral.Typing)]
    [InlineData(true, true, true, true, true, UpdatePromptDeferral.Fullscreen)]
    [InlineData(false, true, true, true, true, UpdatePromptDeferral.NoWindow)]
    public void The_prompt_waits_for_a_good_moment(bool window, bool fullscreen, bool slideshow, bool modal, bool typing, UpdatePromptDeferral expected)
    {
        Assert.Equal(expected, UpdateSchedule.Defer(window, fullscreen, slideshow, modal, typing));
    }

    [Fact]
    public void The_schedule_constants_match_the_agreed_behaviour()
    {
        Assert.Equal(TimeSpan.FromHours(24), UpdateSchedule.CheckInterval);
        Assert.Equal(TimeSpan.FromHours(6), UpdateSchedule.FailureBackoff);
        Assert.Equal(TimeSpan.FromSeconds(20), UpdateSchedule.StartupDelay);
        Assert.Equal(TimeSpan.FromMinutes(1), UpdateSchedule.TickInterval);
        Assert.True(UpdateSchedule.PromptRepeatInterval < TimeSpan.FromHours(24));
    }

    // ================================================================ status wording ========

    [Fact]
    public void The_status_line_covers_every_state()
    {
        var utc = TimeZoneInfo.Utc;
        var now = new DateTime(2026, 11, 15, 22, 0, 0, DateTimeKind.Utc);

        var never = UpdateStatusText.Describe(new UpdateCheckState(), V("5.0.1"), now, zone: utc);
        Assert.Equal(UpdateStatusKind.NotChecked, never.Kind);
        Assert.Equal("Not checked yet", never.ResultLine);
        Assert.Equal(string.Empty, never.LastCheckedLine);
        Assert.False(never.UpdateAvailable);

        var upToDate = UpdateStatusText.Describe(Known("5.0.1"), V("5.0.1"), now, zone: utc);
        Assert.Equal(UpdateStatusKind.UpToDate, upToDate.Kind);
        Assert.Equal("You're up to date (5.0.1)", upToDate.ResultLine);

        var available = UpdateStatusText.Describe(new UpdateCheckState { LastCheckedUtc = new DateTime(2026, 11, 15, 21, 40, 0, DateTimeKind.Utc), LatestKnownVersion = "5.0.1" }, V("5.0.0"), now, zone: utc);
        Assert.Equal(UpdateStatusKind.UpdateAvailable, available.Kind);
        Assert.Equal("Last checked: today 21:40.", available.LastCheckedLine);
        Assert.Equal("Glide 5.0.1 is available", available.ResultLine);
        Assert.True(available.UpdateAvailable);
        Assert.Equal("Last checked: today 21:40. Glide 5.0.1 is available", available.FullText);

        var failed = UpdateStatusText.Describe(new UpdateCheckState { LastAttemptUtc = now, LastAttemptFailed = true }, V("5.0.1"), now, zone: utc);
        Assert.Equal(UpdateStatusKind.CouldNotReach, failed.Kind);
        Assert.Equal("Couldn't reach GitHub", failed.ResultLine);

        var checking = UpdateStatusText.Describe(new UpdateCheckState(), V("5.0.1"), now, checking: true, zone: utc);
        Assert.Equal(UpdateStatusKind.Checking, checking.Kind);
        Assert.Equal("Checking...", checking.ResultLine);
    }

    [Fact]
    public void Times_read_naturally_in_local_time()
    {
        var utc = TimeZoneInfo.Utc;
        var now = new DateTime(2026, 11, 15, 22, 0, 0, DateTimeKind.Utc);
        Assert.Equal("today 21:40", UpdateStatusText.FormatWhen(new DateTime(2026, 11, 15, 21, 40, 0, DateTimeKind.Utc), now, utc));
        Assert.Equal("yesterday 08:05", UpdateStatusText.FormatWhen(new DateTime(2026, 11, 14, 8, 5, 0, DateTimeKind.Utc), now, utc));
        Assert.Equal("3 Nov 09:00", UpdateStatusText.FormatWhen(new DateTime(2026, 11, 3, 9, 0, 0, DateTimeKind.Utc), now, utc));
        Assert.Equal("3 Nov 2025 09:00", UpdateStatusText.FormatWhen(new DateTime(2025, 11, 3, 9, 0, 0, DateTimeKind.Utc), now, utc));
    }

    [Fact]
    public void The_prompt_subtitle_says_how_long_ago()
    {
        var now = T0;
        Assert.Equal("Released on GitHub", UpdateStatusText.ReleasedAgo(null, now));
        Assert.Equal("Released today on GitHub", UpdateStatusText.ReleasedAgo(now.AddHours(-3), now));
        Assert.Equal("Released today on GitHub", UpdateStatusText.ReleasedAgo(now.AddHours(2), now));   // clock skew
        Assert.Equal("Released 1 day ago on GitHub", UpdateStatusText.ReleasedAgo(now.AddDays(-1), now));
        Assert.Equal("Released 2 days ago on GitHub", UpdateStatusText.ReleasedAgo(now.AddDays(-2), now));
        Assert.Equal("Released 59 days ago on GitHub", UpdateStatusText.ReleasedAgo(now.AddDays(-59), now));
        Assert.Equal("Released 3 months ago on GitHub", UpdateStatusText.ReleasedAgo(now.AddDays(-100), now));
        Assert.Equal("Released 2 years ago on GitHub", UpdateStatusText.ReleasedAgo(now.AddDays(-800), now));
    }

    // ================================================================ checker ========

    [Fact]
    public async Task The_checker_reports_up_to_date_available_and_failures_from_the_feed()
    {
        var feed = new FakeUpdateFeed().ThenJson(ReleaseJson("v5.0.2")).ThenJson(ReleaseJson("v5.0.1")).ThenFail(UpdateFeedStatus.Timeout)
            .ThenJson("garbage").ThenFail(UpdateFeedStatus.TooLarge);
        var checker = new UpdateChecker(feed, V("5.0.1"), new SemaphoreSlim(1, 1));

        var a = await checker.CheckAsync();
        Assert.Equal(UpdateCheckStatus.UpdateAvailable, a.Status);
        Assert.Equal("v5.0.2", a.Release!.Tag);
        Assert.Equal(UpdateCheckStatus.UpToDate, (await checker.CheckAsync()).Status);
        var timeout = await checker.CheckAsync();
        Assert.Equal(UpdateCheckStatus.Failed, timeout.Status);
        Assert.Contains("Timeout", timeout.FailureReason);
        var parse = await checker.CheckAsync();
        Assert.Equal(UpdateCheckStatus.Failed, parse.Status);
        Assert.Contains("BadJson", parse.FailureReason);
        Assert.Equal(UpdateCheckStatus.Failed, (await checker.CheckAsync()).Status);
        Assert.Equal(5, feed.Calls);
    }

    [Fact]
    public async Task A_feed_that_throws_is_a_failed_check_not_a_crash()
    {
        var checker = new UpdateChecker(new ThrowingFeed(), V("5.0.1"), new SemaphoreSlim(1, 1));
        var outcome = await checker.CheckAsync();
        Assert.Equal(UpdateCheckStatus.Failed, outcome.Status);
        Assert.False(checker.IsBusy);
    }

    [Fact]
    public async Task No_configured_feed_means_no_check()
    {
        var feed = new FakeUpdateFeed().ThenFail(UpdateFeedStatus.Unavailable);
        var outcome = await new UpdateChecker(feed, V("5.0.1"), new SemaphoreSlim(1, 1)).CheckAsync();
        Assert.Equal(UpdateCheckStatus.Unavailable, outcome.Status);
    }

    [Fact]
    public async Task Only_one_check_runs_at_a_time_across_checkers_that_share_a_gate()
    {
        var gate = new SemaphoreSlim(1, 1);
        var slow = new FakeUpdateFeed { Hold = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously) }.ThenJson(ReleaseJson());
        var first = new UpdateChecker(slow, V("5.0.1"), gate);
        var second = new UpdateChecker(new FakeUpdateFeed().ThenJson(ReleaseJson()), V("5.0.1"), gate);   // "another window"

        var running = first.CheckAsync();
        await slow.Entered.Task;
        Assert.True(first.IsBusy);
        var rejected = await second.CheckAsync();
        Assert.Equal(UpdateCheckStatus.Busy, rejected.Status);
        slow.Hold!.SetResult(true);
        Assert.Equal(UpdateCheckStatus.UpdateAvailable, (await running).Status);
        Assert.False(first.IsBusy);
        Assert.Equal(UpdateCheckStatus.UpdateAvailable, (await second.CheckAsync()).Status);
    }

    [Fact]
    public void Production_checkers_share_one_static_gate()
    {
        var a = new UpdateChecker(new FakeUpdateFeed(), V("5.0.1"));
        var b = new UpdateChecker(new FakeUpdateFeed(), V("5.0.1"));
        var field = typeof(UpdateChecker).GetField("_gate", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        Assert.Same(UpdateChecker.SharedGate, field.GetValue(a));
        Assert.Same(field.GetValue(a), field.GetValue(b));
    }

    private sealed class ThrowingFeed : IUpdateFeed
    {
        public Task<UpdateFeedResponse> GetLatestReleaseAsync(CancellationToken cancellationToken) => throw new InvalidOperationException("boom");
    }
}
