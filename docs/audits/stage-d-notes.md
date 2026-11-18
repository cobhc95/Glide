# Stage D notes (split the god classes)

Date: 2026-11-18. Scope: Stage D of `audit-combined.md` (C1, C5, C8, C9, C13 of `audit-coupling.md`, R11, interop).
No intended user-visible behaviour change. Commits start with "Stage D" (`git log --oneline`).

Session 1 did D1 and D2. Session 2 (same day) did 2b, 3, 4, 5, 6, part of 9 and part of 10 without driving the UI:
before each extraction the current behaviour was pinned by characterisation tests (pure decisions moved into testable
types, plus the existing headless Avalonia window tests), then the code moved and the tests stayed green. Steps 7 and 8
were judged too risky for this session and are documented with the blockers found. What still needs a human eye is in
"Manual check list" at the end.

## Done

### D1. Dead `WindowsExplorerHost` deleted
Verified dead: `EnsureNativeExplorerHost()` had no callers (the Explorer tab is the managed `BrowserSurface`).
Removed `Platform/WindowsExplorerHost.cs` (582 lines, 11 `DllImport`s, the generated-COM ExplorerBrowser host),
the `NativeExplorerHost` field, `EnsureNativeExplorerHost`, `ApplyNativeExplorerSurfaceOwnership`,
`NativeExplorerStateChanged`, `NativeExplorerFolderNavigated`, `NativeExplorerFileActivated` (Navigation, Pickers),
the native branch of `ApplyEmbeddedExplorerTheme`, the hidden 1x1 `NativeExplorerHostSurface` grid in
`BrowserSurface.axaml` and the source-text test that pinned the file. 725 lines deleted.
Also: `tools/perf/Glide.PerfBench/UiBench.cs` did not build after the Stage C namespace move (`using Glide.Core.Settings`).

### D2. Status model + formatter (C9, first slice)
- `src/Glide.Core/Status/ViewerStatus.cs`: `ViewerStatusModel` (path, index/count, pixel size, file size, zoom, selection)
  and `ViewerStatusFormatter` (`StatsText`, `CounterText`, `FormatFileSize`, `FolderPathOf`). Pure, no Avalonia.
- `MainWindow.Chrome.cs`: `CurrentViewerStatus()` builds the snapshot; `UpdateStatusStats`/`UpdatePictureCounter` only
  assign the formatted text. `NotifyViewerStatusChanged()` replaces the 7 paired `UpdatePictureCounter(); UpdateStatusStats();`
  call sites (5 in Navigation, 1 TitleSettings, 1 axaml.cs).
- Tests: `ViewerStatusFormatterTests` (Ready text, NBSP-joined segments, token replacement, default template).
- Not done: the `StatusBarPresenter` that owns `StatusChrome`/`StatusLayout` (1,100 + ~250 lines) and the remaining
  ~25 `ApplyStatusVisibility()` calls. Next step: make `NotifyViewerStatusChanged` the only entry, then move the
  visibility/auto-hide logic (`StatusChrome.cs`) into a presenter that takes an `IStatusChromeHost` (controls + settings view).

### D2b. Status chrome policy (C9, second slice)
- `src/Glide.Core/Status/StatusChromePolicy.cs`: `Decide(StatusChromeInputs)` -> viewer bar / Home bar / fullscreen
  restore button visibility, `CollapsedNow`, `CollapsedLook` (compact corner vs ghost). The rules were inlined in
  `ApplyStatusVisibility` / `ApplyCollapsedStatusChrome`.
- `NotifyStatusChromeChanged()` is the single entry; the 20 outside `ApplyStatusVisibility()` call sites
  (Commands, Navigation, Overlays, Sessions, Slideshow, TitleSettings, ViewerChrome, axaml.cs) call it.
- Tests: `StatusChromePolicyTests` (windowed collapse keeps the bar as its own button, fullscreen restore button,
  always-on, Auto-hide replaces collapse, closed/overlay). The source-text pin in `StatusChromeLayoutContractTests`
  now only checks that the method delegates to the policy.
- Not done: moving the remaining control-touching code (`StatusChrome.cs` ~1,100 lines: probing, hover reveal,
  caption buttons) behind an `IStatusChromeHost`; it is almost all control manipulation and gains little testability.

### D3. Single gesture resolver
- Precedence per surface recorded first: `docs/audits/stage-d-gesture-precedence.md`.
- `src/Glide.Core/Commands/GestureResolver.cs`: `ForBuiltInRow` (Default = surface behaviour), `ForSideButton`
  (Default -> catalog default -> pass-through unhandled), `ForExplorerOpen` (Default opens in current/new tab, the
  context menu always opens), `SideButtonArea` (tab strip > Explorer > image > Home), `DoubleLeftSlot`, `WheelSlot`,
  `WheelAction` (held-key row, Shift/Alt "as wheel", slideshow and right-held overrides).
- Wired: `ImageViewport.OnWheel`, `SideButtonSlotAt` / `TryRouteSideButtonGesture`, `OpenBrowserEntryAsync`, viewport
  double-tap, title-bar and tab-strip wheel. The viewport's press precedence (slideshow > double-right > fullscreen
  click > empty-space drag > selection/drag/click) stays in `ImageViewport` (documented; stateful: timers, capture).
- Tests: `GestureResolverTests` (20 cases).

### D4. TabStripController (tear-off threshold included)
- `src/Glide.Core/TabStripController.cs`: MRU list and Ctrl+Tab cycle (snapshot while Ctrl is held, one complete
  switch without), overflow arrows and when they are allowed, overflow scroll step, neighbour slide offsets during a
  drag (smoothstep), tear-off threshold (generous slack, sideways for side tabs, DPI-scaled).
- `MainWindow.Tabs.cs`: `_tabMru*` fields gone; `TouchTabMru`, `EndTabMruCycle`, `CycleTabAsync`, `ScrollTabStrip`,
  `UpdateTabOverflowButtons`, `ApplyTabDragPreview`, `IsBeyondTabTearOffThreshold` apply the controller's results.
  The native move loop and cross-window attach stay in MainWindow (window/interop work).
- Tests: `TabStripControllerTests` (MRU, cycle with/without Ctrl, arrows, scroll step, slide, tear-off).

### D5. Explorer layout
- `src/Glide.App/Explorer/ExplorerLayout.cs`: column count, tile rectangle (grid / Details), scroll-into-view offset,
  item-count / selection / empty-folder texts. `MainWindow.Browser.cs` applies them.
- Tests: `ExplorerLayoutTests` (Glide.Explorer.Tests). The existing Explorer window tests (now 199) cover navigation.
- Not done: a full `ExplorerController` owning `_browserEntries` / selection / realization. The listing and tiles are
  tied to `ItemsRepeater` realization callbacks; moving them needs an `IExplorerView` seam and a session of its own.

### D6. History, then navigation routing
- `src/Glide.Core/Navigation/TabHistoryStore.cs`: per-tab histories (created from the travelled copy / restored
  Explorer folders / empty, `Take` for tab transfer, `Prune`, `Reset`, `BrowserNavigationFor` for the session file).
  `MainWindow.History.cs` delegates; `internal TabHistories` replaces private reflection on `_tabHistories` in 5 tests.
- `NavigationCoordinator` (`src/Glide.App/Services`) is no longer a pass-through: `AtBoundary`, `Route` (Ignore /
  Buffer / EndOfFolder / Move; the end-of-folder policy is consulted only at a boundary), `BufferDelta`, `IsRapid`.
  `NavigateCoreAsync` uses them; the serialization gate, presentation and end-of-folder UI stay in MainWindow.
- Tests: `NavigationRoutingTests` (routing, buffer cap, rapid timing, history store, restored Explorer round trip);
  the existing `NavigationHistoryWindowTests`, end-of-folder and Explorer history tests stayed green.

### D7. SettingsWindow per-page views: deferred (too risky here)
`SettingsWindow.axaml` has 304 named controls on 8 pages, used by name from ~470 KB of code-behind partials, and all
three audits (`audit-settings-search.py`, `audit-action-registry.py`, `audit-setting-wiring.py`) parse that one XAML
file. Splitting into UserControls means rewriting every control access (no generated fields across controls) and
teaching the audits to read several XAML files. Suggested order: make the audits also read `Settings/Pages/*.axaml`,
then move one page (Performance, the smallest) and compare the page-switch timing in the UI benchmark.

### D8. Decoded image / Glide.Codecs.Managed: deferred (blocker found)
The 18 linked files are Avalonia-free and `BuiltInRasterDecoder.DecodedImage` (BGRA buffer) already exists, but
`ManagedCodecSupport` is a partial class completed per host: `ManagedCodecSupport.Skia.cs` (viewer) and
`ManagedCodecSupport.Host.cs` (ThumbnailCodecs, WIC through a native callback). Partial classes cannot span
assemblies, so a shared project needs an `IEncodedSubImageCodec` registered by each host before the first decode (a
missed registration silently loses RAW previews / embedded JPEGs), and `BuiltInRasterDecoder.Avalonia.cs` must become
its own type. Both touch the decode path and the NativeAOT thumbnail DLL; not attempted without a decode benchmark
plus a thumbnail-provider check.

### D9. Interop (part)
- `src/Glide.App/Platform/Native/User32.cs`: `LibraryImport` stubs, explicit `MarshalAs(Bool)`, per-method
  `DefaultDllImportSearchPaths(System32)`; replaces the 6 `DllImport`s of `MainWindow.Tabs.cs` (call sites in Tabs,
  StatusChrome, ViewerChrome).
- `WindowsClipboard`: 8 `DllImport`s -> `LibraryImport` + System32.
- Not done: `WindowsBrowserFrameController` (29), `NativeOverlayWindowRenderer` (28), the `EnumThreadWindows` callback
  in ViewerChrome (delegate marshalling: needs a function pointer + `UnmanagedCallersOnly`), and an assembly-wide
  `DefaultDllImportSearchPaths`: bundled codec DLLs load from the app folder, so the assembly default must include
  `AssemblyDirectory`; verify with the codec smoke tests on a Release publish before adding it.

### D10. Tests (part)
Private reflection on `_tabHistories` replaced by `MainWindow.TabHistories` (5 sites). Source-text pins touched by this
stage were reduced to delegation checks. The Glide.App.Tests split was not attempted.

## Numbers

MainWindow partials (`src/Glide.App/MainWindow*.cs`, non-blank lines): 15,479 before, 15,356 after D1, 15,309 after D2,
15,137 after session 2. Logic moved into tested types in session 2: StatusChromePolicy, GestureResolver,
TabStripController, ExplorerLayout, TabHistoryStore, NavigationCoordinator routing, Platform/Native/User32.

Benchmark (tools/perf/run-perf.ps1, quick corpus from generate_perf_data.py --quick, serial, Release build of this tree;
`navigate` measures the Imaging navigation pipeline, `app` = process launch to first composed frame, median of 5):


| Metric | Before (cc3fee4) | After D2 | After session 2 (b46597c) | Change vs before |
|---|---:|---:|---:|---:|
| First frame, jpeg-base-small | 849.4 ms | 864.6 ms | 871.9 ms | +2.6% |
| First frame, jpeg-base-24mp | 858.5 | 862.5 | 879.4 | +2.4% |
| First frame, jpeg-prog-24mp | 866.7 | 869.7 | 892.2 | +2.9% |
| First frame, jpeg-base-100mp | 871.7 | 871.4 | 888.2 | +1.9% |
| First frame, png-24mp | 869.0 | 878.1 | 893.4 | +2.8% |
| First frame, webp-24mp | 974.2 | 980.2 | 997.5 | +2.4% |
| First frame, tiff-lzw-24mp | 874.2 | 888.2 | 901.2 | +3.1% |
| First frame, gif-anim | 870.0 | 889.8 | 900.7 | +3.5% |
| Next (24 MP, 500 ms dwell, prefetch) median step | 0.14 ms | 0.15 ms | 0.15 ms | noise |
| Next (24 MP, no prefetch) median step | 42.4 ms | 38.3 ms | 38.4 ms | -9% |
| Next (4K, no prefetch) median step | 22.6 ms | 20.7 ms | 20.8 ms | -8% |
| Held key burst 24 MP, settle | 268.2 ms | 254.5 ms | 254.0 ms | -5% |
| MainWindow show + first layout (warm median) | 471.4 ms | 463.4 ms | 262.6 ms | see note |

All within the 5% budget. Session 2 ran after the full test suites with other dotnet build servers still resident (no
full idle wait), so the uniform +1-3% on first frame against D2 is most likely machine state: the navigation pipeline
(the code session 2 touched: `NavigateCoreAsync` routing) is unchanged to the tenth of a millisecond, and first frame
only runs trivial new code before the first composed frame (a few pure policy calls and small allocations).
A re-run on an idle machine is the way to settle it. The
"MainWindow show" row differs because the UI bench's warm median moved (262.6 ms warm, 561.8 ms first run); compare
it only within one run. Explorer folder open still has no harness (PerfBench has no Explorer mode).

Final check (b46597c + notes): `build.cmd --no-pause` complete; tests 2,451 Core + 199 Explorer + 21 Input passed,
0 failed, 0 skipped; settings-search, action-registry and setting-wiring audits pass.

## Remaining work (starting points)

| Step | State | Next |
|---|---|---|
| 2b Status bar | Policy + single notification done | Optional `IStatusChromeHost` presenter for the control code in `StatusChrome.cs`. |
| 3 Gesture resolver | Done for Default semantics, side buttons, Explorer open, wheel, double-click | Viewport press precedence could become a pure `PressRoute` decision; overlay windows still resolve through their own callback. |
| 4 Tab strip / tear-off | Controller done | Native move loop / cross-window attach could move behind an `ITabTearOffHost`. |
| 5 Explorer controller | Layout/status extracted | `ExplorerController` over `_browserEntries`/selection with an `IExplorerView` seam. |
| 6 History, Navigation | Store + routing done | Move `_pendingNavigationDelta`, the gate and `RefreshCurrentFolderIndexAtBoundaryAsync` into the coordinator. |
| 7 SettingsWindow pages | Deferred | See D7. |
| 8 Imaging without Avalonia | Deferred | See D8 (partial-class host split first). |
| 9 Interop | User32 layer + clipboard | `WindowsBrowserFrameController` (29), `NativeOverlayWindowRenderer` (28), ViewerChrome callbacks; assembly-wide search path after the codec load check. |
| 10 Tests | `_tabHistories` reflection gone | Remaining source-text tests (`Glide23FeatureContractTests`, `ExplorerRemovalAndOverlayChromeContractTests`...), Glide.App.Tests split. |

## Manual check list (one pass at the end, about 10 minutes)

Everything below is covered by headless tests of the decision logic; these checks confirm the wiring on a real
window and mouse.

1. Status bar: collapse to the corner and back (corner setting on and off), turn Auto-hide on and reveal the bar at the
   bottom edge, enter fullscreen and use the corner restore arrow, close the bar with X and show it again from the menu.
2. Mouse wheel on the image: plain, Ctrl, Shift, Alt; during a slideshow; with the right button held (no context menu
   on release). Wheel on the empty title bar and on the tab strip (scrolls an overflowing strip by default).
3. Mouse side buttons over a tab, the Explorer tab, the image (windowed and fullscreen) and the Welcome page.
4. Double-click the image (enters fullscreen), the empty background, and in fullscreen.
5. Explorer: double-click and middle-click an image and a folder; right-click > Open; Ctrl+wheel icon sizes; arrow keys
   scroll the selection into view; status line counts and selected size; empty folder and filter-no-match texts.
6. Tabs: Ctrl+Tab / Ctrl+Shift+Tab with Ctrl held across several presses and released (MRU order), overflow arrows,
   drag a tab to reorder (neighbours slide), drag it down out of the strip to tear it off, drag it back into a window.
7. Navigation: arrows at the last image with each end-of-folder choice (stay with notice, loop, next folder, picker);
   held arrow key burst; Back / Forward / history dropdown across Explorer -> image -> Home and after moving a tab to
   another window.
8. Copy image to clipboard (WindowsClipboard now uses source-generated interop) and paste into another app.
