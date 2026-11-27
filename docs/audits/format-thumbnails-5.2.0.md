# Thumbnails for every format the viewer opens, 5.2.0: before / after

Implements audit items 1, 4, 5 and 9 of `docs/audits/format-speed-audit-5.2.0.md` (Explorer-tab thumbnails for every format; SVG; codec preload; cost-aware
queue with look-ahead). Everything was measured headless (no Glide window was started, no installed Glide was touched) with the audit's own harness
(`tools/perf/run-format-audit.ps1`, `Glide.PerfBench`, `Glide.FormatAudit`) on the audit's 100 synthetic fixtures (`tools/perf/generate_format_fixtures.py`),
before = the tree at `fa5dfb2`, after = this branch. Hardware, method and the noise rule are those of the audit: AMD Ryzen 7 9700X, 61.6 GB, Windows 11 LTSC,
.NET 10, plain Release build (no ReadyToRun), **warm = median of 7 runs in one process, cold = median of 5 fresh processes**. A cell is flagged `~` when its warm
samples spread by more than 40 % of the median and more than 3 ms, and `!` when the machine was above 35 % CPU when the cell started.

## Summary

| Item | Result |
|---|---|
| Formats with a thumbnail in the Explorer tab | **11 suffixes before, every format the viewer decodes now**: 99 of the 100 audit fixtures (51 of them showed the glyph before). The one without is the BigTIFF fixture, which no decoder here opens. A file that cannot be decoded keeps the glyph. |
| Cost of the new thumbnails (236 px, warm) | ICO / CUR 1 ms, JPEG 2000 5.5 ms, DDS 7 ms, RAW with a 1620 px preview 2.7 - 2.9 ms (full-size preview 32 - 34 ms), TGA / PPM / SGI 25 - 26 ms, QOI 34 ms, EXR 40 ms, PCX 46 ms, PFM 47 ms, PSD 62 - 122 ms, HDR 63 ms, AVIF 54 ms, JPEG XL 55 ms, HEIC 107 ms; every cached repeat 0.1 - 0.3 ms. |
| SVG tiles (item 4) | 5000-element SVG **26.9 ms warm against 213 ms** in the audit (Svg.Skia at 480 px), 8x; SVGZ 28.6 ms; trivial SVG 4.7 ms. Direct2D draws plain documents only; text, CSS, filters, masks, patterns, markers, DOCTYPE and any image element stay on Svg.Skia (the embedded-PNG SVG is 70 ms, as before). |
| Idle pre-initialisation of Svg.Skia | First SVG in a process: viewer first view 102 -> 28 ms (trivial SVG, cold), tile 34 -> 10 ms; path-heavy SVG viewer 345 -> 292 ms, tile 62 -> 35 ms (the rest is parsing the document). |
| Codec preload (item 5) | First open of HEIC / AVIF / JPEG XL / JPEG 2000 **-24 to -31 ms** (viewer, cold process after the preload: HEIC 4K 152 -> 124, AVIF 12 MP 127 -> 103, JXL 12 MP 130 -> 100, JP2 248 -> 217); first tile -27 to -31 ms (HEIC 4K 110 -> 82, AVIF 4K 73 -> 42, JXL 4K 75 -> 48, JP2 37 -> 10). |
| Queue order and look-ahead (item 9) | Mixed 100-file folder, 40 visible tiles, 12 lanes, 152 px, three shuffles: the median tile shows up **2.0 - 2.4x sooner** (201 / 219 / 188 ms -> 83 / 97 / 94 ms), the 10th tile 70 / 70 / 85 -> 47 / 50 / 38 ms, the mean tile 205 / 209 / 171 -> 172 / 160 / 127 ms (-16 to -26 %); the last tile of the screen arrives at the same time (422 / 422 / 373 -> 426 / 409 / 310 ms). The look-ahead has no headless time to report (it needs the UI); it is covered by a test that its result is a disk-cache hit. |
| Setup size | **+10,361 bytes** in `dist-installer\Glide Setup.exe` (21,161,980 -> 21,172,341, +0.05 %; both from `build.cmd`, the first from the base commit). Added DLL bytes (Release IL): `Glide.dll` +17,920, `Glide.Imaging.dll` +33,792, `Glide.Native.dll` +3,584 (941,056 -> 944,640). No new third-party library and no new DLL. |
| Existing formats | Unchanged within noise: warm thumbnails and warm viewer first / full view of JPEG, PNG, GIF, BMP, TIFF, WebP and JPEG XR move by less than the run-to-run spread (the WebP 24 MP thumbnail 67 -> 55 ms is libwebp thread placement; its code path is untouched). Cold thumbnails of those formats are 1 - 3 ms slower (first use of the route table, JIT in this non-ReadyToRun harness). |
| One viewer change that costs time | The viewer's SVG preview drew the picture 1:1 into a frame of the requested size (cropped or tiny when the sizes differ; `SvgTileRendererTests.The_svg_preview_scales_the_whole_picture_into_the_requested_frame` fails on the old code). It now scales the whole picture, so the audit's embedded-PNG SVG first view is 63 -> 132 ms warm: the real cost of drawing it, the cropped render skipped most of it. Path-heavy SVG first view: 157 -> 146 ms. |

## Before / after, one fixture per format family

Thumbnail = `ThumbnailDecode.DecodeToWidth(path, 236)`, disk cache off. "glyph" = the Explorer tab showed the file-type icon (for a suffix the tab did not ask a thumbnail for, the
decoder-only number is what Skia would have cost had it asked). View first = the viewer's first frame at a 2560 x 1369 viewport (`ImageLoadCoordinator.LoadForegroundAsync`, codec
plug-ins on the route); view full = the full-resolution decode. "after + preload" = the same cold run after the folder listing had warmed the codec plug-in (and the idle SVG warm-up
had run). All times in ms.

| Fixture | Thumb cold ms before | after | after + preload | Thumb warm ms before | after | View first cold ms before | after | after + preload | View first warm ms before | after | View full warm ms before | after |
|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| jpeg-base-24mp.jpg | 36.3 | 39.3 |  | 25.9 | 25.0 | 73.2 | 72.3 |  | 39.1 | 39.3 | 35.0 | 34.4 |
| jpeg-prog-24mp.jpg | 28.1 | 30.0 |  | 16.5 | 16.2 | 137 | 135 |  | 111 | 111 | 124 | 123 |
| jpeg-camera-mpf-24mp.jpg | 13.1 | 14.4 |  | 2.13 | 2.74 | 33.3 | 34.2 |  | 7.43 | 7.06 | 58.3 | 51.9 |
| mpo-12mp.mpo | glyph (29.4 decoder only) | 36.8 |  | glyph (14.3 decoder only) | 13.9 | 149 | 150 |  | 61.8 | 62.5 | 49.6 | 50.6 |
| png-24mp.png | 150 | 157 |  | 133 | 132 | 169 | 170 |  | 133 | 135 | 134 | 135 |
| apng-720p.png | 21.2 | 24.5 |  | 6.12 | 6.11 | 44.2 | 43.4 |  | 9.58 | 9.70 | 9.79 | 9.76 |
| gif-4k.gif | 32.1 | 32.6 |  | 16.2 | 18.2~ | 58.5 | 59.0 |  | 24.8 | 24.2 | 24.5 | 24.1 |
| bmp-24mp.bmp | 22.7 | 25.4 |  | 7.10 | 7.23 | 50.4 | 52.9 |  | 17.7 | 18.0 | 17.8 | 18.4 |
| ico-256.ico | glyph (16.3 decoder only) | 17.2 |  | glyph (1.00 decoder only) | 1.01 | 40.9 | 38.3 |  | 1.06 | 1.10 | 0.93 | 0.90 |
| cur-256.cur | glyph (16.2 decoder only) | 17.8 |  | glyph (1.11 decoder only) | 1.02 | 40.2 | 39.7 |  | 1.06 | 1.04 | 0.87 | 0.88 |
| webp-24mp.webp | 64.9 | 67.6 |  | 66.9 | 55.1 | 93.7 | 96.8 |  | 66.7 | 66.1 | 99.5 | 101 |
| tiff-lzw-24mp.tif | 55.5! | 54.3! |  | 28.7 | 29.5 | 78.4 | 76.6 |  | 37.7~ | 40.6 | 38.1 | 42.0 |
| jxr-12mp.jxr | 23.6 | 24.4 |  | 3.61 | 3.55 | 227 | 235 |  | 197 | 199 | 224 | 233 |
| heic-12mp.heic | glyph | 137 | 114 | glyph | 107 | 179 | 179 | 151 | 114 | 119 | 115 | 120 |
| avif-12mp.avif | glyph | 89.5 | 57.8 | glyph | 53.8 | 127 | 128 | 103 | 64.2 | 63.8 | 64.1 | 64.0 |
| jxl-12mp.jxl | glyph | 88.4 | 64.5 | glyph | 55.1 | 130 | 129 | 100 | 65.4~ | 66.2 | 66.4 | 68.2 |
| jp2-4k.jp2 | glyph | 37.2 | 10.1 | glyph | 5.49 | 248 | 245 | 217 | 148 | 144 | 139 | 137 |
| j2k-4k.j2k | glyph | 37.3 | 10.2 | glyph | 5.59 | 248 | 247 | 220 | 149 | 145 | 139 | 137 |
| raw-dng-1600-preview-24mp.dng | glyph | 24.4 |  | glyph | 2.79 | 38.7 | 40.6 |  | 10.5 | 10.6 | 10.8 | 10.7 |
| raw-cr3-1620-preview-24mp.cr3 | glyph | 23.5 |  | glyph | 2.90 | 40.0 | 40.1 |  | 12.0 | 11.5 | 12.1 | 11.8 |
| raw-nef-fullsize-preview-24mp.nef | glyph | 62.4 |  | glyph | 33.7 | 108 | 109 |  | 76.7 | 78.6 | 165 | 169 |
| raw-arw-1620-preview-24mp.arw | glyph | 22.4 |  | glyph | 2.73 | 39.7 | 39.7 |  | 11.9 | 11.4 | 11.7 | 11.7 |
| raw-raf-fullsize-preview-24mp.raf | glyph | 57.0 |  | glyph | 32.4 | 92.3 | 92.6 |  | 64.0 | 63.8 | 118 | 118 |
| psd-rle-4k.psd | glyph | 88.3 |  | glyph | 62.4 | 98.1 | 99.1 |  | 62.8~ | 61.5~ | 64.8 | 64.7 |
| psd-zip-pred-4k.psd | glyph | 151 |  | glyph | 122 | 158 | 161 |  | 99.3 | 99.3 | 101 | 99.0 |
| exr-zip-half-4k.exr | glyph | 72.4 |  | glyph | 40.3 | 78.6 | 84.4 |  | 42.9 | 42.5 | 40.0 | 41.7 |
| hdr-4k.hdr | glyph | 144 |  | glyph | 63.4 | 154 | 157! |  | 66.5~ | 68.8~ | 70.4 | 68.6 |
| pfm-4k.pfm | glyph | 82.1 |  | glyph | 47.2 | 87.8 | 90.9 |  | 47.6 | 49.0 | 48.1 | 48.2 |
| svg-5000.svg | glyph | 61.6 | 34.9 | glyph | 26.9 | 345 | 346 | 292 | 157~ | 146~ | 175 | 150 |
| svgz-5000.svgz | glyph | 62.1 | 36.2 | glyph | 28.6 | 348 | 345 | 284 | 155 | 144 | 149 | 147 |
| svg-simple.svg | glyph | 34.4 | 10.2 | glyph | 4.70 | 102 | 101 | 28.4 | 3.93 | 4.01 | 3.58 | 3.61 |
| svg-embedded-png.svg | glyph | 127 | 83.2 | glyph | 70.2 | 180 | 252 | 212 | 62.9 | 132 | 61.4 | 61.4 |
| tga-4k.tga | glyph | 44.2 |  | glyph | 25.1 | 58.5 | 55.6 |  | 27.7 | 27.9 | 27.9 | 27.6 |
| ppm-4k.ppm | glyph | 46.4 |  | glyph | 25.6 | 59.6 | 57.9 |  | 25.2 | 26.2 | 24.6 | 26.0 |
| qoi-4k.qoi | glyph | 53.4 |  | glyph | 34.0 | 65.6 | 65.4 |  | 38.7 | 38.0 | 38.6 | 37.1 |
| pcx-4k.pcx | glyph | 70.3 |  | glyph | 45.8 | 82.6 | 81.9! |  | 45.8 | 45.7 | 42.6 | 45.0 |
| sgi-4k.sgi | glyph | 47.3 |  | glyph | 26.2 | 59.7 | 59.6 |  | 28.8 | 29.9 | 28.0 | 29.4 |
| dds-dxt1-4k.dds | glyph | 30.2 |  | glyph | 7.23 | 61.6 | 61.0 |  | 18.7 | 18.6 | 11.7 | 11.7 |
| ktx1-rgba8-4k.ktx | glyph | 47.6 |  | glyph | 20.2 | 56.1 | 57.0 |  | 23.8 | 24.9 | 24.7 | 24.4 |
| fits-int16-2k.fits | glyph | 49.0 |  | glyph | 23.6 | 51.8 | 51.2 |  | 25.8 | 25.4 | 25.3 | 24.9 |
| ora-4k.ora | glyph | 74.5 |  | glyph | 51.7 | 84.6 | 88.3 |  | 55.5 | 54.8 | 54.7 | 55.4 |

## Every fixture

| Fixture | Thumb cold ms before | after | after + preload | Thumb warm ms before | after | View first cold ms before | after | after + preload | View first warm ms before | after | View full warm ms before | after |
|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| apng-720p.png | 21.2 | 24.5 |  | 6.12 | 6.11 | 44.2 | 43.4 |  | 9.58 | 9.70 | 9.79 | 9.76 |
| avif-12mp.avif | glyph | 89.5 | 57.8 | glyph | 53.8 | 127 | 128 | 103 | 64.2 | 63.8 | 64.1 | 64.0 |
| avif-4k.avif | glyph | 73.3 | 42.2 | glyph | 38.1 | 113 | 113 | 85.1 | 48.6 | 48.6 | 47.5 | 47.2 |
| avif-alpha-4k.avif | glyph | 94.6 |  | glyph | 62.1 | 137 | 145 | 112 | 72.2 | 73.7 | 70.3 | 70.7 |
| bmp-24mp.bmp | 22.7 | 25.4 |  | 7.10 | 7.23 | 50.4 | 52.9 |  | 17.7 | 18.0 | 17.8 | 18.4 |
| bmp-32alpha-4k.bmp | 20.1 | 22.3 |  | 4.31 | 4.53 | 41.9 | 42.0 |  | 7.88 | 7.94 | 7.91 | 7.98 |
| bmp-4k.bmp | 19.5 | 22.1 |  | 3.86 | 4.16 | 40.4 | 41.6 |  | 7.38 | 7.24 | 7.15 | 7.18 |
| bmp-8bit-4k.bmp | 19.9 | 21.8 |  | 3.51 | 3.66 | 40.0 | 39.7 |  | 6.51 | 6.65 | 6.72 | 6.63 |
| cur-256.cur | glyph (16.2 decoder only) | 17.8 |  | glyph (1.11 decoder only) | 1.02 | 40.2 | 39.7 |  | 1.06 | 1.04 | 0.87 | 0.88 |
| dds-dxt1-4k.dds | glyph | 30.2 |  | glyph | 7.23 | 61.6 | 61.0 |  | 18.7 | 18.6 | 11.7 | 11.7 |
| dds-dxt1-mips-4k.dds | glyph | 29.2 |  | glyph | 7.23 | 61.2 | 68.4 |  | 19.7 | 19.9 | 12.6 | 12.3 |
| dds-dxt5-4k.dds | glyph | 33.7 |  | glyph | 10.0 | 72.4 | 71.1 |  | 27.0 | 25.9 | 17.0 | 17.1 |
| dds-rgba-4k.dds | glyph | 40.7 |  | glyph | 18.2 | 48.7 | 49.6 |  | 21.2 | 21.9 | 20.4 | 21.3 |
| exr-none-float-4k.exr | glyph | 68.1 |  | glyph | 36.0 | 76.7 | 76.3 |  | 39.8~ | 45.5~ | 37.8 | 38.2 |
| exr-rle-half-4k.exr | glyph | 80.0 |  | glyph | 46.6 | 86.8 | 89.5 |  | 50.1~ | 41.4~ | 42.6 | 40.9 |
| exr-zip-float-4k.exr | glyph | 102 |  | glyph | 71.1 | 111 | 111 |  | 73.0 | 71.0 | 71.4 | 71.8 |
| exr-zip-half-4k.exr | glyph | 72.4 |  | glyph | 40.3 | 78.6 | 84.4 |  | 42.9 | 42.5 | 40.0 | 41.7 |
| exr-zips-half-4k.exr | glyph | 78.1 |  | glyph | 39.7 | 88.5 | 91.3 |  | 44.5 | 41.3 | 44.1 | 41.0 |
| fits-int16-2k.fits | glyph | 49.0 |  | glyph | 23.6 | 51.8 | 51.2 |  | 25.8 | 25.4 | 25.3 | 24.9 |
| gif-4k.gif | 32.1 | 32.6 |  | 16.2 | 18.2~ | 58.5 | 59.0 |  | 24.8 | 24.2 | 24.5 | 24.1 |
| gif-anim-720p.gif | 17.9 | 19.1 |  | 2.29 | 2.25 | 38.1 | 36.1 |  | 2.50 | 2.32 | 2.32 | 2.20 |
| hdr-4k.hdr | glyph | 144 |  | glyph | 63.4 | 154 | 157! |  | 66.5~ | 68.8~ | 70.4 | 68.6 |
| heic-12mp.heic | glyph | 137 | 114 | glyph | 107 | 179 | 179 | 151 | 114 | 119 | 115 | 120 |
| heic-4k.heic | glyph | 110 | 82.1 | glyph | 77.3 | 152 | 152 | 124 | 90.9 | 87.0 | 88.4 | 85.6 |
| ico-256.ico | glyph (16.3 decoder only) | 17.2 |  | glyph (1.00 decoder only) | 1.01 | 40.9 | 38.3 |  | 1.06 | 1.10 | 0.93 | 0.90 |
| j2k-4k.j2k | glyph | 37.3 | 10.2 | glyph | 5.59 | 248 | 247 | 220 | 149 | 145 | 139 | 137 |
| jp2-4k.jp2 | glyph | 37.2 | 10.1 | glyph | 5.49 | 248 | 245 | 217 | 148 | 144 | 139 | 137 |
| jp2-lossless-4k.jp2 | glyph | 43.7 |  | glyph | 7.85 | 322 | 312 | 293 | 221 | 212 | 207 | 203 |
| jpeg-444-4k.jpg | 28.0 | 31.0 |  | 17.1 | 17.1 | 66.1 | 66.9 |  | 33.9 | 33.5 | 21.2 | 21.2 |
| jpeg-base-12mp.jpg | 23.7 | 26.7 |  | 12.9 | 12.8 | 52.4 | 53.0 |  | 19.8 | 19.6 | 17.9 | 17.3 |
| jpeg-base-24mp.jpg | 36.3 | 39.3 |  | 25.9 | 25.0 | 73.2 | 72.3 |  | 39.1 | 39.3 | 35.0 | 34.4 |
| jpeg-camera-mpf-24mp.jpg | 13.1 | 14.4 |  | 2.13 | 2.74 | 33.3 | 34.2 |  | 7.43 | 7.06 | 58.3 | 51.9 |
| jpeg-cmyk-4k.jpg | 51.8 | 53.5 |  | 30.9 | 31.2 | 435! | 446 |  | 403 | 410 | 399 | 404 |
| jpeg-exif-thumb-24mp.jpg | 41.1 | 43.6 |  | 30.9 | 30.0 | 72.3 | 72.7 |  | 40.7 | 40.0 | 54.2 | 52.3 |
| jpeg-gray-4k.jpg | 19.8 | 20.8 |  | 8.82 | 7.84 | 53.4 | 57.1 |  | 21.4 | 21.0 | 12.2 | 11.9 |
| jpeg-icc-4k.jpg | 19.9 | 21.5 |  | 8.81 | 9.09 | 54.3 | 57.4! |  | 22.9 | 22.8 | 13.3 | 13.0 |
| jpeg-prog-12mp.jpg | 20.0 | 21.2 |  | 8.04 | 8.34 | 81.1 | 85.1 |  | 60.1~ | 53.4 | 68.5 | 58.7 |
| jpeg-prog-24mp.jpg | 28.1 | 30.0 |  | 16.5 | 16.2 | 137 | 135 |  | 111 | 111 | 124 | 123 |
| jxl-12mp.jxl | glyph | 88.4 | 64.5 | glyph | 55.1 | 130 | 129 | 100 | 65.4~ | 66.2 | 66.4 | 68.2 |
| jxl-4k.jxl | glyph | 75.2 | 47.7 | glyph | 42.4 | 116 | 117 | 89.5 | 53.7 | 55.3 | 51.0 | 52.0 |
| jxl-alpha-4k.jxl | glyph | 108 |  | glyph | 76.7 | 153 | 158 | 135 | 88.7 | 89.0 | 86.7 | 85.9 |
| jxl-lossless-4k.jxl | glyph | 129 |  | glyph | 95.3 | 172 | 179! | 152 | 106 | 104 | 105 | 100 |
| jxr-12mp.jxr | 23.6 | 24.4 |  | 3.61 | 3.55 | 227 | 235 |  | 197 | 199 | 224 | 233 |
| jxr-4k.jxr | 20.9 | 22.5 |  | 2.63 | 2.36 | 176 | 176 |  | 142 | 144 | 146 | 148 |
| ktx1-rgba8-4k.ktx | glyph | 47.6 |  | glyph | 20.2 | 56.1 | 57.0 |  | 23.8 | 24.9 | 24.7 | 24.4 |
| mpo-12mp.mpo | glyph (29.4 decoder only) | 36.8 |  | glyph (14.3 decoder only) | 13.9 | 149 | 150 |  | 61.8 | 62.5 | 49.6 | 50.6 |
| nifti-256x256x64.nii | glyph | 16.7 |  | glyph | 0.63 | 32.0 | 32.3 |  | 0.59 | 0.59 | 0.41 | 0.40 |
| ora-4k.ora | glyph | 74.5 |  | glyph | 51.7 | 84.6 | 88.3 |  | 55.5 | 54.8 | 54.7 | 55.4 |
| pcx-4k.pcx | glyph | 70.3 |  | glyph | 45.8 | 82.6 | 81.9! |  | 45.8 | 45.7 | 42.6 | 45.0 |
| pfm-4k.pfm | glyph | 82.1 |  | glyph | 47.2 | 87.8 | 90.9 |  | 47.6 | 49.0 | 48.1 | 48.2 |
| png-16bit-4k.png | 80.9 | 82.5 |  | 64.2 | 63.6 | 112 | 114 |  | 76.8 | 79.6 | 77.6 | 78.9 |
| png-24mp.png | 150 | 157 |  | 133 | 132 | 169 | 170 |  | 133 | 135 | 134 | 135 |
| png-4k.png | 66.3 | 69.3 |  | 51.3 | 50.5 | 88.0 | 87.6 |  | 51.4 | 52.3 | 51.7 | 52.5 |
| png-alpha-4k.png | 94.1 | 99.0 |  | 80.4 | 77.4 | 109 | 111 |  | 75.8 | 78.9 | 76.9 | 78.6 |
| png-gray-4k.png | 41.8 | 44.3 |  | 27.3 | 26.5 | 59.5 | 61.0 |  | 26.3 | 26.6 | 26.3 | 27.0 |
| png-interlaced-4k.png | 107 | 113 |  | 95.7 | 91.3 | 102 | 99.7 |  | 67.2 | 67.8 | 67.1 | 67.5 |
| png-palette-4k.png | 29.9 | 32.9! |  | 14.8 | 15.1 | 49.8 | 49.2 |  | 15.7 | 16.0 | 15.8 | 15.8 |
| png-up-filter-4k.png | 72.1 | 76.0 |  | 56.4 | 57.2 | 88.3 | 87.3 |  | 54.6 | 55.3 | 54.7 | 56.1 |
| ppm-4k.ppm | glyph | 46.4 |  | glyph | 25.6 | 59.6 | 57.9 |  | 25.2 | 26.2 | 24.6 | 26.0 |
| psd-16bit-raw-4k.psd | glyph | 91.1 |  | glyph | 65.5 | 101 | 102 |  | 66.8 | 66.6 | 67.0 | 68.3 |
| psd-layers-rle-4k.psd | glyph | 103 |  | glyph | 72.4 | 109 | 111 |  | 69.9 | 68.1 | 70.4 | 72.7 |
| psd-raw-4k.psd | glyph | 88.0 |  | glyph | 62.4 | 94.6 | 94.5 |  | 62.6 | 63.2 | 63.9 | 63.0 |
| psd-rle-4k.psd | glyph | 88.3 |  | glyph | 62.4 | 98.1 | 99.1 |  | 62.8~ | 61.5~ | 64.8 | 64.7 |
| psd-zip-pred-4k.psd | glyph | 151 |  | glyph | 122 | 158 | 161 |  | 99.3 | 99.3 | 101 | 99.0 |
| qoi-4k.qoi | glyph | 53.4 |  | glyph | 34.0 | 65.6 | 65.4 |  | 38.7 | 38.0 | 38.6 | 37.1 |
| raw-arw-1620-preview-24mp.arw | glyph | 22.4 |  | glyph | 2.73 | 39.7 | 39.7 |  | 11.9 | 11.4 | 11.7 | 11.7 |
| raw-cr3-1620-preview-24mp.cr3 | glyph | 23.5 |  | glyph | 2.90 | 40.0 | 40.1 |  | 12.0 | 11.5 | 12.1 | 11.8 |
| raw-dng-1600-preview-24mp.dng | glyph | 24.4 |  | glyph | 2.79 | 38.7 | 40.6 |  | 10.5 | 10.6 | 10.8 | 10.7 |
| raw-nef-fullsize-preview-24mp.nef | glyph | 62.4 |  | glyph | 33.7 | 108 | 109 |  | 76.7 | 78.6 | 165 | 169 |
| raw-raf-fullsize-preview-24mp.raf | glyph | 57.0 |  | glyph | 32.4 | 92.3 | 92.6 |  | 64.0 | 63.8 | 118 | 118 |
| sgi-4k.sgi | glyph | 47.3 |  | glyph | 26.2 | 59.7 | 59.6 |  | 28.8 | 29.9 | 28.0 | 29.4 |
| svg-5000.svg | glyph | 61.6 | 34.9 | glyph | 26.9 | 345 | 346 | 292 | 157~ | 146~ | 175 | 150 |
| svg-embedded-png.svg | glyph | 127 | 83.2 | glyph | 70.2 | 180 | 252 | 212 | 62.9 | 132 | 61.4 | 61.4 |
| svg-simple.svg | glyph | 34.4 | 10.2 | glyph | 4.70 | 102 | 101 | 28.4 | 3.93 | 4.01 | 3.58 | 3.61 |
| svgz-5000.svgz | glyph | 62.1 | 36.2 | glyph | 28.6 | 348 | 345 | 284 | 155 | 144 | 149 | 147 |
| tga-4k.tga | glyph | 44.2 |  | glyph | 25.1 | 58.5 | 55.6 |  | 27.7 | 27.9 | 27.9 | 27.6 |
| tga-rle-4k.tga | glyph | 52.9 |  | glyph | 22.4 | 56.0 | 56.0 |  | 24.4~ | 25.2 | 24.7 | 25.3 |
| tiff-16bit-deflate-4k.tif | 41.3 | 45.3 |  | 25.7 | 24.8 | 62.4 | 64.9 |  | 29.4 | 28.6 | 29.2 | 29.9 |
| tiff-bigtiff-deflate-24mp.tif | glyph | glyph |  | glyph | glyph | - | - |  | - | - | - | - |
| tiff-cmyk-4k.tif | 79.9 | 81.4 |  | 77.6 | 70.8 | 114 | 117 |  | 65.5 | 70.1 | 68.0 | 72.9 |
| tiff-deflate-24mp.tif | 44.7 | 52.7 |  | 32.2 | 29.3 | 71.4 | 72.0 |  | 35.8 | 37.7 | 36.4 | 37.3 |
| tiff-deflate-4k.tif | 31.5! | 32.5 |  | 14.3 | 12.5 | 52.3 | 53.1 |  | 16.5 | 17.4 | 15.8 | 17.0 |
| tiff-float32-4k.tif | 43.7 | 47.7 |  | 31.8 | 27.5 | 64.9 | 63.8 |  | 29.2 | 31.8 | 28.6 | 32.9 |
| tiff-gray-4k.tif | 23.1 | 25.6 |  | 6.72 | 6.38 | 45.5 | 44.1 |  | 8.91 | 9.28 | 8.77 | 9.66 |
| tiff-jpeg-4k.tif | 31.3 | 36.2 |  | 17.0 | 15.7 | 53.4 | 52.8 |  | 16.8 | 18.0 | 16.7 | 17.6 |
| tiff-lzw-24mp.tif | 55.5! | 54.3! |  | 28.7 | 29.5 | 78.4 | 76.6 |  | 37.7~ | 40.6 | 38.1 | 42.0 |
| tiff-lzw-4k.tif | 38.8 | 33.1! |  | 14.1 | 12.9 | 54.2 | 55.3 |  | 14.9 | 16.8 | 14.4 | 15.7 |
| tiff-multipage-1080p.tif | 21.9 | 25.2! |  | 5.14 | 5.08 | 53.7 | 54.5! |  | 15.5 | 16.7 | 15.6 | 16.3 |
| tiff-packbits-4k.tif | 21.3 | 24.1 |  | 3.95 | 3.77 | 43.6 | 49.0 |  | 7.04 | 7.22 | 6.71 | 7.09 |
| tiff-pyramid-deflate-24mp.tif | 203 | 209 |  | 219 | 201 | 232 | 246 |  | 194 | 233~ | 191 | 249 |
| tiff-raw-24mp.tif | 26.0 | 28.9 |  | 11.9 | 9.48 | 58.1 | 57.8 |  | 18.8 | 18.8 | 18.7 | 19.0 |
| tiff-raw-4k.tif | 22.3 | 25.2 |  | 5.28 | 4.67 | 46.3! | 47.9 |  | 7.72 | 8.34 | 7.53 | 8.40 |
| tiff-tiled-deflate-24mp.tif | 210 | 204 |  | 216 | 197 | 248 | 251 |  | 192 | 195 | 192 | 198 |
| tiff-tiled-deflate-4k.tif | 89.1 | 85.9 |  | 78.7 | 69.3 | 112 | 108! |  | 69.8 | 67.5 | 67.9 | 67.2 |
| webp-24mp.webp | 64.9 | 67.6 |  | 66.9 | 55.1 | 93.7 | 96.8 |  | 66.7 | 66.1 | 99.5 | 101 |
| webp-4k.webp | 30.7 | 33.7 |  | 24.5 | 20.8 | 65.6 | 66.2 |  | 34.8 | 35.1 | 35.2 | 34.6 |
| webp-alpha-4k.webp | 53.5 | 56.0 |  | 48.0 | 41.7 | 102! | 99.5 |  | 63.6 | 64.7 | 56.7 | 57.8 |
| webp-anim-720p.webp | 23.3 | 25.9 |  | 2.93 | 2.87 | 34.8! | 33.7 |  | 3.60 | 3.73 | 3.44 | 3.47 |
| webp-lossless-4k.webp | 119 | 120 |  | 109 | 112 | 167 | 167 |  | 136 | 138 | 98.6 | 99.2 |
| webp-lossless-alpha-4k.webp | 134 | 136 |  | 124 | 125 | 163 | 166 |  | 125 | 126 | 103 | 105 |

## What changed

- `Services/ThumbnailRoutes.cs`: suffix -> decoder family (JPEG family incl. `.jfif` `.jif` `.mpo`; native bridge; Skia; built-in managed decoders; codec plug-in; SVG; WIC). `MainWindow.CanUseBrowserThumbnailDecoder` now asks it.
- `Services/ThumbnailExtendedDecode.cs`: the reduced-size decodes (managed decoders bounded to the tile, so a RAW reads only the smallest embedded preview that covers it; the plug-ins' reduced decode; WIC; SVG), with the decoder asked for the longest side that gives the tile width (portrait pictures need more).
- `Services/ThumbnailDecode.cs`: routes through the above; the disk cache now also stores straight-alpha results (premultiplied in the copy).
- `Services/ThumbnailScheduler.cs`: the lane pool as a priority queue (visible first, cost class, arrival order, look-ahead last), a per-file timeout (8 s) that gives the lane up, a failure memory per file version, abandoned-decode accounting (fewer lanes while a given-up decode still runs, never below one; a decode that was only slow clears its own failure).
- `MainWindow.Browser.cs`: scheduler instead of the semaphore; look-ahead of two rows below the last prepared tile while the cache is on; plug-in warm-up from the listing. `MainWindow.Navigation.cs`: the viewer's folder index also warms the plug-in. `MainWindow.Startup.cs`: idle SVG warm-up.
- `Glide.Imaging/CodecPreloader.cs`, `CodecWarmSamples.cs`: background resolve of the plug-in (hash, load) plus one decode of a 32 x 16 built-in picture per format family (46 - 1601 bytes, 2.5 KB of source).
- `Glide.Imaging/SvgTileRenderer.cs`, `native/Glide.Native/GlideSvg.cpp`: Direct2D SVG tiles (WARP device, premultiplied BGRA, the document's own size as the viewport so a viewBox-only document lays out correctly); the eligibility scan is `SvgTileRenderer.IsDirect2DSafe`. `GLIDE_SVG_D2D=0` turns the Direct2D route off.
- `Glide.Imaging/SvgDecoder.cs`: the preview scaling fix and the warm-up parser.

## Tests

`ThumbnailEveryFormatTests` (route and a tile that shows the picture for 18 formats, the codec plug-in formats through the provider, the glyph fallback for 23 broken files, disk cache store and hit for 10 formats, premultiply, longest side),
`ThumbnailSchedulerTests` (cost model, queue order, cancel, timeout, failure memory, a slow decode is forgiven, lane accounting, look-ahead fills the cache), `CodecPreloaderTests`,
`SvgTileRendererTests` (eligibility, Direct2D against Svg.Skia, fallbacks, security, preview scaling, warm-up), `ViewerFirstAndFullViewEveryFormatTests` (the viewer opens every one of those fixtures, first view and full view, with the right picture),
`ExplorerEveryFormatThumbnailTests` (the real Explorer tab: thumbnails for PPM, TGA, SVG; the glyph for a broken PSD and HEIC).

## Reproduce

```
python tools/perf/generate_format_fixtures.py %TEMP%\glide-format-audit\data
dotnet build tools\perf\Glide.PerfBench\Glide.PerfBench.csproj -c Release -p:ArtifactsPath=%TEMP%\glide-format-audit\art
dotnet build tools\perf\Glide.FormatAudit\Glide.FormatAudit.csproj -c Release -p:ArtifactsPath=%TEMP%\glide-format-audit\art
powershell -NoProfile -ExecutionPolicy Bypass -File tools\perf\run-format-audit.ps1 -Data %TEMP%\glide-format-audit\data -Out %TEMP%\glide-format-audit\results -Bin %TEMP%\glide-format-audit\art\bin -Phases thumb-warm,thumb-cold,thumb-cold-preload,view-cold,view-cold-preload,view-warm
python tools/perf/compare-format-thumbs.py <results before> <results after> [--all]
Glide.FormatAudit queue --data <corpus> --out <dir> --screen 40 --runs 7 [--seed N]
```

Run the "before" with a build of the base commit (its harness lacks `--preload` and `queue`; those columns are blank there).

## Noise and caveats

- Another session used the machine throughout (load 2 - 80 %). Cells that started above 35 % CPU carry `!`; the worst were re-run when quiet (the base view-warm phase, 32 + 68 files; the thumbnail and cold cells of about 70 files); a few stay flagged (`tiff-lzw-24mp` cold thumbnails, `hdr` and `pcx` view-cold). The queue measurements ran at 31 - 63 % load; they compare two variants interleaved in one process, so the ratios hold, the absolute times do not.
- Cold numbers include this harness's non-ReadyToRun JIT; the shipped app is ReadyToRun, so cold gaps are an upper bound. The "after + preload" columns model the case the preload is for: the user browses a folder (the listing warms the plug-in), then opens a file or scrolls to a tile. Without a preload the first tile or open of a plug-in format still pays the provider hash, load and first decode.
- `.jfif` / `.jif` / `.jpe` use the same route as `.mpo` and `.jpg` (libjpeg-turbo thumbnail by content); the audit has an MPO fixture only.

## Not changed (other work, or not worth it)

TIFF tiled / BigTIFF, the HEIC embedded thumbnail, JPEG XL DC decode and CMYK JPEG are done in a separate worktree (native decoders). The viewer's first view of a path-heavy SVG still goes through Svg.Skia (146 ms warm, parse-bound): Direct2D would make the first frame differ from the full render. The PSD, EXR, HDR and progressive JPEG viewer paths are untouched (audit items 7, 8, 10).
