# Vendored libwebp (decode-only)

This directory is a trimmed copy of **libwebp 1.5.0**, used by
`Glide.ShellThumbnail.dll` to decode WebP images without depending on the optional
Windows "Webp Image Extensions" WIC codec.

- Upstream: https://github.com/webmproject/libwebp
- Version: `v1.5.0` (tag `v1.5.0`)
- License: BSD-3-Clause (see `COPYING`) plus the additional patent grant in `PATENTS`.
- Kept: `CMakeLists.txt`, `configure.ac`, `cmake/`, `sharpyuv/`, `src/`, licence/notice files.
- Removed: docs, examples, imageio, man pages, tests, extras, Android/Gradle/SWIG glue and
  other build systems. None of these are referenced when the encoder/command-line options
  are disabled (see `native/Glide.ShellThumbnail/CMakeLists.txt`).

Only the decode path is built (`webpdecoder` target). The encoder, demuxer, muxer and all
command-line tools are disabled, so the provider links a small, self-contained decoder and
adds no runtime dependency.

To update: replace this directory with the same subset from a newer upstream tag, then run
`build.cmd` and confirm the Explorer thumbnail provider still links and passes the build.
