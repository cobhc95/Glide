#pragma once

// Bridge from the Explorer thumbnail provider to Glide's optional codec libraries, which implement the
// provider ABI v2 (native/Glide.CodecProvider.ABI.h):
//
//   codecs\glidecodecs.dll   AVIF/AVIFS, HEIC/HEIF (+sequences), JPEG XL, JPEG 2000 family   (bundled C/C++)
//   codecs\glidemanaged.dll  camera RAW previews, EXR, DICOM/NIfTI/FITS/PFM, ORA/KRA/PDN/XCF,
//                            KTX/KTX2/PVR/ASTC, ZIP-compressed PSD/PSB, .hdr.gz          (NativeAOT, src/Glide.ThumbnailCodecs)
//
// Both are loaded lazily, only when a file of one of their families is thumbnailed, from the "codecs"
// folder next to this DLL and nowhere else (LoadLibraryExW with LOAD_LIBRARY_SEARCH_DLL_LOAD_DIR |
// LOAD_LIBRARY_SEARCH_SYSTEM32, absolute path; the image's folder is never searched). Each file must match
// the SHA-256 pinned into this DLL at build time (GlideCodecPins.h), checked on a handle that denies writes
// until the module is mapped. Loaded modules stay pinned for the life of the thumbnail host.
//
// The ABI decodes from a path, but Explorer hands thumbnail providers a stream, so the bytes are spooled to
// a delete-on-close temporary file first. Every call into a codec library runs under a structured-exception
// guard; every surface it returns is validated before use; dimensions and memory are capped.

#include "ThumbnailCore.h"

#include <cstddef>
#include <cstdint>
#include <string>
#include <vector>

namespace glide::thumb::providers
{
    enum class Kind : int
    {
        Bundled = 0,  // glidecodecs.dll
        Managed = 1,  // glidemanaged.dll
    };

    /// Suffixes each library is expected to own. Used only to decide whether a library is worth loading;
    /// once loaded, the library's own GetInfo() extension table is authoritative (a -NoHevc build of
    /// glidecodecs.dll, for example, drops the HEIF suffixes).
    bool IsCandidate(Kind kind, const std::wstring& normalizedExtension);

    /// Spools `data` to a temporary file and asks the library for a preview no larger than `longestSide`.
    /// Returns a premultiplied BGRA image. Never throws; any failure (library missing, hash mismatch,
    /// unsupported or hostile file, cap exceeded) returns false so the caller can try the next decoder.
    bool Decode(Kind kind, const std::uint8_t* data, std::size_t size, const std::wstring& normalizedExtension,
                std::uint32_t longestSide, Image& out, std::uint32_t& sourceWidth, std::uint32_t& sourceHeight);

    /// Inflates a gzip member (SVGZ) through glidemanaged.dll, capped at `maxOutput` bytes.
    bool Gunzip(const std::uint8_t* data, std::size_t size, std::size_t maxOutput, std::vector<std::uint8_t>& out);

    /// Tests only: use `directory` instead of "<this DLL's folder>\codecs" (nullptr restores the default).
    /// The pinned hashes still apply, so this can never load an unverified library.
    void SetCodecDirectoryForTests(const wchar_t* directory);

    /// The SHA-256 (lower-case hex) pinned at build time, or "" when the library was absent at build time.
    const char* PinnedHash(Kind kind);

    /// 1 = loaded and verified, 0 = not attempted yet, negative = refused (see CodecProviders.cpp).
    int LoadState(Kind kind);
}

namespace glide::thumb
{
    /// Host image returned to glidemanaged.dll by DecodeEncodedForHost (straight BGRA, stride = width * 4,
    /// allocated with malloc and released through FreeEncodedForHost).
    struct HostImage
    {
        std::uint32_t width;
        std::uint32_t height;
        std::uint32_t orientation;
        std::uint32_t reserved;
        void* data;
        std::size_t size;
    };

    /// WIC decode of an embedded JPEG/PNG for the managed decoders (RAW previews, ORA/KRA layers, PDN
    /// thumbnails, DICOM JPEG frames). maxLongestSide > 0 fits the result within it; probeOnly reports the
    /// size and EXIF orientation without pixels. Orientation is reported, never applied. Implemented in
    /// ThumbnailCore.cpp.
    int __cdecl DecodeEncodedForHost(const std::uint8_t* data, std::size_t size, std::uint32_t maxLongestSide,
                                     int probeOnly, HostImage* out);
    void __cdecl FreeEncodedForHost(void* data);
}
