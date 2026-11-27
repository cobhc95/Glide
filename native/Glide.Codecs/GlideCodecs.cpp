// Glide bundled optional codec provider: AVIF/AVIFS (libheif + dav1d), HEIC/HEIF (libheif +
// libde265, optional), JPEG XL (libjxl decoder) and JPEG 2000 (OpenJPEG decoder), plus the ABI v2
// encode extension: JPEG 2000 (OpenJPEG encoder) and WebP (vendored libwebp encoder) for Save as.
//
// ABI v2 (native/Glide.CodecProvider.ABI.h): every decode returns one premultiplied BGRA8 surface.
// The DLL is loaded lazily by Glide's CodecProviderRuntime only when one of these suffixes is first
// opened; nothing here runs at process start (no static initialisers beyond constant tables).
//
// Routing is by content, not by suffix: ISO-BMFF 'ftyp' -> libheif, JXL signature -> libjxl,
// JP2 signature / J2K SOC+SIZ -> OpenJPEG. Orientation (HEIF irot/imir, JXL orientation) and alpha
// are applied here, and every source bit depth is reduced to 8 bits per channel.
#include "Glide.CodecProvider.ABI.h"
#include "glide_codecs_config.h"

#include <windows.h>

#include <algorithm>
#include <cmath>
#include <cstdint>
#include <cstdlib>
#include <cstring>
#include <memory>
#include <new>
#include <thread>
#include <vector>

#include <libheif/heif.h>
#include <libheif/heif_items.h>
#include <libheif/heif_sequences.h>
#include <jxl/cms.h>
#include <jxl/decode.h>
#include <jxl/thread_parallel_runner.h>
#include <openjpeg.h>
#include <webp/encode.h>

using namespace glide_codec_abi;

namespace
{
    // Same ceilings as Glide's decode guard (ImageHeaderProbe/DecodeMemoryGuard and CodecPixelSurface):
    // never allocate a surface the managed side would reject anyway.
    constexpr std::uint64_t MaxPixels = 512ull * 1024 * 1024;
    constexpr std::uint32_t MaxDimension = 1000000;
    constexpr std::uint64_t MaxFileBytes = 4ull * 1024 * 1024 * 1024;

    const char* const Extensions[] = { GLIDE_CODECS_EXTENSION_LIST };

    const ProviderInfo Info = {
        CurrentVersion,
        static_cast<std::uint32_t>(sizeof(ProviderInfo)),
        GLIDE_CODECS_ID,
        GLIDE_CODECS_VERSION,
        GLIDE_CODECS_LICENSE,
        Extensions,
        static_cast<std::uint32_t>(sizeof(Extensions) / sizeof(Extensions[0])),
        SupportsPreview | SupportsFull | SupportsEncode,
    };

    bool DimensionsOk(std::uint64_t w, std::uint64_t h)
    {
        return w > 0 && h > 0 && w <= MaxDimension && h <= MaxDimension && w * h <= MaxPixels;
    }

    // Worker cap shared by every decoder here. 5.0.7: every logical processor (the former clamp to 8 left half of a
    // 16-thread CPU idle; benchmark-report-5.0.7.md section 6). GLIDE_CODECS_MAX_THREADS is a build-time cap.
#ifndef GLIDE_CODECS_MAX_THREADS
#define GLIDE_CODECS_MAX_THREADS 64
#endif
    unsigned DecodeThreads()
    {
        SYSTEM_INFO si{};
        GetSystemInfo(&si);
        return static_cast<unsigned>(std::clamp<DWORD>(si.dwNumberOfProcessors, 1, GLIDE_CODECS_MAX_THREADS));
    }

    // ---------------------------------------------------------------------------------------------
    // File access: one read-only handle, shared-delete so Glide can rename/delete while decoding.
    class File
    {
    public:
        explicit File(const char* utf8Path)
        {
            if (!utf8Path || !*utf8Path) return;
            const int chars = MultiByteToWideChar(CP_UTF8, MB_ERR_INVALID_CHARS, utf8Path, -1, nullptr, 0);
            if (chars <= 0 || chars > 32768) return;
            std::vector<wchar_t> wide(static_cast<size_t>(chars));
            if (MultiByteToWideChar(CP_UTF8, MB_ERR_INVALID_CHARS, utf8Path, -1, wide.data(), chars) != chars) return;
            _handle = CreateFileW(wide.data(), GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE,
                nullptr, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL | FILE_FLAG_SEQUENTIAL_SCAN, nullptr);
            LARGE_INTEGER size{};
            if (_handle == INVALID_HANDLE_VALUE || !GetFileSizeEx(_handle, &size) || size.QuadPart <= 0 ||
                static_cast<std::uint64_t>(size.QuadPart) > MaxFileBytes)
            {
                Close();
                return;
            }
            _size = static_cast<std::uint64_t>(size.QuadPart);
        }
        ~File() { Close(); }
        File(const File&) = delete;
        File& operator=(const File&) = delete;

        bool Ok() const { return _handle != INVALID_HANDLE_VALUE; }
        std::uint64_t Size() const { return _size; }

        // Positional read; returns bytes read (short only at end of file or on error).
        size_t ReadAt(std::uint64_t offset, void* buffer, size_t count) const
        {
            if (offset >= _size) return 0;
            count = static_cast<size_t>(std::min<std::uint64_t>(count, _size - offset));
            auto* out = static_cast<std::uint8_t*>(buffer);
            size_t done = 0;
            while (done < count)
            {
                OVERLAPPED ov{};
                const std::uint64_t at = offset + done;
                ov.Offset = static_cast<DWORD>(at);
                ov.OffsetHigh = static_cast<DWORD>(at >> 32);
                const DWORD want = static_cast<DWORD>(std::min<size_t>(count - done, 1u << 30));
                DWORD got = 0;
                if (!ReadFile(_handle, out + done, want, &got, &ov) || got == 0) break;
                done += got;
            }
            return done;
        }

    private:
        void Close()
        {
            if (_handle != INVALID_HANDLE_VALUE) CloseHandle(_handle);
            _handle = INVALID_HANDLE_VALUE;
        }
        HANDLE _handle = INVALID_HANDLE_VALUE;
        std::uint64_t _size = 0;
    };

    // ---------------------------------------------------------------------------------------------
    // Output surface (premultiplied BGRA8, tightly packed). Ownership passes to Glide on success and
    // returns through GlideCodecProvider_FreeBuffer.
    struct Bgra
    {
        std::uint8_t* data = nullptr;
        std::uint32_t width = 0;
        std::uint32_t height = 0;

        Bgra() = default;
        Bgra(const Bgra&) = delete;
        Bgra& operator=(const Bgra&) = delete;
        Bgra(Bgra&& other) noexcept { *this = static_cast<Bgra&&>(other); }
        Bgra& operator=(Bgra&& other) noexcept
        {
            if (this != &other)
            {
                Reset();
                data = other.data; width = other.width; height = other.height;
                other.data = nullptr; other.width = other.height = 0;
            }
            return *this;
        }
        ~Bgra() { Reset(); }

        bool Allocate(std::uint32_t w, std::uint32_t h)
        {
            Reset();
            if (!DimensionsOk(w, h)) return false;
            data = static_cast<std::uint8_t*>(std::malloc(static_cast<size_t>(w) * h * 4));
            if (!data) return false;
            width = w; height = h;
            return true;
        }
        size_t Bytes() const { return static_cast<size_t>(width) * height * 4; }
        std::uint8_t* Row(std::uint32_t y) const { return data + static_cast<size_t>(y) * width * 4; }
        void Reset()
        {
            std::free(data);
            data = nullptr; width = height = 0;
        }
    };

    inline std::uint8_t Premul(std::uint32_t c, std::uint32_t a)
    {
        const std::uint32_t t = c * a + 128;
        return static_cast<std::uint8_t>((t + (t >> 8)) >> 8);
    }

    inline void Store(std::uint8_t* px, std::uint32_t r, std::uint32_t g, std::uint32_t b, std::uint32_t a)
    {
        if (a == 255) { px[0] = static_cast<std::uint8_t>(b); px[1] = static_cast<std::uint8_t>(g); px[2] = static_cast<std::uint8_t>(r); px[3] = 255; return; }
        px[0] = Premul(b, a); px[1] = Premul(g, a); px[2] = Premul(r, a); px[3] = static_cast<std::uint8_t>(a);
    }

    // Runs f(yBegin, yEnd) over [0, rows) on up to DecodeThreads() threads when the surface is large (5.2.0: the RGB -> BGRA
    // conversion of a decoded HEIC / JPEG XL picture was a single-threaded 15 - 25 ms pass over 12 MP). Nothing a worker touches can throw.
    template <class F>
    void ForRowBands(std::uint32_t rows, std::uint64_t pixels, F&& f)
    {
        const std::uint32_t bands = pixels >= (1u << 20) ? std::min(DecodeThreads(), rows) : 1u;
        if (bands <= 1) { f(0u, rows); return; }
        auto run = [&](std::uint32_t band)
        {
            f(static_cast<std::uint32_t>(static_cast<std::uint64_t>(rows) * band / bands), static_cast<std::uint32_t>(static_cast<std::uint64_t>(rows) * (band + 1) / bands));
        };
        std::vector<std::thread> workers;
        std::uint32_t started = 1;
        try
        {
            workers.reserve(bands - 1);
            for (; started < bands; ++started) workers.emplace_back(run, started);
        }
        catch (...) { /* the bands that could not start run below */ }
        run(0);
        for (std::uint32_t band = started; band < bands; ++band) run(band);
        for (auto& worker : workers) worker.join();
    }

    struct DownscaleSpan { std::uint32_t first, last; float wFirst, wLast; };

    // Output rows [yStart, yEnd) of an area-average downscale; `accRow` is this band's own float row.
    void DownscaleRows(const Bgra& image, const Bgra& out, const DownscaleSpan* cols, float* accRow, std::uint32_t dw, std::uint32_t sh,
        double sy, float norm, std::uint32_t yStart, std::uint32_t yEnd)
    {
        const size_t accCount = static_cast<size_t>(dw) * 4;
        for (std::uint32_t y = yStart; y < yEnd; ++y)
        {
            std::fill(accRow, accRow + accCount, 0.0f);
            const double a = y * sy, b = std::min<double>((y + 1) * sy, sh);
            const auto r0 = static_cast<std::uint32_t>(a);
            const auto r1 = std::min(static_cast<std::uint32_t>(std::ceil(b)) - 1, sh - 1);
            for (std::uint32_t r = r0; r <= r1; ++r)
            {
                const double top = std::max<double>(a, r), bottom = std::min<double>(b, r + 1.0);
                const float wy = static_cast<float>(bottom - top);
                if (wy <= 0) continue;
                const std::uint8_t* row = image.Row(r);
                for (std::uint32_t x = 0; x < dw; ++x)
                {
                    const DownscaleSpan& s = cols[x];
                    float c0 = 0, c1 = 0, c2 = 0, c3 = 0;
                    const std::uint8_t* p = row + static_cast<size_t>(s.first) * 4;
                    c0 += p[0] * s.wFirst; c1 += p[1] * s.wFirst; c2 += p[2] * s.wFirst; c3 += p[3] * s.wFirst;
                    for (std::uint32_t i = s.first + 1; i < s.last; ++i)
                    {
                        p = row + static_cast<size_t>(i) * 4;
                        c0 += p[0]; c1 += p[1]; c2 += p[2]; c3 += p[3];
                    }
                    if (s.last > s.first)
                    {
                        p = row + static_cast<size_t>(s.last) * 4;
                        c0 += p[0] * s.wLast; c1 += p[1] * s.wLast; c2 += p[2] * s.wLast; c3 += p[3] * s.wLast;
                    }
                    float* o = accRow + static_cast<size_t>(x) * 4;
                    o[0] += c0 * wy; o[1] += c1 * wy; o[2] += c2 * wy; o[3] += c3 * wy;
                }
            }
            std::uint8_t* dst = out.Row(y);
            for (size_t i = 0; i < accCount; ++i)
            {
                const float v = accRow[i] * norm + 0.5f;
                dst[i] = static_cast<std::uint8_t>(v >= 255.0f ? 255 : (v <= 0 ? 0 : v));
            }
            // Premultiplied invariant: colour never exceeds alpha after rounding.
            for (std::uint32_t x = 0; x < dw; ++x)
            {
                std::uint8_t* px = dst + static_cast<size_t>(x) * 4;
                px[0] = std::min(px[0], px[3]); px[1] = std::min(px[1], px[3]); px[2] = std::min(px[2], px[3]);
            }
        }
    }

    // Area-average (box) downscale of a premultiplied BGRA surface so the longest side equals
    // `longestSide`. Exact fractional pixel coverage; each output row accumulates the source rows it
    // overlaps, so memory stays at one float row per worker regardless of the source size.
    bool Downscale(Bgra& image, std::uint32_t longestSide)
    {
        const std::uint32_t sw = image.width, sh = image.height;
        const std::uint32_t longest = std::max(sw, sh);
        if (longestSide == 0 || longest <= longestSide) return true;
        const double ratio = static_cast<double>(longestSide) / longest;
        const auto dw = static_cast<std::uint32_t>(std::max(1.0, std::floor(sw * ratio + 0.5)));
        const auto dh = static_cast<std::uint32_t>(std::max(1.0, std::floor(sh * ratio + 0.5)));
        if (dw >= sw && dh >= sh) return true;

        Bgra out;
        if (!out.Allocate(dw, dh)) return false;
        const double sx = static_cast<double>(sw) / dw, sy = static_cast<double>(sh) / dh;

        std::vector<DownscaleSpan> cols(dw);
        for (std::uint32_t x = 0; x < dw; ++x)
        {
            const double a = x * sx, b = std::min<double>((x + 1) * sx, sw);
            DownscaleSpan s{ static_cast<std::uint32_t>(a), std::min(static_cast<std::uint32_t>(std::ceil(b)) - 1, sw - 1), 0, 0 };
            s.wFirst = static_cast<float>(std::min<double>(s.first + 1, b) - a);
            s.wLast = s.last > s.first ? static_cast<float>(b - s.last) : 0.0f;
            cols[x] = s;
        }
        const float norm = static_cast<float>(1.0 / (sx * sy));
        // Output rows are independent (each band owns its accumulator row), so bands of rows run on
        // worker threads with exactly the serial arithmetic. Everything a worker touches is allocated
        // here first: nothing on a worker thread can throw.
        const std::uint32_t bands = static_cast<std::uint64_t>(sw) * sh >= (1u << 20) ? std::min(DecodeThreads(), dh) : 1u;
        std::vector<std::vector<float>> accs(bands, std::vector<float>(static_cast<size_t>(dw) * 4));
        auto rows = [&](std::uint32_t band)
        {
            const auto yStart = static_cast<std::uint32_t>(static_cast<std::uint64_t>(dh) * band / bands);
            const auto yEnd = static_cast<std::uint32_t>(static_cast<std::uint64_t>(dh) * (band + 1) / bands);
            DownscaleRows(image, out, cols.data(), accs[band].data(), dw, sh, sy, norm, yStart, yEnd);
        };
        std::vector<std::thread> workers;
        std::uint32_t started = 1;
        try
        {
            workers.reserve(bands - 1);
            for (; started < bands; ++started) workers.emplace_back(rows, started);
        }
        catch (...)
        {
            // A worker could not be started: the remaining bands run on this thread below.
        }
        rows(0);
        for (std::uint32_t band = started; band < bands; ++band) rows(band);
        for (auto& worker : workers) worker.join();
        image = static_cast<Bgra&&>(out);
        return true;
    }

    struct Request
    {
        bool probeOnly = false;
        std::uint32_t longestSide = 0; // 0 = full resolution
        std::uint32_t width = 0;       // probe result (display orientation)
        std::uint32_t height = 0;
        Bgra image;
    };

    // ---------------------------------------------------------------------------------------------
    // HEIF / AVIF via libheif (file-backed reader: only the boxes and items needed are read).
    struct HeifReaderState { const File* file; std::int64_t position; };

    std::int64_t HeifGetPosition(void* user) { return static_cast<HeifReaderState*>(user)->position; }
    int HeifRead(void* data, size_t size, void* user)
    {
        auto* s = static_cast<HeifReaderState*>(user);
        if (s->position < 0) return -1;
        const size_t got = s->file->ReadAt(static_cast<std::uint64_t>(s->position), data, size);
        s->position += static_cast<std::int64_t>(got);
        return got == size ? 0 : -1;
    }
    int HeifSeek(std::int64_t position, void* user)
    {
        auto* s = static_cast<HeifReaderState*>(user);
        if (position < 0 || static_cast<std::uint64_t>(position) > s->file->Size()) return -1;
        s->position = position;
        return 0;
    }
    heif_reader_grow_status HeifWaitForFileSize(std::int64_t target, void* user)
    {
        auto* s = static_cast<HeifReaderState*>(user);
        return target >= 0 && static_cast<std::uint64_t>(target) <= s->file->Size()
            ? heif_reader_grow_status_size_reached : heif_reader_grow_status_size_beyond_eof;
    }

    bool CopyHeifImage(heif_image* img, bool hasAlpha, Request& req)
    {
        size_t stride = 0;
        const std::uint8_t* plane = heif_image_get_plane_readonly2(img, heif_channel_interleaved, &stride);
        const int w = heif_image_get_width(img, heif_channel_interleaved);
        const int h = heif_image_get_height(img, heif_channel_interleaved);
        const size_t bpp = hasAlpha ? 4 : 3;
        if (!plane || w <= 0 || h <= 0 || stride < bpp * static_cast<size_t>(w)) return false;
        if (heif_image_get_bits_per_pixel_range(img, heif_channel_interleaved) != 8) return false;
        if (!req.image.Allocate(static_cast<std::uint32_t>(w), static_cast<std::uint32_t>(h))) return false;
        const bool premultiplied = heif_image_is_premultiplied_alpha(img) != 0;
        ForRowBands(static_cast<std::uint32_t>(h), static_cast<std::uint64_t>(w) * h, [&](std::uint32_t yBegin, std::uint32_t yEnd)
        {
            for (std::uint32_t y = yBegin; y < yEnd; ++y)
            {
                const std::uint8_t* s = plane + stride * static_cast<size_t>(y);
                std::uint8_t* d = req.image.Row(y);
                for (int x = 0; x < w; ++x, s += bpp, d += 4)
                {
                    if (!hasAlpha) { d[0] = s[2]; d[1] = s[1]; d[2] = s[0]; d[3] = 255; }
                    else if (premultiplied) { d[3] = s[3]; d[0] = std::min(s[2], s[3]); d[1] = std::min(s[1], s[3]); d[2] = std::min(s[0], s[3]); }
                    else Store(d, s[0], s[1], s[2], s[3]);
                }
            }
        });
        return true;
    }

    // 5.2.0: a HEIF file often carries a small thumbnail item (iPhone: 320 px, referenced with 'thmb'). When one is at least as large as
    // the requested preview and has the picture's aspect ratio, decoding it (a few milliseconds) replaces decoding the whole HEVC / AV1
    // picture (about 100 ms for 12 MP) and then averaging it down. Opaque pictures only: alpha, auxiliary and HDR handling stay on the
    // primary item. The caller still fits the result to the requested size.
    bool TryHeifThumbnail(heif_image_handle* primary, int primaryWidth, int primaryHeight, const heif_decoding_options* options, Request& req)
    {
        if (req.longestSide == 0 || heif_image_handle_has_alpha_channel(primary)) return false;
        const int count = heif_image_handle_get_number_of_thumbnails(primary);
        if (count <= 0 || count > 8) return false;
        heif_item_id ids[8];
        const int got = std::min(count, heif_image_handle_get_list_of_thumbnail_IDs(primary, ids, count));
        heif_image_handle* best = nullptr;
        int bestLongest = 0;
        const double aspect = static_cast<double>(primaryWidth) / primaryHeight;
        for (int i = 0; i < got; ++i)
        {
            heif_image_handle* t = nullptr;
            if (heif_image_handle_get_thumbnail(primary, ids[i], &t).code != heif_error_Ok || !t) continue;
            const int tw = heif_image_handle_get_width(t), th = heif_image_handle_get_height(t);
            const int longest = std::max(tw, th);
            const bool usable = tw > 0 && th > 0 && DimensionsOk(static_cast<std::uint64_t>(tw), static_cast<std::uint64_t>(th)) &&
                                std::fabs(static_cast<double>(tw) / th - aspect) <= 0.03 * aspect &&
                                static_cast<std::uint32_t>(longest) >= req.longestSide && !heif_image_handle_has_alpha_channel(t) &&
                                (best == nullptr || longest < bestLongest);
            if (!usable) { heif_image_handle_release(t); continue; }
            if (best) heif_image_handle_release(best);
            best = t;
            bestLongest = longest;
        }
        if (!best) return false;
        struct HandleGuard { heif_image_handle* h; ~HandleGuard() { heif_image_handle_release(h); } } guard{ best };
        heif_image* img = nullptr;
        if (heif_decode_image(best, &img, heif_colorspace_RGB, heif_chroma_interleaved_RGB, options).code != heif_error_Ok || !img) return false;
        struct ImgGuard { heif_image* i; ~ImgGuard() { heif_image_release(i); } } imgGuard{ img };
        return CopyHeifImage(img, false, req);
    }

    bool DecodeHeif(const File& file, Request& req)
    {
        heif_context* ctx = heif_context_alloc();
        if (!ctx) return false;
        struct CtxGuard { heif_context* c; ~CtxGuard() { heif_context_free(c); } } ctxGuard{ ctx };

        HeifReaderState state{ &file, 0 };
        heif_reader reader{};
        reader.reader_api_version = 1;
        reader.get_position = HeifGetPosition;
        reader.read = HeifRead;
        reader.seek = HeifSeek;
        reader.wait_for_file_size = HeifWaitForFileSize;
        if (heif_context_read_from_reader(ctx, &reader, &state, nullptr).code != heif_error_Ok) return false;

        heif_decoding_options* options = heif_decoding_options_alloc();
        if (!options) return false;
        struct OptGuard { heif_decoding_options* o; ~OptGuard() { heif_decoding_options_free(o); } } optGuard{ options };
        options->convert_hdr_to_8bit = 1;
        options->ignore_transformations = 0;
        // Grid images (phone HEIC/AVIF are usually 512x512 tiles): libheif decodes tiles concurrently.
        // Its default is 4; use the same DecodeThreads() as OpenJPEG/libjxl. Output is unchanged.
        const unsigned threads = DecodeThreads();
        heif_context_set_max_decoding_threads(ctx, static_cast<int>(threads));

        heif_image_handle* handle = nullptr;
        if (heif_context_get_primary_image_handle(ctx, &handle).code == heif_error_Ok && handle)
        {
            struct HandleGuard { heif_image_handle* h; ~HandleGuard() { heif_image_handle_release(h); } } handleGuard{ handle };
            // Display size: libheif reports the item size after irot/imir/clap transformations.
            const int w = heif_image_handle_get_width(handle), h = heif_image_handle_get_height(handle);
            if (!DimensionsOk(static_cast<std::uint64_t>(std::max(w, 0)), static_cast<std::uint64_t>(std::max(h, 0)))) return false;
            req.width = static_cast<std::uint32_t>(w);
            req.height = static_cast<std::uint32_t>(h);
            if (req.probeOnly) return true;

            // Codec threads: a grid already runs one decoder per tile in parallel, so each tile decoder
            // stays single-threaded (dav1d would otherwise start a full thread pool per tile). A single
            // HEVC item gets libde265 worker threads (WPP/tiles; libheif's default is one); a single AV1
            // item keeps dav1d's own automatic threading.
            const std::uint32_t itemType = heif_item_get_item_type(ctx, heif_image_handle_get_item_id(handle));
            if (itemType == heif_item_type_grid) options->num_codec_threads = 1;
            else if (itemType == heif_item_type_hvc1) options->num_codec_threads = static_cast<int>(threads);

            if (TryHeifThumbnail(handle, w, h, options, req)) return true;
            req.image.Reset();
            const bool alpha = heif_image_handle_has_alpha_channel(handle) != 0;
            heif_image* img = nullptr;
            if (heif_decode_image(handle, &img, heif_colorspace_RGB,
                    alpha ? heif_chroma_interleaved_RGBA : heif_chroma_interleaved_RGB, options).code != heif_error_Ok || !img)
                return false;
            struct ImgGuard { heif_image* i; ~ImgGuard() { heif_image_release(i); } } imgGuard{ img };
            return CopyHeifImage(img, alpha, req);
        }

        // Image sequence without a still primary item (some AVIFS/HEIFS): show the first frame.
        if (!heif_context_has_sequence(ctx)) return false;
        heif_track* track = heif_context_get_track(ctx, 0);
        if (!track) return false;
        struct TrackGuard { heif_track* t; ~TrackGuard() { heif_track_release(t); } } trackGuard{ track };
        std::uint16_t tw = 0, th = 0;
        if (heif_track_get_image_resolution(track, &tw, &th).code != heif_error_Ok || !DimensionsOk(tw, th)) return false;
        req.width = tw; req.height = th;
        if (req.probeOnly) return true;
        const bool alpha = heif_track_has_alpha_channel(track) != 0;
        heif_image* img = nullptr;
        if (heif_track_decode_next_image(track, &img, heif_colorspace_RGB,
                alpha ? heif_chroma_interleaved_RGBA : heif_chroma_interleaved_RGB, options).code != heif_error_Ok || !img)
            return false;
        struct ImgGuard { heif_image* i; ~ImgGuard() { heif_image_release(i); } } imgGuard{ img };
        if (!CopyHeifImage(img, alpha, req)) return false;
        req.width = req.image.width; req.height = req.image.height;
        return true;
    }

    // ---------------------------------------------------------------------------------------------
    // JPEG XL via libjxl (decoder only). Input is streamed in chunks; output is 8-bit sRGB when the
    // image is XYB-encoded; libjxl's default colour management (skcms) converts ICC-tagged images to sRGB.
    // The DC image reaches the caller through libjxl's pixel callback: only the pixel at the centre of every 8 x 8 block is kept, so the
    // full-size smooth upscale libjxl renders for a flush is never stored (nor converted) as a whole.
    // Outside a flush (a file without a DC step decodes to its end through the same callback) the pixels are copied into `full`, whose
    // pages are only committed when they are written, so the DC route never pays for the full-size buffer.
    struct DcSampler
    {
        std::uint32_t width, height, cols, rows, channels;
        Bgra* out;
        std::uint8_t* full;
        bool flushing;
    };

    void JxlDcCallback(void* opaque, size_t x, size_t y, size_t count, const void* pixels)
    {
        auto* s = static_cast<DcSampler*>(opaque);
        if (y >= s->height || x >= s->width) return;
        if (!s->flushing)
        {
            count = std::min<size_t>(count, s->width - x);
            std::memcpy(s->full + (y * s->width + x) * s->channels, pixels, count * s->channels);
            return;
        }
        const std::uint32_t j = static_cast<std::uint32_t>(y / 8);
        if (j >= s->rows || y != std::min<size_t>(static_cast<size_t>(j) * 8 + 4, s->height - 1)) return;
        const auto* p = static_cast<const std::uint8_t*>(pixels);
        std::uint8_t* row = s->out->Row(j);
        const std::uint32_t first = static_cast<std::uint32_t>(x / 8);
        for (std::uint32_t i = first; i < s->cols; ++i)
        {
            const size_t cx = std::min<size_t>(static_cast<size_t>(i) * 8 + 4, s->width - 1);
            if (cx < x) continue;
            if (cx >= x + count) break;
            const std::uint8_t* q = p + (cx - x) * s->channels;
            std::uint8_t* d = row + static_cast<size_t>(i) * 4;
            if (s->channels == 1) { d[0] = d[1] = d[2] = q[0]; }
            else { d[0] = q[2]; d[1] = q[1]; d[2] = q[0]; }
            d[3] = 255;
        }
    }

    bool DecodeJxl(const File& file, Request& req)
    {
        // 5.2.0: a preview of one eighth of the picture or less is served from the DC image (VarDCT keeps one pixel per 8 x 8 block of
        // the picture in the first groups of the file): libjxl is asked to stop at its DC progression step and flush that image, which
        // costs a fraction of the full decode. Whether this applies needs the size, so a header-only pass runs first; the progression
        // event is only subscribed when it does (a non-progressive file or one with alpha just decodes in full, as before).
        bool wantDc = false;
        if (!req.probeOnly && req.longestSide > 0)
        {
            Request probe;
            probe.probeOnly = true;
            if (DecodeJxl(file, probe) && probe.width > 0 && probe.height > 0)
                wantDc = static_cast<std::uint64_t>(req.longestSide) * 8 * 100 <= static_cast<std::uint64_t>(std::max(probe.width, probe.height)) * 105;
        }
        JxlDecoder* dec = JxlDecoderCreate(nullptr);
        if (!dec) return false;
        struct DecGuard { JxlDecoder* d; ~DecGuard() { JxlDecoderDestroy(d); } } decGuard{ dec };
        // Colour management so ICC-profiled images (Display P3, Adobe RGB, channel-permuting profiles...) are
        // converted to sRGB instead of being shown in their native space.
        if (const JxlCmsInterface* cms = JxlGetDefaultCms()) JxlDecoderSetCms(dec, *cms);
        void* runner = nullptr;
        struct RunnerGuard { void* r; ~RunnerGuard() { if (r) JxlThreadParallelRunnerDestroy(r); } } runnerGuard{ nullptr };
        if (!req.probeOnly)
        {
            const size_t threads = std::min<size_t>(JxlThreadParallelRunnerDefaultNumWorkerThreads(), DecodeThreads());
            if (threads > 1 && (runner = JxlThreadParallelRunnerCreate(nullptr, threads)) != nullptr)
            {
                runnerGuard.r = runner;
                if (JxlDecoderSetParallelRunner(dec, JxlThreadParallelRunner, runner) != JXL_DEC_SUCCESS) return false;
            }
        }
        const int events = req.probeOnly ? JXL_DEC_BASIC_INFO
            : (JXL_DEC_BASIC_INFO | JXL_DEC_COLOR_ENCODING | JXL_DEC_FULL_IMAGE | (wantDc ? JXL_DEC_FRAME_PROGRESSION : 0));
        if (JxlDecoderSubscribeEvents(dec, events) != JXL_DEC_SUCCESS) return false;
        if (wantDc) JxlDecoderSetProgressiveDetail(dec, kDC);
        JxlDecoderSetKeepOrientation(dec, JXL_FALSE);
        JxlDecoderSetUnpremultiplyAlpha(dec, JXL_TRUE);

        std::vector<std::uint8_t> chunk;
        size_t chunkFill = 0;
        std::uint64_t offset = 0;
        const size_t chunkSize = req.probeOnly ? 64 * 1024 : 4 * 1024 * 1024;

        JxlBasicInfo info{};
        JxlPixelFormat format{ 4, JXL_TYPE_UINT8, JXL_NATIVE_ENDIAN, 0 };
        std::vector<std::uint8_t> pixels;
        bool gray = false, alpha = false;
        Bgra dc;
        DcSampler sampler{};
        std::unique_ptr<std::uint8_t[]> fullBuffer;

        for (;;)
        {
            const JxlDecoderStatus status = JxlDecoderProcessInput(dec);
            switch (status)
            {
            case JXL_DEC_NEED_MORE_INPUT:
            {
                const size_t unread = JxlDecoderReleaseInput(dec);
                if (offset >= file.Size()) return false; // truncated
                // Keep the unconsumed tail, append the next chunk.
                if (unread > 0 && chunkFill >= unread) std::memmove(chunk.data(), chunk.data() + (chunkFill - unread), unread);
                chunkFill = unread;
                if (chunk.size() < chunkFill + chunkSize) chunk.resize(chunkFill + chunkSize);
                const size_t got = file.ReadAt(offset, chunk.data() + chunkFill, chunkSize);
                if (got == 0) return false;
                offset += got;
                chunkFill += got;
                if (JxlDecoderSetInput(dec, chunk.data(), chunkFill) != JXL_DEC_SUCCESS) return false;
                if (offset >= file.Size()) JxlDecoderCloseInput(dec);
                break;
            }
            case JXL_DEC_BASIC_INFO:
            {
                if (JxlDecoderGetBasicInfo(dec, &info) != JXL_DEC_SUCCESS) return false;
                const bool swap = info.orientation >= JXL_ORIENT_TRANSPOSE;
                const std::uint32_t w = swap ? info.ysize : info.xsize, h = swap ? info.xsize : info.ysize;
                if (!DimensionsOk(w, h)) return false;
                req.width = w; req.height = h;
                if (req.probeOnly) return true;
                gray = info.num_color_channels == 1;
                alpha = info.alpha_bits > 0;
                format.num_channels = gray ? (alpha ? 2 : 1) : (alpha ? 4 : 3);
                // HDR (PQ/HLG, high intensity target): ask libjxl to tone map towards SDR white.
                if (info.intensity_target > 255.0f) JxlDecoderSetDesiredIntensityTarget(dec, 255.0f);
                break;
            }
            case JXL_DEC_COLOR_ENCODING:
            {
                JxlColorEncoding srgb{};
                srgb.color_space = gray ? JXL_COLOR_SPACE_GRAY : JXL_COLOR_SPACE_RGB;
                srgb.white_point = JXL_WHITE_POINT_D65;
                srgb.primaries = JXL_PRIMARIES_SRGB;
                srgb.transfer_function = JXL_TRANSFER_FUNCTION_SRGB;
                srgb.rendering_intent = JXL_RENDERING_INTENT_PERCEPTUAL;
                // With the default CMS this converts any image (XYB or ICC-tagged) to sRGB.
                JxlDecoderSetPreferredColorProfile(dec, &srgb);
                break;
            }
            case JXL_DEC_NEED_IMAGE_OUT_BUFFER:
            {
                if (wantDc && !alpha)
                {
                    const size_t bytes = static_cast<size_t>(req.width) * req.height * format.num_channels;
                    fullBuffer.reset(new (std::nothrow) std::uint8_t[bytes]);
                    sampler = DcSampler{ req.width, req.height, (req.width + 7) / 8, (req.height + 7) / 8, format.num_channels, &dc, fullBuffer.get(), false };
                    if (!fullBuffer || !dc.Allocate(sampler.cols, sampler.rows)) return false;
                    std::memset(dc.data, 0, dc.Bytes());
                    if (JxlDecoderSetImageOutCallback(dec, &format, JxlDcCallback, &sampler) != JXL_DEC_SUCCESS) return false;
                    break;
                }
                size_t bytes = 0;
                if (JxlDecoderImageOutBufferSize(dec, &format, &bytes) != JXL_DEC_SUCCESS) return false;
                if (bytes != static_cast<size_t>(req.width) * req.height * format.num_channels) return false;
                pixels.resize(bytes);
                if (JxlDecoderSetImageOutBuffer(dec, &format, pixels.data(), pixels.size()) != JXL_DEC_SUCCESS) return false;
                break;
            }
            case JXL_DEC_FRAME_PROGRESSION:
            {
                // The DC image (one eighth of the size, rendered at full size as a smooth upscale) is ready: the callback keeps one pixel per
                // 8 x 8 block, which is the DC picture itself. Take it and stop; a file without a DC step just decodes in full.
                if (!dc.data || alpha || JxlDecoderGetIntendedDownsamplingRatio(dec) < 8) break;
                sampler.flushing = true;
                const bool flushed = JxlDecoderFlushImage(dec) == JXL_DEC_SUCCESS;
                sampler.flushing = false;
                if (!flushed) break;
                req.image = static_cast<Bgra&&>(dc);
                return true;
            }
            case JXL_DEC_FULL_IMAGE:
            {
                // First displayed frame (coalesced) is complete; animation frames beyond it are ignored.
                const std::uint8_t* s = !pixels.empty() ? pixels.data() : fullBuffer.get();
                if (!s || !req.image.Allocate(req.width, req.height)) return false;
                const std::uint32_t n = format.num_channels;
                const std::uint8_t* const source = s;
                ForRowBands(req.height, static_cast<std::uint64_t>(req.width) * req.height, [&](std::uint32_t yBegin, std::uint32_t yEnd)
                {
                    for (std::uint32_t y = yBegin; y < yEnd; ++y)
                    {
                        const std::uint8_t* p = source + static_cast<size_t>(y) * req.width * n;
                        std::uint8_t* d = req.image.Row(y);
                        for (std::uint32_t x = 0; x < req.width; ++x, p += n, d += 4)
                        {
                            if (n == 1) Store(d, p[0], p[0], p[0], 255);
                            else if (n == 2) Store(d, p[0], p[0], p[0], p[1]);
                            else if (n == 3) Store(d, p[0], p[1], p[2], 255);
                            else Store(d, p[0], p[1], p[2], p[3]);
                        }
                    }
                });
                return true;
            }
            case JXL_DEC_SUCCESS:
                return false; // finished without producing a frame
            default:
                return false; // JXL_DEC_ERROR or an unexpected event
            }
        }
    }

    // ---------------------------------------------------------------------------------------------
    // JPEG 2000 via OpenJPEG (decode entry points only; the encoder is discarded by /OPT:REF).
    struct OpjStreamState { const File* file; std::uint64_t base; std::uint64_t length; std::uint64_t position; };

    OPJ_SIZE_T OpjRead(void* buffer, OPJ_SIZE_T bytes, void* user)
    {
        auto* s = static_cast<OpjStreamState*>(user);
        if (s->position >= s->length) return static_cast<OPJ_SIZE_T>(-1);
        const size_t want = static_cast<size_t>(std::min<std::uint64_t>(bytes, s->length - s->position));
        const size_t got = s->file->ReadAt(s->base + s->position, buffer, want);
        s->position += got;
        return got ? got : static_cast<OPJ_SIZE_T>(-1);
    }
    OPJ_OFF_T OpjSkip(OPJ_OFF_T bytes, void* user)
    {
        auto* s = static_cast<OpjStreamState*>(user);
        if (bytes < 0)
        {
            const auto back = static_cast<std::uint64_t>(-bytes);
            if (back > s->position) return -1;
            s->position -= back;
            return bytes;
        }
        const std::uint64_t target = std::min<std::uint64_t>(s->position + static_cast<std::uint64_t>(bytes), s->length);
        const auto skipped = static_cast<OPJ_OFF_T>(target - s->position);
        s->position = target;
        return skipped;
    }
    OPJ_BOOL OpjSeek(OPJ_OFF_T position, void* user)
    {
        auto* s = static_cast<OpjStreamState*>(user);
        if (position < 0 || static_cast<std::uint64_t>(position) > s->length) return OPJ_FALSE;
        s->position = static_cast<std::uint64_t>(position);
        return OPJ_TRUE;
    }
    void OpjQuiet(const char*, void*) {}

    std::uint8_t To8(const opj_image_comp_t& c, std::uint32_t x, std::uint32_t y)
    {
        x = std::min(x, c.w - 1); y = std::min(y, c.h - 1);
        std::int64_t v = c.data[static_cast<size_t>(y) * c.w + x];
        const std::uint32_t prec = std::clamp<std::uint32_t>(c.prec, 1, 31);
        if (c.sgnd) v += std::int64_t{ 1 } << (prec - 1);
        const std::int64_t max = (std::int64_t{ 1 } << prec) - 1;
        v = std::clamp<std::int64_t>(v, 0, max);
        if (prec == 8) return static_cast<std::uint8_t>(v);
        return static_cast<std::uint8_t>((v * 255 + max / 2) / max);
    }

    bool ConvertOpj(const opj_image_t* image, std::uint32_t reduce, Request& req)
    {
        const std::uint32_t n = image->numcomps;
        if (n == 0 || !image->comps) return false;
        for (std::uint32_t i = 0; i < n; ++i)
            if (!image->comps[i].data || image->comps[i].w == 0 || image->comps[i].h == 0 || image->comps[i].dx == 0 || image->comps[i].dy == 0) return false;
        const opj_image_comp_t& base = image->comps[0];
        // The picture is the reference grid (image x0..x1 / y0..y1, reduced by the resolution factor), not
        // component 0: a file whose components are all subsampled (e.g. 4x4) still shows its full extent.
        const auto ceilShift = [](std::uint64_t v, std::uint32_t r) { return (v + ((1ull << r) - 1)) >> r; };
        const std::uint64_t gridW = ceilShift(image->x1, reduce) - ceilShift(image->x0, reduce);
        const std::uint64_t gridH = ceilShift(image->y1, reduce) - ceilShift(image->y0, reduce);
        if (gridW == 0 || gridH == 0 || !DimensionsOk(gridW, gridH)) return false;
        const std::uint32_t w = static_cast<std::uint32_t>(gridW), h = static_cast<std::uint32_t>(gridH);
        if (!req.image.Allocate(w, h)) return false;

        // Channel roles: colour components first, alpha from the JP2 cdef box when present, else
        // the conventional 2 (gray+alpha) / 4 (RGB+alpha) layout of a raw codestream.
        int alphaIndex = -1;
        bool alphaPremultiplied = false;
        for (std::uint32_t i = 0; i < n; ++i)
            if (image->comps[i].alpha) { alphaIndex = static_cast<int>(i); alphaPremultiplied = image->comps[i].alpha == 2; break; }
        const bool cmyk = image->color_space == OPJ_CLRSPC_CMYK && n >= 4;
        if (alphaIndex < 0 && !cmyk && (n == 2 || n == 4)) alphaIndex = static_cast<int>(n - 1);
        const std::uint32_t colour = std::min<std::uint32_t>(cmyk ? 4u : (n >= 3 ? 3u : 1u), n);
        const bool ycc = colour == 3 && (image->color_space == OPJ_CLRSPC_SYCC || image->color_space == OPJ_CLRSPC_EYCC ||
            ((image->color_space == OPJ_CLRSPC_UNKNOWN || image->color_space == OPJ_CLRSPC_UNSPECIFIED) &&
             (image->comps[1].dx != base.dx || image->comps[1].dy != base.dy)));

        auto sample = [&](std::uint32_t c, std::uint32_t x, std::uint32_t y) -> std::uint8_t {
            const opj_image_comp_t& comp = image->comps[c];
            return To8(comp, x / comp.dx, y / comp.dy);
        };
        for (std::uint32_t y = 0; y < h; ++y)
        {
            std::uint8_t* d = req.image.Row(y);
            for (std::uint32_t x = 0; x < w; ++x, d += 4)
            {
                std::int32_t r, g, b;
                if (cmyk)
                {
                    const std::int32_t c = sample(0, x, y), m = sample(1, x, y), ye = sample(2, x, y), k = sample(3, x, y);
                    r = (255 - c) * (255 - k) / 255; g = (255 - m) * (255 - k) / 255; b = (255 - ye) * (255 - k) / 255;
                }
                else if (colour >= 3)
                {
                    r = sample(0, x, y); g = sample(1, x, y); b = sample(2, x, y);
                    if (ycc)
                    {
                        const float Y = static_cast<float>(r), cb = static_cast<float>(g) - 128.0f, cr = static_cast<float>(b) - 128.0f;
                        r = static_cast<std::int32_t>(Y + 1.402f * cr + 0.5f);
                        g = static_cast<std::int32_t>(Y - 0.344136f * cb - 0.714136f * cr + 0.5f);
                        b = static_cast<std::int32_t>(Y + 1.772f * cb + 0.5f);
                        r = std::clamp(r, 0, 255); g = std::clamp(g, 0, 255); b = std::clamp(b, 0, 255);
                    }
                }
                else r = g = b = sample(0, x, y);
                const std::uint32_t a = alphaIndex >= 0 ? sample(static_cast<std::uint32_t>(alphaIndex), x, y) : 255u;
                if (alphaPremultiplied) { d[0] = static_cast<std::uint8_t>(std::min<std::uint32_t>(b, a)); d[1] = static_cast<std::uint8_t>(std::min<std::uint32_t>(g, a)); d[2] = static_cast<std::uint8_t>(std::min<std::uint32_t>(r, a)); d[3] = static_cast<std::uint8_t>(a); }
                else Store(d, static_cast<std::uint32_t>(r), static_cast<std::uint32_t>(g), static_cast<std::uint32_t>(b), a);
            }
        }
        return true;
    }

    bool DecodeOpjRange(const File& file, std::uint64_t base, std::uint64_t length, OPJ_CODEC_FORMAT codecFormat, Request& req)
    {
        opj_codec_t* codec = opj_create_decompress(codecFormat);
        if (!codec) return false;
        struct CodecGuard { opj_codec_t* c; ~CodecGuard() { opj_destroy_codec(c); } } codecGuard{ codec };
        opj_set_info_handler(codec, OpjQuiet, nullptr);
        opj_set_warning_handler(codec, OpjQuiet, nullptr);
        opj_set_error_handler(codec, OpjQuiet, nullptr);
        opj_dparameters_t params;
        opj_set_default_decoder_parameters(&params);
        if (!opj_setup_decoder(codec, &params)) return false;
        if (!req.probeOnly)
        {
            opj_codec_set_threads(codec, static_cast<int>(DecodeThreads()));
        }

        OpjStreamState state{ &file, base, length, 0 };
        opj_stream_t* stream = opj_stream_create(1u << 20, OPJ_TRUE);
        if (!stream) return false;
        struct StreamGuard { opj_stream_t* s; ~StreamGuard() { opj_stream_destroy(s); } } streamGuard{ stream };
        opj_stream_set_user_data(stream, &state, nullptr);
        opj_stream_set_user_data_length(stream, length);
        opj_stream_set_read_function(stream, OpjRead);
        opj_stream_set_skip_function(stream, OpjSkip);
        opj_stream_set_seek_function(stream, OpjSeek);

        opj_image_t* image = nullptr;
        if (!opj_read_header(stream, codec, &image) || !image) { if (image) opj_image_destroy(image); return false; }
        struct ImageGuard { opj_image_t* i; ~ImageGuard() { opj_image_destroy(i); } } imageGuard{ image };
        if (image->x1 <= image->x0 || image->y1 <= image->y0 || image->numcomps == 0) return false;
        const std::uint64_t fullW = image->x1 - image->x0, fullH = image->y1 - image->y0;
        if (!DimensionsOk(fullW, fullH)) return false;
        req.width = static_cast<std::uint32_t>(fullW);
        req.height = static_cast<std::uint32_t>(fullH);
        if (req.probeOnly) return true;

        // Preview: let the wavelet stop early. Each discarded resolution level halves both sides
        // while staying >= the requested longest side; the remainder is area-averaged afterwards.
        std::uint32_t reduce = 0;
        if (req.longestSide > 0)
        {
            std::uint32_t levels = 0;
            if (opj_codestream_info_v2_t* cs = opj_get_cstr_info(codec))
            {
                if (cs->m_default_tile_info.tccp_info) levels = cs->m_default_tile_info.tccp_info[0].numresolutions;
                opj_destroy_cstr_info(&cs);
            }
            std::uint64_t longest = std::max(fullW, fullH);
            while (reduce + 1 < levels && ((longest + 1) >> 1) >= req.longestSide) { longest = (longest + 1) >> 1; ++reduce; }
            if (reduce > 0 && !opj_set_decoded_resolution_factor(codec, reduce)) return false;
        }
        if (!opj_decode(codec, stream, image)) return false;
        opj_end_decompress(codec, stream);
        return ConvertOpj(image, reduce, req);
    }

    // Finds the first J2K codestream (SOC + SIZ) inside a box-structured file: JPM pages, MJ2
    // samples and JPX files the JP2 reader rejects. Bounded to the first 64 MB.
    bool FindCodestream(const File& file, std::uint64_t& at)
    {
        constexpr size_t block = 1 << 20;
        const std::uint64_t limit = std::min<std::uint64_t>(file.Size(), 64ull << 20);
        std::vector<std::uint8_t> buf(block + 3);
        for (std::uint64_t pos = 0; pos < limit; pos += block)
        {
            const size_t got = file.ReadAt(pos, buf.data(), block + 3);
            if (got < 4) break;
            for (size_t i = 0; i + 3 < got; ++i)
            {
                if (buf[i] == 0xFF && buf[i + 1] == 0x4F && buf[i + 2] == 0xFF && buf[i + 3] == 0x51)
                {
                    at = pos + i;
                    return true;
                }
            }
        }
        return false;
    }

    bool DecodeJpeg2000(const File& file, const std::uint8_t* head, size_t headLen, Request& req)
    {
        const bool codestream = headLen >= 4 && head[0] == 0xFF && head[1] == 0x4F && head[2] == 0xFF && head[3] == 0x51;
        if (codestream) return DecodeOpjRange(file, 0, file.Size(), OPJ_CODEC_J2K, req);
        // JP2 family: signature box then ftyp. 'jp2 '/'jpx ' go through the JP2 reader first.
        bool jp2Brand = true;
        if (headLen >= 24 && std::memcmp(head + 16, "ftyp", 4) == 0)
            jp2Brand = std::memcmp(head + 20, "jp2 ", 4) == 0 || std::memcmp(head + 20, "jpx ", 4) == 0;
        if (jp2Brand)
        {
            Request attempt;
            attempt.probeOnly = req.probeOnly;
            attempt.longestSide = req.longestSide;
            if (DecodeOpjRange(file, 0, file.Size(), OPJ_CODEC_JP2, attempt)) { req = static_cast<Request&&>(attempt); return true; }
        }
        std::uint64_t at = 0;
        if (!FindCodestream(file, at)) return false;
        return DecodeOpjRange(file, at, file.Size() - at, OPJ_CODEC_J2K, req);
    }

    // ---------------------------------------------------------------------------------------------
    enum class Kind { Unknown, Heif, Jxl, Jpeg2000 };

    Kind Sniff(const std::uint8_t* h, size_t n)
    {
        static const std::uint8_t jxlBox[] = { 0, 0, 0, 0x0C, 'J', 'X', 'L', ' ', 0x0D, 0x0A, 0x87, 0x0A };
        static const std::uint8_t jp2Box[] = { 0, 0, 0, 0x0C, 'j', 'P', ' ', ' ', 0x0D, 0x0A, 0x87, 0x0A };
        if (n >= 2 && h[0] == 0xFF && h[1] == 0x0A) return Kind::Jxl;
        if (n >= 12 && std::memcmp(h, jxlBox, 12) == 0) return Kind::Jxl;
        if (n >= 12 && std::memcmp(h, jp2Box, 12) == 0) return Kind::Jpeg2000;
        if (n >= 4 && h[0] == 0xFF && h[1] == 0x4F && h[2] == 0xFF && h[3] == 0x51) return Kind::Jpeg2000;
        if (n >= 12 && std::memcmp(h + 4, "ftyp", 4) == 0) return Kind::Heif;
        return Kind::Unknown;
    }

    bool RunCore(const char* utf8Path, Request& req)
    {
        try
        {
            File file(utf8Path);
            if (!file.Ok()) return false;
            std::uint8_t head[32]{};
            const size_t headLen = file.ReadAt(0, head, sizeof(head));
            bool ok = false;
            switch (Sniff(head, headLen))
            {
            case Kind::Heif: ok = DecodeHeif(file, req); break;
            case Kind::Jxl: ok = DecodeJxl(file, req); break;
            case Kind::Jpeg2000: ok = DecodeJpeg2000(file, head, headLen, req); break;
            default: return false;
            }
            if (!ok) return false;
            if (req.probeOnly) return true;
            if (!req.image.data) return false;
            return Downscale(req.image, req.longestSide);
        }
        catch (...)
        {
            return false; // std::bad_alloc or any C++ exception from libheif: report failure, never unwind into Glide
        }
    }

    struct GuardedCall { const char* path; Request* req; bool ok; };

    void RunGuardedBody(GuardedCall* call) { call->ok = RunCore(call->path, *call->req); }

    int SehFilter(DWORD code)
    {
        // Contain hardware faults raised by a decoder on a hostile/malformed file so one bad image
        // cannot take Glide down; let everything else (including C++ exceptions) propagate.
        switch (code)
        {
        case EXCEPTION_ACCESS_VIOLATION:
        case EXCEPTION_ARRAY_BOUNDS_EXCEEDED:
        case EXCEPTION_DATATYPE_MISALIGNMENT:
        case EXCEPTION_IN_PAGE_ERROR:
        case EXCEPTION_INT_DIVIDE_BY_ZERO:
        case EXCEPTION_INT_OVERFLOW:
        case EXCEPTION_ILLEGAL_INSTRUCTION:
            return EXCEPTION_EXECUTE_HANDLER;
        default:
            return EXCEPTION_CONTINUE_SEARCH;
        }
    }

    bool RunGuarded(const char* utf8Path, Request& req)
    {
        GuardedCall call{ utf8Path, &req, false };
        __try { RunGuardedBody(&call); }
        __except (SehFilter(GetExceptionCode())) { return false; }
        return call.ok;
    }

    int DecodeSurface(const char* utf8Path, std::uint32_t longestSide, Surface* result)
    {
        if (!result) return 0;
        *result = Surface{};
        Request req;
        req.longestSide = longestSide;
        if (!RunGuarded(utf8Path, req) || !req.image.data) return 0;
        result->data = req.image.data;
        result->size = req.image.Bytes();
        result->width = req.image.width;
        result->height = req.image.height;
        result->stride = req.image.width * 4;
        result->pixelFormat = PixelFormatBgra8;
        result->alphaMode = AlphaPremultiplied;
        req.image.data = nullptr; // ownership transferred to Glide (released via FreeBuffer)
        return 1;
    }


    // ---------------------------------------------------------------------------------------------
    // Encoding (ABI v2 extension, SupportsEncode): JPEG 2000 (.jp2 container / .j2k codestream) through
    // OpenJPEG and WebP through libwebp's encoder. Input is one BGRA8 surface (premultiplied or straight);
    // quality >= 1 selects the lossless mode, otherwise quality (0..1) maps to WebP quality or a JPEG 2000
    // compression ratio.
    enum class EncodeKind { None, Jp2, J2k, WebP };

    EncodeKind ParseEncodeFormat(const char* format)
    {
        if (!format) return EncodeKind::None;
        if (std::strcmp(format, "jp2") == 0) return EncodeKind::Jp2;
        if (std::strcmp(format, "j2k") == 0) return EncodeKind::J2k;
        if (std::strcmp(format, "webp") == 0) return EncodeKind::WebP;
        return EncodeKind::None;
    }

    // Growable in-memory output for OpenJPEG (it seeks back to patch box lengths and markers).
    struct MemorySink
    {
        std::vector<std::uint8_t> data;
        size_t position = 0;
    };
    OPJ_SIZE_T SinkWrite(void* buffer, OPJ_SIZE_T bytes, void* user)
    {
        auto* sink = static_cast<MemorySink*>(user);
        if (sink->position + bytes > sink->data.size()) sink->data.resize(sink->position + bytes);
        std::memcpy(sink->data.data() + sink->position, buffer, bytes);
        sink->position += bytes;
        return bytes;
    }
    OPJ_OFF_T SinkSkip(OPJ_OFF_T bytes, void* user)
    {
        auto* sink = static_cast<MemorySink*>(user);
        if (bytes < 0 && static_cast<size_t>(-bytes) > sink->position) return -1;
        sink->position = static_cast<size_t>(static_cast<OPJ_OFF_T>(sink->position) + bytes);
        if (sink->position > sink->data.size()) sink->data.resize(sink->position);
        return bytes;
    }
    OPJ_BOOL SinkSeek(OPJ_OFF_T position, void* user)
    {
        auto* sink = static_cast<MemorySink*>(user);
        if (position < 0) return OPJ_FALSE;
        sink->position = static_cast<size_t>(position);
        if (sink->position > sink->data.size()) sink->data.resize(sink->position);
        return OPJ_TRUE;
    }

    bool EncodeJpeg2000(const std::uint8_t* bgra, std::uint32_t width, std::uint32_t height, bool codestream,
                        float quality, std::vector<std::uint8_t>& output)
    {
        bool alpha = false;
        const size_t pixels = static_cast<size_t>(width) * height;
        for (size_t i = 0; i < pixels && !alpha; ++i) alpha = bgra[i * 4 + 3] != 255;
        const OPJ_UINT32 components = alpha ? 4 : 3;
        opj_image_cmptparm_t parameters[4]{};
        for (OPJ_UINT32 c = 0; c < components; ++c)
        {
            parameters[c].dx = 1; parameters[c].dy = 1;
            parameters[c].w = width; parameters[c].h = height;
            parameters[c].prec = 8; parameters[c].sgnd = 0;
        }
        opj_image_t* image = opj_image_create(components, parameters, OPJ_CLRSPC_SRGB);
        if (!image) return false;
        struct ImageGuard { opj_image_t* i; ~ImageGuard() { opj_image_destroy(i); } } imageGuard{ image };
        image->x0 = 0; image->y0 = 0; image->x1 = width; image->y1 = height;
        for (size_t i = 0; i < pixels; ++i)
        {
            image->comps[0].data[i] = bgra[i * 4 + 2];
            image->comps[1].data[i] = bgra[i * 4 + 1];
            image->comps[2].data[i] = bgra[i * 4 + 0];
            if (alpha) image->comps[3].data[i] = bgra[i * 4 + 3];
        }
        if (alpha) image->comps[3].alpha = 1;

        opj_cparameters_t p;
        opj_set_default_encoder_parameters(&p);
        p.tcp_numlayers = 1;
        p.cp_disto_alloc = 1;
        if (quality >= 1.0f)
        {
            p.irreversible = 0;      // 5/3 reversible wavelet + RCT: bit-exact
            p.tcp_rates[0] = 0;      // 0 = lossless
        }
        else
        {
            // quality -> compression ratio: 0.92 ~ 6:1 (visually lossless), 0.5 ~ 45:1, 0.05 ~ 95:1.
            p.irreversible = 1;
            const float q = std::clamp(quality, 0.0f, 1.0f);
            p.tcp_rates[0] = 1.0f + 100.0f * std::pow(1.0f - q, 1.2f);
        }
        p.tcp_mct = components >= 3 ? 1 : 0;
        // Default is 6 resolution levels; small images need fewer (each level halves the smallest side).
        int resolutions = 6;
        while (resolutions > 1 && (std::min(width, height) >> (resolutions - 1)) == 0) --resolutions;
        p.numresolution = resolutions;

        opj_codec_t* codec = opj_create_compress(codestream ? OPJ_CODEC_J2K : OPJ_CODEC_JP2);
        if (!codec) return false;
        struct CodecGuard { opj_codec_t* c; ~CodecGuard() { opj_destroy_codec(c); } } codecGuard{ codec };
        opj_set_info_handler(codec, OpjQuiet, nullptr);
        opj_set_warning_handler(codec, OpjQuiet, nullptr);
        opj_set_error_handler(codec, OpjQuiet, nullptr);
        if (!opj_setup_encoder(codec, &p, image)) return false;
        opj_codec_set_threads(codec, static_cast<int>(DecodeThreads()));

        MemorySink sink;
        sink.data.reserve(std::min<size_t>(pixels * components, size_t{ 64 } << 20) / 2 + 4096);
        opj_stream_t* stream = opj_stream_create(1u << 20, OPJ_FALSE);
        if (!stream) return false;
        struct StreamGuard { opj_stream_t* s; ~StreamGuard() { opj_stream_destroy(s); } } streamGuard{ stream };
        opj_stream_set_user_data(stream, &sink, nullptr);
        opj_stream_set_write_function(stream, SinkWrite);
        opj_stream_set_skip_function(stream, SinkSkip);
        opj_stream_set_seek_function(stream, SinkSeek);
        if (!opj_start_compress(codec, image, stream)) return false;
        if (!opj_encode(codec, stream)) return false;
        if (!opj_end_compress(codec, stream)) return false;
        output.swap(sink.data);
        return !output.empty();
    }

    bool EncodeWebP(const std::uint8_t* bgra, std::uint32_t width, std::uint32_t height, float quality,
                    std::vector<std::uint8_t>& output)
    {
        if (width > WEBP_MAX_DIMENSION || height > WEBP_MAX_DIMENSION) return false;
        std::uint8_t* encoded = nullptr;
        const int stride = static_cast<int>(width * 4);
        const size_t size = quality >= 1.0f
            ? WebPEncodeLosslessBGRA(bgra, static_cast<int>(width), static_cast<int>(height), stride, &encoded)
            : WebPEncodeBGRA(bgra, static_cast<int>(width), static_cast<int>(height), stride,
                std::clamp(quality, 0.0f, 1.0f) * 100.0f, &encoded);
        if (size == 0 || !encoded) { if (encoded) WebPFree(encoded); return false; }
        output.assign(encoded, encoded + size);
        WebPFree(encoded);
        return true;
    }

    bool WriteWholeFile(const char* utf8Path, const std::vector<std::uint8_t>& bytes)
    {
        const int chars = MultiByteToWideChar(CP_UTF8, MB_ERR_INVALID_CHARS, utf8Path, -1, nullptr, 0);
        if (chars <= 0 || chars > 32768) return false;
        std::vector<wchar_t> wide(static_cast<size_t>(chars));
        if (MultiByteToWideChar(CP_UTF8, MB_ERR_INVALID_CHARS, utf8Path, -1, wide.data(), chars) != chars) return false;
        HANDLE file = CreateFileW(wide.data(), GENERIC_WRITE, 0, nullptr, CREATE_ALWAYS, FILE_ATTRIBUTE_NORMAL, nullptr);
        if (file == INVALID_HANDLE_VALUE) return false;
        size_t done = 0;
        bool ok = true;
        while (ok && done < bytes.size())
        {
            const DWORD want = static_cast<DWORD>(std::min<size_t>(bytes.size() - done, 1u << 30));
            DWORD wrote = 0;
            ok = WriteFile(file, bytes.data() + done, want, &wrote, nullptr) && wrote == want;
            done += wrote;
        }
        ok = CloseHandle(file) && ok;
        if (!ok) DeleteFileW(wide.data());
        return ok;
    }

    struct EncodeCall
    {
        const char* path;
        const Surface* surface;
        EncodeKind kind;
        float quality;
        bool ok;
    };

    void EncodeBody(EncodeCall* call)
    {
        const Surface& s = *call->surface;
        // Straight (unassociated) BGRA, tightly packed: both encoders store unassociated alpha.
        std::vector<std::uint8_t> straight(static_cast<size_t>(s.width) * s.height * 4);
        for (std::uint32_t y = 0; y < s.height; ++y)
        {
            const auto* row = static_cast<const std::uint8_t*>(s.data) + static_cast<size_t>(y) * s.stride;
            std::uint8_t* out = straight.data() + static_cast<size_t>(y) * s.width * 4;
            if (s.alphaMode != AlphaPremultiplied) { std::memcpy(out, row, static_cast<size_t>(s.width) * 4); continue; }
            for (std::uint32_t x = 0; x < s.width; ++x)
            {
                const std::uint8_t a = row[x * 4 + 3];
                for (int c = 0; c < 3; ++c)
                    out[x * 4 + c] = a == 0 ? 0 : a == 255 ? row[x * 4 + c]
                        : static_cast<std::uint8_t>(std::min(255u, (row[x * 4 + c] * 255u + a / 2u) / a));
                out[x * 4 + 3] = a;
            }
        }
        std::vector<std::uint8_t> encoded;
        const bool ok = call->kind == EncodeKind::WebP
            ? EncodeWebP(straight.data(), s.width, s.height, call->quality, encoded)
            : EncodeJpeg2000(straight.data(), s.width, s.height, call->kind == EncodeKind::J2k, call->quality, encoded);
        call->ok = ok && WriteWholeFile(call->path, encoded);
    }

    int EncodeGuarded(EncodeCall& call)
    {
        __try { EncodeBody(&call); }
        __except (SehFilter(GetExceptionCode())) { return 0; }
        return call.ok ? 1 : 0;
    }

    struct Instance { std::uint32_t magic; };
    constexpr std::uint32_t InstanceMagic = 0x474C4344; // 'GLCD'
}

GLIDE_CODEC_EXPORT const ProviderInfo* GLIDE_CODEC_CALL GlideCodecProvider_GetInfo() { return &Info; }

GLIDE_CODEC_EXPORT void* GLIDE_CODEC_CALL GlideCodecProvider_Create()
{
    // Reference-counted; with plugin loading compiled out this only registers the built-in decoders.
    if (heif_init(nullptr).code != heif_error_Ok) return nullptr;
    auto* instance = new (std::nothrow) Instance{ InstanceMagic };
    if (!instance) heif_deinit();
    return instance;
}

GLIDE_CODEC_EXPORT void GLIDE_CODEC_CALL GlideCodecProvider_Destroy(void* instance)
{
    auto* typed = static_cast<Instance*>(instance);
    if (!typed || typed->magic != InstanceMagic) return;
    typed->magic = 0;
    delete typed;
    heif_deinit();
}

GLIDE_CODEC_EXPORT int GLIDE_CODEC_CALL GlideCodecProvider_Probe(void* instance, const char* utf8Path, ProbeResult* result)
{
    if (!result) return 0;
    *result = ProbeResult{};
    if (!instance) return 0;
    Request req;
    req.probeOnly = true;
    if (!RunGuarded(utf8Path, req)) return 0;
    result->supported = 1;
    result->width = req.width;
    result->height = req.height;
    // Only the first (or primary) image is decoded, so report a still image even for sequences.
    result->frameCount = 1;
    result->animated = 0;
    return 1;
}

GLIDE_CODEC_EXPORT int GLIDE_CODEC_CALL GlideCodecProvider_DecodePreviewSurface(void* instance, const char* utf8Path, std::uint32_t longestSide, Surface* result)
{
    if (!instance) { if (result) *result = Surface{}; return 0; }
    return DecodeSurface(utf8Path, longestSide, result);
}

GLIDE_CODEC_EXPORT int GLIDE_CODEC_CALL GlideCodecProvider_DecodeFullSurface(void* instance, const char* utf8Path, std::uint32_t, Surface* result)
{
    if (!instance) { if (result) *result = Surface{}; return 0; }
    return DecodeSurface(utf8Path, 0, result);
}

GLIDE_CODEC_EXPORT void GLIDE_CODEC_CALL GlideCodecProvider_FreeBuffer(void* data, std::size_t)
{
    std::free(data);
}

// ABI v2 extension (SupportsEncode). Writes `surface` to utf8Path as `format` ("jp2", "j2k" or "webp").
// quality >= 1 is lossless; otherwise 0..1. Called with a null path and surface it only answers whether
// `format` is supported (1) or not (0).
GLIDE_CODEC_EXPORT int GLIDE_CODEC_CALL GlideCodecProvider_EncodeSurface(void* instance, const char* utf8Path, const Surface* surface, const char* format, float quality)
{
    auto* typed = static_cast<Instance*>(instance);
    if (!typed || typed->magic != InstanceMagic) return 0;
    const EncodeKind kind = ParseEncodeFormat(format);
    if (kind == EncodeKind::None) return 0;
    if (!utf8Path && !surface) return 1;
    if (!utf8Path || !*utf8Path || !surface || !surface->data || surface->pixelFormat != PixelFormatBgra8 ||
        (surface->alphaMode != AlphaPremultiplied && surface->alphaMode != AlphaStraight) ||
        !DimensionsOk(surface->width, surface->height) ||
        static_cast<std::uint64_t>(surface->stride) < static_cast<std::uint64_t>(surface->width) * 4 ||
        static_cast<std::uint64_t>(surface->stride) * surface->height > surface->size)
        return 0;
    if (!(quality > 0.0f)) quality = 0.92f;
    EncodeCall call{ utf8Path, surface, kind, quality, false };
    try { return EncodeGuarded(call); }
    catch (...) { return 0; }
}
