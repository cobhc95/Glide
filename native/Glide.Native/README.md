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
