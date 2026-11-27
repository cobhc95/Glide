// 5.0.7 libjpeg-turbo JPEG path. See GlideJpeg.h and docs/perf/benchmark-report-5.0.7.md section 6.
#include "GlideJpeg.h"
#include "GlideJpegLib.h"
#include "GlideCmyk.h"
#include <objbase.h>
#include <turbojpeg.h>
#include <algorithm>
#include <atomic>
#include <cstdlib>
#include <cstring>
#include <cmath>
#include <csetjmp>
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
    bool previewTurbo = false;       // baseline first view through libjpeg-turbo DCT scaling (5.1.1: off, 5.0.7 WIC / MPF first view)
    bool previewTurboProgressive = true; // progressive bounded decode through libjpeg-turbo DCT scaling, where the caller
                                         // allows it: the prefetch/unstaged decode (WIC decoded those at full size, about 2x
                                         // slower). The staged foreground first view keeps WIC's faster first-scans preview.
    bool fullTurbo = true;           // full-resolution decode through libjpeg-turbo
    bool minScale = false;           // true: any n/8 DCT scale; false: 1/2, 1/4, 1/8 only (measured: n/8 gives no gain)
    unsigned maxThreads = 8;         // banded full decode threads (8 measured best of 4 / 8 / 16)
    bool memoryMapped = true;        // input via a read-only file mapping (measured 2-3 % faster than one ReadFile)
    bool direct = true;              // decode straight to 32bpp BGRA (false: 24bpp + expansion)
    bool embeddedPreview = true;     // first view = camera's MPF large preview when it is big enough
    bool progressiveDc = true;       // Explorer thumbnails of a progressive file: DC-only buffered-image decode (5.1.7)
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
    bool cmyk = false; // CMYK / YCCK (5.2.0): decoded as 4 samples per pixel, converted by GlideCmyk (the colours WIC shows)
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
    out.cmyk = colorspace == TJCS_CMYK || colorspace == TJCS_YCCK;
    if (colorspace != TJCS_YCbCr && colorspace != TJCS_GRAY && colorspace != TJCS_RGB && !out.cmyk) return false;
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

// 5.2.0 CMYK / YCCK: TurboJPEG decodes to packed CMYK (the stored, Adobe style samples); GlideCmyk turns them into the BGRA
// Windows shows for the same file. One band at a time, so a banded full decode converts on its own thread.
thread_local bool g_inBand = false; // set while a band of the banded decode runs: its CMYK conversion stays on that thread

bool DecodeCmyk(tjhandle h, const Input& in, std::uint8_t* dst, int pitch, int outW, int outH)
{
    std::vector<std::uint8_t> cmyk;
    try { cmyk.resize(static_cast<std::size_t>(outW) * outH * 4); } catch (...) { return false; }
    if (tj3Decompress8(h, in.data, in.size, cmyk.data(), outW * 4, TJPF_CMYK) != 0) return false;
    auto convert = [&](int yBegin, int yEnd)
    {
        for (int y = yBegin; y < yEnd; ++y)
            GlideCmykToBgra(cmyk.data() + static_cast<std::size_t>(y) * outW * 4, static_cast<std::size_t>(outW),
                            dst + static_cast<std::size_t>(y) * pitch, true);
    };
    // The lattice conversion costs about as much as the decode; a large single-thread frame (scaled first view, thumbnail, one band of
    // the banded decode never reaches this size) spreads it over the cores.
    const unsigned parts = !g_inBand && static_cast<std::uint64_t>(outW) * outH >= 1'000'000ull ? std::min<unsigned>(8u, std::max<unsigned>(1u, std::thread::hardware_concurrency())) : 1u;
    if (parts < 2) { convert(0, outH); return true; }
    std::vector<std::thread> workers;
    const int chunk = (outH + static_cast<int>(parts) - 1) / static_cast<int>(parts);
    try { for (unsigned p = 1; p < parts && static_cast<int>(p) * chunk < outH; ++p) workers.emplace_back(convert, static_cast<int>(p) * chunk, std::min(outH, static_cast<int>(p + 1) * chunk)); }
    catch (...) { for (unsigned p = static_cast<unsigned>(workers.size()) + 1; p < parts && static_cast<int>(p) * chunk < outH; ++p) convert(static_cast<int>(p) * chunk, std::min(outH, static_cast<int>(p + 1) * chunk)); }
    convert(0, std::min(outH, chunk));
    for (auto& w : workers) w.join();
    return true;
}

bool Decode(tjhandle h, const Input& in, std::uint8_t* dst, int pitch, int outW, int outH, bool direct, bool cmyk = false)
{
    if (cmyk) return DecodeCmyk(h, in, dst, pitch, outW, outH);
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

// ---- 5.1.7 second pass: DC-only decode of a progressive JPEG (libjpeg buffered-image mode) -----------------------------------
// A progressive JPEG stores its first scan(s) as the DC coefficients of every block: exactly a 1/8 size picture. libjpeg's
// buffered-image mode can output after any scan, so a thumbnail needs only that first scan (about 5 % of the file) instead of
// entropy-decoding every scan the way the TurboJPEG one-shot call must (measured 97 ms for 24 MP, now a few ms).

struct DcContext
{
    std::jmp_buf jump;
    int warnings = 0;
};

void DcErrorExit(void* cinfo)
{
    auto* c = static_cast<gjpeg::Decompress*>(cinfo);
    std::longjmp(static_cast<DcContext*>(c->client_data)->jump, 1);
}

void DcEmitMessage(void* cinfo, int level)
{
    if (level < 0) ++static_cast<DcContext*>(static_cast<gjpeg::Decompress*>(cinfo)->client_data)->warnings; // a corrupt or truncated file
}

void DcNoop(void*) {}
int DcFill(void* cinfo)
{
    // Out of data before the DC scan was complete: a truncated file. Feed an EOI so the library stops, and count a warning.
    static const std::uint8_t eoi[2] = { 0xFF, 0xD9 };
    auto* c = static_cast<gjpeg::Decompress*>(cinfo);
    ++static_cast<DcContext*>(c->client_data)->warnings;
    c->src->next_input_byte = eoi;
    c->src->bytes_in_buffer = 2;
    return 1;
}
void DcSkip(void* cinfo, long bytes)
{
    auto* src = static_cast<gjpeg::Decompress*>(cinfo)->src;
    if (bytes <= 0) return;
    const std::size_t n = std::min<std::size_t>(static_cast<std::size_t>(bytes), src->bytes_in_buffer);
    src->next_input_byte += n;
    src->bytes_in_buffer -= n;
}

// True when every component's DC coefficient has arrived (coef_bits[component][0] is -1 until then).
bool DcComplete(const gjpeg::Decompress* c)
{
    if (!c->coef_bits) return false;
    for (int i = 0; i < c->num_components; ++i)
        if (c->coef_bits[i][0] < 0) return false;
    return true;
}

// Decodes the DC scans of a progressive JPEG to a 1/8 size 32bpp BGRA picture (CoTaskMem). False on anything unusual
// (not YCbCr / grayscale, a warning or error, DC not complete within the first scans): the caller uses the one-shot path.
bool DecodeProgressiveDc(const Input& in, std::uint8_t** pixelsOut, int* widthOut, int* heightOut)
{
    alignas(16) unsigned char storage[gjpeg::kDecompressSize + 64] = {};
    auto* c = reinterpret_cast<gjpeg::Decompress*>(storage);
    gjpeg::ErrorMgr errors{};
    gjpeg::Source source{};
    DcContext context;
    std::uint8_t* volatile pixels = nullptr; // volatile: read after a longjmp
    volatile bool created = false;
    if (setjmp(context.jump))
    {
        if (created) jpeg_destroy_decompress(c);
        if (pixels) CoTaskMemFree(pixels);
        return false;
    }
    c->err = jpeg_std_error(&errors);
    errors.error_exit = DcErrorExit;
    errors.emit_message = DcEmitMessage;
    c->client_data = &context;
    jpeg_CreateDecompress(c, gjpeg::kLibVersion, gjpeg::kDecompressSize);
    created = true;
    source.next_input_byte = in.data;
    source.bytes_in_buffer = in.size;
    source.init_source = DcNoop;
    source.fill_input_buffer = DcFill;
    source.skip_input_data = DcSkip;
    source.resync_to_restart = [](void* cinfo, int desired) { return jpeg_resync_to_restart(cinfo, desired); };
    source.term_source = DcNoop;
    c->src = &source;
    jpeg_read_header(c, 1);
    const bool colourOk = (c->num_components == 3 && c->jpeg_color_space == gjpeg::YCbCr) || (c->num_components == 1 && c->jpeg_color_space == gjpeg::Grayscale);
    if (!colourOk) { jpeg_destroy_decompress(c); return false; }
    c->buffered_image = 1;
    c->scale_num = 1;
    c->scale_denom = 8;
    c->dct_method = gjpeg::Ifast;
    c->do_fancy_upsampling = 0;
    c->do_block_smoothing = 0;
    c->out_color_space = gjpeg::ExtBgra;
    jpeg_start_decompress(c);
    int completedScans = 0;
    for (;;)
    {
        const int result = jpeg_consume_input(c);
        if (result == gjpeg::Suspended) break;
        if (result == gjpeg::ScanCompleted)
        {
            ++completedScans;
            if (DcComplete(c)) goto ready;
        }
        if (result == gjpeg::ReachedEoi || completedScans > 3) break;
    }
    jpeg_destroy_decompress(c);
    return false; // DC never completed within the first scans
ready:
    if (context.warnings != 0) { jpeg_destroy_decompress(c); return false; }
    jpeg_start_output(c, c->input_scan_number);
    const int w = static_cast<int>(c->output_width), h = static_cast<int>(c->output_height);
    if (w <= 0 || h <= 0 || c->output_components != 4 || !GlideWithinDecodeCap(w, h)) { jpeg_destroy_decompress(c); return false; }
    pixels = static_cast<std::uint8_t*>(CoTaskMemAlloc(static_cast<std::size_t>(w) * h * 4));
    if (!pixels) { jpeg_destroy_decompress(c); return false; }
    while (c->output_scanline < static_cast<unsigned>(h))
    {
        std::uint8_t* row = pixels + static_cast<std::size_t>(c->output_scanline) * w * 4;
        jpeg_read_scanlines(c, &row, 1);
    }
    jpeg_finish_output(c);
    jpeg_destroy_decompress(c);
    if (context.warnings != 0) { CoTaskMemFree(pixels); return false; }
    *pixelsOut = pixels;
    *widthOut = w;
    *heightOut = h;
    return true;
}

// Whole frame at full scale into target: baseline files of 4 MP and more in horizontal bands of whole iMCU rows, one TurboJPEG instance
// per thread (each band entropy-decodes, but does not IDCT / colour-convert, the rows above it, so the gain flattens beyond 8 - 16 bands);
// progressive files and small frames on one thread.
bool DecodeFrame(tjhandle handle, const Input& in, const Header& hdr, std::uint8_t* target, int pitch, const JpegConfig& cfg)
{
    SYSTEM_INFO si{};
    GetSystemInfo(&si);
    const unsigned cap = cfg.maxThreads ? cfg.maxThreads : std::min<unsigned>(si.dwNumberOfProcessors, 16u);
    const int mcuH = tjMCUHeight[hdr.subsamp];
    unsigned bands = 1;
    if (!hdr.progressive && static_cast<std::uint64_t>(hdr.width) * hdr.height >= 4'000'000ull)
        bands = std::max(1u, std::min<unsigned>(cap, hdr.height / 256u));
    if (bands < 2) return Decode(handle, in, target, pitch, hdr.width, hdr.height, cfg.direct, hdr.cmyk);
    int bandRows = (hdr.height + static_cast<int>(bands) - 1) / static_cast<int>(bands);
    bandRows = (bandRows + mcuH - 1) / mcuH * mcuH;
    std::atomic<bool> good{ true };
    auto run = [&](unsigned b)
    {
        const int y0 = static_cast<int>(b) * bandRows;
        if (y0 >= hdr.height) return;
        const int rows = std::min(bandRows, hdr.height - y0);
        struct InBand { bool previous; InBand() : previous(g_inBand) { g_inBand = true; } ~InBand() { g_inBand = previous; } } inBand;
        Turbo t;
        Header bh;
        if (!ReadHeader(t.h, in, bh) || bh.width != hdr.width || bh.height != hdr.height ||
            tj3SetCroppingRegion(t.h, tjregion{ 0, y0, hdr.width, rows }) != 0 ||
            !Decode(t.h, in, target + static_cast<std::size_t>(y0) * pitch, pitch, hdr.width, rows, cfg.direct, hdr.cmyk))
            good = false;
    };
    std::vector<std::thread> workers;
    try { for (unsigned b = 1; b < bands; ++b) workers.emplace_back(run, b); }
    catch (...) { good = false; }
    run(0);
    for (auto& w : workers) w.join();
    return good;
}
}

bool GlideIsJpegPath(const wchar_t* path)
{
    if (!path) return false;
    const wchar_t* dot = wcsrchr(path, L'.');
    if (!dot) return false;
    return !_wcsicmp(dot, L".jpg") || !_wcsicmp(dot, L".jpeg") || !_wcsicmp(dot, L".jpe") || !_wcsicmp(dot, L".jfif") || !_wcsicmp(dot, L".jif");
}

namespace
{
// The bounded decode once the compressed file is in `in` (read from disk, mapped, or handed over in memory).
bool TurboDecodeBoundedFromInput(const Input& in, std::uint32_t maxWidth, std::uint32_t maxHeight, bool progressiveScaled, GlideDecodedImage* image,
                                 bool thumbnail);
}

bool GlideTurboDecodeBounded(const wchar_t* path, std::uint32_t maxWidth, std::uint32_t maxHeight, bool progressiveScaled, GlideDecodedImage* image,
                             bool thumbnail)
{
    const JpegConfig& cfg = Config();
    if (thumbnail) g_lastJpegThumbnailRoute = 0;
    if (!image || !(thumbnail || cfg.previewTurbo || cfg.previewTurboProgressive || cfg.embeddedPreview) || !GlideIsJpegPath(path) || maxWidth == 0 || maxHeight == 0) return false;
    Input in;
    if (!in.Open(path, cfg.memoryMapped)) return false;
    return TurboDecodeBoundedFromInput(in, maxWidth, maxHeight, progressiveScaled, image, thumbnail);
}

// 5.2.0 (Explorer thumbnails): the same bounded decode of a JPEG the caller already read (the I/O stage of the thumbnail
// pipeline), so a slow first open of the file is paid once, off the decode lanes. The bytes are only read, never kept.
bool GlideTurboDecodeBoundedMemory(const std::uint8_t* data, std::size_t size, std::uint32_t maxWidth, std::uint32_t maxHeight, GlideDecodedImage* image)
{
    g_lastJpegThumbnailRoute = 0;
    if (!image || !data || size < 4 || size > 0x7FFFFFFF || maxWidth == 0 || maxHeight == 0 || data[0] != 0xFF || data[1] != 0xD8) return false;
    Input in; // borrowed memory: no handle, mapping or owned buffer, so the destructor releases nothing
    in.data = data;
    in.size = size;
    return TurboDecodeBoundedFromInput(in, maxWidth, maxHeight, true, image, true);
}

namespace
{
bool TurboDecodeBoundedFromInput(const Input& in, std::uint32_t maxWidth, std::uint32_t maxHeight, bool progressiveScaled, GlideDecodedImage* image,
                                 bool thumbnail)
{
    const JpegConfig& cfg = Config();
    Turbo turbo;
    Header hdr;
    if (!ReadHeader(turbo.h, in, hdr)) return false;
    if (hdr.cmyk && !GlideCmykReady()) return false; // WIC converts it as before
    if (!thumbnail && !cfg.embeddedPreview && (hdr.progressive ? !(cfg.previewTurboProgressive && progressiveScaled) : !cfg.previewTurbo)) return false;
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
    if (cfg.embeddedPreview || thumbnail)
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
    // Thumbnails (Explorer tab grid) always take the DCT-scaled decode: WIC's reduced decode is about 2x slower.
    // CMYK (5.2.0) always: WIC's CMYK frame goes through its single-threaded colour converter (about 0.34 s for 8 MP) whatever the size.
    const bool scaledAllowed = (thumbnail || hdr.cmyk) ? true : hdr.progressive ? (cfg.previewTurboProgressive && progressiveScaled) : cfg.previewTurbo;
    GlideJpegFirstView choice = GlideJpegChooseFirstView(hdr.width, hdr.height, needW, needH, previewLong, scaledAllowed);
    // Scaling disabled (bench H0/H3): the 5.0.7 rule, a preview of at least half the requested size.
    if (choice == GlideJpegFirstViewWic && previewLong * 2 >= std::max(needW, needH)) choice = GlideJpegFirstViewEmbedded;
    if (choice == GlideJpegFirstViewWic) return false;
    const bool useEmbedded = choice == GlideJpegFirstViewEmbedded;
    if (useEmbedded && !ReadHeader(turbo.h, embedded, hdr)) return false;
    const Input& source = useEmbedded ? embedded : in;
    // Explorer thumbnails of a progressive file: the DC scan alone is a 1/8 picture (see DecodeProgressiveDc). Used when 1/8 still
    // covers the tile; otherwise (a small source) the one-shot scaled decode below.
    if (thumbnail && !useEmbedded && hdr.progressive && cfg.progressiveDc &&
        static_cast<std::uint32_t>((hdr.width + 7) / 8) >= needW && static_cast<std::uint32_t>((hdr.height + 7) / 8) >= needH)
    {
        std::uint8_t* dcPixels = nullptr;
        int dcW = 0, dcH = 0;
        // 5.2.0: Glide's own DC reader first (no coefficient buffer); libjpeg's buffered-image mode for anything it declines.
        bool dcOk = GlideJpegDcOnly(in.data, in.size, &dcPixels, &dcW, &dcH);
        g_lastJpegThumbnailRoute = dcOk ? 1 : 0;
        if (!dcOk && DecodeProgressiveDc(in, &dcPixels, &dcW, &dcH)) { dcOk = true; g_lastJpegThumbnailRoute = 2; }
        if (dcOk)
        {
            std::uint32_t outW = static_cast<std::uint32_t>(dcW), outH = static_cast<std::uint32_t>(dcH);
            if (orientation != 1)
            {
                outW = rotated ? dcH : dcW;
                outH = rotated ? dcW : dcH;
                auto* oriented = static_cast<std::uint8_t*>(CoTaskMemAlloc(static_cast<std::size_t>(dcW) * dcH * 4));
                if (!oriented) { CoTaskMemFree(dcPixels); return false; }
                GlideOrientBgra(dcPixels, dcW, dcH, orientation, oriented, outW);
                CoTaskMemFree(dcPixels);
                dcPixels = oriented;
            }
            image->width = outW;
            image->height = outH;
            image->stride = outW * 4;
            image->bufferBytes = static_cast<std::uint64_t>(dcW) * dcH * 4ull;
            image->data = dcPixels;
            return true;
        }
    }
    const tjscalingfactor sf = ChooseScale(hdr.width, hdr.height, std::min<std::uint32_t>(needW, hdr.width), std::min<std::uint32_t>(needH, hdr.height), cfg.minScale);
    if (tj3SetScalingFactor(turbo.h, sf) != 0) return false;
    if (thumbnail) g_lastJpegThumbnailRoute = useEmbedded ? 4 : 3;
    if (thumbnail)
    {
        // The result is shrunk again to a tile: the fast integer IDCT and plain chroma upsampling are invisible there.
        tj3Set(turbo.h, TJPARAM_FASTDCT, 1);
        tj3Set(turbo.h, TJPARAM_FASTUPSAMPLE, 1);
    }
    const int w = Scaled(hdr.width, sf), h = Scaled(hdr.height, sf);
    const std::uint64_t bytes = static_cast<std::uint64_t>(w) * h * 4ull;
    if (!GlideWithinDecodeCap(w, h) || bytes > std::numeric_limits<UINT>::max()) return false;
    auto* pixels = static_cast<std::uint8_t*>(CoTaskMemAlloc(static_cast<std::size_t>(bytes)));
    if (!pixels) return false;
    // CMYK at full scale (a viewport that DCT scaling cannot reduce) is a whole-frame decode like the full-size one: banded and multi-threaded.
    const bool whole = hdr.cmyk && sf.denom == 1 && !useEmbedded && !thumbnail;
    if (!(whole ? DecodeFrame(turbo.h, source, hdr, pixels, w * 4, cfg) : Decode(turbo.h, source, pixels, w * 4, w, h, cfg.direct, hdr.cmyk)))
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
} // namespace

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
    if (hdr.cmyk && !GlideCmykReady()) return false;
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

    const bool ok = DecodeFrame(turbo.h, in, hdr, target, pitch, cfg);
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
    return ReadHeader(turbo.h, in, hdr) && (!hdr.cmyk || GlideCmykReady());
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

bool GlideTurboProgressiveDc(const wchar_t* path, GlideDecodedImage* image)
{
    if (!image || !GlideIsJpegPath(path)) return false;
    Input in;
    if (!in.Open(path, true)) return false;
    Turbo turbo;
    Header hdr;
    if (!ReadHeader(turbo.h, in, hdr) || !hdr.progressive) return false;
    std::uint8_t* pixels = nullptr;
    int w = 0, h = 0;
    if (!DecodeProgressiveDc(in, &pixels, &w, &h)) return false;
    image->width = static_cast<std::uint32_t>(w);
    image->height = static_cast<std::uint32_t>(h);
    image->stride = static_cast<std::uint32_t>(w) * 4;
    image->bufferBytes = static_cast<std::uint64_t>(w) * h * 4ull;
    image->data = pixels;
    return true;
}
