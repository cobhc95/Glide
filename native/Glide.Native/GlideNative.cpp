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
    if (FAILED(hr) || width == 0 || height == 0 || width > 65536 || height > 65536)
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
        const bool validSource = scaledWidth > 0 && scaledHeight > 0 && scaledWidth <= 65536 && scaledHeight <= 65536 &&
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

// Encodes an in-memory premultiplied BGRA crop through the same Windows Imaging
// Component boundary used by the decoder.  Keeping this in the small native bridge
// avoids pulling a second managed image stack into the application and preserves
// real PNG/JPEG/BMP output on Windows.
extern "C" __declspec(dllexport) int __cdecl GlideEncodeImageW(const wchar_t* path,
    std::uint32_t width, std::uint32_t height, std::uint32_t stride,
    const void* pixels, std::uint32_t format)
{
    const std::uint64_t minimumStride = static_cast<std::uint64_t>(width) * 4ull;
    const std::uint64_t bufferBytes = static_cast<std::uint64_t>(stride) * height;
    if (!path || !pixels || width == 0 || height == 0 || width > std::numeric_limits<UINT>::max() / 4u ||
        static_cast<std::uint64_t>(stride) < minimumStride || bufferBytes > std::numeric_limits<UINT>::max() || format > 2) return 0;
    const HRESULT init = CoInitializeEx(nullptr, COINIT_MULTITHREADED);
    const bool mustUninit = SUCCEEDED(init);
    if (FAILED(init) && init != RPC_E_CHANGED_MODE) return 0;
    IWICImagingFactory* factory = nullptr;
    IWICStream* stream = nullptr;
    IWICBitmap* bitmap = nullptr;
    IWICFormatConverter* formatConverter = nullptr;
    IWICBitmapFrameEncode* frame = nullptr;
    IWICBitmapEncoder* encoder = nullptr;
    IPropertyBag2* properties = nullptr;
    int ok = 0;
    HRESULT hr = CoCreateInstance(CLSID_WICImagingFactory, nullptr, CLSCTX_INPROC_SERVER,
                                  IID_PPV_ARGS(&factory));
    if (SUCCEEDED(hr)) hr = factory->CreateStream(&stream);
    if (SUCCEEDED(hr)) hr = stream->InitializeFromFilename(path, GENERIC_WRITE);
    if (SUCCEEDED(hr)) hr = factory->CreateBitmapFromMemory(width, height,
        GUID_WICPixelFormat32bppPBGRA, stride, stride * height,
        static_cast<BYTE*>(const_cast<void*>(pixels)), &bitmap);
    const GUID* container = format == 0 ? &GUID_ContainerFormatPng :
                            format == 1 ? &GUID_ContainerFormatJpeg : &GUID_ContainerFormatBmp;
    // CreateEncoder returns an encoder; create the frame with its property bag
    // so JPEG remains a genuine WIC encode.
    if (SUCCEEDED(hr)) hr = factory->CreateEncoder(*container, nullptr, &encoder);
    if (SUCCEEDED(hr)) hr = encoder->Initialize(stream, WICBitmapEncoderNoCache);
    if (SUCCEEDED(hr)) hr = encoder->CreateNewFrame(&frame, &properties);
    if (SUCCEEDED(hr) && format == 1 && properties)
    {
        PROPBAG2 option{}; option.pstrName = const_cast<LPOLESTR>(L"ImageQuality");
        VARIANT value; VariantInit(&value); value.vt = VT_R4; value.fltVal = 0.95f;
        properties->Write(1, &option, &value); VariantClear(&value);
    }
    if (SUCCEEDED(hr)) hr = frame->Initialize(properties);
    if (SUCCEEDED(hr)) hr = frame->SetSize(width, height);
    // PNG keeps alpha as 32-bit BGRA. JPEG and the broadly compatible BMP path
    // intentionally use 24-bit BGR; no undocumented BMP V5/alpha assumptions.
    WICPixelFormatGUID pixelFormat = format == 0 ? GUID_WICPixelFormat32bppBGRA : GUID_WICPixelFormat24bppBGR;
    if (SUCCEEDED(hr)) hr = frame->SetPixelFormat(&pixelFormat);
    IWICBitmapSource* source = bitmap;
    // SetPixelFormat is a negotiation: use the encoder's returned format rather
    // than assuming it accepted the request, and convert explicitly when needed.
    if (SUCCEEDED(hr) && !IsEqualGUID(pixelFormat, GUID_WICPixelFormat32bppPBGRA))
    {
        hr = factory->CreateFormatConverter(&formatConverter);
        if (SUCCEEDED(hr)) hr = formatConverter->Initialize(bitmap, pixelFormat,
            WICBitmapDitherTypeNone, nullptr, 0.0, WICBitmapPaletteTypeCustom);
        if (SUCCEEDED(hr)) source = formatConverter;
    }
    if (SUCCEEDED(hr)) hr = frame->WriteSource(source, nullptr);
    if (SUCCEEDED(hr)) hr = frame->Commit();
    if (SUCCEEDED(hr)) hr = encoder->Commit();
    ok = SUCCEEDED(hr) ? 1 : 0;
    if (properties) properties->Release();
    if (frame) frame->Release();
    if (encoder) encoder->Release();
    if (formatConverter) formatConverter->Release();
    if (bitmap) bitmap->Release();
    if (stream) stream->Release();
    if (factory) factory->Release();
    if (mustUninit) CoUninitialize();
    return ok;
}
