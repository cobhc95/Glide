using System.Text.Json;
using Glide.Core;

namespace Glide.App.Settings;

/// <summary>In-memory recent-open history with best-effort debounced persistence.</summary>
public static class RecentHistoryStore
{
    private const string FileName = "glide.history.json";
    private static readonly object Gate = new();
    private static readonly List<Entry> Items = new();
    private static readonly SemaphoreSlim FlushGate = new(1, 1);
    private static CancellationTokenSource? _debounce;
    private static Task? _loadTask;
    private static Task? _maintenanceTask;
    private static Exception? _lastPersistenceError;
    private static bool _clearBeforeLoad;
    private static bool _flushPendingUntilInitialized;
    private static int? _activeLimit;

    // Test-only seams; production leaves these null.
    internal static Func<string>? TestStoragePathOverride { get; set; }
    internal static Func<Entry, bool>? TestPathExistsOverride { get; set; }
    internal static void ResetForTests() { lock (Gate) { _debounce?.Cancel(); _debounce = null; _loadTask = null; _maintenanceTask = null; _lastPersistenceError = null; _clearBeforeLoad = false; _flushPendingUntilInitialized = false; _activeLimit = null; Items.Clear(); } }

    public sealed record Entry(string Kind, string Path, DateTimeOffset LastOpenedUtc);
    private sealed class Document { public int Schema { get; set; } = 1; public List<Entry> Items { get; set; } = new(); }

    /// <summary>Raised when background or close-time persistence fails. The exception is also retained in LastPersistenceError.</summary>
    public static event Action<Exception>? PersistenceFailed;
    public static Exception? LastPersistenceError { get { lock (Gate) return _lastPersistenceError; } }

    public static Task InitializeAsync()
    {
        if (GlidePerformanceTrace.Enabled) GlidePerformanceTrace.Mark("history_initialize_requested");
        lock (Gate) return _loadTask ??= Task.Run(InitializeCoreAsync);
    }

    private static async Task InitializeCoreAsync()
    {
        await LoadIntoMemoryAsync().ConfigureAwait(false);
        bool flushPending;
        lock (Gate)
        {
            flushPending = _flushPendingUntilInitialized;
            _flushPendingUntilInitialized = false;
        }
        if (flushPending) ScheduleFlush();
    }

    public static IReadOnlyList<Entry> Snapshot(int limit)
    {
        lock (Gate) return Items.OrderByDescending(x => x.LastOpenedUtc).Take(Math.Clamp(limit, 1, 32)).ToArray();
    }

    public static void RecordFile(string path, int limit) => Record("file", path, limit);
    public static void RecordFolder(string path, int limit) => Record("folder", path, limit);

    public static void Trim(int limit)
    {
        var max = Math.Clamp(limit, 1, 32);
        lock (Gate)
        {
            _activeLimit = max;
            Items.Sort((a, b) => b.LastOpenedUtc.CompareTo(a.LastOpenedUtc));
            if (Items.Count > max) Items.RemoveRange(max, Items.Count - max);
        }
        ScheduleFlush();
    }

    public static void Clear()
    {
        lock (Gate) { Items.Clear(); if (_loadTask is null) _clearBeforeLoad = true; }
        ScheduleFlush();
    }

    /// <summary>Waits for initialization and lazy maintenance, then persists the latest state.</summary>
    public static async Task FlushAsync()
    {
        await InitializeAsync().ConfigureAwait(false);
        Task? maintenance;
        lock (Gate) maintenance = _maintenanceTask;
        if (maintenance is not null) await maintenance.ConfigureAwait(false);
        CancellationTokenSource? pending;
        lock (Gate) { pending = _debounce; _debounce = null; }
        pending?.Cancel();
        await PersistAsync().ConfigureAwait(false);
    }

    private static void Record(string kind, string path, int limit)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        try { path = Path.GetFullPath(path); } catch { return; }
        var max = Math.Clamp(limit, 1, 32);
        lock (Gate)
        {
            _activeLimit = max;
            Items.RemoveAll(x => SameKey(x, kind, path));
            Items.Insert(0, new Entry(kind, path, DateTimeOffset.UtcNow));
            Items.Sort((a, b) => b.LastOpenedUtc.CompareTo(a.LastOpenedUtc));
            if (Items.Count > max) Items.RemoveRange(max, Items.Count - max);
        }
        if (GlidePerformanceTrace.Enabled) GlidePerformanceTrace.Mark("history_record", $"kind={kind}");
        ScheduleFlush();
    }

    private static async Task LoadIntoMemoryAsync()
    {
        if (GlidePerformanceTrace.Enabled) GlidePerformanceTrace.Mark("history_load_start");
        Document? document = null;
        try
        {
            var path = StoragePath();
            if (File.Exists(path)) document = JsonSerializer.Deserialize<Document>(await File.ReadAllTextAsync(path).ConfigureAwait(false), Options());
        }
        catch (Exception ex) { ReportFailure(ex); }
        lock (Gate)
        {
            var merged = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
            var ignoreDisk = _clearBeforeLoad;
            if (!ignoreDisk) foreach (var item in document?.Items ?? new()) MergeNewest(merged, item);
            foreach (var item in Items) MergeNewest(merged, item); // records made while disk load was in flight win by timestamp
            Items.Clear();
            Items.AddRange(merged.Values.OrderByDescending(x => x.LastOpenedUtc));
            if (_activeLimit is { } max && Items.Count > max) Items.RemoveRange(max, Items.Count - max);
            _maintenanceTask = Task.Run(PruneMissingAsync);
        }
        if (GlidePerformanceTrace.Enabled) GlidePerformanceTrace.Mark("history_load_end");
    }

    private static async Task PruneMissingAsync()
    {
        Entry[] snapshot;
        lock (Gate) snapshot = Items.ToArray();
        var valid = await Task.Run(() => snapshot.Where(x => TestPathExistsOverride?.Invoke(x) ??
            (string.Equals(x.Kind, "folder", StringComparison.OrdinalIgnoreCase) ? Directory.Exists(x.Path) : File.Exists(x.Path))).ToArray()).ConfigureAwait(false);
        var validKeys = valid.Select(Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var captured = snapshot.GroupBy(Key, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.OrderByDescending(x => x.LastOpenedUtc).First(), StringComparer.OrdinalIgnoreCase);
        lock (Gate)
        {
            // Remove only the captured stale version. A record added while probes ran survives.
            Items.RemoveAll(current => captured.TryGetValue(Key(current), out var old) &&
                !validKeys.Contains(Key(current)) && current.LastOpenedUtc <= old.LastOpenedUtc);
        }
        ScheduleFlush();
    }

    private static void ScheduleFlush()
    {
        CancellationTokenSource cts;
        lock (Gate)
        {
            // A startup Trim/Record must never cause history disk I/O before the application has
            // explicitly crossed its first-presentation gate and called InitializeAsync().
            if (_loadTask is null)
            {
                _flushPendingUntilInitialized = true;
                return;
            }
            _debounce?.Cancel();
            _debounce = cts = new CancellationTokenSource();
        }
        _ = Task.Run(async () => { try { await Task.Delay(350, cts.Token).ConfigureAwait(false); await PersistAsync().ConfigureAwait(false); } catch (OperationCanceledException) { } });
    }

    private static async Task PersistAsync()
    {
        if (GlidePerformanceTrace.Enabled) GlidePerformanceTrace.Mark("history_persist_start");
        await FlushGate.WaitAsync().ConfigureAwait(false);
        try
        {
            Entry[] items; lock (Gate) items = Items.ToArray();
            var path = StoragePath();
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temp = path + ".tmp";
            if (items.Length == 0)
            {
                if (File.Exists(path)) File.Delete(path);
                lock (Gate) _lastPersistenceError = null;
                if (GlidePerformanceTrace.Enabled) GlidePerformanceTrace.Mark("history_persist_end", "items=0");
                return;
            }
            await File.WriteAllTextAsync(temp, JsonSerializer.Serialize(new Document { Items = items.ToList() }, Options())).ConfigureAwait(false);
            File.Move(temp, path, true);
            lock (Gate) _lastPersistenceError = null;
            if (GlidePerformanceTrace.Enabled) GlidePerformanceTrace.Mark("history_persist_end", $"items={items.Length}");
        }
        catch (Exception ex) { ReportFailure(ex); if (GlidePerformanceTrace.Enabled) GlidePerformanceTrace.Mark("history_persist_failed", ex.GetType().Name); }
        finally { FlushGate.Release(); }
    }

    private static void ReportFailure(Exception ex)
    {
        lock (Gate) _lastPersistenceError = ex;
        try { PersistenceFailed?.Invoke(ex); } catch { }
    }

    private static bool SameKey(Entry x, string kind, string path) => string.Equals(x.Kind, kind, StringComparison.OrdinalIgnoreCase) && string.Equals(x.Path, path, StringComparison.OrdinalIgnoreCase);
    private static string Key(Entry x) => x.Kind + "\u001f" + x.Path;
    private static void MergeNewest(Dictionary<string, Entry> map, Entry item) { var key = Key(item); if (!map.TryGetValue(key, out var old) || item.LastOpenedUtc > old.LastOpenedUtc) map[key] = item; }
    private static string StoragePath() => TestStoragePathOverride?.Invoke() ?? Path.Combine(SettingsStore.GetSettingsDirectory(), FileName);
    private static JsonSerializerOptions Options() => new() { WriteIndented = true, PropertyNameCaseInsensitive = true };
}
