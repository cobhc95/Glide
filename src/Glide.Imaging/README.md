# Glide.Imaging

**Owns:** the staged first-paint/refinement scheduler, common-format dimension probing, compressed-byte cache, prepared decoded-frame cache, stale-request rejection, direction-aware speculative neighbour preparation, codec backend boundary, the mandatory narrow native WIC TIFF decoder and the optional separate native image-probe boundary.

**Must not own:** navigation ordering, tab/workspace state, window chrome, or Settings UI.

Critical invariants:
- an older decode/refinement never replaces a newer request;
- foreground first-paint, visible-image refinement and speculative neighbour decode use separate lanes;
- cache reuse validates file length + mtime identity;
- purge/cancel epochs prevent obsolete speculation from repopulating purged caches;
- compressed bytes and decoded frames have independent memory budgets;
- `IImageDecoderBackend` is the permanent codec boundary, so future HEIC/AVIF/JXL/RAW/native backends inherit the same scheduler rather than bypassing it.

`ImageHeaderProbe` performs pooled allocation-light probing for JPEG, PNG, GIF, BMP, TIFF, WebP and ICO. Declared TIFF decoding uses `NativeImageDecoder`/WIC for bounded preview and full-resolution refinement; the separate `NativeImageProbe` remains optional for supplementary dimensions/metadata. Large images can use decoder-scaled first frames and automatically refine to full resolution after the configured idle delay.

Bundled decoders run ahead of the Skia/WIC/Shell fallbacks: `SvgDecoder` rasterises SVG/SVGZ with Svg.Skia at the requested resolution, and `BuiltInRasterDecoder` handles TGA, PCX, PNM/PBM/PGM/PPM/PAM, QOI, Radiance HDR/RGBE, WBMP, XBM, XPM and SGI/RGB/RGBA/BW in managed code. Both are total entry points (malformed input returns "cannot decode" rather than throwing) and both participate in dimension probing, path previews and full decode. When no decoder applies, the backend falls back to WIC, then a genuine Windows Shell thumbnail/preview for any recognized suffix, and finally reports the failure — it never crashes or fabricates pixels.
