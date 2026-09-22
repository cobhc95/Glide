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

## Performance

Design rules, all enforced in `ThumbnailCore.cpp`:

1. **Bound the source first.** A 6000×4000 image is never decoded at full size for a 256px thumbnail.
2. **Embedded previews first.** `IWICBitmapFrameDecode::GetThumbnail` is tried before any full decode
   (the big win for RAW/PSD/large TIFF, which carry a usable preview).
3. **Decoder-side downscaling.** `IWICBitmapSourceTransform::GetClosestSize` lets JPEG/JPEG-XR reduce
   during DCT rather than after.
4. **Vectors render at the target size** — SVG is rasterised directly at the requested edge, never at
   intrinsic size and then shrunk.
5. **Hard caps**: 256 MP, 65535 per side, 1 GiB source; the longest side is capped at 2048 regardless
   of what Explorer asks for.
6. **One allocation** for the decoded surface; EXIF orientation is applied on the already-small image.
7. **No synchronous logging in release.** `GLIDE_THUMBNAIL_TRACE=1` (opt-in) writes a TSV trace.

### Measured (provider-direct, 256px, one COM instance per file — the honest per-item cost)

Corpus: 300 generated JPEGs (640×480 → 8000×6000, ~70 MB) plus TGA/SVG/QOI/PPM fixtures.

| metric | value |
|---|---|
| files | 304 |
| median | **1 ms** |
| p95 | **13 ms** |
| mean | 3.85 ms |
| min / max | 0 ms / 19 ms |
| under 20 ms | **100%** |

Reproduce with `tools/thumbnail-benchmark.ps1`.

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
| Custom | an explicit per-extension selection |

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
