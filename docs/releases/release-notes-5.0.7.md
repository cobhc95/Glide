# Glide 5.0.7

A speed release with a new choice for what happens at the end of a folder. Settings from 5.0.x load unchanged, apart from two one-time migrations (see **Defaults and migration** below). The full list is in [CHANGELOG.md](https://github.com/cobhc95/Glide/blob/main/CHANGELOG.md).

## Faster opening

- **TIFF about 4x faster** (0.18–0.38x the time), **BMP 1.4–1.7x faster**: large files decode in parallel bands with identical pixels.
- **JPEG full decode 1.3–1.8x faster**: libjpeg-turbo, in parallel bands for large baseline files. Pixels match the Windows decoder within one level per channel; CMYK and unusual JPEGs still use Windows.
- **Camera JPEGs show a picture almost at once**: a large embedded preview (MPF) is shown first and the full picture swaps in moments later (45 MP: 225 → 39 ms). Tiny EXIF thumbnails are never used.
- **JPEG XL, HEIC and JPEG 2000** are 12–20% faster: the bundled codec module is built for speed and can use every processor core. AVIF is unchanged.

Measured on a 16-thread desktop through Glide's own open path (median of several runs; details in [docs/perf/benchmark-report-5.0.7.md](https://github.com/cobhc95/Glide/blob/main/docs/perf/benchmark-report-5.0.7.md)):

| File | 5.0.6 | 5.0.7 |
|---|---:|---:|
| TIFF 24 MP LZW, first picture | 184 ms | 39 ms |
| TIFF 100 MP LZW, first picture | 768 ms | 164 ms |
| JPEG 24 MP (Canon R6 II), full resolution | 131 ms | 97 ms |
| JPEG 45 MP (Nikon D850), full resolution | 300 ms | 230 ms |
| JPEG 24 MP progressive, full resolution | 445 ms | 253 ms |
| JPEG 45 MP camera file with embedded preview, first picture | 225 ms | 39 ms |
| JPEG 24 MP rotated (EXIF), full resolution | 163 ms | 118 ms |
| JPEG XL 12 MP / 24 MP lossless | 151 / 256 ms | 130 / 206 ms |
| HEIC 12 MP | 164 ms | 144 ms |
| JPEG 2000 12 MP | 315 ms | 252 ms |

## What's new

- **When the last image is reached** (Settings > Window & navigation > Moving between folders): Do nothing, Loop this folder, Move to the next folder (default), Move to the next folder in a new tab, Go back to the previous folder, Show Home, or **Ask me each time**.
- **Ask me each time** shows a small card over the picture with the next folder's name and numbered choices (arrow keys + Enter, a number key, or the same Next / Previous key again). **Remember this choice** saves it as the setting.
- Status-bar button **Loop this folder** (on by default): loops the current tab's folder, whatever the setting says. It is per tab and not saved.
- Explorer: the selected thumbnail has a crisp accent outline on the edge of its highlight, with even spacing above and beside the picture. Long file names that must break mid-word break before the extension.

## Fixes

- Explorer: a selected thumbnail's wrapped file name no longer spills below the selection highlight; names longer than two lines end with "…" (full name in the tooltip).
- Camera RAW previews: Glide picks the largest preview anywhere in the file. Mamiya MEF, Hasselblad 3FR/FFF, Kodak DCR, Epson ERF and Phase One IIQ show larger or, for the first time, any preview; IIQ opens about 3x faster.
- Newly decoded: headerless DIB, Apple ICNS, Sun raster, X11 window dumps (XWD), DPX and OpenEXR with DWAA/DWAB compression.
- Files that only resemble a WBMP no longer open as a tiny black-and-white picture.
- Settings: the selected category in the left rail has square corners.

## Defaults and migration

- **Automatically continue into sibling folders** becomes **When the last image is reached**: on → Move to the next folder, off → Do nothing.
- A hotkey or gesture bound to the old "Toggle: Continue through sibling folders" action is dropped; bind "Cycle: When the last image is reached" or "Loop this folder (this tab)" instead.
- A customised status bar gets the **Loop this folder** button once, after the navigation buttons. Remove it in Customize status-bar buttons if you do not want it.

## Downloads

- `Glide.Setup.exe`: installer (Windows 10 1607 or later, x64)
- `Glide-5.0.7-Portable.zip`: portable build
- `Glide.Setup.sha256`: checksum
