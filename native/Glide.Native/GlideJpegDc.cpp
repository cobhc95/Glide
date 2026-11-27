// 5.2.0 progressive JPEG thumbnails without libjpeg's coefficient buffer.
//
// libjpeg's buffered-image mode (DecodeProgressiveDc in GlideJpeg.cpp) allocates and zeroes the whole image's coefficient arrays
// before it reads the first scan: 64 x 2 bytes per block, about 132 MB for a 45 MP 4:2:0 picture. Twelve decode lanes then commit
// 1.6 GB and spend most of their time faulting in and zeroing pages (32 ms per file alone, 108 ms each with 12 in flight).
// The 1/8 picture only needs the DC coefficients of the first DC scan(s): this reader parses the markers itself, Huffman-decodes
// those scans into one 16-bit value per block (about 1.5 MB for 45 MP) and stops. Output is what libjpeg produces at scale 1/8
// for the same scans (1x1 IDCT, no fancy upsampling, YCbCr to BGRA), within a rounding step of its colour conversion.
// Anything unusual (arithmetic coding, 12-bit, non-YCbCr, a corrupt code, a DC scan that ends early) returns false: the
// caller then uses the libjpeg path as before.
#include "GlideJpeg.h"
#include <objbase.h>
#include <algorithm>
#include <cstdint>
#include <cstring>
#include <vector>

namespace
{
struct Huff
{
    bool defined = false;
    std::uint8_t lookLen[512] = {};  // 9-bit look-ahead: code length (0 = longer code)
    std::uint8_t lookSym[512] = {};
    std::int32_t maxcode[18] = {};
    std::int32_t valoffset[18] = {};
    std::uint8_t vals[256] = {};

    bool Build(const std::uint8_t* bits, const std::uint8_t* symbols, int count)
    {
        if (count > 256) return false;
        std::memcpy(vals, symbols, static_cast<std::size_t>(count));
        std::memset(lookLen, 0, sizeof lookLen);
        std::int32_t code = 0;
        int k = 0;
        for (int l = 1; l <= 16; ++l)
        {
            const int n = bits[l - 1];
            valoffset[l] = k - code;
            for (int i = 0; i < n; ++i, ++k, ++code)
            {
                if (code >= (1 << l)) return false; // over-subscribed table
                if (l <= 9)
                {
                    const int first = code << (9 - l), span = 1 << (9 - l);
                    for (int j = 0; j < span; ++j) { lookLen[first + j] = static_cast<std::uint8_t>(l); lookSym[first + j] = vals[k]; }
                }
            }
            maxcode[l] = n ? code - 1 : -1;
            code <<= 1;
        }
        maxcode[17] = 0x7FFFFFFF;
        defined = true;
        return true;
    }
};

// MSB-first bit reader over entropy-coded data: removes 0xFF00 stuffing, stops at a marker and pads with zero bits after it
// (padding consumed = the data ended early, which the caller treats as a failure).
struct Bits
{
    const std::uint8_t* p = nullptr;
    const std::uint8_t* end = nullptr;
    const std::uint8_t* marker = nullptr; // where the marker that stopped the reader starts
    std::uint64_t acc = 0;
    int count = 0;
    int pad = 0;

    void Reset(const std::uint8_t* from) { p = from; marker = nullptr; acc = 0; count = 0; pad = 0; }

    void Fill()
    {
        while (count <= 56)
        {
            std::uint32_t b = 0;
            if (!marker && p < end)
            {
                b = *p;
                if (b == 0xFF)
                {
                    const std::uint8_t next = p + 1 < end ? p[1] : 0xD9;
                    if (next == 0x00) p += 2;
                    else { marker = p; b = 0; pad += 8; }
                }
                else ++p;
            }
            else pad += 8;
            acc = (acc << 8) | b;
            count += 8;
        }
    }
    std::uint32_t Peek(int n) const { return static_cast<std::uint32_t>(acc >> (count - n)) & ((1u << n) - 1u); }
    void Skip(int n) { count -= n; }
    bool Overrun() const { return count < pad; }

    // Huffman symbol, or -1 on an invalid code. Fill() must have run (at least 27 bits buffered).
    int Decode(const Huff& h)
    {
        const std::uint32_t look = Peek(9);
        if (const int len = h.lookLen[look]) { Skip(len); return h.lookSym[look]; }
        for (int l = 10; l <= 16; ++l)
        {
            const std::int32_t code = static_cast<std::int32_t>(Peek(l));
            if (code <= h.maxcode[l]) { Skip(l); return h.vals[(h.valoffset[l] + code) & 0xFF]; }
        }
        return -1;
    }
    // Position of the next marker at or after the reader (the bytes skipped are entropy data or fill bits).
    const std::uint8_t* NextMarker() const
    {
        if (marker) return marker;
        const std::uint8_t* q = p;
        while (q + 1 < end && !(q[0] == 0xFF && q[1] != 0x00 && q[1] != 0xFF)) ++q;
        return q + 1 < end ? q : nullptr;
    }
};

struct Component
{
    int id = 0, h = 1, v = 1, tq = 0;
    int blocksW = 0, blocksH = 0;     // blocks of the component's own picture (ceil(compWidth / 8))
    int stride = 0, rows = 0;         // allocated (MCU-padded) block grid
    int dcTable = 0;
    bool dcDone = false;
    std::vector<std::int16_t> dc;
};

inline int Extend(int v, int s) { return v < (1 << (s - 1)) ? v - (1 << s) + 1 : v; }
inline std::uint8_t Clamp(int v) { return static_cast<std::uint8_t>(v < 0 ? 0 : v > 255 ? 255 : v); }
inline std::uint16_t Be16(const std::uint8_t* p) { return static_cast<std::uint16_t>((p[0] << 8) | p[1]); }
} // namespace

thread_local int g_lastJpegThumbnailRoute = 0;

bool GlideJpegDcOnly(const std::uint8_t* data, std::size_t size, std::uint8_t** pixelsOut, int* widthOut, int* heightOut)
{
    if (!data || size < 4 || data[0] != 0xFF || data[1] != 0xD8) return false;
    const std::uint8_t* const end = data + size;
    Huff dcTables[4];
    std::uint16_t quant[4] = {};
    bool quantDefined[4] = {};
    std::vector<Component> comps;
    int width = 0, height = 0, hmax = 1, vmax = 1, mcusX = 0, mcusY = 0, restart = 0;
    bool adobeRgb = false, sawFrame = false;
    const std::uint8_t* p = data + 2;
    auto allDone = [&] { return !comps.empty() && std::all_of(comps.begin(), comps.end(), [](const Component& c) { return c.dcDone; }); };
    while (!allDone())
    {
        // Next marker (fill bytes 0xFF allowed before it).
        while (p < end && *p != 0xFF) ++p;
        while (p < end && *p == 0xFF) ++p;
        if (p >= end) return false;
        const std::uint8_t m = *p++;
        if (m == 0xD8 || (m >= 0xD0 && m <= 0xD7) || m == 0x01) continue;
        if (m == 0xD9) return false; // EOI before the DC scans finished
        if (p + 2 > end) return false;
        const int len = Be16(p);
        if (len < 2 || p + len > end) return false;
        const std::uint8_t* seg = p + 2;
        const int segLen = len - 2;
        p += len;
        switch (m)
        {
        case 0xC2: // progressive, Huffman
        {
            if (sawFrame || segLen < 6 || seg[0] != 8) return false;
            sawFrame = true;
            height = Be16(seg + 1);
            width = Be16(seg + 3);
            const int n = seg[5];
            if ((n != 1 && n != 3) || segLen < 6 + 3 * n || width <= 0 || height <= 0) return false;
            comps.resize(static_cast<std::size_t>(n));
            for (int i = 0; i < n; ++i)
            {
                Component& c = comps[static_cast<std::size_t>(i)];
                c.id = seg[6 + 3 * i];
                c.h = seg[7 + 3 * i] >> 4;
                c.v = seg[7 + 3 * i] & 15;
                c.tq = seg[8 + 3 * i];
                if (c.h < 1 || c.h > 4 || c.v < 1 || c.v > 4 || c.tq > 3) return false;
                hmax = std::max(hmax, c.h);
                vmax = std::max(vmax, c.v);
            }
            if (n == 3 && comps[0].id == 'R' && comps[1].id == 'G' && comps[2].id == 'B') return false; // RGB, not YCbCr
            mcusX = (width + 8 * hmax - 1) / (8 * hmax);
            mcusY = (height + 8 * vmax - 1) / (8 * vmax);
            for (Component& c : comps)
            {
                const int cw = (width * c.h + hmax - 1) / hmax, ch = (height * c.v + vmax - 1) / vmax;
                c.blocksW = (cw + 7) / 8;
                c.blocksH = (ch + 7) / 8;
                c.stride = mcusX * c.h;
                c.rows = mcusY * c.v;
                c.dc.assign(static_cast<std::size_t>(c.stride) * c.rows, 0);
            }
            break;
        }
        case 0xC0: case 0xC1: case 0xC3: case 0xC5: case 0xC6: case 0xC7:
        case 0xC9: case 0xCA: case 0xCB: case 0xCD: case 0xCE: case 0xCF:
            return false; // baseline / lossless / arithmetic: not this reader's case
        case 0xC4: // DHT
        {
            int q = 0;
            while (q + 17 <= segLen)
            {
                const int tc = seg[q] >> 4, th = seg[q] & 15;
                if (th > 3 || tc > 1) return false;
                int count = 0;
                for (int i = 0; i < 16; ++i) count += seg[q + 1 + i];
                if (q + 17 + count > segLen) return false;
                if (tc == 0 && !dcTables[th].Build(seg + q + 1, seg + q + 17, count)) return false;
                q += 17 + count;
            }
            break;
        }
        case 0xDB: // DQT: only element 0 (the DC step) is needed
        {
            int q = 0;
            while (q < segLen)
            {
                const int pq = seg[q] >> 4, tq = seg[q] & 15;
                if (tq > 3 || pq > 1) return false;
                const int bytes = pq ? 128 : 64;
                if (q + 1 + bytes > segLen) return false;
                quant[tq] = pq ? Be16(seg + q + 1) : seg[q + 1];
                quantDefined[tq] = true;
                q += 1 + bytes;
            }
            break;
        }
        case 0xDD:
            if (segLen < 2) return false;
            restart = Be16(seg);
            break;
        case 0xEE: // Adobe: transform 0 on three components means RGB
            if (segLen >= 12 && std::memcmp(seg, "Adobe", 5) == 0 && seg[11] == 0) adobeRgb = true;
            break;
        case 0xDA:
        {
            if (!sawFrame || adobeRgb && comps.size() == 3 || segLen < 1) return false;
            const int ns = seg[0];
            if (ns < 1 || ns > 4 || segLen < 1 + 2 * ns + 3) return false;
            Component* scan[4] = {};
            for (int i = 0; i < ns; ++i)
            {
                const int id = seg[1 + 2 * i];
                for (Component& c : comps) if (c.id == id) scan[i] = &c;
                if (!scan[i]) return false;
                scan[i]->dcTable = seg[2 + 2 * i] >> 4;
                if (scan[i]->dcTable > 3) return false;
            }
            const int ss = seg[1 + 2 * ns], se = seg[2 + 2 * ns], ah = seg[3 + 2 * ns] >> 4, al = seg[3 + 2 * ns] & 15;
            Bits bits;
            bits.end = end;
            bits.Reset(p);
            if (ss != 0 || ah != 0 || se != 0)
            {
                // AC or refinement scan before every DC arrived: skip its entropy data.
                const std::uint8_t* next = bits.NextMarker();
                if (!next) return false;
                p = next;
                break;
            }
            if (al > 13) return false;
            for (int i = 0; i < ns; ++i)
                if (!dcTables[scan[i]->dcTable].defined || !quantDefined[scan[i]->tq] || scan[i]->dcDone) return false;
            int pred[4] = {};
            const bool interleaved = ns > 1;
            const int unitsX = interleaved ? mcusX : scan[0]->blocksW;
            const int unitsY = interleaved ? mcusY : scan[0]->blocksH;
            const long long units = static_cast<long long>(unitsX) * unitsY;
            int untilRestart = restart;
            int expectRst = 0;
            for (long long u = 0; u < units; ++u)
            {
                if (restart && untilRestart == 0)
                {
                    if (bits.Overrun()) return false;
                    const std::uint8_t* mk = bits.NextMarker();
                    if (!mk || mk + 1 >= end || mk[1] != 0xD0 + expectRst) return false;
                    expectRst = (expectRst + 1) & 7;
                    bits.Reset(mk + 2);
                    std::memset(pred, 0, sizeof pred);
                    untilRestart = restart;
                }
                const int ux = static_cast<int>(u % unitsX), uy = static_cast<int>(u / unitsX);
                for (int i = 0; i < ns; ++i)
                {
                    Component& c = *scan[i];
                    const Huff& table = dcTables[c.dcTable];
                    const int bh = interleaved ? c.h : 1, bv = interleaved ? c.v : 1;
                    for (int by = 0; by < bv; ++by)
                        for (int bx = 0; bx < bh; ++bx)
                        {
                            bits.Fill();
                            const int s = bits.Decode(table);
                            if (s < 0 || s > 11) return false;
                            int diff = 0;
                            if (s) { diff = Extend(static_cast<int>(bits.Peek(s)), s); bits.Skip(s); }
                            pred[i] += diff;
                            const int x = interleaved ? ux * c.h + bx : ux, y = interleaved ? uy * c.v + by : uy;
                            c.dc[static_cast<std::size_t>(y) * c.stride + x] = static_cast<std::int16_t>(std::clamp(pred[i] * (1 << al), -32768, 32767));
                        }
                }
                if (restart) --untilRestart;
            }
            if (bits.Overrun()) return false;
            for (int i = 0; i < ns; ++i) scan[i]->dcDone = true;
            const std::uint8_t* next = bits.NextMarker();
            if (!next && !allDone()) return false;
            if (next) p = next;
            break;
        }
        default:
            break; // APPn, COM and others: skipped
        }
    }

    // 1/8 picture: libjpeg's output size at scale 1/8 is ceil(width / 8) x ceil(height / 8).
    const int w = (width + 7) / 8, h = (height + 7) / 8;
    if (w <= 0 || h <= 0 || !GlideWithinDecodeCap(static_cast<std::uint64_t>(w), static_cast<std::uint64_t>(h))) return false;
    auto* pixels = static_cast<std::uint8_t*>(CoTaskMemAlloc(static_cast<std::size_t>(w) * h * 4));
    if (!pixels) return false;
    // Sample value of a block = DC * q / 8 + 128, rounded (libjpeg's 1x1 IDCT); one lookup row per component.
    auto sampleRow = [&](const Component& c, int y, std::vector<std::uint8_t>& out)
    {
        const int cy = std::min(c.rows - 1, y * c.v / vmax);
        const std::int16_t* src = c.dc.data() + static_cast<std::size_t>(cy) * c.stride;
        const int q = quant[c.tq];
        for (int x = 0; x < w; ++x)
        {
            const int cx = std::min(c.stride - 1, x * c.h / hmax);
            out[static_cast<std::size_t>(x)] = Clamp(((src[cx] * q + 4) >> 3) + 128);
        }
    };
    std::vector<std::uint8_t> yRow(static_cast<std::size_t>(w)), cbRow(static_cast<std::size_t>(w)), crRow(static_cast<std::size_t>(w));
    for (int y = 0; y < h; ++y)
    {
        std::uint8_t* dst = pixels + static_cast<std::size_t>(y) * w * 4;
        sampleRow(comps[0], y, yRow);
        if (comps.size() == 1)
        {
            for (int x = 0; x < w; ++x) { dst[4 * x] = dst[4 * x + 1] = dst[4 * x + 2] = yRow[x]; dst[4 * x + 3] = 255; }
            continue;
        }
        sampleRow(comps[1], y, cbRow);
        sampleRow(comps[2], y, crRow);
        for (int x = 0; x < w; ++x)
        {
            // JFIF YCbCr to RGB, 16.16 fixed point (libjpeg's jdcolor.c constants).
            const int yy = yRow[x], cb = cbRow[x] - 128, cr = crRow[x] - 128;
            const int r = yy + ((91881 * cr + 32768) >> 16);
            const int g = yy + ((-22554 * cb - 46802 * cr + 32768) >> 16);
            const int b = yy + ((116130 * cb + 32768) >> 16);
            dst[4 * x] = Clamp(b);
            dst[4 * x + 1] = Clamp(g);
            dst[4 * x + 2] = Clamp(r);
            dst[4 * x + 3] = 255;
        }
    }
    *pixelsOut = pixels;
    *widthOut = w;
    *heightOut = h;
    return true;
}
