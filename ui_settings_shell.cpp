#include "ui_settings_shell.h"

#include "ui_settings_ids.h"
#include "ui_settings_layout.h"

#include <algorithm>
#include <string>

namespace GlideSettingsShell {
using namespace GlideSettingsLayout;

void UpdateSettingsPageHeader(HWND wnd) {
    static const wchar_t* titles[]={L"General",L"Viewing & Interface",L"Mouse & Fullscreen",L"Performance & Startup",L"Status Bar",L"Slideshow",L"Hotkeys",L"Tabs & Workspace",L"Window in Window",L"Profiles & Presets",L"Windows Integration",L"Developer Options"};
    static const wchar_t* subs[]={
        L"Application behavior, history, navigation and startup preferences",
        L"Appearance, image presentation, fullscreen chrome and theme",
        L"Mouse gestures, selection, zoom, panning and fullscreen interaction",
        L"Cold-start, decode quality, prefetch, refinement and memory controls",
        L"Choose the controls and information shown on Glide's status surface",
        L"Timing, looping, shuffling and cross-folder slideshow behavior",
        L"Search, sort and customize every keyboard shortcut",
        L"Tab sizing, detach/attach behavior and closed-tab history",
        L"Overlay controls, opacity, remembered folders and Window-in-Window behavior",
        L"Whole-program presets, three user profiles and portable import/export",
        L"Native Windows integration and Open With registration",
        L"Diagnostics and developer-only troubleshooting tools"};
    wchar_t q[128]{}; GetDlgItemTextW(wnd,IDC_SET_SEARCH,q,127);
    HWND t=GetDlgItem(wnd,IDC_SET_PAGE_TITLE), st=GetDlgItem(wnd,IDC_SET_PAGE_SUBTITLE);
    if(q[0]){SetWindowTextW(t,L"Search results");std::wstring msg=L"Showing settings matching ‘";msg+=q;msg+=L"’";SetWindowTextW(st,msg.c_str());return;}
    int cat=std::clamp(static_cast<int>(GetWindowLongPtrW(wnd,GWLP_USERDATA)),0,11);
    SetWindowTextW(t,titles[cat]);SetWindowTextW(st,subs[cat]);
}

void LayoutSettingsChrome(HWND wnd){
    // Transparent static controls (notably the version label) do not erase
    // their previous pixels when moved.  Remember the whole footer before
    // changing geometry so a resize cannot leave old text painted through a
    // newly positioned action button.
    RECT oldClient{};GetClientRect(wnd,&oldClient);const int oldFooter=SettingsFooterTop(wnd);
    RECT oldFooterRect{0,std::max(0,oldFooter-2),oldClient.right,oldClient.bottom};
    RECT r{};GetClientRect(wnd,&r);const int right=std::max(kSettingsContentLeft+500,static_cast<int>(r.right));const int footer=SettingsFooterTop(wnd);
    MoveSettingsControl(wnd,IDC_SET_SEARCH,kSettingsContentLeft,23,std::max(360,right-kSettingsContentLeft-50),30);
    MoveSettingsControl(wnd,IDC_SET_PAGE_TITLE,kSettingsContentLeft,76,std::max(360,right-kSettingsContentLeft-50),34);
    MoveSettingsControl(wnd,IDC_SET_PAGE_SUBTITLE,kSettingsContentLeft,110,std::max(360,right-kSettingsContentLeft-50),24);
    MoveSettingsControl(wnd,IDC_SET_SECTION_PREV,32,footer-58,42,34);
    MoveSettingsControl(wnd,IDC_SET_SECTION_LABEL,82,footer-51,138,22);
    MoveSettingsControl(wnd,IDC_SET_SECTION_NEXT,236,footer-58,42,34);
    MoveSettingsControl(wnd,IDC_SET_VERSION_LABEL,22,footer+16,280,22);
    MoveSettingsControl(wnd,IDC_SET_DEFAULTS,22,footer+50,180,38);
    const int okW=90,gap=10;const int okX=right-38-okW;const int applyX=okX-gap-okW;const int cancelX=applyX-gap-okW;
    MoveSettingsControl(wnd,IDC_SET_OK,okX,footer+50,okW,38);MoveSettingsControl(wnd,IDC_SET_APPLY,applyX,footer+50,okW,38);MoveSettingsControl(wnd,IDC_SET_CANCEL,cancelX,footer+50,okW,38);
    const int hintW=310;const int hintX=std::max(kSettingsContentLeft+80,right-hintW-38);
    MoveSettingsControl(wnd,IDC_SET_SCROLL_HINT,hintX,footer+16,hintW,22);
    // A compact horizontal pager makes the existing whole-page navigation
    // discoverable without restoring the old fragile child-window scrolling.
    // Leave deliberate breathing room after the last setting row; this is an
    // indicator/navigation affordance, not a border glued to the content.
    const int pagerHeight=12;
    const int pagerGapAboveFooter=10;
    const int pagerY=std::max(kSettingsContentTop,footer-pagerGapAboveFooter-pagerHeight);
    MoveSettingsControl(wnd,IDC_SET_SECTION_SCROLL,kSettingsContentLeft,pagerY,
                        std::max(180,right-kSettingsContentLeft-38),pagerHeight);

    // The category rail is independently scrollable.  This keeps every tab
    // reachable when a user makes Settings short, without moving controls into
    // the fixed footer or forcing an artificially tall minimum window.
    constexpr int railTop=98, railStep=48, railButtonH=40, railCount=12;
    const int railBottom=std::max(railTop+railButtonH,footer-12);
    const int railViewport=std::max(railButtonH,railBottom-railTop);
    const int railContent=(railCount-1)*railStep+railButtonH;
    const int railMax=std::max(0,railContent-railViewport);
    int railOffset=std::clamp(static_cast<int>(reinterpret_cast<INT_PTR>(GetPropW(wnd,L"GlideSettingsRailScroll"))),0,railMax);
    SetPropW(wnd,L"GlideSettingsRailScroll",reinterpret_cast<HANDLE>(static_cast<INT_PTR>(railOffset)));
    if(HWND bar=GetDlgItem(wnd,IDC_SET_RAIL_SCROLL)){
        MoveSettingsControl(wnd,IDC_SET_RAIL_SCROLL,kSettingsRailWidth-18,railTop,14,railViewport);
        SCROLLINFO si{};si.cbSize=sizeof(si);si.fMask=SIF_RANGE|SIF_PAGE|SIF_POS;si.nMin=0;si.nMax=std::max(0,railContent-1);si.nPage=static_cast<UINT>(railViewport);si.nPos=railOffset;SetScrollInfo(bar,SB_CTL,&si,TRUE);
        ShowWindow(bar,railMax>0?SW_SHOW:SW_HIDE);
    }
    const int categoryIds[railCount]={IDC_SET_CAT_GENERAL,IDC_SET_CAT_VIEWING,IDC_SET_CAT_MOUSE,IDC_SET_CAT_PERFORMANCE,IDC_SET_CAT_STATUS,IDC_SET_CAT_SLIDESHOW,IDC_SET_CAT_HOTKEYS,IDC_SET_CAT_TABS,IDC_SET_CAT_OVERLAYS,IDC_SET_CAT_PROFILES,IDC_SET_CAT_WINDOWS,IDC_SET_CAT_DEVELOPER};
    for(int i=0;i<railCount;++i){
        const int id=categoryIds[i];const int y=railTop+i*railStep-railOffset;
        MoveSettingsControl(wnd,id,18,y,railMax>0?247:265,railButtonH);
        if(HWND button=GetDlgItem(wnd,id))ShowWindow(button,(y+railButtonH>railTop&&y<railBottom)?SW_SHOW:SW_HIDE);
    }
    RECT newFooterRect{0,std::max(0,footer-2),r.right,r.bottom};RECT dirty{};UnionRect(&dirty,&oldFooterRect,&newFooterRect);
    // Request an erasing parent repaint after every footer reflow.  This is
    // intentionally broader than invalidating individual children: child
    // statics can be transparent and otherwise retain stale glyph pixels.
    InvalidateRect(wnd,&dirty,TRUE);
    RedrawWindow(wnd,&dirty,nullptr,RDW_INVALIDATE|RDW_ERASE|RDW_ALLCHILDREN);
}

void UpdateSettingsSectionNav(HWND wnd,int page,int pages,bool searchMode){
    page=std::clamp(page,0,std::max(0,pages-1));HWND prev=GetDlgItem(wnd,IDC_SET_SECTION_PREV),next=GetDlgItem(wnd,IDC_SET_SECTION_NEXT),lab=GetDlgItem(wnd,IDC_SET_SECTION_LABEL);
    if(prev){EnableWindow(prev,page>0);ShowWindow(prev,pages>1?SW_SHOW:SW_HIDE);}if(next){EnableWindow(next,page+1<pages);ShowWindow(next,pages>1?SW_SHOW:SW_HIDE);}
    if(lab){if(pages>1){std::wstring t=(searchMode?L"Results ":L"Section ")+std::to_wstring(page+1)+L" / "+std::to_wstring(pages);SetWindowTextW(lab,t.c_str());ShowWindow(lab,SW_SHOW);}else ShowWindow(lab,SW_HIDE);}
    if(HWND hint=GetDlgItem(wnd,IDC_SET_SCROLL_HINT)){if(pages>1&&!searchMode){SetWindowTextW(hint,L"●  Scroll / ‹ › for more settings");ShowWindow(hint,SW_SHOW);}else ShowWindow(hint,SW_HIDE);}
    if(HWND bar=GetDlgItem(wnd,IDC_SET_SECTION_SCROLL)){
        SCROLLINFO si{};si.cbSize=sizeof(si);si.fMask=SIF_RANGE|SIF_PAGE|SIF_POS;si.nMin=0;si.nMax=std::max(0,pages-1);si.nPage=1;si.nPos=page;SetScrollInfo(bar,SB_CTL,&si,TRUE);
        ShowWindow(bar,pages>1&&!searchMode?SW_SHOW:SW_HIDE);
    }
    ShowScrollBar(wnd,SB_VERT,FALSE);
}

void SelectExclusiveRadio(HWND wnd, int firstId, int lastId, int selectedId){
    if(!wnd || selectedId<firstId || selectedId>lastId)return;
    for(int id=firstId;id<=lastId;++id){
        if(HWND c=GetDlgItem(wnd,id)){
            SendMessageW(c,BM_SETCHECK,id==selectedId?BST_CHECKED:BST_UNCHECKED,0);
            InvalidateRect(c,nullptr,FALSE);
        }
    }
}

int ExclusiveRadioSelection(HWND wnd, int firstId, int lastId){
    if(!wnd)return -1;int selected=-1,count=0;
    for(int id=firstId;id<=lastId;++id){
        if(HWND c=GetDlgItem(wnd,id);c&&SendMessageW(c,BM_GETCHECK,0,0)==BST_CHECKED){selected=id;++count;}
    }
    return count==1?selected:-1;
}

} // namespace GlideSettingsShell
