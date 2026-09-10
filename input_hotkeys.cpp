#include "input_hotkeys.h"

namespace GlideHotkeys {

const std::array<HotkeyDef, 67> kHotkeyDefs = {{

    {L"OpenFile", L"Open file", HotkeyAction::OpenFile, 0, HotkeyChord('O',true)},
    {L"OpenFolder", L"Open folder", HotkeyAction::OpenFolder, 0, HotkeyChord('O',true,true)},
    {L"Settings", L"Open Settings", HotkeyAction::Settings, 0, HotkeyChord(VK_OEM_COMMA,true)},
    {L"Refresh", L"Refresh current folder", HotkeyAction::Refresh, 0, HotkeyChord('R',true)},
    {L"Slideshow", L"Start / pause / resume slideshow", HotkeyAction::ToggleSlideshow, 0, HotkeyChord('S')},
    {L"StopSlideshow", L"Stop slideshow session", HotkeyAction::StopSlideshow, 0, HotkeyChord('S',false,true)},
    {L"Metadata", L"Toggle image information", HotkeyAction::ToggleMetadata, 0, HotkeyChord('I')},
    {L"RotateLeft", L"Rotate view left", HotkeyAction::RotateLeft, 0, HotkeyChord(VK_OEM_4)},
    {L"RotateRight", L"Rotate view right", HotkeyAction::RotateRight, 0, HotkeyChord(VK_OEM_6)},
    {L"NextSpace", L"Next image (Space)", HotkeyAction::NextImage, 0, HotkeyChord(VK_SPACE)},
    {L"NextRight", L"Next image (Right Arrow)", HotkeyAction::NextImage, 0, HotkeyChord(VK_RIGHT)},
    {L"NextPageDown", L"Next image (Page Down)", HotkeyAction::NextImage, 0, HotkeyChord(VK_NEXT)},
    {L"PreviousBackspace", L"Previous image (Backspace)", HotkeyAction::PreviousImage, 0, HotkeyChord(VK_BACK)},
    {L"PreviousLeft", L"Previous image (Left Arrow)", HotkeyAction::PreviousImage, 0, HotkeyChord(VK_LEFT)},
    {L"PreviousPageUp", L"Previous image (Page Up)", HotkeyAction::PreviousImage, 0, HotkeyChord(VK_PRIOR)},
    {L"JumpNext10", L"Jump forward 10 images", HotkeyAction::JumpNext10, 0, HotkeyChord(VK_RIGHT,true)},
    {L"JumpPrevious10", L"Jump back 10 images", HotkeyAction::JumpPrevious10, 0, HotkeyChord(VK_LEFT,true)},
    {L"FirstImage", L"First image in folder", HotkeyAction::FirstImage, 0, HotkeyChord(VK_HOME)},
    {L"LastImage", L"Last image in folder", HotkeyAction::LastImage, 0, HotkeyChord(VK_END)},
    {L"FitImageF", L"Fit image", HotkeyAction::FitImage, 0, HotkeyChord('F')},
    {L"FitImage0", L"Fit image (0)", HotkeyAction::FitImage, 0, HotkeyChord('0')},
    {L"FitWidth", L"Fit width", HotkeyAction::FitWidth, 0, HotkeyChord('W')},
    {L"FitHeight", L"Fit height", HotkeyAction::FitHeight, 0, HotkeyChord('H')},
    {L"ActualSize", L"Actual size / 100%", HotkeyAction::ActualSize, 0, HotkeyChord('1')},
    {L"ToggleFit100", L"Toggle Fit / 100%", HotkeyAction::ToggleFit100, 0, 0},
    {L"ZoomIn", L"Zoom in", HotkeyAction::ZoomIn, 0, HotkeyChord(VK_OEM_PLUS,false,true)},
    {L"ZoomInNumpad", L"Zoom in (numpad +)", HotkeyAction::ZoomIn, 0, HotkeyChord(VK_ADD)},
    {L"ZoomOut", L"Zoom out", HotkeyAction::ZoomOut, 0, HotkeyChord(VK_OEM_MINUS)},
    {L"ZoomOutNumpad", L"Zoom out (numpad -)", HotkeyAction::ZoomOut, 0, HotkeyChord(VK_SUBTRACT)},
    {L"DeleteImage", L"Delete current image to Recycle Bin", HotkeyAction::DeleteImage, 0, HotkeyChord(VK_DELETE)},
    {L"Escape", L"Escape / leave fullscreen / close", HotkeyAction::EscapeAction, 0, HotkeyChord(VK_ESCAPE)},
    {L"Fullscreen", L"Toggle fullscreen", HotkeyAction::ToggleFullscreen, 0, HotkeyChord(VK_F11)},
    {L"FullscreenEnter", L"Toggle fullscreen (Enter)", HotkeyAction::ToggleFullscreen, 0, HotkeyChord(VK_RETURN)},
    {L"External1", L"Open current image with external program 1", HotkeyAction::OpenExternal, 0, HotkeyChord('1',false,true)},
    {L"External2", L"Open current image with external program 2", HotkeyAction::OpenExternal, 1, HotkeyChord('2',false,true)},
    {L"External3", L"Open current image with external program 3", HotkeyAction::OpenExternal, 2, HotkeyChord('3',false,true)},
    {L"NewTab", L"New tab", HotkeyAction::NewTab, 0, HotkeyChord('T',true)},
    {L"CloseTab", L"Close current tab", HotkeyAction::CloseTab, 0, HotkeyChord('W',true)},
    {L"CloseTabF4", L"Close current tab (Ctrl+F4)", HotkeyAction::CloseTab, 0, HotkeyChord(VK_F4,true)},
    {L"ReopenTab", L"Reopen last closed tab", HotkeyAction::ReopenClosedTab, 0, HotkeyChord('T',true,true)},
    {L"DuplicateTab", L"Duplicate current tab", HotkeyAction::DuplicateTab, 0, HotkeyChord('K',true,true)},
    {L"NextTab", L"Next tab", HotkeyAction::NextTab, 0, HotkeyChord(VK_TAB,true)},
    {L"PreviousTab", L"Previous tab", HotkeyAction::PreviousTab, 0, HotkeyChord(VK_TAB,true,true)},
    {L"NextTabPageDown", L"Next tab (Ctrl+Page Down)", HotkeyAction::NextTab, 0, HotkeyChord(VK_NEXT,true)},
    {L"PreviousTabPageUp", L"Previous tab (Ctrl+Page Up)", HotkeyAction::PreviousTab, 0, HotkeyChord(VK_PRIOR,true)},
    {L"Tab1", L"Switch to tab 1", HotkeyAction::SwitchTab, 0, HotkeyChord('1',true)},
    {L"Tab2", L"Switch to tab 2", HotkeyAction::SwitchTab, 1, HotkeyChord('2',true)},
    {L"Tab3", L"Switch to tab 3", HotkeyAction::SwitchTab, 2, HotkeyChord('3',true)},
    {L"Tab4", L"Switch to tab 4", HotkeyAction::SwitchTab, 3, HotkeyChord('4',true)},
    {L"Tab5", L"Switch to tab 5", HotkeyAction::SwitchTab, 4, HotkeyChord('5',true)},
    {L"Tab6", L"Switch to tab 6", HotkeyAction::SwitchTab, 5, HotkeyChord('6',true)},
    {L"Tab7", L"Switch to tab 7", HotkeyAction::SwitchTab, 6, HotkeyChord('7',true)},
    {L"Tab8", L"Switch to tab 8", HotkeyAction::SwitchTab, 7, HotkeyChord('8',true)},
    {L"LastTab", L"Switch to last tab", HotkeyAction::SwitchTab, -1, HotkeyChord('9',true)},
    {L"NewWindow", L"New Glide window", HotkeyAction::NewWindow, 0, HotkeyChord('N',true)},
    {L"DetachTab", L"Move current tab to new window", HotkeyAction::DetachTab, 0, 0},
    {L"MoveTabLeft", L"Move current tab left", HotkeyAction::MoveTabLeft, 0, HotkeyChord(VK_PRIOR,true,true)},
    {L"MoveTabRight", L"Move current tab right", HotkeyAction::MoveTabRight, 0, HotkeyChord(VK_NEXT,true,true)},
    {L"AddOverlay", L"Add Window-in-Window overlay", HotkeyAction::AddOverlay, 0, HotkeyChord('O',false,true)},
    {L"ToggleOverlays", L"Show / hide all overlays", HotkeyAction::ToggleOverlays, 0, HotkeyChord('O',true,false,true)},
    {L"SaveOverlayLayout", L"Save overlay layout", HotkeyAction::SaveOverlayLayout, 0, HotkeyChord('S',true,false,true)},
    {L"LoadOverlayLayout", L"Load overlay layout", HotkeyAction::LoadOverlayLayout, 0, HotkeyChord('L',true,false,true)},
    {L"ClearOverlays", L"Clear all overlays", HotkeyAction::ClearOverlays, 0, HotkeyChord(VK_DELETE,true,false,true)},
    {L"CloseWindow", L"Close Glide window", HotkeyAction::CloseWindow, 0, HotkeyChord('W',true,true)},
    {L"ToggleStatusBar", L"Show / hide status bar", HotkeyAction::ToggleStatusBar, 0, HotkeyChord('B',true,true)},
    {L"ToggleStatusCollapsed", L"Collapse / expand status bar", HotkeyAction::ToggleStatusCollapsed, 0, HotkeyChord('B',true)},
    {L"NextSettingsCategory", L"Next Settings tab", HotkeyAction::NextSettingsCategory, 0, HotkeyChord(VK_TAB)}
}};

std::wstring HotkeyName(DWORD chord) {
    if (!chord) return L"Unassigned";
    std::wstring s;
    if (chord & HK_CTRL) s += L"Ctrl+";
    if (chord & HK_SHIFT) s += L"Shift+";
    if (chord & HK_ALT) s += L"Alt+";
    UINT vk=static_cast<UINT>(chord & HK_KEYMASK);
    switch(vk){
        case VK_LBUTTON:s+=L"Mouse Left";break; case VK_RBUTTON:s+=L"Mouse Right";break; case VK_MBUTTON:s+=L"Mouse Middle";break;
        case VK_XBUTTON1:s+=L"Mouse X1";break; case VK_XBUTTON2:s+=L"Mouse X2";break;
        case VK_TAB:s+=L"Tab";break; case VK_RETURN:s+=L"Enter";break; case VK_ESCAPE:s+=L"Esc";break;
        case VK_SPACE:s+=L"Space";break; case VK_BACK:s+=L"Backspace";break; case VK_DELETE:s+=L"Delete";break;
        case VK_HOME:s+=L"Home";break; case VK_END:s+=L"End";break; case VK_PRIOR:s+=L"Page Up";break; case VK_NEXT:s+=L"Page Down";break;
        case VK_LEFT:s+=L"Left Arrow";break; case VK_RIGHT:s+=L"Right Arrow";break; case VK_UP:s+=L"Up Arrow";break; case VK_DOWN:s+=L"Down Arrow";break;
        case VK_ADD:s+=L"Num +";break; case VK_SUBTRACT:s+=L"Num -";break; case VK_OEM_PLUS:s+=L"+";break; case VK_OEM_MINUS:s+=L"-";break;
        case VK_OEM_COMMA:s+=L",";break; case VK_OEM_4:s+=L"[";break; case VK_OEM_6:s+=L"]";break;
        case VK_F1:case VK_F2:case VK_F3:case VK_F4:case VK_F5:case VK_F6:case VK_F7:case VK_F8:case VK_F9:case VK_F10:case VK_F11:case VK_F12:
            s += L"F" + std::to_wstring(vk-VK_F1+1); break;
        default:
            if ((vk>='A'&&vk<='Z')||(vk>='0'&&vk<='9')) s.push_back(static_cast<wchar_t>(vk));
            else { wchar_t name[64]{}; UINT sc=MapVirtualKeyW(vk,MAPVK_VK_TO_VSC)<<16; if(GetKeyNameTextW(static_cast<LONG>(sc),name,63)>0)s+=name; else s+=L"Key "+std::to_wstring(vk); }
            break;
    }
    return s;
}

const wchar_t* HotkeyCategory(HotkeyAction action) {
    switch(action) {
        case HotkeyAction::OpenFile: case HotkeyAction::OpenFolder: case HotkeyAction::Refresh:
        case HotkeyAction::DeleteImage: return L"File";
        case HotkeyAction::NextImage: case HotkeyAction::PreviousImage: case HotkeyAction::JumpNext10:
        case HotkeyAction::JumpPrevious10: case HotkeyAction::FirstImage: case HotkeyAction::LastImage:
            return L"Navigation";
        case HotkeyAction::FitImage: case HotkeyAction::FitWidth: case HotkeyAction::FitHeight:
        case HotkeyAction::ActualSize: case HotkeyAction::ToggleFit100: case HotkeyAction::ZoomIn:
        case HotkeyAction::ZoomOut: case HotkeyAction::RotateLeft: case HotkeyAction::RotateRight:
        case HotkeyAction::ToggleMetadata: return L"Viewing";
        case HotkeyAction::ToggleFullscreen: case HotkeyAction::EscapeAction: return L"Fullscreen";
        case HotkeyAction::ToggleSlideshow: case HotkeyAction::StopSlideshow: return L"Slideshow";
        case HotkeyAction::NewTab: case HotkeyAction::CloseTab: case HotkeyAction::ReopenClosedTab:
        case HotkeyAction::DuplicateTab: case HotkeyAction::NextTab: case HotkeyAction::PreviousTab:
        case HotkeyAction::SwitchTab: case HotkeyAction::DetachTab: case HotkeyAction::MoveTabLeft:
        case HotkeyAction::MoveTabRight: return L"Tabs";
        case HotkeyAction::AddOverlay: case HotkeyAction::ToggleOverlays: case HotkeyAction::SaveOverlayLayout:
        case HotkeyAction::LoadOverlayLayout: case HotkeyAction::ClearOverlays: return L"Overlays";
        case HotkeyAction::OpenExternal: return L"External programs";
        case HotkeyAction::Settings: case HotkeyAction::NewWindow: case HotkeyAction::CloseWindow: case HotkeyAction::ToggleStatusBar: case HotkeyAction::ToggleStatusCollapsed: return L"Application";
        case HotkeyAction::NextSettingsCategory: return L"Settings";
    }
    return L"Other";
}

} // namespace GlideHotkeys
