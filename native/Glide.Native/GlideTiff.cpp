// 5.2.0 native TIFF reader. See GlideTiff.h.
#include "GlideTiff.h"
#include "GlideCmyk.h"
#include "GlideJpeg.h"
#include <objbase.h>
#include <algorithm>
#include <atomic>
#include <cmath>
#include <cstdlib>
#include <cstring>
#include <limits>
#include <mutex>
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
using u8 = std::uint8_t;
using u16 = std::uint16_t;
using u32 = std::uint32_t;
using u64 = std::uint64_t;

constexpr u64 MaxUnitBytes = 1ull << 29;     // 512 MB of raw samples in one strip or tile
constexpr u64 MaxUnits = 1ull << 22;         // 4 M strips / tiles
constexpr unsigned MaxLevels = 24;

// ------------------------------------------------------------------------------------------------------------------------------
// Inflate (RFC 1951) with the zlib wrapper TIFF "Deflate" uses. Two-level Huffman lookup (10 bit root, canonical fallback), 64 bit
// bit buffer. Output stops at `cap` (a tile is exactly that big); every read is bounded by the input and every match distance by
// the bytes produced so far.

struct Huff
{
    static constexpr int Root = 10;
    u16 fast[1 << Root];
    u16 count[16];
    u16 symbols[320];
    int maxLen = 0;

    // False for an over-subscribed code. Incomplete codes are accepted (a lone distance code is legal); an unassigned code decodes to an error.
    bool Build(const u8* lengths, int n)
    {
        std::memset(count, 0, sizeof(count));
        std::memset(fast, 0, sizeof(fast));
        for (int i = 0; i < n; ++i) ++count[lengths[i]];
        count[0] = 0;
        int left = 1;
        for (int len = 1; len <= 15; ++len)
        {
            left <<= 1;
            left -= count[len];
            if (left < 0) return false;
        }
        u16 offs[16];
        offs[1] = 0;
        for (int len = 1; len < 15; ++len) offs[len + 1] = static_cast<u16>(offs[len] + count[len]);
        for (int i = 0; i < n; ++i)
            if (lengths[i]) symbols[offs[lengths[i]]++] = static_cast<u16>(i);
        // canonical codes -> root table (bit-reversed: deflate packs Huffman codes MSB first into an LSB first stream)
        u32 next[16];
        u32 code = 0;
        next[0] = 0;
        for (int len = 1; len <= 15; ++len) { code = (code + count[len - 1]) << 1; next[len] = code; }
        int index = 0;
        for (int len = 1; len <= 15; ++len)
        {
            for (int c = 0; c < count[len]; ++c, ++index)
            {
                const u32 assigned = next[len]++;
                if (len > Root) continue;
                u32 rev = 0;
                for (int b = 0; b < len; ++b) rev |= ((assigned >> b) & 1u) << (len - 1 - b);
                const u16 entry = static_cast<u16>((symbols[index] << 4) | len);
                for (u32 j = rev; j < (1u << Root); j += 1u << len) fast[j] = entry;
            }
        }
        return true;
    }

    // Slow path for codes longer than the root: canonical decode one bit at a time (puff.c). Returns the symbol or -1.
    int DecodeSlow(u64 bits, int& used) const
    {
        int code = 0, first = 0, index = 0;
        for (int len = 1; len <= 15; ++len)
        {
            code |= static_cast<int>((bits >> (len - 1)) & 1);
            const int cnt = count[len];
            if (code - cnt < first) { used = len; return symbols[index + (code - first)]; }
            index += cnt;
            first += cnt;
            first <<= 1;
            code <<= 1;
        }
        return -1;
    }
};

const u16 LenBase[29] = { 3, 4, 5, 6, 7, 8, 9, 10, 11, 13, 15, 17, 19, 23, 27, 31, 35, 43, 51, 59, 67, 83, 99, 115, 131, 163, 195, 227, 258 };
const u8 LenExtra[29] = { 0, 0, 0, 0, 0, 0, 0, 0, 1, 1, 1, 1, 2, 2, 2, 2, 3, 3, 3, 3, 4, 4, 4, 4, 5, 5, 5, 5, 0 };
const u16 DistBase[30] = { 1, 2, 3, 4, 5, 7, 9, 13, 17, 25, 33, 49, 65, 97, 129, 193, 257, 385, 513, 769, 1025, 1537, 2049, 3073, 4097, 6145, 8193, 12289, 16385, 24577 };
const u8 DistExtra[30] = { 0, 0, 0, 0, 1, 1, 2, 2, 3, 3, 4, 4, 5, 5, 6, 6, 7, 7, 8, 8, 9, 9, 10, 10, 11, 11, 12, 12, 13, 13 };

struct FixedTables
{
    Huff lit, dist;
    FixedTables()
    {
        u8 l[288];
        for (int i = 0; i < 144; ++i) l[i] = 8;
        for (int i = 144; i < 256; ++i) l[i] = 9;
        for (int i = 256; i < 280; ++i) l[i] = 7;
        for (int i = 280; i < 288; ++i) l[i] = 8;
        lit.Build(l, 288);
        u8 d[30];
        for (auto& x : d) x = 5;
        dist.Build(d, 30);
    }
};

const FixedTables& Fixed() { static const FixedTables t; return t; }

struct Bits
{
    u64 buf = 0;
    unsigned cnt = 0;
    const u8* p;
    const u8* end;
    void Refill()
    {
        if (end - p >= 8)
        {
            u64 v;
            std::memcpy(&v, p, 8);
            buf |= v << cnt;
            p += (63 - cnt) >> 3;
            cnt |= 56;
        }
        else
        {
            while (cnt <= 56 && p < end) { buf |= static_cast<u64>(*p++) << cnt; cnt += 8; }
        }
    }
};

// Inflates a raw deflate stream into out[0, cap). On success *produced = bytes written (cap when the stream has more).
bool InflateRaw(const u8* in, size_t n, u8* out, size_t cap, size_t* produced)
{
    Bits b;
    b.p = in;
    b.end = in + n;
    size_t op = 0;
    auto need = [&](unsigned bits) { return b.cnt >= bits; };
    for (;;)
    {
        b.Refill();
        if (!need(3)) return false;
        const unsigned final = static_cast<unsigned>(b.buf & 1);
        const unsigned type = static_cast<unsigned>((b.buf >> 1) & 3);
        b.buf >>= 3; b.cnt -= 3;
        if (type == 0)
        {
            const unsigned drop = b.cnt & 7;
            b.buf >>= drop; b.cnt -= drop;
            b.Refill();
            if (!need(32)) return false;
            const u32 len = static_cast<u32>(b.buf & 0xFFFF), nlen = static_cast<u32>((b.buf >> 16) & 0xFFFF);
            if ((len ^ 0xFFFF) != nlen) return false;
            b.buf >>= 32; b.cnt -= 32;
            u32 remaining = len;
            while (remaining > 0 && b.cnt >= 8)
            {
                if (op >= cap) { *produced = op; return true; }
                out[op++] = static_cast<u8>(b.buf & 255); b.buf >>= 8; b.cnt -= 8; --remaining;
            }
            if (remaining > static_cast<size_t>(b.end - b.p)) return false;
            const size_t room = cap - op;
            const size_t take = std::min<size_t>(remaining, room);
            std::memcpy(out + op, b.p, take);
            op += take;
            b.p += remaining;
            if (take < remaining) { *produced = op; return true; }
        }
        else if (type == 1 || type == 2)
        {
            Huff dynLit, dynDist;
            const Huff* lit;
            const Huff* dist;
            if (type == 1) { lit = &Fixed().lit; dist = &Fixed().dist; }
            else
            {
                b.Refill();
                if (!need(14)) return false;
                const int nlit = static_cast<int>(b.buf & 31) + 257, ndist = static_cast<int>((b.buf >> 5) & 31) + 1, ncode = static_cast<int>((b.buf >> 10) & 15) + 4;
                b.buf >>= 14; b.cnt -= 14;
                if (nlit > 286 || ndist > 30) return false;
                static const u8 order[19] = { 16, 17, 18, 0, 8, 7, 9, 6, 10, 5, 11, 4, 12, 3, 13, 2, 14, 1, 15 };
                u8 cl[19] = {};
                for (int i = 0; i < ncode; ++i)
                {
                    b.Refill();
                    if (!need(3)) return false;
                    cl[order[i]] = static_cast<u8>(b.buf & 7);
                    b.buf >>= 3; b.cnt -= 3;
                }
                Huff clh;
                if (!clh.Build(cl, 19)) return false;
                u8 lengths[320] = {};
                int idx = 0;
                while (idx < nlit + ndist)
                {
                    b.Refill();
                    const u16 e = clh.fast[b.buf & ((1u << Huff::Root) - 1)];
                    int sym, used;
                    if (e) { sym = e >> 4; used = e & 15; }
                    else { sym = clh.DecodeSlow(b.buf, used); if (sym < 0) return false; }
                    if (b.cnt < static_cast<unsigned>(used)) return false;
                    b.buf >>= used; b.cnt -= used;
                    if (sym < 16) lengths[idx++] = static_cast<u8>(sym);
                    else
                    {
                        int rep, val = 0;
                        unsigned extra;
                        if (sym == 16) { if (idx == 0) return false; val = lengths[idx - 1]; extra = 2; rep = 3; }
                        else if (sym == 17) { extra = 3; rep = 3; }
                        else { extra = 7; rep = 11; }
                        if (b.cnt < extra) return false;
                        rep += static_cast<int>(b.buf & ((1u << extra) - 1));
                        b.buf >>= extra; b.cnt -= extra;
                        if (idx + rep > nlit + ndist) return false;
                        while (rep--) lengths[idx++] = static_cast<u8>(val);
                    }
                }
                if (lengths[256] == 0) return false;
                if (!dynLit.Build(lengths, nlit) || !dynDist.Build(lengths + nlit, ndist)) return false;
                lit = &dynLit; dist = &dynDist;
            }
            constexpr u32 RootMask = (1u << Huff::Root) - 1;
            for (;;)
            {
                b.Refill();
                u16 e = lit->fast[b.buf & RootMask];
                int sym, used;
                if (e) { sym = e >> 4; used = e & 15; }
                else { sym = lit->DecodeSlow(b.buf, used); if (sym < 0) return false; }
                if (b.cnt < static_cast<unsigned>(used)) return false;
                b.buf >>= used; b.cnt -= used;
                if (sym < 256)
                {
                    if (op >= cap) { *produced = op; return true; }
                    out[op++] = static_cast<u8>(sym);
                    continue;
                }
                if (sym == 256) break;
                sym -= 257;
                if (sym >= 29) return false;
                const unsigned lextra = LenExtra[sym];
                if (b.cnt < lextra) return false;
                size_t length = LenBase[sym] + static_cast<size_t>(b.buf & ((1u << lextra) - 1));
                b.buf >>= lextra; b.cnt -= lextra;
                e = dist->fast[b.buf & RootMask];
                if (e) { sym = e >> 4; used = e & 15; }
                else { sym = dist->DecodeSlow(b.buf, used); if (sym < 0) return false; }
                if (b.cnt < static_cast<unsigned>(used) || sym >= 30) return false;
                b.buf >>= used; b.cnt -= used;
                const unsigned dextra = DistExtra[sym];
                if (b.cnt < dextra) return false;
                const size_t distance = DistBase[sym] + static_cast<size_t>(b.buf & ((1u << dextra) - 1));
                b.buf >>= dextra; b.cnt -= dextra;
                if (distance > op) return false;
                const bool truncated = length > cap - op;
                if (truncated) length = cap - op;
                u8* d = out + op;
                const u8* s = d - distance;
                if (distance >= 8 && op + length + 8 <= cap)
                {
                    size_t i = 0;
                    for (; i + 8 <= length; i += 8) { u64 v; std::memcpy(&v, s + i, 8); std::memcpy(d + i, &v, 8); }
                    for (; i < length; ++i) d[i] = s[i];
                }
                else for (size_t i = 0; i < length; ++i) d[i] = s[i];
                op += length;
                if (truncated) { *produced = op; return true; }
            }
        }
        else return false;
        if (final) break;
    }
    *produced = op;
    return true;
}

bool InflateZlib(const u8* in, size_t n, u8* out, size_t cap, size_t* produced)
{
    if (n < 6) return false;
    if ((in[0] & 15) != 8 || ((static_cast<unsigned>(in[0]) << 8) | in[1]) % 31 != 0 || (in[1] & 0x20)) return false;
    return InflateRaw(in + 2, n - 2, out, cap, produced);
}

// ------------------------------------------------------------------------------------------------------------------------------
// TIFF LZW (MSB first, 9 - 12 bit codes, "early change") and PackBits.

bool LzwDecode(const u8* in, size_t n, u8* out, size_t cap, size_t* produced)
{
    u16 prefix[4096];
    u8 suffix[4096], first[4096];
    u16 length[4096];
    for (int i = 0; i < 256; ++i) { prefix[i] = 0; suffix[i] = static_cast<u8>(i); first[i] = static_cast<u8>(i); length[i] = 1; }
    unsigned next = 258, width = 9;
    int prev = -1;
    u64 buf = 0;
    unsigned cnt = 0;
    const u8* p = in;
    const u8* end = in + n;
    size_t op = 0;
    auto emit = [&](unsigned code) -> bool
    {
        const size_t len = length[code];
        if (op + len > cap) { // fill what fits
            const size_t room = cap - op;
            // walk to the prefix of the right length
            unsigned c = code;
            size_t l = len;
            while (l > room) { c = prefix[c]; --l; }
            size_t pos = room;
            while (pos > 0) { out[op + --pos] = suffix[c]; if (pos > 0) c = prefix[c]; }
            op = cap;
            return false;
        }
        unsigned c = code;
        size_t pos = len;
        while (pos > 0) { out[op + --pos] = suffix[c]; if (pos > 0) c = prefix[c]; }
        op += len;
        return true;
    };
    for (;;)
    {
        while (cnt < width && p < end) { buf = (buf << 8) | *p++; cnt += 8; }
        if (cnt < width) break; // input ended without an EOI code: accept what was produced
        const unsigned code = static_cast<unsigned>((buf >> (cnt - width)) & ((1u << width) - 1));
        cnt -= width;
        if (code == 257) break;
        if (code == 256) { next = 258; width = 9; prev = -1; continue; }
        if (prev < 0)
        {
            if (code > 255) return false;
            if (!emit(code)) { *produced = op; return true; }
            prev = static_cast<int>(code);
            continue;
        }
        if (code < next)
        {
            if (!emit(code)) { *produced = op; return true; }
            if (next < 4096)
            {
                prefix[next] = static_cast<u16>(prev); suffix[next] = first[code]; first[next] = first[prev];
                length[next] = static_cast<u16>(length[prev] + 1);
                ++next;
            }
        }
        else if (code == next && next < 4096)
        {
            prefix[next] = static_cast<u16>(prev); suffix[next] = first[prev]; first[next] = first[prev];
            length[next] = static_cast<u16>(length[prev] + 1);
            ++next;
            if (!emit(code)) { *produced = op; return true; }
        }
        else return false;
        prev = static_cast<int>(code);
        if (next >= (1u << width) - 1 && width < 12) ++width;
        if (op >= cap) break;
    }
    *produced = op;
    return true;
}

bool PackBitsDecode(const u8* in, size_t n, u8* out, size_t cap, size_t* produced)
{
    size_t ip = 0, op = 0;
    while (ip < n && op < cap)
    {
        const int c = static_cast<signed char>(in[ip++]);
        if (c >= 0)
        {
            const size_t run = static_cast<size_t>(c) + 1;
            if (ip + run > n) return false;
            const size_t take = std::min(run, cap - op);
            std::memcpy(out + op, in + ip, take);
            op += take; ip += run;
        }
        else if (c != -128)
        {
            const size_t run = static_cast<size_t>(1 - c);
            if (ip >= n) return false;
            const size_t take = std::min(run, cap - op);
            std::memset(out + op, in[ip++], take);
            op += take;
        }
    }
    *produced = op;
    return true;
}

// ------------------------------------------------------------------------------------------------------------------------------
// File mapping and TIFF structure.

struct Mapped
{
    HANDLE file = INVALID_HANDLE_VALUE, mapping = nullptr;
    const u8* data = nullptr;
    u64 size = 0;
    ~Mapped()
    {
        if (data) UnmapViewOfFile(data);
        if (mapping) CloseHandle(mapping);
        if (file != INVALID_HANDLE_VALUE) CloseHandle(file);
    }
    bool Open(const wchar_t* path)
    {
        file = CreateFileW(path, GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE, nullptr, OPEN_EXISTING, FILE_FLAG_RANDOM_ACCESS, nullptr);
        if (file == INVALID_HANDLE_VALUE) return false;
        LARGE_INTEGER length{};
        if (!GetFileSizeEx(file, &length) || length.QuadPart < 16) return false;
        size = static_cast<u64>(length.QuadPart);
        mapping = CreateFileMappingW(file, nullptr, PAGE_READONLY, 0, 0, nullptr);
        if (!mapping) return false;
        data = static_cast<const u8*>(MapViewOfFile(mapping, FILE_MAP_READ, 0, 0, 0));
        return data != nullptr;
    }
};

struct Rd
{
    const u8* p = nullptr;
    u64 n = 0;
    bool le = true, big = false;
    mutable bool bad = false;
    u64 U(u64 at, unsigned bytes) const
    {
        if (at > n || bytes > n - at) { bad = true; return 0; }
        u64 v = 0;
        if (le) for (unsigned i = bytes; i-- > 0;) v = (v << 8) | p[at + i];
        else for (unsigned i = 0; i < bytes; ++i) v = (v << 8) | p[at + i];
        return v;
    }
    u16 U16(u64 at) const { return static_cast<u16>(U(at, 2)); }
    u32 U32(u64 at) const { return static_cast<u32>(U(at, 4)); }
    u64 U64(u64 at) const { return U(at, 8); }
};

struct Entry
{
    u16 tag = 0, type = 0;
    u64 count = 0;
    u64 valueAt = 0; // file offset of the inline value / offset field
    bool present = false;
};

unsigned TypeSize(unsigned type)
{
    static const unsigned sizes[19] = { 0, 1, 1, 2, 4, 8, 1, 1, 2, 4, 8, 4, 8, 4, 0, 0, 8, 8, 8 };
    return type < 19 ? sizes[type] : 0;
}

// Where the entry's data lives (inline field or the offset it holds); false when it does not fit the file.
bool DataAt(const Rd& rd, const Entry& e, u64& at)
{
    const unsigned size = TypeSize(e.type);
    if (!e.present || size == 0 || e.count == 0) return false;
    if (e.count > std::numeric_limits<u64>::max() / size) return false;
    const u64 total = e.count * size;
    const u64 inlineBytes = rd.big ? 8 : 4;
    if (total <= inlineBytes) at = e.valueAt;
    else at = rd.big ? rd.U64(e.valueAt) : rd.U32(e.valueAt);
    return !rd.bad && at <= rd.n && total <= rd.n - at;
}

u64 Element(const Rd& rd, const Entry& e, u64 dataAt, u64 i)
{
    switch (e.type)
    {
    case 1: case 2: case 6: case 7: return rd.U(dataAt + i, 1);
    case 3: case 8: return rd.U(dataAt + i * 2, 2);
    case 4: case 9: case 13: return rd.U(dataAt + i * 4, 4);
    case 16: case 17: case 18: return rd.U(dataAt + i * 8, 8);
    default: return 0;
    }
}

bool Scalar(const Rd& rd, const Entry& e, u64& v)
{
    u64 at;
    if (!DataAt(rd, e, at)) return false;
    v = Element(rd, e, at, 0);
    return !rd.bad;
}

struct Ifd
{
    u64 offset = 0, next = 0;
    u32 w = 0, h = 0, rps = 0, tw = 0, th = 0;
    u16 compression = 1, photometric = 0xFFFF, planar = 1, predictor = 1, orientation = 1, spp = 1, fillOrder = 1, sampleFormat = 1;
    u32 subfile = 0;
    u16 bps = 0;
    bool bpsUniform = true, bpsSeen = false;
    int extra = -1;          // first ExtraSamples value
    u64 extraCount = 0;
    bool tiled = false;
    Entry offsets, counts, subIfds;
    bool hasW = false, hasH = false;
    u64 units = 0;
    u32 across = 0;
};

bool ParseIfd(const Rd& rd, u64 offset, Ifd& ifd)
{
    rd.bad = false;
    ifd = Ifd{};
    ifd.offset = offset;
    const unsigned entrySize = rd.big ? 20 : 12;
    const u64 count = rd.big ? rd.U64(offset) : rd.U16(offset);
    if (rd.bad || count == 0 || count > 4096) return false;
    const u64 first = offset + (rd.big ? 8 : 2);
    if (first > rd.n || count * entrySize + (rd.big ? 8 : 4) > rd.n - first) return false;
    Entry stripOffsets, stripCounts, tileOffsets, tileCounts, bitsE, extraE;
    bool haveTileW = false, haveTileH = false;
    for (u64 i = 0; i < count; ++i)
    {
        const u64 at = first + i * entrySize;
        Entry e;
        e.tag = rd.U16(at);
        e.type = rd.U16(at + 2);
        e.count = rd.big ? rd.U64(at + 4) : rd.U32(at + 4);
        e.valueAt = at + (rd.big ? 12 : 8);
        e.present = true;
        u64 v = 0;
        switch (e.tag)
        {
        case 254: if (Scalar(rd, e, v)) ifd.subfile = static_cast<u32>(v); break;
        case 256: if (Scalar(rd, e, v)) { ifd.w = static_cast<u32>(v); ifd.hasW = v > 0 && v <= 0xFFFFFFFFull; } break;
        case 257: if (Scalar(rd, e, v)) { ifd.h = static_cast<u32>(v); ifd.hasH = v > 0 && v <= 0xFFFFFFFFull; } break;
        case 258: bitsE = e; break;
        case 259: if (Scalar(rd, e, v)) ifd.compression = static_cast<u16>(v); else ifd.compression = 0; break;
        case 262: if (Scalar(rd, e, v)) ifd.photometric = static_cast<u16>(v); break;
        case 266: if (Scalar(rd, e, v)) ifd.fillOrder = static_cast<u16>(v); break;
        case 273: stripOffsets = e; break;
        case 274: if (Scalar(rd, e, v) && v >= 1 && v <= 8) ifd.orientation = static_cast<u16>(v); break;
        case 277: if (Scalar(rd, e, v)) ifd.spp = static_cast<u16>(v); break;
        case 278: if (Scalar(rd, e, v)) ifd.rps = static_cast<u32>(std::min<u64>(v, 0xFFFFFFFFull)); break;
        case 279: stripCounts = e; break;
        case 284: if (Scalar(rd, e, v)) ifd.planar = static_cast<u16>(v); break;
        case 317: if (Scalar(rd, e, v)) ifd.predictor = static_cast<u16>(v); break;
        case 322: if (Scalar(rd, e, v)) { ifd.tw = static_cast<u32>(std::min<u64>(v, 0xFFFFFFFFull)); haveTileW = true; } break;
        case 323: if (Scalar(rd, e, v)) { ifd.th = static_cast<u32>(std::min<u64>(v, 0xFFFFFFFFull)); haveTileH = true; } break;
        case 324: tileOffsets = e; break;
        case 325: tileCounts = e; break;
        case 330: ifd.subIfds = e; break;
        case 338: extraE = e; break;
        case 339: if (Scalar(rd, e, v)) ifd.sampleFormat = static_cast<u16>(v); break;
        default: break;
        }
    }
    ifd.next = rd.big ? rd.U64(first + count * entrySize) : rd.U32(first + count * entrySize);
    if (rd.bad || !ifd.hasW || !ifd.hasH) return false;
    // BitsPerSample: every sample must have the same depth
    if (bitsE.present)
    {
        u64 at;
        if (!DataAt(rd, bitsE, at) || bitsE.count > 8) return false;
        ifd.bps = static_cast<u16>(Element(rd, bitsE, at, 0));
        for (u64 i = 1; i < bitsE.count; ++i) if (Element(rd, bitsE, at, i) != ifd.bps) ifd.bpsUniform = false;
        ifd.bpsSeen = true;
    }
    else { ifd.bps = 1; ifd.bpsSeen = true; }
    if (extraE.present)
    {
        u64 at;
        if (!DataAt(rd, extraE, at)) return false;
        ifd.extraCount = extraE.count;
        ifd.extra = static_cast<int>(Element(rd, extraE, at, 0));
    }
    ifd.tiled = haveTileW && haveTileH && tileOffsets.present;
    if (ifd.tiled)
    {
        ifd.offsets = tileOffsets; ifd.counts = tileCounts;
        if (ifd.tw == 0 || ifd.th == 0 || ifd.tw > 65536 || ifd.th > 65536) return false;
        ifd.across = (ifd.w + ifd.tw - 1) / ifd.tw;
        const u64 down = (static_cast<u64>(ifd.h) + ifd.th - 1) / ifd.th;
        ifd.units = static_cast<u64>(ifd.across) * down;
    }
    else
    {
        ifd.offsets = stripOffsets; ifd.counts = stripCounts;
        if (!stripOffsets.present) return false;
        if (ifd.rps == 0 || ifd.rps > ifd.h) ifd.rps = ifd.h;
        ifd.units = (static_cast<u64>(ifd.h) + ifd.rps - 1) / ifd.rps;
    }
    if (ifd.units == 0 || ifd.units > MaxUnits) return false;
    return !rd.bad;
}

enum class Mode : u8 { Gray, GrayA, Rgb, Rgba, Cmyk };

struct Layout
{
    Mode mode = Mode::Rgb;
    u32 bytesPerSample = 1, spp = 3, pixelBytes = 3;
    bool alphaAssociated = false, whiteIsZero = false;
};

// Supported variants: contiguous 8 / 16 bit unsigned gray, gray + alpha, RGB, RGBA, 8 bit CMYK; none / LZW / Deflate / PackBits;
// predictor 1 or 2. Everything else stays with WIC.
bool MakeLayout(const Rd& rd, const Ifd& ifd, Layout& lay)
{
    if (ifd.planar != 1 || ifd.fillOrder != 1 || ifd.sampleFormat != 1 || !ifd.bpsUniform) return false;
    if (ifd.bps != 8 && ifd.bps != 16) return false;
    if (ifd.predictor != 1 && ifd.predictor != 2) return false;
    switch (ifd.compression)
    {
    case 1: case 5: case 8: case 32946: case 32773: break;
    default: return false;
    }
    if (!ifd.offsets.present || !ifd.counts.present || ifd.offsets.count != ifd.units || ifd.counts.count != ifd.units) return false;
    u64 at;
    if (!DataAt(rd, ifd.offsets, at) || !DataAt(rd, ifd.counts, at)) return false;
    lay.bytesPerSample = ifd.bps / 8;
    lay.spp = ifd.spp;
    lay.pixelBytes = lay.bytesPerSample * ifd.spp;
    const bool noExtra = ifd.extraCount == 0;
    switch (ifd.spp)
    {
    case 1:
        if (ifd.photometric != 0 && ifd.photometric != 1) return false;
        lay.mode = Mode::Gray; lay.whiteIsZero = ifd.photometric == 0;
        return noExtra;
    case 2:
        if ((ifd.photometric != 0 && ifd.photometric != 1) || ifd.extraCount != 1 || (ifd.extra != 1 && ifd.extra != 2)) return false;
        lay.mode = Mode::GrayA; lay.whiteIsZero = ifd.photometric == 0; lay.alphaAssociated = ifd.extra == 1;
        return true;
    case 3:
        if (ifd.photometric != 2) return false;
        lay.mode = Mode::Rgb;
        return noExtra;
    case 4:
        if (ifd.photometric == 5 && ifd.bps == 8) { lay.mode = Mode::Cmyk; return noExtra; }
        if (ifd.photometric != 2 || ifd.extraCount != 1 || (ifd.extra != 1 && ifd.extra != 2)) return false;
        lay.mode = Mode::Rgba; lay.alphaAssociated = ifd.extra == 1;
        return true;
    default: return false;
    }
}

struct Doc
{
    Mapped map;
    Rd rd;
    Ifd primary;
    Layout primaryLayout;
    struct Level { Ifd ifd; Layout layout; };
    std::vector<Level> levels; // usable reduced levels, smallest first
};

bool IsReducedOf(const Ifd& primary, const Ifd& level)
{
    if (level.w == 0 || level.h == 0 || level.w >= primary.w || level.h >= primary.h) return false;
    const double expectedH = static_cast<double>(primary.h) * level.w / primary.w;
    return std::fabs(expectedH - static_cast<double>(level.h)) <= 2.0 + 0.01 * level.h;
}

bool OpenDoc(const wchar_t* path, Doc& doc, bool needLayout = true)
{
    if (!doc.map.Open(path)) return false;
    const u8* d = doc.map.data;
    if (d[0] == 'I' && d[1] == 'I') doc.rd.le = true;
    else if (d[0] == 'M' && d[1] == 'M') doc.rd.le = false;
    else return false;
    doc.rd.p = d;
    doc.rd.n = doc.map.size;
    const u16 version = doc.rd.U16(2);
    u64 firstIfd;
    if (version == 42) { doc.rd.big = false; firstIfd = doc.rd.U32(4); }
    else if (version == 43)
    {
        if (doc.rd.U16(4) != 8 || doc.rd.U16(6) != 0) return false;
        doc.rd.big = true; firstIfd = doc.rd.U64(8);
    }
    else return false;
    if (doc.rd.bad || firstIfd < 8 || firstIfd >= doc.rd.n) return false;
    if (!ParseIfd(doc.rd, firstIfd, doc.primary)) return false;
    if (!GlideWithinDecodeCap(doc.primary.w, doc.primary.h)) return false;
    if (!needLayout) return true;
    if (!MakeLayout(doc.rd, doc.primary, doc.primaryLayout)) return false;

    // Reduced-resolution levels: SubIFDs of the first image, and later pages flagged "reduced" (NewSubfileType bit 0).
    std::vector<u64> visited{ firstIfd };
    auto consider = [&](u64 off)
    {
        if (off < 8 || off >= doc.rd.n || doc.levels.size() >= MaxLevels) return;
        for (u64 v : visited) if (v == off) return;
        visited.push_back(off);
        Ifd ifd;
        if (!ParseIfd(doc.rd, off, ifd) || !IsReducedOf(doc.primary, ifd)) return;
        Layout lay;
        if (!MakeLayout(doc.rd, ifd, lay) || lay.mode != doc.primaryLayout.mode) return;
        doc.levels.push_back({ ifd, lay });
    };
    if (doc.primary.subIfds.present)
    {
        u64 at;
        if (DataAt(doc.rd, doc.primary.subIfds, at))
            for (u64 i = 0; i < std::min<u64>(doc.primary.subIfds.count, 16); ++i) consider(Element(doc.rd, doc.primary.subIfds, at, i));
    }
    u64 next = doc.primary.next;
    for (int pages = 0; next != 0 && pages < 32; ++pages)
    {
        if (next < 8 || next >= doc.rd.n) break;
        bool seen = false;
        for (u64 v : visited) if (v == next) seen = true;
        if (seen) break;
        Ifd page;
        if (!ParseIfd(doc.rd, next, page)) break;
        if ((page.subfile & 1) != 0) consider(next);
        else visited.push_back(next);
        next = page.next;
    }
    doc.rd.bad = false;
    std::sort(doc.levels.begin(), doc.levels.end(), [](const Doc::Level& a, const Doc::Level& b)
              { return static_cast<u64>(a.ifd.w) * a.ifd.h < static_cast<u64>(b.ifd.w) * b.ifd.h; });
    return true;
}

// ------------------------------------------------------------------------------------------------------------------------------
// Decoding

inline u8 Down16(u32 v) { return static_cast<u8>((v * 255u + 32767u) / 65535u); }

struct Scratch
{
    std::vector<u8> raw, row;
    std::vector<u32> acc;
};

// n pixels of raw samples -> premultiplied BGRA.
void RowToBgra(const Layout& lay, bool le, const u8* src, u32 n, u8* dst)
{
    const bool wide = lay.bytesPerSample == 2;
    auto s16 = [&](const u8* p) -> u32 { return le ? static_cast<u32>(p[0] | (p[1] << 8)) : static_cast<u32>((p[0] << 8) | p[1]); };
    auto premultiply = [](u32 c, u32 a) -> u8 { return static_cast<u8>((c * a + 127) / 255); };
    switch (lay.mode)
    {
    case Mode::Gray:
        for (u32 i = 0; i < n; ++i, dst += 4)
        {
            u8 g = wide ? Down16(s16(src + i * 2)) : src[i];
            if (lay.whiteIsZero) g = static_cast<u8>(255 - g);
            dst[0] = dst[1] = dst[2] = g; dst[3] = 255;
        }
        break;
    case Mode::GrayA:
        for (u32 i = 0; i < n; ++i, dst += 4)
        {
            const u8* p = src + i * (wide ? 4 : 2);
            u8 g = wide ? Down16(s16(p)) : p[0];
            const u8 a = wide ? Down16(s16(p + 2)) : p[1];
            if (lay.whiteIsZero) g = static_cast<u8>(255 - g);
            if (!lay.alphaAssociated) g = premultiply(g, a);
            dst[0] = dst[1] = dst[2] = g; dst[3] = a;
        }
        break;
    case Mode::Rgb:
        if (!wide) for (u32 i = 0; i < n; ++i, dst += 4) { const u8* p = src + i * 3; dst[0] = p[2]; dst[1] = p[1]; dst[2] = p[0]; dst[3] = 255; }
        else for (u32 i = 0; i < n; ++i, dst += 4) { const u8* p = src + i * 6; dst[0] = Down16(s16(p + 4)); dst[1] = Down16(s16(p + 2)); dst[2] = Down16(s16(p)); dst[3] = 255; }
        break;
    case Mode::Rgba:
        for (u32 i = 0; i < n; ++i, dst += 4)
        {
            u8 r, g, b, a;
            if (!wide) { const u8* p = src + i * 4; r = p[0]; g = p[1]; b = p[2]; a = p[3]; }
            else { const u8* p = src + i * 8; r = Down16(s16(p)); g = Down16(s16(p + 2)); b = Down16(s16(p + 4)); a = Down16(s16(p + 6)); }
            if (!lay.alphaAssociated && a != 255) { r = premultiply(r, a); g = premultiply(g, a); b = premultiply(b, a); }
            dst[0] = b; dst[1] = g; dst[2] = r; dst[3] = a;
        }
        break;
    case Mode::Cmyk:
        GlideCmykToBgra(src, n, dst, false);
        break;
    }
}

// Undo the horizontal predictor over `rows` rows of `rowBytes` bytes (spp samples per pixel).
void UndoPredictor(const Layout& lay, bool le, u8* data, u32 rows, size_t rowBytes)
{
    const u32 spp = lay.spp;
    if (lay.bytesPerSample == 1)
    {
        for (u32 r = 0; r < rows; ++r)
        {
            u8* row = data + r * rowBytes;
            for (size_t i = spp; i < rowBytes; ++i) row[i] = static_cast<u8>(row[i] + row[i - spp]);
        }
    }
    else
    {
        for (u32 r = 0; r < rows; ++r)
        {
            u8* row = data + r * rowBytes;
            const size_t samples = rowBytes / 2;
            for (size_t i = spp; i < samples; ++i)
            {
                u8* cur = row + i * 2;
                const u8* prv = row + (i - spp) * 2;
                const u32 a = le ? static_cast<u32>(cur[0] | (cur[1] << 8)) : static_cast<u32>((cur[0] << 8) | cur[1]);
                const u32 b = le ? static_cast<u32>(prv[0] | (prv[1] << 8)) : static_cast<u32>((prv[0] << 8) | prv[1]);
                const u32 s = (a + b) & 0xFFFF;
                if (le) { cur[0] = static_cast<u8>(s); cur[1] = static_cast<u8>(s >> 8); }
                else { cur[0] = static_cast<u8>(s >> 8); cur[1] = static_cast<u8>(s); }
            }
        }
    }
}

struct LevelJob
{
    const Doc* doc;
    const Ifd* ifd;
    const Layout* lay;
    u32 k;               // box reduction (power of two)
    u8* dst;
    size_t dstStride;
    u32 outW, outH;
};

bool DecodeUnit(const LevelJob& job, u64 unit, Scratch& s, unsigned innerThreads)
{
    const Rd& rd = job.doc->rd;
    const Ifd& ifd = *job.ifd;
    const Layout& lay = *job.lay;
    u32 x0 = 0, y0, uw, rows;
    size_t rawRowBytes;
    if (ifd.tiled)
    {
        const u32 tx = static_cast<u32>(unit % ifd.across);
        const u64 ty = unit / ifd.across;
        x0 = tx * ifd.tw;
        y0 = static_cast<u32>(ty * ifd.th);
        uw = std::min(ifd.tw, ifd.w - x0);
        rows = std::min(ifd.th, ifd.h - y0);
        rawRowBytes = static_cast<size_t>(ifd.tw) * lay.pixelBytes;
    }
    else
    {
        y0 = static_cast<u32>(unit * ifd.rps);
        uw = ifd.w;
        rows = std::min(ifd.rps, ifd.h - y0);
        rawRowBytes = static_cast<size_t>(ifd.w) * lay.pixelBytes;
    }
    const size_t fullRows = ifd.tiled ? ifd.th : rows;
    const size_t rawBytes = rawRowBytes * fullRows;
    if (rawBytes > MaxUnitBytes) return false;

    u64 offAt, cntAt;
    if (!DataAt(rd, ifd.offsets, offAt) || !DataAt(rd, ifd.counts, cntAt)) return false;
    const u64 off = Element(rd, ifd.offsets, offAt, unit), count = Element(rd, ifd.counts, cntAt, unit);
    if (rd.bad || count == 0 || off > rd.n || count > rd.n - off) return false;
    const u8* src = rd.p + off;

    const u8* raw;
    const bool needsWrite = ifd.predictor == 2;
    if (ifd.compression == 1)
    {
        if (count < rawBytes) return false;
        if (needsWrite) { s.raw.assign(src, src + rawBytes); raw = s.raw.data(); }
        else raw = src;
    }
    else
    {
        if (s.raw.size() < rawBytes) s.raw.resize(rawBytes);
        size_t produced = 0;
        bool ok;
        switch (ifd.compression)
        {
        case 5: ok = LzwDecode(src, static_cast<size_t>(count), s.raw.data(), rawBytes, &produced); break;
        case 32773: ok = PackBitsDecode(src, static_cast<size_t>(count), s.raw.data(), rawBytes, &produced); break;
        default: ok = InflateZlib(src, static_cast<size_t>(count), s.raw.data(), rawBytes, &produced); break;
        }
        if (!ok || produced < rawBytes) return false;
        raw = s.raw.data();
    }
    if (needsWrite) UndoPredictor(lay, rd.le, const_cast<u8*>(raw), static_cast<u32>(fullRows), rawRowBytes);

    // rows [rowBegin, rowEnd) of the unit -> output; rowBegin is a multiple of k, so box blocks never straddle two chunks
    auto convert = [&](u32 rowBegin, u32 rowEnd, Scratch& sc)
    {
        const u32 k = job.k;
        if (k == 1)
        {
            for (u32 r = rowBegin; r < rowEnd; ++r)
                RowToBgra(lay, rd.le, raw + r * rawRowBytes, uw, job.dst + static_cast<size_t>(y0 + r) * job.dstStride + static_cast<size_t>(x0) * 4);
            return;
        }
        // box reduction: k x k source pixels -> one output pixel (partial blocks at the right / bottom edge average what exists)
        if (sc.row.size() < static_cast<size_t>(uw) * 4) sc.row.resize(static_cast<size_t>(uw) * 4);
        const u32 outCols = (uw + k - 1) / k;
        if (sc.acc.size() < static_cast<size_t>(outCols) * 4) sc.acc.resize(static_cast<size_t>(outCols) * 4);
        for (u32 r0 = rowBegin; r0 < rowEnd; r0 += k)
        {
            const u32 groupRows = std::min(k, rows - r0);
            std::fill(sc.acc.begin(), sc.acc.begin() + static_cast<size_t>(outCols) * 4, 0u);
            for (u32 r = 0; r < groupRows; ++r)
            {
                RowToBgra(lay, rd.le, raw + static_cast<size_t>(r0 + r) * rawRowBytes, uw, sc.row.data());
                const u8* px = sc.row.data();
                for (u32 c = 0; c < outCols; ++c)
                {
                    const u32 cols = std::min(k, uw - c * k);
                    u32 b = 0, g = 0, rr = 0, a = 0;
                    for (u32 i = 0; i < cols; ++i, px += 4) { b += px[0]; g += px[1]; rr += px[2]; a += px[3]; }
                    u32* acc = sc.acc.data() + static_cast<size_t>(c) * 4;
                    acc[0] += b; acc[1] += g; acc[2] += rr; acc[3] += a;
                }
            }
            u8* out = job.dst + static_cast<size_t>((y0 + r0) / k) * job.dstStride + static_cast<size_t>(x0 / k) * 4;
            for (u32 c = 0; c < outCols; ++c, out += 4)
            {
                const u32 cols = std::min(k, uw - c * k);
                const u32 n = cols * groupRows;
                const u32* acc = sc.acc.data() + static_cast<size_t>(c) * 4;
                out[0] = static_cast<u8>((acc[0] + n / 2) / n);
                out[1] = static_cast<u8>((acc[1] + n / 2) / n);
                out[2] = static_cast<u8>((acc[2] + n / 2) / n);
                out[3] = static_cast<u8>((acc[3] + n / 2) / n);
            }
        }
    };
    // A strip that is the whole image (or a few huge strips) cannot be spread over the units: spread its rows instead.
    if (innerThreads > 1 && static_cast<u64>(rows) * uw >= 1'000'000ull)
    {
        const u32 k = job.k;
        const u32 chunk = (((rows + innerThreads - 1) / innerThreads) + k - 1) / k * k;
        std::atomic<bool> good{ true };
        std::vector<std::thread> workers;
        auto part = [&](u32 b, u32 e)
        {
            try { Scratch sc; convert(b, e, sc); }
            catch (...) { good = false; }
        };
        try { for (u32 b = chunk; b < rows; b += chunk) workers.emplace_back(part, b, std::min(rows, b + chunk)); }
        catch (...) { good = false; }
        part(0, std::min(rows, chunk));
        for (auto& w : workers) w.join();
        return good;
    }
    convert(0, rows, s);
    return true;
}

unsigned CoreCount()
{
    SYSTEM_INFO si{};
    GetSystemInfo(&si);
    return std::max<unsigned>(1, si.dwNumberOfProcessors);
}

// Decodes every strip / tile of the level into job.dst on up to maxThreads threads. False when any unit fails.
bool DecodeLevel(const LevelJob& job, unsigned maxThreads)
{
    const Ifd& ifd = *job.ifd;
    const u64 units = ifd.units;
    // strips are grabbed in groups so tiny strips (one row) do not make the work counter the bottleneck
    const u64 rowsPerUnit = ifd.tiled ? ifd.th : ifd.rps;
    const u64 group = ifd.tiled ? 1 : std::max<u64>(1, 64 / std::max<u64>(1, rowsPerUnit));
    const u64 groups = (units + group - 1) / group;
    const u64 pixels = static_cast<u64>(ifd.w) * ifd.h;
    unsigned threads = 1;
    if (groups > 1 && pixels >= 1'000'000ull) threads = static_cast<unsigned>(std::min<u64>(groups, maxThreads));
    const unsigned inner = (groups < maxThreads && pixels >= 1'000'000ull) ? std::max(1u, maxThreads / static_cast<unsigned>(std::max<u64>(1, groups))) : 1u;
    std::atomic<u64> nextGroup{ 0 };
    std::atomic<bool> ok{ true };
    auto work = [&]()
    {
        try
        {
            Scratch scratch;
            for (;;)
            {
                if (!ok.load(std::memory_order_relaxed)) return;
                const u64 g = nextGroup.fetch_add(1);
                if (g >= groups) return;
                const u64 end = std::min(units, (g + 1) * group);
                for (u64 u = g * group; u < end; ++u)
                    if (!DecodeUnit(job, u, scratch, inner)) { ok = false; return; }
            }
        }
        catch (...) { ok = false; }
    };
    std::vector<std::thread> workers;
    try { for (unsigned t = 1; t < threads; ++t) workers.emplace_back(work); }
    catch (...) { } // fewer threads than planned: the work counter still hands every group to someone
    work();
    for (auto& w : workers) w.join();
    return ok;
}

// Largest power of two (>= 1) that divides a unit edge, so box blocks never straddle two units.
bool ReductionFits(const Ifd& ifd, u32 k)
{
    if (ifd.tiled) return ifd.tw % k == 0 && ifd.th % k == 0;
    return ifd.units == 1 || ifd.rps % k == 0;
}

struct Plan
{
    const Ifd* ifd = nullptr;
    const Layout* lay = nullptr;
    u32 k = 1, outW = 0, outH = 0;
    bool pyramid = false, reduced = false;
};

// Chooses the pyramid level and box reduction for a physical (unrotated) box.
Plan MakePlan(const Doc& doc, u32 boxW, u32 boxH)
{
    Plan plan;
    plan.ifd = &doc.primary;
    plan.lay = &doc.primaryLayout;
    const double ratio = (boxW == 0 || boxH == 0) ? 1.0 : std::min(static_cast<double>(boxW) / doc.primary.w, static_cast<double>(boxH) / doc.primary.h);
    if (ratio < 1.0)
    {
        plan.reduced = true;
        const u32 needW = std::max<u32>(1, static_cast<u32>(std::ceil(doc.primary.w * ratio)));
        const u32 needH = std::max<u32>(1, static_cast<u32>(std::ceil(doc.primary.h * ratio)));
        for (const auto& level : doc.levels)
            if (level.ifd.w >= needW && level.ifd.h >= needH) { plan.ifd = &level.ifd; plan.lay = &level.layout; plan.pyramid = true; break; }
        u32 k = 1;
        while (k < 64 && plan.ifd->w / (k * 2) >= needW && plan.ifd->h / (k * 2) >= needH && ReductionFits(*plan.ifd, k * 2)) k *= 2;
        plan.k = k;
    }
    plan.outW = (plan.ifd->w + plan.k - 1) / plan.k;
    plan.outH = (plan.ifd->h + plan.k - 1) / plan.k;
    return plan;
}

u8* AllocPixels(u32 w, u32 h, size_t* bytes)
{
    const u64 b = static_cast<u64>(w) * h * 4ull;
    if (w == 0 || h == 0 || b > std::numeric_limits<UINT>::max()) return nullptr;
    *bytes = static_cast<size_t>(b);
    return static_cast<u8*>(CoTaskMemAlloc(static_cast<size_t>(b)));
}

// Which files this reader takes over from WIC. Tiled files (WIC decodes tiles one at a time, about six times slower than strips),
// BigTIFF (WIC cannot read it), anything with a covering pyramid level, and CMYK (WIC's converter is single-threaded and slow for a
// reduced request) are ours. Plain strip files stay with WIC: its banded decode measured equal to this reader's.
bool PreferOwn(const Doc& doc, const Plan& plan)
{
    return doc.rd.big || plan.pyramid || plan.ifd->tiled || plan.lay->mode == Mode::Cmyk;
}

unsigned ThreadBudget(bool thumbnail)
{
    return std::min<unsigned>(CoreCount(), thumbnail ? 8u : 16u);
}

bool Rotated(u16 orientation) { return orientation >= 5 && orientation <= 8; }
} // namespace

bool GlideIsTiffPath(const wchar_t* path)
{
    if (!path) return false;
    const wchar_t* dot = wcsrchr(path, L'.');
    return dot && (!_wcsicmp(dot, L".tif") || !_wcsicmp(dot, L".tiff"));
}

bool GlideTiffDecodeBounded(const wchar_t* path, std::uint32_t maxWidth, std::uint32_t maxHeight, bool thumbnail, GlideDecodedImage* image, bool forceOwn)
{
    if (!path || !image) return false;
    *image = {};
    try
    {
        Doc doc;
        if (!OpenDoc(path, doc)) return false;
        const u16 orientation = doc.primary.orientation;
        const bool rotated = Rotated(orientation);
        const Plan plan = MakePlan(doc, rotated ? maxHeight : maxWidth, rotated ? maxWidth : maxHeight);
        if (!forceOwn && !PreferOwn(doc, plan)) return false;
        size_t bytes = 0;
        u8* pixels = AllocPixels(plan.outW, plan.outH, &bytes);
        if (!pixels) return false;
        const LevelJob job{ &doc, plan.ifd, plan.lay, plan.k, pixels, static_cast<size_t>(plan.outW) * 4, plan.outW, plan.outH };
        if (!DecodeLevel(job, ThreadBudget(thumbnail))) { CoTaskMemFree(pixels); return false; }
        u32 w = plan.outW, h = plan.outH;
        if (orientation != 1)
        {
            const u32 ow = rotated ? h : w, oh = rotated ? w : h;
            size_t obytes = 0;
            u8* oriented = AllocPixels(ow, oh, &obytes);
            if (!oriented) { CoTaskMemFree(pixels); return false; }
            GlideOrientBgra(pixels, w, h, orientation, oriented, ow);
            CoTaskMemFree(pixels);
            pixels = oriented; w = ow; h = oh;
        }
        image->width = w;
        image->height = h;
        image->stride = w * 4;
        image->bufferBytes = static_cast<u64>(w) * h * 4ull;
        image->data = pixels;
        return true;
    }
    catch (...) { *image = {}; return false; }
}

bool GlideTiffDecodeInto(const wchar_t* path, GlideSurfaceAllocator allocate, void* context, std::uint8_t** surface, std::uint32_t* stride,
                         std::uint32_t* surfaceWidth, std::uint32_t* surfaceHeight)
{
    if (!path || !allocate) return false;
    try
    {
        Doc doc;
        if (!OpenDoc(path, doc)) return false;
        const Plan plan = MakePlan(doc, 0, 0);
        if (!(doc.rd.big || plan.ifd->tiled || plan.lay->mode == Mode::Cmyk)) return false; // plain strip files: WIC's banded decode is as fast
        const u16 orientation = doc.primary.orientation;
        const bool rotated = Rotated(orientation);
        const u32 w = doc.primary.w, h = doc.primary.h;
        const u32 outW = rotated ? h : w, outH = rotated ? w : h;
        u8* upright = nullptr;
        size_t bytes = 0;
        if (orientation != 1)
        {
            upright = AllocPixels(w, h, &bytes);
            if (!upright) return false;
        }
        u32 dstStride = 0;
        auto* destination = static_cast<u8*>(allocate(context, outW, outH, &dstStride));
        if (!destination || dstStride < static_cast<u64>(outW) * 4ull || static_cast<u64>(dstStride) * outH > std::numeric_limits<UINT>::max())
        {
            if (upright) CoTaskMemFree(upright);
            return false;
        }
        *surface = destination;
        *stride = dstStride;
        *surfaceWidth = outW;
        *surfaceHeight = outH;
        const LevelJob job{ &doc, &doc.primary, &doc.primaryLayout, 1, upright ? upright : destination,
                            upright ? static_cast<size_t>(w) * 4 : static_cast<size_t>(dstStride), w, h };
        const bool ok = DecodeLevel(job, ThreadBudget(false));
        if (ok && upright) GlideOrientIntoSurface(upright, w, h, orientation, destination, dstStride);
        if (upright) CoTaskMemFree(upright);
        return ok;
    }
    catch (...) { return false; }
}

bool GlideTiffProbe(const wchar_t* path, std::uint32_t* width, std::uint32_t* height, std::uint32_t* orientation)
{
    if (!path || !width || !height) return false;
    try
    {
        Doc doc;
        if (!GlideIsTiffPath(path) || !OpenDoc(path, doc, false)) return false;
        *width = doc.primary.w;
        *height = doc.primary.h;
        if (orientation) *orientation = doc.primary.orientation;
        return true;
    }
    catch (...) { return false; }
}

bool GlideTiffPlan(const wchar_t* path, std::uint32_t maxWidth, std::uint32_t maxHeight, std::uint32_t* levelWidth, std::uint32_t* levelHeight,
                   std::uint32_t* reduction, std::uint32_t* levels, std::uint32_t* flags)
{
    if (!path) return false;
    try
    {
        Doc doc;
        if (!OpenDoc(path, doc)) return false;
        const bool rotated = Rotated(doc.primary.orientation);
        const Plan plan = MakePlan(doc, rotated ? maxHeight : maxWidth, rotated ? maxWidth : maxHeight);
        if (levelWidth) *levelWidth = plan.ifd->w;
        if (levelHeight) *levelHeight = plan.ifd->h;
        if (reduction) *reduction = plan.k;
        if (levels) *levels = static_cast<u32>(doc.levels.size());
        if (flags) *flags = (plan.ifd->tiled ? 1u : 0u) | (doc.rd.big ? 2u : 0u) | (plan.pyramid ? 4u : 0u) | (PreferOwn(doc, plan) ? 8u : 0u);
        return true;
    }
    catch (...) { return false; }
}
