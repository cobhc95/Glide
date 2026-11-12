# GLIDE 4.7.0 — ACTIVE HANDOFF

**Current active release:** Glide 4.7.0 (single-sourced from `Directory.Build.props`). Release files
and source metadata must stay aligned to 4.7.0 until a newer version is explicitly requested. The
changes in the "Current checkpoint — unreleased" section below are built and tested but are **not**
a version bump and have not been packaged into a release. Historical checkpoints, audits and the
previous full handoff are archived under `Archive/`.

## Release output contract (permanent)

Every clean release build removes generated caches and prior compiled outputs, then produces:

1. `Glide-<version>-Portable.zip` — published `dist` contents.
2. `dist-installer\Glide Setup.exe` — compiled Windows setup installer.
3. `Glide-<version>.exe` — standalone executable for immediate testing.
4. `Glide-Zero-Context-Handover.zip` — essentials-only source handoff (source, tests, scripts and
   this document; binaries, caches, generated artifacts and nested ZIPs excluded).

The version is single-sourced from `Directory.Build.props`. Build scripts read it and must never
hard-code a version in banners, output names or fixture identities.

## Build, test, run

```bat
build.cmd                  rem clean build: native + managed + tests + diagnostics + publish + portable + installer + handover
build.cmd fast --no-pause  rem compile/publish/package without running tests
build-installer.cmd        rem installer only
run.cmd                    rem run from source
```

Toolchain: .NET 8 SDK (x64), MSVC VS 2022+ (or BuildTools) with the C++ x64 workload, CMake 3.20+,
Inno Setup 6 or 7. No administrator rights are required for the app itself.

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

## Current checkpoint — unreleased (after 4.7.0): wheel-gesture merge, searchable gesture matrix, status-bar collapse position

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

### Open items / watch-outs for the next agent

1. **Awaiting user confirmation** that the collapsed expand button now stays in place (viewer and
   Home surfaces, windowed mode). Ask the user before assuming it is accepted.
2. Known deliberate asymmetry: fullscreen collapse still uses the corner restore button; do not
   "unify" it without asking, it was pre-existing intended behaviour.
3. `GestureCatalog.Actions` is a global list; per-slot filtering lives in `ActionsForSlot`. Any
   new caller that renders action choices must use `ActionsForSlot`, not `Actions` directly.
4. The catalogue count (233) and gesture counts (25 slots × 23 actions) are hard-coded in
   `SettingsCatalogTests`, `DiagnosticRunner` and this section — update all together.
5. No version bump, packaging, installer or release notes were produced for this checkpoint.

### Prompt for the next agent (review and continue)

> You are continuing work on the Glide image viewer in this workspace. First read
> `AGENTS.md` and the "Current checkpoint — unreleased" section above, then **review the changes
> it describes before changing anything else**: re-read the listed hunks in
> `GestureCatalog.cs`, `OverlayGesturePolicy.cs`, `ImageViewport.cs` (`OnWheel`),
> `SettingsCatalog.cs`, `SettingEffectRegistry.cs`, `SettingsWindow.axaml.cs`
> (`BuildGestureMatrix`, `BuildSettingControlMap`, `SearchHaystackFor`,
> `CreateSearchCompanionButtons`), `MainWindow.StatusChrome.cs` (`ApplyStatusVisibility`,
> `ApplyCollapsedStatusChrome`, `UpdateStatusHoverReveal`), `MainWindow.TitleSettings.cs`
> (`ApplyStatusBarSize` tail), `MainWindow.ViewerChrome.cs` (`CollapseStatusClicked`), and
> `HomeSurface.axaml(.cs)`. Re-run `dotnet build Glide.sln -c Release`,
> `dotnet test tests/Glide.Core.Tests` , `dotnet test tests/Glide.Input.Tests` and
> `python tools/audit-settings-search.py` and report whether they still pass. Check specifically:
> (a) legacy gesture settings files still load and migrate; (b) searching Settings for e.g.
> "Windowed: mouse wheel", "zoom in/out (reversed)" and "right button" finds the gesture slots;
> (c) collapsing the status bar leaves the expand button at the same screen position on both the
> viewer and Home surfaces, and clicking it re-expands; (d) the X still fully closes the bar and
> Alt+Shift+S still toggles collapse. **Then talk to the user**: summarise your review findings,
> state any discrepancies you found between this handoff and the code, ask whether the collapsed
> button position is now correct, and wait for their direction before making further changes. Do
> not bump the version or package a release unless the user asks.

## Current checkpoint — 4.3.2 (2026-09-26)

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
