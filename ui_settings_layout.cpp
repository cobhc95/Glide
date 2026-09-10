#include "ui_settings_layout.h"

#include <commctrl.h>
#include <algorithm>
#include <utility>

namespace GlideSettingsLayout {

int SettingsContentBottom(HWND wnd) {
    RECT r{};
    GetClientRect(wnd, &r);
    return std::max(kSettingsContentTop + 180, static_cast<int>(r.bottom) - kSettingsFooterHeight);
}

int SettingsFooterTop(HWND wnd) {
    RECT r{};
    GetClientRect(wnd, &r);
    return std::max(kSettingsContentTop + 200, static_cast<int>(r.bottom) - kSettingsFooterHeight + 2);
}

void MoveSettingsControl(HWND wnd, int id, int x, int y, int w, int h) {
    if (HWND c = GetDlgItem(wnd, id)) {
        SetWindowPos(c, nullptr, x, y, std::max(1, w), std::max(1, h), SWP_NOZORDER | SWP_NOACTIVATE);
    }
}

int SettingsSectionPage(HWND wnd) {
    HANDLE h = GetPropW(wnd, L"GlideSettingsSectionPage");
    return h ? std::max(0, static_cast<int>(reinterpret_cast<INT_PTR>(h)) - 1) : 0;
}

void SetSettingsSectionPage(HWND wnd, int page) {
    SetPropW(wnd, L"GlideSettingsSectionPage",
             reinterpret_cast<HANDLE>(static_cast<INT_PTR>(std::max(0, page) + 1)));
}

std::vector<SettingsLayoutNode> CollectSettingsLayoutNodes(HWND wnd) {
    std::vector<SettingsLayoutNode> out;
    for (HWND c = GetWindow(wnd, GW_CHILD); c; c = GetWindow(c, GW_HWNDNEXT)) {
        const INT_PTR tok = reinterpret_cast<INT_PTR>(GetPropW(c, L"GlideCat"));
        if (tok <= 0) continue;
        SettingsLayoutNode n{};
        n.hwnd = c;
        n.cat = static_cast<int>(tok - 1);
        n.id = GetDlgCtrlID(c);
        n.x = static_cast<int>(reinterpret_cast<INT_PTR>(GetPropW(c, L"GlideOX"))) - 1;
        n.y = static_cast<int>(reinterpret_cast<INT_PTR>(GetPropW(c, L"GlideOY"))) - 1;
        n.w = static_cast<int>(reinterpret_cast<INT_PTR>(GetPropW(c, L"GlideOW"))) - 1;
        n.h = static_cast<int>(reinterpret_cast<INT_PTR>(GetPropW(c, L"GlideOH"))) - 1;
        out.push_back(n);
    }
    std::sort(out.begin(), out.end(), [](const SettingsLayoutNode& a, const SettingsLayoutNode& b) {
        if (a.cat != b.cat) return a.cat < b.cat;
        if (a.y != b.y) return a.y < b.y;
        return a.x < b.x;
    });
    return out;
}

std::vector<SettingsNormalRow> BuildSettingsNormalRows(const std::vector<SettingsLayoutNode>& nodes,
                                                        int cat, int page) {
    const int sourceTop = kSettingsSourceBaseY + page * kSettingsSourcePageSpan;
    const int sourceBottom = sourceTop + kSettingsSourcePageSpan;
    std::vector<SettingsNormalRow> rows;
    for (size_t i = 0; i < nodes.size(); ++i) {
        const auto& n = nodes[i];
        if (n.cat != cat || n.y < sourceTop || n.y >= sourceBottom) continue;
        if (rows.empty() || n.y - rows.back().anchorY > 14) {
            SettingsNormalRow r{};
            r.anchorY = n.y;
            r.minX = n.x;
            rows.push_back(std::move(r));
        }
        auto& r = rows.back();
        r.nodes.push_back(i);
        r.minX = std::min(r.minX, n.x);
    }
    return rows;
}

int SettingsNormalPageCount(const std::vector<SettingsLayoutNode>& nodes, int cat) {
    // Page membership is defined by each logical row's source Y anchor (the same
    // rule used by BuildSettingsNormalRows). Including control height here can make
    // a checkbox/combo that merely crosses a source-page boundary create a trailing
    // section with no rows at all.
    int maxAnchorY = kSettingsSourceBaseY;
    bool found = false;
    for (const auto& n : nodes) {
        if (n.cat != cat) continue;
        found = true;
        maxAnchorY = std::max(maxAnchorY, n.y);
    }
    if (!found) return 1;
    return std::max(1, 1 + std::max(0, maxAnchorY - kSettingsSourceBaseY) / kSettingsSourcePageSpan);
}

int SettingsVisualHeight(const SettingsLayoutNode& n) {
    wchar_t cls[40]{};
    GetClassNameW(n.hwnd, cls, 39);
    if (_wcsicmp(cls, L"COMBOBOX") == 0) return 30;
    if (_wcsicmp(cls, WC_LISTVIEWW) == 0) return n.h;
    if (_wcsicmp(cls, L"EDIT") == 0) return std::min(n.h, 30);
    if (_wcsicmp(cls, L"BUTTON") == 0) return std::min(n.h, 40);
    return std::min(n.h, 80);
}

} // namespace GlideSettingsLayout
