// 5.1.7 libwebp path. See GlideWebp.h.
#include "GlideWebp.h"
#include "GlideJpeg.h"
#include <objbase.h>
#include <webp/decode.h>
#include <algorithm>
#include <cmath>
#include <cstring>
#include <cwchar>
#include <limits>

struct GlideDecodedImage
{
    std::uint32_t width;
    std::uint32_t height;
    std::uint32_t stride;
    std::uint64_t bufferBytes;
    void* data;
};

namespace
{
// Whole file through a read-only mapping.
struct MappedFile
{
    HANDLE file = INVALID_HANDLE_VALUE, mapping = nullptr;
    const std::uint8_t* data = nullptr;
    std::size_t size = 0;
    ~MappedFile()
    {
        if (data) UnmapViewOfFile(data);
        if (mapping) CloseHandle(mapping);
        if (file != INVALID_HANDLE_VALUE) CloseHandle(file);
    }
    bool Open(const wchar_t* path)
    {
        file = CreateFileW(path, GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE, nullptr, OPEN_EXISTING,
                           FILE_FLAG_SEQUENTIAL_SCAN, nullptr);
        if (file == INVALID_HANDLE_VALUE) return false;
        LARGE_INTEGER length{};
        if (!GetFileSizeEx(file, &length) || length.QuadPart < 30 || length.QuadPart > 0x7FFFFFFF) return false;
        size = static_cast<std::size_t>(length.QuadPart);
        mapping = CreateFileMappingW(file, nullptr, PAGE_READONLY, 0, 0, nullptr);
        if (!mapping) return false;
        data = static_cast<const std::uint8_t*>(MapViewOfFile(mapping, FILE_MAP_READ, 0, 0, 0));
        return data != nullptr;
    }
};

// A still WebP this path decodes: RIFF/WEBP signature, readable features, no animation, inside the pixel cap.
bool ReadStillFeatures(const MappedFile& in, WebPBitstreamFeatures& features)
{
    if (in.size < 30 || std::memcmp(in.data, "RIFF", 4) != 0 || std::memcmp(in.data + 8, "WEBP", 4) != 0) return false;
    if (WebPGetFeatures(in.data, in.size, &features) != VP8_STATUS_OK) return false;
    if (features.has_animation) return false;
    return features.width > 0 && features.height > 0 &&
           GlideWithinDecodeCap(static_cast<std::uint64_t>(features.width), static_cast<std::uint64_t>(features.height));
}

// Premultiplied BGRA into external memory. Threads on: libwebp overlaps the loop filter with parsing, which is most of a lossy
// frame's cost (24 MP: 113 ms single-threaded, 65 ms threaded; a single-threaded thumbnail decode measured 90 ms against 55-69 ms).
bool DecodeExternal(const MappedFile& in, std::uint8_t* buffer, std::uint32_t stride, std::uint32_t outWidth, std::uint32_t outHeight,
                    bool scaled, bool fastUpsampling)
{
    WebPDecoderConfig config;
    if (!WebPInitDecoderConfig(&config)) return false;
    config.options.use_threads = 1;
    config.options.no_fancy_upsampling = fastUpsampling ? 1 : 0;
    config.options.bypass_filtering = fastUpsampling ? 1 : 0; // loop filter off: its effect is far below one pixel after a 4x+ reduction
    if (scaled)
    {
        config.options.use_scaling = 1;
        config.options.scaled_width = static_cast<int>(outWidth);
        config.options.scaled_height = static_cast<int>(outHeight);
    }
    config.output.colorspace = MODE_bgrA;
    config.output.is_external_memory = 1;
    config.output.u.RGBA.rgba = buffer;
    config.output.u.RGBA.stride = static_cast<int>(stride);
    config.output.u.RGBA.size = static_cast<std::size_t>(stride) * outHeight;
    const auto status = WebPDecode(in.data, in.size, &config);
    WebPFreeDecBuffer(&config.output);
    return status == VP8_STATUS_OK;
}
}

bool GlideIsWebpPath(const wchar_t* path)
{
    if (!path) return false;
    const auto length = std::wcslen(path);
    return length >= 6 && _wcsicmp(path + length - 5, L".webp") == 0;
}

bool GlideWebpDecodeBounded(const wchar_t* path, std::uint32_t maxWidth, std::uint32_t maxHeight, bool thumbnail, GlideDecodedImage* image)
{
    if (!path || !image || maxWidth == 0 || maxHeight == 0) return false;
    *image = {};
    MappedFile in;
    WebPBitstreamFeatures features{};
    if (!in.Open(path) || !ReadStillFeatures(in, features)) return false;
    // The viewer's first view of a picture with transparency stays on the previous route (measured: libwebp's alpha plane +
    // premultiply made it slower there); a thumbnail is cached after the first decode, so it takes libwebp either way.
    if (!thumbnail && features.has_alpha) return false;
    const auto width = static_cast<std::uint32_t>(features.width), height = static_cast<std::uint32_t>(features.height);
    std::uint32_t outWidth = width, outHeight = height;
    bool scaled = false;
    if (width > maxWidth || height > maxHeight)
    {
        const double ratio = std::min(static_cast<double>(maxWidth) / width, static_cast<double>(maxHeight) / height);
        outWidth = std::max<std::uint32_t>(1, static_cast<std::uint32_t>(thumbnail ? width * ratio + 0.5 : std::floor(width * ratio)));
        outHeight = std::max<std::uint32_t>(1, static_cast<std::uint32_t>(thumbnail ? height * ratio + 0.5 : std::floor(height * ratio)));
        scaled = true;
    }
    const std::uint64_t bytes = static_cast<std::uint64_t>(outWidth) * outHeight * 4ull;
    if (bytes > std::numeric_limits<std::uint32_t>::max()) return false;
    auto* pixels = static_cast<std::uint8_t*>(CoTaskMemAlloc(static_cast<std::size_t>(bytes)));
    if (!pixels) return false;
    // Fast chroma upsampling and no loop filter only when the picture shrinks a lot: the loss is far below one output pixel.
    const bool fast = thumbnail && scaled && (width >= outWidth * 4ull);
    if (!DecodeExternal(in, pixels, outWidth * 4, outWidth, outHeight, scaled, fast))
    {
        CoTaskMemFree(pixels);
        return false;
    }
    image->width = outWidth;
    image->height = outHeight;
    image->stride = outWidth * 4;
    image->bufferBytes = bytes;
    image->data = pixels;
    return true;
}
