# Glide 5.0.4

About 112 of Glide's 196 recognised extensions now open on a clean Windows install, Explorer shows thumbnails for all of them, and Save as writes about 30 formats. Settings from 5.0.x load unchanged. The full list is in [CHANGELOG.md](https://github.com/cobhc95/Glide/blob/main/CHANGELOG.md).

## New formats

- **Opens with nothing extra installed:** AVIF, HEIC / HEIF, JPEG XL and JPEG 2000 (bundled codec module, loaded only when needed); camera RAW from 40 formats (the full-size preview stored in the file); OpenEXR; DICOM, NIfTI, FITS, PFM; OpenRaster, Krita, GIMP XCF and Paint.NET (all layers, opacity and blend modes); GPU textures KTX / KTX2 / PVR / ASTC; ZIP-compressed PSD / PSB; uncompressed DDS.
- **Explorer thumbnails** for every format Glide decodes, drawn by the same decoders as the viewer.
- **Save as** writes JPEG, PNG, BMP, TIFF, GIF, JPEG XR, WebP, JPEG 2000, QOI, TGA, ICO, PNM / PAM / PFM, HDR, SGI, XPM, XBM, WBMP, DDS, KTX, EXR, PSD / PSB and OpenRaster, plus HEIC / AVIF / JPEG XL when Windows has the matching encoder. EXIF, XMP and colour profiles are kept; transparency in formats without alpha becomes white instead of black.

## New features

- **Default viewer:** Glide becomes the default for file types no other app owns; for the rest, Setup (new *Integrate with Windows* option, on by default) and Settings open Windows' Default apps page for Glide.
- **Back up and restore** (Settings > Windows & developer): export settings, hotkeys and gestures, profiles, window positions, recent files and the last session to one `.glidebackup` file, and pick what to import. Shares its tick boxes with Reset Glide.
- **Full screen + Explorer tab:** the title bar stays visible and the Explorer navigation row sits below it (setting, on by default).
- Tooltips show your current key and gesture bindings and hide hints for disabled gestures.
- Settings footer: **Defaults** (settings only) next to a new **Reset Glide** button.
- **Videos** play with the codecs installed in Windows (the optional FFmpeg download is gone); if one is missing, Glide names it, for example VP9, HEVC (H.265), AV1 or Opus audio.

## Fixes

- The **+** new-tab button and tabs now click from their very top pixel in every window state (no more hover flicker or dead clicks).
- Restore defaults and Reset Glide > All settings reset options only; they no longer wipe the schema version, remembered folders, window positions, hotkeys or gestures.
- Saved and rotated images now refresh their Explorer thumbnail.
- Save as: the file name's extension always matches the chosen type.
- Native components no longer need the Visual C++ runtime; optional codec providers load correctly.
- Decoder fixes found by testing 289 real sample files: TGA RLE, SGI orientation, PCX colours, XPM, KTX orientation, EXR (PXR24, B44, luminance-chroma, empty alpha), DICOM RGB JPEG, XCF empty layers, subsampled JPEG 2000, JPEG XL colour profiles, WBMP colours, layer blending of faint edges, and HDR Explorer thumbnails.
- Faster decoding for the new formats; smaller installer for what it contains.

## License

Glide is now free software under **GPL-3.0-or-later**. Setup shows the licence; bundled components keep their own licences (see `THIRD-PARTY-NOTICES.md` and the `licenses` folder).

## Downloads

- `Glide.Setup.exe`: installer (Windows 10 1607 or later, x64)
- `Glide-5.0.4-Portable.zip`: portable build
- `Glide.Setup.sha256`: checksum

SHA256 (Glide.Setup.exe): cb94a981a9b247b24d417778d4c87b7d6f03edf2817eb11e8085e79a6cb3eabc
