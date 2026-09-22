# Glide format coverage and IrfanView comparison

Glide recognises **196 filename suffixes** for folder navigation, file pickers and Explorer routing
(`ImageFormatRegistry`). Recognition is deliberately broader than guaranteed decoding: a suffix is
resolved through the first available route below, and a missing decoder degrades gracefully instead of
crashing. This document records the routes and compares them with IrfanView's format list.

## Decode chain (first match wins)

1. **Built-in vector / raster decoders** (bundled, no optional codec):
   - `SvgDecoder` — SVG and gzip-compressed SVGZ, rasterised with `Svg.Skia` at the requested
     resolution (preview bounded; full capped to 4096 px longest side). Transparency preserved.
   - `BuiltInRasterDecoder` — compact managed decoders for legacy/simple raster formats:
     TGA/TARGA/ICB/VDA/VST, PCX, PNM (PBM/PGM/PPM/PAM), QOI, Radiance HDR/RGBE, WBMP, XBM, XPM, SGI.
2. **Native Windows WIC** (`Glide.Native` → WIC): JPEG aliases, PNG/APNG, BMP/DIB, GIF, TIFF,
   ICO/CUR, JPEG-XR/HDP/WDP, DDS, and any other content whose bytes a WIC codec understands
   (including HEIC/AVIF/RAW when the matching Windows codec extension is installed).
3. **Avalonia/Skia** (`new Bitmap(stream)`): additional raster formats Skia understands — including
   **WebP**, which Windows does not decode through WIC unless the optional *Webp Image Extensions*
   Store package is installed. The Explorer thumbnail provider uses a bundled decode-only **libwebp**
   for the same reason; see `docs/explorer-thumbnails.md`.
4. **Windows Shell thumbnail/preview** (last resort, THUMBNAILONLY — never a generic icon): any
   recognised suffix with a registered Windows thumbnail/preview handler (PDF, EPS/AI, Office, CAD,
   camera RAW, HEIC/AVIF without a WIC codec, etc.). Alpha may be flattened in this route.
5. **Optional modular provider** (`ICodecProvider` ABI): external codecs dropped into `codecs/`.

If none succeed, Glide reports the failure in the title bar and keeps the previous view; it never
crashes, hangs or fabricates pixels.

## Coverage after this release

| Group | Suffixes | Route | Clean-PC decode |
|---|---|---|---|
| Core fast path | .jpg .jpeg .jpe .png .bmp .gif .tif .tiff .webp .ico | WIC/Skia | ✅ |
| JPEG/BMP/PNG/GIF/TIFF aliases | .jfif .jif .jfi .pjpeg .pjpg .dib .rle .apng .btf | WIC/Skia | ✅ |
| JPEG-XR / DDS | .jxr .wdp .hdp .dds | WIC | ✅ |
| Icons/cursors | .cur | WIC | ✅ |
| **Vector** | **.svg .svgz** | **built-in Svg.Skia** | ✅ |
| **Legacy raster** | **.tga .targa .icb .vda .vst .pcx .pnm .ppm .pgm .pbm .pam .qoi .hdr .rgbe .wbmp .xbm .xpm .sgi .rgb .rgba .bw** | **built-in managed decoders** | ✅ |
| JPEG-container stills | .mpo .jps .pns | WIC/Skia (first frame) | ✅ |
| HEIC/HEIF/AVIF | .heic .heif .heics .heifs .hif .avif .avifs | WIC (if Windows HEIF/AV1 extension installed) → Shell | ⚠️ optional codec |
| JPEG XL / JPEG 2000 / EXR | .jxl .jp2 .j2k .j2c .jpc .jpx .jpf .jpm .mj2 .exr | provider / Shell | ⚠️ optional codec |
| Camera RAW | .dng .cr2 .cr3 .crw .nef .nrw .arw .srf .sr2 .raf .orf .ori .rw2 .rwl .pef .ptx .3fr .fff .iiq .cap .eip .mef .mos .mrw .x3f .erf .kdc .dcr .k25 .bay .srw .rwz .gpr .mdc .raw .r3d .ari .cinema .dcs .drf .dsc .r2d .rw1 | WIC RAW extension → Shell | ⚠️ optional codec |
| Layered editors | .psd .psb .pdd .xcf .ora .kra .afphoto .afdesign .afpub .clip .csp .pdn .psp .pspimage .pxr | provider / Shell | ⚠️ optional codec |
| Medical | .dcm .dicom .ima .nii .nii.gz .mha .mhd .nrrd .ndpi .svs .vms .vmu .scn .mrxs .bif .czi .lif .lsm .ome.tif .ome.tiff .vsi | provider | ⚠️ optional codec |
| Documents / CAD | .pdf .eps .epsf .ai .cdr .cmx .cpt .dwg .dxf .skp .emf .wmf .emz .wmz | Shell (if handler installed) | ⚠️ optional codec |
| GPU textures | .ktx .ktx2 .pvr .astc .basis .tex .vtf .wal .spr .dpx .cin | provider / Shell | ⚠️ optional codec |
| Scientific / misc | .fits .fit .fts .fts.gz .ff .hdr.gz .xyze .xwd .cut .mac .img .pic .pict .pct .pict2 .iff .lbm .ilbm .ras .sun | provider / Shell | ⚠️ optional codec |

Legend: ✅ decodes on a clean Windows install; ⚠️ requires an optional Windows Store codec extension,
an installed Shell thumbnail handler, or a dropped-in Glide codec provider.

## IrfanView comparison

IrfanView's own documentation marks most non-core formats as **PlugIn-required** (³) or dependent on
installed Windows codecs (¹). Glide's routes map as follows.

| IrfanView format | IrfanView route | Glide route |
|---|---|---|
| BMP/DIB, GIF, ICO/CUR, JPG/JPEG, PBM/PGM/PPM, PNG, TGA, TIF/TIFF, WMF, EMF | built-in | ✅ built-in/WIC |
| PCX/DCX, QOI, PSP, DDS, RAS/SUN, IFF/LBM, XBM, XPM, WBMP, SGI/RGB, FITS, PIC, GEM-IMG | PlugIn | ✅ built-in (except DCX/PSP/FITS/PIC/GEM-IMG → provider/Shell) |
| SVG, DXF/DWG/HPGL/CGM | PlugIn | ✅ SVG built-in; CAD via Shell |
| HDR, EXR | PlugIn | ✅ HDR built-in; EXR optional |
| HEIC, JXL, JP2/JPC/J2K, JPM, AVIF | PlugIn | ⚠️ optional codec |
| CRW/CR2/CR3, DNG, NEF, ORF, RAF, MRW, DCR, PEF, SRF, RW2, NRW, ARW, X3F | PlugIn | ⚠️ optional codec |
| PSD, PDN, XCF, CPT | PlugIn / built-in | ⚠️ optional codec |
| DCM/ACR/IMA, NII, MrSID, ECW, WSQ | PlugIn | ⚠️ optional codec |
| EPS/PS/PDF/AI, DJVU | PlugIn / Ghostscript | ⚠️ Shell/provider |
| ANI, MNG/JNG, FLI/FLC, FPX, JLS, QTIF, Mac PICT, SWF/FLV | PlugIn / QuickTime | ⚠️ optional codec |
| AIF, AU/SND, AVI/WMV, MP3, MPG, MOV/MP4, MIDI, OGG, WAV, RA | media (installed codecs) | out of scope — Glide is an image viewer, not a media player |
| TTF fonts, TXT | built-in | out of scope |

**Where Glide is now broader than IrfanView:** the built-in legacy raster set (TGA, PCX, PNM/PAM, QOI,
HDR, WBMP, XBM, XPM, SGI) needs no plug-in, and the SVG vector route is built in.

**Where IrfanView is still broader:** its bundled/optional plug-ins cover HEIC/AVIF/JXL, JPEG 2000,
camera RAW, PSD, DICOM, MrSID/ECW, DjVu, PostScript/PDF and several animation formats. Reaching full
parity would mean shipping large third-party codec libraries (libheif, libjxl, LibRaw, OpenJPEG,
Ghostscript/PDFium, OpenEXR, …) and is tracked as an optional-codec roadmap rather than part of the
compact installer.

## Optional codec roadmap

1. HEIC/AVIF/JXL — bundle or auto-detect `libheif`/`libjxl` providers behind the existing
   `ICodecProvider` ABI so a clean PC gets them without the Store extensions.
2. Camera RAW — LibRaw provider.
3. JPEG 2000 / EXR — OpenJPEG / OpenEXR providers.
4. PSD — a read-only flattened-composite decoder (large but self-contained).
5. PDF/EPS — PDFium / Ghostscript route.

Each provider implements `ICodecProvider` and registers through `CodecProviderRuntime`; the core
pipeline, caching and preview/refinement already handle provider results.
