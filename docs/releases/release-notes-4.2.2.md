# Glide 4.2.2

Stability and responsiveness release. Compiled, tested and packaged on Windows x64.

## Fixes

### 1. Crash when closing or minimizing a window with multiple image tabs

**Symptom:** with the default configuration (`Speed Boost` enabled, `Confirm closing multiple tabs`
enabled, remembered choice `Ask`), closing or minimizing a Glide window that contains more than one
image tab could terminate the process with:

```
System.InvalidOperationException: Cannot show window with non-visible owner.
   at Avalonia.Controls.Window.EnsureParentStateBeforeShow(Window owner)
   at Glide.App.MainWindow.ShowMultiTabCloseDialogAsync()
```

**Cause:** the internal Speed Boost standby path hides the HWND and then posts `Close()`. The
re-entrant `Closing` handler then ran the multi-tab confirmation, calling `ShowDialog` with a hidden
owner. The handler is `async void`, so the escaped exception crashed the process.

**Fix:** the multi-tab confirmation is skipped for standby and any hidden-owner teardown; the owned
dialog helpers refuse to show with a non-visible owner; and the confirmation branch catches and logs
failures instead of allowing an unhandled exception.

### 2. Settings search responsiveness

Search rebuilt a live editor for every match on every keystroke — up to ~200 controls, measured at
23–39 ms for common single letters (`o`, `e`, `s`) on a CPU-only headless host, before real Skia
rendering. Typing is now debounced by 140 ms; the last query always renders, and the diagnostic
capture flushes explicitly so evidence screenshots stay correct. A per-hotkey list allocation in the
filter was also removed.

### 3. Duplicate "Open" buttons in search results

Action settings that are not backed by a named control (Central command registry, gesture matrix)
rendered both a generic "Open" fallback and a labelled "Open" companion. Action rows now render only
the companion button(s).

### 4. Maximum-quality performance profile

`Maximum quality` reused Balanced's 1800 preview side, so the built-in `profile_coordination`
self-test failed and every diagnostic export reported `OVERALL FAIL`. Maximum quality now uses a
2400 preview side, restoring the intended speed < balanced < quality ordering.

### 5. Self-audit false positive

`escape.resetRememberedClose` is disabled by design while the remembered Escape choice is "Ask". The
control-effect audit no longer labels it "unexpectedly disabled".

## Release-engineering cleanup

- Build banners, deliverable names (`Glide-<version>.exe`, `Glide-<version>-Portable.zip`) and
  diagnostic fixture identity are derived from a single version source (`Directory.Build.props`).
- All prior versioned portable outputs are cleaned generically, so mislabeled archives cannot
  accumulate.
- Version, assembly, file, installer and UI metadata advanced to 4.2.2.
- Stale "Glide 3.0" strings replaced in the startup crash log, diagnostic bundle header and the
  native overlay window class name.
- The headless UI test harness now blocks until dispatched work completes, so UI tests cannot pass
  by silently skipping their body.
- Added `CHANGELOG.md` and restored the README screenshot gallery under `docs/screenshots/`.

## Verification

| Check | Result |
|---|---|
| Managed build (Release) | 0 errors |
| Glide.Core.Tests | all passed |
| Glide.Input.Tests | all passed |
| Headless diagnostics self-test | structural PASS |
| Live diagnostic export | OVERALL PASS (no nested FAIL) |
| Navigation benchmark (240 files) | sub-frame presentation retained |
| Installer | `dist-installer\Glide Setup.exe` built and PE-validated |
