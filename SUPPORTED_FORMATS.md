# Supported formats

Glide Alpha 0.12107 currently registers **196 extension suffixes**.

Glide uses a layered strategy:

1. **Windows Imaging Component (WIC)** for formats Windows can decode directly.
2. **Compact built-in decoders** for lightweight formats such as PSD composite, TGA, Netpbm, QOI, PCX, HDR/RGBE, farbfeld, DDS, WBMP, PFM and XBM.
3. **Bundled native codec bridge** for WebP, HEIF/HEIC, AVIF, JPEG XL, JPEG 2000 and OpenEXR.
4. **Windows/application preview providers** as a final fallback for many specialist design, RAW, medical, microscopy and scientific formats.

A registered suffix therefore means Glide knows the file family and will attempt the appropriate decode/preview path. It does **not** mean every specialist suffix has a guaranteed internal decoder on every Windows installation.

## Complete registered suffix list

`.jpg`, `.jpeg`, `.jpe`, `.jfif`, `.jif`, `.jfi`, `.pjpeg`, `.pjpg`, `.png`, `.apng`, `.mng`, `.jng`, `.bmp`, `.dib`, `.rle`, `.wbmp`, `.tif`, `.tiff`, `.btf`, `.gif`, `.ico`, `.cur`, `.ani`, `.icns`, `.webp`, `.heic`, `.heif`, `.heics`, `.heifs`, `.hif`, `.avif`, `.avifs`, `.svg`, `.svgz`, `.jxr`, `.wdp`, `.hdp`, `.jp2`, `.j2k`, `.j2c`, `.jpc`, `.jpx`, `.jpf`, `.jpm`, `.mj2`, `.jxl`, `.psd`, `.psb`, `.pdd`, `.xcf`, `.ora`, `.kra`, `.afphoto`, `.afdesign`, `.afpub`, `.clip`, `.csp`, `.pdn`, `.psp`, `.pspimage`, `.pxr`, `.tga`, `.targa`, `.icb`, `.vda`, `.vst`, `.dpx`, `.cin`, `.sgi`, `.rgb`, `.rgba`, `.bw`, `.ras`, `.sun`, `.iff`, `.lbm`, `.ilbm`, `.img`, `.pic`, `.pict`, `.pct`, `.pict2`, `.eps`, `.epsf`, `.ai`, `.pdf`, `.pnm`, `.ppm`, `.pgm`, `.pbm`, `.pam`, `.pfm`, `.pcx`, `.qoi`, `.hdr`, `.rgbe`, `.xyze`, `.exr`, `.dds`, `.ff`, `.fits`, `.fit`, `.fts`, `.fts.gz`, `.hdr.gz`, `.xbm`, `.xpm`, `.xwd`, `.cut`, `.mac`, `.mpo`, `.jps`, `.pns`, `.dng`, `.cr2`, `.cr3`, `.crw`, `.nef`, `.nrw`, `.arw`, `.srf`, `.sr2`, `.raf`, `.orf`, `.ori`, `.rw2`, `.rwl`, `.pef`, `.ptx`, `.3fr`, `.fff`, `.iiq`, `.cap`, `.eip`, `.mef`, `.mos`, `.mrw`, `.x3f`, `.erf`, `.kdc`, `.dcr`, `.k25`, `.bay`, `.srw`, `.rwz`, `.gpr`, `.mdc`, `.raw`, `.r3d`, `.ari`, `.cinema`, `.dcs`, `.drf`, `.dsc`, `.r2d`, `.rw1`, `.dcm`, `.dicom`, `.ima`, `.nii`, `.nii.gz`, `.mha`, `.mhd`, `.nrrd`, `.ndpi`, `.svs`, `.vms`, `.vmu`, `.scn`, `.mrxs`, `.bif`, `.czi`, `.lif`, `.lsm`, `.ome.tif`, `.ome.tiff`, `.vsi`, `.ktx`, `.ktx2`, `.pvr`, `.astc`, `.basis`, `.tex`, `.vtf`, `.wal`, `.spr`, `.cdr`, `.cmx`, `.cpt`, `.emf`, `.wmf`, `.emz`, `.wmz`, `.dwg`, `.dxf`, `.skp`

## Native bundled codec stack

- WebP — libwebp 1.6.0
- HEIF / HEIC / HIF — libheif 1.23.4 + libde265 1.1.1
- AVIF / AVIFS — libheif 1.23.4 + dav1d 1.5.1
- JPEG XL — libjxl 0.12.0
- JPEG 2000 — OpenJPEG 2.5.3
- OpenEXR — TinyEXR 3.2.0 + miniz

## Notes

- Full animated WebP playback is not claimed in this alpha; the native path can provide the composed first frame.
- Generic shell file icons are rejected and are not counted as successful image previews.
- Installed Windows codecs or the owning application's shell preview handler can expand practical support for specialist formats.
- `image_formats.h` is the canonical source registry used by Glide and installer association generation.
