# Glide.Diagnostics

**Owns:** truthful, machine-readable structural evidence about Glide's runtime/build state and export format.  
**Must not own:** UI feature behavior, image decoding, workspace state mutation, or hiding failures.

## Current contract

`DiagnosticRunner` provides the headless `--diagnostics self-test` build gate and export files. It verifies assertions that can be proven without a live Avalonia visual tree, including settings-schema integrity, typed workspace transfer, the hotkey and gesture contracts (the 75 persisted hotkey row ids and 56 persisted gesture action ids, which are projections of the master action registry in `Glide.Core/Commands/AppActionRegistry.cs`), overlay state/layout and the 196-suffix capability registry. Live-only checks remain `SKIP`/`WARN`; structural PASS is never visual certification.

Exports include `codec_capabilities.json`, which reports recognised/routed breadth separately from guaranteed decode, and `codec_providers.json`, which reports only providers already verified in the current process. Exporting this evidence does not enumerate, hash or load optional providers.

`Glide.App/Diagnostics/UiDiagnosticCapture` extends the same evidence model when real windows exist. Developer Options export captures main-window screenshots, every Settings category immediate/settled, the editable search fixture, resize scenarios, logical/physical geometry, clipping/hit-target audits, control inventory, settings/effect state, runtime state and the application behaviour trace.

Exports are written to the Windows Downloads known folder. Keep diagnostic wording synchronized with actual implementation state and never upgrade a live-only claim to PASS without live evidence.

## Live diagnostic trace (opt-in)

`LiveDiagnosticTrace` is a flushed-per-line breadcrumb file for problems that need a live repro. It is off by default
(privacy contract: nothing is written to Downloads unless asked).

Enable it for one run, after fully exiting Glide (tray / Speed Boost standby included, otherwise the running owner
process receives the launch and the switch is ignored):

```
"%ProgramFiles%\Glide\Glide.exe" --diagnostic-trace      (or the portable Glide.exe)
```

or set `GLIDE_DIAGNOSTIC_TRACE=1` for the process. Output: `%USERPROFILE%\Downloads\Glide-Diagnostic-Latest.txt`
(`GLIDE_DIAGNOSTIC_TRACE_DIR` overrides the folder; the file rotates at 2 MB). `LiveDiagnosticTrace.IsEnabled` lets hot
paths skip building payloads when it is off.

Title-bar / top-edge entries (5.0.5):

- `win32.nchittest_top`: the WM_NCHITTEST answer for the top band of the window (`hit` 1 = HTCLIENT, 12 = HTTOP,
  8/9/20 = min/max/close, ...), with the winning rule (`reason`: `resize`, `top_band_control`, `caption_*`,
  `title_actions`, `fullscreen_*`, or `defer` = Avalonia/DefWindowProc answered), the row (`rowFromTop`), window rect,
  monitor top and DPI scale. Logged only when the answer, rule or row changes, so a flickering hover shows as
  alternating lines.
- `titlebar.chrome_pressed_tunnel` / `chrome_pressed_bubble` / `chrome_released` / `chrome_capture_lost`: every pointer
  press/release on the title bar with the routed source, whether it counted as an interactive control, the captured
  element, `handled`, the window-DIP and physical Y, the position relative to the "+" (`plusX`, `plusY`, `overPlus`)
  and the "+" pressed state / RenderTransform.
- `titlebar.plus_entered` / `plus_exited` / `plus_capture_lost` / `plus_click`: hover, capture and Click of the "+".
  A press on the "+" followed by `chrome_released` without `plus_click` pinpoints where the click was lost.
