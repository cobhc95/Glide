// Glide.ShellThumbnail — lazy, hash-pinned bridge to Glide's optional codec libraries.
// See CodecProviders.h for the contract and docs/explorer-thumbnails.md for the overall design.

#include "CodecProviders.h"
#include "GlideCodecPins.h"
#include "../Glide.CodecProvider.ABI.h"

#include <windows.h>
#include <bcrypt.h>

#include <algorithm>
#include <atomic>
#include <cstring>
#include <new>
#include <string>
#include <vector>

#pragma comment(lib, "bcrypt.lib")

namespace glide::thumb::providers
{
    namespace
    {
        using glide_codec_abi::ProbeResult;
        using glide_codec_abi::ProviderInfo;
        using glide_codec_abi::Surface;

        using GetInfoFn = const ProviderInfo* (__cdecl*)();
        using CreateFn = void* (__cdecl*)();
        using ProbeFn = int (__cdecl*)(void*, const char*, ProbeResult*);
        using DecodeFn = int (__cdecl*)(void*, const char*, std::uint32_t, Surface*);
        using FreeFn = void (__cdecl*)(void*, std::size_t);
        using HostDecodeFn = int (__cdecl*)(const std::uint8_t*, std::size_t, std::uint32_t, int, HostImage*);
        using HostFreeFn = void (__cdecl*)(void*);
        using SetHostFn = void (__cdecl*)(HostDecodeFn, HostFreeFn);
        using GunzipFn = int (__cdecl*)(const std::uint8_t*, std::size_t, std::size_t, std::uint8_t**, std::size_t*);

        // Caps for anything handed to a codec library from inside Explorer's thumbnail host.
        constexpr std::size_t MaxSpoolBytes = 256ull * 1024 * 1024;      // source file
        constexpr std::uint32_t MaxSide = 65'535;                         // probed width/height
        constexpr std::uint64_t MaxSourcePixels = 128'000'000ull;         // probed frame (the codec decodes it whole)
        constexpr std::uint32_t MaxSurfaceSide = 32'768;                  // returned preview surface
        constexpr std::uint64_t MaxSurfacePixels = 64'000'000ull;
        constexpr std::uint64_t MaxLibraryBytes = 64ull * 1024 * 1024;    // codec DLL being hashed
        constexpr std::uint32_t MaxExtensions = 512;
        constexpr std::size_t MaxExtensionLength = 24;

        // Load states (LoadState()).
        constexpr int StateNotLoaded = 0;
        constexpr int StateLoaded = 1;
        constexpr int StateNoPin = -1;
        constexpr int StateMissing = -2;
        constexpr int StateHashMismatch = -3;
        constexpr int StateLoadFailed = -4;
        constexpr int StateBadAbi = -5;

        struct Library
        {
            const wchar_t* fileName;
            const char* pin;
            INIT_ONCE once = INIT_ONCE_STATIC_INIT;
            std::atomic<int> state{ StateNotLoaded };
            void* instance = nullptr;
            ProbeFn probe = nullptr;
            DecodeFn preview = nullptr;
            FreeFn free = nullptr;
            GunzipFn gunzip = nullptr; // glidemanaged.dll only
            std::vector<std::wstring> extensions;

            bool Owns(const std::wstring& extension) const
            {
                return std::find(extensions.begin(), extensions.end(), extension) != extensions.end();
            }
        };

        Library g_bundled{ L"glidecodecs.dll", GLIDE_PIN_GLIDECODECS };
        Library g_managed{ L"glidemanaged.dll", GLIDE_PIN_GLIDEMANAGED };

        SRWLOCK g_directoryLock = SRWLOCK_INIT;
        std::wstring g_directoryOverride;

        Library& For(Kind kind) { return kind == Kind::Bundled ? g_bundled : g_managed; }

        // ------------------------------------------------------------ structured-exception guards ------
        //
        // Hardware faults (and a C++ exception escaping a library) raised inside a codec library are
        // contained here so a hostile file fails instead of taking the thumbnail host down. These helpers
        // hold no objects with destructors, as __try requires.

        int SehFilter(DWORD code)
        {
            switch (code)
            {
            case EXCEPTION_ACCESS_VIOLATION:
            case EXCEPTION_ARRAY_BOUNDS_EXCEEDED:
            case EXCEPTION_DATATYPE_MISALIGNMENT:
            case EXCEPTION_IN_PAGE_ERROR:
            case EXCEPTION_INT_DIVIDE_BY_ZERO:
            case EXCEPTION_INT_OVERFLOW:
            case EXCEPTION_ILLEGAL_INSTRUCTION:
            case EXCEPTION_PRIV_INSTRUCTION:
            case 0xE06D7363: // C++ exception thrown by the library
                return EXCEPTION_EXECUTE_HANDLER;
            default:
                return EXCEPTION_CONTINUE_SEARCH;
            }
        }

        struct InfoCopy
        {
            std::uint32_t abiVersion;
            std::uint32_t structSize;
            std::uint32_t capabilities;
            std::uint32_t count;
            char extensions[MaxExtensions][MaxExtensionLength];
        };

        bool GuardedReadInfo(GetInfoFn getInfo, InfoCopy* copy)
        {
            __try
            {
                const ProviderInfo* info = getInfo();
                if (!info) return false;
                copy->abiVersion = info->abiVersion;
                copy->structSize = info->structSize;
                copy->capabilities = info->capabilities;
                copy->count = 0;
                if (info->extensionCount == 0 || info->extensionCount > MaxExtensions || !info->extensions) return false;
                for (std::uint32_t i = 0; i < info->extensionCount; ++i)
                {
                    const char* extension = info->extensions[i];
                    if (!extension) return false;
                    std::size_t length = 0;
                    while (length < MaxExtensionLength && extension[length] != 0) ++length;
                    if (length == 0 || length >= MaxExtensionLength) continue;
                    std::memcpy(copy->extensions[copy->count], extension, length + 1);
                    ++copy->count;
                }
                return true;
            }
            __except (SehFilter(GetExceptionCode()))
            {
                return false;
            }
        }

        void* GuardedCreate(CreateFn create)
        {
            __try { return create(); }
            __except (SehFilter(GetExceptionCode())) { return nullptr; }
        }

        bool GuardedSetHost(SetHostFn setHost)
        {
            __try { setHost(&DecodeEncodedForHost, &FreeEncodedForHost); return true; }
            __except (SehFilter(GetExceptionCode())) { return false; }
        }

        int GuardedProbe(ProbeFn probe, void* instance, const char* path, ProbeResult* result)
        {
            __try { return probe(instance, path, result); }
            __except (SehFilter(GetExceptionCode())) { return 0; }
        }

        int GuardedDecode(DecodeFn decode, void* instance, const char* path, std::uint32_t longestSide, Surface* result)
        {
            __try { return decode(instance, path, longestSide, result); }
            __except (SehFilter(GetExceptionCode())) { return 0; }
        }

        void GuardedFree(FreeFn free, void* data, std::size_t size)
        {
            __try { free(data, size); }
            __except (SehFilter(GetExceptionCode())) { /* leak rather than fault */ }
        }

        int GuardedGunzip(GunzipFn gunzip, const std::uint8_t* data, std::size_t size, std::size_t maxOutput,
                          std::uint8_t** output, std::size_t* outputSize)
        {
            __try { return gunzip(data, size, maxOutput, output, outputSize); }
            __except (SehFilter(GetExceptionCode())) { return 0; }
        }

        bool GuardedCopy(void* destination, const void* source, std::size_t count)
        {
            __try { std::memcpy(destination, source, count); return true; }
            __except (SehFilter(GetExceptionCode())) { return false; }
        }

        // ---------------------------------------------------------------------------- loading ------

        std::wstring CodecDirectory()
        {
            AcquireSRWLockShared(&g_directoryLock);
            std::wstring overridden = g_directoryOverride;
            ReleaseSRWLockShared(&g_directoryLock);
            if (!overridden.empty()) return overridden;

            HMODULE self = nullptr;
            if (!GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT,
                                    reinterpret_cast<LPCWSTR>(&CodecDirectory), &self) || !self)
                return L"";
            std::wstring path(MAX_PATH, L'\0');
            for (;;)
            {
                const DWORD length = GetModuleFileNameW(self, path.data(), static_cast<DWORD>(path.size()));
                if (length == 0) return L"";
                if (length < path.size()) { path.resize(length); break; }
                if (path.size() >= 32'768) return L"";
                path.resize(path.size() * 2);
            }
            const std::size_t slash = path.find_last_of(L"\\/");
            if (slash == std::wstring::npos) return L"";
            return path.substr(0, slash) + L"\\codecs";
        }

        bool Sha256Matches(HANDLE file, const char* expectedHex)
        {
            LARGE_INTEGER size{};
            if (!GetFileSizeEx(file, &size) || size.QuadPart <= 0 || static_cast<std::uint64_t>(size.QuadPart) > MaxLibraryBytes)
                return false;

            BCRYPT_ALG_HANDLE algorithm = nullptr;
            BCRYPT_HASH_HANDLE hash = nullptr;
            bool ok = false;
            std::vector<std::uint8_t> buffer;
            try { buffer.resize(1u << 20); }
            catch (...) { return false; }
            std::uint8_t digest[32]{};
            if (BCryptOpenAlgorithmProvider(&algorithm, BCRYPT_SHA256_ALGORITHM, nullptr, 0) == 0 &&
                BCryptCreateHash(algorithm, &hash, nullptr, 0, nullptr, 0, 0) == 0)
            {
                ok = true;
                for (;;)
                {
                    DWORD read = 0;
                    if (!ReadFile(file, buffer.data(), static_cast<DWORD>(buffer.size()), &read, nullptr)) { ok = false; break; }
                    if (read == 0) break;
                    if (BCryptHashData(hash, buffer.data(), read, 0) != 0) { ok = false; break; }
                }
                ok = ok && BCryptFinishHash(hash, digest, sizeof(digest), 0) == 0;
            }
            if (hash) BCryptDestroyHash(hash);
            if (algorithm) BCryptCloseAlgorithmProvider(algorithm, 0);
            if (!ok) return false;

            static const char hex[] = "0123456789abcdef";
            char actual[65]{};
            for (int i = 0; i < 32; ++i)
            {
                actual[i * 2] = hex[digest[i] >> 4];
                actual[i * 2 + 1] = hex[digest[i] & 15];
            }
            if (std::strlen(expectedHex) != 64) return false;
            for (int i = 0; i < 64; ++i)
            {
                char expected = expectedHex[i];
                if (expected >= 'A' && expected <= 'F') expected = static_cast<char>(expected - 'A' + 'a');
                if (expected != actual[i]) return false;
            }
            return true;
        }

        int LoadLibraryState(Library& library)
        {
            if (!library.pin || library.pin[0] == 0) return StateNoPin;
            const std::wstring directory = CodecDirectory();
            if (directory.empty()) return StateMissing;
            const std::wstring path = directory + L"\\" + library.fileName;

            // Deny writers and deleters while the file is hashed and mapped, so the bytes that were
            // verified are the bytes that get loaded.
            HANDLE file = CreateFileW(path.c_str(), GENERIC_READ, FILE_SHARE_READ, nullptr, OPEN_EXISTING,
                                      FILE_ATTRIBUTE_NORMAL | FILE_FLAG_SEQUENTIAL_SCAN, nullptr);
            if (file == INVALID_HANDLE_VALUE) return StateMissing;
            if (!Sha256Matches(file, library.pin))
            {
                CloseHandle(file);
                return StateHashMismatch;
            }
            // Absolute path + DLL_LOAD_DIR|SYSTEM32: dependencies resolve from the codecs folder and
            // System32 only, never from the image's folder, the current directory or PATH.
            HMODULE module = LoadLibraryExW(path.c_str(), nullptr, LOAD_LIBRARY_SEARCH_DLL_LOAD_DIR | LOAD_LIBRARY_SEARCH_SYSTEM32);
            CloseHandle(file);
            if (!module) return StateLoadFailed;
            // Never unloaded: native buffers and (for the NativeAOT library) its runtime must outlive every
            // call, and a NativeAOT module cannot be unloaded at all.
            HMODULE pinned = nullptr;
            GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_PIN, path.c_str(), &pinned);

            const auto getInfo = reinterpret_cast<GetInfoFn>(GetProcAddress(module, "GlideCodecProvider_GetInfo"));
            const auto create = reinterpret_cast<CreateFn>(GetProcAddress(module, "GlideCodecProvider_Create"));
            library.probe = reinterpret_cast<ProbeFn>(GetProcAddress(module, "GlideCodecProvider_Probe"));
            library.preview = reinterpret_cast<DecodeFn>(GetProcAddress(module, "GlideCodecProvider_DecodePreviewSurface"));
            library.free = reinterpret_cast<FreeFn>(GetProcAddress(module, "GlideCodecProvider_FreeBuffer"));
            if (!getInfo || !create || !library.probe || !library.preview || !library.free) return StateBadAbi;

            auto* info = new (std::nothrow) InfoCopy{};
            if (!info) return StateLoadFailed;
            const bool infoOk = GuardedReadInfo(getInfo, info);
            if (!infoOk || info->abiVersion != glide_codec_abi::CurrentVersion ||
                info->structSize < sizeof(ProviderInfo) ||
                (info->capabilities & glide_codec_abi::SupportsPreview) == 0 || info->count == 0)
            {
                delete info;
                return StateBadAbi;
            }
            try
            {
                for (std::uint32_t i = 0; i < info->count; ++i)
                {
                    std::wstring extension;
                    for (const char* c = info->extensions[i]; *c; ++c)
                        extension.push_back(static_cast<wchar_t>(towlower(static_cast<unsigned char>(*c))));
                    library.extensions.push_back(std::move(extension));
                }
            }
            catch (...)
            {
                delete info;
                return StateLoadFailed;
            }
            delete info;

            if (library.fileName == g_managed.fileName)
            {
                const auto setHost = reinterpret_cast<SetHostFn>(GetProcAddress(module, "GlideManaged_SetHostDecoder"));
                if (!setHost || !GuardedSetHost(setHost)) return StateBadAbi;
                library.gunzip = reinterpret_cast<GunzipFn>(GetProcAddress(module, "GlideManaged_Gunzip"));
            }
            library.instance = GuardedCreate(create);
            return library.instance ? StateLoaded : StateBadAbi;
        }

        BOOL CALLBACK LoadOnce(PINIT_ONCE, PVOID parameter, PVOID*)
        {
            auto* library = static_cast<Library*>(parameter);
            int state = StateLoadFailed;
            try { state = LoadLibraryState(*library); }
            catch (...) { state = StateLoadFailed; }
            library->state.store(state);
            return TRUE;
        }

        Library* Ensure(Kind kind)
        {
            Library& library = For(kind);
            InitOnceExecuteOnce(&library.once, &LoadOnce, &library, nullptr);
            return library.state.load() == StateLoaded ? &library : nullptr;
        }

        // ---------------------------------------------------------------------- source spooling ------

        /// The bytes Explorer streamed to us, as a delete-on-close file the path-based ABI can open. The
        /// name keeps the (sanitised) extension because the managed decoders dispatch on it.
        class Spool
        {
        public:
            ~Spool() { if (handle_ != INVALID_HANDLE_VALUE) CloseHandle(handle_); }

            bool Create(const std::wstring& extension, const std::uint8_t* data, std::size_t size)
            {
                std::wstring suffix = L".bin";
                if (extension.size() >= 2 && extension.size() <= 16 && extension[0] == L'.' &&
                    std::all_of(extension.begin() + 1, extension.end(),
                                [](wchar_t c) { return (c >= L'a' && c <= L'z') || (c >= L'0' && c <= L'9') || c == L'.'; }))
                    suffix = extension;

                wchar_t directory[MAX_PATH + 1]{};
                const DWORD length = GetTempPathW(MAX_PATH + 1, directory);
                if (length == 0 || length > MAX_PATH) return false;
                static std::atomic<unsigned long> counter{ 0 };
                for (int attempt = 0; attempt < 4 && handle_ == INVALID_HANDLE_VALUE; ++attempt)
                {
                    path_ = std::wstring(directory) + L"glide-thumb-" + std::to_wstring(GetCurrentProcessId()) + L"-" +
                            std::to_wstring(GetCurrentThreadId()) + L"-" + std::to_wstring(counter.fetch_add(1)) + suffix;
                    handle_ = CreateFileW(path_.c_str(), GENERIC_READ | GENERIC_WRITE | DELETE, FILE_SHARE_READ | FILE_SHARE_DELETE,
                                          nullptr, CREATE_NEW, FILE_ATTRIBUTE_TEMPORARY | FILE_FLAG_DELETE_ON_CLOSE, nullptr);
                }
                if (handle_ == INVALID_HANDLE_VALUE) return false;
                std::size_t written = 0;
                while (written < size)
                {
                    const DWORD chunk = static_cast<DWORD>(std::min<std::size_t>(size - written, 16u << 20));
                    DWORD wrote = 0;
                    if (!WriteFile(handle_, data + written, chunk, &wrote, nullptr) || wrote == 0) return false;
                    written += wrote;
                }
                return true;
            }

            bool Utf8Path(std::string& utf8) const
            {
                const int bytes = WideCharToMultiByte(CP_UTF8, WC_ERR_INVALID_CHARS, path_.c_str(), -1, nullptr, 0, nullptr, nullptr);
                if (bytes <= 1) return false;
                utf8.assign(static_cast<std::size_t>(bytes), '\0');
                if (WideCharToMultiByte(CP_UTF8, WC_ERR_INVALID_CHARS, path_.c_str(), -1, utf8.data(), bytes, nullptr, nullptr) != bytes)
                    return false;
                utf8.resize(static_cast<std::size_t>(bytes) - 1);
                return true;
            }

        private:
            HANDLE handle_ = INVALID_HANDLE_VALUE;
            std::wstring path_;
        };

        constexpr const wchar_t* BundledCandidates[] = {
            L".avif", L".avifs", L".heic", L".heif", L".heics", L".heifs", L".hif", L".jxl",
            L".jp2", L".j2k", L".j2c", L".jpc", L".jpx", L".jpf", L".jpm", L".mj2",
        };

        constexpr const wchar_t* ManagedCandidates[] = {
            L".exr", L".pfm", L".dcm", L".dicom", L".ima", L".nii", L".fits", L".fit", L".fts",
            L".ora", L".kra", L".pdn", L".xcf", L".ktx", L".ktx2", L".pvr", L".astc", L".psd", L".psb", L".pdd",
            L".dng", L".cr2", L".cr3", L".crw", L".nef", L".nrw", L".arw", L".srf", L".sr2", L".raf", L".orf", L".ori",
            L".rw2", L".rwl", L".pef", L".ptx", L".3fr", L".fff", L".iiq", L".cap", L".eip", L".mef", L".mos", L".mrw",
            L".x3f", L".erf", L".kdc", L".dcr", L".k25", L".bay", L".srw", L".rwz", L".gpr", L".mdc", L".raw", L".dcs",
            L".drf", L".dsc", L".r2d", L".rw1",
        };
    }

    bool IsCandidate(Kind kind, const std::wstring& extension)
    {
        if (kind == Kind::Bundled)
            return std::any_of(std::begin(BundledCandidates), std::end(BundledCandidates), [&](const wchar_t* e) { return extension == e; });
        return std::any_of(std::begin(ManagedCandidates), std::end(ManagedCandidates), [&](const wchar_t* e) { return extension == e; });
    }

    bool Decode(Kind kind, const std::uint8_t* data, std::size_t size, const std::wstring& extension,
                std::uint32_t longestSide, Image& out, std::uint32_t& sourceWidth, std::uint32_t& sourceHeight)
    {
        try
        {
            out = {};
            if (!data || size == 0 || size > MaxSpoolBytes || !IsCandidate(kind, extension)) return false;
            Library* library = Ensure(kind);
            if (!library || !library->Owns(extension)) return false;

            Spool spool;
            std::string path;
            if (!spool.Create(extension, data, size) || !spool.Utf8Path(path)) return false;

            ProbeResult probe{};
            if (!GuardedProbe(library->probe, library->instance, path.c_str(), &probe) || !probe.supported) return false;
            if (probe.width == 0 || probe.height == 0 || probe.width > MaxSide || probe.height > MaxSide ||
                static_cast<std::uint64_t>(probe.width) * probe.height > MaxSourcePixels)
                return false;

            Surface surface{};
            const std::uint32_t side = std::clamp<std::uint32_t>(longestSide, 1, MaxThumbnailEdge);
            const int decoded = GuardedDecode(library->preview, library->instance, path.c_str(), side, &surface);
            if (!surface.data) return false;
            bool ok = decoded != 0 &&
                      surface.width > 0 && surface.height > 0 &&
                      surface.width <= MaxSurfaceSide && surface.height <= MaxSurfaceSide &&
                      static_cast<std::uint64_t>(surface.width) * surface.height <= MaxSurfacePixels &&
                      surface.pixelFormat == glide_codec_abi::PixelFormatBgra8 &&
                      surface.alphaMode == glide_codec_abi::AlphaPremultiplied &&
                      static_cast<std::uint64_t>(surface.stride) >= static_cast<std::uint64_t>(surface.width) * 4 &&
                      static_cast<std::uint64_t>(surface.stride) * (surface.height - 1) + static_cast<std::uint64_t>(surface.width) * 4 <=
                          static_cast<std::uint64_t>(surface.size);
            if (ok)
            {
                try
                {
                    out.width = surface.width;
                    out.height = surface.height;
                    out.premultiplied = true;
                    out.pixels.resize(static_cast<std::size_t>(surface.width) * surface.height * 4);
                    const std::size_t row = static_cast<std::size_t>(surface.width) * 4;
                    for (std::uint32_t y = 0; y < surface.height && ok; ++y)
                        ok = GuardedCopy(out.pixels.data() + y * row,
                                         static_cast<const std::uint8_t*>(surface.data) + static_cast<std::size_t>(y) * surface.stride, row);
                }
                catch (...)
                {
                    ok = false;
                }
            }
            GuardedFree(library->free, surface.data, surface.size);
            if (!ok || !out.Valid())
            {
                out = {};
                return false;
            }
            sourceWidth = probe.width;
            sourceHeight = probe.height;
            return true;
        }
        catch (...)
        {
            out = {};
            return false;
        }
    }

    bool Gunzip(const std::uint8_t* data, std::size_t size, std::size_t maxOutput, std::vector<std::uint8_t>& out)
    {
        try
        {
            out.clear();
            if (!data || size < 18 || size > MaxSpoolBytes || data[0] != 0x1F || data[1] != 0x8B) return false;
            Library* library = Ensure(Kind::Managed);
            if (!library || !library->gunzip) return false;
            std::uint8_t* inflated = nullptr;
            std::size_t inflatedSize = 0;
            const int ok = GuardedGunzip(library->gunzip, data, size, maxOutput, &inflated, &inflatedSize);
            if (!inflated) return false;
            bool copied = false;
            if (ok && inflatedSize > 0 && inflatedSize <= maxOutput)
            {
                out.resize(inflatedSize);
                copied = GuardedCopy(out.data(), inflated, inflatedSize);
            }
            GuardedFree(library->free, inflated, inflatedSize);
            if (!copied) out.clear();
            return copied;
        }
        catch (...)
        {
            out.clear();
            return false;
        }
    }

    void SetCodecDirectoryForTests(const wchar_t* directory)
    {
        AcquireSRWLockExclusive(&g_directoryLock);
        try { g_directoryOverride = directory ? directory : L""; }
        catch (...) { g_directoryOverride.clear(); }
        ReleaseSRWLockExclusive(&g_directoryLock);
    }

    const char* PinnedHash(Kind kind)
    {
        const char* pin = For(kind).pin;
        return pin ? pin : "";
    }

    int LoadState(Kind kind)
    {
        return For(kind).state.load();
    }
}
