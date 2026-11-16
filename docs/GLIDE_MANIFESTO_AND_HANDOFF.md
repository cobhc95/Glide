# GLIDE 5.0.1 — ACTIVE HANDOFF

**Current active release:** Glide 5.0.1 (single-sourced from `Directory.Build.props`; 5.0.0 is the previous
published release, 5.0.1 adds the .NET 10 migration, the file-safety audit fixes and the daily update checker).
Release files and source metadata must stay aligned to 5.0.1 until a newer version is explicitly requested.
4.8.0 was developed but never published; everything built for it shipped in 5.0.0, so there is no
4.8.0 release, tag or release-notes file. The "Current checkpoint — 5.0.0" section below summarises
the release; `CHANGELOG.md` and `docs/releases/release-notes-5.0.0.md` hold the full lists, and
`docs/releases/RELEASE-5.0.0-CHECKLIST.md` is the owner's publish and hand-check list. Historical
checkpoints, audits and the previous full handoff are archived under `Archive/`.

## Release output contract (permanent)

Every clean release build removes generated caches and prior compiled outputs, then produces:

1. `Glide-<version>-Portable.zip` — published `dist` contents.
2. `dist-installer\Glide Setup.exe` — compiled Windows setup installer.
3. `Glide-<version>.exe` — standalone executable for immediate testing.
4. `Glide-Zero-Context-Handover.zip` — essentials-only source handoff (source, tests, scripts and
   this document; binaries, caches, generated artifacts and nested ZIPs excluded).

GitHub release assets are named `Glide.Setup.exe` (the `Glide Setup.exe` above, renamed), `Glide-<version>-Portable.zip`
and `Glide.Setup.sha256`. The checksum file written by the installer build names the file with a space
(`Glide Setup.exe`), so edit that name to `Glide.Setup.exe` in a copy before uploading it.

The version is single-sourced from `Directory.Build.props`. Build scripts read it and must never
hard-code a version in banners, output names or fixture identities.

## Build, test, run

```bat
build.cmd                  rem clean build: native + managed + tests + diagnostics + publish + portable + installer + handover
build.cmd fast --no-pause  rem compile/publish/package without running tests
tools\build-installer.cmd  rem installer only
tools\run.cmd              rem run from source
```

Repository layout (root stays small; see `docs/CLEANUP-5.0.md` for the 5.0.0 reorganisation):
`src/` app + libraries, `tests/`, `native/` C++ bridge + thumbnail provider, `tools/` scripts (build helpers,
benchmarks, `package-source.ps1`, `build-installer.cmd`, `run.cmd`), `installer/` Inno Setup script, `codecs/`
optional codec providers (copied to `dist\codecs` by the build), `docs/` design notes, this handoff and
`docs/releases/` (all release notes), `Archive/` retained backups (zips). Generated and git-ignored:
`.artifacts/`, `artifacts/`, `dist/`, `dist-installer/`, `Glide-*.exe|zip`.

Toolchain: .NET 10 SDK (x64; `global.json` pins 10.0.100 with `rollForward: latestFeature`), VS 2022/2026
(or Build Tools) with the C++ x64 workload ("Desktop development with C++"), CMake 3.20+, Inno Setup 6 or 7.
No administrator rights are required for the app itself. Since 5.0.1 every project targets `net10.0`
(.NET 10 LTS; .NET 8 reached end of support on 2026-11-10); `LangVersion` stays pinned at 12. The NativeAOT
publish needs `%ProgramFiles(x86)%\Microsoft Visual Studio\Installer` (vswhere) on PATH; `build.cmd` adds it,
and any other script or agent job that publishes must do the same. The released exe is self-contained, so
users need no .NET runtime. `Glide.App.csproj` sets `<CETCompat>false</CETCompat>` (the .NET 10 AOT default
is on; Glide loads third-party shell extensions and Windows codecs in-process): enabling CET needs a manual
interop test pass on a CET-capable PC first. See `docs/NET10-MIGRATION-PLAN.md`.

## Permanent invariants (do not regress)

- The UI thread never performs synchronous image decode or heavy directory work.
- Older decode completions never replace a newer request; every publish re-checks window/tab/request
  identity.
- Physical input routes to semantic commands before feature behaviour.
- Viewer and tab state is explicit and headlessly inspectable.
- Shared appearance is centralized; persistent controls use Glide vector glyphs, not font/emoji.
- **Every user-facing setting is declared in `SettingsCatalog` and must be searchable and editable
  from Settings search.** Search indexes catalog metadata *and* the on-screen wording of each mapped
  control (content, tooltip, control name, and the visible row label used by combo/number/text
  editors), plus declared search terms. Every control wired in the Settings window must be declared
  in the catalogue (the intentionally hidden removed-Explorer controls are the sole exception).
- Visible controls must work or be explicitly disabled; no fake-active placeholders.
- A feature is incomplete until targeted diagnostics and tests exist.
- The native imaging bridge stays isolated from UI/workspace logic.
- Overlay presentation stays on the native retained-window worker (or the Avalonia compositor
  fallback); the main canvas decode/navigation path is never coupled to overlay motion.
- Speed Boost standby invalidates pending UI/loader requests before hiding and never exposes prior
  pixels on restore.
- Diagnostic status is truthful PASS/FAIL/SKIP/WARN; structural PASS never certifies UI parity.
- Small-screen safety: prompts size to their content and clamp to the owner's working area; a
  fixed pixel height must never clip decision buttons.

## Current checkpoint — 5.0.0 (2026-11-14)

Shipped in 5.0.0 (see `CHANGELOG.md`): the Explorer tab (browser, file operations, session restore),
the mouse gesture panel (areas, custom gestures, action picker, mouse side buttons that go Back / Forward by
default; settings schema 7 migrates the old mouse toggles), browser-style Back / Forward per tab (Alt+Left,
Shift+Backspace / Alt+Right, Backspace = Back or Previous image, title-bar buttons, `docs/NAVIGATION-HISTORY.md`), a status bar whose info line is
stacked on its own row by default (*Always stack the information on a separate line*), collapsible Settings cards in a compact window (930 x 630 default,
remembered size), animated GIF controls, the master action registry (every command, 131 Toggle / Choice
settings and the fixed keys behind one list; hotkey audit fixes), auto-hide status bar with a reveal area
equal to the bar's own box (hover reveal on by default, adjustment setting), the Reset Glide card,
window-placement memory for every window, a print preview that no longer waits for the printer driver,
fullscreen menus that keep the pointer, folder-aware Custom startup / new-tab paths, hotkey list sorting /
search / custom presets / conflict prompt, working Explorer thumbnail quality settings, the performance
pass, two security passes and a code-hygiene pass. Settings schema is **11** (schema 7: old mouse toggles
become gesture rows; schema 8: `status.toggle` default Alt+Shift+S -> Ctrl+Shift+B and a stored zoom
step of 10% -> 15%; schema 9: Back / Forward added to a title bar still on the previous default and Back /
Forward keys added to saved shortcut maps unless a key is taken; schema 10: *Explore/Browse containing folder* gets Ctrl+E; schema 11: Backspace moves to *Back, or Previous image*). All file references are repo-relative.

### Architecture notes added in 5.0.0

- **Master action registry** (`src/Glide.Core/Commands/AppActionRegistry.cs`, about 268 actions; read
  `docs/ACTIONS-AND-HOTKEYS.md` before touching hotkeys, gestures, menus or buttons): the hotkey list,
  gesture picker, title-bar buttons, status-bar buttons, context-menu items and Settings search entries are
  projections of it; `MainWindow.CreateCommandDispatcher` is the one place that registers handlers and
  `MainWindow.ExecuteActionAsync` the one execution path (viewer-only actions are gated so they never run
  against a hidden image from an Explorer / Welcome tab). Toggle / Choice settings become `Toggle:` /
  `Cycle:` actions (131 of 151 at audit time; the rest are in `SettingActionCatalog.Excluded` with reasons).
  Shortcut strings are compared canonically (`ShortcutText`); defaults must be written canonically.
  `GlideCommand` values, the 75 legacy hotkey row ids and the 56 legacy gesture ids are persisted: append
  only. Guard tests: `AppActionRegistryTests`, `HotkeyEndToEndTests`; static audit
  `python tools/audit-action-registry.py`. `status.toggle` defaults to `Ctrl+Shift+B` (migrated once by
  `SettingsStore.MigrateDefaultsSchema8`); the zoom step (`ZoomStepPolicy`) is wired; the five fit commands go
  through `ApplyStatusBarFit` and follow *Fit control scope*; `app.exit` quits the process while
  `window.close` closes one window; mouse X1 / X2 are gesture inputs (`GestureInputs.BackButton` /
  `ForwardButton`), never hotkeys, and default to `nav.back` / `nav.forward` (`GestureCatalog.DefaultActionFor`).
- **Navigation history** (`src/Glide.Core/Navigation/NavigationHistory.cs`, pure, up to 100 steps per tab;
  `src/Glide.App/MainWindow.History.cs` records and replays steps across Home, image and Explorer tabs):
  actions `nav.back` (Alt+Left, strict) / `nav.forward` (Shift+Backspace, Alt+Right) and `nav.backOrPrevious`
  (Backspace: Back when there is a step back, else Previous image in an image tab, else nothing; settings schema 11);
  Alt+Up stays *Up one folder*; image-to-image moves are not steps; a new tab starts empty. See `docs/NAVIGATION-HISTORY.md`.
- **Stacked status bar**: setting *Always stack the information on a separate line* (default on, in the status
  bar settings and the bar's right-click menu, bindable like every toggle) puts the info line on its own row;
  the default bar is one row taller, and the collapsed button, *Reserve status bar space when fitting* and the
  reveal area follow the taller box and re-measure when the option is toggled.
- **Status bar reveal** (`MainWindow.StatusChrome.cs`): the invisible reveal area is the box the bar occupies
  when shown (read from the live layout, or measured while hidden), grown or shrunk by the
  `status.revealZoneExpand` setting (-40..+300 DIP, default 0); `status.hoverWhenClosed` is on by default and
  has no context-menu item; with Auto-hide on the area equals the revealed bar exactly and the bar's wrap
  decision does not reserve room for the collapse button. A revealed bar hides about 0.6 s after the pointer
  leaves it. The earlier fixed *Reveal zone height* setting never shipped; old files that contain it ignore it.
- **Window placement** (`WindowPlacementService` in Glide.App, pure `WindowPlacementResolver` in Glide.Core,
  catalogue `src/Glide.App/Services/WindowPlacementCatalog.cs`): every window is catalogued with a key;
  resizable windows remember size + position + maximized, fixed-size dialogs remember position only; stored
  under `WindowPlacements` in `glide.settings.json` (max 64 entries). `WindowPlacementGuardTests` fails when a
  new window or dialog is not catalogued (or allow-listed with a reason). The main window keeps its own
  placement system; the Settings window's older `SettingsWindowX/Y/Width/Height/WasMaximized` are migrated.
- **Reset Glide** (`src/Glide.Core/Reset`: `ResetCatalog`, `ResetPlanner`, `ResetExecutor`, `ResetBackup`,
  `ResetCoordinator` / `ResetHelper` behind `IResetFileSystem`, `IResetRegistry` (HKCU only) and
  `IResetProcessHost`). In-memory data is wiped after exit by the same `Glide.exe` started as
  `--reset-glide --items <ids> --wait-pid <pid> --settings-dir <dir>`. Reset items are `reset.*` catalogue
  Actions (not changed settings, not bindable). HKLM, UserChoice, `thumbcache_*.db`, crash logs and the install
  folder are never touched.
- **Popup cursor guard** (`PopupCursorGuard`, `MainWindow.PopupCursor.cs`, `FullscreenCursorAutoHide`): popups
  inherit the window's hidden cursor, so styles force an arrow on every popup type and the idle timer pauses
  while any non-tooltip popup is open. The only `StandardCursorType.None` in `src` is the fullscreen idle one
  (a test enforces it).
- **Print preview** (`PrintWindow`, `PrintQueryCache`): capabilities per printer and geometry per
  printer + paper + source are read off the UI thread and cached for 10 minutes; orientation and layout need
  no driver call; the preview draws a 2048 px proxy. `PrintService.PrintAsync`, `PrintLayoutEngine` and
  `RenderPage` are unchanged (a test proves preview layout == spool layout). Opt-in harness:
  `PrintPreviewPerfHarness` (`GLIDE_PRINT_PERF=1`).
- **Custom startup / new-tab path**: a folder opens an Explorer tab, a file opens the image; an unusable path
  falls back to Welcome (or the usual Explorer start folder) with an on-page message.

- **Bitmap lifetime** (`src/Glide.Imaging/ImageLoadCoordinator.cs`): every cache-owned bitmap handed to a
  caller carries a presentation lease. `ReleaseBitmap` returns it after the compositor fence (or at
  once for stale or rejected results); eviction or purge of a leased or viewport-active bitmap moves
  it to a retired set, and the final release disposes it exactly once. `MainWindow` uses one rule
  (`RetirePresentedBitmap` / `ReleaseBitmap`). A code path that receives a cached `ImageLoadResult` and
  never releases it keeps the bitmap alive until purge or window close (a bounded leak).
- **Decode pipeline**: full-first for PNG/TIFF/BMP/GIF/ICO when the frame fits half the decoded budget
  (WebP keeps its scaled first frame); refinement is skipped when the first frame already covers the
  viewport; the native export `GlideDecodeImageIntoW` decodes orientation-1 full frames straight into
  the presentation `WriteableBitmap` (older DLLs and rotated images fall back automatically); parallel
  speculative prefetch lanes with in-flight joining and jump detection.
- **Pixel cap and memory guard**: `src/Glide.Imaging/DecodeMemoryGuard.cs` is the single source for the
  512 MP / 65,535-per-side cap and for the free-memory check (frames over 256 MiB need at most half of
  free physical and commit memory). The native bridge enforces the same cap (`GlideGetMaxDecodePixels`),
  as do the Skia fallback, the built-in decoders and the startup preloader. The Explorer thumbnail
  provider deliberately keeps its own 256 MP limit.
- **Single-instance hand-off**: pipe and presence names are per user and per protocol version
  (`ProtocolNames.UserScoped`), `PipeOptions.CurrentUserOnly` on both ends. A running 4.x and 5.x do not
  hand images to each other, and an elevated Glide does not forward to a normal one.
- **SVG**: `SvgDecoder` removes every `image`/`feImage` that could reach another machine before building
  the picture; keep the SVG security tests green when upgrading Svg.Skia (currently 2.0.0.8, the newest
  line that works with SkiaSharp 2.88.x).
- **Settings code layout**: `SettingsWindow` is split into `.axaml.cs` (search, the `M(...)` control map
  `BuildSettingControlMap`, Load/Read, contract-pinned search-card code), `.Diagnostics.cs`, `.Hotkeys.cs`,
  `.Profiles.cs` and `.Thumbnails.cs`. `SettingsChangeSet` is the UI-free diff of two settings snapshots.
  Profile and settings import keeps this PC's external programs and drops network folders
  (`KeepLocalLaunchTargets`, `KeepLocalFolderPaths`). Final settings audit: 210 editors, 216 settings in
  40 cards on 8 pages, 54 generated gesture rows; the settings catalogue has 271 entries (the stacked-info option and, since 5.0.1,
  the two update options are among them).
- **Video playback (5.0.4)**: overlays play videos only with the codecs installed in Windows (Media Foundation); Glide ships and
  downloads no decoder. When Windows can't play a file, `VideoContainerProbe` (Glide.Imaging) reads the container's track list
  (bounded, never throws) and `VideoPlaybackDiagnoser` asks Windows per codec (`GlideVideoHasDecoder`, MFTEnumEx) and names only
  what is missing. `LegacyVideoDecoderCleanup` deletes folders the old optional FFmpeg pack left in %LOCALAPPDATA%\Glide.
- **Update checker (5.0.1)**: Glide's only network use. `Glide.Core/Updates` holds the pure,
  tested logic (`UpdateVersion` comparison, `UpdateReleaseParser` over a source-generated DTO, `UpdateUrlPolicy` for the release
  address and redirect hosts, `UpdateSchedule` for the 24 h / 6 h back-off / 20 h prompt rules with a clock seam, `UpdateChecker`
  with one process-wide gate, `UpdateStatusText`). `Glide.App/Updates` holds `GitHubUpdateFeed` (the only HTTP code: HTTPS GET of
  `api.github.com/repos/cobhc95/Glide/releases/latest`, no cookies / auth / personal data, 10 s timeout, 256 KB cap, redirects only to
  https on api.github.com / github.com), `UpdateService` (schedule, prompt, manual check; seams `IUpdateHost` / `IUpdateTicker`),
  `UpdatePromptWindow` and `UpdateProduction` (the single place that builds the real feed, called only from `App`). Tests and headless
  windows never create a service (`UpdateService.Current` is null), and `UpdateStateAndGuardTests` fails if any test or any other
  source file builds the real feed or an HTTP stack. The memory (`UpdateLastCheckedUtc`, `UpdateLatestKnownVersion`, ...) is part of
  `GlideSettingsState` but is bookkeeping: it is ignored by `ContentEquals` / `SettingsChangeSet`, written on its own with
  `SettingsStore.SaveUpdateState` (so an unapplied Settings preview is never persisted) and overlaid on every full save by
  `UpdateService.ApplyTo`. Settings: `updates.checkDaily` (default on) and `updates.dontRemind`; action `app.checkUpdates`.
- **Other extractions**: `CommandLineTools` (headless modes out of `Program.Main`; the GUI startup path and
  the diagnostic-trace opt-in lines stay in `Program.cs` for the privacy contract test),
  `ThemePaletteApplier`, `KeyboardShortcutText`, `OverlayDriftMotion` (the renderers must still call these
  by name for the smoothness contract tests), `ExplorerShell` (shell launches with a test seam).
- **Installer** (`installer/Glide.iss`): no HKCU install-directory lookup, "Launch Glide" runs as the
  original user, and a custom install folder outside Program Files and the user's profile is locked down
  with `icacls` before files are copied and again before Glide runs elevated for registration.
- **Native projects** build with `/guard:cf`, CET compatibility, `/sdl` and (only when the
  Spectre-mitigated libraries are installed) `/Qspectre`. `build.cmd` rebuilds them, so the shipped
  `Glide.Native.dll` and `Glide.ShellThumbnail.dll` must come from a fresh `build.cmd`.
- **Signing**: `tools/sign-local.ps1` plus the `build.cmd` / `tools\build-installer.cmd` hooks sign only
  when `GLIDE_SIGN_CERT_THUMBPRINT` or `GLIDE_SIGN_PFX` is set (`docs/code-signing.md`). No certificate has
  been obtained; releases are currently unsigned.
- **Performance harness**: `tools/perf` (`Glide.PerfBench`, `run-perf.ps1`, `compare-perf.ps1`,
  `generate_perf_data.py`), not part of `Glide.sln`. The frozen baseline lives in
  `.artifacts\perf-baseline`.

### Verification of record (5.0.0 source)

Glide.Core.Tests 1258, Glide.Input.Tests 21, Glide.Explorer.Tests about 125 (not in `Glide.sln`; build it with
`--no-incremental` if every UI test reports missing precompiled XAML), `python tools/audit-settings-search.py`
and `python tools/audit-action-registry.py` passing, NativeAOT publish succeeding (the AOT build's
`--diagnostics self-test` validated the registry). The UI, installer, printers, multi-monitor setups,
mouse side buttons (now Back / Forward by default) and other real-hardware behaviour were not driven by agents: see
`docs/releases/RELEASE-5.0.0-CHECKLIST.md`.

### Deliberately left alone (owner decisions)

- The dormant native Explorer host (`WindowsExplorerHost` and the `NativeExplorer*` handlers): no
  production entry point, but `Glide23FeatureContractTests.ExplorerContractDeclaresLazyNativeHostAndActivationRoute`
  requires it.
- God methods on input and startup hot paths (`ImageViewport.OnPressed` / `OnReleased`,
  `MainWindow.PresentCurrentAsync` / `RefinePresentedAsync`, `PopulateExplorerTile`,
  `WindowInWindowOverlayManager.SurfacePointerMoved`) and the 2,100-line `WindowInWindowOverlayManager.cs`,
  which about ten source-text contract tests pin.
- Two "show in Explorer" implementations (`FileOperationController.TryOpenContainingFolder` and
  `ExplorerShell.Reveal`); merging them would change behaviour.
- `BrushCardAccent` (still set by the theme code, no longer read), unreferenced named XAML elements, and
  code used only by tests (`OverlayCollisionPolicy`, `OverlayAnimationSimulator`, `ImageNavigator.OpenSingle`,
  `WorkspaceState.CloseActive`, `InputRouter.ResolveKey` / `ResolveGestureCommand`). `SecureImageFetcher` and
  `SecureDiskCache` were deleted in the security pass.

### Open items / watch-outs for the next agent

1. Run the owner's hand checks (`docs/releases/RELEASE-5.0.0-CHECKLIST.md`) before treating 5.0.0 as accepted.
2. README screenshots are the 5.0 captures in `docs/screenshots/5.0` (the older 4.6 set is in `Archive/screenshots-4.6`). Still missing: a Welcome page, the stacked status bar close-up, the title-bar customiser, the gesture dropdown, the hotkey-conflict prompt, the Reset Glide dialog, the GIF controls and a Print window from a 5.0 build.
3. Registry-audit leftovers that were reported and not changed (confirm or decide): *Show / hide status bar*
   while Auto-hide is on only flips a remembered flag (no visible effect, by design); the status-bar 100 %
   button clears the selection-zoom demand but the `view.actual` command does not; *External programs 1-3*
   now act on the image selected in an Explorer tab (nothing selected: nothing happens and Explorer says
   "Select an image first"; Welcome tab: nothing) instead of the window's last image. The code string
   `unversioned, pre-4.8` in `ThumbnailProviderStatus.cs` and the Settings description of *Auto-hide status
   bar* (still says "reveal zone height") are stale wording in code, not docs.
4. Next release: .NET 10 migration (`docs/NET10-MIGRATION-PLAN.md`). Known remaining performance work: lazy
   per-page Settings XAML (the Settings window builds every page at once, 50-260 ms), a screen-sized mip level
   for images over 50 MP, a shared thumbnail cache across tab switches (`TODO(perf)` in
   `MainWindow.axaml.cs`).
5. Every new user-facing setting must be catalogued and searchable (`AGENTS.md`); run
   `python tools/audit-settings-search.py`.

## Previous checkpoint — 4.7.0 (shipped; the gesture matrix below was later replaced by the 4.8/5.0 gesture panel)

Three user requests were implemented together. All file references are repo-relative.

### 1. Wheel gesture actions merged, with a reversed variant

- `src/Glide.Core/Commands/GestureCatalog.cs` — added two merged direction-aware wheel actions:
  `ZoomWheelReversed` (`zoomWheelReversed`, label "Zoom in/out (reversed)") and
  `BrowseWheelReversed` (`browseWheelReversed`, label "Previous/next image (reversed)"), alongside
  the existing `ZoomWheel` (relabeled "Zoom in/out") and `BrowseWheel` ("Previous/next image").
  Gesture action count is now **23** (was 21); slot count unchanged at **25**.
- `GestureCatalog.ActionsForSlot(slotId)` returns the actions offered per slot: **wheel slots**
  (slot ID contains "wheel": `wheel.windowed`, `wheel.windowedCtrl`, `wheel.fullscreen`,
  `wheel.fullscreenCtrl`, `wheel.rightHeld`, `overlay.wheel`, `overlay.ctrlWheel`) hide the four
  single-direction items (`Zoom in`, `Zoom out`, `Next image`, `Previous image`) and offer only the
  merged pairs plus their reversed variants; **non-wheel slots** hide all four wheel-direction items.
  The Settings matrix builder (`BuildGestureMatrix` in
  `src/Glide.App/Settings/SettingsWindow.axaml.cs`) now populates each combo from `ActionsForSlot`.
- `GestureCatalog.NormalizeActionForSlot(slotId, actionId)` migrates stored bindings, and
  `ResolveAction` routes through it: legacy `zoomIn`/`zoomOut` on a wheel slot resolve to
  `zoomWheel`, `next`/`previous` to `browseWheel`; wheel-only IDs on non-wheel slots fall back to
  `default`. Existing user settings files therefore upgrade silently with no schema change.
- Runtime dispatch: `src/Glide.App/Controls/ImageViewport.cs` (`OnWheel`) handles the reversed
  variants by inverting the zoom/browse direction (composed with the existing
  `InvertWheelDirection` setting via XOR for browsing); `src/Glide.Core/OverlayGesturePolicy.cs`
  maps `ZoomWheel`/`ZoomWheelReversed` to direction-aware overlay zoom. The single-direction
  `ZoomIn`/`ZoomOut` handling in the viewport is kept for non-wheel dispatch paths.
- `src/Glide.Diagnostics/DiagnosticRunner.cs` contracts updated: catalogue **233/233/0**, gesture
  contract renamed `gesture_contract_25x23`, unique-ID count 23.

### 2. Advanced gesture matrix fully searchable in Settings

- `src/Glide.Core/Settings/SettingsCatalog.cs` — added **25 entries**, one per gesture slot, with
  IDs `gesture.<slotId>`, category `Mouse & Fullscreen`, label `Gesture: <slot label>`, kind
  `Action`/"Configure". Catalogue count is now **233** (was 208; test renamed accordingly in
  `tests/Glide.Core.Tests/SettingsCatalogTests.cs`). Search terms include slot words plus
  "zoom in/out", "zoom in/out (reversed)", "previous/next image (reversed)" on wheel slots.
- `src/Glide.Core/Settings/SettingEffectRegistry.cs` — matching 25 bindings
  (`SettingEffectOwner.Input`, `DynamicAction`, `LivePreview`), keeping the catalog/registry
  one-to-one invariant enforced by `SettingEffectRegistry.Validate()`.
- `src/Glide.App/Settings/SettingsWindow.axaml.cs` — (a) `BuildSettingControlMap` documents and
  performs the `gesture.<slotId>` registration; because matrix editors are built lazily,
  `BuildGestureMatrix` registers each combo into `_settingControls` on build and clears the search
  haystack caches; (b) `RunSearchRebuildCore` builds the matrix on first search so result cards
  proxy the real dropdowns; (c) `CreateSearchCompanionButtons` gives every `gesture.*` entry an
  "Open" button jumping to Mouse & keyboard; (d) `SearchHaystackFor` explicitly indexes each
  slot's ID/label/context/default behaviour and every offered action label, so slots are findable
  even before the editors exist.
- Audit: `python tools/audit-settings-search.py` passes (203 editors mapped; gesture editors are
  dynamic, hence indexed via the catalogue + `_settingControls`, consistent with the audit's
  dynamic-control exclusions).

### 3. Status-bar minimise/collapse button stays in place and never closes the app

- Behaviour contract: the status bar's collapse ("−") button is a **toggle**, not a close. The X
  (`CloseStatusClicked`, persists `ShowStatusSurface=false`) is the only close path;
  `CollapseStatusClicked` in `src/Glide.App/MainWindow.ViewerChrome.cs` only flips
  `_statusCollapsed` and re-applies visibility. The caption Minimize button and its configurable
  actions (`GlideSettingsState.Caption*MinimizeAction`, default "Minimize") were inspected and
  were not the defect.
- `src/Glide.App/MainWindow.StatusChrome.cs` — `ApplyStatusVisibility` now treats a windowed
  collapsed bar as **visible but collapsed-only** (`(expanded || collapsedOnly)`), instead of
  hiding the surface and parking a separate expand arrow in the corner; the corner
  `StatusRestoreButton` is fullscreen-only. New `ApplyCollapsedStatusChrome()` hides the stats
  host, all control groups and the X, leaving only the collapse button, whose glyph/tooltip switch
  to Expand via the existing `UpdateCollapseButtonGlyphs`. It is also invoked at the end of
  `ApplyStatusBarSize` (`src/Glide.App/MainWindow.TitleSettings.cs`) so later layout passes cannot
  resurrect hidden controls. `_statusHoverRevealed` makes bottom-edge hover temporarily expand
  the bar in place and return to collapsed-only on leave (never hides the surface outright).
- **Position fix (user-reported follow-up):** the first implementation shrank the centred surface,
  so the button jumped to screen centre. `ApplyCollapsedStatusChrome` now freezes the surface
  width at its expanded value while collapsed (`_collapsedStatusWidth` /
  `_collapsedHomeStatusWidth` in `src/Glide.App/MainWindow.axaml.cs`, captured on the first
  collapsed pass before layout shrinks the bar, clamped to `Bounds.Width - 28`) and restores
  `Width = double.NaN` on expand/hover-reveal. The expand arrow therefore sits at the exact
  position the collapse button occupied and is re-clickable without moving the pointer.
- Home surface parity: `src/Glide.App/Controls/HomeSurface.axaml(.cs)` — the controls panel got
  `x:Name="HomeStatusControlsPanel"` and the hint text `HomeStatusText`; when collapsed, all home
  status buttons except the collapse button and the hint text hide, with the same width freeze.
- Fullscreen behaviour is intentionally unchanged (bar suppressed, corner restore button shown).

### Tests and verification for this checkpoint

- `tests/Glide.Core.Tests/GestureCatalogTests.cs` — action count 21→23; new tests: wheel slots
  offer only merged pairs + reversed, non-wheel slots exclude wheel items, legacy single-direction
  wheel bindings migrate, every slot has a `gesture.*` catalogue entry in "Mouse & Fullscreen",
  reversed overlay wheel direction. The pre-existing resolve test now expects the migrated
  `ZoomWheel` result.
- `tests/Glide.Input.Tests/InputRouterTests.cs` — router test updated for the same migration
  (command resolution asserted on a non-wheel slot).
- `tests/Glide.Core.Tests/StatusChromeLayoutContractTests.cs` — new contract: collapsed surface
  stays visible, `ApplyCollapsedStatusChrome` hides controls/X, collapse path contains no
  `Close()`/`CloseStatusClicked`, and the width freeze/restore lines are present.
- Results: `dotnet build Glide.sln -c Release` = 0 errors; **Glide.Core.Tests 535 passed**;
  **Glide.Input.Tests 19 passed**; `python tools/audit-settings-search.py` passed. The user
  verified earlier behaviour live in the running build; the width-freeze refinement was built,
  tested and relaunched for the user, but **final visual confirmation by the user is the open item
  below**.

## Previous checkpoint — 4.3.2 (2026-09-26)

1. **A viewed image stayed locked after it was closed.** Deleting or moving an image that had already
   been closed (including after a Speed Boost standby close) failed with *"the file is open in Glide
   Image Viewer"*. The metadata/EXIF reader, the print DPI reader, the print-preview decoder and the
   SVG loader opened the user's file without `FileShare.Delete`, so any asynchronous read that
   overlapped the delete/move denied the rename; the SVG loader also handed the raw path to the
   third-party `Svg.Skia` loader. Every user-image read now opens with
   `FileShare.ReadWrite | FileShare.Delete`, and the SVG loader reads the document into a bounded
   in-memory buffer. Regression: `FileHandleReleaseTests` (behaviour for every core format plus a
   source contract that rejects any read-open missing delete sharing, `File.OpenRead`, or
   `svg.Load(path)`).
2. **Version reporting could drift.** The Settings footer still read `Glide 4.2.3` and the
   `MainWindow` XAML fallback `Glide 4.2.5`. Version, assembly, installer, window titles and the
   Settings footer are now aligned to **4.3.2**; the footer label derives from assembly metadata so it
   can never lag the build again.

## Previous checkpoint — 4.3.1 (2026-09-22)

1. **WebP Explorer thumbnails were missing.** Windows has no in-box WebP WIC codec; the in-box
   "Photo Thumbnail Provider" registered for `.webp` fails with
   `WINCODEC_ERR_COMPONENTINITIALIZEFAILURE` unless the optional *Webp Image Extensions* Store package
   is installed, so `.webp` showed a generic icon. Two coordinated fixes:
   - **Native decode.** `Glide.ShellThumbnail.dll` now links a vendored, decode-only **libwebp**
     (`native/third_party/libwebp`, BSD-3-Clause) and decodes lossy VP8, lossless VP8L and alpha WebP
     itself, ahead of WIC. No Store package or runtime download is required.
   - **Registration.** `.webp` is now a Glide-native format in **Recommended** thumbnail mode, so
     Glide claims it even though Windows registers a weak, WIC-dependent handler for it.
   Regression: `ThumbnailRegistrationTests.Recommended_mode_includes_the_glide_native_formats` asserts
   `.webp` stays claimed; the provider was verified against lossy, lossless and alpha WebP fixtures.

## Previous checkpoint — 4.2.3 (2026-09-19)

1. **Folder-boundary prompt could clip its buttons on small/scaled laptop screens.** The prompt used
   a fixed 245 px height while a wrapped long folder path could exceed it. Prompts now size to
   content, clamp to the owner's working area, and scroll long messages. Applied to the folder
   boundary, Escape-close, rename and profile-name dialogs.
2. **One arrow-key press could skip several images on some laptop keyboards.** Some laptop keyboards
   deliver a duplicated KeyDown (switch bounce) that the old code treated as a second navigation
   intent. Glide now uses the existing **Navigation minimum interval** setting with an **80 ms stock
   default** instead of a hard-coded guard; the interval already gates every accepted keyboard and
   mouse-button next/previous action and stays fully user-configurable (0 = unlimited). A schema-5
   migration upgrades the historical stock 0 to 80 while preserving explicit non-zero values. Losing
   window activation also stops any held-navigation producer so a missed KeyUp cannot continue
   browsing. The held-browse producer suppresses the interval indicator to avoid flashing "wait"
   while a key is held.
3. **Settings search is now genuinely global.** A deeper audit of the live Settings window found 10
   settings wired to controls but missing from the catalogue (invisible to search/profiles/
   diagnostics) — including "Always-on-top strength". Six user-facing ones were declared in the
   catalogue with effect-registry bindings; the four removed-Explorer settings stay hidden by
   contract. Search also indexes each editor's visible row label (combo/number/text) in addition to
   `Content`, tooltip and name. The coverage test now asserts every UI-wired setting is declared and
   every setting is findable by label, declared terms and visible wording. Audit result: **189/189
   searchable — 178 editable, 11 actionable**, also verified live in the GUI.
4. **No automatic diagnostic files in the user's Downloads folder.** The always-on crash breadcrumb
   (and its abnormal-exit copy) is now opt-in via `--diagnostic-trace` or `GLIDE_DIAGNOSTIC_TRACE=1`;
   normal installed/portable runs write nothing to Downloads. User-initiated diagnostic/settings
   exports still work as before. Guarded by `DiagnosticPrivacyContractTests`.
5. **Directory organization.** Superseded release notes, the previous full handoff and the audit
   report moved to `Archive/`; the authoritative handoff is now this lean document; the README
   screenshot gallery is restored under `docs/screenshots/`.

## Verification (4.2.3)

| Check | Result |
|---|---|
| Managed Release build | 0 errors |
| Glide.Core.Tests | 294 passed |
| Glide.Input.Tests | 19 passed |
| Settings search coverage audit | 189/189 settings searchable; 178 editable + 11 actionable |
| Live diagnostic export | OVERALL PASS (no nested FAIL) |
| Navigation benchmark | sub-frame presentation retained |
| Installer | `dist-installer\Glide Setup.exe` built and PE-validated |

## One-handoff rule

This is the single authoritative handoff document for the project inside `Glide Image Viewer/`.
Do not add a second file whose name contains "handoff"/"handover" — the packaging contract rejects it.
