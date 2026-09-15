using Xunit;
using Glide.App.Settings;
using System.Text.Json;

namespace Glide.Core.Tests;

[CollectionDefinition("RecentHistory", DisableParallelization = true)]
public sealed class RecentHistoryCollection { }

[Collection("RecentHistory")]
public sealed class RecentHistoryStoreTests
{
    private static string NewStorePath() => Path.Combine(Path.GetTempPath(), "glide-history-test", Guid.NewGuid().ToString("N"), "history.json");
    private static void Reset(string path) { RecentHistoryStore.ResetForTests(); RecentHistoryStore.TestStoragePathOverride = () => path; RecentHistoryStore.TestPathExistsOverride = _ => false; }
    private static void Cleanup(string path) { RecentHistoryStore.ResetForTests(); RecentHistoryStore.TestStoragePathOverride = null; RecentHistoryStore.TestPathExistsOverride = null; try { Directory.Delete(Path.GetDirectoryName(path)!, true); } catch { } }

    [Fact]
    public void RecordIsImmediatelyVisibleWithoutFilesystemValidation()
    {
        var path = NewStorePath(); Reset(path);
        RecentHistoryStore.RecordFile(Path.Combine(Path.GetDirectoryName(path)!, "missing.jpg"), 8);
        Assert.Single(RecentHistoryStore.Snapshot(8));
        Cleanup(path);
    }


    [Fact]
    public async Task PreInitializationTrimDoesNotTouchHistoryFileUntilExplicitInitialization()
    {
        var path = NewStorePath(); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var sentinel = "{\"Schema\":1,\"Items\":[]}";
        await File.WriteAllTextAsync(path, sentinel);
        Reset(path);
        RecentHistoryStore.Trim(8);
        await Task.Delay(450);
        Assert.Equal(sentinel, await File.ReadAllTextAsync(path));
        await RecentHistoryStore.FlushAsync();
        Cleanup(path);
    }

    [Fact]
    public async Task ClearBeforeInitialLoadCannotBeReplacedByDiskState()
    {
        var path = NewStorePath(); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(new { Schema = 1, Items = new[] { new { Kind = "file", Path = "old.jpg", LastOpenedUtc = DateTimeOffset.UtcNow } } }));
        Reset(path); RecentHistoryStore.Clear();
        await RecentHistoryStore.FlushAsync();
        Assert.Empty(RecentHistoryStore.Snapshot(8));
        Assert.False(File.Exists(path)); Cleanup(path);
    }

    [Fact]
    public async Task RecordDuringBarrierControlledPruneSurvives()
    {
        var path = NewStorePath(); Reset(path);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        RecentHistoryStore.TestPathExistsOverride = _ => { entered.TrySetResult(); release.Task.GetAwaiter().GetResult(); return false; };
        RecentHistoryStore.RecordFile(Path.Combine(Path.GetDirectoryName(path)!, "stale.jpg"), 8);
        var init = RecentHistoryStore.InitializeAsync(); await entered.Task;
        RecentHistoryStore.RecordFile(Path.Combine(Path.GetDirectoryName(path)!, "stale.jpg"), 8);
        release.SetResult(); await init; await RecentHistoryStore.FlushAsync();
        Assert.Single(RecentHistoryStore.Snapshot(8)); Cleanup(path);
    }

    [Fact]
    public async Task ActualLoadMergeKeepsNewestTimestamp()
    {
        var path = NewStorePath(); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var image = Path.Combine(Path.GetDirectoryName(path)!, "same.jpg");
        var old = DateTimeOffset.UtcNow.AddMinutes(-1); var newer = DateTimeOffset.UtcNow;
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(new { Schema = 1, Items = new[] { new { Kind = "file", Path = image, LastOpenedUtc = old } } }));
        Reset(path); RecentHistoryStore.TestPathExistsOverride = _ => true;
        RecentHistoryStore.RecordFile(image, 8); await RecentHistoryStore.InitializeAsync();
        Assert.True(RecentHistoryStore.Snapshot(8).Single().LastOpenedUtc >= newer.AddSeconds(-2)); Cleanup(path);
    }

    [Fact]
    public async Task PersistenceFailureIsObservableThroughEventAndProperty()
    {
        var path = Path.Combine(Path.GetTempPath(), "glide-history-test", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(path);
        Reset(path);
        // Preserve the in-memory item through initialization/pruning so FlushAsync reaches
        // the intended persistence failure: the configured storage path is an existing directory.
        RecentHistoryStore.TestPathExistsOverride = _ => true;
        RecentHistoryStore.RecordFile(Path.Combine(path, "image.jpg"), 8);
        Exception? observed = null; void Handler(Exception error) => observed = error; RecentHistoryStore.PersistenceFailed += Handler;
        try { await RecentHistoryStore.FlushAsync(); Assert.NotNull(observed); Assert.Same(observed, RecentHistoryStore.LastPersistenceError); }
        finally { RecentHistoryStore.PersistenceFailed -= Handler; Cleanup(path); }
    }
}
