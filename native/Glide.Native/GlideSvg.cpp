// 5.2.0: Direct2D SVG rasteriser for Explorer-tab tiles of path-heavy vector files. Svg.Skia needs about 210 ms to parse and
// record a 5000-element document; Direct2D's built-in SVG engine (Windows 10 1703+, ID2D1DeviceContext5::CreateSvgDocument)
// renders the same file at tile size in about 25 ms (docs/audits/format-speed-audit-5.2.0.md, item 4). The managed caller
// (ThumbnailExtendedDecode) only sends documents it knows Direct2D draws like Svg.Skia (no text, CSS, filters, masks,
// patterns, embedded or external images) and falls back to Svg.Skia for everything else and whenever this returns 0.
//
// A software (WARP) Direct3D 11 device is used on purpose: it needs no GPU, works in remote sessions and on servers, and the
// tile is tiny. Output: premultiplied BGRA, allocated with CoTaskMemAlloc (free with GlideFreeImageBuffer).
#include <windows.h>
#include <objbase.h>
#include <d3d11.h>
#include <dxgi.h>
#include <d2d1_3.h>
#include <d2d1svg.h>
#include <shlwapi.h>
#include <algorithm>
#include <cmath>
#include <cstdint>
#include <cstring>

namespace
{
struct SvgDecodedImage
{
    std::uint32_t width;
    std::uint32_t height;
    std::uint32_t stride;
    std::uint64_t bufferBytes;
    void* data;
};

template <typename T>
void SafeRelease(T*& pointer)
{
    if (pointer)
    {
        pointer->Release();
        pointer = nullptr;
    }
}
} // namespace

extern "C" __declspec(dllexport) int __cdecl GlideRenderSvgBuffer(const std::uint8_t* data, std::uint32_t size, float intrinsicWidth,
                                                                  float intrinsicHeight, std::uint32_t maxLongestSide, SvgDecodedImage* image)
{
    if (!image) return 0;
    *image = {};
    if (!data || size == 0 || maxLongestSide == 0 || !(intrinsicWidth > 0.0f) || !(intrinsicHeight > 0.0f)) return 0;
    if (intrinsicWidth > 1.0e6f || intrinsicHeight > 1.0e6f) return 0;

    const double ratio = static_cast<double>(maxLongestSide) / std::max(intrinsicWidth, intrinsicHeight);
    const auto targetWidth = static_cast<std::uint32_t>(std::clamp(std::floor(intrinsicWidth * ratio + 0.5), 1.0, 4096.0));
    const auto targetHeight = static_cast<std::uint32_t>(std::clamp(std::floor(intrinsicHeight * ratio + 0.5), 1.0, 4096.0));

    const HRESULT comResult = CoInitializeEx(nullptr, COINIT_MULTITHREADED);
    const bool uninitialize = SUCCEEDED(comResult); // RPC_E_CHANGED_MODE: the thread already has an apartment, use it as it is

    IStream* stream = SHCreateMemStream(data, size);
    ID3D11Device* device = nullptr;
    ID3D11DeviceContext* context = nullptr;
    IDXGIDevice* dxgiDevice = nullptr;
    ID2D1Factory1* factory = nullptr;
    ID2D1Device* d2dDevice = nullptr;
    ID2D1DeviceContext* deviceContext = nullptr;
    ID2D1DeviceContext5* deviceContext5 = nullptr;
    ID3D11Texture2D* targetTexture = nullptr;
    ID3D11Texture2D* stagingTexture = nullptr;
    ID2D1Bitmap1* targetBitmap = nullptr;
    ID2D1SvgDocument* svg = nullptr;
    std::uint8_t* pixels = nullptr;
    bool ok = false;

    try
    {
        if (!stream) goto cleanup;
        {
            D3D_FEATURE_LEVEL level{};
            const D3D_FEATURE_LEVEL levels[] = { D3D_FEATURE_LEVEL_11_1, D3D_FEATURE_LEVEL_11_0, D3D_FEATURE_LEVEL_10_1 };
            if (FAILED(D3D11CreateDevice(nullptr, D3D_DRIVER_TYPE_WARP, nullptr, D3D11_CREATE_DEVICE_BGRA_SUPPORT, levels, 3,
                                         D3D11_SDK_VERSION, &device, &level, &context)) || !device)
                goto cleanup;
        }
        if (FAILED(device->QueryInterface(IID_PPV_ARGS(&dxgiDevice))) || !dxgiDevice) goto cleanup;
        if (FAILED(D2D1CreateFactory(D2D1_FACTORY_TYPE_SINGLE_THREADED, IID_PPV_ARGS(&factory))) || !factory) goto cleanup;
        if (FAILED(factory->CreateDevice(dxgiDevice, &d2dDevice)) || !d2dDevice) goto cleanup;
        if (FAILED(d2dDevice->CreateDeviceContext(D2D1_DEVICE_CONTEXT_OPTIONS_NONE, &deviceContext)) || !deviceContext) goto cleanup;
        if (FAILED(deviceContext->QueryInterface(IID_PPV_ARGS(&deviceContext5))) || !deviceContext5) goto cleanup;

        {
            D3D11_TEXTURE2D_DESC description{};
            description.Width = targetWidth;
            description.Height = targetHeight;
            description.MipLevels = 1;
            description.ArraySize = 1;
            description.Format = DXGI_FORMAT_B8G8R8A8_UNORM;
            description.SampleDesc.Count = 1;
            description.Usage = D3D11_USAGE_DEFAULT;
            description.BindFlags = D3D11_BIND_RENDER_TARGET;
            if (FAILED(device->CreateTexture2D(&description, nullptr, &targetTexture)) || !targetTexture) goto cleanup;
            description.Usage = D3D11_USAGE_STAGING;
            description.BindFlags = 0;
            description.CPUAccessFlags = D3D11_CPU_ACCESS_READ;
            if (FAILED(device->CreateTexture2D(&description, nullptr, &stagingTexture)) || !stagingTexture) goto cleanup;
        }

        {
            IDXGISurface* surface = nullptr;
            if (FAILED(targetTexture->QueryInterface(IID_PPV_ARGS(&surface))) || !surface) goto cleanup;
            const D2D1_BITMAP_PROPERTIES1 properties = D2D1::BitmapProperties1(
                D2D1_BITMAP_OPTIONS_TARGET | D2D1_BITMAP_OPTIONS_CANNOT_DRAW,
                D2D1::PixelFormat(DXGI_FORMAT_B8G8R8A8_UNORM, D2D1_ALPHA_MODE_PREMULTIPLIED));
            const HRESULT created = deviceContext5->CreateBitmapFromDxgiSurface(surface, &properties, &targetBitmap);
            surface->Release();
            if (FAILED(created) || !targetBitmap) goto cleanup;
        }

        {
            // The viewport is the document's own size (not the tile's): a document without width / height then lays out at its
            // viewBox size and one transform scales it to the tile.
            const D2D1_SIZE_F viewport = D2D1::SizeF(intrinsicWidth, intrinsicHeight);
            if (FAILED(deviceContext5->CreateSvgDocument(stream, viewport, &svg)) || !svg) goto cleanup;
            deviceContext5->SetTarget(targetBitmap);
            deviceContext5->SetDpi(96.0f, 96.0f);
            deviceContext5->BeginDraw();
            deviceContext5->Clear(D2D1::ColorF(0.0f, 0.0f, 0.0f, 0.0f));
            deviceContext5->SetTransform(D2D1::Matrix3x2F::Scale(static_cast<float>(targetWidth) / intrinsicWidth,
                                                                 static_cast<float>(targetHeight) / intrinsicHeight));
            deviceContext5->DrawSvgDocument(svg);
            const HRESULT ended = deviceContext5->EndDraw();
            deviceContext5->SetTarget(nullptr);
            if (FAILED(ended)) goto cleanup;
        }

        {
            context->CopyResource(stagingTexture, targetTexture);
            D3D11_MAPPED_SUBRESOURCE mapped{};
            if (FAILED(context->Map(stagingTexture, 0, D3D11_MAP_READ, 0, &mapped))) goto cleanup;
            const std::uint64_t bytes = static_cast<std::uint64_t>(targetWidth) * targetHeight * 4;
            pixels = static_cast<std::uint8_t*>(CoTaskMemAlloc(static_cast<SIZE_T>(bytes)));
            if (pixels)
            {
                for (std::uint32_t y = 0; y < targetHeight; ++y)
                    std::memcpy(pixels + static_cast<std::size_t>(y) * targetWidth * 4,
                                static_cast<const std::uint8_t*>(mapped.pData) + static_cast<std::size_t>(y) * mapped.RowPitch,
                                static_cast<std::size_t>(targetWidth) * 4);
            }
            context->Unmap(stagingTexture, 0);
            if (!pixels) goto cleanup;
            image->width = targetWidth;
            image->height = targetHeight;
            image->stride = targetWidth * 4;
            image->bufferBytes = bytes;
            image->data = pixels;
            pixels = nullptr;
            ok = true;
        }
    }
    catch (...) { ok = false; }

cleanup:
    if (pixels) CoTaskMemFree(pixels);
    SafeRelease(svg);
    SafeRelease(targetBitmap);
    SafeRelease(stagingTexture);
    SafeRelease(targetTexture);
    SafeRelease(deviceContext5);
    SafeRelease(deviceContext);
    SafeRelease(d2dDevice);
    SafeRelease(factory);
    SafeRelease(dxgiDevice);
    SafeRelease(context);
    SafeRelease(device);
    if (stream) stream->Release();
    if (uninitialize) CoUninitialize();
    if (!ok) *image = {};
    return ok ? 1 : 0;
}
