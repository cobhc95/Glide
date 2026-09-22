// Glide.ShellThumbnail — COM in-proc server for Windows Explorer thumbnails.
//
// Explorer loads this DLL inside its isolated thumbnail host (dllhost.exe). It implements
// IThumbnailProvider plus IInitializeWithStream and IInitializeWithItem, and delegates all decoding
// to ThumbnailCore. It never loads .NET, Avalonia, Glide.exe or any Glide UI, never shows UI, and
// never lets an exception escape a COM boundary.

#include "ThumbnailCore.h"

#include <windows.h>
#include <thumbcache.h>
#include <shobjidl.h>
#include <shlobj.h>
#include <shlwapi.h>
#include <propvarutil.h>

#include <atomic>
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
    constexpr std::size_t kMaxStreamBytes = 1ull << 30; // 1 GiB safety cap

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

    bool ReadStreamIntoBuffer(IStream* stream, std::vector<std::uint8_t>& buffer)
    {
        if (!stream) return false;
        LARGE_INTEGER zero{};
        stream->Seek(zero, STREAM_SEEK_SET, nullptr);
        buffer.clear();
        std::uint8_t chunk[64 * 1024];
        for (;;)
        {
            ULONG read = 0;
            const HRESULT hr = stream->Read(chunk, static_cast<ULONG>(sizeof(chunk)), &read);
            if (FAILED(hr) || read == 0) break;
            if (buffer.size() + read > kMaxStreamBytes) return false;
            buffer.insert(buffer.end(), chunk, chunk + read);
        }
        return !buffer.empty();
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
        ~GlideThumbnailProvider() { g_objectCount.fetch_sub(1); }

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
            if (!ReadStreamIntoBuffer(stream, buffer_)) return E_FAIL;
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
            if (buffer_.empty()) return E_FAIL;
            if (cx == 0) cx = 256;

            try
            {
                const ULONGLONG started = GetTickCount64();
                glide::thumb::DecodeOptions options;
                options.maxWidth = cx;
                options.maxHeight = cx;
                options.preferEmbedded = true;
                options.quality = 1;

                const auto result = glide::thumb::DecodeForThumbnail(
                    buffer_.data(), buffer_.size(), extension_.c_str(), options);
                if (!result.ok || !result.image.Valid()) return E_FAIL;

                const auto composed = glide::thumb::ComposeThumbnail(result.image, cx);
                if (!composed.Valid()) return E_FAIL;

                if (!CreateThumbnailBitmap(composed, phbmp)) return E_FAIL;
                *pdwAlpha = WTSAT_ARGB;
                Trace(result.decoder, extension_.c_str(), cx, result.embedded, result.decodeMicros, GetTickCount64() - started);
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
        std::vector<std::uint8_t> buffer_;
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
