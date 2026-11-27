# Glide Explorer thumbnail provider

Glide registers a native Windows Shell thumbnail provider so **Windows Explorer shows real
thumbnails for the formats Glide can open**, instead of a generic icon. It is deliberately separate
from Open With / file associations.

## Architecture

```
Explorer (isolated thumbnail host: dllhost.exe)
      │  IThumbnailProvider + IInitializeWithStream / IInitializeWithItem
      ▼
Glide.ShellThumbnail.dll            native/Glide.ShellThumbnail
      │  COM in-proc server, no .NET, no Avalonia, no Glide UI
      ▼
ThumbnailCore.cpp
      ├─ libwebp           WebP (lossy VP8 / lossless VP8L / alpha) via a vendored, decode-only
      │                    libwebp build; no optional Windows codec required
      ├─ WIC               JPEG/PNG/BMP/GIF/TIFF/ICO/DDS/JPEG-XR
      │                    + embedded thumbnail/preview extraction
      │                    + decoder-native reduced decode (IWICBitmapSourceTransform)
      ├─ Direct2D (D2D1SVG) SVG / SVGZ rasterised straight to the requested size
      ├─ compact raster     TGA, PCX, PNM/PBM/PGM/PPM/PAM, QOI, HDR/RGBE, WBMP, XBM, XPM, SGI, PSD (raw/RLE)
      └─ CodecProviders.cpp lazy, SHA-256-pinned codec libraries from <install>\codecs (ABI v2):
           ├─ glidecodecs.dll   AVIF/AVIFS, HEIC/HEIF/HEICS/HEIFS/HIF, JPEG XL, JPEG 2000 family
           └─ glidemanaged.dll  camera RAW (40 suffixes, embedded JPEG), OpenEXR, DICOM, NIfTI, FITS,
                                PFM, OpenRaster, Krita, GIMP XCF, Paint.NET, KTX/KTX2/PVR/ASTC,
                                ZIP-compressed PSD/PSB; also inflates SVGZ for Direct2D
```

### Codec libraries

Formats Windows cannot thumbnail on a clean PC (a HEIC showed a blank page icon: the in-box *Photo
Thumbnail Provider* is registered for it but needs the optional HEIF Store codec) are decoded through the
same libraries the viewer uses:

- **`codecs\glidecodecs.dll`** — the bundled native codec provider (`codecs/README.md`), reused as is through
  its ABI v2 `GlideCodecProvider_DecodePreviewSurface` export.
- **`codecs\glidemanaged.dll`** — `src/Glide.ThumbnailCodecs`: Glide's managed decoders (`RawPreviewDecoder`,
  `ExrDecoder`, `ScientificDecoders`, `LayeredEditorDecoders`/`PdnDecoder`, `TextureDecoders`, the PSD path of
  `BuiltInRasterDecoder`) compiled from the *same source files* into a ~3.1 MB NativeAOT DLL with no Avalonia and
  no Skia, exporting the same ABI v2. Embedded JPEG/PNG sub-images (RAW previews, ORA/KRA layers, PDN thumbnails,
  DICOM JPEG) are decoded by the provider through WIC (`DecodeEncodedForHost`, registered with
  `GlideManaged_SetHostDecoder`); the viewer keeps its Skia path (`ManagedCodecSupport.Skia.cs`), so its pixels
  are unchanged. It has no `.glidecodec.json`, so `codecs.index.json` never lists it and the viewer's decode
  routing is untouched. Workstation, non-concurrent GC with a 2 GiB heap hard limit; every export catches all
  exceptions.

Safety rules (all in `CodecProviders.cpp`):

1. **Lazy**: a library is loaded only when a file of one of its suffixes is thumbnailed; JPEG/PNG/... never
   touch it. Loaded modules stay pinned for the life of the thumbnail host (a NativeAOT DLL cannot unload).
2. **Pinned hash**: CMake writes the SHA-256 of `codecs\glidecodecs.dll` and `codecs\glidemanaged.dll` into
   `GlideCodecPins.h` at build time (`build.cmd` publishes `glidemanaged.dll` before building the provider). The
   file is opened deny-write/deny-delete, hashed with BCrypt and loaded while that handle is still open, so the
   verified bytes are the mapped bytes. A missing pin, missing file or mismatch disables that library only.
3. **Load path**: absolute path + `LOAD_LIBRARY_SEARCH_DLL_LOAD_DIR | LOAD_LIBRARY_SEARCH_SYSTEM32` — never the
   image's folder, the current directory or PATH.
4. **Spooling**: the ABI decodes from a path but Explorer hands providers a stream, so the bytes (max 256 MiB)
   go to a `FILE_FLAG_DELETE_ON_CLOSE` temp file (`%TEMP%\glide-thumb-<pid>-<tid>-<n>.<ext>`), removed by the
   kernel even if the host dies.
5. **Caps and validation**: the file is probed first (refused above 65535 px per side or 128 MP); the returned
   surface must be BGRA8 premultiplied, at most 32768 px per side / 64 MP, with a consistent stride and size.
6. **Fault containment**: every call into a library runs under `__try/__except` (hardware faults and C++
   exceptions), on top of the libraries' own guards and the existing `try/catch` in `GetThumbnail`.

Order inside `Decode`: SVG/SVGZ → WebP → codec library for its suffixes (before WIC: a TIFF-based RAW would
otherwise show WIC's tiny IFD0 strip) → WIC → compact decoders → `glidemanaged.dll` as the fallback for
PSD/PSB/PDD the compact decoder cannot read. `tests/Glide.Core.Tests/ThumbnailCoverageTests.cs` renders a
synthetic fixture of every format the viewer decodes through the provider and prints the per-extension matrix.

Provider size: 542,720 bytes before, 575,488 after (+32 KiB; still only system imports, no MSVCP/VCRUNTIME).

The provider **never** loads Glide.exe, .NET, Avalonia, settings, tabs, history or plugins, **never**
creates a window, and **never** shows UI. All decoding happens in-process inside Explorer's isolated
thumbnail host, so a malformed file cannot destabilise Explorer.

### Why WebP is bundled

Windows has no in-box WebP **WIC** codec; it arrives only with the optional *Webp Image Extensions*
Store package. On a clean install the in-box "Photo Thumbnail Provider" is still registered for
`.webp`, but it fails (`WINCODEC_ERR_COMPONENTINITIALIZEFAILURE`) because the codec it needs is
absent, leaving Explorer with a generic icon. Glide therefore links a decode-only build of
**libwebp** (see `native/third_party/libwebp/GLIDE_VENDOR.md`) and decodes WebP itself, so WebP
thumbnails work regardless of which optional Store codecs are installed.

### Why native C++ rather than .NET

Every thumbnail activation otherwise pays the full .NET + Avalonia runtime and assembly load. The
native shim starts in microseconds and reuses the same WIC concepts as `Glide.Native`, so the
per-thumbnail cost is dominated by real decode work rather than process/framework startup. The DLL
targets the ABI-compatible `IThumbnailProvider` contract (ABI v2 model, S_OK/S_FALSE unload), and
`DisableProcessIsolation=0` keeps it inside Explorer's isolated host.

## Settings and how they reach the provider

`Settings → Windows & developer → Explorer thumbnails`. *Apply thumbnail settings* saves
`thumbnail.settings.json` (UI state), writes the presets for the provider and (re)registers the
per-user handler set for the mode:

```
SettingsWindow → ThumbnailSettingsStore.Apply
   ├─ ThumbnailProviderConfig.Write → HKCU\Software\Glide\ExplorerThumbnails
   │     Quality        DWORD 0 Fast | 1 Balanced | 2 High
   │     MaxSourceSize  DWORD 0 (Automatic) | 512 | 1024 | 2048
   │     PreferEmbedded DWORD 0 | 1
   └─ WindowsThumbnailRegistration (HKCU CLSID + <ext>\ShellEx\{E357FCCD-…} per mode)

GlideThumbnailProvider::GetThumbnail → LoadConfig (HKLM default, then HKCU) on every request
   → DecodeStreamForThumbnail(options) → ComposeThumbnail(quality)
```

Before 5.0 the provider hard-coded Balanced / embedded on / Automatic and nothing was written for
it, so the three performance controls had no effect. The values are read per request, so a change
applies to the next thumbnail Explorer asks for; thumbnails Windows already cached stay until
*Refresh thumbnails* (or Windows) rebuilds them.

| | Fast (default since 5.2.0) | Balanced | High |
|---|---|---|---|
| Intermediate decode | requested size | requested size | 2× requested (≤ 2048) |
| Resampling (WIC) | bilinear | Fant (area averaging) | high-quality cubic |
| Embedded preview accepted when its long side is | ≥ 50% of the thumbnail (may be enlarged) | ≥ 100% | ≥ 100% |
| Animated GIF with a blank first frame | frame 0 only | first visible frame (≤ 8 frames) | same as Balanced |
| WebP | skips in-loop filter and fancy chroma upsampling | skips fancy upsampling when shrinking ≥ 2× | full quality |

*Maximum source decode size* caps the intermediate decode; when Explorer asks for more (extra large
icons), the capped result is enlarged to the requested size. *Prefer embedded previews* allows the
frame thumbnail (EXIF IFD1, TIFF sub-IFD) and the container preview (RAW codecs) when it is large
enough for the preset; previews are rotated by the photo's EXIF orientation, letterbox bars are
cropped to the photo's aspect ratio, and previews of a clearly different shape are rejected.

*Provider mode* only changes which extensions are registered. *Custom* has no per-format editor: it
keeps the saved selection, else the formats currently routed to Glide for this user, else the
Recommended set (it used to register nothing).

### Status line

The status line reports the provider Explorer actually loads: the per-user CLSID `InprocServer32`
path if present, else the machine-wide one, whether that file exists, and its version resource
(5.0+; older DLLs show *unversioned, pre-5.0*, the wording the status line still uses). It also says when that DLL belongs to a different
Glide copy than the running one, and counts the formats whose effective handler is Glide. The old
line only checked for a DLL next to the running Glide.exe, so a standalone/portable exe or a dev
build showed *Provider: missing* while Explorer used the installed provider.

## Performance

Design rules, all enforced in `ThumbnailCore.cpp`:

1. **Read only what is needed.** The provider keeps Explorer's stream and reads it through a 256 KiB
   block cache; WIC decodes straight from it, so an embedded preview or the first frame of a large
   animated GIF never forces the whole file to be read (or copied across the thumbnail-host
   boundary). Formats that need the whole buffer (SVG, WebP, compact decoders) read it once.
2. **Bound the source first.** A 6000×4000 image is never decoded at full size for a 256px thumbnail.
3. **Embedded previews when they are good enough** for the preset (see the table above).
4. **Decoder-side downscaling.** `IWICBitmapSourceTransform` lets JPEG reduce during the DCT
   (1/2, 1/4, 1/8) to the smallest size at or above the target. For high-entropy JPEGs the Huffman
   decode, not the IDCT, dominates, so the gain is modest.
   **5.1.7: JPEG goes through libjpeg-turbo** (`DecodeJpegTurbo`, the same vendored static library as
   Glide.Native): smallest covering DCT scale, fast integer IDCT and plain chroma upsampling (accurate ones for
   High), then the preset's WIC scaler. Before that, when *Prefer embedded previews* is on, the camera's **MPF
   preview** (CIPA DC-007 large thumbnail, ~1620 × 1080, same aspect ratio within 1 %; gain/depth maps never used)
   is decoded instead when it covers the target (`decoder = "mpf"`). CMYK, 12-bit, lossless or any decoder
   warning fall back to WIC unchanged.
5. **Premultiplied resampling.** Every resample runs on premultiplied BGRA, so transparent pixels
   never bleed dark fringes; the result keeps its transparency (premultiplied, WTSAT_ARGB) so Explorer blends it like its own PNG thumbnails.
6. **Vectors render at the target size** — SVG is rasterised directly at the requested edge.
7. **Hard caps**: 256 MP, 65535 per side, 1 GiB source; the longest side is capped at 2048 regardless
   of what Explorer asks for. Since 5.0 the requested thumbnail size itself is clamped to 2048 too
   (`ClampRequestedEdge`), so no host can make the provider compose a larger canvas. The 256 MP cap is
   deliberately lower than the viewer's 512 MP limit: the provider runs inside Explorer's thumbnail host
   and never needs a full-size frame to make a thumbnail.
8. **Exploit mitigations (5.0)**: built with Control Flow Guard (`/guard:cf`, libwebp included), CET
   shadow-stack compatibility (`/CETCOMPAT`), SDL checks (`/sdl`), ASLR and DEP, plus `/Qspectre` when the
   Spectre-mitigated libraries are installed for the build toolset.
9. **No synchronous logging in release.** `GLIDE_THUMBNAIL_TRACE=1` (opt-in) writes a TSV trace.

### Measured (5.0 development build, headless, in-process, 256 px, median of 7)

`python -I tools/thumbnail-preset-benchmark.py --dll native/Glide.ShellThumbnail/build/Release/Glide.ShellThumbnail.dll`
generates the corpus and drives the provider like Explorer does (`DllGetClassObject` →
`IInitializeWithStream` → `GetThumbnail`) without registering it. *err* is the mean absolute
difference (0–255) from a Lanczos reference; for the EXIF samples the embedded preview is
deliberately tinted, so a high *err* there just means the preview was used.

| sample | before | Fast | Balanced | High |
|---|---|---|---|---|
| 6000×4000 JPEG, 160 px EXIF preview | 4.0 ms (err 33, enlarged preview) | 0.3 ms (preview) | 42.9 ms (err 1.6, real decode) | 44.5 ms (err 0.6) |
| 6000×4000 JPEG, 320 px EXIF preview | 4.8 ms | 0.4 ms | 0.5 ms | 0.6 ms |
| 6000×4000 JPEG, no preview | 44.0 ms | 41.2 ms | 41.5 ms | 43.0 ms |
| 1600×1200 JPEG, orientation 6 | 0.7 ms, **sideways** | 0.7 ms, upright | 0.7 ms, upright | 0.9 ms, upright |
| 8000×6000 PNG | 404 ms | 370 ms | 383 ms | 449 ms |
| 4000×3000 BMP | 22.2 ms | 8.7 ms | 11.4 ms | 29.9 ms |
| 4000×3000 LZW TIFF | 81.2 ms | 33.2 ms | 75.6 ms | 90.5 ms |
| 3000×2000 TGA (compact) | 41.6 ms (err 22.7, aliased) | 37.9 ms (err 22.7) | 41.1 ms (err 3.1) | 48.6 ms (err 0.8) |
| 1200×900 animated GIF, 24 frames | 6.7 ms | 1.8 ms | 2.3 ms | 5.0 ms |
| 1600×1200 animated GIF, 75 MB | 42.3 ms (reads 75 MB) | 3.7 ms | 4.6 ms (reads 2.2 MB) | 8.2 ms |
| GIF whose frame 0 is offset | err 121 (frame stretched) | err 0.2 | err 0.2 | err 0.03 |
| 4000×3000 WebP | 116 ms | 114 ms | 115 ms | 117 ms |

### Measured (5.1.7, camera JPEGs vs Windows' own provider)

`python -I tools/thumbnail-speed-benchmark.py --dll <provider> --preset 1,0,1 [--mpf]` generates 200 camera-style
6000×4000 JPEGs (sensor-noise texture, 7 MB, 160×120 EXIF thumbnail; `--mpf` adds a 1620×1080 MPF preview) and
thumbnails each one in-process through Glide's provider and through Windows' Photo Thumbnail Provider
`{C7657C4A-…}` (CoCreateInstance + IInitializeWithStream; nothing registered or cached). Median per file:

| request | Windows | Glide before | Glide 5.1.7 |
|---|---|---|---|
| 96 px (EXIF thumbnail) | 52–62 ms | 3.6 ms | 0.3 ms |
| 256 px, MPF preview present | 61 ms | 57 ms (WIC reduced) | **4.8 ms** (MPF) |
| 256 px, no MPF preview | 53 ms | 58 ms (WIC reduced) | 47 ms (turbo) |

Without a big enough embedded preview the cost is the main image's Huffman decode (~40 ms for 7 MB, single
thread), which no DCT scale avoids; Explorer runs several thumbnail requests in parallel.

The Explorer *tab* grid (`ThumbnailDecode`) uses Glide.Native's `GlideDecodeJpegThumbnailW` (MPF preview or
DCT-scaled libjpeg-turbo, area-averaged to the tile) and `ImagePerformanceGovernor.ThumbnailDecodeLanes` (half the
logical processors, 2..8). `GLIDE_THUMB_BENCH_DIR=<corpus>` enables `ExplorerThumbnailSpeedBenchmark`
(Glide.Explorer.Tests, headless MainWindow): 21 Large tiles all shown in ~410 ms (was 640–890), ~320 ms with MPF
previews (was 670); reopening the folder ~220 ms (was 450–550), ~90 ms with MPF.

`tests/Glide.Core.Tests/ShellThumbnailProviderTests.cs` loads the freshly built DLL the same way and
asserts the presets resolve to different profiles and pixels, that Fast is not slower than High on a
large image, the decode cap, embedded-preview selection and orientation, GIF placement and partial
reads, and the status-line wording for installed, portable and stale registrations.
`tools/thumbnail-benchmark.ps1` (older, registers the provider per-user) is kept for reference.

## Glide's own Explorer tab thumbnails (in-app, not the shell provider)

Everything above is the Windows Shell provider. The Explorer tab inside Glide decodes its own tiles (`Glide.App/Services/ThumbnailDecode.cs`):

1. **Disk cache** (`Glide.Imaging/ThumbnailDiskCache.cs`, Settings > Window & navigation > "Remember Explorer thumbnails on disk"): one small file per picture path and tile width under `%LOCALAPPDATA%\Glide\thumbnails`, keyed by the picture's size and last-write time, trimmed oldest first to the size limit (default 512 MB). A hit is a single file read (0.1 - 0.3 ms).
2. **JPEG**: libjpeg-turbo DCT-scaled decode, or the camera's MPF preview (`GlideDecodeJpegThumbnailW`).
3. **WebP, TIFF, BMP, JPEG XR**: `GlideDecodeThumbnailW` in `Glide.Native` (libwebp scaled decode; parallel row bands with a box reduction for TIFF / BMP of 2 MP and more; WIC decoder-native size or Fant scaler otherwise).
4. **PNG, GIF, ICO and anything the bridge declines**: Skia's scaled decode behind `SkiaDecodeGuard`.
5. **Every other format the viewer opens (5.2.0)**, routed by suffix in `Services/ThumbnailRoutes.cs`: the JPEG family (`.jfif`, `.jif`, `.mpo`, ... through the same libjpeg-turbo path); camera RAW (the smallest embedded preview that covers the tile), PSD, EXR, HDR, PFM, TGA, PPM, QOI, PCX, SGI, DDS, KTX, FITS, ORA, ... through the bounded managed decoders (`ThumbnailExtendedDecode.DecodeBuiltIn`); HEIC, AVIF, JPEG XL and JPEG 2000 through the codec plug-in's reduced decode; SVG at the tile size (`Glide.Imaging/SvgTileRenderer.cs`: Direct2D through `GlideRenderSvgBuffer` in `Glide.Native` for plain path-heavy documents, Svg.Skia for text, CSS, filters, masks, patterns and images); WIC for what is left. A file that cannot be decoded keeps the file-type glyph. Every result is stored in the disk cache.
6. **Scheduling** (`Services/ThumbnailScheduler.cs`): the decode lanes run a priority queue instead of a first-come gate: visible tiles first, cheapest format and size first (`ThumbnailCostModel`, from the 5.2.0 audit), arrival order inside a cost class, and look-ahead (the two rows after the last prepared tile, only while the disk cache is on) last. A decode that takes longer than 8 s gives its lane up and the tile keeps its glyph; that file is not retried until its size or last-write time changes. The folder listing also warms the codec plug-in (`Glide.Imaging/CodecPreloader.cs`) when it holds HEIC, AVIF, JPEG XL or JPEG 2000.
7. **I/O stage (5.2.0, `Services/ThumbnailPipeline.cs`)**: in front of the decode lanes, 4 to 16 I/O lanes (twice the decode lanes) ask the disk cache, then Windows' own thumbnail cache (`IShellItemImageFactory`, `SIIGBF_INCACHEONLY`; `GLIDE_THUMB_WINCACHE=0` skips it), then read the file whole (JPEG, PNG, GIF, ICO), so a slow first open (on-access scanner, virtual disk) overlaps the decoding of other tiles and a file is opened once. Reads start in on-screen reading order.
8. **Per-tile trace**: start Glide with `--diagnostic-trace` (or `GLIDE_DIAGNOSTIC_TRACE=1`) and every tile logs its stage timings to `Glide-Diagnostic-Latest.txt` in Downloads (`thumbnail.tile`, `thumbnail.applied`, `thumbnail.burst`; file names as a hash). Fields and how to read them: `docs/audits/explorer-thumbnail-io-5.2.0.md`.

Measured before / after: `docs/audits/thumbnail-perf-5.1.7.md` (`tools/perf/run-thumb-bench.ps1`) and `docs/audits/format-thumbnails-5.2.0.md` (`tools/perf/run-format-audit.ps1`, `compare-format-thumbs.py`).

## Failure behaviour

`DecodeForThumbnail` is total: corrupt, truncated, zero-byte, gigantic, malformed-SVG and
changing-during-read inputs return a failed result and Explorer falls back to its normal icon. No
exception crosses the COM boundary (`GetThumbnail` is wrapped in `try/catch`), and allocation
failures return `E_FAIL` rather than throwing.

## Security

- The provider parses untrusted bytes only; it never executes embedded scripts, macros or content.
- SVG is rendered by **Direct2D** (`ID2D1DeviceContext5::CreateSvgDocument`), which does not fetch
  external resources or touch the network — unlike a browser-based renderer.
- No shelling out to external processes.
- Explorer's thumbnail-handler isolation (`DisableProcessIsolation=0`) is never weakened.

## Extension coverage

The registration set is **derived from `ImageFormatRegistry`** (`WindowsThumbnailRegistration`), so a
new Glide format is thumbnail-capable with no provider change. Modes:

| mode | behaviour |
|---|---|
| Recommended | Glide owns the formats it decodes natively, fills gaps where no handler exists, and takes codec-library formats (HEIC/AVIF/JXL/JPEG 2000/RAW/EXR/...) over from Windows' codec-dependent Photo Thumbnail Provider `{C7657C4A-…}` — never from a third-party handler, and only when the library is installed |
| Glide for unsupported formats only | only extensions with no other registered thumbnail handler |
| Glide for all supported formats (default since 5.2.0) | every supported extension, overriding existing handlers. Existing users still on the old defaults (Recommended + Balanced) are moved to Glide for all supported formats + Fast once at startup (`ThumbnailSettingsStore.EnsureMigrationApplied`, keyed on `DefaultsRevision` in thumbnail.settings.json) |
| Custom | keeps the saved selection, else the formats currently routed to Glide, else Recommended |

Extension→handler detection reads `Software\Classes\<ext>\ShellEx\{E357FCCD-…}`.

## Known limitations

- **`<text>` is not rendered by D2D's SVG support.** Shapes, gradients, clip paths and embedded
  raster images are all supported; text is the one D2D SVG gap.
- Formats Glide only *recognises* (PDF, AI/EPS, CDR, DWG, slide scanners, cinema RAW, ...) show a thumbnail
  only when the matching WIC/Shell handler is installed on the machine — the provider returns a clean failure
  otherwise.
- **Compound suffixes** (`.nii.gz`, `.fts.gz`, `.hdr.gz`) never get a Glide thumbnail: Explorer resolves
  handlers by the final suffix only, and claiming `.gz` would take over every gzip archive.
- Windows remains responsible for the thumbnail cache; Glide does not maintain a second database.
  Use **Settings → Windows Integration → Refresh thumbnails** to clear the per-user `thumbcache_*.db`
  files and ask the shell to rebuild.

## Registration surfaces

- **Installer** (machine-wide, `HKLM`): `Glide.exe --register-thumbnails` /
  `--unregister-thumbnails`, invoked from `installer/Glide.iss` `[Code]`.
- **App / portable** (per-user, `HKCU`): `Settings → Windows Integration → Explorer thumbnails`.
  Configuration persists in `thumbnail.settings.json`.
