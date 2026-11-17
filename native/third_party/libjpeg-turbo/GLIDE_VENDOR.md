# libjpeg-turbo 3.2.1 (vendored binary, Glide 5.0.7)

Used by `native/Glide.Native` (GlideJpeg.cpp) for the JPEG first view (DCT-scaled decode of baseline JPEGs)
and full-resolution JPEG decode. WIC remains the fallback for everything this path declines (CMYK/YCCK,
12-bit, lossless, arithmetic-coded, unknown subsampling, any decoder warning or error).

- Source: official release `libjpeg-turbo-3.2.1-vc-x64.exe`
  (https://github.com/libjpeg-turbo/libjpeg-turbo/releases/download/3.2.1/libjpeg-turbo-3.2.1-vc-x64.exe,
  sha256 `1f05bc3ed1585e6b12a8b0c24c8c5f07b1f6d90d01b4b709b331bedc9d6e5838`), unpacked with 7-Zip, never executed.
- `lib/x64/turbojpeg-static.lib`: the package's static TurboJPEG library (SIMD, static CRT), linked into
  Glide.Native.dll. sha256 recorded in `SHA256SUMS`.
- `include/`: `turbojpeg.h` from the same package.
- Licence: IJG License, Modified (3-clause) BSD License and zlib License; full text in `LICENSE.md` and
  `README.ijg` (also reproduced in Glide's THIRD-PARTY-NOTICES).

Update: replace the four files from a newer official vc-x64 release, refresh `SHA256SUMS`, rebuild, and rerun
the JPEG parity tests (`JpegTurboParityTests`).
