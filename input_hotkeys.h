#pragma once

#ifndef NOMINMAX
#define NOMINMAX
#endif
#ifndef WIN32_LEAN_AND_MEAN
#define WIN32_LEAN_AND_MEAN
#endif
#include <windows.h>
#include <array>
#include <string>

namespace GlideHotkeys {

enum class HotkeyAction {
    OpenFile, OpenFolder, Settings, Refresh, ToggleSlideshow, StopSlideshow, ToggleMetadata,
    RotateLeft, RotateRight, NextImage, PreviousImage, JumpNext10, JumpPrevious10,
    FirstImage, LastImage, FitImage, FitWidth, FitHeight, ActualSize, ToggleFit100,
    ZoomIn, ZoomOut, DeleteImage, EscapeAction, ToggleFullscreen,
    NewTab, CloseTab, ReopenClosedTab, DuplicateTab, NextTab, PreviousTab,
    SwitchTab, NewWindow, DetachTab, MoveTabLeft, MoveTabRight,
    AddOverlay, ToggleOverlays, SaveOverlayLayout, LoadOverlayLayout, ClearOverlays, OpenExternal, CloseWindow, ToggleStatusBar, ToggleStatusCollapsed, NextSettingsCategory
};

inline constexpr DWORD HK_CTRL  = 0x00010000u;
inline constexpr DWORD HK_SHIFT = 0x00020000u;
inline constexpr DWORD HK_ALT   = 0x00040000u;
inline constexpr DWORD HK_KEYMASK = 0x0000FFFFu;
inline constexpr DWORD HotkeyChord(UINT vk, bool ctrl=false, bool shift=false, bool alt=false) {
    return static_cast<DWORD>(vk) | (ctrl?HK_CTRL:0) | (shift?HK_SHIFT:0) | (alt?HK_ALT:0);
}

struct HotkeyDef {
    const wchar_t* iniKey;
    const wchar_t* label;
    HotkeyAction action;
    int parameter;
    DWORD defaultChord;
};

extern const std::array<HotkeyDef, 67> kHotkeyDefs;
std::wstring HotkeyName(DWORD chord);
const wchar_t* HotkeyCategory(HotkeyAction action);

} // namespace GlideHotkeys
