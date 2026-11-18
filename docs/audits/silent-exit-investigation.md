# Silent exit on launch (5.0.7) — investigation

Date: 2026-11-18. Scope: the installed 5.0.7 twice "started and vanished": the process ran, then exited with no
window, no process left, no crash log and no Windows event; the next launch worked.

1. Right after installing 5.0.7 (the installer's "Launch Glide" postinstall step may have overlapped a manual launch).
2. After using whole-app Overlay mode and exiting Glide, the next launch(es) vanished.

User settings: ReuseSingleInstance=true, SpeedBoostEnabled=true, ShowTrayIcon=true, OverlayAlwaysOnTop=true,
RememberWindowPlacement=true.

## How a launch can end without a window (code reading, 5.0.7)

`Program.Main` exits with code 0 **without writing anything** in these cases:

| # | Where | Condition |
|---|-------|-----------|
| A | `TryForwardToExisting(batch)` succeeded | A running owner acknowledged the request ('A' = activate for a plain launch). The launcher trusts the ACK and exits. If the owner then never shows a window, nothing does. |
| B | after the 2 s second handshake | `forwardBatch is not null && ExistingProcessPresent()` → `return 0`. Any owner that is slow (cold start not yet at `Opened`), hung, or **exiting but still holding the presence event** made the launch vanish. |
| C | final re-election | `ClaimProcessPresence()` lost → `return 0`. The presence event survives as long as *any* handle is open, including another launcher's momentary `OpenExisting` probe, so two launches racing a dying owner could both lose and both vanish. |
| D | `ReuseSingleInstance=false` | No forward batch, election lost → falls into C → `return 0`. With reuse off, a second launch never opened a window. |

None of these paths wrote a log, which is why the reports had no evidence.

### Root cause for occurrence 2 (Overlay mode, then exit): the dying process kept acknowledging launches

The whole-app Overlay close button called `desktop.Shutdown()` directly. Shutdown closes every window; the
window's `Closing` handler did not know the application was exiting, so with Speed Boost on
`ShouldEnterSpeedBoostStandby()` was true and it ran the standby branch: it saved the session/settings/history
(the files the user saw being written) and called `EnterSpeedBoostStandby()`, which sets
`_closeForSpeedBoostStandby`. The `Closed` handler then took the standby branch,
`ExternalLaunchBroker.DetachForStandby(this)`, which **deliberately keeps the presence event and the pipe server
alive** (that is what standby is for). But the lifetime was shutting down, so the dispatcher stopped and the process
proceeded to exit; nothing ever called `ExternalLaunchBroker.Stop()` (it had no production caller).

From that moment until the OS finally tore the process down (thread-pool/finalizer/background work can keep a
.NET process alive after `Main`'s UI loop ends), the pipe server, which runs on the thread pool, not the UI thread,
kept answering. A new launch sent 'A', got `RESULTS … Accepted`, and exited (path A). The owner's
`Dispatcher.UIThread.Post(...)` never ran because the dispatcher had shut down, and then the owner itself exited.
Result: launcher and owner both gone, no window, no crash, no event. That matches "next launch(es)" and "the
settings/session files were just written".

A smaller form of the same gap exists for tray **Exit Glide** while in standby (`desktop.MainWindow` is null, so
`desktop.Shutdown()` ran with the broker still alive) and for `ForceExit`, where presence was released only from the
`Closed` handler. Path B is the same mechanism with a launcher that did not get through the 2 s handshake.

### Occurrence 1 (right after install): best evidence, not proven

Setup `taskkill /F`s every Glide.exe and later starts one with `postinstall nowait`. A first launch after an
install is the slowest cold start (fresh files, Defender scans, codec index), so the owner holds the presence event
for longer than usual before its pipe is ready (presence is claimed before Avalonia starts; the pipe opens at the
first window's `Opened`). Any launch that overlaps that interval and does not get through within ~2.25 s fell into
path B or C and vanished. We could not confirm which path fired, because 5.0.7 had no launch log; the new
`startup-trace.log` records exactly this.

### Hypotheses checked and ruled out

- **Overlay state making startup bail** (`AlwaysStartWholeAppOverlayMode`, placement): startup never exits on these.
  `ApplyStartupWholeAppOverlayPreference` only enters Overlay; `IsSafeSavedWindowPosition` rejects off-screen
  positions (including -32000) and centers the window. Off-screen placement can hide a window but never ends the
  process.
- **Exceptions swallowed before the window shows**: `Main`'s catch writes `startup-crash.log`; dispatcher
  exceptions are handled and kept running. No such log was produced, consistent with a *clean* exit via a forward.
- **Hidden/tray instance ignoring activation**: a standby owner that is not shutting down rehydrates a window from the
  queued 'A' request (`EnsureMainWindowForPendingExternalRequest`); reproduced working below. The failure needs an
  owner that is exiting.

## Fixes (5.0.8)

1. **Shutdown stops the broker first.** New `App.BeginApplicationShutdown(reason)` (idempotent) calls
   `ExternalLaunchBroker.Stop()` (releases presence, stops the pipe) before windows are torn down. Called by the
   whole-app Overlay X (`App.ShutdownApplication`), `ForceExit`, tray Exit in standby, the lifetime's
   `ShutdownRequested`/`Exit` events, and `Program` after the UI lifetime returns.
2. **No standby during shutdown.** `ShouldEnterSpeedBoostStandby` now includes `!App.IsShuttingDown`
   (`ShouldEnterSpeedBoostStandbyPolicy`).
3. **A stopping broker never ACKs.** `AcceptOwnership` returns null while the broker is `Stopping`; the server drops
   the connection without `RESULTS`, so the launcher keeps trying and elects itself.
4. **Launchers never vanish.** `LaunchElection.Resolve` replaces paths B/C/D: it keeps forwarding (2 s attempts)
   and re-electing (every 100 ms) for up to 8 s; if the owner still has not answered, the launch starts its own
   window as a follower (the broker's existing takeover monitor claims ownership when the old owner leaves). With
   reuse off, a launch opens its own window instead of exiting. Hidden `--background` launches still exit when a Glide
   is already running.
5. **Minimized placement.** `TrackNormalWindowPlacement` now ignores a natively iconic window and Windows'
   (-32000, -32000) parking position (`IsMinimizedParkingPosition`), so minimizing can no longer overwrite the
   remembered placement.
6. **Isolated profiles are isolated instances.** When `GLIDE_SETTINGS_DIRECTORY` is set, the pipe and presence names
   are also scoped by that directory (`ProtocolNames.InstanceKey`). A test or diagnostic build can no longer forward
   to (or receive launches from) the user's real Glide. Without the variable the names are unchanged.

## Diagnosing a recurrence: `startup-trace.log`

Always on, next to `glide.settings.json` (installed: `%LOCALAPPDATA%\Glide\startup-trace.log`), trimmed to about the
last 600 lines by the owner. One line per decision, each with UTC time, `pid=` and version:

| Event | Meaning |
|-------|---------|
| `launch args=… hidden=… forceNew=… exe=…` | process start |
| `exit forwarded_to_running_owner kind=A` | handed to a running Glide (A = activate, F = file, D = folder); look for the owner's `broker_accepted` line from another pid at the same time |
| `follower_forwarded` / `follower_elected` / `owner_unresponsive` / `starting_follower_ui reason=…` | the retry loop and its result |
| `ui_process owner=…`, `first_window_opened` | this process creates the UI; a window opened |
| `broker_accepted kinds=… statuses=…` (owner) | the owner acknowledged a launcher |
| `standby_window_rehydrate` / `standby_window_rehydrate_failed …` | Speed Boost owner created (or failed to create) a window for a queued launch |
| `broker_refused_while_stopping` | an exiting owner turned a launch away (expected; the launcher then elects itself) |
| `shutdown_begin reason=…` | why the app started exiting (whole_app_overlay_close, force_exit, tray_exit_standby, shutdown_requested, lifetime_exit, lifetime_returned) |
| `exit ui_lifetime_ended … windowEverShown=…`, `exit fatal_exception …`, `unhandled_exception …` | how the process ended |

A silent exit now shows up as a launcher pid whose `exit` reason is a forward, matched to an owner pid whose lines
end without `standby_window_rehydrate`/`first_window_opened`, or as an owner with `windowEverShown=False`.

## Reproduction (own build only, isolated profile)

All runs used `dist\Glide.exe` with `GLIDE_SETTINGS_DIRECTORY` set to a scratch folder (and therefore its own
pipe/presence names), never the installed Glide or `%LOCALAPPDATA%\Glide`. No desktop UI was driven.

Script: a PowerShell harness that sets `GLIDE_SETTINGS_DIRECTORY`, computes that profile's presence name and launches
`dist\Glide.exe`; it kills only the processes it started.

**Unresponsive / dying owner (path B).** The harness creates the profile's presence event and never serves the pipe,
which is what a launcher sees while an owner is exiting or still cold-starting. 5.0.7 code exits 0 after ~2.25 s here
with no window. The fixed build:

```
12:19:46.104Z pid=18932 launch args=0 hidden=False forceNew=False
12:19:54.378Z pid=18932 owner_unresponsive attempts=4 elapsedMs=8008
12:19:54.378Z pid=18932 starting_follower_ui reason=owner_unresponsive
12:19:54.378Z pid=18932 ui_process owner=False
12:19:54.523Z pid=18932 first_window_opened
```

**Forward to a Speed Boost standby owner (the normal path still works).** Owner started with `--background`, then a
plain launch:

```
12:19:42.897Z pid=33588 launch args=0 hidden=True ...
12:19:42.908Z pid=33588 ui_process owner=True
12:19:43.085Z pid=33588 first_window_opened
12:19:45.160Z pid=21348 launch args=0 hidden=False ...
12:19:45.173Z pid=21348 exit forwarded_to_running_owner kind=A
12:19:45.173Z pid=33588 broker_accepted kinds=A statuses=Accepted
12:19:45.184Z pid=33588 standby_window_rehydrate
```

The first version of the trace showed that two processes appending at the same moment (`FileMode.Append` is
seek+write, not atomic) overwrote each other's line. Appends are now serialized with a per-file named mutex.

**The dying-owner ACK (path A, occurrence 2)** needs the whole-app Overlay X clicked, which means driving the UI, so
it is covered by code reading plus unit tests (`BeginApplicationShutdown` releases presence and blocks standby; a
`Stopping` broker refuses requests) rather than an end-to-end run. Manual check for the user: enter whole-app Overlay,
click its X, immediately launch Glide again; a window must appear, and `startup-trace.log` should show
`shutdown_begin reason=whole_app_overlay_close` for the old pid and `ui_process owner=True` for the new one.

## Tests

`tests/Glide.Core.Tests/SilentExitRegressionTests.cs`: unresponsive owner → follower UI (never exit); lingering
presence handle → retried until elected; slow cold owner → forwarded on a later attempt; reuse off → own window,
hidden duplicate → exit; stopping broker refuses requests; `BeginApplicationShutdown` releases presence and blocks
standby; isolated profile names; minimized parking position; trace format and trimming.
