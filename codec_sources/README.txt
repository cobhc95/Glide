Glide native codec sources (offline, decoder-focused)
======================================================
These pinned source archives are the minimum conservative source set bundled for
Glide's native codec bridge. They are used only at build time; Glide performs
no codec download at build time or runtime.

Main decoders:
- libwebp 1.6.0 (WebP)
- libheif 1.23.4 + libde265 1.1.1 (HEIF/HEIC/HIF, including ISO uncompressed images)
- libheif 1.23.4 + dav1d 1.5.1 (AVIF)
- libjxl 0.12.0 + pinned Highway/Brotli/minimal skcms (JPEG XL)
- OpenJPEG 2.5.3 (JPEG 2000)
- TinyEXR 3.2.0 minimal source + miniz (OpenEXR)

Intentionally not bundled: ImageMagick, LCMS2, libpng, zlib, libjpeg-turbo,
codec command-line utilities, examples, test images, benchmarks, or large
upstream documentation/test trees that Glide does not need for decoding.

Reproducibility / Glide Alpha 0.12107
------------------------------
SHA256SUMS.txt records the exact bundled archive hashes. build_codecs.cmd verifies
all hashes with Windows certutil before extraction. Glide's bundled libjxl source archive is intentionally reduced further by removing
the unused tools/benchmark tree, which contains POSIX shell entries that Windows
bsdtar may reject on some systems. libjxl's release archive also omits its git
submodules, so the builder deterministically stages the pinned Brotli, Highway
and minimal skcms archives into libjxl-0.12.0/third_party before CMake.
Generated dav1d config/vcs headers are written by native_codecs/CMakeLists.txt;
libde265's generated de265-version.h is consumed from its CMake binary directory.
No fix depends on a pre-extracted codec_native_src tree.
