#pragma once

#ifndef NOMINMAX
#define NOMINMAX
#endif
#ifndef WIN32_LEAN_AND_MEAN
#define WIN32_LEAN_AND_MEAN
#endif
#include <windows.h>

namespace GlideSettingsShell {

void UpdateSettingsPageHeader(HWND wnd);
void LayoutSettingsChrome(HWND wnd);
void UpdateSettingsSectionNav(HWND wnd, int page, int pages, bool searchMode);

// Custom-drawn radio buttons still use normal Win32 check state, but Glide
// explicitly normalizes the group and repaints every member so visual state
// and persisted state can never diverge.
void SelectExclusiveRadio(HWND wnd, int firstId, int lastId, int selectedId);
int ExclusiveRadioSelection(HWND wnd, int firstId, int lastId);

} // namespace GlideSettingsShell
