#pragma once

#include <cstdint>
#include <cstddef>
#include <vector>

struct IStream;

namespace glide::thumb
{
    /// A decoded image in BGRA, stride = width * 4. `premultiplied` says whether the colour channels
    /// are already multiplied by alpha (WIC/libwebp paths) or straight (compact decoders).
    struct Image
    {
        std::uint32_t width = 0;
        std::uint32_t height = 0;
        bool premultiplied = false;
        std::vector<std::uint8_t> pixels;

        bool Valid() const
        {
            return width > 0 && height > 0 &&
                   pixels.size() == static_cast<std::size_t>(width) * height * 4u;
        }
    };

    /// Thumbnail quality presets. These are the Settings -> Explorer thumbnails "Thumbnail quality"
    /// values and are persisted by Glide.exe as HKCU\Software\Glide\ExplorerThumbnails\Quality.
    enum class Quality : int
    {
        /// Cheapest path: bilinear resampling, small embedded previews accepted (>= half the
        /// requested size), animated GIFs use frame 0 only, WebP skips its in-loop filter.
        Fast = 0,
        /// Default: area-averaging (Fant) resampling, embedded previews only when they are at least
        /// as large as the requested thumbnail, blank first GIF frames are skipped.
        Balanced = 1,
        /// Decodes at twice the requested size (bounded) and resamples with high-quality cubic;
        /// WebP uses full chroma upsampling.
        High = 2,
    };

    /// Quality/behaviour knobs. These mirror the Explorer Thumbnails performance settings.
    struct DecodeOptions
    {
        /// Requested thumbnail edge (Explorer's cx). 0 means 256.
        std::uint32_t maxWidth = 0;
        std::uint32_t maxHeight = 0;
        /// "Maximum source decode size": caps the longest side of the intermediate decode. 0 = automatic.
        /// When it is smaller than the requested edge, the result is upscaled by ComposeThumbnail.
        std::uint32_t maxSourceEdge = 0;
        /// Extract an embedded thumbnail/preview before touching the full decode.
        bool preferEmbedded = true;
        /// 0 = fast, 1 = balanced (default), 2 = high. Out-of-range values are treated as balanced.
        int quality = 1;
    };

    /// The concrete knobs a quality preset resolves to. Exposed so tests and diagnostics can prove
    /// the presets really differ.
    struct QualityProfile
    {
        Quality quality = Quality::Balanced;
        /// Intermediate decode size as a multiple of the requested edge.
        std::uint32_t supersample = 1;
        /// An embedded preview is used only if its longest side is at least this percentage of the
        /// requested edge.
        std::uint32_t embeddedMinPercent = 100;
        /// WICBitmapInterpolationMode used for every resample.
        int interpolation = 0;
        /// Frames composited while looking for a non-blank first image in animated GIFs.
        std::uint32_t gifFrameSearch = 1;
        /// libwebp decode shortcuts.
        bool webpBypassFiltering = false;
        bool webpNoFancyUpsampling = false;
    };

    QualityProfile ProfileFor(int quality);

    struct DecodeResult
    {
        bool ok = false;
        Image image;
        /// Short decoder tag for diagnostics ("wic", "wic-reduced", "embedded", "gif", "svg", ...).
        const char* decoder = "none";
        bool embedded = false;
        /// True when the decoder reported the source is a vector document.
        bool vector = false;
        /// Full source dimensions when known (0 when the decoder could not report them).
        std::uint32_t sourceWidth = 0;
        std::uint32_t sourceHeight = 0;
        std::uint64_t decodeMicros = 0;
        std::uint64_t resizeMicros = 0;
        /// Bytes pulled from the source stream (stream decodes only).
        std::uint64_t bytesRead = 0;
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

    /// Same as DecodeForThumbnail, but reads from a stream on demand. WIC formats are decoded
    /// straight from the stream (through a block cache), so an embedded preview or the first frame
    /// of a large animated GIF never forces the whole file to be read. Formats that need the whole
    /// buffer (SVG, WebP, compact decoders) read it once, bounded by the 1 GiB source cap.
    DecodeResult DecodeStreamForThumbnail(IStream* stream,
                                          const wchar_t* extension,
                                          const DecodeOptions& options);

    /// Largest thumbnail edge the provider ever renders. Explorer asks for at most 2560 (it uses 256 to
    /// 1024 in practice); any larger request from another host is clamped to this, never honoured.
    constexpr std::uint32_t MaxThumbnailEdge = 2048;

    /// Clamps a host's requested thumbnail edge to [1, MaxThumbnailEdge] (0 means the 256 default).
    constexpr std::uint32_t ClampRequestedEdge(std::uint32_t requested)
    {
        return requested == 0 ? 256u : (requested > MaxThumbnailEdge ? MaxThumbnailEdge : requested);
    }

    /// Aspect-fits `source` into a square of `edge` pixels (clamped to MaxThumbnailEdge) and composites it
    /// onto an opaque white background. The result is what Explorer receives: an opaque, square-ish thumbnail.
    Image ComposeThumbnail(const Image& source, std::uint32_t edge, int quality = 1);
}
