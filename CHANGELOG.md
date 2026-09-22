# Changelog

All notable changes to Glide Image Viewer are documented here. The format is based on
[Keep a Changelog](https://keepachangelog.com/), and the project uses dotted feature releases with
hyphenated follow-up corrections (for example `4.2.0-1`).

## [4.2.4] - 2026-09-22

Polish and correctness release. See the GitHub release notes for the same list.

### Fixed

- 🪟 **Minimizing no longer makes the window vanish.** With Speed Boost on, minimizing the last window
  also entered the hidden standby path, so it disappeared from the taskbar leaving only the tray icon.
  Minimize is a real Windows minimize again; standby stays reserved for closing the last window.
- 🖼️ **Aspect ratio is preserved for EXIF-rotated photos.** Pictures with an EXIF orientation tag
  (portrait phone/camera shots, orientation 5–8) were stretched because the reported source size
  ignored the rotation the decoder applied. Source orientation is now aligned to the decoded bitmap
  on every decode route, so these images fit correctly.
- 🖱️ **Fullscreen right-click (backwards) now tolerates small pointer movement.** Left-click forward
  fired on press, but backwards navigation required a near-motionless pointer; both now share a
  forgiving click-versus-drag tolerance.
- 🧰 **Title bar and status bar resizing.** Title-bar icons now yield one at a time as the window
  narrows instead of all vanishing at once. The status bar now sizes to its real wrapped content — a
  single line hugs its buttons (no reserved phantom second row or extra width) and it only grows when
  a row is genuinely added.

### Added

- 📐 **Fit image button + Fit control scope.** The status bar has a new **Fit image** control beside
  Fit width / Fit height (same Shift+W behavior), and all three follow the new **Fit control scope**
  setting (`status.fitScope`, default **This session and future sessions**): Only the current image /
  This session / This session and future sessions.
- 🧲 **Auto-resize status bar** (`status.autoResize`, default on): shrink the buttons only when the
  window is too narrow to fit them within two rows, restoring the configured size when it grows.
  Toggle in Settings → Status or the status bar right-click menu.
- 📌 **Always on top in the context menus** — a tickable toggle is directly visible in the windowed
  viewer menu and the fullscreen chrome menu.

### Changed

- **Always-on-top strength now defaults to Soft** (Settings → Interface & Behavior). The choice remains
  Soft / Hard; Hard still reasserts topmost after competing Z-order changes.
- **Settings: the Animation tab is folded into Window in Window.** Overlay animation controls (region,
  speed, turn interval/angle, pause, avoid-overlap, refresh target) now sit with the other overlay
  settings, since animation only applies to Window-in-Window overlays.

## [4.2.3-3] - 2026-09-21

### Fixed

- **Some photos were stretched/distorted (EXIF-rotated images).** Glide's lightweight header probe
  reports the raw stored pixel dimensions of a JPEG (e.g. 6016×4016), but the native WIC decoder
  honours the EXIF orientation tag and returns the rotated bitmap (e.g. 4016×6016). The viewport sized
  its destination rectangle from the reported source size and stretched the bitmap into it, so any
  photo with EXIF orientation 5–8 (portrait phone/camera shots, the reported `0089.jpg`) was drawn at
  the wrong aspect ratio. The loader now aligns the reported source orientation with the bitmap the
  decoder actually produced, for every decode route (startup preload, path preview, full/preview
  decode and neighbour prefetch), so rotation is respected without distortion for JPEG and any other
  eXIf-carrying format. Raw/native decode of `0089.jpg` was confirmed as 6016×4016 in and
  4016×6016 out.

## [4.2.3-2] - 2026-09-21

### Added

- **Fit control scope (`status.fitScope`, default "This session and future sessions").** The status bar
  Fit Width / Fit Height controls now carry the chosen fit mode forward instead of snapping back on the
  next image. The new Settings → Status option chooses the scope: **Only the current image** (previous
  behavior), **This session** (every image until Glide closes), or **This session and future sessions**
  (also becomes the persisted Default view for new images).
- **Direct "Always on top" toggle in the viewer context menus.** Right-clicking the image in the
  ordinary window now shows a tickable **Always on top** item at the top level, and the fullscreen
  chrome context menu exposes the same item, so topmost can be toggled without leaving fullscreen or
  opening Settings. The tick reflects the active state (ordinary window or Overlay mode).

### Changed

- Settings search coverage is now **189/189** catalogued settings searchable (178 editable + 11
  actionable) after adding the Fit control scope entry.

## [4.2.3-1] - 2026-09-19

### Fixed

- **Minimizing could make the window disappear.** With Speed Boost enabled (the default), minimizing
  the last Glide window also entered the Speed Boost *standby* path, which removed the window from the
  taskbar and hid it, leaving only the tray icon. A minimized window therefore looked like it had
  closed, and the behavior depended on how many Glide windows were open (only the last window entered
  standby) — matching the intermittent "I minimized it and the window vanished" report. Minimize is
  now a genuine Windows minimize that keeps the window in the taskbar and restorable; Speed Boost
  standby stays reserved for *closing* the last window. Standby additionally requires an
  actually-visible tray icon, so it can never hide the last window with no way back.

## [4.2.3] - 2026-09-19

Search-coverage, small-screen and keyboard-input release.

### Fixed

- **Folder-boundary prompt clipped its buttons on small/scaled screens.** The prompt used a fixed
  245 px height; long folder paths wrapped past it and pushed Continue/Cancel out of view. Prompts
  now size to content, clamp to the owner's working area and scroll long messages. Applied to the
  boundary, Escape-close, rename and profile-name dialogs.
- **One arrow-key press could skip several images on some laptop keyboards.** The existing
  **Navigation minimum interval** setting now ships an 80 ms stock default (instead of a hard-coded
  guard) so a duplicated key event inside the interval is ignored; it remains fully user-configurable
  (0 = unlimited). A schema-5 migration upgrades the historical stock 0 to 80 while preserving
  explicit non-zero values. Losing window activation also stops any held navigation producer so a
  missed KeyUp cannot continue browsing.
- **No automatic diagnostic files in Downloads.** The always-on crash breadcrumb previously wrote
  `Glide-Diagnostic-Latest.txt` (and an abnormal-exit copy) to the user's Downloads folder on every
  run. It is now opt-in only (`--diagnostic-trace` or `GLIDE_DIAGNOSTIC_TRACE=1`); normal installs
  write nothing there.
- **Settings search is now genuinely global.** A deeper audit of the live Settings window found
  **10 settings wired to controls but missing from the catalogue** (invisible to search, profiles and
  diagnostics) — including "Always-on-top strength" (`windows.alwaysOnTopMode`). Six user-facing ones
  were declared in the catalogue; the four removed-Explorer settings stay hidden by contract. Search
  additionally indexes each editor's visible row label, not just its `Content`, so combo/number/text
  settings are found by the wording shown on screen.

### Added

- `SettingsSearchCoverageTests`: asserts every UI-wired setting is declared in the catalogue
  (excluding the intentionally hidden Explorer controls) and that every setting is findable by label,
  terms and visible on-screen wording. Audit: **188/188 searchable (177 editable, 11 actionable)**.

### Changed

- Superseded release notes, the previous full handoff and the audit report moved to `Archive/`; the
  authoritative handoff is now a lean current-state document.
- Version metadata advanced to 4.2.3.

## [4.2.2] - 2026-09-19

Stability and responsiveness release. Fixes a default-configuration crash, removes Settings-search
typing lag, and cleans up release metadata. See [release-notes-4.2.2.md](release-notes-4.2.2.md)
for full detail and evidence.

### Fixed

- **Crash when closing/minimizing with multiple image tabs (default configuration).** Speed Boost
  standby hid the window and then posted `Close()`; the re-entrant closing handler tried to show the
  multi-tab close confirmation against the now hidden owner, which Avalonia rejects. The async-void
  handler rethrew the exception and terminated the process — matching the reported
  `startup-crash.log`. The confirmation is now skipped for standby/hidden owners, owned dialogs are
  refused safely when the owner is not visible, and the confirmation path can no longer escape as an
  unhandled exception.
- **Settings search typing lag.** Search rebuilt a live editor for every match on every keystroke
  (measured 23–39 ms and up to 200 controls per keystroke for common letters). Input is now
  debounced (140 ms), the final query is always rendered, and a per-hotkey list allocation was
  removed.
- **Duplicate "Open" buttons in Settings search results** for action entries such as the Central
  command registry and the gesture matrix. Action rows now render only their labelled companion
  button(s).
- **Maximum-quality performance profile** used the same preview side as Balanced (1800), failing the
  product's own `profile_coordination` diagnostic so every diagnostic export reported `OVERALL FAIL`.
  Maximum quality now uses a larger preview (2400).
- **Self-audit false positive:** `escape.resetRememberedClose` was reported as "unexpectedly
  disabled" even though it is legitimately disabled while the remembered Escape choice is "Ask".

### Changed

- Build banners, deliverable names and diagnostic fixture identities are now derived from a single
  version source (`Directory.Build.props`) instead of hard-coded strings, and all prior versioned
  portable outputs are cleaned generically. This removes the 4.2.0-vs-4.2.1 naming drift.
- Version, assembly, installer and UI metadata advanced to **4.2.2**.
- Stale "Glide 3.0" strings in the crash log, diagnostic bundle header and the native overlay window
  class name were corrected.
- The headless test harness now blocks until a dispatched UI action completes, so UI assertions can
  no longer silently no-op.

### Documentation

- Added this changelog and `release-notes-4.2.2.md`.
- Added the missing `docs/screenshots/` gallery referenced by the README.

## [4.2.1] - 2026-09-17

### Added

- Settings-search completeness: audited the catalogue against the live Settings controls and added
  searchable/editable entries for always-on-top strength, fullscreen exit behaviour, same-image
  behaviour, embedded Explorer theme, new Explorer-tab location controls, folder-navigation
  behaviour, left-window-drag behaviour, Overlay interaction behaviour, and the two slideshow toggles
  previously missing from the editable search map.
- Normalized Overlay/Profile catalogue categories so category-filtered search finds them under
  "Window in Window" and "Profiles & Presets".
- Hotkey/interaction presets can be selected and applied directly from search; reset-hotkeys is
  exposed as a search action.

### Changed

- Always-on-top wording replaced with a technical Soft/Hard explanation.

## [4.2.0] - 2026-09-17

### Added

- Built-in Explorer overhaul with Back/Forward history that preserves the selected file per folder,
  Up navigation that highlights the exited child, and view-mode changes that retire the previous
  `ItemsRepeater` generation.

### Changed

- Folder traversal uses the native Windows logical comparator (`StrCmpLogicalW`), so numeric
  sequences order like Explorer.
- Removed smooth image transitions everywhere; frame changes are immediate.
- Overlay resize behaves like a normal cover-fit resize (no exponential zoom, no letterbox gaps);
  overlay controls auto-hide with the chrome.
- Title-bar Previous/Next Folder buttons route through the canonical serialized folder-navigation
  path.

### Fixed

- Sibling scan-ahead could skip folders; direct sibling navigation now advances exactly one sibling
  at a time, and reparse/link folders are no longer silently removed (with cycle protection).
- Overlay crash when opening multiple instances of the same overlay image.

## [4.1.9] - 2026-09-16

- Audited folder-boundary traversal so nested picture branches are not skipped, with deterministic
  tie-breaking and stale-navigation protection.
- Restored native overlay zoom/enlarge and the on-overlay transparency slider.
- Added independent width-only and height-only overlay resizing.
- Improved high-refresh overlay motion timing and reduced zig-zag after manual repositioning.

## [4.1.7] - 2026-09-16

- Per-overlay animation by region, speed, turn behaviour and interaction pausing.
- Overlay layouts retain each overlay's animation profile and running state; save/load via
  `Ctrl+Shift+S` / `Ctrl+Shift+L`.

## [4.1.6] - 2026-09-16

- Fixed an instant crash opening multiple instances of the same overlay image.
- Added defensive rendering boundaries for Window-in-Window overlays.

## [4.1.5] - 2026-09-15

- Window-placement stability on warm restores; no hidden Alt-Tab windows or duplicate taskbar icons.
- Fullscreen left-click navigates backward; slideshow right-click stop and play-quality indicator.

## [4.1.4] - 2026-09-15

- Navigation crash diagnostic/stability release: transactional Home/End navigation, atomic viewport
  presentation, and expanded diagnostics.

## [4.1.1] - 2026-09-14

- Launch Speed Boost warm engine with a ~3–4 MB standby footprint, Snap preservation, a theme-matched
  privacy curtain, single warm instance, and an installer running-instance safety check.
