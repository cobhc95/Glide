#include <windows.h>
#include <wincodec.h>
#include <propvarutil.h>
#include <shobjidl.h>
#include <algorithm>
#include <cstring>
#include <cstdint>
#include <cmath>
#include <limits>

struct GlideImageInfo
{
    std::uint32_t width;
    std::uint32_t height;
    std::uint32_t frameCount;
    std::uint32_t reserved;
};

// Decoded-pixel cap (must match DecodeMemoryGuard.MaxTotalPixels in Glide.Imaging): no decode path in this
// bridge produces a frame larger than 512 MP (~2.05 GB as 32bpp). The managed loader checks the size
// before decoding whenever a probe can read it; this cap also covers files whose size only WIC can read,
// so a hostile header can never make the bridge allocate more than this.
constexpr std::uint64_t GlideMaxDecodePixels = 512'000'000ull;

static bool WithinDecodeCap(std::uint64_t width, std::uint64_t height)
{
    return width > 0 && height > 0 && width <= 65536 && height <= 65536 && width * height <= GlideMaxDecodePixels;
}

// Lets the managed side (and tests) confirm the loaded bridge enforces the cap.
extern "C" __declspec(dllexport) std::uint64_t __cdecl GlideGetMaxDecodePixels()
{
    return GlideMaxDecodePixels;
}


extern "C" __declspec(dllexport) HCURSOR __cdecl GlideCreateZoomCursor(int zoomOut)
{
    // Exact legacy 1.2.125 magnifier cursor: 48x48 alpha cursor, 4x supersampled.
    // The OS cursor compositor owns movement, so selection hover never participates in Avalonia rendering.
    constexpr int W = 48;
    constexpr int H = 48;
    BITMAPV5HEADER bi{};
    bi.bV5Size = sizeof(bi);
    bi.bV5Width = W;
    bi.bV5Height = -H;
    bi.bV5Planes = 1;
    bi.bV5BitCount = 32;
    bi.bV5Compression = BI_BITFIELDS;
    bi.bV5RedMask = 0x00FF0000;
    bi.bV5GreenMask = 0x0000FF00;
    bi.bV5BlueMask = 0x000000FF;
    bi.bV5AlphaMask = 0xFF000000;

    void* bits = nullptr;
    HDC screen = GetDC(nullptr);
    HBITMAP color = CreateDIBSection(screen, reinterpret_cast<BITMAPINFO*>(&bi), DIB_RGB_COLORS, &bits, nullptr, 0);
    ReleaseDC(nullptr, screen);
    if (!color || !bits)
    {
        if (color) DeleteObject(color);
        return nullptr;
    }

    std::memset(bits, 0, W * H * 4);
    auto* px = static_cast<std::uint32_t*>(bits);
    constexpr int SS = 4;
    constexpr float cx = 20.0f;
    constexpr float cy = 19.0f;
    constexpr float radius = 10.5f;
    constexpr float ringHalf = 1.7f;
    constexpr float signHalf = 1.25f;
    for (int y = 0; y < H; ++y)
    {
        for (int x = 0; x < W; ++x)
        {
            int covered = 0;
            for (int sy = 0; sy < SS; ++sy)
            {
                for (int sx = 0; sx < SS; ++sx)
                {
                    const float fx = x + (sx + 0.5f) / SS;
                    const float fy = y + (sy + 0.5f) / SS;
                    const float dx = fx - cx;
                    const float dy = fy - cy;
                    const float dist = std::sqrt(dx * dx + dy * dy);
                    bool ink = std::abs(dist - radius) <= ringHalf;
                    const float hx = fx - 29.2f;
                    const float hy = fy - 28.2f;
                    const float along = (hx + hy) * 0.70710678f;
                    const float across = (hx - hy) * 0.70710678f;
                    if (along >= 0.0f && along <= 12.5f && std::abs(across) <= 1.9f) ink = true;
                    if (std::abs(fy - cy) <= signHalf && std::abs(fx - cx) <= 5.5f) ink = true;
                    if (!zoomOut && std::abs(fx - cx) <= signHalf && std::abs(fy - cy) <= 5.5f) ink = true;
                    if (ink) ++covered;
                }
            }
            if (!covered) continue;
            const auto a = static_cast<std::uint8_t>((covered * 255) / (SS * SS));
            const auto c = a;
            px[y * W + x] = (static_cast<std::uint32_t>(a) << 24) |
                            (static_cast<std::uint32_t>(c) << 16) |
                            (static_cast<std::uint32_t>(c) << 8) |
                            static_cast<std::uint32_t>(c);
        }
    }

    HBITMAP mask = CreateBitmap(W, H, 1, 1, nullptr);
    if (!mask)
    {
        DeleteObject(color);
        return nullptr;
    }
    ICONINFO ii{};
    ii.fIcon = FALSE;
    ii.xHotspot = 20;
    ii.yHotspot = 19;
    ii.hbmMask = mask;
    ii.hbmColor = color;
    HCURSOR cursor = static_cast<HCURSOR>(CreateIconIndirect(&ii));
    DeleteObject(mask);
    DeleteObject(color);
    return cursor;
}

extern "C" __declspec(dllexport) int __cdecl GlideProbeImageW(const wchar_t* path, GlideImageInfo* info)
{
    if (!path || !info) return 0;
    *info = {};

    const HRESULT init = CoInitializeEx(nullptr, COINIT_MULTITHREADED);
    const bool mustUninit = SUCCEEDED(init);
    if (FAILED(init) && init != RPC_E_CHANGED_MODE) return 0;

    IWICImagingFactory* factory = nullptr;
    IWICBitmapDecoder* decoder = nullptr;
    IWICBitmapFrameDecode* frame = nullptr;
    int ok = 0;

    HRESULT hr = CoCreateInstance(CLSID_WICImagingFactory, nullptr, CLSCTX_INPROC_SERVER,
                                  IID_PPV_ARGS(&factory));
    if (SUCCEEDED(hr))
        hr = factory->CreateDecoderFromFilename(path, nullptr, GENERIC_READ,
                                                WICDecodeMetadataCacheOnDemand, &decoder);

    UINT frameCount = 0;
    if (SUCCEEDED(hr)) hr = decoder->GetFrameCount(&frameCount);
    if (SUCCEEDED(hr) && frameCount > 0) hr = decoder->GetFrame(0, &frame);

    UINT width = 0, height = 0;
    if (SUCCEEDED(hr)) hr = frame->GetSize(&width, &height);
    if (SUCCEEDED(hr))
    {
        info->width = width;
        info->height = height;
        info->frameCount = frameCount;
        ok = 1;
    }

    if (frame) frame->Release();
    if (decoder) decoder->Release();
    if (factory) factory->Release();
    if (mustUninit) CoUninitialize();
    return ok;
}

struct GlideDecodedImage
{
    std::uint32_t width;
    std::uint32_t height;
    std::uint32_t stride;
    std::uint64_t bufferBytes;
    void* data;
};

static std::uint16_t ReadOrientation(IWICBitmapFrameDecode* frame)
{
    if (!frame) return 1;
    IWICMetadataQueryReader* reader = nullptr;
    PROPVARIANT value;
    PropVariantInit(&value);
    std::uint16_t orientation = 1;
    if (SUCCEEDED(frame->GetMetadataQueryReader(&reader)) && reader)
    {
        const wchar_t* paths[] = { L"/ifd/{ushort=274}", L"/app1/ifd/{ushort=274}" };
        for (const auto* query : paths)
        {
            PropVariantClear(&value);
            if (SUCCEEDED(reader->GetMetadataByName(query, &value)))
            {
                if (value.vt == VT_UI2) orientation = value.uiVal;
                else if (value.vt == VT_UI4) orientation = static_cast<std::uint16_t>(value.ulVal);
                if (orientation >= 1 && orientation <= 8) break;
                orientation = 1;
            }
        }
        reader->Release();
    }
    PropVariantClear(&value);
    return orientation;
}

static void OrientBgra(const std::uint8_t* source, std::uint32_t width, std::uint32_t height,
                       std::uint16_t orientation, std::uint8_t* destination,
                       std::uint32_t destinationWidth)
{
    for (std::uint32_t y = 0; y < height; ++y)
    {
        for (std::uint32_t x = 0; x < width; ++x)
        {
            std::uint32_t dx = x, dy = y;
            switch (orientation)
            {
            case 2: dx = width - 1 - x; break;
            case 3: dx = width - 1 - x; dy = height - 1 - y; break;
            case 4: dy = height - 1 - y; break;
            case 5: dx = y; dy = x; break;
            case 6: dx = height - 1 - y; dy = x; break;
            case 7: dx = height - 1 - y; dy = width - 1 - x; break;
            case 8: dx = y; dy = width - 1 - x; break;
            default: break;
            }
            const auto* input = source + (static_cast<std::size_t>(y) * width + x) * 4;
            auto* output = destination + (static_cast<std::size_t>(dy) * destinationWidth + dx) * 4;
            std::memcpy(output, input, 4);
        }
    }
}

// Latency-critical progressive-JPEG first paint. Do NOT call GetLevelCount(): Microsoft documents
// that progressive-level discovery can wait for all levels. Maximum-speed mode explicitly requests
// level 0. Colour-first mode requests level 2, then falls back to 1/0 only if the file exposes fewer
// WIC progressive levels. This mirrors the legacy colour-first intent without its GetLevelCount stall.
// The representative 21 MP field JPEG has ten scans whose first three are Y-DC, Cb-DC and Cr-DC; for
// that common optimized scan layout, level 2 is the earliest preview containing all three components.
// Background refinement/full decode still uses the normal highest-quality path.
static void SelectProgressivePreviewLevel(IWICBitmapFrameDecode* frame, bool colourFirst)
{
    if (!frame) return;
    IWICProgressiveLevelControl* progressive = nullptr;
    if (FAILED(frame->QueryInterface(IID_PPV_ARGS(&progressive))) || !progressive) return;
    if (!colourFirst)
    {
        progressive->SetCurrentLevel(0);
        progressive->Release();
        return;
    }

    HRESULT hr = progressive->SetCurrentLevel(2);
    if (hr == WINCODEC_ERR_INVALIDPROGRESSIVELEVEL) hr = progressive->SetCurrentLevel(1);
    if (hr == WINCODEC_ERR_INVALIDPROGRESSIVELEVEL) progressive->SetCurrentLevel(0);
    progressive->Release();
}

static bool TryDecoderNativePreview(IWICBitmapFrameDecode* frame, UINT requestedWidth, UINT requestedHeight,
                                    std::uint8_t** pixelsOut, UINT* widthOut, UINT* heightOut)
{
    if (!frame || !pixelsOut || !widthOut || !heightOut || requestedWidth == 0 || requestedHeight == 0) return false;
    *pixelsOut = nullptr;
    *widthOut = 0;
    *heightOut = 0;

    IWICBitmapSourceTransform* transform = nullptr;
    HRESULT hr = frame->QueryInterface(IID_PPV_ARGS(&transform));
    if (FAILED(hr) || !transform) return false;

    UINT width = requestedWidth;
    UINT height = requestedHeight;
    hr = transform->GetClosestSize(&width, &height);
    if (FAILED(hr) || !WithinDecodeCap(width, height))
    {
        transform->Release();
        return false;
    }

    // Prefer a 32-bit output so the decoder can write directly into Glide's presentation format.
    // Microsoft's JPEG decoder commonly selects 24bpp BGR instead; that still retains decoder-native
    // DCT reduction and only needs one compact preview-sized expansion to 32bpp PBGRA.
    WICPixelFormatGUID format = GUID_WICPixelFormat32bppPBGRA;
    hr = transform->GetClosestPixelFormat(&format);
    if (FAILED(hr))
    {
        transform->Release();
        return false;
    }

    UINT bytesPerPixel = 0;
    if (IsEqualGUID(format, GUID_WICPixelFormat24bppBGR)) bytesPerPixel = 3;
    else if (IsEqualGUID(format, GUID_WICPixelFormat32bppPBGRA) ||
             IsEqualGUID(format, GUID_WICPixelFormat32bppBGRA) ||
             IsEqualGUID(format, GUID_WICPixelFormat32bppBGR)) bytesPerPixel = 4;
    if (bytesPerPixel == 0)
    {
        transform->Release();
        return false;
    }

    const std::uint64_t nativeStride64 = static_cast<std::uint64_t>(width) * bytesPerPixel;
    const std::uint64_t nativeBytes64 = nativeStride64 * height;
    const std::uint64_t outputBytes64 = static_cast<std::uint64_t>(width) * height * 4ull;
    if (nativeStride64 > std::numeric_limits<UINT>::max() ||
        nativeBytes64 > std::numeric_limits<UINT>::max() ||
        outputBytes64 > std::numeric_limits<UINT>::max())
    {
        transform->Release();
        return false;
    }

    auto* nativePixels = static_cast<std::uint8_t*>(CoTaskMemAlloc(static_cast<std::size_t>(nativeBytes64)));
    if (!nativePixels)
    {
        transform->Release();
        return false;
    }

    auto requestedFormat = format;
    hr = transform->CopyPixels(nullptr, width, height, &requestedFormat, WICBitmapTransformRotate0,
                               static_cast<UINT>(nativeStride64), static_cast<UINT>(nativeBytes64), nativePixels);
    transform->Release();
    if (FAILED(hr) || !IsEqualGUID(requestedFormat, format))
    {
        CoTaskMemFree(nativePixels);
        return false;
    }

    // 32bpp PBGRA is already the exact surface Glide consumes.
    if (IsEqualGUID(format, GUID_WICPixelFormat32bppPBGRA))
    {
        *pixelsOut = nativePixels;
        *widthOut = width;
        *heightOut = height;
        return true;
    }

    // 32bpp variants can be normalised in-place without another allocation.
    if (bytesPerPixel == 4)
    {
        const std::size_t count = static_cast<std::size_t>(width) * height;
        if (IsEqualGUID(format, GUID_WICPixelFormat32bppBGRA))
        {
            for (std::size_t i = 0; i < count; ++i)
            {
                auto* px = nativePixels + i * 4;
                const auto alpha = px[3];
                if (alpha != 255)
                {
                    px[0] = static_cast<std::uint8_t>((static_cast<unsigned>(px[0]) * alpha + 127u) / 255u);
                    px[1] = static_cast<std::uint8_t>((static_cast<unsigned>(px[1]) * alpha + 127u) / 255u);
                    px[2] = static_cast<std::uint8_t>((static_cast<unsigned>(px[2]) * alpha + 127u) / 255u);
                }
            }
        }
        else // 32bppBGR has an unused high byte; Glide needs opaque alpha.
        {
            for (std::size_t i = 0; i < count; ++i) nativePixels[i * 4 + 3] = 255;
        }
        *pixelsOut = nativePixels;
        *widthOut = width;
        *heightOut = height;
        return true;
    }

    auto* pbgra = static_cast<std::uint8_t*>(CoTaskMemAlloc(static_cast<std::size_t>(outputBytes64)));
    if (!pbgra)
    {
        CoTaskMemFree(nativePixels);
        return false;
    }
    const std::size_t count = static_cast<std::size_t>(width) * height;
    for (std::size_t i = 0; i < count; ++i)
    {
        const auto* src = nativePixels + i * 3;
        auto* dst = pbgra + i * 4;
        dst[0] = src[0]; dst[1] = src[1]; dst[2] = src[2]; dst[3] = 255;
    }
    CoTaskMemFree(nativePixels);
    *pixelsOut = pbgra;
    *widthOut = width;
    *heightOut = height;
    return true;
}

static int DecodeImageBoundedW(const wchar_t* path, std::uint32_t maxWidth, std::uint32_t maxHeight,
                               bool stagedProgressivePreview, bool progressiveColourFirst, GlideDecodedImage* image)
{
    if (!path || !image) return 0;
    *image = {};
    const HRESULT init = CoInitializeEx(nullptr, COINIT_MULTITHREADED);
    const bool mustUninit = SUCCEEDED(init);
    if (FAILED(init) && init != RPC_E_CHANGED_MODE) return 0;

    IWICImagingFactory* factory = nullptr;
    IWICBitmapDecoder* decoder = nullptr;
    IWICBitmapFrameDecode* frame = nullptr;
    IWICBitmapScaler* scaler = nullptr;
    IWICFormatConverter* converter = nullptr;
    std::uint8_t* converted = nullptr;
    std::uint8_t* oriented = nullptr;
    int ok = 0;

    HRESULT hr = CoCreateInstance(CLSID_WICImagingFactory, nullptr, CLSCTX_INPROC_SERVER,
                                  IID_PPV_ARGS(&factory));
    // Metadata-on-demand matches the accepted legacy fast path. Metadata-on-load forces work Glide
    // does not need before first paint and was a measurable architectural regression in Glide 2.x.
    if (SUCCEEDED(hr)) hr = factory->CreateDecoderFromFilename(path, nullptr, GENERIC_READ,
                                                               WICDecodeMetadataCacheOnDemand, &decoder);
    if (SUCCEEDED(hr)) hr = decoder->GetFrame(0, &frame);
    UINT width = 0, height = 0;
    if (SUCCEEDED(hr)) hr = frame->GetSize(&width, &height);
    const auto orientation = ReadOrientation(frame);
    const bool rotated = orientation >= 5 && orientation <= 8;

    UINT boundedWidth = width;
    UINT boundedHeight = height;
    const UINT physicalMaxWidth = rotated ? maxHeight : maxWidth;
    const UINT physicalMaxHeight = rotated ? maxWidth : maxHeight;
    const bool bounded = SUCCEEDED(hr) && physicalMaxWidth > 0 && physicalMaxHeight > 0 &&
                         (width > physicalMaxWidth || height > physicalMaxHeight);
    // An unbounded (full-resolution) request for a frame beyond the decoded-pixel cap is refused before
    // any pixel buffer exists. Bounded requests only ever allocate their reduced output (capped below).
    if (SUCCEEDED(hr) && !bounded && !WithinDecodeCap(width, height)) hr = E_INVALIDARG;
    if (bounded)
    {
        const double ratio = std::min(static_cast<double>(physicalMaxWidth) / static_cast<double>(width),
                                      static_cast<double>(physicalMaxHeight) / static_cast<double>(height));
        boundedWidth = std::max<UINT>(1, static_cast<UINT>(std::floor(width * ratio)));
        boundedHeight = std::max<UINT>(1, static_cast<UINT>(std::floor(height * ratio)));
    }

    UINT scaledWidth = boundedWidth;
    UINT scaledHeight = boundedHeight;
    bool decoderNativeScaled = false;
    if (bounded && SUCCEEDED(hr))
    {
        if (stagedProgressivePreview) SelectProgressivePreviewLevel(frame, progressiveColourFirst);
        // Critical large-photo fast path: ask the codec itself for the reduced frame. Microsoft's
        // JPEG WIC decoder can perform reduced IDCT/pyramid decoding here, avoiding construction of
        // the full-resolution 20-100+ MP raster merely to throw most pixels away in a scaler.
        decoderNativeScaled = TryDecoderNativePreview(frame, boundedWidth, boundedHeight,
                                                       &converted, &scaledWidth, &scaledHeight);
    }

    if (!decoderNativeScaled && SUCCEEDED(hr))
    {
        if (bounded)
        {
            hr = factory->CreateBitmapScaler(&scaler);
            if (SUCCEEDED(hr)) hr = scaler->Initialize(frame, boundedWidth, boundedHeight, WICBitmapInterpolationModeFant);
            scaledWidth = boundedWidth;
            scaledHeight = boundedHeight;
        }
        IWICBitmapSource* decodeSource = scaler ? static_cast<IWICBitmapSource*>(scaler) : static_cast<IWICBitmapSource*>(frame);
        if (SUCCEEDED(hr)) hr = factory->CreateFormatConverter(&converter);
        if (SUCCEEDED(hr)) hr = converter->Initialize(decodeSource, GUID_WICPixelFormat32bppPBGRA,
                                                       WICBitmapDitherTypeNone, nullptr, 0.0,
                                                       WICBitmapPaletteTypeCustom);

        const std::uint64_t sourceBytes64 = static_cast<std::uint64_t>(scaledWidth) * scaledHeight * 4ull;
        const bool validSource = WithinDecodeCap(scaledWidth, scaledHeight) &&
                                 scaledWidth <= std::numeric_limits<UINT>::max() / 4u &&
                                 sourceBytes64 <= std::numeric_limits<UINT>::max();
        if (!validSource) hr = E_INVALIDARG;
        if (SUCCEEDED(hr)) converted = static_cast<std::uint8_t*>(CoTaskMemAlloc(static_cast<std::size_t>(sourceBytes64)));
        if (SUCCEEDED(hr) && !converted) hr = E_OUTOFMEMORY;
        if (SUCCEEDED(hr)) hr = converter->CopyPixels(nullptr, scaledWidth * 4, static_cast<UINT>(sourceBytes64), converted);
    }

    const UINT outWidth = rotated ? scaledHeight : scaledWidth;
    const UINT outHeight = rotated ? scaledWidth : scaledHeight;
    const std::uint64_t sourceBytes64 = static_cast<std::uint64_t>(scaledWidth) * scaledHeight * 4ull;
    const std::uint64_t outputBytes64 = static_cast<std::uint64_t>(outWidth) * outHeight * 4ull;
    const bool validSizes = SUCCEEDED(hr) && converted && width > 0 && height > 0 && scaledWidth > 0 && scaledHeight > 0 && outWidth > 0 && outHeight > 0 &&
                            width <= 65536 && height <= 65536 && scaledWidth <= 65536 && scaledHeight <= 65536 && outWidth <= 65536 && outHeight <= 65536 &&
                            sourceBytes64 <= std::numeric_limits<std::size_t>::max() && outputBytes64 <= std::numeric_limits<std::size_t>::max() &&
                            scaledWidth <= std::numeric_limits<UINT>::max() / 4u &&
                            sourceBytes64 <= std::numeric_limits<UINT>::max() && outputBytes64 <= std::numeric_limits<UINT>::max();
    if (!validSizes) hr = E_INVALIDARG;
    const std::size_t outputBytes = static_cast<std::size_t>(outputBytes64);

    // Orientation 1 is overwhelmingly common. Return the decoder/converter buffer directly instead
    // of allocating and copying another complete surface. Rotated/mirrored EXIF cases transform only
    // the already-reduced preview when a bounded first frame was requested.
    if (SUCCEEDED(hr) && orientation == 1)
    {
        image->width = scaledWidth;
        image->height = scaledHeight;
        image->stride = scaledWidth * 4;
        image->bufferBytes = sourceBytes64;
        image->data = converted;
        converted = nullptr;
        ok = 1;
    }
    else
    {
        if (SUCCEEDED(hr)) oriented = static_cast<std::uint8_t*>(CoTaskMemAlloc(outputBytes));
        if (SUCCEEDED(hr) && !oriented) hr = E_OUTOFMEMORY;
        if (SUCCEEDED(hr))
        {
            OrientBgra(converted, scaledWidth, scaledHeight, orientation, oriented, outWidth);
            image->width = outWidth;
            image->height = outHeight;
            image->stride = outWidth * 4;
            image->bufferBytes = outputBytes64;
            image->data = oriented;
            oriented = nullptr;
            ok = 1;
        }
    }

    if (oriented) CoTaskMemFree(oriented);
    if (converted) CoTaskMemFree(converted);
    if (converter) converter->Release();
    if (scaler) scaler->Release();
    if (frame) frame->Release();
    if (decoder) decoder->Release();
    if (factory) factory->Release();
    if (mustUninit) CoUninitialize();
    return ok;
}

extern "C" __declspec(dllexport) int __cdecl GlideDecodeImageW(const wchar_t* path, std::uint32_t maxLongestSide, GlideDecodedImage* image)
{
    return DecodeImageBoundedW(path, maxLongestSide, maxLongestSide, false, true, image);
}

// Caller-provided destination for a full-resolution decode: returns a writable 32bpp PBGRA surface of
// width x height pixels and its row stride, or nullptr to abort. Lets the managed side decode straight
// into the presentation bitmap instead of into a CoTaskMem buffer that is then copied (which doubled the
// peak memory of every full decode: 2 x 384 MB for a 100 MP frame).
typedef void* (__cdecl* GlideSurfaceAllocator)(void* context, std::uint32_t width, std::uint32_t height, std::uint32_t* stride);

// Full-resolution decode of frame 0 into a caller surface. Returns 1 on success, 0 on failure, and 2
// when the image needs an EXIF orientation transform (the caller then uses GlideDecodeImageW, which
// rotates through an intermediate buffer). Pixels are identical to GlideDecodeImageW(path, 0).
extern "C" __declspec(dllexport) int __cdecl GlideDecodeImageIntoW(const wchar_t* path, GlideSurfaceAllocator allocate, void* context)
{
    if (!path || !allocate) return 0;
    const HRESULT init = CoInitializeEx(nullptr, COINIT_MULTITHREADED);
    const bool mustUninit = SUCCEEDED(init);
    if (FAILED(init) && init != RPC_E_CHANGED_MODE) return 0;

    IWICImagingFactory* factory = nullptr;
    IWICBitmapDecoder* decoder = nullptr;
    IWICBitmapFrameDecode* frame = nullptr;
    IWICFormatConverter* converter = nullptr;
    int result = 0;

    HRESULT hr = CoCreateInstance(CLSID_WICImagingFactory, nullptr, CLSCTX_INPROC_SERVER, IID_PPV_ARGS(&factory));
    if (SUCCEEDED(hr)) hr = factory->CreateDecoderFromFilename(path, nullptr, GENERIC_READ,
                                                               WICDecodeMetadataCacheOnDemand, &decoder);
    if (SUCCEEDED(hr)) hr = decoder->GetFrame(0, &frame);
    UINT width = 0, height = 0;
    if (SUCCEEDED(hr)) hr = frame->GetSize(&width, &height);
    if (SUCCEEDED(hr) && ReadOrientation(frame) != 1)
    {
        result = 2;
        hr = E_ABORT;
    }
    const std::uint64_t minimumStride = static_cast<std::uint64_t>(width) * 4ull;
    // Decoded-pixel cap before the caller is asked for a surface (R3: the managed side may not know the size).
    if (SUCCEEDED(hr) && !WithinDecodeCap(width, height)) hr = E_INVALIDARG;
    if (SUCCEEDED(hr)) hr = factory->CreateFormatConverter(&converter);
    if (SUCCEEDED(hr)) hr = converter->Initialize(frame, GUID_WICPixelFormat32bppPBGRA,
                                                   WICBitmapDitherTypeNone, nullptr, 0.0, WICBitmapPaletteTypeCustom);
    std::uint32_t stride = 0;
    std::uint8_t* destination = nullptr;
    if (SUCCEEDED(hr))
    {
        destination = static_cast<std::uint8_t*>(allocate(context, width, height, &stride));
        const std::uint64_t bytes = static_cast<std::uint64_t>(stride) * height;
        if (!destination || stride < minimumStride || bytes > std::numeric_limits<UINT>::max()) hr = E_OUTOFMEMORY;
    }
    if (SUCCEEDED(hr)) hr = converter->CopyPixels(nullptr, stride, static_cast<UINT>(static_cast<std::uint64_t>(stride) * height), destination);
    if (SUCCEEDED(hr)) result = 1;

    if (converter) converter->Release();
    if (frame) frame->Release();
    if (decoder) decoder->Release();
    if (factory) factory->Release();
    if (mustUninit) CoUninitialize();
    return result;
}

// Compatibility entry point retains the safe colour-first behaviour. New managed callers use the
// Ex variant so Maximum speed can deliberately opt into level 0 while Balanced remains colour-first.
extern "C" __declspec(dllexport) int __cdecl GlideDecodeImageBoundedW(const wchar_t* path, std::uint32_t maxWidth, std::uint32_t maxHeight, GlideDecodedImage* image)
{
    return DecodeImageBoundedW(path, maxWidth, maxHeight, true, true, image);
}

extern "C" __declspec(dllexport) int __cdecl GlideDecodeImageBoundedExW(const wchar_t* path, std::uint32_t maxWidth, std::uint32_t maxHeight,
                                                                          int progressiveColourFirst, GlideDecodedImage* image)
{
    return DecodeImageBoundedW(path, maxWidth, maxHeight, true, progressiveColourFirst != 0, image);
}


// Last-resort rasterisation bridge for declared formats that have a registered Windows Shell
// thumbnail provider but no direct WIC/Avalonia/bundled codec. THUMBNAILONLY is intentional: Glide
// rejects generic file icons as fake image support. The cache-only variant is also used as a first-
// paint accelerator: it is forbidden from extracting a new thumbnail, so a miss falls through to
// the real decoder instead of turning the Shell into hidden foreground decode work.
static int DecodeShellPreviewBoundedW(const wchar_t* path, std::uint32_t maxWidth, std::uint32_t maxHeight,
                                      SIIGBF flags, GlideDecodedImage* image)
{
    if (!path || !image) return 0;
    *image = {};
    maxWidth = std::clamp<std::uint32_t>(maxWidth ? maxWidth : 2048u, 64u, 8192u);
    maxHeight = std::clamp<std::uint32_t>(maxHeight ? maxHeight : 2048u, 64u, 8192u);

    const HRESULT init = CoInitializeEx(nullptr, COINIT_MULTITHREADED);
    const bool mustUninit = SUCCEEDED(init);
    if (FAILED(init) && init != RPC_E_CHANGED_MODE) return 0;

    IShellItemImageFactory* shellFactory = nullptr;
    HBITMAP shellBitmap = nullptr;
    IWICImagingFactory* wic = nullptr;
    IWICBitmap* sourceBitmap = nullptr;
    IWICBitmapScaler* scaler = nullptr;
    IWICFormatConverter* converter = nullptr;
    std::uint8_t* pixels = nullptr;
    int ok = 0;

    HRESULT hr = SHCreateItemFromParsingName(path, nullptr, IID_PPV_ARGS(&shellFactory));
    if (SUCCEEDED(hr))
    {
        SIZE requested{ static_cast<LONG>(maxWidth), static_cast<LONG>(maxHeight) };
        hr = shellFactory->GetImage(requested, flags, &shellBitmap);
    }
    if (SUCCEEDED(hr)) hr = CoCreateInstance(CLSID_WICImagingFactory, nullptr, CLSCTX_INPROC_SERVER, IID_PPV_ARGS(&wic));
    if (SUCCEEDED(hr)) hr = wic->CreateBitmapFromHBITMAP(shellBitmap, nullptr, WICBitmapUsePremultipliedAlpha, &sourceBitmap);

    UINT width = 0, height = 0;
    if (SUCCEEDED(hr)) hr = sourceBitmap->GetSize(&width, &height);
    UINT scaledWidth = width, scaledHeight = height;
    if (SUCCEEDED(hr) && width > 0 && height > 0 && (width > maxWidth || height > maxHeight))
    {
        const double ratio = std::min(static_cast<double>(maxWidth) / static_cast<double>(width),
                                      static_cast<double>(maxHeight) / static_cast<double>(height));
        scaledWidth = std::max<UINT>(1, static_cast<UINT>(std::floor(width * ratio)));
        scaledHeight = std::max<UINT>(1, static_cast<UINT>(std::floor(height * ratio)));
        hr = wic->CreateBitmapScaler(&scaler);
        if (SUCCEEDED(hr)) hr = scaler->Initialize(sourceBitmap, scaledWidth, scaledHeight, WICBitmapInterpolationModeFant);
    }
    IWICBitmapSource* decodeSource = scaler ? static_cast<IWICBitmapSource*>(scaler) : static_cast<IWICBitmapSource*>(sourceBitmap);
    if (SUCCEEDED(hr)) hr = wic->CreateFormatConverter(&converter);
    if (SUCCEEDED(hr)) hr = converter->Initialize(decodeSource, GUID_WICPixelFormat32bppPBGRA,
        WICBitmapDitherTypeNone, nullptr, 0.0, WICBitmapPaletteTypeCustom);

    const std::uint64_t bytes64 = static_cast<std::uint64_t>(scaledWidth) * scaledHeight * 4ull;
    const bool valid = scaledWidth > 0 && scaledHeight > 0 && scaledWidth <= 65536 && scaledHeight <= 65536 &&
        scaledWidth <= std::numeric_limits<UINT>::max() / 4u && bytes64 <= std::numeric_limits<UINT>::max();
    if (!valid) hr = E_INVALIDARG;
    if (SUCCEEDED(hr)) pixels = static_cast<std::uint8_t*>(CoTaskMemAlloc(static_cast<std::size_t>(bytes64)));
    if (SUCCEEDED(hr) && !pixels) hr = E_OUTOFMEMORY;
    if (SUCCEEDED(hr)) hr = converter->CopyPixels(nullptr, scaledWidth * 4, static_cast<UINT>(bytes64), pixels);
    if (SUCCEEDED(hr))
    {
        image->width = scaledWidth;
        image->height = scaledHeight;
        image->stride = scaledWidth * 4;
        image->bufferBytes = bytes64;
        image->data = pixels;
        pixels = nullptr;
        ok = 1;
    }

    if (pixels) CoTaskMemFree(pixels);
    if (converter) converter->Release();
    if (scaler) scaler->Release();
    if (sourceBitmap) sourceBitmap->Release();
    if (wic) wic->Release();
    if (shellBitmap) DeleteObject(shellBitmap);
    if (shellFactory) shellFactory->Release();
    if (mustUninit) CoUninitialize();
    return ok;
}

extern "C" __declspec(dllexport) int __cdecl GlideDecodeShellPreviewW(const wchar_t* path, std::uint32_t maxLongestSide, GlideDecodedImage* image)
{
    maxLongestSide = std::clamp<std::uint32_t>(maxLongestSide ? maxLongestSide : 2048u, 64u, 8192u);
    return DecodeShellPreviewBoundedW(path, maxLongestSide, maxLongestSide,
        static_cast<SIIGBF>(SIIGBF_THUMBNAILONLY | SIIGBF_BIGGERSIZEOK), image);
}

extern "C" __declspec(dllexport) int __cdecl GlideDecodeShellCachedPreviewW(const wchar_t* path, std::uint32_t maxWidth,
                                                                             std::uint32_t maxHeight, GlideDecodedImage* image)
{
    // INCACHEONLY guarantees this path never invokes the registered thumbnail extractor. With
    // THUMBNAILONLY it also cannot silently substitute a generic file icon on a cache miss.
    return DecodeShellPreviewBoundedW(path, maxWidth, maxHeight,
        static_cast<SIIGBF>(SIIGBF_THUMBNAILONLY | SIIGBF_BIGGERSIZEOK | SIIGBF_INCACHEONLY), image);
}

extern "C" __declspec(dllexport) void __cdecl GlideFreeImageBuffer(void* data)
{
    if (data) CoTaskMemFree(data);
}

#include <cstdio>
#include <vector>

// ---------------------------------------------------------------------------------------------------
// Encoding (Save / Save as / Save selection) through the Windows Imaging Component.
//
// Input is always the viewer's premultiplied BGRA (rotation/flip already baked in). Format ids are a
// stable C ABI shared with NativeImageEncoder.WicFormat:
//   0 PNG  1 JPEG  2 BMP  3 TIFF   (built into every Windows; the original 4.x set)
//   4 GIF (single frame, 8-bit palette)  5 JPEG XR (.jxr/.wdp)  (built into every Windows)
//   6 HEIF/HEIC (HEVC)  7 AVIF (HEIF container, AV1)  8 JPEG XL   (only when a Store codec registers an
//     encoder; GlideProbeEncoderW tells the managed side whether this PC can write them)
// quality is 0..1 for the lossy encoders (JPEG, JPEG XR, HEIF, AVIF, JPEG XL); <=0 means 0.95.
// ---------------------------------------------------------------------------------------------------
#include <wincodecsdk.h>
#include <wrl/client.h>
using Microsoft::WRL::ComPtr;

namespace
{
    // Declared locally so the bridge builds against older Windows SDKs too (same values as wincodec.h).
    const GUID GlideContainerHeif = { 0xe1e62521, 0x6787, 0x405b, { 0xa3, 0x39, 0x50, 0x07, 0x15, 0xb5, 0x76, 0x3f } };
    const GUID GlideContainerJpegXL = { 0xfec14e3f, 0x427a, 0x4736, { 0xaa, 0xe6, 0x27, 0xed, 0x84, 0xf6, 0x93, 0x22 } };

    enum : std::uint32_t
    {
        EncodePng = 0, EncodeJpeg = 1, EncodeBmp = 2, EncodeTiff = 3, EncodeGif = 4, EncodeJxr = 5,
        EncodeHeif = 6, EncodeAvif = 7, EncodeJxl = 8, EncodeLast = 8
    };

    // GlideEncodeImage2W flags.
    enum : std::uint32_t
    {
        EncodeCopyIcc = 1u << 0,      // copy the source's embedded ICC profile (colour context)
        EncodeCopyExifXmp = 1u << 1,  // copy EXIF (IFD0 descriptive tags + Exif/GPS IFDs) and XMP; Orientation := 1
        EncodeLossless = 1u << 2      // JPEG XR: lossless
    };

    // WICHeifCompressionOption values (wincodec.h, Windows 11 SDK).
    constexpr BYTE HeifCompressionHevc = 2;
    constexpr BYTE HeifCompressionAv1 = 3;

    const GUID* EncodeContainer(std::uint32_t format)
    {
        switch (format)
        {
        case EncodePng: return &GUID_ContainerFormatPng;
        case EncodeJpeg: return &GUID_ContainerFormatJpeg;
        case EncodeBmp: return &GUID_ContainerFormatBmp;
        case EncodeTiff: return &GUID_ContainerFormatTiff;
        case EncodeGif: return &GUID_ContainerFormatGif;
        case EncodeJxr: return &GUID_ContainerFormatWmp;
        case EncodeHeif:
        case EncodeAvif: return &GlideContainerHeif;
        case EncodeJxl: return &GlideContainerJpegXL;
        default: return nullptr;
        }
    }

    struct ComScope
    {
        bool uninit = false;
        bool ok = false;
        ComScope()
        {
            const HRESULT hr = CoInitializeEx(nullptr, COINIT_MULTITHREADED);
            uninit = SUCCEEDED(hr);
            ok = SUCCEEDED(hr) || hr == RPC_E_CHANGED_MODE;
        }
        ~ComScope() { if (uninit) CoUninitialize(); }
        ComScope(const ComScope&) = delete;
        ComScope& operator=(const ComScope&) = delete;
    };

    // Writes one encoder option; an encoder that does not know the option keeps its default.
    HRESULT WriteOption(IPropertyBag2* bag, const wchar_t* name, const VARIANT& value)
    {
        if (!bag) return E_POINTER;
        PROPBAG2 option{};
        option.pstrName = const_cast<LPOLESTR>(name);
        VARIANT copy = value;
        return bag->Write(1, &option, &copy);
    }

    HRESULT WriteFloatOption(IPropertyBag2* bag, const wchar_t* name, float value)
    {
        VARIANT v; VariantInit(&v); v.vt = VT_R4; v.fltVal = value;
        return WriteOption(bag, name, v);
    }

    void CopyBlock(IWICComponentFactory* components, IWICMetadataQueryReader* reader, const wchar_t* from,
                   IWICMetadataQueryWriter* writer, const wchar_t* to)
    {
        PROPVARIANT value; PropVariantInit(&value);
        if (SUCCEEDED(reader->GetMetadataByName(from, &value)) && value.vt == VT_UNKNOWN && value.punkVal)
        {
            ComPtr<IWICMetadataQueryReader> blockReader;
            ComPtr<IWICMetadataQueryWriter> blockWriter;
            if (SUCCEEDED(value.punkVal->QueryInterface(IID_PPV_ARGS(&blockReader))) &&
                SUCCEEDED(components->CreateQueryWriterFromReader(blockReader.Get(), &GUID_VendorMicrosoft, &blockWriter)))
            {
                PROPVARIANT block; PropVariantInit(&block);
                block.vt = VT_UNKNOWN;
                block.punkVal = blockWriter.Get(); // borrowed: SetMetadataByName takes its own reference
                writer->SetMetadataByName(to, &block);
            }
        }
        PropVariantClear(&value);
    }

    void CopyValue(IWICMetadataQueryReader* reader, const wchar_t* from, IWICMetadataQueryWriter* writer, const wchar_t* to)
    {
        PROPVARIANT value; PropVariantInit(&value);
        if (SUCCEEDED(reader->GetMetadataByName(from, &value)) && value.vt != VT_EMPTY && value.vt != VT_UNKNOWN)
            writer->SetMetadataByName(to, &value);
        PropVariantClear(&value);
    }

    bool HasBlock(IWICMetadataQueryReader* reader, const wchar_t* name)
    {
        PROPVARIANT value; PropVariantInit(&value);
        const bool found = SUCCEEDED(reader->GetMetadataByName(name, &value)) && value.vt == VT_UNKNOWN;
        PropVariantClear(&value);
        return found;
    }

    void SetUInt(IWICMetadataQueryWriter* writer, const wchar_t* name, VARTYPE type, std::uint32_t number)
    {
        PROPVARIANT value; PropVariantInit(&value);
        value.vt = type;
        if (type == VT_UI2) value.uiVal = static_cast<USHORT>(number); else value.ulVal = number;
        writer->SetMetadataByName(name, &value);
    }

    // Copies colour profile and descriptive metadata from the first frame of `sourcePath`. Best effort:
    // a source WIC cannot read, or a block the destination cannot hold, is skipped (the pixels still save).
    // The EXIF thumbnail (IFD1, "/app1/thumb") is never copied: it would show the old orientation.
    void CopySourceMetadata(IWICImagingFactory* factory, IWICBitmapFrameEncode* frame, std::uint32_t format,
                            std::uint32_t flags, const wchar_t* sourcePath, std::uint32_t width, std::uint32_t height)
    {
        ComPtr<IWICBitmapDecoder> decoder;
        ComPtr<IWICBitmapFrameDecode> source;
        if (FAILED(factory->CreateDecoderFromFilename(sourcePath, nullptr, GENERIC_READ, WICDecodeMetadataCacheOnDemand, &decoder)) ||
            FAILED(decoder->GetFrame(0, &source)))
            return;

        if ((flags & EncodeCopyIcc) && format != EncodeGif && format != EncodeBmp)
        {
            UINT count = 0;
            if (SUCCEEDED(source->GetColorContexts(0, nullptr, &count)) && count > 0 && count <= 8)
            {
                IWICColorContext* contexts[8]{};
                UINT created = 0;
                for (; created < count; ++created)
                    if (FAILED(factory->CreateColorContext(&contexts[created]))) break;
                UINT actual = 0;
                if (created == count && SUCCEEDED(source->GetColorContexts(count, contexts, &actual)))
                {
                    for (UINT i = 0; i < actual; ++i)
                    {
                        WICColorContextType type{};
                        if (SUCCEEDED(contexts[i]->GetType(&type)) && type == WICColorContextProfile)
                        {
                            frame->SetColorContexts(1, &contexts[i]);
                            break;
                        }
                    }
                }
                for (UINT i = 0; i < created; ++i) contexts[i]->Release();
            }
        }

        if (!(flags & EncodeCopyExifXmp)) return;
        const wchar_t* dstIfd = nullptr;
        const wchar_t* dstXmp = nullptr;
        switch (format)
        {
        case EncodeJpeg: dstIfd = L"/app1/ifd"; dstXmp = L"/xmp"; break;
        case EncodeTiff:
        case EncodeJxr: dstIfd = L"/ifd"; dstXmp = L"/ifd/xmp"; break;
        case EncodeHeif:
        case EncodeAvif:
        case EncodeJxl: dstIfd = L"/ifd"; dstXmp = L"/xmp"; break;
        default: return; // PNG/WebP metadata is written by the managed container writer; GIF/BMP hold none
        }
        ComPtr<IWICMetadataQueryReader> reader;
        ComPtr<IWICMetadataQueryWriter> writer;
        ComPtr<IWICComponentFactory> components;
        if (FAILED(source->GetMetadataQueryReader(&reader)) || FAILED(frame->GetMetadataQueryWriter(&writer)) ||
            FAILED(factory->QueryInterface(IID_PPV_ARGS(&components))))
            return;

        const wchar_t* srcIfd = HasBlock(reader.Get(), L"/app1/ifd") ? L"/app1/ifd" : HasBlock(reader.Get(), L"/ifd") ? L"/ifd" : nullptr;
        if (srcIfd)
        {
            wchar_t from[96], to[96];
            for (const wchar_t* sub : { L"exif", L"gps" })
            {
                swprintf_s(from, L"%s/%s", srcIfd, sub);
                swprintf_s(to, L"%s/%s", dstIfd, sub);
                CopyBlock(components.Get(), reader.Get(), from, writer.Get(), to);
            }
            // Descriptive IFD0 tags only: never the source's image-structure tags (size, strips, compression).
            // ImageDescription, Make, Model, XResolution, YResolution, ResolutionUnit, Software, DateTime,
            // Artist, Copyright.
            for (const unsigned tag : { 270u, 271u, 272u, 282u, 283u, 296u, 305u, 306u, 315u, 33432u })
            {
                swprintf_s(from, L"%s/{ushort=%u}", srcIfd, tag);
                swprintf_s(to, L"%s/{ushort=%u}", dstIfd, tag);
                CopyValue(reader.Get(), from, writer.Get(), to);
            }
            // Rotation/flip is baked into the pixels, so the saved file is always upright.
            swprintf_s(to, L"%s/{ushort=274}", dstIfd);
            SetUInt(writer.Get(), to, VT_UI2, 1);
            // The Exif IFD's pixel dimensions describe the stored image: swap them with a 90-degree turn.
            swprintf_s(from, L"%s/exif", dstIfd);
            PROPVARIANT probe; PropVariantInit(&probe);
            if (SUCCEEDED(writer->GetMetadataByName(from, &probe)))
            {
                swprintf_s(to, L"%s/exif/{ushort=40962}", dstIfd);
                SetUInt(writer.Get(), to, VT_UI4, width);
                swprintf_s(to, L"%s/exif/{ushort=40963}", dstIfd);
                SetUInt(writer.Get(), to, VT_UI4, height);
            }
            PropVariantClear(&probe);
        }
        else if (format == EncodeJpeg || format == EncodeTiff || format == EncodeJxr)
        {
            wchar_t to[96];
            swprintf_s(to, L"%s/{ushort=274}", dstIfd);
            SetUInt(writer.Get(), to, VT_UI2, 1);
        }

        const wchar_t* srcXmp = HasBlock(reader.Get(), L"/xmp") ? L"/xmp" : HasBlock(reader.Get(), L"/ifd/xmp") ? L"/ifd/xmp" : nullptr;
        if (srcXmp)
        {
            CopyBlock(components.Get(), reader.Get(), srcXmp, writer.Get(), dstXmp);
            wchar_t name[96];
            swprintf_s(name, L"%s/tiff:Orientation", dstXmp);
            writer->RemoveMetadataByName(name);
        }
    }

    bool HasTransparency(const BYTE* pixels, std::uint32_t width, std::uint32_t height, std::uint32_t stride)
    {
        for (std::uint32_t y = 0; y < height; ++y)
        {
            const BYTE* row = pixels + static_cast<size_t>(y) * stride;
            for (std::uint32_t x = 0; x < width; ++x)
                if (row[x * 4 + 3] != 255) return true;
        }
        return false;
    }

    HRESULT EncodeToStream(IWICImagingFactory* factory, IStream* stream, std::uint32_t width, std::uint32_t height,
                           std::uint32_t stride, const void* pixels, std::uint32_t format, float quality,
                           std::uint32_t flags, const wchar_t* metadataSource)
    {
        const GUID* container = EncodeContainer(format);
        if (!container) return E_INVALIDARG;
        if (!(quality > 0.0f) || quality > 1.0f) quality = 0.95f;
        ComPtr<IWICBitmap> bitmap;
        ComPtr<IWICBitmapEncoder> encoder;
        ComPtr<IWICBitmapFrameEncode> frame;
        ComPtr<IPropertyBag2> properties;
        HRESULT hr = factory->CreateBitmapFromMemory(width, height, GUID_WICPixelFormat32bppPBGRA, stride, stride * height,
            static_cast<BYTE*>(const_cast<void*>(pixels)), &bitmap);
        if (SUCCEEDED(hr)) hr = factory->CreateEncoder(*container, nullptr, &encoder);
        if (SUCCEEDED(hr)) hr = encoder->Initialize(stream, WICBitmapEncoderNoCache);
        if (SUCCEEDED(hr)) hr = encoder->CreateNewFrame(&frame, &properties);
        if (FAILED(hr)) return hr;
        if (properties)
        {
            switch (format)
            {
            case EncodeJpeg:
                WriteFloatOption(properties.Get(), L"ImageQuality", quality);
                break;
            case EncodeJxr:
                if (flags & EncodeLossless)
                {
                    VARIANT v; VariantInit(&v); v.vt = VT_BOOL; v.boolVal = VARIANT_TRUE;
                    WriteOption(properties.Get(), L"Lossless", v);
                }
                else WriteFloatOption(properties.Get(), L"ImageQuality", quality);
                break;
            case EncodeHeif:
            case EncodeAvif:
            {
                VARIANT v; VariantInit(&v); v.vt = VT_UI1;
                v.bVal = format == EncodeAvif ? HeifCompressionAv1 : HeifCompressionHevc;
                const HRESULT method = WriteOption(properties.Get(), L"HeifCompressionMethod", v);
                // AVIF exists only through this option; an older HEIF encoder without it would write HEVC.
                if (format == EncodeAvif && FAILED(method)) return method;
                WriteFloatOption(properties.Get(), L"ImageQuality", quality);
                break;
            }
            case EncodeJxl:
                WriteFloatOption(properties.Get(), L"ImageQuality", quality);
                break;
            default:
                break;
            }
        }
        hr = frame->Initialize(properties.Get());
        if (SUCCEEDED(hr)) hr = frame->SetSize(width, height);
        if (FAILED(hr)) return hr;

        ComPtr<IWICBitmapSource> source = bitmap;
        ComPtr<IWICFormatConverter> straight;
        ComPtr<IWICFormatConverter> converter;
        ComPtr<IWICPalette> palette;
        if (format == EncodeGif)
        {
            // GIF is 8-bit indexed: build an optimal 256-colour palette (one entry transparent when the
            // image has transparency) and error-diffuse into it.
            const bool transparent = HasTransparency(static_cast<const BYTE*>(pixels), width, height, stride);
            hr = factory->CreateFormatConverter(&straight);
            if (SUCCEEDED(hr)) hr = straight->Initialize(bitmap.Get(), GUID_WICPixelFormat32bppBGRA, WICBitmapDitherTypeNone, nullptr, 0.0, WICBitmapPaletteTypeCustom);
            if (SUCCEEDED(hr)) hr = factory->CreatePalette(&palette);
            if (SUCCEEDED(hr)) hr = palette->InitializeFromBitmap(straight.Get(), 256, transparent ? TRUE : FALSE);
            if (SUCCEEDED(hr)) hr = factory->CreateFormatConverter(&converter);
            if (SUCCEEDED(hr)) hr = converter->Initialize(straight.Get(), GUID_WICPixelFormat8bppIndexed,
                WICBitmapDitherTypeErrorDiffusion, palette.Get(), 50.0, WICBitmapPaletteTypeCustom);
            WICPixelFormatGUID indexed = GUID_WICPixelFormat8bppIndexed;
            if (SUCCEEDED(hr)) hr = frame->SetPixelFormat(&indexed);
            if (SUCCEEDED(hr) && !IsEqualGUID(indexed, GUID_WICPixelFormat8bppIndexed)) hr = WINCODEC_ERR_UNSUPPORTEDPIXELFORMAT;
            if (SUCCEEDED(hr)) hr = frame->SetPalette(palette.Get());
            if (SUCCEEDED(hr) && transparent)
            {
                WICColor colors[256]{};
                UINT count = 0;
                ComPtr<IWICMetadataQueryWriter> writer;
                if (SUCCEEDED(palette->GetColors(256, colors, &count)) && SUCCEEDED(frame->GetMetadataQueryWriter(&writer)))
                {
                    for (UINT i = 0; i < count; ++i)
                    {
                        if ((colors[i] >> 24) != 0) continue;
                        PROPVARIANT flag; PropVariantInit(&flag); flag.vt = VT_BOOL; flag.boolVal = VARIANT_TRUE;
                        writer->SetMetadataByName(L"/grctlext/TransparencyFlag", &flag);
                        PROPVARIANT index; PropVariantInit(&index); index.vt = VT_UI1; index.bVal = static_cast<BYTE>(i);
                        writer->SetMetadataByName(L"/grctlext/TransparentColorIndex", &index);
                        break;
                    }
                }
            }
            if (SUCCEEDED(hr)) source = converter;
        }
        else
        {
            // PNG/TIFF/JPEG XR keep alpha as straight 32-bit BGRA. JPEG and BMP use 24-bit BGR (the managed
            // side has already composited transparency over the save background). HEIF/AVIF/JPEG XL ask for
            // BGRA and take whatever the codec negotiates.
            WICPixelFormatGUID pixelFormat = (format == EncodeJpeg || format == EncodeBmp) ? GUID_WICPixelFormat24bppBGR : GUID_WICPixelFormat32bppBGRA;
            hr = frame->SetPixelFormat(&pixelFormat);
            // SetPixelFormat is a negotiation: convert to what the encoder accepted.
            if (SUCCEEDED(hr) && !IsEqualGUID(pixelFormat, GUID_WICPixelFormat32bppPBGRA))
            {
                hr = factory->CreateFormatConverter(&converter);
                if (SUCCEEDED(hr)) hr = converter->Initialize(bitmap.Get(), pixelFormat, WICBitmapDitherTypeNone, nullptr, 0.0, WICBitmapPaletteTypeCustom);
                if (SUCCEEDED(hr)) source = converter;
            }
        }
        if (FAILED(hr)) return hr;
        if (metadataSource && *metadataSource && (flags & (EncodeCopyIcc | EncodeCopyExifXmp)))
            CopySourceMetadata(factory, frame.Get(), format, flags, metadataSource, width, height);
        hr = frame->WriteSource(source.Get(), nullptr);
        if (SUCCEEDED(hr)) hr = frame->Commit();
        if (SUCCEEDED(hr)) hr = encoder->Commit();
        return hr;
    }

    bool ValidEncodeInput(std::uint32_t width, std::uint32_t height, std::uint32_t stride, const void* pixels)
    {
        const std::uint64_t minimumStride = static_cast<std::uint64_t>(width) * 4ull;
        const std::uint64_t bufferBytes = static_cast<std::uint64_t>(stride) * height;
        return pixels && width > 0 && height > 0 && width <= std::numeric_limits<UINT>::max() / 4u &&
            static_cast<std::uint64_t>(stride) >= minimumStride && bufferBytes <= std::numeric_limits<UINT>::max();
    }

    // Encodes into memory; on success the bytes are returned in a CoTaskMemAlloc block (GlideFreeImageBuffer).
    HRESULT EncodeToMemory(IWICImagingFactory* factory, std::uint32_t width, std::uint32_t height, std::uint32_t stride,
                           const void* pixels, std::uint32_t format, float quality, std::uint32_t flags,
                           const wchar_t* metadataSource, void** outData, std::uint64_t* outSize)
    {
        ComPtr<IStream> memory;
        HRESULT hr = CreateStreamOnHGlobal(nullptr, TRUE, &memory);
        if (SUCCEEDED(hr)) hr = EncodeToStream(factory, memory.Get(), width, height, stride, pixels, format, quality, flags, metadataSource);
        STATSTG stat{};
        if (SUCCEEDED(hr)) hr = memory->Stat(&stat, STATFLAG_NONAME);
        if (SUCCEEDED(hr) && (stat.cbSize.QuadPart == 0 || stat.cbSize.QuadPart > 0x7FFFFFFFull)) hr = E_FAIL;
        HGLOBAL global = nullptr;
        if (SUCCEEDED(hr)) hr = GetHGlobalFromStream(memory.Get(), &global);
        if (FAILED(hr)) return hr;
        const auto size = static_cast<size_t>(stat.cbSize.QuadPart);
        void* copy = CoTaskMemAlloc(size);
        if (!copy) return E_OUTOFMEMORY;
        const void* locked = GlobalLock(global);
        if (!locked) { CoTaskMemFree(copy); return E_FAIL; }
        std::memcpy(copy, locked, size);
        GlobalUnlock(global);
        *outData = copy;
        *outSize = size;
        return S_OK;
    }
}

// Encodes a premultiplied BGRA image. With `path` the result is written to that file; with a null path it is
// returned in *outData/*outSize (free with GlideFreeImageBuffer). `metadataSource` (optional) is the original
// file whose ICC profile / EXIF / XMP are carried over according to `flags`. Returns 1 on success.
extern "C" __declspec(dllexport) int __cdecl GlideEncodeImage2W(const wchar_t* path,
    std::uint32_t width, std::uint32_t height, std::uint32_t stride, const void* pixels, std::uint32_t format,
    float quality, std::uint32_t flags, const wchar_t* metadataSource, void** outData, std::uint64_t* outSize)
{
    if (outData) *outData = nullptr;
    if (outSize) *outSize = 0;
    if (!ValidEncodeInput(width, height, stride, pixels) || format > EncodeLast) return 0;
    if (!path && (!outData || !outSize)) return 0;
    ComScope com;
    if (!com.ok) return 0;
    ComPtr<IWICImagingFactory> factory;
    HRESULT hr = CoCreateInstance(CLSID_WICImagingFactory, nullptr, CLSCTX_INPROC_SERVER, IID_PPV_ARGS(&factory));
    if (FAILED(hr)) return 0;
    if (!path)
        return SUCCEEDED(EncodeToMemory(factory.Get(), width, height, stride, pixels, format, quality, flags, metadataSource, outData, outSize)) ? 1 : 0;
    ComPtr<IWICStream> stream;
    hr = factory->CreateStream(&stream);
    if (SUCCEEDED(hr)) hr = stream->InitializeFromFilename(path, GENERIC_WRITE);
    if (SUCCEEDED(hr)) hr = EncodeToStream(factory.Get(), stream.Get(), width, height, stride, pixels, format, quality, flags, metadataSource);
    return SUCCEEDED(hr) ? 1 : 0;
}

// 1 when this PC can write `format`: a WIC encoder for its container is registered and (for the codecs that
// come from optional Store extensions: HEIF, AVIF, JPEG XL) a small trial encode succeeds. Called lazily by the
// managed side the first time a Save as list is built, never at startup.
extern "C" __declspec(dllexport) int __cdecl GlideProbeEncoderW(std::uint32_t format)
{
    const GUID* container = EncodeContainer(format);
    if (!container) return 0;
    ComScope com;
    if (!com.ok) return 0;
    ComPtr<IWICImagingFactory> factory;
    if (FAILED(CoCreateInstance(CLSID_WICImagingFactory, nullptr, CLSCTX_INPROC_SERVER, IID_PPV_ARGS(&factory)))) return 0;
    ComPtr<IEnumUnknown> components;
    if (FAILED(factory->CreateComponentEnumerator(WICEncoder, WICComponentEnumerateDefault, &components))) return 0;
    bool registered = false;
    for (;;)
    {
        ComPtr<IUnknown> item;
        ULONG fetched = 0;
        if (components->Next(1, &item, &fetched) != S_OK || fetched == 0) break;
        ComPtr<IWICBitmapEncoderInfo> info;
        GUID itemContainer{};
        if (SUCCEEDED(item.As(&info)) && SUCCEEDED(info->GetContainerFormat(&itemContainer)) && IsEqualGUID(itemContainer, *container))
        {
            registered = true;
            break;
        }
    }
    if (!registered) return 0;
    if (format < EncodeHeif) return 1;
    // The HEIF encoder is registered by the HEIF extension even when the HEVC/AV1 video encoder it needs is
    // missing, so only a real encode proves the format is writable.
    constexpr std::uint32_t side = 64;
    std::vector<std::uint32_t> pixels(side * side);
    for (std::uint32_t i = 0; i < side * side; ++i) pixels[i] = 0xFF000000u | (i * 2654435761u >> 8);
    void* data = nullptr;
    std::uint64_t size = 0;
    const HRESULT hr = EncodeToMemory(factory.Get(), side, side, side * 4, pixels.data(), format, 0.8f, 0, nullptr, &data, &size);
    bool ok = SUCCEEDED(hr) && data && size >= 12;
    if (ok && format == EncodeAvif)
    {
        // Must really be AVIF (ISO-BMFF 'ftyp' with an 'avif'/'avis' brand), not HEVC in a HEIF container.
        const auto* bytes = static_cast<const BYTE*>(data);
        const size_t box = std::min<size_t>(static_cast<size_t>(size), (static_cast<size_t>(bytes[0]) << 24) | (bytes[1] << 16) | (bytes[2] << 8) | bytes[3]);
        ok = std::memcmp(bytes + 4, "ftyp", 4) == 0;
        bool brand = false;
        for (size_t at = 8; ok && at + 4 <= box; at += 4)
            if (std::memcmp(bytes + at, "avif", 4) == 0 || std::memcmp(bytes + at, "avis", 4) == 0) brand = true;
        ok = ok && brand;
    }
    if (data) CoTaskMemFree(data);
    return ok ? 1 : 0;
}

// Compatibility exports (4.x-5.0.3 managed code): formats 0-3, no metadata.
extern "C" __declspec(dllexport) int __cdecl GlideEncodeImageExW(const wchar_t* path,
    std::uint32_t width, std::uint32_t height, std::uint32_t stride,
    const void* pixels, std::uint32_t format, float quality)
{
    if (!path || format > EncodeTiff) return 0;
    return GlideEncodeImage2W(path, width, height, stride, pixels, format, quality, 0, nullptr, nullptr, nullptr);
}

extern "C" __declspec(dllexport) int __cdecl GlideEncodeImageW(const wchar_t* path,
    std::uint32_t width, std::uint32_t height, std::uint32_t stride,
    const void* pixels, std::uint32_t format)
{
    if (format > 2) return 0;
    return GlideEncodeImageExW(path, width, height, stride, pixels, format, 0.95f);
}
