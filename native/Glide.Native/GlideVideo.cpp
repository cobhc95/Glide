// Glide.Native — overlay video playback.
//
// Wraps the Windows Media Foundation Media Engine in frame-server mode so Glide can present video
// frames inside its own overlay renderers (which own position, opacity, crop, zoom and motion).
// Decoding, audio output, A/V sync, seeking and looping are owned by Media Foundation; this file
// only exposes a tiny C ABI and copies the current frame, already scaled on the GPU, into a
// caller-owned BGRA buffer. When Windows lacks a codec, the managed side reopens the file with the
// bundled FFmpeg backend (GlideVideoFfmpeg.cpp) through the same exports.
//
// Files are opened with FILE_SHARE_DELETE so a playing overlay never blocks deleting or moving the
// user's video (the same invariant every Glide image read follows).

#include <windows.h>
#include <mfapi.h>
#include <mfidl.h>
#include <mfreadwrite.h>
#include <mfmediaengine.h>
#include <mferror.h>
#include <d3d11.h>
#include <d3d10.h>
#include <dxgi.h>
#include <wincodec.h>
#include <atomic>
#include <cstdint>
#include <cstring>
#include <mutex>
#include <new>

#include "GlideVideo.h"

namespace
{
    template <typename T> void SafeRelease(T*& p) { if (p) { p->Release(); p = nullptr; } }

    std::mutex g_startupGate;
    int g_startupCount = 0;

    bool AcquireMediaFoundation()
    {
        std::lock_guard lock(g_startupGate);
        if (g_startupCount == 0 && FAILED(MFStartup(MF_VERSION, MFSTARTUP_FULL))) return false;
        ++g_startupCount;
        return true;
    }

    void ReleaseMediaFoundation()
    {
        std::lock_guard lock(g_startupGate);
        if (g_startupCount > 0 && --g_startupCount == 0) MFShutdown();
    }

    // Minimal read-only IStream over a file handle opened with full sharing.
    class SharedFileStream final : public IStream
    {
    public:
        explicit SharedFileStream(HANDLE file) : file_(file) {}
        ~SharedFileStream() { if (file_ != INVALID_HANDLE_VALUE) CloseHandle(file_); }

        IFACEMETHODIMP QueryInterface(REFIID riid, void** ppv) override
        {
            if (!ppv) return E_POINTER;
            if (riid == IID_IUnknown || riid == IID_IStream || riid == IID_ISequentialStream) { *ppv = static_cast<IStream*>(this); AddRef(); return S_OK; }
            *ppv = nullptr; return E_NOINTERFACE;
        }
        IFACEMETHODIMP_(ULONG) AddRef() override { return ++refs_; }
        IFACEMETHODIMP_(ULONG) Release() override { const auto r = --refs_; if (r == 0) delete this; return r; }

        IFACEMETHODIMP Read(void* buffer, ULONG count, ULONG* read) override
        {
            DWORD done = 0;
            if (!ReadFile(file_, buffer, count, &done, nullptr)) return HRESULT_FROM_WIN32(GetLastError());
            if (read) *read = done;
            return done < count ? S_FALSE : S_OK;
        }
        IFACEMETHODIMP Write(const void*, ULONG, ULONG*) override { return STG_E_ACCESSDENIED; }
        IFACEMETHODIMP Seek(LARGE_INTEGER move, DWORD origin, ULARGE_INTEGER* position) override
        {
            LARGE_INTEGER result{};
            if (!SetFilePointerEx(file_, move, &result, origin)) return HRESULT_FROM_WIN32(GetLastError());
            if (position) position->QuadPart = static_cast<ULONGLONG>(result.QuadPart);
            return S_OK;
        }
        IFACEMETHODIMP SetSize(ULARGE_INTEGER) override { return E_NOTIMPL; }
        IFACEMETHODIMP CopyTo(IStream*, ULARGE_INTEGER, ULARGE_INTEGER*, ULARGE_INTEGER*) override { return E_NOTIMPL; }
        IFACEMETHODIMP Commit(DWORD) override { return S_OK; }
        IFACEMETHODIMP Revert() override { return E_NOTIMPL; }
        IFACEMETHODIMP LockRegion(ULARGE_INTEGER, ULARGE_INTEGER, DWORD) override { return STG_E_INVALIDFUNCTION; }
        IFACEMETHODIMP UnlockRegion(ULARGE_INTEGER, ULARGE_INTEGER, DWORD) override { return STG_E_INVALIDFUNCTION; }
        IFACEMETHODIMP Stat(STATSTG* stat, DWORD) override
        {
            if (!stat) return E_POINTER;
            std::memset(stat, 0, sizeof(*stat));
            stat->type = STGTY_STREAM;
            LARGE_INTEGER size{};
            if (GetFileSizeEx(file_, &size)) stat->cbSize.QuadPart = static_cast<ULONGLONG>(size.QuadPart);
            stat->grfMode = STGM_READ | STGM_SHARE_DENY_NONE;
            return S_OK;
        }
        IFACEMETHODIMP Clone(IStream**) override { return E_NOTIMPL; }

    private:
        HANDLE file_;
        std::atomic<ULONG> refs_{ 1 };
    };

    class EngineNotify final : public IMFMediaEngineNotify
    {
    public:
        std::atomic<int> state{ 0 };   // 0 loading, 1 ready, -1 error
        std::atomic<int> ended{ 0 };
        std::atomic<long> error{ 0 };

        IFACEMETHODIMP QueryInterface(REFIID riid, void** ppv) override
        {
            if (!ppv) return E_POINTER;
            if (riid == IID_IUnknown || riid == __uuidof(IMFMediaEngineNotify)) { *ppv = static_cast<IMFMediaEngineNotify*>(this); AddRef(); return S_OK; }
            *ppv = nullptr; return E_NOINTERFACE;
        }
        IFACEMETHODIMP_(ULONG) AddRef() override { return ++refs_; }
        IFACEMETHODIMP_(ULONG) Release() override { const auto r = --refs_; if (r == 0) delete this; return r; }

        // Only records state. Calling back into the engine from here can deadlock.
        IFACEMETHODIMP EventNotify(DWORD event, DWORD_PTR param1, DWORD param2) override
        {
            switch (event)
            {
            case MF_MEDIA_ENGINE_EVENT_LOADEDMETADATA: if (state.load() == 0) state = 1; break;
            case MF_MEDIA_ENGINE_EVENT_ERROR: error = static_cast<long>(param2 ? param2 : param1); state = -1; break;
            case MF_MEDIA_ENGINE_EVENT_ENDED: ended = 1; break;
            case MF_MEDIA_ENGINE_EVENT_PLAYING: case MF_MEDIA_ENGINE_EVENT_SEEKED: ended = 0; break;
            default: break;
            }
            return S_OK;
        }

    private:
        std::atomic<ULONG> refs_{ 1 };
    };

    struct VideoPlayer
    {
        std::mutex gate;
        EngineNotify* notify = nullptr;
        IMFMediaEngine* engine = nullptr;
        IMFMediaEngineEx* engineEx = nullptr;
        IMFDXGIDeviceManager* manager = nullptr;
        ID3D11Device* device = nullptr;
        ID3D11DeviceContext* context = nullptr;
        ID3D11Texture2D* target = nullptr;
        ID3D11Texture2D* staging = nullptr;
        IWICImagingFactory* wic = nullptr;
        IWICBitmap* wicTarget = nullptr;
        UINT targetWidth = 0, targetHeight = 0;
        bool mfStarted = false;

        ~VideoPlayer()
        {
            if (engine) engine->Shutdown();
            SafeRelease(engineEx); SafeRelease(engine);
            SafeRelease(wicTarget); SafeRelease(wic);
            SafeRelease(staging); SafeRelease(target);
            SafeRelease(context); SafeRelease(manager); SafeRelease(device);
            SafeRelease(notify);
            if (mfStarted) ReleaseMediaFoundation();
        }
    };

    bool CreateD3D(VideoPlayer& player)
    {
        const D3D_FEATURE_LEVEL levels[] = { D3D_FEATURE_LEVEL_11_1, D3D_FEATURE_LEVEL_11_0, D3D_FEATURE_LEVEL_10_1, D3D_FEATURE_LEVEL_10_0 };
        const UINT flags = D3D11_CREATE_DEVICE_VIDEO_SUPPORT | D3D11_CREATE_DEVICE_BGRA_SUPPORT;
        if (FAILED(D3D11CreateDevice(nullptr, D3D_DRIVER_TYPE_HARDWARE, nullptr, flags, levels, ARRAYSIZE(levels),
                                     D3D11_SDK_VERSION, &player.device, nullptr, &player.context)))
            return false;
        ID3D10Multithread* multithread = nullptr;
        if (SUCCEEDED(player.device->QueryInterface(IID_PPV_ARGS(&multithread))))
        {
            multithread->SetMultithreadProtected(TRUE);
            multithread->Release();
        }
        UINT token = 0;
        if (FAILED(MFCreateDXGIDeviceManager(&token, &player.manager)) ||
            FAILED(player.manager->ResetDevice(player.device, token)))
        {
            SafeRelease(player.manager); SafeRelease(player.context); SafeRelease(player.device);
            return false;
        }
        return true;
    }

    bool EnsureTargets(VideoPlayer& player, UINT width, UINT height)
    {
        if (player.targetWidth == width && player.targetHeight == height && (player.target || player.wicTarget)) return true;
        SafeRelease(player.target); SafeRelease(player.staging); SafeRelease(player.wicTarget);
        player.targetWidth = player.targetHeight = 0;
        if (player.device)
        {
            D3D11_TEXTURE2D_DESC desc{};
            desc.Width = width; desc.Height = height; desc.MipLevels = 1; desc.ArraySize = 1;
            desc.Format = DXGI_FORMAT_B8G8R8A8_UNORM; desc.SampleDesc.Count = 1;
            desc.Usage = D3D11_USAGE_DEFAULT; desc.BindFlags = D3D11_BIND_RENDER_TARGET | D3D11_BIND_SHADER_RESOURCE;
            if (FAILED(player.device->CreateTexture2D(&desc, nullptr, &player.target))) return false;
            desc.Usage = D3D11_USAGE_STAGING; desc.BindFlags = 0; desc.CPUAccessFlags = D3D11_CPU_ACCESS_READ;
            if (FAILED(player.device->CreateTexture2D(&desc, nullptr, &player.staging))) { SafeRelease(player.target); return false; }
        }
        else
        {
            if (!player.wic && FAILED(CoCreateInstance(CLSID_WICImagingFactory, nullptr, CLSCTX_INPROC_SERVER, IID_PPV_ARGS(&player.wic))))
                return false;
            if (FAILED(player.wic->CreateBitmap(width, height, GUID_WICPixelFormat32bppBGRA, WICBitmapCacheOnDemand, &player.wicTarget)))
                return false;
        }
        player.targetWidth = width; player.targetHeight = height;
        return true;
    }

    struct ComScope
    {
        HRESULT hr;
        ComScope() : hr(CoInitializeEx(nullptr, COINIT_MULTITHREADED)) {}
        ~ComScope() { if (SUCCEEDED(hr)) CoUninitialize(); }
    };
}

namespace
{
    // Media Foundation Media Engine backend (frame-server mode).
    class MediaEngineBackend final : public GlideVideoBackend
    {
    public:
        explicit MediaEngineBackend(VideoPlayer* player) : p_(player) {}
        ~MediaEngineBackend() override { ComScope com; delete p_; }

        void GetInfo(GlideVideoInfo& info) override
        {
            std::lock_guard lock(p_->gate);
            info.backend = 1;
            info.state = p_->notify->state.load();
            info.error = p_->notify->error.load();
            if (info.state == 1)
            {
                DWORD w = 0, h = 0;
                if (SUCCEEDED(p_->engine->GetNativeVideoSize(&w, &h)) && w > 0 && h > 0)
                {
                    // GetVideoAspectRatio reports the picture (display) aspect ratio, so the display width
                    // follows from the coded height; this corrects anamorphic (non-square pixel) video.
                    DWORD ax = 0, ay = 0;
                    double width = w;
                    if (SUCCEEDED(p_->engine->GetVideoAspectRatio(&ax, &ay)) && ax > 0 && ay > 0) width = h * static_cast<double>(ax) / ay;
                    info.width = static_cast<std::int32_t>(width + 0.5);
                    info.height = static_cast<std::int32_t>(h);
                }
                else if (p_->engine->HasVideo() == FALSE)
                {
                    info.state = -1; // audio-only files are not overlay content
                    info.error = static_cast<std::int32_t>(MF_E_INVALIDMEDIATYPE);
                }
            }
            const double duration = p_->engine->GetDuration();
            info.duration = (duration > 0 && duration < 1e9) ? duration : 0;
            info.position = p_->engine->GetCurrentTime();
            info.playing = p_->engine->IsPaused() ? 0 : 1;
            info.ended = p_->notify->ended.load() || p_->engine->IsEnded() ? 1 : 0;
            info.muted = p_->engine->GetMuted() ? 1 : 0;
            info.loop = p_->engine->GetLoop() ? 1 : 0;
            info.volume = p_->engine->GetVolume();
        }

        int SetPlaying(bool playing) override
        {
            std::lock_guard lock(p_->gate);
            if (playing && p_->engine->IsEnded()) p_->engine->SetCurrentTime(0);
            return playing ? p_->engine->Play() : p_->engine->Pause();
        }

        int Seek(double seconds) override
        {
            std::lock_guard lock(p_->gate);
            return p_->engine->SetCurrentTime(seconds < 0 ? 0 : seconds);
        }

        int SetMuted(bool muted) override { std::lock_guard lock(p_->gate); return p_->engine->SetMuted(muted ? TRUE : FALSE); }
        int SetVolume(double volume) override { std::lock_guard lock(p_->gate); return p_->engine->SetVolume(volume < 0 ? 0 : volume > 1 ? 1 : volume); }
        int SetLoop(bool loop) override { std::lock_guard lock(p_->gate); return p_->engine->SetLoop(loop ? TRUE : FALSE); }

        int ReadFrame(std::uint8_t* destination, std::int32_t width, std::int32_t height, std::int32_t stride, bool force) override
        {
            auto* player = p_;
            std::lock_guard lock(player->gate);
            if (player->notify->state.load() != 1) return 0;

            LONGLONG pts = 0;
            if (player->engine->OnVideoStreamTick(&pts) != S_OK && !force) return 0;
            if (!EnsureTargets(*player, static_cast<UINT>(width), static_cast<UINT>(height))) return E_OUTOFMEMORY;

            const MFVideoNormalizedRect sourceRect{ 0.f, 0.f, 1.f, 1.f };
            const RECT targetRect{ 0, 0, width, height };
            const MFARGB border{ 0, 0, 0, 255 };
            if (player->device)
            {
                HRESULT hr = player->engine->TransferVideoFrame(player->target, &sourceRect, &targetRect, &border);
                if (FAILED(hr)) return hr;
                player->context->CopyResource(player->staging, player->target);
                D3D11_MAPPED_SUBRESOURCE mapped{};
                if (FAILED(hr = player->context->Map(player->staging, 0, D3D11_MAP_READ, 0, &mapped))) return hr;
                CopyOpaqueRows(static_cast<const std::uint8_t*>(mapped.pData), mapped.RowPitch, destination, stride, width, height);
                player->context->Unmap(player->staging, 0);
                return 1;
            }

            HRESULT hr = player->engine->TransferVideoFrame(player->wicTarget, &sourceRect, &targetRect, &border);
            if (FAILED(hr)) return hr;
            IWICBitmapLock* bitmapLock = nullptr;
            const WICRect all{ 0, 0, width, height };
            if (FAILED(hr = player->wicTarget->Lock(&all, WICBitmapLockRead, &bitmapLock))) return hr;
            UINT sourceStride = 0, size = 0;
            BYTE* source = nullptr;
            bitmapLock->GetStride(&sourceStride);
            if (SUCCEEDED(hr = bitmapLock->GetDataPointer(&size, &source)) && source)
                CopyOpaqueRows(source, sourceStride, destination, stride, width, height);
            bitmapLock->Release();
            return FAILED(hr) ? hr : 1;
        }

    private:
        static void CopyOpaqueRows(const std::uint8_t* source, std::size_t sourceStride, std::uint8_t* destination,
                                   std::int32_t stride, std::int32_t width, std::int32_t height)
        {
            for (std::int32_t y = 0; y < height; ++y)
            {
                auto* row = destination + static_cast<std::size_t>(y) * stride;
                std::memcpy(row, source + static_cast<std::size_t>(y) * sourceStride, static_cast<std::size_t>(width) * 4);
                for (std::int32_t x = 0; x < width; ++x) row[x * 4 + 3] = 255;
            }
        }

        VideoPlayer* p_;
    };

    int OpenMediaEngine(const wchar_t* path, GlideVideoBackend** backend)
    {
        ComScope com;
        auto* player = new (std::nothrow) VideoPlayer();
        if (!player) return E_OUTOFMEMORY;
        if (!AcquireMediaFoundation()) { delete player; return E_FAIL; }
        player->mfStarted = true;

        HRESULT hr = S_OK;
        IMFMediaEngineClassFactory* factory = nullptr;
        IMFAttributes* attributes = nullptr;
        IMFByteStream* byteStream = nullptr;
        BSTR url = nullptr;
        HANDLE file = INVALID_HANDLE_VALUE;
        do
        {
            player->notify = new (std::nothrow) EngineNotify();
            if (!player->notify) { hr = E_OUTOFMEMORY; break; }
            CreateD3D(*player); // optional: software frame transfer is used when unavailable

            if (FAILED(hr = MFCreateAttributes(&attributes, 3))) break;
            if (FAILED(hr = attributes->SetUnknown(MF_MEDIA_ENGINE_CALLBACK, player->notify))) break;
            if (FAILED(hr = attributes->SetUINT32(MF_MEDIA_ENGINE_VIDEO_OUTPUT_FORMAT, DXGI_FORMAT_B8G8R8A8_UNORM))) break;
            if (player->manager && FAILED(hr = attributes->SetUnknown(MF_MEDIA_ENGINE_DXGI_MANAGER, player->manager))) break;

            if (FAILED(hr = CoCreateInstance(CLSID_MFMediaEngineClassFactory, nullptr, CLSCTX_INPROC_SERVER, IID_PPV_ARGS(&factory)))) break;
            if (FAILED(hr = factory->CreateInstance(0, attributes, &player->engine))) break;
            if (FAILED(hr = player->engine->QueryInterface(IID_PPV_ARGS(&player->engineEx)))) break;

            file = CreateFileW(path, GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE, nullptr,
                               OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL | FILE_FLAG_SEQUENTIAL_SCAN, nullptr);
            if (file == INVALID_HANDLE_VALUE) { hr = HRESULT_FROM_WIN32(GetLastError()); break; }
            auto* stream = new (std::nothrow) SharedFileStream(file);
            if (!stream) { hr = E_OUTOFMEMORY; break; }
            file = INVALID_HANDLE_VALUE; // owned by stream
            hr = MFCreateMFByteStreamOnStream(stream, &byteStream);
            stream->Release();
            if (FAILED(hr)) break;

            // The URL only supplies the extension/scheme hint used to pick a media source.
            url = SysAllocString(path);
            if (!url) { hr = E_OUTOFMEMORY; break; }
            if (FAILED(hr = player->engineEx->SetSourceFromByteStream(byteStream, url))) break;
            player->engine->SetLoop(TRUE);
            player->engine->SetAutoPlay(TRUE);
        } while (false);

        if (url) SysFreeString(url);
        if (file != INVALID_HANDLE_VALUE) CloseHandle(file);
        SafeRelease(byteStream); SafeRelease(factory); SafeRelease(attributes);
        if (FAILED(hr)) { delete player; return hr; }
        auto* wrapper = new (std::nothrow) MediaEngineBackend(player);
        if (!wrapper) { delete player; return E_OUTOFMEMORY; }
        *backend = wrapper;
        return S_OK;
    }

    GlideVideoBackend* AsBackend(void* handle) { return static_cast<GlideVideoBackend*>(handle); }
}

// backendKind: 0 = Media Foundation (hardware decode, Windows codecs), 1 = bundled FFmpeg fallback.
extern "C" __declspec(dllexport) int __cdecl GlideVideoOpenExW(const wchar_t* path, int backendKind, void** handle)
{
    if (!path || !handle) return E_POINTER;
    *handle = nullptr;
    GlideVideoBackend* backend = nullptr;
    const int hr = backendKind == 1 ? CreateFfmpegVideoBackend(path, &backend) : OpenMediaEngine(path, &backend);
    if (hr < 0) return hr;
    *handle = backend;
    return S_OK;
}

extern "C" __declspec(dllexport) int __cdecl GlideVideoOpenW(const wchar_t* path, void** handle)
{
    return GlideVideoOpenExW(path, 0, handle);
}

// Reports 1 when the file's first video stream is H.264 larger than HardwareDecodeMaxSide, which
// Media Foundation cannot hardware-decode on this GPU. Reads only the container header, so it is
// cheap. A file that cannot be probed reports 0 and keeps the normal open path.
extern "C" __declspec(dllexport) int __cdecl GlideVideoExceedsHardwareDecodeLimitW(const wchar_t* path, int* exceeds)
{
    if (!path || !exceeds) return E_POINTER;
    *exceeds = 0;
    if (!AcquireMediaFoundation()) return E_FAIL;
    ComScope com;
    IMFSourceReader* reader = nullptr;
    IMFMediaType* type = nullptr;
    HRESULT hr = MFCreateSourceReaderFromURL(path, nullptr, &reader);
    if (SUCCEEDED(hr)) hr = reader->GetNativeMediaType(MF_SOURCE_READER_FIRST_VIDEO_STREAM, 0, &type);
    if (SUCCEEDED(hr))
    {
        GUID subtype{};
        UINT32 width = 0, height = 0;
        if (SUCCEEDED(type->GetGUID(MF_MT_SUBTYPE, &subtype)) && SUCCEEDED(MFGetAttributeSize(type, MF_MT_FRAME_SIZE, &width, &height)))
            *exceeds = IsEqualGUID(subtype, MFVideoFormat_H264) && (width > HardwareDecodeMaxSide || height > HardwareDecodeMaxSide) ? 1 : 0;
    }
    SafeRelease(type);
    SafeRelease(reader);
    ReleaseMediaFoundation();
    return hr;
}

extern "C" __declspec(dllexport) void __cdecl GlideVideoClose(void* handle)
{
    delete AsBackend(handle);
}

extern "C" __declspec(dllexport) int __cdecl GlideVideoGetInfo(void* handle, GlideVideoInfo* info)
{
    if (!handle || !info) return E_POINTER;
    std::memset(info, 0, sizeof(*info));
    AsBackend(handle)->GetInfo(*info);
    return S_OK;
}

extern "C" __declspec(dllexport) int __cdecl GlideVideoSetPlaying(void* handle, int playing)
{ return handle ? AsBackend(handle)->SetPlaying(playing != 0) : E_POINTER; }

extern "C" __declspec(dllexport) int __cdecl GlideVideoSeek(void* handle, double seconds)
{ return handle ? AsBackend(handle)->Seek(seconds) : E_POINTER; }

extern "C" __declspec(dllexport) int __cdecl GlideVideoSetMuted(void* handle, int muted)
{ return handle ? AsBackend(handle)->SetMuted(muted != 0) : E_POINTER; }

extern "C" __declspec(dllexport) int __cdecl GlideVideoSetVolume(void* handle, double volume)
{ return handle ? AsBackend(handle)->SetVolume(volume) : E_POINTER; }

extern "C" __declspec(dllexport) int __cdecl GlideVideoSetLoop(void* handle, int loop)
{ return handle ? AsBackend(handle)->SetLoop(loop != 0) : E_POINTER; }

// Copies the current frame, scaled to width x height, into a top-down opaque BGRA buffer.
// Returns 1 when a new frame was written, 0 when no new frame is available, or a failed HRESULT.
extern "C" __declspec(dllexport) int __cdecl GlideVideoReadFrame(void* handle, std::uint8_t* destination,
                                                                 std::int32_t width, std::int32_t height, std::int32_t stride,
                                                                 std::int32_t force)
{
    if (!handle || !destination) return E_POINTER;
    if (width <= 0 || height <= 0 || width > 8192 || height > 8192 || stride < width * 4) return E_INVALIDARG;
    return AsBackend(handle)->ReadFrame(destination, width, height, stride, force != 0);
}
