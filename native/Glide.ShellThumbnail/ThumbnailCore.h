#pragma once

#include <cstdint>
#include <cstddef>
#include <vector>

namespace glide::thumb
{
    /// A decoded image in straight (non-premultiplied) BGRA, stride = width * 4.
    struct Image
    {
        std::uint32_t width = 0;
        std::uint32_t height = 0;
        std::vector<std::uint8_t> pixels;

        bool Valid() const
        {
            return width > 0 && height > 0 &&
                   pixels.size() == static_cast<std::size_t>(width) * height * 4u;
        }
    };

    /// Quality/behaviour knobs. These mirror the Explorer Thumbnails performance settings.
    struct DecodeOptions
    {
        /// Upper bound for the decoded/rendered source. 0 means "no explicit bound" (still capped).
        std::uint32_t maxWidth = 0;
        std::uint32_t maxHeight = 0;
        /// Extract an embedded thumbnail/preview before touching the full decode.
        bool preferEmbedded = true;
        /// 0 = fast, 1 = balanced (default), 2 = high.
        int quality = 1;
    };

    struct DecodeResult
    {
        bool ok = false;
        Image image;
        /// Short decoder tag for diagnostics ("wic", "embedded", "svg", "tga", ...).
        const char* decoder = "none";
        bool embedded = false;
        /// True when the decoder reported the source is a vector document.
        bool vector = false;
        std::uint64_t decodeMicros = 0;
        std::uint64_t resizeMicros = 0;
    };

    /// Returns true for the extensions the compact native decoders own.
    bool IsCompactRasterExtension(const wchar_t* extension);

    /// Returns true for SVG/SVGZ.
    bool IsVectorExtension(const wchar_t* extension);

    /// Decodes the first frame/preview of an image held entirely in memory. Never throws; malformed
    /// or hostile input returns a failed result so Explorer can fall back to its normal icon.
    DecodeResult DecodeForThumbnail(const std::uint8_t* data,
                                    std::size_t size,
                                    const wchar_t* extension,
                                    const DecodeOptions& options);

    /// Aspect-fits `source` into a square of `edge` pixels and composites it onto an opaque white
    /// background. The result is what Explorer receives: an opaque, square-ish thumbnail.
    Image ComposeThumbnail(const Image& source, std::uint32_t edge);
}
