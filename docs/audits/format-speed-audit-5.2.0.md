# Format speed audit, 5.2.0: every format Glide opens, measured

Read-and-measure audit. No product code was changed. Everything below was measured headless (no Glide window, no installed Glide touched) on
synthetic, deterministic fixtures, one per format variant, by `tools/perf/Glide.FormatAudit`, `tools/perf/Glide.PerfBench` and the drivers listed under
"Reproduce". The previous round is `docs/audits/thumbnail-perf-5.1.7.md`; this one extends it from 10 formats to 100 fixtures covering every decoder family.

Hardware: AMD Ryzen 7 9700X (8 cores / 16 threads), 61.6 GB RAM, SSDs, Windows 11 IoT Enterprise LTSC 2024 (10.0.26100), .NET 10.0.403, Release build of
the tree at `f02e12f` (native `Glide.Native.dll` built from the same tree). The machine was shared with another session driving the desktop UI and a third
agent building, so load varied between 2 % and 76 %: see "Noise" for what was re-run and what is still flagged.

## Headline findings

1. **The Explorer tab shows a thumbnail for 11 of Glide's 196 recognised suffixes** (`.jpg .jpeg .png .bmp .gif .tif .tiff .webp .jxr .wdp .hdp`, the list in
   `MainWindow.CanUseBrowserThumbnailDecoder`). Every other format, including formats Glide opens in 4 - 200 ms (RAW, HEIC, AVIF, JPEG XL, JPEG 2000, PSD,
   EXR, SVG, ICO/CUR, DDS, TGA, ...), shows the file-type glyph. Windows' own Explorer shows real thumbnails for them through Glide's registered shell provider. The
   viewer's decoders can produce a ~480 px frame for each of these in 1 - 210 ms warm (table "As-if thumbnail"), so this is the largest gap by a wide margin.
2. **Tiled and pyramid TIFF are the slowest decode in the table relative to their size**: a 24 MP deflate TIFF takes 29 ms as strips but **185 - 189 ms
   as tiles or as a SubIFD pyramid** (4K: 12.5 ms strips, 72.6 ms tiles, so it is the tiling, not the codec). The thumbnail path ignores the pyramid levels, and the
   BigTIFF fixture cannot be opened at all (WIC does not read BigTIFF).
3. **Windows' in-box thumbnail provider is 4.5 - 6.5x slower than Glide's own paths for TIFF and BMP, 12 - 16x slower for progressive and camera JPEG, 49 - 54x slower for JPEG XR**,
   about equal for baseline JPEG, PNG and GIF, and **cannot decode WebP, HEIC, AVIF, JPEG XL, JPEG 2000, RAW, PSD, EXR, SVG, HDR, TGA** and the rest on this PC (no Store codecs).
4. **Cold-start gaps are large for the plug-in codecs**: the first HEIC / AVIF / JPEG XL / JPEG 2000 open in a process costs 63 - 106 ms more than the second (about 30 - 40 ms of that is the first-decode cost every format pays in this non-ReadyToRun harness, the rest is provider verification and load), SVG 100 ms more (Svg.Skia initialisation, even for a trivial file), ICO / CUR / GIF about 40 ms more (WIC / Skia first use).
5. **Already at the floor** (nothing worth doing): baseline JPEG thumbnails (13 - 26 ms, libjpeg-turbo 1/8 scale), camera JPEG with MPF preview (2 ms), progressive JPEG thumbnails
   (8.6 - 16.6 ms, DC-only), BMP (4 - 8 ms), uncompressed / PackBits / LZW strip TIFF (3.5 - 34 ms), JPEG XR thumbnails (2.8 - 3.8 ms), ICO / GIF animation (1 - 2 ms), and every
   disk-cache hit (0.1 - 0.3 ms, all formats).

## Top 10 improvements (details and the rest of the list below)

| # | Improvement | Where the gain was measured | Expected gain | Effort | Risk |
|---|---|---|---|---|---|
| 1 | Explorer-tab thumbnails for every format the viewer opens (start with the XS slice: `.ico .cur .jfif .jpe .jif .mpo .dds .apng`); route each remaining format through the viewer's bounded decoder or the shell provider, whichever measures faster (table "As-if thumbnail") | glyph today; viewer bounded 480 px: ICO 1 ms, DDS 10 - 20, RAW 3.6 - 34, JP2 13 - 17, PSD 64 - 122, EXR 40 - 76, TGA/PPM/SGI 22 - 26, HEIC/AVIF/JXL 46 - 114 | no thumbnail -> 1 - 120 ms per tile, cached afterwards (0.2 ms) | M (XS for the extension slice) | low - medium (a failed decode per tile; add a negative cache and a size / time budget) |
| 2 | Tiled and pyramid TIFF: read tiles in parallel bands (the strip path already does), and take the smallest SubIFD / reduced-resolution page that covers the request; parse BigTIFF ourselves | tiled 24 MP 185 ms vs 29 ms as strips; pyramid 189 ms; BigTIFF fails; Windows 960 ms | tiled 6x (about 30 ms); pyramid about 30x (estimate: 1/64 of the pixels, about 5 ms); viewer first view 200 -> about 40 ms; BigTIFF opens | M | medium (own IFD / tile parser, memory guards) |
| 3 | HEIC: use the embedded `thmb` thumbnail item for tiles and for the first paint. JPEG XL: decode the 1:8 DC image (libjpeg-xl progressive detail) for tiles. AVIF: no equivalent, leave | HEIC 12 MP 114 ms, JXL 12 MP 61 - 103 ms, both decode the full picture then downscale (`GlideCodecs.cpp` `Downscale`) | HEIC about 10x (estimate, iPhone files carry a 320 px thumbnail); JXL about 4 - 5x (estimate) | M - L | medium (libheif / libjxl API surface, files without a thumbnail fall back) |
| 4 | SVG: send path-heavy SVG to Direct2D (`D2D1SVG`, already in the shell provider) for tiles; keep Svg.Skia for embedded rasters; pre-initialise Svg.Skia on idle | 5000-element SVG: viewer decoder 213 ms vs shell provider 25 ms (8.5x); but an SVG with an embedded 2000 px PNG is 779 ms in the provider vs 66 ms in Svg.Skia; first SVG in a process +100 ms | tile 8x for vector SVG; first SVG open -100 ms with pre-init | S (route) - M (viewer first view) | medium (rendering differences between D2D and Svg.Skia) |
| 5 | Provider pre-load: when a folder listing contains `.heic .avif .jxl .jp2`, resolve (hash + load) `glidecodecs.dll` on a background thread before the first open | first open costs +63 - 106 ms over a warm open (HEIC 167 vs 89, AVIF 119 vs 51, JXL 117 vs 54, JP2 250 vs 144; JPEG 24 MP for comparison +32) | first open of those formats about -30 to -70 ms | S | low |
| 6 | CMYK JPEG (and CMYK TIFF): decode with libjpeg-turbo (YCCK / CMYK, DCT-scaled) instead of WIC for the first view, and make the thumbnail native path accept it | CMYK JPEG first view 428 ms (settled 857), thumbnail 31 ms via Skia fallback; CMYK TIFF thumbnail 65 ms | first view about 7x (estimate 50 - 70 ms), thumbnail about 3x | S - M | medium (colour: inverted Adobe CMYK, no ICC in a quick path) |
| 7 | PSD: read the composite with row / column subsampling for small requests, use the 1036 thumbnail resource as the instant frame for tiles up to its size, parallelise the ZIP-with-prediction inverse | PSD 4K first view 63 - 105 ms; ZIP+prediction 105 ms (2x raw); managed decoder has no `Parallel` use | first view about 2x, ZIP files about 2x, tiles up to 160 px about 100x | S - M | low |
| 8 | EXR: decode ZIP / ZIPS / RLE scanline blocks in parallel (blocks are independent) | EXR 4K half ZIP 44 - 47 ms, float ZIP 73 ms; `ExrDecoder.cs` has no `Parallel` use | about 3 - 4x on 8+ cores (estimate) | S - M | low |
| 9 | Explorer scheduling: shortest-expected-job-first by extension and size, prefetch one screen beyond the viewport, keep the 12 lanes busy with cheap formats first | per-format warm costs span 1 ms (ICO) to 189 ms (tiled TIFF) in one folder; the lane gate is first-come first-served | mean time to first screen of thumbnails (unmeasured: needs the UI) | S - M | low |
| 10 | Progressive JPEG viewer first frame: show the DC / first scans immediately and refine, instead of the staged WIC route | progressive 24 MP: first view 113 ms warm (138 cold), settled 249 ms; baseline 24 MP is 39 ms; thumbnail DC-only is 16.6 ms | first pixels about 2x sooner (estimate 50 - 60 ms) | M | medium (stage ordering with the refinement scheduler) |

PNG deserves a note because it is the other large item: 24 MP PNG is 135 ms and the cost is zlib inflate (see 5.1.7). The only measured lever here is **interlaced PNG**, whose Explorer-tab
thumbnail (Skia, 93 ms for 4K) is 1.35x slower than the viewer route (69 ms) and Windows' provider (73 ms); route it through the same path. A faster inflate (libdeflate) is estimated at 1.2 - 1.3x for an M effort, so it is not in the top 10.

## Method

- **Fixtures** (`tools/perf/generate_format_fixtures.py`, which first runs `generate_thumb_fixtures.py`): 100 files, 4K (3840 x 2160), 12 MP, 24 MP and a few small ones, deterministic, synthetic photo-like content.
  JPEG (baseline, progressive, camera with EXIF thumbnail + MPF, EXIF-thumbnail only, 4:4:4, gray, CMYK, ICC, MPO), PNG (8-bit, gray, palette, 16-bit, Adam7, alpha, APNG), WebP (lossy, lossless,
  alpha, animated), GIF (still, animated), BMP (24/8/32-bit), ICO / CUR, TIFF (LZW, deflate, PackBits, raw, JPEG-in-TIFF, tiled, SubIFD pyramid, BigTIFF, 16-bit, float32, gray, CMYK, multi-page),
  HEIC, AVIF (opaque, alpha), JPEG XL (lossy, lossless, alpha), JPEG 2000 (jp2, j2k), JPEG XR (WPF encoder), five camera-RAW containers (DNG, NEF, ARW, RAF, CR3; real embedded JPEG previews around
  24 MB of filler "sensor data"), PSD (raw, RLE, ZIP with prediction, 16-bit, layered; with and without the 1036 thumbnail), OpenEXR (none, RLE, ZIPS, ZIP; half and float), Radiance HDR, PFM, SVG / SVGZ,
  TGA (raw, RLE), PCX, PPM, QOI, SGI, DDS (RGBA, DXT1, DXT5, DXT1 with a mip chain), KTX 1, FITS, NIfTI, OpenRaster.
- **Thumbnail columns** call the production entry point `ThumbnailDecode.DecodeToWidth(path, 236)` (the Extra large tile) through `Glide.PerfBench thumb` / `thumb-one`, with the disk cache off, plus one cache hit. The call is made for **every** fixture, but
  the Explorer tab only issues it for the 11 listed suffixes; the "Explorer asks" column says which. Rows for the other formats show the cost the decoder would have if the tab asked.
- **Warm** = median of 7 runs in one process after one untimed warm-up (codecs loaded, OS file cache hot). **Cold** = the first decode in a fresh process, median of 5 processes. The bench binaries are a plain Release build
  (no ReadyToRun composite), so cold numbers include more JIT than the shipped app; read cold as an upper bound and the warm - cold gap as a relative signal.
- **Viewer columns** (`Glide.FormatAudit view` / `view-one`) use the same measurement as PerfBench `decode` (factory-default Maximum quality policy, 2560 x 1369 viewport: first frame, settled = first frame + Fit refinement, full decode) but build the
  loader the way `MainWindow` does, with `CodecProviderDecoderBackend`, so the bundled codec plug-ins are on the route (PerfBench's own `decode` mode uses the plain Skia / WIC backend and cannot open HEIC / AVIF / JXL / JP2).
  "Peak private MB" is the peak delta of process private bytes.
- **As-if thumbnail** (`Glide.FormatAudit vsmall`): the viewer's first-frame decode bounded to a 480 px box (the policy floor), for every format. It is what a tile would cost if the Explorer tab reused the viewer's decoder.
- **Windows comparison** (`Glide.FormatAudit shell`): the in-box **Windows Photo Thumbnail Provider** (CLSID `C7657C4A-...`, `PhotoMetadataHandler.dll`) created in-process and asked for 236 px via `IThumbnailProvider` (no registry change, no dllhost, no thumbcache), and
  **Glide's shell provider** (`Glide.ShellThumbnail.dll` from the bench output, its pins matching `codecs\`) loaded the same way. On this PC the registered thumbnail handler for almost every extension is Glide's installed provider, so `IShellItemImageFactory` would run
  Glide's DLL from `C:\Program Files\Glide`; to honour "never touch the installed Glide" it was run only for `.jxr`, whose handler is Windows' (`jxr-4k` 132 ms warm, 165 ms for the first request, against 137 ms direct, so the direct measurement is representative).
  The shell numbers exclude Explorer's process hop (dllhost) and its thumbcache.
- **Path used**: the Explorer route is read by calling the same internal decisions as `ThumbnailDecode` (`Glide.FormatAudit route`); the viewer route is `ImageLoadCoordinator`'s `DecodeRoute` plus the codec registry tier (`CodecCapabilityRegistry`).
  The viewer's first view of PNG / GIF / ICO goes native bridge (WIC) first with Skia as the fallback (`AvaloniaImageDecoderBackend`); the route label the loader reports for them is just `stream`.

### Noise

Samples were load-checked per cell (CPU % at the start of the cell) and per phase. A timing is flagged **noisy** when its samples spread by more than 40 % of the median and more than 3 ms. The first full run had busy samples (43 %, 46 %, 44 %, 68 %, 74 %), so
`vsmall-warm` was re-run for all 100 files, `inbox-cold` for the 39 affected files, the cold phases for every file that started above 35 % CPU, and every noisy warm cell was re-run (twice for the worst); two TIFF files added late were measured while the machine was busy and re-run once.
**Still flagged after the re-runs**: `view warm / full` of `exr-none-float-4k`, `hdr-4k` (also its bounded decode) and `svg-5000` (noisy), and the cells marked **†phase** in the Flags column, which started with the machine above 35 % CPU in that phase (the samples inside the cell did not spread, so the values are plausible, but treat them as +/- 15 %).
Another session was driving the desktop at the same time throughout, so none of the cold numbers are lab-clean.

## Format coverage

`CodecCapabilityRegistry` recognises **196 suffixes**, of which 101 are "decode-enabled" (bundled decoder or core path):

| Registry group | Suffixes | Decoder | Measured here by |
|---|---|---|---|
| Core fast path + WIC aliases (NativeOs) | 23: `.jpg .jpeg .jpe .png .bmp .tif .tiff .gif .ico .webp` + `.jfif .jif .jfi .pjpeg .pjpg .apng .dib .rle .cur .jxr .wdp .hdp .dds` | WIC bridge, libjpeg-turbo, libwebp, Skia | all JPEG / PNG / WebP / GIF / BMP / ICO / CUR / TIFF / JXR / DDS fixtures |
| Codec plug-in (ModularNative) | 16: `.heic .heif .heics .heifs .hif .avif .avifs .jp2 .j2k .j2c .jpc .jpx .jpf .jpm .mj2 .jxl` | `glidecodecs.dll` (libheif, dav1d, libjxl, OpenJPEG) | HEIC, AVIF, JXL, JP2, J2K fixtures |
| Built-in RAW preview | 40 camera suffixes | managed embedded-JPEG extractor | DNG, NEF, ARW, RAF, CR3 containers (TIFF / RAF / ISO-BMFF families) |
| Built-in raster | 31: TGA family, PCX, PNM family, PFM, QOI, HDR, WBMP, XBM, XPM, SGI family, PSD / PSB / PDD, ICNS, RAS, DPX, XWD | managed | TGA, PCX, PPM, QOI, SGI, HDR, PFM, PSD |
| Built-in other | OpenEXR (1), scientific (9), layered (4), GPU textures (4), SVG / SVGZ (2) | managed / Svg.Skia | EXR, FITS, NIfTI, ORA, KTX 1, SVG, SVGZ |
| Routed on demand | 66 (`.mng .btf .ani .pdf .eps .ai .mpo .jps .r3d .psp ...`) | Shell / WIC extensions / optional providers, not installed on this PC | `.mpo` and `.btf`-style BigTIFF only |

Not measured, and why: 12-bit JPEG (no encoder on the PC; the native thumbnail path declines it), JPEG arithmetic / lossless, DICOM, XCF, PDN, KRA, KTX2, PVR, ASTC, PIZ / B44 / DWA and tiled EXR (no writer),
real camera files (the RAW fixtures are synthetic containers with a real embedded JPEG; a real file with a lossless-JPEG sensor plane reads the same bytes), HEIC with iPhone thumbnail items or 10-bit, animated AVIF / HEIC sequences,
and the 66 routed suffixes that need a Windows codec extension or an optional provider (none installed). WebP, HEIC and AVIF cannot be decoded by Windows here (no Store extension), which is itself a measured result.

## Summary table (one representative per family)

Ms; "-" = not asked (Explorer tab) or not applicable; "fails" = the decoder cannot open the file on this PC. Full per-fixture tables follow.

| Format | Fixture | Explorer thumbnail path | Thumb cold ms | Thumb warm ms | Viewer path | View cold ms | View warm ms | Full decode ms | Windows in-box thumb warm ms | Glide shell thumb warm ms |
|---|---|---|---:|---:|---|---:|---:|---:|---:|---:|
| JPEG baseline 24 MP | jpeg-base-24mp.jpg | libjpeg-turbo | 37.5 | 25.6 | WIC bridge, scale | 71.4 | 39.0 | 34.3 | 28.0 | 24.8 |
| JPEG progressive 24 MP | jpeg-prog-24mp.jpg | libjpeg-turbo | 27.9 | 16.6 | WIC bridge, progressive-staged | 138 | 113 | 125 | 204 | 96.8 |
| JPEG camera (MPF preview) 24 MP | jpeg-camera-mpf-24mp.jpg | libjpeg-turbo | 13.3 | 2.08 | WIC bridge, scale | 33.7 | 6.37 | 52.3 | 33.4 | 2.52 |
| JPEG CMYK 4K | jpeg-cmyk-4k.jpg | Skia (libjpeg declined) | 52.8 | 31.1 | WIC bridge, scale | 437 | 428 | 412 | 44.7 | 40.2 |
| JPEG 4:4:4 / gray / ICC 4K | jpeg-444-4k.jpg | libjpeg-turbo | 29.5 | 18.1 | WIC bridge, scale | 66.7 | 33.3 | 20.7 | 20.0 | 16.9 |
| MPO 12 MP | mpo-12mp.mpo | none: file-type glyph | - | - | Skia (first frame) | 153 | 62.0 | 49.9 | 14.3 | 12.7 |
| PNG 8-bit 24 MP | png-24mp.png | Skia | 153 | 135 | native bridge (WIC), Skia fallback | 170 | 133 | 133 | 147 | 118 |
| PNG 8-bit 4K | png-4k.png | Skia | 67.1 | 50.9 | native bridge (WIC), Skia fallback | 86.8 | 51.2 | 51.0 | 55.6 | 44.9 |
| PNG 16-bit 4K | png-16bit-4k.png | Skia | 84.2 | 64.1 | native bridge (WIC), Skia fallback | 111 | 77.5 | 77.7 | 85.9 | 73.7 |
| PNG alpha 4K | png-alpha-4k.png | Skia | 93.9 | 78.3 | native bridge (WIC), Skia fallback | 110 | 76.2 | 75.8 | 83.9 | 69.2 |
| PNG interlaced 4K | png-interlaced-4k.png | Skia | 108 | 93.3 | native bridge (WIC), Skia fallback | 101 | 69.0 | 69.1 | 72.7 | 61.7 |
| PNG palette 4K | png-palette-4k.png | Skia | 31.4 | 14.9 | native bridge (WIC), Skia fallback | 49.6 | 16.3 | 15.4 | 14.4 | 12.4 |
| APNG 720p | apng-720p.png | Skia | 21.6 | 6.36 | native bridge (WIC), Skia fallback | 48.5 | 9.21 | 9.20 | 4.93 | 5.97 |
| WebP lossy 24 MP | webp-24mp.webp | native bridge (libwebp) | 67.0 | 55.3 | libwebp (lossy first view) / Skia | 94.3 | 65.9 | 96.3 | fails | 87.7 |
| WebP lossy 4K | webp-4k.webp | native bridge (libwebp) | 31.6 | 20.5 | libwebp (lossy first view) / Skia | 66.1 | 33.6 | 33.8 | fails | 32.0 |
| WebP lossless 4K | webp-lossless-4k.webp | native bridge (libwebp) | 122 | 107 | libwebp (lossy first view) / Skia | 167 | 137 | 98.2 | fails | 107 |
| WebP animated 720p | webp-anim-720p.webp | Skia (native bridge declined) | 24.5 | 2.89 | libwebp (lossy first view) / Skia | 33.8 | 3.61 | 3.41 | fails | fails |
| GIF 4K | gif-4k.gif | Skia | 32.4 | 16.3 | native bridge (WIC), Skia fallback | 61.9 | 23.3 | 23.4 | 22.9 | 21.2 |
| GIF animated 720p | gif-anim-720p.gif | Skia | 19.2 | 2.12 | native bridge (WIC), Skia fallback | 41.0 | 2.58 | 2.26 | 2.08 | 2.17 |
| BMP 24-bit 24 MP | bmp-24mp.bmp | native bridge (WIC) | 24.2 | 7.72 | native bridge (WIC) | 55.7 | 19.0 | 18.8 | 34.8 | 23.3 |
| BMP 32-bit alpha 4K | bmp-32alpha-4k.bmp | native bridge (WIC) | 21.0 | 4.73 | native bridge (WIC) | 47.2 | 8.00 | 8.00 | 17.2 | 12.1 |
| ICO 256 | ico-256.ico | none: file-type glyph | - | - | native bridge (WIC), Skia fallback | 42.8 | 1.00 | 0.84 | fails | fails |
| CUR 256 | cur-256.cur | none: file-type glyph | - | - | native bridge (WIC), Skia fallback | 40.3 | 1.19 | 0.90 | fails | fails |
| TIFF LZW strips 24 MP | tiff-lzw-24mp.tif | native bridge (WIC) | 50.5 | 33.7 | native bridge (WIC) | 76.6 | 41.8 | 39.2 | 175 | 173 |
| TIFF deflate strips 24 MP | tiff-deflate-24mp.tif | native bridge (WIC) | 49.2 | 29.2 | native bridge (WIC) | 76.5 | 38.1 | 39.3 | 170 | 143 |
| TIFF uncompressed 24 MP | tiff-raw-24mp.tif | native bridge (WIC) | 28.8 | 8.51 | native bridge (WIC) | 53.8 | 18.2 | 18.4 | 54.9 | 30.2 |
| TIFF tiled deflate 24 MP | tiff-tiled-deflate-24mp.tif | native bridge (WIC) | 214 | 185 | native bridge (WIC) | 229 | 200 | 208 | 960 | 216 |
| TIFF pyramid (SubIFD) 24 MP | tiff-pyramid-deflate-24mp.tif | native bridge (WIC) | 232 | 189 | native bridge (WIC) | 231 | 205 | 208 | 960 | 214 |
| TIFF BigTIFF 24 MP | tiff-bigtiff-deflate-24mp.tif | Skia (native bridge declined) | - | - | native bridge (WIC) | **fails** | **fails** | **fails** | fails | fails |
| TIFF 16-bit 4K | tiff-16bit-deflate-4k.tif | native bridge (WIC) | 44.9 | 23.9 | native bridge (WIC) | 68.8 | 28.6 | 29.3 | 142 | 126 |
| TIFF float32 4K | tiff-float32-4k.tif | native bridge (WIC) | 49.4 | 27.3 | native bridge (WIC) | 64.9 | 32.2 | 31.2 | 154 | 135 |
| TIFF CMYK 4K | tiff-cmyk-4k.tif | native bridge (WIC) | 86.4 | 64.9 | native bridge (WIC) | 103 | 68.5 | 68.9 | 374 | 360 |
| TIFF JPEG-in-TIFF 4K | tiff-jpeg-4k.tif | native bridge (WIC) | 34.3 | 14.5 | native bridge (WIC) | 53.7 | 17.5 | 18.1 | 72.4 | 70.3 |
| HEIC 12 MP | heic-12mp.heic | none: file-type glyph | - | - | codec plugin (glidecodecs.dll) | 193 | 123 | 123 | fails | 114 |
| AVIF 12 MP | avif-12mp.avif | none: file-type glyph | - | - | codec plugin (glidecodecs.dll) | 138 | 64.7 | 64.1 | fails | 58.9 |
| JPEG XL lossy 12 MP | jxl-12mp.jxl | none: file-type glyph | - | - | codec plugin (glidecodecs.dll) | 128 | 67.6 | 67.3 | fails | 60.6 |
| JPEG XL lossless 4K | jxl-lossless-4k.jxl | none: file-type glyph | - | - | codec plugin (glidecodecs.dll) | 168 | 103 | 99.2 | fails | 108 |
| JPEG 2000 4K | jp2-4k.jp2 | none: file-type glyph | - | - | codec plugin (glidecodecs.dll) | 250 | 144 | 137 | fails | 9.85 |
| JPEG XR 12 MP | jxr-12mp.jxr | native bridge (WIC) | 22.0 | 3.83 | WIC bridge, scale | 225 | 194 | 225 | 205 | 3.19 |
| RAW DNG (1600 px preview) | raw-dng-1600-preview-24mp.dng | none: file-type glyph | - | - | managed RAW embedded-JPEG preview | 39.2 | 10.7 | 10.7 | fails | 17.8 |
| RAW NEF (full-size preview) | raw-nef-fullsize-preview-24mp.nef | none: file-type glyph | - | - | managed RAW embedded-JPEG preview | 109 | 76.4 | 168 | fails | 51.3 |
| RAW ARW (1620 px preview) | raw-arw-1620-preview-24mp.arw | none: file-type glyph | - | - | managed RAW embedded-JPEG preview | 41.5 | 11.1 | 12.4 | fails | 18.7 |
| RAW RAF (full-size preview) | raw-raf-fullsize-preview-24mp.raf | none: file-type glyph | - | - | managed RAW embedded-JPEG preview | 96.0 | 62.8 | 117 | fails | 53.1 |
| RAW CR3 (1620 px preview) | raw-cr3-1620-preview-24mp.cr3 | none: file-type glyph | - | - | managed RAW embedded-JPEG preview | 42.2 | 10.0 | 11.4 | fails | 19.8 |
| PSD RLE 4K | psd-rle-4k.psd | none: file-type glyph | - | - | managed raster decoder | 98.1 | 63.0 | 62.4 | fails | 35.6 |
| PSD ZIP+prediction 4K | psd-zip-pred-4k.psd | none: file-type glyph | - | - | managed raster decoder | 159 | 105 | 104 | fails | 134 |
| PSD layered 4K | psd-layers-rle-4k.psd | none: file-type glyph | - | - | managed raster decoder | 109 | 72.5 | 73.1 | fails | 47.1 |
| EXR ZIP half 4K | exr-zip-half-4k.exr | none: file-type glyph | - | - | managed OpenEXR | 88.0 | 46.5 | 44.7 | fails | 65.4 |
| EXR ZIP float 4K | exr-zip-float-4k.exr | none: file-type glyph | - | - | managed OpenEXR | 122 | 73.1 | 72.6 | fails | 104 |
| HDR (Radiance) 4K | hdr-4k.hdr | none: file-type glyph | - | - | managed raster decoder | 154 | 69.1 | 70.5 | fails | 275 |
| PFM 4K | pfm-4k.pfm | none: file-type glyph | - | - | managed raster decoder | 89.6 | 47.8 | 47.8 | fails | 144 |
| SVG simple | svg-simple.svg | none: file-type glyph | - | - | Svg.Skia (built-in) | 102 | 3.18 | 2.86 | fails | 4.53 |
| SVG 5000 elements | svg-5000.svg | none: file-type glyph | - | - | Svg.Skia (built-in) | 351 | 188 | 195 | fails | 24.9 |
| SVGZ 5000 elements | svgz-5000.svgz | none: file-type glyph | - | - | Svg.Skia (built-in) | 369 | 170 | 176 | fails | 25.4 |
| SVG with embedded PNG | svg-embedded-png.svg | none: file-type glyph | - | - | Svg.Skia (built-in) | 188 | 62.5 | 62.7 | fails | 779 |
| DDS DXT1 4K | dds-dxt1-4k.dds | none: file-type glyph | - | - | native bridge (WIC) | 60.4 | 20.3 | 13.4 | 8.90 | 6.30 |
| DDS DXT5 4K | dds-dxt5-4k.dds | none: file-type glyph | - | - | native bridge (WIC) | 77.3 | 26.6 | 17.0 | 11.7 | 10.8 |
| DDS RGBA 4K | dds-rgba-4k.dds | none: file-type glyph | - | - | managed raster decoder (uncompressed DDS) | 49.2 | 22.2 | 23.2 | fails | fails |
| KTX 1 RGBA8 4K | ktx1-rgba8-4k.ktx | none: file-type glyph | - | - | managed texture | 57.3 | 23.6 | 22.8 | fails | 40.1 |
| TGA 4K | tga-4k.tga | none: file-type glyph | - | - | managed raster decoder | 59.2 | 24.1 | 23.5 | fails | 53.9 |
| PCX 4K | pcx-4k.pcx | none: file-type glyph | - | - | managed raster decoder | 79.4 | 48.3 | 42.0 | fails | 38.4 |
| PPM 4K | ppm-4k.ppm | none: file-type glyph | - | - | managed raster decoder | 58.6 | 26.7 | 27.0 | fails | 33.0 |
| QOI 4K | qoi-4k.qoi | none: file-type glyph | - | - | managed raster decoder | 64.3 | 37.5 | 37.8 | fails | 37.8 |
| SGI 4K | sgi-4k.sgi | none: file-type glyph | - | - | managed raster decoder | 57.8 | 27.8 | 27.7 | fails | 25.9 |
| FITS 2K | fits-int16-2k.fits | none: file-type glyph | - | - | managed FITS/NIfTI | 54.1 | 25.2 | 24.3 | fails | 32.6 |
| NIfTI 256x256x64 | nifti-256x256x64.nii | none: file-type glyph | - | - | managed FITS/NIfTI | 32.2 | 0.53 | 0.38 | fails | 8.16 |
| OpenRaster 4K | ora-4k.ora | none: file-type glyph | - | - | managed layered (ORA merged PNG) | 85.2 | 55.7 | 55.6 | fails | 63.6 |

## Explorer-tab thumbnail, every fixture (236 px)

Cold = first decode in a fresh process, warm = median of 7. Windows / Glide shell columns are warm / cold, in-process `IThumbnailProvider` at 236 px. Rows with "no" are not thumbnailed by the Explorer tab today; the numbers are what the decoder would cost.

| Fixture | MB | Explorer asks for a thumbnail? | Thumbnail path (Explorer tab) | Cold ms | Warm ms | Cache hit ms | Windows in-box provider warm / cold | Glide shell provider warm / cold | Flags |
|---|---:|---|---|---:|---:|---:|---|---|---|
| jpeg-444-4k.jpg | 2.3 | yes | libjpeg-turbo | 29.5 | 18.1 | 0.22 | 20.0 / 20.7 | 16.9 / 24.2 |  |
| jpeg-base-12mp.jpg | 1.8 | yes | libjpeg-turbo | 24.2 | 13.2 | 0.18 | 14.8 / 14.6 | 12.4 / 19.2 |  |
| jpeg-base-24mp.jpg | 3.6 | yes | libjpeg-turbo | 37.5 | 25.6 | 0.17 | 28.0 / 29.0 | 24.8 / 31.5 |  |
| jpeg-camera-mpf-24mp.jpg | 4.4 | yes | libjpeg-turbo | 13.3 | 2.08 | 0.24 | 33.4 / 36.8 | 2.52 / 9.99 |  |
| jpeg-cmyk-4k.jpg | 4.7 | yes | Skia (libjpeg declined) | 52.8 | 31.1 | 0.16 | 44.7 / 44.8 | 40.2 / 47.7 |  |
| jpeg-exif-thumb-24mp.jpg | 4.3 | yes | libjpeg-turbo | 43.5 | 30.6 | 0.22 | 33.6 / 37.0 | 29.1 / 36.5 |  |
| jpeg-gray-4k.jpg | 1.1 | yes | libjpeg-turbo | 19.4 | 8.06 | 0.13 | 8.60 / 9.15 | 7.82 / 14.8 |  |
| jpeg-icc-4k.jpg | 1.2 | yes | libjpeg-turbo | 19.6 | 8.65 | 0.13 | 10.6 / 14.8 | 8.57 / 15.5 |  |
| jpeg-prog-12mp.jpg | 1.5 | yes | libjpeg-turbo | 19.6 | 8.60 | 0.15 | 102 / 97.9 | 49.1 / 55.4 |  |
| jpeg-prog-24mp.jpg | 3.1 | yes | libjpeg-turbo | 27.9 | 16.6 | 0.21 | 204 / 198 | 96.8 / 103 |  |
| mpo-12mp.mpo | 3.5 | no | none: file-type glyph | 30.1 | 14.2 | 0.14 | 14.3 / 16.6 | 12.7 / 20.0 |  |
| apng-720p.png | 1.9 | yes | Skia | 21.6 | 6.36 | 0.28 | 4.93 / 5.39 | 5.97 / 13.1 | †thumb-cold |
| png-16bit-4k.png | 13.3 | yes | Skia | 84.2 | 64.1 | 0.11 | 85.9 / 86.0 | 73.7 / 76.2 |  |
| png-24mp.png | 25.4 | yes | Skia | 153 | 135 | 0.13 | 147 / 144 | 118 / 123 |  |
| png-4k.png | 10.1 | yes | Skia | 67.1 | 50.9 | 0.13 | 55.6 / 56.3 | 44.9 / 50.4 |  |
| png-alpha-4k.png | 14.3 | yes | Skia | 93.9 | 78.3 | 0.14 | 83.9 / 82.7 | 69.2 / 74.1 | †vsmall-cold |
| png-gray-4k.png | 3.3 | yes | Skia | 42.9 | 25.9 | 0.13 | 22.8 / 23.2 | 22.9 / 27.7 | †vsmall-cold |
| png-interlaced-4k.png | 12.6 | yes | Skia | 108 | 93.3 | 0.14 | 72.7 / 73.1 | 61.7 / 66.1 | †vsmall-cold |
| png-palette-4k.png | 2.1 | yes | Skia | 31.4 | 14.9 | 0.22 | 14.4 / 15.1 | 12.4 / 17.9 | †vsmall-cold |
| png-up-filter-4k.png | 10.6 | yes | Skia | 74.9 | 56.0 | 0.14 | 58.8 / 59.7 | 49.0 / 54.4 |  |
| webp-24mp.webp | 1.3 | yes | native bridge (libwebp) | 67.0 | 55.3 | 0.14 | fails | 87.7 / 88.1 | †thumb-cold |
| webp-4k.webp | 0.5 | yes | native bridge (libwebp) | 31.6 | 20.5 | 0.22 | fails | 32.0 / 32.7 |  |
| webp-alpha-4k.webp | 0.9 | yes | native bridge (libwebp) | 54.6 | 42.2 | 0.10 | fails | 57.6 / 58.9 | †vsmall-cold |
| webp-anim-720p.webp | 0.5 | yes | Skia (native bridge declined) | 24.5 | 2.89 | 0.12 | fails | fails |  |
| webp-lossless-4k.webp | 10.5 | yes | native bridge (libwebp) | 122 | 107 | 0.14 | fails | 107 / 108 | †thumb-cold |
| webp-lossless-alpha-4k.webp | 11.8 | yes | native bridge (libwebp) | 139 | 122 | 0.09 | fails | 121 / 122 | †vsmall-cold |
| gif-4k.gif | 2.7 | yes | Skia | 32.4 | 16.3 | 0.22 | 22.9 / 22.5 | 21.2 / 28.2 |  |
| gif-anim-720p.gif | 3.2 | yes | Skia | 19.2 | 2.12 | 0.15 | 2.08 / 2.54 | 2.17 / 9.18 |  |
| bmp-24mp.bmp | 72.0 | yes | native bridge (WIC) | 24.2 | 7.72 | 0.25 | 34.8 / 34.5 | 23.3 / 31.0 |  |
| bmp-32alpha-4k.bmp | 33.2 | yes | native bridge (WIC) | 21.0 | 4.73 | 0.15 | 17.2 / 18.2 | 12.1 / 18.3 | †glide-cold |
| bmp-4k.bmp | 24.9 | yes | native bridge (WIC) | 19.9 | 4.16 | 0.17 | 18.8 / 17.2 | 8.07 / 14.3 | †glide-cold |
| bmp-8bit-4k.bmp | 8.3 | yes | native bridge (WIC) | 20.1 | 4.00 | 0.16 | 13.8 / 14.0 | 5.21 / 10.5 |  |
| cur-256.cur | 0.1 | no | none: file-type glyph | 16.9 | 1.05 | 0.20 | fails | fails |  |
| ico-256.ico | 0.1 | no | none: file-type glyph | 16.1 | 1.00 | 0.23 | fails | fails | †thumb-cold |
| tiff-16bit-deflate-4k.tif | 27.1 | yes | native bridge (WIC) | 44.9 | 23.9 | 0.24 | 142 / 139 | 126 / 132 |  |
| tiff-bigtiff-deflate-24mp.tif | 52.4 | yes | Skia (native bridge declined) | - | - | - | fails | fails |  |
| tiff-cmyk-4k.tif | 33.2 | yes | native bridge (WIC) | 86.4 | 64.9 | 0.13 | 374 / 369 | 360 / 367 | †thumb-cold |
| tiff-deflate-24mp.tif | 52.4 | yes | native bridge (WIC) | 49.2 | 29.2 | 0.18 | 170 / 178 | 143 / 158 | †view-warm, †view-cold, †vsmall-cold, †inbox-cold, †glide-cold |
| tiff-deflate-4k.tif | 18.8 | yes | native bridge (WIC) | 31.7 | 12.5 | 0.13 | 70.4 / 71.2 | 53.6 / 61.9 | †thumb-cold, †vsmall-cold |
| tiff-float32-4k.tif | 28.0 | yes | native bridge (WIC) | 49.4 | 27.3 | 0.14 | 154 / 154 | 135 / 141 | †vsmall-cold |
| tiff-gray-4k.tif | 5.7 | yes | native bridge (WIC) | 25.8 | 6.25 | 0.17 | 22.8 / 22.5 | 25.5 / 32.5 |  |
| tiff-jpeg-4k.tif | 4.6 | yes | native bridge (WIC) | 34.3 | 14.5 | 0.14 | 72.4 / 72.1 | 70.3 / 78.1 |  |
| tiff-lzw-24mp.tif | 69.2 | yes | native bridge (WIC) | 50.5 | 33.7 | 0.14 | 175 / 176 | 173 / 182 |  |
| tiff-lzw-4k.tif | 25.1 | yes | native bridge (WIC) | 30.6 | 12.1 | 0.24 | 60.9 / 60.7 | 61.0 / 67.4 | †vsmall-cold |
| tiff-multipage-1080p.tif | 32.3 | yes | native bridge (WIC) | 23.1 | 4.49 | 0.13 | 15.1 / 15.8 | 15.3 / 22.6 |  |
| tiff-packbits-4k.tif | 25.1 | yes | native bridge (WIC) | 22.8 | 3.50 | 0.17 | 10.2 / 11.4 | 9.83 / 17.8 |  |
| tiff-pyramid-deflate-24mp.tif | 61.1 | yes | native bridge (WIC) | 232 | 189 | 0.15 | 960 / 951 | 214 / 222 |  |
| tiff-raw-24mp.tif | 72.0 | yes | native bridge (WIC) | 28.8 | 8.51 | 0.26 | 54.9 / 55.0 | 30.2 / 36.5 |  |
| tiff-raw-4k.tif | 24.9 | yes | native bridge (WIC) | 24.7 | 4.35 | 0.13 | 24.8 / 26.3 | 9.72 / 17.7 |  |
| tiff-tiled-deflate-24mp.tif | 45.9 | yes | native bridge (WIC) | 214 | 185 | 0.20 | 960 / 961 | 216 / 221 | †thumb-cold, †inbox-cold |
| tiff-tiled-deflate-4k.tif | 16.6 | yes | native bridge (WIC) | 91.3 | 72.6 | 0.18 | 320 / 338 | 78.7 / 88.8 | †view-warm, †view-cold, †vsmall-cold |
| heic-12mp.heic | 6.3 | no | none: file-type glyph | - | - | - | fails | 114 / 119 |  |
| heic-4k.heic | 4.3 | no | none: file-type glyph | - | - | - | fails | 83.0 / 89.4 |  |
| avif-12mp.avif | 0.3 | no | none: file-type glyph | - | - | - | fails | 58.9 / 63.7 | †thumb-cold |
| avif-4k.avif | 0.2 | no | none: file-type glyph | - | - | - | fails | 42.6 / 46.9 | †vsmall-cold |
| avif-alpha-4k.avif | 0.3 | no | none: file-type glyph | - | - | - | fails | 67.9 / 73.1 |  |
| jxl-12mp.jxl | 1.2 | no | none: file-type glyph | - | - | - | fails | 60.6 / 65.0 |  |
| jxl-4k.jxl | 0.8 | no | none: file-type glyph | - | - | - | fails | 48.0 / 53.1 |  |
| jxl-alpha-4k.jxl | 1.5 | no | none: file-type glyph | - | - | - | fails | 82.8 / 86.9 |  |
| jxl-lossless-4k.jxl | 12.1 | no | none: file-type glyph | - | - | - | fails | 108 / 108 |  |
| j2k-4k.j2k | 2.1 | no | none: file-type glyph | - | - | - | fails | 11.5 / 15.8 |  |
| jp2-4k.jp2 | 2.1 | no | none: file-type glyph | - | - | - | fails | 9.85 / 16.0 |  |
| jp2-lossless-4k.jp2 | 11.6 | no | none: file-type glyph | - | - | - | fails | 16.8 / 22.7 |  |
| jxr-12mp.jxr | 1.0 | yes | native bridge (WIC) | 22.0 | 3.83 | 0.14 | 205 / 195 | 3.19 / 10.9 |  |
| jxr-4k.jxr | 0.7 | yes | native bridge (WIC) | 20.4 | 2.77 | 0.20 | 137 / 135 | 2.06 / 10.1 |  |
| raw-arw-1620-preview-24mp.arw | 24.1 | no | none: file-type glyph | - | - | - | fails | 18.7 / 31.2 |  |
| raw-cr3-1620-preview-24mp.cr3 | 24.1 | no | none: file-type glyph | - | - | - | fails | 19.8 / 31.3 | †thumb-cold |
| raw-dng-1600-preview-24mp.dng | 24.1 | no | none: file-type glyph | - | - | - | fails | 17.8 / 31.0 | †thumb-cold |
| raw-nef-fullsize-preview-24mp.nef | 28.3 | no | none: file-type glyph | - | - | - | fails | 51.3 / 63.1 | †thumb-cold |
| raw-raf-fullsize-preview-24mp.raf | 28.3 | no | none: file-type glyph | - | - | - | fails | 53.1 / 62.8 |  |
| psd-16bit-raw-4k.psd | 49.8 | no | none: file-type glyph | - | - | - | fails | 44.6 / 50.6 | †vsmall-cold |
| psd-layers-rle-4k.psd | 74.9 | no | none: file-type glyph | - | - | - | fails | 47.1 / 52.2 | †thumb-cold, †vsmall-cold |
| psd-raw-4k.psd | 24.9 | no | none: file-type glyph | - | - | - | fails | 34.9 / 40.8 | †thumb-cold |
| psd-rle-4k.psd | 25.1 | no | none: file-type glyph | - | - | - | fails | 35.6 / 40.9 |  |
| psd-zip-pred-4k.psd | 8.3 | no | none: file-type glyph | - | - | - | fails | 134 / 146 | †vsmall-cold |
| exr-none-float-4k.exr | 99.6 | no | none: file-type glyph | - | - | - | fails | 93.2 / 108 | †view-warm |
| exr-rle-half-4k.exr | 35.5 | no | none: file-type glyph | - | - | - | fails | 70.0 / 78.1 |  |
| exr-zip-float-4k.exr | 36.1 | no | none: file-type glyph | - | - | - | fails | 104 / 106 |  |
| exr-zip-half-4k.exr | 17.0 | no | none: file-type glyph | - | - | - | fails | 65.4 / 67.4 |  |
| exr-zips-half-4k.exr | 17.8 | no | none: file-type glyph | - | - | - | fails | 65.6 / 75.3 |  |
| hdr-4k.hdr | 20.8 | no | none: file-type glyph | - | - | - | fails | 275 / 277 | †view-warm |
| pfm-4k.pfm | 99.5 | no | none: file-type glyph | - | - | - | fails | 144 / 154 |  |
| svg-5000.svg | 0.5 | no | none: file-type glyph | - | - | - | fails | 24.9 / 41.7 | †view-warm |
| svg-embedded-png.svg | 4.8 | no | none: file-type glyph | - | - | - | fails | 779 / 797 |  |
| svg-simple.svg | 0.0 | no | none: file-type glyph | - | - | - | fails | 4.53 / 18.2 |  |
| svgz-5000.svgz | 0.1 | no | none: file-type glyph | - | - | - | fails | 25.4 / 44.1 | †view-warm |
| dds-dxt1-4k.dds | 4.1 | no | none: file-type glyph | - | - | - | 8.90 / 9.38 | 6.30 / 11.8 | †view-warm |
| dds-dxt1-mips-4k.dds | 5.5 | no | none: file-type glyph | - | - | - | 8.98 / 9.50 | 6.40 / 11.8 |  |
| dds-dxt5-4k.dds | 8.3 | no | none: file-type glyph | - | - | - | 11.7 / 12.4 | 10.8 / 16.0 |  |
| dds-rgba-4k.dds | 33.2 | no | none: file-type glyph | - | - | - | fails | fails |  |
| ktx1-rgba8-4k.ktx | 33.2 | no | none: file-type glyph | - | - | - | fails | 40.1 / 52.6 |  |
| pcx-4k.pcx | 22.4 | no | none: file-type glyph | - | - | - | fails | 38.4 / 44.3 |  |
| ppm-4k.ppm | 24.9 | no | none: file-type glyph | - | - | - | fails | 33.0 / 37.3 | †vsmall-cold |
| qoi-4k.qoi | 21.2 | no | none: file-type glyph | - | - | - | fails | 37.8 / 42.5 |  |
| sgi-4k.sgi | 24.9 | no | none: file-type glyph | - | - | - | fails | 25.9 / 31.1 | †vsmall-cold |
| tga-4k.tga | 24.9 | no | none: file-type glyph | - | - | - | fails | 53.9 / 59.6 |  |
| tga-rle-4k.tga | 20.3 | no | none: file-type glyph | - | - | - | fails | 32.3 / 36.9 |  |
| fits-int16-2k.fits | 8.4 | no | none: file-type glyph | - | - | - | fails | 32.6 / 38.0 |  |
| nifti-256x256x64.nii | 8.4 | no | none: file-type glyph | - | - | - | fails | 8.16 / 14.1 |  |
| ora-4k.ora | 20.3 | no | none: file-type glyph | - | - | - | fails | 63.6 / 72.9 |  |

## Viewer, every fixture (a full image opened at 2560 x 1369)

First view = what reaches the viewport first. Settled = first frame plus the Fit refinement. Full decode = what 100 % zoom, save and print need (new loader, no cache).

| Fixture | Viewer path | First view cold ms | First view warm ms | Settled ms | Full decode warm ms | First-view px | Full px | Peak private MB (delta) | Flags |
|---|---|---:|---:|---:|---:|---|---|---:|---|
| jpeg-444-4k.jpg | WIC bridge, scale | 66.7 | 33.3 | 33.3 | 20.7 | 3840x2160 | 3840x2160 | 64 |  |
| jpeg-base-12mp.jpg | WIC bridge, scale | 51.6 | 19.6 | 30.8 | 17.5 | 2000x1500 | 4000x3000 | 46 |  |
| jpeg-base-24mp.jpg | WIC bridge, scale | 71.4 | 39.0 | 46.8 | 34.3 | 3000x2000 | 6000x4000 | 92 |  |
| jpeg-camera-mpf-24mp.jpg | WIC bridge, scale | 33.7 | 6.37 | 14.9 | 52.3 | 1080x1620 | 4000x6000 | 184 |  |
| jpeg-cmyk-4k.jpg | WIC bridge, scale | 437 | 428 | 857 | 412 | 2282x1284 | 3840x2160 | 43 |  |
| jpeg-exif-thumb-24mp.jpg | WIC bridge, scale | 72.6 | 39.9 | 46.8 | 53.9 | 1000x1500 | 4000x6000 | 184 |  |
| jpeg-gray-4k.jpg | WIC bridge, scale | 53.9 | 20.8 | 43.5 | 11.8 | 2282x1284 | 3840x2160 | 43 |  |
| jpeg-icc-4k.jpg | WIC bridge, scale | 54.8 | 22.6 | 22.6 | 12.9 | 3840x2160 | 3840x2160 | 32 |  |
| jpeg-prog-12mp.jpg | WIC bridge, progressive-staged | 80.6 | 56.4 | 123 | 60.9 | 2000x1500 | 4000x3000 | 92 |  |
| jpeg-prog-24mp.jpg | WIC bridge, progressive-staged | 138 | 113 | 249 | 125 | 3000x2000 | 6000x4000 | 184 |  |
| mpo-12mp.mpo | Skia (first frame) | 153 | 62.0 | 128 | 49.9 | 1712x1284 | 4000x3000 | 62 |  |
| apng-720p.png | native bridge (WIC), Skia fallback | 48.5 | 9.21 | 9.21 | 9.20 | 1280x720 | 1280x720 | 5 | †thumb-cold |
| png-16bit-4k.png | native bridge (WIC), Skia fallback | 111 | 77.5 | 77.5 | 77.7 | 3840x2160 | 3840x2160 | 32 |  |
| png-24mp.png | native bridge (WIC), Skia fallback | 170 | 133 | 133 | 133 | 6000x4000 | 6000x4000 | 92 |  |
| png-4k.png | native bridge (WIC), Skia fallback | 86.8 | 51.2 | 51.2 | 51.0 | 3840x2160 | 3840x2160 | 32 |  |
| png-alpha-4k.png | native bridge (WIC), Skia fallback | 110 | 76.2 | 76.2 | 75.8 | 3840x2160 | 3840x2160 | 32 | †vsmall-cold |
| png-gray-4k.png | native bridge (WIC), Skia fallback | 60.2 | 27.1 | 27.1 | 26.9 | 3840x2160 | 3840x2160 | 32 | †vsmall-cold |
| png-interlaced-4k.png | native bridge (WIC), Skia fallback | 101 | 69.0 | 69.0 | 69.1 | 3840x2160 | 3840x2160 | 44 | †vsmall-cold |
| png-palette-4k.png | native bridge (WIC), Skia fallback | 49.6 | 16.3 | 16.3 | 15.4 | 3840x2160 | 3840x2160 | 32 | †vsmall-cold |
| png-up-filter-4k.png | native bridge (WIC), Skia fallback | 89.2 | 56.3 | 56.3 | 55.9 | 3840x2160 | 3840x2160 | 32 |  |
| webp-24mp.webp | libwebp (lossy first view) / Skia | 94.3 | 65.9 | 173 | 96.3 | 1926x1284 | 6000x4000 | 102 | †thumb-cold |
| webp-4k.webp | libwebp (lossy first view) / Skia | 66.1 | 33.6 | 80.5 | 33.8 | 2283x1284 | 3840x2160 | 43 |  |
| webp-alpha-4k.webp | libwebp (lossy first view) / Skia | 107 | 64.7 | 134 | 56.9 | 2283x1284 | 3840x2160 | 91 | †vsmall-cold |
| webp-anim-720p.webp | libwebp (lossy first view) / Skia | 33.8 | 3.61 | 3.61 | 3.41 | 1280x720 | 1280x720 | 0 |  |
| webp-lossless-4k.webp | libwebp (lossy first view) / Skia | 167 | 137 | 242 | 98.2 | 2283x1284 | 3840x2160 | 101 | †thumb-cold |
| webp-lossless-alpha-4k.webp | libwebp (lossy first view) / Skia | 162 | 127 | 247 | 105 | 2283x1284 | 3840x2160 | 102 | †vsmall-cold |
| gif-4k.gif | native bridge (WIC), Skia fallback | 61.9 | 23.3 | 23.3 | 23.4 | 3840x2160 | 3840x2160 | 40 |  |
| gif-anim-720p.gif | native bridge (WIC), Skia fallback | 41.0 | 2.58 | 2.58 | 2.26 | 1280x720 | 1280x720 | 0 |  |
| bmp-24mp.bmp | native bridge (WIC) | 55.7 | 19.0 | 19.0 | 18.8 | 6000x4000 | 6000x4000 | 95 |  |
| bmp-32alpha-4k.bmp | native bridge (WIC) | 47.2 | 8.00 | 8.00 | 8.00 | 3840x2160 | 3840x2160 | 34 | †glide-cold |
| bmp-4k.bmp | native bridge (WIC) | 46.3 | 7.71 | 7.71 | 7.43 | 3840x2160 | 3840x2160 | 34 | †glide-cold |
| bmp-8bit-4k.bmp | native bridge (WIC) | 45.1 | 7.37 | 7.37 | 7.21 | 3840x2160 | 3840x2160 | 34 |  |
| cur-256.cur | native bridge (WIC), Skia fallback | 40.3 | 1.19 | 1.19 | 0.90 | 256x256 | 256x256 | 0 |  |
| ico-256.ico | native bridge (WIC), Skia fallback | 42.8 | 1.00 | 1.00 | 0.84 | 256x256 | 256x256 | 0 | †thumb-cold |
| tiff-16bit-deflate-4k.tif | native bridge (WIC) | 68.8 | 28.6 | 28.6 | 29.3 | 3840x2160 | 3840x2160 | 32 |  |
| tiff-bigtiff-deflate-24mp.tif | native bridge (WIC) | - | - | - | - | - | - | - | **fails**: ArgumentException: Unable to load bitmap from provided data |
| tiff-cmyk-4k.tif | native bridge (WIC) | 103 | 68.5 | 68.5 | 68.9 | 3840x2160 | 3840x2160 | 36 | †thumb-cold |
| tiff-deflate-24mp.tif | native bridge (WIC) | 76.5 | 38.1 | 38.1 | 39.3 | 6000x4000 | 6000x4000 | 99 | †view-warm, †view-cold, †vsmall-cold, †inbox-cold, †glide-cold |
| tiff-deflate-4k.tif | native bridge (WIC) | 52.0 | 16.8 | 16.8 | 16.4 | 3840x2160 | 3840x2160 | 32 | †thumb-cold, †vsmall-cold |
| tiff-float32-4k.tif | native bridge (WIC) | 64.9 | 32.2 | 32.2 | 31.2 | 3840x2160 | 3840x2160 | 32 | †vsmall-cold |
| tiff-gray-4k.tif | native bridge (WIC) | 44.3 | 8.78 | 8.78 | 8.45 | 3840x2160 | 3840x2160 | 32 |  |
| tiff-jpeg-4k.tif | native bridge (WIC) | 53.7 | 17.5 | 17.5 | 18.1 | 3840x2160 | 3840x2160 | 35 |  |
| tiff-lzw-24mp.tif | native bridge (WIC) | 76.6 | 41.8 | 41.8 | 39.2 | 6000x4000 | 6000x4000 | 92 |  |
| tiff-lzw-4k.tif | native bridge (WIC) | 49.8 | 14.2 | 14.2 | 13.6 | 3840x2160 | 3840x2160 | 35 | †vsmall-cold |
| tiff-multipage-1080p.tif | native bridge (WIC) | 53.5 | 15.4 | 15.4 | 15.7 | 1920x1080 | 1920x1080 | 8 |  |
| tiff-packbits-4k.tif | native bridge (WIC) | 42.0 | 6.31 | 6.31 | 6.32 | 3840x2160 | 3840x2160 | 32 |  |
| tiff-pyramid-deflate-24mp.tif | native bridge (WIC) | 231 | 205 | 205 | 208 | 6000x4000 | 6000x4000 | 94 |  |
| tiff-raw-24mp.tif | native bridge (WIC) | 53.8 | 18.2 | 18.2 | 18.4 | 6000x4000 | 6000x4000 | 92 |  |
| tiff-raw-4k.tif | native bridge (WIC) | 42.8 | 7.81 | 7.81 | 7.74 | 3840x2160 | 3840x2160 | 32 |  |
| tiff-tiled-deflate-24mp.tif | native bridge (WIC) | 229 | 200 | 200 | 208 | 6000x4000 | 6000x4000 | 92 | †thumb-cold, †inbox-cold |
| tiff-tiled-deflate-4k.tif | native bridge (WIC) | 108 | 77.3 | 77.3 | 76.2 | 3840x2160 | 3840x2160 | 43 | †view-warm, †view-cold, †vsmall-cold |
| heic-12mp.heic | codec plugin (glidecodecs.dll) | 193 | 123 | 241 | 123 | 1712x1284 | 4000x3000 | 100 |  |
| heic-4k.heic | codec plugin (glidecodecs.dll) | 167 | 88.6 | 175 | 85.8 | 2283x1284 | 3840x2160 | 75 |  |
| avif-12mp.avif | codec plugin (glidecodecs.dll) | 138 | 64.7 | 138 | 64.1 | 1712x1284 | 4000x3000 | 104 | †thumb-cold |
| avif-4k.avif | codec plugin (glidecodecs.dll) | 119 | 51.2 | 104 | 48.7 | 2283x1284 | 3840x2160 | 88 | †vsmall-cold |
| avif-alpha-4k.avif | codec plugin (glidecodecs.dll) | 139 | 76.3 | 159 | 73.8 | 2283x1284 | 3840x2160 | 87 |  |
| jxl-12mp.jxl | codec plugin (glidecodecs.dll) | 128 | 67.6 | 143 | 67.3 | 1712x1284 | 4000x3000 | 134 |  |
| jxl-4k.jxl | codec plugin (glidecodecs.dll) | 117 | 53.9 | 108 | 51.9 | 2283x1284 | 3840x2160 | 115 |  |
| jxl-alpha-4k.jxl | codec plugin (glidecodecs.dll) | 150 | 89.7 | 177 | 88.0 | 2283x1284 | 3840x2160 | 137 |  |
| jxl-lossless-4k.jxl | codec plugin (glidecodecs.dll) | 168 | 103 | 205 | 99.2 | 2283x1284 | 3840x2160 | 113 |  |
| j2k-4k.j2k | codec plugin (glidecodecs.dll) | 268 | 146 | 291 | 138 | 2283x1284 | 3840x2160 | 147 |  |
| jp2-4k.jp2 | codec plugin (glidecodecs.dll) | 250 | 144 | 293 | 137 | 2283x1284 | 3840x2160 | 146 |  |
| jp2-lossless-4k.jp2 | codec plugin (glidecodecs.dll) | 311 | 210 | 421 | 206 | 2283x1284 | 3840x2160 | 156 |  |
| jxr-12mp.jxr | WIC bridge, scale | 225 | 194 | 205 | 225 | 2000x1500 | 4000x3000 | 65 |  |
| jxr-4k.jxr | WIC bridge, scale | 180 | 144 | 144 | 148 | 3840x2160 | 3840x2160 | 64 |  |
| raw-arw-1620-preview-24mp.arw | managed RAW embedded-JPEG preview | 41.5 | 11.1 | 39.2 | 12.4 | 1080x1620 | 1080x1620 | 30 |  |
| raw-cr3-1620-preview-24mp.cr3 | managed RAW embedded-JPEG preview | 42.2 | 10.0 | 38.8 | 11.4 | 1080x1620 | 1080x1620 | 30 | †thumb-cold |
| raw-dng-1600-preview-24mp.dng | managed RAW embedded-JPEG preview | 39.2 | 10.7 | 38.7 | 10.7 | 1067x1600 | 1067x1600 | 29 | †thumb-cold |
| raw-nef-fullsize-preview-24mp.nef | managed RAW embedded-JPEG preview | 109 | 76.4 | 89.8 | 168 | 1600x2400 | 4000x6000 | 341 | †thumb-cold |
| raw-raf-fullsize-preview-24mp.raf | managed RAW embedded-JPEG preview | 96.0 | 62.8 | 74.9 | 117 | 2400x1600 | 6000x4000 | 133 |  |
| psd-16bit-raw-4k.psd | managed raster decoder | 101 | 71.7 | 156 | 70.7 | 2400x1350 | 3840x2160 | 243 | †vsmall-cold |
| psd-layers-rle-4k.psd | managed raster decoder | 109 | 72.5 | 158 | 73.1 | 2400x1350 | 3840x2160 | 210 | †thumb-cold, †vsmall-cold |
| psd-raw-4k.psd | managed raster decoder | 97.3 | 66.0 | 149 | 66.8 | 2400x1350 | 3840x2160 | 211 | †thumb-cold |
| psd-rle-4k.psd | managed raster decoder | 98.1 | 63.0 | 132 | 62.4 | 2400x1350 | 3840x2160 | 210 |  |
| psd-zip-pred-4k.psd | managed raster decoder | 159 | 105 | 218 | 104 | 2400x1350 | 3840x2160 | 273 | †vsmall-cold |
| exr-none-float-4k.exr | managed OpenEXR | 75.6 | 43.8 | 92.8 | 41.7 | 2400x1350 | 3840x2160 | 430 | noisy:viewWarm, noisy:viewFull, †view-warm |
| exr-rle-half-4k.exr | managed OpenEXR | 87.6 | 41.5 | 89.4 | 40.8 | 2400x1350 | 3840x2160 | 362 |  |
| exr-zip-float-4k.exr | managed OpenEXR | 122 | 73.1 | 161 | 72.6 | 2400x1350 | 3840x2160 | 469 |  |
| exr-zip-half-4k.exr | managed OpenEXR | 88.0 | 46.5 | 99.5 | 44.7 | 2400x1350 | 3840x2160 | 485 |  |
| exr-zips-half-4k.exr | managed OpenEXR | 97.5 | 44.1 | 94.9 | 46.9 | 2400x1350 | 3840x2160 | 391 |  |
| hdr-4k.hdr | managed raster decoder | 154 | 69.1 | 157 | 70.5 | 2400x1350 | 3840x2160 | 172 | noisy:viewWarm, †view-warm |
| pfm-4k.pfm | managed raster decoder | 89.6 | 47.8 | 103 | 47.8 | 2400x1350 | 3840x2160 | 268 |  |
| svg-5000.svg | Svg.Skia (built-in) | 351 | 188 | 191 | 195 | 2400x1600 | 3000x2000 | 89 | noisy:viewWarm, noisy:viewFull, †view-warm |
| svg-embedded-png.svg | Svg.Skia (built-in) | 188 | 62.5 | 116 | 62.7 | 2400x1800 | 2000x1500 | 229 |  |
| svg-simple.svg | Svg.Skia (built-in) | 102 | 3.18 | 3.18 | 2.86 | 1200x800 | 1200x800 | 0 |  |
| svgz-5000.svgz | Svg.Skia (built-in) | 369 | 170 | 183 | 176 | 2400x1600 | 3000x2000 | 93 | †view-warm |
| dds-dxt1-4k.dds | native bridge (WIC) | 60.4 | 20.3 | 44.4 | 13.4 | 2283x1284 | 3840x2160 | 32 | †view-warm |
| dds-dxt1-mips-4k.dds | native bridge (WIC) | 68.0 | 19.6 | 43.2 | 12.4 | 2283x1284 | 3840x2160 | 40 |  |
| dds-dxt5-4k.dds | native bridge (WIC) | 77.3 | 26.6 | 48.4 | 17.0 | 2283x1284 | 3840x2160 | 43 |  |
| dds-rgba-4k.dds | managed raster decoder (uncompressed DDS) | 49.2 | 22.2 | 53.3 | 23.2 | 2400x1350 | 3840x2160 | 109 |  |
| ktx1-rgba8-4k.ktx | managed texture | 57.3 | 23.6 | 52.3 | 22.8 | 2400x1350 | 3840x2160 | 96 |  |
| pcx-4k.pcx | managed raster decoder | 79.4 | 48.3 | 101 | 42.0 | 2400x1350 | 3840x2160 | 135 |  |
| ppm-4k.ppm | managed raster decoder | 58.6 | 26.7 | 66.6 | 27.0 | 2400x1350 | 3840x2160 | 140 | †vsmall-cold |
| qoi-4k.qoi | managed raster decoder | 64.3 | 37.5 | 92.1 | 37.8 | 2400x1350 | 3840x2160 | 139 |  |
| sgi-4k.sgi | managed raster decoder | 57.8 | 27.8 | 67.7 | 27.7 | 2400x1350 | 3840x2160 | 107 | †vsmall-cold |
| tga-4k.tga | managed raster decoder | 59.2 | 24.1 | 54.5 | 23.5 | 2400x1350 | 3840x2160 | 32 |  |
| tga-rle-4k.tga | managed raster decoder | 59.0 | 21.7 | 50.1 | 25.4 | 2400x1350 | 3840x2160 | 63 |  |
| fits-int16-2k.fits | managed FITS/NIfTI | 54.1 | 25.2 | 63.6 | 24.3 | 2048x2048 | 2048x2048 | 140 |  |
| nifti-256x256x64.nii | managed FITS/NIfTI | 32.2 | 0.53 | 0.53 | 0.38 | 256x256 | 256x256 | 0 |  |
| ora-4k.ora | managed layered (ORA merged PNG) | 85.2 | 55.7 | 117 | 55.6 | 2400x1350 | 3840x2160 | 114 |  |

## As-if thumbnail for the formats the Explorer tab does not list

The viewer's decoder bounded to 480 px against the Glide shell provider (236 px). The cold column of the viewer decoder includes the one-off provider / codec start-up.

| Fixture | Viewer decoder bounded to 480 px: cold ms | warm ms | Glide shell provider (236 px): warm ms | cold ms | Windows in-box provider: warm ms | Flags |
|---|---:|---:|---:|---:|---:|---|
| mpo-12mp.mpo | 111 | 21.6 | 12.7 | 20.0 | 14.3 |  |
| cur-256.cur | 38.7 | 1.07 | - | - | fails |  |
| ico-256.ico | 40.1 | 0.98 | - | - | fails | †thumb-cold |
| heic-12mp.heic | 178 | 114 | 114 | 119 | fails |  |
| heic-4k.heic | 147 | 84.6 | 83.0 | 89.4 | fails |  |
| avif-12mp.avif | 127 | 60.5 | 58.9 | 63.7 | fails | †thumb-cold |
| avif-4k.avif | 107 | 46.5 | 42.6 | 46.9 | fails | †vsmall-cold |
| avif-alpha-4k.avif | 132 | 68.6 | 67.9 | 73.1 | fails |  |
| jxl-12mp.jxl | 125 | 61.6 | 60.6 | 65.0 | fails |  |
| jxl-4k.jxl | 111 | 50.0 | 48.0 | 53.1 | fails |  |
| jxl-alpha-4k.jxl | 145 | 81.6 | 82.8 | 86.9 | fails |  |
| jxl-lossless-4k.jxl | 162 | 107 | 108 | 108 | fails |  |
| j2k-4k.j2k | 115 | 13.0 | 11.5 | 15.8 | fails |  |
| jp2-4k.jp2 | 113 | 13.4 | 9.85 | 16.0 | fails |  |
| jp2-lossless-4k.jp2 | 119 | 17.1 | 16.8 | 22.7 | fails |  |
| raw-arw-1620-preview-24mp.arw | 32.8 | 3.70 | 18.7 | 31.2 | fails |  |
| raw-cr3-1620-preview-24mp.cr3 | 32.5 | 3.64 | 19.8 | 31.3 | fails | †thumb-cold |
| raw-dng-1600-preview-24mp.dng | 32.0 | 3.55 | 17.8 | 31.0 | fails | †thumb-cold |
| raw-nef-fullsize-preview-24mp.nef | 64.1 | 33.6 | 51.3 | 63.1 | fails | †thumb-cold |
| raw-raf-fullsize-preview-24mp.raf | 62.4 | 33.3 | 53.1 | 62.8 | fails |  |
| psd-16bit-raw-4k.psd | 99.6 | 65.3 | 44.6 | 50.6 | fails | †vsmall-cold |
| psd-layers-rle-4k.psd | 109 | 74.1 | 47.1 | 52.2 | fails | †thumb-cold, †vsmall-cold |
| psd-raw-4k.psd | 93.9 | 64.4 | 34.9 | 40.8 | fails | †thumb-cold |
| psd-rle-4k.psd | 99.0 | 66.0 | 35.6 | 40.9 | fails |  |
| psd-zip-pred-4k.psd | 158 | 122 | 134 | 146 | fails | †vsmall-cold |
| exr-none-float-4k.exr | 73.4 | 50.4 | 93.2 | 108 | fails | †view-warm |
| exr-rle-half-4k.exr | 86.2 | 50.9 | 70.0 | 78.1 | fails |  |
| exr-zip-float-4k.exr | 109 | 75.8 | 104 | 106 | fails |  |
| exr-zip-half-4k.exr | 77.3 | 44.1 | 65.4 | 67.4 | fails |  |
| exr-zips-half-4k.exr | 84.7 | 39.6 | 65.6 | 75.3 | fails |  |
| hdr-4k.hdr | 151 | 65.6 | 275 | 277 | fails | noisy:vsWarm, †view-warm |
| pfm-4k.pfm | 85.2 | 47.4 | 144 | 154 | fails |  |
| svg-5000.svg | 345 | 213 | 24.9 | 41.7 | fails | †view-warm |
| svg-embedded-png.svg | 173 | 66.0 | 779 | 797 | fails |  |
| svg-simple.svg | 93.5 | 1.01 | 4.53 | 18.2 | fails |  |
| svgz-5000.svgz | 323 | 127 | 25.4 | 44.1 | fails | †view-warm |
| dds-dxt1-4k.dds | 53.0 | 10.5 | 6.30 | 11.8 | 8.90 | †view-warm |
| dds-dxt1-mips-4k.dds | 53.9 | 11.0 | 6.40 | 11.8 | 8.98 |  |
| dds-dxt5-4k.dds | 61.1 | 15.6 | 10.8 | 16.0 | 11.7 |  |
| dds-rgba-4k.dds | 46.1 | 20.4 | - | - | fails |  |
| ktx1-rgba8-4k.ktx | 56.5 | 20.7 | 40.1 | 52.6 | fails |  |
| pcx-4k.pcx | 76.3 | 47.5 | 38.4 | 44.3 | fails |  |
| ppm-4k.ppm | 55.5 | 23.1 | 33.0 | 37.3 | fails | †vsmall-cold |
| qoi-4k.qoi | 60.2 | 35.4 | 37.8 | 42.5 | fails |  |
| sgi-4k.sgi | 57.2 | 25.8 | 25.9 | 31.1 | fails | †vsmall-cold |
| tga-4k.tga | 51.7 | 24.9 | 53.9 | 59.6 | fails |  |
| tga-rle-4k.tga | 53.3 | 22.0 | 32.3 | 36.9 | fails |  |
| fits-int16-2k.fits | 55.1 | 24.8 | 32.6 | 38.0 | fails |  |
| nifti-256x256x64.nii | 31.8 | 0.47 | 8.16 | 14.1 | fails |  |
| ora-4k.ora | 81.8 | 51.8 | 63.6 | 72.9 | fails |  |

What the table says: RAW is cheapest through the viewer's managed extractor (3.6 ms with a 1620 px preview, 34 ms with a full-size preview; the shell provider is 5x slower on small previews); SVG is cheapest through Direct2D in the shell provider (25 ms against 213 ms for path-heavy files) but
the opposite for SVG with an embedded raster (779 against 66 ms); PSD is cheaper in the shell provider for raw / RLE files (35 against 64 - 66 ms) and about equal for ZIP (134 against 122 ms); EXR, HDR, PFM, TGA, KTX are cheaper through the viewer decoder, QOI and SGI about equal; HEIC / AVIF / JXL are the same either way (both decode the whole picture, then downscale); JPEG 2000 is already cheap (reduced-resolution decode, 13 - 17 ms).

## Windows' own thumbnails

Warm ms at 236 px, in-box Windows Photo Thumbnail Provider against the Glide Explorer-tab path (libjpeg-turbo, libwebp, native bridge, Skia):

| Format | Windows in-box | Glide Explorer tab | Ratio |
|---|---:|---:|---|
| JPEG baseline 24 MP | 28.0 | 25.6 | 1.1x |
| JPEG progressive 24 MP | 204 | 16.6 | 12x |
| JPEG camera with MPF preview | 33.4 | 2.1 | 16x |
| JPEG CMYK 4K | 44.7 | 31.1 | 1.4x |
| PNG 24 MP / 4K | 147 / 55.6 | 135 / 50.9 | 1.1x |
| PNG interlaced 4K | 72.7 | 93.3 | **0.8x (Windows faster)** |
| PNG 16-bit 4K | 85.9 | 64.1 | 1.3x |
| GIF 4K | 22.9 | 16.3 | 1.4x |
| BMP 24 MP / 4K | 34.8 / 18.8 | 7.7 / 4.2 | 4.5x |
| TIFF LZW 24 MP | 175 | 33.7 | 5.2x |
| TIFF raw 24 MP | 54.9 | 8.5 | 6.5x |
| TIFF tiled / pyramid 24 MP | 960 | 185 / 189 | 5.2x |
| TIFF 16-bit / float32 / CMYK 4K | 142 / 154 / 374 | 23.9 / 27.3 / 64.9 | 5.6 - 5.9x |
| JPEG XR 4K / 12 MP | 137 / 205 | 2.8 / 3.8 | 49x / 54x |
| DDS DXT1 / DXT5 4K | 8.9 / 11.7 | not listed (shell provider 6.3 / 10.8) | similar |
| WebP, HEIC, AVIF, JPEG XL, JPEG 2000, RAW, PSD, EXR, SVG, HDR, TGA, PCX, QOI, ICO, CUR, FITS, ORA | fails | - | Windows has no codec or handler for these on this PC |

Glide's own shell provider (the DLL Windows Explorer uses) is about as fast as the Explorer tab for JPEG, PNG, GIF, but **3 - 5x slower than the Explorer tab for TIFF and BMP** (it has no banded parallel decode: TIFF 24 MP LZW 173 ms against 34 ms; BMP 24 MP 23 against 7.7), and 2 - 4x slower than the viewer's own decoders for HDR (275 against 66 ms), PFM (144 against 47), TGA (54 against 25), KTX (40 against 21) and EXR (65 - 104 against 40 - 76).
The shell provider and the Explorer tab are two implementations of the same job; porting the tab's TIFF / BMP bands and the viewer's HDR / PFM / EXR decoders to the provider would speed up Windows Explorer's own thumbnails for Glide's formats (first request after install, with an empty thumbcache).

## Improvement opportunities, by format

Gain is measured against the numbers above; "estimate" means a bound from pixel counts or from the library's documented behaviour, not a measurement.

| Format | Opportunity | Expected gain | Effort | Risk |
|---|---|---|---|---|
| JPEG baseline | At the floor (libjpeg-turbo 1/8 DCT scale, Huffman-bound; 5.1.7 showed the TurboJPEG API alone takes 22.6 ms for 24 MP). Optional: show the EXIF IFD1 thumbnail (0.2 ms, 160 px) as an instant placeholder for tiles that fit it | perceived latency only: 25 ms -> 0.2 ms for the placeholder; does not cover 152 / 236 px tiles (measured: `jpeg-exif-thumb-24mp` at 152 px still decodes in 30 ms) | S | low |
| JPEG progressive | Thumbnail done (DC-only, 16.6 ms). Viewer: stage the first frame from the first scans | first view 113 -> about 55 ms (estimate) | M | medium |
| JPEG camera (MPF / IFD1) | At the floor (2.1 ms) | - | - | - |
| JPEG CMYK / YCCK | libjpeg-turbo path for first view and thumbnail instead of WIC / Skia | first view 428 -> about 60 ms; thumbnail 31 -> about 10 ms (estimates) | S - M | medium (colour) |
| JPEG 12-bit / lossless | Not measured; make sure the viewer declines cleanly | - | - | - |
| MPO, JFIF, JPE, JIF | Not listed by the Explorer tab: add (14 ms warm) | glyph -> thumbnail | XS | low |
| PNG | Inflate-bound (89.8 of 132 ms at 24 MP, 5.1.7). Route Adam7 PNG thumbnails through WIC (viewer route); libdeflate / streaming row decode estimated at 1.2 - 1.3x; the cache already makes repeat visits free | interlaced thumbnail 93 -> about 70 ms (measured: the viewer route does 69 ms); libdeflate about 1.2x (estimate) | S / M | low |
| WebP lossy | libwebp, 55 ms for 24 MP (multi-threaded loop filter); at the floor for this codec | - | - | - |
| WebP lossless | VP8L is single-threaded in libwebp and has no scaled decode: 107 - 122 ms at 4K. A tiled / parallel decode is not possible; rely on the cache | none worth the effort | - | - |
| WebP animated, GIF, APNG, ICO | 1 - 6 ms; ICO / CUR not listed (1 ms): add | glyph -> thumbnail | XS | low |
| BMP | Done: 4 - 8 ms | - | - | - |
| TIFF strips (LZW, deflate, PackBits, raw, JPEG, 16-bit, float, CMYK) | Done / bounded by codec (12 - 65 ms at 4K). TIFF 16-bit / float / CMYK could use the same banded reduction in 16-bit space | small | - | - |
| TIFF tiled | The 6.3x gap to strips: tile-aligned parallel bands | 185 -> about 30 ms | M | medium |
| TIFF pyramid / SubIFD / reduced pages | Read the smallest level that covers the request | 189 -> about 5 ms (estimate) | M | medium |
| BigTIFF | WIC cannot open it: own reader | opens (today: fails) | M - L | medium |
| HEIC | `thmb` thumbnail item; hardware-independent | 114 -> about 10 ms (estimate) | M | medium |
| AVIF | No reduced decode exists; dav1d is already threaded (`GlideCodecs.cpp` sets codec threads); only the provider start-up (+68 ms cold, about 30 of it common first-decode cost) can be hidden | cold about -35 ms | S | low |
| JPEG XL | 1:8 DC decode for tiles (libjxl progressive detail) | 61 -> about 12 ms (estimate) | M | medium |
| JPEG 2000 | Done for tiles (reduced resolution levels: 13 ms); first view at full size 144 ms is decode-bound; provider start-up about +70 ms cold on top of the common first-decode cost | cold about -70 ms | S | low |
| JPEG XR | Thumbnail done (2.8 ms); first view 144 - 194 ms is decode-bound (WIC) | - | - | - |
| RAW (DNG / NEF / ARW / CR3 / RAF) | Not listed by the Explorer tab (shell provider 18 - 53 ms; viewer extractor 3.6 - 34 ms). Prefer the smallest embedded preview that covers the tile (already how the viewer picks); full-size NEF / RAF full decode peaks at 341 / 133 MB | glyph -> 4 - 34 ms | M (with #1) | low |
| PSD | Row / column subsampling, 1036 thumbnail, parallel ZIP-prediction inverse | see #7 | S - M | low |
| EXR | Parallel block decode | 3 - 4x (estimate) | S - M | low |
| HDR / PFM / TGA / PCX / PPM / QOI / SGI | 22 - 70 ms, mostly row conversion; parallelise the conversion and read only needed rows for bounded requests (QOI is inherently sequential) | about 2x on the big ones (estimate) | S | low |
| SVG | See #4 | 8x tile | S - M | medium |
| DDS / KTX | 10 - 20 ms: fine. Mip chain unused (a mipped DXT1 is 10.5 ms bounded, a flat one 10.5 ms); ignore | - | - | - |
| Everything | Cache: hit 0.1 - 0.3 ms, but every format pays its full cost the first time a folder is visited; a background pre-thumbnailing pass over recent folders on idle would hide it (unmeasured) | first visit -> instant | M | low (CPU while idle) |
| Everything (Explorer) | Visible-first and nearest-first ordering already comes from tile realisation; cost-aware ordering and an overscan prefetch are not in place (#9, unmeasured: needs the UI) | - | S - M | low |

## Not changed

No product code, setting or installed file was touched. While looking for a JPEG XL encoder a `pip download` of a JPEG XL plug-in wheel (4.9 MB from PyPI) was run; it was never installed or executed and was deleted; JPEG XL, OpenEXR and APNG fixtures
come from the `ffmpeg` already on the PC, JPEG XR from the Windows WPF encoder, the rest from Pillow, tifffile, OpenCV and hand-written writers (PSD, DDS mips, KTX, FITS, NIfTI, ORA, PNG Adam7 / 16-bit, RAW containers).

## Reproduce

```
python tools/perf/generate_format_fixtures.py %TEMP%\glide-format-audit\data
dotnet build tools\perf\Glide.PerfBench\Glide.PerfBench.csproj -c Release -p:ArtifactsPath=%TEMP%\glide-format-audit\art
dotnet build tools\perf\Glide.FormatAudit\Glide.FormatAudit.csproj -c Release -p:ArtifactsPath=%TEMP%\glide-format-audit\art
powershell -NoProfile -ExecutionPolicy Bypass -File tools\perf\run-format-audit.ps1 -Data %TEMP%\glide-format-audit\data -Out %TEMP%\glide-format-audit\results -Bin %TEMP%\glide-format-audit\art\bin -GlideShellDll %TEMP%\glide-format-audit\art\bin\Glide.FormatAudit\release\Glide.ShellThumbnail.dll
python tools/perf/aggregate-format-audit.py %TEMP%\glide-format-audit\results %TEMP%\glide-format-audit\data
```

The harness (`tools/perf/Glide.FormatAudit/`, `run-format-audit.ps1`, `aggregate-format-audit.py`) is new and was left uncommitted on purpose: the commit that adds this document contains only the document and `generate_format_fixtures.py`.

`noisy.txt` lists the files to re-run with `run-format-audit.ps1 -Only a.jpg,b.png -Phases ...`. `Glide.FormatAudit registry` prints the suffix registry by tier.

## Status

Items 1 (thumbnails for every format), 4 (SVG: Direct2D tiles, Svg.Skia kept for everything else, idle pre-initialisation), 5 (codec preload) and 9 (cost-aware queue, look-ahead) are implemented in 5.2.0; before / after numbers in `docs/audits/format-thumbnails-5.2.0.md`. Items 2, 3 and 6 are done in a separate worktree (native decoders). Items 7, 8 and 10 are open.
