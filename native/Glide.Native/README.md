# Glide.Native

**Owns:** only native operations that produce a measured benefit or are required for Windows fidelity.  
**Must not own:** application navigation, settings, tabs or general UI state.

It started with one tiny C ABI function, `GlideProbeImageW` (Windows Imaging Component dimensions/frame count). Today it also exports the bounded and full decoders, the encoder, the Shell-cache preview and the Media Foundation video entry points (playback and decoder-availability checks). C ABI is deliberate: it keeps managed/native coupling obvious and stable for AI agents.

Do not grow this DLL casually. New native functions require a clear performance/platform reason and a matching managed wrapper/diagnostic path.


## 2.5 measured-performance boundary

`GlideDecodeImageBoundedW` and `GlideDecodeImageBoundedExW` request a physical-width/height bounded WIC first frame and uses decoder-native `IWICBitmapSourceTransform` scaling where the codec exposes it (notably JPEG/JPEG-XR). `GlideDecodeShellCachedPreviewW` is cache-only (`SIIGBF_INCACHEONLY`) and must never invoke a thumbnail extractor on the foreground path. `GlideDecodeImageW` / `GlideDecodeShellPreviewW` remain compatibility/fallback exports. Every native allocation returned to managed code is released through `GlideFreeImageBuffer`.


### Progressive JPEG first-frame policy (2.5)

`GlideDecodeImageBoundedExW` accepts the managed colour-first preference. When enabled, WIC is asked for progressive level 2, falling back to 1 then 0 without `GetLevelCount`; when disabled, level 0 is requested directly for absolute first-pixel speed. Both routes still attempt `IWICBitmapSourceTransform` reduced decode before scaler fallback. Full/background refinement does not use the staged low-level selection.


## 5.0.0 additions

`GlideDecodeImageIntoW` decodes a full-resolution, orientation-1 image straight into a caller-supplied bitmap through an allocator callback (no temporary buffer and copy: half the peak memory, 6-34 % faster for JPEG, PNG, TIFF and BMP; rotated images and older DLLs fall back to the buffered path). `GlideGetMaxDecodePixels` reports the 512 MP decode limit. The project builds with Control Flow Guard, CET compatibility and `/sdl` (and `/Qspectre` when the Spectre-mitigated libraries are installed).


## 5.0.4 additions

`GlideEncodeImage2W` is the encoder behind Save, Save as and Save selection: PNG, JPEG, BMP, TIFF, GIF (single frame, optimal 256-colour palette with a transparent entry) and JPEG XR on every Windows, plus HEIF/HEIC, AVIF (HEIF container with AV1) and JPEG XL when a Windows codec extension registers an encoder. It writes to a file or into memory (the PNGs inside Glide's ICO and OpenRaster files), and can carry the source's ICC profile (colour context), EXIF (descriptive IFD0 tags plus the Exif and GPS IFDs, Orientation set to 1, Exif pixel dimensions updated, no thumbnail) and XMP (tiff:Orientation removed) across through the WIC metadata query writer. `GlideProbeEncoderW` reports whether a format is writable on this PC (WIC encoder enumeration, plus a 64 x 64 trial encode for the Store-extension formats); the managed side calls it lazily, never at startup. `GlideEncodeImageExW` / `GlideEncodeImageW` remain as compatibility wrappers.

## 5.1.7 additions

`GlideDecodeThumbnailW` is the Explorer tab's thumbnail decode for everything but PNG, GIF and ICO: JPEG (libjpeg-turbo, as `GlideDecodeJpegThumbnailW`), still WebP (the vendored libwebp 1.5.0 decoder, `GlideWebp.cpp`: threaded, decoder-native `use_scaling`, so no full-size intermediate frame; animated files are declined) and, through WIC, TIFF / BMP of 2 MP and more in parallel row bands with an integer box reduction per band, an embedded thumbnail that already covers the tile, decoder-native size, or a Fant scaler, always finished by an exact area-averaged fit. Nothing throws across the C boundary: the export and every band thread catch. `GlideDecodeImageBoundedW` / `GlideDecodeImageBoundedExW` also use the libwebp reduced decode for a bounded WebP without transparency (the viewer's first view); a declined WebP returns 0 at once (no WIC attempt) and WebP full frames stay with Skia, which measured faster. Numbers: `docs/audits/thumbnail-perf-5.1.7.md`.

Progressive JPEG thumbnails (5.1.7 second pass): `GlideJpeg.cpp` decodes only the DC scan of a progressive file through libjpeg buffered-image mode (`GlideJpegLib.h`, `scale_denom` 8, `JDCT_IFAST`, no fancy upsampling, BGRA straight out of the library) and stops; used when 1/8 of the picture still covers the tile. `GlideJpegProgressiveDcW` exposes it for tests. A truncated file, any warning, a non-YCbCr colour space or a DC scan that does not complete in the first scans returns false and the one-shot TurboJPEG decode runs as before.

## 5.2.0 additions (format speed audit, `docs/audits/format-speed-audit-5.2.0.md`, results `docs/audits/format-speed-5.2.0-results.md`)

No new library: about 50 KB of own code in `Glide.Native.dll` (941 056 to 991 232 bytes) and 16 KB in `glidecodecs.dll`.

**Native TIFF reader** (`GlideTiff.cpp`). WIC decodes a tiled TIFF one tile at a time (6x slower than the same picture in strips), cannot read BigTIFF and ignores the reduced-resolution pyramid. The reader parses classic TIFF and BigTIFF (both byte orders) itself, decodes strips and tiles on up to 8 (thumbnails) / 16 (viewer) threads with its own inflate (RFC 1951, 10 bit root table), LZW and PackBits, the horizontal predictor, and converts 8 / 16 bit gray, gray + alpha, RGB, RGBA and 8 bit CMYK to premultiplied BGRA. It reads the first image's SubIFDs and the reduced pages (NewSubfileType bit 0, same aspect ratio) as pyramid levels. A request takes the smallest level that covers it, then a power-of-two box reduction that still covers it (never across a tile / strip, so every unit is independent). Anything else (JPEG-in-TIFF, palette, bilevel, float, planar, YCbCr, Lab, damaged) is declined and WIC runs as before; plain strip files stay with WIC (measured equal), tiled files, BigTIFF, pyramids and CMYK are the reader's. Entry points: `GlideDecodeImageIntoW` (full frame, tiled / BigTIFF / CMYK), `GlideDecodeImageBoundedW/ExW` and `GlideDecodeImageW(path, longest)` (first view, prefetch), `GlideDecodeThumbnailW` (Explorer tab), `GlideProbeImageW` (BigTIFF size), and for tests and the viewer's first-view gate `GlideTiffPlanW` and `GlideTiffDecodeOwnW`. Every offset and count read from the file is bounds-checked, unit sizes are capped, nothing throws across the C boundary (`NativeTiffReaderTests` runs a corruption fuzz).

**CMYK** (`GlideCmyk.cpp`, `GlideJpeg.cpp`). A CMYK / YCCK JPEG used to go through WIC, whose single-threaded ICC managed CMYK converter cost 340 of the 428 ms of a 4K file. libjpeg-turbo now decodes it (DCT scaled, banded and multi-threaded like every other JPEG) and the colours come from a 17 x 17 x 17 x 17 lattice sampled once per process from Windows' own converter (`IWICFormatConverter`, 83 521 pixels, about 9 ms on the first CMYK file) and interpolated (tetrahedral in C, M, Y, linear in K, packed 21 bit lanes). The result follows WIC's colours with a mean difference of 0.25 levels (largest 8 on fully saturated test colours), so there is no colour change to see. CMYK samples are treated as Adobe style (255 = no ink), which is what WIC does for every CMYK JPEG, with or without an Adobe marker; CMYK TIFFs use natural ink amounts. If the lattice cannot be built the file is declined and WIC decodes it.

**Codec plug-in previews** (`native/Glide.Codecs/GlideCodecs.cpp`, no ABI change: the existing `GlideCodecProvider_DecodePreviewSurface(longestSide)`). HEIC / AVIF: an embedded `thmb` thumbnail item at least as large as the request, with the picture's aspect ratio and no alpha, is decoded instead of the whole picture. JPEG XL: a request of one eighth of the picture or less (5 % slack) is served from libjxl's DC progression step (`JXL_DEC_FRAME_PROGRESSION`, `JxlDecoderFlushImage`), keeping one pixel per 8 x 8 block through the pixel callback; lossless, alpha and non-progressive files decode in full as before. The RGB to BGRA conversion of full HEIC / JPEG XL frames now runs on all cores.
