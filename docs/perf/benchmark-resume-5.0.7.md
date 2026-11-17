# 5.0.7 decode benchmark: resume note

**Status: completed** on 2026-11-17 (it had been paused and was resumed the same day). The results are in `docs/perf/benchmark-report-5.0.7.md`; the raw data is in `<bench-results>\`.

What was done:

- **Measuring** (about 30 minutes in total):
  - quick tier (groups A and C)
  - targeted re-run of TIFF, BMP and EXIF-rotated JPEG
  - final serial comparison of baseline vs integrated
- **Integrated** into `native/Glide.Native/GlideNative.cpp`:
  - TIFF/BMP parallel-band decoding
  - EXIF orientation applied straight into the bitmap
  - tests to go with both changes
- **Build and checks:** `build.cmd`, all tests and both audits passed.

Two pieces were measured and not shipped:

- **Prototype bridge with bench switches:** `<bench-codecs>\glide-native-tuned\`. It includes the dropped SSSE3 change and the `GLIDE_TUNE_DISABLE` switch.
- **Pre-change bridge:** `<bench-codecs>\glide-native-baseline\` (`Glide.Native.dll` + `GlideNative.cpp.orig`).

Not done, kept for a later round:

- full tier
- libvips "all" bundle and the CLI decoders (djxl, dwebp, avifdec, opj)
- JPEG XL / JPEG 2000 alternatives
- cold-start samples
