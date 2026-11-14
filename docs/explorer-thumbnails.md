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
      └─ compact raster     TGA, PCX, PNM/PBM/PGM/PPM/PAM, QOI, HDR/RGBE, WBMP, XBM, XPM, SGI
```

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

| | Fast | Balanced (default) | High |
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
5. **Premultiplied resampling.** Every resample runs on premultiplied BGRA, so transparent pixels
   never bleed dark fringes; the result is composited onto white at the end.
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

`tests/Glide.Core.Tests/ShellThumbnailProviderTests.cs` loads the freshly built DLL the same way and
asserts the presets resolve to different profiles and pixels, that Fast is not slower than High on a
large image, the decode cap, embedded-preview selection and orientation, GIF placement and partial
reads, and the status-line wording for installed, portable and stale registrations.
`tools/thumbnail-benchmark.ps1` (older, registers the provider per-user) is kept for reference.

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
| Recommended (default) | Glide owns the formats it decodes natively, and fills gaps where no handler exists |
| Glide for unsupported formats only | only extensions with no other registered thumbnail handler |
| Glide for all supported formats | every supported extension, overriding existing handlers |
| Custom | keeps the saved selection, else the formats currently routed to Glide, else Recommended |

Extension→handler detection reads `Software\Classes\<ext>\ShellEx\{E357FCCD-…}`.

## Known limitations

- **`<text>` is not rendered by D2D's SVG support.** Shapes, gradients, clip paths and embedded
  raster images are all supported; text is the one D2D SVG gap.
- Formats with **no in-box Windows codec and no bundled decoder** (HEIC/AVIF/JXL, RAW, PSD, DICOM,
  PDF/TGA-in-container exotic variants) show a thumbnail only when the matching WIC/Shell handler is
  installed on the machine — the provider returns a clean failure otherwise.
- Windows remains responsible for the thumbnail cache; Glide does not maintain a second database.
  Use **Settings → Windows Integration → Refresh thumbnails** to clear the per-user `thumbcache_*.db`
  files and ask the shell to rebuild.

## Registration surfaces

- **Installer** (machine-wide, `HKLM`): `Glide.exe --register-thumbnails` /
  `--unregister-thumbnails`, invoked from `installer/Glide.iss` `[Code]`.
- **App / portable** (per-user, `HKCU`): `Settings → Windows Integration → Explorer thumbnails`.
  Configuration persists in `thumbnail.settings.json`.
