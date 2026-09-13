#pragma once
#include <cstddef>
#include <cstdint>

#if defined(_WIN32)
#define GLIDE_CODEC_EXPORT extern "C" __declspec(dllexport)
#define GLIDE_CODEC_CALL __cdecl
#else
#define GLIDE_CODEC_EXPORT extern "C"
#define GLIDE_CODEC_CALL
#endif

// Glide optional codec ABI. Providers are loaded only when a matching non-core file is opened.
// ABI v2 is preferred because it returns decoded pixels directly and avoids an encoded
// native->managed buffer plus a second decode. All strings are UTF-8 NUL-terminated.
namespace glide_codec_abi
{
    inline constexpr std::uint32_t LegacyVersion = 1;
    inline constexpr std::uint32_t CurrentVersion = 2;

    inline constexpr std::uint32_t SupportsPreview  = 1u << 0;
    inline constexpr std::uint32_t SupportsFull     = 1u << 1;
    inline constexpr std::uint32_t SupportsMetadata = 1u << 2;
    inline constexpr std::uint32_t SupportsFrames   = 1u << 3;

    // ABI v2 surface constants. Glide currently accepts only premultiplied BGRA8.
    inline constexpr std::uint32_t PixelFormatBgra8 = 1;
    inline constexpr std::uint32_t AlphaPremultiplied = 1;

    struct ProviderInfo
    {
        std::uint32_t abiVersion;
        std::uint32_t structSize;
        const char* id;
        const char* version;
        const char* license;
        const char* const* extensions;
        std::uint32_t extensionCount;
        std::uint32_t capabilities;
    };

    struct ProbeResult
    {
        std::uint32_t supported;
        std::uint32_t width;
        std::uint32_t height;
        std::uint32_t frameCount;
        std::uint32_t animated;
    };

    // ABI v1 only: encoded interchange payload. Kept for compatibility; do not use for new work.
    struct Buffer
    {
        void* data;
        std::size_t size;
    };

    // ABI v2: decoded surface. `size` must be >= stride*height. Glide copies this once into its
    // presentation bitmap and then calls GlideCodecProvider_FreeBuffer(data,size).
    struct Surface
    {
        void* data;
        std::size_t size;
        std::uint32_t width;
        std::uint32_t height;
        std::uint32_t stride;
        std::uint32_t pixelFormat;
        std::uint32_t alphaMode;
    };
}

// Required exports for every provider:
// GLIDE_CODEC_EXPORT const glide_codec_abi::ProviderInfo* GLIDE_CODEC_CALL GlideCodecProvider_GetInfo();
// GLIDE_CODEC_EXPORT void* GLIDE_CODEC_CALL GlideCodecProvider_Create();
// GLIDE_CODEC_EXPORT void GLIDE_CODEC_CALL GlideCodecProvider_Destroy(void* instance);
// GLIDE_CODEC_EXPORT int GLIDE_CODEC_CALL GlideCodecProvider_Probe(void* instance, const char* utf8Path, glide_codec_abi::ProbeResult* result);
// GLIDE_CODEC_EXPORT void GLIDE_CODEC_CALL GlideCodecProvider_FreeBuffer(void* data, std::size_t size);
//
// ABI v2 decode exports when corresponding capability flags are set:
// GLIDE_CODEC_EXPORT int GLIDE_CODEC_CALL GlideCodecProvider_DecodePreviewSurface(void* instance, const char* utf8Path, std::uint32_t longestSide, glide_codec_abi::Surface* result);
// GLIDE_CODEC_EXPORT int GLIDE_CODEC_CALL GlideCodecProvider_DecodeFullSurface(void* instance, const char* utf8Path, std::uint32_t ignored, glide_codec_abi::Surface* result);
//
// Metadata / frame exports (UTF-8 JSON in glide_codec_abi::Buffer) retain the v1 buffer shape:
// GLIDE_CODEC_EXPORT int GLIDE_CODEC_CALL GlideCodecProvider_ReadMetadata(void* instance, const char* utf8Path, glide_codec_abi::Buffer* result);
// GLIDE_CODEC_EXPORT int GLIDE_CODEC_CALL GlideCodecProvider_ReadFrames(void* instance, const char* utf8Path, glide_codec_abi::Buffer* result);
