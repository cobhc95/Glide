#pragma once
#include <cstdint>

#ifdef _WIN32
#define GLIDE_CODEC_ABI_CALL __cdecl
#else
#define GLIDE_CODEC_ABI_CALL
#endif

// Stable C ABI between the tiny Glide core and the optional modular codec pack.
// The bridge is intentionally delay-loaded at runtime so Glide.exe remains
// lightweight and starts normally even when the codec pack is absent.
struct GlideCodecImageV1 {
    std::uint32_t structSize;
    std::uint32_t width;
    std::uint32_t height;
    std::uint32_t stride;
    std::uint64_t dataSize;
    unsigned char* pixels; // premultiplied BGRA8, allocated by codec bridge
    std::uint32_t codecId;
    std::uint32_t reserved;
};

using GlideCodecDecodeFileWFn = int (GLIDE_CODEC_ABI_CALL*)(const wchar_t* path, GlideCodecImageV1* outImage,
                                               wchar_t* errorText, std::uint32_t errorChars);
using GlideCodecFreeFn = void (GLIDE_CODEC_ABI_CALL*)(void* p);
using GlideCodecVersionFn = const wchar_t* (GLIDE_CODEC_ABI_CALL*)();
