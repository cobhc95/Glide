# Code hygiene audit (Glide 5.0.8)

Date: 2026-11-18. Scope: `src/`, `native/` (excluding `third_party/`), `tests/`, `tools/`, `build.cmd`, `Directory.Build.props`.
Method: read-only static inspection (grep and small scripts). Nothing was built, run or edited.
Size: 505 C#/C++ source files, about 141k lines (including tests and tools).

Severity: **H** = causes or hides real defects or lost coverage; **M** = maintenance cost that slows every change; **L** = polish.
Effort: **S** < 1 h, **M** = half a day to 2 days, **L** = multi-day.

## Top 10 (do these first)

| # | Finding | Sev | Effort |
|---|---------|-----|--------|
| 1 | `Glide.Explorer.Tests` (20 files, about 125 tests) is not in `Glide.sln`, so `build.cmd` never builds or runs it (T1) | H | S |
| 2 | Settings to control mapping is written three times (LoadControls, ReadControls, BuildSettingControlMap) with defaults repeated as literals (D1) | H | L |
| 3 | `async void NativeExplorerFileActivated` has no try/catch (E1) | M | S |
| 4 | Warning noise: about 930 unique / about 2000 total analyzer warnings hide new ones; no SARIF baseline (W0 to W3) | M | M |
| 5 | Tests reflect into private members of Glide types by string name (61 calls) even though `InternalsVisibleTo` already exists (T3) | M | M |
| 6 | Sleep-then-assert tests (`Task.Delay(300)` then assert position/state) are flaky by design (T2) | M | M |
| 7 | `MainWindow` is one class spread over 32 partial files / 16.5k lines; `SettingsWindow` 13 partials / 6.3k lines (S1, S2) | M | L |
| 8 | Confirmed dead code: 8 unreferenced public/internal members including an attached property nobody sets (X1) | L | S |
| 9 | 133 `DllImport` vs 38 `LibraryImport`, 25 Win32 functions declared 2 to 3 times in different files (I1, I2) | M | M |
| 10 | 93 copies of `try { Directory.Delete(dir, true); } catch { }` plus several private temp-folder helpers in tests (T4) | L | S |

Counts by severity: **H 3, M 17, L 18** (38 findings).

---

## 1. Test hygiene

**T1. Explorer test project never runs.** H / S
`Glide.sln` lists Core, Imaging, Diagnostics, App, Input, ThumbnailCodecs, Core.Tests and Input.Tests only. `tests/Glide.Explorer.Tests/Glide.Explorer.Tests.csproj` is missing, and `build.cmd:300,316` (`dotnet build/test Glide.sln`) is the only test entry point. `docs/NET10-MIGRATION-PLAN.md:54` already records this. Tests such as `ExplorerTabOperationsTests`, `TitleBarTopEdgeClickTests` and `ExplorerNavigationHistoryTests` can rot without anyone noticing.
Fix: `dotnet sln add tests\Glide.Explorer.Tests\Glide.Explorer.Tests.csproj`. If it is too slow for every build, give it a trait and have `build.cmd` run it on full (non-quick) builds.

**T2. Fixed sleeps used as synchronisation.** M / M
- `tests/Glide.Core.Tests/OverlayVideoTests.cs:113,117` waits `Task.Delay(300/200)`, then asserts `Position >= 1.4` / `!Playing`.
- `tests/Glide.Core.Tests/ImageLoadCoordinatorLifetimeTests.cs:266,302,306` waits `Task.Delay(300)` to "let in-flight neighbour decodes finish".
- `tests/Glide.Core.Tests/NavigationSkipHintTests.cs:330` waits `Task.Delay(400)` to break a key chain. That is wall-clock time standing in for the production rate limit.
- `tests/Glide.Core.Tests/SvgDecoderTests.cs:108` `Thread.Sleep(200)`; `PrintWindowResponsivenessTests.cs:353`; `Glide.Explorer.Tests/TitleBarTopEdgeClickTests.cs:151` (`Thread.Sleep(120)` for a Fluent transition).
- 131 `Task.Delay` in tests overall (top files: TabHoverPreviewTests, SettingsWheelToolTipTests and ImageLoadCoordinatorLifetimeTests, 6 each).

Fix: replace each with a poll-until helper (`await Eventually(() => cond, timeout: 5s)`), or with an awaitable the code under test exposes (for example a `WhenIdleAsync()` on ImageLoadCoordinator). Inject a clock (`TimeProvider`) where the code times things itself (skip hint, rate limit, tooltips).

**T3. Reflection into Glide private members by string name.** M / M
61 `GetField/GetMethod/GetProperty("...")` calls. Hotspots: `EndOfFolderRenderTests.cs` (10), `UpdateUiTests.cs` (7), `SettingsSearchCoverageTests.cs` (7), `GestureDropdownRenderTests.cs` (7), `PrintPreviewPerfHarness.cs:186-188` (`"_portraitRadio"`, `"_scalingCombo"`). Most-used targets are `RunSearchRebuild` (5), `ReadControls` (5) and `_settingControls` (3). A rename compiles and only fails at test runtime with a NullReferenceException.
Fix: `Glide.App/Properties/AssemblyInfo.cs` already has `InternalsVisibleTo` for both test projects, so make those members `internal` and call them directly.
Framework internals: `AvaloniaHeadlessFixture.cs:96-114` reflects into `Avalonia.AvaloniaLocator`/`IDispatcherImpl`. It is documented and fails loudly, which is acceptable, but pin it with a test that names the Avalonia version, and recheck it on every Avalonia bump.

**T4. Repeated temp-directory boilerplate.** L / S
`try { Directory.Delete(folder, true); } catch { }` appears 93 times (for example `NavigationHistoryWindowTests.cs:164,219,250,275,334,361`). There are 119 `Path.GetTempPath` call sites. Temp helpers have been written several times: `NavigationHistoryWindowTests.cs:595 TempFolder`, a disposable in `BackupCardUiTests.cs`, and `ResetTestSupport.cs`.
Fix: one shared `sealed class TempDir : IDisposable` (unique folder, best-effort recursive delete with retry) in a `TestSupport/` folder. Replace the copies mechanically.

**T5. Test projects named after the wrong layer.** L / M
`Glide.Core.Tests` holds 165 files, most of them `Glide.App` UI tests (headless Avalonia windows, SettingsWindow, Print). Release-named files (`Release503Tests.cs`, `Release506Tests.cs`, `Glide22BoundaryTests.cs`, `Glide22CapabilityTests.cs`, `AuditItemsTests.cs`) group tests by when they were written, not what they cover.
Fix: over time, move UI tests to a `Glide.App.Tests` project (or rename). Rename release-named files by feature when they are next touched.

**T6. Opt-in perf harness shows up as passing tests.** L / S
`tests/Glide.Core.Tests/PrintPreviewPerfHarness.cs:47,88,166` are `[Fact]`s that return immediately unless `GLIDE_PRINT_PERF=1`, so they report green without running.
Fix: a `[PerfFact]` attribute that sets `Skip` when the variable is absent, so the run shows them as skipped.

**T7. Registry writes under HKCU in tests.** L / S
`ResetExecutorTests.cs:281-316` creates and deletes keys under `HKCU\Software\GlideResetTests`. The cleanup at :316 swallows all errors, so a failed run leaves keys behind silently.
Fix: give the key name a GUID suffix, and log (do not throw) on cleanup failure.

Hard-coded paths: only synthetic literals (`@"C:\pics\A.jpg"` in `AsyncRequestIdentityTests.cs:12`, `CrossTabHistoryTests.cs`) used as pure strings. That is fine; no machine-specific paths were found in tests or tools.

## 2. Duplicated logic

**D1. Three hand-written settings and control maps.** H / L
`src/Glide.App/Settings/SettingsWindow.axaml.cs:409` `LoadControls` (266 lines) and `:721` `ReadControls` (253 lines) map each setting to its control in both directions, and `BuildSettingControlMap` (required by AGENTS.md) maps them a third time. Defaults are repeated as literals that can drift from `SettingsDefaults`: `"First image"` (:423 and :737), `"Both"` (:425), `HistorySizeBox.Value ?? 8` (:732), `Math.Clamp(..., 0, 600000)` (:424).
Fix: one table of `SettingBinding(id, control, load: s => ..., read: (s, c) => ...)` built next to `BuildSettingControlMap`. LoadControls and ReadControls then become loops, and defaults come from `SettingsDefaults` and `SettingsCatalog`. Extend `tools/audit-settings-search.py` to flag any control missing from the table.

**D2. Duplicated P/Invoke declarations.** M / S
25 Win32 entry points are declared in 2 or 3 files: `DwmSetWindowAttribute`, `SetWindowPos`, `SHChangeNotify` and `IsWindowVisible` three times each; the clipboard set (`OpenClipboard/EmptyClipboard/SetClipboardData/CloseClipboard/GlobalAlloc/Lock/Unlock/Free`) in both `Platform/WindowsClipboard.cs` and `Explorer/ExplorerFileClipboard.cs`; `GlideFreeImageBuffer` twice.
Fix: an `internal static partial class NativeMethods` per DLL (User32, Dwm, Shell32, Kernel32, GlideNative) in `Glide.App/Platform/Interop/`. Merge the two clipboard implementations, which also do the same open-retry `Thread.Sleep` loop (`WindowsClipboard.cs:131`, `ExplorerFileClipboard.cs:232`).

**D3. Imaging sources compiled into two assemblies.** L / S
`src/Glide.ThumbnailCodecs/Glide.ThumbnailCodecs.csproj:60-77` links 18 `Glide.Imaging` files (`BuiltInRasterDecoder*.cs`, `ExrDecoder.cs` and others) into the NativeAOT thumbnail DLL. This is deliberate (no Avalonia dependency), but each file builds twice under different `NoWarn` sets (:48), so warning counts and `#if` drift differ by project.
Fix: keep it, but put the shared decoders in a `Glide.Imaging.Decoders` folder or a `Shared.projitems` so the boundary is explicit, and add a comment in each linked file's header.

**D4. Repeated colour literals.** L / S
57 inline `Color.Parse("#...")/FromRgb` calls (for example `#FF6B6B` 6 times; `EndOfFolderPickerCard.cs` 8, `PrintPreviewControl.cs` 7, `WindowInWindowOverlayManager.cs` 7). They bypass theme resources, so light and dark modes cannot retheme them.
Fix: move them to `App.axaml` resource keys, or to a `GlidePalette` static class.

## 3. Swallowed exceptions

Context: `src/` has 366 untyped `catch` blocks and 181 `catch (Exception)`. 147 of the bare catches carry a one-line justification comment (for example `ExternalLaunchBroker.cs:429-447`). That is acceptable for best-effort teardown. The ones below have no comment, or swallow more than teardown.

**E1. Unprotected `async void`.** M / S
`src/Glide.App/MainWindow.Pickers.cs:311` `private async void NativeExplorerFileActivated(string path)` awaits `OpenAsImageTabAsync` with no try/catch. A fault reaches the dispatcher's unhandled-exception path. Compare `SettingsWindow.Hotkeys.cs:561`, which wraps the same pattern correctly. `MainWindow.Chrome.cs:68` `UpdateFolderCounter` only protects its `Task.Run` body.
Fix: wrap the body in try/catch with a `LiveDiagnosticTrace` log, or make it `async Task` and fire-and-forget through a shared `RunSafe(Func<Task>)` helper. Optionally add a Roslyn rule or grep to `audit-*.py` that rejects `async void` unless the signature is an event handler.

**E2. Silent catch-alls on the close path.** M / S
`src/Glide.App/MainWindow.axaml.cs:656` (overlay layout save) and `:660` (layout delete) use `catch { }`. A failed save means the user loses their overlay layout without any trace. `:252` (DWM attribute) and `:852` (`await startupSettingsTask`) also swallow everything without a log.
Fix: catch `IOException`/`UnauthorizedAccessException` (plus `ObjectDisposedException` where relevant) and log one trace line. Keep the swallow, but record it.

**E3. Unread `SetLastError = true`.** L / S
There are 28 `SetLastError = true` declarations, and only 1 call to `Marshal.GetLastWin32Error/GetLastPInvokeError` in `src/`. Failures are discarded and the marshalling cost is paid for nothing (for example `WindowsBrowserFrameController.cs:836 DestroyCursor`).
Fix: drop `SetLastError` where the result is ignored, or log `GetLastPInvokeError()` on the failure branches that matter (window creation, clipboard).

**E4. Native catch-all with no diagnostics.** L / S
19 `catch (...)` blocks in native code. They are correct at C ABI boundaries (`GlideCodecs.cpp:1139`, `CodecProviders.cpp:491,520`), but `GlideNative.cpp:585` silently runs fewer threads and `CodecProviders.cpp:340` sets `StateLoadFailed` without saying why.
Fix: in `catch (const std::exception& e)` paths, `OutputDebugStringA(e.what())` or record the reason in the provider state that the C# side already reads.

## 4. Size and structure

**S1. `MainWindow` partial-class sprawl.** M / L
32 files, 16,523 lines, one class. The largest are `MainWindow.Navigation.cs` (1,802), `.Tabs.cs` (1,653), `.Commands.cs` (1,404), `.Browser.cs` (1,196), `.StatusChrome.cs` (1,098), `.TitleSettings.cs` (977), `.Sessions.cs` (958), `.axaml.cs` (890). Partials share every field, so there is no encapsulation: `_browser*` fields declared in `MainWindow.axaml.cs:126-135` are used across five Explorer partials.
Fix: extract feature controllers that own their state and take a narrow host interface. `ExplorerBrowserController` (Browser + Explorer* partials, about 2.6k lines) and `TabStripController` (Tabs/SideTabs/TabPreview/NewTabs) are the natural first two. Do one per release, with no behaviour change.

**S2. `SettingsWindow`.** M / L
13 partials, 6,335 lines. `SettingsWindow.axaml.cs` alone is 2,997 lines, the largest C# file in the repo. Fix D1 first: it removes about 500 lines on its own.

**S3. Largest files (non-test).**

| Lines | File |
|---|---|
| 2,997 | `src/Glide.App/Settings/SettingsWindow.axaml.cs` |
| 2,693 | `native/Glide.ShellThumbnail/ThumbnailCore.cpp` |
| 2,153 | `src/Glide.Imaging/BuiltInRasterDecoder.cs` (+3 partials) |
| 2,138 | `src/Glide.App/Controls/WindowInWindowOverlayManager.cs` |
| 1,988 | `src/Glide.App/Controls/ImageViewport.cs` |
| 1,373 | `native/Glide.Native/GlideNative.cpp` |
| 1,358 | `src/Glide.Imaging/ImageLoadCoordinator.cs` |
| 1,140 | `native/Glide.Codecs/GlideCodecs.cpp` |
| 1,137 | `src/Glide.Imaging/ScientificDecoders.cs` |
| 1,111 | `src/Glide.Imaging/PdnDecoder.cs` |

Largest test files: `StatusBarAutoHideTests.cs` (1,473) and `HotkeyEndToEndTests.cs` (982).
Fix: split `ThumbnailCore.cpp` by stage (format sniffing, decode, scaling, cache). Split `WindowInWindowOverlayManager.cs` into layout/persistence versus pointer interaction. Split `ScientificDecoders.cs` into one file per format, matching how `ExrDecoder`/`ExrPiz`/`ExrDwa` are already split.

**S4. Longest methods (at least 150 lines).** M / M
- `ImageViewport.cs:1174 OnPressed` (322 lines) and `:1654 OnReleased` (178)
- `SettingsWindow.axaml.cs:409 LoadControls` (266) and `:721 ReadControls` (253) (see D1)
- `ImageLoadCoordinator.cs:278 LoadForegroundAsync` (248)
- `MainWindow.Navigation.cs:293 PresentCurrentAsync` (228) and `:583 RefinePresentedAsync` (175)
- `SettingsStore.cs:450 MigrateFromJson` (211)
- `MainWindow.Browser.cs:343 PopulateExplorerTile` (205)
- `WindowsBrowserFrameController.cs:299 WindowProc` (185)
- `DiagnosticRunner.cs:352 MeasureLaunch` (184)
- `Program.cs:12 Main` (174)
- `MainWindow.Tabs.cs:24 RebuildTabStrip` (174)
- `WindowInWindowOverlayManager.cs:946 SurfacePointerMoved` (170)

Fix: `OnPressed` should dispatch to a per-gesture handler (pan, zoom-drag, selection, side buttons). `MigrateFromJson` should become one method per schema step (`MigrateV7toV8`, and so on), which also makes each step testable. `WindowProc` should become a `switch` that calls named handlers.

**S5. Test-only hooks in production types.** L / M
42 distinct `*ForTests` members in `src/`. Top files: `PresentationFence.cs` (8), `SettingsWindow.axaml.cs` (7, for example the `ReadControlsCountForTests++` counter at :723), `WindowPlacementService.cs` (7), `ExplorerFileOperations.cs` (6). Two are unused (X1).
Fix: acceptable with `InternalsVisibleTo`, but mark them all `internal`. Prefer constructor-injected seams (`TimeProvider`, `IFileSystem`) over mutable static flags such as `IImageDecoderBackend.cs:482 MarkAllocationFailureForTests`.

## 5. Dead code and stray files

**X1. Unreferenced members (verified by whole-repo grep, src + tests + axaml).** L / S
- `src/Glide.App/Platform/WindowsThumbnailRegistration.cs:109` `HasForeignHandler`
- `src/Glide.Core/Data/GlideDataCategoryCatalog.cs:101` `FromBackupEntryName`
- `src/Glide.Core/ImageEditTransform.cs:60` `EnsureExtension`
- `src/Glide.Core/Reset/ResetCatalog.cs:57` `FromSettingId`
- `src/Glide.Imaging/VideoContainerProbe.cs:190` `TryU8`
- `src/Glide.App/Settings/SettingsWindow.axaml.cs:716` `IsCardExpandedForTests` (unused test hook)
- `src/Glide.Imaging/IImageDecoderBackend.cs:482` `MarkAllocationFailureForTests` (unused test hook)
- `src/Glide.App/Services/DialogEscape.cs:24-28` `IsDisabledProperty`/`SetIsDisabled`: nothing in C# or AXAML ever sets it, so `Applies()` (:45) always sees false. Either delete the opt-out or document who is meant to use it.

Fix: delete them, or wire them up if they were meant to be used. Run an IDE0051/IDE0052 pass (see W2) for private members, which this grep does not cover reliably.

**X2. Stray files in the working tree.** M / S
- `Archive/` (62 MB, about 30 `backup-*` folders with full copies of `src/`, `tools/` and `docs/`). `.gitignore` only ignores `Archive/*MANIFESTO*`/`*HANDOFF*`, so a future `git init`/`add` would pick the backups up, and every grep or IDE search returns duplicates (for example `Archive/backup-net10-*/tools/perf/Glide.PerfBench/*.csproj`).
- Root-level `Glide-5.0.8-Portable.zip` (25 MB) and `Glide-Zero-Context-Handover.zip` (8 MB). Ignored by `*.zip`, but release outputs belong in `dist*/`.
- `tools/bench-507/__pycache__/` and `tests/Glide.Input.Tests/TestResults/` (2 GUID run folders).
- Two output roots: `.artifacts/` (964 MB, MSBuild `ArtifactsPath`) and `artifacts/` (139 MB, script outputs). The names differ only by a dot.

Fix: add `Archive/` and `**/__pycache__/` to `.gitignore`. Move the backups outside the repo, or prune to the last two. Have `build.cmd` write release zips to `dist/`. Rename `artifacts/` to `out/` (or move script outputs under `.artifacts/tools/`).

**X3. One-off release tooling kept in `tools/`.** L / S
`tools/bench-507/` (a .NET project, 4 Python scripts and 4 PowerShell scripts) serves only `docs/perf/benchmark-plan-5.0.7.md`. `Glide.DecodeBench507` and `tools/perf/Glide.PerfBench` both have `AnalysisMode=None` and the same `NoWarn` and are not in the sln (so they bit-rot silently).
Fix: either fold `bench-507` into `tools/perf/` as a "decode" mode of PerfBench, or move it to `Archive/`. Add a `tools.slnf` solution filter so the tool projects at least compile in CI.

**X4. No-op MSBuild property.** L / S
`Directory.Build.props:34` sets `IlcOptimizationPreference`, which the ILCompiler does not read (the comment at :28-33 admits this). A dead property that looks like configuration misleads readers.
Fix: delete it, or change it to `OptimizationPreference` in the measured release that the comment plans for.

**X5. Only one TODO in the codebase.** (good)
`src/Glide.App/MainWindow.axaml.cs:130` `TODO(perf)` for a shared thumbnail cache. No FIXME/HACK/XXX. Commented-out code: effectively none. Move the TODO to an issue or to `docs/perf` and delete the comment.

## 6. Warnings (about 930 unique, about 2000 raw)

No build log is on disk (`build-output.txt` is git-ignored and absent). `docs/NET10-MIGRATION-PLAN.md:27` records **931 unique warnings** for `dotnet build Glide.sln -c Release`. The raw count of about 2000 includes each warning repeated per project, per pass and per linked file (D3). The breakdown below comes from source patterns under `AnalysisMode=Recommended` + `EnforceCodeStyleInBuild`, so treat the numbers as **estimates**. The first fix is to make the real numbers visible (W0).

**W0. No warning baseline.** M / S
Fix: `dotnet build Glide.sln -c Release -p:ErrorLog=.artifacts\warnings.sarif%2Cversion=2.1`, plus a 20-line script in `tools/` that groups by rule ID and fails when a rule's count grows. That stops regressions without fixing the backlog first. Then follow the policy below.

**W1. Suppress (justify once in `.editorconfig`, not per file).** M / S

| Rule | Est. sites | Why suppress |
|---|---|---|
| CA1707 (underscores in identifiers) | about 1,600 test method names | xUnit sentence-style names are a deliberate convention. Scope `[tests/**.cs] dotnet_diagnostic.CA1707.severity = none` |
| CA1861 (constant array arguments) | about 420 `new[] { ... }` | Nearly all are in tests or one-shot setup; no perf value |
| CA1305 on `$"..."` UI/trace strings | about 1,000 interpolations | UI text must use the current culture. Suppress for `Glide.App`; keep it as a warning in `Glide.Core`/`Glide.Imaging` (W2) |
| CA1822 (mark members static) | many | Noise on partial-class instance members. Set to `suggestion` |
| CA2007 (ConfigureAwait) | - | Wrong for a UI app. Already off in the tool projects; turn it off globally |

**W2. Fix (real bugs or real cost).** M / M

| Rule | Where | Why fix |
|---|---|---|
| CA1305/CA1304 on `Parse`/`ToString("fmt")` | 21 `int/double.Parse` and 108 `ToString("fmt")` without a culture, especially in `SettingsStore`, `ImageLoadCoordinator`, codec headers | Settings files and format headers parsed in de-DE or fr-FR break silently. Use `CultureInfo.InvariantCulture` |
| CA1307/CA1309/CA1310 | 23 `StartsWith/EndsWith/IndexOf("...")` and about 140 one-argument `Contains/Replace("...")` | Culture-sensitive comparison on paths and extensions is a correctness and speed issue. Use `StringComparison.Ordinal(IgnoreCase)` |
| CA1416 (platform compatibility) | `Glide.App` Win32 and registry code | Add `[SupportedOSPlatform("windows")]` at assembly level instead of scattered `OperatingSystem.IsWindows()` |
| CA1869 | 2 `new JsonSerializerOptions` | Cache them; it matters once AOT source-gen contexts exist (`CoreJsonContext`) |
| CA2101/CA1838 | `WindowsExplorerHost.cs:475` `StringBuilder` in `DragQueryFile` (plus 3 others) | Use a `char[]`/`Span<char>` buffer under `LibraryImport` (I1) |
| IDE0051/IDE0052 (unused private members) | unknown | Gives the dead-code list for private members that X1 could not cover |

**W3. Set to suggestion (IDE-visible, not in build output).** L / S
IDE style rules under `EnforceCodeStyleInBuild` that are not formatting (IDE0057 range, IDE0090 `new()`, IDE0300-series collection expressions). They are mechanical and add little review value; enforce only the formatting rules (IDE0055) and IDE0005.

**W4. Inconsistent NoWarn lists.** L / S
`Glide.ThumbnailCodecs.csproj:48` suppresses 12 rules, including CA1305/CA1307/CA1310, for the same linked files that `Glide.Imaging` builds with those rules on. So the same line is "clean" in one project and a warning in the other.
Fix: once W1/W2 are in a root `.editorconfig`, delete the per-project `NoWarn` (keep `IDE0005` only if needed).

## 7. Naming and magic numbers

**N1. Three static-field conventions.** L / S
Private static fields: PascalCase (about 102), `_camel` (about 53) and `s_camel` (about 5, in `MainWindow.ViewerChrome.cs`, `ThumbnailDecode.cs`, `ToolTipGuard.cs`, `ThumbnailCodecs/Exports.cs`, `ManagedCodecSupport.Host.cs`). `.editorconfig` has no `dotnet_naming_rule` entries.
Fix: add naming rules (private static readonly = PascalCase; private static mutable = `s_camel`; instance = `_camel`) as `suggestion`, then fix files as they are touched.

**N2. Timing magic numbers.** L / S
26 `TimeSpan.FromMilliseconds(<literal>)` (6 in `SettingsWindow.axaml.cs`; the values 500, 350 and 100 each appear three times) and 22 `Task.Delay(<literal>)` in `src/`. Polling sleeps: `NativeOverlayWindowRenderer.cs:424 Thread.Sleep(16)` (a frame pump), `DiagnosticRunner.cs:488 Thread.Sleep(150)`.
Fix: a `UiTimings` static class (TooltipDelay, SearchDebounce, AutoHideDelay...) so related delays change together, and named constants in the native renderer.

**N3. Layout literals in code-behind.** L / M
157 `new Thickness(<number>...)` in C#. Fine in custom-drawn controls; for code-built UI (`EndOfFolderPickerCard`, settings cards), use shared resources so density and themes stay consistent.

## 8. Unsafe interop

**I1. Mixed `DllImport`/`LibraryImport`.** M / M
`Glide.App` uses 133 runtime-marshalled `DllImport`s (`WindowsBrowserFrameController.cs` 29, `NativeOverlayWindowRenderer.cs` 28, `WindowsExplorerHost.cs` 11, `WindowsClipboard.cs` 8, `MainWindow.Tabs.cs` 6...). `Glide.Imaging` and newer code already use `LibraryImport` (38). Under NativeAOT, the `DllImport`s with `bool`, `StringBuilder` or `CharSet` need runtime-generated stubs. `bool` returns rely on the implicit 4-byte BOOL default (for example `NativeOverlayWindowRenderer.cs:691-695`).
Fix: convert one file at a time to `[LibraryImport]` + `[return: MarshalAs(UnmanagedType.Bool)]` together with D2. Add `[DefaultDllImportSearchPaths(DllImportSearchPath.System32)]` at assembly level: there are zero occurrences now, and the CA5392 hardening matters for a viewer launched from arbitrary folders.

**I2. P/Invoke declared inside UI partials.** L / S
`MainWindow.Tabs.cs` (6), `MainWindow.ViewerChrome.cs` (4), `MainWindow.Commands.cs` (2), `MainWindow.axaml.cs` (1, `DwmSetWindowAttribute`), `Glide.Core/ImageSort.cs` (1, `StrCmpLogicalW`, in a platform-neutral core assembly).
Fix: move them into the `NativeMethods` classes from D2. `Glide.Core` should take the comparer through an interface, or mark the class `[SupportedOSPlatform("windows")]`.

**I3. Pinned `GCHandle` where `fixed` would do.** L / S
`ImageSaveService.cs:29`, `ImageSelectionExportService.cs:25`, `PrintPreviewControl.cs:246` allocate a pinned `GCHandle` around a synchronous `CopyPixels`. They are correct (try/finally), but `fixed (byte* p = band)` is cheaper and cannot leak. `PrintPreviewControl.cs:245` `new byte[rows * stride]` has no `checked`, unlike `ImageSaveService.cs:27`.
Fix: use `fixed`, and `checked(rows * stride)`.

**I4. Raw owning pointers in native video.** L / S
`native/Glide.Native/GlideVideo.cpp:345,390,392` manually `delete player` on each error path, `:232` `delete p_`, `:446` `delete AsBackend(handle)`.
Fix: `std::unique_ptr` until the handle is released to C#, with `.release()` at the ABI boundary. The other native allocations checked (`GlideCodecs.cpp:165` malloc guarded by `DimensionsOk`, the `CtxGuard`/`OptGuard` RAII at :366/:379) are sound.

`Glide.ThumbnailCodecs/Exports.cs`: every `[UnmanagedCallersOnly]` export (:76-182) is wrapped in try/catch and validates the instance magic. No issues found.

## 9. Build scripts

**B1. `build.cmd` is 573 lines of batch.** L / M
It does restore, build, test, self-test, AOT publish, codec copy, installer and packaging in one file (45 labels/gotos). Errors in delayed-expansion batch are hard to diagnose.
Fix: keep `build.cmd` as a thin wrapper (it already tees to `build-output.txt`) and move the logic to `tools/build.ps1`, with one function per stage and a `-Stage` parameter.

**B2. Minified PowerShell benchmarks.** L / S
`tools/benchmark-glide.ps1`, `benchmark-existing-instance.ps1` and `benchmark-visible-first-pixel.ps1` put several statements per line with `;` and aliases (`?`, `%`). `benchmark-glide.ps1:15` assigns to `$args`, PowerShell's automatic variable.
Fix: rename it to `$exeArgs`, and run `Invoke-Formatter` (PSScriptAnalyzer) once over `tools/*.ps1`.

---

### Appendix: how the numbers were produced
- Empty catches: regex `catch(\s*\([^)]*\))?\s*\{\s*\}`. That gives 143 single-line empties (8 in `src/`, the rest in tests and tools), 366 untyped `catch` in `src/`, and 147 catches with a justification comment.
- Dead code: declarations whose identifier occurs exactly once across `src/`, `tests/` and `*.axaml`, then hand-verified. COM vtable slots and `UnmanagedCallersOnly` exports were excluded as false positives.
- Long methods: brace-matching script; class-level false positives were removed by hand.
- Warning estimates: grep for the code shapes that trigger each rule. Confirm with the SARIF from W0 before acting on the counts.
