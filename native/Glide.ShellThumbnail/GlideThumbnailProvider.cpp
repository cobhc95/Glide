// Glide.ShellThumbnail — COM in-proc server for Windows Explorer thumbnails.
//
// Explorer loads this DLL inside its isolated thumbnail host (dllhost.exe). It implements
// IThumbnailProvider plus IInitializeWithStream and IInitializeWithItem, and delegates all decoding
// to ThumbnailCore. It never loads .NET, Avalonia, Glide.exe or any Glide UI, never shows UI, and
// never lets an exception escape a COM boundary.

#include "ThumbnailCore.h"
#include "CodecProviders.h"

#include <windows.h>
#include <thumbcache.h>
#include <shobjidl.h>
#include <shlobj.h>
#include <shlwapi.h>
#include <propvarutil.h>

#include <atomic>
#include <cstdio>
#include <cstdint>
#include <cstring>
#include <new>
#include <string>
#include <vector>

#pragma comment(lib, "shlwapi.lib")
#pragma comment(lib, "shell32.lib")
#pragma comment(lib, "ole32.lib")
#pragma comment(lib, "propsys.lib")

namespace
{
    // {6E3C1B2A-9F41-4E7C-9B1E-2C7A5D8F0A31}
    const CLSID CLSID_GlideThumbnailProvider =
        { 0x6E3C1B2A, 0x9F41, 0x4E7C, { 0x9B, 0x1E, 0x2C, 0x7A, 0x5D, 0x8F, 0x0A, 0x31 } };

    constexpr wchar_t kProviderFriendlyName[] = L"Glide Explorer Thumbnail Provider";
    constexpr wchar_t kProviderThreadingModel[] = L"Apartment";

    HMODULE g_module = nullptr;
    std::atomic<long> g_objectCount{ 0 };

    bool TraceEnabled()
    {
        static const bool enabled = []
        {
            wchar_t value[8]{};
            const DWORD length = GetEnvironmentVariableW(L"GLIDE_THUMBNAIL_TRACE", value, 8);
            return length == 1 && value[0] == L'1';
        }();
        return enabled;
    }

    void Trace(const char* decoder, const wchar_t* extension, std::uint32_t requested,
               bool embedded, std::uint64_t decodeMicros, std::uint64_t totalMicros)
    {
        if (!TraceEnabled()) return;
        // Diagnostics are opt-in only; a normal Explorer session never touches the disk here.
        wchar_t path[MAX_PATH]{};
        if (FAILED(SHGetFolderPathW(nullptr, CSIDL_LOCAL_APPDATA, nullptr, 0, path))) return;
        wcscat_s(path, L"\\Glide");
        CreateDirectoryW(path, nullptr);
        wcscat_s(path, L"\\thumbnail-trace.tsv");
        FILE* file = nullptr;
        if (_wfopen_s(&file, path, L"a") != 0 || !file) return;
        fwprintf(file, L"%hs\t%s\t%u\t%s\t%llu\t%llu\n",
                 decoder ? decoder : "?",
                 extension ? extension : L"",
                 requested,
                 embedded ? L"embedded" : L"decode",
                 static_cast<unsigned long long>(decodeMicros),
                 static_cast<unsigned long long>(totalMicros));
        fclose(file);
    }

    std::wstring ExtensionFromPath(const wchar_t* path)
    {
        if (!path) return L"";
        const wchar_t* dot = wcsrchr(path, L'.');
        return dot ? dot : L"";
    }

    // ------------------------------------------------------------------ configuration ---------
    //
    // Glide.exe (Settings -> Explorer thumbnails -> Apply) writes the presets as DWORDs under
    // HKCU\Software\Glide\ExplorerThumbnails; a machine-wide default may live under the same path in
    // HKLM. They are read on every GetThumbnail call (a few registry reads, microseconds) so a change
    // applies to the next thumbnail Explorer requests without restarting its thumbnail host.

    constexpr wchar_t kConfigKey[] = L"Software\\Glide\\ExplorerThumbnails";

    struct ThumbnailConfig
    {
        int quality = 1;              // 0 Fast, 1 Balanced, 2 High
        std::uint32_t maxSource = 0;  // 0 Automatic, else 512 / 1024 / 2048
        bool preferEmbedded = true;
    };

    // In-process override used only by the headless test harness (GlideThumbnailSetTestOverrides).
    std::atomic<bool> g_overrideActive{ false };
    std::atomic<int> g_overrideQuality{ 1 };
    std::atomic<std::uint32_t> g_overrideMaxSource{ 0 };
    std::atomic<bool> g_overridePreferEmbedded{ true };

    bool ReadDword(HKEY root, const wchar_t* name, DWORD& value)
    {
        DWORD size = sizeof(value);
        return RegGetValueW(root, kConfigKey, name, RRF_RT_REG_DWORD, nullptr, &value, &size) == ERROR_SUCCESS;
    }

    bool ValidMaxSource(DWORD value) { return value == 0 || value == 512 || value == 1024 || value == 2048; }

    ThumbnailConfig LoadConfig()
    {
        ThumbnailConfig config;
        if (g_overrideActive.load())
        {
            config.quality = g_overrideQuality.load();
            config.maxSource = g_overrideMaxSource.load();
            config.preferEmbedded = g_overridePreferEmbedded.load();
            return config;
        }
        // HKLM first (machine default), then HKCU (the user's own choice) wins.
        for (HKEY root : { HKEY_LOCAL_MACHINE, HKEY_CURRENT_USER })
        {
            DWORD value = 0;
            if (ReadDword(root, L"Quality", value) && value <= 2) config.quality = static_cast<int>(value);
            if (ReadDword(root, L"MaxSourceSize", value) && ValidMaxSource(value)) config.maxSource = value;
            if (ReadDword(root, L"PreferEmbedded", value)) config.preferEmbedded = value != 0;
        }
        return config;
    }

    // -------------------------------------------------------------------- diagnostics ---------

    struct GlideThumbnailDiagnostics
    {
        std::uint32_t size;
        std::int32_t quality;
        std::int32_t embedded;
        std::uint32_t sourceWidth;
        std::uint32_t sourceHeight;
        std::uint32_t decodedWidth;
        std::uint32_t decodedHeight;
        std::uint32_t outputWidth;
        std::uint32_t outputHeight;
        std::uint64_t decodeMicros;
        std::uint64_t totalMicros;
        std::uint64_t bytesRead;
        char decoder[16];
    };

    thread_local GlideThumbnailDiagnostics t_lastDiagnostics{};
    thread_local bool t_hasDiagnostics = false;

    std::uint64_t MicrosSince(const LARGE_INTEGER& started)
    {
        LARGE_INTEGER now{}, frequency{};
        QueryPerformanceCounter(&now);
        QueryPerformanceFrequency(&frequency);
        if (frequency.QuadPart == 0) return 0;
        return static_cast<std::uint64_t>(((now.QuadPart - started.QuadPart) * 1'000'000ll) / frequency.QuadPart);
    }

    bool CreateThumbnailBitmap(const glide::thumb::Image& image, HBITMAP* bitmap)
    {
        if (!bitmap || !image.Valid()) return false;
        *bitmap = nullptr;
        BITMAPINFO info{};
        info.bmiHeader.biSize = sizeof(BITMAPINFOHEADER);
        info.bmiHeader.biWidth = static_cast<LONG>(image.width);
        info.bmiHeader.biHeight = -static_cast<LONG>(image.height); // top-down
        info.bmiHeader.biPlanes = 1;
        info.bmiHeader.biBitCount = 32;
        info.bmiHeader.biCompression = BI_RGB;
        void* bits = nullptr;
        HBITMAP created = CreateDIBSection(nullptr, &info, DIB_RGB_COLORS, &bits, nullptr, 0);
        if (!created || !bits)
        {
            if (created) DeleteObject(created);
            return false;
        }
        std::memcpy(bits, image.pixels.data(), static_cast<std::size_t>(image.width) * image.height * 4);
        *bitmap = created;
        return true;
    }

    class GlideThumbnailProvider final : public IThumbnailProvider, public IInitializeWithStream, public IInitializeWithItem
    {
    public:
        GlideThumbnailProvider() { g_objectCount.fetch_add(1); }
        ~GlideThumbnailProvider()
        {
            if (stream_) stream_->Release();
            g_objectCount.fetch_sub(1);
        }

        // IUnknown
        IFACEMETHODIMP QueryInterface(REFIID riid, void** ppv) override
        {
            if (!ppv) return E_POINTER;
            *ppv = nullptr;
            if (riid == IID_IUnknown || riid == IID_IThumbnailProvider)
                *ppv = static_cast<IThumbnailProvider*>(this);
            else if (riid == IID_IInitializeWithStream)
                *ppv = static_cast<IInitializeWithStream*>(this);
            else if (riid == IID_IInitializeWithItem)
                *ppv = static_cast<IInitializeWithItem*>(this);
            else
                return E_NOINTERFACE;
            AddRef();
            return S_OK;
        }

        IFACEMETHODIMP_(ULONG) AddRef() override { return static_cast<ULONG>(InterlockedIncrement(&referenceCount_)); }
        IFACEMETHODIMP_(ULONG) Release() override
        {
            const long count = InterlockedDecrement(&referenceCount_);
            if (count == 0) delete this;
            return static_cast<ULONG>(count);
        }

        // IInitializeWithStream
        IFACEMETHODIMP Initialize(IStream* stream, DWORD) override
        {
            if (!stream) return E_INVALIDARG;
            STATSTG stat{};
            if (SUCCEEDED(stream->Stat(&stat, STATFLAG_DEFAULT)) && stat.pwcsName)
            {
                extension_ = ExtensionFromPath(stat.pwcsName);
                CoTaskMemFree(stat.pwcsName);
            }
            // Keep the stream and read it lazily in GetThumbnail: WIC formats pull only the blocks
            // they need (an embedded preview, the first GIF frame) instead of the whole file.
            if (stream_) stream_->Release();
            stream_ = stream;
            stream_->AddRef();
            return S_OK;
        }

        // IInitializeWithItem
        IFACEMETHODIMP Initialize(IShellItem* item, DWORD) override
        {
            if (!item) return E_INVALIDARG;
            PWSTR path = nullptr;
            if (SUCCEEDED(item->GetDisplayName(SIGDN_FILESYSPATH, &path)) && path)
            {
                extension_ = ExtensionFromPath(path);
                CoTaskMemFree(path);
            }
            // The stream is supplied separately; nothing else to do here.
            return S_OK;
        }

        // IThumbnailProvider
        IFACEMETHODIMP GetThumbnail(UINT cx, HBITMAP* phbmp, WTS_ALPHATYPE* pdwAlpha) override
        {
            if (!phbmp || !pdwAlpha) return E_POINTER;
            *phbmp = nullptr;
            *pdwAlpha = WTSAT_UNKNOWN;
            if (!stream_) return E_FAIL;
            // Never trust the host's requested size: Explorer asks for <= 2560, other hosts could ask for
            // anything. The provider renders at most MaxThumbnailEdge (2048) per side.
            cx = glide::thumb::ClampRequestedEdge(cx);

            try
            {
                LARGE_INTEGER started{};
                QueryPerformanceCounter(&started);
                const ThumbnailConfig config = LoadConfig();
                glide::thumb::DecodeOptions options;
                options.maxWidth = cx;
                options.maxHeight = cx;
                options.maxSourceEdge = config.maxSource;
                options.preferEmbedded = config.preferEmbedded;
                options.quality = config.quality;

                const auto result = glide::thumb::DecodeStreamForThumbnail(stream_, extension_.c_str(), options);
                if (!result.ok || !result.image.Valid()) return E_FAIL;

                const auto composed = glide::thumb::ComposeThumbnail(result.image, cx, config.quality);
                if (!composed.Valid()) return E_FAIL;

                if (!CreateThumbnailBitmap(composed, phbmp)) return E_FAIL;
                *pdwAlpha = WTSAT_ARGB;
                const std::uint64_t total = MicrosSince(started);

                GlideThumbnailDiagnostics diagnostics{};
                diagnostics.size = sizeof(diagnostics);
                diagnostics.quality = config.quality;
                diagnostics.embedded = result.embedded ? 1 : 0;
                diagnostics.sourceWidth = result.sourceWidth;
                diagnostics.sourceHeight = result.sourceHeight;
                diagnostics.decodedWidth = result.image.width;
                diagnostics.decodedHeight = result.image.height;
                diagnostics.outputWidth = composed.width;
                diagnostics.outputHeight = composed.height;
                diagnostics.decodeMicros = result.decodeMicros;
                diagnostics.totalMicros = total;
                diagnostics.bytesRead = result.bytesRead;
                strncpy_s(diagnostics.decoder, result.decoder ? result.decoder : "?", _TRUNCATE);
                t_lastDiagnostics = diagnostics;
                t_hasDiagnostics = true;

                Trace(result.decoder, extension_.c_str(), cx, result.embedded, result.decodeMicros, total);
                return S_OK;
            }
            catch (...)
            {
                // A thumbnail provider must never let an exception cross the COM boundary.
                if (*phbmp) { DeleteObject(*phbmp); *phbmp = nullptr; }
                *pdwAlpha = WTSAT_UNKNOWN;
                return E_FAIL;
            }
        }

    private:
        long referenceCount_ = 1;
        IStream* stream_ = nullptr;
        std::wstring extension_;
    };

    class ClassFactory final : public IClassFactory
    {
    public:
        IFACEMETHODIMP QueryInterface(REFIID riid, void** ppv) override
        {
            if (!ppv) return E_POINTER;
            *ppv = nullptr;
            if (riid == IID_IUnknown || riid == IID_IClassFactory)
            {
                *ppv = static_cast<IClassFactory*>(this);
                AddRef();
                return S_OK;
            }
            return E_NOINTERFACE;
        }
        IFACEMETHODIMP_(ULONG) AddRef() override { return 2; }
        IFACEMETHODIMP_(ULONG) Release() override { return 1; }

        IFACEMETHODIMP CreateInstance(IUnknown* outer, REFIID riid, void** ppv) override
        {
            if (outer) return CLASS_E_NOAGGREGATION;
            auto* provider = new (std::nothrow) GlideThumbnailProvider();
            if (!provider) return E_OUTOFMEMORY;
            const HRESULT hr = provider->QueryInterface(riid, ppv);
            provider->Release();
            return hr;
        }
        IFACEMETHODIMP LockServer(BOOL) override { return S_OK; }
    };
}

BOOL APIENTRY DllMain(HMODULE module, DWORD reason, LPVOID)
{
    if (reason == DLL_PROCESS_ATTACH)
    {
        g_module = module;
        DisableThreadLibraryCalls(module);
    }
    return TRUE;
}

STDAPI DllGetClassObject(REFCLSID clsid, REFIID riid, void** ppv)
{
    if (!ppv) return E_POINTER;
    *ppv = nullptr;
    if (clsid != CLSID_GlideThumbnailProvider) return CLASS_E_CLASSNOTAVAILABLE;
    static ClassFactory factory;
    return factory.QueryInterface(riid, ppv);
}

STDAPI DllCanUnloadNow()
{
    return g_objectCount.load() == 0 ? S_OK : S_FALSE;
}

namespace
{
    HRESULT SetKeyValue(HKEY root, const wchar_t* subkey, const wchar_t* name, const wchar_t* value)
    {
        HKEY key = nullptr;
        const LSTATUS status = RegCreateKeyExW(root, subkey, 0, nullptr, 0, KEY_WRITE, nullptr, &key, nullptr);
        if (status != ERROR_SUCCESS) return HRESULT_FROM_WIN32(status);
        const LSTATUS set = RegSetValueExW(key, name, 0, REG_SZ,
                                           reinterpret_cast<const BYTE*>(value),
                                           static_cast<DWORD>((wcslen(value) + 1) * sizeof(wchar_t)));
        RegCloseKey(key);
        return HRESULT_FROM_WIN32(set);
    }
}

STDAPI DllRegisterServer()
{
    wchar_t path[MAX_PATH]{};
    if (!GetModuleFileNameW(g_module, path, MAX_PATH)) return HRESULT_FROM_WIN32(GetLastError());

    wchar_t clsid[64]{};
    StringFromGUID2(CLSID_GlideThumbnailProvider, clsid, 64);

    std::wstring clsidKey = std::wstring(L"CLSID\\") + clsid;
    HRESULT hr = SetKeyValue(HKEY_CLASSES_ROOT, clsidKey.c_str(), nullptr, kProviderFriendlyName);
    if (FAILED(hr)) return hr;
    hr = SetKeyValue(HKEY_CLASSES_ROOT, (clsidKey + L"\\InprocServer32").c_str(), nullptr, path);
    if (FAILED(hr)) return hr;
    hr = SetKeyValue(HKEY_CLASSES_ROOT, (clsidKey + L"\\InprocServer32").c_str(), L"ThreadingModel", kProviderThreadingModel);
    if (FAILED(hr)) return hr;
    // Keep the handler in Explorer's isolated thumbnail host so a fault cannot destabilise Explorer.
    return SetKeyValue(HKEY_CLASSES_ROOT, clsidKey.c_str(), L"DisableProcessIsolation", L"0");
}

STDAPI DllUnregisterServer()
{
    wchar_t clsid[64]{};
    StringFromGUID2(CLSID_GlideThumbnailProvider, clsid, 64);
    std::wstring clsidKey = std::wstring(L"CLSID\\") + clsid;
    SHDeleteKeyW(HKEY_CLASSES_ROOT, clsidKey.c_str());
    return S_OK;
}

// ------------------------------------------------------------------ test/diagnostic exports ------
//
// These exist so the provider can be exercised headlessly and in-process (tests/Glide.Core.Tests,
// tools/thumbnail-preset-benchmark.py) without registering it, touching Explorer or the thumbnail
// cache. They only affect the calling process.

/// Forces the presets for this process. quality < 0 removes the override (registry is used again).
extern "C" void __stdcall GlideThumbnailSetTestOverrides(int quality, unsigned int maxSource, int preferEmbedded)
{
    if (quality < 0)
    {
        g_overrideActive.store(false);
        return;
    }
    g_overrideQuality.store(quality > 2 ? 1 : quality);
    g_overrideMaxSource.store(ValidMaxSource(maxSource) ? maxSource : 0);
    g_overridePreferEmbedded.store(preferEmbedded != 0);
    g_overrideActive.store(true);
}

/// Copies the diagnostics of the last successful GetThumbnail on the calling thread.
extern "C" int __stdcall GlideThumbnailGetLastDiagnostics(GlideThumbnailDiagnostics* diagnostics)
{
    if (!diagnostics || diagnostics->size < sizeof(GlideThumbnailDiagnostics) || !t_hasDiagnostics) return 0;
    *diagnostics = t_lastDiagnostics;
    return 1;
}

/// Largest thumbnail edge the provider renders (any larger host request is clamped to it).
extern "C" unsigned int __stdcall GlideThumbnailMaxEdge()
{
    return glide::thumb::MaxThumbnailEdge;
}

/// Resolved preset knobs, so tests can prove Fast/Balanced/High differ without decoding anything.
/// Layout: supersample, embeddedMinPercent, interpolation, gifFrameSearch, webpBypassFiltering,
/// webpNoFancyUpsampling (all int32).
extern "C" int __stdcall GlideThumbnailDescribeQuality(int quality, std::int32_t* values, int count)
{
    if (!values || count < 6) return 0;
    const auto profile = glide::thumb::ProfileFor(quality);
    values[0] = static_cast<std::int32_t>(profile.supersample);
    values[1] = static_cast<std::int32_t>(profile.embeddedMinPercent);
    values[2] = profile.interpolation;
    values[3] = static_cast<std::int32_t>(profile.gifFrameSearch);
    values[4] = profile.webpBypassFiltering ? 1 : 0;
    values[5] = profile.webpNoFancyUpsampling ? 1 : 0;
    return 6;
}

/// Tests: load the codec libraries from `directory` instead of "<this DLL>\codecs" (nullptr = default).
/// Only affects this process, and only before a library is first loaded; the build-time pins still apply.
extern "C" void __stdcall GlideThumbnailSetCodecDirectory(const wchar_t* directory)
{
    glide::thumb::providers::SetCodecDirectoryForTests(directory);
}

/// Copies the SHA-256 pinned for codec library `kind` (0 glidecodecs.dll, 1 glidemanaged.dll) and returns its
/// length (0 when the library was absent at build time, so its formats get no thumbnails from it).
extern "C" int __stdcall GlideThumbnailCodecPin(int kind, char* buffer, int length)
{
    if (kind < 0 || kind > 1 || !buffer || length < 65) return 0;
    const char* pin = glide::thumb::providers::PinnedHash(static_cast<glide::thumb::providers::Kind>(kind));
    strncpy_s(buffer, static_cast<std::size_t>(length), pin, _TRUNCATE);
    return static_cast<int>(strlen(buffer));
}

/// Load state of codec library `kind`: 1 loaded and verified, 0 not attempted, negative = refused
/// (-1 no pin, -2 missing, -3 hash mismatch, -4 load failed, -5 ABI rejected).
extern "C" int __stdcall GlideThumbnailCodecState(int kind)
{
    if (kind < 0 || kind > 1) return 0;
    return glide::thumb::providers::LoadState(static_cast<glide::thumb::providers::Kind>(kind));
}
