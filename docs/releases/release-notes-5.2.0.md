# Glide 5.2.0

Released 2026-11-27. Thumbnails for every format Glide opens, a much faster 45 MP progressive-JPEG folder, a new batch rename and tags, a rebuilt overlay mode, and a fourth round of fixes and ideas from the community. Settings from 5.1.x load unchanged, apart from one one-time migration of the Windows thumbnail defaults (see **Defaults changed** below). The full list is in [CHANGELOG.md](https://github.com/cobhc95/Glide/blob/main/CHANGELOG.md).

## 🙏 Thank you, Vertigo220

Thank you to **Vertigo220** for round 4 of detailed testing and suggestions in [issue #1](https://github.com/cobhc95/Glide/issues/1). Every numbered item below came from that report and shipped in this release, except G2 (a delayed permanent delete with undo): this release adds the confirmation dialog, and the delayed delete is coming in a later update as an opt-in setting.

### 🧾 Vertigo's round 4, item by item

| # | Item | Result |
|---|---|---|
| A1 | 🖱️ Mouse Back / Forward side buttons browse Explorer tabs by default | ✅ done |
| A2 | ↩️ Back from an image returns to the Explorer tab (also after a rename) | ✅ done |
| A3 | 🐛 Back / forward regression | ✅ fixed |
| B1 | 💡 "Alt+drag to move" hint on the text overlay | ✅ done |
| B2 | 📊 Status bar stays put while Alt-dragging the overlay | ✅ done |
| B3 | 📐 Text overlay kept inside the picture area | ✅ done |
| B4 | 🔢 Offset boxes inside the per-layout cards, readable, any whole number | ✅ done |
| B5 | 🎨 Per-position font size, colour, border and template (title bar, tab bar) | ✅ done |
| B6 | ➗ Separator symbol (• ▪ \| – / none) and its colour | ✅ done |
| B7 | 🏷️ Short tokens `{ext} {w} {h} {res} {MP} {filesizeb}`, shown in columns | ✅ done |
| C1 | 🖼️ Explorer thumbnail info template and date formats | ✅ done |
| C2 | 📅 Sort by date taken (EXIF) | ✅ done |
| C3 | 🔍 Alt+double-click opens Properties | ✅ done |
| D1 | 📑 Duplicated tab: position (next to current / at the end) and foreground / background | ✅ done |
| D2 | ⚠️ Warning when both "open instead" keys are the same | ✅ done |
| D3 | 🔠 Modifier label layout | ✅ done |
| D4 | 📝 Unsaved-changes prompt shows readable values | ✅ done |
| D5 | 💬 "Open in new tab (Shift: background)" hint | ✅ done |
| E1 | 🙈 "Hide cursor in normal window" moved to Mouse & keyboard | ✅ done |
| F1 | 📋 Duplicate tab keeps its Back / Forward history | ✅ done |
| F2 | ⚙️ Duplicate tab options | ✅ done |
| G1 | 🗑️ Shift+Delete deletes permanently, with a confirmation | ✅ done |
| G2 | ⏳ Delayed permanent delete | 🔜 coming in a later update, as an opt-in setting (5.2.0 has the confirmation dialog) |
| H1 | ⌨️ Space no longer reopens Settings | ✅ fixed |
| H2 | ⏸️ A paused GIF stays paused when you switch tabs | ✅ fixed |
| I1 | 🗂️ File icons show the extension (JPG, PNG, WEBP...) | ✅ done |
| I2 | 📌 Bigger taskbar icon | ✅ done |
| I3 | 🔖 Explorer and image tab icons tell apart | ✅ done |
| J1 | 🚫 "File no longer on disk" badge | ✅ done |
| J2 | 🖼️ A tab keeps its own picture when its file disappears | ✅ fixed |
| K | 🪟 Overlay mode is an exact copy of a picture-in-picture overlay, plus menu and drag strip | ✅ done |

## ⚙️ Defaults changed: Windows File Explorer thumbnails

- **Thumbnail mode** is now **"Glide for all supported formats"** (it was "Recommended"), and the installer registers Glide's thumbnail provider for every supported format.
- **Thumbnail quality** is now **Fast** (it was Balanced).
- **Existing users:** anyone still on the old defaults is moved to the new ones **once**, at the next start. Anyone who picked another mode or quality keeps it. You can change both again in Settings at any time.
- **After installing, Windows Explorer may need a restart (or a thumbnail-cache clear) before it shows the new thumbnails.** Explorer keeps the thumbnails it already made. Restart Explorer from Task Manager, or run Disk Cleanup and tick "Thumbnails".
- The provider now also reads progressive JPEGs through Glide's own DC-only reader and is registered as free-threaded ("Both"), so Windows can ask for several thumbnails at once. The installer re-registers it on update.

## ⚡ Speed

All numbers are from the 5.2.0 audits in [docs/audits](https://github.com/cobhc95/Glide/tree/main/docs/audits), measured headless on synthetic test images (Ryzen 7 9700X, 8 cores / 16 threads, Windows 11, Release build). Before = the 5.1.x tree (`format-speed-audit-5.2.0.md`), after = the merged 5.2.0 tree (`format-speed-final-5.2.0.md`). Warm = median of 7 runs; cold = first call in a fresh process. No Glide window was started and no installed Glide was touched.

### 🖼️ Explorer thumbnails

| Case | Before | After |
|---|---:|---:|
| 133 progressive JPEGs of 45 MP, time to every thumbnail (decode pipeline, disk cache off) | 1537 ms | 255 ms |
| Same folder in the real Explorer tab, without scrolling | tiles past the screen never prepared until scrolled to (2731 ms scrolling as fast as tiles came) | all 133 ready in 538 ms; the last screen shows 7 ms after scrolling to it |
| One 45 MP progressive JPEG, decode alone | 31.6 ms | 10.5 ms |
| Same, 12 at once | 106 ms | 18.9 ms |
| Memory for 12 such decodes at once | +1576 MB | +56 MB |
| **Windows Explorer provider: one 45 MP progressive JPEG** | **153 ms** | **11.5 ms** |
| Tiled TIFF 24 MP, thumbnail (warm) | 185 ms | 30.8 ms |
| Pyramid TIFF 24 MP, thumbnail (warm) | 189 ms | 3.74 ms |
| CMYK TIFF 4K, thumbnail (warm) | 64.9 ms | 22.9 ms |
| Path-heavy SVG (5000 elements), tile (warm) | 213 ms (as drawn by the viewer decoder) | 29.4 ms (Direct2D) |
| 120 files, every open takes 500 ms, Windows already has the thumbnails | 5386 ms | 156 ms (no file opened) |
| 120 files on a device that takes 1.5 s per open and serves two at a time, 8 s timeout | 10 tiles shown, 110 timed out | all 120 shown, 0 timed out |
| Median tile on screen, mixed 100-file folder, 40 visible tiles | 201 / 219 / 188 ms | 83 / 97 / 94 ms (2.0 - 2.4x sooner) |

**New: thumbnails for every format Glide opens.** The Explorer tab used to ask for 11 suffixes and showed a file-type glyph for everything else. Now 99 of the 100 audit fixtures get a real tile (the exception is a BigTIFF that no decoder in the test setup opens). 51 of them showed only the glyph before. Warm tile cost, 236 px: ICO / CUR about 1 ms, JPEG 2000 5.3 ms, DDS 7 ms, RAW with a 1620 px preview 2.5 - 2.7 ms, TGA / PPM / SGI 21 - 27 ms, QOI 35 ms, EXR 32 - 67 ms, HEIC 4K 71.9 ms, AVIF 4K 34.4 ms, JPEG XL 4K 25.0 ms, PSD 62 - 128 ms. The first HEIC / AVIF / JPEG XL / JPEG 2000 tile or open after a folder listing is 24 - 31 ms faster because the codec module is loaded in the background.

### 📂 Normal image opening (viewer first frame)

| File | Before | After |
|---|---:|---:|
| CMYK JPEG 4K, warm | 428 ms | 54.0 ms |
| CMYK JPEG 4K, cold | 437 ms | 85.2 ms |
| Tiled TIFF 24 MP, warm | 200 ms | 28.9 ms |
| Tiled TIFF 24 MP, cold | 229 ms | 63.2 ms |
| Pyramid TIFF 24 MP, warm | 205 ms | 11.7 ms |
| Pyramid TIFF 24 MP, cold | 231 ms | 38.1 ms |
| CMYK TIFF 4K, warm | 68.5 ms | 17.5 ms |
| BigTIFF 24 MP, warm | did not open (WIC cannot read it) | 29.3 ms |

Everything else (plain JPEG, PNG, GIF, BMP, WebP, JPEG XR, RAW, PSD, EXR, DDS...) is unchanged within run-to-run noise, as intended. One deliberate slowdown: the first view of an SVG with an embedded raster picture takes 131 ms instead of 62.5 ms warm, because the preview is now scaled whole into the requested frame instead of being cropped.

### 💾 Setup size

`Glide Setup.exe` is **21,293,497 bytes (20.3 MB)**, against 19,624,381 bytes for 5.1.6: **+1,669,116 bytes (+8.5 %)**. Measured against the unreleased 5.1.7 build (21,146,645 bytes), the 5.2.0 work itself added about 70 KB; the rest of the growth comes from the 5.1.7 changes that are now part of this release. No new library was added.

## ✨ What else is new

### 🏷️ Batch rename and tags (issue #4)
- Batch rename rebuilt around plain boxes: **New name** mixes typed text with pieces (Old name, Number, Date taken, Date modified, Folder name, Size), plus **Replace text**, **Numbers**, **Capitals**, saved presets, a live Now / After table with conflict warnings, and **Undo last rename**.
- Tags in file names (off by default): tag groups, prefix, brackets and separator, and a tag sidebar (Ctrl+Shift+G).

### 🪟 Overlay mode and picture-in-picture
- Overlay mode draws the same chrome as a picture-in-picture overlay, with the same menu and drag strip.
- Resizing is smoother: one native window move per step instead of separate moves and resizes.
- The hover highlight is a ring around the border, not a fill over the picture.
- **Preserve aspect ratio** on: edge handles crop the picture (the opposite edge stays put). Off: the picture is stretched. Overlay mode has the same toggle.

### 📁 Explorer
- Windows-style type-to-select, parity keys (F3, Ctrl+Shift+C, Ctrl+Insert / Shift+Insert, Ctrl+Shift+1 to 6), cross-drive drag and drop copies.
- Thumbnails remembered on disk (size limit and "Clear thumbnail cache" in Settings), "Thumbnails loaded at once" and whole-folder background preparation.
- Folder position line with the parent folder now reads `6/94 - (10/30)`.
- Info template under thumbnails, sort by date taken, Alt / Ctrl / Shift + double-click rows.

### 🧰 Other
- Settings reopens exactly as you left it. Tabs torn off into new windows are placed correctly and never lost. Opening from a program run as administrator is instant. Speed Boost no longer leaves duplicate processes.
- New file-type icons with the extension for 39 common formats, and a fuller app icon.
- Diagnostics: `--diagnostic-trace` logs a line per Explorer thumbnail.

## 🐞 Fixes

Batch rename dialog placement and scrolling (including the Numbers box fitting its fields), tab tear-off and re-attach, paused GIFs across tabs, Space reopening Settings, the "File no longer on disk" badge, the text overlay offsets and hint, development and portable builds taking over the installed file associations, thumbnail timeouts on slow disks, and more: see the [changelog](https://github.com/cobhc95/Glide/blob/main/CHANGELOG.md).

## ⚠️ Known issue

- 🗂️ **Tearing a tab off takes a long pull.** A tab has to be dragged quite far out of the tab bar before it detaches into its own window. This will be fixed in the next update.
- 🪟 **A few flickers when resizing in overlay mode.** Resizing is smoother than before but can still flicker now and then. This will be fixed in the next update.
- 🖼️ **Not every file type shows its own icon yet.** JPG and others show a file-type icon, but some (for example PNG) still show the plain Glide icon in Windows Explorer. Every supported extension will get its own icon in the next update.

## Downloads

- `Glide.Setup.exe`: installer (Windows 10 1607 or later, x64)
- `Glide-5.2.0-Portable.zip`: portable build (SHA256 `17A75E5BAFEE46C3E6E9AF67C592B57C54D83E6A8AC2559D78D922C8F25D23DB`)
- `Glide.Setup.sha256`: checksum (SHA256 `D76B540CECE45E524B0A7554554B36471855201E378AC9EBDCE7A4819E8F972B`)
