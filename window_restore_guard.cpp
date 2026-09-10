#define NOMINMAX
#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include "window_restore_guard.h"
#include <algorithm>
#include <cwchar>

namespace {

constexpr wchar_t kGlideViewerClass[] = L"GlideViewerWindow";

bool IsGlideViewer(HWND hwnd) noexcept {
    if (!hwnd || !IsWindow(hwnd) || GetAncestor(hwnd, GA_ROOT) != hwnd) return false;
    wchar_t cls[64]{};
    if (!GetClassNameW(hwnd, cls, static_cast<int>(sizeof(cls) / sizeof(cls[0])))) return false;
    return std::wcscmp(cls, kGlideViewerClass) == 0;
}

void RepairLayeredPresentation(HWND hwnd) noexcept {
    const LONG_PTR ex = GetWindowLongPtrW(hwnd, GWL_EXSTYLE);
    if ((ex & WS_EX_LAYERED) == 0) return;

    COLORREF colorKey{};
    BYTE alpha = 255;
    DWORD flags{};
    if (!GetLayeredWindowAttributes(hwnd, &colorKey, &alpha, &flags)) return;

    // Glide's session-opacity control is clamped to 10..100%, so an active
    // LWA_ALPHA state with alpha==0 is never legitimate application state.
    // Preserve every valid user-selected alpha and PNG colour-key state.
    if ((flags & LWA_ALPHA) != 0 && alpha == 0) alpha = 255;
    SetLayeredWindowAttributes(hwnd, colorKey, alpha, flags);
}

void RecoverIfOffscreen(HWND hwnd) noexcept {
    RECT r{};
    if (!GetWindowRect(hwnd, &r)) return;
    if (r.right <= r.left || r.bottom <= r.top) return;

    // Do nothing when any monitor intersects the actual window rectangle.
    if (MonitorFromRect(&r, MONITOR_DEFAULTTONULL)) return;

    HMONITOR monitor = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
    if (!monitor) return;
    MONITORINFO mi{};
    mi.cbSize = sizeof(mi);
    if (!GetMonitorInfoW(monitor, &mi)) return;

    const int width = r.right - r.left;
    const int height = r.bottom - r.top;
    const int workWidth = mi.rcWork.right - mi.rcWork.left;
    const int workHeight = mi.rcWork.bottom - mi.rcWork.top;

    // Preserve size. Put enough of an oversized window on-screen to make it
    // recoverable rather than silently resizing a remembered layout.
    int x = mi.rcWork.left + 24;
    int y = mi.rcWork.top + 24;
    if (width <= workWidth) x = std::clamp(r.left, mi.rcWork.left, mi.rcWork.right - width);
    if (height <= workHeight) y = std::clamp(r.top, mi.rcWork.top, mi.rcWork.bottom - height);

    SetWindowPos(hwnd, nullptr, x, y, 0, 0,
                 SWP_NOZORDER | SWP_NOACTIVATE | SWP_NOSIZE | SWP_NOOWNERZORDER);
}

void RepairRestoredWindow(HWND hwnd) noexcept {
    if (!IsGlideViewer(hwnd) || IsIconic(hwnd) || !IsWindowVisible(hwnd)) return;
    RepairLayeredPresentation(hwnd);
    RecoverIfOffscreen(hwnd);
    RedrawWindow(hwnd, nullptr, nullptr,
                 RDW_INVALIDATE | RDW_FRAME | RDW_ALLCHILDREN);
}

void CALLBACK RestoreEventProc(HWINEVENTHOOK, DWORD event, HWND hwnd,
                               LONG idObject, LONG idChild, DWORD, DWORD) {
    if (idObject != OBJID_WINDOW || idChild != CHILDID_SELF) return;
    if (event == EVENT_SYSTEM_MINIMIZEEND || event == EVENT_SYSTEM_FOREGROUND)
        RepairRestoredWindow(hwnd);
}

class RestoreGuard final {
public:
    RestoreGuard() noexcept {
        const DWORD pid = GetCurrentProcessId();
        foregroundHook_ = SetWinEventHook(EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND,
                                          nullptr, RestoreEventProc, pid, 0,
                                          WINEVENT_OUTOFCONTEXT);
        minimizeHook_ = SetWinEventHook(EVENT_SYSTEM_MINIMIZESTART, EVENT_SYSTEM_MINIMIZEEND,
                                        nullptr, RestoreEventProc, pid, 0,
                                        WINEVENT_OUTOFCONTEXT);
    }
    ~RestoreGuard() {
        if (foregroundHook_) UnhookWinEvent(foregroundHook_);
        if (minimizeHook_) UnhookWinEvent(minimizeHook_);
    }
    RestoreGuard(const RestoreGuard&) = delete;
    RestoreGuard& operator=(const RestoreGuard&) = delete;
private:
    HWINEVENTHOOK foregroundHook_{};
    HWINEVENTHOOK minimizeHook_{};
};

RestoreGuard gRestoreGuard;

} // namespace


bool GlideRepairRestoredWindowNow(HWND hwnd) noexcept {
    if (!hwnd || !IsWindow(hwnd)) return false;
    RepairRestoredWindow(hwnd);
    return IsWindow(hwnd) && !IsIconic(hwnd) && IsWindowVisible(hwnd);
}
