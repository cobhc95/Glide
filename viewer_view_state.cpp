#include "viewer_view_state.h"
#include <algorithm>
#include <cmath>

namespace glide_view {

float FitScale(ViewMode mode, float manualZoom, UINT imageW, UINT imageH,
               float clientWidth, float contentHeight, float minZoom, float maxZoom) {
    if (imageW == 0 || imageH == 0) return 1.0f;
    clientWidth = (std::max)(1.0f, clientWidth);
    contentHeight = (std::max)(1.0f, contentHeight);
    float scale = 1.0f;
    switch (mode) {
        case ViewMode::Fit:
            scale = (std::min)(clientWidth / static_cast<float>(imageW), contentHeight / static_cast<float>(imageH));
            scale = (std::min)(scale, 1.0f);
            break;
        case ViewMode::FitWidth:
            scale = clientWidth / static_cast<float>(imageW);
            break;
        case ViewMode::FitHeight:
            scale = contentHeight / static_cast<float>(imageH);
            break;
        case ViewMode::Manual:
            scale = manualZoom;
            break;
    }
    return std::clamp(scale, minZoom, maxZoom);
}

D2D1_RECT_F DestinationRect(ViewMode mode, float manualZoom, float panX, float panY,
                            UINT imageW, UINT imageH, float clientWidth,
                            float contentHeight, float topInset, float minZoom, float maxZoom) {
    clientWidth = (std::max)(1.0f, clientWidth);
    contentHeight = (std::max)(1.0f, contentHeight);
    if (imageW == 0 || imageH == 0) return D2D1::RectF(0, 0, clientWidth, contentHeight);
    const float scale = FitScale(mode, manualZoom, imageW, imageH, clientWidth, contentHeight, minZoom, maxZoom);
    const float dw = static_cast<float>(imageW) * scale;
    const float dh = static_cast<float>(imageH) * scale;
    float left = (clientWidth - dw) * 0.5f;
    float top = topInset + (contentHeight - dh) * 0.5f;
    if (mode == ViewMode::Manual) {
        left += panX;
        top += panY;
    }
    if (mode == ViewMode::Manual && std::fabs(scale - 1.0f) < 0.0001f &&
        std::fabs(panX) < 0.0001f && std::fabs(panY) < 0.0001f) {
        left = std::round(left);
        top = std::round(top);
    }
    return D2D1::RectF(left, top, left + dw, top + dh);
}

void ClampManualPan(ViewMode mode, float zoom, float& panX, float& panY,
                    UINT imageW, UINT imageH, float clientWidth,
                    float contentHeight, float topInset) {
    if (mode != ViewMode::Manual || imageW == 0 || imageH == 0) return;
    clientWidth = (std::max)(1.0f, clientWidth);
    contentHeight = (std::max)(1.0f, contentHeight);
    const float dw = static_cast<float>(imageW) * zoom;
    const float dh = static_cast<float>(imageH) * zoom;
    const float baseLeft = (clientWidth - dw) * 0.5f;
    const float baseTop = topInset + (contentHeight - dh) * 0.5f;
    if (dw <= clientWidth) panX = 0.0f;
    else panX = std::clamp(panX, clientWidth - dw - baseLeft, -baseLeft);
    if (dh <= contentHeight) panY = 0.0f;
    else panY = std::clamp(panY, contentHeight - dh - baseTop, -baseTop);
}

ZoomAtResult ZoomAtPoint(float oldScale, const D2D1_RECT_F& oldDestination,
                         UINT imageW, UINT imageH, float clientWidth, float clientHeight,
                         float sx, float sy, float factor, float minZoom, float maxZoom) {
    ZoomAtResult result{};
    if (imageW == 0 || imageH == 0 || oldScale <= 0.0f || factor <= 0.0f) return result;
    if (sx < oldDestination.left || sx > oldDestination.right || sy < oldDestination.top || sy > oldDestination.bottom) {
        sx = clientWidth * 0.5f;
        sy = clientHeight * 0.5f;
    }
    const float imageX = (sx - oldDestination.left) / oldScale;
    const float imageY = (sy - oldDestination.top) / oldScale;
    result.zoom = std::clamp(oldScale * factor, minZoom, maxZoom);
    const float centeredLeft = (clientWidth - static_cast<float>(imageW) * result.zoom) * 0.5f;
    const float centeredTop = (clientHeight - static_cast<float>(imageH) * result.zoom) * 0.5f;
    result.panX = sx - imageX * result.zoom - centeredLeft;
    result.panY = sy - imageY * result.zoom - centeredTop;
    return result;
}

} // namespace glide_view
