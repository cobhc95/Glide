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
     TGA/TARGA/ICB/VDA/VST, PCX, PNM (PBM/PGM/PPM/PAM), PFM, QOI, Radiance HDR/RGBE (+ gzip'd .hdr.gz),
     WBMP, XBM, XPM, SGI, plus Photoshop PSD/PSB/PDD (flattened composite — raw, RLE, ZIP and
     ZIP-with-prediction at 8/16/32 bit; layers are not re-composited).
   - Bundled **managed (pure C#, zero native size)** decoders, dispatched through the same
     `BuiltInRasterDecoder` entry points and touched only when such a file is opened (no static
     initialisation at startup):
     - `RawPreviewDecoder` — camera RAW: the largest embedded camera JPEG (see *Camera RAW* below).
     - `ExrDecoder` / `ExrPiz` — OpenEXR (see *OpenEXR* below).
     - `ScientificDecoders` — DICOM, NIfTI, FITS, PFM.
     - `LayeredEditorDecoders` — OpenRaster, Krita, GIMP XCF; `PdnDecoder` — Paint.NET (full layers, via a
       data-only MS-NRBF reader).
     - `TextureDecoders` / `TextureBlockDecoders` / `AstcDecoder` — KTX, KTX2, PVR v3, .astc; uncompressed DDS
       (5.0.4: Windows' WIC DDS codec reads only BC1-BC3, so plain 24/32-bit DDS, including the files Glide writes,
       are decoded here and block-compressed DDS continues to WIC).
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

| Group | Suffixes | Route | Clean-PC decode | Save / Save as (5.0.4) |
|---|---|---|---|---|
| Core fast path | .jpg .jpeg .jpe .png .bmp .gif .tif .tiff .webp .ico | WIC/Skia | ✅ | ✅ JPEG, PNG, BMP, GIF (one frame), TIFF via WIC; WebP via the bundled codec module; ICO built-in |
| JPEG/BMP/PNG/GIF/TIFF aliases | .jfif .jif .jfi .pjpeg .pjpg .dib .rle .apng .btf | WIC/Skia | ✅ | ✅ .jfif .jpe .dib (others: save under the main suffix) |
| JPEG-XR / DDS | .jxr .wdp .hdp .dds | WIC | ✅ | ✅ (DDS: uncompressed B8G8R8A8) |
| Icons/cursors | .cur | WIC | ✅ | — |
| **Vector** | **.svg .svgz** | **built-in Svg.Skia** | ✅ | — (Save as rasterises to another format) |
| **Legacy raster + Photoshop** | **.tga .targa .icb .vda .vst .pcx .pnm .ppm .pgm .pbm .pam .pfm .qoi .hdr .rgbe .hdr.gz .wbmp .xbm .xpm .sgi .rgb .rgba .bw .psd .psb .pdd** | **built-in managed decoders** | ✅ | ✅ .tga .pnm .ppm .pgm .pbm .pam .pfm .qoi .hdr .wbmp .xbm .xpm .sgi .rgb .rgba .psd .psb |
| **OpenEXR** | **.exr** | **built-in managed decoder** | ✅ (NONE/RLE/ZIPS/ZIP/PIZ/PXR24/B44/B44A) | ✅ half RGBA, ZIP |
| **Camera RAW (embedded preview)** | **.dng .cr2 .cr3 .crw .nef .nrw .arw .srf .sr2 .raf .orf .ori .rw2 .rwl .pef .ptx .3fr .fff .iiq .cap .eip .mef .mos .mrw .x3f .erf .kdc .dcr .k25 .bay .srw .rwz .gpr .mdc .raw .dcs .drf .dsc .r2d .rw1** | **built-in managed preview extractor** → WIC RAW extension → Shell | ✅ camera JPEG preview | — |
| **Layered editors** | **.xcf .ora .kra .pdn** | **built-in managed decoders** | ✅ | ✅ .ora (not .kra/.xcf/.pdn) |
| **Medical / scientific** | **.dcm .dicom .ima .nii .nii.gz .fits .fit .fts .fts.gz** | **built-in managed decoders** | ✅ | — |
| **GPU textures** | **.ktx .ktx2 .pvr .astc** | **built-in managed decoders** | ✅ (see limits) | ✅ .ktx (KTX 1, RGBA8) |
| JPEG-container stills | .mpo .jps .pns | WIC/Skia (first frame) | ✅ | — |
| **HEIC/HEIF/AVIF** | **.heic .heif .heics .heifs .hif .avif .avifs** | **bundled codec module (glidecodecs.dll)** | ✅ | ⚠️ only with a Windows HEIF/AV1 encoder (probed) |
| **JPEG XL / JPEG 2000** | **.jxl .jp2 .j2k .j2c .jpc .jpx .jpf .jpm .mj2** | **bundled codec module (glidecodecs.dll)** | ✅ (.jpm/.mj2: first image) | ✅ .jp2 .j2k .j2c .jpc (bundled); ⚠️ .jxl only with a Windows JPEG XL encoder |
| Cinema RAW | .r3d .ari .cinema | WIC RAW extension → Shell | ⚠️ optional codec | — |
| Layered editors (other) | .afphoto .afdesign .afpub .clip .csp .psp .pspimage .pxr | provider / Shell | ⚠️ optional codec | — |
| Medical (other) | .mha .mhd .nrrd .ndpi .svs .vms .vmu .scn .mrxs .bif .czi .lif .lsm .ome.tif .ome.tiff .vsi | provider | ⚠️ optional codec | — |
| Documents / CAD | .pdf .eps .epsf .ai .cdr .cmx .cpt .dwg .dxf .skp .emf .wmf .emz .wmz | Shell (if handler installed) | ⚠️ optional codec | — |
| GPU textures (other) | .basis .tex .vtf .wal .spr .dpx .cin | provider / Shell | ⚠️ optional codec | — |
| Scientific / misc (other) | .ff .xyze .xwd .cut .mac .img .pic .pict .pct .pict2 .iff .lbm .ilbm .ras .sun | provider / Shell | ⚠️ optional codec | — |

**Counts:** 196 recognised suffixes; **about 112 decode on a clean PC** — the 10-suffix core fast path,
**16 suffixes through the bundled codec module** (AVIF/HEIF/JPEG XL/JPEG 2000) and **86 bundled built-in suffixes** (SVG/SVGZ, 26 legacy raster + Photoshop, EXR,
40 camera RAW, 4 layered-editor, 9 medical/scientific, 4 GPU texture). These are the
`DecodeEnabled` rows of `CodecCapabilityRegistry`; WIC aliases such as .jfif/.dib/.dds/.jxr also open
on a clean PC but are reported as on-demand rather than pre-verified.

## Writing: Save, Save as and Save selection (5.0.4)

Every save starts from the viewer's full-resolution pixels with the current rotation/flip baked in, is written
to a temp file in the destination folder and then atomically replaces the target, and tells Windows the file
changed (so Explorer refreshes its thumbnail). A multi-page/multi-frame original is never overwritten in place.
The Save as dialog lists exactly the formats this PC can write, common ones first, preselects the source's own
format when it is writable (otherwise PNG), and suggests a name whose extension matches the preselected type.
Whatever is picked, the written format and the file name agree: a typed extension of a writable type selects
that type; otherwise the selected type's extension is appended.

| Tier | Formats (suffixes written) | Writer | Size | Alpha |
|---|---|---|---|---|
| 1 Windows built-in | JPEG (.jpg .jpeg .jpe .jfif), PNG, BMP (.bmp .dib), TIFF (.tif .tiff), GIF (single frame), JPEG XR (.jxr .wdp .hdp) | WIC via `Glide.Native` | 0 KB | PNG/TIFF/JPEG XR yes; GIF 1-bit (transparent palette entry); JPEG/BMP flattened |
| 2 Windows extension | HEIC (.heic .heif .hif), AVIF (.avif), JPEG XL (.jxl) | WIC, offered only when an encoder is registered and a trial encode succeeds (probed lazily on the first Save as, never at startup) | 0 KB | flattened |
| 3-5 Managed | QOI, PPM/PNM, PGM, PBM, PAM, PFM, TGA (RLE, 32-bit), WBMP, XBM, ICO (PNG-compressed), Radiance HDR (RLE), SGI (RLE, .sgi .rgb .rgba), XPM, DDS (uncompressed B8G8R8A8, legacy header), KTX 1 (RGBA8), OpenEXR (half, ZIP), Photoshop PSD/PSB (one layer + composite, RLE), OpenRaster (.ora) | pure C#, `src/Glide.Imaging/Encoders` | in Glide.exe | QOI/PAM/TGA/SGI/ICO/DDS/KTX/EXR/PSD/PSB/ORA yes; XPM 1-bit (alpha < 50 % becomes `None`); the rest flattened |
| 6 Bundled codec module | JPEG 2000 (.jp2, codestream .j2k .j2c .jpc) | OpenJPEG encoder in `codecs\glidecodecs.dll` | see below | yes |
| 7 Bundled codec module | WebP | libwebp encoder in `codecs\glidecodecs.dll` | see below | yes |

**Quality and lossless.** Glide has no quality setting: every lossy format (JPEG, WebP, HEIC, AVIF, JPEG XL,
JPEG XR) uses Glide's long-standing JPEG save quality, 0.92. JPEG 2000 is always written lossless (5/3 reversible
wavelet; the provider also supports a lossy 9/7 mode with quality mapped to a compression ratio, not exposed in the
UI). WebP is lossy at that quality, except that a lossless (VP8L) WebP source stays lossless.

**Transparency.** Formats without alpha (JPEG, BMP, PPM/PGM/PBM, PFM, WBMP, XBM, HDR, HEIC/AVIF/JPEG XL) are
composited over white, the same background Print uses. Before 5.0.4, JPEG and BMP saves dropped alpha instead
(fully transparent areas came out black).

**Metadata.** Save and Save as carry the original's metadata over where the target can hold it; Save selection
copies only the colour profile (a crop is a new picture, but its colours must not shift). In every case the EXIF
Orientation is written as 1 (the rotation is in the pixels), the Exif pixel dimensions are updated, the old EXIF
thumbnail is dropped (it would show the old orientation) and XMP `tiff:Orientation` is removed or set to 1.

| Target | ICC profile | EXIF | XMP | From which sources |
|---|---|---|---|---|
| JPEG, TIFF, JPEG XR, HEIC/AVIF/JPEG XL | ✅ | ✅ IFD0 descriptive tags (description, make, model, resolution, software, date, artist, copyright) + Exif and GPS IFDs | ✅ | ICC: any source WIC can read; EXIF/XMP: sources whose metadata WIC exposes as an IFD / XMP block (JPEG, TIFF, JPEG XR; HEIC with the Windows HEIF extension) |
| PNG | ✅ (`iCCP`) | ✅ (`eXIf`) | ✅ (`iTXt XML:com.adobe.xmp`) | ICC: any WIC-readable source; EXIF/XMP: JPEG, PNG or WebP sources |
| WebP | ✅ (`ICCP`) | ✅ (`EXIF`) | ✅ (`XMP `) | JPEG, PNG or WebP sources |
| everything else | — | — | — | — |

**Size limits** (refused before anything is written, with the limit in the message): JPEG, GIF and JPEG XR
65,535 px a side; WebP 16,383; PSD 30,000 (the message suggests PSB); PSB 300,000; DDS and KTX 16,384 (Direct3D /
common GPU texture limit); TGA, SGI, WBMP 65,535; QOI 400 million pixels. ICO is limited to 256 × 256: larger
images are scaled down to fit with alpha-weighted area averaging rather than refused. XPM is a palette format:
more than 4,096 distinct colours is refused (choose PNG).

**Plain Save** rewrites the original in place only when nothing would be lost; layered PSD/PSB/ORA, HDR/EXR/PFM
(8-bit pixels would replace high dynamic range), ICO (other sizes) and DDS/KTX (mipmaps, block compression) go to
Save as instead, like formats Glide cannot write.

**Not written.** Krita `.kra`: a file Krita opens needs `maindoc.xml` plus each layer in Krita's private tiled
format (LZF-compressed 64 × 64 tiles, version-specific XML), which cannot be validated here without Krita; writing
files Glide reads but Krita might reject would be worse than not offering it, and OpenRaster is the interchange
format Krita itself opens and saves. Also not written: animation (every save is one frame), camera RAW, vector
formats, PCX, XCF, Paint.NET, KTX2/PVR/ASTC, medical/scientific formats.

### Managed decoder details and honest limits

**Camera RAW.** Glide does not develop sensor data; it shows the camera-rendered JPEG every RAW file
embeds (full size on most Canon/Nikon/Pentax/Fujifilm/Panasonic bodies, smaller on some Sony/Olympus
and DNG converters). Containers walked: TIFF IFD chains incl. SubIFDs and EXIF (DNG, CR2, NEF, NRW,
ARW, SRF, SR2, PEF, SRW, ERF, 3FR, …; ORF `IIRO`/`IIRS` and RW2/RWL `IIU` magics, RW2 `JpgFromRaw`),
Canon CR3 (ISO-BMFF: full-size JPEG track, PRVW/THMB, CMT1 orientation), Fujifilm RAF (header JPEG),
Canon CRW (CIFF heap `JpgFromRaw`), Sigma X3F (IMA2/IMAG JPEG sections) and Minolta MRW (MakerNote
preview with the `0x00` SOI quirk). Every candidate's JPEG header is validated (8-bit baseline/
extended/progressive only — lossless DNG/CR2 tiles are rejected) and the largest wins; when the
directories offer nothing ≥ 1024 px the file is scanned for embedded JPEG streams (covers
Olympus/Pentax/Phase One MakerNote previews and unknown containers). Only the chosen JPEG's bytes are
read, and bounded previews use JPEG DCT downscaling. Orientation: the preview's own EXIF orientation,
otherwise IFD0/CMT1 orientation (CRW rotation is not applied). Files without any embedded JPEG
(uncompressed-only or video RAWs) fall through to WIC/Shell and otherwise fail gracefully.

**OpenEXR.** Single-part scanline and tiled images (level 0 of mip/rip maps); NONE, RLE, ZIPS, ZIP,
PIZ, PXR24 and B44/B44A; HALF/FLOAT/UINT channels; R/G/B/A, `layer.R`-style names, luminance/chroma
`Y`+`RY`+`BY` (2x2-subsampled chroma, reconstructed to colour from the file's chromaticities), luminance
`Y` or the first/only channel as grey. Scene-linear values are clamped to [0,1] and encoded with the
sRGB curve (the same transfer is used for PFM and float/BC6H textures); premultiplied alpha is undone,
and an alpha channel that is zero everywhere is ignored. Not supported: DWAA/DWAB, deep and multi-part
files.

**DICOM.** First frame; implicit/explicit VR little endian, explicit big endian and deflated
syntaxes, RLE Lossless, encapsulated 8-bit JPEG (baseline/extended/progressive). Monochrome
8/16/32-bit with rescale slope/intercept, the stored VOI window (otherwise min/max), MONOCHROME1
inversion; RGB and YBR_FULL colour. Not supported: JPEG Lossless, JPEG-LS and JPEG 2000 transfer
syntaxes, PALETTE COLOR, native YBR_*_422. **NIfTI** (.nii/.nii.gz, NIfTI-1 and -2, either byte
order): the middle axial slice of the first volume, min/max normalised (RGB24/RGBA32 in colour), +Y
up; only the header and that slice are read/decompressed. Separate `.hdr/.img` Analyze pairs are not
handled (`.hdr` is Radiance). **FITS** (.fits/.fit/.fts/.fts.gz): the first 2-D image HDU (primary or
IMAGE extension; first plane of a cube), BITPIX 8/16/32/64/-32/-64 with BZERO/BSCALE/BLANK, a
0.5–99.5 percentile linear stretch, bottom-up rows. Tile-compressed (BINTABLE) images are not
supported. **PFM**: colour and grey, both byte orders.

**Layered editors.** OpenRaster and Krita show their stored `mergedimage.png`; an ORA without it is
composited from `stack.xml` (normal blending, opacity, visibility, offsets, nested stacks
approximated as pass-through); a KRA without it shows `preview.png`. **Paint.NET .pdn** (files from
Paint.NET 2.x, 3.x, 4.x and 5.x — 5.x up to at least 5.2 still writes this format) is decoded at full
resolution: the layer list lives in a .NET BinaryFormatter (MS-NRBF) object graph, which Glide never
deserializes — a bounded, read-only record walker reads it as plain data (no type loading or object
construction; caps on record/object counts, string and array lengths, member counts and nesting depth;
cycles are harmless because references stay ids). Visible layers are composited bottom-up with their
opacity and blend mode (Normal, Multiply, Additive, Color Burn, Color Dodge, Reflect, Glow, Overlay,
Difference, Negation, Lighten, Darken, Screen, Xor — matched against Paint.NET's own flattened output);
the pixel data is the chunked (gzip or raw) BGRA "deferred" data after the graph, inflated in parallel.
Older gzip-wrapped files (2.1 beta and earlier, and bare 2.0 files) are read too. A browse-size preview
no larger than the embedded thumbnail (≤ 256 px) is served from that thumbnail, and any parse failure,
damaged pixel data, or a canvas that does not fit the available memory falls back to the thumbnail.
Limits: blend modes Paint.NET may add after 4.x/5.x (unknown enum values) composite as Normal; layer
names and metadata are not shown; the new .PDN format planned for Paint.NET 6.0 is undocumented and
falls back to its thumbnail if it keeps one. **GIMP XCF** (all versions incl. 64-bit-pointer v11+): visible
layers flattened with normal blending, opacity, offsets, layer masks and group visibility/opacity;
uncompressed, RLE and zlib tiles; 8-bit RGB/grey/indexed with or without alpha. Higher bit depths
are not decoded and non-normal blend modes are composited as normal.

**GPU textures.** KTX 1.1, KTX 2.0 (no supercompression or zlib), PVR v3 and `.astc`; the first
face/layer of mip level 0 (a `KTXorientation` of `T=u` / `ru` / `lu` marks bottom-up rows and is flipped
upright). Block formats: BC1–BC5, BC6H (signed/unsigned, tone-mapped), BC7, ETC1,
ETC2 RGB/RGBA/punch-through (incl. T/H/planar modes), EAC R11/RG11, ASTC LDR 2-D (all 14 block
footprints; HDR blocks decode to ASTC's magenta error colour). Uncompressed RGBA8/BGRA8/RGB8/BGR8/
R8/RG8/L8/LA8/A8/565/4444/5551 and RGBA16F/RGBA32F/RGB32F/R16F/R32F. Not supported (fail
gracefully): KTX2 Zstandard supercompression (no zstd in .NET's base library), Basis Universal
(KTX2 BasisLZ/UASTC and `.basis` files — transcoding is out of scope), PVRTC 1/2, 3-D ASTC.

Legend: ✅ decodes on a clean Windows install; ⚠️ requires an optional Windows Store codec extension,
an installed Shell thumbnail handler, or a dropped-in Glide codec provider.

## IrfanView comparison

IrfanView's own documentation marks most non-core formats as **PlugIn-required** (³) or dependent on
installed Windows codecs (¹). Glide's routes map as follows.

| IrfanView format | IrfanView route | Glide route |
|---|---|---|
| BMP/DIB, GIF, ICO/CUR, JPG/JPEG, PBM/PGM/PPM, PNG, TGA, TIF/TIFF, WMF, EMF | built-in | ✅ built-in/WIC |
| PCX/DCX, QOI, PSP, DDS, RAS/SUN, IFF/LBM, XBM, XPM, WBMP, SGI/RGB, FITS, PIC, GEM-IMG | PlugIn | ✅ built-in (except DCX/PSP/RAS/IFF/PIC/GEM-IMG → provider/Shell) |
| SVG, DXF/DWG/HPGL/CGM | PlugIn | ✅ SVG built-in; CAD via Shell |
| HDR, EXR | PlugIn | ✅ built-in |
| HEIC, JXL, JP2/JPC/J2K, JPM, AVIF | PlugIn | ⚠️ optional codec |
| CRW/CR2/CR3, DNG, NEF, ORF, RAF, MRW, DCR, PEF, SRF, RW2, NRW, ARW, X3F | PlugIn | ✅ built-in embedded preview (full RAW develop needs the WIC RAW extension) |
| PSD, PDN, XCF, CPT | PlugIn / built-in | ✅ PSD/PDN/XCF built-in; CPT → Shell |
| DCM/ACR/IMA, NII, MrSID, ECW, WSQ | PlugIn | ✅ DCM/IMA/NII built-in; others ⚠️ optional codec |
| EPS/PS/PDF/AI, DJVU | PlugIn / Ghostscript | ⚠️ Shell/provider |
| ANI, MNG/JNG, FLI/FLC, FPX, JLS, QTIF, Mac PICT, SWF/FLV | PlugIn / QuickTime | ⚠️ optional codec |
| AIF, AU/SND, AVI/WMV, MP3, MPG, MOV/MP4, MIDI, OGG, WAV, RA | media (installed codecs) | out of scope — Glide is an image viewer, not a media player |
| TTF fonts, TXT | built-in | out of scope |

**Where Glide is now broader than IrfanView:** the built-in legacy raster set (TGA, PCX, PNM/PAM, QOI,
HDR, WBMP, XBM, XPM, SGI), the SVG vector route, OpenEXR, camera-RAW previews, DICOM/NIfTI/FITS,
OpenRaster/Krita/XCF and KTX/KTX2/PVR/ASTC GPU textures need no plug-in.

**Where IrfanView is still broader:** its bundled/optional plug-ins cover HEIC/AVIF/JXL, JPEG 2000,
full camera-RAW development, MrSID/ECW, DjVu, PostScript/PDF and several animation formats. Reaching full
parity would mean shipping large third-party codec libraries (libheif, libjxl, LibRaw, OpenJPEG,
Ghostscript/PDFium, OpenEXR, …) and is tracked as an optional-codec roadmap rather than part of the
compact installer.

## Optional codec roadmap

1. HEIC/AVIF/JXL — bundle or auto-detect `libheif`/`libjxl` providers behind the existing
   `ICodecProvider` ABI so a clean PC gets them without the Store extensions.
2. Camera RAW — full sensor development (LibRaw provider); the embedded preview is already built in.
3. JPEG 2000 — OpenJPEG provider. (EXR is built in.)
4. PDF/EPS — PDFium / Ghostscript route.
5. KTX2 Zstandard / Basis Universal transcoding, EXR DWAA/DWAB and multi-part, DICOM JPEG-LS/JPEG 2000.

Each provider implements `ICodecProvider` and registers through `CodecProviderRuntime`; the core
pipeline, caching and preview/refinement already handle provider results.
