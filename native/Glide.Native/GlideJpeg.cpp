// 5.0.7 libjpeg-turbo JPEG path. See GlideJpeg.h and docs/perf/benchmark-report-5.0.7.md section 6.
#include "GlideJpeg.h"
#include <objbase.h>
#include <turbojpeg.h>
#include <algorithm>
#include <atomic>
#include <cstdlib>
#include <cstring>
#include <cmath>
#include <cwchar>
#include <limits>
#include <thread>
#include <vector>

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
struct JpegConfig
{
    bool previewTurbo = true;        // baseline first view through libjpeg-turbo DCT scaling
    bool previewTurboProgressive = true; // progressive bounded decode through libjpeg-turbo DCT scaling, where the caller
                                         // allows it: the prefetch/unstaged decode (WIC decoded those at full size, about 2x
                                         // slower). The staged foreground first view keeps WIC's faster first-scans preview.
    bool fullTurbo = true;           // full-resolution decode through libjpeg-turbo
    bool minScale = false;           // true: any n/8 DCT scale; false: 1/2, 1/4, 1/8 only (measured: n/8 gives no gain)
    unsigned maxThreads = 8;         // banded full decode threads (8 measured best of 4 / 8 / 16)
    bool memoryMapped = true;        // input via a read-only file mapping (measured 2-3 % faster than one ReadFile)
    bool direct = true;              // decode straight to 32bpp BGRA (false: 24bpp + expansion)
    bool embeddedPreview = true;     // first view = camera's MPF large preview when it is big enough
};

#ifdef GLIDE_JPEG_BENCH_KNOBS
// Bench-only switches (GLIDE_JPEG_BENCH="H1;scale=pow2;threads=4;read=mmap;direct=0;thumb=1"). Never compiled
// into the shipped bridge.
JpegConfig LoadConfig()
{
    JpegConfig c;
    wchar_t buf[512]{};
    const DWORD n = GetEnvironmentVariableW(L"GLIDE_JPEG_BENCH", buf, 511);
    if (n == 0 || n >= 511) return c;
    wchar_t* ctx = nullptr;
    for (wchar_t* t = wcstok_s(buf, L";", &ctx); t; t = wcstok_s(nullptr, L";", &ctx))
    {
        if (!wcscmp(t, L"H0")) { c.previewTurbo = false; c.previewTurboProgressive = false; c.fullTurbo = false; c.embeddedPreview = false; }
        else if (!wcscmp(t, L"H1")) { c.previewTurbo = true; c.previewTurboProgressive = false; c.fullTurbo = true; }
        else if (!wcscmp(t, L"H2")) { c.previewTurbo = true; c.previewTurboProgressive = true; c.fullTurbo = true; }
        else if (!wcscmp(t, L"H3")) { c.previewTurbo = false; c.previewTurboProgressive = false; c.fullTurbo = true; }
        else if (!wcscmp(t, L"scale=pow2")) c.minScale = false;
        else if (!wcscmp(t, L"scale=min")) c.minScale = true;
        else if (!wcsncmp(t, L"threads=", 8)) c.maxThreads = static_cast<unsigned>(_wtoi(t + 8));
        else if (!wcscmp(t, L"read=mmap")) c.memoryMapped = true;
        else if (!wcscmp(t, L"direct=0")) c.direct = false;
        else if (!wcscmp(t, L"thumb=1")) c.embeddedPreview = true;
        else if (!wcscmp(t, L"thumb=0")) c.embeddedPreview = false;
        else if (!wcscmp(t, L"read=file")) c.memoryMapped = false;
    }
    return c;
}
const JpegConfig& Config() { static const JpegConfig c = LoadConfig(); return c; }
#else
const JpegConfig& Config() { static const JpegConfig c{}; return c; }
#endif

// Whole compressed file in memory (one ReadFile, or a read-only mapping).
struct Input
{
    HANDLE file = INVALID_HANDLE_VALUE, mapping = nullptr;
    const std::uint8_t* data = nullptr;
    std::size_t size = 0;
    std::uint8_t* owned = nullptr;
    ~Input()
    {
        if (mapping && data) UnmapViewOfFile(data);
        if (mapping) CloseHandle(mapping);
        if (owned) std::free(owned);
        if (file != INVALID_HANDLE_VALUE) CloseHandle(file);
    }
    bool Open(const wchar_t* path, bool mapped)
    {
        file = CreateFileW(path, GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE, nullptr, OPEN_EXISTING,
                           FILE_FLAG_SEQUENTIAL_SCAN, nullptr);
        if (file == INVALID_HANDLE_VALUE) return false;
        LARGE_INTEGER length{};
        if (!GetFileSizeEx(file, &length) || length.QuadPart < 4 || length.QuadPart > 0x7FFFFFFF) return false;
        size = static_cast<std::size_t>(length.QuadPart);
        if (mapped)
        {
            mapping = CreateFileMappingW(file, nullptr, PAGE_READONLY, 0, 0, nullptr);
            if (!mapping) return false;
            data = static_cast<const std::uint8_t*>(MapViewOfFile(mapping, FILE_MAP_READ, 0, 0, 0));
            return data != nullptr && data[0] == 0xFF && data[1] == 0xD8;
        }
        owned = static_cast<std::uint8_t*>(std::malloc(size));
        if (!owned) return false;
        std::size_t done = 0;
        while (done < size)
        {
            DWORD got = 0;
            const DWORD want = static_cast<DWORD>(std::min<std::size_t>(size - done, 1u << 30));
            if (!ReadFile(file, owned + done, want, &got, nullptr) || got == 0) return false;
            done += got;
        }
        data = owned;
        return data[0] == 0xFF && data[1] == 0xD8;
    }
};

struct Turbo
{
    tjhandle h = nullptr;
    Turbo() : h(tj3Init(TJINIT_DECOMPRESS)) {}
    ~Turbo() { if (h) tj3Destroy(h); }
    Turbo(const Turbo&) = delete;
    Turbo& operator=(const Turbo&) = delete;
};

struct Header
{
    int width = 0, height = 0, subsamp = TJSAMP_UNKNOWN;
    bool progressive = false;
};

// Reads the header and decides whether this path may decode the file. Anything unusual stays with WIC.
bool ReadHeader(tjhandle h, const Input& in, Header& out)
{
    if (!h || tj3DecompressHeader(h, in.data, in.size) != 0) return false;
    out.width = tj3Get(h, TJPARAM_JPEGWIDTH);
    out.height = tj3Get(h, TJPARAM_JPEGHEIGHT);
    out.subsamp = tj3Get(h, TJPARAM_SUBSAMP);
    out.progressive = tj3Get(h, TJPARAM_PROGRESSIVE) == 1;
    const int colorspace = tj3Get(h, TJPARAM_COLORSPACE);
    if (tj3Get(h, TJPARAM_PRECISION) != 8 || tj3Get(h, TJPARAM_LOSSLESS) == 1 || tj3Get(h, TJPARAM_ARITHMETIC) == 1) return false;
    if (colorspace != TJCS_YCbCr && colorspace != TJCS_GRAY && colorspace != TJCS_RGB) return false; // CMYK/YCCK: WIC
    if (out.subsamp == TJSAMP_UNKNOWN) return false;
    if (!GlideWithinDecodeCap(static_cast<std::uint64_t>(out.width), static_cast<std::uint64_t>(out.height))) return false;
    // Fail (and fall back to WIC) on any warning: corrupt or truncated data keeps WIC's existing behaviour.
    tj3Set(h, TJPARAM_STOPONWARNING, 1);
    tj3Set(h, TJPARAM_FASTDCT, 0);
    tj3Set(h, TJPARAM_FASTUPSAMPLE, 0);
    return true;
}

// EXIF orientation from the APP1 Exif IFD0 (tag 274), the same tag WIC's /app1/ifd/{ushort=274} query reads.
std::uint16_t ExifOrientation(const std::uint8_t* d, std::size_t n)
{
    std::size_t i = 2;
    while (i + 4 <= n && d[i] == 0xFF)
    {
        const std::uint8_t marker = d[i + 1];
        if (marker == 0xD8 || marker == 0x01 || (marker >= 0xD0 && marker <= 0xD7)) { i += 2; continue; }
        if (marker == 0xFF) { ++i; continue; }
        if (marker == 0xDA || marker == 0xD9) break;
        const std::size_t len = (static_cast<std::size_t>(d[i + 2]) << 8) | d[i + 3];
        if (len < 2 || i + 2 + len > n) break;
        if (marker == 0xE1 && len >= 16 && std::memcmp(d + i + 4, "Exif\0\0", 6) == 0)
        {
            const std::uint8_t* t = d + i + 10;
            const std::size_t tn = len - 8;
            const bool le = t[0] == 'I' && t[1] == 'I';
            if (!le && !(t[0] == 'M' && t[1] == 'M')) return 1;
            auto u16 = [&](std::size_t o) -> std::uint32_t { return o + 2 > tn ? 0u : le ? (t[o] | (t[o + 1] << 8)) : ((t[o] << 8) | t[o + 1]); };
            auto u32 = [&](std::size_t o) -> std::uint32_t { return o + 4 > tn ? 0u : le ? (t[o] | (t[o + 1] << 8) | (t[o + 2] << 16) | (static_cast<std::uint32_t>(t[o + 3]) << 24))
                                                                                          : ((static_cast<std::uint32_t>(t[o]) << 24) | (t[o + 1] << 16) | (t[o + 2] << 8) | t[o + 3]); };
            const std::size_t ifd = u32(4);
            if (ifd + 2 > tn) return 1;
            const std::uint32_t count = u16(ifd);
            for (std::uint32_t e = 0; e < count; ++e)
            {
                const std::size_t entry = ifd + 2 + static_cast<std::size_t>(e) * 12;
                if (entry + 12 > tn) break;
                if (u16(entry) != 274) continue;
                const std::uint32_t type = u16(entry + 2);
                const std::uint32_t value = type == 3 ? u16(entry + 8) : type == 4 ? u32(entry + 8) : 1;
                return (value >= 1 && value <= 8) ? static_cast<std::uint16_t>(value) : 1;
            }
            return 1;
        }
        i += 2 + len;
    }
    return 1;
}

int Scaled(int dimension, tjscalingfactor f) { return TJSCALED(dimension, f); }

// Smallest supported scale whose output still covers (w, h) >= (needW, needH).
tjscalingfactor ChooseScale(int width, int height, std::uint32_t needW, std::uint32_t needH, bool anyEighth)
{
    if (!anyEighth)
    {
        const int denom = GlideJpegChooseScaleDenom(width, height, needW, needH);
        return tjscalingfactor{ 1, denom };
    }
    tjscalingfactor best{ 1, 1 };
    for (int num = 1; num <= 8; ++num)
    {
        if (!anyEighth && num != 1 && num != 2 && num != 4 && num != 8) continue;
        const tjscalingfactor f{ num, 8 };
        if (static_cast<std::uint32_t>(Scaled(width, f)) >= needW && static_cast<std::uint32_t>(Scaled(height, f)) >= needH)
        {
            best = (num == 8) ? tjscalingfactor{ 1, 1 } : f;
            break;
        }
    }
    return best;
}

bool Decode(tjhandle h, const Input& in, std::uint8_t* dst, int pitch, int outW, int outH, bool direct)
{
    if (direct) return tj3Decompress8(h, in.data, in.size, dst, pitch, TJPF_BGRA) == 0;
    // Ablation path: 24bpp output, then expansion to 32bpp (the WIC preview's former shape).
    const std::size_t rowBytes = static_cast<std::size_t>(outW) * 3;
    std::vector<std::uint8_t> bgr;
    try { bgr.resize(rowBytes * outH); } catch (...) { return false; }
    if (tj3Decompress8(h, in.data, in.size, bgr.data(), static_cast<int>(rowBytes), TJPF_BGR) != 0) return false;
    for (int y = 0; y < outH; ++y)
    {
        const std::uint8_t* s = bgr.data() + rowBytes * y;
        std::uint8_t* d = dst + static_cast<std::size_t>(pitch) * y;
        for (int x = 0; x < outW; ++x) { d[x * 4] = s[x * 3]; d[x * 4 + 1] = s[x * 3 + 1]; d[x * 4 + 2] = s[x * 3 + 2]; d[x * 4 + 3] = 255; }
    }
    return true;
}

// Embedded preview: the largest MPF large-thumbnail image (CIPA DC-007) stored after the main image.
bool FindEmbeddedPreview(const std::uint8_t* d, std::size_t n, std::size_t& offset, std::size_t& length)
{
    offset = length = 0;
    std::size_t i = 2;
    while (i + 4 <= n && d[i] == 0xFF)
    {
        const std::uint8_t marker = d[i + 1];
        if (marker == 0xDA || marker == 0xD9) break;
        const std::size_t len = (static_cast<std::size_t>(d[i + 2]) << 8) | d[i + 3];
        if (len < 2 || i + 2 + len > n) break;
        const bool exif = false; // the EXIF IFD1 thumbnail (160 x 120) measured too small to be a useful first view
        const bool mpf = marker == 0xE2 && len >= 16 && std::memcmp(d + i + 4, "MPF\0", 4) == 0;
        if (exif || mpf)
        {
            const std::size_t base = i + (exif ? 10 : 8);
            const std::uint8_t* t = d + base;
            const std::size_t tn = std::min(n - base, exif ? len - 8 : n - base);
            const bool le = t[0] == 'I';
            auto u16 = [&](std::size_t o) -> std::uint32_t { return o + 2 > tn ? 0u : le ? (t[o] | (t[o + 1] << 8)) : ((t[o] << 8) | t[o + 1]); };
            auto u32 = [&](std::size_t o) -> std::uint32_t { return o + 4 > tn ? 0u : le ? (t[o] | (t[o + 1] << 8) | (t[o + 2] << 16) | (static_cast<std::uint32_t>(t[o + 3]) << 24))
                                                                                          : ((static_cast<std::uint32_t>(t[o]) << 24) | (t[o + 1] << 16) | (t[o + 2] << 8) | t[o + 3]); };
            std::size_t ifd = u32(4);
            if (exif)
            {
                if (ifd + 2 > tn) break;
                ifd = u32(ifd + 2 + u16(ifd) * 12); // IFD1
                if (ifd != 0 && ifd + 2 <= tn)
                {
                    std::size_t off = 0, sz = 0;
                    for (std::uint32_t e = 0, c = u16(ifd); e < c; ++e)
                    {
                        const std::size_t en = ifd + 2 + e * 12;
                        if (u16(en) == 0x0201) off = u32(en + 8);
                        if (u16(en) == 0x0202) sz = u32(en + 8);
                    }
                    if (sz > length && off + sz <= tn) { offset = base + off; length = sz; }
                }
            }
            else if (ifd + 2 <= tn)
            {
                for (std::uint32_t e = 0, c = u16(ifd); e < c; ++e)
                {
                    const std::size_t en = ifd + 2 + e * 12;
                    if (u16(en) != 0xB002) continue;
                    const std::size_t entries = u32(en + 4) / 16, at = u32(en + 8);
                    for (std::size_t k = 1; k < entries; ++k)
                    {
                        // MP type: only the large-thumbnail classes (0x010001 VGA, 0x010002 full HD). Gain maps,
                        // depth maps and other undefined (0x000000) entries are never used as a picture.
                        const std::uint32_t type = u32(at + k * 16) & 0x00FFFFFFu;
                        if (type != 0x010001u && type != 0x010002u) continue;
                        const std::size_t sz = u32(at + k * 16 + 4), off = u32(at + k * 16 + 8);
                        if (sz > length && base + off + sz <= n && off != 0) { offset = base + off; length = sz; }
                    }
                }
            }
        }
        i += 2 + len;
    }
    return length > 0;
}
}

bool GlideIsJpegPath(const wchar_t* path)
{
    if (!path) return false;
    const wchar_t* dot = wcsrchr(path, L'.');
    if (!dot) return false;
    return !_wcsicmp(dot, L".jpg") || !_wcsicmp(dot, L".jpeg") || !_wcsicmp(dot, L".jpe") || !_wcsicmp(dot, L".jfif") || !_wcsicmp(dot, L".jif");
}

bool GlideTurboDecodeBounded(const wchar_t* path, std::uint32_t maxWidth, std::uint32_t maxHeight, bool progressiveScaled, GlideDecodedImage* image)
{
    const JpegConfig& cfg = Config();
    if (!image || !(cfg.previewTurbo || cfg.previewTurboProgressive || cfg.embeddedPreview) || !GlideIsJpegPath(path) || maxWidth == 0 || maxHeight == 0) return false;
    Input in;
    if (!in.Open(path, cfg.memoryMapped)) return false;
    Turbo turbo;
    Header hdr;
    if (!ReadHeader(turbo.h, in, hdr)) return false;
    if (!cfg.embeddedPreview && (hdr.progressive ? !(cfg.previewTurboProgressive && progressiveScaled) : !cfg.previewTurbo)) return false;
    const std::uint16_t orientation = ExifOrientation(in.data, in.size);
    const bool rotated = orientation >= 5 && orientation <= 8;
    const std::uint32_t physW = rotated ? maxHeight : maxWidth, physH = rotated ? maxWidth : maxHeight;
    if (static_cast<std::uint32_t>(hdr.width) <= physW && static_cast<std::uint32_t>(hdr.height) <= physH) return false; // not a bounded request
    const double ratio = std::min(static_cast<double>(physW) / hdr.width, static_cast<double>(physH) / hdr.height);
    const std::uint32_t needW = std::max<std::uint32_t>(1, static_cast<std::uint32_t>(hdr.width * ratio));
    const std::uint32_t needH = std::max<std::uint32_t>(1, static_cast<std::uint32_t>(hdr.height * ratio));

    // Camera MPF "large thumbnail" (typically 1620 x 1080, same aspect ratio): the first view when it alone covers
    // the requested size (cheapest). A smaller one loses to a DCT-scaled decode of the main image, which is sharper;
    // refinement then replaces either as with any reduced view.
    Input embedded;
    std::uint32_t previewLong = 0;
    if (cfg.embeddedPreview)
    {
        std::size_t off = 0, len = 0;
        if (FindEmbeddedPreview(in.data, in.size, off, len))
        {
            embedded.data = in.data + off;
            embedded.size = len;
            Turbo probe;
            Header eh;
            if (ReadHeader(probe.h, embedded, eh) && !eh.progressive &&
                std::abs(static_cast<double>(eh.width) / eh.height - static_cast<double>(hdr.width) / hdr.height) <= 0.01 * hdr.width / hdr.height)
                previewLong = static_cast<std::uint32_t>(std::max(eh.width, eh.height));
        }
    }
    const bool scaledAllowed = hdr.progressive ? (cfg.previewTurboProgressive && progressiveScaled) : cfg.previewTurbo;
    GlideJpegFirstView choice = GlideJpegChooseFirstView(hdr.width, hdr.height, needW, needH, previewLong, scaledAllowed);
    // Scaling disabled (bench H0/H3): the 5.0.7 rule, a preview of at least half the requested size.
    if (choice == GlideJpegFirstViewWic && previewLong * 2 >= std::max(needW, needH)) choice = GlideJpegFirstViewEmbedded;
    if (choice == GlideJpegFirstViewWic) return false;
    const bool useEmbedded = choice == GlideJpegFirstViewEmbedded;
    if (useEmbedded && !ReadHeader(turbo.h, embedded, hdr)) return false;
    const Input& source = useEmbedded ? embedded : in;
    const tjscalingfactor sf = ChooseScale(hdr.width, hdr.height, std::min<std::uint32_t>(needW, hdr.width), std::min<std::uint32_t>(needH, hdr.height), cfg.minScale);
    if (tj3SetScalingFactor(turbo.h, sf) != 0) return false;
    const int w = Scaled(hdr.width, sf), h = Scaled(hdr.height, sf);
    const std::uint64_t bytes = static_cast<std::uint64_t>(w) * h * 4ull;
    if (!GlideWithinDecodeCap(w, h) || bytes > std::numeric_limits<UINT>::max()) return false;
    auto* pixels = static_cast<std::uint8_t*>(CoTaskMemAlloc(static_cast<std::size_t>(bytes)));
    if (!pixels) return false;
    if (!Decode(turbo.h, source, pixels, w * 4, w, h, cfg.direct))
    {
        CoTaskMemFree(pixels);
        return false;
    }
    std::uint32_t outW = static_cast<std::uint32_t>(w), outH = static_cast<std::uint32_t>(h);
    if (orientation != 1)
    {
        outW = rotated ? h : w;
        outH = rotated ? w : h;
        auto* oriented = static_cast<std::uint8_t*>(CoTaskMemAlloc(static_cast<std::size_t>(bytes)));
        if (!oriented) { CoTaskMemFree(pixels); return false; }
        GlideOrientBgra(pixels, w, h, orientation, oriented, outW);
        CoTaskMemFree(pixels);
        pixels = oriented;
    }
    image->width = outW;
    image->height = outH;
    image->stride = outW * 4;
    image->bufferBytes = bytes;
    image->data = pixels;
    return true;
}

bool GlideTurboDecodeInto(const wchar_t* path, GlideSurfaceAllocator allocate, void* context,
                          std::uint8_t** surface, std::uint32_t* stride, std::uint32_t* surfaceWidth, std::uint32_t* surfaceHeight)
{
    const JpegConfig& cfg = Config();
    if (!cfg.fullTurbo || !allocate || !GlideIsJpegPath(path)) return false;
    Input in;
    if (!in.Open(path, cfg.memoryMapped)) return false;
    Turbo turbo;
    Header hdr;
    if (!ReadHeader(turbo.h, in, hdr)) return false;
    const std::uint16_t orientation = ExifOrientation(in.data, in.size);
    const bool rotated = orientation >= 5 && orientation <= 8;
    const std::uint32_t width = static_cast<std::uint32_t>(hdr.width), height = static_cast<std::uint32_t>(hdr.height);
    const std::uint32_t outW = rotated ? height : width, outH = rotated ? width : height;

    // Orientation 1 decodes straight into the surface; other orientations decode upright, then transform.
    std::uint8_t* upright = nullptr;
    if (orientation != 1)
    {
        upright = static_cast<std::uint8_t*>(CoTaskMemAlloc(static_cast<std::size_t>(width) * height * 4));
        if (!upright) return false;
    }
    std::uint32_t dstStride = 0;
    auto* destination = static_cast<std::uint8_t*>(allocate(context, outW, outH, &dstStride));
    if (!destination || dstStride < static_cast<std::uint64_t>(outW) * 4ull ||
        static_cast<std::uint64_t>(dstStride) * outH > std::numeric_limits<UINT>::max())
    {
        if (upright) CoTaskMemFree(upright);
        return false;
    }
    *surface = destination;
    *stride = dstStride;
    *surfaceWidth = outW;
    *surfaceHeight = outH;
    std::uint8_t* target = upright ? upright : destination;
    const int pitch = upright ? static_cast<int>(width * 4) : static_cast<int>(dstStride);

    SYSTEM_INFO si{};
    GetSystemInfo(&si);
    const unsigned cap = cfg.maxThreads ? cfg.maxThreads : std::min<unsigned>(si.dwNumberOfProcessors, 16u);
    const int mcuH = tjMCUHeight[hdr.subsamp], mcuW = tjMCUWidth[hdr.subsamp];
    (void)mcuW;
    unsigned bands = 1;
    if (!hdr.progressive && static_cast<std::uint64_t>(width) * height >= 4'000'000ull)
        bands = std::max(1u, std::min<unsigned>(cap, height / 256u));
    bool ok;
    if (bands < 2)
        ok = Decode(turbo.h, in, target, pitch, hdr.width, hdr.height, cfg.direct);
    else
    {
        // Horizontal bands of whole iMCU rows, one TurboJPEG instance each. Each band entropy-decodes (but
        // does not IDCT/colour-convert) the rows above it, so the gain flattens beyond ~8-16 bands.
        int bandRows = (hdr.height + static_cast<int>(bands) - 1) / static_cast<int>(bands);
        bandRows = (bandRows + mcuH - 1) / mcuH * mcuH;
        std::atomic<bool> good{ true };
        auto run = [&](unsigned b)
        {
            const int y0 = static_cast<int>(b) * bandRows;
            if (y0 >= hdr.height) return;
            const int rows = std::min(bandRows, hdr.height - y0);
            Turbo t;
            Header bh;
            if (!ReadHeader(t.h, in, bh) || bh.width != hdr.width || bh.height != hdr.height ||
                tj3SetCroppingRegion(t.h, tjregion{ 0, y0, hdr.width, rows }) != 0 ||
                !Decode(t.h, in, target + static_cast<std::size_t>(y0) * pitch, pitch, hdr.width, rows, cfg.direct))
                good = false;
        };
        std::vector<std::thread> workers;
        try { for (unsigned b = 1; b < bands; ++b) workers.emplace_back(run, b); }
        catch (...) { good = false; }
        run(0);
        for (auto& w : workers) w.join();
        ok = good;
    }
    if (ok && upright) GlideOrientIntoSurface(upright, width, height, orientation, destination, dstStride);
    if (upright) CoTaskMemFree(upright);
    return ok;
}

bool GlideTurboAccepts(const wchar_t* path)
{
    if (!GlideIsJpegPath(path)) return false;
    Input in;
    if (!in.Open(path, true)) return false;
    Turbo turbo;
    Header hdr;
    return ReadHeader(turbo.h, in, hdr);
}

int GlideJpegChooseScaleDenom(int width, int height, std::uint32_t needW, std::uint32_t needH)
{
    // TJSCALED rounds up, as libjpeg-turbo's output_width does.
    for (int denom = 8; denom > 1; denom /= 2)
    {
        const tjscalingfactor f{ 1, denom };
        if (static_cast<std::uint32_t>(TJSCALED(width, f)) >= needW && static_cast<std::uint32_t>(TJSCALED(height, f)) >= needH)
            return denom;
    }
    return 1;
}

GlideJpegFirstView GlideJpegChooseFirstView(int width, int height, std::uint32_t needW, std::uint32_t needH,
                                            std::uint32_t previewLongSide, bool scaledAllowed)
{
    if (width <= 0 || height <= 0 || needW == 0 || needH == 0) return GlideJpegFirstViewWic;
    // Request covers the whole image: not a reduced first view (the full decode path handles it).
    if (static_cast<std::uint32_t>(width) <= needW && static_cast<std::uint32_t>(height) <= needH) return GlideJpegFirstViewWic;
    if (previewLongSide > 0 && previewLongSide >= std::max(needW, needH)) return GlideJpegFirstViewEmbedded;
    return scaledAllowed ? GlideJpegFirstViewScaled : GlideJpegFirstViewWic;
}
