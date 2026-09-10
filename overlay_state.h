#pragma once

#ifndef NOMINMAX
#define NOMINMAX
#endif
#ifndef WIN32_LEAN_AND_MEAN
#define WIN32_LEAN_AND_MEAN
#endif
#include <windows.h>
#include <d2d1.h>
#include <wrl/client.h>
#include <string>
#include <vector>

namespace glide_overlay {

struct OverlayImage {
    std::wstring path;
    UINT width{}, height{}, stride{};
    std::vector<BYTE> pixels;
    Microsoft::WRL::ComPtr<ID2D1Bitmap> bitmap;
    D2D1_RECT_F rect{};
    D2D1_RECT_F closeRect{};
    D2D1_RECT_F resizeRect{};
    D2D1_RECT_F sliderRect{};
    float opacity{1.0f};
    // Zoom changes only the sampled source region. The overlay frame remains fixed.
    float zoom{1.0f};
    float sourceCenterX{0.5f};
    float sourceCenterY{0.5f};
};

struct OverlayLayoutRecord {
    float opacity{1.0f};
    float x{}, y{}, width{}, height{};
    float zoom{1.0f};
    float sourceCenterX{0.5f};
    float sourceCenterY{0.5f};
    std::wstring path;
};

bool Contains(const D2D1_RECT_F& rect, POINT point) noexcept;
int HitTest(const std::vector<OverlayImage>& overlays, POINT point) noexcept;

bool Zoom(OverlayImage& overlay, int direction, int stepPercent) noexcept;
void ResetZoom(OverlayImage& overlay) noexcept;
void PanContent(OverlayImage& overlay, int deltaX, int deltaY,
                float startCenterX, float startCenterY) noexcept;

bool BringToFront(std::vector<OverlayImage>& overlays, int& activeIndex, int index);
bool Remove(std::vector<OverlayImage>& overlays, int& activeIndex, int index);

std::vector<OverlayLayoutRecord> CaptureLayout(const std::vector<OverlayImage>& overlays,
                                                float clientWidth, float clientHeight,
                                                bool rememberZoom);
bool WriteLayoutFile(const std::wstring& filePath,
                     const std::vector<OverlayLayoutRecord>& records);
bool ReadLayoutFile(const std::wstring& filePath,
                    std::vector<OverlayLayoutRecord>& records);

} // namespace glide_overlay
