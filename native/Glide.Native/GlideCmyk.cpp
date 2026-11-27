#include "GlideCmyk.h"
#include <windows.h>
#include <wincodec.h>
#include <algorithm>
#include <cstring>
#include <memory>
#include <vector>

namespace
{
constexpr int N = 17;                    // lattice points per axis
constexpr int PlaneSize = N * N * N;     // one K plane

struct Lut
{
    // One 64 bit word per lattice point, index ((k * N + y) * N + m) * N + c: blue, green and red in 21 bit lanes (bits 0, 21, 42), so a
    // weighted sum of up to four points (weights adding to 255, so lanes stay below 2^16) is four multiplies for all three channels.
    std::vector<std::uint64_t> table;
};

std::unique_ptr<Lut> BuildLut()
{
    const HRESULT init = CoInitializeEx(nullptr, COINIT_MULTITHREADED);
    const bool mustUninit = SUCCEEDED(init);
    if (FAILED(init) && init != RPC_E_CHANGED_MODE) return nullptr;
    std::unique_ptr<Lut> lut;
    IWICImagingFactory* factory = nullptr;
    IWICBitmap* bitmap = nullptr;
    IWICFormatConverter* converter = nullptr;
    try
    {
        constexpr UINT total = static_cast<UINT>(PlaneSize) * N;           // 83521
        constexpr UINT width = N * N, height = N * N;                       // 289 x 289
        static_assert(width * height == total, "lattice must tile exactly");
        std::vector<std::uint8_t> source(static_cast<std::size_t>(total) * 4);
        auto level = [](int i) { return static_cast<std::uint8_t>((i * 255 + 8) / 16); };
        std::size_t at = 0;
        for (int k = 0; k < N; ++k)
            for (int y = 0; y < N; ++y)
                for (int m = 0; m < N; ++m)
                    for (int c = 0; c < N; ++c, at += 4)
                    {
                        source[at] = level(c); source[at + 1] = level(m); source[at + 2] = level(y); source[at + 3] = level(k);
                    }
        std::vector<std::uint32_t> converted(total);
        HRESULT hr = CoCreateInstance(CLSID_WICImagingFactory, nullptr, CLSCTX_INPROC_SERVER, IID_PPV_ARGS(&factory));
        if (SUCCEEDED(hr)) hr = factory->CreateBitmapFromMemory(width, height, GUID_WICPixelFormat32bppCMYK, width * 4,
                                                                 static_cast<UINT>(source.size()), source.data(), &bitmap);
        if (SUCCEEDED(hr)) hr = factory->CreateFormatConverter(&converter);
        if (SUCCEEDED(hr)) hr = converter->Initialize(bitmap, GUID_WICPixelFormat32bppBGRA, WICBitmapDitherTypeNone, nullptr, 0.0,
                                                       WICBitmapPaletteTypeCustom);
        if (SUCCEEDED(hr)) hr = converter->CopyPixels(nullptr, width * 4, static_cast<UINT>(converted.size() * 4),
                                                       reinterpret_cast<BYTE*>(converted.data()));
        if (SUCCEEDED(hr))
        {
            // A converter that returned nothing useful (all zero) must not become the colour model.
            bool any = false;
            for (std::uint32_t v : converted) { if ((v & 0x00FFFFFFu) != 0) { any = true; break; } }
            if (any)
            {
                lut = std::make_unique<Lut>();
                lut->table.resize(converted.size());
                for (std::size_t i = 0; i < converted.size(); ++i)
                {
                    const std::uint32_t v = converted[i];
                    lut->table[i] = static_cast<std::uint64_t>(v & 255) | (static_cast<std::uint64_t>((v >> 8) & 255) << 21) | (static_cast<std::uint64_t>((v >> 16) & 255) << 42);
                }
            }
        }
    }
    catch (...) { lut.reset(); }
    if (converter) converter->Release();
    if (bitmap) bitmap->Release();
    if (factory) factory->Release();
    if (mustUninit) CoUninitialize();
    return lut;
}

const Lut* GetLut()
{
    static const std::unique_ptr<Lut> lut = BuildLut(); // thread-safe one-time initialisation
    return lut.get();
}

struct Split
{
    std::uint8_t cell[256], frac[256];
    Split()
    {
        // position on the lattice in 1/255 cell units: p = v * 16, cell = p / 255, fraction = p % 255 (the last cell is closed on top)
        for (int v = 0; v < 256; ++v)
        {
            const int p = v * (N - 1);
            int c = p / 255;
            if (c >= N - 1) c = N - 2;
            cell[v] = static_cast<std::uint8_t>(c);
            frac[v] = static_cast<std::uint8_t>(p - c * 255);
        }
    }
};
const Split SplitTable;

// Tetrahedral interpolation in (c, m, y) inside one K plane: P0*(255-w1) + P1*(w1-w2) + P2*(w2-w3) + P3*w3 with the weights sorted
// descending and the vertices walked in the same axis order. All three channels at once (packed lanes), result lanes <= 255 * 255.
inline std::uint64_t Plane(const std::uint64_t* t, int w1, int s1, int w2, int s2, int w3, int s3)
{
    return t[0] * static_cast<std::uint64_t>(255 - w1) + t[s1] * static_cast<std::uint64_t>(w1 - w2) +
           t[s1 + s2] * static_cast<std::uint64_t>(w2 - w3) + t[s1 + s2 + s3] * static_cast<std::uint64_t>(w3);
}

inline std::uint32_t Convert(const std::uint64_t* table, int c, int m, int y, int k)
{
    const int ic = SplitTable.cell[c], im = SplitTable.cell[m], iy = SplitTable.cell[y], ik = SplitTable.cell[k];
    int w1 = SplitTable.frac[c], s1 = 1, w2 = SplitTable.frac[m], s2 = N, w3 = SplitTable.frac[y], s3 = N * N;
    if (w1 < w2) { std::swap(w1, w2); std::swap(s1, s2); }
    if (w2 < w3) { std::swap(w2, w3); std::swap(s2, s3); }
    if (w1 < w2) { std::swap(w1, w2); std::swap(s1, s2); }
    const std::uint64_t* base = table + ((static_cast<std::size_t>(ik) * N + iy) * N + im) * N + ic;
    const std::uint64_t a = Plane(base, w1, s1, w2, s2, w3, s3);
    const std::uint64_t b = Plane(base + PlaneSize, w1, s1, w2, s2, w3, s3);
    const std::uint32_t fk = SplitTable.frac[k], wk = 255 - fk;
    constexpr std::uint64_t mask = (1ull << 21) - 1;
    auto lane = [&](int shift) -> std::uint32_t
    {
        const std::uint32_t v = static_cast<std::uint32_t>((a >> shift) & mask) * wk + static_cast<std::uint32_t>((b >> shift) & mask) * fk;
        return std::min<std::uint32_t>(255, (v + 32512) / 65025);
    };
    return 0xFF000000u | (lane(42) << 16) | (lane(21) << 8) | lane(0);
}
}

bool GlideCmykReady() { return GetLut() != nullptr; }

void GlideCmykToBgra(const std::uint8_t* cmyk, std::size_t pixels, std::uint8_t* bgra, bool inverted)
{
    const Lut* lut = GetLut();
    if (!lut) return;
    const std::uint64_t* table = lut->table.data();
    auto* out = reinterpret_cast<std::uint32_t*>(bgra);
    std::uint32_t lastKey = 0, lastValue = 0xFF000000u; // flat areas (paper white, solid fills) repeat the previous pixel
    bool haveLast = false;
    const std::uint32_t flip = inverted ? 0xFFFFFFFFu : 0u;
    for (std::size_t i = 0; i < pixels; ++i)
    {
        std::uint32_t key;
        std::memcpy(&key, cmyk + i * 4, 4);
        key ^= flip;
        if (!haveLast || key != lastKey)
        {
            lastKey = key;
            lastValue = Convert(table, static_cast<int>(key & 255), static_cast<int>((key >> 8) & 255),
                                static_cast<int>((key >> 16) & 255), static_cast<int>(key >> 24));
            haveLast = true;
        }
        out[i] = lastValue;
    }
}

void GlideCmykConvertOne(std::uint8_t c, std::uint8_t m, std::uint8_t y, std::uint8_t k, std::uint8_t* bgra)
{
    const std::uint8_t px[4] = { c, m, y, k };
    GlideCmykToBgra(px, 1, bgra, false);
}
