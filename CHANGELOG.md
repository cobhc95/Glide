# Changelog

All notable changes to Glide Image Viewer are documented here. The format is based on
[Keep a Changelog](https://keepachangelog.com/), and the project uses dotted feature releases with
hyphenated follow-up corrections (for example `4.2.0-1`).

## [4.3.1] - 2026-09-22

Follow-up to 4.3.0 that fixes WebP thumbnails in Windows Explorer.

### Fixed

- 🖼️ **WebP files now show real thumbnails in Explorer.** Windows ships no in-box WebP codec — it
  is delivered only by the optional *Webp Image Extensions* Store package — and the in-box
  "Photo Thumbnail Provider" registered for `.webp` fails without it
  (`WINCODEC_ERR_COMPONENTINITIALIZEFAILURE`), leaving a generic icon. Glide now:
  - links a **vendored, decode-only libwebp** (`native/third_party/libwebp`, BSD-3-Clause) into
    `Glide.ShellThumbnail.dll`, decoding lossy VP8, lossless VP8L and alpha WebP directly; and
  - treats `.webp` as a Glide-native format in **Recommended** thumbnail mode, so Glide claims it
    even though Windows registers a weak handler for it.

  Both changes are self-contained: no Store package and no runtime download is required.

### Changed

- Version, assembly, installer and UI metadata advanced to **4.3.1**.

## [4.3.0] - 2026-09-22

Explorer integration and window-management release. Glide now draws real thumbnails in Windows
Explorer, multiple Glide windows each get their own taskbar button, and minimizing can no longer
lose a window.

### Added

- 🖼️ **Windows Explorer thumbnail provider.** Explorer now shows real thumbnails for the formats
  Glide can open instead of a generic icon. A small native COM shim
  (`Glide.ShellThumbnail.dll`) decodes inside Explorer's isolated thumbnail host — it never loads
  .NET, Avalonia, Glide.exe or any Glide UI, never creates a window, and never shows a dialog.
  - Format coverage is **derived from Glide's format registry**, so a newly supported format becomes
    thumbnail-capable automatically (196 extensions, 210 when counting every suffix Glide opens).
  - Decode strategy: embedded thumbnail/preview first, then decoder-native reduced decode (WIC), then
    a bounded full decode; SVG is rasterised with Direct2D at the size Explorer asked for.
  - **Measured:** median **1 ms**, p95 **13 ms**, 100% under 20 ms across a 300-image corpus
    (640×480 up to 8000×6000) plus TGA/SVG/QOI/PPM, counting COM activation per item.
    See `docs/explorer-thumbnails.md` and `tools/thumbnail-benchmark.ps1`.
  - Windows keeps owning the thumbnail cache; Glide adds no second database.
- ⚙️ **Settings → Windows Integration → Explorer thumbnails**: master toggle, provider mode
  (Recommended / unsupported-only / all / custom), per-group and per-extension selection, thumbnail
  quality (Fast/Balanced/High), embedded-preview preference, maximum source decode size, a
  diagnostics readout (provider present, registered, architecture, formats, mode, version) and
  actions to re-register, restore recommended defaults and refresh the Windows thumbnail cache.
- 🗂️ **Each Glide window is its own taskbar button.** Secondary windows now keep their own taskbar
  entry and receive a distinct AppUserModelID (verified on real windows), so multiple Glide windows
  no longer collapse into a single button.
- 🔧 **Installer integration** for the thumbnail provider (machine-wide `HKLM`) with clean uninstall,
  including stopping the warm host before removal.

### Fixed

- 🪟 **Multiple Glide windows now appear as separate taskbar entries.** The single-taskbar
  representative suppression was removed; the registry no longer toggles `ShowInTaskbar`.
- 🗕 **Minimizing can no longer make a window vanish.** Because every window now keeps its own
  taskbar button and `ExitStandby` restores it after Speed Boost standby, a minimized window can
  always be restored from the taskbar.

## [4.2.7] - 2026-09-22

Automatic Windows integration release. Glide now associates itself with every supported format when
it is installed, with no trip to Options.

### Added

- 🔗 **Automatic Open With integration.** Glide adds itself to Windows' **Open With** menu for all
  **196** supported extensions without the user having to configure anything:
  - The **installer** registers it **machine-wide** (`HKLM`) for every user of the PC, immediately
    after the files are copied — including in silent installs.
  - A **portable** copy (and any first launch) registers it **per-user** (`HKCU`) automatically.
  - Registration is idempotent, runs at most once per version, is deferred to background priority so
    it can never affect first paint, and never touches Windows' protected default-app (`UserChoice`)
    selection.
- 🚫 **Opt-out is respected.** Removing Glide from Open With in **Settings → Windows Integration**
  records the choice, so automatic integration does not silently re-add it. Re-adding clears it.
- 🧹 **Clean uninstall.** The uninstaller removes the machine-wide registration it created.

### Changed

- The **File associations / Open With** setting now describes the automatic behaviour and is used to
  re-add or remove the per-user registration rather than to enable integration in the first place.
- The installer no longer hard-codes a short list of common formats; it invokes Glide's own
  registration so the set can never drift from `ImageFormatRegistry`.
- Diagnostic and benchmark harnesses (`--capture-print-dialog`, benchmark/first-frame flags and
  diagnostics export) never mutate the real Open With registration.

## [4.2.6] - 2026-09-22

Print correctness release plus the first major expansion of native format decoding. Every recognized
suffix now has a defined, non-crashing route, and a new family of bundled decoders removes the
optional-codec dependency for vector and legacy raster formats. See `docs/format-coverage.md` for the
full route map and the IrfanView comparison.

### Added

- 🖼️ **Native SVG / SVGZ (vector) support.** `.svg` and gzip-compressed `.svgz` are now rasterised
  in-process with Svg.Skia at exactly the resolution needed — a bounded render for browse previews
  (so vectors stay crisp at any zoom) and an intrinsic-size render, capped at 4096 px, for the full
  1:1 and print source. Transparency is preserved.
- 🧩 **Bundled built-in raster decoders** for the long tail of simple formats that neither Skia nor
  Windows WIC can be relied on for, so they work on a clean PC with no optional codec installed:
  **TGA/TARGA/ICB/VDA/VST**, **PCX**, **PNM (PBM/PGM/PPM/PAM)**, **QOI**, **Radiance HDR/RGBE**,
  **WBMP**, **XBM**, **XPM**, and **SGI/RGB/RGBA/BW**. All entry points are total: malformed or
  hostile input returns "cannot decode" instead of throwing, with strict allocation caps.
- 🪟 **Windows Shell last-resort decode.** When Skia, WIC and the built-in decoders all decline a
  recognized suffix, Glide now accepts a genuine Windows thumbnail/preview handler for *any*
  recognized format (never a generic file icon), so PDF, EPS/AI, Office, CAD and camera-RAW files
  render whenever the platform has a handler. Alpha may be flattened on this route.
- 📊 **Format coverage documentation** (`docs/format-coverage.md`): the full decode chain, a
  per-group coverage matrix, and a side-by-side comparison with IrfanView's format/plug-in list.

### Fixed

- 🔄 **Auto-rotate now rotates instead of stretching.** "Automatically rotate for best fit" performs a
  real 90° rotation in both the live preview and the spooled output.
- 🖥️ **Actual-size / Fill-page no longer overflow the window.** Oversized renders are clipped to the
  content box, so the preview keeps its buttons and margins and gains a scrollbar instead of pushing
  the controls off-screen.
- 🖨️ **Changing printer settings to landscape no longer crashes** and is now reflected in the preview
  and orientation radios. The driver DEVMODE is seeded/read through `DocumentProperties` instead of a
  fragile handle copy.
- ⬛ **Grayscale is truthful.** Colour/grayscale now changes the preview and is forced onto the spooled
  bitmap with a Rec.601 colour matrix, so drivers that ignore `PageSettings.Color` (such as Microsoft
  Print to PDF) still print grayscale.
- 📄 **Paper Source is populated** from the driver's real trays and the row is hidden (rather than
  shown empty) when the driver reports none.
- 📜 **The Print window scrolls** instead of cutting options off on short screens; the left options
  panel is a scrollable grid.

### Changed

- The decode failure message now names the suffix and points at the optional-codec requirement
  (e.g. `No decoder for .cr2 (optional codec required)`) instead of a generic error.

## [4.2.5] - 2026-09-22

Image-printing release. Glide now has a complete, WYSIWYG image print workflow modelled on the mature
viewers (FastStone Image Viewer, IrfanView, XnView). See `docs/print-research.md` for the Phase-1
reference research and `docs/screenshots/print-research/` for the FastStone 8.5 reference capture.

### Added

- 🖨️ **Print the current image** from `Ctrl+P`, the viewer right-click menu, and the browser item
  Context menu. It prints the original full-resolution decoded image — never a screenshot of the
  viewport, so Glide's zoom, pan, selection, overlay borders and other chrome are never baked in.
- 🪟 **Glide-native Print window** with a live WYSIWYG page preview that updates immediately on any
  paper, orientation, scaling, margin or alignment change:
  - Printer selector and native **Properties…** (driver preferences) button
  - Paper size and paper source (when the driver reports trays)
  - Portrait / Landscape, Copies (1–99), Colour / Grayscale (when supported)
  - Scaling: **Best fit**, **Fill page** (crop to fill), **Actual size (100%)**, **Custom scale %**,
    **Stretch**; with **Keep aspect ratio** and **Automatically rotate for best fit**
  - Alignment: Left / Centre / Right and Top / Centre / Bottom (all nine positions)
  - Four independent margins in the locale's physical unit (mm/in)
- **Embedded-DPI Actual size.** JFIF density, EXIF X/YResolution + unit, PNG `pHYs`, TIFF and BMP
  resolution fields are read; absent or nonsensical values fall back to a documented **96 DPI**.
- **Layout preferences persist** between sessions (scaling, margins, auto-rotate, alignment,
  orientation, copies) in `print.settings.json`. Printer-specific capabilities are never persisted.
- Print command is user-bindable through the hotkey catalogue; the **Print** title-bar button is
  available in the title-bar customizer with a new vector glyph.

### Notes

- **Printing is high quality:** the source image's full resolution is spooled, aspect ratio is
  preserved except in the explicit Stretch mode, the printer's real printable rectangle is respected
  (zero margins mean "to the printable edge", not over it — avoiding the historical XnView
  preview/print divergence), alpha is composited against white, and EXIF orientation is honoured.
- **No print code runs at startup.** Printer enumeration and the GDI+/System.Drawing pipeline load
  only when Print is invoked, so normal navigation, overlay mode, fullscreen, zoom/pan, tabs and
  startup performance are unaffected.
- Single-image printing is complete. Multi-image / contact-sheet printing is intentionally deferred
  to a follow-up (the layout engine is page-shaped so it can be added without disturbing v1).

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
