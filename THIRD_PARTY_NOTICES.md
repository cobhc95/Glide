# Third-party notices

Glide's original source is licensed under MIT. The repository also bundles pinned source archives for independent third-party codec libraries. Those components are **not relicensed under Glide's MIT license**; each retains its upstream license.

| Component | Pinned version/revision | Purpose |
|---|---|---|
| libwebp | 1.6.0 | WebP decode/demux |
| libheif | 1.23.4 | HEIF/HEIC/AVIF container/codec integration |
| libde265 | 1.1.1 | HEVC decode for HEIF/HEIC |
| dav1d | 1.5.1 | AV1 decode for AVIF |
| libjxl | 0.12.0 | JPEG XL decoding |
| OpenJPEG | 2.5.3 | JPEG 2000 decoding |
| TinyEXR | 3.2.0 | OpenEXR decoding |
| miniz | bundled with TinyEXR package | compression support |
| Highway | revision `457c891775a7397bdb0376bb1031e6e027af1c48` | libjxl SIMD support |
| Brotli | revision `028fb5a23661f123017c060daa546b55cf4bde29` | libjxl compression support |
| skcms | minimal revision `96d9171` | colour-management support used by libjxl build |

Copies of the corresponding license texts are provided in `third_party/licenses/`. The original source archives are in `codec_sources/` with SHA-256 hashes in `codec_sources/SHA256SUMS.txt`.

In particular, libheif/libde265 carry LGPL-family obligations; downstream distributors should review the exact bundled license texts and preserve replaceability/source availability as required by those licenses.
