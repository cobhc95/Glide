#pragma once
#ifndef NOMINMAX
#define NOMINMAX
#endif
#include <windows.h>
#include <d2d1.h>

namespace glide_view {

enum class ViewMode { Fit, FitWidth, FitHeight, Manual };

float FitScale(ViewMode mode, float manualZoom, UINT imageW, UINT imageH,
               float clientWidth, float contentHeight, float minZoom, float maxZoom);

D2D1_RECT_F DestinationRect(ViewMode mode, float manualZoom, float panX, float panY,
                            UINT imageW, UINT imageH, float clientWidth,
                            float contentHeight, float topInset, float minZoom, float maxZoom);

void ClampManualPan(ViewMode mode, float zoom, float& panX, float& panY,
                    UINT imageW, UINT imageH, float clientWidth,
                    float contentHeight, float topInset);

struct ZoomAtResult {
    float zoom{1.0f};
    float panX{};
    float panY{};
};

ZoomAtResult ZoomAtPoint(float oldScale, const D2D1_RECT_F& oldDestination,
                         UINT imageW, UINT imageH, float clientWidth, float clientHeight,
                         float sx, float sy, float factor, float minZoom, float maxZoom);

} // namespace glide_view
