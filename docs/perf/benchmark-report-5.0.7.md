# 5.0.7 decode benchmark: report

**Bottom line**

- One change is integrated into the native WIC bridge: parallel-band decoding for TIFF and BMP, plus EXIF rotation applied straight into the bitmap. Through Glide's own open path, TIFF opens in **0.18–0.38x** the time, BMP in **0.59–0.71x**, and a rotated 24 MP JPEG at full resolution in **0.69x**. JPEG, PNG and the other formats are unchanged.
- **No engine was switched.** The alternative-engine numbers below are for the user's decision.

Run on 2026-11-17 against the 5.0.7 source snapshot.

- **Machine:** AMD Ryzen 7 9700X (8 cores / 16 threads), 61.6 GB RAM, Windows 11 (26100), .NET 10.0.14. The PC was not in use.
- **Harness:** `tools/bench-507` (method in `docs/perf/benchmark-plan-5.0.7.md`).
  - Serial, one case at a time, one warm-up, then 5 repetitions (3 at ≥ 90 MP).
  - Medians with p95 (nearest rank: with 5 samples p95 is the slowest run).
  - Idle gate before every case: system CPU ≤ 15 % for 750 ms. No case timed out (0 NOISY cases). Recorded busy % before a case was mostly 1–6 % (max 13 %).
  - Viewport 2560x1369, factory-default policy (Maximum quality, 2400 px first-frame cap).
- **Raw data:** `<bench-results>\`. Each run folder holds `raw.csv`, `summary.csv`, `summary.md` and `run-info.txt`. Comparison CSVs: `5.0.7-targeted-comparison.csv` and `5.0.7-final-comparison.csv`.

## Scope and confidence limits (reduced round, as agreed)

About 30 minutes of measuring in total, against a 1-hour cap:

| Run | Time |
|---|---|
| Quick tier (24 MP synthetic + real corpus, at most 2 files per format) | ~21 min |
| Targeted re-run (TIFF, BMP, EXIF JPEG) | ~4 min |
| Final serial comparison (TIFF, BMP, JPEG, PNG at 12–100 MP) | ~5 min |

The plan's full tier (cold samples, 7 repetitions, 50/100 MP AVIF/HEIC/JXL, libvips "all", CLI decoders) was **not** run.

What follows from that:

- **Repetitions:** n = 5 (3 at 100 MP). Differences under about ±5 % are treated as noise. Every claimed win is at least 25 %, and the integrated one was reproduced in three separate runs: quick, targeted and final.
- **Cache state:** warm OS file cache and warm process only. No cold-process or cold-disk numbers.
- **Corpus:** synthetic files are deterministic ("detail" content for lossy formats). The real corpus is 2 files per format. The converted real lossy files are low-detail, so lossy-format speed claims rest on the synthetic set.
- **Missing Windows extensions:** this PC has no Store WebP, HEIF/HEVC, AV1, JPEG XL or Raw Image extensions. Those `wic` rows show errors; this is expected and not a harness fault.
- **Correctness check:** the tuned bridge was checked against the current one with the harness's 4096-pixel sampled checksum (identical on every file). The new unit tests then compare whole frames pixel by pixel.

## 1. Integrated: native bridge changes (`native/Glide.Native/GlideNative.cpp`)

| Trick | What changed | Result |
|---|---|---|
| **TIFF/BMP parallel bands** | For TIFF and BMP of 4 MP or more, `GlideDecodeImageIntoW` splits the frame into up to 8 horizontal bands (at least 256 rows each). Each band uses its own WIC decoder and `CopyPixels(rect)` into the same surface. If any band fails, the whole frame is decoded serially | **Kept.** Big win, identical pixels |
| **EXIF orientation into the surface** | An oriented JPEG used to return status 2 and be decoded again through the buffered path: decode, then a scalar per-pixel rotate into a new buffer, then a managed copy. Now the bridge decodes once and rotates straight into the bitmap. The rotate is cache-blocked (64x64 tiles) and runs on up to 8 threads at 4 MP or more. Orientations 2–8 are handled | **Kept.** 0.69–0.72x full decode |
| SSSE3 24→32 bpp expansion (JPEG scaled preview) | Vector shuffle instead of the scalar loop | **Dropped.** 0.96–1.00x, within noise |
| `IWICBitmapFlipRotator` for orientation | Rotate inside WIC | **Dropped.** Pathological: over 3 minutes for one 24 MP Rotate90 (WIC re-decodes per column). Replaced by the blocked rotate above |

### Before / after (final serial run, Glide production path `ImageLoadCoordinator`, synthetic)

**Time to first pixel** is `LoadForegroundAsync`, decode-ready. TIFF and BMP use the full-first route, so their first frame is the full frame. **Full** is `LoadFullForegroundAsync`. Cells are median ms (p95).

| File | TTFP before | TTFP after | Ratio | Full before | Full after | Ratio | Peak MB before → after |
|---|---:|---:|---:|---:|---:|---:|---|
| TIFF LZW 12 MP | 89.4 (90.1) | 19.6 (20.3) | **0.22** | 87.9 (91.0) | 20.6 (20.9) | **0.23** | 47 → 50 |
| TIFF LZW 24 MP | 179.7 (181.2) | 38.4 (41.9) | **0.21** | 182.4 (189.7) | 39.1 (41.6) | **0.21** | 93 → 97 |
| TIFF LZW 50 MP | 360.6 (362.0) | 76.1 (80.5) | **0.21** | 365.2 (380.1) | 82.6 (83.6) | **0.23** | 193 → 199 |
| TIFF LZW 100 MP | 754.5 (754.8) | 156.3 (163.7) | **0.21** | 765.9 (779.4) | 164.3 (165.2) | **0.21** | 386 → 392 |
| TIFF Deflate 24 MP | 141.2 (142.1) | 31.1 (32.5) | **0.22** | 138.0 (139.0) | 32.8 (33.8) | **0.24** | 92 → 95 |
| TIFF Deflate 100 MP | 562.4 (563.2) | 121.9 (124.2) | **0.22** | 570.7 (570.8) | 131.7 (134.6) | **0.23** | 385 → 393 |
| TIFF JPEG-compressed 24 MP | 307.2 (320.8) | 60.2 (61.4) | **0.20** | 321.2 (322.3) | 59.0 (66.5) | **0.18** | 93 → 98 |
| TIFF uncompressed 24 MP | 37.0 (38.2) | 17.6 (18.4) | **0.48** | 38.7 (39.4) | 19.8 (20.5) | **0.51** | 92 → 96 |
| TIFF uncompressed 100 MP | 130.3 (136.4) | 67.0 (68.6) | **0.51** | 152.7 (156.5) | 76.6 (84.8) | **0.50** | 385 → 392 |
| TIFF tiled 256 LZW 24 MP | 514.3 (535.7) | 193.0 (196.0) | **0.38** | 515.9 (525.8) | 190.4 (192.9) | **0.37** | 95 → 114 |
| TIFF tiled 256 LZW 100 MP | 2171.3 (2214.3) | 584.5 (597.1) | **0.27** | 2171.9 (2177.5) | 607.2 (612.2) | **0.28** | 389 → 424 |
| BMP 24-bit 24 MP | 29.8 (36.3) | 17.7 (18.6) | **0.59** | 31.0 (33.2) | 19.7 (21.7) | **0.64** | 93 → 95 |
| BMP 24-bit 100 MP | 100.0 (107.6) | 70.5 (80.9) | **0.71** | 110.6 (121.6) | 78.8 (88.4) | **0.71** | 386 → 390 |
| JPEG EXIF-6 24 MP | 75.4 (78.6) | 76.1 (79.0) | 1.01 | 165.0 (176.8) | 114.0 (115.8) | **0.69** | 184 → 184 |

Real corpus (targeted run, same bridge change):

| File | TTFP | Full |
|---|---|---|
| `conv_0_24mp_lzw.tif` | 234 → 45 ms (0.19x) | 0.20x |
| `conv_0_24mp_deflate.tif` | 0.21x | 0.22x |
| `conv_0_24mp_jpeg.tif` | 0.24x | 0.22x |
| 16-bit `conv_0_12mp_rgb16_deflate.tif` | 236 → 39 ms (0.16x) | 0.17x |
| `conv_0_24mp_packbits.tif` | 0.54x | 0.55x |
| `conv_0_24mp_raw.tif` | 0.48x | 0.49x |
| `conv_0_24mp.bmp` | 0.63x | 0.57x |

The small real TIFFs (< 0.3 MP) and the 8192² JPEGs are unchanged (0.91–1.15x on sub-2 ms decodes; JPEG within ±3 %).

**Regression check:** every JPEG variant at 12, 24, 50 and 100 MP (baseline, progressive, 4:4:4, restart markers) and every PNG variant are within **0.97–1.04x** in the final run, i.e. noise. That is expected, because the bridge only changes the TIFF, BMP and oriented-JPEG paths.

**Cost:**

- Peak memory: +3 to +7 MB for strip TIFFs (the extra decoder instances) and +19 / +35 MB for tiled TIFF at 24 / 100 MP, about 9 %.
- CPU: up to 8 threads for about 0.04–0.6 s while one large TIFF or BMP opens. A file under 4 MP stays on one thread.
- `Glide.Native.dll` grows from 193,024 to 202,240 bytes (+9 KB uncompressed). The installer impact is negligible (`dist-installer\Glide Setup.exe` is 19,156,836 bytes after the change).
- Licence: none (Glide code + OS WIC).
- **Risk: low.**
  - Every band failure falls back to the serial decode.
  - The 512 MP decode cap is still checked before any allocation.
  - The managed side still accepts status 2 from older bridges.

**Tests added or changed** (`tests/Glide.Core.Tests/ImageLoadCoordinatorLifetimeTests.cs`):

- `DirectFullDecode_IsPixelIdenticalToTheBufferedPath` gains 3000x2000 TIFF/BMP and an odd-sized 2999x2003 TIFF, so the multi-band path and an uneven last band are covered.
- `DirectFullDecode_DefersRotatedImagesToTheOrientationAwarePath` is replaced by `DirectFullDecode_OfAnExifOrientedJpeg_IsPixelIdenticalToTheBufferedOrientationPath`, for orientations 2–8 at 4.08 MP (the multi-threaded path). It checks the size swap and whole-frame pixel identity against the buffered path.

## 2. Alternative engines and other tricks (quick tier): for decision, not adopted

These numbers come from the quick-tier runs `5.0.7-quick-A-synthetic` and `5.0.7-quick-C-real`. Ratios are geometric means of engine median / Glide median for that format; below 1 is faster. Cells are synthetic (24 MP) / real.

### JPEG (current: Windows WIC)

| Engine | TTFP | Full | Size added | Licence | Risk |
|---|---|---|---|---|---|
| WIC via bridge only (no coordinator) | 0.97 / 0.97 | 0.94 / 0.94 | 0 | OS | — |
| **libjpeg-turbo 3.2.1, accurate** | **0.86–0.89** baseline, but **3.5x slower** on progressive (no colour-first level trick) / 0.88 | **0.87** (progressive **0.56**) / 0.86 | 1.25 MB DLL, or ~0.5 MB static in `glidecodecs.dll` | IJG + BSD-3 + zlib | Medium: new core-format path. EXIF, CMYK and ICC handling must be re-verified. Progressive previews would need WIC kept for TTFP |
| libjpeg-turbo fast DCT/upsample | 0.86 (same as accurate; no extra gain) | 0.70 | same | same | as above |
| **libjpeg-turbo banded (8 threads)** | — | **0.59** / **0.63** (24 MP baseline 95 → 63 ms; 4:4:4 221 → 163 ms) | same | same | Medium; more CPU per open |
| Skia (already shipped) | 1.30 / 1.25 | 1.31 / 1.68 | 0 | BSD-3 | Slower: reject |
| libvips 8.18.7 | 1.54 / 1.33 | 0.83 / 0.99 | 19 MB | LGPL-2.1+ | Size and licence: reject |

Reading:

- WIC is already competitive on the first frame. Its progressive-JPEG level-2 trick (65 ms vs ~240 ms for every libjpeg-turbo variant) is unique.
- The only large JPEG lever is **full-resolution** decode (zoom to 100 %, refinement, save): libjpeg-turbo with bands is 0.59–0.63x, and progressive with plain libjpeg-turbo is 0.54–0.56x.
- Recommendation for the user to decide: *prototype* libjpeg-turbo, banded, **for full-resolution/refinement decodes only**, keeping WIC for the first frame. Estimated cost: +0.5 MB statically linked. Needs a pixel-parity and ICC/CMYK test pass. **Not adopted.**

### TIFF (current: WIC, now parallel bands)

- libvips/libtiff: 0.70x TTFP / 0.67x full on the quick tier, before the bridge change; real files 0.83 / 0.77. Tiled LZW 0.27x.
- After the integrated change Glide is 0.18–0.38x of the old time, i.e. faster than libvips on every strip TIFF. On tiled 24 MP it is about even (Glide 190 ms vs vips 145 ms, quick tier).
- Recommendation: no engine change. Tiled TIFF could gain more from tile-aligned bands; follow-up.

### PNG (current: WIC)

- Skia 0.99–1.10x and vips 1.08–1.13x on synthetic. Real 4096² PNG: Skia 0.83–0.86x and vips 0.87x on TTFP, but the comparison isn't like-for-like (one file is 16-bit; vips refused 16-bit output).
- No clear win at n = 2. Wuffs and spng were not tested (they need a native shim). Recommendation: **no change**; revisit with Wuffs in a later round.

### WebP (current on this PC: Skia, because the WIC extension is not installed)

- Skia via harness 0.99x (same code). vips (libwebp 1.6) 1.05x synthetic, 1.61–2.36x real.
- Recommendation: **no change**. The libwebp 1.6 update inside Skia is not reachable without a SkiaSharp upgrade.

### AVIF / HEIC / JXL / JPEG 2000 (current: `glidecodecs.dll`)

| Format | Glide 24 MP synthetic (TTFP / full) | Alternative |
|---|---|---|
| AVIF (dav1d) | 220 / 226 ms | vips with aom: 4.2–4.4x **slower** |
| HEIC (libde265) | 356 / 367 ms | vips web build has no HEVC |
| JXL d1 | 194 / 203 ms | not measured (djxl/vips-all skipped for time) |
| JPEG 2000 | 155 / 490 ms | not measured (opj skipped) |

- AVIF/HEIC/JXL decode in full and then downscale, so TTFP ≈ full decode.
- Two candidates remain for a later round:
  - a JXL DC-first preview (expected 3–8x TTFP)
  - an `/O2` rebuild of `glidecodecs.dll` (+1–2 MB, 10–40 % expected)
- Recommendation: **no change** this release.

### Camera RAW (current: managed embedded-preview extractor + Skia JPEG)

| Engine | Ratio vs Glide | Size added | Licence | Notes |
|---|---|---|---|---|
| **LibRaw 0.22.3 thumbnail + libjpeg-turbo** | **0.31–0.77x** on the mainstream formats (CR2 0.60, CR3 0.61, NEF 0.70, ARW 0.77, ORF 0.31, RW2 0.48, RAF 0.60 TTFP); full 0.25–0.49x | 1.15 MB + VC++ runtime (needs a static rebuild) | LGPL-2.1 / CDDL-1.0 | Medium |

Example: OM-1 80 MP ORF, 49 → 15 ms TTFP. Most of the difference is Glide's managed per-file overhead (container walk, a second byte copy, straight→premultiplied conversion), not the JPEG decode.

**Correctness findings (not speed; worth fixing in Glide whatever the engine choice):**

- **Tiny preview chosen when a larger one exists.**
  - MOS: Glide shows 332x244 where WIC decodes 4008x5344.
  - MEF: Glide shows 192x144; LibRaw finds 992x1328.
- **IIQ: 34–42 ms for a 644x480 thumbnail** (LibRaw 0.3–0.5 ms). This looks like the whole-file embedded-JPEG scan running on 50–80 MB files.
- A 40 MP phone DNG with no usable preview takes 556 ms TTFP and 1.3 s full.
- No picture at all (`Unable to load bitmap`) for: both 3FR, 2 DCR, 2 ERF, 2 FFF, 2 GPR, 2 CHDK CRW, Gitup RAW, ARI, 1 of 2 EXR (`suburban_parking_area_4k.exr`), `conv_0_12mp.dib`, ICNS, RAS, XWD, DPX and PDF.
  - Part of this is expected: there is no Shell handler on this PC, and the harness backend has no Shell preview for some of these suffixes.
  - The RAW, EXR and DIB failures should be checked in the app.

Recommendation for the user to decide: either fix the managed RAW selection and overhead (zero size, low licence risk), or adopt LibRaw for previews (+~1.5 MB statically linked, LGPL obligations). **Not adopted.**

### BMP, GIF and others

- After the integrated change BMP is 0.59–0.71x. Skia is 1.4–2.1x slower.
- GIF: WIC wins (Skia 3x, vips 4–6x slower).
- Managed formats (QOI, HDR, EXR, PSD, ...) were measured for Glide only. They are a reference for later rounds, not compared.

## 3. Summary for decision

| Format | Metric | Current (before) | Integrated tuning | Best alternative (not adopted) | Size delta | Licence | Risk | Recommendation |
|---|---|---:|---|---|---:|---|---|---|
| TIFF LZW 24 / 100 MP | TTFP ms | 180 / 755 | **38 / 156 (0.21x)** | vips 0.70x (before tuning) | +9 KB | — | Low | **Adopted** |
| TIFF tiled LZW 100 MP | TTFP ms | 2171 | **585 (0.27x)** | vips ~0.27x | +9 KB | — | Low | **Adopted**; tile-aligned bands as follow-up |
| BMP 24 / 100 MP | TTFP ms | 30 / 100 | **18 / 71 (0.59–0.71x)** | — | — | — | Low | **Adopted** |
| JPEG EXIF-rotated 24 MP | Full ms | 165 | **114 (0.69x)** | (libjpeg-turbo bands 0.39x, but the harness engine does not rotate: not comparable) | — | — | Low | **Adopted** |
| JPEG 24 MP | TTFP ms | 84 | unchanged | libjpeg-turbo 0.86x (progressive 3.5x slower) | +0.5–1.25 MB | IJG/BSD | Medium | User decision: probably keep WIC for TTFP |
| JPEG 24–100 MP | Full ms | 95 / 393 | unchanged | libjpeg-turbo bands 0.59–0.63x; progressive 0.54x | +0.5–1.25 MB | IJG/BSD | Medium | User decision: prototype for full/refine only |
| PNG | TTFP / full | 136 / 142 (24 MP) | unchanged | none clearly faster | — | — | — | Keep; try Wuffs later |
| WebP | TTFP / full | 321 / 336 (24 MP) | unchanged | none faster | — | — | — | Keep |
| AVIF / HEIC | TTFP | 220 / 356 (24 MP) | unchanged | vips/aom 4x slower | — | — | — | Keep; `/O2` rebuild later |
| JXL / JPEG 2000 | TTFP | 194 / 155 (24 MP) | unchanged | not measured | — | — | — | Next round: DC-first JXL preview, `/O2` |
| RAW (embedded preview) | TTFP | 5–60 ms (format-dependent) | unchanged | LibRaw 0.31–0.77x | +1.15 MB + static rebuild | LGPL/CDDL | Medium | User decision; fix the preview-selection bugs regardless |

## 4. Verification after integration

- **`build.cmd --no-pause`** succeeded: native bridge rebuilt, NativeAOT app published, portable ZIP and installer created. Nothing was published and setup was not run.
  - `Glide.Core.Tests` 2330/2330 passed, including the new band and orientation tests, run with `GLIDE_EXPECT_DIRECT_DECODE=1` so an old bridge could not silently pass.
  - `Glide.Input.Tests` 21/21 passed.
- **`tests/Glide.Explorer.Tests`**: 191/191 passed.
- **Audits:** `python tools/audit-action-registry.py` passed, and `python tools/audit-settings-search.py` passed. No actions or settings changed.

## 5. Correctness fixes (after the benchmark)

Every file below was reproduced and re-checked through Glide's production open path (`tools/bench-507` `one --engine glide --mode preview --box 1920x1080`, a fresh process per file). Times are the harness `ms` (cold process, so roughly 30 ms of each is fixed overhead). The picture content was checked by eye on dumped PNGs. The tests use synthetic fixtures only.

**What changed**
- `RawPreviewDecoder` now treats uncompressed chunky RGB strips (8- or 16-bit, any IFD or SubIFD) as preview candidates next to the JPEGs, and picks the largest.
  - Ties go to JPEG first, then 8-bit RGB, then 16-bit RGB.
  - 16-bit previews are scaled by their own largest sample, because Kodak stores about 10-bit values.
  - A DNG whose only preview is a thumbnail under 1024 px is still left to the platform DNG codec.
- The fallback scan for hidden JPEGs now skips every byte range the directories declare as image data (sensor strips and tiles) and the Phase One raw makernote, so it no longer reads the whole 50–80 MB file.
- New managed readers, with no new engine:
  - packed and file-header DIB (the bundled BMP codec does the pixel work)
  - ICNS (largest PNG member)
  - Sun raster (standard, RLE, colour-mapped)
  - XWD (ZPixmap: TrueColor and colour-mapped)
  - DPX (8/10/12/16-bit)
  - OpenEXR DWAA/DWAB (`ExrDwa.cs`)
- ICNS, RAS, SUN, XWD and DPX were added to the managed thumbnail library and the native provider's managed list.

| File | Before | After | Status |
|---|---|---|---|
| MEF `RAW_MAMIYA_ZD` | 192x144, 83 ms | **1328x992**, 36 ms | Fixed (uncompressed RGB SubIFD preview; LibRaw's 992x1328 rotated by orientation 6) |
| MOS `L_003173`, `RAW_APTUS_75` | 332x244, 52–59 ms | 332x244, 38 ms | Correct as is: the only rendered preview is the 332x244 JPEG (Leaf PKTS `JPEG_preview_data`). The "4008x5344" WIC result was WIC's TIFF codec decoding the raw 16-bit sensor mosaic as greyscale. A bigger picture needs RAW development (needs an engine) |
| IIQ `CF006803`, `cap_22908` | 588x442 / 644x480, 95–96 ms (builtin scan failed, then WIC) | same size, **35 ms** | Fixed. The uncompressed IFD0 preview is read directly, and the scan skips the Phase One raw block. No larger preview exists |
| 3FR (both) | no picture | 218x218 / 290x218 | Fixed (uncompressed thumbnail is the only rendered preview; the main image is lossless-JPEG sensor data) |
| FFF `Job_3583`, `x1d-II-sample-02` | no picture | **1217x913 / 1181x885** | Fixed |
| DCR `86L57188` (DCS 760C) | no picture | **760x504**, 48 ms | Fixed. The 760x504 RGB preview uses Kodak compression 65000, now decoded by a managed port of dcraw's decoder (`Kodak65000Decoder.cs`); most of its blocks are stored raw (absolute values). The camera's preview has a green cast, as does its 380x252 16-bit thumbnail |
| DCR `D4C50031`, `D7465857`, `F3G93631`, `G3X25544` (DCS Pro 14N / SLR) | no picture | **1125x750**, 47–48 ms | Fixed. The previews are Kodak 65000-compressed YCbCr; the declared 1125-pixel width is coded as 1126 columns (2x2 chroma blocks), which LibRaw reports as 1126x750 |
| ERF (3 files) | no picture | **640x424**, 38–39 ms | Fixed. The 640x424 JPEG is in the Epson maker note (tag 0x0280) with its first byte stored as 0xEE instead of 0xFF, so a plain JPEG scan cannot find it; IFD0 only has the 160x120 thumbnail |
| GPR (5 files) | no picture | no picture | **Needs engine:** VC-5 compressed DNG with no preview IFD (GoPro's VC-5 decoder) |
| CRW CHDK (`IXUS140`, `IXUS175`, `canon_ixy_220f`) | no picture | no picture | **Needs engine:** headerless sensor dumps (no container, no preview) |
| RAW `Gitup_G3_duo_170°` | no picture | no picture | **Needs engine:** headerless 12-bit Bayer dump |
| ARI `A004C002` | no picture | no picture | **Needs engine:** ARRI raw, uncompressed Bayer, no preview |
| CR3 `RAW.CR3` (also found in this pass) | no picture | no picture | **Needs engine:** HDR PQ CR3 with HEVC previews (only `glidecodecs.dll`'s libde265 could decode them, and that would need HEIF wrapping; not done) |
| EXR `suburban_parking_area_4k` | no picture | **2151x1076 preview**, 139 ms (full 4096x2048, about 80 ms decode) | Fixed (DWAA) |
| DIB `conv_0_12mp` | no picture | **4000x3000**, 60 ms | Fixed (packed DIB without a file header; WIC rejects it) |
| ICNS `conv_0_1024` | no picture | **1024x1024**, 58 ms | Fixed |
| RAS `conv_0_12mp` | no picture | **2151x1613 preview**, 74 ms | Fixed (byte-encoded RLE, 24-bit) |
| XWD `conv_0_12mp` | no picture | **2151x1613 preview**, 80–83 ms | Fixed |
| DPX `conv_0_12mp` | no picture | **2151x1613 preview**, 93 ms | Fixed (16-bit little-endian RGB) |
| PDF `conv_0_24mp` | no picture | no picture | **Needs engine:** a PDF renderer. In the app, a PDF opens only through an installed Shell thumbnail handler, and this PC has none |
| MRW / SR2 / SRF | 640x480 / 640x424 / 640x480 | same | Correct: the largest preview in each file |

Other notes:
- `M001.MDC` (Minolta RD-175, a headerless sensor dump) showed a meaningless 33x4 picture: Skia sniffs its first bytes (00 00 21 04) as WBMP, a format with no real magic number. Fixed: Skia's WBMP result is now accepted only for files named .wbmp (viewer and Explorer thumbnails), so this file fails cleanly (WIC/Shell still get their turn). It contains no preview, so showing it would need RAW development.
- The 40 MP phone DNG with no preview still goes to the Windows DNG codec, as before.
- Every other RAW format in the corpus (ARW, CR2, CR3, DNG, KDC, NEF, NRW, ORF, ORI, PEF, RAF, RW2, RWL, SRW, X3F, `pi.raw`) gives the same size as before.

New tests:
- `RawPreviewDecoderTests`:
  - the largest preview wins as an uncompressed RGB SubIFD
  - 16-bit range scaling
  - 8-bit preferred over 16-bit at equal size
  - a small DNG thumbnail is left to the platform codec
  - the scan skips declared sensor data
  - the Phase One makernote is not scanned
  - a truncated RGB preview fails
- `LegacyRasterDecoderTests`: DIB, ICNS, RAS, XWD, DPX and malformed input.
- `ExrDecoderTests`: DWAA/DWAB and malformed DWA.
- `ThumbnailCoverageTests`: fixtures for ICNS, RAS, SUN, XWD and DPX.

Verification:
- `build.cmd --no-pause` succeeded.
- `Glide.Core.Tests` 2352/2352, `Glide.Input.Tests` 21/21 and `tests/Glide.Explorer.Tests` 191/191 passed.
- Both audits passed.

---

## 5. Round 2 (same day, 19:50–20:05): remaining alternatives, full sizes, all RAW, cold start

About 15 minutes of measuring, so the two rounds total about 45 minutes. Results are in `<bench-results>\5.0.7-r2-*`; the runner is `tools/bench-507/run-round2.ps1`.

**Method changes in this round:**
- **Short idle check only**, at the user's request: 500 ms at ≤ 30 % CPU, waiting at most 5 s before each case. Busy % is recorded for every sample. The highest recorded value was 15 % (one vips sample); almost all were 1–8 %.
- **Strictly serial**, one case at a time.
- **Code under test:** synthetic sets at 12/24/50/100 MP, run against the **5.0.7 source including the integrated bridge change and the other agent's RAW fixes** (the harness was rebuilt at 19:1x).

**CLI engines** (djxl, opj, dwebp, avifdec) are wall times. They include process start-up (~20–30 ms) and, for opj and avifdec, writing an output file. They also skip Glide's BGRA-premultiplied conversion. Treat them as a *signal* about library speed, not a like-for-like number.

### 5.1 JPEG XL (Glide: libjxl 0.13 in `glidecodecs.dll`, `/O1`, ≤ 8 threads)

Median ms, first frame / full (p95 within 2–7 %):

| File | Glide | djxl 0.13 official release (CLI wall) | libvips-all |
|---|---:|---:|---:|
| d1 e3 12 MP | 106 / 105 | — / 68 | 294 / 283 |
| d1 e3 24 MP | 196 / 201 | — / 103 | 580 / 572 |
| d1 e3 50 MP | 440 / 455 | — / 195 | 1238 / 1217 |
| d1 e3 100 MP | 1037 / 1087 | — / **360** | 2515 / 2497 |
| lossless e1 24 MP | 182 / 187 | — / 100 | 395 / 385 |

**Reading:**
- The **same libjxl version, built by upstream, decodes 1.5–3.0x faster** than Glide's build, even including process start: 100 MP 1087 → 360 ms.
- The difference grows with size, which points at optimisation level and thread count: Glide uses `/O1` and at most 8 threads; djxl uses `/O2` and all 16. It also points at Glide's extra full-size BGRA conversion and area-average downscale.
- libvips-all is 2.3–2.8x slower than Glide. Reject it.
- Glide's first frame is no faster than its full decode: JXL has no reduced decode today.

**Candidates** (none adopted; they need native work and a measured rebuild):
1. Rebuild `glidecodecs.dll` with `/O2` for libjxl and Highway. Est. +0.5–1.5 MB uncompressed, BSD-3.
2. Use all logical processors for the libjxl runner.
3. A DC-first preview for the first frame.

Expected combined result: TTFP 0.3–0.5x for large JXL. **Risk:** low (same library), apart from the size increase.

### 5.2 JPEG 2000 (Glide: OpenJPEG 2.5.4 in `glidecodecs.dll`, `/O1`, ≤ 8 threads)

| File | Glide TTFP (reduced resolution) | Glide full | libvips-all TTFP / full | opj_decompress 2.5.4 (CLI, writes .raw) |
|---|---:|---:|---:|---:|
| 12 MP | **85** | 246 | 192 / **183** | 693 |
| 24 MP | **156** | 492 | 360 / **330** | 1371 |
| 50 MP | **113** | 1043 | 753 / **712** | 2859 |

**Reading:**
- Glide's first frame is the best of all engines, thanks to its wavelet-level reduction (50 MP: 113 ms).
- For **full** resolution, libvips' OpenJPEG build is **0.67–0.74x** of Glide's.
- The opj CLI is far slower: output writing, and probably a single-thread default path.

**Candidate:** an `/O2` rebuild plus all cores, the same rebuild as for JXL. **Expected:** full decode 0.7–0.8x. **Not adopted.**

### 5.3 AVIF / HEIC / WebP at full size

| File | Glide (TTFP / full) | Alternative |
|---|---:|---|
| AVIF 12 / 24 / 50 MP | 118 / 117 · 221 / 224 · 439 / 458 | avifdec 1.4.2 CLI (full, wall + y4m write): **78 · 130 · 212**. vips (aom): 4.2–4.4x slower |
| HEIC 12 / 24 / 50 MP | 197 / 198 · 358 / 362 · 755 / 756 | none on this PC: the vips web build has no HEVC, and WIC has no HEVC extension |
| WebP lossy 12 / 24 / 50 / 100 MP | 165 / 169 · 330 / 335 · 645 / 667 · 1219 / 1369 | Skia via harness 0.96–1.03x (same code); dwebp 1.6.0 CLI 1.04–1.15x; vips 1.05–1.17x |
| WebP lossless 12 / 24 MP | 145 / 124 · 286 / 261 | Skia 0.96–0.98x; dwebp 1.09–1.16x; vips 1.12–1.30x |

**Reading:**
- **AVIF:** upstream libavif + dav1d decodes 1.5–2.2x faster than Glide's libheif + dav1d path, *including* process start and writing a 24–50 MP y4m file.
- The likely causes:
  - libheif's colour-conversion and plane-copy overhead;
  - Glide's BGRA-premultiplied conversion plus area downscale;
  - the `/O1` build (dav1d's asm is unaffected, but its C parts are not).
- **Candidate:** decode AVIF through libavif's direct path, or tune the libheif conversion. ~+0.2 MB, BSD-2. **Not adopted.**
- **WebP:** no faster engine. **No change.**

### 5.4 JPEG full-resolution at every size (`--modes full`), and a libjpeg-turbo estimate for full-resolution/refinement only

Median ms. Glide here is the production path after the integrated change.

| File | Glide | WIC (bridge only) | libjpeg-turbo 3.2.1 | libjpeg-turbo banded (8 threads) |
|---|---:|---:|---:|---:|
| baseline 4:2:0 12 MP | 48.0 | 45.1 | 42.4 (0.88x) | **31.7 (0.66x)** |
| baseline 4:2:0 24 MP | 97.8 | 90.1 | 83.5 (0.85x) | **62.6 (0.64x)** |
| baseline 4:2:0 50 MP | 196.7 | 186.2 | 170.1 (0.86x) | **131.0 (0.67x)** |
| baseline 4:2:0 100 MP | 396.3 | 377.5 | 351.5 (0.89x) | **264.1 (0.67x)** |
| restart markers 100 MP | 397.4 | 380.4 | 358.6 (0.90x) | **281.5 (0.71x)** |
| 4:4:4 q95 24 / 100 MP | 214.1 / 904.6 | 209.5 / 1013.0 | 191.3 / 829.9 | **164.9 / 687.0 (0.77 / 0.76x)** |
| progressive 12 MP | 235.6 | 232.0 | **123.7 (0.53x)** | 148.0 (0.63x), peak memory 328 MB |
| progressive 24 MP | 475.1 | 458.7 | **251.7 (0.53x)** | 293.4 (0.62x), 653 MB |
| progressive 50 MP | 957.9 | 964.8 | **516.2 (0.54x)** | 579.3, 1369 MB |
| progressive 100 MP | 1849.1 | 1833.4 | **987.9 (0.53x)** | 1194.5, **2736 MB** |

The EXIF-6 row is left out: the harness's TurboJPEG engine does not rotate, so it would not be comparable.

**Estimate for "libjpeg-turbo for full-resolution/refinement decodes only"**, keeping WIC for the first frame, where WIC's progressive level-2 preview is 3.5x faster than any libjpeg-turbo variant:

- **Rule:** banded decode for baseline JPEG, plain single-thread decode for progressive. Banding a progressive file must be avoided: every band re-runs the whole multi-scan coefficient decode, and memory explodes.
- **Where it helps:**
  - the refinement after the first frame;
  - 100 % zoom;
  - full neighbour pre-decode;
  - print and save.
- **Expected savings** in those paths:

| File | Before | After |
|---|---:|---:|
| 24 MP baseline | ~98 ms | ~63 ms (−35 ms) |
| 100 MP baseline | ~396 ms | ~264 ms (−132 ms) |
| 24 MP progressive | ~475 ms | ~252 ms (−223 ms) |
| 100 MP progressive | ~1.85 s | ~0.99 s (−0.86 s) |

  The first frame is unchanged.
- **Cost:** libjpeg-turbo decoder statically linked (SIMD, no encoder, transform or 12/16-bit): est. **+0.4–0.6 MB uncompressed (≈ +0.2–0.3 MB in the installer)**. The official `turbojpeg.dll`, with everything, is 1.25 MB. Licence: IJG + BSD-3 + zlib, i.e. attribution only.
- **Peak memory:** +10–35 % (102 vs 92 MB at 24 MP; 429 vs 386 MB at 100 MP; 4:4:4 510 vs 385 MB).
- **Risk: medium.**
  - A second JPEG decoder for one format.
  - Must match WIC exactly for CMYK/YCCK, 12-bit, EXIF orientation, and ICC behaviour (WIC applies none today).
  - Pixel-level differences in IDCT/upsampling are visible only at 1:1 zoom, as ±1–2 levels.
- **Recommendation for the user's decision:** worthwhile for progressive JPEG (≈ 2x full decode) and moderately for large baseline files (≈ 1.5x).

### 5.5 Every real RAW file (89 files, 29 formats), after the other agent's RAW fixes: Glide vs LibRaw 0.22.3 (thumbnail + TurboJPEG)

**Speed:**
- Where both produce the **same output size**, LibRaw is **0.30x** of Glide on the first frame (geometric mean of 21 pairs) and **0.36x** on full (63 pairs). Most of those pairs are small thumbnails (IIQ 1.9 → 0.5–0.7 ms, MRW 4.4–5.1 → 0.9–1.1 ms).
- On mainstream cameras with large previews, first-frame ratios are:

| Format | LibRaw / Glide | Example |
|---|---:|---|
| CR2 | 0.57–0.65x | |
| CR3 | 0.55–0.66x | |
| NEF | 0.66–0.70x | |
| ARW | 0.70–0.78x | |
| ORF / ORI | 0.48–0.50x | 29–32 → 14–16 ms |
| RAF | 0.39–0.61x | |
| RW2 / RWL | 0.34–0.48x | |
| KDC | 0.46–0.65x | |
| DNG | 0.56–0.72x | |

  Output sizes differ slightly here (LibRaw keeps the DCT-scaled size, Glide resamples to the 2400 px cap), so these are approximate.
- **Cold start, first frame** (fresh process, 3 runs):

| File | Glide | LibRaw |
|---|---:|---:|
| ARW | 98 ms | 60 ms |
| CR3 | 95 ms | 41 ms |
| DNG | 67 ms | 27 ms |
| NEF | 102 ms | 55 ms |
| ORF | 61 ms | 21 ms |

  Glide's cold numbers include JIT, because the harness is not NativeAOT. The shipped AOT app pays less of this ~30 ms fixed cost.
- **Where the time goes:** mostly Glide's managed per-file work, not the JPEG decode itself:
  - walking the container,
  - copying the preview into a byte array and again into `SKData`,
  - straight BGRA → premultiplied conversion,
  - the final resample to 2400 px.
- **Zero-size alternative to LibRaw:** decode the embedded JPEG through the native WIC bridge (`GlideDecodeImageBoundedExW` on a memory stream) or with Skia straight to premultiplied output without the copies. **Expected** to recover most of the gap. Not measured.

**LibRaw static estimate:**
- `libraw.dll` 0.22.3 is 1.15 MB with a dynamic VC++ runtime, which Glide does not ship.
- A static build (`/MT`, `/O1 /GL`, no demosaic packs or DNG SDK) is est. **+1.0–1.4 MB uncompressed (≈ +0.5–0.6 MB in the installer)**.
- **Licence:** use the **CDDL-1.0** option. It is file-level copyleft, publishing LibRaw source and any changes is enough, and it avoids LGPL's relink requirement for static linking.
- **Risk:** medium. It is a parser exposed to hostile files, so it needs the same fuzz and size caps as the managed decoder.
- **Recommendation for the user's decision:** first try the zero-size managed/bridge fix above. Adopt LibRaw only if that leaves more than ~1.3x on the table.

**Correctness, after the RAW fixes** (the harness shows what Glide's production path returns):
- **Improved:** 3FR, MEF (1328x992), IIQ (now 1.9 ms instead of 37 ms) and X3F (which LibRaw cannot open) now work.
- **Still failing or wrong in Glide:**
  - ~~4 of 5 **DCR** show nothing; LibRaw finds 1126x750 previews.~~ Fixed after this report: 1125x750 (see section 5).
  - ~~**ERF** shows 160x120 where LibRaw finds 640x424.~~ Fixed: 640x424.
  - ~~**MDC** returns a 33x4 image.~~ Fixed: no bogus picture (the file has no preview).
  - Both fail: 3 CHDK **CRW**, 5 GoPro **GPR** (VC-5 RAW, no JPEG preview), `RAW.CR3` and the Gitup `.RAW`.
- These are for the RAW-fix agent and user to judge; this report does not change them.

### 5.6 Cold start, first frame (fresh process; OS file cache warm; harness runs JIT, not NativeAOT)

| File (24 MP) | Glide warm → cold | WIC bridge warm → cold | TurboJPEG warm → cold |
|---|---|---|---|
| JPEG baseline | 81 → 112 | 80 → 90 | 73 → 78 |
| JPEG progressive | 68 → 96 | 65 → 71 | 232 → 239 |
| PNG | 136 → 171 | 159 → 164 | — |
| TIFF LZW (integrated bands) | 38 → 75 | 186 → 194 (old bridge path, no bands) | — |
| TIFF tiled LZW | 188 → 230 | 525 → 538 | — |
| WebP | 329 → 375 | — | — |
| AVIF | 217 → 281 | — | — |
| HEIC | 354 → 422 | — | — |
| JXL | 195 → 256 | — | — |
| JPEG 2000 | 156 → 241 | — | — |

**Reading:**
- The first open in a new process costs Glide about **+30–40 ms** over warm for WIC formats, and **+60–85 ms** for the codec-module formats (AVIF/HEIC/JXL/JP2).
- The WIC codec's own first-use cost is only +5–10 ms. Much of the rest is managed JIT and first-use initialisation, which the NativeAOT app mostly does not pay. Loading and hashing `glidecodecs.dll` is the extra cost for the module formats.
- Speed Boost (a warm background Glide) already hides all of this for Explorer opens. **No change proposed.**

### 5.7 Round-2 summary for decision (nothing adopted; engine choices are the user's)

| Candidate | Gain (measured) | Size | Licence | Risk | Suggestion |
|---|---|---:|---|---|---|
| `glidecodecs.dll` `/O2` + all cores (JXL, JP2, AVIF C parts) | JXL full up to 3x faster (100 MP 1087 → ~360–500 ms est.); JP2 full ~0.7x | +0.5–1.5 MB | unchanged (BSD) | Low | **Strongest next step**; measure with a rebuilt module |
| AVIF via libavif + dav1d directly | 1.5–2.2x (CLI signal) | +0.2 MB | BSD-2 | Low–medium | Prototype together with the rebuild above |
| libjpeg-turbo for full/refine only | progressive 0.53x; baseline 0.64–0.71x (banded) | +0.4–0.6 MB | IJG/BSD | Medium | User decision |
| RAW: native/no-copy embedded-JPEG path | expected most of LibRaw's 0.3–0.7x | 0 | — | Low | Try before LibRaw |
| LibRaw (static, CDDL) | 0.30x (same output), 0.5–0.7x mainstream | +1.0–1.4 MB | CDDL-1.0 | Medium | Only if the above falls short |
| libvips (any bundle) | slower or no better on JXL, AVIF, WebP; faster only on full JP2 (0.7x) | 19–44 MB | LGPL | — | Reject |
| dwebp / libwebp 1.6 | 1.04–1.16x slower | — | — | — | Reject (no change for WebP) |

## 6. JPEG first frame + codec pack round (final configuration in 6.4)

Stopped at the user's request on 2026-11-17 ~21:10 during the second iteration round. Nothing below went through the final serial before/after comparison, the parity tests or `build.cmd`; treat every number as preliminary.

**Method.** `tools/bench-507/run-jpeg.ps1` (Glide production open path, viewport 2560x1369, warm-up 1, 5 reps / 3 at 100 MP, short idle check 500 ms ≤ 30 % CPU, max 5 s, busy % recorded; most samples 1–15 % busy). Prototype bridge = `native/Glide.Native` built with `-DGLIDE_JPEG_BENCH_KNOBS=ON` (switches via `GLIDE_JPEG_BENCH`). Ratios = geometric mean of median(variant) / median(BEFORE); `tools/bench-507/compare_jpeg.py`. Raw data: `<bench-results>\jpeg507-*`.

**Corpus.** BEFORE = synthetic JPEG (14 files, 12–100 MP), real `jpg`, `jpg-camera`, `jpg-phone`, `jpg-web` (96 files) and a new derived set (20 files, `Glide-bench-corpus\derived\`, made by `tools/bench-507/make_derived_jpeg.py` from a Canon R6 II 24 MP and a Nikon D850 45 MP photo: progressive, progressive 4:4:4, 4:4:4, restart markers, EXIF 6, CMYK, grayscale, odd 4001x2999, with / without EXIF). Iterations used synthetic + derived (34 files).

### 6.1 Hybrids and tricks (first view = `LoadForegroundAsync`, full = `LoadFullForegroundAsync`)

| Variant | First view | Full | Notes |
|---|---:|---:|---|
| H0 prototype (WIC, turbo compiled in but off) | 1.02 | 1.01 | = BEFORE within noise: no overhead |
| H1 turbo DCT-scaled first view (baseline), WIC (progressive), turbo full (16 bands) | 1.02 | **0.73** | first view: 444 0.94–0.99, odd 0.86, progressive **+5–10 %** (wasted file read) |
| H2 turbo everything | 1.05 (progressive up to **3.5x slower**) | 0.73 | reject |
| H3 WIC first view, turbo full | 1.01 | **0.72** | |
| H1, power-of-two scales only | 1.03 | 0.72 | min-scale trick: no gain |
| H1, 4 threads | 1.00 | 0.71 | |
| H1, **8 threads** | 1.00 | **0.70** | best thread count |
| H1, memory-mapped input | **0.98** | 0.71 | small win (vs one ReadFile) |
| H1, 24 bpp + expand (direct BGRA off) | 1.01 | 0.76 | direct BGRA is a win (keep) |
| Embedded preview as first view | **0.30** overall; untouched camera files with MPF 1620x1080 preview **0.05–0.09** (Canon 51 → 9 ms, Nikon D850 184 → 17 ms) | 1.00 | EXIF IFD1 thumbnails (160x120) rejected as too small; only MPF large-thumbnail entries used |

Full-decode detail (H1): baseline 4:2:0 0.73–0.90 (24 MP 94.7 → 77.8 ms, 100 MP 399 → 322 ms; Canon 24 MP 88 → 67 ms), 4:4:4 0.71–0.87, **progressive 0.52–0.56** (24 MP 460 → 244 ms, 100 MP 1868 → 984 ms, Nikon 45 MP 1319 → 683 ms), CMYK unchanged (WIC fallback). Peak memory: +5–15 % baseline, progressive single-thread +6 % synthetic, up to +57 % on the 45 MP progressive derived file (348 → 545 MB). Progressive is never banded.

Turbo DCT-scaled first views are **not** faster than WIC's (Huffman decode of the whole file dominates), so the first view stays on WIC except where a camera MPF preview exists.

**Chosen configuration (preliminary, not yet confirmed by a full-corpus run):** first view = MPF large preview when present (aspect within 1 %, ≥ half the requested size), otherwise WIC as before; full = libjpeg-turbo, memory-mapped, direct BGRA, baseline in ≤ 8 bands, progressive single-threaded; WIC fallback for CMYK/YCCK, 12-bit, lossless, arithmetic, unknown subsampling and any libjpeg-turbo warning. Round 2 (full corpus with this configuration, MPF on/off ablation) was stopped before it finished; only a partial `jpeg507-r2-nompf` folder exists.

### 6.2 Codec pack

Not measured. Source changes only (not built): `tools/build-codecs.ps1 -Optimize speed|size` (default speed = /O2, dav1d optimization=2, separate cache flavour `-o2`), `-MaxThreads` (default 64); glue `GLIDE_CODECS_OPT` / `GLIDE_CODECS_MAX_THREADS`, and `DecodeThreads()` now used for libjxl and OpenJPEG instead of the fixed 8. The shipped `codecs\glidecodecs.dll` is unchanged (build.cmd rebuilds it only when missing).

### 6.3 State of the tree

- Integrated (compiles, prototype built, **not** parity-tested, not through build.cmd): `native/Glide.Native/GlideJpeg.cpp/.h`, hooks in `GlideNative.cpp` (bounded + `GlideDecodeImageIntoW`, surface reused on WIC fallback), CMake links vendored `native/third_party/libjpeg-turbo` (3.2.1 official static lib, licence files, SHA256SUMS). Glide.Native.dll 202 KB → 791 KB uncompressed.
- Not done: parity tests (draft parked outside the repo; fixtures not generated), THIRD-PARTY-NOTICES entry, CHANGELOG, final serial comparison, codec rebuild/measurement, build.cmd, test runs, audits.
- Setup size: 19,215,363 bytes (18.3 MiB) BEFORE; not rebuilt since.

### 6.4 Final configuration (shipped in 5.0.7)

Built from the preliminary data above (no further benchmark campaign, by the user's decision); confirmed with a quick smoke timing only.

- **JPEG first view:** camera MPF large preview (large-thumbnail MP types only, aspect within 1 %, at least half the requested size, baseline) when present, replaced by the full decode; otherwise WIC's scaled first view exactly as in 5.0.6. No EXIF IFD1 thumbnails, no libjpeg-turbo DCT-scaled first view.
- **JPEG full resolution:** libjpeg-turbo 3.2.1, memory-mapped input, direct 32bpp BGRA (opaque = premultiplied), baseline >= 4 MP in at most 8 horizontal bands, progressive single-threaded, EXIF orientation applied into the surface. WIC fallback for CMYK/YCCK, non-8-bit, lossless, arithmetic, unknown subsampling and any libjpeg-turbo warning/error (into the same surface).
- **Codec pack:** `glidecodecs.dll` rebuilt with `-Optimize speed` (/O2) and up to 64 decoder threads; kept (faster in every smoke case, all codec tests pass).

**Parity (tests/Glide.Core.Tests/JpegTurboParityTests.cs, synthetic fixtures from tools/bench-507/make_jpeg_fixtures.py):** turbo vs WIC within 1 LSB per channel for baseline 4:2:0 / 4:2:2 / 4:4:4, grayscale, progressive (4:2:0 and 4:4:4), restart markers, odd sizes, banded >= 4 MP, EXIF orientations 2-8; CMYK declined (WIC); truncated file keeps the WIC outcome; MPF preview used when present and rejected when absent, too small, wrong aspect, corrupt, out-of-file offset or a non-thumbnail MP type. All pass.

**Smoke timing** (16-thread desktop, `Glide.DecodeBench507 one`, fresh process per sample, median of 4, box 2560x1369; BEFORE = 5.0.7 bridge without libjpeg-turbo):

| File | BEFORE first / full (ms) | AFTER first / full (ms) |
|---|---:|---:|
| Canon R6 II 24 MP baseline | 89 / 131 | 88 / 97 |
| Canon R6 II 24 MP progressive | 117 / 445 | 116 / 253 |
| Nikon D850 45 MP baseline | 222 / 300 | 222 / 230 |
| Nikon D850 45 MP EXIF 6 (MPF preview used) | 225 / 303 | 39 / 230 |

| Codec pack, full decode (ms) | /O1, 8 threads | /O2, all cores |
|---|---:|---:|
| JPEG XL 12 MP | 151 | 130 |
| JPEG XL 24 MP lossless | 256 | 206 |
| AVIF 12 MP | 133 | 131 |
| HEIC 12 MP | 164 | 144 |
| JPEG 2000 12 MP | 315 | 252 |

**Setup size:** 19,215,363 bytes before; 19,352,990 with libjpeg-turbo (codec pack /O1); 19,463,164 with libjpeg-turbo + /O2 codec pack (final build) (glidecodecs.dll 5,127,168 -> 5,693,440 bytes; Glide.Native.dll ~202 KB -> ~790 KB). Under the 20 MB target.

