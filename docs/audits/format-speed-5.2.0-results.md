# Format speed work, 5.2.0: what was built and what it measured

Implements audit items #2 (tiled / pyramid / BigTIFF), #3 (HEIC thumbnail item, JPEG XL DC image) and #6 (CMYK JPEG and TIFF) of
`docs/audits/format-speed-audit-5.2.0.md`. Same fixtures (`tools/perf/generate_format_fixtures.py`), same harness (`tools/perf/Glide.FormatAudit`,
`Glide.PerfBench`, `run-format-audit.ps1`), same PC (Ryzen 7 9700X, 8 cores / 16 threads, Windows 11 LTSC 26100, .NET 10). Headless; no Glide window was
started and nothing installed was touched. **Before** = the tree at `fa5dfb2` with its own `Glide.Native.dll` / `glidecodecs.dll` (built from that tree);
**after** = this branch. Both sides were built into separate output folders and measured by the same scripts, phase by phase, one side after the other.

## Method

- Warm = median of 7 runs in one process after an untimed warm-up; cold = median of 5 fresh processes (first decode in the process, so provider load,
  native DLL load and first-call costs are in). Viewer = the app's own `ImageLoadCoordinator` + `CodecProviderDecoderBackend`, Maximum quality policy,
  2560 x 1369 viewport (`view` / `view-one`): "first" is the first frame, "settled" the first frame plus the Fit refinement, "full" the full-resolution decode.
- Thumbnail = the Explorer tab's decode at 236 px (`Glide.PerfBench thumb` / `thumb-one`, the real `ThumbnailDecode` route) for TIFF and JPEG. HEIC / AVIF /
  JPEG XL / JPEG 2000 are not listed by the Explorer tab yet (another change adds them), so their thumbnail columns are the codec plug-in's own preview call at
  236 px (`Glide.FormatAudit provider` / `provider-one`: `ICodecSurfaceProvider.DecodePreviewSurfaceAsync`, which is exactly the call that pipeline makes).
- The PC was shared with other agents that build and test. Machine load was sampled at the start and end of every phase; a cell is marked `*` when its
  samples spread by more than 40 % of the median (and 3 ms) or the load was above 35 % when it started. The key rows were measured again (rounds r2 / r3) in
  quieter minutes and those values replace the first round's; the rest of the controls are round r1 (the CMYK JPEG change after r1 only touches CMYK).
- `tools/perf/compare-format-audit.py before-results after-results` prints the table below from two result folders.

## Result (ms, before -> after)

| File | Thumbnail cold | Thumbnail warm | View first cold | View first warm | View settled | View full |
|---|---|---|---|---|---|---|
| **TIFF tiled deflate 24 MP** | 201 -> 40.7 (4.9x) | 191 -> 28.6 (6.7x) | 264 -> 63.7 (4.1x) * | 191 -> 29.7 (6.4x) | 191 -> 29.7 (6.4x) | 192 -> 30.1 (6.4x) |
| TIFF tiled deflate 4K | 86.1 -> 21.6 (4.0x) | 65.7 -> 10.8 (6.1x) | 123 -> 43.7 (2.8x) * | 66.6 -> 11.8 (5.6x) | 66.6 -> 11.8 (5.6x) | 66.2 -> 11.9 (5.6x) |
| **TIFF pyramid (SubIFD) 24 MP** | 200 -> 14.6 (13.7x) | 187 -> 3.74 (49.8x) | 270 -> 41.8 (6.4x) * | 206 -> 12.7 (16.3x) * | 206 -> 15.2 (13.6x) | 205 -> 29.6 (6.9x) |
| **TIFF BigTIFF 24 MP** (could not be opened at all) | fails -> 39.1 | fails -> 30.3 | fails -> 64.3 | fails -> 30.1 | fails -> 30.1 | fails -> 30.2 |
| TIFF CMYK 4K | 85.7 -> 39.3 (2.2x) | 62.2 -> 22.2 (2.8x) | 118 -> 62.2 (1.9x) * | 71.4 -> 17.8 (4.0x) | 71.4 -> 17.8 (4.0x) | 70.9 -> 17.7 (4.0x) |
| **JPEG CMYK 4K** | 52.1 -> 51.1 (same) | 31.5 -> 32.4 (same) | 447 -> 91.0 (4.9x) * | 407 -> 51.3 (7.9x) | 816 -> 51.3 (15.9x) | 400 -> 49.3 (8.1x) |
| **HEIC 12 MP with a 320 px thumbnail item** | 133 -> 27.7 (4.8x) | 106 -> 2.70 (39.1x) | 187 -> 189 (same) * | 117 -> 108 (same) | 237 -> 228 (same) | 116 -> 109 (same) |
| HEIC 12 MP without a thumbnail item | 132 -> 125 (same) | 122 -> 101 (1.2x) * | 179 -> 199 (same) * | 116 -> 108 (same) | 236 -> 230 (same) | 116 -> 109 (same) |
| **JPEG XL lossy 12 MP** | 83.2 -> 57.9 (1.4x) | 57.4 -> 31.6 (1.8x) | 143 -> 139 (same) * | 69.5 -> 59.5 (1.2x) | 142 -> 131 (same) | 69.6 -> 59.6 (1.2x) |
| JPEG XL lossy 4K | 68.3 -> 52.2 (1.3x) | 42.6 -> 25.2 (1.7x) | 130 -> 124 (same) * | 55.7 -> 49.2 (1.1x) | 111 -> 104 (same) | 53.2 -> 46.9 (1.1x) |
| JPEG XL lossless 4K (no DC step: unchanged by design) | 118 -> 115 (same) | 96.0 -> 93.1 (same) | 177 -> 170 (same) | 107 -> 96.5 (1.1x) | 222 -> 199 (1.1x) | 108 -> 95.5 (1.1x) |
| JPEG XL with alpha 4K (unchanged by design) | 104 -> 92.5 (1.1x) | 80.3 -> 70.7 (1.1x) * | 151 -> 143 (same) | 85.5 -> 75.2 (1.1x) | 171 -> 145 (1.2x) | 83.8 -> 73.5 (1.1x) |

Controls (nothing should move; the full table is the output of `compare-format-audit.py`): TIFF deflate / LZW / raw / PackBits / gray / 16-bit / float / JPEG-in-TIFF /
multi-page strips, baseline / progressive / gray JPEG, AVIF, JPEG 2000 are all within noise (about +/- 10 %; a few viewer rows read 10 - 20 % lower after, on a PC whose load varied from 2 % to 96 %,
which is run-to-run noise, not an effect: those files do not take the new code).

### Reading the table honestly

- **Tiled TIFF, pyramid TIFF, BigTIFF, CMYK TIFF** reach or pass the audit's targets (tiled about 6x, pyramid thumbnails 50x warm, BigTIFF opens). Cold cells are smaller
  gains than warm ones because process start, DLL load and first-call costs are the same on both sides.
- **CMYK JPEG**: the first view is 7.9x faster warm (407 -> 51 ms; the audit's target was about 60 ms) and 4.9x cold; settled and full follow. The Explorer **thumbnail did not get faster**:
  it is bound by the entropy decode of the 4.7 MB file (about 30 ms, the same as Skia's), so the gain there is only that CMYK now takes the native path like every other JPEG.
- **HEIC**: the thumbnail item gives 39x warm / 4.8x cold at tile size, but only for files that carry one (iPhone files do; the audit's pillow-heif file does not, so `heic-12mp` is unchanged).
  The viewer's first view is **unchanged**: a 2560 x 1369 first frame of a 4000 x 3000 picture cannot be served by a 320 px item and HEVC has no reduced-size decode. It is used when the
  requested preview is at most the thumbnail's size (small windows, Overlay / picture-in-picture, prefetch of small previews).
- **JPEG XL**: the DC route measured 1.7 - 1.9x warm (57 -> 32 ms for 12 MP), not the 5x the audit estimated. libjxl renders the DC image into a full-size buffer on every flush; the work left is
  that render (parallel) plus the DC decode. It applies to lossy (VarDCT) pictures without alpha for requests of one eighth of the size or less. The first view at the viewer's 2560 px box is
  unchanged apart from a 1.1 - 1.2x gain from converting the decoded frame on all cores.
- Not everything can be sped up: the viewer's first views of HEIC, JPEG XL and AVIF at the full viewport stay decode-bound.

## What changed

| Area | Change |
|---|---|
| `native/Glide.Native/GlideTiff.cpp` (new, about 1200 lines) | Own TIFF reader: classic + BigTIFF, both byte orders, strips and tiles, none / LZW / Deflate / PackBits, predictor 2, 8 / 16 bit gray, gray + alpha, RGB, RGBA, 8 bit CMYK; own inflate (10 bit root table + canonical fallback, 64 bit bit buffer); unit-parallel decode (work counter over tiles / strip groups, row-parallel inside a single huge strip); box reduction by a power of two that divides the tile / strip so units stay independent; pyramid level choice (SubIFDs and reduced pages); EXIF orientation. Declines everything else to WIC. |
| `GlideNative.cpp` | Hooks: `GlideDecodeImageIntoW` (tiled / BigTIFF / CMYK full frames), `DecodeImageBoundedW` (first view, prefetch, startup), `GlideDecodeThumbnailW` (Explorer), `GlideProbeImageW` (BigTIFF size); new exports `GlideTiffPlanW`, `GlideTiffDecodeOwnW`, `GlideCmykConvertW`. |
| `GlideCmyk.cpp` (new), `GlideJpeg.cpp` | CMYK / YCCK JPEG through libjpeg-turbo (DCT scaled, banded, multi-threaded) with the colours of Windows' own ICC-managed converter, sampled once into a 17^4 lattice and interpolated (packed-lane tetrahedral); mean difference to WIC 0.23 levels, largest 8. Also used for CMYK TIFF. |
| `native/Glide.Codecs/GlideCodecs.cpp` | HEIC / AVIF thumbnail item for covered previews; JPEG XL DC image for previews of 1/8 or less; multi-core RGB -> BGRA conversion. No ABI change. |
| `IImageDecoderBackend.cs`, `ImageLoadCoordinator.cs`, `ImageHeaderProbe.cs`, `StartupImagePreloader.cs` | Viewer: a pyramid TIFF is not "full first frame" any more and its first view is the covering level (route `tiff-pyramid-level`); BigTIFF dimensions; startup preload of a TIFF goes through the bounded entry so the native reader is used. Test hooks. |
| Not touched | `ThumbnailDecode.cs`, the Explorer queue (the existing `SupportsNativeThumbnail` TIFF route and `TryDecodeJpegThumbnail` already reach the new code). |

### For the thumbnails-for-all-formats work

HEIC / AVIF / JPEG XL / JPEG 2000: call `ICodecSurfaceProvider.DecodePreviewSurfaceAsync(path, longestSide)` with the tile size; the provider picks the thumbnail item or the DC image itself and always returns
a frame whose longest side is `longestSide` (or the picture's if smaller). TIFF: `NativeImageDecoder.TryDecodeThumbnail(path, width)` (already routed) now returns BigTIFF, pyramid and tiled files fast and
CMYK TIFF correctly; CMYK JPEG goes through `TryDecodeJpegThumbnail`.

## Size of the setup

No library was added. Own code only: `Glide.Native.dll` 941 056 -> 993 792 bytes (+52 736), `glidecodecs.dll` 5 693 440 -> 5 709 312 bytes (+15 872). Together +68 608 bytes raw, about +27 KB LZMA
compressed (the installer's compressor). Test fixtures (about 4 MB in `tests/`) are not shipped.

## Tests

`NativeTiffReaderTests` (pixel identity against WIC for 14 tiled / strip variants in both byte orders, 8 and 16 bit, predictor, orientation, alpha; CMYK tolerance; BigTIFF against classic twins and its
header probe; pyramid level choice and content; power-of-two reductions; viewer first view of pyramid / BigTIFF / plain TIFF; damaged files and a 60-file random corruption run), `JpegCmykTests` (five CMYK / YCCK
files against WIC, first views, thumbnails), `CodecPreviewShortcutTests` (HEIC thumbnail item chosen exactly when it covers the request, JPEG XL DC route and its fallbacks). The test fixtures
(`tests/Glide.Core.Tests/Fixtures/Tiff`, the new CMYK JPEGs, `heic-thumb.heic`, `jxl-dc.jxl`) come from the committed generator scripts. Full suites: Core 3134, Explorer 321, Input 21, all passing (one
Explorer UI timing test failed once under load and passed on its own).
