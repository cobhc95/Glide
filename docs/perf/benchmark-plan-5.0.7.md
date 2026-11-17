# Decode / open-speed benchmark plan (5.0.7)

Status: **prepared, not run.** Nothing below has been measured yet. The harness refuses to measure without `--confirm`, and the user has to agree first because the round loads the CPU heavily for 30 minutes (quick tier) to about 2 hours (full tier).

Goal: for every format Glide opens, find out whether the first frame (time to first pixel) or the full-resolution decode can get faster in three ways:

1. **Current**: the 5.0.7 production path, as it is now.
2. **Tuned**: the same libraries with better settings or glue code.
3. **Alternative engine**: a different library.

For each option we record the installer size cost, the licence and the risk. The output is a recommendation per format.

---

## 1. Current decode pipeline (5.0.7 source, read on 2026-11-17)

Entry point: `ImageLoadCoordinator` (`src/Glide.Imaging/ImageLoadCoordinator.cs`). The backend is the `CodecProviderDecoderBackend` → `AvaloniaImageDecoderBackend` chain that `MainWindow` builds (`src/Glide.App/MainWindow.axaml.cs:310`).

- The coordinator probes the header (`ImageHeaderProbe`) and then picks one of three routes:
  - **Path preview**: a decoder-scaled first frame, fitted to the viewport box (factory default: 2400 px cap → 2400x1283 for a 2560x1369 viewport).
  - **Full-first**: formats that cannot decode at a reduced size go straight to a full decode when the frame fits half the decoded-cache budget (`PrefersFullFirstFrame`). This applies to PNG, TIFF, BMP, GIF and ICO, but not WebP.
  - **Stream preview / full**: the fallback.
- Refinement: `RefineForegroundAsync` decodes the full frame in the background after the first paint.
- Threading:
  - Foreground decodes go through a lane gate: 1 lane on constrained PCs, 2 otherwise.
  - Refinement uses 1 lane.
  - Prefetch uses 1–6 lanes depending on the hardware tier.
  - WIC decodes one image on one thread. The bundled codec module runs its own threads, capped at `clamp(cores, 1, 8)`.
- Colour management: **none**. No ICC transform runs on any path. All paths output 8-bit BGRA premultiplied. Higher bit depths are reduced to 8-bit inside the codecs.

| Suffix | First frame (TTFP) route | Full decode route | Library | Downscale on decode |
|---|---|---|---|---|
| jpg jpeg jpe jfif | `wic-native-scale`: `GlideDecodeImageBoundedExW` → `IWICBitmapSourceTransform::GetClosestSize` → reduced IDCT (1/2, 1/4, 1/8). Progressive files use `IWICProgressiveLevelControl` level 2 (colour-first) | `GlideDecodeImageIntoW` decodes straight into the `WriteableBitmap`. EXIF orientation ≠ 1 instead goes through the buffered path plus a scalar `OrientBgra` | Windows WIC JPEG | Yes (DCT scaling). A 24bpp result is expanded to 32bpp with a scalar loop |
| jxr wdp hdp | Same as JPEG (WIC scaled) | WIC | WIC JPEG XR | Yes |
| png apng bmp dib gif ico cur tif tiff | Full-first (WIC full decode into the bitmap). Otherwise WIC + `IWICBitmapScaler` (Fant) | WIC into the bitmap | WIC (zlib / LZW / ...) | No (decode at full size, then scale) |
| webp | WIC when the Store "WebP Image Extensions" are installed. Otherwise the native path fails 3 times, after which the suffix is marked unavailable and Skia is used (`Bitmap.DecodeToWidth`) | WIC or Skia `new Bitmap(stream)` | WIC WebP / libwebp inside libSkiaSharp 2.88.9 | libwebp output scaling only |
| heic heif hif avif avifs heics heifs | `glidecodecs.dll` (ABI v2) decodes the full frame, then a multi-threaded area-average downscale | `glidecodecs.dll` | libheif 1.23.7 + libde265 1.1.3 / dav1d 1.5.4 (built at `/O1`) | No |
| jxl | `glidecodecs.dll`: full decode + area average | Same | libjxl 0.13.0 decoder (Highway SSE2+AVX2, `/O1`), thread runner ≤ 8 | No (progressive DC not used) |
| jp2 j2k j2c jpc jpx jpf jpm mj2 | `glidecodecs.dll`: `opj_set_decoded_resolution_factor` to the nearest level, then area average | Same | OpenJPEG 2.5.4 (`/O1`), `opj_codec_set_threads` ≤ 8 | Yes (wavelet levels) |
| 40 camera RAW suffixes | `RawPreviewDecoder`: walks the container and reads the **largest** embedded JPEG into a `byte[]`. `SKData.CreateCopy` makes a 2nd copy. Skia then decodes it with n/8 DCT scaling to straight BGRA, followed by a bitmap copy | Same, at the preview's full size (the RAW sensor data is never developed) | libjpeg-turbo inside libSkiaSharp | Yes (n/8) |
| tga pcx pnm pam pfm qoi hdr wbmp xbm xpm sgi psd psb pdd | `BuiltInRasterDecoder` (managed C#), bounded | Same | Glide managed code | Bounded resample |
| exr, dcm nii fits, ora kra xcf pdn, ktx ktx2 pvr astc dds(uncompressed) | Managed decoders (`ExrDecoder`, `ScientificDecoders`, `LayeredEditorDecoders`, `PdnDecoder`, `Texture*`) | Same | Glide managed code | Mostly not |
| svg svgz | Svg.Skia, rendered at the needed size | Capped at 4096 px | Svg.Skia | Vector |
| everything else (pdf, eps, ...) | Shell thumbnail/preview last resort (`GlideDecodeShellPreviewW`) | Same | Windows Shell | Yes |

Opaque formats can also hit an existing Explorer thumbnail-cache entry (`shell-cache`, INCACHEONLY) when it is at least 72 % of the wanted size. Explorer thumbnails use `Glide.ShellThumbnail` + `glidemanaged.dll`; they are not part of this round.

### Existing tooling and prior numbers (reused)

- `tools/perf` (`Glide.PerfBench` + `run-perf.ps1` + `generate_perf_data.py`) is the headless harness behind the 5.0.0 table. It covers the coordinator decode modes, navigation, render, UI and app first frame, with medians of 5 warm runs (3 at 90 MP and above), a 1 ms private-bytes sampler, cold runs in a fresh process, and synthetic photo-like images.
- The new harness copies its policy (Maximum quality, 2400 px cap, 2560x1369 viewport), its memory sampler and its content generator (`make_photo`). It adds four things: alternative engines, an idle gate, p95, and CSV output.
- Prior reference (README "What changed in 5.0.0", Ryzen 7 9700X):
  - JPEG first frame is bound by WIC: 24 MP 36.7 ms in v4 vs 37.2 ms in v5.
  - Settled times: progressive JPEG 100 MP 201 ms, LZW TIFF 100 MP 756 ms, PNG 100 MP 530 ms, BMP 100 MP 98 ms.
  - Peak memory of a full decode: 93 / 385 MB at 24 / 100 MP.
  - The README itself calls decode "bound by the Windows codecs, which are single-threaded". **This round tests that claim.**
- `tools/benchmark-visible-first-pixel.ps1` / `run-perf.ps1 -Modes app` measure the end-to-end visible first frame (Glide.exe launch → composed frame). Use them only to confirm a winner in the real app after the engine round.

---

## 2. Speed tricks with the existing engines

Each candidate below has a measurement in this round (the variant shown) or is clearly marked as needing a code change first. "Gain" is the expected effect on the stated metric before measuring.

| # | Format | Trick | Where | Expected gain | Risk | In this round |
|---|---|---|---|---|---|---|
| T1 | JPEG full | Banded parallel decode: N horizontal iMCU-row crop regions decoded on N threads. Entropy decoding above each band is repeated. With restart markers (DRI) a real split is possible | new glue (libjpeg-turbo) | full 24–100 MP: 2–4x on 8+ threads; best on `rst1` files | Medium: more CPU, edge cases (progressive, CMYK) | `turbojpeg-bands` |
| T2 | JPEG preview | FASTDCT + FASTUPSAMPLE for the first frame only (refinement stays accurate) | flags | 10–25 % on TTFP | Low: small visible softness, replaced by refinement | `turbojpeg-fast` |
| T3 | JPEG all | Skia's libjpeg-turbo (already shipped) instead of WIC for JPEG | managed switch | unknown; WIC JPEG is reasonably SIMD'd. Could be 0.7–1.5x | Low size (0 bytes), medium behaviour (EXIF, CMYK, ICC handling differs) | `skia` |
| T4 | JPEG EXIF 5–8 full | Rotate inside WIC (`IWICBitmapFlipRotator`) into the direct surface instead of the buffered path + scalar per-pixel `OrientBgra` | GlideNative.cpp | −50 % peak memory; −30–80 ms at 24 MP | Low | measured as the `base420-exif6` row (wic vs turbojpeg); fix needs a native change |
| T5 | All WIC | Cache `IWICImagingFactory` per thread (today `CoCreateInstance` runs per decode). SSSE3 24→32 bpp expansion. Decode the bounded preview into the allocator surface too (today it is a `CoTaskMem` buffer + memcpy) | GlideNative.cpp + NativeImageDecoder | 0.2–3 ms per open; matters for small files and prefetch | Low | `wic` engine with `glideNative` pointed at a tuned build |
| T6 | PNG / TIFF / BMP full | Strip/tile-parallel decode (TIFF strips and tiles are independent; Pillow writes many strips) | libtiff or WIC per-strip `CopyPixels(rect)` on N threads | LZW TIFF 100 MP: 3–6x | Medium | `vips` (libtiff, threaded tiles) on `tiled256-lzw` vs `lzw` |
| T7 | PNG | Faster inflate (zlib-ng / libdeflate) + spng, or Wuffs | new codec | 1.3–2.5x | Medium (new code path for a core format) | `vips` (spng + zlib-ng). Wuffs needs a build (phase 2) |
| T8 | WebP | Skip the 3 doomed WIC attempts on PCs without the Store extension (detect the codec once). libwebp 1.6 `use_threads` + output scaling for the preview | NativeImageDecoder + Skia/libwebp | 4–12 ms once per session; 1.2–1.5x decode with threads | Low | `wic` vs `skia` vs `vips` vs `dwebp` |
| T9 | JXL preview | Progressive DC: `JxlDecoderSetProgressiveDetail(kDC)` gives a 1/8 image after the DC groups only. Show it, then refine | GlideCodecs.cpp | TTFP 3–8x on 24–100 MP lossy JXL | Medium: 1/8 of 100 MP (1536 px) is below the 2400 box, so the first frame is softer for ~100 ms | needs a code change; this round measures the full-decode gap (`glide` vs `djxl`) |
| T10 | AVIF/HEIC/JXL/JP2 | Rebuild `glidecodecs.dll` at `/O2` instead of `/O1` (dav1d is mostly asm and gains little; libde265, libjxl/Highway and OpenJPEG are C/C++ and may gain 10–40 %) | `tools/build-codecs.ps1` `$CFlags` | 10–40 % decode | Low risk, +1–2 MB | signal from `djxl` (official `/O2` build) vs `glide`. Exact A/B: harness copy with an `/O2` codecs folder |
| T11 | RAW | Pass the embedded JPEG to Skia without `SKData.CreateCopy` (pin or memory-map), decode straight to premul BGRA, and pick the smallest embedded preview ≥ the box rather than the largest | RawPreviewDecoder | 5–20 ms per RAW TTFP (largest preview 5–15 MB) | Low | `libraw` (+TurboJPEG) vs `glide` shows the headroom |
| T12 | HEIC/AVIF preview | Use the HEIF `thmb` item when it is ≥ the box (rare: phones store ~320 px) | GlideCodecs.cpp | large when present, usually absent | Low | not measurable on the corpus; skip |
| T13 | All | Memory-mapped input instead of a buffered read | engine glue | ≈0 with a warm cache; helps only when the file is cold | Low | `turbojpeg-mmap` |
| T14 | All | Lazy metadata (already `CacheOnDemand`); ICC handling is absent today, so there is no cost to remove. **Fairness note:** vips `thumbnail` imports an embedded ICC profile when present. Synthetic files carry none; real camera files may | n/a | none | n/a | documented |

---

## 3. Alternative engines (binaries in `<bench-codecs>`, official sources; see `SOURCES.txt` there for URLs and SHA-256)

All candidates expose a plain C ABI. The harness binds them through `NativeLibrary` + unmanaged function pointers, which is the same NativeAOT-safe pattern Glide uses for `Glide.Native` and the codec providers. Sizes are uncompressed DLL bytes (the installer compresses them by roughly 40–55 %).

| Engine | Formats | Version | Licence | Size added | AOT / interop | Maturity | Notes |
|---|---|---|---|---|---|---|---|
| Windows WIC (current) | JPEG, PNG, BMP, GIF, TIFF, ICO, JXR, DDS (+ WebP/HEIF/AV1/JXL/RAW via Store extensions) | OS | OS | 0 | in use | high | single-threaded per image |
| Skia in libSkiaSharp 2.88.9 (already shipped) | JPEG (libjpeg-turbo 2.x), PNG (libpng), WebP (libwebp), GIF, BMP, ICO | 2.88.9 | BSD-3 + component licences | **0** | in use (SkiaSharp) | high | candidate for a zero-cost JPEG/WebP switch |
| libjpeg-turbo TurboJPEG 3 | JPEG | 3.2.1 (2026-10-27) | IJG + BSD-3 + zlib | 1.25 MB `turbojpeg.dll` (encoder included); ~0.4–0.6 MB decode-only static into `glidecodecs.dll` | C ABI, no CRT dependency | very high | enables T1/T2; same core as Skia's JPEG but newer SIMD |
| libvips (NetVips.Native.win-x64) | JPEG (mozjpeg), PNG (spng/libpng + zlib-ng), TIFF (libtiff 4.7.2), WebP 1.6.0, GIF (cgif/nsgif), HEIF/AVIF (libheif 1.23.5 + aom), HDR | 8.18.7 | LGPL-2.1+ (dynamic link OK) | **19.0 MB** single DLL (official "web" bundle 23.3 MB, "all" 43.6 MB) | C ABI; varargs calls are fine on win-x64 | high | threaded, demand-driven; size is the blocker |
| libvips "all" bundle | adds JPEG XL (libjxl), JPEG 2000 (OpenJPEG), LibRaw, OpenEXR, FITS, PDF | 8.18.7 | LGPL-2.1+ / mixed | 43.6 MB (multi-DLL) | as above | high | reference only (JXL/JP2 comparison) |
| LibRaw | RAW embedded previews (+ full develop) | 0.22.3 | LGPL-2.1 or CDDL-1.0 | 1.15 MB + **VC++ runtime** (MSVCP140/VCRUNTIME140, not shipped with Glide; a static rebuild is required) | C ABI | very high | preview extraction is what Glide already does in C# at 0 bytes |
| libjxl (official release DLLs) | JPEG XL | 0.13.0 | BSD-3 | `jxl.dll` 3.1 MB + threads/cms/brotli ≈ 3.5 MB + VC++ runtime (Glide already links libjxl statically inside `glidecodecs.dll`) | C ABI | high | measured through `djxl` (release `/O2`) to size T10 |
| jxl-oxide | JPEG XL | 0.12.6 | MIT / Apache-2.0 | est. 1.5–2.5 MB cdylib | needs a Rust `cargo build --release` of the C API crate; no prebuilt Windows binary | medium | **blocked** for this round (no official binary); phase 2 |
| libwebp | WebP | 1.6.0 | BSD-3 (+ patent grant) | decoder static ~0.3–0.4 MB (vendored 1.5.0 encoder already in `glidecodecs.dll`) | C ABI | very high | via `dwebp` CLI and vips |
| libavif + dav1d | AVIF | 1.4.2 | BSD-2 | ~0.2 MB on top of the dav1d Glide already has | C ABI | high | `avifdec` CLI (AV1 decoder as bundled in the official artifacts). The aom decoder (vips) is a slower reference |
| libheif + libde265 (current) | HEIC/HEIF | 1.23.7 / 1.1.3 | LGPL-3 | in `glidecodecs.dll` | in use | medium (libde265 is not SIMD-heavy) | no practical alternative: FFmpeg's HEVC decoder is LGPL and ~3–5 MB. The Microsoft HEVC Video Extensions (paid Store app) are measured through `wic` if installed |
| OpenJPEG (current) | JPEG 2000 | 2.5.4 | BSD-2 | in `glidecodecs.dll` (DLL 0.36 MB) | in use | high | `opj` CLI (official build) checks the `/O1` cost. Grok (AGPL) and Kakadu (commercial) are excluded |
| Wuffs | PNG, GIF, BMP, WBMP, (JPEG) | v0.4.0-alpha.12 (single C file) | Apache-2.0 / MIT | est. 0.3–0.6 MB compiled | C, needs our own build (`wuffs-v0.4.0-alpha.12.c` downloaded) | medium (alpha tag; Chrome ships the GIF decoder) | phase 2 (needs a small native shim) |
| libdeflate / zlib-ng | inflate for PNG/TIFF | 1.26 / 2.3.3 | MIT / zlib | 97 KB / 152 KB | C ABI; libdeflate's MinGW build pulls `libgcc_s_dw2-1.dll` | high | only useful under a new PNG decoder (spng); phase 2 |
| SkiaSharp 3.119.4 / 4.156.1 | newer Skia codecs | — | BSD-3 | +2.2 MB / +3.7 MB `libSkiaSharp.dll` | managed API changes; Avalonia 11.3.20 is built against 2.88.9, **compatibility not verified** | high | not in this round (needs an Avalonia upgrade test) |
| Excluded | Magick.NET (>20 MB, AOT-hostile), ImageSharp (managed, Six Labors Split licence, slower than native), stb_image (slow, no SIMD), Grok (AGPL), Kakadu (commercial) | | | | | | |

---

## 4. Benchmark matrix

### 4.1 Synthetic set (deterministic, `tools/bench-507/generate_synthetic.py`)

Sizes: 12 MP 4000x3000, 24 MP 6000x4000, 50 MP 8688x5792, 100 MP 12288x8192.

There are two content profiles:

- **smooth**: the 5.0.0 `make_photo`. Lossless results stay comparable with earlier releases.
- **detail**: smooth plus multi-scale noise, fine stripes and grain, for camera-like entropy (JPEG q90 about 3–5 bpp).

Every lossy format uses *detail*. This is a deliberate fix for the coordinator's caveat: the real corpus's converted webp/avif/heic/jxl/jp2 files came from smooth sources at 12–24 MP and are unrealistically small and fast.

| Format | Variants | Sizes (MP) | Content |
|---|---|---|---|
| JPEG | baseline 4:2:0 q90 · progressive 4:2:0 q90 | 12 24 50 100 | detail |
| JPEG | baseline 4:4:4 q95 · baseline 4:2:0 with restart markers every MCU row | 24 100 | detail |
| JPEG | baseline + EXIF orientation 6 (rotated path) · baseline from smooth content (entropy control) | 24 | detail / smooth |
| PNG | RGB8 zlib 6 (12 24 50) · RGB8 zlib 1 (100) · RGBA8 · RGB8 detail | 12–100 | smooth (+1 detail) |
| WebP | lossy q85 (12–100) · lossless (12 24) · lossy + alpha (24) | 12–100 | detail / smooth |
| TIFF | LZW (12–100) · Deflate · none (24 100) · JPEG-in-TIFF (24) · tiled 256 LZW (24 100, via vips) | 12–100 | smooth (JPEG detail) |
| BMP | 24-bit | 24 100 | smooth |
| GIF | static 256 colours | 12 | smooth |
| QOI | RGB | 24 | smooth |
| AVIF | q70 4:2:0 8-bit | 12 24 50 | detail |
| HEIC | q80 4:2:0 8-bit (x265) | 12 24 50 | detail |
| JPEG XL | d1 e3 (12–100) · lossless e1 (24) | 12–100 | detail / smooth |
| JPEG 2000 | lossy rate 20, 6 resolutions | 12 24 50 | detail |

Totals: 56 files, 2358 MP in the full tier; 24 files (all variants at 24 MP), 576 MP in the quick tier. Output folder: `<bench-synthetic>`.

### 4.2 Real corpus (`<bench-corpus>\manifest.csv`, 173 files, ~6 GB)

- **Real files**:
  - camera RAW in 30+ formats (2–5 per format: 3fr arw cr2 cr3 crw dcr dng erf fff gpr iiq kdc mdc mef mos mrw nef nrw orf ori pef raf raw rw2 rwl sr2 srf srw x3f, ari)
  - jpg ×8 (up to 8192²)
  - png ×4 (4096²)
  - tif ×8
  - hdr ×3
  - exr ×5
- **Converted files** (12–24 MP, smooth sources): webp ×4, gif ×2, heic ×3, heif, avif ×3, jxl ×5, jp2 ×3, j2k, jpf, psd ×2 (flattened only), tif ×6, plus one each of bmp, dib, tga, pcx, qoi, dds, pam, pbm, pgm, ppm, sgi, ras, wbmp, xbm, xwd, icns, ico, mpo, dpx, fits, svg ×2, pdf.

Caveats for the decision report:

- **Converted lossy files** carry little detail and are small. Their absolute times are optimistic, so use the synthetic *detail* set for lossy-format speed claims. Converted files still prove correctness and routing on real containers.
- **No real Commons photos** were collected (HTTP 429). Real-photo JPEG evidence comes from the 8 real jpg files plus the embedded JPEGs inside the RAW files.
- **No real animated GIF/WebP and no xcf/psb/jxr/svgz** files exist. Animation is out of scope for this round (first frame only). jxr is covered only if a synthetic file is added later.
- RAW "full" in Glide means the largest embedded preview, not a demosaiced sensor frame. Every engine is compared on that basis.

### 4.3 Engines per format (cases = files × engines × {preview, full})

| Format | Engines |
|---|---|
| JPEG | glide, wic, skia, turbojpeg, turbojpeg-fast, turbojpeg-bands (full only differs), turbojpeg-mmap, vips |
| PNG / GIF | glide, wic, skia, vips |
| WebP | glide, wic (needs the Store extension), skia, vips, dwebp (CLI) |
| TIFF | glide, wic, vips |
| BMP / ICO / WBMP | glide, wic, skia |
| AVIF | glide, wic (needs the AV1 extension), vips (aom), avifdec (CLI) |
| HEIC/HEIF | glide, wic (needs HEVC), vips (libheif; the web bundle has no HEVC, so expect "unsupported") |
| JXL | glide, wic (needs the JXL extension), djxl (CLI), vips-all (separate process) |
| JPEG 2000 | glide, opj (CLI), vips-all (separate process) |
| RAW | glide, wic (needs the Raw Image Extension), libraw (+TurboJPEG) |
| everything else | glide (+ wic where WIC has a codec): regression and coverage only |

The cases are 514 synthetic (group A), 16 vips-all (group B) and 1026 real (group C) in the full tier, and 218 / 6 / 562 in the quick tier (real: at most 2 files per format).

---

## 5. What is measured

- **Time to first pixel (`preview`)**: request → a complete BGRA8 premultiplied frame fitted to the 2560x1369 box.
  - For `glide` this is `LoadForegroundAsync` with the factory-default policy, on a fresh coordinator with empty caches. It is decode-ready time, not composition; use the app harness for that.
  - For other engines it is the engine's fastest reduced decode (DCT scaling, vips shrink-on-load, wavelet levels). Engines without one decode in full and then resize.
- **Full decode (`full`)**: request → full-resolution BGRA8 premul frame.
  - `glide`: `LoadFullForegroundAsync`.
  - `wic`: the production direct-into-surface path.
- **Conversion cost (`convert_ms`)**: RGB→BGRA and premultiply time, for engines that cannot emit BGRA (vips, some LibRaw thumbnails). It is included in `ms` and reported separately so it can be subtracted when judging the library itself.
- **Peak memory (`peak_delta_mb`)**: private bytes above the baseline, sampled every 1 ms (same method as 5.0.0). Cold samples report the peak working set of the child process.
- **Cold vs warm**:
  - *Warm*: same process, codec DLLs loaded, OS file cache warm, Glide caches empty. Comes after 1 untimed warm-up.
  - *Cold* (full tier, ≤ 30 MP): a brand-new process per sample. DLL loads and codec first-use init are inside the timed decode, but engine construction is not. The OS file cache stays warm.
  - True disk-cold numbers would need a standby-list purge (admin, affects the whole PC) or a reboot. They are deliberately **not** automated. If needed, do one manual pass after a reboot with `--reps 1 --warmup 0`.
- **Statistics**: per case n = 7 (5 at ≥ 90 MP; quick tier 5/3). The report gives median, p95 (nearest rank: with n = 7 this is the maximum), min, max, stdev, CV %, the busy % of the machine before the case, route, and output size and checksum (a 64x64 sample hash for a correctness sanity check across engines).
- **Outputs** go to `<bench-results>\<label>-<group>\`:
  - `raw.csv`: every sample, flushed as it is written.
  - `summary.csv`
  - `summary.md`: per-file tables plus a per-format geometric-mean ratio against `glide`.
  - `run-info.txt`: machine, settings, engine libraries.

### Method and controls

- **Serial**: one case at a time, one harness process per group.
- **Order**: engines are interleaved per file in a seeded shuffled order (`--shuffle 507`), so thermal drift or background bursts do not bias one engine.
- **Process settings**: priority High; EcoQoS / power throttling off (process-local); affinity left at all cores by default. Use `-Affinity 0xFF` for an 8-core run, or a single-core mask to compare single-thread efficiency.
- **Idle gate**: before every case, the harness waits until system CPU (excluding the harness itself) has stayed ≤ 15 % for 750 ms, for at most 60 s. A timeout is logged as `(NOISY)`, and each sample keeps its `busy_pct`, so noisy samples can be dropped afterwards.
- **GC**: a forced GC before every sample, outside the timer. Concurrent GC is off.
- **Same output contract for every engine**: a full BGRA premul surface, touched by the checksum. No engine can "win" by returning a lazy or partial frame.
- **Before running**:
  - The PC must be plugged in, on the *Best performance* power mode, with no other build running. The other agents' `build.cmd` runs must be finished, and the user should not be using the PC.
  - The installed Glide in Program Files is never touched. The harness loads Glide's assemblies from its own isolated build output (`tools/bench-507/.out`).

---

## 6. Estimated run time

The harness `plan` command estimates measuring time from rough per-format ms/MP figures. Idle waits come on top.

| Step | Quick tier | Full tier |
|---|---|---|
| Synthetic generation (`--jobs 2`) | ~5–8 min (24 files) | ~25–40 min (56 files; the AVIF/HEIC/JXL 50–100 MP encodes dominate) |
| A: synthetic, main engines | ~7 min (218 cases) | ~31 min (514 cases, incl. cold) |
| B: synthetic, vips-all (JXL/JP2) | <1 min | ~2 min |
| C: real corpus | ~14 min (562 cases, ≤ 2 files per format) | ~49 min (1026 cases, incl. cold) |
| **Total** | **~30 min** | **~1 h 50 min – 2 h 10 min** |

Recommendation: run the **quick tier first**. It answers the go/no-go question per engine. Then run the full tier only for formats where an alternative or tuning shows at least a 15 % gain.

---

## 7. How to run (after the user confirms)

```powershell
# plan only: builds the harness in isolation, prints the matrix and the estimates, measures nothing
powershell -ExecutionPolicy Bypass -File tools\bench-507\run-bench.ps1 -Tier quick
# quick round: generates 24 MP synthetic files if missing, then measures groups A, B, C
powershell -ExecutionPolicy Bypass -File tools\bench-507\run-bench.ps1 -Tier quick -Confirm
# full round
powershell -ExecutionPolicy Bypass -File tools\bench-507\run-bench.ps1 -Tier full -Confirm
# engine sanity (loads DLLs, no decoding)
tools\bench-507\.out\bin\Glide.DecodeBench507\release\Glide.DecodeBench507.exe probe
```

To A/B a *tuned* native build (T4, T5, T10) without touching src:

- **Glide.Native**: point `glideNative` in `engines.json` at the tuned `Glide.Native.dll`.
- **glidecodecs**: copy the harness output folder and replace `codecs\` with the `/O2` build plus a regenerated `codecs.index.json` (`tools/generate-codec-index.ps1`). Run the `glide` engine from both copies.

---

## 8. Decision report template (`docs/perf/benchmark-results-5.0.7.md` after the run)

```markdown
# 5.0.7 decode benchmark results

Machine / power mode / date / harness commit-equivalent (5.0.7 source snapshot) / tier / NOISY cases dropped: n

## Summary
| Format | Metric | Current (glide) | Best tuned | Best alternative | Synthetic ratio | Real ratio | Size delta | Licence | Risk | Recommendation |
|---|---|---:|---|---|---:|---:|---:|---|---|---|
| JPEG 24 MP | TTFP ms (median/p95) | | turbojpeg-fast | skia / turbojpeg | 0.xx | 0.xx | +0 / +0.5 MB | IJG/BSD | Low | adopt / test further / reject |
| JPEG 100 MP | full ms | | turbojpeg-bands | | | | | | | |
| PNG | full ms | | | vips (spng+zlib-ng) | | | | | | |
| TIFF LZW | full ms | | strip-parallel | vips (libtiff) | | | | | | |
| WebP | TTFP / full | | skip-WIC probe | skia / libwebp 1.6 | | | | | | |
| AVIF | TTFP / full | | /O2 build | avifdec | | | | | | |
| HEIC | TTFP / full | | /O2 build | — | | | | | | |
| JXL | TTFP / full | | DC preview, /O2 | djxl (/O2) | | | | | | |
| JPEG 2000 | TTFP / full | | /O2 | opj | | | | | | |
| RAW (embedded preview) | TTFP | | no-copy + smallest-sufficient preview | libraw+turbojpeg | | | | | | |

Ratios = geometric mean of (engine median / glide median) over the files of that format; < 1.00 is faster.

## Per format
### <Format>
- Current route and numbers (synthetic detail + smooth, real): table copied from summary.md.
- Tuned variant: what changed, numbers, memory delta, visual difference (checksum/size mismatch explained).
- Alternative engine: numbers, installer size delta (compressed and uncompressed), licence obligations (LGPL: dynamic link or relinkable objects + source offer), AOT/interop notes, maturity and security posture (fuzzing, CVE history), threading behaviour while browsing (does it steal CPU from prefetch?).
- Decision: adopt now / adopt behind a setting / prototype in 5.0.8 / reject, with the reason.

## Caveats
- Converted real files are low-detail: lossy-format claims are based on the synthetic detail set.
- Warm OS file cache throughout. Cold = process-cold only.
- CLI engines (djxl, dwebp, avifdec, opj) include process start-up and output writing: use them as an upper bound only.
- vips `thumbnail` adds a lanczos resample and ICC import that Glide does not do.

## Follow-ups
- Changes to implement, the tests needed (pixel identity, orientation, alpha, malformed input), and the release-notes / README perf-table update (5.0.0 table format).
```

---

## 9. Blocked or deferred

- **jxl-oxide**: no official prebuilt Windows binary. It needs a Rust toolchain to build its C API crate as a cdylib (crate name and exports to be checked first). Deferred to phase 2.
- **Wuffs PNG/GIF and libspng + libdeflate**: the sources and binaries are downloaded, but each needs a small native shim DLL (C, about 150 lines) before the harness can bind it. Phase 2, if the `vips` PNG numbers show headroom.
- **SkiaSharp 3.x / 4.x**: a newer Skia would need Avalonia compatibility work. It cannot be swapped in-process with 2.88.9. Not in this round.
- **WIC extension rows** (WebP, HEIF/HEVC, AV1, JXL, RAW): measured only if the Store extensions are installed on this PC. Otherwise they show as `err` (recorded, not a harness failure).
- **True disk-cold timing**: needs a standby purge or a reboot. It is manual and optional (see §5).
- **The other agents**: the harness builds into `tools/bench-507/.out` (its own ArtifactsPath), so it never shares obj/bin with a concurrent `src` build. The measurements still should not run while `build.cmd` is running.
