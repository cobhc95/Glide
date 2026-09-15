using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using Avalonia.Threading;
using Glide.Core;

namespace Glide.App.Services;

internal enum ExternalLaunchItemStatus { Accepted, Rejected, Failed }
internal readonly record struct ExternalLaunchItemResult(Guid RequestId, ExternalLaunchItemStatus Status, string Message = "");
internal readonly record struct ExternalLaunchRequest(char Kind, string Path, Guid RequestId);
internal sealed record ExternalLaunchBatch(IReadOnlyList<ExternalLaunchRequest> Requests);

/// <summary>
/// Process-wide external-open broker. Protocol v2 deliberately uses a new pipe name: older Glide
/// builds cannot understand typed per-item results, so new/old coexistence falls back to a new UI
/// process instead of pretending wire compatibility from a shared pipe name.
///
/// ACK semantics are "accepted ownership/queueing", not "image already presented".  The owner
/// records a request ID before acknowledging it and keeps accepted work in a process-level FIFO until
/// the UI dispatcher owns it.  Retries therefore reuse the same IDs and cannot duplicate a request
/// merely because a slow decode outlived the launcher's transport deadline.
/// </summary>
internal static class ExternalLaunchBroker
{
    private const string PipeName = "Glide3.ExternalOpen.v2";
    // v3 is an EventWaitHandle sentinel rather than a thread-affine mutex. Creation is the process
    // election primitive; followers close their handle immediately, so the sentinel disappears when
    // the elected owner releases/exits and another live Glide process can take over.
    private const string PresenceName = @"Local\Glide3.ProcessPresence.v3";
    private const string ProtocolHeader = "GLIDE3\t2";
    // A ready warm broker normally accepts the connection immediately. This deadline is only a
    // bounded safety net for the owner-startup/pipe-replacement race; it is not a hot-path delay.
    private static readonly TimeSpan ClientDeadline = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan ServerPeerDeadline = TimeSpan.FromSeconds(2);
    private const int MaxProtocolLineChars = 128 * 1024;
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool AllowSetForegroundWindow(int dwProcessId);
    private static readonly object Gate = new();
    private static readonly List<WeakReference<MainWindow>> Windows = new();
    private static readonly Dictionary<Guid, ExternalLaunchItemResult> CompletedRequests = new();
    private static readonly Queue<ExternalLaunchRequest> PendingRequests = new();
    private static WeakReference<MainWindow>? _activeWindow;
    private static CancellationTokenSource? _serverCts;
    private static Task? _serverTask;
    private static CancellationTokenSource? _takeoverCts;
    private static Task? _takeoverTask;
    private static EventWaitHandle? _presence;
    private static bool _ownsPresence;
    private static bool _dispatchScheduled;
    private static BrokerState _state = BrokerState.Follower;

    private enum BrokerState { Owner, Follower, Ready, Stopping }

    internal static string PresenceObjectNameForTests => PresenceName;

    public static ExternalLaunchBatch? PrepareForwardBatch(IEnumerable<string> paths)
    {
        var requests = paths
            .Select(path => Directory.Exists(path)
                ? new ExternalLaunchRequest('D', Path.GetFullPath(path), Guid.NewGuid())
                : File.Exists(path)
                    ? new ExternalLaunchRequest('F', Path.GetFullPath(path), Guid.NewGuid())
                    : default)
            .Where(x => x != default)
            .ToArray();
        return requests.Length == 0 ? null : new ExternalLaunchBatch(requests);
    }

    public static ExternalLaunchBatch PrepareActivateBatch()
    {
        return new ExternalLaunchBatch(new[] { new ExternalLaunchRequest('A', string.Empty, Guid.NewGuid()) });
    }

    public static bool ClaimProcessPresence()
    {
        lock (Gate)
        {
            if (!OperatingSystem.IsWindows())
            {
                _ownsPresence = true;
                _state = BrokerState.Owner;
                return true;
            }
            if (_presence is not null) return _ownsPresence;
            try
            {
                var candidate = new EventWaitHandle(false, EventResetMode.ManualReset, PresenceName, out var createdNew);
                if (!createdNew)
                {
                    candidate.Dispose();
                    _ownsPresence = false;
                    _state = BrokerState.Follower;
                    return false;
                }
                _presence = candidate;
                _ownsPresence = true;
                _state = BrokerState.Owner;
                return true;
            }
            catch
            {
                _ownsPresence = false;
                _state = BrokerState.Follower;
                return false;
            }
        }
    }

    private static bool TryTakeOverPresenceLocked()
    {
        if (_ownsPresence) return true;
        if (!OperatingSystem.IsWindows()) { _ownsPresence = true; _state = BrokerState.Owner; return true; }
        try
        {
            var candidate = new EventWaitHandle(false, EventResetMode.ManualReset, PresenceName, out var createdNew);
            if (!createdNew) { candidate.Dispose(); return false; }
            _presence = candidate;
            _ownsPresence = true;
            _state = BrokerState.Owner;
            return true;
        }
        catch { return false; }
    }

    internal static bool ExistingProcessPresent()
    {
        if (!OperatingSystem.IsWindows()) return false;
        try
        {
            using var existing = EventWaitHandle.OpenExisting(PresenceName);
            return existing is not null;
        }
        catch { return false; }
    }

    private static bool WaitForReadyPresence(TimeSpan timeout)
    {
        if (!OperatingSystem.IsWindows()) return true;
        try
        {
            using var existing = EventWaitHandle.OpenExisting(PresenceName);
            return existing.WaitOne(timeout);
        }
        catch { return false; }
    }

    public static bool TryForwardToExisting(ExternalLaunchBatch? batch)
    {
        return TryForwardToExisting(batch, ClientDeadline);
    }

    internal static bool TryForwardToExisting(ExternalLaunchBatch? batch, TimeSpan timeout)
    {
        if (batch is null || batch.Requests.Count == 0 || timeout <= TimeSpan.Zero) return false;
        try { return TryForwardAsync(batch.Requests, timeout).GetAwaiter().GetResult(); }
        catch { return false; }
    }

    private static async Task<bool> TryForwardAsync(IReadOnlyList<ExternalLaunchRequest> requests, TimeSpan timeout)
    {
        var started = Stopwatch.GetTimestamp();
        while (Stopwatch.GetElapsedTime(started) < timeout)
        {
            var remaining = timeout - Stopwatch.GetElapsedTime(started);
            if (remaining <= TimeSpan.Zero) break;
            // Presence is published only after the owner has created its pipe server. Waiting on
            // the event avoids turning a legitimate cold-start race into a second UI process.
            if (!WaitForReadyPresence(remaining)) return false;
            remaining = timeout - Stopwatch.GetElapsedTime(started);
            if (remaining <= TimeSpan.Zero) break;
            using var attemptCts = new CancellationTokenSource(remaining);
            try
            {
                await using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
                await client.ConnectAsync(attemptCts.Token).ConfigureAwait(false);
                if (OperatingSystem.IsWindows())
                {
                    try { AllowSetForegroundWindow(-1); } catch { }
                }
                using var writer = new StreamWriter(client, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
                using var reader = new StreamReader(client, Encoding.UTF8, leaveOpen: true);
                var batchId = Guid.NewGuid();
                await WriteLineAsync(writer, $"{ProtocolHeader}\t{batchId:N}\t{requests.Count}", attemptCts.Token).ConfigureAwait(false);
                foreach (var request in requests)
                {
                    var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(request.Path));
                    await WriteLineAsync(writer, $"{request.Kind}\t{request.RequestId:N}\t{encoded}", attemptCts.Token).ConfigureAwait(false);
                }

                var ack = await ReadBoundedLineAsync(reader, attemptCts.Token).ConfigureAwait(false);
                if (!string.Equals(ack, $"RESULTS\t{batchId:N}\t{requests.Count}", StringComparison.Ordinal)) return false;
                var expected = requests.ToDictionary(r => r.RequestId);
                var seen = new HashSet<Guid>();
                for (var i = 0; i < requests.Count; i++)
                {
                    var line = await ReadBoundedLineAsync(reader, attemptCts.Token).ConfigureAwait(false);
                    if (line is null) return false;
                    var parts = line.Split('\t', 3);
                    if (parts.Length < 2 || !Guid.TryParseExact(parts[0], "N", out var requestId) ||
                        !Enum.TryParse<ExternalLaunchItemStatus>(parts[1], ignoreCase: false, out _)) return false;
                    if (!expected.ContainsKey(requestId) || !seen.Add(requestId)) return false;
                }
                return seen.Count == requests.Count;
            }
            catch (OperationCanceledException) { continue; }
            catch
            {
                var pause = timeout - Stopwatch.GetElapsedTime(started);
                if (pause <= TimeSpan.Zero) break;
                await Task.Delay(TimeSpan.FromMilliseconds(Math.Min(25, pause.TotalMilliseconds))).ConfigureAwait(false);
            }
        }
        return false;
    }

    private static async Task WriteLineAsync(StreamWriter writer, string line, CancellationToken token)
    {
        if (line.Length > MaxProtocolLineChars)
            throw new InvalidDataException("IPC protocol line exceeds the bounded wire limit.");
        token.ThrowIfCancellationRequested();
        await writer.WriteLineAsync(line.AsMemory(), token).ConfigureAwait(false);
    }

    private static async Task<string?> ReadBoundedLineAsync(StreamReader reader, CancellationToken token)
    {
        // StreamReader.ReadLineAsync has no line-length cap and can allocate an attacker-controlled
        // string before callers can reject it. Read one buffered character at a time so both time
        // (via the caller token) and memory are bounded. External-open messages are tiny relative to
        // this cap, so this is intentionally correctness/security biased rather than throughput tuned.
        var builder = new StringBuilder(Math.Min(4096, MaxProtocolLineChars));
        var one = new char[1];
        while (true)
        {
            token.ThrowIfCancellationRequested();
            var read = await reader.ReadAsync(one.AsMemory(0, 1), token).ConfigureAwait(false);
            if (read == 0) return builder.Length == 0 ? null : builder.ToString();
            var ch = one[0];
            if (ch == '\n') return builder.ToString();
            if (ch == '\r') continue;
            if (builder.Length >= MaxProtocolLineChars)
                throw new InvalidDataException("IPC protocol line exceeds the bounded wire limit.");
            builder.Append(ch);
        }
    }

    public static void Start(MainWindow window)
    {
        lock (Gate)
        {
            CleanupWindowsLocked();
            if (!Windows.Any(reference => reference.TryGetTarget(out var existing) && ReferenceEquals(existing, window)))
                Windows.Add(new WeakReference<MainWindow>(window));
            _activeWindow = new WeakReference<MainWindow>(window);

            if (!_ownsPresence && !TryTakeOverPresenceLocked())
            {
                EnsureTakeoverMonitorLocked();
                return;
            }
            EnsureServerStartedLocked();
        }
    }

    public static void MarkActive(MainWindow window)
    {
        lock (Gate)
        {
            CleanupWindowsLocked();
            if (Windows.Any(reference => reference.TryGetTarget(out var existing) && ReferenceEquals(existing, window)))
                _activeWindow = new WeakReference<MainWindow>(window);
        }
    }

    public static void Unregister(MainWindow window)
    {
        CancellationTokenSource? stop = null;
        Task? serverTask = null;
        lock (Gate)
        {
            Windows.RemoveAll(reference => !reference.TryGetTarget(out var target) || ReferenceEquals(target, window));
            if (_activeWindow?.TryGetTarget(out var active) == true && ReferenceEquals(active, window))
                _activeWindow = null;
            if (Windows.Count == 0)
            {
                _state = BrokerState.Stopping;
                stop = _serverCts;
                serverTask = _serverTask;
                _serverCts = null;
                _serverTask = null;
                StopTakeoverMonitorLocked();
                ReleasePresenceLocked();
            }
        }
        if (stop is not null) StopServerBounded(stop, serverTask);
    }

    public static void Stop()
    {
        CancellationTokenSource? stop;
        Task? serverTask;
        lock (Gate)
        {
            Windows.Clear();
            PendingRequests.Clear();
            _dispatchScheduled = false;
            _activeWindow = null;
            _state = BrokerState.Stopping;
            stop = _serverCts;
            serverTask = _serverTask;
            _serverCts = null;
            _serverTask = null;
            StopTakeoverMonitorLocked();
            ReleasePresenceLocked();
        }
        if (stop is not null) StopServerBounded(stop, serverTask);
    }

    private static void EnsureServerStartedLocked()
    {
        if (!_ownsPresence || _serverTask is { IsCompleted: false }) return;
        var cts = new CancellationTokenSource();
        _serverCts = cts;
        _state = BrokerState.Owner;
        _serverTask = Task.Run(() => ServerLoopAsync(cts.Token));
    }

    private static void EnsureTakeoverMonitorLocked()
    {
        if (_takeoverTask is { IsCompleted: false }) return;
        var cts = new CancellationTokenSource();
        _takeoverCts = cts;
        _takeoverTask = Task.Run(() => TakeoverLoopAsync(cts.Token));
    }

    private static async Task TakeoverLoopAsync(CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                await Task.Delay(250, token).ConfigureAwait(false);
                lock (Gate)
                {
                    CleanupWindowsLocked();
                    if (Windows.Count == 0 || _ownsPresence) return;
                    if (!TryTakeOverPresenceLocked()) continue;
                    EnsureServerStartedLocked();
                    return;
                }
            }
        }
        catch (OperationCanceledException) { }
    }

    private static void StopTakeoverMonitorLocked()
    {
        var cts = _takeoverCts;
        _takeoverCts = null;
        _takeoverTask = null;
        try { cts?.Cancel(); } catch { }
        cts?.Dispose();
    }

    private static void ReleasePresenceLocked()
    {
        try { _presence?.Reset(); } catch { }
        try { _presence?.Dispose(); } catch { }
        _presence = null;
        _ownsPresence = false;
        if (_state != BrokerState.Stopping) _state = BrokerState.Follower;
    }

    private static void StopServerBounded(CancellationTokenSource cts, Task? task)
    {
        try { cts.Cancel(); } catch { }
        if (task is not null)
        {
            try { task.Wait(TimeSpan.FromSeconds(2)); } catch { }
        }
        cts.Dispose();
    }

    public static MainWindow? FindWindowContainingImage(string path)
    {
        lock (Gate)
        {
            CleanupWindowsLocked();
            for (var i = Windows.Count - 1; i >= 0; i--)
                if (Windows[i].TryGetTarget(out var window) && window.ContainsOpenImage(path)) return window;
            return null;
        }
    }

    private static MainWindow? SelectReceiver()
    {
        lock (Gate)
        {
            CleanupWindowsLocked();
            if (_activeWindow?.TryGetTarget(out var active) == true &&
                Windows.Any(reference => reference.TryGetTarget(out var item) && ReferenceEquals(item, active)))
                return active;
            for (var i = Windows.Count - 1; i >= 0; i--)
                if (Windows[i].TryGetTarget(out var window)) return window;
            return null;
        }
    }

    private static void CleanupWindowsLocked() => Windows.RemoveAll(reference => !reference.TryGetTarget(out _));

    private static async Task ServerLoopAsync(CancellationToken token)
    {
        var readinessPublished = false;
        while (!token.IsCancellationRequested)
        {
            try
            {
                await using var server = new NamedPipeServerStream(
                    PipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                if (!readinessPublished)
                {
                    lock (Gate)
                    {
                        if (_ownsPresence)
                        {
                            try { _presence?.Set(); } catch { }
                            _state = BrokerState.Ready;
                        }
                    }
                    readinessPublished = true;
                }

                await server.WaitForConnectionAsync(token).ConfigureAwait(false);
                using var peerCts = CancellationTokenSource.CreateLinkedTokenSource(token);
                peerCts.CancelAfter(ServerPeerDeadline);
                using var reader = new StreamReader(server, Encoding.UTF8, leaveOpen: true);
                using var writer = new StreamWriter(server, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
                var header = await ReadBoundedLineAsync(reader, peerCts.Token).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(header)) continue;
                var headerParts = header.Split('\t');
                if (headerParts.Length != 4 || !string.Equals($"{headerParts[0]}\t{headerParts[1]}", ProtocolHeader, StringComparison.Ordinal) ||
                    !Guid.TryParseExact(headerParts[2], "N", out var batchId) ||
                    !int.TryParse(headerParts[3], out var count) || count < 1 || count > 4096)
                    continue;

                var requests = new List<ExternalLaunchRequest>(count);
                var malformed = false;
                for (var i = 0; i < count; i++)
                {
                    var line = await ReadBoundedLineAsync(reader, peerCts.Token).ConfigureAwait(false);
                    if (line is null) { malformed = true; break; }
                    var parts = line.Split('\t', 3);
                    if (parts.Length != 3 || parts[0].Length != 1 || (parts[0][0] != 'F' && parts[0][0] != 'D' && parts[0][0] != 'A') ||
                        !Guid.TryParseExact(parts[1], "N", out var requestId)) { malformed = true; break; }
                    try
                    {
                        var path = Encoding.UTF8.GetString(Convert.FromBase64String(parts[2]));
                        requests.Add(new ExternalLaunchRequest(parts[0][0], path, requestId));
                    }
                    catch { malformed = true; break; }
                }
                if (malformed || requests.Count != count) continue;

                var results = AcceptOwnership(requests);
                await WriteLineAsync(writer, $"RESULTS\t{batchId:N}\t{requests.Count}", peerCts.Token).ConfigureAwait(false);
                foreach (var result in results)
                {
                    var message = Convert.ToBase64String(Encoding.UTF8.GetBytes(result.Message ?? string.Empty));
                    await WriteLineAsync(writer, $"{result.RequestId:N}\t{result.Status}\t{message}", peerCts.Token).ConfigureAwait(false);
                }
                SchedulePendingDispatch();
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
            catch (OperationCanceledException) { }
            catch
            {
                try { await Task.Delay(50, token).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
            }
        }
    }

    private static IReadOnlyList<ExternalLaunchItemResult> AcceptOwnership(IReadOnlyList<ExternalLaunchRequest> requests)
    {
        var results = new List<ExternalLaunchItemResult>(requests.Count);
        lock (Gate)
        {
            foreach (var request in requests)
            {
                if (CompletedRequests.TryGetValue(request.RequestId, out var previous))
                {
                    results.Add(previous);
                    continue;
                }

                ExternalLaunchItemResult result;
                if (request.Kind == 'A')
                    result = new(request.RequestId, ExternalLaunchItemStatus.Accepted, "Activation queued by owner.");
                else if (request.Kind == 'D')
                    result = Directory.Exists(request.Path)
                        ? new(request.RequestId, ExternalLaunchItemStatus.Accepted, "Folder queued by owner.")
                        : new(request.RequestId, ExternalLaunchItemStatus.Rejected, "Folder does not exist.");
                else if (!File.Exists(request.Path))
                    result = new(request.RequestId, ExternalLaunchItemStatus.Rejected, "File does not exist.");
                else if (!ImageNavigator.IsSupported(request.Path))
                    result = new(request.RequestId, ExternalLaunchItemStatus.Rejected, "Unsupported image type.");
                else
                    result = new(request.RequestId, ExternalLaunchItemStatus.Accepted, "Image queued by owner.");

                CompletedRequests[request.RequestId] = result;
                if (result.Status == ExternalLaunchItemStatus.Accepted)
                    PendingRequests.Enqueue(request);
                results.Add(result);
            }
            TrimCompletedLocked();
        }
        return results;
    }

    private static void TrimCompletedLocked()
    {
        if (CompletedRequests.Count <= 4096) return;
        foreach (var key in CompletedRequests.Keys.Take(1024).ToArray()) CompletedRequests.Remove(key);
    }

    private static void SchedulePendingDispatch()
    {
        lock (Gate)
        {
            if (_dispatchScheduled || PendingRequests.Count == 0) return;
            _dispatchScheduled = true;
        }
        Dispatcher.UIThread.Post(() => _ = ProcessPendingQueueAsync(), DispatcherPriority.Send);
    }

    private static async Task ProcessPendingQueueAsync()
    {
        try
        {
            while (true)
            {
                ExternalLaunchRequest request;
                lock (Gate)
                {
                    if (PendingRequests.Count == 0)
                    {
                        _dispatchScheduled = false;
                        return;
                    }
                    request = PendingRequests.Dequeue();
                }

                try
                {
                    var receiver = SelectReceiver();
                    if (receiver is null)
                    {
                        // Ownership was acknowledged only because a live owner existed. If all windows
                        // disappeared before UI dispatch, keep the failure explicit in diagnostics/state;
                        // the request ID remains deduplicated rather than being opened twice by a retry.
                        lock (Gate)
                            CompletedRequests[request.RequestId] = new(request.RequestId, ExternalLaunchItemStatus.Failed, "Owner lost all receiver windows before dispatch.");
                        continue;
                    }

                    ExternalLaunchItemResult result = request.Kind switch
                    {
                        'D' => await receiver.HandleExternalFolderOpenAsync(request.RequestId, request.Path),
                        'A' => await receiver.HandleExternalActivateAsync(request.RequestId),
                        _ => await receiver.HandleExternalOpenAsync(request.RequestId, request.Path, receiver.CurrentExternalOpenBehavior)
                    };
                    if (result.Status != ExternalLaunchItemStatus.Accepted)
                    {
                        lock (Gate) CompletedRequests[request.RequestId] = result;
                    }
                }
                catch (Exception ex)
                {
                    lock (Gate)
                        CompletedRequests[request.RequestId] = new(request.RequestId, ExternalLaunchItemStatus.Failed, ex.GetType().Name + ": " + ex.Message);
                }
            }
        }
        catch
        {
            lock (Gate) _dispatchScheduled = false;
        }
    }
}
