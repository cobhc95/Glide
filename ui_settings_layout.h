#pragma once

#ifndef NOMINMAX
#define NOMINMAX
#endif
#ifndef WIN32_LEAN_AND_MEAN
#define WIN32_LEAN_AND_MEAN
#endif
#include <windows.h>
#include <cstddef>
#include <vector>

namespace GlideSettingsLayout {

inline constexpr int kSettingsRailWidth = 302;
inline constexpr int kSettingsContentLeft = 322;
inline constexpr int kSettingsContentTop = 148;
inline constexpr int kSettingsFooterHeight = 110;
inline constexpr int kSettingsSourceBaseY = 130;
inline constexpr int kSettingsSourcePageSpan = 380;
inline constexpr int kSettingsSearchRowsPerPage = 9;

struct SettingsLayoutNode {
    HWND hwnd{};
    int cat{}, id{}, x{}, y{}, w{}, h{};
};

struct SettingsNormalRow {
    int anchorY{}, minX{};
    std::vector<size_t> nodes;
};

int SettingsContentBottom(HWND wnd);
int SettingsFooterTop(HWND wnd);
void MoveSettingsControl(HWND wnd, int id, int x, int y, int w, int h);
int SettingsSectionPage(HWND wnd);
void SetSettingsSectionPage(HWND wnd, int page);
std::vector<SettingsLayoutNode> CollectSettingsLayoutNodes(HWND wnd);
std::vector<SettingsNormalRow> BuildSettingsNormalRows(const std::vector<SettingsLayoutNode>& nodes, int cat, int page);
int SettingsNormalPageCount(const std::vector<SettingsLayoutNode>& nodes, int cat);
int SettingsVisualHeight(const SettingsLayoutNode& node);

} // namespace GlideSettingsLayout
