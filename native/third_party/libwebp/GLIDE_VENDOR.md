# Vendored libwebp

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

Two consumers build from this tree:

- `Glide.ShellThumbnail.dll` builds only the decode path (`webpdecoder` target). The encoder,
  demuxer, muxer and all command-line tools are disabled, so the thumbnail provider links a small,
  self-contained decoder and adds no runtime dependency.
- `codecs\glidecodecs.dll` (5.0.4, `native/Glide.Codecs/CMakeLists.txt`) links the full `webp`
  library statically for Save as WebP; only the encoder entry points are referenced, so the linker
  drops the decoder. Its licence texts are in `codecs\glidecodecs.NOTICE.txt`.

To update: replace this directory with the same subset from a newer upstream tag, then run
`build.cmd` and confirm the Explorer thumbnail provider still links and passes the build.
