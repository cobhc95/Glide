# Format speed, final 5.2.0: before / after on the merged build

The full format audit (100 fixtures, every decoder family) re-run on the merged 5.2.0 tree and set against the baseline in
`docs/audits/format-speed-audit-5.2.0.md` (commit `fa5dfb2`; that document's tables are the "before" numbers, the raw result folder of that run was not kept).
"After" = the tree at `83f3c93` (native TIFF / CMYK / HEIC / JPEG XL preview, thumbnails for every format, scheduler, SVG via Direct2D, codec preload, tear-off fixes),
Release build, `Glide.Native.dll` and `glidecodecs.dll` built from that tree (codec manifest SHA-256 `2e1b5be2...beaaa`), same PC (Ryzen 7 9700X, 8 cores / 16 threads, Windows 11 LTSC 26100, .NET 10),
same fixtures, same harness (`tools/perf/run-format-audit.ps1`, phases thumb-warm, thumb-cold, view-warm, view-cold; median of 7 warm runs, median of 5 fresh processes for cold).
Headless; no Glide window was started. The table is produced by `tools/perf/compare-format-final.py`.

## Setup size

| | Bytes | MB |
|---|---:|---:|
| 5.1.7 `Glide Setup.exe` | 21,146,645 | 20.17 |
| 5.2.0 `Glide Setup.exe` (this build, 2026-11-27 12:45, SHA-256 `F9696A4B...A629`) | 21,216,826 | 20.23 |
| Difference | +70,181 | +0.33 % |

## How to read the table

- Columns: **thumbnail cold** (production `ThumbnailDecode.DecodeToWidth` at 236 px, first call in a fresh process), **thumbnail warm** (median of 7), **view first cold** (viewer first frame, fresh process), **view first warm** (median of 7). Each cell is "before -> after (factor)"; "same" = within 10 %.
- **glyph (as-if N)**: the Explorer tab showed the file-type glyph for this format before 5.2.0 (it asked for 11 suffixes only), so there was no tile to time; N is what the viewer's decoder bounded to 480 px cost (the audit's "As-if thumbnail" table) and the factor is against that. Every one of these formats now has a real thumbnail.
- **d** after a baseline number: the baseline row carried a dagger or noisy flag in the audit (machine busy when it was measured). **\*** after a new number: that sample is noisy (spread over 40 % of the median and over 3 ms, or the machine was above 35 % CPU at the start of the phase).
- BigTIFF has no baseline: WIC cannot open it, so the fixture failed to open at all before.

## What moved

- **TIFF** (audit item 2): tiled 24 MP 185 -> 31 ms warm thumbnail (6x), 200 -> 29 ms viewer first frame (7x); SubIFD pyramid 24 MP 189 -> 3.7 ms thumbnail (50x), 205 -> 11.7 ms first frame (18x); tiled 4K 6x / 7x; CMYK TIFF 2.8x / 3.9x. Plain strip TIFFs (LZW, PackBits, raw, deflate strips) are unchanged.
- **CMYK JPEG** (item 6): viewer first frame 428 -> 54 ms warm (7.9x), 437 -> 85 ms cold (5.1x); its thumbnail is unchanged (entropy-decode bound).
- **Thumbnails for every format** (item 1): 51 fixtures went from the glyph (or, for BigTIFF, from not opening) to a tile, 0.6 - 128 ms warm; the largest gains over the viewer decoder are JPEG 2000 (13.4 -> 5.3 ms warm), path-heavy SVG (213 -> 29 ms), JPEG XL (1.2 - 2.0x), HEIC / AVIF (1.2 - 1.6x), ICO first tile (40 -> 18 ms).
- **HEIC / AVIF / JPEG XL previews**: the tile route calls the plug-in's `DecodePreviewSurfaceAsync` (`ThumbnailExtendedDecode.DecodeProvider`), so the native shortcuts apply. A HEIC with an embedded thumbnail item (fixture `heic-thumb.heic`, 1600 x 1200 plus a 320 x 240 item) costs 2.15 ms warm at 236 px against 9.76 ms at 480 px, where the item no longer covers the request and the main picture decodes; `ThumbnailEveryFormatTests.A_heic_tile_is_served_from_the_thumbnail_item_through_the_provider_preview_call` asserts it from the pixels (the item has inverted colours). The audit's synthetic 4K / 12 MP HEIC fixtures carry no thumbnail item, so their tile decodes the picture (72 / 98 ms warm); real phone files carry one. JPEG XL tiles of one eighth of the picture or less come from the DC image (12 MP 61.6 -> 31.4 ms warm, 4K 50 -> 25 ms).
- **Codec preload** (item 5) is not in these numbers: it acts on the first open after a folder listing, and the audit's cold cells open a file in a fresh process with no listing before it. Its measurement is in `format-thumbnails-5.2.0.md` (-24 to -31 ms for the first HEIC / AVIF / JPEG XL / JPEG 2000).
- **Everything else** (JPEG, PNG, GIF, BMP, WebP, JPEG XR, RAW, PSD, EXR, DDS, ...) is within the run-to-run spread, as intended.

## Noise, re-runs and the one real slowdown

The PC was in use by someone else during the runs (a UI session; CPU 3 - 31 % at phase starts, no phase above the 35 % busy threshold, but short bursts are possible).
The first full pass flagged 27 files with a "slower" or noisy cell (mostly TIFF strips, WebP and small warm thumbnails of 3 - 30 ms, where 1 - 4 ms is 10 - 30 %). Those 27 files were re-measured
with the baseline build (the fa5dfb2 tree) and the merged build back to back under the same load (thumb-warm, view-warm, thumb-cold, view-cold), and the six files still noisy were measured a third time:

- Every TIFF-strip and WebP "slower" cell was noise: baseline against merged in the same conditions is within 0 - 10 % in both directions (tiff-raw-4k thumbnail warm 4.71 -> 5.05, tiff-lzw-24mp 29.6 -> 29.4, webp-24mp 54.6 -> 56.5).
- Still flagged after three passes (the machine, not the code: samples spread over 40 %): hdr-4k thumbnail warm and view warm (noisy in the baseline too), svg-5000 view warm, sgi-4k view cold.
- **One real, deliberate slowdown: the viewer's first frame of an SVG with an embedded raster** (svg-embedded-png: 62 -> 131 ms warm, 182 -> 256 ms cold; svg-5000 view cold 351 -> 392 ms has the same cause). `SvgDecoder.Render` now scales the picture to the requested preview size; before 5.2.0 it drew it 1:1 into the requested frame, so a preview of a different size than the document showed a cropped or tiny picture until the full render replaced it. The correct preview costs a larger raster. Path-heavy SVG tiles are 6 - 7x faster through Direct2D; the viewer's first view of an SVG still goes through Svg.Skia.
- Cells still marked "slower" in the table: nifti-256 / ppm-4k / svg-simple compare a real tile with the as-if number of a decoder that costs 0.5 - 26 ms (the tile includes the cache write and, for the first SVG tile, Direct2D set-up; 4 ms against 1 ms for a trivial SVG); EXR view warm 41.5 -> 47.0 ms (the same-load control measured 43.3 -> 47.0, inside the spread); tiff-raw-4k thumbnail warm 4.35 -> 5.05 ms (control 4.71 -> 5.05, noise).

## Summary

thumb_cold: 99 compared, 48 faster (> 1.1x), 50 same, 1 slower; median ratio 1.09x

thumb_warm: 99 compared, 34 faster (> 1.1x), 61 same, 4 slower; median ratio 1.03x

view_cold: 99 compared, 15 faster (> 1.1x), 82 same, 2 slower; median ratio 1.02x

view_warm: 99 compared, 16 faster (> 1.1x), 81 same, 2 slower; median ratio 1.02x

## Table

| Fixture | Thumbnail cold (ms) | Thumbnail warm (ms) | View first cold (ms) | View first warm (ms) |
|---|---|---|---|---|
| apng-720p.png | 21.6d -> 22.5 (same) | 6.36 -> 6.53 (same) | 48.5d -> 43.9 (1.1x) | 9.21 -> 9.41 (same) |
| avif-12mp.avif | glyph (as-if 127) -> 80.4 (1.6x) | glyph (as-if 60.5) -> 48.7 (1.2x) | 138d -> 121 (1.1x) | 64.7 -> 57.4 (1.1x) |
| avif-4k.avif | glyph (as-if 107) -> 67.0 (1.6x) | glyph (as-if 46.5) -> 34.4 (1.4x) | 119 -> 108 (1.1x) | 51.2 -> 45.5 (1.1x) |
| avif-alpha-4k.avif | glyph (as-if 132) -> 82.1 (1.6x) | glyph (as-if 68.6) -> 49.1 (1.4x) | 139 -> 122 (1.1x) | 76.3 -> 60.1 (1.3x) |
| bmp-24mp.bmp | 24.2 -> 24.8 (same) | 7.72 -> 7.40 (same) | 55.7 -> 50.8 (same) | 19.0 -> 18.1 (same) |
| bmp-32alpha-4k.bmp | 21.0 -> 22.2 (same) | 4.73 -> 4.66 (same) | 47.2 -> 41.5 (1.1x) | 8.00 -> 7.94 (same) |
| bmp-4k.bmp | 19.9 -> 21.8 (same) | 4.16 -> 4.08 (same) | 46.3 -> 39.9 (1.2x) | 7.71 -> 7.40 (same) |
| bmp-8bit-4k.bmp | 20.1 -> 21.2 (same) | 4.00 -> 3.68 (same) | 45.1 -> 39.7 (1.1x) | 7.37 -> 6.89 (same) |
| cur-256.cur | glyph (as-if 38.7) -> 17.8 (2.2x) | glyph (as-if 1.07) -> 1.02 (same) | 40.3 -> 39.1 (same) | 1.19 -> 1.01 (1.2x) |
| dds-dxt1-4k.dds | glyph (as-if 53.0) -> 28.1 (1.9x) | glyph (as-if 10.5) -> 7.11 (1.5x) | 60.4 -> 60.6 (same) | 20.3d -> 18.6 (same) |
| dds-dxt1-mips-4k.dds | glyph (as-if 53.9) -> 28.9 (1.9x) | glyph (as-if 11.0) -> 7.21 (1.5x) | 68.0 -> 63.3 (same) | 19.6 -> 19.3 (same) |
| dds-dxt5-4k.dds | glyph (as-if 61.1) -> 32.1 (1.9x) | glyph (as-if 15.6) -> 10.3 (1.5x) | 77.3 -> 71.0 (same) | 26.6 -> 25.6 (same) |
| dds-rgba-4k.dds | glyph (as-if 46.1) -> 38.3 (1.2x) | glyph (as-if 20.4) -> 17.1 (1.2x) | 49.2 -> 48.6 (same) | 22.2 -> 23.7 (same) |
| exr-none-float-4k.exr | glyph (as-if 73.4) -> 63.3 (1.2x) | glyph (as-if 50.4) -> 32.2 (1.6x) | 75.6 -> 76.0 (same) | 43.8d -> 40.4 (same) |
| exr-rle-half-4k.exr | glyph (as-if 86.2) -> 75.6 (1.1x) | glyph (as-if 50.9) -> 45.0 (1.1x) | 87.6 -> 85.7 (same) | 41.5 -> 47.0 (1.1x slower) |
| exr-zip-float-4k.exr | glyph (as-if 109) -> 100 (same) | glyph (as-if 75.8) -> 67.4 (1.1x) | 122 -> 111 (1.1x) | 73.1 -> 79.4 (same) |
| exr-zip-half-4k.exr | glyph (as-if 77.3) -> 69.3 (1.1x) | glyph (as-if 44.1) -> 37.2 (1.2x) | 88.0 -> 80.3 (same) | 46.5 -> 50.0 (same) |
| exr-zips-half-4k.exr | glyph (as-if 84.7) -> 77.4 (same) | glyph (as-if 39.6) -> 39.8 (same) | 97.5 -> 89.1 (same) | 44.1 -> 42.0 (same) |
| fits-int16-2k.fits | glyph (as-if 55.1) -> 47.2 (1.2x) | glyph (as-if 24.8) -> 22.5 (1.1x) | 54.1 -> 51.1 (same) | 25.2 -> 27.4 (same) |
| gif-4k.gif | 32.4 -> 34.1 (same) | 16.3 -> 15.6 (same) | 61.9 -> 59.2 (same) | 23.3 -> 23.4 (same) |
| gif-anim-720p.gif | 19.2 -> 20.0 (same) | 2.12 -> 2.07 (same) | 41.0 -> 36.5 (1.1x) | 2.58 -> 2.40 (same) |
| hdr-4k.hdr | glyph (as-if 151) -> 143 (same) | glyph (as-if 65.6) -> 63.6 (same) * | 154 -> 154 (same) | 69.1d -> 68.1 (same) * |
| heic-12mp.heic | glyph (as-if 178) -> 134 (1.3x) | glyph (as-if 114) -> 97.6 (1.2x) | 193 -> 177 (same) | 123 -> 112 (same) |
| heic-4k.heic | glyph (as-if 147) -> 106 (1.4x) | glyph (as-if 84.6) -> 71.9 (1.2x) | 167 -> 149 (1.1x) | 88.6 -> 86.8 (same) |
| ico-256.ico | glyph (as-if 40.1) -> 17.2 (2.3x) | glyph (as-if 0.98) -> 0.96 (same) | 42.8d -> 39.5 (same) | 1.00 -> 1.05 (same) |
| j2k-4k.j2k | glyph (as-if 115) -> 37.4 (3.1x) | glyph (as-if 13.0) -> 5.40 (2.4x) | 268 -> 246 (same) | 146 -> 146 (same) |
| jp2-4k.jp2 | glyph (as-if 113) -> 37.2 (3.0x) | glyph (as-if 13.4) -> 5.34 (2.5x) | 250 -> 244 (same) | 144 -> 144 (same) |
| jp2-lossless-4k.jp2 | glyph (as-if 119) -> 41.2 (2.9x) | glyph (as-if 17.1) -> 7.27 (2.4x) | 311 -> 312 (same) | 210 -> 212 (same) |
| jpeg-444-4k.jpg | 29.5 -> 30.2 (same) | 18.1 -> 17.2 (same) | 66.7 -> 66.3 (same) | 33.3 -> 33.3 (same) |
| jpeg-base-12mp.jpg | 24.2 -> 25.3 (same) | 13.2 -> 12.9 (same) | 51.6 -> 52.9 (same) | 19.6 -> 19.6 (same) |
| jpeg-base-24mp.jpg | 37.5 -> 38.2 (same) | 25.6 -> 25.1 (same) | 71.4 -> 72.5 (same) | 39.0 -> 39.3 (same) |
| jpeg-camera-mpf-24mp.jpg | 13.3 -> 14.8 (same) | 2.08 -> 2.04 (same) | 33.7 -> 33.2 (same) | 6.37 -> 6.50 (same) |
| jpeg-cmyk-4k.jpg | 52.8 -> 53.0 (same) | 31.1 -> 31.7 (same) | 437 -> 85.2 (5.1x) | 428 -> 54.0 (7.9x) |
| jpeg-exif-thumb-24mp.jpg | 43.5 -> 43.2 (same) | 30.6 -> 30.6 (same) | 72.6 -> 71.3 (same) | 39.9 -> 39.2 (same) |
| jpeg-gray-4k.jpg | 19.4 -> 20.4 (same) | 8.06 -> 8.12 (same) | 53.9 -> 54.1 (same) | 20.8 -> 20.4 (same) |
| jpeg-icc-4k.jpg | 19.6 -> 20.9 (same) | 8.65 -> 8.73 (same) | 54.8 -> 54.5 (same) | 22.6 -> 21.2 (same) |
| jpeg-prog-12mp.jpg | 19.6 -> 21.3 (same) | 8.60 -> 7.85 (same) | 80.6 -> 80.3 (same) | 56.4 -> 53.0 (same) |
| jpeg-prog-24mp.jpg | 27.9 -> 29.9 (same) | 16.6 -> 16.5 (same) | 138 -> 135 (same) | 113 -> 108 (same) |
| jxl-12mp.jxl | glyph (as-if 125) -> 64.0 (2.0x) | glyph (as-if 61.6) -> 31.4 (2.0x) | 128 -> 124 (same) | 67.6 -> 59.7 (1.1x) |
| jxl-4k.jxl | glyph (as-if 111) -> 57.0 (1.9x) | glyph (as-if 50.0) -> 25.0 (2.0x) | 117 -> 114 (same) | 53.9 -> 46.3 (1.2x) |
| jxl-alpha-4k.jxl | glyph (as-if 145) -> 101 (1.4x) | glyph (as-if 81.6) -> 66.3 (1.2x) | 150 -> 145 (same) | 89.7 -> 75.0 (1.2x) |
| jxl-lossless-4k.jxl | glyph (as-if 162) -> 121 (1.3x) | glyph (as-if 107) -> 86.5 (1.2x) | 168 -> 163 (same) | 103 -> 97.8 (same) |
| jxr-12mp.jxr | 22.0 -> 22.9 (same) | 3.83 -> 3.50 (same) | 225 -> 224 (same) | 194 -> 193 (same) |
| jxr-4k.jxr | 20.4 -> 22.1 (same) | 2.77 -> 2.41 (1.1x) | 180 -> 177 (same) | 144 -> 141 (same) |
| ktx1-rgba8-4k.ktx | glyph (as-if 56.5) -> 47.1 (1.2x) | glyph (as-if 20.7) -> 19.2 (same) | 57.3 -> 56.2 (same) | 23.6 -> 24.5 (same) |
| mpo-12mp.mpo | glyph (as-if 111) -> 36.0 (3.1x) | glyph (as-if 21.6) -> 13.9 (1.6x) | 153 -> 151 (same) | 62.0 -> 62.0 (same) |
| nifti-256x256x64.nii | glyph (as-if 31.8) -> 16.5 (1.9x) | glyph (as-if 0.47) -> 0.63 (1.4x slower) | 32.2 -> 34.9 (same) | 0.53 -> 0.51 (same) |
| ora-4k.ora | glyph (as-if 81.8) -> 74.1 (1.1x) | glyph (as-if 51.8) -> 51.8 (same) | 85.2 -> 82.9 (same) | 55.7 -> 57.4 (same) |
| pcx-4k.pcx | glyph (as-if 76.3) -> 67.9 (1.1x) | glyph (as-if 47.5) -> 45.1 (same) | 79.4 -> 77.5 (same) | 48.3 -> 50.9 (same) |
| pfm-4k.pfm | glyph (as-if 85.2) -> 78.1 (same) | glyph (as-if 47.4) -> 43.6 (same) | 89.6 -> 87.1 (same) | 47.8 -> 49.5 (same) |
| png-16bit-4k.png | 84.2 -> 81.3 (same) | 64.1 -> 64.0 (same) | 111 -> 110 (same) | 77.5 -> 76.4 (same) |
| png-24mp.png | 153 -> 150 (same) | 135 -> 134 (same) | 170 -> 165 (same) | 133 -> 132 (same) |
| png-4k.png | 67.1 -> 69.9 (same) | 50.9 -> 51.0 (same) | 86.8 -> 84.9 (same) | 51.2 -> 51.5 (same) |
| png-alpha-4k.png | 93.9 -> 94.5 (same) | 78.3 -> 78.0 (same) | 110 -> 109 (same) | 76.2 -> 76.6 (same) |
| png-gray-4k.png | 42.9 -> 42.7 (same) | 25.9 -> 26.3 (same) | 60.2 -> 59.5 (same) | 27.1 -> 25.7 (same) |
| png-interlaced-4k.png | 108 -> 108 (same) | 93.3 -> 92.0 (same) | 101 -> 103 (same) | 69.0 -> 67.5 (same) |
| png-palette-4k.png | 31.4 -> 31.8 (same) | 14.9 -> 15.8 (same) | 49.6 -> 48.4 (same) | 16.3 -> 15.2 (same) |
| png-up-filter-4k.png | 74.9 -> 73.1 (same) | 56.0 -> 58.4 (same) | 89.2 -> 86.7 (same) | 56.3 -> 53.9 (same) |
| ppm-4k.ppm | glyph (as-if 55.5) -> 48.3 (1.1x) | glyph (as-if 23.1) -> 26.2 (1.1x slower) | 58.6 -> 58.6 (same) | 26.7 -> 25.5 (same) |
| psd-16bit-raw-4k.psd | glyph (as-if 99.6) -> 91.0 (same) | glyph (as-if 65.3) -> 65.6 (same) | 101 -> 101 (same) | 71.7 -> 69.4 (same) |
| psd-layers-rle-4k.psd | glyph (as-if 109) -> 97.6 (1.1x) | glyph (as-if 74.1) -> 69.7 (same) | 109d -> 107 (same) | 72.5 -> 76.4 (same) |
| psd-raw-4k.psd | glyph (as-if 93.9) -> 86.7 (same) | glyph (as-if 64.4) -> 62.3 (same) | 97.3d -> 95.6 (same) | 66.0 -> 68.6 (same) |
| psd-rle-4k.psd | glyph (as-if 99.0) -> 87.9 (1.1x) | glyph (as-if 66.0) -> 65.3 (same) | 98.1 -> 97.3 (same) | 63.0 -> 43.8 (1.4x) |
| psd-zip-pred-4k.psd | glyph (as-if 158) -> 152 (same) | glyph (as-if 122) -> 128 (same) | 159 -> 161 (same) | 105 -> 105 (same) |
| qoi-4k.qoi | glyph (as-if 60.2) -> 52.8 (1.1x) | glyph (as-if 35.4) -> 35.5 (same) | 64.3 -> 64.3 (same) | 37.5 -> 38.8 (same) |
| raw-arw-1620-preview-24mp.arw | glyph (as-if 32.8) -> 23.7 (1.4x) | glyph (as-if 3.70) -> 2.70 (1.4x) | 41.5 -> 39.9 (same) | 11.1 -> 10.2 (same) |
| raw-cr3-1620-preview-24mp.cr3 | glyph (as-if 32.5) -> 22.4 (1.4x) | glyph (as-if 3.64) -> 2.57 (1.4x) | 42.2d -> 40.2 (same) | 10.0 -> 10.3 (same) |
| raw-dng-1600-preview-24mp.dng | glyph (as-if 32.0) -> 22.4 (1.4x) | glyph (as-if 3.55) -> 2.51 (1.4x) | 39.2d -> 38.9 (same) | 10.7 -> 9.77 (same) |
| raw-nef-fullsize-preview-24mp.nef | glyph (as-if 64.1) -> 57.9 (1.1x) | glyph (as-if 33.6) -> 33.4 (same) | 109d -> 108 (same) | 76.4 -> 77.9 (same) |
| raw-raf-fullsize-preview-24mp.raf | glyph (as-if 62.4) -> 54.7 (1.1x) | glyph (as-if 33.3) -> 31.8 (same) | 96.0 -> 92.4 (same) | 62.8 -> 64.3 (same) |
| sgi-4k.sgi | glyph (as-if 57.2) -> 47.2 (1.2x) | glyph (as-if 25.8) -> 27.0 (same) | 57.8 -> 57.9 (same) * | 27.8 -> 30.1 (same) |
| svg-5000.svg | glyph (as-if 345) -> 56.2 (6.1x) | glyph (as-if 213) -> 29.4 (7.3x) | 351 -> 392 (1.1x slower) | 188d -> 183 (same) * |
| svg-embedded-png.svg | glyph (as-if 173) -> 127 (1.4x) | glyph (as-if 66.0) -> 67.8 (same) | 188 -> 256 (1.4x slower) | 62.5 -> 131 (2.1x slower) |
| svg-simple.svg | glyph (as-if 93.5) -> 32.4 (2.9x) | glyph (as-if 1.01) -> 4.55 (4.5x slower) | 102 -> 108 (same) | 3.18 -> 3.45 (same) |
| svgz-5000.svgz | glyph (as-if 323) -> 58.4 (5.5x) | glyph (as-if 127) -> 27.5 (4.6x) | 369 -> 358 (same) | 170d -> 137 (1.2x) |
| tga-4k.tga | glyph (as-if 51.7) -> 44.5 (1.2x) | glyph (as-if 24.9) -> 23.9 (same) | 59.2 -> 56.5 (same) | 24.1 -> 26.1 (same) |
| tga-rle-4k.tga | glyph (as-if 53.3) -> 46.6 (1.1x) | glyph (as-if 22.0) -> 21.1 (same) | 59.0 -> 55.2 (same) | 21.7 -> 23.1 (same) |
| tiff-16bit-deflate-4k.tif | 44.9 -> 42.5 (same) | 23.9 -> 23.2 (same) | 68.8 -> 70.9 (same) | 28.6 -> 28.6 (same) |
| tiff-bigtiff-deflate-24mp.tif | n/a -> 40.3 | n/a -> 31.1 | n/a -> 59.1 | n/a -> 29.3 |
| tiff-cmyk-4k.tif | 86.4d -> 40.1 (2.2x) | 64.9 -> 22.9 (2.8x) | 103d -> 58.7 (1.8x) | 68.5 -> 17.5 (3.9x) |
| tiff-deflate-24mp.tif | 49.2d -> 47.3 (same) | 29.2d -> 29.4 (same) | 76.5d -> 81.3 (same) | 38.1d -> 36.1 (same) |
| tiff-deflate-4k.tif | 31.7d -> 31.6 (same) | 12.5 -> 12.3 (same) | 52.0d -> 53.0 (same) | 16.8 -> 16.4 (same) |
| tiff-float32-4k.tif | 49.4 -> 44.1 (1.1x) | 27.3 -> 24.5 (1.1x) | 64.9 -> 67.7 (same) | 32.2 -> 29.1 (1.1x) |
| tiff-gray-4k.tif | 25.8 -> 24.5 (same) | 6.25 -> 6.44 (same) | 44.3 -> 47.2 (same) | 8.78 -> 9.42 (same) |
| tiff-jpeg-4k.tif | 34.3 -> 32.7 (same) | 14.5 -> 13.4 (same) | 53.7 -> 54.1 (same) | 17.5 -> 17.0 (same) |
| tiff-lzw-24mp.tif | 50.5 -> 49.8 (same) | 33.7 -> 29.4 (1.1x) | 76.6 -> 76.9 (same) | 41.8 -> 37.3 (1.1x) |
| tiff-lzw-4k.tif | 30.6 -> 31.1 (same) | 12.1 -> 12.0 (same) | 49.8 -> 50.4 (same) | 14.2 -> 13.6 (same) |
| tiff-multipage-1080p.tif | 23.1 -> 23.8 (same) | 4.49 -> 4.81 (same) | 53.5 -> 53.5 (same) | 15.4 -> 15.7 (same) |
| tiff-packbits-4k.tif | 22.8 -> 23.6 (same) | 3.50 -> 3.76 (same) | 42.0 -> 43.3 (same) | 6.31 -> 6.70 (same) |
| tiff-pyramid-deflate-24mp.tif | 232 -> 16.4 (14.1x) | 189 -> 3.74 (50.5x) | 231 -> 38.1 (6.1x) | 205 -> 11.7 (17.6x) |
| tiff-raw-24mp.tif | 28.8 -> 28.1 (same) | 8.51 -> 8.81 (same) | 53.8 -> 55.7 (same) | 18.2 -> 18.1 (same) |
| tiff-raw-4k.tif | 24.7 -> 24.5 (same) | 4.35 -> 5.05 (1.2x slower) | 42.8 -> 45.0 (same) | 7.81 -> 7.47 (same) |
| tiff-tiled-deflate-24mp.tif | 214d -> 41.1 (5.2x) | 185 -> 30.8 (6.0x) | 229d -> 63.2 (3.6x) | 200 -> 28.9 (6.9x) |
| tiff-tiled-deflate-4k.tif | 91.3d -> 22.9 (4.0x) | 72.6d -> 11.4 (6.4x) | 108d -> 41.2 (2.6x) | 77.3d -> 10.6 (7.3x) |
| webp-24mp.webp | 67.0d -> 66.5 (same) | 55.3 -> 56.5 (same) | 94.3d -> 97.0 (same) | 65.9 -> 65.3 (same) |
| webp-4k.webp | 31.6 -> 32.9 (same) | 20.5 -> 21.3 (same) | 66.1 -> 64.9 (same) | 33.6 -> 33.7 (same) |
| webp-alpha-4k.webp | 54.6 -> 54.9 (same) | 42.2 -> 43.6 (same) | 107 -> 101 (same) | 64.7 -> 62.4 (same) |
| webp-anim-720p.webp | 24.5 -> 24.7 (same) | 2.89 -> 2.90 (same) | 33.8 -> 32.9 (same) | 3.61 -> 3.53 (same) |
| webp-lossless-4k.webp | 122d -> 121 (same) | 107 -> 108 (same) | 167d -> 168 (same) | 137 -> 138 (same) |
| webp-lossless-alpha-4k.webp | 139 -> 135 (same) | 122 -> 122 (same) | 162 -> 162 (same) | 127 -> 126 (same) |
