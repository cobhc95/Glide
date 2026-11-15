using Glide.App.Settings;
using Glide.App.Updates;
using Glide.Core.Updates;

namespace Glide.Core.Tests;

/// <summary>Fakes for the update checker tests. Nothing here (or anywhere in the tests) touches the network.</summary>
internal static class UpdateTestSupport
{
    public static string ReleaseJson(string tag = "v5.0.2", string? htmlUrl = "https://github.com/cobhc95/Glide/releases/tag/v5.0.2",
        string published = "2026-11-13T10:00:00Z", bool draft = false, bool prerelease = false) =>
        "{" +
        $"\"tag_name\":\"{tag}\"," +
        (htmlUrl is null ? string.Empty : $"\"html_url\":\"{htmlUrl}\",") +
        $"\"published_at\":\"{published}\",\"draft\":{(draft ? "true" : "false")},\"prerelease\":{(prerelease ? "true" : "false")}," +
        "\"name\":\"Glide\",\"body\":\"notes\",\"assets\":[]}";

    public static UpdateVersion V(string text) => UpdateVersion.ParseOrNull(text) ?? throw new ArgumentException(text);

    public static readonly DateTime T0 = new(2026, 11, 15, 12, 0, 0, DateTimeKind.Utc);
}

/// <summary>A feed that answers from a queue (the last answer repeats) and can hold a request open until released.</summary>
internal sealed class FakeUpdateFeed : IUpdateFeed
{
    private readonly Queue<UpdateFeedResponse> _answers = new();
    private UpdateFeedResponse _last = new(UpdateFeedStatus.NetworkError);
    private int _calls;
    public TaskCompletionSource<bool>? Hold { get; set; }
    public TaskCompletionSource<bool> Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public int Calls => Volatile.Read(ref _calls);

    public FakeUpdateFeed Then(UpdateFeedResponse response) { _answers.Enqueue(response); return this; }
    public FakeUpdateFeed ThenJson(string json) => Then(new UpdateFeedResponse(UpdateFeedStatus.Ok, json, 200));
    public FakeUpdateFeed ThenFail(UpdateFeedStatus status = UpdateFeedStatus.NetworkError) => Then(new UpdateFeedResponse(status));

    public async Task<UpdateFeedResponse> GetLatestReleaseAsync(CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _calls);
        Entered.TrySetResult(true);
        if (Hold is { } hold) await hold.Task.ConfigureAwait(false);
        if (_answers.Count > 0) _last = _answers.Dequeue();
        return _last;
    }
}

internal sealed class FakeTicker : IUpdateTicker
{
    public bool IsRunning { get; private set; }
    public int Starts { get; private set; }
    public int Stops { get; private set; }
    public TimeSpan FirstDelay { get; private set; }
    public TimeSpan Interval { get; private set; }
    public Func<Task>? Tick { get; private set; }

    public void Start(TimeSpan firstDelay, TimeSpan interval, Func<Task> tick)
    {
        IsRunning = true; Starts++; FirstDelay = firstDelay; Interval = interval; Tick = tick;
    }

    public void Stop() { IsRunning = false; Stops++; }
}

internal sealed class FakeUpdateHost : IUpdateHost
{
    public GlideSettingsState? Settings { get; set; } = new();
    public UpdatePromptDeferral Deferral { get; set; } = UpdatePromptDeferral.None;
    public Func<UpdatePromptModel, Task<UpdatePromptResult?>> OnPrompt { get; set; } = _ => Task.FromResult<UpdatePromptResult?>(new UpdatePromptResult(UpdatePromptAction.NotNow, false));
    public List<UpdateCheckState> Saved { get; } = new();
    public List<bool> DontRemindSet { get; } = new();
    public List<string> Opened { get; } = new();
    public List<string> Logs { get; } = new();
    public List<UpdatePromptModel> Prompts { get; } = new();

    public GlideSettingsState? GetSettings() => Settings;
    public void SaveState(UpdateCheckState state) => Saved.Add(state.Clone());
    public void SetDontRemind(bool value)
    {
        DontRemindSet.Add(value);
        if (Settings is not null) Settings.UpdateDontRemind = value;
    }
    public UpdatePromptDeferral GetDeferral() => Deferral;
    public Task<UpdatePromptResult?> ShowPromptAsync(UpdatePromptModel model) { Prompts.Add(model); return OnPrompt(model); }
    public void OpenUrl(string url) => Opened.Add(url);
    public void Log(string name, object? data = null) => Logs.Add(name);
}

/// <summary>Builds an <see cref="UpdateService"/> on fakes with a controllable clock.</summary>
internal sealed class UpdateRig
{
    public FakeUpdateFeed Feed { get; } = new();
    public FakeUpdateHost Host { get; } = new();
    public FakeTicker Ticker { get; } = new();
    public DateTime Now { get; set; } = UpdateTestSupport.T0;
    public UpdateService Service { get; }

    public UpdateRig(string running = "5.0.1")
    {
        // A private gate per rig: tests run in parallel and must not see each other's "busy" state.
        var checker = new UpdateChecker(Feed, UpdateTestSupport.V(running), new SemaphoreSlim(1, 1));
        Service = new UpdateService(Host, checker, Ticker, () => Now);
    }
}
