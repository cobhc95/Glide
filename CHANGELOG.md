# Changelog

All notable changes to Glide Image Viewer are documented here. The format is based on
[Keep a Changelog](https://keepachangelog.com/), and the project uses dotted feature releases with
hyphenated follow-up corrections (for example `4.2.0-1`).

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
