using System.Diagnostics;
using System.IO.Pipes;
using System.Security.Principal;
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
    // Security (R5): the pipe and presence names carry a hash of the current user's SID, at both ends.
    // A machine-global name let another account create "Glide3.ExternalOpen.v2" first and block this
    // user's single-instance forwarding (images then did not open); CurrentUserOnly (below) already
    // stopped it from receiving anything.
    // GLIDE_SETTINGS_DIRECTORY (an isolated profile) also scopes both names: see ProtocolNames.InstanceKey.
    internal static readonly string PipeName = ProtocolNames.UserScoped(ProtocolNames.PipeName, InstanceKey());
    // v3+ is an EventWaitHandle sentinel rather than a thread-affine mutex. Creation is the process
    // election primitive; followers close their handle immediately, so the sentinel disappears when
    // the elected owner releases/exits and another live Glide process can take over.
    private static readonly string PresenceName = ProtocolNames.UserScoped(ProtocolNames.PresenceName, InstanceKey());

    // 5.1.7 (issue #5): test hosts, portable copies and dev builds no longer share the installed Glide's family
    // (see ProtocolNames.ScopedInstanceKey).
    private static string InstanceKey() =>
        ProtocolNames.ScopedInstanceKey(CurrentUserKey(), Environment.GetEnvironmentVariable("GLIDE_SETTINGS_DIRECTORY"),
            AppContext.BaseDirectory, IsInstalledCopy(), IsForeignHost(), Environment.ProcessId);

    private static bool IsInstalledCopy()
    {
        try { return File.Exists(Path.Combine(AppContext.BaseDirectory, "installed.flag")); }
        catch { return false; }
    }

    /// <summary>True when Glide.App is hosted by another program (a test host). An unknown entry assembly counts as Glide.</summary>
    internal static bool IsForeignHost()
    {
        var entry = System.Reflection.Assembly.GetEntryAssembly()?.GetName().Name;
        return entry is not null && !string.Equals(entry, typeof(ExternalLaunchBroker).Assembly.GetName().Name, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>True while this process is the elected single-instance owner (holds the presence object). Only the owner
    /// may stay resident in Speed Boost standby or show the Speed Boost tray icon.</summary>
    internal static bool IsPresenceOwner
    {
        get { lock (Gate) return _ownsPresence; }
    }
    private static string EffectivePresenceName =>
        AppContext.BaseDirectory.Contains("Glide.Core.Tests", StringComparison.OrdinalIgnoreCase)
            ? $"{PresenceName}.{Environment.ProcessId}"
            : PresenceName;

    /// <summary>The current user's SID (stable across sessions and renames); the user name as a fallback.</summary>
    internal static string CurrentUserKey()
    {
        if (OperatingSystem.IsWindows())
        {
            try
            {
                using var identity = WindowsIdentity.GetCurrent();
                if (identity.User?.Value is { Length: > 0 } sid) return sid;
            }
            catch { /* identity lookup failed: the caller falls back to the user name */ }
        }
        return Environment.UserDomainName + "\\" + Environment.UserName;
    }
    private const string ProtocolHeader = ProtocolNames.ProtocolHeader;
    // A ready warm broker normally accepts the connection immediately. This deadline is only a
    // bounded safety net for the owner-startup/pipe-replacement race; it is not a hot-path delay.
    private static readonly TimeSpan ClientDeadline = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan ServerPeerDeadline = TimeSpan.FromSeconds(2);
    private const int MaxProtocolLineChars = 128 * 1024;

    // Security: pipe names are machine-global, so another account could create "Glide3.ExternalOpen.v2"
    // first (or in the gap between two server instances) and receive the paths users open. With
    // CurrentUserOnly the client refuses a pipe not owned by the current user before writing anything,
    // and the server's pipe grants access to the current user only (previously Everyone/Anonymous could
    // read). 5.0 also scopes the pipe and presence names to the user (see PipeName), so 4.x and 5.x
    // builds no longer forward to each other; each version elects and forwards within its own kind.
    // 5.1.7: CurrentUserOnly compared token OWNERS (Administrators for an elevated token), so an elevated launch
    // could not forward to the user's normal Glide. Both ends now check the logon user SID + session instead
    // (PipePeerIdentity): the server pipe's DACL grants the user's SID only, the client verifies the server process.
    internal const PipeOptions ClientPipeOptions = PipeOptions.Asynchronous;
    internal const PipeOptions ServerPipeOptions = PipeOptions.Asynchronous;
    // Set when the last forward found an owner this process must not use (other user/session, normal -> elevated,
    // access denied): the launcher then opens its own window at once instead of retrying for the whole budget.
    private static volatile bool _ownerIncompatible;
    internal static bool LastOwnerIncompatible => _ownerIncompatible;
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool AllowSetForegroundWindow(int dwProcessId);
    private static readonly object Gate = new();
    // The windows that receive externally opened paths live in GlideWindowRegistry (one window list per process).
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
                var candidate = new EventWaitHandle(false, EventResetMode.ManualReset, EffectivePresenceName, out var createdNew);
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
            var candidate = new EventWaitHandle(false, EventResetMode.ManualReset, EffectivePresenceName, out var createdNew);
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
            using var existing = EventWaitHandle.OpenExisting(EffectivePresenceName);
            return existing is not null;
        }
        catch { return false; }
    }

    private static bool WaitForReadyPresence(TimeSpan timeout)
    {
        if (!OperatingSystem.IsWindows()) return true;
        try
        {
            using var existing = EventWaitHandle.OpenExisting(EffectivePresenceName);
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
        _ownerIncompatible = false;
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
                await using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut, ClientPipeOptions);
                await client.ConnectAsync(attemptCts.Token).ConfigureAwait(false);
                if (OperatingSystem.IsWindows() &&
                    !PipePeerIdentity.ClientAccepts(PipePeerIdentity.Self(), PipePeerIdentity.Server(client)))
                {
                    _ownerIncompatible = true; // nothing has been written; never send paths to this owner
                    return false;
                }
                if (OperatingSystem.IsWindows())
                {
                    try { AllowSetForegroundWindow(-1); } catch { /* foreground permission is best effort */ }
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
            catch (UnauthorizedAccessException)
            {
                // The owner's pipe DACL excludes this token (e.g. an elevated owner's older ACL): not transient.
                _ownerIncompatible = true;
                return false;
            }
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
            Glide.App.Platform.GlideWindowRegistry.SetLaunchReceiver(window, true);
            _activeWindow = new WeakReference<MainWindow>(window);

            if (!_ownsPresence && !TryTakeOverPresenceLocked())
            {
                EnsureTakeoverMonitorLocked();
                return;
            }
            EnsureServerStartedLocked();
            SchedulePendingDispatch();
        }
    }

    /// <summary>Removes a render window while keeping this process' single-instance pipe alive.</summary>
    public static void DetachForStandby(MainWindow window)
    {
        lock (Gate)
        {
            Glide.App.Platform.GlideWindowRegistry.SetLaunchReceiver(window, false);
            if (_activeWindow?.TryGetTarget(out var active) == true && ReferenceEquals(active, window))
                _activeWindow = null;
            // Deliberately keep presence/server ownership. Requests remain queued until the next
            // window registers, at which point Start schedules dispatch again.
        }
    }

    public static void MarkActive(MainWindow window)
    {
        lock (Gate)
        {
            if (Glide.App.Platform.GlideWindowRegistry.LaunchReceivers().Any(existing => ReferenceEquals(existing, window)))
                _activeWindow = new WeakReference<MainWindow>(window);
        }
    }

    public static void Unregister(MainWindow window)
    {
        CancellationTokenSource? stop = null;
        Task? serverTask = null;
        lock (Gate)
        {
            Glide.App.Platform.GlideWindowRegistry.SetLaunchReceiver(window, false);
            if (_activeWindow?.TryGetTarget(out var active) == true && ReferenceEquals(active, window))
                _activeWindow = null;
            if (Glide.App.Platform.GlideWindowRegistry.LaunchReceivers().Count == 0)
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
            Glide.App.Platform.GlideWindowRegistry.ClearLaunchReceivers();
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
                    if (Glide.App.Platform.GlideWindowRegistry.LaunchReceivers().Count == 0 || _ownsPresence) return;
                    if (!TryTakeOverPresenceLocked()) continue;
                    EnsureServerStartedLocked();
                }
                // This process is now the owner: it hosts the Speed Boost tray icon from here on.
                Dispatcher.UIThread.Post(() => App.UpdateTrayIconState());
                return;
            }
        }
        catch (OperationCanceledException) { /* cancellation is the expected way to stop this work */ }
    }

    private static void StopTakeoverMonitorLocked()
    {
        var cts = _takeoverCts;
        _takeoverCts = null;
        _takeoverTask = null;
        try { cts?.Cancel(); } catch { /* best-effort teardown; the resource may already be closed */ }
        cts?.Dispose();
    }

    private static void ReleasePresenceLocked()
    {
        try { _presence?.Reset(); } catch { /* best-effort teardown; the resource may already be closed */ }
        try { _presence?.Dispose(); } catch { /* best-effort teardown; the resource may already be closed */ }
        _presence = null;
        _ownsPresence = false;
        if (_state != BrokerState.Stopping) _state = BrokerState.Follower;
    }

    private static void StopServerBounded(CancellationTokenSource cts, Task? task)
    {
        try { cts.Cancel(); } catch { /* best-effort teardown; the resource may already be closed */ }
        if (task is not null)
        {
            try { task.Wait(TimeSpan.FromSeconds(2)); } catch { /* bounded wait during shutdown; the task is abandoned */ }
        }
        cts.Dispose();
    }

    public static MainWindow? FindWindowContainingImage(string path)
    {
        lock (Gate)
        {
            var receivers = Glide.App.Platform.GlideWindowRegistry.LaunchReceivers();
            for (var i = receivers.Count - 1; i >= 0; i--)
                if (receivers[i].ContainsOpenImage(path)) return receivers[i];
            return null;
        }
    }

    private static MainWindow? SelectReceiver()
    {
        lock (Gate)
        {
            var receivers = Glide.App.Platform.GlideWindowRegistry.LaunchReceivers();
            if (_activeWindow?.TryGetTarget(out var active) == true && receivers.Any(item => ReferenceEquals(item, active)))
                return active;
            return receivers.Count > 0 ? receivers[^1] : null;
        }
    }

    private static NamedPipeServerStream CreateServerPipe()
    {
        if (OperatingSystem.IsWindows())
        {
            using var identity = WindowsIdentity.GetCurrent();
            if (identity.User is { } user)
                return NamedPipeServerStreamAcl.Create(PipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
                    ServerPipeOptions, 0, 0, PipePeerIdentity.BuildServerSecurity(user));
        }
        return new NamedPipeServerStream(PipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
            ServerPipeOptions | PipeOptions.CurrentUserOnly);
    }

    private static async Task ServerLoopAsync(CancellationToken token)
    {
        var readinessPublished = false;
        while (!token.IsCancellationRequested)
        {
            try
            {
                // User-SID-only pipe ACL (CreateServerPipe / PipePeerIdentity.BuildServerSecurity).
                await using var server = CreateServerPipe();
                if (!readinessPublished)
                {
                    lock (Gate)
                    {
                        if (_ownsPresence)
                        {
                            try { _presence?.Set(); } catch { /* best-effort teardown; the resource may already be closed */ }
                            _state = BrokerState.Ready;
                        }
                    }
                    readinessPublished = true;
                }

                await server.WaitForConnectionAsync(token).ConfigureAwait(false);
                // Peer gate (PipePeerIdentity.ServerAccepts): same session; an elevated owner only serves the
                // same user's elevated launches. The DACL already limits connections to the user's SID.
                if (OperatingSystem.IsWindows() &&
                    !PipePeerIdentity.ServerAccepts(PipePeerIdentity.Self(), PipePeerIdentity.Client(server)))
                {
                    GlidePerformanceTrace.Mark("ipc_peer_rejected");
                    continue;
                }
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
                    // Server-side allowlist: Kind must be exactly 'A' (activate), 'D' (directory),
                    // or 'F' (file). Anything else is rejected before any filesystem touch.
                    if (parts.Length != 3 || parts[0].Length != 1 || (parts[0][0] != 'F' && parts[0][0] != 'D' && parts[0][0] != 'A') ||
                        !Guid.TryParseExact(parts[1], "N", out var requestId)) { malformed = true; break; }
                    try
                    {
                        var rawPath = Encoding.UTF8.GetString(Convert.FromBase64String(parts[2]));
                        var kind = parts[0][0];
                        string normalizedPath;
                        if (kind == 'A')
                        {
                            // Activate carries no path; reject smuggled payloads.
                            if (!string.IsNullOrEmpty(rawPath)) { malformed = true; break; }
                            normalizedPath = string.Empty;
                        }
                        else
                        {
                            if (string.IsNullOrWhiteSpace(rawPath)) { malformed = true; break; }
                            // Canonicalize to defeat "..", mixed separators, and UNC tricks;
                            // AcceptOwnership re-checks existence/type after this.
                            normalizedPath = Path.GetFullPath(rawPath);
                            if (!Path.IsPathRooted(normalizedPath)) { malformed = true; break; }
                        }
                        requests.Add(new ExternalLaunchRequest(kind, normalizedPath, requestId));
                    }
                    catch { malformed = true; break; }
                }
                if (malformed || requests.Count != count) continue;

                StartupTrace.Write("broker_received", $"items={requests.Count}");
                var results = AcceptOwnership(requests);
                if (results is null)
                {
                    // Shutting down: drop the connection without RESULTS so the launcher retries and
                    // elects itself rather than trusting an owner that will never show a window.
                    StartupTrace.Write("broker_refused_while_stopping", $"items={requests.Count}");
                    continue;
                }
                await WriteLineAsync(writer, $"RESULTS\t{batchId:N}\t{requests.Count}", peerCts.Token).ConfigureAwait(false);
                foreach (var result in results)
                {
                    var message = Convert.ToBase64String(Encoding.UTF8.GetBytes(result.Message ?? string.Empty));
                    await WriteLineAsync(writer, $"{result.RequestId:N}\t{result.Status}\t{message}", peerCts.Token).ConfigureAwait(false);
                }
                StartupTrace.Write("broker_accepted", "kinds=" + new string(requests.Select(r => r.Kind).ToArray()) +
                    " statuses=" + string.Join(",", results.Select(r => r.Status)));
                SchedulePendingDispatch();
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
            catch (OperationCanceledException) { /* cancellation is the expected way to stop this work */ }
            catch
            {
                try { await Task.Delay(50, token).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
            }
        }
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1859:Use concrete types when possible for improved performance",
        Justification = "Cold path (one call per forwarded launch batch); the read-only shapes document that the batch is not mutated.")]
    internal static IReadOnlyList<ExternalLaunchItemResult>? AcceptOwnership(IReadOnlyList<ExternalLaunchRequest> requests)
    {
        var results = new List<ExternalLaunchItemResult>(requests.Count);
        lock (Gate)
        {
            // A stopping broker must never take ownership of work: the process is exiting.
            if (_state == BrokerState.Stopping) return null;
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

    /// <summary>
    /// True only while the broker still owns work which requires a render window.  Window
    /// rehydration retries must consult this instead of relying on an earlier queue snapshot: a
    /// different receiver can consume the request while a standby-close retry is waiting.
    /// </summary>
    internal static bool HasPendingExternalRequests()
    {
        lock (Gate) return PendingRequests.Count != 0;
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
                        // Standby intentionally has no render window. Leave ownership queued until a
                        // fresh window is created; the process-level broker remains authoritative.
                        lock (Gate)
                        {
                            PendingRequests.Enqueue(request);
                            _dispatchScheduled = false;
                        }
                        App.EnsureMainWindowForPendingExternalRequest();
                        return;
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
