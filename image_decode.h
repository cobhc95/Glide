#pragma once
#define NOMINMAX
#include <windows.h>
#include <wincodec.h>
#include <wrl/client.h>
#include <algorithm>
#include <atomic>
#include <condition_variable>
#include <cstdint>
#include <deque>
#include <mutex>
#include <new>
#include <string>
#include <thread>
#include <vector>

namespace GlideDecode {

using Microsoft::WRL::ComPtr;

struct DecodedImage {
    std::wstring path;
    UINT width{};
    UINT height{};
    UINT sourceWidth{};
    UINT sourceHeight{};
    UINT stride{};
    std::vector<BYTE> pixels;
    HRESULT hr{E_FAIL};
    uint64_t generation{};
    bool currentRequest{};
    bool preview{};
    bool refinement{};
    ULONGLONG decodeMs{};
    // WIC diagnostics are retained even when a compact native fallback later
    // succeeds. This distinguishes an advertised-but-unusable codec from a
    // successful content-sniffed stream retry or a genuinely native decode.
    HRESULT wicFilenameHr{E_NOTIMPL};
    HRESULT wicStreamInitHr{E_NOTIMPL};
    HRESULT wicStreamDecoderHr{E_NOTIMPL};
    bool wicStreamUsed{};
    bool nativeFallback{};
};

struct DecodeRequest {
    std::wstring path;
    uint64_t generation{};
    bool currentRequest{};
    bool preview{};
    bool refinement{};
    bool fastNavigation{};
    UINT previewMaxWidth{};
    UINT previewMaxHeight{};
};


DecodedImage DecodeFile(IWICImagingFactory* wic, const DecodeRequest& req, const std::atomic<uint64_t>* latestGeneration = nullptr);
void SetPreferColorProgressivePreview(bool enabled);

class DecodeWorker {
public:
    ~DecodeWorker() { Stop(); }

    bool Start(HWND hwnd, UINT completionMessage) {
        hwnd_ = hwnd;
        completionMessage_ = completionMessage;
        stop_ = false;
        try {
            thread_ = std::thread([this] { ThreadMain(); });
            return true;
        } catch (...) {
            return false;
        }
    }

    void Stop() {
        {
            std::lock_guard<std::mutex> lock(mutex_);
            stop_ = true;
            requests_.clear();
        }
        cv_.notify_all();
        if (thread_.joinable()) thread_.join();
    }

    void RequestCurrent(const std::wstring& path, uint64_t generation,
                        bool preview, bool refinement,
                        UINT previewMaxWidth = 0, UINT previewMaxHeight = 0,
                        bool fastNavigation = false) {
        latestGeneration_.store(generation, std::memory_order_relaxed);
        std::lock_guard<std::mutex> lock(mutex_);
        requests_.erase(std::remove_if(requests_.begin(), requests_.end(),
            [](const DecodeRequest& r) { return r.currentRequest; }), requests_.end());
        requests_.push_front({path, generation, true, preview, refinement, fastNavigation,
                              previewMaxWidth, previewMaxHeight});
        cv_.notify_one();
    }

    void CancelPending() {
        std::lock_guard<std::mutex> lock(mutex_);
        requests_.clear();
    }

    bool IsBusy() const { return busy_.load(std::memory_order_acquire); }

    void RequestPrefetch(const std::wstring& path, uint64_t generation,
                         UINT previewMaxWidth, UINT previewMaxHeight) {
        if (path.empty()) return;
        std::lock_guard<std::mutex> lock(mutex_);
        for (const auto& r : requests_) {
            if (!r.currentRequest && _wcsicmp(r.path.c_str(), path.c_str()) == 0) return;
        }
        requests_.push_back({path, generation, false, true, false, false,
                             previewMaxWidth, previewMaxHeight});
        while (requests_.size() > 8) requests_.pop_back();
        cv_.notify_one();
    }

private:
    void ThreadMain() {
        // The Microsoft Store-delivered WebP/HEIF/AVIF/JXL WIC codecs can be
        // registered and enumerable from an MTA yet fail to instantiate there
        // (WINCODEC_ERR_COMPONENTNOTFOUND).  A dedicated decoder thread does
        // not need MTA semantics, so use STA to match the application's UI
        // apartment and keep those system codecs usable without bundling a
        // heavyweight codec library.
        if (FAILED(CoInitializeEx(nullptr, COINIT_APARTMENTTHREADED))) return;
        ComPtr<IWICImagingFactory> wic;
        if (FAILED(CoCreateInstance(CLSID_WICImagingFactory, nullptr, CLSCTX_INPROC_SERVER,
                                    IID_PPV_ARGS(&wic)))) {
            CoUninitialize();
            return;
        }

        for (;;) {
            DecodeRequest req;
            {
                std::unique_lock<std::mutex> lock(mutex_);
                cv_.wait(lock, [this] { return stop_ || !requests_.empty(); });
                if (stop_) break;
                req = requests_.front();
                requests_.pop_front();
            }

            busy_.store(true, std::memory_order_release);
            const ULONGLONG decodeStart = GetTickCount64();
            auto* result = new (std::nothrow) DecodedImage(DecodeFile(wic.Get(), req, &latestGeneration_));
            if (result) result->decodeMs = GetTickCount64() - decodeStart;
            busy_.store(false, std::memory_order_release);
            if (!result) continue;
            if (!PostMessageW(hwnd_, completionMessage_, 0, reinterpret_cast<LPARAM>(result))) {
                delete result;
            }
        }

        wic.Reset();
        CoUninitialize();
    }

    HWND hwnd_{};
    UINT completionMessage_{};
    std::thread thread_;
    std::mutex mutex_;
    std::condition_variable cv_;
    std::deque<DecodeRequest> requests_;
    std::atomic<uint64_t> latestGeneration_{0};
    std::atomic<bool> busy_{false};
    bool stop_{};
};


} // namespace GlideDecode
