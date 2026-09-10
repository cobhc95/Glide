#include "overlay_state.h"

#include <algorithm>
#include <climits>
#include <filesystem>
#include <fstream>
#include <iterator>
#include <sstream>
#include <utility>

namespace glide_overlay {

namespace {

bool WideToUtf8(const std::wstring& text, std::string& utf8) {
    utf8.clear();
    if (text.empty()) return true;
    if (text.size() > static_cast<size_t>(INT_MAX)) return false;
    const int chars = static_cast<int>(text.size());
    const int needed = WideCharToMultiByte(CP_UTF8, WC_ERR_INVALID_CHARS,
                                           text.data(), chars, nullptr, 0, nullptr, nullptr);
    if (needed <= 0) return false;
    utf8.resize(static_cast<size_t>(needed));
    return WideCharToMultiByte(CP_UTF8, WC_ERR_INVALID_CHARS, text.data(), chars,
                               utf8.data(), needed, nullptr, nullptr) == needed;
}

bool BytesToWide(const std::string& bytes, UINT codePage, DWORD flags, std::wstring& text) {
    text.clear();
    if (bytes.empty()) return true;
    if (bytes.size() > static_cast<size_t>(INT_MAX)) return false;
    const int count = static_cast<int>(bytes.size());
    const int needed = MultiByteToWideChar(codePage, flags, bytes.data(), count, nullptr, 0);
    if (needed <= 0) return false;
    text.resize(static_cast<size_t>(needed));
    return MultiByteToWideChar(codePage, flags, bytes.data(), count, text.data(), needed) == needed;
}

bool ReadTextFile(const std::filesystem::path& path, std::wstring& text) {
    std::ifstream f(path, std::ios::binary);
    if (!f) return false;
    std::string bytes((std::istreambuf_iterator<char>(f)), std::istreambuf_iterator<char>());
    if (bytes.size() >= 3 && static_cast<unsigned char>(bytes[0]) == 0xEF &&
        static_cast<unsigned char>(bytes[1]) == 0xBB && static_cast<unsigned char>(bytes[2]) == 0xBF)
        bytes.erase(0, 3);
    if (BytesToWide(bytes, CP_UTF8, MB_ERR_INVALID_CHARS, text)) return true;
    // Compatibility for legacy V1/V2 files written through the process ANSI locale.
    return BytesToWide(bytes, CP_ACP, 0, text);
}

bool WriteTextFileUtf8(const std::filesystem::path& path, const std::wstring& text) {
    std::string utf8;
    if (!WideToUtf8(text, utf8)) return false;
    std::ofstream f(path, std::ios::binary | std::ios::trunc);
    if (!f) return false;
    f.write(utf8.data(), static_cast<std::streamsize>(utf8.size()));
    return static_cast<bool>(f);
}

bool ParseLayoutText(const std::wstring& text, std::vector<OverlayLayoutRecord>& records) {
    std::wistringstream f(text);
    std::wstring header;
    if (!std::getline(f, header) || (header != L"GLIDE_OVERLAY_V1" && header != L"GLIDE_OVERLAY_V2"))
        return false;
    const bool v2 = header == L"GLIDE_OVERLAY_V2";
    std::vector<OverlayLayoutRecord> parsed;
    std::wstring line;
    while (std::getline(f, line)) {
        std::wistringstream ss(line);
        OverlayLayoutRecord r{};
        wchar_t tab{};
        if (!(ss >> r.opacity)) continue; ss.get(tab);
        if (!(ss >> r.x)) continue; ss.get(tab);
        if (!(ss >> r.y)) continue; ss.get(tab);
        if (!(ss >> r.width)) continue; ss.get(tab);
        if (!(ss >> r.height)) continue; ss.get(tab);
        if (v2) {
            if (!(ss >> r.zoom)) continue; ss.get(tab);
            if (!(ss >> r.sourceCenterX)) continue; ss.get(tab);
            if (!(ss >> r.sourceCenterY)) continue; ss.get(tab);
        }
        std::getline(ss, r.path);
        if (r.path.empty()) continue;
        r.opacity = std::clamp(r.opacity, 0.10f, 1.0f);
        r.zoom = std::clamp(r.zoom, 1.0f, 32.0f);
        r.sourceCenterX = std::clamp(r.sourceCenterX, 0.0f, 1.0f);
        r.sourceCenterY = std::clamp(r.sourceCenterY, 0.0f, 1.0f);
        parsed.push_back(std::move(r));
    }
    records = std::move(parsed);
    return true;
}

} // namespace

bool Contains(const D2D1_RECT_F& rect, POINT point) noexcept {
    return point.x >= rect.left && point.x <= rect.right &&
           point.y >= rect.top && point.y <= rect.bottom;
}

int HitTest(const std::vector<OverlayImage>& overlays, POINT point) noexcept {
    for (int i = static_cast<int>(overlays.size()) - 1; i >= 0; --i) {
        if (Contains(overlays[static_cast<size_t>(i)].rect, point)) return i;
    }
    return -1;
}

bool Zoom(OverlayImage& overlay, int direction, int stepPercent) noexcept {
    const float step = 1.0f + std::clamp(stepPercent, 5, 100) / 100.0f;
    const float oldZoom = overlay.zoom;
    overlay.zoom = std::clamp(direction > 0 ? overlay.zoom * step : overlay.zoom / step,
                              1.0f, 32.0f);
    return overlay.zoom != oldZoom;
}

void ResetZoom(OverlayImage& overlay) noexcept {
    overlay.zoom = 1.0f;
    overlay.sourceCenterX = 0.5f;
    overlay.sourceCenterY = 0.5f;
}

void PanContent(OverlayImage& overlay, int deltaX, int deltaY,
                float startCenterX, float startCenterY) noexcept {
    const float rw = (std::max)(1.0f, overlay.rect.right - overlay.rect.left);
    const float rh = (std::max)(1.0f, overlay.rect.bottom - overlay.rect.top);
    const float z = (std::max)(1.0f, overlay.zoom);
    const float half = 0.5f / z;
    overlay.sourceCenterX = std::clamp(startCenterX - static_cast<float>(deltaX) / (rw * z),
                                       half, 1.0f - half);
    overlay.sourceCenterY = std::clamp(startCenterY - static_cast<float>(deltaY) / (rh * z),
                                       half, 1.0f - half);
}

bool BringToFront(std::vector<OverlayImage>& overlays, int& activeIndex, int index) {
    if (index < 0 || index >= static_cast<int>(overlays.size())) return false;
    if (index == static_cast<int>(overlays.size()) - 1) {
        activeIndex = index;
        return true;
    }
    OverlayImage moved = std::move(overlays[static_cast<size_t>(index)]);
    overlays.erase(overlays.begin() + index);
    overlays.push_back(std::move(moved));
    activeIndex = static_cast<int>(overlays.size()) - 1;
    return true;
}

bool Remove(std::vector<OverlayImage>& overlays, int& activeIndex, int index) {
    if (index < 0 || index >= static_cast<int>(overlays.size())) return false;
    overlays.erase(overlays.begin() + index);
    if (overlays.empty()) activeIndex = -1;
    else if (activeIndex == index) activeIndex = -1;
    else if (activeIndex > index) --activeIndex;
    return true;
}

std::vector<OverlayLayoutRecord> CaptureLayout(const std::vector<OverlayImage>& overlays,
                                                float clientWidth, float clientHeight,
                                                bool rememberZoom) {
    clientWidth = (std::max)(1.0f, clientWidth);
    clientHeight = (std::max)(1.0f, clientHeight);
    std::vector<OverlayLayoutRecord> records;
    records.reserve(overlays.size());
    for (const auto& overlay : overlays) {
        OverlayLayoutRecord r{};
        r.opacity = overlay.opacity;
        r.x = overlay.rect.left / clientWidth;
        r.y = overlay.rect.top / clientHeight;
        r.width = (overlay.rect.right - overlay.rect.left) / clientWidth;
        r.height = (overlay.rect.bottom - overlay.rect.top) / clientHeight;
        r.zoom = rememberZoom ? overlay.zoom : 1.0f;
        r.sourceCenterX = rememberZoom ? overlay.sourceCenterX : 0.5f;
        r.sourceCenterY = rememberZoom ? overlay.sourceCenterY : 0.5f;
        r.path = overlay.path;
        records.push_back(std::move(r));
    }
    return records;
}

bool WriteLayoutFile(const std::wstring& filePath,
                     const std::vector<OverlayLayoutRecord>& records) {
    std::wostringstream text;
    text << L"GLIDE_OVERLAY_V2\n";
    for (const auto& r : records) {
        text << r.opacity << L'\t' << r.x << L'\t' << r.y << L'\t'
             << r.width << L'\t' << r.height << L'\t' << r.zoom << L'\t'
             << r.sourceCenterX << L'\t' << r.sourceCenterY << L'\t' << r.path << L'\n';
    }
    return WriteTextFileUtf8(std::filesystem::path(filePath), text.str());
}

bool ReadLayoutFile(const std::wstring& filePath,
                    std::vector<OverlayLayoutRecord>& records) {
    std::wstring text;
    if (!ReadTextFile(std::filesystem::path(filePath), text)) return false;
    return ParseLayoutText(text, records);
}

} // namespace glide_overlay
