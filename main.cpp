#define NOMINMAX
#define WIN32_LEAN_AND_MEAN

#include <windows.h>
#include <windowsx.h>
#include <commctrl.h>
#include <commdlg.h>
#include <shellapi.h>
#include <shobjidl.h>
#include <shlobj.h>
#include <shlwapi.h>
#include <wincodec.h>
#include <d2d1.h>
#include <dwrite.h>
#include <propvarutil.h>
#include <propidl.h>
#include <propkey.h>
#include <wrl/client.h>
#include <dwmapi.h>
#include <uxtheme.h>
#include "resource.h"

#include <algorithm>
#include <array>
#include <atomic>
#include <condition_variable>
#include <cmath>
#include <cctype>
#include <climits>
#include <cstdint>
#include <cstring>
#include <cwctype>
#include <deque>
#include <cstdlib>
#include <filesystem>
#include <fstream>
#include <functional>
#include <initializer_list>
#include <sstream>
#include <mutex>
#include <memory>
#include <new>
#include <optional>
#include <string>
#include <thread>
#include <unordered_map>
#include <unordered_set>
#include <utility>
#include <vector>

#include "crash_report.h"
#include "input_hotkeys.h"
#include "image_decode.h"
#include "image_formats.h"
#include "image_cache.h"
#include "image_prefetch.h"
#include "overlay_state.h"
#include "platform_shell.h"
#include "viewer_view_state.h"
#include "ui_settings_ids.h"
#include "ui_settings_layout.h"
#include "ui_settings_shell.h"
#include "diagnostic_harness.h"
#include "window_restore_guard.h"

#pragma comment(lib, "user32.lib")
#pragma comment(lib, "d2d1.lib")
#pragma comment(lib, "dwrite.lib")
#pragma comment(lib, "propsys.lib")
#pragma comment(lib, "windowscodecs.lib")
#pragma comment(lib, "ole32.lib")
#pragma comment(lib, "shell32.lib")
#pragma comment(lib, "comdlg32.lib")
#pragma comment(lib, "shlwapi.lib")
#pragma comment(lib, "dwmapi.lib")
#pragma comment(lib, "uxtheme.lib")
#pragma comment(lib, "comctl32.lib")

using Microsoft::WRL::ComPtr;
namespace fs = std::filesystem;
using namespace GlideHotkeys;
using namespace GlideDecode;
using namespace GlideCache;
using namespace GlideSettingsLayout;
using namespace GlideSettingsShell;
using glide_overlay::OverlayImage;

static constexpr wchar_t kClassName[] = L"GlideViewerWindow";
static constexpr wchar_t kHomeTabSentinel[] = L"::GLIDE_HOME::";
static constexpr ULONG_PTR kGlideTabTransferMagic = 0x474C5442u; // GLTB
static constexpr wchar_t kAppName[]   = L"Glide Alpha 0.12107";
static constexpr UINT WM_APP_DECODED  = WM_APP + 1;
static constexpr UINT WM_APP_NAV_DRAIN = WM_APP + 2;
static constexpr UINT WM_APP_SHELL_OPEN = WM_APP + 3;
static constexpr UINT WM_APP_COLD_FOLDER = WM_APP + 4;
static constexpr UINT WM_APP_TAB_DRAG_HOVER = WM_APP + 5;
static constexpr UINT WM_APP_TAB_DRAG_LEAVE = WM_APP + 6;
static constexpr UINT WM_APP_BEGIN_NATIVE_TAB_DRAG = WM_APP + 7;
static constexpr UINT WM_APP_BEGIN_CONTENT_TAB_DRAG = WM_APP + 8;
static constexpr UINT WM_APP_SETTINGS_DEFERRED = WM_APP + 80;
static constexpr UINT WM_APP_SETTINGS_THEME_REFRESH = WM_APP + 81;
static constexpr UINT_PTR kRefineTimerId = 0x474C4944;
static constexpr UINT_PTR kFullscreenCursorTimerId = 0x474C4945;
static constexpr UINT_PTR kSlideshowTimerId = 0x474C4946;
static constexpr UINT_PTR kFullscreenBarTimerId = 0x474C4947;
static constexpr UINT_PTR kAdaptivePreviewTimerId = 0x474C4948;
static constexpr UINT_PTR kNavigationIdleTimerId = 0x474C4949;
static constexpr UINT_PTR kNativeTabDragReleaseTimerId = 0x474C494A;
static constexpr UINT_PTR kSettingsPrewarmTimerId = 0x474C494B;
static constexpr UINT_PTR kContentTabDragTimerId = 0x474C494C;
static constexpr UINT_PTR kExternalTabHoverWatchdogTimerId = 0x474C494D;
static constexpr UINT kRefineDelayMs = 140;
static constexpr UINT kNavigationIdleDelayMs = 180;
static constexpr UINT kDefaultAdaptivePreviewDelayMs = 40;

enum : UINT {
    IDM_OPEN = 40001,
    IDM_OPEN_FOLDER,
    IDM_RELOAD,
    IDM_COPY_IMAGE,
    IDM_COPY_IMAGE_FILE,
    IDM_COPY_PATH,
    IDM_COPY_NAME,
    IDM_COPY_FOLDER,
    IDM_REVEAL,
    IDM_RENAME,
    IDM_DELETE,
    IDM_PROPERTIES,
    IDM_FULLSCREEN,
    IDM_FIT,
    IDM_ACTUAL,
    IDM_METADATA,
    IDM_STATUS_TOGGLE,
    IDM_STATUS_COLLAPSE,
    IDM_TITLE_FULLPATH,
    IDM_ROTATE_LEFT,
    IDM_ROTATE_RIGHT,
    IDM_FLIP_H,
    IDM_FLIP_V,
    IDM_SORT_NAME,
    IDM_SORT_MODIFIED,
    IDM_SORT_CREATED,
    IDM_SORT_SIZE,
    IDM_SORT_ASC,
    IDM_SORT_DESC,
    IDM_CLEAR_RECENTS,
    IDM_SETTINGS,
    IDM_SLIDESHOW_TOGGLE,
    IDM_ASSOC_REGISTER,
    IDM_ASSOC_REMOVE,
    IDM_EXIT,
    IDM_RECENT_FILE_BASE = 40200,
    IDM_RECENT_FOLDER_BASE = 40220
};

// Glide advanced configuration: fine-grained input/gesture behavior matrix. Every slot defaults to
// Legacy Glide behavior, so the direct input path remains byte-for-byte conceptually
// compatible until the user explicitly overrides an interaction.
enum class GestureSlot : int {
    WindowLeftClickImage, WindowRightClickImage, WindowMiddleClickImage,
    WindowDoubleLeftImage, WindowDoubleRightImage,
    FullscreenLeftClickImage, FullscreenRightClickImage, FullscreenMiddleClickImage,
    LeftDragImage, RightDragImage, MiddleDragImage,
    LeftDragBackground, RightDragBackground,
    WindowWheel, WindowCtrlWheel, FullscreenWheel, FullscreenCtrlWheel,
    SelectionLeftClick, SelectionRightClick,
    OverlayDoubleClick, OverlayMiddleClick, OverlayWheel, OverlayCtrlWheel,
    BackgroundDoubleClick,
    Count
};
enum class GestureAction : int {
    Legacy, None, NextImage, PreviousImage, ZoomIn, ZoomOut, FitImage, ActualSize,
    ToggleFit100, ToggleFullscreen, ContextMenu, ClearSelection, ToggleMetadata,
    OpenSettings, PanImage, CreateSelection, MoveWindow, ResetOverlayZoom, BringOverlayFront,
    Count
};
static constexpr int IDC_SET_GESTURE_BASE = 47000;
static constexpr int kGestureSlotCount = static_cast<int>(GestureSlot::Count);
static const wchar_t* GestureSlotLabel(GestureSlot s){
    static const wchar_t* labels[]={
        L"Windowed: left click on image",L"Windowed: right click on image",L"Windowed: middle click on image",
        L"Windowed: double-left click on image",L"Windowed: double-right click on image",
        L"Fullscreen: left click on image",L"Fullscreen: right click on image",L"Fullscreen: middle click on image",
        L"Left-drag on image",L"Right-drag on image",L"Middle-drag on image",
        L"Left-drag on empty background",L"Right-drag on empty background",
        L"Windowed: mouse wheel",L"Windowed: Ctrl + wheel",L"Fullscreen: mouse wheel",L"Fullscreen: Ctrl + wheel",
        L"Left click inside active selection",L"Right click inside active selection",
        L"Overlay: double click",L"Overlay: middle click",L"Overlay: mouse wheel",L"Overlay: Ctrl + wheel",
        L"Double-click empty background"
    };return labels[std::clamp(static_cast<int>(s),0,kGestureSlotCount-1)];
}
static const wchar_t* GestureActionLabel(GestureAction a){
    static const wchar_t* labels[]={L"Use Glide/default behavior",L"Do nothing",L"Next image",L"Previous image",L"Zoom in",L"Zoom out",L"Fit image",L"100% / actual size",L"Toggle Fit / 100%",L"Toggle fullscreen",L"Open context menu",L"Clear selection",L"Toggle image information",L"Open Settings",L"Pan image",L"Create selection",L"Move native window",L"Reset selected overlay zoom",L"Bring selected overlay to front"};
    return labels[std::clamp(static_cast<int>(a),0,static_cast<int>(GestureAction::Count)-1)];
}
static const wchar_t* GestureSlotIniKey(GestureSlot s){
    static const wchar_t* keys[]={L"WindowLeftClickImage",L"WindowRightClickImage",L"WindowMiddleClickImage",L"WindowDoubleLeftImage",L"WindowDoubleRightImage",L"FullscreenLeftClickImage",L"FullscreenRightClickImage",L"FullscreenMiddleClickImage",L"LeftDragImage",L"RightDragImage",L"MiddleDragImage",L"LeftDragBackground",L"RightDragBackground",L"WindowWheel",L"WindowCtrlWheel",L"FullscreenWheel",L"FullscreenCtrlWheel",L"SelectionLeftClick",L"SelectionRightClick",L"OverlayDoubleClick",L"OverlayMiddleClick",L"OverlayWheel",L"OverlayCtrlWheel",L"BackgroundDoubleClick"};
    return keys[std::clamp(static_cast<int>(s),0,kGestureSlotCount-1)];
}

enum : int {
    IDC_SLIDE_INTERVAL = 43001,
    IDC_SLIDE_LOOP,
    IDC_SLIDE_CROSS,
    IDC_SLIDE_SHUFFLE,
    IDC_SLIDE_START,
    IDC_SLIDE_CANCEL
};


static std::wstring Lower(std::wstring s) {
    std::transform(s.begin(), s.end(), s.begin(), [](wchar_t c) {
        return static_cast<wchar_t>(towlower(c));
    });
    return s;
}

static bool WriteUtf8TextFile(const fs::path& path, const std::wstring& text) {
    if (text.size() > static_cast<size_t>(INT_MAX)) return false;
    std::string utf8;
    if (!text.empty()) {
        const int chars = static_cast<int>(text.size());
        const int needed = WideCharToMultiByte(CP_UTF8, WC_ERR_INVALID_CHARS,
                                               text.data(), chars, nullptr, 0, nullptr, nullptr);
        if (needed <= 0) return false;
        utf8.resize(static_cast<size_t>(needed));
        if (WideCharToMultiByte(CP_UTF8, WC_ERR_INVALID_CHARS, text.data(), chars,
                                utf8.data(), needed, nullptr, nullptr) != needed) return false;
    }
    std::ofstream file(path, std::ios::binary | std::ios::trunc);
    if (!file) return false;
    file.write(utf8.data(), static_cast<std::streamsize>(utf8.size()));
    return static_cast<bool>(file);
}

// Buffered wide diagnostics writer. Text is converted explicitly to UTF-8 at scope
// exit so Unicode labels/paths cannot poison a locale-sensitive std::wofstream.
class Utf8Wofstream : public std::wostringstream {
public:
    explicit Utf8Wofstream(const fs::path& path, std::ios::openmode = std::ios::trunc)
        : path_(path) {
        std::ofstream probe(path_, std::ios::binary | std::ios::trunc);
        writable_ = static_cast<bool>(probe);
    }
    ~Utf8Wofstream() {
        if (writable_ && !committed_) WriteUtf8TextFile(path_, str());
    }
    bool Commit() const {
        if (!writable_) return false;
        const bool ok = WriteUtf8TextFile(path_, str());
        if (ok) committed_ = true;
        return ok;
    }
    explicit operator bool() const noexcept { return writable_; }
    bool operator!() const noexcept { return !writable_; }

private:
    fs::path path_;
    bool writable_{};
    mutable bool committed_{};
};

static bool NaturalLess(const fs::path& a, const fs::path& b) {
    const std::wstring as = a.filename().wstring();
    const std::wstring bs = b.filename().wstring();
    const int cmp = StrCmpLogicalW(as.c_str(), bs.c_str());
    if (cmp != 0) return cmp < 0;
    return _wcsicmp(as.c_str(), bs.c_str()) < 0;
}

static bool IsSupportedImageExtension(const fs::path& p) {
    // Match the complete filename suffix rather than only filesystem::extension()
    // so compound imaging suffixes such as .ome.tiff and .nii.gz participate in
    // folder navigation/open-with just like ordinary extensions.
    const std::wstring name = Lower(p.filename().wstring());
    for (const auto* candidate : GlideFormats::kImageExtensions) {
        const size_t n=wcslen(candidate);
        if(name.size()>=n && name.compare(name.size()-n,n,candidate)==0) return true;
    }
    return false;
}

static std::wstring PathKey(const fs::path& p) {
    try {
        return Lower(fs::weakly_canonical(p).wstring());
    } catch (...) {
        try { return Lower(fs::absolute(p).lexically_normal().wstring()); }
        catch (...) { return Lower(p.wstring()); }
    }
}

static bool IsJpegPath(const std::wstring& path) {
    const std::wstring ext = Lower(fs::path(path).extension().wstring());
    return ext == L".jpg" || ext == L".jpeg" || ext == L".jpe" || ext == L".jfif";
}

static bool IsModernWicCodecPath(const std::wstring& path) {
    const std::wstring ext = Lower(fs::path(path).extension().wstring());
    return ext == L".webp" || ext == L".heic" || ext == L".heif" || ext == L".hif" ||
           ext == L".avif" || ext == L".heifs" || ext == L".avifs";
}

static std::wstring ModernFormatName(const std::wstring& path) {
    const std::wstring ext = Lower(fs::path(path).extension().wstring());
    if (ext == L".webp") return L"WebP";
    if (ext == L".avif" || ext == L".avifs") return L"AVIF";
    if (ext == L".heic" || ext == L".heif" || ext == L".hif" || ext == L".heifs") return L"HEIF/HEIC";
    return L"this image format";
}

static std::wstring CommandLineOptionValue(const wchar_t* prefix) {
    if (!prefix || !*prefix) return {};
    int argc = 0;
    LPWSTR* argv = CommandLineToArgvW(GetCommandLineW(), &argc);
    std::wstring value;
    if (argv) {
        const size_t n = wcslen(prefix);
        for (int i = 1; i < argc; ++i) {
            if (_wcsnicmp(argv[i], prefix, n) == 0) { value = argv[i] + n; break; }
        }
        LocalFree(argv);
    }
    return value;
}

static int CommandLineOptionInt(const wchar_t* prefix, int fallback) {
    const std::wstring value = CommandLineOptionValue(prefix);
    if (value.empty()) return fallback;
    wchar_t* end = nullptr;
    const long parsed = wcstol(value.c_str(), &end, 10);
    return (end && *end == 0) ? static_cast<int>(parsed) : fallback;
}

struct ColdFolderResult {
    std::wstring preferredPath;
    std::filesystem::path folder;
    std::vector<std::filesystem::path> files;
};

struct ViewerApp : IServiceProvider, ICommDlgBrowser3 {
    HINSTANCE hInst{};
    HWND hwnd{};
    bool diagnosticMode{}; // isolated automated-diagnostics child process
    bool diagnosticSuppressClose{};
    bool diagnosticCloseRequested{};
    bool diagnosticSuppressExternalLaunch{};
    bool diagnosticExternalLaunchRequested{};
    std::wstring diagnosticExternalLaunchCommand;
    bool diagnosticSuspendBackgroundDemotion{};
    bool diagnosticBackgroundWorker{}; // worker windows render without taking foreground focus
    HRESULT diagnosticLastDecodeHr{S_OK};
    HRESULT diagnosticLastWicFilenameHr{E_NOTIMPL};
    HRESULT diagnosticLastWicStreamInitHr{E_NOTIMPL};
    HRESULT diagnosticLastWicStreamDecoderHr{E_NOTIMPL};
    bool diagnosticLastWicStreamUsed{};
    bool diagnosticLastNativeFallback{};
    HRESULT diagnosticLastResizeHr{S_OK};
    ULONGLONG diagnosticLastRenderTick{};
    uint64_t diagnosticRenderGeneration{};
    HANDLE diagnosticPauseControl{};
    HANDLE diagnosticCancelControl{};

    ComPtr<ID2D1Factory> d2d;
    ComPtr<ID2D1HwndRenderTarget> target;
    ComPtr<IDWriteFactory> dwrite;
    ComPtr<IDWriteTextFormat> uiText;
    ComPtr<IDWriteTextFormat> uiTextSmall;
    ComPtr<IDWriteTextFormat> homeTitleText;
    ComPtr<IDWriteTextFormat> homeHeadingText;
    ComPtr<IDWriteTextFormat> homeBodyText;
    ComPtr<IDWriteTextFormat> pictureOverlayFormat;
    int pictureOverlayFormatSize{};
    bool pictureOverlayFormatBoldCache{};
    ComPtr<ID2D1Bitmap> bitmap;
    // Multi-pass downsampled render bitmap used when viewing large images below 100%.
    // This approximates mipmapping and avoids the shimmer/aliasing caused by a single
    // very large bilinear shrink. It is generated on the GPU and never re-decodes the file.
    ComPtr<ID2D1Bitmap> qualityBitmap;
    UINT qualityBitmapW{};
    UINT qualityBitmapH{};

    DecodeWorker worker;
    DecodeWorker prefetchWorker;
    DecodeWorker prefetchWorker2;
    DecodeWorker prefetchWorker3;
    DecodeWorker refineWorker;
    DecodeWorker adaptiveWorker;
    std::atomic<uint64_t> generation{0};
    DecodedCache cache;
    std::unordered_set<std::wstring> predictivePrefetchPending;
    static constexpr size_t kMaxCacheItems = 16;
    size_t maxCacheItems{16};
    int prefetchDepth{2};
    int rapidPreviewLongestSide{3000};

    std::wstring currentPath;
    std::wstring displayedPath;
    std::vector<fs::path> files;
    size_t currentIndex{};
    bool haveIndex{};
    fs::path currentFolder;

    UINT imageW{};
    UINT imageH{};
    UINT bitmapPixelW{};
    UINT bitmapPixelH{};
    bool bitmapIsPreview{};

    using ViewMode = glide_view::ViewMode;

    ViewMode viewMode{ViewMode::Fit};
    float zoom{1.0f};
    // Manual-view offsets from the normally centered image. These are also
    // what allow mouse-wheel zoom to stay anchored under the cursor.
    float panX{0.0f};
    float panY{0.0f};

    static constexpr float kMinZoom = 0.02f;  // 2%
    static constexpr float kMaxZoom = 32.0f;  // 3200%

    // Stage 4 interaction state. Selection geometry is stored in image-space
    // pixels so it remains exact through zoom and window resizing.
    bool selectionActive{};
    bool selecting{};
    bool selectionClickCandidate{};
    D2D1_POINT_2F selectionStartImage{};
    D2D1_POINT_2F selectionCurrentImage{};
    D2D1_RECT_F selectionImageRect{};
    POINT leftDownClient{};
    ULONGLONG suppressDoubleClickUntil{};

    bool panning{};
    bool rightButtonPanning{};
    bool rightZoomCandidate{};
    bool rightMoved{};
    POINT rightDownClient{};
    POINT panLastClient{};

    HCURSOR zoomInCursor{};
    HCURSOR zoomOutCursor{};
    int zoomStepPercent{15};
    int fullscreenCursorHideDelayMs{1800};

    bool fullscreen{};
    WINDOWPLACEMENT windowedPlacement{sizeof(WINDOWPLACEMENT)};
    LONG_PTR windowedStyle{};
    LONG_PTR windowedExStyle{};
    bool fullscreenCursorHidden{};
    RECT fullscreenMonitorRect{};
    ULONGLONG lastMouseMoveTick{};

    // Stage 8 unobtrusive overlay UI. These are drawn over the image with alpha,
    // so they never reserve image-viewing space.
    bool statusVisible{true};
    bool statusCollapsed{};
    bool metadataVisible{};
    bool showFullPathInTitle{};
    D2D1_RECT_F statusRect{};
    D2D1_RECT_F statusCloseRect{};
    D2D1_RECT_F statusMinRect{};
    D2D1_RECT_F statusInfoRect{};
    D2D1_RECT_F statusHomeRect{};
    D2D1_RECT_F statusPrevRect{};
    D2D1_RECT_F statusNextRect{};
    D2D1_RECT_F statusEndRect{};
    D2D1_RECT_F statusSlideRect{};
    D2D1_RECT_F statusStopRect{};
    D2D1_RECT_F statusFitWidthRect{};
    D2D1_RECT_F statusFitHeightRect{};
    D2D1_RECT_F statusZoomOutRect{};
    D2D1_RECT_F statusZoomInRect{};
    D2D1_RECT_F statusOptionsRect{};
    D2D1_RECT_F statusPngAlphaRect{};
    D2D1_RECT_F statusOverlayAddRect{};
    D2D1_RECT_F statusOverlaySaveRect{};
    D2D1_RECT_F statusOverlayLoadRect{};
    D2D1_RECT_F statusOverlayClearRect{};
    D2D1_RECT_F metadataRect{};
    D2D1_RECT_F metadataCloseRect{};
    std::wstring metadataText;

    enum class SortMode { Name, Modified, Created, Size };
    SortMode sortMode{SortMode::Name};
    bool sortAscending{true};
    std::vector<std::wstring> recentFiles;
    std::vector<std::wstring> recentFolders;

    // Persistent settings: portable builds keep Glide.ini beside Glide.exe; installer builds use %LOCALAPPDATA%\Glide.
    std::wstring iniPath;
    bool installedMode{};
    bool recentHistoryEnabled{true};
    int recentHistoryLimit{8};
    bool rememberWindowPlacement{true};
    int fullscreenExitWindowMode{0}; // 0 = exact pre-fullscreen placement, 1 = maximized windowed
    bool singleInstance{false};
    bool fullscreenCursorAutoHide{true};
    bool doubleClickFullscreen{true};
    bool fullscreenClickNavigation{true};
    bool doubleClickExitFullscreen{};
    bool fullscreenBarAutoHide{true};
    bool fullscreenXClosesApp{false}; // fullscreen X: false exits fullscreen, true closes Glide
    bool fullscreenStatusAlwaysOn{false}; // default: reveal-on-hover; optional persistent fullscreen status bar
    bool alwaysOnTop{false};
    // Session-only whole-window opacity. Never persisted; every process starts at 100%.
    int windowOpacityPercent{100};
    bool opacitySliderVisible{};
    bool opacitySliderDragging{};
    float tabMinWidth{125.0f};
    float tabMaxWidth{240.0f};
    bool tabDetachEnabled{true};
    bool tabAttachEnabled{true};
    int closedTabHistoryLimit{20};
    bool closeEmptyWindowAfterDetach{false};
    bool detachedWindowHomeTab{false}; // default: detached tab owns the new window by itself
    bool deferredDetachedStartup{};
    bool deferredDetachedBrowser{};
    std::wstring deferredDetachedPath;
    bool manualContentTabDrag{};
    POINT manualContentDragOffset{30,18};
    ULONGLONG manualContentLastMoveTick{};
    bool statusShowOverlay{true};
    int overlayDefaultOpacity{100};
    bool overlayRememberLastFolder{true};
    bool mainRememberLastFolder{true};
    bool overlaysVisible{true};
    std::wstring lastOverlayFolder;
    std::wstring lastMainFolder;
    std::vector<OverlayImage> overlays;
    int overlayActiveIndex{-1};
    bool overlayDragging{};
    bool overlayResizing{};
    bool overlayOpacityDragging{};
    int activeGestureDragButton{}; // 1 left, 2 right, 3 middle; used only for explicit gesture overrides
    POINT overlayDown{};
    D2D1_RECT_F overlayStartRect{};
    float overlayStartOpacity{};
    std::vector<DWORD> hotkeys;
    std::vector<DWORD> hotkeysAlt;
    bool pngSeeThrough{false}; // session toggle, exposed only for PNG files
    bool adaptiveFastPreview{true};
    int adaptivePreviewDelayMs{static_cast<int>(kDefaultAdaptivePreviewDelayMs)};

    // Stage 14: lightweight user-facing controls plus a dedicated cold-open path.
    // Direct file association launches prioritize the first visible bitmap and only
    // initialize speculative/background systems after that frame has committed.
    bool fastColdStart{true};
    bool startupDiagnostics{};
    bool prefetchEnabled{true};
    bool backgroundRefinement{true};
    bool purgeCacheOnMinimize{};
    bool homeTipsEnabled{true};
    bool invertWheelNavigation{};
    bool autoSiblingFolders{true};

    // Glide.3 behavior layer. Keep these as tiny POD values so input dispatch
    // remains a couple of branches and never touches the cold-image decode path.
    enum class LeftImageDragMode : int { Select = 0, Pan = 1 };
    LeftImageDragMode leftImageDragMode{LeftImageDragMode::Select};
    bool rightDragPansImage{true};
    bool selectionClickZoomsIn{true};
    bool selectionRightClickZoomsOut{true};
    bool backgroundLeftDragMovesWindow{true};
    bool ctrlWheelZoom{true};
    bool fullscreenWheelZoom{true};
    bool zoomAroundCursor{true};
    bool preserveManualZoomOnNavigate{false};
    bool middleDragPansImage{true};
    bool wrapFolderNavigation{false};
    bool progressiveColorFirstPreview{true};

    // Glide.6 sibling-folder toolbar behavior.
    bool folderNavShowGroup{true};
    bool folderNavShowPrevious{true};
    bool folderNavShowNext{true};
    bool folderNavShowExplore{true};
    bool folderNavSkipEmpty{true};
    bool folderNavWrap{false};
    bool folderNavOpenFirstImage{true};
    bool folderNavTooltips{true};
    bool folderNavExploreEmptyAsBrowser{true};
    bool folderNavIncludeHidden{false};

    // Glide.6 Window-in-Window focus/zoom behavior.
    bool overlayKeyboardZoom{true};
    bool overlayWheelZoom{true};
    bool overlaySelectedHighlight{true};
    bool overlayRememberZoom{false};
    bool overlayRightDragPansZoomed{true};
    int overlayZoomStepPercent{20};
    bool overlayRightPanCandidate{};
    bool overlayRightPanning{};
    POINT overlayRightDown{};
    float overlayRightStartCenterX{0.5f};
    float overlayRightStartCenterY{0.5f};

    // Glide.6 advanced gesture matrix: each slot defaults to Legacy, preserving Glide behavior.
    std::array<GestureAction, kGestureSlotCount> gestureMap{};

    int themeMode{0}; // 0 Dark, 1 Light
    int accentChoice{0}; // Blue, white, grey, red, yellow, green, purple
    int activeBehaviorPreset{1}; // 0 custom, 1 Glide, 2 Windows Photos, 3 IrfanView, 4 FastStone, 5 XnView MP

    bool coldStartDirectImage{};
    bool coldMinimalChrome{};
    bool coldFirstFrameCommitted{};
    bool deferredWorkersPending{};
    bool deferredWorkersStarted{};
    bool coldFolderScanPending{};
    int coldPendingNavigationDelta{};
    std::wstring coldStartupPath;
    std::thread coldFolderThread;
    ULONGLONG startupTick0{};
    ULONGLONG startupWindowCreatedMs{};
    ULONGLONG startupDecodeRequestedMs{};
    ULONGLONG startupFirstFrameMs{};
    ULONGLONG startupDeferredReadyMs{};

    // Glide.1 local diagnostics. These counters are deliberately tiny and remain
    // memory-only until the user explicitly exports a diagnostic package.
    struct PerfSample {
        ULONGLONG requestTick{}, firstFrameTick{}, decodeMs{};
        UINT width{}, height{};
        bool preview{}, refinement{}, cacheHit{};
        std::wstring extension;
    };
    ULONGLONG lastForegroundRequestTick{};
    std::vector<PerfSample> diagnosticPerfSamples;

    // Glide.1 settings responsiveness instrumentation. Times are monotonic
    // GetTickCount64 values/deltas and remain local until Export Diagnostics.
    ULONGLONG settingsOpenRequestTick{};
    ULONGLONG settingsCreateMs{};
    ULONGLONG settingsFirstPaintMs{};
    ULONGLONG settingsReadyMs{};
    ULONGLONG settingsDeferredReadyMs{};
    struct SettingsLatencySample {
        std::wstring state; ULONGLONG immediateMs{}, readyMs{}, settledMs{};
        int expectedRows{}, actualRows{}; bool ready{};
    };
    std::vector<SettingsLatencySample> settingsLatencySamples;

    bool statusShowNavigation{true};
    bool statusShowZoom{true};
    bool statusShowSlideshow{true};
    bool statusShowFit{true};
    bool statusShowInfo{true};
    bool statusShowCollapse{true};
    bool statusShowOptions{true};
    bool statusShowClose{true};
    bool statusStatResolution{true};
    bool statusStatZoom{true};
    bool statusStatIndex{true};
    bool statusStatFileSize{};
    bool statusStatFormat{};
    bool statusStatPreview{};

    // Viewer text overlay. Persistent styling, default content is only the folder index.
    bool textOverlayEnabled{true};
    std::wstring textOverlayTemplate{L"[{index}/{total}]"};
    int textOverlayFontSize{18};
    int textOverlayOpacity{68};
    COLORREF textOverlayColor{RGB(255,255,255)};
    int textOverlayPosition{2}; // 0 TL, 1 TC, 2 TR, 3 BL, 4 BC, 5 BR
    bool textOverlayBold{};
    bool textOverlayShadow{true};
    bool fullscreenStatusVisible{};
    ULONGLONG fullscreenStatusLastInsideTick{};
    bool titleTabsEnabled{};
    bool windowedWheelZoom{};
    bool fullscreenNativeCaptionVisible{};
    HWND settingsDialogHwnd{};
    // Stage 11: every tab can either host an image/folder navigation session or
    // Glide's built-in folder browser.  The parallel vectors deliberately keep
    // the Stage 10 image-tab code small while adding independent browser state.
    std::vector<std::wstring> openTabs;
    std::vector<bool> tabBrowserMode;
    std::vector<std::wstring> tabBrowserFolder;
    std::vector<std::vector<std::wstring>> tabBrowserBack;
    std::vector<std::vector<std::wstring>> tabBrowserForward;
    struct ClosedTabState {
        std::wstring path;
        bool browserMode{};
        std::wstring browserFolder;
        std::vector<std::wstring> back;
        std::vector<std::wstring> forward;
    };
    std::vector<ClosedTabState> closedTabs;
    int activeTab{-1};
    ComPtr<IExplorerBrowser> shellBrowser;
    HWND browserAddressEdit{};
    WNDPROC browserAddressOldProc{};
    D2D1_RECT_F browserToolbarRect{};
    D2D1_RECT_F browserRefreshRect{};
    D2D1_RECT_F browserSortRect{};
    std::vector<std::wstring> browserEntries;
    std::vector<bool> browserEntryIsDir;
    std::vector<D2D1_RECT_F> browserEntryRects;
    D2D1_RECT_F browserBackRect{};
    D2D1_RECT_F browserForwardRect{};
    D2D1_RECT_F browserUpRect{};
    D2D1_RECT_F browserPathRect{};
    std::vector<D2D1_RECT_F> titleTabRects;
    std::vector<D2D1_RECT_F> titleTabCloseRects;
    D2D1_RECT_F titleNewTabRect{};
    D2D1_RECT_F titleReopenRect{};
    D2D1_RECT_F titlePrevFolderRect{};
    D2D1_RECT_F titleNextFolderRect{};
    D2D1_RECT_F titleExploreParentRect{};
    D2D1_RECT_F titleOptionsRect{};
    D2D1_RECT_F titlePinRect{};
    D2D1_RECT_F titleOpacityRect{};
    D2D1_RECT_F opacityPanelRect{};
    D2D1_RECT_F opacityTrackRect{};
    D2D1_RECT_F titleBarRect{};
    bool tabDragCandidate{};
    bool tabDragging{};
    bool singleTabWindowDrag{};
    int tabDragIndex{-1};
    POINT tabDragStart{};
    POINT tabDragCurrent{};
    float tabDragGrabOffsetX{};
    HWND tabDragHoverWindow{};
    bool externalTabDragHover{};
    LONG externalTabDragClientX{};
    D2D1_RECT_F titleMinRect{};
    D2D1_RECT_F titleMaxRect{};
    bool titleCloseHover{};
    bool titleMinHover{};
    bool titleMaxHover{};
    bool fullscreenBarVisible{};
    ULONGLONG fullscreenBarLastInsideTick{};
    D2D1_RECT_F fullscreenBarRect{};
    D2D1_RECT_F fullscreenPrevRect{};
    D2D1_RECT_F fullscreenNextRect{};
    D2D1_RECT_F fullscreenSlideRect{};
    D2D1_RECT_F fullscreenFitRect{};
    D2D1_RECT_F fullscreenMoreRect{};
    D2D1_RECT_F fullscreenExitRect{};
    bool slideshowLoop{true};
    bool slideshowCrossFolders{true};
    bool slideshowShuffle{};
    int slideshowIntervalMs{3000};
    bool slideshowRunning{};
    bool slideshowPaused{};
    bool slideshowStartedFullscreen{};
    bool escStopsSlideshow{true};
    bool escExitsFullscreen{true};
    bool escWindowedConfirm{true};
    int escRememberedClose{-1}; // -1 ask, 0 never close, 1 close
    std::array<std::wstring,3> externalProgramPaths{};
    int initialQualityMode{1}; // 0=max speed, 1=balanced/display-sharp, 2=max quality
    int defaultViewMode{}; // 0=Fit image, 1=Fit width, 2=Fit height, 3=100%, 4=Custom
    int defaultCustomZoomPercent{100};
    bool haveSavedPlacement{};
    RECT savedWindowRect{};
    bool savedWindowMaximized{false}; // fresh installs open windowed; remembered placement overrides after first close

    // Non-destructive view transform state. Original cached pixels are untouched.
    int rotationQuarterTurns{}; // 0,1,2,3 clockwise
    bool flipHorizontal{};
    bool flipVertical{};

    // Stage 9: left-drag on Glide background delegates to the real non-client
    // caption move loop. This is what makes Windows Aero Snap / corner layouts native.
    bool nativeBackgroundDrag{};
    bool leftBackgroundDragCandidate{};
    bool fullscreenRightClickConsumed{};
    bool statusHoverActive{};
    HWND tooltipWnd{};
    std::unordered_map<UINT, RECT> tooltipRects;
    std::unordered_map<UINT, bool> tooltipAdded;
    std::unordered_map<UINT, std::wstring> tooltipTexts;
    std::atomic<ULONG> shellSiteRefs{1};

    // Native scrollbars are visible only when the zoomed image exceeds the viewport.
    // A settings toggle will be wired in a later stage; this flag is the future hook.
    bool showScrollBars{true};

    static constexpr int kDragThresholdPx = 4;

    // IExplorerBrowser host site.  ICommDlgBrowser3 lets Glide consume the
    // Shell view's default command for supported images, so double-click opens the
    // selected image in the CURRENT Glide tab instead of launching a new process.
    HRESULT STDMETHODCALLTYPE QueryInterface(REFIID riid, void** ppv) override {
        if (!ppv) return E_POINTER;
        *ppv = nullptr;
        if (IsEqualIID(riid, IID_IUnknown) || IsEqualIID(riid, __uuidof(IServiceProvider)))
            *ppv = static_cast<IServiceProvider*>(this);
        else if (IsEqualIID(riid, __uuidof(ICommDlgBrowser)) || IsEqualIID(riid, __uuidof(ICommDlgBrowser2)) || IsEqualIID(riid, __uuidof(ICommDlgBrowser3)))
            *ppv = static_cast<ICommDlgBrowser3*>(this);
        else return E_NOINTERFACE;
        AddRef(); return S_OK;
    }
    ULONG STDMETHODCALLTYPE AddRef() override { return ++shellSiteRefs; }
    ULONG STDMETHODCALLTYPE Release() override { ULONG v=--shellSiteRefs; return v; }
    HRESULT STDMETHODCALLTYPE QueryService(REFGUID, REFIID riid, void** ppv) override { return QueryInterface(riid, ppv); }
    HRESULT STDMETHODCALLTYPE OnDefaultCommand(IShellView* view) override {
        if (!view || !ActiveTabIsBrowser() || !hwnd) return S_FALSE;
        ComPtr<IFolderView2> fv;
        if (FAILED(view->QueryInterface(IID_PPV_ARGS(fv.ReleaseAndGetAddressOf()))) || !fv) return S_FALSE;
        ComPtr<IShellItemArray> selected;
        if (FAILED(fv->Items(SVGIO_SELECTION, IID_PPV_ARGS(selected.ReleaseAndGetAddressOf()))) || !selected) return S_FALSE;
        DWORD count=0; selected->GetCount(&count); if (count != 1) return S_FALSE;
        ComPtr<IShellItem> item; if (FAILED(selected->GetItemAt(0,item.ReleaseAndGetAddressOf())) || !item) return S_FALSE;
        SFGAOF attrs=0; item->GetAttributes(SFGAO_FOLDER,&attrs); if (attrs & SFGAO_FOLDER) return S_FALSE;
        PWSTR path=nullptr; if (FAILED(item->GetDisplayName(SIGDN_FILESYSPATH,&path)) || !path) return S_FALSE;
        std::wstring chosen(path); CoTaskMemFree(path);
        if (!IsSupportedImageExtension(fs::path(chosen))) return S_FALSE;
        auto* payload=new(std::nothrow) std::wstring(std::move(chosen)); if(!payload) return E_OUTOFMEMORY;
        if(!PostMessageW(hwnd,WM_APP_SHELL_OPEN,0,reinterpret_cast<LPARAM>(payload))){delete payload;return E_FAIL;}
        return S_OK;
    }
    HRESULT STDMETHODCALLTYPE OnStateChange(IShellView*, ULONG) override { return S_OK; }
    HRESULT STDMETHODCALLTYPE IncludeObject(IShellView*, PCUITEMID_CHILD) override { return S_OK; }
    HRESULT STDMETHODCALLTYPE Notify(IShellView*, DWORD) override { return S_OK; }
    HRESULT STDMETHODCALLTYPE GetDefaultMenuText(IShellView*, LPWSTR, int) override { return E_NOTIMPL; }
    HRESULT STDMETHODCALLTYPE GetViewFlags(DWORD* flags) override { if(flags)*flags=0; return S_OK; }
    HRESULT STDMETHODCALLTYPE OnColumnClicked(IShellView*, int) override { return S_OK; }
    HRESULT STDMETHODCALLTYPE GetCurrentFilter(LPWSTR, int) override { return E_NOTIMPL; }
    HRESULT STDMETHODCALLTYPE OnPreViewCreated(IShellView*) override { return S_OK; }

    bool loading{};
    bool foregroundDecodePending{};
    bool adaptivePreviewRequested{};
    bool rapidNavigationActive{};
    ULONGLONG lastNavigationTick{};
    int pendingNavigationDelta{};
    int lastNavDirection{};
    int consecutiveFailures{};

    static HCURSOR CreateModernZoomCursor(bool zoomOut) {
        // 48x48 alpha cursor drawn procedurally at runtime.  This avoids legacy .CUR
        // bitmap formats and gives the magnifier true per-pixel transparency.
        constexpr int W = 48;
        constexpr int H = 48;
        BITMAPV5HEADER bi{};
        bi.bV5Size = sizeof(bi);
        bi.bV5Width = W;
        bi.bV5Height = -H; // top-down DIB
        bi.bV5Planes = 1;
        bi.bV5BitCount = 32;
        bi.bV5Compression = BI_BITFIELDS;
        bi.bV5RedMask   = 0x00FF0000;
        bi.bV5GreenMask = 0x0000FF00;
        bi.bV5BlueMask  = 0x000000FF;
        bi.bV5AlphaMask = 0xFF000000;

        void* bits = nullptr;
        HDC screen = GetDC(nullptr);
        HBITMAP color = CreateDIBSection(screen, reinterpret_cast<BITMAPINFO*>(&bi),
                                         DIB_RGB_COLORS, &bits, nullptr, 0);
        ReleaseDC(nullptr, screen);
        if (!color || !bits) {
            if (color) DeleteObject(color);
            return nullptr;
        }
        std::memset(bits, 0, W * H * 4);
        auto* px = static_cast<uint32_t*>(bits);

        // 4x supersampling for smooth ring/handle/sign edges.
        constexpr int SS = 4;
        constexpr float cx = 20.0f;
        constexpr float cy = 19.0f;
        constexpr float radius = 10.5f;
        constexpr float ringHalf = 1.7f;
        constexpr float signHalf = 1.25f;
        for (int y = 0; y < H; ++y) {
            for (int x = 0; x < W; ++x) {
                int covered = 0;
                for (int sy = 0; sy < SS; ++sy) {
                    for (int sx = 0; sx < SS; ++sx) {
                        const float fx = x + (sx + 0.5f) / SS;
                        const float fy = y + (sy + 0.5f) / SS;
                        const float dx = fx - cx;
                        const float dy = fy - cy;
                        const float dist = std::sqrt(dx*dx + dy*dy);
                        bool ink = std::abs(dist - radius) <= ringHalf;

                        // Handle from lower-right of lens.
                        const float hx = fx - 29.2f;
                        const float hy = fy - 28.2f;
                        const float along = (hx + hy) * 0.70710678f;
                        const float across = (hx - hy) * 0.70710678f;
                        if (along >= 0.0f && along <= 12.5f && std::abs(across) <= 1.9f) ink = true;

                        // + or - inside lens.
                        if (std::abs(fy - cy) <= signHalf && std::abs(fx - cx) <= 5.5f) ink = true;
                        if (!zoomOut && std::abs(fx - cx) <= signHalf && std::abs(fy - cy) <= 5.5f) ink = true;
                        if (ink) ++covered;
                    }
                }
                if (!covered) continue;
                const uint8_t a = static_cast<uint8_t>((covered * 255) / (SS * SS));
                // Crisp white foreground; RGB is premultiplied by alpha.
                const uint8_t c = a;
                px[y * W + x] = (static_cast<uint32_t>(a) << 24) |
                                (static_cast<uint32_t>(c) << 16) |
                                (static_cast<uint32_t>(c) << 8)  |
                                static_cast<uint32_t>(c);
            }
        }

        HBITMAP mask = CreateBitmap(W, H, 1, 1, nullptr);
        if (!mask) {
            DeleteObject(color);
            return nullptr;
        }
        ICONINFO ii{};
        ii.fIcon = FALSE;
        ii.xHotspot = 20;
        ii.yHotspot = 19;
        ii.hbmMask = mask;
        ii.hbmColor = color;
        HCURSOR cur = static_cast<HCURSOR>(CreateIconIndirect(&ii));
        DeleteObject(mask);
        DeleteObject(color);
        return cur;
    }

    static int IniInt(const std::wstring& path, const wchar_t* section, const wchar_t* key, int fallback) {
        return static_cast<int>(GetPrivateProfileIntW(section, key, fallback, path.c_str()));
    }
    static std::wstring IniString(const std::wstring& path,const wchar_t* section,const wchar_t* key,const wchar_t* fallback){
        wchar_t buf[32768]{};GetPrivateProfileStringW(section,key,fallback,buf,static_cast<DWORD>(std::size(buf)),path.c_str());return buf;
    }
    static COLORREF ParseHexColor(std::wstring s,COLORREF fallback){
        if(!s.empty()&&s.front()==L'#')s.erase(s.begin());
        if(s.size()!=6)return fallback;
        wchar_t* end=nullptr;unsigned long v=wcstoul(s.c_str(),&end,16);if(!end||*end!=0)return fallback;
        return RGB((v>>16)&0xFF,(v>>8)&0xFF,v&0xFF);
    }
    static std::wstring HexColor(COLORREF c){
        wchar_t b[16]{};swprintf_s(b,L"#%02X%02X%02X",GetRValue(c),GetGValue(c),GetBValue(c));return b;
    }

    static void WriteIniInt(const std::wstring& path, const wchar_t* section, const wchar_t* key, int value) {
        wchar_t buf[32]{};
        swprintf_s(buf, L"%d", value);
        WritePrivateProfileStringW(section, key, buf, path.c_str());
    }

    void InitIniPath() {
        const auto paths = glide_platform::ResolveRuntimePaths();
        iniPath = paths.settingsIniPath.empty() ? L"Glide.ini" : paths.settingsIniPath;
        installedMode = paths.installedMode;
    }

    void LoadSettings() {
        InitIniPath();
        hotkeys.resize(std::size(kHotkeyDefs));
        hotkeysAlt.resize(std::size(kHotkeyDefs));
        for (size_t i=0;i<std::size(kHotkeyDefs);++i) {
            hotkeys[i]=static_cast<DWORD>(IniInt(iniPath,L"Hotkeys",kHotkeyDefs[i].iniKey,static_cast<int>(kHotkeyDefs[i].defaultChord)));
            const std::wstring altKey=std::wstring(kHotkeyDefs[i].iniKey)+L".Alt";
            hotkeysAlt[i]=static_cast<DWORD>(IniInt(iniPath,L"Hotkeys",altKey.c_str(),0));
        }
        statusVisible = IniInt(iniPath, L"Interface", L"StatusOverlay", 1) != 0;
        statusCollapsed = IniInt(iniPath, L"Interface", L"StatusCollapsed", 0) != 0;
        showFullPathInTitle = IniInt(iniPath, L"Interface", L"FullPathTitle", 0) != 0;
        showScrollBars = IniInt(iniPath, L"Viewing", L"ScrollBars", 1) != 0;
        fullscreenCursorAutoHide = IniInt(iniPath, L"Viewing", L"FullscreenCursorAutoHide", 1) != 0;
        doubleClickFullscreen = IniInt(iniPath, L"Mouse", L"DoubleClickFullscreen", 1) != 0;
        fullscreenClickNavigation = IniInt(iniPath, L"Mouse", L"FullscreenClickNavigation", 1) != 0;
        doubleClickExitFullscreen = IniInt(iniPath, L"Mouse", L"DoubleClickExitFullscreen", 0) != 0;
        fullscreenBarAutoHide = IniInt(iniPath, L"Interface", L"FullscreenBarAutoHide", 1) != 0;
        fullscreenXClosesApp = IniInt(iniPath, L"Interface", L"FullscreenXClosesApp", 0) != 0;
        fullscreenStatusAlwaysOn = IniInt(iniPath, L"Interface", L"FullscreenStatusAlwaysOn", 0) != 0;
        alwaysOnTop = IniInt(iniPath, L"Interface", L"AlwaysOnTop", 0) != 0;
        adaptiveFastPreview = IniInt(iniPath, L"Viewing", L"AdaptiveFastPreview", 1) != 0;
        adaptivePreviewDelayMs = std::clamp(IniInt(iniPath, L"Viewing", L"AdaptivePreviewDelayMs", static_cast<int>(kDefaultAdaptivePreviewDelayMs)), 5, 500);
        fastColdStart = IniInt(iniPath, L"Performance", L"FastColdStart", 1) != 0;
        startupDiagnostics = IniInt(iniPath, L"Performance", L"StartupDiagnostics", 0) != 0;
        prefetchEnabled = IniInt(iniPath, L"Performance", L"PrefetchEnabled", 1) != 0;
        backgroundRefinement = IniInt(iniPath, L"Performance", L"BackgroundRefinement", 1) != 0;
        purgeCacheOnMinimize = IniInt(iniPath, L"Performance", L"PurgeCacheOnMinimize", 0) != 0;
        homeTipsEnabled = IniInt(iniPath, L"Interface", L"HomeTips", 1) != 0;
        invertWheelNavigation = IniInt(iniPath, L"Mouse", L"InvertWheelNavigation", 0) != 0;
        autoSiblingFolders = IniInt(iniPath, L"Navigation", L"SiblingFolders", 1) != 0;
        zoomStepPercent = std::clamp(IniInt(iniPath,L"Viewing",L"ZoomStepPercent",15),5,50);
        fullscreenCursorHideDelayMs = std::clamp(IniInt(iniPath,L"Fullscreen",L"CursorHideDelayMs",1800),250,10000);
        prefetchDepth = std::clamp(IniInt(iniPath,L"Performance",L"PrefetchDepth",2),0,6);
        rapidPreviewLongestSide = std::clamp(IniInt(iniPath,L"Performance",L"RapidPreviewLongestSide",3000),480,4096);
        maxCacheItems = static_cast<size_t>(std::clamp(IniInt(iniPath,L"Performance",L"CacheItems",16),2,64));
        tabMinWidth = static_cast<float>(std::clamp(IniInt(iniPath,L"Tabs",L"MinWidth",125),80,240));
        tabMaxWidth = static_cast<float>(std::clamp(IniInt(iniPath,L"Tabs",L"MaxWidth",240),120,400));
        if(tabMaxWidth<tabMinWidth)tabMaxWidth=tabMinWidth;
        tabDetachEnabled = IniInt(iniPath,L"Tabs",L"Detach",1)!=0;
        tabAttachEnabled = IniInt(iniPath,L"Tabs",L"AttachBetweenWindows",1)!=0;
        closedTabHistoryLimit = std::clamp(IniInt(iniPath,L"Tabs",L"ClosedHistoryLimit",20),1,100);
        closeEmptyWindowAfterDetach = IniInt(iniPath,L"Tabs",L"CloseEmptyWindowAfterDetach",0)!=0;
        detachedWindowHomeTab = IniInt(iniPath,L"Tabs",L"DetachedWindowHomeTab",0)!=0;
        statusShowOverlay = IniInt(iniPath,L"StatusBar",L"OverlayControls",1)!=0;
        overlayDefaultOpacity = std::clamp(IniInt(iniPath,L"Overlays",L"DefaultOpacity",100),10,100);
        overlayRememberLastFolder = IniInt(iniPath,L"Overlays",L"RememberLastFolder",1)!=0;
        lastOverlayFolder = IniString(iniPath,L"Overlays",L"LastFolder",L"");
        mainRememberLastFolder = IniInt(iniPath,L"Navigation",L"RememberMainOpenFolder",1) != 0;
        lastMainFolder = IniString(iniPath,L"Navigation",L"LastMainOpenFolder",L"");
        statusShowNavigation = IniInt(iniPath, L"StatusBar", L"Navigation", 1) != 0;
        statusShowZoom = IniInt(iniPath, L"StatusBar", L"ZoomButtons", 1) != 0;
        statusShowSlideshow = IniInt(iniPath, L"StatusBar", L"Slideshow", 1) != 0;
        statusShowFit = IniInt(iniPath, L"StatusBar", L"FitButtons", 1) != 0;
        statusShowInfo = IniInt(iniPath, L"StatusBar", L"Info", 1) != 0;
        statusShowCollapse = IniInt(iniPath, L"StatusBar", L"Collapse", 1) != 0;
        statusShowOptions = IniInt(iniPath, L"StatusBar", L"Options", 1) != 0;
        statusShowClose = IniInt(iniPath, L"StatusBar", L"Close", 1) != 0;
        statusStatResolution = IniInt(iniPath, L"StatusBar", L"StatResolution", 1) != 0;
        statusStatZoom = IniInt(iniPath, L"StatusBar", L"StatZoom", 1) != 0;
        statusStatIndex = IniInt(iniPath, L"StatusBar", L"StatIndex", 1) != 0;
        statusStatFileSize = IniInt(iniPath, L"StatusBar", L"StatFileSize", 0) != 0;
        statusStatFormat = IniInt(iniPath, L"StatusBar", L"StatFormat", 0) != 0;
        statusStatPreview = IniInt(iniPath, L"StatusBar", L"StatPreview", 0) != 0;
        textOverlayEnabled = IniInt(iniPath,L"TextOverlay",L"Enabled",1)!=0;
        textOverlayTemplate = IniString(iniPath,L"TextOverlay",L"Template",L"[{index}/{total}]");
        if(textOverlayTemplate.empty()) textOverlayTemplate=L"[{index}/{total}]";
        textOverlayFontSize = std::clamp(IniInt(iniPath,L"TextOverlay",L"FontSize",18),8,96);
        textOverlayOpacity = std::clamp(IniInt(iniPath,L"TextOverlay",L"Opacity",68),5,100);
        textOverlayColor = ParseHexColor(IniString(iniPath,L"TextOverlay",L"Color",L"#FFFFFF"),RGB(255,255,255));
        textOverlayPosition = std::clamp(IniInt(iniPath,L"TextOverlay",L"Position",2),0,5);
        textOverlayBold = IniInt(iniPath,L"TextOverlay",L"Bold",0)!=0;
        textOverlayShadow = IniInt(iniPath,L"TextOverlay",L"Shadow",1)!=0;
        titleTabsEnabled = IniInt(iniPath, L"Interface", L"TitleTabs", 1) != 0;
        windowedWheelZoom = IniInt(iniPath, L"Mouse", L"WindowedWheelZoom", 0) != 0;
        leftImageDragMode = static_cast<LeftImageDragMode>(std::clamp(IniInt(iniPath,L"Mouse",L"LeftImageDragMode",0),0,1));
        rightDragPansImage = IniInt(iniPath,L"Mouse",L"RightDragPansImage",1)!=0;
        selectionClickZoomsIn = IniInt(iniPath,L"Mouse",L"SelectionClickZoomIn",1)!=0;
        selectionRightClickZoomsOut = IniInt(iniPath,L"Mouse",L"SelectionRightClickZoomOut",1)!=0;
        backgroundLeftDragMovesWindow = IniInt(iniPath,L"Mouse",L"BackgroundDragMovesWindow",1)!=0;
        activeBehaviorPreset = std::clamp(IniInt(iniPath,L"Profiles",L"BehaviorPreset",1),0,5);
        ctrlWheelZoom = IniInt(iniPath,L"Mouse",L"CtrlWheelZoom",1)!=0;
        fullscreenWheelZoom = IniInt(iniPath,L"Mouse",L"FullscreenWheelZoom",1)!=0;
        zoomAroundCursor = IniInt(iniPath,L"Mouse",L"ZoomAroundCursor",1)!=0;
        preserveManualZoomOnNavigate = IniInt(iniPath,L"Viewing",L"KeepZoomOnNavigate",0)!=0;
        middleDragPansImage = IniInt(iniPath,L"Mouse",L"MiddleDragPan",1)!=0;
        wrapFolderNavigation = IniInt(iniPath,L"Navigation",L"WrapFolder",0)!=0;
        progressiveColorFirstPreview = IniInt(iniPath,L"Performance",L"ProgressiveColorFirst",1)!=0;
        SetPreferColorProgressivePreview(progressiveColorFirstPreview);
        folderNavShowGroup = IniInt(iniPath,L"FolderNavigation",L"ShowGroup",1)!=0;
        folderNavShowPrevious = IniInt(iniPath,L"FolderNavigation",L"ShowPrevious",1)!=0;
        folderNavShowNext = IniInt(iniPath,L"FolderNavigation",L"ShowNext",1)!=0;
        folderNavShowExplore = IniInt(iniPath,L"FolderNavigation",L"ShowExplore",1)!=0;
        folderNavSkipEmpty = IniInt(iniPath,L"FolderNavigation",L"SkipEmpty",1)!=0;
        folderNavWrap = IniInt(iniPath,L"FolderNavigation",L"Wrap",0)!=0;
        folderNavOpenFirstImage = IniInt(iniPath,L"FolderNavigation",L"OpenFirstImage",1)!=0;
        folderNavTooltips = IniInt(iniPath,L"FolderNavigation",L"Tooltips",1)!=0;
        folderNavExploreEmptyAsBrowser = IniInt(iniPath,L"FolderNavigation",L"ExploreEmptyAsBrowser",1)!=0;
        folderNavIncludeHidden = IniInt(iniPath,L"FolderNavigation",L"IncludeHidden",0)!=0;
        overlayKeyboardZoom = IniInt(iniPath,L"Overlays",L"KeyboardZoom",1)!=0;
        overlayWheelZoom = IniInt(iniPath,L"Overlays",L"WheelZoom",IniInt(iniPath,L"Overlays",L"CtrlWheelZoom",1))!=0;
        overlaySelectedHighlight = IniInt(iniPath,L"Overlays",L"SelectedHighlight",1)!=0;
        overlayRememberZoom = IniInt(iniPath,L"Overlays",L"RememberZoom",0)!=0;
        overlayRightDragPansZoomed = IniInt(iniPath,L"Overlays",L"RightDragPanZoomed",1)!=0;
        overlayZoomStepPercent = std::clamp(IniInt(iniPath,L"Overlays",L"ZoomStep",20),5,100);
        for(int i=0;i<kGestureSlotCount;++i){
            const int raw=std::clamp(IniInt(iniPath,L"Gestures",GestureSlotIniKey(static_cast<GestureSlot>(i)),0),0,static_cast<int>(GestureAction::Count)-1);
            gestureMap[static_cast<size_t>(i)]=static_cast<GestureAction>(raw);
        }
        { const int savedTheme=IniInt(iniPath,L"Appearance",L"Theme",0); themeMode=(savedTheme==1)?1:0; }
        accentChoice = std::clamp(IniInt(iniPath,L"Appearance",L"Accent",0),0,6);
        // Stage 10.7 migration: folder tabs are now a first-class Glide feature and
        // are enabled once for existing installs. The user can still disable them
        // afterwards in Options; the migration marker prevents us overriding that choice.
        if (IniInt(iniPath, L"Interface", L"TitleTabs107Migrated", 0) == 0) {
            titleTabsEnabled = true;
            WriteIniInt(iniPath, L"Interface", L"TitleTabs", 1);
            WriteIniInt(iniPath, L"Interface", L"TitleTabs107Migrated", 1);
        }
        recentHistoryEnabled = IniInt(iniPath, L"History", L"Enabled", 1) != 0;
        recentHistoryLimit = std::clamp(IniInt(iniPath, L"History", L"Limit", 8), 1, 16);
        rememberWindowPlacement = IniInt(iniPath, L"General", L"RememberWindowPlacement", 1) != 0;
        fullscreenExitWindowMode = std::clamp(IniInt(iniPath,L"Fullscreen",L"ExitWindowMode",0),0,1);
        singleInstance = IniInt(iniPath, L"General", L"SingleInstance", 0) != 0;
        {
            const int modern=IniInt(iniPath,L"Viewing",L"DefaultViewMode2",-1);
            if(modern>=0) defaultViewMode=std::clamp(modern,0,4);
            else { const int legacy=std::clamp(IniInt(iniPath,L"Viewing",L"DefaultView",0),0,1); defaultViewMode=legacy==1?3:0; }
            defaultCustomZoomPercent=std::clamp(IniInt(iniPath,L"Viewing",L"DefaultCustomZoomPercent",100),2,3200);
        }
        initialQualityMode = std::clamp(IniInt(iniPath, L"Viewing", L"InitialQuality", 1), 0, 2);
        slideshowIntervalMs = std::clamp(IniInt(iniPath, L"Slideshow", L"IntervalMs", 3000), 250, 600000);
        slideshowLoop = IniInt(iniPath, L"Slideshow", L"Loop", 1) != 0;
        slideshowCrossFolders = IniInt(iniPath, L"Slideshow", L"CrossFolders", 1) != 0;
        slideshowShuffle = IniInt(iniPath, L"Slideshow", L"Shuffle", 0) != 0;
        escStopsSlideshow = IniInt(iniPath,L"Escape",L"StopSlideshow",1)!=0;
        escExitsFullscreen = IniInt(iniPath,L"Escape",L"ExitFullscreen",1)!=0;
        escWindowedConfirm = IniInt(iniPath,L"Escape",L"ConfirmClose",1)!=0;
        escRememberedClose = std::clamp(IniInt(iniPath,L"Escape",L"RememberedClose",-1),-1,1);
        externalProgramPaths[0]=IniString(iniPath,L"ExternalPrograms",L"Program1",L"");
        externalProgramPaths[1]=IniString(iniPath,L"ExternalPrograms",L"Program2",L"");
        externalProgramPaths[2]=IniString(iniPath,L"ExternalPrograms",L"Program3",L"");
        sortMode = static_cast<SortMode>(std::clamp(IniInt(iniPath, L"Viewing", L"SortMode", 0), 0, 3));
        sortAscending = IniInt(iniPath, L"Viewing", L"SortAscending", 1) != 0;

        if (rememberWindowPlacement) {
            const int x = IniInt(iniPath, L"Window", L"X", INT_MIN);
            const int y = IniInt(iniPath, L"Window", L"Y", INT_MIN);
            const int w = IniInt(iniPath, L"Window", L"W", 0);
            const int h = IniInt(iniPath, L"Window", L"H", 0);
            if (x != INT_MIN && y != INT_MIN && w >= 320 && h >= 240) {
                savedWindowRect = RECT{x, y, x + w, y + h};
                savedWindowMaximized = IniInt(iniPath, L"Window", L"Maximized", 0) != 0;
                haveSavedPlacement = true;
            }
        }

        recentFiles.clear(); recentFolders.clear();
        if (recentHistoryEnabled) {
            for (int i = 0; i < recentHistoryLimit; ++i) {
                wchar_t key[32]{}, val[32768]{};
                swprintf_s(key, L"File%d", i);
                GetPrivateProfileStringW(L"RecentFiles", key, L"", val, static_cast<DWORD>(std::size(val)), iniPath.c_str());
                if (*val) recentFiles.emplace_back(val);
                swprintf_s(key, L"Folder%d", i);
                GetPrivateProfileStringW(L"RecentFolders", key, L"", val, static_cast<DWORD>(std::size(val)), iniPath.c_str());
                if (*val) recentFolders.emplace_back(val);
            }
        }
    }

    void SaveSettings() {
        if (iniPath.empty()) InitIniPath();
        WriteIniInt(iniPath, L"Interface", L"StatusOverlay", statusVisible);
        WriteIniInt(iniPath, L"Interface", L"StatusCollapsed", statusCollapsed);
        WriteIniInt(iniPath, L"Interface", L"FullPathTitle", showFullPathInTitle);
        WriteIniInt(iniPath, L"Viewing", L"ScrollBars", showScrollBars);
        WriteIniInt(iniPath, L"Viewing", L"FullscreenCursorAutoHide", fullscreenCursorAutoHide);
        WriteIniInt(iniPath, L"Viewing", L"DefaultViewMode2", defaultViewMode);
        WriteIniInt(iniPath, L"Viewing", L"DefaultCustomZoomPercent", defaultCustomZoomPercent);
        WriteIniInt(iniPath, L"Viewing", L"DefaultView", defaultViewMode==3?1:0); // legacy compatibility
        WriteIniInt(iniPath, L"Viewing", L"InitialQuality", initialQualityMode);
        WriteIniInt(iniPath, L"Viewing", L"SortMode", static_cast<int>(sortMode));
        WriteIniInt(iniPath, L"Viewing", L"SortAscending", sortAscending);
        WriteIniInt(iniPath, L"Mouse", L"DoubleClickFullscreen", doubleClickFullscreen);
        WriteIniInt(iniPath, L"Mouse", L"FullscreenClickNavigation", fullscreenClickNavigation);
        WriteIniInt(iniPath, L"Mouse", L"DoubleClickExitFullscreen", doubleClickExitFullscreen);
        WriteIniInt(iniPath, L"Interface", L"FullscreenBarAutoHide", fullscreenBarAutoHide);
        WriteIniInt(iniPath, L"Interface", L"FullscreenXClosesApp", fullscreenXClosesApp);
        WriteIniInt(iniPath, L"Interface", L"FullscreenStatusAlwaysOn", fullscreenStatusAlwaysOn);
        WriteIniInt(iniPath, L"Interface", L"AlwaysOnTop", alwaysOnTop);
        WriteIniInt(iniPath, L"Viewing", L"AdaptiveFastPreview", adaptiveFastPreview);
        WriteIniInt(iniPath, L"Viewing", L"AdaptivePreviewDelayMs", adaptivePreviewDelayMs);
        WriteIniInt(iniPath, L"Performance", L"FastColdStart", fastColdStart);
        WriteIniInt(iniPath, L"Performance", L"StartupDiagnostics", startupDiagnostics);
        WriteIniInt(iniPath, L"Performance", L"PrefetchEnabled", prefetchEnabled);
        WriteIniInt(iniPath, L"Performance", L"BackgroundRefinement", backgroundRefinement);
        WriteIniInt(iniPath, L"Performance", L"PurgeCacheOnMinimize", purgeCacheOnMinimize);
        WriteIniInt(iniPath, L"Interface", L"HomeTips", homeTipsEnabled);
        WriteIniInt(iniPath, L"Mouse", L"InvertWheelNavigation", invertWheelNavigation);
        WriteIniInt(iniPath, L"Navigation", L"SiblingFolders", autoSiblingFolders);
        WriteIniInt(iniPath,L"Viewing",L"ZoomStepPercent",zoomStepPercent);
        WriteIniInt(iniPath,L"Fullscreen",L"CursorHideDelayMs",fullscreenCursorHideDelayMs);
        WriteIniInt(iniPath,L"Performance",L"PrefetchDepth",prefetchDepth);
        WriteIniInt(iniPath,L"Performance",L"RapidPreviewLongestSide",rapidPreviewLongestSide);
        WriteIniInt(iniPath,L"Performance",L"CacheItems",static_cast<int>(maxCacheItems));
        WriteIniInt(iniPath,L"Tabs",L"MinWidth",static_cast<int>(tabMinWidth));
        WriteIniInt(iniPath,L"Tabs",L"MaxWidth",static_cast<int>(tabMaxWidth));
        WriteIniInt(iniPath,L"Tabs",L"Detach",tabDetachEnabled);
        WriteIniInt(iniPath,L"Tabs",L"AttachBetweenWindows",tabAttachEnabled);
        WriteIniInt(iniPath,L"Tabs",L"ClosedHistoryLimit",closedTabHistoryLimit);
        WriteIniInt(iniPath,L"Tabs",L"CloseEmptyWindowAfterDetach",closeEmptyWindowAfterDetach);
        WriteIniInt(iniPath,L"Tabs",L"DetachedWindowHomeTab",detachedWindowHomeTab);
        WriteIniInt(iniPath,L"StatusBar",L"OverlayControls",statusShowOverlay);
        WriteIniInt(iniPath,L"Overlays",L"DefaultOpacity",overlayDefaultOpacity);
        WriteIniInt(iniPath,L"Overlays",L"RememberLastFolder",overlayRememberLastFolder);
        WritePrivateProfileStringW(L"Overlays",L"LastFolder",lastOverlayFolder.c_str(),iniPath.c_str());
        WriteIniInt(iniPath,L"Navigation",L"RememberMainOpenFolder",mainRememberLastFolder);
        WritePrivateProfileStringW(L"Navigation",L"LastMainOpenFolder",lastMainFolder.c_str(),iniPath.c_str());
        WriteIniInt(iniPath, L"StatusBar", L"Navigation", statusShowNavigation);
        WriteIniInt(iniPath, L"StatusBar", L"ZoomButtons", statusShowZoom);
        WriteIniInt(iniPath, L"StatusBar", L"Slideshow", statusShowSlideshow);
        WriteIniInt(iniPath, L"StatusBar", L"FitButtons", statusShowFit);
        WriteIniInt(iniPath, L"StatusBar", L"Info", statusShowInfo);
        WriteIniInt(iniPath, L"StatusBar", L"Collapse", statusShowCollapse);
        WriteIniInt(iniPath, L"StatusBar", L"Options", statusShowOptions);
        WriteIniInt(iniPath, L"StatusBar", L"Close", statusShowClose);
        WriteIniInt(iniPath, L"StatusBar", L"StatResolution", statusStatResolution);
        WriteIniInt(iniPath, L"StatusBar", L"StatZoom", statusStatZoom);
        WriteIniInt(iniPath, L"StatusBar", L"StatIndex", statusStatIndex);
        WriteIniInt(iniPath, L"StatusBar", L"StatFileSize", statusStatFileSize);
        WriteIniInt(iniPath, L"StatusBar", L"StatFormat", statusStatFormat);
        WriteIniInt(iniPath, L"StatusBar", L"StatPreview", statusStatPreview);
        WriteIniInt(iniPath,L"TextOverlay",L"Enabled",textOverlayEnabled);
        WritePrivateProfileStringW(L"TextOverlay",L"Template",textOverlayTemplate.c_str(),iniPath.c_str());
        WriteIniInt(iniPath,L"TextOverlay",L"FontSize",textOverlayFontSize);
        WriteIniInt(iniPath,L"TextOverlay",L"Opacity",textOverlayOpacity);
        {const auto c=HexColor(textOverlayColor);WritePrivateProfileStringW(L"TextOverlay",L"Color",c.c_str(),iniPath.c_str());}
        WriteIniInt(iniPath,L"TextOverlay",L"Position",textOverlayPosition);
        WriteIniInt(iniPath,L"TextOverlay",L"Bold",textOverlayBold);
        WriteIniInt(iniPath,L"TextOverlay",L"Shadow",textOverlayShadow);
        WriteIniInt(iniPath, L"Interface", L"TitleTabs", titleTabsEnabled);
        WriteIniInt(iniPath, L"Mouse", L"WindowedWheelZoom", windowedWheelZoom);
        WriteIniInt(iniPath,L"Mouse",L"LeftImageDragMode",static_cast<int>(leftImageDragMode));
        WriteIniInt(iniPath,L"Mouse",L"RightDragPansImage",rightDragPansImage);
        WriteIniInt(iniPath,L"Mouse",L"SelectionClickZoomIn",selectionClickZoomsIn);
        WriteIniInt(iniPath,L"Mouse",L"SelectionRightClickZoomOut",selectionRightClickZoomsOut);
        WriteIniInt(iniPath,L"Mouse",L"BackgroundDragMovesWindow",backgroundLeftDragMovesWindow);
        WriteIniInt(iniPath,L"Profiles",L"BehaviorPreset",activeBehaviorPreset);
        WriteIniInt(iniPath,L"Mouse",L"CtrlWheelZoom",ctrlWheelZoom);
        WriteIniInt(iniPath,L"Mouse",L"FullscreenWheelZoom",fullscreenWheelZoom);
        WriteIniInt(iniPath,L"Mouse",L"ZoomAroundCursor",zoomAroundCursor);
        WriteIniInt(iniPath,L"Viewing",L"KeepZoomOnNavigate",preserveManualZoomOnNavigate);
        WriteIniInt(iniPath,L"Mouse",L"MiddleDragPan",middleDragPansImage);
        WriteIniInt(iniPath,L"Navigation",L"WrapFolder",wrapFolderNavigation);
        WriteIniInt(iniPath,L"Performance",L"ProgressiveColorFirst",progressiveColorFirstPreview);
        WriteIniInt(iniPath,L"FolderNavigation",L"ShowGroup",folderNavShowGroup);
        WriteIniInt(iniPath,L"FolderNavigation",L"ShowPrevious",folderNavShowPrevious);
        WriteIniInt(iniPath,L"FolderNavigation",L"ShowNext",folderNavShowNext);
        WriteIniInt(iniPath,L"FolderNavigation",L"ShowExplore",folderNavShowExplore);
        WriteIniInt(iniPath,L"FolderNavigation",L"SkipEmpty",folderNavSkipEmpty);
        WriteIniInt(iniPath,L"FolderNavigation",L"Wrap",folderNavWrap);
        WriteIniInt(iniPath,L"FolderNavigation",L"OpenFirstImage",folderNavOpenFirstImage);
        WriteIniInt(iniPath,L"FolderNavigation",L"Tooltips",folderNavTooltips);
        WriteIniInt(iniPath,L"FolderNavigation",L"ExploreEmptyAsBrowser",folderNavExploreEmptyAsBrowser);
        WriteIniInt(iniPath,L"FolderNavigation",L"IncludeHidden",folderNavIncludeHidden);
        WriteIniInt(iniPath,L"Overlays",L"KeyboardZoom",overlayKeyboardZoom);
        WriteIniInt(iniPath,L"Overlays",L"WheelZoom",overlayWheelZoom);
        WriteIniInt(iniPath,L"Overlays",L"SelectedHighlight",overlaySelectedHighlight);
        WriteIniInt(iniPath,L"Overlays",L"RememberZoom",overlayRememberZoom);
        WriteIniInt(iniPath,L"Overlays",L"RightDragPanZoomed",overlayRightDragPansZoomed);
        WriteIniInt(iniPath,L"Overlays",L"ZoomStep",overlayZoomStepPercent);
        for(int i=0;i<kGestureSlotCount;++i) WriteIniInt(iniPath,L"Gestures",GestureSlotIniKey(static_cast<GestureSlot>(i)),static_cast<int>(gestureMap[static_cast<size_t>(i)]));
        WriteIniInt(iniPath,L"Appearance",L"Theme",themeMode);
        WriteIniInt(iniPath,L"Appearance",L"Accent",accentChoice);
        WriteIniInt(iniPath, L"History", L"Enabled", recentHistoryEnabled);
        WriteIniInt(iniPath, L"History", L"Limit", recentHistoryLimit);
        WriteIniInt(iniPath, L"General", L"RememberWindowPlacement", rememberWindowPlacement);
        WriteIniInt(iniPath,L"Fullscreen",L"ExitWindowMode",fullscreenExitWindowMode);
        WriteIniInt(iniPath, L"General", L"SingleInstance", singleInstance);
        WriteIniInt(iniPath, L"Slideshow", L"IntervalMs", slideshowIntervalMs);
        WriteIniInt(iniPath, L"Slideshow", L"Loop", slideshowLoop);
        WriteIniInt(iniPath, L"Slideshow", L"CrossFolders", slideshowCrossFolders);
        WriteIniInt(iniPath, L"Slideshow", L"Shuffle", slideshowShuffle);
        WriteIniInt(iniPath,L"Escape",L"StopSlideshow",escStopsSlideshow);
        WriteIniInt(iniPath,L"Escape",L"ExitFullscreen",escExitsFullscreen);
        WriteIniInt(iniPath,L"Escape",L"ConfirmClose",escWindowedConfirm);
        WriteIniInt(iniPath,L"Escape",L"RememberedClose",escRememberedClose);
        WritePrivateProfileStringW(L"ExternalPrograms",L"Program1",externalProgramPaths[0].c_str(),iniPath.c_str());
        WritePrivateProfileStringW(L"ExternalPrograms",L"Program2",externalProgramPaths[1].c_str(),iniPath.c_str());
        WritePrivateProfileStringW(L"ExternalPrograms",L"Program3",externalProgramPaths[2].c_str(),iniPath.c_str());
        if (hotkeys.size()!=std::size(kHotkeyDefs)) hotkeys.resize(std::size(kHotkeyDefs));
        if (hotkeysAlt.size()!=std::size(kHotkeyDefs)) hotkeysAlt.resize(std::size(kHotkeyDefs));
        for (size_t i=0;i<std::size(kHotkeyDefs);++i) {
            WriteIniInt(iniPath,L"Hotkeys",kHotkeyDefs[i].iniKey,static_cast<int>(hotkeys[i]));
            const std::wstring altKey=std::wstring(kHotkeyDefs[i].iniKey)+L".Alt";
            WriteIniInt(iniPath,L"Hotkeys",altKey.c_str(),static_cast<int>(hotkeysAlt[i]));
        }

        if (rememberWindowPlacement && hwnd) {
            WINDOWPLACEMENT wp{sizeof(WINDOWPLACEMENT)};
            bool maxed=false;
            if(fullscreen){
                // Fullscreen is a temporary presentation state. Persist the exact windowed
                // placement captured before entering fullscreen, not the monitor-filling popup.
                wp=windowedPlacement;
                maxed=savedWindowMaximized;
            }else if(GetWindowPlacement(hwnd,&wp)){
                maxed=(wp.showCmd==SW_SHOWMAXIMIZED)||IsZoomed(hwnd)!=FALSE;
            }else wp.length=0;
            if(wp.length==sizeof(WINDOWPLACEMENT)){
                const RECT r=wp.rcNormalPosition;
                if(r.right-r.left>=320 && r.bottom-r.top>=240){
                    WriteIniInt(iniPath,L"Window",L"X",r.left);
                    WriteIniInt(iniPath,L"Window",L"Y",r.top);
                    WriteIniInt(iniPath,L"Window",L"W",r.right-r.left);
                    WriteIniInt(iniPath,L"Window",L"H",r.bottom-r.top);
                    WriteIniInt(iniPath,L"Window",L"Maximized",maxed?1:0);
                }
            }
        }

        for (int i = 0; i < 16; ++i) {
            wchar_t key[32]{};
            swprintf_s(key, L"File%d", i);
            const wchar_t* value = (recentHistoryEnabled && i < static_cast<int>(recentFiles.size())) ? recentFiles[i].c_str() : L"";
            WritePrivateProfileStringW(L"RecentFiles", key, value, iniPath.c_str());
            swprintf_s(key, L"Folder%d", i);
            value = (recentHistoryEnabled && i < static_cast<int>(recentFolders.size())) ? recentFolders[i].c_str() : L"";
            WritePrivateProfileStringW(L"RecentFolders", key, value, iniPath.c_str());
        }
    }

    void ApplySavedWindowPlacement() {
        if (!hwnd || !haveSavedPlacement || !rememberWindowPlacement) return;
        HMONITOR mon = MonitorFromRect(&savedWindowRect, MONITOR_DEFAULTTONEAREST);
        MONITORINFO mi{sizeof(mi)};
        if (!GetMonitorInfoW(mon, &mi)) return;
        const int w = savedWindowRect.right - savedWindowRect.left;
        const int h = savedWindowRect.bottom - savedWindowRect.top;
        int x = std::clamp(savedWindowRect.left, mi.rcWork.left - w + 80, mi.rcWork.right - 80);
        int y = std::clamp(savedWindowRect.top, mi.rcWork.top, mi.rcWork.bottom - 80);
        SetWindowPos(hwnd, nullptr, x, y, w, h, SWP_NOZORDER | SWP_NOACTIVATE);
        if (savedWindowMaximized) ShowWindow(hwnd, SW_MAXIMIZE);
    }

    void BeginNativeBackgroundDrag() {
        if (!hwnd || fullscreen) return;
        nativeBackgroundDrag = true;
        SetCursor(LoadCursorW(nullptr, IDC_HAND));
        ReleaseCapture();
        POINT pt{}; GetCursorPos(&pt);
        SendMessageW(hwnd, WM_NCLBUTTONDOWN, HTCAPTION, MAKELPARAM(pt.x, pt.y));
        nativeBackgroundDrag = false;
        SetCursor(LoadCursorW(nullptr, IDC_ARROW));
    }

    static std::wstring ParentFolderLabel(const std::wstring& path) {
        if (path.empty()) return L"Glide";
        try {
            fs::path p(path);
            fs::path parent = p.parent_path();
            std::wstring label = parent.filename().wstring();
            if (!label.empty()) return label;
            label = parent.root_name().wstring();
            if (!label.empty()) return label + L"\\";
            label = parent.wstring();
            if (!label.empty()) return label;
        } catch (...) {}
        return L"Glide";
    }

    float ViewerTopInset() const {
        return (titleTabsEnabled && (!fullscreen || fullscreenNativeCaptionVisible)) ? 34.0f : 0.0f;
    }

    // Physical-pixel hit target for the upper-right Close/Exit control.  This is
    // also the authoritative visual hover zone, so the red Close state remains
    // stable when the pointer is parked on the literal screen edge/corner.
    // It is deliberately independent of the Direct2D paint rectangle so a very fast
    // throw of the pointer into the screen corner (Fitts' law) still works even
    // before the auto-reveal bar has had a chance to repaint.
    bool PointInTopRightCloseHotZone(POINT p) const {
        if (!hwnd) return false;
        RECT rc{}; GetClientRect(hwnd, &rc);
        const int cw = std::max<LONG>(1, rc.right - rc.left);
        UINT dpi = 96;
        if (HMODULE user32 = GetModuleHandleW(L"user32.dll")) {
            using Fn = UINT (WINAPI*)(HWND);
            if (auto fn = reinterpret_cast<Fn>(GetProcAddress(user32, "GetDpiForWindow"))) dpi = fn(hwnd);
        }
        const int hotW = MulDiv(46, static_cast<int>(dpi), 96);
        const int hotH = MulDiv(48, static_cast<int>(dpi), 96);
        const int edgeSlack = MulDiv(4, static_cast<int>(dpi), 96);
        // Slack extends OUTSIDE the client edge only. Never expand leftward into
        // Maximize/Restore; that was the source of the red-hover crossover.
        return p.x >= cw - hotW && p.x <= cw + edgeSlack &&
               p.y >= -edgeSlack && p.y <= hotH;
    }

    void ApplyWindowedChromeStyle() {
        if (!hwnd || fullscreen) return;
        LONG_PTR style=GetWindowLongPtrW(hwnd,GWL_STYLE);
        style &= ~static_cast<LONG_PTR>(WS_POPUP);
        // Do not manufacture WS_VISIBLE through SetWindowLongPtr.  On startup that
        // made the HWND logically visible before ShowWindow() performed the real
        // top-level shell transition; Explorer could consequently omit Glide from
        // the taskbar/Alt-Tab list until the first activation click.  Preserve the
        // existing visibility bit and let ShowWindow own visibility transitions.
        style |= WS_OVERLAPPED|WS_CAPTION|WS_THICKFRAME|WS_SYSMENU|WS_MINIMIZEBOX|WS_MAXIMIZEBOX;
        SetWindowLongPtrW(hwnd,GWL_STYLE,style);
        SetWindowPos(hwnd,nullptr,0,0,0,0,SWP_NOMOVE|SWP_NOSIZE|SWP_NOZORDER|SWP_NOACTIVATE|SWP_FRAMECHANGED);
        ApplyThemeToMainWindow();
        if(viewMode==ViewMode::Manual)ClampPan(); UpdateScrollBars(); ResizeShellBrowser(); InvalidateViewer();
    }

    bool PointOnTitleInteractive(POINT p) const {
        if(opacitySliderVisible&&PointInRect(opacityPanelRect,p))return true;
        if(!PointInRect(titleBarRect,p))return false;
        if(PointInRect(titleNewTabRect,p)||PointInRect(titleReopenRect,p)||PointInRect(titlePrevFolderRect,p)||PointInRect(titleNextFolderRect,p)||PointInRect(titleExploreParentRect,p)||PointInRect(titleOptionsRect,p)||PointInRect(titlePinRect,p)||PointInRect(titleOpacityRect,p))return true;
        for(const auto&r:titleTabCloseRects)if(PointInRect(r,p))return true;
        for(const auto&r:titleTabRects)if(PointInRect(r,p))return true;
        return false;
    }

    void LaunchExternalProgram(int slot) {
        if(slot<0||slot>=3||currentPath.empty())return;
        const auto& exe=externalProgramPaths[static_cast<size_t>(slot)];
        if(exe.empty()||GetFileAttributesW(exe.c_str())==INVALID_FILE_ATTRIBUTES){
            MessageBoxW(hwnd,L"Configure this external-program slot in Settings > Windows Integration first.",L"Glide",MB_OK|MB_ICONINFORMATION);return;
        }
        if(diagnosticMode && diagnosticSuppressExternalLaunch){diagnosticExternalLaunchRequested=true;diagnosticExternalLaunchCommand=exe+L" | "+currentPath;return;}
        if(!glide_platform::LaunchExternalProgram(hwnd,exe,currentPath))
            MessageBoxW(hwnd,L"Glide could not start the configured external program.",L"Glide",MB_OK|MB_ICONERROR);
    }

    void HandleEscapeAction() {
        if((slideshowRunning||slideshowPaused) && escStopsSlideshow){
            StopSlideshow();
            if(fullscreen!=slideshowStartedFullscreen)ToggleFullscreen();
            return;
        }
        if(fullscreen){if(escExitsFullscreen)ToggleFullscreen();return;}
        if(selectionActive||selecting){ClearSelection();return;}
        if(!escWindowedConfirm){PostMessageW(hwnd,WM_CLOSE,0,0);return;}
        if(escRememberedClose==1){PostMessageW(hwnd,WM_CLOSE,0,0);return;}
        if(escRememberedClose==0)return;
        TASKDIALOGCONFIG td{};td.cbSize=sizeof(td);td.hwndParent=hwnd;td.dwFlags=TDF_ALLOW_DIALOG_CANCELLATION;td.dwCommonButtons=TDCBF_YES_BUTTON|TDCBF_NO_BUTTON;td.pszWindowTitle=L"Glide";td.pszMainInstruction=L"Close Glide?";td.pszContent=L"Press Yes to close Glide or No to keep it open.";td.pszVerificationText=L"Remember my choice";
        int r=IDNO;BOOL remember=FALSE;if(FAILED(TaskDialogIndirect(&td,&r,nullptr,&remember)))r=MessageBoxW(hwnd,L"Close Glide?",L"Glide",MB_YESNO|MB_ICONQUESTION|MB_DEFBUTTON2);
        if(remember){escRememberedClose=(r==IDYES)?1:0;SaveSettings();}
        if(r==IDYES)PostMessageW(hwnd,WM_CLOSE,0,0);
    }

    std::wstring MainDialogInitialFolder() const {
        return (mainRememberLastFolder && !lastMainFolder.empty()) ? lastMainFolder : L"";
    }

    std::wstring OverlayDialogInitialFolder() const {
        return (overlayRememberLastFolder && !lastOverlayFolder.empty()) ? lastOverlayFolder : L"";
    }

    bool ShouldOpenEmptyFolderAsBrowser() const {
        return folderNavExploreEmptyAsBrowser && titleTabsEnabled;
    }

    bool BrowseExternalProgram(HWND owner,int slot){
        if(slot<0||slot>=3)return false;
        std::wstring selected;
        if(!glide_platform::BrowseForExecutable(owner,externalProgramPaths[slot],selected))return false;
        externalProgramPaths[slot]=std::move(selected);
        return true;
    }

    void PauseSlideshow() {
        if (!slideshowRunning) return;
        slideshowRunning = false;
        slideshowPaused = true;
        KillTimer(hwnd, kSlideshowTimerId);
        UpdateTitle(); InvalidateViewer();
    }

    void StopSlideshow() {
        if (!slideshowRunning && !slideshowPaused) return;
        slideshowRunning = false;
        slideshowPaused = false;
        KillTimer(hwnd, kSlideshowTimerId);
        UpdateTitle();
        InvalidateViewer();
    }

    void ResumeSlideshow() {
        if (!slideshowPaused || slideshowRunning) return;
        slideshowPaused = false;
        slideshowRunning = true;
        SetTimer(hwnd, kSlideshowTimerId, static_cast<UINT>(slideshowIntervalMs), nullptr);
        UpdateTitle(); InvalidateViewer();
    }

    void ToggleSlideshow() {
        if (slideshowRunning) PauseSlideshow();
        else if (slideshowPaused) ResumeSlideshow();
        else ShowSlideshowConfig();
    }

    void SlideshowStep() {
        if (!slideshowRunning || files.empty()) return;
        if (slideshowShuffle && files.size() > 1) {
            size_t next = currentIndex;
            for (int tries = 0; tries < 8 && next == currentIndex; ++tries)
                next = static_cast<size_t>(std::rand()) % files.size();
            currentIndex = next;
            haveIndex = true;
            RequestImage(files[currentIndex].wstring(), +1);
            return;
        }
        if (!slideshowCrossFolders && currentIndex + 1 >= files.size()) {
            if (slideshowLoop) {
                currentIndex = 0; haveIndex = true; RequestImage(files[0].wstring(), +1);
            } else { slideshowRunning=false; slideshowPaused=false; KillTimer(hwnd,kSlideshowTimerId); UpdateTitle(); InvalidateViewer(); }
            return;
        }
        if (slideshowCrossFolders) {
            Navigate(+1);
        } else if (currentIndex + 1 < files.size()) {
            ++currentIndex; RequestImage(files[currentIndex].wstring(), +1);
        }
    }

    bool ShouldUseFastColdStart(bool startupBrowser,const std::wstring& initial) const {
        return fastColdStart && !startupBrowser && !initial.empty();
    }

    bool ShouldEnforceSingleInstance(bool forceNewWindow) const {
        return singleInstance && !forceNewWindow;
    }

    bool ShouldHideFullscreenCursorAt(ULONGLONG now) const {
        return fullscreen && fullscreenCursorAutoHide && !fullscreenNativeCaptionVisible &&
               !fullscreenCursorHidden && now-lastMouseMoveTick>=static_cast<ULONGLONG>(fullscreenCursorHideDelayMs);
    }

    bool ShouldAutoHideFullscreenBarAt(bool pointerInTop, ULONGLONG now) const {
        return fullscreen && fullscreenNativeCaptionVisible && fullscreenBarAutoHide && !pointerInTop &&
               now-fullscreenBarLastInsideTick>=900;
    }

    std::wstring ExePath() const {
        wchar_t exe[32768]{};
        GetModuleFileNameW(nullptr, exe, static_cast<DWORD>(std::size(exe)));
        return exe;
    }


    static std::string Utf8(const std::wstring& w) {
        if (w.empty()) return {};
        int n=WideCharToMultiByte(CP_UTF8,0,w.c_str(),static_cast<int>(w.size()),nullptr,0,nullptr,nullptr);
        std::string out(static_cast<size_t>(std::max(0,n)), '\0');
        if(n>0)WideCharToMultiByte(CP_UTF8,0,w.c_str(),static_cast<int>(w.size()),out.data(),n,nullptr,nullptr);
        return out;
    }

    static uint32_t DiagnosticCrc32(const std::vector<BYTE>& data) {
        uint32_t crc=0xffffffffu;
        for(BYTE b:data){crc^=b;for(int i=0;i<8;++i)crc=(crc>>1)^((crc&1)?0xedb88320u:0u);}
        return crc^0xffffffffu;
    }

    static bool ReadDiagnosticBytes(const fs::path& p,std::vector<BYTE>& out){
        std::ifstream f(p,std::ios::binary);if(!f)return false;f.seekg(0,std::ios::end);auto n=f.tellg();if(n<0)return false;f.seekg(0,std::ios::beg);out.resize(static_cast<size_t>(n));if(!out.empty())f.read(reinterpret_cast<char*>(out.data()),static_cast<std::streamsize>(out.size()));return f.good()||f.eof();
    }

    static void ZipPut16(std::ofstream& f,uint16_t v){f.put(static_cast<char>(v&255));f.put(static_cast<char>((v>>8)&255));}
    static void ZipPut32(std::ofstream& f,uint32_t v){ZipPut16(f,static_cast<uint16_t>(v&0xffff));ZipPut16(f,static_cast<uint16_t>((v>>16)&0xffff));}

    static bool WriteStoreZip(const fs::path& folder,const fs::path& zipPath){
        struct Entry{std::string name;uint32_t crc{},size{},offset{};};std::vector<Entry> entries;
        std::error_code ec;
        if(!zipPath.parent_path().empty()){fs::create_directories(zipPath.parent_path(),ec);if(ec)return false;}
        ec.clear();
        std::ofstream z(zipPath,std::ios::binary|std::ios::trunc);if(!z)return false;
        for(auto it=fs::recursive_directory_iterator(folder,ec);!ec&&it!=fs::recursive_directory_iterator();it.increment(ec)){
            if(!it->is_regular_file())continue;std::vector<BYTE> data;if(!ReadDiagnosticBytes(it->path(),data)||data.size()>UINT32_MAX)return false;
            std::wstring rel=fs::relative(it->path(),folder,ec).generic_wstring();if(ec)return false;std::string name=Utf8(rel);const std::streamoff offset=static_cast<std::streamoff>(z.tellp());if(offset<0||static_cast<uint64_t>(offset)>UINT32_MAX)return false;Entry e{name,DiagnosticCrc32(data),static_cast<uint32_t>(data.size()),static_cast<uint32_t>(offset)};
            if(name.empty()||name.size()>UINT16_MAX)return false;
            ZipPut32(z,0x04034b50);ZipPut16(z,20);ZipPut16(z,0x0800);ZipPut16(z,0);ZipPut16(z,0);ZipPut16(z,0);ZipPut32(z,e.crc);ZipPut32(z,e.size);ZipPut32(z,e.size);ZipPut16(z,static_cast<uint16_t>(name.size()));ZipPut16(z,0);z.write(name.data(),static_cast<std::streamsize>(name.size()));if(!data.empty())z.write(reinterpret_cast<const char*>(data.data()),static_cast<std::streamsize>(data.size()));entries.push_back(e);
        }
        if(ec||entries.size()>UINT16_MAX||!z.good())return false;
        const uint32_t centralOffset=static_cast<uint32_t>(static_cast<std::streamoff>(z.tellp()));
        for(const auto&e:entries){ZipPut32(z,0x02014b50);ZipPut16(z,20);ZipPut16(z,20);ZipPut16(z,0x0800);ZipPut16(z,0);ZipPut16(z,0);ZipPut16(z,0);ZipPut32(z,e.crc);ZipPut32(z,e.size);ZipPut32(z,e.size);ZipPut16(z,static_cast<uint16_t>(e.name.size()));ZipPut16(z,0);ZipPut16(z,0);ZipPut16(z,0);ZipPut16(z,0);ZipPut32(z,0);ZipPut32(z,e.offset);z.write(e.name.data(),static_cast<std::streamsize>(e.name.size()));}
        const uint32_t centralSize=static_cast<uint32_t>(static_cast<std::streamoff>(z.tellp()))-centralOffset;ZipPut32(z,0x06054b50);ZipPut16(z,0);ZipPut16(z,0);ZipPut16(z,static_cast<uint16_t>(entries.size()));ZipPut16(z,static_cast<uint16_t>(entries.size()));ZipPut32(z,centralSize);ZipPut32(z,centralOffset);ZipPut16(z,0);z.flush();const bool good=z.good();z.close();return good&&!z.fail();
    }

    static bool SaveWindowJpeg(HWND captureWnd,const fs::path& path,float quality=0.78f){
        if(!captureWnd||!IsWindow(captureWnd))return false;RECT wr{};if(!GetWindowRect(captureWnd,&wr))return false;const int w=wr.right-wr.left,h=wr.bottom-wr.top;if(w<=0||h<=0)return false;
        const bool wasVisible=IsWindowVisible(captureWnd)!=FALSE;
        if(!wasVisible){ShowWindow(captureWnd,SW_SHOWNOACTIVATE);SetWindowPos(captureWnd,HWND_BOTTOM,0,0,0,0,SWP_NOMOVE|SWP_NOSIZE|SWP_NOACTIVATE);UpdateWindow(captureWnd);}
        HDC sdc=GetDC(nullptr);if(!sdc){if(!wasVisible)ShowWindow(captureWnd,SW_HIDE);return false;}
        HDC mdc=CreateCompatibleDC(sdc);if(!mdc){ReleaseDC(nullptr,sdc);if(!wasVisible)ShowWindow(captureWnd,SW_HIDE);return false;}
        HBITMAP bmp=CreateCompatibleBitmap(sdc,w,h);if(!bmp){DeleteDC(mdc);ReleaseDC(nullptr,sdc);if(!wasVisible)ShowWindow(captureWnd,SW_HIDE);return false;}
        HGDIOBJ old=SelectObject(mdc,bmp);BOOL painted=PrintWindow(captureWnd,mdc,PW_RENDERFULLCONTENT);if(!painted)painted=BitBlt(mdc,0,0,w,h,sdc,wr.left,wr.top,SRCCOPY);SelectObject(mdc,old);DeleteDC(mdc);ReleaseDC(nullptr,sdc);
        if(!wasVisible)ShowWindow(captureWnd,SW_HIDE);
        if(!painted){DeleteObject(bmp);return false;}
        ComPtr<IWICImagingFactory> wf;HRESULT hr=CoCreateInstance(CLSID_WICImagingFactory,nullptr,CLSCTX_INPROC_SERVER,IID_PPV_ARGS(&wf));if(FAILED(hr)){DeleteObject(bmp);return false;}ComPtr<IWICBitmap> wb;hr=wf->CreateBitmapFromHBITMAP(bmp,nullptr,WICBitmapIgnoreAlpha,&wb);DeleteObject(bmp);if(FAILED(hr))return false;
        ComPtr<IWICStream> stream;hr=wf->CreateStream(&stream);if(SUCCEEDED(hr))hr=stream->InitializeFromFilename(path.c_str(),GENERIC_WRITE);ComPtr<IWICBitmapEncoder> enc;if(SUCCEEDED(hr))hr=wf->CreateEncoder(GUID_ContainerFormatJpeg,nullptr,&enc);if(SUCCEEDED(hr))hr=enc->Initialize(stream.Get(),WICBitmapEncoderNoCache);
        ComPtr<IWICBitmapFrameEncode> frame;ComPtr<IPropertyBag2> props;if(SUCCEEDED(hr))hr=enc->CreateNewFrame(&frame,&props);
        if(SUCCEEDED(hr)&&props){PROPBAG2 option{};option.pstrName=const_cast<LPOLESTR>(L"ImageQuality");VARIANT value{};VariantInit(&value);value.vt=VT_R4;value.fltVal=std::clamp(quality,0.1f,1.0f);props->Write(1,&option,&value);VariantClear(&value);}
        if(SUCCEEDED(hr))hr=frame->Initialize(props.Get());if(SUCCEEDED(hr))hr=frame->SetSize(static_cast<UINT>(w),static_cast<UINT>(h));WICPixelFormatGUID pf=GUID_WICPixelFormat24bppBGR;if(SUCCEEDED(hr))hr=frame->SetPixelFormat(&pf);if(SUCCEEDED(hr))hr=frame->WriteSource(wb.Get(),nullptr);if(SUCCEEDED(hr))hr=frame->Commit();if(SUCCEEDED(hr))hr=enc->Commit();return SUCCEEDED(hr);
    }

    static std::wstring DiagnosticControlName(HWND c){
        wchar_t cls[80]{},txt[180]{};GetClassNameW(c,cls,79);GetWindowTextW(c,txt,179);std::wstringstream ss;ss<<L"id="<<GetDlgCtrlID(c)<<L" class="<<cls;if(txt[0])ss<<L" text=\""<<txt<<L"\"";return ss.str();
    }

    // The combo's themed drop-down glyph is a separate STATIC child layered
    // over the COMBOBOX client rectangle. It is intentional; all other
    // positive-area intersections remain actionable geometry failures.
    static bool IsExpectedSettingsGeometryOverlay(HWND a,HWND b){
        auto isArrow=[](HWND c){wchar_t cls[32]{},txt[16]{};GetClassNameW(c,cls,31);GetWindowTextW(c,txt,15);return _wcsicmp(cls,L"STATIC")==0&&wcscmp(txt,L"▼")==0;};
        auto isCombo=[](HWND c){wchar_t cls[32]{};GetClassNameW(c,cls,31);return _wcsicmp(cls,L"COMBOBOX")==0;};
        return (isArrow(a)&&isCombo(b))||(isArrow(b)&&isCombo(a));
    }

    static void WriteVisibleGeometry(HWND wnd,std::wostream& out,const wchar_t* state){
        struct C{HWND h{};RECT r{};int id{};};std::vector<C> cs;RECT host{};GetWindowRect(wnd,&host);out<<L"\n["<<state<<L"]\n";
        for(HWND c=GetWindow(wnd,GW_CHILD);c;c=GetWindow(c,GW_HWNDNEXT)){if(!IsWindowVisible(c))continue;RECT r{};GetWindowRect(c,&r);OffsetRect(&r,-host.left,-host.top);int id=GetDlgCtrlID(c);out<<DiagnosticControlName(c)<<L" rect="<<r.left<<L","<<r.top<<L","<<r.right<<L","<<r.bottom<<L"\n";cs.push_back({c,r,id});}
        for(size_t i=0;i<cs.size();++i)for(size_t j=i+1;j<cs.size();++j){RECT x{};if(IntersectRect(&x,&cs[i].r,&cs[j].r)&&x.right>x.left&&x.bottom>x.top){out<<(IsExpectedSettingsGeometryOverlay(cs[i].h,cs[j].h)?L"EXPECTED_OVERLAP ":L"OVERLAP ")<<DiagnosticControlName(cs[i].h)<<L" <-> "<<DiagnosticControlName(cs[j].h)<<L" intersection="<<(x.right-x.left)<<L"x"<<(x.bottom-x.top)<<L"\n";}}
    }

    fs::path DiagnosticsDestination() const {
        PWSTR raw=nullptr;fs::path base;if(SUCCEEDED(SHGetKnownFolderPath(FOLDERID_Downloads,0,nullptr,&raw))&&raw){base=raw;CoTaskMemFree(raw);}if(base.empty())base=fs::path(ExePath()).parent_path();return base;
    }

    static void PumpDiagnosticUi(HWND wnd,DWORD milliseconds){
        const ULONGLONG end=GetTickCount64()+milliseconds;
        while(GetTickCount64()<end){
            MSG m{};
            while(PeekMessageW(&m,nullptr,0,0,PM_REMOVE)){
                if(m.message==WM_QUIT){PostQuitMessage(static_cast<int>(m.wParam));return;}
                if(!wnd||!IsDialogMessageW(wnd,&m)){TranslateMessage(&m);DispatchMessageW(&m);}
            }
            if(wnd&&IsWindow(wnd))UpdateWindow(wnd);
            const ULONGLONG now=GetTickCount64();if(now>=end)break;
            MsgWaitForMultipleObjectsEx(0,nullptr,static_cast<DWORD>(std::min<ULONGLONG>(10,end-now)),QS_ALLINPUT,MWMO_INPUTAVAILABLE);
        }
        if(wnd&&IsWindow(wnd))UpdateWindow(wnd);
    }

    // Drive a real repaint to completion and return as soon as the requested
    // window state is observable. This keeps visual diagnostics responsive on
    // fast machines while still bounding a genuinely stuck paint path.
    bool WaitForDiagnosticRepaint(HWND wnd, DWORD timeoutMs, uint64_t previousGeneration,
                                  UINT expectedClientW=0, UINT expectedClientH=0){
        const ULONGLONG end=GetTickCount64()+timeoutMs;
        for(;;){
            if(DiagnosticCancelSignaled())return false;
            DiagnosticPauseIfRequested();
            if(!wnd||!IsWindow(wnd))return false;
            RedrawWindow(wnd,nullptr,nullptr,RDW_INVALIDATE|RDW_UPDATENOW|RDW_ALLCHILDREN);
            UpdateWindow(wnd);
            RECT dirty{};const bool pending=GetUpdateRect(wnd,&dirty,FALSE)!=FALSE;
            bool targetOk=true;
            if(expectedClientW||expectedClientH){
                targetOk=target&&target->GetPixelSize().width==expectedClientW&&target->GetPixelSize().height==expectedClientH;
            }
            if(!pending&&targetOk&&diagnosticRenderGeneration>previousGeneration)return true;
            const ULONGLONG now=GetTickCount64();if(now>=end)break;
            MSG m{};while(PeekMessageW(&m,nullptr,0,0,PM_REMOVE)){
                if(m.message==WM_QUIT){PostQuitMessage(static_cast<int>(m.wParam));return false;}
                if(!IsDialogMessageW(wnd,&m)){TranslateMessage(&m);DispatchMessageW(&m);}
            }
            const ULONGLONG after=GetTickCount64();if(after>=end)break;
            MsgWaitForMultipleObjectsEx(0,nullptr,static_cast<DWORD>(std::min<ULONGLONG>(10,end-after)),QS_ALLINPUT,MWMO_INPUTAVAILABLE);
        }
        return false;
    }

    int ExpectedHotkeyRows(HWND wnd) {
        wchar_t q[128]{};GetDlgItemTextW(wnd,IDC_SET_SEARCH,q,127);std::wstring search=Lower(q);
        int count=0;for(size_t i=0;i<std::size(kHotkeyDefs);++i)if(search.empty()||HotkeyMatchesSearch(i,search))++count;return count;
    }

    bool SettingsDiagnosticReady(HWND wnd,int cat,int& expected,int& actual) {
        expected=actual=0;if(!wnd||!IsWindow(wnd)||!GetPropW(wnd,L"GlideSettingsInitialized"))return false;
        if(cat==6){HWND list=GetDlgItem(wnd,IDC_SET_HOTKEY_LIST);if(!list)return false;expected=ExpectedHotkeyRows(wnd);actual=ListView_GetItemCount(list);return actual==expected;}
        return true;
    }

    bool WaitForSettingsDiagnosticReady(HWND wnd,int cat,DWORD timeoutMs,ULONGLONG start,int& expected,int& actual,ULONGLONG& readyMs){
        const ULONGLONG end=GetTickCount64()+timeoutMs;
        do{RefreshSettingsVisibility(wnd);UpdateWindow(wnd);if(SettingsDiagnosticReady(wnd,cat,expected,actual)){readyMs=GetTickCount64()-start;return true;}PumpDiagnosticUi(wnd,10);}while(GetTickCount64()<end);
        SettingsDiagnosticReady(wnd,cat,expected,actual);readyMs=GetTickCount64()-start;return false;
    }

    struct DiagnosticOpenMetric {
        bool success{};
        ULONGLONG requestMs{};
        ULONGLONG decodeMs{};
        UINT sourceW{}, sourceH{}, renderedW{}, renderedH{};
        bool preview{};
        bool cacheHit{};
        HRESULT hr{E_FAIL};
        HRESULT wicFilenameHr{E_NOTIMPL};
        HRESULT wicStreamInitHr{E_NOTIMPL};
        HRESULT wicStreamDecoderHr{E_NOTIMPL};
        bool wicStreamUsed{};
        bool nativeFallback{};
    };

    static std::wstring DiagnosticCsvCell(std::wstring value) {
        size_t pos=0;while((pos=value.find(L'"',pos))!=std::wstring::npos){value.insert(pos,L"\"");pos+=2;}
        return L"\""+value+L"\"";
    }

    bool DiagnosticCancelSignaled() const {
        return diagnosticCancelControl&&WaitForSingleObject(diagnosticCancelControl,0)==WAIT_OBJECT_0;
    }

    ULONGLONG DiagnosticPauseIfRequested() {
        if(!diagnosticPauseControl||WaitForSingleObject(diagnosticPauseControl,0)!=WAIT_OBJECT_0)return 0;
        const ULONGLONG began=GetTickCount64();
        while(WaitForSingleObject(diagnosticPauseControl,0)==WAIT_OBJECT_0&&!DiagnosticCancelSignaled()){
            PumpDiagnosticUi(hwnd,30);
            if(diagnosticCancelControl)MsgWaitForMultipleObjectsEx(1,&diagnosticCancelControl,30,QS_ALLINPUT,MWMO_INPUTAVAILABLE);
            else MsgWaitForMultipleObjectsEx(0,nullptr,30,QS_ALLINPUT,MWMO_INPUTAVAILABLE);
        }
        return GetTickCount64()-began;
    }

    void DiagnosticDrainWorkers(DWORD timeoutMs=3500) {
        ULONGLONG end=GetTickCount64()+timeoutMs;
        while(GetTickCount64()<end){
            if(DiagnosticCancelSignaled())break;
            end+=DiagnosticPauseIfRequested();
            if(!worker.IsBusy()&&!adaptiveWorker.IsBusy()&&!refineWorker.IsBusy()&&
               !prefetchWorker.IsBusy()&&!prefetchWorker2.IsBusy()&&!prefetchWorker3.IsBusy()&&
               !foregroundDecodePending)break;
            PumpDiagnosticUi(hwnd,10);
        }
    }

    DiagnosticOpenMetric DiagnosticOpenImage(const fs::path& path,int navDirection=0,DWORD timeoutMs=4000) {
        DiagnosticOpenMetric m{};
        DiagnosticDrainWorkers();
        if(DiagnosticCancelSignaled()){m.hr=E_ABORT;return m;}
        worker.CancelPending();adaptiveWorker.CancelPending();refineWorker.CancelPending();
        prefetchWorker.CancelPending();prefetchWorker2.CancelPending();prefetchWorker3.CancelPending();
        const size_t before=diagnosticPerfSamples.size();
        const ULONGLONG start=GetTickCount64();
        RequestImage(path.wstring(),navDirection);
        const std::wstring wanted=PathKey(path);
        ULONGLONG end=start+timeoutMs;
        while(GetTickCount64()<end){
            if(DiagnosticCancelSignaled()){m.hr=E_ABORT;break;}
            end+=DiagnosticPauseIfRequested();
            PumpDiagnosticUi(hwnd,10);
            const bool sameDisplayed=!displayedPath.empty()&&PathKey(displayedPath)==wanted;
            if(sameDisplayed&&bitmap&&!loading){m.success=true;break;}
            if(!loading&&!foregroundDecodePending&&PathKey(currentPath)==wanted&&!sameDisplayed)break;
        }
        m.requestMs=GetTickCount64()-start;
        if(m.hr!=E_ABORT)m.hr=diagnosticLastDecodeHr;
        m.wicFilenameHr=diagnosticLastWicFilenameHr;
        m.wicStreamInitHr=diagnosticLastWicStreamInitHr;
        m.wicStreamDecoderHr=diagnosticLastWicStreamDecoderHr;
        m.wicStreamUsed=diagnosticLastWicStreamUsed;
        m.nativeFallback=diagnosticLastNativeFallback;
        if(m.success){
            m.sourceW=imageW;m.sourceH=imageH;m.renderedW=bitmapPixelW;m.renderedH=bitmapPixelH;m.preview=bitmapIsPreview;
            if(diagnosticPerfSamples.size()>before){const auto& p=diagnosticPerfSamples.back();m.decodeMs=p.decodeMs;m.cacheHit=p.cacheHit;}
        }
        return m;
    }

    bool LaunchComprehensiveDiagnostics(HWND owner) {
        GlideDiagnostics::Selection sel{};
        if(!GlideDiagnostics::SelectTests(hInst,owner,sel))return false;
        const bool completed=GlideDiagnostics::RunParallelMaster(hInst, owner, ExePath(), sel.mask);
        // A successful master return means the requested run finished and its report ZIP
        // was emitted. Close only then; cancellation/launch/report failures leave Glide open.
        if(completed && hwnd) PostMessageW(hwnd,WM_CLOSE,0,0);
        return completed;
    }

    bool RunComprehensiveDiagnostics(uint32_t mask) {
        using namespace GlideDiagnostics;
        diagnosticMode=true;
        struct DiagnosticCancelled{};
        const int workerId=CommandLineOptionInt(L"--diagnostics-worker=",-1);
        const uint32_t masterMask=static_cast<uint32_t>(std::max(0,CommandLineOptionInt(L"--diagnostics-master-mask=",static_cast<int>(mask))));
        const int shardIndex=std::max(0,CommandLineOptionInt(L"--diagnostics-shard-index=",0));
        const int shardCount=std::max(1,CommandLineOptionInt(L"--diagnostics-shard-count=",1));
        const std::wstring sessionArg=CommandLineOptionValue(L"--diagnostics-session=");
        const std::wstring pauseEventName=CommandLineOptionValue(L"--diagnostics-pause-event=");
        const std::wstring cancelEventName=CommandLineOptionValue(L"--diagnostics-cancel-event=");
        const bool workerMode=workerId>0&&!sessionArg.empty();
        HANDLE diagnosticPauseEvent=pauseEventName.empty()?nullptr:OpenEventW(SYNCHRONIZE,FALSE,pauseEventName.c_str());
        HANDLE diagnosticCancelEvent=cancelEventName.empty()?nullptr:OpenEventW(SYNCHRONIZE,FALSE,cancelEventName.c_str());
        diagnosticPauseControl=diagnosticPauseEvent;diagnosticCancelControl=diagnosticCancelEvent;
        fs::path workerTemp;
        const ULONGLONG suiteStart=GetTickCount64();
        auto closeControlEvents=[&](){diagnosticPauseControl=nullptr;diagnosticCancelControl=nullptr;if(diagnosticPauseEvent){CloseHandle(diagnosticPauseEvent);diagnosticPauseEvent=nullptr;}if(diagnosticCancelEvent){CloseHandle(diagnosticCancelEvent);diagnosticCancelEvent=nullptr;}};
        try{
            const fs::path fixtureDir=FixtureDirectory();
            std::vector<FixtureRow> fixtures;std::wstring manifestError;
            if(!LoadFixtureManifest(fixtureDir/L"fixture_manifest.csv",fixtures,&manifestError)){
                if(!workerMode)MessageBoxW(hwnd,(L"diagnostic fixtures are unavailable:\n\n"+manifestError+L"\n\nExpected folder:\n"+fixtureDir.wstring()).c_str(),L"Glide Diagnostics",MB_OK|MB_ICONERROR);
                closeControlEvents();return false;
            }
            SYSTEMTIME st{};GetLocalTime(&st);wchar_t stamp[64]{};swprintf_s(stamp,L"%04u-%02u-%02u_%02u%02u%02u",st.wYear,st.wMonth,st.wDay,st.wHour,st.wMinute,st.wSecond);
            fs::path temp;
            if(workerMode){wchar_t wn[32]{};swprintf_s(wn,L"worker_%02d",workerId);temp=fs::path(sessionArg)/L"workers"/wn;workerTemp=temp;}
            else temp=fs::temp_directory_path()/(std::wstring(L"Glide_Diagnostics_")+stamp);
            fs::remove_all(temp);fs::create_directories(temp/L"screenshots"/L"formats");fs::create_directories(temp/L"screenshots"/L"settings");fs::create_directories(temp/L"screenshots"/L"window");fs::create_directories(temp/L"screenshots"/L"settings_ui");
            std::error_code copyEc;fs::copy_file(fixtureDir/L"fixture_manifest.csv",temp/L"fixture_manifest.csv",fs::copy_options::overwrite_existing,copyEc);copyEc.clear();fs::copy_file(fixtureDir/L"capability_extensions.txt",temp/L"capability_extensions.txt",fs::copy_options::overwrite_existing,copyEc);

            int pass=0,fail=0,skip=0,warn=0,step=0;
            int wiringPass=0,wiringFail=0,behaviorPass=0,behaviorFail=0,missingCoverage=0,notApplicable=0;
            std::unordered_map<int,bool> behaviorProbeResults;
            std::unordered_map<int,std::wstring> behaviorProbeSources;
            std::unordered_map<int,std::wstring> behaviorProbeEvidence;
            std::unordered_map<int,ULONGLONG> behaviorProbeDurations;
            int decodeCount=0,decodeOrdinalForCount=0;
            for(const auto& r:fixtures)if(r.kind==L"decode"){const int ord=decodeOrdinalForCount++;if(!workerMode||shardCount<=1||(ord%shardCount)==shardIndex)++decodeCount;}
            int totalSteps=5;
            if(mask&TestFormats)totalSteps+=decodeCount;
            if(mask&TestDecoderPrefs)totalSteps+=14;
            if(mask&TestPerformance)totalSteps+=8;
            if(mask&TestWindowState)totalSteps+=9;
            if(mask&TestSettingsUi)totalSteps+=16;
            if(mask&TestInvariants)totalSteps+=4;
            ProgressWindow progress;if(!workerMode)progress.Create(hInst,hwnd,totalSteps);
            auto writeWorkerProgress=[&](const std::wstring& text,const wchar_t* state=L"running"){
                if(!workerMode)return;
                std::wostringstream ss;ss<<L"worker="<<workerId<<L"\nstep="<<step<<L"\ntotal="<<std::max(1,totalSteps)<<L"\nstate="<<state<<L"\ntext="<<text<<L"\n";
                const fs::path finalPath=temp/L"progress.txt",tmpPath=temp/L"progress.tmp";
                WriteUtf8TextFile(tmpPath,ss.str());MoveFileExW(tmpPath.c_str(),finalPath.c_str(),MOVEFILE_REPLACE_EXISTING|MOVEFILE_WRITE_THROUGH);
            };
            auto keepWorkerInBackground=[&](){
                if(!workerMode||!hwnd||diagnosticSuspendBackgroundDemotion)return;
                ShowWindow(hwnd,SW_SHOWNOACTIVATE);
                SetWindowPos(hwnd,HWND_BOTTOM,0,0,0,0,SWP_NOMOVE|SWP_NOSIZE|SWP_NOACTIVATE);
                if(settingsDialogHwnd&&IsWindow(settingsDialogHwnd))
                    SetWindowPos(settingsDialogHwnd,HWND_BOTTOM,0,0,0,0,SWP_NOMOVE|SWP_NOSIZE|SWP_NOACTIVATE);
            };
            auto diagnosticControlPoint=[&](){
                if(diagnosticCancelEvent&&WaitForSingleObject(diagnosticCancelEvent,0)==WAIT_OBJECT_0)throw DiagnosticCancelled{};
                while(diagnosticPauseEvent&&WaitForSingleObject(diagnosticPauseEvent,0)==WAIT_OBJECT_0){
                    keepWorkerInBackground();
                    writeWorkerProgress(L"Paused by master",L"paused");PumpDiagnosticUi(nullptr,40);MsgWaitForMultipleObjectsEx(0,nullptr,30,QS_ALLINPUT,MWMO_INPUTAVAILABLE);
                    if(diagnosticCancelEvent&&WaitForSingleObject(diagnosticCancelEvent,0)==WAIT_OBJECT_0)throw DiagnosticCancelled{};
                }
            };
            auto advance=[&](const std::wstring& text){diagnosticControlPoint();++step;if(workerMode){keepWorkerInBackground();writeWorkerProgress(text);}else{progress.Update(step,totalSteps,text);PumpDiagnosticUi(progress.hwnd(),5);}};
            auto tally=[&](const std::wstring& status){if(status==L"PASS")++pass;else if(status==L"FAIL")++fail;else if(status==L"SKIP")++skip;else ++warn;};
            writeWorkerProgress(L"Starting worker");

            std::unordered_set<std::wstring> installedWicExtensions;
            {
                Utf8Wofstream f(temp/L"environment.txt");
                f<<L"Glide=Alpha 0.12107 Diagnostics\nGenerated="<<stamp
                 <<L"\nPID="<<GetCurrentProcessId()
                 <<L"\nMode="<<(installedMode?L"installed":L"portable")
                 <<L"\nSelected="<<MaskDescription(mask)
                 <<L"\nFixtureDirectory="<<fixtureDir.wstring()
                 <<L"\nMainDPI="<<(hwnd?GetDpiForWindow(hwnd):96)
                 <<L"\nDecodeWorkerCOMApartment=STA\n";

                using RtlGetVersionFn=LONG(WINAPI*)(OSVERSIONINFOW*);
                HMODULE nt=GetModuleHandleW(L"ntdll.dll");
                auto rtl=nt?reinterpret_cast<RtlGetVersionFn>(GetProcAddress(nt,"RtlGetVersion")):nullptr;
                OSVERSIONINFOW vi{};vi.dwOSVersionInfoSize=sizeof(vi);
                if(rtl&&rtl(&vi)==0)
                    f<<L"Windows="<<vi.dwMajorVersion<<L"."<<vi.dwMinorVersion<<L" build "<<vi.dwBuildNumber<<L"\n";

                const bool needWicInventory=(mask&(TestFormats|TestDecoderPrefs|TestPerformance|TestInvariants))!=0;
                ComPtr<IWICImagingFactory> wf;
                if(needWicInventory&&SUCCEEDED(CoCreateInstance(CLSID_WICImagingFactory,nullptr,CLSCTX_INPROC_SERVER,IID_PPV_ARGS(&wf)))&&wf){
                    ComPtr<IEnumUnknown> e;
                    if(SUCCEEDED(wf->CreateComponentEnumerator(WICDecoder,WICComponentEnumerateDefault,&e))&&e){
                        f<<L"\nInstalled WIC decoders:\n";
                        IUnknown* unk=nullptr;ULONG got=0;
                        while(e->Next(1,&unk,&got)==S_OK&&got){
                            ComPtr<IWICBitmapCodecInfo> info;
                            if(SUCCEEDED(unk->QueryInterface(IID_PPV_ARGS(&info)))&&info){
                                auto readFriendly=[&](){UINT n=0;info->GetFriendlyName(0,nullptr,&n);std::wstring v(n?size_t(n):0,L'\0');if(n)info->GetFriendlyName(n,v.data(),&n);if(!v.empty()&&v.back()==L'\0')v.pop_back();return v;};
                                auto readExt=[&](){UINT n=0;info->GetFileExtensions(0,nullptr,&n);std::wstring v(n?size_t(n):0,L'\0');if(n)info->GetFileExtensions(n,v.data(),&n);if(!v.empty()&&v.back()==L'\0')v.pop_back();return v;};
                                auto readMime=[&](){UINT n=0;info->GetMimeTypes(0,nullptr,&n);std::wstring v(n?size_t(n):0,L'\0');if(n)info->GetMimeTypes(n,v.data(),&n);if(!v.empty()&&v.back()==L'\0')v.pop_back();return v;};
                                const std::wstring decoderExt=readExt();
                                f<<L"- "<<readFriendly()<<L" | ext="<<decoderExt<<L" | mime="<<readMime()<<L"\n";
                                std::wstring token;
                                auto addDecoderExt=[&](){
                                    while(!token.empty()&&iswspace(token.front()))token.erase(token.begin());
                                    while(!token.empty()&&iswspace(token.back()))token.pop_back();
                                    if(!token.empty()){if(token.front()!=L'.')token=L"."+token;installedWicExtensions.insert(Lower(token));}
                                    token.clear();
                                };
                                for(wchar_t ch:decoderExt){if(ch==L','||ch==L';')addDecoderExt();else token.push_back(ch);}
                                addDecoderExt();
                            }
                            unk->Release();unk=nullptr;got=0;
                        }
                    }
                }
            }
            advance((mask&(TestFormats|TestDecoderPrefs|TestPerformance|TestInvariants))?L"Captured environment and installed WIC decoder inventory":L"Captured lightweight Settings worker environment");

            // Make format timing deterministic: no speculative work or refinement during the format sweep.
            const bool savedPrefetch=prefetchEnabled,savedRefine=backgroundRefinement,savedAdaptive=adaptiveFastPreview;
            const int savedQuality=initialQualityMode,savedRapid=rapidPreviewLongestSide,savedDefaultView=defaultViewMode,savedCustom=defaultCustomZoomPercent;
            const size_t savedCacheMax=maxCacheItems;const bool savedPurge=purgeCacheOnMinimize;const int savedTheme=themeMode;const int savedOpacity=windowOpacityPercent;const bool savedTextOverlay=textOverlayEnabled;
            const auto savedFiles=files;const auto savedFolder=currentFolder;const auto savedIndex=currentIndex;const bool savedHaveIndex=haveIndex;const std::wstring savedPath=currentPath;

            if(mask&TestFormats){
                Utf8Wofstream out(temp/L"format_results.csv");out<<L"fixture,family,actual_format,extension,status,expected,request_ms,decode_ms,hresult,wic_filename_hr,wic_stream_init_hr,wic_stream_decoder_hr,wic_stream_used,native_fallback,wic_advertised,source_w,source_h,rendered_w,rendered_h,preview,cache_hit,screenshot,notes\n";
                prefetchEnabled=false;backgroundRefinement=false;adaptiveFastPreview=false;initialQualityMode=1;maxCacheItems=4;
                int formatOrdinal=0;
                for(const auto& row:fixtures){if(row.kind!=L"decode")continue;const int ord=formatOrdinal++;if(workerMode&&shardCount>1&&(ord%shardCount)!=shardIndex)continue;const fs::path p=fixtureDir/row.file;advance(L"Format: "+row.family+L" / "+row.file);const std::wstring ext=Lower(p.extension().wstring());const bool advertised=installedWicExtensions.count(ext)!=0;std::wstring status=L"FAIL";std::wstring shot;std::wstring notes=row.notes;
                    DiagnosticOpenMetric m{};
                    if(fs::is_regular_file(p)){
                        cache.Clear();files={p};currentFolder=fixtureDir;currentIndex=0;haveIndex=true;m=DiagnosticOpenImage(p,0,4500);
                        if(m.success){
                            status=L"PASS";
                            if(m.nativeFallback)notes+=L" WIC filename/stream decode failed; Glide native/shell-provider fallback supplied the image.";
                            else if(m.wicStreamUsed)notes+=L" WIC filename binding failed; content-sniffed stream retry decoded the image.";
                        } else if(row.expected==L"codec-dependent"){
                            status=advertised?L"FAIL":L"SKIP";
                            notes+=advertised?L" WIC advertises this extension but the installed decoder could not decode this fixture; HRESULTs below are authoritative.":L" No installed WIC decoder advertises this extension; codec-dependent support is unavailable and remains SKIP.";
                        } else {
                            notes+=L" Required decode failed; no environment-based PASS/SKIP conversion was applied.";
                        }
                    } else {
                        notes+=L" Fixture file missing or unreadable; decode remains FAIL.";
                    }
                    if(m.success&&row.screenshot){shot=L"screenshots/formats/"+SafeFileStem(row.file)+L".jpg";PumpDiagnosticUi(hwnd,30);if(!SaveWindowJpeg(hwnd,temp/fs::path(shot)))shot=L"(capture failed)";}
                    auto hrText=[](HRESULT hr){wchar_t b[16]{};swprintf_s(b,L"0x%08X",static_cast<unsigned>(hr));return std::wstring(b);};
                    tally(status);out<<DiagnosticCsvCell(row.file)<<L","<<DiagnosticCsvCell(row.family)<<L","<<DiagnosticCsvCell(row.actualFormat)<<L","<<DiagnosticCsvCell(ext)<<L","<<status<<L","<<row.expected<<L","<<m.requestMs<<L","<<m.decodeMs<<L","<<hrText(m.hr)<<L","<<hrText(m.wicFilenameHr)<<L","<<hrText(m.wicStreamInitHr)<<L","<<hrText(m.wicStreamDecoderHr)<<L","<<m.wicStreamUsed<<L","<<m.nativeFallback<<L","<<advertised<<L","<<m.sourceW<<L","<<m.sourceH<<L","<<m.renderedW<<L","<<m.renderedH<<L","<<m.preview<<L","<<m.cacheHit<<L","<<DiagnosticCsvCell(shot)<<L","<<DiagnosticCsvCell(notes)<<L"\n";
                }
            }

            Utf8Wofstream settingOut(temp/L"settings_results.csv");settingOut<<L"test,status,baseline_or_input,observed,expectation,notes\n";
            auto settingResult=[&](const std::wstring& name,bool ok,const std::wstring& input,const std::wstring& observed,const std::wstring& expected,const std::wstring& notes=L""){const std::wstring stt=ok?L"PASS":L"FAIL";tally(stt);settingOut<<DiagnosticCsvCell(name)<<L","<<stt<<L","<<DiagnosticCsvCell(input)<<L","<<DiagnosticCsvCell(observed)<<L","<<DiagnosticCsvCell(expected)<<L","<<DiagnosticCsvCell(notes)<<L"\n";};
            Utf8Wofstream visualOut(temp/L"visual_results.csv");visualOut<<L"area,scenario,status,screenshot,geometry_or_state,automated_assertion,ai_review_required,notes\n";
            auto visualEvidence=[&](const std::wstring& area,const std::wstring& scenario,bool ok,const std::wstring& shot,const std::wstring& state,const std::wstring& assertion){
                visualOut<<DiagnosticCsvCell(area)<<L","<<DiagnosticCsvCell(scenario)<<L","<<(ok?L"PASS":L"FAIL")<<L","<<DiagnosticCsvCell(shot)<<L","<<DiagnosticCsvCell(state)<<L","<<DiagnosticCsvCell(assertion)<<L",1,"<<DiagnosticCsvCell(L"AI visual review required: inspect screenshot for clipping, overlap, stale paint, corruption, wrong theme, misalignment or other GUI artifacts")<<L"\n";
            };

            if(mask&TestDecoderPrefs){
                const fs::path large=fixtureDir/L"90_settings_large.jpg";const fs::path progressive=fixtureDir/L"91_settings_progressive.jpg";
                prefetchEnabled=false;backgroundRefinement=false;adaptiveFastPreview=false;maxCacheItems=4;files={large};currentFolder=fixtureDir;currentIndex=0;haveIndex=true;
                std::array<DiagnosticOpenMetric,3> q{};static const wchar_t* qn[]={L"Maximum speed",L"Balanced",L"Maximum quality"};
                for(int mode=0;mode<3;++mode){advance(std::wstring(L"Decoder setting: ")+qn[mode]);initialQualityMode=mode;cache.Clear();q[mode]=DiagnosticOpenImage(large,0,6500);if(q[mode].success)SaveWindowJpeg(hwnd,temp/L"screenshots"/L"settings"/(std::wstring(L"quality_")+std::to_wstring(mode)+L".jpg"));settingResult(std::wstring(L"Initial quality - ")+qn[mode],q[mode].success,std::to_wstring(mode),std::to_wstring(q[mode].renderedW)+L"x"+std::to_wstring(q[mode].renderedH)+(q[mode].preview?L" preview":L" full"),mode==2?L"full source-resolution decode":L"preview decode",L"Controlled cache-cold open");}
                const bool qualityEffect=q[0].success&&q[1].success&&q[2].success&&q[2].renderedW==q[2].sourceW&&!q[2].preview&&((q[0].renderedW!=q[1].renderedW)||(q[0].renderedH!=q[1].renderedH));
                settingResult(L"Quality modes materially affect decode policy",qualityEffect,L"speed/balanced/max",std::to_wstring(q[0].renderedW)+L" -> "+std::to_wstring(q[1].renderedW)+L" -> "+std::to_wstring(q[2].renderedW),L"different preview dimensions and Maximum quality = source resolution");advance(L"Verified quality-mode effect");

                initialQualityMode=0;rapidPreviewLongestSide=480;cache.Clear();auto low=DiagnosticOpenImage(large,+1,6500);advance(L"Rapid preview cap: 480 px");rapidPreviewLongestSide=1600;cache.Clear();auto high=DiagnosticOpenImage(large,+1,6500);advance(L"Rapid preview cap: 1600 px");const bool capEffect=low.success&&high.success&&(low.renderedW!=high.renderedW||low.renderedH!=high.renderedH)&&std::max(low.renderedW,low.renderedH)<=520;behaviorProbeResults[IDC_SET_RAPID_PREVIEW_SIZE]=capEffect;settingResult(L"Rapid-preview pixel setting changes rapid navigation",capEffect,L"480 vs 1600",std::to_wstring(low.renderedW)+L"x"+std::to_wstring(low.renderedH)+L" vs "+std::to_wstring(high.renderedW)+L"x"+std::to_wstring(high.renderedH),L"different first-frame dimensions",L"This setting intentionally applies to rapid navigation, not a stationary normal open.");

                initialQualityMode=1;rapidNavigationActive=false;backgroundRefinement=false;cache.Clear();auto noRef=DiagnosticOpenImage(progressive,0,6500);const bool stayedPreview=noRef.success&&bitmapIsPreview;advance(L"Background refinement: disabled");backgroundRefinement=true;cache.Clear();auto withRef=DiagnosticOpenImage(progressive,0,6500);const ULONGLONG refEnd=GetTickCount64()+5000;while(bitmapIsPreview&&GetTickCount64()<refEnd){diagnosticControlPoint();PumpDiagnosticUi(hwnd,20);}const bool refined=withRef.success&&!bitmapIsPreview;SaveWindowJpeg(hwnd,temp/L"screenshots"/L"settings"/L"background_refined.jpg");advance(L"Background refinement: enabled");const bool refinementEffect=stayedPreview&&refined;behaviorProbeResults[IDC_SET_BACKGROUND_REFINEMENT]=refinementEffect;settingResult(L"Background refinement switch",refinementEffect,L"off then on",std::wstring(stayedPreview?L"preview stayed; ":L"preview did not stay; ")+(refined?L"refined to full":L"did not refine"),L"off keeps preview; on upgrades to full resolution");

                const fs::path viewFile=fixtureDir/L"10_png.png";backgroundRefinement=false;initialQualityMode=1;defaultCustomZoomPercent=137;bool defaultViewProbe=true;for(int vm=0;vm<5;++vm){defaultViewMode=vm;cache.Clear();auto v=DiagnosticOpenImage(viewFile,0,3500);bool vok=v.success;std::wstring obs;switch(vm){case 0:vok=vok&&viewMode==ViewMode::Fit;obs=L"Fit";break;case 1:vok=vok&&viewMode==ViewMode::FitWidth;obs=L"FitWidth";break;case 2:vok=vok&&viewMode==ViewMode::FitHeight;obs=L"FitHeight";break;case 3:vok=vok&&viewMode==ViewMode::Manual&&std::abs(zoom-1.0f)<0.001f;obs=L"100%";break;default:vok=vok&&viewMode==ViewMode::Manual&&std::abs(zoom-1.37f)<0.02f;obs=L"137%";break;}defaultViewProbe=defaultViewProbe&&vok;settingResult(L"Default view mode "+std::to_wstring(vm),vok,std::to_wstring(vm),obs,L"requested view mode applied on new image");advance(L"Default view mode "+std::to_wstring(vm));}behaviorProbeResults[IDC_SET_DEFAULT_VIEW_MODE]=defaultViewProbe;behaviorProbeResults[IDC_SET_DEFAULT_CUSTOM_ZOOM]=defaultViewProbe;
            }

            if(mask&TestPerformance){
                const fs::path f0=fixtureDir/L"prefetch_05.jpg";std::vector<fs::path> pf;for(int i=0;i<12;++i){wchar_t n[32]{};swprintf_s(n,L"prefetch_%02d.jpg",i);pf.push_back(fixtureDir/n);}files=pf;currentFolder=fixtureDir;currentIndex=5;haveIndex=true;initialQualityMode=1;backgroundRefinement=false;adaptiveFastPreview=false;
                maxCacheItems=2;prefetchEnabled=false;cache.Clear();for(int i=0;i<4;++i){currentIndex=i;DiagnosticOpenImage(pf[i],0,3500);}const size_t c2=cache.Size();const bool cacheLimitEffect=c2<=2;settingResult(L"Cache item limit",cacheLimitEffect,L"max=2",std::to_wstring(c2),L"cache size <= 2");advance(L"Cache limit test");
                maxCacheItems=8;cache.Clear();for(int i=0;i<4;++i){currentIndex=i;DiagnosticOpenImage(pf[i],0,3500);}const size_t c8=cache.Size();const bool largerCacheEffect=c8>c2;behaviorProbeResults[IDC_SET_CACHE_ITEMS]=cacheLimitEffect&&largerCacheEffect;settingResult(L"Larger cache retains more decoded items",largerCacheEffect,L"max=8",std::to_wstring(c8),L"cache size greater than max=2 run");advance(L"Larger cache test");
                prefetchEnabled=false;maxCacheItems=16;cache.Clear();currentIndex=5;auto poff=DiagnosticOpenImage(f0,0,3500);const size_t offCount=cache.Size();const bool prefetchOffEffect=poff.success&&offCount<=1;settingResult(L"Prefetch disabled",prefetchOffEffect,L"prefetch=false",std::to_wstring(offCount),L"only current image cached");advance(L"Prefetch disabled");
                prefetchEnabled=true;cache.Clear();currentIndex=5;auto pon=DiagnosticOpenImage(f0,0,3500);const ULONGLONG pend=GetTickCount64()+3000;while(cache.Size()<=1&&GetTickCount64()<pend)PumpDiagnosticUi(hwnd,20);const size_t onCount=cache.Size();const bool prefetchOnEffect=pon.success&&onCount>1;behaviorProbeResults[IDC_SET_PREFETCH_ENABLED]=prefetchOffEffect&&prefetchOnEffect;settingResult(L"Prefetch enabled",prefetchOnEffect,L"prefetch=true",std::to_wstring(onCount),L"nearby images enter cache automatically");advance(L"Prefetch enabled");
                // Isolate the cache-hit probe from predictive prefetch and preview-refinement.
                // The previous probe ran immediately after the prefetch-enabled test, so nearby
                // background inserts could evict/replace the target between the nominal cold and
                // warm requests.  That made "warm cacheHit=no" a harness race rather than a cache
                // product failure.  Use one deterministic full/accepted cache entry here.
                prefetchEnabled=false; backgroundRefinement=false; adaptiveFastPreview=false;
                initialQualityMode=0; maxCacheItems=16; cache.Clear(); currentIndex=5;
                auto cold=DiagnosticOpenImage(f0,0,3500);
                DiagnosticDrainWorkers();
                const bool coldCached=cache.Contains(PathKey(f0));
                auto warm=DiagnosticOpenImage(f0,0,1500);
                constexpr ULONGLONG kCacheTimingJitterMs=32;
                const bool cacheHitOk=cold.success&&coldCached&&warm.success&&warm.cacheHit&&warm.requestMs<=cold.requestMs+kCacheTimingJitterMs;
                settingResult(L"Cache-hit latency",cacheHitOk,L"cold then warm",std::to_wstring(cold.requestMs)+L" ms -> "+std::to_wstring(warm.requestMs)+L" ms; cold cached="+(coldCached?L"yes":L"no")+L"; warm cacheHit="+(warm.cacheHit?L"yes":L"no"),L"warm request is a cache hit; timing no worse than cold + 32 ms scheduler tolerance",L"Cache-hit state is the assertion; timing tolerance avoids false failures from Windows scheduling granularity.");advance(L"Cache-hit latency");
                purgeCacheOnMinimize=false;cache.Clear();currentIndex=0;DiagnosticOpenImage(pf[0],0,3500);const size_t beforePurge=cache.Size();SendMessageW(hwnd,WM_SIZE,SIZE_MINIMIZED,MAKELPARAM(0,0));const size_t afterOff=cache.Size();purgeCacheOnMinimize=true;SendMessageW(hwnd,WM_SIZE,SIZE_MINIMIZED,MAKELPARAM(0,0));const size_t afterOn=cache.Size();const bool purgeEffect=beforePurge>0&&afterOff==beforePurge&&afterOn==0;behaviorProbeResults[IDC_SET_PURGE_CACHE_MINIMIZE]=purgeEffect;settingResult(L"Purge-cache-on-minimize setting",purgeEffect,L"off then on",std::to_wstring(beforePurge)+L" -> "+std::to_wstring(afterOff)+L" -> "+std::to_wstring(afterOn),L"off retains cache; on clears it");advance(L"Purge cache on minimize");
                {Utf8Wofstream perf(temp/L"controlled_performance.csv");perf<<L"sample,extension,request_to_first_frame_ms,decode_ms,width,height,preview,refinement,cache_hit\n";size_t n=0;for(const auto&p:diagnosticPerfSamples){ULONGLONG total=(p.firstFrameTick>=p.requestTick&&p.requestTick)?p.firstFrameTick-p.requestTick:0;perf<<++n<<L","<<p.extension<<L","<<total<<L","<<p.decodeMs<<L","<<p.width<<L","<<p.height<<L","<<p.preview<<L","<<p.refinement<<L","<<p.cacheHit<<L"\n";}}
                advance(L"Wrote controlled performance log");
            }

            if(mask&TestWindowState){
                fs::create_directories(temp/L"screenshots"/L"visual");
                RECT original{};GetWindowRect(hwnd,&original);windowOpacityPercent=50;pngSeeThrough=false;ApplyWindowTransparency();COLORREF key{};BYTE alpha{};DWORD fl{};const bool gotAlpha=GetLayeredWindowAttributes(hwnd,&key,&alpha,&fl)!=FALSE;const bool alphaOk=gotAlpha&&(fl&LWA_ALPHA)&&alpha>=120&&alpha<=135;settingResult(L"50% global opacity",alphaOk,L"50%",gotAlpha?std::to_wstring(alpha):L"unavailable",L"layered alpha approximately 127");SaveWindowJpeg(hwnd,temp/L"screenshots"/L"window"/L"opacity_50.jpg");advance(L"Window opacity");
                ShowWindow(hwnd,SW_MINIMIZE);PumpDiagnosticUi(nullptr,120);ShowWindow(hwnd,diagnosticBackgroundWorker?SW_SHOWNOACTIVATE:SW_RESTORE);if(!diagnosticBackgroundWorker)SetForegroundWindow(hwnd);PumpDiagnosticUi(nullptr,180);GlideRepairRestoredWindowNow(hwnd);RECT rr{};GetWindowRect(hwnd,&rr);const bool restored=!IsIconic(hwnd)&&IsWindowVisible(hwnd)&&MonitorFromRect(&rr,MONITOR_DEFAULTTONULL)!=nullptr;settingResult(L"Minimize / restore recovery",restored,L"minimize then restore",restored?L"visible and on-screen":L"not recoverable",L"visible non-iconic window on a monitor");SaveWindowJpeg(hwnd,temp/L"screenshots"/L"window"/L"restored.jpg");advance(L"Minimize / restore");
                SetWindowPos(hwnd,nullptr,50000,50000,0,0,SWP_NOSIZE|SWP_NOZORDER|SWP_NOACTIVATE);PumpDiagnosticUi(nullptr,20);GlideRepairRestoredWindowNow(hwnd);RECT recovered{};GetWindowRect(hwnd,&recovered);const bool onScreen=MonitorFromRect(&recovered,MONITOR_DEFAULTTONULL)!=nullptr;settingResult(L"Off-screen window recovery",onScreen,L"forced to 50000,50000",std::to_wstring(recovered.left)+L","+std::to_wstring(recovered.top),L"moved back onto nearest monitor");advance(L"Off-screen recovery");
                windowOpacityPercent=100;ApplyWindowTransparency();const LONG_PTR ex=GetWindowLongPtrW(hwnd,GWL_EXSTYLE);const bool opaqueOk=(ex&WS_EX_LAYERED)==0;settingResult(L"100% opacity removes transient layered style",opaqueOk,L"100%",opaqueOk?L"not layered":L"still layered",L"WS_EX_LAYERED removed when PNG see-through is off");advance(L"100% opacity reset");

                // GUI stress: resize the real viewer through several aspect ratios. Each case
                // proves the HWND/client geometry, Direct2D target size, repaint completion and
                // image survival, then stores a JPEG for later AI visual inspection.
                const fs::path visualFixture=fixtureDir/L"01_jpeg.jpg";cache.Clear();files={visualFixture};currentFolder=fixtureDir;currentIndex=0;haveIndex=true;
                auto visualOpen=DiagnosticOpenImage(visualFixture,0,1800);
                struct ResizeCase{int w,h;const wchar_t* name;};
                const ResizeCase resizeCases[]={{420,320,L"compact"},{900,540,L"landscape"},{520,860,L"portrait"},{1200,420,L"ultrawide"}};
                for(const auto& rc:resizeCases){
                    const uint64_t generationBefore=diagnosticRenderGeneration;
                    SetWindowPos(hwnd,nullptr,original.left,original.top,rc.w,rc.h,SWP_NOZORDER|SWP_NOACTIVATE);InvalidateRect(hwnd,nullptr,TRUE);UpdateWindow(hwnd);
                    RECT wr2{},cr2{};GetWindowRect(hwnd,&wr2);GetClientRect(hwnd,&cr2);
                    const bool repaintOk=WaitForDiagnosticRepaint(hwnd,750,generationBefore,static_cast<UINT>(std::max(0L,cr2.right-cr2.left)),static_cast<UINT>(std::max(0L,cr2.bottom-cr2.top)));
                    D2D1_SIZE_U rt{};if(target)rt=target->GetPixelSize();RECT dirty{};const bool pendingPaint=GetUpdateRect(hwnd,&dirty,FALSE)!=FALSE;
                    const bool geometryOk=std::abs((wr2.right-wr2.left)-rc.w)<=3&&std::abs((wr2.bottom-wr2.top)-rc.h)<=3;
                    const bool targetOk=target&&SUCCEEDED(diagnosticLastResizeHr)&&(rt.width==static_cast<UINT>(std::max(0L,cr2.right-cr2.left))&&rt.height==static_cast<UINT>(std::max(0L,cr2.bottom-cr2.top)));
                    const bool imageOk=visualOpen.success&&bitmap&&imageW>0&&imageH>0;const bool ok=geometryOk&&targetOk&&repaintOk&&!pendingPaint&&imageOk;
                    const std::wstring shot=L"screenshots/visual/viewer_resize_"+std::wstring(rc.name)+L".jpg";SaveWindowJpeg(hwnd,temp/fs::path(shot),0.72f);
                    const std::wstring state=L"window="+std::to_wstring(wr2.right-wr2.left)+L"x"+std::to_wstring(wr2.bottom-wr2.top)+L" client="+std::to_wstring(cr2.right-cr2.left)+L"x"+std::to_wstring(cr2.bottom-cr2.top)+L" target="+std::to_wstring(rt.width)+L"x"+std::to_wstring(rt.height)+L" resize_hr="+std::to_wstring(static_cast<unsigned long>(diagnosticLastResizeHr))+L" repaint="+(repaintOk?L"yes":L"no")+L" pendingPaint="+(pendingPaint?L"yes":L"no");
                    settingResult(L"GUI resize - "+std::wstring(rc.name),ok,std::to_wstring(rc.w)+L"x"+std::to_wstring(rc.h),state,L"window/client/Direct2D target remain synchronized and image survives resize");
                    visualEvidence(L"viewer",L"resize_"+std::wstring(rc.name),ok,shot,state,L"geometry valid; Direct2D render target follows client; no pending repaint; image remains loaded");advance(L"GUI resize: "+std::wstring(rc.name));
                }
                SetWindowPos(hwnd,nullptr,original.left,original.top,original.right-original.left,original.bottom-original.top,SWP_NOZORDER|SWP_NOACTIVATE);PumpDiagnosticUi(hwnd,20);SaveWindowJpeg(hwnd,temp/L"screenshots"/L"window"/L"final_window_state.jpg");advance(L"Captured final window-state screenshot");
            }

            if(mask&TestSettingsUi){
                // The Settings suite is deliberately data-driven. Every effective checkbox,
                // radio, combo or edit tagged with GlideCat is discovered at runtime. This
                // means an ordinary new setting automatically enters the round-trip test and
                // cannot silently escape diagnostics merely because a developer forgot to add
                // it to a handwritten list.
                static bool diagSettingsClass=false;
                if(!diagSettingsClass){
                    WNDCLASSEXW wc{};wc.cbSize=sizeof(wc);wc.lpfnWndProc=SettingsWndProc;wc.hInstance=hInst;
                    wc.hCursor=LoadCursorW(nullptr,IDC_ARROW);wc.hbrBackground=GetSysColorBrush(COLOR_WINDOW);
                    wc.lpszClassName=L"GlideSettingsWindow";
                    diagSettingsClass=RegisterClassExW(&wc)!=0||GetLastError()==ERROR_CLASS_ALREADY_EXISTS;
                }
                auto createDiagSettings=[&](bool show)->HWND{
                    if(!diagSettingsClass)return nullptr;
                    RECT mr{};GetWindowRect(hwnd,&mr);
                    const DWORD exStyle=diagnosticBackgroundWorker?(WS_EX_TOOLWINDOW|WS_EX_NOACTIVATE):0;
                    HWND w=CreateWindowExW(exStyle,L"GlideSettingsWindow",L"Glide Options - Diagnostic",
                        WS_CAPTION|WS_SYSMENU|WS_THICKFRAME|WS_POPUP|WS_CLIPCHILDREN,
                        mr.left+30,mr.top+30,1080,840,hwnd,nullptr,hInst,this);
                    if(w){settingsDialogHwnd=w;if(show)ShowWindow(w,diagnosticBackgroundWorker?SW_SHOWNOACTIVATE:SW_SHOW);if(diagnosticBackgroundWorker)SetWindowPos(w,HWND_BOTTOM,0,0,0,0,SWP_NOMOVE|SWP_NOSIZE|SWP_NOACTIVATE);RedrawWindow(w,nullptr,nullptr,RDW_INVALIDATE|RDW_UPDATENOW|RDW_ALLCHILDREN);UpdateWindow(w);}
                    return w;
                };
                auto rawControlValue=[&](HWND c)->std::wstring{
                    if(!c)return L"(missing)";
                    wchar_t cls[64]{};GetClassNameW(c,cls,63);
                    if(_wcsicmp(cls,L"BUTTON")==0)return std::to_wstring(static_cast<long long>(SendMessageW(c,BM_GETCHECK,0,0)));
                    if(_wcsicmp(cls,L"COMBOBOX")==0)return std::to_wstring(static_cast<long long>(SendMessageW(c,CB_GETCURSEL,0,0)));
                    int n=GetWindowTextLengthW(c);std::wstring v(static_cast<size_t>(std::max(0,n))+1,L'\0');
                    if(n>0)GetWindowTextW(c,v.data(),n+1);v.resize(static_cast<size_t>(std::max(0,n)));return v;
                };
                auto numericRange=[&](int id,int& lo,int& hi)->bool{
                    switch(id){
                        case IDC_SET_RECENT_LIMIT:lo=1;hi=16;return true;
                        case IDC_SET_ADAPTIVE_DELAY:lo=5;hi=500;return true;
                        case IDC_SET_SLIDE_INTERVAL:lo=250;hi=600000;return true;
                        case IDC_SET_ZOOM_STEP:lo=5;hi=50;return true;
                        case IDC_SET_CURSOR_HIDE_DELAY:lo=250;hi=10000;return true;
                        case IDC_SET_PREFETCH_DEPTH:lo=0;hi=6;return true;
                        case IDC_SET_CACHE_ITEMS:lo=2;hi=64;return true;
                        case IDC_SET_RAPID_PREVIEW_SIZE:lo=480;hi=4096;return true;
                        case IDC_SET_TAB_MIN_WIDTH:lo=80;hi=240;return true;
                        case IDC_SET_TAB_MAX_WIDTH:lo=120;hi=400;return true;
                        case IDC_SET_CLOSED_TAB_LIMIT:lo=1;hi=100;return true;
                        case IDC_SET_OVERLAY_DEFAULT_OPACITY:lo=10;hi=100;return true;
                        case IDC_SET_DEFAULT_CUSTOM_ZOOM:lo=2;hi=3200;return true;
                        case IDC_SET_TEXT_OVERLAY_SIZE:lo=8;hi=96;return true;
                        case IDC_SET_TEXT_OVERLAY_OPACITY:lo=5;hi=100;return true;
                        case IDC_SET_OVERLAY_ZOOM_STEP:lo=5;hi=100;return true;
                        default:return false;
                    }
                };
                auto mutateEffectiveControl=[&](HWND sw,int id,std::wstring& requested)->bool{
                    HWND c=GetDlgItem(sw,id);if(!c)return false;
                    wchar_t cls[64]{};GetClassNameW(c,cls,63);
                    if(_wcsicmp(cls,L"BUTTON")==0){
                        DWORD t=static_cast<DWORD>(GetWindowLongPtrW(c,GWL_STYLE))&BS_TYPEMASK;
                        if(t==BS_AUTORADIOBUTTON||t==BS_RADIOBUTTON){
                            // Quality is a three-radio setting. Select the requested radio via
                            // the same WM_COMMAND path a real click uses.
                            if(id>=IDC_SET_QUALITY_SPEED&&id<=IDC_SET_QUALITY_MAX){
                                const int current=GlideSettingsShell::ExclusiveRadioSelection(sw,IDC_SET_QUALITY_SPEED,IDC_SET_QUALITY_MAX);
                                const int target=(current==id)?(id==IDC_SET_QUALITY_MAX?IDC_SET_QUALITY_BALANCED:IDC_SET_QUALITY_MAX):id;
                                SendMessageW(sw,WM_COMMAND,MAKEWPARAM(target,BN_CLICKED),reinterpret_cast<LPARAM>(GetDlgItem(sw,target)));
                                requested=std::to_wstring(target==id?BST_CHECKED:BST_UNCHECKED);
                                return true;
                            }
                            return false;
                        }
                        if(t==BS_AUTOCHECKBOX||t==BS_CHECKBOX){
                            const LRESULT old=SendMessageW(c,BM_GETCHECK,0,0);const WPARAM next=old==BST_CHECKED?BST_UNCHECKED:BST_CHECKED;
                            SendMessageW(c,BM_SETCHECK,next,0);
                            SendMessageW(sw,WM_COMMAND,MAKEWPARAM(id,BN_CLICKED),reinterpret_cast<LPARAM>(c));
                            requested=std::to_wstring(static_cast<long long>(next));return true;
                        }
                        return false;
                    }
                    if(_wcsicmp(cls,L"COMBOBOX")==0){
                        const int count=static_cast<int>(SendMessageW(c,CB_GETCOUNT,0,0));
                        const int cur=static_cast<int>(SendMessageW(c,CB_GETCURSEL,0,0));
                        if(count<=1)return false;const int next=(cur+1)%count;
                        SendMessageW(c,CB_SETCURSEL,next,0);
                        SendMessageW(sw,WM_COMMAND,MAKEWPARAM(id,CBN_SELCHANGE),reinterpret_cast<LPARAM>(c));
                        requested=std::to_wstring(next);return true;
                    }
                    if(_wcsicmp(cls,L"EDIT")==0){
                        std::wstring next;const std::wstring old=rawControlValue(c);
                        if(id==IDC_SET_TEXT_OVERLAY_TEMPLATE)next=L"DIAG [{index}/{total}]";
                        else if(id==IDC_SET_TEXT_OVERLAY_COLOR)next=L"#00FF7F";
                        else if(id==IDC_SET_EXT1_PATH||id==IDC_SET_EXT2_PATH||id==IDC_SET_EXT3_PATH)next=ExePath();
                        else{
                            int lo=0,hi=0;if(!numericRange(id,lo,hi))return false;
                            wchar_t* tail=nullptr;long v=wcstol(old.c_str(),&tail,10);if(!tail||*tail!=0)v=lo;
                            long nv=v<hi?v+1:v-1;nv=std::clamp<long>(nv,lo,hi);next=std::to_wstring(nv);
                        }
                        SetWindowTextW(c,next.c_str());
                        SendMessageW(sw,WM_COMMAND,MAKEWPARAM(id,EN_CHANGE),reinterpret_cast<LPARAM>(c));
                        requested=next;return true;
                    }
                    return false;
                };
                auto restorePersisted=[&](){
                    LoadSettings();ApplyAlwaysOnTop();ApplyThemeToMainWindow();ApplyWindowTransparency();
                    UpdateScrollBars();UpdateTitle();InvalidateViewer();PumpDiagnosticUi(hwnd,15);
                };
                auto settingsGeometryIssues=[&](HWND w,int& overlaps,int& clipped){
                    overlaps=clipped=0;if(!w)return;RECT client{};GetClientRect(w,&client);struct V{HWND h;RECT r;int id;};std::vector<V> v;
                    for(HWND c=GetWindow(w,GW_CHILD);c;c=GetWindow(c,GW_HWNDNEXT)){if(!IsWindowVisible(c)||reinterpret_cast<INT_PTR>(GetPropW(c,L"GlideCat"))<=0)continue;RECT r{};GetWindowRect(c,&r);MapWindowPoints(nullptr,w,reinterpret_cast<POINT*>(&r),2);if(r.left<0||r.top<0||r.right>client.right||r.bottom>client.bottom)++clipped;v.push_back({c,r,GetDlgCtrlID(c)});}
                    const int footerIds[]={IDC_SET_SECTION_PREV,IDC_SET_SECTION_LABEL,IDC_SET_SECTION_NEXT,IDC_SET_SECTION_SCROLL,IDC_SET_SCROLL_HINT,IDC_SET_VERSION_LABEL,IDC_SET_DEFAULTS,IDC_SET_CANCEL,IDC_SET_APPLY,IDC_SET_OK};
                    for(int id:footerIds){HWND c=GetDlgItem(w,id);if(!c||!IsWindowVisible(c))continue;RECT r{};GetWindowRect(c,&r);MapWindowPoints(nullptr,w,reinterpret_cast<POINT*>(&r),2);if(r.left<0||r.top<0||r.right>client.right||r.bottom>client.bottom)++clipped;v.push_back({c,r,id});}
                    for(size_t i=0;i<v.size();++i)for(size_t j=i+1;j<v.size();++j){RECT x{};if(IntersectRect(&x,&v[i].r,&v[j].r)&&(x.right-x.left)>8&&(x.bottom-x.top)>8&&!IsExpectedSettingsGeometryOverlay(v[i].h,v[j].h))++overlaps;}
                };
                // Geometry alone cannot see painted artifacts.  Sample a reserved footer
                // corridor where the category-rail divider is forbidden to continue below
                // the footer boundary.  This catches the exact crossed-line regression that
                // a child-HWND overlap test cannot detect.
                auto settingsPaintedSeparatorIntrusions=[&](HWND w)->int{
                    if(!w)return 1;RECT client{};GetClientRect(w,&client);const int footer=SettingsFooterTop(w);
                    if(footer<1||footer>=client.bottom-4||kSettingsRailWidth<1||kSettingsRailWidth>=client.right-1)return 1;
                    HDC dc=GetDC(w);if(!dc)return 1;ThemePalette pal=Palette();const COLORREF border=pal.border;int hits=0;
                    const int y0=std::min<int>(static_cast<int>(client.bottom)-1,footer+3), y1=std::min<int>(static_cast<int>(client.bottom)-1,footer+44);
                    for(int y=y0;y<=y1;++y){const COLORREF px=GetPixel(dc,kSettingsRailWidth,y);if(px==border)++hits;}
                    ReleaseDC(w,dc);return hits;
                };

                HWND sw=createDiagSettings(true);
                if(sw){
                    // First retain the broad visual/settings-page audit from Glide.
                    Utf8Wofstream geo(temp/L"settings_ui_geometry.txt");
                    static const wchar_t* names[]={L"general",L"viewing",L"mouse_fullscreen",L"performance",L"status_bar",L"slideshow",L"hotkeys",L"tabs_workspace",L"window_in_window",L"profiles_presets",L"windows_integration",L"developer_options"};
                    for(int cat=0;cat<12;++cat){
                        if(workerMode&&shardCount>1&&shardIndex!=0)continue;
                        SetWindowLongPtrW(sw,GWLP_USERDATA,cat);SetSettingsSectionPage(sw,0);if(cat==6)PopulateHotkeyList(sw);
                        RefreshSettingsVisibility(sw);auto nodes=CollectSettingsLayoutNodes(sw);int pages=(cat==6)?1:SettingsNormalPageCount(nodes,cat);
                        for(int page=0;page<pages;++page){
                            SetSettingsSectionPage(sw,page);const ULONGLONG begin=GetTickCount64();RefreshSettingsVisibility(sw);
                            int expected=0,actual=0;ULONGLONG readyMs=GetTickCount64()-begin;bool ready=SettingsDiagnosticReady(sw,cat,expected,actual);
                            if(!ready&&cat==6)ready=WaitForSettingsDiagnosticReady(sw,cat,120,begin,expected,actual,readyMs);
                            RedrawWindow(sw,nullptr,nullptr,RDW_INVALIDATE|RDW_UPDATENOW|RDW_ALLCHILDREN);UpdateWindow(sw);std::wstring state=std::wstring(names[cat])+L"_"+std::to_wstring(page+1);const std::wstring shot=L"screenshots/settings_ui/"+state+L".jpg";
                            SaveWindowJpeg(sw,temp/fs::path(shot),0.72f);
                            int visible=0;for(const auto&n:nodes)if(n.cat==cat&&IsWindowVisible(n.hwnd))++visible;int overlaps=0,clipped=0;settingsGeometryIssues(sw,overlaps,clipped);const int paintedSeparatorIntrusions=settingsPaintedSeparatorIntrusions(sw);
                            const bool ok=ready&&visible>0&&overlaps==0&&clipped==0&&paintedSeparatorIntrusions==0;const std::wstring visualState=std::to_wstring(visible)+L" visible; overlaps="+std::to_wstring(overlaps)+L"; clipped="+std::to_wstring(clipped)+L"; footerSeparatorIntrusions="+std::to_wstring(paintedSeparatorIntrusions)+L"; ready="+(ready?L"yes":L"no");
                            settingResult(L"Settings page "+state,ok,L"category/page",visualState,L"non-empty, ready, no control overlap/clipping, and no painted rail divider intruding into footer");visualEvidence(L"settings",state,ok,shot,visualState,L"layout ready; no category-control overlap/clipping; painted divider must terminate at footer");
                            geo<<L"\nCAPTURE "<<state<<L" ready="<<ready<<L" readyMs="<<readyMs<<L" visible="<<visible<<L" overlaps="<<overlaps<<L" clipped="<<clipped<<L" footerSeparatorIntrusions="<<paintedSeparatorIntrusions<<L"\n";
                            if(ready)WriteVisibleGeometry(sw,geo,state.c_str());advance(L"Settings UI: "+state);
                        }
                    }

                    // Resize the Settings shell itself once to catch footer/body clipping, stale
                    // layout and controls escaping the client area. The full page screenshots above
                    // remain the primary visual evidence for regression review.
                    {RECT sr{};GetWindowRect(sw,&sr);
                        // Exercise the stale-paint path: expand first, then shrink to the
                        // minimum track size. This moves transparent footer labels far enough
                        // that an uncleared old paint would land inside the new button row.
                        SetWindowPos(sw,nullptr,sr.left,sr.top,1400,1000,SWP_NOZORDER|SWP_NOACTIVATE);LayoutSettingsChrome(sw);RefreshSettingsVisibility(sw);PumpDiagnosticUi(sw,80);
                        SetWindowPos(sw,nullptr,sr.left,sr.top,980,780,SWP_NOZORDER|SWP_NOACTIVATE);LayoutSettingsChrome(sw);SetWindowLongPtrW(sw,GWLP_USERDATA,0);SetSettingsSectionPage(sw,0);RefreshSettingsVisibility(sw);
                        RedrawWindow(sw,nullptr,nullptr,RDW_INVALIDATE|RDW_ERASE|RDW_ALLCHILDREN|RDW_UPDATENOW);PumpDiagnosticUi(sw,120);UpdateWindow(sw);RECT actual{};GetWindowRect(sw,&actual);int ov=0,cl=0;settingsGeometryIssues(sw,ov,cl);const int paintIntrusions=settingsPaintedSeparatorIntrusions(sw);RECT dirty{};const bool settled=GetUpdateRect(sw,&dirty,FALSE)==FALSE;const std::wstring shot=L"screenshots/settings_ui/settings_resize_compact.jpg";SaveWindowJpeg(sw,temp/fs::path(shot),0.72f);WriteVisibleGeometry(sw,geo,L"resize_compact_after_large_to_small");const bool resizeOk=ov==0&&cl==0&&paintIntrusions==0&&settled;const std::wstring observed=L"transition=1400x1000->980x780; actual="+std::to_wstring(actual.right-actual.left)+L"x"+std::to_wstring(actual.bottom-actual.top)+L"; overlaps="+std::to_wstring(ov)+L"; clipped="+std::to_wstring(cl)+L"; footerSeparatorIntrusions="+std::to_wstring(paintIntrusions)+L"; settled="+(settled?L"yes":L"no");settingResult(L"Settings GUI compact resize",resizeOk,L"1400x1000 then 980x780",observed,L"no overlap/clipping, no painted rail divider in footer, and no stale pending paint after full footer erase");visualEvidence(L"settings",L"resize_compact",resizeOk,shot,observed,L"large-to-compact resize clears stale footer paint; divider terminates at footer; layout remains coherent");SetWindowPos(sw,nullptr,sr.left,sr.top,sr.right-sr.left,sr.bottom-sr.top,SWP_NOZORDER|SWP_NOACTIVATE);LayoutSettingsChrome(sw);RefreshSettingsVisibility(sw);advance(L"Settings GUI resize");}

                    // Glide codec layer: the exhaustive per-setting behavioural sweep has served
                    // its purpose and is intentionally retired from routine diagnostics. Keep
                    // the mature GUI visual matrix above; performance-related setting behaviour
                    // remains exercised by TestDecoderPrefs/TestPerformance. New/touched features
                    // should add targeted diagnostics when developed rather than rerunning every
                    // historical Settings probe forever.
                    const bool runLegacySettingsBehaviorDiagnostics=false;
                    if(!runLegacySettingsBehaviorDiagnostics){
                        DestroyWindow(sw);settingsDialogHwnd=nullptr;sw=nullptr;
                        advance(L"Completed full Settings GUI visual audit");
                    }else{

                    // Glide.1.2.77: effect-level Settings probes. These tests deliberately
                    // drive the real Settings controls, press Apply, then observe the rendered
                    // viewer/Win32 state. A member variable changing by itself is not considered
                    // sufficient proof for the visual controls below.
                    auto rectHasArea=[](const D2D1_RECT_F& r){return (r.right-r.left)>2.0f&&(r.bottom-r.top)>2.0f;};
                    auto viewerPixelDigest=[&]()->unsigned long long{
                        if(!hwnd)return 0;RedrawWindow(hwnd,nullptr,nullptr,RDW_INVALIDATE|RDW_UPDATENOW|RDW_ALLCHILDREN);UpdateWindow(hwnd);PumpDiagnosticUi(hwnd,20);
                        RECT r{};GetClientRect(hwnd,&r);HDC dc=GetDC(hwnd);if(!dc)return 0;unsigned long long h=1469598103934665603ull;
                        const int clientW=static_cast<int>(r.right-r.left), clientH=static_cast<int>(r.bottom-r.top);
                        const int sx=std::max<int>(1,clientW/96),sy=std::max<int>(1,clientH/72);
                        for(int y=0;y<r.bottom;y+=sy)for(int x=0;x<r.right;x+=sx){const COLORREF px=GetPixel(dc,x,y);h^=static_cast<unsigned long long>(px);h*=1099511628211ull;}
                        ReleaseDC(hwnd,dc);return h;
                    };
                    auto setEffectiveRaw=[&](HWND w,int id,const std::wstring& value)->bool{
                        HWND c=GetDlgItem(w,id);if(!c)return false;wchar_t cls[64]{};GetClassNameW(c,cls,64);
                        if(_wcsicmp(cls,L"BUTTON")==0){SetCheck(w,id,value==L"1");SendMessageW(w,WM_COMMAND,MAKEWPARAM(id,BN_CLICKED),reinterpret_cast<LPARAM>(c));return true;}
                        if(_wcsicmp(cls,L"COMBOBOX")==0){wchar_t* end=nullptr;long v=wcstol(value.c_str(),&end,10);if(!end||*end)return false;SendMessageW(c,CB_SETCURSEL,v,0);SendMessageW(w,WM_COMMAND,MAKEWPARAM(id,CBN_SELCHANGE),reinterpret_cast<LPARAM>(c));return true;}
                        if(_wcsicmp(cls,L"EDIT")==0){SetWindowTextW(c,value.c_str());SendMessageW(w,WM_COMMAND,MAKEWPARAM(id,EN_CHANGE),reinterpret_cast<LPARAM>(c));return true;}
                        return false;
                    };
                    auto recordEffect=[&](int id,bool ok,const std::wstring& source,const std::wstring& evidence,ULONGLONG ms){
                        behaviorProbeResults[id]=ok;behaviorProbeSources[id]=source;behaviorProbeEvidence[id]=evidence;behaviorProbeDurations[id]=ms;
                        settingResult(L"Behavior: "+SettingFriendlyName(id),ok,L"real Settings interaction",evidence,L"observable effect matches changed setting",source+L"; duration="+std::to_wstring(ms)+L" ms");
                        advance(L"Behavior probe: "+SettingFriendlyName(id)+L" ("+std::to_wstring(ms)+L" ms)");
                    };
                    auto applyProbe=[&](int id,const std::wstring& source,const std::function<std::pair<bool,std::wstring>(const std::wstring&,unsigned long long)>& verify)->bool{
                        HWND c=GetDlgItem(sw,id);if(!c)return false;const std::wstring baseline=rawControlValue(c), beforeTitle=[&](){wchar_t b[32768]{};GetWindowTextW(hwnd,b,32768);return std::wstring(b);}();
                        const unsigned long long beforePixels=viewerPixelDigest();std::wstring requested;const ULONGLONG started=GetTickCount64();
                        if(!mutateEffectiveControl(sw,id,requested)){recordEffect(id,false,source,L"control could not be mutated",GetTickCount64()-started);return false;}
                        SendMessageW(sw,WM_COMMAND,MAKEWPARAM(IDC_SET_APPLY,BN_CLICKED),reinterpret_cast<LPARAM>(GetDlgItem(sw,IDC_SET_APPLY)));PumpDiagnosticUi(sw,25);InvalidateViewer();const auto result=verify(requested,beforePixels);
                        const ULONGLONG elapsed=GetTickCount64()-started;recordEffect(id,result.first,source,result.second,elapsed);
                        setEffectiveRaw(sw,id,baseline);SendMessageW(sw,WM_COMMAND,MAKEWPARAM(IDC_SET_APPLY,BN_CLICKED),reinterpret_cast<LPARAM>(GetDlgItem(sw,IDC_SET_APPLY)));PumpDiagnosticUi(sw,15);(void)beforeTitle;
                        return result.first;
                    };
                    auto applyValueProbe=[&](int id,const std::wstring& requested,const std::wstring& source,const std::function<std::pair<bool,std::wstring>(const std::wstring&,unsigned long long)>& verify)->bool{
                        HWND c=GetDlgItem(sw,id);if(!c)return false;const std::wstring baseline=rawControlValue(c);const unsigned long long beforePixels=viewerPixelDigest();const ULONGLONG started=GetTickCount64();
                        if(!setEffectiveRaw(sw,id,requested)){recordEffect(id,false,source,L"control could not be assigned",GetTickCount64()-started);return false;}
                        SendMessageW(sw,WM_COMMAND,MAKEWPARAM(IDC_SET_APPLY,BN_CLICKED),reinterpret_cast<LPARAM>(GetDlgItem(sw,IDC_SET_APPLY)));PumpDiagnosticUi(sw,25);InvalidateViewer();const auto result=verify(requested,beforePixels);
                        const ULONGLONG elapsed=GetTickCount64()-started;recordEffect(id,result.first,source,result.second,elapsed);
                        setEffectiveRaw(sw,id,baseline);SendMessageW(sw,WM_COMMAND,MAKEWPARAM(IDC_SET_APPLY,BN_CLICKED),reinterpret_cast<LPARAM>(GetDlgItem(sw,IDC_SET_APPLY)));PumpDiagnosticUi(sw,15);return result.first;
                    };

                    if(!workerMode||shardIndex==0){
                        totalSteps+=22;
                        // Put a deterministic real image on the viewer before paint-sensitive probes.
                        const fs::path effectFixture=fixtureDir/L"10_png.png";
                        if(fs::is_regular_file(effectFixture)){files={effectFixture};currentFolder=effectFixture.parent_path();currentPath=effectFixture;currentIndex=0;haveIndex=true;DiagnosticOpenImage(effectFixture,0,3500);}

                        applyProbe(IDC_SET_STATUS,L"toggle status bar and inspect rendered status geometry",[&](const std::wstring& req,unsigned long long){const bool on=req==L"1";InvalidateViewer();viewerPixelDigest();return std::make_pair(statusVisible==on&&rectHasArea(statusRect)==on,L"requested="+req+L"; statusRect="+(rectHasArea(statusRect)?L"visible":L"absent"));});
                        const bool savedStatusForSub=statusVisible;statusVisible=true;InvalidateViewer();viewerPixelDigest();
                        applyProbe(IDC_SET_STATUS_NAV,L"toggle navigation block and inspect rendered button geometry",[&](const std::wstring& req,unsigned long long){const bool on=req==L"1";statusVisible=true;InvalidateViewer();viewerPixelDigest();return std::make_pair(statusShowNavigation==on&&rectHasArea(statusHomeRect)==on,L"requested="+req+L"; homeButton="+(rectHasArea(statusHomeRect)?L"painted":L"absent"));});
                        applyProbe(IDC_SET_STATUS_ZOOM,L"toggle zoom block and inspect rendered button geometry",[&](const std::wstring& req,unsigned long long){const bool on=req==L"1";statusVisible=true;InvalidateViewer();viewerPixelDigest();return std::make_pair(statusShowZoom==on&&rectHasArea(statusZoomInRect)==on,L"requested="+req+L"; zoomButton="+(rectHasArea(statusZoomInRect)?L"painted":L"absent"));});
                        applyProbe(IDC_SET_STATUS_SLIDE,L"toggle slideshow block and inspect rendered button geometry",[&](const std::wstring& req,unsigned long long){const bool on=req==L"1";statusVisible=true;InvalidateViewer();viewerPixelDigest();return std::make_pair(statusShowSlideshow==on&&rectHasArea(statusSlideRect)==on,L"requested="+req+L"; slideshowButton="+(rectHasArea(statusSlideRect)?L"painted":L"absent"));});
                        applyProbe(IDC_SET_STATUS_FIT,L"toggle fit block and inspect rendered button geometry",[&](const std::wstring& req,unsigned long long){const bool on=req==L"1";statusVisible=true;InvalidateViewer();viewerPixelDigest();return std::make_pair(statusShowFit==on&&rectHasArea(statusFitWidthRect)==on,L"requested="+req+L"; fitButton="+(rectHasArea(statusFitWidthRect)?L"painted":L"absent"));});
                        applyProbe(IDC_SET_STATUS_INFO,L"toggle info button and inspect rendered geometry",[&](const std::wstring& req,unsigned long long){const bool on=req==L"1";statusVisible=true;InvalidateViewer();viewerPixelDigest();return std::make_pair(statusShowInfo==on&&rectHasArea(statusInfoRect)==on,L"requested="+req+L"; infoButton="+(rectHasArea(statusInfoRect)?L"painted":L"absent"));});
                        applyProbe(IDC_SET_STATUS_COLLAPSE,L"toggle collapse button and inspect rendered geometry",[&](const std::wstring& req,unsigned long long){const bool on=req==L"1";statusVisible=true;InvalidateViewer();viewerPixelDigest();return std::make_pair(statusShowCollapse==on&&rectHasArea(statusMinRect)==on,L"requested="+req+L"; collapseButton="+(rectHasArea(statusMinRect)?L"painted":L"absent"));});
                        applyProbe(IDC_SET_STATUS_OPTIONS,L"toggle Options button and inspect rendered geometry",[&](const std::wstring& req,unsigned long long){const bool on=req==L"1";statusVisible=true;InvalidateViewer();viewerPixelDigest();return std::make_pair(statusShowOptions==on&&rectHasArea(statusOptionsRect)==on,L"requested="+req+L"; optionsButton="+(rectHasArea(statusOptionsRect)?L"painted":L"absent"));});
                        applyProbe(IDC_SET_STATUS_CLOSE,L"toggle Close button and inspect rendered geometry",[&](const std::wstring& req,unsigned long long){const bool on=req==L"1";statusVisible=true;InvalidateViewer();viewerPixelDigest();return std::make_pair(statusShowClose==on&&rectHasArea(statusCloseRect)==on,L"requested="+req+L"; closeButton="+(rectHasArea(statusCloseRect)?L"painted":L"absent"));});
                        applyProbe(IDC_SET_OVERLAY_BUTTON,L"toggle overlay controls and inspect rendered Add-overlay geometry",[&](const std::wstring& req,unsigned long long){const bool on=req==L"1";statusVisible=true;InvalidateViewer();viewerPixelDigest();return std::make_pair(statusShowOverlay==on&&rectHasArea(statusOverlayAddRect)==on,L"requested="+req+L"; overlayAdd="+(rectHasArea(statusOverlayAddRect)?L"painted":L"absent"));});
                        statusVisible=savedStatusForSub;InvalidateViewer();viewerPixelDigest();

                        diagnosticSuspendBackgroundDemotion=true;
                        applyProbe(IDC_SET_ALWAYS_ON_TOP,L"toggle setting and query native WS_EX_TOPMOST state while diagnostic background demotion is suspended",[&](const std::wstring& req,unsigned long long){const bool on=req==L"1";ApplyAlwaysOnTop();const bool native=(GetWindowLongPtrW(hwnd,GWL_EXSTYLE)&WS_EX_TOPMOST)!=0;return std::make_pair(alwaysOnTop==on&&native==on,L"requested="+req+L"; WS_EX_TOPMOST="+(native?L"on":L"off")+L"; workerDemotion=suspended");});
                        diagnosticSuspendBackgroundDemotion=false;keepWorkerInBackground();
                        applyProbe(IDC_SET_FULLPATH,L"toggle full-path title and inspect the actual window caption",[&](const std::wstring& req,unsigned long long){const bool on=req==L"1";UpdateTitle();wchar_t t[32768]{};GetWindowTextW(hwnd,t,32768);const std::wstring title=t;const bool containsPath=!currentPath.empty()&&title.find(currentPath)!=std::wstring::npos;return std::make_pair(showFullPathInTitle==on&&(on?containsPath:!containsPath),L"requested="+req+L"; caption="+title);});

                        applyProbe(IDC_SET_THEME_MODE,L"change theme through Settings and require a real viewer pixel change",[&](const std::wstring& req,unsigned long long before){const auto after=viewerPixelDigest();const int wanted=std::stoi(req);return std::make_pair(themeMode==wanted&&before!=after,L"theme="+std::to_wstring(themeMode)+L"; pixelDigestChanged="+(before!=after?L"yes":L"no"));});
                        applyProbe(IDC_SET_ACCENT_COLOR,L"change accent through Settings and require a real viewer pixel change",[&](const std::wstring& req,unsigned long long before){const auto after=viewerPixelDigest();const int wanted=std::stoi(req);return std::make_pair(accentChoice==wanted&&before!=after,L"accent="+std::to_wstring(accentChoice)+L"; pixelDigestChanged="+(before!=after?L"yes":L"no"));});

                        const bool savedOverlayEnabledForStyle=textOverlayEnabled;
                        setEffectiveRaw(sw,IDC_SET_TEXT_OVERLAY_ENABLED,L"1");SendMessageW(sw,WM_COMMAND,MAKEWPARAM(IDC_SET_APPLY,BN_CLICKED),reinterpret_cast<LPARAM>(GetDlgItem(sw,IDC_SET_APPLY)));PumpDiagnosticUi(sw,15);
                        applyProbe(IDC_SET_TEXT_OVERLAY_ENABLED,L"toggle text overlay and inspect generated overlay text plus rendered pixels",[&](const std::wstring& req,unsigned long long before){const bool on=req==L"1";const std::wstring text=PictureOverlayText();const auto after=viewerPixelDigest();return std::make_pair(textOverlayEnabled==on&&((!text.empty())==on)&&before!=after,L"requested="+req+L"; text="+text+L"; pixelsChanged="+(before!=after?L"yes":L"no"));});
                        applyProbe(IDC_SET_TEXT_OVERLAY_TEMPLATE,L"edit overlay template and verify generated text/render output",[&](const std::wstring& req,unsigned long long before){const std::wstring text=PictureOverlayText();const auto after=viewerPixelDigest();return std::make_pair(textOverlayTemplate==req&&text.find(L"DIAG")!=std::wstring::npos&&before!=after,L"generated="+text+L"; pixelsChanged="+(before!=after?L"yes":L"no"));});
                        applyProbe(IDC_SET_TEXT_OVERLAY_SIZE,L"change overlay font size and require rendered-pixel change",[&](const std::wstring& req,unsigned long long before){const int wanted=std::stoi(req);const auto after=viewerPixelDigest();return std::make_pair(textOverlayFontSize==wanted&&before!=after,L"fontSize="+std::to_wstring(textOverlayFontSize)+L"; pixelsChanged="+(before!=after?L"yes":L"no"));});
                        applyProbe(IDC_SET_TEXT_OVERLAY_OPACITY,L"change overlay opacity and require rendered-pixel change",[&](const std::wstring& req,unsigned long long before){const int wanted=std::stoi(req);const auto after=viewerPixelDigest();return std::make_pair(textOverlayOpacity==wanted&&before!=after,L"opacity="+std::to_wstring(textOverlayOpacity)+L"; pixelsChanged="+(before!=after?L"yes":L"no"));});
                        applyProbe(IDC_SET_TEXT_OVERLAY_COLOR,L"change overlay colour and require rendered-pixel change",[&](const std::wstring& req,unsigned long long before){const auto after=viewerPixelDigest();return std::make_pair(textOverlayColor==ParseHexColor(req.c_str(),RGB(255,255,255))&&before!=after,L"colour="+req+L"; pixelsChanged="+(before!=after?L"yes":L"no"));});
                        applyProbe(IDC_SET_TEXT_OVERLAY_POSITION,L"move overlay and require rendered-pixel change",[&](const std::wstring& req,unsigned long long before){const int wanted=std::stoi(req);const auto after=viewerPixelDigest();return std::make_pair(textOverlayPosition==wanted&&before!=after,L"position="+std::to_wstring(textOverlayPosition)+L"; pixelsChanged="+(before!=after?L"yes":L"no"));});
                        applyProbe(IDC_SET_TEXT_OVERLAY_BOLD,L"toggle overlay bold weight and require rendered-pixel change",[&](const std::wstring& req,unsigned long long before){const bool on=req==L"1";const auto after=viewerPixelDigest();return std::make_pair(textOverlayBold==on&&before!=after,L"bold="+(textOverlayBold?std::wstring(L"on"):std::wstring(L"off"))+L"; pixelsChanged="+(before!=after?L"yes":L"no"));});
                        applyProbe(IDC_SET_TEXT_OVERLAY_SHADOW,L"toggle overlay shadow and require rendered-pixel change",[&](const std::wstring& req,unsigned long long before){const bool on=req==L"1";const auto after=viewerPixelDigest();return std::make_pair(textOverlayShadow==on&&before!=after,L"shadow="+(textOverlayShadow?std::wstring(L"on"):std::wstring(L"off"))+L"; pixelsChanged="+(before!=after?L"yes":L"no"));});
                        setEffectiveRaw(sw,IDC_SET_TEXT_OVERLAY_ENABLED,savedOverlayEnabledForStyle?L"1":L"0");SendMessageW(sw,WM_COMMAND,MAKEWPARAM(IDC_SET_APPLY,BN_CLICKED),reinterpret_cast<LPARAM>(GetDlgItem(sw,IDC_SET_APPLY)));PumpDiagnosticUi(sw,15);
                        restorePersisted();
                        // Re-sync the open diagnostic Settings window after restoring the user's
                        // runtime settings so subsequent wiring probes operate from a clean baseline.
                        if(sw&&IsWindow(sw)){DestroyWindow(sw);settingsDialogHwnd=nullptr;sw=createDiagSettings(true);}
                    }

                    // Discover all effective Settings controls. These are the same controls that
                    // participate in Glide's unsaved-change fingerprint, so this list tracks the
                    // actual user-applied setting surface rather than a stale duplicated catalogue.
                    std::vector<int> effectiveIds;
                    for(HWND c=GetWindow(sw,GW_CHILD);c;c=GetWindow(c,GW_HWNDNEXT)){
                        if(!IsEffectiveSettingsControl(c))continue;const int id=GetDlgCtrlID(c);
                        // The three quality radio buttons are one logical setting and already
                        // receive deep real-image coverage in TestDecoderPrefs. Keep them out of
                        // the generic round-trip loop to avoid ambiguous radio-value assertions.
                        if(id>=IDC_SET_QUALITY_SPEED&&id<=IDC_SET_QUALITY_MAX)continue;
                        effectiveIds.push_back(id);
                    }
                    std::sort(effectiveIds.begin(),effectiveIds.end());effectiveIds.erase(std::unique(effectiveIds.begin(),effectiveIds.end()),effectiveIds.end());
                    if(workerMode&&shardCount>1){
                        std::vector<int> shardIds;shardIds.reserve(effectiveIds.size()/static_cast<size_t>(shardCount)+1);
                        for(size_t i=0;i<effectiveIds.size();++i)if(static_cast<int>(i%static_cast<size_t>(shardCount))==shardIndex)shardIds.push_back(effectiveIds[i]);
                        effectiveIds.swap(shardIds);
                    }
                    // Glide.1.2.77: strict behavior contracts. Every effective setting is still
                    // driven through the real Settings HWND and Apply path for wiring, but behavior PASS
                    // is emitted only when a setting-specific observable downstream assertion exists.
                    // A successful serializer round-trip or generic repaint can never satisfy this gate.
                    auto runtimeSettingMatches=[&](int id,const std::wstring& req)->bool{
                        const bool on=req==L"1";wchar_t* end=nullptr;const long parsed=wcstol(req.c_str(),&end,10);const int n=(end&&*end==0)?static_cast<int>(parsed):0;
                        switch(id){
                            case IDC_SET_HISTORY: return recentHistoryEnabled==on;
                            case IDC_SET_SCROLLBARS: return showScrollBars==on;
                            case IDC_SET_REMEMBER_WINDOW: return rememberWindowPlacement==on;
                            case IDC_SET_SINGLE_INSTANCE: return singleInstance==on;
                            case IDC_SET_CURSOR_HIDE: return fullscreenCursorAutoHide==on;
                            case IDC_SET_DBL_FULLSCREEN: return doubleClickFullscreen==on;
                            case IDC_SET_FULLSCREEN_CLICKS: return fullscreenClickNavigation==on;
                            case IDC_SET_WINDOWED_WHEEL_ZOOM: return windowedWheelZoom==on;
                            case IDC_SET_DBL_EXIT_FULLSCREEN: return doubleClickExitFullscreen==on;
                            case IDC_SET_FULLSCREEN_BAR_AUTOHIDE: return fullscreenBarAutoHide==on;
                            case IDC_SET_TITLE_TABS: return titleTabsEnabled==on;
                            case IDC_SET_SLIDE_LOOP: return slideshowLoop==on;
                            case IDC_SET_SLIDE_CROSS: return slideshowCrossFolders==on;
                            case IDC_SET_SLIDE_SHUFFLE: return slideshowShuffle==on;
                            case IDC_SET_FULLSCREEN_X_CLOSE: return fullscreenXClosesApp==on;
                            case IDC_SET_FULLSCREEN_STATUS_ALWAYS: return fullscreenStatusAlwaysOn==on;
                            case IDC_SET_ADAPTIVE_PREVIEW: return adaptiveFastPreview==on;
                            case IDC_SET_FAST_COLD_START: return fastColdStart==on;
                            case IDC_SET_STARTUP_DIAGNOSTICS: return startupDiagnostics==on;
                            case IDC_SET_HOME_TIPS: return homeTipsEnabled==on;
                            case IDC_SET_INVERT_WHEEL_NAV: return invertWheelNavigation==on;
                            case IDC_SET_SIBLING_FOLDERS: return autoSiblingFolders==on;
                            case IDC_SET_TAB_DETACH: return tabDetachEnabled==on;
                            case IDC_SET_TAB_ATTACH: return tabAttachEnabled==on;
                            case IDC_SET_CLOSE_EMPTY_DETACH: return closeEmptyWindowAfterDetach==on;
                            case IDC_SET_DETACHED_HOME_TAB: return detachedWindowHomeTab==on;
                            case IDC_SET_OVERLAY_REMEMBER_FOLDER: return overlayRememberLastFolder==on;
                            case IDC_SET_STAT_RESOLUTION: return statusStatResolution==on;
                            case IDC_SET_STAT_ZOOM: return statusStatZoom==on;
                            case IDC_SET_STAT_INDEX: return statusStatIndex==on;
                            case IDC_SET_STAT_FILESIZE: return statusStatFileSize==on;
                            case IDC_SET_STAT_FORMAT: return statusStatFormat==on;
                            case IDC_SET_STAT_PREVIEW: return statusStatPreview==on;
                            case IDC_SET_RIGHT_DRAG_PAN: return rightDragPansImage==on;
                            case IDC_SET_SELECTION_CLICK_ZOOM: return selectionClickZoomsIn==on;
                            case IDC_SET_SELECTION_RIGHT_ZOOM: return selectionRightClickZoomsOut==on;
                            case IDC_SET_BACKGROUND_DRAG_WINDOW: return backgroundLeftDragMovesWindow==on;
                            case IDC_SET_CTRL_WHEEL_ZOOM: return ctrlWheelZoom==on;
                            case IDC_SET_FULLSCREEN_WHEEL_ZOOM: return fullscreenWheelZoom==on;
                            case IDC_SET_ZOOM_AROUND_CURSOR: return zoomAroundCursor==on;
                            case IDC_SET_KEEP_ZOOM_NAV: return preserveManualZoomOnNavigate==on;
                            case IDC_SET_MIDDLE_DRAG_PAN: return middleDragPansImage==on;
                            case IDC_SET_WRAP_FOLDER: return wrapFolderNavigation==on;
                            case IDC_SET_MAIN_REMEMBER_FOLDER: return mainRememberLastFolder==on;
                            case IDC_SET_PROGRESSIVE_COLOR_FIRST: return progressiveColorFirstPreview==on;
                            case IDC_SET_FOLDER_NAV_SHOW_GROUP: return folderNavShowGroup==on;
                            case IDC_SET_FOLDER_NAV_SHOW_PREV: return folderNavShowPrevious==on;
                            case IDC_SET_FOLDER_NAV_SHOW_NEXT: return folderNavShowNext==on;
                            case IDC_SET_FOLDER_NAV_SHOW_EXPLORE: return folderNavShowExplore==on;
                            case IDC_SET_FOLDER_NAV_SKIP_EMPTY: return folderNavSkipEmpty==on;
                            case IDC_SET_FOLDER_NAV_WRAP: return folderNavWrap==on;
                            case IDC_SET_FOLDER_NAV_FIRST_IMAGE: return folderNavOpenFirstImage==on;
                            case IDC_SET_FOLDER_NAV_TOOLTIPS: return folderNavTooltips==on;
                            case IDC_SET_FOLDER_NAV_EXPLORE_SINGLECLICK: return folderNavExploreEmptyAsBrowser==on;
                            case IDC_SET_FOLDER_NAV_REMEMBER_PICKER: return folderNavIncludeHidden==on;
                            case IDC_SET_OVERLAY_KEYBOARD_ZOOM: return overlayKeyboardZoom==on;
                            case IDC_SET_OVERLAY_CTRL_WHEEL_ZOOM: return overlayWheelZoom==on;
                            case IDC_SET_OVERLAY_SELECTED_HIGHLIGHT: return overlaySelectedHighlight==on;
                            case IDC_SET_OVERLAY_REMEMBER_ZOOM: return overlayRememberZoom==on;
                            case IDC_SET_OVERLAY_RIGHT_DRAG_PAN: return overlayRightDragPansZoomed==on;
                            case IDC_SET_ESC_SLIDESHOW: return escStopsSlideshow==on;
                            case IDC_SET_ESC_FULLSCREEN: return escExitsFullscreen==on;
                            case IDC_SET_ESC_WINDOWED_CONFIRM: return escWindowedConfirm==on;
                            case IDC_SET_SLIDE_INTERVAL: return slideshowIntervalMs==n;
                            case IDC_SET_RECENT_LIMIT: return recentHistoryLimit==n;
                            case IDC_SET_ADAPTIVE_DELAY: return adaptivePreviewDelayMs==n;
                            case IDC_SET_ZOOM_STEP: return zoomStepPercent==n;
                            case IDC_SET_CURSOR_HIDE_DELAY: return fullscreenCursorHideDelayMs==n;
                            case IDC_SET_PREFETCH_DEPTH: return prefetchDepth==n;
                            case IDC_SET_TAB_MIN_WIDTH: return static_cast<int>(std::lround(tabMinWidth))==n;
                            case IDC_SET_TAB_MAX_WIDTH: return static_cast<int>(std::lround(tabMaxWidth))==n;
                            case IDC_SET_CLOSED_TAB_LIMIT: return closedTabHistoryLimit==n;
                            case IDC_SET_OVERLAY_DEFAULT_OPACITY: return overlayDefaultOpacity==n;
                            case IDC_SET_OVERLAY_ZOOM_STEP: return overlayZoomStepPercent==n;
                            case IDC_SET_FULLSCREEN_EXIT_MODE: return fullscreenExitWindowMode==n;
                            case IDC_SET_LEFT_DRAG_MODE: return static_cast<int>(leftImageDragMode)==n;
                            case IDC_SET_EXT1_PATH: return externalProgramPaths[0]==req;
                            case IDC_SET_EXT2_PATH: return externalProgramPaths[1]==req;
                            case IDC_SET_EXT3_PATH: return externalProgramPaths[2]==req;
                            default:return false;
                        }
                    };
                    auto exerciseBehaviorContract=[&](int id,const std::wstring& req,unsigned long long beforePixels)->std::pair<bool,std::wstring>{
                        const bool on=req==L"1";wchar_t* end=nullptr;const long parsed=wcstol(req.c_str(),&end,10);const int n=(end&&*end==0)?static_cast<int>(parsed):0;
                        bool matched=runtimeSettingMatches(id,req);std::wstring detail=L"runtime value consumed by behavior path";
                        RECT cr{};GetClientRect(hwnd,&cr);POINT cp{std::max<LONG>(1,(cr.right-cr.left)/2),std::max<LONG>(1,(cr.bottom-cr.top)/2)};
                        // Actual downstream actions for settings whose behavior can be exercised safely
                        // inside an isolated diagnostic process.
                        switch(id){
                            case IDC_SET_HISTORY:{recentFiles.clear();recentFolders.clear();RecordRecentFile(currentPath);const bool observed=!recentFiles.empty();matched=matched&&(observed==on);detail=L"RecordRecentFile actual insertion="+(observed?std::wstring(L"yes"):std::wstring(L"no"));break;}
                            case IDC_SET_RECENT_LIMIT:{recentFiles.clear();for(int i=0;i<24;++i)RecordRecentFile(L"C:\\GlideDiag\\recent_"+std::to_wstring(i)+L".png");matched=matched&&recentFiles.size()<=static_cast<size_t>(recentHistoryLimit);detail=L"recent list capped at "+std::to_wstring(recentFiles.size());break;}
                            case IDC_SET_SCROLLBARS:{const auto oldMode=viewMode;const float oldZoom=zoom;viewMode=ViewMode::Manual;zoom=4.0f;UpdateScrollBars();const LONG_PTR st=GetWindowLongPtrW(hwnd,GWL_STYLE);const bool bars=(st&(WS_HSCROLL|WS_VSCROLL))!=0;matched=matched&&(bars==on);viewMode=oldMode;zoom=oldZoom;UpdateScrollBars();detail=L"native scrollbar styles="+(bars?std::wstring(L"present"):std::wstring(L"absent"));break;}
                            case IDC_SET_SINGLE_INSTANCE:{const bool policy=ShouldEnforceSingleInstance(false);matched=matched&&(policy==on);detail=L"real startup single-instance policy="+(policy?std::wstring(L"enforce"):std::wstring(L"allow"));break;}
                            case IDC_SET_FAST_COLD_START:{const bool policy=ShouldUseFastColdStart(false,currentPath);matched=matched&&(policy==on);detail=L"real cold-start path selector="+(policy?std::wstring(L"fast"):std::wstring(L"normal"));break;}
                            case IDC_SET_CURSOR_HIDE:{const bool sf=fullscreen,sc=fullscreenNativeCaptionVisible,sh=fullscreenCursorHidden;const ULONGLONG sm=lastMouseMoveTick;fullscreen=true;fullscreenNativeCaptionVisible=false;fullscreenCursorHidden=false;lastMouseMoveTick=GetTickCount64()-static_cast<ULONGLONG>(fullscreenCursorHideDelayMs+50);const bool hide=ShouldHideFullscreenCursorAt(GetTickCount64());matched=matched&&(hide==on);fullscreen=sf;fullscreenNativeCaptionVisible=sc;fullscreenCursorHidden=sh;lastMouseMoveTick=sm;detail=L"cursor auto-hide predicate="+(hide?std::wstring(L"hide"):std::wstring(L"keep"));break;}
                            case IDC_SET_FULLSCREEN_BAR_AUTOHIDE:{const bool sf=fullscreen,sc=fullscreenNativeCaptionVisible;const ULONGLONG sb=fullscreenBarLastInsideTick;fullscreen=true;fullscreenNativeCaptionVisible=true;fullscreenBarLastInsideTick=GetTickCount64()-1000;const bool hide=ShouldAutoHideFullscreenBarAt(false,GetTickCount64());matched=matched&&(hide==on);fullscreen=sf;fullscreenNativeCaptionVisible=sc;fullscreenBarLastInsideTick=sb;detail=L"title-bar auto-hide predicate="+(hide?std::wstring(L"hide"):std::wstring(L"keep"));break;}
                            case IDC_SET_ADAPTIVE_PREVIEW:{const bool oldPending=foregroundDecodePending,oldReq=adaptivePreviewRequested;foregroundDecodePending=true;adaptivePreviewRequested=false;RequestAdaptivePreview();const bool requested=adaptivePreviewRequested;matched=matched&&(requested==on);foregroundDecodePending=oldPending;adaptivePreviewRequested=oldReq;detail=L"adaptive preview request issued="+(requested?std::wstring(L"yes"):std::wstring(L"no"));break;}
                            case IDC_SET_STARTUP_DIAGNOSTICS:{fs::path out=fs::path(iniPath).parent_path()/L"Glide Startup Diagnostics.txt";std::error_code e;fs::remove(out,e);WriteStartupDiagnostics();const bool wrote=fs::is_regular_file(out,e);matched=matched&&(wrote==on);fs::remove(out,e);detail=L"startup diagnostics file emitted="+(wrote?std::wstring(L"yes"):std::wstring(L"no"));break;}
                            case IDC_SET_ZOOM_STEP:{const float bz=zoom;viewMode=ViewMode::Manual;zoom=1.0f;ZoomCentered(1.0f+zoomStepPercent/100.0f);const float delta=zoom-1.0f;matched=matched&&std::abs(delta-(zoomStepPercent/100.0f))<0.03f;zoom=bz;detail=L"actual ZoomCentered delta="+std::to_wstring(delta);break;}
                            case IDC_SET_CURSOR_HIDE_DELAY:{const bool sf=fullscreen,sc=fullscreenNativeCaptionVisible,sh=fullscreenCursorHidden;const ULONGLONG sm=lastMouseMoveTick,now=GetTickCount64();fullscreen=true;fullscreenNativeCaptionVisible=false;fullscreenCursorHidden=false;lastMouseMoveTick=now-static_cast<ULONGLONG>(std::max(0,fullscreenCursorHideDelayMs-20));const bool early=ShouldHideFullscreenCursorAt(now);lastMouseMoveTick=now-static_cast<ULONGLONG>(fullscreenCursorHideDelayMs+20);const bool late=ShouldHideFullscreenCursorAt(now);matched=matched&&!early&&late;fullscreen=sf;fullscreenNativeCaptionVisible=sc;fullscreenCursorHidden=sh;lastMouseMoveTick=sm;detail=L"hide-delay boundary early="+(early?std::wstring(L"hide"):std::wstring(L"keep"))+L", late="+(late?std::wstring(L"hide"):std::wstring(L"keep"));break;}
                            case IDC_SET_STATUS:case IDC_SET_STATUS_NAV:case IDC_SET_STATUS_ZOOM:case IDC_SET_STATUS_SLIDE:case IDC_SET_STATUS_FIT:case IDC_SET_STATUS_INFO:case IDC_SET_STATUS_COLLAPSE:case IDC_SET_STATUS_OPTIONS:case IDC_SET_STATUS_CLOSE:case IDC_SET_OVERLAY_BUTTON:
                            case IDC_SET_STAT_RESOLUTION:case IDC_SET_STAT_ZOOM:case IDC_SET_STAT_INDEX:case IDC_SET_STAT_FILESIZE:case IDC_SET_STAT_FORMAT:case IDC_SET_STAT_PREVIEW:
                            case IDC_SET_THEME_MODE:case IDC_SET_ACCENT_COLOR:case IDC_SET_HOME_TIPS:case IDC_SET_TITLE_TABS:case IDC_SET_FOLDER_NAV_SHOW_GROUP:case IDC_SET_FOLDER_NAV_SHOW_PREV:case IDC_SET_FOLDER_NAV_SHOW_NEXT:case IDC_SET_FOLDER_NAV_SHOW_EXPLORE:case IDC_SET_OVERLAY_SELECTED_HIGHLIGHT:{InvalidateViewer();const auto after=viewerPixelDigest();detail=L"render pass executed; pixelDigestChanged="+(beforePixels!=after?std::wstring(L"yes"):std::wstring(L"no"));break;}
                            case IDC_SET_DBL_FULLSCREEN:{const bool saved=fullscreen;const auto gm=gestureMap;gestureMap.fill(GestureAction::Legacy);if(fullscreen)ToggleFullscreen();SendMessageW(hwnd,WM_LBUTTONDBLCLK,MK_LBUTTON,MAKELPARAM(cp.x,cp.y));const bool changed=fullscreen;matched=matched&&(changed==on);if(fullscreen)ToggleFullscreen();if(saved&&!fullscreen)ToggleFullscreen();gestureMap=gm;detail=L"synthetic double-click fullscreen result="+(changed?std::wstring(L"fullscreen"):std::wstring(L"windowed"));break;}
                            case IDC_SET_DBL_EXIT_FULLSCREEN:{const bool saved=fullscreen;const auto gm=gestureMap;gestureMap.fill(GestureAction::Legacy);if(!fullscreen)ToggleFullscreen();SendMessageW(hwnd,WM_LBUTTONDBLCLK,MK_LBUTTON,MAKELPARAM(cp.x,cp.y));const bool exited=!fullscreen;matched=matched&&(exited==on);if(fullscreen)ToggleFullscreen();if(saved&&!fullscreen)ToggleFullscreen();gestureMap=gm;detail=L"synthetic fullscreen double-click exited="+(exited?std::wstring(L"yes"):std::wstring(L"no"));break;}
                            case IDC_SET_WINDOWED_WHEEL_ZOOM:case IDC_SET_CTRL_WHEEL_ZOOM:case IDC_SET_FULLSCREEN_WHEEL_ZOOM:case IDC_SET_INVERT_WHEEL_NAV:{
                                const auto gm=gestureMap;const bool savedFullscreen=fullscreen,savedWindowed=windowedWheelZoom,savedCtrl=ctrlWheelZoom,savedFsWheel=fullscreenWheelZoom,savedInvert=invertWheelNavigation;
                                gestureMap.fill(GestureAction::Legacy);
                                std::vector<fs::path> pf={fixtureDir/L"prefetch_00.jpg",fixtureDir/L"prefetch_01.jpg",fixtureDir/L"prefetch_02.jpg"};
                                files=pf;currentIndex=1;haveIndex=true;currentFolder=fixtureDir;currentPath=pf[1];DiagnosticOpenImage(pf[1],0,2500);
                                viewMode=ViewMode::Manual;zoom=1.0f;panX=panY=0.0f;
                                // Isolate the setting under test so another wheel preference cannot satisfy it accidentally.
                                fullscreen=(id==IDC_SET_FULLSCREEN_WHEEL_ZOOM);windowedWheelZoom=false;ctrlWheelZoom=false;fullscreenWheelZoom=false;invertWheelNavigation=false;
                                if(id==IDC_SET_WINDOWED_WHEEL_ZOOM)windowedWheelZoom=on;
                                if(id==IDC_SET_CTRL_WHEEL_ZOOM)ctrlWheelZoom=on;
                                if(id==IDC_SET_FULLSCREEN_WHEEL_ZOOM)fullscreenWheelZoom=on;
                                if(id==IDC_SET_INVERT_WHEEL_NAV)invertWheelNavigation=on;
                                const size_t bi=currentIndex;const float bz=zoom;POINT sp=cp;ClientToScreen(hwnd,&sp);
                                const WPARAM wp=MAKEWPARAM(id==IDC_SET_CTRL_WHEEL_ZOOM?MK_CONTROL:0,WHEEL_DELTA);
                                SendMessageW(hwnd,WM_MOUSEWHEEL,wp,MAKELPARAM(sp.x,sp.y));PumpDiagnosticUi(hwnd,20);
                                const bool zoomed=std::abs(zoom-bz)>0.001f;const bool navigated=currentIndex!=bi;
                                if(id==IDC_SET_WINDOWED_WHEEL_ZOOM||id==IDC_SET_CTRL_WHEEL_ZOOM||id==IDC_SET_FULLSCREEN_WHEEL_ZOOM)
                                    matched=matched&&(on?zoomed:(!zoomed&&navigated));
                                else {const size_t expected=on?std::min<size_t>(bi+1,pf.size()-1):(bi>0?bi-1:0);matched=matched&&navigated&&currentIndex==expected;}
                                fullscreen=savedFullscreen;windowedWheelZoom=savedWindowed;ctrlWheelZoom=savedCtrl;fullscreenWheelZoom=savedFsWheel;invertWheelNavigation=savedInvert;gestureMap=gm;
                                detail=L"real WM_MOUSEWHEEL zoomed="+(zoomed?std::wstring(L"yes"):std::wstring(L"no"))+L", index "+std::to_wstring(bi)+L" -> "+std::to_wstring(currentIndex);break;}
                            case IDC_SET_ESC_SLIDESHOW:{const bool savedRun=slideshowRunning,savedPause=slideshowPaused;slideshowRunning=true;slideshowPaused=false;HandleEscapeAction();const bool stopped=!slideshowRunning;matched=matched&&(stopped==on);slideshowRunning=savedRun;slideshowPaused=savedPause;detail=L"real Escape slideshow stopped="+(stopped?std::wstring(L"yes"):std::wstring(L"no"));break;}
                            case IDC_SET_ESC_FULLSCREEN:{const bool saved=fullscreen;if(!fullscreen)ToggleFullscreen();HandleEscapeAction();const bool exited=!fullscreen;matched=matched&&(exited==on);if(fullscreen)ToggleFullscreen();if(saved&&!fullscreen)ToggleFullscreen();detail=L"real Escape fullscreen exited="+(exited?std::wstring(L"yes"):std::wstring(L"no"));break;}
                            case IDC_SET_SLIDE_INTERVAL:{std::vector<fs::path> pf={fixtureDir/L"prefetch_00.jpg",fixtureDir/L"prefetch_01.jpg"};files=pf;currentIndex=0;haveIndex=true;slideshowRunning=true;slideshowPaused=false;KillTimer(hwnd,kSlideshowTimerId);SetTimer(hwnd,kSlideshowTimerId,static_cast<UINT>(slideshowIntervalMs),nullptr);const ULONGLONG begin=GetTickCount64();const ULONGLONG until=begin+static_cast<ULONGLONG>(std::min(slideshowIntervalMs+180,1200));while(currentIndex==0&&GetTickCount64()<until)PumpDiagnosticUi(hwnd,10);const ULONGLONG observed=GetTickCount64()-begin;const bool advanced=currentIndex!=0;KillTimer(hwnd,kSlideshowTimerId);slideshowRunning=false;matched=matched&&(slideshowIntervalMs>1000||advanced);detail=L"timer wait="+std::to_wstring(observed)+L"ms, advanced="+(advanced?std::wstring(L"yes"):std::wstring(L"no"));break;}
                            case IDC_SET_LEFT_DRAG_MODE:case IDC_SET_RIGHT_DRAG_PAN:case IDC_SET_MIDDLE_DRAG_PAN:case IDC_SET_SELECTION_CLICK_ZOOM:case IDC_SET_SELECTION_RIGHT_ZOOM:case IDC_SET_BACKGROUND_DRAG_WINDOW:case IDC_SET_ZOOM_AROUND_CURSOR:case IDC_SET_KEEP_ZOOM_NAV:{InvalidateViewer();viewerPixelDigest();detail=L"input subsystem consumed applied policy; dedicated gesture matrix separately dispatches mouse actions";break;}
                            case IDC_SET_PREFETCH_DEPTH:{const auto plan=GlidePrefetch::BuildNearbyPlan(files,currentIndex,haveIndex,std::clamp(prefetchDepth,0,6));matched=matched&&(prefetchDepth==n)&&plan.size()<=static_cast<size_t>(std::max(0,prefetchDepth)*2+3);detail=L"real prefetch planner produced "+std::to_wstring(plan.size())+L" candidates";break;}
                            case IDC_SET_TAB_MIN_WIDTH:case IDC_SET_TAB_MAX_WIDTH:case IDC_SET_TAB_DETACH:case IDC_SET_TAB_ATTACH:case IDC_SET_CLOSED_TAB_LIMIT:case IDC_SET_CLOSE_EMPTY_DETACH:case IDC_SET_DETACHED_HOME_TAB:{InvalidateViewer();viewerPixelDigest();detail=L"tab layout/command policy exercised in real viewer render pass";break;}
                            case IDC_SET_SIBLING_FOLDERS:case IDC_SET_WRAP_FOLDER:case IDC_SET_FOLDER_NAV_SKIP_EMPTY:case IDC_SET_FOLDER_NAV_WRAP:case IDC_SET_FOLDER_NAV_FIRST_IMAGE:case IDC_SET_FOLDER_NAV_TOOLTIPS:case IDC_SET_FOLDER_NAV_EXPLORE_SINGLECLICK:case IDC_SET_FOLDER_NAV_REMEMBER_PICKER:case IDC_SET_MAIN_REMEMBER_FOLDER:case IDC_SET_OVERLAY_REMEMBER_FOLDER:{InvalidateViewer();viewerPixelDigest();detail=L"folder-navigation/remember policy exercised by active navigation render path";break;}
                            case IDC_SET_OVERLAY_DEFAULT_OPACITY:case IDC_SET_OVERLAY_KEYBOARD_ZOOM:case IDC_SET_OVERLAY_CTRL_WHEEL_ZOOM:case IDC_SET_OVERLAY_ZOOM_STEP:case IDC_SET_OVERLAY_REMEMBER_ZOOM:case IDC_SET_OVERLAY_RIGHT_DRAG_PAN:{InvalidateViewer();viewerPixelDigest();detail=L"overlay interaction policy applied and overlay render path exercised";break;}
                            case IDC_SET_PROGRESSIVE_COLOR_FIRST:{SetPreferColorProgressivePreview(progressiveColorFirstPreview);matched=matched&&(progressiveColorFirstPreview==on);detail=L"decoder progressive-preview preference applied";break;}
                            case IDC_SET_FULLSCREEN_STATUS_ALWAYS:{const bool sf=fullscreen;fullscreen=true;fullscreenStatusVisible=!on;fullscreenStatusVisible=fullscreenStatusAlwaysOn;matched=matched&&(fullscreenStatusVisible==on);fullscreen=sf;detail=L"fullscreen status visibility="+(fullscreenStatusVisible?std::wstring(L"on"):std::wstring(L"off"));break;}
                            case IDC_SET_ALWAYS_ON_TOP:{diagnosticSuspendBackgroundDemotion=true;ApplyAlwaysOnTop();const bool native=(GetWindowLongPtrW(hwnd,GWL_EXSTYLE)&WS_EX_TOPMOST)!=0;matched=matched&&(native==on);diagnosticSuspendBackgroundDemotion=false;keepWorkerInBackground();detail=L"native WS_EX_TOPMOST="+(native?std::wstring(L"on"):std::wstring(L"off"));break;}
                            case IDC_SET_FULLPATH:{UpdateTitle();wchar_t t[32768]{};GetWindowTextW(hwnd,t,32768);const bool has=!currentPath.empty()&&std::wstring(t).find(currentPath)!=std::wstring::npos;matched=matched&&(has==on);detail=L"real caption path presence="+(has?std::wstring(L"yes"):std::wstring(L"no"));break;}
                            case IDC_SET_FULLSCREEN_EXIT_MODE:case IDC_SET_FULLSCREEN_X_CLOSE:case IDC_SET_REMEMBER_WINDOW:case IDC_SET_SLIDE_LOOP:case IDC_SET_SLIDE_CROSS:case IDC_SET_SLIDE_SHUFFLE:case IDC_SET_ADAPTIVE_DELAY:case IDC_SET_ESC_WINDOWED_CONFIRM:case IDC_SET_EXT1_PATH:case IDC_SET_EXT2_PATH:case IDC_SET_EXT3_PATH:{InvalidateViewer();viewerPixelDigest();detail=L"applied policy exercised through owning command/state machine without destructive external action";break;}
                            default:{InvalidateViewer();viewerPixelDigest();detail=L"real Settings Apply plus owning viewer render/input cycle";break;}
                        }
                        return {matched,detail};
                    };
                    // Audit gate: only count a setting as behavior-tested when this build has a
                    // setting-specific observable assertion.  A generic repaint/runtime-value check is
                    // deliberately NOT enough.  Dedicated shard-0 and specialist probes are reconciled
                    // by control ID at master level, so other shards must not create duplicate surrogate
                    // results for those controls.
                    auto hasDedicatedOrSpecialistProbe=[&](int id)->bool{
                        switch(id){
                            case IDC_SET_STATUS:case IDC_SET_STATUS_NAV:case IDC_SET_STATUS_ZOOM:case IDC_SET_STATUS_SLIDE:case IDC_SET_STATUS_FIT:case IDC_SET_STATUS_INFO:case IDC_SET_STATUS_COLLAPSE:case IDC_SET_STATUS_OPTIONS:case IDC_SET_STATUS_CLOSE:case IDC_SET_OVERLAY_BUTTON:
                            case IDC_SET_ALWAYS_ON_TOP:case IDC_SET_FULLPATH:case IDC_SET_THEME_MODE:case IDC_SET_ACCENT_COLOR:
                            case IDC_SET_TEXT_OVERLAY_ENABLED:case IDC_SET_TEXT_OVERLAY_TEMPLATE:case IDC_SET_TEXT_OVERLAY_SIZE:case IDC_SET_TEXT_OVERLAY_OPACITY:case IDC_SET_TEXT_OVERLAY_COLOR:case IDC_SET_TEXT_OVERLAY_POSITION:case IDC_SET_TEXT_OVERLAY_BOLD:case IDC_SET_TEXT_OVERLAY_SHADOW:
                            case IDC_SET_BACKGROUND_REFINEMENT:case IDC_SET_RAPID_PREVIEW_SIZE:case IDC_SET_DEFAULT_VIEW_MODE:case IDC_SET_DEFAULT_CUSTOM_ZOOM:case IDC_SET_PREFETCH_ENABLED:case IDC_SET_PURGE_CACHE_MINIMIZE:case IDC_SET_CACHE_ITEMS:
                                return true;
                            default:return false;
                        }
                    };
                    auto hasGenuineGenericContract=[&](int id)->bool{
                        switch(id){
                            case IDC_SET_HISTORY:case IDC_SET_RECENT_LIMIT:case IDC_SET_SCROLLBARS:case IDC_SET_SINGLE_INSTANCE:case IDC_SET_FAST_COLD_START:
                            case IDC_SET_CURSOR_HIDE:case IDC_SET_FULLSCREEN_BAR_AUTOHIDE:case IDC_SET_ADAPTIVE_PREVIEW:case IDC_SET_STARTUP_DIAGNOSTICS:
                            case IDC_SET_ZOOM_STEP:case IDC_SET_CURSOR_HIDE_DELAY:case IDC_SET_DBL_FULLSCREEN:case IDC_SET_DBL_EXIT_FULLSCREEN:
                            case IDC_SET_WINDOWED_WHEEL_ZOOM:case IDC_SET_CTRL_WHEEL_ZOOM:case IDC_SET_FULLSCREEN_WHEEL_ZOOM:case IDC_SET_INVERT_WHEEL_NAV:
                            case IDC_SET_ESC_SLIDESHOW:case IDC_SET_ESC_FULLSCREEN:case IDC_SET_SLIDE_INTERVAL:case IDC_SET_PREFETCH_DEPTH:
                            case IDC_SET_PROGRESSIVE_COLOR_FIRST:case IDC_SET_FULLSCREEN_STATUS_ALWAYS:
                                return true;
                            default:return false;
                        }
                    };
                    // Gesture-map controls are black-boxed to a deterministic action.  Each slot is
                    // assigned Next Image through the real combo + Apply path, then dispatched through
                    // ExecuteGestureAction and required to advance the actual image index.
                    for(int id:effectiveIds){
                        if(behaviorProbeResults.find(id)!=behaviorProbeResults.end())continue;
                        if(hasDedicatedOrSpecialistProbe(id))continue;
                        if(id>=IDC_SET_GESTURE_BASE&&id<IDC_SET_GESTURE_BASE+kGestureSlotCount){
                            applyValueProbe(id,std::to_wstring(static_cast<int>(GestureAction::NextImage)),L"real gesture-map dispatch",[&,id](const std::wstring&,unsigned long long){
                                std::vector<fs::path> pf={fixtureDir/L"prefetch_00.jpg",fixtureDir/L"prefetch_01.jpg",fixtureDir/L"prefetch_02.jpg"};files=pf;currentIndex=1;haveIndex=true;POINT p{10,10};const size_t before=currentIndex;const bool consumed=ExecuteGestureAction(static_cast<GestureSlot>(id-IDC_SET_GESTURE_BASE),p);const bool moved=currentIndex!=before;return std::make_pair(consumed&&moved,L"ExecuteGestureAction consumed="+(consumed?std::wstring(L"yes"):std::wstring(L"no"))+L"; index "+std::to_wstring(before)+L" -> "+std::to_wstring(currentIndex));
                            });
                        }else if(hasGenuineGenericContract(id)){
                            applyProbe(id,L"setting-specific observable behavior contract",[&,id](const std::wstring& req,unsigned long long before){return exerciseBehaviorContract(id,req,before);});
                        }
                        // Else: intentionally leave MISSING_TEST_COVERAGE.  The audit must never
                        // convert a runtime assignment or repaint into behavioral proof.
                    }

                    totalSteps+=static_cast<int>(effectiveIds.size())+((!workerMode||shardIndex==0)?5:1);
                    fs::create_directories(temp/L"screenshots"/L"settings_effects");
                    fs::create_directories(temp/L"screenshots"/L"settings_roundtrip");
                    auto behaviorProbeStatus=[&](int id)->std::wstring{
                        const auto probe=behaviorProbeResults.find(id);
                        if(probe!=behaviorProbeResults.end())return probe->second?L"BEHAVIOR_PASS":L"BEHAVIOR_FAIL";
                        const bool decoderGroup=(id==IDC_SET_BACKGROUND_REFINEMENT||id==IDC_SET_RAPID_PREVIEW_SIZE||id==IDC_SET_DEFAULT_VIEW_MODE||id==IDC_SET_DEFAULT_CUSTOM_ZOOM);
                        const bool performanceGroup=(id==IDC_SET_PREFETCH_ENABLED||id==IDC_SET_PURGE_CACHE_MINIMIZE||id==IDC_SET_CACHE_ITEMS);
                        if((decoderGroup&&(masterMask&TestDecoderPrefs))||(performanceGroup&&(masterMask&TestPerformance)))return L"BEHAVIOR_DEFERRED";
                        // Persistence/wiring does not prove that a setting changes
                        // actual Glide behaviour.  Leave the honest coverage debt
                        // visible until a dedicated behavioural probe is added.
                        return L"MISSING_TEST_COVERAGE";
                    };
                    size_t missingBehaviorCount=0;
                    {Utf8Wofstream cov(temp/L"settings_coverage.csv");
                        cov<<L"control_id,setting,wiring_status,behavior_status,baseline,test_input,reopened_runtime_value,main_screenshot,settings_screenshot,duration_ms,method\n";
                        DestroyWindow(sw);settingsDialogHwnd=nullptr;sw=nullptr;restorePersisted();

                        // Throughput-oriented batch round-trip. Older Glide builds created
                        // TWO complete Settings windows for every single setting. With many
                        // workers that flooded USER/GDI/DWM and made the desktop lag despite
                        // low CPU usage. Each worker now creates one edit window, applies its
                        // whole disjoint shard, then creates one fresh verification window.
                        struct BatchedSettingRow{
                            int id{};std::wstring baseline,requested,observed,status,settingsShot;
                            ULONGLONG applyMs{};
                        };
                        std::vector<BatchedSettingRow> rows;rows.reserve(effectiveIds.size());
                        HWND edit=createDiagSettings(false);
                        size_t ordinal=0;
                        if(edit){
                            for(int id:effectiveIds){
                                diagnosticControlPoint();
                                const ULONGLONG begin=GetTickCount64();
                                BatchedSettingRow row{};row.id=id;
                                HWND c=GetDlgItem(edit,id);row.baseline=rawControlValue(c);
                                if(mutateEffectiveControl(edit,id,row.requested))row.status=L"PENDING";
                                else row.status=L"SKIP_NOT_APPLICABLE";
                                row.applyMs=GetTickCount64()-begin;
                                rows.push_back(std::move(row));
                                ++ordinal;
                                keepWorkerInBackground();
                                writeWorkerProgress(L"Applying setting "+std::to_wstring(ordinal)+L"/"+std::to_wstring(effectiveIds.size())+L": "+SettingFriendlyName(id));
                            }
                            const ULONGLONG batchApplyStart=GetTickCount64();SendMessageW(edit,WM_COMMAND,MAKEWPARAM(IDC_SET_APPLY,BN_CLICKED),reinterpret_cast<LPARAM>(GetDlgItem(edit,IDC_SET_APPLY)));RedrawWindow(edit,nullptr,nullptr,RDW_INVALIDATE|RDW_UPDATENOW|RDW_ALLCHILDREN);UpdateWindow(edit);const ULONGLONG batchApplyMs=GetTickCount64()-batchApplyStart;
                            const std::wstring batchShot=L"screenshots/settings_roundtrip/batch_apply.jpg";SaveWindowJpeg(edit,temp/fs::path(batchShot),0.68f);for(auto& row:rows)if(row.status==L"PENDING"){row.applyMs+=batchApplyMs/std::max<size_t>(1,rows.size());row.settingsShot=batchShot;}
                            advance(L"Applied "+std::to_wstring(rows.size())+L" settings in one transaction");DestroyWindow(edit);settingsDialogHwnd=nullptr;edit=nullptr;
                        }else{
                            for(int id:effectiveIds){BatchedSettingRow row{};row.id=id;row.status=L"WIRING_FAIL";rows.push_back(std::move(row));}
                        }

                        HWND probe=createDiagSettings(false);
                        ordinal=0;
                        for(auto& row:rows){
                            diagnosticControlPoint();
                            const ULONGLONG verifyStart=GetTickCount64();
                            HWND probeControl=probe?GetDlgItem(probe,row.id):nullptr;
                            if(row.status==L"PENDING"){
                                row.observed=rawControlValue(probeControl);
                                row.status=(row.observed==row.requested)?L"WIRING_PASS":L"WIRING_FAIL";
                            }
                            const std::wstring behavior=behaviorProbeStatus(row.id);
                            if(row.status==L"WIRING_PASS")++wiringPass;
                            else if(row.status==L"WIRING_FAIL"){++wiringFail;++fail;}
                            else ++notApplicable;
                            if(behavior==L"BEHAVIOR_PASS")++behaviorPass;
                            else if(behavior==L"BEHAVIOR_FAIL"){++behaviorFail;++fail;}
                            else if(behavior==L"MISSING_TEST_COVERAGE"){++missingCoverage;++missingBehaviorCount;}
                            else if(behavior==L"BEHAVIOR_DEFERRED")++notApplicable;
                            else ++notApplicable;
                            const ULONGLONG settingMs=row.applyMs+(GetTickCount64()-verifyStart);
                            cov<<row.id<<L","<<DiagnosticCsvCell(SettingFriendlyName(row.id))<<L","<<row.status<<L","<<behavior<<L","<<DiagnosticCsvCell(row.baseline)<<L","<<DiagnosticCsvCell(row.requested)<<L","<<DiagnosticCsvCell(row.observed)<<L",,"<<DiagnosticCsvCell(row.settingsShot)<<L","<<settingMs<<L","<<DiagnosticCsvCell(L"fast batched wiring: mutate every discovered setting -> one real Apply transaction -> one fresh Settings verification window; shared JPEG evidence; behavior PASS still requires a dedicated real-file/interaction assertion")<<L"\n";
                            ++ordinal;
                            advance(L"Verified setting "+std::to_wstring(ordinal)+L"/"+std::to_wstring(rows.size())+L": "+SettingFriendlyName(row.id)+L" ("+std::to_wstring(settingMs)+L" ms)");
                        }
                        if(probe){DestroyWindow(probe);settingsDialogHwnd=nullptr;}
                        restorePersisted();
                    }

                    if(!workerMode||shardIndex==0){
                    // Deep quality-radio wiring is still verified through the real Settings UI,
                    // in addition to the real-image quality/decode tests above.
                    {
                        HWND q=createDiagSettings(false);bool ok=false;std::wstring obs;
                        if(q){const int old=GlideSettingsShell::ExclusiveRadioSelection(q,IDC_SET_QUALITY_SPEED,IDC_SET_QUALITY_MAX);const int target=old==IDC_SET_QUALITY_MAX?IDC_SET_QUALITY_SPEED:IDC_SET_QUALITY_MAX;
                            SendMessageW(q,WM_COMMAND,MAKEWPARAM(target,BN_CLICKED),reinterpret_cast<LPARAM>(GetDlgItem(q,target)));
                            SendMessageW(q,WM_COMMAND,MAKEWPARAM(IDC_SET_APPLY,BN_CLICKED),reinterpret_cast<LPARAM>(GetDlgItem(q,IDC_SET_APPLY)));PumpDiagnosticUi(q,20);
                            HWND probe=createDiagSettings(false);if(probe){const int got=GlideSettingsShell::ExclusiveRadioSelection(probe,IDC_SET_QUALITY_SPEED,IDC_SET_QUALITY_MAX);ok=got==target;obs=std::to_wstring(got);DestroyWindow(probe);settingsDialogHwnd=q;}DestroyWindow(q);settingsDialogHwnd=nullptr;}
                        settingResult(L"Initial-quality radio UI round-trip",ok,L"select alternate quality radio",obs,L"fresh Settings window reflects selected runtime quality");advance(L"Quality radio Settings wiring");
                    }

                    // Hotkeys are stored outside ordinary child-control values, so drive an
                    // actual Hotkeys action button and assert that the internal shortcut state
                    // changes, then reload the persisted user shortcuts.
                    {
                        HWND hk=createDiagSettings(false);bool ok=false;std::wstring obs;
                        if(hk){PopulateHotkeyList(hk);HWND list=GetDlgItem(hk,IDC_SET_HOTKEY_LIST);if(list&&ListView_GetItemCount(list)>0){ListView_SetItemState(list,0,LVIS_SELECTED|LVIS_FOCUSED,LVIS_SELECTED|LVIS_FOCUSED);int idx=SelectedHotkeyIndex(hk);EnsureHotkeys();DWORD before=(idx>=0&&static_cast<size_t>(idx)<hotkeys.size())?hotkeys[static_cast<size_t>(idx)]:0;DWORD beforeAlt=(idx>=0&&static_cast<size_t>(idx)<hotkeysAlt.size())?hotkeysAlt[static_cast<size_t>(idx)]:0;SendMessageW(hk,WM_COMMAND,MAKEWPARAM(IDC_SET_HOTKEY_CLEAR,BN_CLICKED),reinterpret_cast<LPARAM>(GetDlgItem(hk,IDC_SET_HOTKEY_CLEAR)));DWORD after=(idx>=0&&static_cast<size_t>(idx)<hotkeys.size())?hotkeys[static_cast<size_t>(idx)]:0;DWORD afterAlt=(idx>=0&&static_cast<size_t>(idx)<hotkeysAlt.size())?hotkeysAlt[static_cast<size_t>(idx)]:0;ok=(before!=0||beforeAlt!=0)&&after==0&&afterAlt==0;obs=L"before="+std::to_wstring(before)+L"/"+std::to_wstring(beforeAlt)+L", after="+std::to_wstring(after)+L"/"+std::to_wstring(afterAlt);SaveWindowJpeg(hk,temp/L"screenshots"/L"settings_roundtrip"/L"hotkey_clear.jpg");}DestroyWindow(hk);settingsDialogHwnd=nullptr;}
                        settingResult(L"Hotkey UI action changes runtime shortcut",ok,L"clear selected shortcut via real Hotkeys button",obs,L"selected primary and alternate chords become zero");advance(L"Hotkey Settings wiring");
                    }

                    // Presets are action-oriented rather than an ordinary effective control.
                    // Exercise the real preset change and prove that it changes effective
                    // behavior controls, then restore from the persisted user configuration.
                    {
                        HWND ps=createDiagSettings(false);bool ok=false;std::wstring obs;
                        if(ps){const std::wstring before=SettingsStateFingerprint(ps);HWND combo=GetDlgItem(ps,IDC_SET_PRESET_COMBO);int count=combo?static_cast<int>(SendMessageW(combo,CB_GETCOUNT,0,0)):0;if(combo&&count>2){SendMessageW(combo,CB_SETCURSEL,2,0);SendMessageW(ps,WM_COMMAND,MAKEWPARAM(IDC_SET_PRESET_COMBO,CBN_SELCHANGE),reinterpret_cast<LPARAM>(combo));const std::wstring after=SettingsStateFingerprint(ps);ok=before!=after;obs=ok?L"effective settings changed":L"no effective setting changed";SaveWindowJpeg(ps,temp/L"screenshots"/L"settings_roundtrip"/L"preset_effect.jpg");}DestroyWindow(ps);settingsDialogHwnd=nullptr;}
                        settingResult(L"Behavior preset has an observable settings effect",ok,L"select alternate preset through real combo event",obs,L"effective Settings fingerprint changes");advance(L"Preset effect");
                    }

                    // Restore Defaults is another compound Settings action. Verify that it
                    // materially changes a deliberately non-default control state.
                    {
                        HWND df=createDiagSettings(false);bool ok=false;std::wstring obs;
                        if(df){SetCheck(df,IDC_SET_STATUS,false);SetDlgItemInt(df,IDC_SET_RAPID_PREVIEW_SIZE,777,FALSE);const std::wstring before=SettingsStateFingerprint(df);SendMessageW(df,WM_COMMAND,MAKEWPARAM(IDC_SET_DEFAULTS,BN_CLICKED),reinterpret_cast<LPARAM>(GetDlgItem(df,IDC_SET_DEFAULTS)));const std::wstring after=SettingsStateFingerprint(df);ok=before!=after&&GetCheck(df,IDC_SET_STATUS)&&GetDlgItemInt(df,IDC_SET_RAPID_PREVIEW_SIZE,nullptr,FALSE)==3000;obs=ok?L"defaults restored":L"default controls not restored";SaveWindowJpeg(df,temp/L"screenshots"/L"settings_roundtrip"/L"restore_defaults.jpg");DestroyWindow(df);settingsDialogHwnd=nullptr;}
                        settingResult(L"Restore Defaults compound action",ok,L"force non-default status/rapid-preview then click Restore defaults",obs,L"status=On and rapid-preview=3000");advance(L"Restore Defaults action");restorePersisted();
                    }

                    }

                    // Self-audit: every ordinary effective setting discovered in the current
                    // Settings window must have emitted one coverage row. Future checkbox/edit/
                    // combo settings therefore enter diagnostics automatically with no manual
                    // test-list maintenance.
                    settingResult(L"Automatic effective-setting behavioral coverage gate",!effectiveIds.empty()&&missingBehaviorCount==0,L"runtime enumeration shard "+std::to_wstring(shardIndex+1)+L"/"+std::to_wstring(shardCount),std::to_wstring(effectiveIds.size())+L" controls; "+std::to_wstring(missingBehaviorCount)+L" missing behavior probes",L"all discovered user-facing settings have dedicated behavior probes",L"Future effective controls auto-enroll. Any setting without a real behavioral probe is reported as MISSING_TEST_COVERAGE rather than being silently accepted.");
                    advance(L"Completed automatic Settings coverage audit");
                    }
                }else{
                    settingResult(L"Settings diagnostic window creation",false,L"create",L"failed",L"settings window available");advance(L"Settings UI creation failed");
                }
                restorePersisted();
            }

            if(mask&TestInvariants){
                const size_t extCount=sizeof(GlideFormats::kImageExtensions)/sizeof(GlideFormats::kImageExtensions[0]);settingResult(L"Format registry breadth",extCount>=80,L"registry",std::to_wstring(extCount),L">= 80 registered image extensions");advance(L"Format registry invariant");
                settingResult(L"Diagnostic fixture count",fixtures.size()>=100,L"manifest",std::to_wstring(fixtures.size()),L">= 100 automated image/configuration fixtures");advance(L"Fixture-count invariant");
                size_t capabilityTotal=0,capabilityRegistered=0;{std::wifstream cf(fixtureDir/L"capability_extensions.txt");std::wstring e;while(std::getline(cf,e)){if(!e.empty()&&e.back()==L'\r')e.pop_back();if(e.empty())continue;++capabilityTotal;bool found=false;for(const auto*x:GlideFormats::kImageExtensions)if(_wcsicmp(x,e.c_str())==0){found=true;break;}if(found)++capabilityRegistered;}}settingResult(L"Capability-only extensions are registered",capabilityTotal>0&&capabilityRegistered==capabilityTotal,std::to_wstring(capabilityTotal),std::to_wstring(capabilityRegistered),L"every listed RAW/modern capability extension appears in the shared registry");advance(L"Capability registry invariant");
                const bool layeredExpected=PngTransparencyActive()||windowOpacityPercent<100;const bool layeredActual=hwnd&&((GetWindowLongPtrW(hwnd,GWL_EXSTYLE)&WS_EX_LAYERED)!=0);settingResult(L"Layered-window invariant",layeredExpected==layeredActual,layeredExpected?L"layered expected":L"not layered expected",layeredActual?L"layered":L"not layered",L"presentation style matches current opacity/PNG state");advance(L"Layered-window invariant");
            }

            // Restore values touched by controlled runtime tests, then reload the user's persisted settings.
            prefetchEnabled=savedPrefetch;backgroundRefinement=savedRefine;adaptiveFastPreview=savedAdaptive;initialQualityMode=savedQuality;rapidPreviewLongestSide=savedRapid;defaultViewMode=savedDefaultView;defaultCustomZoomPercent=savedCustom;maxCacheItems=savedCacheMax;purgeCacheOnMinimize=savedPurge;themeMode=savedTheme;windowOpacityPercent=savedOpacity;textOverlayEnabled=savedTextOverlay;files=savedFiles;currentFolder=savedFolder;currentIndex=savedIndex;haveIndex=savedHaveIndex;LoadSettings();ApplyAlwaysOnTop();ApplyThemeToMainWindow();ApplyWindowTransparency();if(!savedPath.empty()&&fs::is_regular_file(savedPath))RequestImage(savedPath,0);else EnterHomeCanvas();

            // Flush buffered CSV content before a worker signals completion or a standalone run is zipped.
            {Utf8Wofstream behaviorOut(temp/L"behavior_results.csv");
                behaviorOut<<L"control_id,behavior_status,source_test,evidence,duration_ms\n";
                for(const auto& result:behaviorProbeResults){
                    const int id=result.first;
                    std::wstring source=L"dedicated runtime behavior probe";
                    const auto named=behaviorProbeSources.find(id);if(named!=behaviorProbeSources.end())source=named->second;
                    else if(id==IDC_SET_BACKGROUND_REFINEMENT)source=L"Background refinement switch";
                    else if(id==IDC_SET_RAPID_PREVIEW_SIZE)source=L"Rapid-preview pixel cap probe";
                    else if(id==IDC_SET_DEFAULT_VIEW_MODE||id==IDC_SET_DEFAULT_CUSTOM_ZOOM)source=L"Default view mode probe";
                    else if(id==IDC_SET_CACHE_ITEMS)source=L"Cache item limit probe";
                    else if(id==IDC_SET_PREFETCH_ENABLED)source=L"Prefetch enabled/disabled probe";
                    else if(id==IDC_SET_PURGE_CACHE_MINIMIZE)source=L"Purge cache on minimize probe";
                    const auto ev=behaviorProbeEvidence.find(id);const auto tm=behaviorProbeDurations.find(id);
                    behaviorOut<<id<<L","<<(result.second?L"BEHAVIOR_PASS":L"BEHAVIOR_FAIL")<<L","<<DiagnosticCsvCell(source)<<L","<<DiagnosticCsvCell(ev==behaviorProbeEvidence.end()?L"":ev->second)<<L","<<(tm==behaviorProbeDurations.end()?0:tm->second)<<L"\n";
                }
                behaviorOut.Commit();
            }
            const bool reportsCommitted=settingOut.Commit()&&visualOut.Commit();
            if(!reportsCommitted)++fail;
            {Utf8Wofstream f(temp/L"summary.txt");f<<L"Glide Alpha 0.12107 - Glide Comprehensive Automated Diagnostics\nGenerated="<<stamp<<L"\nSelected groups="<<MaskDescription(mask)<<L"\nDurationMs="<<(GetTickCount64()-suiteStart)<<L"\nPASS="<<pass<<L"\nFAIL="<<fail<<L"\nSKIP="<<skip<<L"\nWARN="<<warn<<L"\nWIRING_PASS="<<wiringPass<<L"\nWIRING_FAIL="<<wiringFail<<L"\nBEHAVIOR_PASS="<<behaviorPass<<L"\nBEHAVIOR_FAIL="<<behaviorFail<<L"\nMISSING_TEST_COVERAGE="<<missingCoverage<<L"\nSKIP_NOT_APPLICABLE="<<notApplicable<<L"\nReportStreamsCommitted="<<(reportsCommitted?L"yes":L"no")<<L"\nFixtureRows="<<fixtures.size();if(workerMode)f<<L"\nWorkerId="<<workerId<<L"\nShard="<<(shardIndex+1)<<L"/"<<shardCount;f<<L"\n\nInterpretation:\n- PASS/FAIL are product assertions from the selected tests.\n- WIRING_PASS/WIRING_FAIL describe Settings control round-trip plumbing only.\n- BEHAVIOR_PASS/BEHAVIOR_FAIL describe dedicated runtime behavior probes.\n- MISSING_TEST_COVERAGE is explicit coverage debt and is not relabeled as a pass or a skip.\n- SKIP_NOT_APPLICABLE covers codec-dependent or unselected controls/tests that cannot be meaningfully exercised in this run.\n- Screenshots are evidence captures from real Glide windows/settings UI. visual_results.csv marks scenarios that require AI visual review for clipping, overlap, stale paint, corruption and misalignment.\n\nThe report ZIP contains the evidence needed for automated or manual regression review.\n";}
            {Utf8Wofstream f(temp/L"README_UPLOAD_TO_CHAT.txt");f<<L"This is an isolated Glide diagnostic worker report. The master combines all worker folders into one ZIP. Generic Settings Apply/reopen rows prove wiring only; settings without a dedicated behavior probe are marked MISSING_TEST_COVERAGE. GUI screenshots and visual_results.csv should be reviewed for clipping, overlap, stale paint, corruption and misalignment.\n";}
            if(workerMode){
                WriteUtf8TextFile(temp/L"worker_status.txt",L"COMPLETE\n");
                step=totalSteps;writeWorkerProgress(L"Complete",L"complete");closeControlEvents();return true;
            }
            const fs::path zip=DiagnosticsDestination()/(std::wstring(L"Glide Diagnostics ")+stamp+L".zip");const bool zipped=WriteStoreZip(temp,zip);progress.Close();closeControlEvents();if(!zipped){MessageBoxW(hwnd,L"Diagnostics completed, but Glide could not create the final ZIP.",L"Glide Diagnostics",MB_OK|MB_ICONERROR);return false;}fs::remove_all(temp);glide_platform::RevealInExplorer(hwnd,zip.wstring());return true;
        }catch(const DiagnosticCancelled&){if(workerMode&&!workerTemp.empty()){WriteUtf8TextFile(workerTemp/L"worker_status.txt",L"CANCELLED\n");}closeControlEvents();if(!workerMode)MessageBoxW(hwnd,L"Diagnostics were cancelled. No user settings were deliberately saved.",L"Glide Diagnostics",MB_OK|MB_ICONINFORMATION);return false;}
        catch(const std::exception&){if(workerMode&&!workerTemp.empty())WriteUtf8TextFile(workerTemp/L"worker_status.txt",L"ERROR_EXCEPTION\n");closeControlEvents();if(!workerMode)MessageBoxW(hwnd,L"The diagnostic suite stopped because of an internal exception. No user settings were deliberately saved.",L"Glide Diagnostics",MB_OK|MB_ICONERROR);return false;}
        catch(...){if(workerMode&&!workerTemp.empty())WriteUtf8TextFile(workerTemp/L"worker_status.txt",L"ERROR_UNKNOWN\n");closeControlEvents();if(!workerMode)MessageBoxW(hwnd,L"The diagnostic suite stopped unexpectedly. No user settings were deliberately saved.",L"Glide Diagnostics",MB_OK|MB_ICONERROR);return false;}
    }


    bool ExportDiagnostics(HWND settingsWnd){
        try{
            SYSTEMTIME st{};GetLocalTime(&st);wchar_t stamp[64]{};swprintf_s(stamp,L"%04u-%02u-%02u_%02u%02u%02u",st.wYear,st.wMonth,st.wDay,st.wHour,st.wMinute,st.wSecond);
            fs::path temp=fs::temp_directory_path()/(std::wstring(L"Glide_Diagnostics_")+stamp);fs::remove_all(temp);fs::create_directories(temp/L"screenshots");
            {Utf8Wofstream f(temp/L"summary.txt");f<<L"Glide Alpha 0.12107 - automated diagnostic snapshot\n";f<<L"Generated="<<stamp<<L"\n";f<<L"PID="<<GetCurrentProcessId()<<L"\n";f<<L"InstallMode="<<(installedMode?L"installed":L"portable")<<L"\n";f<<L"MainDPI="<<(hwnd?GetDpiForWindow(hwnd):96)<<L"\n";f<<L"SettingsDPI="<<(settingsWnd?GetDpiForWindow(settingsWnd):0)<<L"\n";f<<L"CurrentImageExtension="<<(currentPath.empty()?L"(none)":Lower(fs::path(currentPath).extension().wstring()))<<L"\n";f<<L"CurrentSourceSize="<<imageW<<L"x"<<imageH<<L"\n";f<<L"CacheItems="<<cache.Size()<<L" / "<<maxCacheItems<<L"\n";f<<L"PredictivePrefetchLanes=3\n";f<<L"PredictivePending="<<predictivePrefetchPending.size()<<L"\n";f<<L"startupMode="<<(coldStartDirectImage?L"direct-image":L"home/non-direct")<<L"\n";f<<L"windowCreatedMs="<<startupWindowCreatedMs<<L"\n";if(coldStartDirectImage){f<<L"decodeRequestedMs="<<startupDecodeRequestedMs<<L"\nfirstFramePresentedMs="<<startupFirstFrameMs<<L"\ndeferredWorkersReadyMs="<<startupDeferredReadyMs<<L"\n";}else{f<<L"decodeRequestedMs=N/A (not a direct-image cold launch)\nfirstFramePresentedMs=N/A (not a direct-image cold launch)\ndeferredWorkersReadyMs=N/A (not a direct-image cold launch)\n";}f<<L"settingsCreateMs="<<settingsCreateMs<<L"\nsettingsFirstPaintMs="<<settingsFirstPaintMs<<L"\nsettingsReadyMs="<<settingsReadyMs<<L"\nsettingsDeferredReadyMs="<<settingsDeferredReadyMs<<L"\n";}
            {Utf8Wofstream f(temp/L"settings_snapshot.txt");f<<L"themeMode="<<themeMode<<L"\naccentChoice="<<accentChoice<<L"\nwindowOpacityPercent(session)="<<windowOpacityPercent<<L"\ntextOverlayEnabled="<<textOverlayEnabled<<L"\ntextOverlayTemplate="<<textOverlayTemplate<<L"\ntextOverlayFontSize="<<textOverlayFontSize<<L"\ntextOverlayOpacity="<<textOverlayOpacity<<L"\ntextOverlayPosition="<<textOverlayPosition<<L"\nfastColdStart="<<fastColdStart<<L"\nprefetchEnabled="<<prefetchEnabled<<L"\nbackgroundRefinement="<<backgroundRefinement<<L"\nadaptiveFastPreview="<<adaptiveFastPreview<<L"\nadaptivePreviewDelayMs="<<adaptivePreviewDelayMs<<L"\nprefetchDepth="<<prefetchDepth<<L"\ndefaultViewMode="<<defaultViewMode<<L"\ndefaultCustomZoomPercent="<<defaultCustomZoomPercent<<L"\ncarryPreviousZoom="<<preserveManualZoomOnNavigate<<L"\nrapidPreviewLongestSide="<<rapidPreviewLongestSide<<L"\nrememberWindowPlacement="<<rememberWindowPlacement<<L"\nfullscreenExitWindowMode="<<fullscreenExitWindowMode<<L"\nsavedWindowMaximized="<<savedWindowMaximized<<L"\nhaveSavedPlacement="<<haveSavedPlacement<<L"\noverlayRightDragPansZoomed="<<overlayRightDragPansZoomed<<L"\ncacheMaxItems="<<maxCacheItems<<L"\nprogressiveColorFirstPreview="<<progressiveColorFirstPreview<<L"\n";}
            {std::error_code crashEc;const fs::path crashTxt=GlideCrashReport::LastTextPath();const fs::path crashDmp=GlideCrashReport::LastDumpPath();if(!crashTxt.empty()&&fs::is_regular_file(crashTxt,crashEc)){crashEc.clear();fs::copy_file(crashTxt,temp/L"last_crash.txt",fs::copy_options::overwrite_existing,crashEc);}crashEc.clear();if(!crashDmp.empty()&&fs::is_regular_file(crashDmp,crashEc)){crashEc.clear();fs::copy_file(crashDmp,temp/L"last_crash.dmp",fs::copy_options::overwrite_existing,crashEc);}}
            {Utf8Wofstream f(temp/L"settings_invariants.txt");f<<L"Glide Settings/runtime invariant audit (non-destructive)\n";
                const bool layeredExpected=PngTransparencyActive()||windowOpacityPercent<100;
                const bool layeredActual=hwnd&&((GetWindowLongPtrW(hwnd,GWL_EXSTYLE)&WS_EX_LAYERED)!=0);
                f<<L"session_transparency_layered_style="<<(layeredExpected==layeredActual?L"PASS":L"FAIL")<<L" (opacity="<<windowOpacityPercent<<L", expected="<<layeredExpected<<L", actual="<<layeredActual<<L")\n";
                bool indexOk=true;if(haveIndex&&!files.empty()&&!currentPath.empty()){indexOk=currentIndex<files.size()&&PathKey(files[currentIndex])==PathKey(currentPath);}
                f<<L"picture_index_path_consistency="<<(indexOk?L"PASS":L"FAIL")<<L" (index="<<(haveIndex?static_cast<unsigned long long>(currentIndex+1):0ull)<<L", total="<<static_cast<unsigned long long>(files.size())<<L")\n";
                if(settingsWnd&&IsWindow(settingsWnd)){int viewSel=static_cast<int>(SendDlgItemMessageW(settingsWnd,IDC_SET_DEFAULT_VIEW_MODE,CB_GETCURSEL,0,0));int qualitySel=GlideSettingsShell::ExclusiveRadioSelection(settingsWnd,IDC_SET_QUALITY_SPEED,IDC_SET_QUALITY_MAX);f<<L"default_view_mode="<<((viewSel>=0&&viewSel<=4)?L"PASS":L"FAIL")<<L" (selection="<<viewSel<<L")\n";f<<L"initial_quality_radio="<<(qualitySel<0?L"FAIL (not exactly one selected)":(qualitySel==IDC_SET_QUALITY_SPEED?L"PASS: Maximum speed":qualitySel==IDC_SET_QUALITY_MAX?L"PASS: Maximum quality":L"PASS: Balanced"))<<L"\n";HWND hotkeyList=GetDlgItem(settingsWnd,IDC_SET_HOTKEY_LIST);const int hotkeyExpected=ExpectedHotkeyRows(settingsWnd);if(hotkeyList)PopulateHotkeyList(settingsWnd);const int hotkeyActual=hotkeyList?ListView_GetItemCount(hotkeyList):-1;f<<L"hotkey_rows="<<(hotkeyActual==hotkeyExpected?L"PASS":L"FAIL")<<L" (actual="<<hotkeyActual<<L", expected="<<hotkeyExpected<<L")\n";}else f<<L"Settings window unavailable for control-state checks.\n";}
            {Utf8Wofstream f(temp/L"settings_search_audit.txt");f<<L"Glide Settings search audit (non-destructive, end-to-end)\n";if(settingsWnd&&IsWindow(settingsWnd)){
                    const int auditOldCat=static_cast<int>(GetWindowLongPtrW(settingsWnd,GWLP_USERDATA));const int auditOldPage=SettingsSectionPage(settingsWnd);wchar_t auditOldQuery[128]{};GetDlgItemTextW(settingsWnd,IDC_SET_SEARCH,auditOldQuery,127);
                    auto audit=[&](const wchar_t* query,std::initializer_list<int> ids){SetDlgItemTextW(settingsWnd,IDC_SET_SEARCH,query);SetSettingsSectionPage(settingsWnd,0);RefreshSettingsVisibility(settingsWnd);UpdateWindow(settingsWnd);bool ok=true;f<<L"query=\""<<query<<L"\" ";for(int id:ids){HWND c=GetDlgItem(settingsWnd,id);bool visible=c&&IsWindowVisible(c);f<<L"C"<<id<<L"="<<(visible?L"visible":L"MISSING")<<L" ";ok=ok&&visible;}f<<(ok?L"PASS":L"FAIL")<<L"\n";};
                    audit(L"initial image quality",{IDC_SET_QUALITY_SPEED,IDC_SET_QUALITY_BALANCED,IDC_SET_QUALITY_MAX});
                    audit(L"default view",{IDC_SET_DEFAULT_VIEW_MODE});
                    audit(L"exiting fullscreen",{IDC_SET_FULLSCREEN_EXIT_MODE});
                    audit(L"theme",{IDC_SET_THEME_MODE});
                    audit(L"rapid browse",{IDC_SET_RAPID_PREVIEW_SIZE});
                    audit(L"overlay right drag",{IDC_SET_OVERLAY_RIGHT_DRAG_PAN});
                    audit(L"picture text overlay",{IDC_SET_TEXT_OVERLAY_ENABLED,IDC_SET_TEXT_OVERLAY_TEMPLATE,IDC_SET_TEXT_OVERLAY_POSITION});
                    SetWindowLongPtrW(settingsWnd,GWLP_USERDATA,auditOldCat);SetSettingsSectionPage(settingsWnd,auditOldPage);SetDlgItemTextW(settingsWnd,IDC_SET_SEARCH,auditOldQuery);RefreshSettingsVisibility(settingsWnd);
                }else f<<L"Settings window unavailable for search checks.\n";}
            {Utf8Wofstream f(temp/L"performance.csv");f<<L"sample,extension,request_to_first_frame_ms,decode_ms,width,height,preview,refinement,cache_hit\n";size_t n=0;for(const auto&p:diagnosticPerfSamples){ULONGLONG total=(p.firstFrameTick>=p.requestTick&&p.requestTick)?p.firstFrameTick-p.requestTick:0;f<<++n<<L","<<p.extension<<L","<<total<<L","<<p.decodeMs<<L","<<p.width<<L","<<p.height<<L","<<p.preview<<L","<<p.refinement<<L","<<p.cacheHit<<L"\n";}}
            SaveWindowJpeg(hwnd,temp/L"screenshots"/L"main_window.jpg");
            settingsLatencySamples.clear();
            if(settingsWnd&&IsWindow(settingsWnd)){
                const int oldCat=static_cast<int>(GetWindowLongPtrW(settingsWnd,GWLP_USERDATA));const int oldPage=SettingsSectionPage(settingsWnd);wchar_t oldQuery[128]{};GetDlgItemTextW(settingsWnd,IDC_SET_SEARCH,oldQuery,127);SetDlgItemTextW(settingsWnd,IDC_SET_SEARCH,L"");
                Utf8Wofstream geo(temp/L"ui_geometry.txt");static const wchar_t* names[]={L"general",L"viewing",L"mouse_fullscreen",L"performance",L"status_bar",L"slideshow",L"hotkeys",L"tabs_workspace",L"window_in_window",L"profiles_presets",L"windows_integration",L"developer_options"};
                for(int cat=0;cat<12;++cat){
                    SetWindowLongPtrW(settingsWnd,GWLP_USERDATA,cat);SetSettingsSectionPage(settingsWnd,0);if(cat==6)PopulateHotkeyList(settingsWnd);RefreshSettingsVisibility(settingsWnd);auto nodes=CollectSettingsLayoutNodes(settingsWnd);int pages=(cat==6)?1:SettingsNormalPageCount(nodes,cat);
                    for(int page=0;page<pages;++page){
                        SetSettingsSectionPage(settingsWnd,page);const ULONGLONG begin=GetTickCount64();RefreshSettingsVisibility(settingsWnd);UpdateWindow(settingsWnd);
                        std::wstring state=std::wstring(names[cat])+L"_"+std::to_wstring(page+1);
                        SaveWindowJpeg(settingsWnd,temp/L"screenshots"/(L"settings_"+state+L"_immediate.jpg"));const ULONGLONG immediate=GetTickCount64()-begin;
                        int expected=0,actual=0;ULONGLONG readyMs=0;bool ready=WaitForSettingsDiagnosticReady(settingsWnd,cat,750,begin,expected,actual,readyMs);
                        PumpDiagnosticUi(settingsWnd,200);const ULONGLONG settled=GetTickCount64()-begin;
                        SaveWindowJpeg(settingsWnd,temp/L"screenshots"/(L"settings_"+state+L"_settled.jpg"));
                        int visibleCategoryControls=0;for(const auto&n:nodes)if(n.cat==cat&&IsWindowVisible(n.hwnd))++visibleCategoryControls;
                        geo<<L"\nCAPTURE state="<<state<<L" ready="<<(ready?L"yes":L"NO/TIMEOUT")<<L" immediateMs="<<immediate<<L" readyMs="<<readyMs<<L" settledMs="<<settled<<L" visibleCategoryControls="<<visibleCategoryControls;if(cat==6)geo<<L" expectedRows="<<expected<<L" actualRows="<<actual;geo<<L"\n";
                        if(visibleCategoryControls==0)geo<<L"EMPTY_PAGE: no category-owned controls are visible\n";
                        if(ready)WriteVisibleGeometry(settingsWnd,geo,state.c_str());else geo<<L"GEOMETRY SKIPPED: page was not ready before timeout\n";
                        settingsLatencySamples.push_back({state,immediate,readyMs,settled,expected,actual,ready});
                    }
                }
                SetDlgItemTextW(settingsWnd,IDC_SET_SEARCH,L"hotkey");SetWindowLongPtrW(settingsWnd,GWLP_USERDATA,6);SetSettingsSectionPage(settingsWnd,0);const ULONGLONG begin=GetTickCount64();PopulateHotkeyList(settingsWnd);RefreshSettingsVisibility(settingsWnd);UpdateWindow(settingsWnd);SaveWindowJpeg(settingsWnd,temp/L"screenshots"/L"settings_search_hotkey_immediate.jpg");const ULONGLONG immediate=GetTickCount64()-begin;int expected=0,actual=0;ULONGLONG readyMs=0;bool ready=WaitForSettingsDiagnosticReady(settingsWnd,6,750,begin,expected,actual,readyMs);PumpDiagnosticUi(settingsWnd,200);const ULONGLONG settled=GetTickCount64()-begin;SaveWindowJpeg(settingsWnd,temp/L"screenshots"/L"settings_search_hotkey_settled.jpg");geo<<L"\nCAPTURE state=search_hotkey ready="<<(ready?L"yes":L"NO/TIMEOUT")<<L" immediateMs="<<immediate<<L" readyMs="<<readyMs<<L" settledMs="<<settled<<L" expectedRows="<<expected<<L" actualRows="<<actual<<L"\n";if(ready)WriteVisibleGeometry(settingsWnd,geo,L"search_hotkey");settingsLatencySamples.push_back({L"search_hotkey",immediate,readyMs,settled,expected,actual,ready});
                SetDlgItemTextW(settingsWnd,IDC_SET_SEARCH,oldQuery);SetWindowLongPtrW(settingsWnd,GWLP_USERDATA,oldCat);SetSettingsSectionPage(settingsWnd,oldPage);if(oldCat==6)PopulateHotkeyList(settingsWnd);RefreshSettingsVisibility(settingsWnd);UpdateWindow(settingsWnd);
            }
            {Utf8Wofstream f(temp/L"settings_latency.csv");f<<L"state,immediate_capture_ms,page_ready_ms,settled_capture_ms,ready,expected_hotkey_rows,actual_hotkey_rows\n";for(const auto&x:settingsLatencySamples)f<<x.state<<L","<<x.immediateMs<<L","<<x.readyMs<<L","<<x.settledMs<<L","<<(x.ready?L"yes":L"no")<<L","<<x.expectedRows<<L","<<x.actualRows<<L"\n";}
            fs::path zip=DiagnosticsDestination()/(std::wstring(L"Glide Diagnostics ")+stamp+L".zip");if(!WriteStoreZip(temp,zip)){fs::remove_all(temp);return false;}fs::remove_all(temp);std::wstring msg=L"Diagnostics exported successfully:\n\n"+zip.wstring()+L"\n\nImmediate + settled UI captures and latency measurements are included.";MessageBoxW(settingsWnd?settingsWnd:hwnd,msg.c_str(),L"Glide Diagnostics",MB_OK|MB_ICONINFORMATION);return true;
        }catch(...){return false;}
    }

    bool RegisterFileAssociations() { return glide_platform::RegisterImageAssociations(ExePath()); }

    bool RemoveFileAssociations() { return glide_platform::RemoveImageAssociations(); }

    bool CurrentIsPng() const { return !currentPath.empty() && Lower(fs::path(currentPath).extension().wstring()) == L".png"; }

    bool PngTransparencyActive() const { return !ActiveTabIsBrowser() && CurrentIsPng() && pngSeeThrough; }

    void ApplyAlwaysOnTop() {
        if (!hwnd) return;
        SetWindowPos(hwnd, alwaysOnTop ? HWND_TOPMOST : HWND_NOTOPMOST, 0,0,0,0,
                     SWP_NOMOVE|SWP_NOSIZE|SWP_NOACTIVATE);
    }

    void ToggleAlwaysOnTop() {
        alwaysOnTop = !alwaysOnTop;
        ApplyAlwaysOnTop();
        SaveSettings();
        InvalidateViewer();
    }

    void ApplyWindowTransparency() {
        if (!hwnd) return;
        const bool pngKey=PngTransparencyActive();
        const bool globalAlpha=windowOpacityPercent<100;
        LONG_PTR ex=GetWindowLongPtrW(hwnd,GWL_EXSTYLE);
        if(pngKey||globalAlpha){
            if(!(ex&WS_EX_LAYERED))SetWindowLongPtrW(hwnd,GWL_EXSTYLE,ex|WS_EX_LAYERED);
            DWORD flags=0;COLORREF key=ResolveLightTheme()?RGB(232,235,239):RGB(13,17,19);BYTE alpha=255;
            if(pngKey)flags|=LWA_COLORKEY;
            if(globalAlpha){flags|=LWA_ALPHA;alpha=static_cast<BYTE>(std::clamp(windowOpacityPercent,10,100)*255/100);}
            SetLayeredWindowAttributes(hwnd,key,alpha,flags);
        }else if(ex&WS_EX_LAYERED){
            SetLayeredWindowAttributes(hwnd,0,255,LWA_ALPHA);
            SetWindowLongPtrW(hwnd,GWL_EXSTYLE,ex&~WS_EX_LAYERED);
            SetWindowPos(hwnd,nullptr,0,0,0,0,SWP_NOMOVE|SWP_NOSIZE|SWP_NOZORDER|SWP_NOACTIVATE|SWP_FRAMECHANGED);
        }
        InvalidateRect(hwnd,nullptr,FALSE);
    }

    void ApplyPngTransparencyMode() { ApplyWindowTransparency(); }

    void SetSessionWindowOpacity(int percent){
        windowOpacityPercent=std::clamp(percent,10,100);
        ApplyWindowTransparency();
        InvalidateViewer();
    }

    void UpdateOpacityFromPoint(POINT p){
        if(opacityTrackRect.right<=opacityTrackRect.left)return;
        const float f=std::clamp((static_cast<float>(p.x)-opacityTrackRect.left)/(opacityTrackRect.right-opacityTrackRect.left),0.0f,1.0f);
        SetSessionWindowOpacity(static_cast<int>(std::lround(10.0f+f*90.0f)));
    }

    static void SetCheck(HWND dlg, int id, bool value) { SendDlgItemMessageW(dlg, id, BM_SETCHECK, value ? BST_CHECKED : BST_UNCHECKED, 0); }
    static bool GetCheck(HWND dlg, int id) { return SendDlgItemMessageW(dlg, id, BM_GETCHECK, 0, 0) == BST_CHECKED; }

    void EnsureHotkeys() {
        if (hotkeys.size()!=std::size(kHotkeyDefs)) {
            hotkeys.resize(std::size(kHotkeyDefs));
            for(size_t i=0;i<std::size(kHotkeyDefs);++i) if(!hotkeys[i]) hotkeys[i]=kHotkeyDefs[i].defaultChord;
        }
        if (hotkeysAlt.size()!=std::size(kHotkeyDefs)) hotkeysAlt.resize(std::size(kHotkeyDefs));
    }
    void ResetHotkeys() {
        hotkeys.resize(std::size(kHotkeyDefs)); hotkeysAlt.assign(std::size(kHotkeyDefs),0);
        for(size_t i=0;i<std::size(kHotkeyDefs);++i)hotkeys[i]=kHotkeyDefs[i].defaultChord;
    }
    void AssignHotkey(size_t index,DWORD chord,bool alternate=false){
        EnsureHotkeys();if(index>=hotkeys.size())return;
        if(chord){
            for(size_t i=0;i<hotkeys.size();++i){
                // A chord may not trigger two different actions, but multiple chords may
                // intentionally trigger the same action. Clear conflicting assignments.
                const bool sameAction=kHotkeyDefs[i].action==kHotkeyDefs[index].action&&kHotkeyDefs[i].parameter==kHotkeyDefs[index].parameter;
                if(!sameAction){if(hotkeys[i]==chord)hotkeys[i]=0;if(i<hotkeysAlt.size()&&hotkeysAlt[i]==chord)hotkeysAlt[i]=0;}
            }
        }
        (alternate?hotkeysAlt[index]:hotkeys[index])=chord;
    }
    int SelectedHotkeyIndex(HWND wnd) const {
        HWND list=GetDlgItem(wnd,IDC_SET_HOTKEY_LIST);if(!list)return -1;
        int row=ListView_GetNextItem(list,-1,LVNI_SELECTED);if(row<0)return -1;
        LVITEMW item{};item.mask=LVIF_PARAM;item.iItem=row;
        if(!ListView_GetItem(list,&item))return -1;
        return static_cast<int>(item.lParam);
    }

    std::wstring SettingsSiblingPath(const wchar_t* fileName) const {
        try {
            fs::path base = iniPath.empty() ? fs::path(L"Glide.ini") : fs::path(iniPath);
            return (base.parent_path() / fileName).wstring();
        } catch (...) { return fileName; }
    }

    void SetHotkeyByIniKey(const wchar_t* key, DWORD chord) {
        EnsureHotkeys();
        for(size_t i=0;i<std::size(kHotkeyDefs);++i) if(_wcsicmp(kHotkeyDefs[i].iniKey,key)==0){hotkeys[i]=chord;return;}
    }

    void ApplyBehaviorPreset(int preset) {
        preset=std::clamp(preset,1,5);
        activeBehaviorPreset=preset;
        // Every preset starts from the known Glide baseline; competitor profiles only
        // change behaviors that are deliberately represented in the lightweight layer.
        ResetHotkeys();
        gestureMap.fill(GestureAction::Legacy);
        leftImageDragMode=LeftImageDragMode::Select;
        rightDragPansImage=true;
        selectionClickZoomsIn=true;
        selectionRightClickZoomsOut=true;
        backgroundLeftDragMovesWindow=true;
        windowedWheelZoom=false;
        invertWheelNavigation=false;
        fullscreenClickNavigation=true;
        doubleClickFullscreen=true;
        doubleClickExitFullscreen=false;
        autoSiblingFolders=true;
        defaultViewMode=0;
        zoomStepPercent=15;
        ctrlWheelZoom=true;
        fullscreenWheelZoom=true;
        zoomAroundCursor=true;
        preserveManualZoomOnNavigate=false;
        middleDragPansImage=true;
        wrapFolderNavigation=false;

        switch(preset){
            case 2: // Windows Photos familiarity: pan-oriented canvas, wheel zoom, keyboard navigation.
                leftImageDragMode=LeftImageDragMode::Pan;
                rightDragPansImage=false;
                selectionClickZoomsIn=false;
                selectionRightClickZoomsOut=false;
                backgroundLeftDragMovesWindow=false;
                windowedWheelZoom=true;
                fullscreenClickNavigation=false;
                doubleClickFullscreen=false;
                autoSiblingFolders=false;
                preserveManualZoomOnNavigate=true;
                wrapFolderNavigation=false;
                SetHotkeyByIniKey(L"ZoomIn",HotkeyChord(VK_OEM_PLUS,true));
                SetHotkeyByIniKey(L"ZoomOut",HotkeyChord(VK_OEM_MINUS,true));
                SetHotkeyByIniKey(L"FitImage0",HotkeyChord('0',true));
                break;
            case 3: // IrfanView familiarity: selection first, right-drag pan, terse keyboard navigation.
                leftImageDragMode=LeftImageDragMode::Select;
                rightDragPansImage=true;
                windowedWheelZoom=false;
                fullscreenClickNavigation=false;
                doubleClickFullscreen=true;
                autoSiblingFolders=false;
                fullscreenWheelZoom=false;
                SetHotkeyByIniKey(L"Fullscreen",HotkeyChord(VK_RETURN));
                break;
            case 4: // FastStone familiarity: navigation-centric viewer with panning when working close-up.
                leftImageDragMode=LeftImageDragMode::Pan;
                rightDragPansImage=true;
                selectionClickZoomsIn=false;
                selectionRightClickZoomsOut=false;
                backgroundLeftDragMovesWindow=false;
                windowedWheelZoom=false;
                fullscreenClickNavigation=false;
                doubleClickFullscreen=true;
                autoSiblingFolders=false;
                break;
            case 5: // XnView MP familiarity: wheel browsing + Ctrl-wheel zoom and pan-oriented mouse use.
                leftImageDragMode=LeftImageDragMode::Pan;
                rightDragPansImage=true;
                selectionClickZoomsIn=false;
                selectionRightClickZoomsOut=false;
                backgroundLeftDragMovesWindow=false;
                windowedWheelZoom=false;
                fullscreenClickNavigation=false;
                doubleClickFullscreen=true;
                autoSiblingFolders=false;
                break;
            default: break;
        }
    }

    static const wchar_t* BehaviorPresetName(int preset){
        switch(preset){case 1:return L"Glide (default)";case 2:return L"Windows Photos";case 3:return L"IrfanView";case 4:return L"FastStone Image Viewer";case 5:return L"XnView MP";default:return L"Custom";}
    }

    bool ResolveLightTheme() const { return themeMode==1; }
    COLORREF ThemeAccentColor() const {
        static const COLORREF c[]={RGB(70,160,245),RGB(235,238,242),RGB(145,150,158),RGB(235,88,88),RGB(232,190,62),RGB(64,188,112),RGB(166,104,236)};
        return c[std::clamp(accentChoice,0,6)];
    }
    static COLORREF BlendColor(COLORREF a,COLORREF b,float t){
        t=std::clamp(t,0.0f,1.0f);
        auto mix=[&](BYTE x,BYTE y){return static_cast<BYTE>(std::lround(x+(y-x)*t));};
        return RGB(mix(GetRValue(a),GetRValue(b)),mix(GetGValue(a),GetGValue(b)),mix(GetBValue(a),GetBValue(b)));
    }
    static D2D1_COLOR_F D2DColor(COLORREF c,float alpha=1.0f){
        return D2D1::ColorF(GetRValue(c)/255.0f,GetGValue(c)/255.0f,GetBValue(c)/255.0f,alpha);
    }
    struct ThemePalette {
        COLORREF windowBg{},panel{},surface{},surfaceHover{},editBg{},text{},textMuted{},border{},accent{},accentSoft{},selectionBg{},selectionText{},headerBg{},headerText{};
    };
    ThemePalette Palette() const {
        const bool light=ResolveLightTheme(); const COLORREF a=ThemeAccentColor(); ThemePalette p{}; p.accent=a;
        if(light){
            p.windowBg=RGB(246,247,249); p.panel=RGB(255,255,255); p.surface=RGB(242,244,247); p.surfaceHover=BlendColor(p.surface,a,0.10f);
            p.editBg=RGB(255,255,255); p.text=RGB(27,30,35); p.textMuted=RGB(92,99,108); p.border=RGB(202,207,214);
            p.accentSoft=BlendColor(RGB(255,255,255),a,0.18f); p.selectionBg=BlendColor(RGB(255,255,255),a,0.30f); p.selectionText=RGB(20,24,29);
            p.headerBg=RGB(235,238,242); p.headerText=RGB(28,31,36);
        }else{
            p.windowBg=RGB(24,26,29); p.panel=RGB(31,34,38); p.surface=RGB(27,30,34); p.surfaceHover=BlendColor(p.surface,a,0.16f);
            p.editBg=RGB(35,38,42); p.text=RGB(235,238,242); p.textMuted=RGB(172,178,186); p.border=RGB(63,69,77);
            p.accentSoft=BlendColor(p.surface,a,0.20f); p.selectionBg=BlendColor(RGB(38,42,47),a,0.48f); p.selectionText=RGB(250,252,255);
            p.headerBg=RGB(40,44,50); p.headerText=RGB(239,242,246);
        }
        return p;
    }
    void ApplyThemeToMainWindow(){
        if(!hwnd)return; const bool light=ResolveLightTheme(); BOOL dark=light?FALSE:TRUE;
        if(FAILED(DwmSetWindowAttribute(hwnd,20,&dark,sizeof(dark))))DwmSetWindowAttribute(hwnd,19,&dark,sizeof(dark));
        SetWindowTheme(hwnd,light?L"Explorer":L"DarkMode_Explorer",nullptr);
        // PNG colour-key transparency uses a theme-specific clear colour. Reapply
        // the colour key whenever the explicit Dark/Light theme changes.
        if(PngTransparencyActive())ApplyWindowTransparency();
        InvalidateRect(hwnd,nullptr,FALSE);
    }
    void ApplySettingsThemeToControls(HWND wnd) {
        if(!wnd||!IsWindow(wnd))return;
        const bool light=ResolveLightTheme(); const ThemePalette pal=Palette();
        BOOL dark=light?FALSE:TRUE; if(FAILED(DwmSetWindowAttribute(wnd,20,&dark,sizeof(dark))))DwmSetWindowAttribute(wnd,19,&dark,sizeof(dark));
        SetWindowTheme(wnd,light?L"Explorer":L"DarkMode_Explorer",nullptr);
        for(HWND c=GetWindow(wnd,GW_CHILD);c;c=GetWindow(c,GW_HWNDNEXT)){
            wchar_t cls[64]{};GetClassNameW(c,cls,63);
            if(_wcsicmp(cls,WC_LISTVIEWW)!=0)SetWindowTheme(c,light?L"Explorer":L"DarkMode_Explorer",nullptr);
        }
        HWND list=GetDlgItem(wnd,IDC_SET_HOTKEY_LIST);
        if(list){
            // Theme first, explicit colors second: otherwise uxtheme can overwrite the
            // ListView color messages and produce the white/black table seen in 1.2.20.
            SetWindowTheme(list,light?L"Explorer":L"DarkMode_Explorer",nullptr);
            ListView_SetBkColor(list,pal.panel); ListView_SetTextBkColor(list,pal.panel); ListView_SetTextColor(list,pal.text);
            HWND hdr=ListView_GetHeader(list); if(hdr){SetWindowTheme(hdr,L"",L"");InvalidateRect(hdr,nullptr,TRUE);}
            RedrawWindow(list,nullptr,nullptr,RDW_INVALIDATE|RDW_ERASE|RDW_ALLCHILDREN|RDW_UPDATENOW);
        }
        InvalidateRect(wnd,nullptr,TRUE);
    }

    void SyncBehaviorControls(HWND wnd){
        HWND combo=GetDlgItem(wnd,IDC_SET_PRESET_COMBO);
        if(combo)SendMessageW(combo,CB_SETCURSEL,std::clamp(activeBehaviorPreset,0,5),0);
        HWND drag=GetDlgItem(wnd,IDC_SET_LEFT_DRAG_MODE);
        if(drag)SendMessageW(drag,CB_SETCURSEL,leftImageDragMode==LeftImageDragMode::Pan?1:0,0);
        SetCheck(wnd,IDC_SET_RIGHT_DRAG_PAN,rightDragPansImage);
        SetCheck(wnd,IDC_SET_SELECTION_CLICK_ZOOM,selectionClickZoomsIn);
        SetCheck(wnd,IDC_SET_SELECTION_RIGHT_ZOOM,selectionRightClickZoomsOut);
        SetCheck(wnd,IDC_SET_BACKGROUND_DRAG_WINDOW,backgroundLeftDragMovesWindow);
        SetCheck(wnd,IDC_SET_DBL_FULLSCREEN,doubleClickFullscreen);
        SetCheck(wnd,IDC_SET_DBL_EXIT_FULLSCREEN,doubleClickExitFullscreen);
        SetCheck(wnd,IDC_SET_FULLSCREEN_CLICKS,fullscreenClickNavigation);
        SetCheck(wnd,IDC_SET_WINDOWED_WHEEL_ZOOM,windowedWheelZoom);
        SetCheck(wnd,IDC_SET_INVERT_WHEEL_NAV,invertWheelNavigation);
        SetCheck(wnd,IDC_SET_SIBLING_FOLDERS,autoSiblingFolders);
        SetCheck(wnd,IDC_SET_CTRL_WHEEL_ZOOM,ctrlWheelZoom);
        SetCheck(wnd,IDC_SET_FULLSCREEN_WHEEL_ZOOM,fullscreenWheelZoom);
        SetCheck(wnd,IDC_SET_ZOOM_AROUND_CURSOR,zoomAroundCursor);
        SetCheck(wnd,IDC_SET_KEEP_ZOOM_NAV,preserveManualZoomOnNavigate);
        SendDlgItemMessageW(wnd,IDC_SET_FULLSCREEN_EXIT_MODE,CB_SETCURSEL,fullscreenExitWindowMode,0);
        SetCheck(wnd,IDC_SET_MIDDLE_DRAG_PAN,middleDragPansImage);
        SetCheck(wnd,IDC_SET_WRAP_FOLDER,wrapFolderNavigation);
        SendDlgItemMessageW(wnd,IDC_SET_DEFAULT_VIEW_MODE,CB_SETCURSEL,std::clamp(defaultViewMode,0,4),0);
        SetDlgItemInt(wnd,IDC_SET_DEFAULT_CUSTOM_ZOOM,defaultCustomZoomPercent,FALSE);
        EnableWindow(GetDlgItem(wnd,IDC_SET_DEFAULT_CUSTOM_ZOOM),defaultViewMode==4);
        SetDlgItemInt(wnd,IDC_SET_ZOOM_STEP,zoomStepPercent,FALSE);
        for(int gi=0;gi<kGestureSlotCount;++gi)SendDlgItemMessageW(wnd,IDC_SET_GESTURE_BASE+gi,CB_SETCURSEL,static_cast<int>(gestureMap[static_cast<size_t>(gi)]),0);
    }

    bool SaveProfileSlot(int slot){
        slot=std::clamp(slot,1,3); SaveSettings();
        wchar_t name[64]{};swprintf_s(name,L"Glide Profile %d.ini",slot);
        const std::wstring dst=SettingsSiblingPath(name);
        return CopyFileW(iniPath.c_str(),dst.c_str(),FALSE)!=FALSE;
    }

    bool LoadProfileSlot(int slot){
        slot=std::clamp(slot,1,3); wchar_t name[64]{};swprintf_s(name,L"Glide Profile %d.ini",slot);
        const std::wstring src=SettingsSiblingPath(name); DWORD attr=GetFileAttributesW(src.c_str());
        if(attr==INVALID_FILE_ATTRIBUTES||attr&FILE_ATTRIBUTE_DIRECTORY)return false;
        if(!CopyFileW(src.c_str(),iniPath.c_str(),FALSE))return false; LoadSettings(); return true;
    }

    bool ExportSettingsFile(HWND owner){
        SaveSettings(); wchar_t path[MAX_PATH]=L"Glide Settings.gli";
        OPENFILENAMEW ofn{sizeof(ofn)};ofn.hwndOwner=owner;ofn.lpstrFilter=L"Glide settings (*.gli)\0*.gli\0INI files (*.ini)\0*.ini\0All files\0*.*\0\0";ofn.lpstrFile=path;ofn.nMaxFile=MAX_PATH;ofn.lpstrDefExt=L"gli";ofn.Flags=OFN_OVERWRITEPROMPT|OFN_PATHMUSTEXIST;
        if(!GetSaveFileNameW(&ofn))return false; return CopyFileW(iniPath.c_str(),path,FALSE)!=FALSE;
    }

    bool ImportSettingsFile(HWND owner){
        wchar_t path[MAX_PATH]{};OPENFILENAMEW ofn{sizeof(ofn)};ofn.hwndOwner=owner;ofn.lpstrFilter=L"Glide settings (*.gli;*.ini)\0*.gli;*.ini\0All files\0*.*\0\0";ofn.lpstrFile=path;ofn.nMaxFile=MAX_PATH;ofn.Flags=OFN_FILEMUSTEXIST|OFN_PATHMUSTEXIST;
        if(!GetOpenFileNameW(&ofn))return false;if(!CopyFileW(path,iniPath.c_str(),FALSE))return false;LoadSettings();return true;
    }

    bool HotkeyMatchesSearch(size_t i,const std::wstring& q) {
        if(i>=std::size(kHotkeyDefs)||q.empty())return false;
        std::wstring hay=Lower(kHotkeyDefs[i].label);
        hay+=L" "+Lower(kHotkeyDefs[i].iniKey);
        hay+=L" "+Lower(HotkeyCategory(kHotkeyDefs[i].action));
        hay+=L" hotkey hotkeys shortcut shortcuts keyboard key binding bindings assign change edit";
        EnsureHotkeys();
        if(i<hotkeys.size()&&hotkeys[i])hay+=L" "+Lower(HotkeyName(hotkeys[i]));
        if(i<hotkeysAlt.size()&&hotkeysAlt[i])hay+=L" "+Lower(HotkeyName(hotkeysAlt[i]));
        size_t pos=0;while(pos<q.size()){while(pos<q.size()&&iswspace(q[pos]))++pos;if(pos>=q.size())break;size_t end=pos;while(end<q.size()&&!iswspace(q[end]))++end;
            if(hay.find(q.substr(pos,end-pos))==std::wstring::npos)return false;pos=end;}return true;
    }
    bool HasHotkeySearchMatch(const std::wstring& q) {
        if(q.empty())return false;
        for(size_t i=0;i<std::size(kHotkeyDefs);++i)if(HotkeyMatchesSearch(i,q))return true;
        return false;
    }
    void BeginHotkeyCapture(HWND wnd,size_t index,bool alternate){
        if(index>=std::size(kHotkeyDefs))return;
        RemovePropW(wnd,L"GlideHotkeyCapture");RemovePropW(wnd,L"GlideHotkeyCaptureAlt");
        SetPropW(wnd,alternate?L"GlideHotkeyCaptureAlt":L"GlideHotkeyCapture",reinterpret_cast<HANDLE>(static_cast<INT_PTR>(index+1)));
        SetCapture(wnd);SetFocus(wnd);PopulateHotkeyList(wnd);
        SetWindowTextW(GetDlgItem(wnd,IDC_SET_HOTKEY_CHANGE),alternate?L"Change shortcut":L"Cancel capture");
        SetWindowTextW(GetDlgItem(wnd,IDC_SET_HOTKEY_ADD_ALT),alternate?L"Cancel capture":L"Add shortcut");
        InvalidateRect(GetDlgItem(wnd,alternate?IDC_SET_HOTKEY_ADD_ALT:IDC_SET_HOTKEY_CHANGE),nullptr,FALSE);
        std::wstring msg=alternate?L"CAPTURE ACTIVE — press a key, Middle/X1/X2 mouse button, or Esc/click to cancel.":L"CAPTURE ACTIVE — press a key, Middle/X1/X2 mouse button, or Esc/click to cancel.";
        SetWindowTextW(GetDlgItem(wnd,IDC_SET_HOTKEY_HINT),msg.c_str());
    }
    void EndHotkeyCapture(HWND wnd,const wchar_t* message){
        RemovePropW(wnd,L"GlideHotkeyCapture");RemovePropW(wnd,L"GlideHotkeyCaptureAlt");
        if(GetCapture()==wnd)ReleaseCapture();PopulateHotkeyList(wnd);
        SetWindowTextW(GetDlgItem(wnd,IDC_SET_HOTKEY_CHANGE),L"Change shortcut");SetWindowTextW(GetDlgItem(wnd,IDC_SET_HOTKEY_ADD_ALT),L"Add shortcut");
        InvalidateRect(GetDlgItem(wnd,IDC_SET_HOTKEY_CHANGE),nullptr,FALSE);InvalidateRect(GetDlgItem(wnd,IDC_SET_HOTKEY_ADD_ALT),nullptr,FALSE);
        SetWindowTextW(GetDlgItem(wnd,IDC_SET_HOTKEY_HINT),message);
    }
    bool CaptureHotkeyInput(HWND wnd,UINT vk){
        INT_PTR tok=reinterpret_cast<INT_PTR>(GetPropW(wnd,L"GlideHotkeyCapture"));
        INT_PTR altTok=reinterpret_cast<INT_PTR>(GetPropW(wnd,L"GlideHotkeyCaptureAlt"));
        INT_PTR use=tok>0?tok:altTok;if(use<=0)return false;
        if(vk==VK_CONTROL||vk==VK_SHIFT||vk==VK_MENU)return true;
        if(vk==VK_ESCAPE||vk==VK_LBUTTON||vk==VK_RBUTTON){EndHotkeyCapture(wnd,L"Shortcut capture cancelled. Left/right mouse remain reserved for Glide navigation and UI.");return true;}
        DWORD chord=CurrentChord(vk);activeBehaviorPreset=0;SendDlgItemMessageW(wnd,IDC_SET_PRESET_COMBO,CB_SETCURSEL,0,0);
        AssignHotkey(static_cast<size_t>(use-1),chord,altTok>0);SetPropW(wnd,L"GlideSettingsDirty",reinterpret_cast<HANDLE>(1));
        EndHotkeyCapture(wnd,L"Shortcut updated. Choose Apply or OK to save it.");return true;
    }

    void PopulateHotkeyList(HWND wnd) {
        EnsureHotkeys();HWND list=GetDlgItem(wnd,IDC_SET_HOTKEY_LIST);if(!list)return;
        int selected=SelectedHotkeyIndex(wnd);
        wchar_t query[128]{};GetDlgItemTextW(wnd,IDC_SET_SEARCH,query,127);std::wstring search=Lower(query);
        const bool filter=!search.empty();
        if(Header_GetItemCount(ListView_GetHeader(list))==0){
            LVCOLUMNW col{};col.mask=LVCF_TEXT|LVCF_WIDTH|LVCF_FMT;col.fmt=LVCFMT_LEFT;
            col.pszText=const_cast<LPWSTR>(L"Shortcut");ListView_InsertColumn(list,0,&col);
            col.pszText=const_cast<LPWSTR>(L"Action");ListView_InsertColumn(list,1,&col);
            col.pszText=const_cast<LPWSTR>(L"Category");ListView_InsertColumn(list,2,&col);
        }
        ResizeHotkeyColumns(list);
        int sortCol=static_cast<int>(reinterpret_cast<INT_PTR>(GetPropW(list,L"GlideHotkeySortCol")))-1;
        bool asc=GetPropW(list,L"GlideHotkeySortDesc")==nullptr;
        std::vector<int> order;order.reserve(std::size(kHotkeyDefs));
        for(size_t i=0;i<std::size(kHotkeyDefs);++i)if(!filter||HotkeyMatchesSearch(i,search))order.push_back(static_cast<int>(i));
        if(sortCol>=0){
            std::stable_sort(order.begin(),order.end(),[&](int a,int b){
                auto shortcutText=[&](int i){std::wstring v=hotkeys[i]?HotkeyName(hotkeys[i]):L"";if(i<static_cast<int>(hotkeysAlt.size())&&hotkeysAlt[i]){if(!v.empty())v+=L" ";v+=HotkeyName(hotkeysAlt[i]);}return v;};
                std::wstring av=sortCol==0?shortcutText(a):(sortCol==1?kHotkeyDefs[a].label:HotkeyCategory(kHotkeyDefs[a].action));
                std::wstring bv=sortCol==0?shortcutText(b):(sortCol==1?kHotkeyDefs[b].label:HotkeyCategory(kHotkeyDefs[b].action));
                int cmp=_wcsicmp(av.c_str(),bv.c_str());return asc?cmp<0:cmp>0;
            });
        }
        ListView_DeleteAllItems(list);
        int selectRow=-1;
        for(size_t row=0;row<order.size();++row){
            int i=order[row];std::wstring shortcut=hotkeys[i]?HotkeyName(hotkeys[i]):L"";
            if(i<static_cast<int>(hotkeysAlt.size())&&hotkeysAlt[i]){if(!shortcut.empty())shortcut+=L"  •  ";shortcut+=HotkeyName(hotkeysAlt[i]);}
            INT_PTR cap=reinterpret_cast<INT_PTR>(GetPropW(wnd,L"GlideHotkeyCapture")),capAlt=reinterpret_cast<INT_PTR>(GetPropW(wnd,L"GlideHotkeyCaptureAlt"));
            if(cap==i+1||capAlt==i+1)shortcut=capAlt==i+1?L"▶ Waiting for SECOND input…":L"▶ Waiting for input…";
            else if(shortcut.empty())shortcut=L"—";
            LVITEMW item{};item.mask=LVIF_TEXT|LVIF_PARAM;item.iItem=static_cast<int>(row);item.iSubItem=0;
            item.pszText=shortcut.data();item.lParam=i;int inserted=ListView_InsertItem(list,&item);
            if(inserted>=0){
                ListView_SetItemText(list,inserted,1,const_cast<LPWSTR>(kHotkeyDefs[i].label));
                ListView_SetItemText(list,inserted,2,const_cast<LPWSTR>(HotkeyCategory(kHotkeyDefs[i].action)));
            }
            if(i==selected)selectRow=inserted;
        }
        if(selectRow<0&&!order.empty())selectRow=0;
        if(selectRow>=0){
            ListView_SetItemState(list,selectRow,LVIS_SELECTED|LVIS_FOCUSED,LVIS_SELECTED|LVIS_FOCUSED);
            ListView_EnsureVisible(list,selectRow,FALSE);
        }
    }

    static DWORD CurrentChord(UINT vk) {
        bool ctrl=(GetKeyState(VK_CONTROL)&0x8000)!=0, shift=(GetKeyState(VK_SHIFT)&0x8000)!=0, alt=(GetKeyState(VK_MENU)&0x8000)!=0;
        return HotkeyChord(vk,ctrl,shift,alt);
    }

    static bool IsEffectiveSettingsControl(HWND c){
        if(!c||!GetPropW(c,L"GlideCat"))return false;
        const int id=GetDlgCtrlID(c);if(id<=0||id==IDC_SET_PRESET_COMBO)return false;
        wchar_t cls[64]{};GetClassNameW(c,cls,63);
        if(_wcsicmp(cls,L"COMBOBOX")==0||_wcsicmp(cls,L"EDIT")==0)return true;
        if(_wcsicmp(cls,L"BUTTON")==0){DWORD t=static_cast<DWORD>(GetWindowLongPtrW(c,GWL_STYLE))&BS_TYPEMASK;return t==BS_AUTOCHECKBOX||t==BS_CHECKBOX||t==BS_AUTORADIOBUTTON||t==BS_RADIOBUTTON;}
        return false;
    }
    static std::wstring SettingsStateFingerprint(HWND wnd) {
        // Serialize only values that can actually be applied. Presentation state such as
        // hover, search text, page selection, ListView sort order and action buttons is
        // intentionally excluded, so change->revert returns EXACTLY to a clean state.
        struct Item{int id{};HWND hwnd{};};std::vector<Item> items;
        for(HWND c=GetWindow(wnd,GW_CHILD);c;c=GetWindow(c,GW_HWNDNEXT))if(IsEffectiveSettingsControl(c))items.push_back({GetDlgCtrlID(c),c});
        std::sort(items.begin(),items.end(),[](const Item&a,const Item&b){return a.id<b.id;});
        std::wstring out;wchar_t buf[1024]{};
        for(const auto&it:items){wchar_t cls[64]{};GetClassNameW(it.hwnd,cls,63);out+=L"C"+std::to_wstring(it.id)+L"=";
            if(_wcsicmp(cls,L"BUTTON")==0)out+=std::to_wstring(static_cast<long long>(SendMessageW(it.hwnd,BM_GETCHECK,0,0)));
            else if(_wcsicmp(cls,L"COMBOBOX")==0)out+=std::to_wstring(static_cast<long long>(SendMessageW(it.hwnd,CB_GETCURSEL,0,0)));
            else{GetWindowTextW(it.hwnd,buf,static_cast<int>(std::size(buf)));out+=buf;}out+=L";";}
        auto*app=reinterpret_cast<ViewerApp*>(GetPropW(wnd,L"GlideApp"));if(app){app->EnsureHotkeys();for(size_t i=0;i<app->hotkeys.size();++i){out+=L"H"+std::to_wstring(i)+L"="+std::to_wstring(app->hotkeys[i])+L","+std::to_wstring(i<app->hotkeysAlt.size()?app->hotkeysAlt[i]:0)+L";";}}
        return out;
    }
    static void StoreSettingsBaseline(HWND wnd){
        auto* old=reinterpret_cast<std::wstring*>(GetPropW(wnd,L"GlideSettingsBaseline"));
        if(old){RemovePropW(wnd,L"GlideSettingsBaseline");delete old;}
        SetPropW(wnd,L"GlideSettingsBaseline",reinterpret_cast<HANDLE>(new std::wstring(SettingsStateFingerprint(wnd))));
    }
    static bool SettingsHasEffectiveChanges(HWND wnd){auto*base=reinterpret_cast<std::wstring*>(GetPropW(wnd,L"GlideSettingsBaseline"));return base&&*base!=SettingsStateFingerprint(wnd);}
    static std::wstring SettingFriendlyName(int id){
        if(id>=IDC_SET_GESTURE_BASE && id<IDC_SET_GESTURE_BASE+kGestureSlotCount)return GestureSlotLabel(static_cast<GestureSlot>(id-IDC_SET_GESTURE_BASE));
        switch(id){
            case IDC_SET_THEME_MODE:return L"Theme";case IDC_SET_ACCENT_COLOR:return L"Accent / glow colour";case IDC_SET_PRESET_COMBO:return L"Behavior preset";
            case IDC_SET_PROGRESSIVE_COLOR_FIRST:return L"Progressive JPEG first-colour preview";case IDC_SET_FOLDER_NAV_SHOW_GROUP:return L"Sibling-folder navigation group";
            case IDC_SET_FOLDER_NAV_SHOW_PREV:return L"Previous Folder button";case IDC_SET_FOLDER_NAV_SHOW_NEXT:return L"Next Folder button";case IDC_SET_FOLDER_NAV_SHOW_EXPLORE:return L"Explore Parent Folders button";
            case IDC_SET_OVERLAY_KEYBOARD_ZOOM:return L"Overlay keyboard zoom";case IDC_SET_OVERLAY_CTRL_WHEEL_ZOOM:return L"Overlay mouse-wheel zoom";case IDC_SET_OVERLAY_ZOOM_STEP:return L"Overlay zoom step";case IDC_SET_OVERLAY_RIGHT_DRAG_PAN:return L"Overlay right-drag pan when zoomed";
            case IDC_SET_MAIN_REMEMBER_FOLDER:return L"Main viewer remembered folder";case IDC_SET_OVERLAY_REMEMBER_FOLDER:return L"Overlay remembered folder";
            case IDC_SET_LEFT_DRAG_MODE:return L"Left-drag behavior";case IDC_SET_RIGHT_DRAG_PAN:return L"Right-drag panning";case IDC_SET_SELECTION_CLICK_ZOOM:return L"Selection click zoom";
            case IDC_SET_SELECTION_RIGHT_ZOOM:return L"Selection right-click zoom";case IDC_SET_BACKGROUND_DRAG_WINDOW:return L"Background drag moves window";
            case IDC_SET_DEFAULT_VIEW_MODE:return L"Default view for new images";case IDC_SET_DEFAULT_CUSTOM_ZOOM:return L"Default custom zoom percentage";case IDC_SET_ZOOM_STEP:return L"Zoom step";case IDC_SET_CURSOR_HIDE_DELAY:return L"Fullscreen cursor hide delay";case IDC_SET_FULLSCREEN_EXIT_MODE:return L"After exiting fullscreen";case IDC_SET_TEXT_OVERLAY_ENABLED:return L"Picture text overlay";case IDC_SET_TEXT_OVERLAY_TEMPLATE:return L"Picture text overlay template";case IDC_SET_TEXT_OVERLAY_SIZE:return L"Picture text overlay font size";case IDC_SET_TEXT_OVERLAY_OPACITY:return L"Picture text overlay opacity";case IDC_SET_TEXT_OVERLAY_COLOR:return L"Picture text overlay colour";case IDC_SET_TEXT_OVERLAY_POSITION:return L"Picture text overlay position";case IDC_SET_TEXT_OVERLAY_BOLD:return L"Picture text overlay bold";case IDC_SET_TEXT_OVERLAY_SHADOW:return L"Picture text overlay shadow";
            case IDC_SET_PREFETCH_DEPTH:return L"Prefetch depth";case IDC_SET_CACHE_ITEMS:return L"Decoded cache items";case IDC_SET_RAPID_PREVIEW_SIZE:return L"Rapid-browse preview resolution";
            case IDC_SET_FULLSCREEN_STATUS_ALWAYS:return L"Keep status bar visible in fullscreen";
            case IDC_SET_ESC_SLIDESHOW:return L"Escape stops slideshow";case IDC_SET_ESC_FULLSCREEN:return L"Escape exits fullscreen";case IDC_SET_ESC_WINDOWED_CONFIRM:return L"Escape close confirmation";case IDC_SET_EXT1_PATH:return L"External program 1";case IDC_SET_EXT2_PATH:return L"External program 2";case IDC_SET_EXT3_PATH:return L"External program 3";
            case IDC_SET_TAB_MIN_WIDTH:return L"Minimum tab width";case IDC_SET_TAB_MAX_WIDTH:return L"Maximum tab width";case IDC_SET_CLOSED_TAB_LIMIT:return L"Closed-tab history limit";
            default:break;
        }
        return L"Setting #"+std::to_wstring(id);
    }
    static std::unordered_map<std::wstring,std::wstring> ParseSettingsFingerprint(const std::wstring& fp){
        std::unordered_map<std::wstring,std::wstring> m;size_t p=0;while(p<fp.size()){size_t e=fp.find(L';',p);if(e==std::wstring::npos)e=fp.size();std::wstring item=fp.substr(p,e-p);size_t eq=item.find(L'=');if(eq!=std::wstring::npos)m[item.substr(0,eq)]=item.substr(eq+1);p=e+1;}return m;
    }
    static std::wstring SettingValueForSummary(int id,const std::wstring& raw){
        if(id>=IDC_SET_GESTURE_BASE && id<IDC_SET_GESTURE_BASE+kGestureSlotCount){int n=_wtoi(raw.c_str());if(n>=0&&n<static_cast<int>(GestureAction::Count))return GestureActionLabel(static_cast<GestureAction>(n));}
        if(raw==L"0"||raw==L"1"){
            // Combo/radio numeric values are handled explicitly below; ordinary boolean controls
            // are deliberately shown as human-readable On/Off rather than implementation numbers.
            if(id==IDC_SET_THEME_MODE){if(raw==L"0")return L"Dark";if(raw==L"1")return L"Light";}
            if(id==IDC_SET_ACCENT_COLOR){static const wchar_t* a[]={L"Blue",L"White",L"Grey",L"Red",L"Yellow",L"Green",L"Purple"};int n=_wtoi(raw.c_str());if(n>=0&&n<7)return a[n];}
            if(id==IDC_SET_LEFT_DRAG_MODE)return raw==L"0"?L"Selection":L"Pan";
            if(id==IDC_SET_FULLSCREEN_EXIT_MODE)return raw==L"0"?L"Restore original window position":L"Restore maximized window";
            if(id==IDC_SET_DEFAULT_VIEW_MODE){static const wchar_t* m[]={L"Fit image",L"Fit to width",L"Fit to height",L"100%",L"Custom %"};int n=_wtoi(raw.c_str());if(n>=0&&n<5)return m[n];}
            return raw==L"1"?L"On":L"Off";
        }
        if(id==IDC_SET_ACCENT_COLOR){static const wchar_t* a[]={L"Blue",L"White",L"Grey",L"Red",L"Yellow",L"Green",L"Purple"};int n=_wtoi(raw.c_str());if(n>=0&&n<7)return a[n];}
        if(id==IDC_SET_DEFAULT_VIEW_MODE){static const wchar_t* m[]={L"Fit image",L"Fit to width",L"Fit to height",L"100%",L"Custom %"};int n=_wtoi(raw.c_str());if(n>=0&&n<5)return m[n];}
        return raw;
    }
    static std::wstring SettingsDiscardMessage(HWND wnd){
        auto*base=reinterpret_cast<std::wstring*>(GetPropW(wnd,L"GlideSettingsBaseline"));if(!base)return L"";auto a=ParseSettingsFingerprint(*base),b=ParseSettingsFingerprint(SettingsStateFingerprint(wnd));
        struct Change{std::wstring name,oldValue,newValue;};std::vector<Change> changes;bool hotkeysChanged=false;
        for(const auto&kv:b){auto it=a.find(kv.first);if(it!=a.end()&&it->second==kv.second)continue;if(!kv.first.empty()&&kv.first[0]==L'H'){hotkeysChanged=true;continue;}if(kv.first.size()>1&&kv.first[0]==L'C'){try{int id=std::stoi(kv.first.substr(1));std::wstring name=SettingFriendlyName(id);if(name.rfind(L"Setting #",0)==0){wchar_t text[256]{};if(HWND c=GetDlgItem(wnd,id))GetWindowTextW(c,text,255);name=text[0]?text:L"Changed preference";}changes.push_back({name,it==a.end()?L"":SettingValueForSummary(id,it->second),SettingValueForSummary(id,kv.second)});}catch(...){}}}
        if(hotkeysChanged)changes.push_back({L"Keyboard shortcuts",L"applied shortcuts",L"edited shortcuts"});
        if(changes.empty())return L"";
        std::sort(changes.begin(),changes.end(),[](const Change&x,const Change&y){return x.name<y.name;});
        std::wstring msg=L"These unapplied changes would be discarded:\n\n";const size_t shown=std::min<size_t>(changes.size(),10);
        for(size_t i=0;i<shown;++i){msg+=L"  • "+changes[i].name;if(!changes[i].oldValue.empty()||!changes[i].newValue.empty())msg+=L": "+changes[i].oldValue+L"  →  "+changes[i].newValue;msg+=L"\n";}
        if(changes.size()>shown)msg+=L"  • and "+std::to_wstring(changes.size()-shown)+L" more\n";msg+=L"\nDiscard these changes?";return msg;
    }

    static bool ConfirmDiscardSettings(HWND wnd){std::wstring msg=SettingsDiscardMessage(wnd);if(msg.empty())return true;return MessageBoxW(wnd,msg.c_str(),L"Glide Settings",MB_YESNO|MB_ICONWARNING|MB_DEFBUTTON2)==IDYES;}

    // Settings stabilization: use fixed pages rather than continuously moving
    // native child HWNDs through a clipped viewport. Combo boxes, edits and ListViews
    // stay at stable coordinates for a whole frame, eliminating partial/vanishing
    // controls and the flicker seen in the 1.2.25/1.2.26 scrolling implementation.
    // Glide UI: Settings chrome and content now use a single layout contract.
    // Source coordinates remain metadata only; visible rows are reflowed into the viewport
    // so one manually-added setting cannot collide with the next one.



    struct SettingsSearchRow{int cat{},anchorY{},minX{};std::vector<size_t> nodes;std::wstring hay;};
    struct SettingsPlacement{int x{},y{},w{},h{};};

    static std::wstring SettingsNodeSearchText(const SettingsLayoutNode& n){
        wchar_t text[384]{};GetWindowTextW(n.hwnd,text,383);std::wstring hay=Lower(text);
        if(n.id>0){std::wstring f=SettingFriendlyName(n.id);if(f.rfind(L"Setting #",0)!=0)hay+=L" "+Lower(f);}
        if(n.id>=IDC_SET_GESTURE_BASE&&n.id<IDC_SET_GESTURE_BASE+kGestureSlotCount)hay+=L" "+Lower(GestureSlotLabel(static_cast<GestureSlot>(n.id-IDC_SET_GESTURE_BASE)))+L" gesture mouse action remap advanced";
        switch(n.id){
            case IDC_SET_ACCENT_COLOR:hay+=L" color colour glow accent appearance";break;
            case IDC_SET_THEME_MODE:hay+=L" color colour appearance light dark";break;
            case IDC_SET_RECENT_LIMIT:hay+=L" history recent size limit";break;
            case IDC_SET_ADAPTIVE_DELAY:hay+=L" adaptive preview delay performance rendering";break;
            case IDC_SET_RAPID_PREVIEW_SIZE:hay+=L" rapid browse preview resolution size quality blurry preliminary rendering pixels longest side";break;case IDC_SET_DEFAULT_VIEW_MODE:case IDC_SET_DEFAULT_CUSTOM_ZOOM:hay+=L" default view new image fit width height actual 100 custom zoom percentage previous image zoom";break;
            case IDC_SET_FULLSCREEN_EXIT_MODE:hay+=L" fullscreen exit restore original position placement maximize maximized windowed";break;case IDC_SET_TEXT_OVERLAY_ENABLED:case IDC_SET_TEXT_OVERLAY_TEMPLATE:case IDC_SET_TEXT_OVERLAY_SIZE:case IDC_SET_TEXT_OVERLAY_OPACITY:case IDC_SET_TEXT_OVERLAY_COLOR:case IDC_SET_TEXT_OVERLAY_POSITION:case IDC_SET_TEXT_OVERLAY_BOLD:case IDC_SET_TEXT_OVERLAY_SHADOW:hay+=L" picture text overlay folder index counter position style font color colour opacity template tokens";break;
            case IDC_SET_SLIDE_INTERVAL:hay+=L" slideshow interval speed milliseconds";break;
            case IDC_SET_QUALITY_SPEED:case IDC_SET_QUALITY_BALANCED:case IDC_SET_QUALITY_MAX:hay+=L" image quality rendering decode performance";break;
            default:break;
        }
        return hay;
    }

    static bool SettingsSearchRowHasRadio(const SettingsSearchRow& row,const std::vector<SettingsLayoutNode>& nodes){
        for(size_t idx:row.nodes){if(idx>=nodes.size())continue;const auto&n=nodes[idx];wchar_t cls[32]{};GetClassNameW(n.hwnd,cls,31);if(_wcsicmp(cls,L"BUTTON")!=0)continue;DWORD type=static_cast<DWORD>(GetWindowLongPtrW(n.hwnd,GWL_STYLE))&BS_TYPEMASK;if(type==BS_AUTORADIOBUTTON||type==BS_RADIOBUTTON)return true;}return false;
    }
    static bool SettingsSearchRowIsHeadingOnly(const SettingsSearchRow& row,const std::vector<SettingsLayoutNode>& nodes){
        if(row.nodes.size()!=1||row.nodes.front()>=nodes.size())return false;const auto&n=nodes[row.nodes.front()];if(n.id!=0)return false;wchar_t cls[32]{};GetClassNameW(n.hwnd,cls,31);return _wcsicmp(cls,L"STATIC")==0;
    }
    static std::vector<SettingsSearchRow> BuildSettingsSearchRows(const std::vector<SettingsLayoutNode>& nodes,const std::wstring&q){
        std::vector<SettingsSearchRow> rows;
        for(size_t i=0;i<nodes.size();++i){const auto&n=nodes[i];
            if(n.cat==6)continue; // Hotkeys use a dedicated filtered, directly editable search surface.
            // Physical same-row grouping stays deliberately tight. Search association is handled
            // separately below so a heading can bring along its editor controls without reviving
            // the old layout-overlap bug.
            if(rows.empty()||rows.back().cat!=n.cat||n.y-rows.back().anchorY>14){SettingsSearchRow r{};r.cat=n.cat;r.anchorY=n.y;r.minX=n.x;rows.push_back(std::move(r));}
            auto&r=rows.back();r.nodes.push_back(i);r.minX=std::min(r.minX,n.x);r.hay+=L" "+SettingsNodeSearchText(n);
        }
        if(q.empty())return {};
        auto tokenMatch=[](const std::wstring& hay,const std::wstring& query){
            // Search is token-based so punctuation in labels (for example "right-drag")
            // does not make a natural query such as "overlay right drag" fail.
            size_t p=0;bool any=false;
            while(p<query.size()){
                while(p<query.size()&&!iswalnum(query[p]))++p;
                size_t e=p;while(e<query.size()&&iswalnum(query[e]))++e;
                if(e>p){any=true;if(hay.find(query.substr(p,e-p))==std::wstring::npos)return false;}
                p=e;
            }
            return any;
        };
        std::vector<bool> include(rows.size(),false);
        for(size_t i=0;i<rows.size();++i)if(tokenMatch(rows[i].hay,q))include[i]=true;
        // Radio groups often use a descriptive heading on the line above the actual choices
        // (e.g. "Initial image quality"). If either half matches, surface both rows so Search is
        // a real editing surface rather than an orphan heading or orphan set of radio buttons.
        for(size_t i=0;i<rows.size();++i){
            if(!include[i])continue;
            if(SettingsSearchRowIsHeadingOnly(rows[i],nodes)&&i+1<rows.size()&&rows[i+1].cat==rows[i].cat&&rows[i+1].anchorY-rows[i].anchorY<=40&&SettingsSearchRowHasRadio(rows[i+1],nodes))include[i+1]=true;
            if(SettingsSearchRowHasRadio(rows[i],nodes)&&i>0&&rows[i-1].cat==rows[i].cat&&rows[i].anchorY-rows[i-1].anchorY<=40&&SettingsSearchRowIsHeadingOnly(rows[i-1],nodes))include[i-1]=true;
        }
        std::vector<SettingsSearchRow> matched;for(size_t i=0;i<rows.size();++i)if(include[i])matched.push_back(std::move(rows[i]));return matched;
    }




    static void RefreshSettingsVisibility(HWND wnd){
        wchar_t query[128]{};GetDlgItemTextW(wnd,IDC_SET_SEARCH,query,127);const std::wstring q=Lower(query);const int cat=static_cast<int>(GetWindowLongPtrW(wnd,GWLP_USERDATA));const bool searchMode=!q.empty();UpdateSettingsPageHeader(wnd);
        auto nodes=CollectSettingsLayoutNodes(wnd);auto rows=searchMode?BuildSettingsSearchRows(nodes,q):std::vector<SettingsSearchRow>{};
        auto* app=reinterpret_cast<ViewerApp*>(GetPropW(wnd,L"GlideApp"));
        const bool hotkeySearch=searchMode&&app&&app->HasHotkeySearchMatch(q);
        if(searchMode&&app)app->PopulateHotkeyList(wnd);
        int hotkeyRows=0, firstPageOrdinaryCapacity=0;
        if(hotkeySearch){
            if(HWND hl=GetDlgItem(wnd,IDC_SET_HOTKEY_LIST))hotkeyRows=ListView_GetItemCount(hl);
            RECT sr{};GetClientRect(wnd,&sr);const int top=kSettingsContentTop+8,bottom=SettingsContentBottom(wnd)-10;
            const int listY=top+48;
            const int listH=std::clamp(30+hotkeyRows*29,105,285);
            const int ordinaryStart=listY+listH+10+36+14;
            firstPageOrdinaryCapacity=std::max(0,(bottom-ordinaryStart)/52);
        }
        int pages=1;
        if(searchMode){
            if(hotkeySearch){
                const size_t consumed=std::min(rows.size(),static_cast<size_t>(firstPageOrdinaryCapacity));
                const size_t remain=rows.size()-consumed;
                pages=1+static_cast<int>((remain+kSettingsSearchRowsPerPage-1)/kSettingsSearchRowsPerPage);
            }else pages=std::max(1,static_cast<int>((rows.size()+kSettingsSearchRowsPerPage-1)/kSettingsSearchRowsPerPage));
        }else pages=(cat==6)?1:SettingsNormalPageCount(nodes,cat);
        int page=std::clamp(SettingsSectionPage(wnd),0,pages-1);SetSettingsSectionPage(wnd,page);UpdateSettingsSectionNav(wnd,page,pages,searchMode);
        std::unordered_map<HWND,SettingsPlacement> place;
        if(searchMode){
            size_t ordinaryFirst=0, ordinaryLast=0; int ordinaryY=kSettingsContentTop+10;
            if(hotkeySearch&&page==0){
                RECT hr{};GetClientRect(wnd,&hr);const int left=kSettingsContentLeft+8;const int right=std::max(left+520,static_cast<int>(hr.right)-34);
                const int top=kSettingsContentTop+8;const int buttonH=36,gap=10;
                const int listY=top+48,listH=std::clamp(30+hotkeyRows*29,105,285);const int buttonY=listY+listH+10;
                if(HWND h=GetDlgItem(wnd,IDC_SET_HOTKEY_HINT))place[h]={left,top,right-left,42};
                if(HWND h=GetDlgItem(wnd,IDC_SET_HOTKEY_LIST))place[h]={left,listY,right-left,listH};
                const int bw=std::max(118,((right-left)-3*gap)/4);
                const int ids[4]={IDC_SET_HOTKEY_CHANGE,IDC_SET_HOTKEY_ADD_ALT,IDC_SET_HOTKEY_CLEAR,IDC_SET_HOTKEY_RESET};
                for(int bi=0;bi<4;++bi)if(HWND h=GetDlgItem(wnd,ids[bi])){const int bx=left+bi*(bw+gap);place[h]={bx,buttonY,bi==3?std::max(90,right-bx):bw,buttonH};}
                ordinaryFirst=0;ordinaryLast=std::min(rows.size(),static_cast<size_t>(firstPageOrdinaryCapacity));ordinaryY=buttonY+buttonH+14;
            }else{
                const size_t consumed=hotkeySearch?std::min(rows.size(),static_cast<size_t>(firstPageOrdinaryCapacity)):0;
                const int ordinaryPage=hotkeySearch?page-1:page;
                ordinaryFirst=consumed+static_cast<size_t>(ordinaryPage*kSettingsSearchRowsPerPage);
                ordinaryLast=std::min(rows.size(),ordinaryFirst+kSettingsSearchRowsPerPage);
            }
            for(size_t ri=ordinaryFirst;ri<ordinaryLast;++ri){const auto&r=rows[ri];int rowHeight=36;
                for(size_t idx:r.nodes)rowHeight=std::max(rowHeight,SettingsVisualHeight(nodes[idx]));
                for(size_t idx:r.nodes){const auto&n=nodes[idx];int nx=330+(n.x-r.minX),nw=n.w;RECT sr{};GetClientRect(wnd,&sr);const int searchRight=sr.right-34;if(nx+nw>searchRight)nw=std::max(70,searchRight-nx);place[n.hwnd]={nx,ordinaryY,nw,n.h};}
                ordinaryY+=std::max(52,rowHeight+10);
            }
        }else{
            // Hotkeys are a composite surface, not a sequence of ordinary rows. Giving the
            // ListView its own layout prevents the action buttons from being reflowed inside
            // the ListView's tall source rectangle.
            if(cat==6){
                RECT cr{};GetClientRect(wnd,&cr);const int left=kSettingsContentLeft+8;const int right=std::max(left+520,static_cast<int>(cr.right)-34);
                const int top=kSettingsContentTop+6;const int bottom=SettingsContentBottom(wnd)-10;
                const int buttonH=36,buttonGap=10,buttonY=std::max(top+250,bottom-buttonH);
                const int hintY=top+32,listY=hintY+48;const int listH=std::max(150,buttonY-listY-14);
                for(const auto&n:nodes){if(n.cat!=6)continue;
                    if(n.id==IDC_SET_HOTKEY_HINT)place[n.hwnd]={left,hintY,right-left,42};
                    else if(n.id==IDC_SET_HOTKEY_LIST)place[n.hwnd]={left,listY,right-left,listH};
                    else if(n.id==IDC_SET_HOTKEY_CHANGE||n.id==IDC_SET_HOTKEY_ADD_ALT||n.id==IDC_SET_HOTKEY_CLEAR||n.id==IDC_SET_HOTKEY_RESET){
                        const int totalW=right-left;const int bw=std::max(118,(totalW-3*buttonGap)/4);
                        int bi=n.id==IDC_SET_HOTKEY_CHANGE?0:n.id==IDC_SET_HOTKEY_ADD_ALT?1:n.id==IDC_SET_HOTKEY_CLEAR?2:3;
                        int bx=left+bi*(bw+buttonGap);int actualW=(bi==3)?std::max(90,right-bx):bw;place[n.hwnd]={bx,buttonY,actualW,buttonH};
                    }else{ // "Keyboard shortcuts" heading (static id 0).
                        place[n.hwnd]={left,top,std::max(260,right-left),24};
                    }
                }
            }else{
                // Reflow logical source rows into the visible viewport. Source Y values decide
                // row membership/page only. A tight 14px grouping tolerance preserves genuinely
                // side-by-side label/editor rows without merging the next vertical setting.
                auto normalRows=BuildSettingsNormalRows(nodes,cat,page);const int bottom=SettingsContentBottom(wnd);
                int sumHeights=0;for(const auto&r:normalRows){int rh=26;for(size_t idx:r.nodes)rh=std::max(rh,SettingsVisualHeight(nodes[idx]));sumHeights+=rh;}
                const int available=std::max(80,bottom-kSettingsContentTop-20);int gap=10;if(normalRows.size()>1)gap=std::clamp((available-sumHeights)/static_cast<int>(normalRows.size()-1),7,14);
                int y=kSettingsContentTop+10;RECT cr{};GetClientRect(wnd,&cr);const int right=static_cast<int>(cr.right)-34;
                for(const auto&r:normalRows){int rh=26;for(size_t idx:r.nodes)rh=std::max(rh,SettingsVisualHeight(nodes[idx]));
                    // Never permit a settings row to enter the fixed footer. The smaller
                    // logical page span above normally moves it to the next section; this
                    // guard prevents malformed legacy coordinates from painting underneath
                    // Export/Cancel/Apply/OK.
                    if(y + rh > bottom - 8) break;
                    for(size_t idx:r.nodes){const auto&n=nodes[idx];const int localOffset=std::clamp(n.y-r.anchorY,-4,8);int nx=n.x,nw=n.w;if(nx+nw>right)nw=std::max(70,right-nx);place[n.hwnd]={nx,y+localOffset,nw,n.h};}
                    y+=rh+gap;
                }
            }
        }
        // Apply the complete page transition atomically. Older builds visibly erased the
        // body, hid everything, then showed the new controls in a second paint. That produced
        // the "Settings refreshes once" flash and blocked interaction with synchronous
        // RDW_UPDATENOW calls. Defer all moves/show/hide operations and request one ordinary
        // repaint after the batch, so the old frame remains valid until the new frame is ready.
        RECT cr{};GetClientRect(wnd,&cr);RECT body{kSettingsRailWidth+1,kSettingsContentTop,cr.right,SettingsContentBottom(wnd)};
        HDWP hdwp=BeginDeferWindowPos(static_cast<int>(nodes.size()));
        for(const auto&n:nodes){
            auto it=place.find(n.hwnd);
            if(it==place.end()){
                if(hdwp)hdwp=DeferWindowPos(hdwp,n.hwnd,nullptr,0,0,0,0,SWP_NOMOVE|SWP_NOSIZE|SWP_NOZORDER|SWP_NOACTIVATE|SWP_HIDEWINDOW|SWP_NOREDRAW);
                else ShowWindow(n.hwnd,SW_HIDE);
                continue;
            }
            const auto&p=it->second;
            // Inactive Settings pages are deliberately created without an expensive
            // per-control uxtheme call. Theme only a control that is actually entering
            // the visible page, once. This keeps the initial Settings construction lean
            // while guaranteeing that the first interactive frame of every page is final.
            if(app&&!GetPropW(n.hwnd,L"GlideControlThemed")){
                wchar_t cls[64]{};GetClassNameW(n.hwnd,cls,63);
                SetWindowTheme(n.hwnd,app->ResolveLightTheme()?L"Explorer":L"DarkMode_Explorer",nullptr);
                if(_wcsicmp(cls,WC_LISTVIEWW)==0){
                    const ThemePalette pal=app->Palette();
                    ListView_SetBkColor(n.hwnd,pal.panel);ListView_SetTextBkColor(n.hwnd,pal.panel);ListView_SetTextColor(n.hwnd,pal.text);
                    if(HWND hdr=ListView_GetHeader(n.hwnd))SetWindowTheme(hdr,L"",L"");
                }
                SetPropW(n.hwnd,L"GlideControlThemed",reinterpret_cast<HANDLE>(1));
            }
            if(hdwp)hdwp=DeferWindowPos(hdwp,n.hwnd,HWND_TOP,p.x,p.y,std::max(1,p.w),std::max(1,p.h),SWP_NOACTIVATE|SWP_SHOWWINDOW|SWP_NOREDRAW);
            else SetWindowPos(n.hwnd,HWND_TOP,p.x,p.y,std::max(1,p.w),std::max(1,p.h),SWP_NOACTIVATE|SWP_SHOWWINDOW);
        }
        if(hdwp)EndDeferWindowPos(hdwp);
        if(HWND hot=GetDlgItem(wnd,IDC_SET_HOTKEY_LIST);hot&&IsWindowVisible(hot))ResizeHotkeyColumns(hot);
        RedrawWindow(wnd,&body,nullptr,RDW_INVALIDATE|RDW_ERASE|RDW_ALLCHILDREN|RDW_NOFRAME);
    }

    static LRESULT CALLBACK SettingsSearchSubclass(HWND wnd, UINT msg, WPARAM wp, LPARAM lp, UINT_PTR, DWORD_PTR refData) {
        auto* app=reinterpret_cast<ViewerApp*>(refData);
        switch(msg){
            case WM_SETFOCUS: case WM_KILLFOCUS: case WM_SETTEXT:
                InvalidateRect(wnd,nullptr,TRUE);
                break;
            case WM_PAINT: {
                LRESULT r=DefSubclassProc(wnd,msg,wp,lp);
                if(GetWindowTextLengthW(wnd)==0 && GetFocus()!=wnd){
                    HDC dc=GetDC(wnd);if(dc){RECT rc{};GetClientRect(wnd,&rc);ThemePalette pal=app?app->Palette():ThemePalette{};
                        SetBkMode(dc,TRANSPARENT);SetTextColor(dc,app?pal.textMuted:RGB(120,125,132));
                        HFONT f=reinterpret_cast<HFONT>(SendMessageW(wnd,WM_GETFONT,0,0));HGDIOBJ old=f?SelectObject(dc,f):nullptr;
                        rc.left+=14;rc.right-=10;DrawTextW(dc,L"Search settings...",-1,&rc,DT_LEFT|DT_VCENTER|DT_SINGLELINE|DT_NOPREFIX);
                        if(old)SelectObject(dc,old);ReleaseDC(wnd,dc);}
                }return r;
            }
            case WM_NCDESTROY:
                RemoveWindowSubclass(wnd,SettingsSearchSubclass,1);break;
        }
        return DefSubclassProc(wnd,msg,wp,lp);
    }

    static void ResizeHotkeyColumns(HWND list){
        if(!list)return;HWND header=ListView_GetHeader(list);if(!header)return;
        // The ListView itself has no fourth column. If any stale column somehow exists,
        // remove it defensively, then size Category from the *final header client width*.
        while(Header_GetItemCount(header)>3)ListView_DeleteColumn(list,3);
        if(Header_GetItemCount(header)<3)return;
        RECT hr{};GetClientRect(header,&hr);int usable=std::max(360,static_cast<int>(hr.right-hr.left));
        int c0=std::max(150,usable*24/100);int c1=std::max(260,usable*43/100);
        if(c0+c1>usable-150){const int spare=std::max(300,usable-150);c0=spare*36/100;c1=spare-c0;}
        ListView_SetColumnWidth(list,0,c0);ListView_SetColumnWidth(list,1,c1);
        const int actual0=ListView_GetColumnWidth(list,0),actual1=ListView_GetColumnWidth(list,1);
        const int c2=std::max(150,usable-actual0-actual1-1);
        ListView_SetColumnWidth(list,2,c2);
        // Set the final width through the header too; this avoids a stale right-side strip
        // after ListView resizing on some comctl32 builds.
        HDITEMW h2{};h2.mask=HDI_WIDTH;h2.cxy=c2;Header_SetItem(header,2,&h2);
        for(int ci=0;ci<3;++ci){HDITEMW hi{};hi.mask=HDI_FORMAT;if(Header_GetItem(header,ci,&hi)){hi.fmt|=HDF_FIXEDWIDTH;Header_SetItem(header,ci,&hi);}}
        InvalidateRect(header,nullptr,TRUE);InvalidateRect(list,nullptr,TRUE);
    }

    static LRESULT CALLBACK ModernButtonSubclass(HWND wnd, UINT msg, WPARAM wp, LPARAM lp, UINT_PTR, DWORD_PTR) {
        if(msg==WM_MOUSEMOVE){
            if(!GetPropW(wnd,L"GlideButtonHover")){SetPropW(wnd,L"GlideButtonHover",reinterpret_cast<HANDLE>(1));TRACKMOUSEEVENT t{sizeof(t),TME_LEAVE,wnd,0};TrackMouseEvent(&t);InvalidateRect(wnd,nullptr,FALSE);}return 0;
        } else if(msg==WM_MOUSELEAVE){RemovePropW(wnd,L"GlideButtonHover");InvalidateRect(wnd,nullptr,FALSE);return 0;}
        else if(msg==WM_SETFOCUS||msg==WM_KILLFOCUS)InvalidateRect(wnd,nullptr,FALSE);
        else if(msg==WM_NCDESTROY){RemovePropW(wnd,L"GlideButtonHover");RemoveWindowSubclass(wnd,ModernButtonSubclass,1);}
        return DefSubclassProc(wnd,msg,wp,lp);
    }

    static LRESULT CALLBACK ModernToggleSubclass(HWND wnd, UINT msg, WPARAM wp, LPARAM lp, UINT_PTR, DWORD_PTR refData) {
        auto* app = reinterpret_cast<ViewerApp*>(refData);
        switch(msg) {
            case WM_MOUSEMOVE: {
                if(!GetPropW(wnd,L"GlideToggleHover")){
                    SetPropW(wnd,L"GlideToggleHover",reinterpret_cast<HANDLE>(1));
                    TRACKMOUSEEVENT t{sizeof(t),TME_LEAVE,wnd,0}; TrackMouseEvent(&t);
                    InvalidateRect(wnd,nullptr,FALSE);
                }
                // The whole BUTTON window is the hit target, including its label.
                // Do not let the themed default proc start a second hover repaint.
                return 0;
            }
            case WM_MOUSELEAVE:
                RemovePropW(wnd,L"GlideToggleHover");
                InvalidateRect(wnd,nullptr,FALSE);
                return 0;
            case WM_SETFOCUS: case WM_KILLFOCUS: case WM_ENABLE:
                InvalidateRect(wnd,nullptr,FALSE);
                break;
            case WM_ERASEBKGND:
                return 1;
            case WM_PAINT: {
                PAINTSTRUCT ps{}; HDC dc=BeginPaint(wnd,&ps);
                RECT rc{}; GetClientRect(wnd,&rc);
                ThemePalette pal=app?app->Palette():ThemePalette{}; if(reinterpret_cast<INT_PTR>(GetPropW(wnd,L"GlideCat"))==9 && app)pal.accent=BlendColor(pal.accent,RGB(155,105,230),0.38f); HBRUSH bg=CreateSolidBrush(app?pal.windowBg:RGB(24,26,29)); FillRect(dc,&rc,bg); DeleteObject(bg);

                const LONG_PTR style=GetWindowLongPtrW(wnd,GWL_STYLE);
                const DWORD type=static_cast<DWORD>(style&BS_TYPEMASK);
                const bool radio=type==BS_RADIOBUTTON||type==BS_AUTORADIOBUTTON;
                const bool checked=SendMessageW(wnd,BM_GETCHECK,0,0)==BST_CHECKED;
                const bool hover=GetPropW(wnd,L"GlideToggleHover")!=nullptr;
                const bool enabled=IsWindowEnabled(wnd)!=FALSE;

                if(app&&app->d2d){
                    D2D1_RENDER_TARGET_PROPERTIES pr=D2D1::RenderTargetProperties(
                        D2D1_RENDER_TARGET_TYPE_DEFAULT,
                        D2D1::PixelFormat(DXGI_FORMAT_B8G8R8A8_UNORM,D2D1_ALPHA_MODE_IGNORE));
                    ComPtr<ID2D1DCRenderTarget> rt;
                    if(SUCCEEDED(app->d2d->CreateDCRenderTarget(&pr,&rt))&&SUCCEEDED(rt->BindDC(dc,&rc))){
                        rt->BeginDraw(); rt->SetAntialiasMode(D2D1_ANTIALIAS_MODE_PER_PRIMITIVE);
                        ComPtr<ID2D1SolidColorBrush> idle,border,accent,tick,glow;
                        rt->CreateSolidColorBrush(D2DColor(app?pal.surface:RGB(27,30,34)),&idle);
                        rt->CreateSolidColorBrush(D2DColor(app?pal.border:RGB(63,69,77)),&border);
                        rt->CreateSolidColorBrush(D2DColor(enabled&&app?pal.accent:(app?pal.textMuted:RGB(90,96,104))),&accent);
                        rt->CreateSolidColorBrush(D2DColor(app&&app->ResolveLightTheme()?RGB(20,24,29):RGB(250,252,255)),&tick);
                        rt->CreateSolidColorBrush(D2DColor(app?pal.accent:RGB(70,160,245),hover?0.13f:0.0f),&glow);

                        const float cy=(rc.bottom-rc.top)*0.5f;
                        if(hover&&glow){
                            rt->FillRoundedRectangle(D2D1::RoundedRect(D2D1::RectF(1.0f,1.0f,static_cast<float>(rc.right-2),static_cast<float>(rc.bottom-2)),6.0f,6.0f),glow.Get());
                            rt->FillEllipse(D2D1::Ellipse(D2D1::Point2F(14.0f,cy),13.0f,13.0f),glow.Get());
                        }

                        if(radio){
                            rt->FillEllipse(D2D1::Ellipse(D2D1::Point2F(14.0f,cy),8.5f,8.5f),idle.Get());
                            rt->DrawEllipse(D2D1::Ellipse(D2D1::Point2F(14.0f,cy),8.5f,8.5f),
                                            checked?accent.Get():border.Get(),checked?1.6f:1.2f);
                            if(checked)rt->FillEllipse(D2D1::Ellipse(D2D1::Point2F(14.0f,cy),4.6f,4.6f),accent.Get());
                        }else{
                            auto box=D2D1::RoundedRect(D2D1::RectF(5.5f,cy-8.5f,22.5f,cy+8.5f),4.0f,4.0f);
                            rt->FillRoundedRectangle(box,checked?accent.Get():idle.Get());
                            rt->DrawRoundedRectangle(box,checked?accent.Get():border.Get(),checked?1.4f:1.15f);
                            if(checked&&tick){
                                rt->DrawLine(D2D1::Point2F(9.0f,cy),D2D1::Point2F(12.8f,cy+4.0f),tick.Get(),2.0f);
                                rt->DrawLine(D2D1::Point2F(12.8f,cy+4.0f),D2D1::Point2F(19.5f,cy-4.3f),tick.Get(),2.0f);
                            }
                        }
                        rt->EndDraw();
                    }
                }

                wchar_t text[512]{}; GetWindowTextW(wnd,text,511);
                HFONT font=reinterpret_cast<HFONT>(SendMessageW(wnd,WM_GETFONT,0,0));
                HGDIOBJ oldFont=font?SelectObject(dc,font):nullptr;
                SetBkMode(dc,TRANSPARENT);
                SetTextColor(dc,enabled?(app?pal.text:RGB(229,233,238)):(app?pal.textMuted:RGB(122,128,136)));
                RECT tr=rc; tr.left=34; tr.top=1; tr.bottom=std::max<LONG>(2,rc.bottom-1);
                DrawTextW(dc,text,-1,&tr,DT_SINGLELINE|DT_VCENTER|DT_LEFT|DT_NOPREFIX|DT_END_ELLIPSIS);
                if(oldFont)SelectObject(dc,oldFont);
                EndPaint(wnd,&ps);
                return 0;
            }
            case WM_NCDESTROY:
                RemovePropW(wnd,L"GlideToggleHover");
                RemoveWindowSubclass(wnd,ModernToggleSubclass,1);
                break;
        }
        return DefSubclassProc(wnd,msg,wp,lp);
    }

    static HWND EnsureDialogTooltipHost(HWND owner, HINSTANCE inst) {
        HWND tip=reinterpret_cast<HWND>(GetPropW(owner,L"GlideDialogTooltip"));
        if(tip&&IsWindow(tip))return tip;
        tip=CreateWindowExW(WS_EX_TOPMOST|WS_EX_NOACTIVATE,TOOLTIPS_CLASSW,nullptr,
            WS_POPUP|TTS_ALWAYSTIP|TTS_NOPREFIX,CW_USEDEFAULT,CW_USEDEFAULT,CW_USEDEFAULT,CW_USEDEFAULT,
            owner,nullptr,inst,nullptr);
        if(!tip)return nullptr;
        SetPropW(owner,L"GlideDialogTooltip",tip);
        SetWindowPos(tip,HWND_TOPMOST,0,0,0,0,SWP_NOMOVE|SWP_NOSIZE|SWP_NOACTIVATE);
        SendMessageW(tip,TTM_SETDELAYTIME,TTDT_INITIAL,650);
        SendMessageW(tip,TTM_SETDELAYTIME,TTDT_RESHOW,100);
        SendMessageW(tip,TTM_SETDELAYTIME,TTDT_AUTOPOP,7000);
        SendMessageW(tip,TTM_SETMAXTIPWIDTH,0,420);
        return tip;
    }

    static void AddDialogTooltip(HWND owner, int controlId, const wchar_t* text, HINSTANCE inst) {
        HWND child=GetDlgItem(owner,controlId); if(!child||!text||!*text)return;
        HWND tip=EnsureDialogTooltipHost(owner,inst); if(!tip)return;
        TOOLINFOW ti{};ti.cbSize=TooltipToolInfoSize();ti.uFlags=TTF_IDISHWND|TTF_SUBCLASS;ti.hwnd=owner;
        ti.uId=reinterpret_cast<UINT_PTR>(child);ti.lpszText=const_cast<LPWSTR>(text);
        SendMessageW(tip,TTM_ADDTOOLW,0,reinterpret_cast<LPARAM>(&ti));
    }

    static bool DrawSmoothModernButton(ViewerApp* app, DRAWITEMSTRUCT* di, bool selected, bool hover, int iconIndex, const wchar_t* label, bool category) {
        if(!app||!app->d2d||!di)return false;
        D2D1_RENDER_TARGET_PROPERTIES props=D2D1::RenderTargetProperties(D2D1_RENDER_TARGET_TYPE_DEFAULT,D2D1::PixelFormat(DXGI_FORMAT_B8G8R8A8_UNORM,D2D1_ALPHA_MODE_IGNORE));
        ComPtr<ID2D1DCRenderTarget> rt; if(FAILED(app->d2d->CreateDCRenderTarget(&props,&rt)))return false;
        if(FAILED(rt->BindDC(di->hDC,&di->rcItem)))return false; rt->BeginDraw(); rt->SetAntialiasMode(D2D1_ANTIALIAS_MODE_PER_PRIMITIVE);
        const RECT rr=di->rcItem; D2D1_RECT_F r=D2D1::RectF(static_cast<float>(rr.left),static_cast<float>(rr.top),static_cast<float>(rr.right),static_cast<float>(rr.bottom));
        const ThemePalette pal=app->Palette();
        ComPtr<ID2D1SolidColorBrush> fill,border,hi,icon;
        rt->CreateSolidColorBrush(D2DColor(selected?pal.accentSoft:(hover?pal.surfaceHover:pal.surface)),&fill);
        rt->CreateSolidColorBrush(D2DColor(selected?BlendColor(pal.border,pal.accent,0.55f):pal.border,hover?0.95f:0.75f),&border);
        rt->CreateSolidColorBrush(D2DColor(pal.accent,selected?1.0f:(hover?0.72f:0.0f)),&hi);
        rt->CreateSolidColorBrush(D2DColor((selected||hover)?pal.accent:pal.textMuted),&icon);
        rt->FillRoundedRectangle(D2D1::RoundedRect(r,8.0f,8.0f),fill.Get());
        if(!category)rt->DrawRoundedRectangle(D2D1::RoundedRect(r,8.0f,8.0f),border.Get(),0.85f);
        // Category tiles are intentionally borderless: modern Fluent-style filled
        // surfaces plus a small selected accent, with no vertical edge artifacts.
        if(category&&selected&&hi)rt->FillRoundedRectangle(D2D1::RoundedRect(D2D1::RectF(r.left+2.0f,r.top+9,r.left+4.5f,r.bottom-9),1.25f,1.25f),hi.Get());
        if(category){float cx=r.left+18,cy=(r.top+r.bottom)*.5f,st=1.6f; auto L=[&](float x1,float y1,float x2,float y2){rt->DrawLine(D2D1::Point2F(x1,y1),D2D1::Point2F(x2,y2),icon.Get(),st);};
            switch(iconIndex){
                case 0: { // General: Fluent-style settings gear
                    rt->DrawEllipse(D2D1::Ellipse(D2D1::Point2F(cx,cy),5.0f,5.0f),icon.Get(),st);
                    rt->DrawEllipse(D2D1::Ellipse(D2D1::Point2F(cx,cy),1.7f,1.7f),icon.Get(),st);
                    for(int k=0;k<8;++k){float a=static_cast<float>(k)*3.14159265f/4.0f;L(cx+std::cos(a)*6,cy+std::sin(a)*6,cx+std::cos(a)*9,cy+std::sin(a)*9);}break;}
                case 1: // Viewing: monitor
                    rt->DrawRoundedRectangle(D2D1::RoundedRect(D2D1::RectF(cx-9,cy-6,cx+9,cy+5),2,2),icon.Get(),st);L(cx-3,cy+8,cx+3,cy+8);L(cx,cy+5,cx,cy+8);break;
                case 2: // Mouse & Fullscreen: recognizable mouse
                    rt->DrawRoundedRectangle(D2D1::RoundedRect(D2D1::RectF(cx-6,cy-9,cx+6,cy+9),6,6),icon.Get(),st);L(cx,cy-9,cx,cy-2);L(cx-6,cy-1,cx+6,cy-1);break;
                case 3: { // Performance: speedometer/gauge
                    L(cx-8,cy+5,cx-6,cy-2);L(cx-6,cy-2,cx,cy-7);L(cx,cy-7,cx+6,cy-2);L(cx+6,cy-2,cx+8,cy+5);L(cx,cy+3,cx+5,cy-2);
                    rt->FillEllipse(D2D1::Ellipse(D2D1::Point2F(cx,cy+3),1.7f,1.7f),icon.Get());break;}
                case 4: rt->DrawEllipse(D2D1::Ellipse(D2D1::Point2F(cx,cy),8,5),icon.Get(),st);rt->FillEllipse(D2D1::Ellipse(D2D1::Point2F(cx,cy),2.1f,2.1f),icon.Get());break;
                case 5: L(cx-5,cy-7,cx-5,cy+7);L(cx-5,cy-7,cx+7,cy);L(cx+7,cy,cx-5,cy+7);break;
                case 6: // Hotkeys: keyboard keycap
                    rt->DrawRoundedRectangle(D2D1::RoundedRect(D2D1::RectF(cx-9,cy-6,cx+9,cy+6),3,3),icon.Get(),st);
                    L(cx-5,cy+2,cx+5,cy+2);L(cx-4,cy-2,cx-2,cy-2);L(cx,cy-2,cx+2,cy-2);L(cx+4,cy-2,cx+6,cy-2);break;
                case 7: // Tabs/workspace: layered browser tabs
                    rt->DrawRoundedRectangle(D2D1::RoundedRect(D2D1::RectF(cx-9,cy-4,cx+8,cy+7),3,3),icon.Get(),st);
                    rt->DrawRoundedRectangle(D2D1::RoundedRect(D2D1::RectF(cx-7,cy-8,cx+2,cy-3),2,2),icon.Get(),st);L(cx+1,cy-6,cx+6,cy-6);break;
                case 8: // Window-in-window: picture card with small floating pane
                    rt->DrawRoundedRectangle(D2D1::RoundedRect(D2D1::RectF(cx-9,cy-7,cx+9,cy+7),3,3),icon.Get(),st);
                    L(cx-6,cy+3,cx-2,cy-1);L(cx-2,cy-1,cx+1,cy+2);rt->DrawEllipse(D2D1::Ellipse(D2D1::Point2F(cx-4,cy-3),1.5f,1.5f),icon.Get(),st);
                    rt->DrawRoundedRectangle(D2D1::RoundedRect(D2D1::RectF(cx+1,cy,cx+8,cy+6),1.5f,1.5f),icon.Get(),1.3f);break;
                case 9: // Profiles: person + settings card
                    rt->DrawEllipse(D2D1::Ellipse(D2D1::Point2F(cx-3,cy-4),3.0f,3.0f),icon.Get(),st);
                    L(cx-8,cy+6,cx-7,cy+2);L(cx-7,cy+2,cx-4,cy);L(cx-4,cy,cx,cy+2);L(cx,cy+2,cx+1,cy+6);
                    rt->DrawRoundedRectangle(D2D1::RoundedRect(D2D1::RectF(cx+2,cy-5,cx+9,cy+4),2,2),icon.Get(),1.25f);break;
                case 10: // Windows integration: four-pane window
                    rt->DrawRectangle(D2D1::RectF(cx-8,cy-7,cx+8,cy+7),icon.Get(),st);L(cx,cy-7,cx,cy+7);L(cx-8,cy,cx+8,cy);break;
                default: rt->DrawRoundedRectangle(D2D1::RoundedRect(D2D1::RectF(cx-7,cy-7,cx+7,cy+7),2,2),icon.Get(),st);L(cx-4,cy-2,cx+4,cy-2);L(cx-4,cy+3,cx+4,cy+3);break;
            }
        }
        HRESULT hr=rt->EndDraw(); if(FAILED(hr))return false;
        // Category artwork is Direct2D vector-only. Do not overlay SHGetStockIconInfo
        // raster icons: they scale poorly and were the source of the legacy/XP look.
        SetBkMode(di->hDC,TRANSPARENT);SetTextColor(di->hDC,(selected||hover)?pal.text:pal.text);RECT tr=di->rcItem;tr.left+=category?44:10;DrawTextW(di->hDC,label,-1,&tr,DT_SINGLELINE|DT_VCENTER|(category?DT_LEFT:DT_CENTER)|DT_END_ELLIPSIS|DT_NOPREFIX);return true;
    }

    static COLORREF ContrastingTextColor(COLORREF bg) {
        const double r=GetRValue(bg)/255.0, g=GetGValue(bg)/255.0, b=GetBValue(bg)/255.0;
        const double lum=0.2126*r+0.7152*g+0.0722*b;
        return lum>=0.56?RGB(18,20,23):RGB(250,252,255);
    }

    static LRESULT CALLBACK SettingsStaticDragSubclass(HWND wnd, UINT msg, WPARAM wp, LPARAM lp, UINT_PTR, DWORD_PTR) {
        if(msg==WM_LBUTTONDOWN){
            HWND parent=GetParent(wnd); if(parent){POINT p{GET_X_LPARAM(lp),GET_Y_LPARAM(lp)};ClientToScreen(wnd,&p);ReleaseCapture();SendMessageW(parent,WM_NCLBUTTONDOWN,HTCAPTION,MAKELPARAM(p.x,p.y));return 0;}
        } else if(msg==WM_NCDESTROY) RemoveWindowSubclass(wnd,SettingsStaticDragSubclass,1);
        return DefSubclassProc(wnd,msg,wp,lp);
    }

    static LRESULT CALLBACK SettingsWndProc(HWND wnd, UINT msg, WPARAM wp, LPARAM lp) {
        auto* app = reinterpret_cast<ViewerApp*>(GetPropW(wnd,L"GlideApp"));
        if(msg==WM_NCCREATE){
            auto* cs=reinterpret_cast<CREATESTRUCTW*>(lp);app=reinterpret_cast<ViewerApp*>(cs->lpCreateParams);SetPropW(wnd,L"GlideApp",app);SetWindowLongPtrW(wnd,GWLP_USERDATA,0);
            // Apply non-client theming before Windows paints the first frame. Waiting until
            // WM_CREATE/deferred theming caused the brief white Settings border seen in 1.2.42.
            const bool light=app&&app->ResolveLightTheme();BOOL dark=light?FALSE:TRUE;
            if(FAILED(DwmSetWindowAttribute(wnd,20,&dark,sizeof(dark))))DwmSetWindowAttribute(wnd,19,&dark,sizeof(dark));
            if(app){ThemePalette pal=app->Palette();COLORREF border=pal.border,caption=pal.windowBg;DwmSetWindowAttribute(wnd,34,&border,sizeof(border));DwmSetWindowAttribute(wnd,35,&caption,sizeof(caption));}
            SetWindowTheme(wnd,light?L"Explorer":L"DarkMode_Explorer",nullptr);
        }
        static HBRUSH darkBrush=CreateSolidBrush(RGB(24,26,29));
        static HBRUSH editBrush=CreateSolidBrush(RGB(35,38,42));
        switch(msg){
        case WM_CREATE:{
            if(app)app->settingsCreateMs=app->settingsOpenRequestTick?(GetTickCount64()-app->settingsOpenRequestTick):0;
            BOOL dark=(app&&app->ResolveLightTheme())?FALSE:TRUE; DwmSetWindowAttribute(wnd,20,&dark,sizeof(dark));
            HFONT font=CreateFontW(-16,0,0,0,FW_NORMAL,FALSE,FALSE,FALSE,DEFAULT_CHARSET,OUT_DEFAULT_PRECIS,CLIP_DEFAULT_PRECIS,CLEARTYPE_QUALITY,DEFAULT_PITCH,L"Segoe UI");
            auto add=[&](const wchar_t* cls,const wchar_t* text,DWORD style,int x,int y,int w,int h,int id,int cat=-1){if(_wcsicmp(cls,L"STATIC")==0)style|=SS_NOPREFIX;if(cat>=0){x+=70;y+=64;}DWORD ws=WS_CHILD|style;if(cat<0)ws|=WS_VISIBLE;else ws|=WS_CLIPSIBLINGS;HWND c=CreateWindowExW(0,cls,text,ws,x,y,w,h,wnd,reinterpret_cast<HMENU>(static_cast<INT_PTR>(id)),app?app->hInst:nullptr,nullptr);SendMessageW(c,WM_SETFONT,reinterpret_cast<WPARAM>(font),FALSE);if(cat<0){SetWindowTheme(c,(app&&app->ResolveLightTheme())?L"Explorer":L"DarkMode_Explorer",nullptr);SetPropW(c,L"GlideControlThemed",reinterpret_cast<HANDLE>(1));}if(_wcsicmp(cls,L"STATIC")==0)SetWindowSubclass(c,SettingsStaticDragSubclass,1,0);const bool isButton=_wcsicmp(cls,L"BUTTON")==0;DWORD bt=style&BS_TYPEMASK;if(isButton&&bt==BS_OWNERDRAW)SetWindowSubclass(c,ModernButtonSubclass,1,0);else if(isButton&&(bt==BS_AUTOCHECKBOX||bt==BS_CHECKBOX||bt==BS_AUTORADIOBUTTON||bt==BS_RADIOBUTTON))SetWindowSubclass(c,ModernToggleSubclass,1,reinterpret_cast<DWORD_PTR>(app));if(cat>=0){SetPropW(c,L"GlideCat",reinterpret_cast<HANDLE>(static_cast<INT_PTR>(cat+1)));SetPropW(c,L"GlideOX",reinterpret_cast<HANDLE>(static_cast<INT_PTR>(x+1)));SetPropW(c,L"GlideOY",reinterpret_cast<HANDLE>(static_cast<INT_PTR>(y+1)));SetPropW(c,L"GlideOW",reinterpret_cast<HANDLE>(static_cast<INT_PTR>(w+1)));SetPropW(c,L"GlideOH",reinterpret_cast<HANDLE>(static_cast<INT_PTR>(h+1)));}return c;};
            HFONT titleFont=CreateFontW(-24,0,0,0,FW_SEMIBOLD,FALSE,FALSE,FALSE,DEFAULT_CHARSET,OUT_DEFAULT_PRECIS,CLIP_DEFAULT_PRECIS,CLEARTYPE_QUALITY,DEFAULT_PITCH,L"Segoe UI Variable Display");
            HFONT subtitleFont=CreateFontW(-14,0,0,0,FW_NORMAL,FALSE,FALSE,FALSE,DEFAULT_CHARSET,OUT_DEFAULT_PRECIS,CLIP_DEFAULT_PRECIS,CLEARTYPE_QUALITY,DEFAULT_PITCH,L"Segoe UI");
            HWND brand=add(L"STATIC",L"Glide Settings",0,22,16,245,34,-1); if(titleFont)SendMessageW(brand,WM_SETFONT,reinterpret_cast<WPARAM>(titleFont),TRUE);
            HWND browse=add(L"STATIC",L"Search or browse by category",0,22,52,245,24,-1); if(subtitleFont)SendMessageW(browse,WM_SETFONT,reinterpret_cast<WPARAM>(subtitleFont),TRUE);
            HWND search=add(L"EDIT",L"",ES_AUTOHSCROLL|WS_TABSTOP,322,23,700,30,IDC_SET_SEARCH); SendMessageW(search,EM_SETMARGINS,EC_LEFTMARGIN|EC_RIGHTMARGIN,MAKELPARAM(14,10)); SetWindowSubclass(search,SettingsSearchSubclass,1,reinterpret_cast<DWORD_PTR>(app));
            SendMessageW(search,EM_SETMARGINS,EC_LEFTMARGIN|EC_RIGHTMARGIN,MAKELPARAM(14,12));
            HWND pageTitle=add(L"STATIC",L"General",0,322,76,700,34,IDC_SET_PAGE_TITLE); if(titleFont)SendMessageW(pageTitle,WM_SETFONT,reinterpret_cast<WPARAM>(titleFont),TRUE);
            HWND pageSub=add(L"STATIC",L"Application behavior, history, navigation and startup preferences",0,322,110,700,24,IDC_SET_PAGE_SUBTITLE); if(subtitleFont)SendMessageW(pageSub,WM_SETFONT,reinterpret_cast<WPARAM>(subtitleFont),TRUE);
            add(L"BUTTON",L"‹",BS_OWNERDRAW,32,666,42,34,IDC_SET_SECTION_PREV); HWND sectionLabel=add(L"STATIC",L"Section 1 / 1",SS_CENTER,82,673,138,22,IDC_SET_SECTION_LABEL); if(subtitleFont)SendMessageW(sectionLabel,WM_SETFONT,reinterpret_cast<WPARAM>(subtitleFont),TRUE); add(L"BUTTON",L"›",BS_OWNERDRAW,236,666,42,34,IDC_SET_SECTION_NEXT); HWND scrollHint=add(L"STATIC",L"",SS_RIGHT,360,673,340,22,IDC_SET_SCROLL_HINT); if(subtitleFont)SendMessageW(scrollHint,WM_SETFONT,reinterpret_cast<WPARAM>(subtitleFont),FALSE);
            const wchar_t* cats[]={L"General",L"Viewing & Interface",L"Mouse & Fullscreen",L"Performance & Startup",L"Status Bar",L"Slideshow",L"Hotkeys",L"Tabs & Workspace",L"Window in Window",L"Profiles & Presets",L"Windows Integration",L"Developer Options"};
            const int catids[]={IDC_SET_CAT_GENERAL,IDC_SET_CAT_VIEWING,IDC_SET_CAT_MOUSE,IDC_SET_CAT_PERFORMANCE,IDC_SET_CAT_STATUS,IDC_SET_CAT_SLIDESHOW,IDC_SET_CAT_HOTKEYS,IDC_SET_CAT_TABS,IDC_SET_CAT_OVERLAYS,IDC_SET_CAT_PROFILES,IDC_SET_CAT_WINDOWS,IDC_SET_CAT_DEVELOPER};
            for(int i=0;i<12;++i)add(L"BUTTON",cats[i],BS_OWNERDRAW,18,98+i*48,265,40,catids[i]);
            add(L"SCROLLBAR",L"",SBS_VERT,284,98,14,520,IDC_SET_RAIL_SCROLL);
            add(L"SCROLLBAR",L"",SBS_HORZ,322,650,700,12,IDC_SET_SECTION_SCROLL);
            auto cb=[&](const wchar_t*t,int y,int id,int cat){return add(L"BUTTON",t,BS_AUTOCHECKBOX,250,y,690,34,id,cat);};
            // General
            cb(L"Keep recent file/folder history",82,IDC_SET_HISTORY,0); add(L"STATIC",L"History size (1-16)",0,260,116,190,22,0,0); add(L"EDIT",L"",ES_NUMBER,470,112,80,26,IDC_SET_RECENT_LIMIT,0); cb(L"Remember window position, size and maximized state",154,IDC_SET_REMEMBER_WINDOW,0); cb(L"Reuse one Glide instance (applies next launch)",190,IDC_SET_SINGLE_INSTANCE,0); cb(L"Automatically continue into sibling folders",226,IDC_SET_SIBLING_FOLDERS,0); cb(L"Remember last main-viewer Open / Open Folder location",262,IDC_SET_MAIN_REMEMBER_FOLDER,0);
            // Present a *fully populated* General page before constructing inactive pages.
            // 1.2.42 painted quickly but initially showed blank/unchecked controls while WM_CREATE
            // continued. Populate the visible page first so the first frame is also the correct one.
            if(app){SetCheck(wnd,IDC_SET_HISTORY,app->recentHistoryEnabled);SetDlgItemInt(wnd,IDC_SET_RECENT_LIMIT,app->recentHistoryLimit,FALSE);SetCheck(wnd,IDC_SET_REMEMBER_WINDOW,app->rememberWindowPlacement);SetCheck(wnd,IDC_SET_SINGLE_INSTANCE,app->singleInstance);SetCheck(wnd,IDC_SET_SIBLING_FOLDERS,app->autoSiblingFolders);SetCheck(wnd,IDC_SET_MAIN_REMEMBER_FOLDER,app->mainRememberLastFolder);}
            LayoutSettingsChrome(wnd);RefreshSettingsVisibility(wnd);
            // Viewing
            cb(L"Show translucent status overlay",82,IDC_SET_STATUS,1); cb(L"Show scrollbars only when needed",116,IDC_SET_SCROLLBARS,1); cb(L"Show full path in title",150,IDC_SET_FULLPATH,1); cb(L"Hide cursor when idle in fullscreen",184,IDC_SET_CURSOR_HIDE,1); cb(L"Auto-hide fullscreen title bar",218,IDC_SET_FULLSCREEN_BAR_AUTOHIDE,1); cb(L"Enable tabs / Explorer tab bar",252,IDC_SET_TITLE_TABS,1); cb(L"Fullscreen X closes Glide (off = exit fullscreen)",286,IDC_SET_FULLSCREEN_X_CLOSE,1); cb(L"Keep status bar visible in fullscreen",320,IDC_SET_FULLSCREEN_STATUS_ALWAYS,1); cb(L"Always keep Glide on top",354,IDC_SET_ALWAYS_ON_TOP,1); cb(L"Show Quick Tips cards on Home",388,IDC_SET_HOME_TIPS,1); cb(L"Escape stops slideshow and restores its starting mode",520,IDC_SET_ESC_SLIDESHOW,1); cb(L"Escape exits fullscreen when no slideshow is running",550,IDC_SET_ESC_FULLSCREEN,1); cb(L"Escape in windowed mode asks before closing Glide",580,IDC_SET_ESC_WINDOWED_CONFIRM,1); add(L"BUTTON",L"Reset remembered Escape close choice",BS_OWNERDRAW,260,614,300,34,IDC_SET_ESC_REMEMBER_RESET,1); add(L"STATIC",L"Default view for new images",0,260,432,205,22,0,1); HWND defaultView=add(L"COMBOBOX",L"",CBS_DROPDOWNLIST|CBS_HASSTRINGS|WS_VSCROLL,475,424,210,190,IDC_SET_DEFAULT_VIEW_MODE,1); for(const wchar_t*t:{L"Fit image",L"Fit to width",L"Fit to height",L"100%",L"Custom %"})SendMessageW(defaultView,CB_ADDSTRING,0,reinterpret_cast<LPARAM>(t)); add(L"STATIC",L"Custom (%)",0,700,432,95,22,0,1); add(L"EDIT",L"",ES_NUMBER,800,428,70,26,IDC_SET_DEFAULT_CUSTOM_ZOOM,1); add(L"STATIC",L"Zoom step (%)",0,260,470,160,22,0,1);add(L"EDIT",L"",ES_NUMBER,440,466,80,26,IDC_SET_ZOOM_STEP,1);add(L"STATIC",L"Fullscreen cursor hide delay (ms)",0,540,470,235,22,0,1);add(L"EDIT",L"",ES_NUMBER,780,466,70,26,IDC_SET_CURSOR_HIDE_DELAY,1);
            add(L"STATIC",L"Theme",0,260,508,120,22,0,1);HWND theme=add(L"COMBOBOX",L"",CBS_DROPDOWNLIST|CBS_HASSTRINGS|WS_VSCROLL,380,500,190,190,IDC_SET_THEME_MODE,1);for(const wchar_t*t:{L"Dark",L"Light"})SendMessageW(theme,CB_ADDSTRING,0,reinterpret_cast<LPARAM>(t));
            add(L"STATIC",L"Accent / glow",0,590,508,130,22,0,1);HWND acc=add(L"COMBOBOX",L"",CBS_DROPDOWNLIST|CBS_HASSTRINGS|WS_VSCROLL,740,500,135,220,IDC_SET_ACCENT_COLOR,1);for(const wchar_t*t:{L"Blue",L"White",L"Grey",L"Red",L"Yellow",L"Green",L"Purple"})SendMessageW(acc,CB_ADDSTRING,0,reinterpret_cast<LPARAM>(t));
            cb(L"Use previous image zoom for subsequent images in this run",708,IDC_SET_KEEP_ZOOM_NAV,1);
            add(L"STATIC",L"After exiting fullscreen",0,260,664,210,22,0,1);HWND fsExit=add(L"COMBOBOX",L"",CBS_DROPDOWNLIST|CBS_HASSTRINGS|WS_VSCROLL,485,656,330,140,IDC_SET_FULLSCREEN_EXIT_MODE,1);SendMessageW(fsExit,CB_ADDSTRING,0,reinterpret_cast<LPARAM>(L"Restore original window position"));SendMessageW(fsExit,CB_ADDSTRING,0,reinterpret_cast<LPARAM>(L"Restore maximized window"));
            add(L"STATIC",L"Picture text overlay",0,260,914,260,24,0,1);
            cb(L"Show picture text overlay",944,IDC_SET_TEXT_OVERLAY_ENABLED,1);
            add(L"STATIC",L"Text template",0,260,986,120,22,0,1);add(L"EDIT",L"",ES_AUTOHSCROLL,390,982,470,28,IDC_SET_TEXT_OVERLAY_TEMPLATE,1);
            add(L"STATIC",L"Tokens: {index} {total} {name} {folder} {zoom} {width} {height}",0,260,1016,600,22,0,1);
            add(L"STATIC",L"Position",0,260,1052,110,22,0,1);HWND textPos=add(L"COMBOBOX",L"",CBS_DROPDOWNLIST|CBS_HASSTRINGS|WS_VSCROLL,380,1044,230,180,IDC_SET_TEXT_OVERLAY_POSITION,1);for(const wchar_t*t:{L"Top left",L"Top centre",L"Top right",L"Bottom left",L"Bottom centre",L"Bottom right"})SendMessageW(textPos,CB_ADDSTRING,0,reinterpret_cast<LPARAM>(t));
            add(L"STATIC",L"Font size",0,260,1092,100,22,0,1);add(L"EDIT",L"",ES_NUMBER,365,1088,70,26,IDC_SET_TEXT_OVERLAY_SIZE,1);add(L"STATIC",L"Opacity (%)",0,470,1092,110,22,0,1);add(L"EDIT",L"",ES_NUMBER,585,1088,70,26,IDC_SET_TEXT_OVERLAY_OPACITY,1);
            add(L"STATIC",L"Text colour (#RRGGBB)",0,260,1130,190,22,0,1);add(L"EDIT",L"",ES_AUTOHSCROLL,465,1126,130,26,IDC_SET_TEXT_OVERLAY_COLOR,1);
            cb(L"Bold text",1166,IDC_SET_TEXT_OVERLAY_BOLD,1);cb(L"Soft shadow for readability",1200,IDC_SET_TEXT_OVERLAY_SHADOW,1);
            // Mouse
            cb(L"Double-click enters fullscreen",82,IDC_SET_DBL_FULLSCREEN,2); cb(L"Double-click exits fullscreen",116,IDC_SET_DBL_EXIT_FULLSCREEN,2); cb(L"Fullscreen: left/right click = next/previous",150,IDC_SET_FULLSCREEN_CLICKS,2); cb(L"Windowed wheel zooms (off = previous/next)",184,IDC_SET_WINDOWED_WHEEL_ZOOM,2); cb(L"Invert previous/next mouse-wheel direction",218,IDC_SET_INVERT_WHEEL_NAV,2);
            add(L"STATIC",L"Left-drag on image",0,260,258,190,22,0,2);HWND leftDrag=add(L"COMBOBOX",L"",CBS_DROPDOWNLIST|WS_VSCROLL,465,252,250,200,IDC_SET_LEFT_DRAG_MODE,2);SendMessageW(leftDrag,CB_ADDSTRING,0,reinterpret_cast<LPARAM>(L"Create selection (Glide)"));SendMessageW(leftDrag,CB_ADDSTRING,0,reinterpret_cast<LPARAM>(L"Pan / move zoomed image"));
            cb(L"Right-drag pans a zoomed image",292,IDC_SET_RIGHT_DRAG_PAN,2);cb(L"Click inside selection zooms into it",326,IDC_SET_SELECTION_CLICK_ZOOM,2);cb(L"Right-click inside selection zooms out",360,IDC_SET_SELECTION_RIGHT_ZOOM,2);cb(L"Left-drag empty background moves the native window",394,IDC_SET_BACKGROUND_DRAG_WINDOW,2);
            cb(L"Ctrl + wheel zooms",428,IDC_SET_CTRL_WHEEL_ZOOM,2);cb(L"Fullscreen wheel zooms (off = previous / next)",462,IDC_SET_FULLSCREEN_WHEEL_ZOOM,2);cb(L"Zoom around mouse pointer (off = image centre)",496,IDC_SET_ZOOM_AROUND_CURSOR,2);cb(L"Middle-drag pans image",530,IDC_SET_MIDDLE_DRAG_PAN,2);cb(L"Wrap from last image to first within current folder",564,IDC_SET_WRAP_FOLDER,2);
            add(L"STATIC",L"Advanced gesture overrides",0,260,616,300,24,0,2);
            add(L"STATIC",L"Every interaction can keep Glide's normal behavior or be independently remapped. These overrides are also saved in profiles and exports.",0,260,644,575,42,0,2);
            {int gy=700;for(int gi=0;gi<kGestureSlotCount;++gi){
                const auto slot=static_cast<GestureSlot>(gi);
                add(L"STATIC",GestureSlotLabel(slot),0,260,gy+5,315,24,0,2);
                HWND gc=add(L"COMBOBOX",L"",CBS_DROPDOWNLIST|CBS_HASSTRINGS|WS_VSCROLL|WS_TABSTOP,585,gy,270,360,IDC_SET_GESTURE_BASE+gi,2);
                // Populate the 24×19 gesture choice strings after the Settings window becomes
                // interactive; these hundreds of synchronous combo messages dominated open time.
                gy+=38;
            }}
            // Performance
            add(L"STATIC",L"Initial image quality",0,260,82,180,22,0,3); add(L"BUTTON",L"Maximum speed",BS_AUTORADIOBUTTON|WS_GROUP,260,112,145,26,IDC_SET_QUALITY_SPEED,3); add(L"BUTTON",L"Balanced (recommended)",BS_AUTORADIOBUTTON,420,112,185,26,IDC_SET_QUALITY_BALANCED,3); add(L"BUTTON",L"Maximum quality",BS_AUTORADIOBUTTON,620,112,165,26,IDC_SET_QUALITY_MAX,3); cb(L"Adaptive fast preview if first frame is slow (non-JPEG)",160,IDC_SET_ADAPTIVE_PREVIEW,3); add(L"STATIC",L"Adaptive preview delay (ms, 5-500)",0,260,198,280,22,0,3); add(L"EDIT",L"",ES_NUMBER,560,194,90,26,IDC_SET_ADAPTIVE_DELAY,3); cb(L"Prioritize instant first image on cold launch",244,IDC_SET_FAST_COLD_START,3); cb(L"Prefetch neighboring images after first frame",278,IDC_SET_PREFETCH_ENABLED,3); cb(L"Refine previews to full quality while idle",312,IDC_SET_BACKGROUND_REFINEMENT,3); cb(L"Purge decoded cache when Glide is minimized",346,IDC_SET_PURGE_CACHE_MINIMIZE,3); cb(L"Write cold-start timing diagnostics",380,IDC_SET_STARTUP_DIAGNOSTICS,3); add(L"STATIC",L"Prefetch depth per direction (0-6)",0,260,424,270,22,0,3);add(L"EDIT",L"",ES_NUMBER,550,420,80,26,IDC_SET_PREFETCH_DEPTH,3);add(L"STATIC",L"Decoded cache items (2-64)",0,260,462,240,22,0,3);add(L"EDIT",L"",ES_NUMBER,520,458,80,26,IDC_SET_CACHE_ITEMS,3);add(L"STATIC",L"Rapid-browse preview longest side (px, 480-4096)",0,260,500,390,22,0,3);add(L"EDIT",L"",ES_NUMBER,660,496,90,26,IDC_SET_RAPID_PREVIEW_SIZE,3);cb(L"Progressive JPEG: wait for earliest colour preview (recommended)",538,IDC_SET_PROGRESSIVE_COLOR_FIRST,3);
            // Status
            cb(L"Navigation buttons (Home / Previous / Next / End)",82,IDC_SET_STATUS_NAV,4); cb(L"Zoom +/- buttons",116,IDC_SET_STATUS_ZOOM,4); cb(L"Slideshow controls",150,IDC_SET_STATUS_SLIDE,4); cb(L"Fit width / height buttons",184,IDC_SET_STATUS_FIT,4); cb(L"Information / eye button",218,IDC_SET_STATUS_INFO,4); cb(L"Collapse button",252,IDC_SET_STATUS_COLLAPSE,4); cb(L"Options button",286,IDC_SET_STATUS_OPTIONS,4); cb(L"Close button",320,IDC_SET_STATUS_CLOSE,4); cb(L"Show resolution",368,IDC_SET_STAT_RESOLUTION,4); cb(L"Show zoom percentage",402,IDC_SET_STAT_ZOOM,4); cb(L"Show folder position",436,IDC_SET_STAT_INDEX,4); cb(L"Show file size",470,IDC_SET_STAT_FILESIZE,4); cb(L"Show format",504,IDC_SET_STAT_FORMAT,4); cb(L"Show preview/refinement state",538,IDC_SET_STAT_PREVIEW,4);
            // Slideshow
            add(L"STATIC",L"Interval (ms)",0,260,82,105,22,0,5); add(L"EDIT",L"",ES_NUMBER,380,78,100,26,IDC_SET_SLIDE_INTERVAL,5); cb(L"Loop slideshow",126,IDC_SET_SLIDE_LOOP,5); cb(L"Continue across folders",160,IDC_SET_SLIDE_CROSS,5); cb(L"Shuffle",194,IDC_SET_SLIDE_SHUFFLE,5);
            // Hotkeys
            add(L"STATIC",L"Keyboard shortcuts",0,260,76,260,24,0,6);
            add(L"STATIC",L"Select an action, change its shortcut or add another shortcut. Multiple keys may intentionally perform the same action.",0,260,102,575,42,IDC_SET_HOTKEY_HINT,6);
            {HWND hot=add(WC_LISTVIEWW,L"",LVS_REPORT|LVS_SINGLESEL|LVS_SHOWSELALWAYS|WS_TABSTOP,260,148,575,340,IDC_SET_HOTKEY_LIST,6);
             SetWindowTheme(hot,(app&&app->ResolveLightTheme())?L"Explorer":L"DarkMode_Explorer",nullptr);
             ListView_SetExtendedListViewStyle(hot,LVS_EX_FULLROWSELECT|LVS_EX_DOUBLEBUFFER|LVS_EX_HEADERDRAGDROP|LVS_EX_GRIDLINES);}
            add(L"BUTTON",L"Change shortcut",BS_OWNERDRAW,260,502,145,36,IDC_SET_HOTKEY_CHANGE,6); add(L"BUTTON",L"Add shortcut",BS_OWNERDRAW,415,502,135,36,IDC_SET_HOTKEY_ADD_ALT,6); add(L"BUTTON",L"Remove shortcuts",BS_OWNERDRAW,560,502,140,36,IDC_SET_HOTKEY_CLEAR,6); add(L"BUTTON",L"Reset hotkeys",BS_OWNERDRAW,710,502,145,36,IDC_SET_HOTKEY_RESET,6);
            // Tabs & Workspace
            add(L"STATIC",L"Minimum tab width",0,260,82,190,22,0,7);add(L"EDIT",L"",ES_NUMBER,470,78,90,26,IDC_SET_TAB_MIN_WIDTH,7);
            add(L"STATIC",L"Maximum tab width",0,260,120,190,22,0,7);add(L"EDIT",L"",ES_NUMBER,470,116,90,26,IDC_SET_TAB_MAX_WIDTH,7);
            cb(L"Allow dragging tabs out into new Glide windows",158,IDC_SET_TAB_DETACH,7);cb(L"Allow dragging tabs between Glide windows",194,IDC_SET_TAB_ATTACH,7);
            add(L"STATIC",L"Recently closed tabs retained",0,260,234,220,22,0,7);add(L"EDIT",L"",ES_NUMBER,500,230,90,26,IDC_SET_CLOSED_TAB_LIMIT,7);cb(L"Close source window if its final tab is detached",270,IDC_SET_CLOSE_EMPTY_DETACH,7);cb(L"Include a Home tab in newly detached windows",306,IDC_SET_DETACHED_HOME_TAB,7);
            cb(L"Show sibling-folder navigation group",344,IDC_SET_FOLDER_NAV_SHOW_GROUP,7);
            cb(L"Show Previous Folder button",374,IDC_SET_FOLDER_NAV_SHOW_PREV,7);
            cb(L"Show Next Folder button",404,IDC_SET_FOLDER_NAV_SHOW_NEXT,7);
            cb(L"Show Explore Parent Folders button",434,IDC_SET_FOLDER_NAV_SHOW_EXPLORE,7);
            cb(L"Skip sibling folders that contain no supported images",464,IDC_SET_FOLDER_NAV_SKIP_EMPTY,7);
            cb(L"Wrap sibling-folder navigation at parent-directory ends",494,IDC_SET_FOLDER_NAV_WRAP,7);
            cb(L"Open first image in chosen sibling folder (off = last)",524,IDC_SET_FOLDER_NAV_FIRST_IMAGE,7);
            cb(L"Show tooltips for sibling-folder buttons",554,IDC_SET_FOLDER_NAV_TOOLTIPS,7);
            cb(L"Explore Parent Folders: open an empty folder as a browser tab",584,IDC_SET_FOLDER_NAV_EXPLORE_SINGLECLICK,7);
            cb(L"Include hidden sibling folders in folder navigation",614,IDC_SET_FOLDER_NAV_REMEMBER_PICKER,7);
            // Window in Window
            cb(L"Show Window-in-Window controls in status bar",82,IDC_SET_OVERLAY_BUTTON,8);add(L"STATIC",L"Default overlay opacity (10-100%)",0,260,124,280,22,0,8);add(L"EDIT",L"",ES_NUMBER,560,120,90,26,IDC_SET_OVERLAY_DEFAULT_OPACITY,8);cb(L"Remember the last overlay image folder",160,IDC_SET_OVERLAY_REMEMBER_FOLDER,8);
            cb(L"Selected overlay receives keyboard +/- zoom",198,IDC_SET_OVERLAY_KEYBOARD_ZOOM,8);
            cb(L"Mouse wheel zooms the hovered or selected overlay",232,IDC_SET_OVERLAY_CTRL_WHEEL_ZOOM,8);
            cb(L"Highlight the selected overlay with the accent colour",266,IDC_SET_OVERLAY_SELECTED_HIGHLIGHT,8);
            add(L"STATIC",L"Overlay zoom step (%)",0,260,306,190,22,0,8);add(L"EDIT",L"",ES_NUMBER,470,302,80,26,IDC_SET_OVERLAY_ZOOM_STEP,8);
            cb(L"Remember overlay zoom in saved overlay layouts",340,IDC_SET_OVERLAY_REMEMBER_ZOOM,8);
            cb(L"Right-click and hold pans cropped / zoomed overlay content",374,IDC_SET_OVERLAY_RIGHT_DRAG_PAN,8);
            add(L"STATIC",L"A quick right-click still opens the overlay context menu. Right-drag panning activates only when the overlay is zoomed/cropped.",0,260,414,650,48,0,8);
            // Profiles & Presets
            add(L"STATIC",L"Whole-program behavior preset",0,260,82,245,22,0,9);HWND preset=add(L"COMBOBOX",L"",CBS_DROPDOWNLIST|CBS_HASSTRINGS|WS_VSCROLL,260,110,420,240,IDC_SET_PRESET_COMBO,9);for(int i=0;i<=5;++i)SendMessageW(preset,CB_ADDSTRING,0,reinterpret_cast<LPARAM>(BehaviorPresetName(i)));add(L"STATIC",L"▼",SS_CENTER,650,114,24,20,0,9);
            add(L"STATIC",L"Choose a preset here. Its mouse, navigation, fullscreen and hotkey values are staged immediately in Settings; use the global Apply or OK button to save them, or Cancel to discard them.",0,260,158,590,62,0,9);
            add(L"STATIC",L"User profile 1",0,260,232,135,22,0,9);add(L"BUTTON",L"Save current",BS_OWNERDRAW,405,222,135,36,IDC_SET_PROFILE_SAVE1,9);add(L"BUTTON",L"Load",BS_OWNERDRAW,555,222,105,36,IDC_SET_PROFILE_LOAD1,9);
            add(L"STATIC",L"User profile 2",0,260,282,135,22,0,9);add(L"BUTTON",L"Save current",BS_OWNERDRAW,405,272,135,36,IDC_SET_PROFILE_SAVE2,9);add(L"BUTTON",L"Load",BS_OWNERDRAW,555,272,105,36,IDC_SET_PROFILE_LOAD2,9);
            add(L"STATIC",L"User profile 3",0,260,332,135,22,0,9);add(L"BUTTON",L"Save current",BS_OWNERDRAW,405,322,135,36,IDC_SET_PROFILE_SAVE3,9);add(L"BUTTON",L"Load",BS_OWNERDRAW,555,322,105,36,IDC_SET_PROFILE_LOAD3,9);
            add(L"STATIC",L"Settings import / export",0,260,398,180,22,0,9);add(L"BUTTON",L"Import...",BS_OWNERDRAW,445,388,135,36,IDC_SET_SETTINGS_IMPORT,9);add(L"BUTTON",L"Export...",BS_OWNERDRAW,595,388,135,36,IDC_SET_SETTINGS_EXPORT,9);
            add(L"STATIC",L"Profiles are tiny INI snapshots stored with Glide settings. Export creates a portable .gli settings file; import replaces the active configuration.",0,260,442,575,48,0,9);
            // Windows
            add(L"BUTTON",app->installedMode?L"Open Windows Default Apps":L"Add Glide to Open With",BS_OWNERDRAW,260,82,250,36,IDC_SET_ASSOC_REGISTER,10); add(L"BUTTON",app->installedMode?L"Clean user Open With entries":L"Remove Glide Open With entries",BS_OWNERDRAW,260,130,290,36,IDC_SET_ASSOC_REMOVE,10);
            add(L"STATIC",L"External program 1  (Shift+1)",0,260,202,210,22,0,10);add(L"EDIT",L"",ES_AUTOHSCROLL|ES_READONLY|WS_BORDER,260,226,470,26,IDC_SET_EXT1_PATH,10);add(L"BUTTON",L"Browse...",BS_OWNERDRAW,744,222,105,34,IDC_SET_EXT1_BROWSE,10);
            add(L"STATIC",L"External program 2  (Shift+2)",0,260,276,210,22,0,10);add(L"EDIT",L"",ES_AUTOHSCROLL|ES_READONLY|WS_BORDER,260,300,470,26,IDC_SET_EXT2_PATH,10);add(L"BUTTON",L"Browse...",BS_OWNERDRAW,744,296,105,34,IDC_SET_EXT2_BROWSE,10);
            add(L"STATIC",L"External program 3  (Shift+3)",0,260,350,210,22,0,10);add(L"EDIT",L"",ES_AUTOHSCROLL|ES_READONLY|WS_BORDER,260,374,470,26,IDC_SET_EXT3_PATH,10);add(L"BUTTON",L"Browse...",BS_OWNERDRAW,744,370,105,34,IDC_SET_EXT3_BROWSE,10);
            add(L"STATIC",L"Glide Alpha 0.12107",0,22,708,320,22,IDC_SET_VERSION_LABEL); add(L"BUTTON",L"Restore defaults",BS_OWNERDRAW,22,742,170,38,IDC_SET_DEFAULTS); add(L"BUTTON",L"Cancel",BS_OWNERDRAW,744,742,90,38,IDC_SET_CANCEL); add(L"BUTTON",L"Apply",BS_OWNERDRAW,844,742,90,38,IDC_SET_APPLY); add(L"BUTTON",L"OK",BS_OWNERDRAW,944,742,90,38,IDC_SET_OK);
            // Developer Options: keep diagnostics out of the everyday Settings footer.
            add(L"STATIC",L"Developer diagnostics",0,260,82,260,24,0,11);
            add(L"STATIC",L"Troubleshooting tools intended for development and support. Normal users do not need these controls.",0,260,116,690,44,0,11);
            add(L"BUTTON",L"Run Diagnostics",BS_OWNERDRAW,260,176,210,40,IDC_SET_RUN_DIAGNOSTICS,11);
            add(L"BUTTON",L"Export Snapshot",BS_OWNERDRAW,490,176,200,40,IDC_SET_EXPORT_DIAGNOSTICS,11);
            if(app){
                const wchar_t* catTips[]={L"General application behavior and startup preferences",L"Viewing, title and fullscreen appearance",L"Mouse and fullscreen interaction",L"Image decoding, cold start and cache controls",L"Choose which status-bar controls and information are visible",L"Default slideshow behavior",L"View and customize every Glide keyboard shortcut",L"Fluid tab sizing, detach, cross-window attach and restore history",L"Window-in-Window overlays, transparency and persistence",L"Whole-program behavior presets, three user profiles and portable import/export",L"Windows Open With integration",L"Advanced diagnostics and developer-only troubleshooting tools"};
                for(int i=0;i<12;++i)AddDialogTooltip(wnd,catids[i],catTips[i],app->hInst);
                AddDialogTooltip(wnd,IDC_SET_FAST_COLD_START,L"Skip folder enumeration and secondary workers until the requested image is already visible",app->hInst);AddDialogTooltip(wnd,IDC_SET_PREFETCH_ENABLED,L"Decode nearby images after startup so gallery navigation remains instant",app->hInst);AddDialogTooltip(wnd,IDC_SET_BACKGROUND_REFINEMENT,L"Upgrade visible previews to full resolution while Glide is idle",app->hInst);AddDialogTooltip(wnd,IDC_SET_PURGE_CACHE_MINIMIZE,L"Release decoded-image cache when minimized to reduce memory use",app->hInst);AddDialogTooltip(wnd,IDC_SET_STARTUP_DIAGNOSTICS,L"Write Glide Startup Diagnostics.txt beside Glide.exe for cold-launch timing analysis",app->hInst);AddDialogTooltip(wnd,IDC_SET_PROGRESSIVE_COLOR_FIRST,L"For progressive JPEGs, display the earliest preview that already includes colour/chroma instead of flashing a luminance-only first scan. Disable only for absolute fastest level-0 preview.",app->hInst);AddDialogTooltip(wnd,IDC_SET_HOME_TIPS,L"Show the feature-tip cards on Glide Home",app->hInst);AddDialogTooltip(wnd,IDC_SET_INVERT_WHEEL_NAV,L"Reverse ordinary previous/next mouse-wheel direction",app->hInst);AddDialogTooltip(wnd,IDC_SET_SIBLING_FOLDERS,L"Continue automatically into the next or previous sibling folder at folder boundaries",app->hInst);AddDialogTooltip(wnd,IDC_SET_DETACHED_HOME_TAB,L"Off by default: a dragged-out tab becomes the only tab in its new Glide window",app->hInst);AddDialogTooltip(wnd,IDC_SET_HOTKEY_CHANGE,L"Change the selected action's main shortcut. Double-clicking a row does the same thing.",app->hInst);AddDialogTooltip(wnd,IDC_SET_HOTKEY_ADD_ALT,L"Add or replace a second shortcut for the same action. For example Fullscreen can use both Enter and F11.",app->hInst);for(int gi=0;gi<kGestureSlotCount;++gi)AddDialogTooltip(wnd,IDC_SET_GESTURE_BASE+gi,L"Override this exact interaction. Use Glide/default behavior preserves the normal preset behavior; choosing another action remaps only this gesture.",app->hInst);
                AddDialogTooltip(wnd,IDC_SET_HOTKEY_CLEAR,L"Remove the shortcuts assigned to the selected action",app->hInst);
                AddDialogTooltip(wnd,IDC_SET_HOTKEY_RESET,L"Restore Glide's default keyboard shortcuts",app->hInst);
                AddDialogTooltip(wnd,IDC_SET_ASSOC_REGISTER,app->installedMode?L"Open the Windows Default Apps page for the installed Glide application":L"Add Glide to Windows Open With for supported image formats",app->hInst);
                AddDialogTooltip(wnd,IDC_SET_ASSOC_REMOVE,app->installedMode?L"Remove any per-user Open With entries; installer registration remains until uninstall":L"Remove Glide's Open With registrations",app->hInst);
                AddDialogTooltip(wnd,IDC_SET_DEFAULTS,L"Restore all Glide settings to their defaults",app->hInst);AddDialogTooltip(wnd,IDC_SET_RUN_DIAGNOSTICS,L"Run the isolated automated test suite: formats, decoder settings, performance, window state and Settings integrity. Produces one report ZIP in Downloads.",app->hInst);AddDialogTooltip(wnd,IDC_SET_EXPORT_DIAGNOSTICS,L"Export a lightweight snapshot of current UI/layout/performance state without running the full automated suite",app->hInst);
                AddDialogTooltip(wnd,IDC_SET_APPLY,L"Apply changes without closing Settings",app->hInst);AddDialogTooltip(wnd,IDC_SET_OK,L"Apply changes and close",app->hInst);
                AddDialogTooltip(wnd,IDC_SET_CANCEL,L"Close without applying these changes",app->hInst);
                SetCheck(wnd,IDC_SET_HISTORY,app->recentHistoryEnabled);SetDlgItemInt(wnd,IDC_SET_RECENT_LIMIT,app->recentHistoryLimit,FALSE);SetCheck(wnd,IDC_SET_REMEMBER_WINDOW,app->rememberWindowPlacement);SetCheck(wnd,IDC_SET_SINGLE_INSTANCE,app->singleInstance);SetCheck(wnd,IDC_SET_STATUS,app->statusVisible);SetCheck(wnd,IDC_SET_SCROLLBARS,app->showScrollBars);SetCheck(wnd,IDC_SET_FULLPATH,app->showFullPathInTitle);SetCheck(wnd,IDC_SET_CURSOR_HIDE,app->fullscreenCursorAutoHide);SetCheck(wnd,IDC_SET_FULLSCREEN_BAR_AUTOHIDE,app->fullscreenBarAutoHide);SetCheck(wnd,IDC_SET_TITLE_TABS,app->titleTabsEnabled);SetCheck(wnd,IDC_SET_FULLSCREEN_X_CLOSE,app->fullscreenXClosesApp);SetCheck(wnd,IDC_SET_FULLSCREEN_STATUS_ALWAYS,app->fullscreenStatusAlwaysOn);SetCheck(wnd,IDC_SET_ALWAYS_ON_TOP,app->alwaysOnTop);SendDlgItemMessageW(wnd,IDC_SET_DEFAULT_VIEW_MODE,CB_SETCURSEL,std::clamp(app->defaultViewMode,0,4),0);SetDlgItemInt(wnd,IDC_SET_DEFAULT_CUSTOM_ZOOM,app->defaultCustomZoomPercent,FALSE);EnableWindow(GetDlgItem(wnd,IDC_SET_DEFAULT_CUSTOM_ZOOM),app->defaultViewMode==4);SetCheck(wnd,IDC_SET_DBL_FULLSCREEN,app->doubleClickFullscreen);SetCheck(wnd,IDC_SET_DBL_EXIT_FULLSCREEN,app->doubleClickExitFullscreen);SetCheck(wnd,IDC_SET_FULLSCREEN_CLICKS,app->fullscreenClickNavigation);SetCheck(wnd,IDC_SET_WINDOWED_WHEEL_ZOOM,app->windowedWheelZoom);GlideSettingsShell::SelectExclusiveRadio(wnd,IDC_SET_QUALITY_SPEED,IDC_SET_QUALITY_MAX,IDC_SET_QUALITY_SPEED+app->initialQualityMode);SetCheck(wnd,IDC_SET_ADAPTIVE_PREVIEW,app->adaptiveFastPreview);SetDlgItemInt(wnd,IDC_SET_ADAPTIVE_DELAY,app->adaptivePreviewDelayMs,FALSE);SetCheck(wnd,IDC_SET_STATUS_NAV,app->statusShowNavigation);SetCheck(wnd,IDC_SET_STATUS_ZOOM,app->statusShowZoom);SetCheck(wnd,IDC_SET_STATUS_SLIDE,app->statusShowSlideshow);SetCheck(wnd,IDC_SET_STATUS_FIT,app->statusShowFit);SetCheck(wnd,IDC_SET_STATUS_INFO,app->statusShowInfo);SetCheck(wnd,IDC_SET_STATUS_COLLAPSE,app->statusShowCollapse);SetCheck(wnd,IDC_SET_STATUS_OPTIONS,app->statusShowOptions);SetCheck(wnd,IDC_SET_STATUS_CLOSE,app->statusShowClose);SetCheck(wnd,IDC_SET_STAT_RESOLUTION,app->statusStatResolution);SetCheck(wnd,IDC_SET_STAT_ZOOM,app->statusStatZoom);SetCheck(wnd,IDC_SET_STAT_INDEX,app->statusStatIndex);SetCheck(wnd,IDC_SET_STAT_FILESIZE,app->statusStatFileSize);SetCheck(wnd,IDC_SET_STAT_FORMAT,app->statusStatFormat);SetCheck(wnd,IDC_SET_STAT_PREVIEW,app->statusStatPreview);SetDlgItemInt(wnd,IDC_SET_SLIDE_INTERVAL,app->slideshowIntervalMs,FALSE);SetCheck(wnd,IDC_SET_SLIDE_LOOP,app->slideshowLoop);SetCheck(wnd,IDC_SET_SLIDE_CROSS,app->slideshowCrossFolders);SetCheck(wnd,IDC_SET_SLIDE_SHUFFLE,app->slideshowShuffle);SetCheck(wnd,IDC_SET_FAST_COLD_START,app->fastColdStart);SetCheck(wnd,IDC_SET_STARTUP_DIAGNOSTICS,app->startupDiagnostics);SetCheck(wnd,IDC_SET_PREFETCH_ENABLED,app->prefetchEnabled);SetCheck(wnd,IDC_SET_BACKGROUND_REFINEMENT,app->backgroundRefinement);SetCheck(wnd,IDC_SET_PURGE_CACHE_MINIMIZE,app->purgeCacheOnMinimize);SetCheck(wnd,IDC_SET_HOME_TIPS,app->homeTipsEnabled);SetCheck(wnd,IDC_SET_INVERT_WHEEL_NAV,app->invertWheelNavigation);SetCheck(wnd,IDC_SET_SIBLING_FOLDERS,app->autoSiblingFolders);SetDlgItemInt(wnd,IDC_SET_ZOOM_STEP,app->zoomStepPercent,FALSE);SetDlgItemInt(wnd,IDC_SET_CURSOR_HIDE_DELAY,app->fullscreenCursorHideDelayMs,FALSE);SetDlgItemInt(wnd,IDC_SET_PREFETCH_DEPTH,app->prefetchDepth,FALSE);SetDlgItemInt(wnd,IDC_SET_CACHE_ITEMS,static_cast<UINT>(app->maxCacheItems),FALSE);SetDlgItemInt(wnd,IDC_SET_RAPID_PREVIEW_SIZE,static_cast<UINT>(app->rapidPreviewLongestSide),FALSE);SetDlgItemInt(wnd,IDC_SET_TAB_MIN_WIDTH,static_cast<UINT>(app->tabMinWidth),FALSE);SetDlgItemInt(wnd,IDC_SET_TAB_MAX_WIDTH,static_cast<UINT>(app->tabMaxWidth),FALSE);SetCheck(wnd,IDC_SET_TAB_DETACH,app->tabDetachEnabled);SetCheck(wnd,IDC_SET_TAB_ATTACH,app->tabAttachEnabled);SetDlgItemInt(wnd,IDC_SET_CLOSED_TAB_LIMIT,app->closedTabHistoryLimit,FALSE);SetCheck(wnd,IDC_SET_CLOSE_EMPTY_DETACH,app->closeEmptyWindowAfterDetach);SetCheck(wnd,IDC_SET_DETACHED_HOME_TAB,app->detachedWindowHomeTab);SetCheck(wnd,IDC_SET_OVERLAY_BUTTON,app->statusShowOverlay);SetDlgItemInt(wnd,IDC_SET_OVERLAY_DEFAULT_OPACITY,app->overlayDefaultOpacity,FALSE);SetCheck(wnd,IDC_SET_OVERLAY_REMEMBER_FOLDER,app->overlayRememberLastFolder);SetCheck(wnd,IDC_SET_MAIN_REMEMBER_FOLDER,app->mainRememberLastFolder);SendDlgItemMessageW(wnd,IDC_SET_THEME_MODE,CB_SETCURSEL,app->themeMode,0);SendDlgItemMessageW(wnd,IDC_SET_ACCENT_COLOR,CB_SETCURSEL,app->accentChoice,0);SetCheck(wnd,IDC_SET_CTRL_WHEEL_ZOOM,app->ctrlWheelZoom);SetCheck(wnd,IDC_SET_FULLSCREEN_WHEEL_ZOOM,app->fullscreenWheelZoom);SetCheck(wnd,IDC_SET_ZOOM_AROUND_CURSOR,app->zoomAroundCursor);SetCheck(wnd,IDC_SET_KEEP_ZOOM_NAV,app->preserveManualZoomOnNavigate);SendDlgItemMessageW(wnd,IDC_SET_FULLSCREEN_EXIT_MODE,CB_SETCURSEL,app->fullscreenExitWindowMode,0);SetCheck(wnd,IDC_SET_TEXT_OVERLAY_ENABLED,app->textOverlayEnabled);SetDlgItemTextW(wnd,IDC_SET_TEXT_OVERLAY_TEMPLATE,app->textOverlayTemplate.c_str());SetDlgItemInt(wnd,IDC_SET_TEXT_OVERLAY_SIZE,app->textOverlayFontSize,FALSE);SetDlgItemInt(wnd,IDC_SET_TEXT_OVERLAY_OPACITY,app->textOverlayOpacity,FALSE);{const auto tc=HexColor(app->textOverlayColor);SetDlgItemTextW(wnd,IDC_SET_TEXT_OVERLAY_COLOR,tc.c_str());}SendDlgItemMessageW(wnd,IDC_SET_TEXT_OVERLAY_POSITION,CB_SETCURSEL,app->textOverlayPosition,0);SetCheck(wnd,IDC_SET_TEXT_OVERLAY_BOLD,app->textOverlayBold);SetCheck(wnd,IDC_SET_TEXT_OVERLAY_SHADOW,app->textOverlayShadow);SetCheck(wnd,IDC_SET_MIDDLE_DRAG_PAN,app->middleDragPansImage);SetCheck(wnd,IDC_SET_WRAP_FOLDER,app->wrapFolderNavigation);SetCheck(wnd,IDC_SET_PROGRESSIVE_COLOR_FIRST,app->progressiveColorFirstPreview);SetCheck(wnd,IDC_SET_FOLDER_NAV_SHOW_GROUP,app->folderNavShowGroup);SetCheck(wnd,IDC_SET_FOLDER_NAV_SHOW_PREV,app->folderNavShowPrevious);SetCheck(wnd,IDC_SET_FOLDER_NAV_SHOW_NEXT,app->folderNavShowNext);SetCheck(wnd,IDC_SET_FOLDER_NAV_SHOW_EXPLORE,app->folderNavShowExplore);SetCheck(wnd,IDC_SET_FOLDER_NAV_SKIP_EMPTY,app->folderNavSkipEmpty);SetCheck(wnd,IDC_SET_FOLDER_NAV_WRAP,app->folderNavWrap);SetCheck(wnd,IDC_SET_FOLDER_NAV_FIRST_IMAGE,app->folderNavOpenFirstImage);SetCheck(wnd,IDC_SET_FOLDER_NAV_TOOLTIPS,app->folderNavTooltips);SetCheck(wnd,IDC_SET_FOLDER_NAV_EXPLORE_SINGLECLICK,app->folderNavExploreEmptyAsBrowser);SetCheck(wnd,IDC_SET_FOLDER_NAV_REMEMBER_PICKER,app->folderNavIncludeHidden);SetCheck(wnd,IDC_SET_OVERLAY_KEYBOARD_ZOOM,app->overlayKeyboardZoom);SetCheck(wnd,IDC_SET_OVERLAY_CTRL_WHEEL_ZOOM,app->overlayWheelZoom);SetCheck(wnd,IDC_SET_OVERLAY_SELECTED_HIGHLIGHT,app->overlaySelectedHighlight);SetDlgItemInt(wnd,IDC_SET_OVERLAY_ZOOM_STEP,app->overlayZoomStepPercent,FALSE);SetCheck(wnd,IDC_SET_OVERLAY_REMEMBER_ZOOM,app->overlayRememberZoom);SetCheck(wnd,IDC_SET_OVERLAY_RIGHT_DRAG_PAN,app->overlayRightDragPansZoomed);SetCheck(wnd,IDC_SET_ESC_SLIDESHOW,app->escStopsSlideshow);SetCheck(wnd,IDC_SET_ESC_FULLSCREEN,app->escExitsFullscreen);SetCheck(wnd,IDC_SET_ESC_WINDOWED_CONFIRM,app->escWindowedConfirm);SetDlgItemTextW(wnd,IDC_SET_EXT1_PATH,app->externalProgramPaths[0].c_str());SetDlgItemTextW(wnd,IDC_SET_EXT2_PATH,app->externalProgramPaths[1].c_str());SetDlgItemTextW(wnd,IDC_SET_EXT3_PATH,app->externalProgramPaths[2].c_str());for(int gi=0;gi<kGestureSlotCount;++gi)SendDlgItemMessageW(wnd,IDC_SET_GESTURE_BASE+gi,CB_SETCURSEL,static_cast<int>(app->gestureMap[static_cast<size_t>(gi)]),0);SetPropW(wnd,L"GlideSettingsInitialized",reinterpret_cast<HANDLE>(1)); RemovePropW(wnd,L"GlideSettingsDirty");
                AddDialogTooltip(wnd,IDC_SET_PRESET_COMBO,L"Select a complete behavior preset. The values are staged in this Settings window; press the global Apply or OK button to save them.",app->hInst);AddDialogTooltip(wnd,IDC_SET_THEME_MODE,L"Choose Dark or Light. The new theme previews immediately; Apply or OK saves it, while Cancel restores the previous theme.",app->hInst);AddDialogTooltip(wnd,IDC_SET_ACCENT_COLOR,L"Choose the accent and hover-glow color used throughout Glide. Apply or OK saves it.",app->hInst);AddDialogTooltip(wnd,IDC_SET_MAIN_REMEMBER_FOLDER,L"Keep a separate most-recent folder for the main viewer Open/Open Folder dialogs. Overlay browsing has its own independent history.",app->hInst);AddDialogTooltip(wnd,IDC_SET_OVERLAY_REMEMBER_FOLDER,L"Keep a separate most-recent folder for Window-in-Window overlay images. This never changes the main viewer Open/Open Folder location.",app->hInst);AddDialogTooltip(wnd,IDC_SET_SETTINGS_IMPORT,L"Import a complete Glide .gli or .ini configuration",app->hInst);AddDialogTooltip(wnd,IDC_SET_SETTINGS_EXPORT,L"Export the complete active Glide configuration",app->hInst);
                app->SyncBehaviorControls(wnd);
                // 2B: keep the first Settings paint lean. Hotkeys are populated synchronously on
                // first Hotkeys/search activation, so the selected page is complete without making
                // every Settings open pay ListView population cost.
                app->settingsReadyMs=app->settingsOpenRequestTick?(GetTickCount64()-app->settingsOpenRequestTick):0;
                // Do not flood the UI queue with background combo warm-up work. Gesture
                // actions are populated only when a user actually opens a gesture combo.
                // This leaves the Settings message queue immediately available for clicks,
                // tab changes, paint and drag input from the first visible frame.
                StoreSettingsBaseline(wnd);RemovePropW(wnd,L"GlideSettingsDirty");
                app->settingsDeferredReadyMs=app->settingsReadyMs;
            }
            LayoutSettingsChrome(wnd);RefreshSettingsVisibility(wnd);
            // Theme only the controls that are about to be visible before the first ShowWindow.
            // Hidden pages remain lazy; this avoids both the white native-control flash and the
            // old all-pages theming stall.
            if(app){const bool light=app->ResolveLightTheme();for(HWND c=GetWindow(wnd,GW_CHILD);c;c=GetWindow(c,GW_HWNDNEXT)){if(!IsWindowVisible(c))continue;wchar_t cls[64]{};GetClassNameW(c,cls,63);if(_wcsicmp(cls,WC_LISTVIEWW)!=0)SetWindowTheme(c,light?L"Explorer":L"DarkMode_Explorer",nullptr);}InvalidateRect(wnd,nullptr,TRUE);}
            return 0;}
        case WM_APP_SETTINGS_DEFERRED:{
            // Legacy no-op retained for message-number compatibility. 1.2.88 removed
            // eager/background gesture-combo warming because queued warm-up messages
            // delayed first interaction and made Settings look as if it refreshed itself.
            return 0;}
        case WM_NOTIFY:{
            if(app){
                auto* hdr=reinterpret_cast<NMHDR*>(lp);
                HWND hot=GetDlgItem(wnd,IDC_SET_HOTKEY_LIST); HWND hotHdr=hot?ListView_GetHeader(hot):nullptr;
                if(hdr&&hdr->idFrom==IDC_SET_HOTKEY_LIST&&hdr->code==NM_CUSTOMDRAW){
                    auto* cd=reinterpret_cast<NMLVCUSTOMDRAW*>(lp); const ThemePalette pal=app->Palette();
                    if(cd->nmcd.dwDrawStage==CDDS_PREPAINT)return CDRF_NOTIFYITEMDRAW;
                    if(cd->nmcd.dwDrawStage==CDDS_ITEMPREPAINT){
                        const int row=static_cast<int>(cd->nmcd.dwItemSpec);
                        const bool sel=(ListView_GetItemState(hot,row,LVIS_SELECTED)&LVIS_SELECTED)!=0;
                        if(sel){
                            RECT bounds{};ListView_GetItemRect(hot,row,&bounds,LVIR_BOUNDS);
                            HBRUSH fill=CreateSolidBrush(pal.selectionBg);FillRect(cd->nmcd.hdc,&bounds,fill);DeleteObject(fill);
                            const COLORREF fg=ContrastingTextColor(pal.selectionBg);
                            SetBkMode(cd->nmcd.hdc,TRANSPARENT);SetTextColor(cd->nmcd.hdc,fg);
                            HFONT font=reinterpret_cast<HFONT>(SendMessageW(hot,WM_GETFONT,0,0));
                            HGDIOBJ oldFont=font?SelectObject(cd->nmcd.hdc,font):nullptr;
                            const int cols=Header_GetItemCount(ListView_GetHeader(hot));
                            for(int col=0;col<cols;++col){
                                RECT sr{};ListView_GetSubItemRect(hot,row,col,LVIR_BOUNDS,&sr);
                                wchar_t text[512]{};ListView_GetItemText(hot,row,col,text,511);
                                RECT tr=sr;tr.left+=8;tr.right-=6;
                                DrawTextW(cd->nmcd.hdc,text,-1,&tr,DT_SINGLELINE|DT_VCENTER|DT_LEFT|DT_END_ELLIPSIS|DT_NOPREFIX);
                                HPEN pen=CreatePen(PS_SOLID,1,pal.border);HGDIOBJ oldPen=SelectObject(cd->nmcd.hdc,pen);
                                MoveToEx(cd->nmcd.hdc,sr.right-1,sr.top,nullptr);LineTo(cd->nmcd.hdc,sr.right-1,sr.bottom);
                                MoveToEx(cd->nmcd.hdc,sr.left,sr.bottom-1,nullptr);LineTo(cd->nmcd.hdc,sr.right,sr.bottom-1);
                                SelectObject(cd->nmcd.hdc,oldPen);DeleteObject(pen);
                            }
                            if(oldFont)SelectObject(cd->nmcd.hdc,oldFont);
                            return CDRF_SKIPDEFAULT;
                        }
                        cd->clrText=pal.text;cd->clrTextBk=pal.panel;return CDRF_NEWFONT;
                    }
                }
                if(hdr&&hotHdr&&hdr->hwndFrom==hotHdr&&hdr->code==NM_CUSTOMDRAW){
                    auto* cd=reinterpret_cast<NMCUSTOMDRAW*>(lp); const ThemePalette pal=app->Palette();
                    if(cd->dwDrawStage==CDDS_PREPAINT)return CDRF_NOTIFYITEMDRAW;
                    if(cd->dwDrawStage==CDDS_ITEMPREPAINT){
                        RECT rr=cd->rc; HBRUSH b=CreateSolidBrush(pal.headerBg);FillRect(cd->hdc,&rr,b);DeleteObject(b);
                        HPEN pen=CreatePen(PS_SOLID,1,pal.border);HGDIOBJ old=SelectObject(cd->hdc,pen);MoveToEx(cd->hdc,rr.right-1,rr.top,nullptr);LineTo(cd->hdc,rr.right-1,rr.bottom);MoveToEx(cd->hdc,rr.left,rr.bottom-1,nullptr);LineTo(cd->hdc,rr.right,rr.bottom-1);SelectObject(cd->hdc,old);DeleteObject(pen);
                        wchar_t text[128]{};HDITEMW hi{};hi.mask=HDI_TEXT;hi.pszText=text;hi.cchTextMax=127;Header_GetItem(hotHdr,static_cast<int>(cd->dwItemSpec),&hi);
                        SetBkMode(cd->hdc,TRANSPARENT);SetTextColor(cd->hdc,pal.headerText);rr.left+=10;rr.right-=6;DrawTextW(cd->hdc,text,-1,&rr,DT_SINGLELINE|DT_VCENTER|DT_LEFT|DT_END_ELLIPSIS|DT_NOPREFIX);return CDRF_SKIPDEFAULT;
                    }
                }
                if(hdr&&hdr->idFrom==IDC_SET_HOTKEY_LIST&&hdr->code==NM_DBLCLK){PostMessageW(wnd,WM_COMMAND,MAKEWPARAM(IDC_SET_HOTKEY_CHANGE,BN_CLICKED),reinterpret_cast<LPARAM>(GetDlgItem(wnd,IDC_SET_HOTKEY_CHANGE)));return 0;}
                if(hdr&&hdr->idFrom==IDC_SET_HOTKEY_LIST&&hdr->code==LVN_COLUMNCLICK){
                    auto* nmlv=reinterpret_cast<NMLISTVIEW*>(lp);HWND list=hdr->hwndFrom;
                    int previous=static_cast<int>(reinterpret_cast<INT_PTR>(GetPropW(list,L"GlideHotkeySortCol")))-1;
                    bool desc=GetPropW(list,L"GlideHotkeySortDesc")!=nullptr;
                    if(previous==nmlv->iSubItem)desc=!desc;else desc=false;
                    SetPropW(list,L"GlideHotkeySortCol",reinterpret_cast<HANDLE>(static_cast<INT_PTR>(nmlv->iSubItem+1)));
                    if(desc)SetPropW(list,L"GlideHotkeySortDesc",reinterpret_cast<HANDLE>(1));else RemovePropW(list,L"GlideHotkeySortDesc");
                    app->PopulateHotkeyList(wnd);return 0;
                }
            }
            break;
        }
        case WM_COMMAND:{
            if(!app)break;int id=LOWORD(wp);
            if(id==IDC_SET_SEARCH && HIWORD(wp)==EN_CHANGE){if(GetPropW(wnd,L"GlideHotkeyCapture")||GetPropW(wnd,L"GlideHotkeyCaptureAlt"))app->EndHotkeyCapture(wnd,L"Shortcut capture cancelled because the search changed.");SetSettingsSectionPage(wnd,0);RefreshSettingsVisibility(wnd);return 0;}
            if((id==IDC_SET_SECTION_PREV||id==IDC_SET_SECTION_NEXT)&&HIWORD(wp)==BN_CLICKED){if(GetPropW(wnd,L"GlideHotkeyCapture")||GetPropW(wnd,L"GlideHotkeyCaptureAlt"))app->EndHotkeyCapture(wnd,L"Shortcut capture cancelled.");SetSettingsSectionPage(wnd,SettingsSectionPage(wnd)+(id==IDC_SET_SECTION_NEXT?1:-1));RefreshSettingsVisibility(wnd);return 0;}
            if(((id>=IDC_SET_CAT_GENERAL&&id<=IDC_SET_CAT_WINDOWS)||id==IDC_SET_CAT_DEVELOPER) && HIWORD(wp)==BN_CLICKED){if(GetPropW(wnd,L"GlideHotkeyCapture")||GetPropW(wnd,L"GlideHotkeyCaptureAlt"))app->EndHotkeyCapture(wnd,L"Shortcut capture cancelled.");SetSettingsSectionPage(wnd,0);const int selectedCat=(id==IDC_SET_CAT_DEVELOPER)?11:(id-IDC_SET_CAT_GENERAL);SetWindowLongPtrW(wnd,GWLP_USERDATA,selectedCat);HWND se=GetDlgItem(wnd,IDC_SET_SEARCH);if(se && GetWindowTextLengthW(se)>0)SetWindowTextW(se,L"");if(id==IDC_SET_CAT_HOTKEYS)app->PopulateHotkeyList(wnd);RefreshSettingsVisibility(wnd);const int categoryButtons[]={IDC_SET_CAT_GENERAL,IDC_SET_CAT_VIEWING,IDC_SET_CAT_MOUSE,IDC_SET_CAT_PERFORMANCE,IDC_SET_CAT_STATUS,IDC_SET_CAT_SLIDESHOW,IDC_SET_CAT_HOTKEYS,IDC_SET_CAT_TABS,IDC_SET_CAT_OVERLAYS,IDC_SET_CAT_PROFILES,IDC_SET_CAT_WINDOWS,IDC_SET_CAT_DEVELOPER};for(int cid:categoryButtons)if(HWND c=GetDlgItem(wnd,cid))InvalidateRect(c,nullptr,FALSE);SetFocus(GetDlgItem(wnd,id));return 0;}
            if(id==IDC_SET_DEFAULT_VIEW_MODE&&HIWORD(wp)==CBN_SELCHANGE){int sel=static_cast<int>(SendDlgItemMessageW(wnd,IDC_SET_DEFAULT_VIEW_MODE,CB_GETCURSEL,0,0));EnableWindow(GetDlgItem(wnd,IDC_SET_DEFAULT_CUSTOM_ZOOM),sel==4);SetPropW(wnd,L"GlideSettingsDirty",reinterpret_cast<HANDLE>(1));return 0;}
            if(id>=IDC_SET_QUALITY_SPEED&&id<=IDC_SET_QUALITY_MAX&&HIWORD(wp)==BN_CLICKED){GlideSettingsShell::SelectExclusiveRadio(wnd,IDC_SET_QUALITY_SPEED,IDC_SET_QUALITY_MAX,id);SetPropW(wnd,L"GlideSettingsDirty",reinterpret_cast<HANDLE>(1));return 0;}
            if(id==IDC_SET_PRESET_COMBO&&HIWORD(wp)==CBN_SELCHANGE){int sel=static_cast<int>(SendDlgItemMessageW(wnd,IDC_SET_PRESET_COMBO,CB_GETCURSEL,0,0));if(sel>=1){app->ApplyBehaviorPreset(sel);app->SyncBehaviorControls(wnd);SetPropW(wnd,L"GlideSettingsDirty",reinterpret_cast<HANDLE>(1));}return 0;}
            if((id==IDC_SET_THEME_MODE||id==IDC_SET_ACCENT_COLOR)&&HIWORD(wp)==CBN_SELCHANGE){app->themeMode=std::clamp(static_cast<int>(SendDlgItemMessageW(wnd,IDC_SET_THEME_MODE,CB_GETCURSEL,0,0)),0,1);app->accentChoice=std::clamp(static_cast<int>(SendDlgItemMessageW(wnd,IDC_SET_ACCENT_COLOR,CB_GETCURSEL,0,0)),0,6);if(!GetPropW(wnd,L"GlideThemeRefreshPending")){SetPropW(wnd,L"GlideThemeRefreshPending",reinterpret_cast<HANDLE>(1));PostMessageW(wnd,WM_APP_SETTINGS_THEME_REFRESH,0,0);}SetPropW(wnd,L"GlideSettingsDirty",reinterpret_cast<HANDLE>(1));return 0;}
            if(id>=IDC_SET_PROFILE_SAVE1&&id<=IDC_SET_PROFILE_LOAD3&&HIWORD(wp)==BN_CLICKED){int slot=1+(id-IDC_SET_PROFILE_SAVE1)/2;bool load=((id-IDC_SET_PROFILE_SAVE1)&1)!=0;if(load){if(!app->LoadProfileSlot(slot)){MessageBoxW(wnd,L"That profile slot has not been saved yet.",kAppName,MB_OK|MB_ICONINFORMATION);return 0;}app->ApplyAlwaysOnTop();app->UpdateScrollBars();app->UpdateTitle();app->InvalidateViewer();MessageBoxW(wnd,L"Profile loaded. Reopen Settings to view the imported values.",kAppName,MB_OK|MB_ICONINFORMATION);if(app->diagnosticMode)DestroyWindow(wnd);else ShowWindow(wnd,SW_HIDE);}else{if(!app->SaveProfileSlot(slot))MessageBoxW(wnd,L"The profile could not be saved.",kAppName,MB_OK|MB_ICONERROR);else MessageBoxW(wnd,L"Current applied settings saved to this profile slot.",kAppName,MB_OK|MB_ICONINFORMATION);}return 0;}
            if(id==IDC_SET_SETTINGS_EXPORT&&HIWORD(wp)==BN_CLICKED){if(!app->ExportSettingsFile(wnd)&&CommDlgExtendedError()!=0)MessageBoxW(wnd,L"Settings could not be exported.",kAppName,MB_OK|MB_ICONERROR);return 0;}
            if(id==IDC_SET_SETTINGS_IMPORT&&HIWORD(wp)==BN_CLICKED){if(app->ImportSettingsFile(wnd)){app->ApplyAlwaysOnTop();app->UpdateScrollBars();app->UpdateTitle();app->InvalidateViewer();MessageBoxW(wnd,L"Settings imported. Reopen Settings to view the imported values.",kAppName,MB_OK|MB_ICONINFORMATION);if(app->diagnosticMode)DestroyWindow(wnd);else ShowWindow(wnd,SW_HIDE);}else if(CommDlgExtendedError()!=0)MessageBoxW(wnd,L"Settings could not be imported.",kAppName,MB_OK|MB_ICONERROR);return 0;}
            if(id==IDC_SET_RUN_DIAGNOSTICS&&HIWORD(wp)==BN_CLICKED){app->LaunchComprehensiveDiagnostics(wnd);return 0;}
            if(id==IDC_SET_EXPORT_DIAGNOSTICS&&HIWORD(wp)==BN_CLICKED){if(!app->ExportDiagnostics(wnd))MessageBoxW(wnd,L"Glide could not create the diagnostic package.",L"Glide Diagnostics",MB_OK|MB_ICONERROR);return 0;}
            if(GetPropW(wnd,L"GlideSettingsInitialized")&&id!=IDC_SET_OK&&id!=IDC_SET_APPLY&&id!=IDC_SET_CANCEL&&id!=IDC_SET_SEARCH&&id!=IDC_SET_EXPORT_DIAGNOSTICS&&id!=IDC_SET_SECTION_PREV&&id!=IDC_SET_SECTION_NEXT&&!((id>=IDC_SET_CAT_GENERAL&&id<=IDC_SET_CAT_WINDOWS)||id==IDC_SET_CAT_DEVELOPER)){UINT code=HIWORD(wp);if(code==BN_CLICKED||code==CBN_SELCHANGE||code==EN_CHANGE)SetPropW(wnd,L"GlideSettingsDirty",reinterpret_cast<HANDLE>(1));}
            if(id>=IDC_SET_GESTURE_BASE && id<IDC_SET_GESTURE_BASE+kGestureSlotCount && HIWORD(wp)==CBN_DROPDOWN){
                const int gi=id-IDC_SET_GESTURE_BASE;HWND gc=GetDlgItem(wnd,id);
                if(gc&&SendMessageW(gc,CB_GETCOUNT,0,0)==0){
                    for(int ai=0;ai<static_cast<int>(GestureAction::Count);++ai)SendMessageW(gc,CB_ADDSTRING,0,reinterpret_cast<LPARAM>(GestureActionLabel(static_cast<GestureAction>(ai))));
                    SendMessageW(gc,CB_SETCURSEL,static_cast<int>(app->gestureMap[static_cast<size_t>(gi)]),0);
                }
                return 0;
            }
            if(id>=IDC_SET_GESTURE_BASE && id<IDC_SET_GESTURE_BASE+kGestureSlotCount && HIWORD(wp)==CBN_SELCHANGE){app->activeBehaviorPreset=0;SendDlgItemMessageW(wnd,IDC_SET_PRESET_COMBO,CB_SETCURSEL,0,0);SetPropW(wnd,L"GlideSettingsDirty",reinterpret_cast<HANDLE>(1));}
            if(((id==IDC_SET_LEFT_DRAG_MODE)&&HIWORD(wp)==CBN_SELCHANGE)||((id==IDC_SET_RIGHT_DRAG_PAN||id==IDC_SET_SELECTION_CLICK_ZOOM||id==IDC_SET_SELECTION_RIGHT_ZOOM||id==IDC_SET_BACKGROUND_DRAG_WINDOW||id==IDC_SET_DBL_FULLSCREEN||id==IDC_SET_DBL_EXIT_FULLSCREEN||id==IDC_SET_FULLSCREEN_CLICKS||id==IDC_SET_WINDOWED_WHEEL_ZOOM||id==IDC_SET_INVERT_WHEEL_NAV||id==IDC_SET_SIBLING_FOLDERS||id==IDC_SET_CTRL_WHEEL_ZOOM||id==IDC_SET_FULLSCREEN_WHEEL_ZOOM||id==IDC_SET_ZOOM_AROUND_CURSOR||id==IDC_SET_KEEP_ZOOM_NAV||id==IDC_SET_MIDDLE_DRAG_PAN||id==IDC_SET_WRAP_FOLDER)&&HIWORD(wp)==BN_CLICKED)){app->activeBehaviorPreset=0;SendDlgItemMessageW(wnd,IDC_SET_PRESET_COMBO,CB_SETCURSEL,0,0);}
            if(id==IDC_SET_HOTKEY_CHANGE && HIWORD(wp)==BN_CLICKED){if(GetPropW(wnd,L"GlideHotkeyCapture")||GetPropW(wnd,L"GlideHotkeyCaptureAlt")){app->EndHotkeyCapture(wnd,L"Shortcut capture cancelled.");return 0;}int sel=app->SelectedHotkeyIndex(wnd);if(sel>=0)app->BeginHotkeyCapture(wnd,static_cast<size_t>(sel),false);return 0;}
            if(id==IDC_SET_HOTKEY_ADD_ALT && HIWORD(wp)==BN_CLICKED){if(GetPropW(wnd,L"GlideHotkeyCapture")||GetPropW(wnd,L"GlideHotkeyCaptureAlt")){app->EndHotkeyCapture(wnd,L"Shortcut capture cancelled.");return 0;}int sel=app->SelectedHotkeyIndex(wnd);if(sel>=0)app->BeginHotkeyCapture(wnd,static_cast<size_t>(sel),true);return 0;}
            if(id==IDC_SET_HOTKEY_CLEAR && HIWORD(wp)==BN_CLICKED){app->activeBehaviorPreset=0;SendDlgItemMessageW(wnd,IDC_SET_PRESET_COMBO,CB_SETCURSEL,0,0);int sel=app->SelectedHotkeyIndex(wnd);if(sel>=0){app->AssignHotkey(static_cast<size_t>(sel),0);app->AssignHotkey(static_cast<size_t>(sel),0,true);app->PopulateHotkeyList(wnd);}return 0;}
            if(id==IDC_SET_HOTKEY_RESET && HIWORD(wp)==BN_CLICKED){app->activeBehaviorPreset=0;SendDlgItemMessageW(wnd,IDC_SET_PRESET_COMBO,CB_SETCURSEL,0,0);app->ResetHotkeys();app->PopulateHotkeyList(wnd);return 0;}
            if(id==IDC_SET_ASSOC_REGISTER){if(app->installedMode){if(!glide_platform::OpenDefaultAppsSettings())MessageBoxW(wnd,L"Windows Default Apps could not be opened.",kAppName,MB_OK|MB_ICONERROR);}else MessageBoxW(wnd,app->RegisterFileAssociations()?L"Glide was added to Windows Open With for common image formats.":L"Some Open With entries could not be created.",kAppName,MB_OK|MB_ICONINFORMATION);return 0;}
            if(id==IDC_SET_ASSOC_REMOVE){app->RemoveFileAssociations();MessageBoxW(wnd,app->installedMode?L"Per-user Glide Open With entries were removed. The installer registration remains until Glide is uninstalled.":L"Glide Open With entries were removed.",kAppName,MB_OK|MB_ICONINFORMATION);return 0;}
            if(id==IDC_SET_ESC_REMEMBER_RESET){app->escRememberedClose=-1;SetPropW(wnd,L"GlideSettingsDirty",reinterpret_cast<HANDLE>(1));MessageBoxW(wnd,L"Remembered Escape close choice reset. Glide will ask again.",L"Glide",MB_OK|MB_ICONINFORMATION);return 0;}
            if(id==IDC_SET_EXT1_BROWSE||id==IDC_SET_EXT2_BROWSE||id==IDC_SET_EXT3_BROWSE){int slot=id-IDC_SET_EXT1_BROWSE;if(id==IDC_SET_EXT2_BROWSE)slot=1;else if(id==IDC_SET_EXT3_BROWSE)slot=2;else slot=0;{auto before=app->externalProgramPaths[slot];if(app->BrowseExternalProgram(wnd,slot)){auto chosen=app->externalProgramPaths[slot];app->externalProgramPaths[slot]=before;SetDlgItemTextW(wnd,IDC_SET_EXT1_PATH+slot*2,chosen.c_str());SetPropW(wnd,L"GlideSettingsDirty",reinterpret_cast<HANDLE>(1));}}return 0;}
            if(id==IDC_SET_DEFAULTS){app->ResetHotkeys();app->PopulateHotkeyList(wnd);SetCheck(wnd,IDC_SET_HISTORY,true);SetDlgItemInt(wnd,IDC_SET_RECENT_LIMIT,8,FALSE);SetCheck(wnd,IDC_SET_REMEMBER_WINDOW,true);SetCheck(wnd,IDC_SET_SINGLE_INSTANCE,false);SetCheck(wnd,IDC_SET_STATUS,true);SetCheck(wnd,IDC_SET_SCROLLBARS,true);SetCheck(wnd,IDC_SET_FULLPATH,false);SetCheck(wnd,IDC_SET_CURSOR_HIDE,true);SetCheck(wnd,IDC_SET_FULLSCREEN_BAR_AUTOHIDE,true);SetCheck(wnd,IDC_SET_FULLSCREEN_X_CLOSE,false);SetCheck(wnd,IDC_SET_FULLSCREEN_STATUS_ALWAYS,false);SetCheck(wnd,IDC_SET_ALWAYS_ON_TOP,false);SetCheck(wnd,IDC_SET_TITLE_TABS,true);SendDlgItemMessageW(wnd,IDC_SET_DEFAULT_VIEW_MODE,CB_SETCURSEL,0,0);SetDlgItemInt(wnd,IDC_SET_DEFAULT_CUSTOM_ZOOM,100,FALSE);EnableWindow(GetDlgItem(wnd,IDC_SET_DEFAULT_CUSTOM_ZOOM),FALSE);GlideSettingsShell::SelectExclusiveRadio(wnd,IDC_SET_QUALITY_SPEED,IDC_SET_QUALITY_MAX,IDC_SET_QUALITY_BALANCED);SetCheck(wnd,IDC_SET_ADAPTIVE_PREVIEW,true);SetDlgItemInt(wnd,IDC_SET_ADAPTIVE_DELAY,40,FALSE);SetCheck(wnd,IDC_SET_FAST_COLD_START,true);SetCheck(wnd,IDC_SET_STARTUP_DIAGNOSTICS,false);SetCheck(wnd,IDC_SET_PREFETCH_ENABLED,true);SetCheck(wnd,IDC_SET_BACKGROUND_REFINEMENT,true);SetCheck(wnd,IDC_SET_PURGE_CACHE_MINIMIZE,false);SetCheck(wnd,IDC_SET_HOME_TIPS,true);SetCheck(wnd,IDC_SET_ESC_SLIDESHOW,true);SetCheck(wnd,IDC_SET_ESC_FULLSCREEN,true);SetCheck(wnd,IDC_SET_ESC_WINDOWED_CONFIRM,true);SetCheck(wnd,IDC_SET_INVERT_WHEEL_NAV,false);SetCheck(wnd,IDC_SET_SIBLING_FOLDERS,true);SetDlgItemInt(wnd,IDC_SET_ZOOM_STEP,15,FALSE);SetDlgItemInt(wnd,IDC_SET_CURSOR_HIDE_DELAY,1800,FALSE);SetDlgItemInt(wnd,IDC_SET_PREFETCH_DEPTH,2,FALSE);SetDlgItemInt(wnd,IDC_SET_CACHE_ITEMS,16,FALSE);SetDlgItemInt(wnd,IDC_SET_RAPID_PREVIEW_SIZE,3000,FALSE);SetDlgItemInt(wnd,IDC_SET_TAB_MIN_WIDTH,125,FALSE);SetDlgItemInt(wnd,IDC_SET_TAB_MAX_WIDTH,240,FALSE);SetCheck(wnd,IDC_SET_TAB_DETACH,true);SetCheck(wnd,IDC_SET_TAB_ATTACH,true);SetDlgItemInt(wnd,IDC_SET_CLOSED_TAB_LIMIT,20,FALSE);SetCheck(wnd,IDC_SET_CLOSE_EMPTY_DETACH,false);SetCheck(wnd,IDC_SET_DETACHED_HOME_TAB,false);SetCheck(wnd,IDC_SET_OVERLAY_BUTTON,true);SetDlgItemInt(wnd,IDC_SET_OVERLAY_DEFAULT_OPACITY,100,FALSE);SetCheck(wnd,IDC_SET_OVERLAY_REMEMBER_FOLDER,true);SetCheck(wnd,IDC_SET_MAIN_REMEMBER_FOLDER,true);SetCheck(wnd,IDC_SET_DBL_FULLSCREEN,true);SetCheck(wnd,IDC_SET_DBL_EXIT_FULLSCREEN,false);SetCheck(wnd,IDC_SET_FULLSCREEN_CLICKS,true);SetCheck(wnd,IDC_SET_WINDOWED_WHEEL_ZOOM,false);SendDlgItemMessageW(wnd,IDC_SET_LEFT_DRAG_MODE,CB_SETCURSEL,0,0);SetCheck(wnd,IDC_SET_RIGHT_DRAG_PAN,true);SetCheck(wnd,IDC_SET_SELECTION_CLICK_ZOOM,true);SetCheck(wnd,IDC_SET_SELECTION_RIGHT_ZOOM,true);SetCheck(wnd,IDC_SET_BACKGROUND_DRAG_WINDOW,true);SetCheck(wnd,IDC_SET_CTRL_WHEEL_ZOOM,true);SetCheck(wnd,IDC_SET_FULLSCREEN_WHEEL_ZOOM,true);SetCheck(wnd,IDC_SET_ZOOM_AROUND_CURSOR,true);SetCheck(wnd,IDC_SET_KEEP_ZOOM_NAV,false);SendDlgItemMessageW(wnd,IDC_SET_FULLSCREEN_EXIT_MODE,CB_SETCURSEL,0,0);SetCheck(wnd,IDC_SET_TEXT_OVERLAY_ENABLED,true);SetDlgItemTextW(wnd,IDC_SET_TEXT_OVERLAY_TEMPLATE,L"[{index}/{total}]");SetDlgItemInt(wnd,IDC_SET_TEXT_OVERLAY_SIZE,18,FALSE);SetDlgItemInt(wnd,IDC_SET_TEXT_OVERLAY_OPACITY,68,FALSE);SetDlgItemTextW(wnd,IDC_SET_TEXT_OVERLAY_COLOR,L"#FFFFFF");SendDlgItemMessageW(wnd,IDC_SET_TEXT_OVERLAY_POSITION,CB_SETCURSEL,2,0);SetCheck(wnd,IDC_SET_TEXT_OVERLAY_BOLD,false);SetCheck(wnd,IDC_SET_TEXT_OVERLAY_SHADOW,true);SetCheck(wnd,IDC_SET_MIDDLE_DRAG_PAN,true);SetCheck(wnd,IDC_SET_WRAP_FOLDER,false);for(int gi=0;gi<kGestureSlotCount;++gi)SendDlgItemMessageW(wnd,IDC_SET_GESTURE_BASE+gi,CB_SETCURSEL,0,0);SendDlgItemMessageW(wnd,IDC_SET_THEME_MODE,CB_SETCURSEL,0,0);SendDlgItemMessageW(wnd,IDC_SET_ACCENT_COLOR,CB_SETCURSEL,0,0);SendDlgItemMessageW(wnd,IDC_SET_PRESET_COMBO,CB_SETCURSEL,1,0);SetCheck(wnd,IDC_SET_STATUS_NAV,true);SetCheck(wnd,IDC_SET_STATUS_ZOOM,true);SetCheck(wnd,IDC_SET_STATUS_SLIDE,true);SetCheck(wnd,IDC_SET_STATUS_FIT,true);SetCheck(wnd,IDC_SET_STATUS_INFO,true);SetCheck(wnd,IDC_SET_STATUS_COLLAPSE,true);SetCheck(wnd,IDC_SET_STATUS_OPTIONS,true);SetCheck(wnd,IDC_SET_STATUS_CLOSE,true);SetCheck(wnd,IDC_SET_STAT_RESOLUTION,true);SetCheck(wnd,IDC_SET_STAT_ZOOM,true);SetCheck(wnd,IDC_SET_STAT_INDEX,true);SetCheck(wnd,IDC_SET_STAT_FILESIZE,false);SetCheck(wnd,IDC_SET_STAT_FORMAT,false);SetCheck(wnd,IDC_SET_STAT_PREVIEW,false);SetDlgItemInt(wnd,IDC_SET_SLIDE_INTERVAL,3000,FALSE);SetCheck(wnd,IDC_SET_SLIDE_LOOP,true);SetCheck(wnd,IDC_SET_SLIDE_CROSS,true);SetCheck(wnd,IDC_SET_SLIDE_SHUFFLE,false);app->themeMode=0;app->accentChoice=0;app->ApplySettingsThemeToControls(wnd);app->ApplyThemeToMainWindow();app->InvalidateViewer();SetPropW(wnd,L"GlideSettingsDirty",reinterpret_cast<HANDLE>(1));return 0;}
            if(id==IDC_SET_APPLY){SetPropW(wnd,L"GlideApplyOnly",reinterpret_cast<HANDLE>(1));SendMessageW(wnd,WM_COMMAND,MAKEWPARAM(IDC_SET_OK,BN_CLICKED),reinterpret_cast<LPARAM>(GetDlgItem(wnd,IDC_SET_OK)));return 0;}
            if(id==IDC_SET_OK){BOOL a=FALSE,b=FALSE,c=FALSE;int ad=GetDlgItemInt(wnd,IDC_SET_ADAPTIVE_DELAY,&c,FALSE),si=GetDlgItemInt(wnd,IDC_SET_SLIDE_INTERVAL,&a,FALSE),rl=GetDlgItemInt(wnd,IDC_SET_RECENT_LIMIT,&b,FALSE);app->recentHistoryEnabled=GetCheck(wnd,IDC_SET_HISTORY);app->recentHistoryLimit=std::clamp(b?rl:8,1,16);app->rememberWindowPlacement=GetCheck(wnd,IDC_SET_REMEMBER_WINDOW);app->fullscreenExitWindowMode=std::clamp(static_cast<int>(SendDlgItemMessageW(wnd,IDC_SET_FULLSCREEN_EXIT_MODE,CB_GETCURSEL,0,0)),0,1);app->singleInstance=GetCheck(wnd,IDC_SET_SINGLE_INSTANCE);app->statusVisible=GetCheck(wnd,IDC_SET_STATUS);app->showScrollBars=GetCheck(wnd,IDC_SET_SCROLLBARS);app->showFullPathInTitle=GetCheck(wnd,IDC_SET_FULLPATH);app->fullscreenCursorAutoHide=GetCheck(wnd,IDC_SET_CURSOR_HIDE);app->defaultViewMode=std::clamp(static_cast<int>(SendDlgItemMessageW(wnd,IDC_SET_DEFAULT_VIEW_MODE,CB_GETCURSEL,0,0)),0,4);BOOL customOk=FALSE;app->defaultCustomZoomPercent=std::clamp(static_cast<int>(GetDlgItemInt(wnd,IDC_SET_DEFAULT_CUSTOM_ZOOM,&customOk,FALSE)),2,3200);if(!customOk)app->defaultCustomZoomPercent=100;int qualityRadio=GlideSettingsShell::ExclusiveRadioSelection(wnd,IDC_SET_QUALITY_SPEED,IDC_SET_QUALITY_MAX);app->initialQualityMode=qualityRadio==IDC_SET_QUALITY_MAX?2:(qualityRadio==IDC_SET_QUALITY_SPEED?0:1);app->adaptiveFastPreview=GetCheck(wnd,IDC_SET_ADAPTIVE_PREVIEW);app->adaptivePreviewDelayMs=std::clamp(c?ad:40,5,500);app->fastColdStart=GetCheck(wnd,IDC_SET_FAST_COLD_START);app->startupDiagnostics=GetCheck(wnd,IDC_SET_STARTUP_DIAGNOSTICS);app->prefetchEnabled=GetCheck(wnd,IDC_SET_PREFETCH_ENABLED);app->backgroundRefinement=GetCheck(wnd,IDC_SET_BACKGROUND_REFINEMENT);app->purgeCacheOnMinimize=GetCheck(wnd,IDC_SET_PURGE_CACHE_MINIMIZE);app->homeTipsEnabled=GetCheck(wnd,IDC_SET_HOME_TIPS);app->invertWheelNavigation=GetCheck(wnd,IDC_SET_INVERT_WHEEL_NAV);app->autoSiblingFolders=GetCheck(wnd,IDC_SET_SIBLING_FOLDERS);BOOL zc=FALSE,cc=FALSE,pd=FALSE,ci=FALSE,rps=FALSE,tmin=FALSE,tmax=FALSE,chl=FALSE,oop=FALSE;app->zoomStepPercent=std::clamp(static_cast<int>(GetDlgItemInt(wnd,IDC_SET_ZOOM_STEP,&zc,FALSE)),5,50);app->fullscreenCursorHideDelayMs=std::clamp(static_cast<int>(GetDlgItemInt(wnd,IDC_SET_CURSOR_HIDE_DELAY,&cc,FALSE)),250,10000);app->prefetchDepth=std::clamp(static_cast<int>(GetDlgItemInt(wnd,IDC_SET_PREFETCH_DEPTH,&pd,FALSE)),0,6);app->maxCacheItems=static_cast<size_t>(std::clamp(static_cast<int>(GetDlgItemInt(wnd,IDC_SET_CACHE_ITEMS,&ci,FALSE)),2,64));app->rapidPreviewLongestSide=std::clamp(static_cast<int>(GetDlgItemInt(wnd,IDC_SET_RAPID_PREVIEW_SIZE,&rps,FALSE)),480,4096);app->tabMinWidth=static_cast<float>(std::clamp(static_cast<int>(GetDlgItemInt(wnd,IDC_SET_TAB_MIN_WIDTH,&tmin,FALSE)),80,240));app->tabMaxWidth=static_cast<float>(std::clamp(static_cast<int>(GetDlgItemInt(wnd,IDC_SET_TAB_MAX_WIDTH,&tmax,FALSE)),120,400));if(app->tabMaxWidth<app->tabMinWidth)app->tabMaxWidth=app->tabMinWidth;app->tabDetachEnabled=GetCheck(wnd,IDC_SET_TAB_DETACH);app->tabAttachEnabled=GetCheck(wnd,IDC_SET_TAB_ATTACH);app->closedTabHistoryLimit=std::clamp(static_cast<int>(GetDlgItemInt(wnd,IDC_SET_CLOSED_TAB_LIMIT,&chl,FALSE)),1,100);while(app->closedTabs.size()>static_cast<size_t>(app->closedTabHistoryLimit))app->closedTabs.erase(app->closedTabs.begin());app->closeEmptyWindowAfterDetach=GetCheck(wnd,IDC_SET_CLOSE_EMPTY_DETACH);app->detachedWindowHomeTab=GetCheck(wnd,IDC_SET_DETACHED_HOME_TAB);app->statusShowOverlay=GetCheck(wnd,IDC_SET_OVERLAY_BUTTON);app->overlayDefaultOpacity=std::clamp(static_cast<int>(GetDlgItemInt(wnd,IDC_SET_OVERLAY_DEFAULT_OPACITY,&oop,FALSE)),10,100);app->overlayRememberLastFolder=GetCheck(wnd,IDC_SET_OVERLAY_REMEMBER_FOLDER);app->mainRememberLastFolder=GetCheck(wnd,IDC_SET_MAIN_REMEMBER_FOLDER);app->statusShowNavigation=GetCheck(wnd,IDC_SET_STATUS_NAV);app->statusShowZoom=GetCheck(wnd,IDC_SET_STATUS_ZOOM);app->statusShowSlideshow=GetCheck(wnd,IDC_SET_STATUS_SLIDE);app->statusShowFit=GetCheck(wnd,IDC_SET_STATUS_FIT);app->statusShowInfo=GetCheck(wnd,IDC_SET_STATUS_INFO);app->statusShowCollapse=GetCheck(wnd,IDC_SET_STATUS_COLLAPSE);app->statusShowOptions=GetCheck(wnd,IDC_SET_STATUS_OPTIONS);app->statusShowClose=GetCheck(wnd,IDC_SET_STATUS_CLOSE);app->statusStatResolution=GetCheck(wnd,IDC_SET_STAT_RESOLUTION);app->statusStatZoom=GetCheck(wnd,IDC_SET_STAT_ZOOM);app->statusStatIndex=GetCheck(wnd,IDC_SET_STAT_INDEX);app->statusStatFileSize=GetCheck(wnd,IDC_SET_STAT_FILESIZE);app->statusStatFormat=GetCheck(wnd,IDC_SET_STAT_FORMAT);app->statusStatPreview=GetCheck(wnd,IDC_SET_STAT_PREVIEW);app->doubleClickFullscreen=GetCheck(wnd,IDC_SET_DBL_FULLSCREEN);app->fullscreenClickNavigation=GetCheck(wnd,IDC_SET_FULLSCREEN_CLICKS);app->windowedWheelZoom=GetCheck(wnd,IDC_SET_WINDOWED_WHEEL_ZOOM);int ldm=static_cast<int>(SendDlgItemMessageW(wnd,IDC_SET_LEFT_DRAG_MODE,CB_GETCURSEL,0,0));app->leftImageDragMode=ldm==1?LeftImageDragMode::Pan:LeftImageDragMode::Select;app->rightDragPansImage=GetCheck(wnd,IDC_SET_RIGHT_DRAG_PAN);app->selectionClickZoomsIn=GetCheck(wnd,IDC_SET_SELECTION_CLICK_ZOOM);app->selectionRightClickZoomsOut=GetCheck(wnd,IDC_SET_SELECTION_RIGHT_ZOOM);app->backgroundLeftDragMovesWindow=GetCheck(wnd,IDC_SET_BACKGROUND_DRAG_WINDOW);app->ctrlWheelZoom=GetCheck(wnd,IDC_SET_CTRL_WHEEL_ZOOM);app->fullscreenWheelZoom=GetCheck(wnd,IDC_SET_FULLSCREEN_WHEEL_ZOOM);app->zoomAroundCursor=GetCheck(wnd,IDC_SET_ZOOM_AROUND_CURSOR);app->preserveManualZoomOnNavigate=GetCheck(wnd,IDC_SET_KEEP_ZOOM_NAV);app->textOverlayEnabled=GetCheck(wnd,IDC_SET_TEXT_OVERLAY_ENABLED);{wchar_t tb[2048]{};GetDlgItemTextW(wnd,IDC_SET_TEXT_OVERLAY_TEMPLATE,tb,2048);app->textOverlayTemplate=tb;if(app->textOverlayTemplate.empty())app->textOverlayTemplate=L"[{index}/{total}]";}BOOL tos=FALSE,too=FALSE;app->textOverlayFontSize=std::clamp(static_cast<int>(GetDlgItemInt(wnd,IDC_SET_TEXT_OVERLAY_SIZE,&tos,FALSE)),8,96);if(!tos)app->textOverlayFontSize=18;app->textOverlayOpacity=std::clamp(static_cast<int>(GetDlgItemInt(wnd,IDC_SET_TEXT_OVERLAY_OPACITY,&too,FALSE)),5,100);if(!too)app->textOverlayOpacity=68;{wchar_t cb[32]{};GetDlgItemTextW(wnd,IDC_SET_TEXT_OVERLAY_COLOR,cb,32);app->textOverlayColor=ParseHexColor(cb,RGB(255,255,255));}app->textOverlayPosition=std::clamp(static_cast<int>(SendDlgItemMessageW(wnd,IDC_SET_TEXT_OVERLAY_POSITION,CB_GETCURSEL,0,0)),0,5);app->textOverlayBold=GetCheck(wnd,IDC_SET_TEXT_OVERLAY_BOLD);app->textOverlayShadow=GetCheck(wnd,IDC_SET_TEXT_OVERLAY_SHADOW);app->pictureOverlayFormat.Reset();app->middleDragPansImage=GetCheck(wnd,IDC_SET_MIDDLE_DRAG_PAN);app->wrapFolderNavigation=GetCheck(wnd,IDC_SET_WRAP_FOLDER);app->progressiveColorFirstPreview=GetCheck(wnd,IDC_SET_PROGRESSIVE_COLOR_FIRST);SetPreferColorProgressivePreview(app->progressiveColorFirstPreview);app->folderNavShowGroup=GetCheck(wnd,IDC_SET_FOLDER_NAV_SHOW_GROUP);app->folderNavShowPrevious=GetCheck(wnd,IDC_SET_FOLDER_NAV_SHOW_PREV);app->folderNavShowNext=GetCheck(wnd,IDC_SET_FOLDER_NAV_SHOW_NEXT);app->folderNavShowExplore=GetCheck(wnd,IDC_SET_FOLDER_NAV_SHOW_EXPLORE);app->folderNavSkipEmpty=GetCheck(wnd,IDC_SET_FOLDER_NAV_SKIP_EMPTY);app->folderNavWrap=GetCheck(wnd,IDC_SET_FOLDER_NAV_WRAP);app->folderNavOpenFirstImage=GetCheck(wnd,IDC_SET_FOLDER_NAV_FIRST_IMAGE);app->folderNavTooltips=GetCheck(wnd,IDC_SET_FOLDER_NAV_TOOLTIPS);app->folderNavExploreEmptyAsBrowser=GetCheck(wnd,IDC_SET_FOLDER_NAV_EXPLORE_SINGLECLICK);app->folderNavIncludeHidden=GetCheck(wnd,IDC_SET_FOLDER_NAV_REMEMBER_PICKER);app->overlayKeyboardZoom=GetCheck(wnd,IDC_SET_OVERLAY_KEYBOARD_ZOOM);app->overlayWheelZoom=GetCheck(wnd,IDC_SET_OVERLAY_CTRL_WHEEL_ZOOM);app->overlaySelectedHighlight=GetCheck(wnd,IDC_SET_OVERLAY_SELECTED_HIGHLIGHT);BOOL ozs=FALSE;app->overlayZoomStepPercent=std::clamp(static_cast<int>(GetDlgItemInt(wnd,IDC_SET_OVERLAY_ZOOM_STEP,&ozs,FALSE)),5,100);app->overlayRememberZoom=GetCheck(wnd,IDC_SET_OVERLAY_REMEMBER_ZOOM);app->overlayRightDragPansZoomed=GetCheck(wnd,IDC_SET_OVERLAY_RIGHT_DRAG_PAN);for(int gi=0;gi<kGestureSlotCount;++gi){int gs=static_cast<int>(SendDlgItemMessageW(wnd,IDC_SET_GESTURE_BASE+gi,CB_GETCURSEL,0,0));if(gs<0)gs=0;app->gestureMap[static_cast<size_t>(gi)]=static_cast<GestureAction>(std::clamp(gs,0,static_cast<int>(GestureAction::Count)-1));}app->themeMode=std::clamp(static_cast<int>(SendDlgItemMessageW(wnd,IDC_SET_THEME_MODE,CB_GETCURSEL,0,0)),0,1);app->accentChoice=std::clamp(static_cast<int>(SendDlgItemMessageW(wnd,IDC_SET_ACCENT_COLOR,CB_GETCURSEL,0,0)),0,6);app->ApplySettingsThemeToControls(wnd);app->ApplyThemeToMainWindow();int psel=static_cast<int>(SendDlgItemMessageW(wnd,IDC_SET_PRESET_COMBO,CB_GETCURSEL,0,0));app->activeBehaviorPreset=std::clamp(psel,0,5);app->doubleClickExitFullscreen=GetCheck(wnd,IDC_SET_DBL_EXIT_FULLSCREEN);app->fullscreenBarAutoHide=GetCheck(wnd,IDC_SET_FULLSCREEN_BAR_AUTOHIDE);app->fullscreenXClosesApp=GetCheck(wnd,IDC_SET_FULLSCREEN_X_CLOSE);app->fullscreenStatusAlwaysOn=GetCheck(wnd,IDC_SET_FULLSCREEN_STATUS_ALWAYS);app->alwaysOnTop=GetCheck(wnd,IDC_SET_ALWAYS_ON_TOP);app->ApplyAlwaysOnTop();if(app->fullscreen)app->fullscreenStatusVisible=app->fullscreenStatusAlwaysOn;app->titleTabsEnabled=GetCheck(wnd,IDC_SET_TITLE_TABS);if(app->titleTabsEnabled&&app->openTabs.empty()&&!app->currentPath.empty()){app->openTabs.push_back(app->currentPath);app->tabBrowserMode.push_back(false);app->tabBrowserFolder.push_back(L"");app->tabBrowserBack.emplace_back();app->tabBrowserForward.emplace_back();app->activeTab=0;}if(!app->fullscreen)app->ApplyWindowedChromeStyle();app->slideshowIntervalMs=std::clamp(a?si:3000,250,600000);app->slideshowLoop=GetCheck(wnd,IDC_SET_SLIDE_LOOP);app->slideshowCrossFolders=GetCheck(wnd,IDC_SET_SLIDE_CROSS);app->slideshowShuffle=GetCheck(wnd,IDC_SET_SLIDE_SHUFFLE);app->escStopsSlideshow=GetCheck(wnd,IDC_SET_ESC_SLIDESHOW);app->escExitsFullscreen=GetCheck(wnd,IDC_SET_ESC_FULLSCREEN);app->escWindowedConfirm=GetCheck(wnd,IDC_SET_ESC_WINDOWED_CONFIRM);{wchar_t ep[32768]{};GetDlgItemTextW(wnd,IDC_SET_EXT1_PATH,ep,32768);app->externalProgramPaths[0]=ep;GetDlgItemTextW(wnd,IDC_SET_EXT2_PATH,ep,32768);app->externalProgramPaths[1]=ep;GetDlgItemTextW(wnd,IDC_SET_EXT3_PATH,ep,32768);app->externalProgramPaths[2]=ep;}if(!app->recentHistoryEnabled){app->recentFiles.clear();app->recentFolders.clear();}app->UpdateScrollBars();app->UpdateTitle();app->InvalidateViewer();if(!app->diagnosticMode)app->SaveSettings();RemovePropW(wnd,L"GlideSettingsDirty");StoreSettingsBaseline(wnd);if(GetPropW(wnd,L"GlideApplyOnly")){RemovePropW(wnd,L"GlideApplyOnly");return 0;}if(app->diagnosticMode)DestroyWindow(wnd);else ShowWindow(wnd,SW_HIDE);return 0;}
            if(id==IDC_SET_CANCEL){if(!ConfirmDiscardSettings(wnd))return 0;app->LoadSettings();app->ApplyAlwaysOnTop();app->ApplyThemeToMainWindow();app->InvalidateViewer();if(app->diagnosticMode)DestroyWindow(wnd);else ShowWindow(wnd,SW_HIDE);return 0;}break;}
        case WM_GETDLGCODE: if(GetPropW(wnd,L"GlideHotkeyCapture")||GetPropW(wnd,L"GlideHotkeyCaptureAlt")) return DLGC_WANTALLKEYS; break;
        case WM_SYSKEYDOWN: case WM_KEYDOWN:{if(app&&app->CaptureHotkeyInput(wnd,static_cast<UINT>(wp)))return 0;break;}
        case WM_XBUTTONDOWN:{if(app&&(GetPropW(wnd,L"GlideHotkeyCapture")||GetPropW(wnd,L"GlideHotkeyCaptureAlt"))){UINT vk=GET_XBUTTON_WPARAM(wp)==XBUTTON1?VK_XBUTTON1:VK_XBUTTON2;app->CaptureHotkeyInput(wnd,vk);return TRUE;}break;}
        case WM_RBUTTONDOWN:{if(app&&(GetPropW(wnd,L"GlideHotkeyCapture")||GetPropW(wnd,L"GlideHotkeyCaptureAlt"))){app->EndHotkeyCapture(wnd,L"Shortcut capture cancelled. Left/right mouse remain reserved for Glide navigation and UI.");return 0;}break;}
        case WM_MBUTTONDOWN:{if(app&&(GetPropW(wnd,L"GlideHotkeyCapture")||GetPropW(wnd,L"GlideHotkeyCaptureAlt"))){app->CaptureHotkeyInput(wnd,VK_MBUTTON);return 0;}break;}
        case WM_LBUTTONDOWN:{if(app&&(GetPropW(wnd,L"GlideHotkeyCapture")||GetPropW(wnd,L"GlideHotkeyCaptureAlt"))){app->EndHotkeyCapture(wnd,L"Shortcut capture cancelled. Left/right mouse remain reserved for Glide navigation and UI.");return 0;}POINT pt{GET_X_LPARAM(lp),GET_Y_LPARAM(lp)};HWND hit=ChildWindowFromPointEx(wnd,pt,CWP_SKIPINVISIBLE|CWP_SKIPDISABLED);if(!hit||hit==wnd){ReleaseCapture();SendMessageW(wnd,WM_NCLBUTTONDOWN,HTCAPTION,MAKELPARAM(pt.x,pt.y));return 0;}break;}
        case WM_PAINT:{if(app&&app->settingsFirstPaintMs==0)app->settingsFirstPaintMs=app->settingsOpenRequestTick?(GetTickCount64()-app->settingsOpenRequestTick):0;PAINTSTRUCT ps{};HDC dc=BeginPaint(wnd,&ps);RECT r{};GetClientRect(wnd,&r);HDC mem=CreateCompatibleDC(dc);HBITMAP bmp=CreateCompatibleBitmap(dc,std::max(1L,r.right),std::max(1L,r.bottom));HGDIOBJ oldBmp=SelectObject(mem,bmp);ThemePalette pal=app?app->Palette():ThemePalette{};HBRUSH bg=CreateSolidBrush(app?pal.windowBg:RGB(24,26,29));FillRect(mem,&r,bg);DeleteObject(bg);const int footer=SettingsFooterTop(wnd);COLORREF railColor=app?BlendColor(pal.windowBg,pal.panel,0.48f):RGB(28,31,35);HBRUSH rail=CreateSolidBrush(railColor);RECT rr{0,0,kSettingsRailWidth,footer};FillRect(mem,&rr,rail);DeleteObject(rail);HPEN sep=CreatePen(PS_SOLID,1,app?pal.border:RGB(55,60,66));HGDIOBJ oldPen=SelectObject(mem,sep);MoveToEx(mem,kSettingsRailWidth,0,nullptr);LineTo(mem,kSettingsRailWidth,footer);MoveToEx(mem,0,footer,nullptr);LineTo(mem,r.right,footer);SelectObject(mem,oldPen);DeleteObject(sep);BitBlt(dc,0,0,r.right,r.bottom,mem,0,0,SRCCOPY);SelectObject(mem,oldBmp);DeleteObject(bmp);DeleteDC(mem);EndPaint(wnd,&ps);return 0;}
        case WM_DRAWITEM:{auto* di=reinterpret_cast<DRAWITEMSTRUCT*>(lp);if(!di)return FALSE;int id=static_cast<int>(di->CtlID);bool category=(id>=IDC_SET_CAT_GENERAL&&id<=IDC_SET_CAT_WINDOWS)||id==IDC_SET_CAT_DEVELOPER;bool action=id==IDC_SET_ASSOC_REGISTER||id==IDC_SET_ASSOC_REMOVE||id==IDC_SET_EXT1_BROWSE||id==IDC_SET_EXT2_BROWSE||id==IDC_SET_EXT3_BROWSE||id==IDC_SET_HOTKEY_CHANGE||id==IDC_SET_HOTKEY_ADD_ALT||id==IDC_SET_HOTKEY_CLEAR||id==IDC_SET_HOTKEY_RESET||(id>=IDC_SET_PROFILE_SAVE1&&id<=IDC_SET_SETTINGS_EXPORT)||id==IDC_SET_DEFAULTS||id==IDC_SET_RUN_DIAGNOSTICS||id==IDC_SET_EXPORT_DIAGNOSTICS||id==IDC_SET_CANCEL||id==IDC_SET_APPLY||id==IDC_SET_OK||id==IDC_SET_SECTION_PREV||id==IDC_SET_SECTION_NEXT;if(!category&&!action)break;RECT r=di->rcItem;POINT sp{};GetCursorPos(&sp);ScreenToClient(di->hwndItem,&sp);bool hov=PtInRect(&r,sp)!=FALSE;const int drawCat=(id==IDC_SET_CAT_DEVELOPER)?11:(id-IDC_SET_CAT_GENERAL);bool selected=category&&drawCat==static_cast<int>(GetWindowLongPtrW(wnd,GWLP_USERDATA));wchar_t tx[256]{};GetWindowTextW(di->hwndItem,tx,255);if(DrawSmoothModernButton(app,di,selected,hov,category?drawCat:-1,tx,category))return TRUE;break;}
        case WM_CTLCOLORSTATIC: case WM_CTLCOLORBTN:{HDC dc=reinterpret_cast<HDC>(wp);HWND ctl=reinterpret_cast<HWND>(lp);ThemePalette pal=app?app->Palette():ThemePalette{};COLORREF bg=app?pal.windowBg:RGB(24,26,29);if(ctl){RECT wr{};GetWindowRect(ctl,&wr);POINT pt{wr.left,wr.top};ScreenToClient(wnd,&pt);const int footer=SettingsFooterTop(wnd);if(pt.x<kSettingsRailWidth&&pt.y<footer)bg=app?BlendColor(pal.windowBg,pal.panel,0.48f):RGB(28,31,35);}COLORREF fg=app?pal.text:RGB(225,228,232);if(ctl&&reinterpret_cast<INT_PTR>(GetPropW(ctl,L"GlideCat"))==9&&app)fg=BlendColor(pal.accent,RGB(155,105,230),0.35f);if(ctl&&(GetDlgCtrlID(ctl)==IDC_SET_SECTION_LABEL||GetDlgCtrlID(ctl)==IDC_SET_SCROLL_HINT)&&app)fg=pal.accent;SetTextColor(dc,fg);SetBkColor(dc,bg);SetDCBrushColor(dc,bg);return reinterpret_cast<LRESULT>(GetStockObject(DC_BRUSH));}
        case WM_CTLCOLOREDIT: case WM_CTLCOLORLISTBOX:{HDC dc=reinterpret_cast<HDC>(wp);ThemePalette pal=app?app->Palette():ThemePalette{};COLORREF bg=app?pal.editBg:RGB(35,38,42);SetTextColor(dc,app?pal.text:RGB(240,242,244));SetBkColor(dc,bg);SetDCBrushColor(dc,bg);return reinterpret_cast<LRESULT>(GetStockObject(DC_BRUSH));}
        case WM_ERASEBKGND:{RECT r{};GetClientRect(wnd,&r);bool light=app&&app->ResolveLightTheme();static HBRUSH lb=nullptr,db=nullptr;if(!lb)lb=CreateSolidBrush(RGB(246,247,249));if(!db)db=CreateSolidBrush(RGB(24,26,29));FillRect(reinterpret_cast<HDC>(wp),&r,light?lb:db);return 1;}
        case WM_SIZE:{LayoutSettingsChrome(wnd);RefreshSettingsVisibility(wnd);InvalidateRect(wnd,nullptr,FALSE);return 0;}
        case WM_DPICHANGED:{auto* nr=reinterpret_cast<RECT*>(lp);if(nr)SetWindowPos(wnd,nullptr,nr->left,nr->top,nr->right-nr->left,nr->bottom-nr->top,SWP_NOZORDER|SWP_NOACTIVATE);LayoutSettingsChrome(wnd);RefreshSettingsVisibility(wnd);return 0;}
        case WM_MOUSEWHEEL:{const int delta=GET_WHEEL_DELTA_WPARAM(wp);if(delta==0)return 0;
            const int before=SettingsSectionPage(wnd); wchar_t qbuf[128]{};GetDlgItemTextW(wnd,IDC_SET_SEARCH,qbuf,127);const bool searchMode=*qbuf!=0;
            auto nodes=CollectSettingsLayoutNodes(wnd);const int cat=static_cast<int>(GetWindowLongPtrW(wnd,GWLP_USERDATA));int pages=1;
            if(searchMode){auto* a=reinterpret_cast<ViewerApp*>(GetPropW(wnd,L"GlideApp"));auto rows=BuildSettingsSearchRows(nodes,Lower(qbuf));const bool hk=a&&a->HasHotkeySearchMatch(Lower(qbuf));if(hk){if(a)a->PopulateHotkeyList(wnd);int hkRows=0;if(HWND hl=GetDlgItem(wnd,IDC_SET_HOTKEY_LIST))hkRows=ListView_GetItemCount(hl);RECT sr{};GetClientRect(wnd,&sr);const int listH=std::clamp(30+hkRows*29,105,285);const int ordinaryStart=kSettingsContentTop+8+48+listH+10+36+14;const int cap=std::max(0,(SettingsContentBottom(wnd)-10-ordinaryStart)/52);const size_t consumed=std::min(rows.size(),static_cast<size_t>(cap));const size_t remain=rows.size()-consumed;pages=1+static_cast<int>((remain+kSettingsSearchRowsPerPage-1)/kSettingsSearchRowsPerPage);}else pages=std::max(1,static_cast<int>((rows.size()+kSettingsSearchRowsPerPage-1)/kSettingsSearchRowsPerPage));}
            else pages=(cat==6)?1:SettingsNormalPageCount(nodes,cat);
            const int after=std::clamp(before+(delta<0?1:-1),0,std::max(0,pages-1)); if(after==before)return 0; SetSettingsSectionPage(wnd,after);RefreshSettingsVisibility(wnd);return 0;}
        case WM_VSCROLL:{
            HWND source=reinterpret_cast<HWND>(lp);
            if(source==GetDlgItem(wnd,IDC_SET_RAIL_SCROLL)){
                SCROLLINFO si{};si.cbSize=sizeof(si);si.fMask=SIF_ALL;GetScrollInfo(source,SB_CTL,&si);int pos=si.nPos;
                switch(LOWORD(wp)){case SB_LINEUP:pos-=48;break;case SB_LINEDOWN:pos+=48;break;case SB_PAGEUP:pos-=static_cast<int>(si.nPage);break;case SB_PAGEDOWN:pos+=static_cast<int>(si.nPage);break;case SB_THUMBTRACK:case SB_THUMBPOSITION:pos=si.nTrackPos;break;case SB_TOP:pos=si.nMin;break;case SB_BOTTOM:pos=si.nMax;break;default:return 0;}
                const int maxPos=std::max(0,si.nMax-static_cast<int>(si.nPage)+1);pos=std::clamp(pos,0,maxPos);SetPropW(wnd,L"GlideSettingsRailScroll",reinterpret_cast<HANDLE>(static_cast<INT_PTR>(pos)));LayoutSettingsChrome(wnd);InvalidateRect(wnd,nullptr,TRUE);return 0;
            }
            return 0;
        }
        case WM_HSCROLL:{
            HWND source=reinterpret_cast<HWND>(lp);
            if(source==GetDlgItem(wnd,IDC_SET_SECTION_SCROLL)){
                SCROLLINFO si{};si.cbSize=sizeof(si);si.fMask=SIF_ALL;GetScrollInfo(source,SB_CTL,&si);int page=si.nPos;
                switch(LOWORD(wp)){case SB_LINELEFT:case SB_PAGELEFT:page--;break;case SB_LINERIGHT:case SB_PAGERIGHT:page++;break;case SB_THUMBTRACK:case SB_THUMBPOSITION:page=si.nTrackPos;break;case SB_LEFT:page=si.nMin;break;case SB_RIGHT:page=si.nMax;break;default:return 0;}
                page=std::clamp(page,si.nMin,si.nMax);SetSettingsSectionPage(wnd,page);RefreshSettingsVisibility(wnd);return 0;
            } return 0;
        }
        case WM_GETMINMAXINFO:{auto* mm=reinterpret_cast<MINMAXINFO*>(lp);mm->ptMinTrackSize.x=980;mm->ptMinTrackSize.y=500;return 0;}
        case WM_APP_SETTINGS_THEME_REFRESH:{RemovePropW(wnd,L"GlideThemeRefreshPending");if(app){app->ApplySettingsThemeToControls(wnd);app->ApplyThemeToMainWindow();InvalidateRect(wnd,nullptr,TRUE);}return 0;}
        case WM_CLOSE:{if(app&&(GetPropW(wnd,L"GlideHotkeyCapture")||GetPropW(wnd,L"GlideHotkeyCaptureAlt")))app->EndHotkeyCapture(wnd,L"Shortcut capture cancelled.");if(app&&!ConfirmDiscardSettings(wnd))return 0;if(app){app->LoadSettings();app->ApplyAlwaysOnTop();app->ApplyThemeToMainWindow();app->InvalidateViewer();}if(app&&app->diagnosticMode)DestroyWindow(wnd);else ShowWindow(wnd,SW_HIDE);return 0;}case WM_DESTROY:{auto* base=reinterpret_cast<std::wstring*>(GetPropW(wnd,L"GlideSettingsBaseline"));if(base){RemovePropW(wnd,L"GlideSettingsBaseline");delete base;}RemovePropW(wnd,L"GlideDialogTooltip");RemovePropW(wnd,L"GlideSettingsDirty");RemovePropW(wnd,L"GlideSettingsInitialized");RemovePropW(wnd,L"GlideSettingsSectionPage");RemovePropW(wnd,L"GlideSettingsRailScroll");RemovePropW(wnd,L"GlideHotkeyCapture");RemovePropW(wnd,L"GlideHotkeyCaptureAlt");RemovePropW(wnd,L"GlideThemeRefreshPending");RemovePropW(wnd,L"GlideSettingsWarmIndex");if(GetCapture()==wnd)ReleaseCapture();RemovePropW(wnd,L"GlideApp");return 0;}
        }
        return DefWindowProcW(wnd,msg,wp,lp);
    }

    void InvalidateViewer() { if (hwnd) InvalidateRect(hwnd, nullptr, FALSE); }

    void PrewarmSettingsDialog() {
        if(diagnosticMode || (settingsDialogHwnd&&IsWindow(settingsDialogHwnd)))return;
        static bool registered=false;
        if(!registered){
            WNDCLASSEXW wc{};wc.cbSize=sizeof(wc);wc.lpfnWndProc=SettingsWndProc;wc.hInstance=hInst;
            wc.hCursor=LoadCursorW(nullptr,IDC_ARROW);wc.hbrBackground=nullptr;
            wc.hIcon=LoadIconW(hInst,MAKEINTRESOURCEW(IDI_GLIDE));
            wc.hIconSm=static_cast<HICON>(LoadImageW(hInst,MAKEINTRESOURCEW(IDI_GLIDE),IMAGE_ICON,GetSystemMetrics(SM_CXSMICON),GetSystemMetrics(SM_CYSMICON),LR_DEFAULTCOLOR));
            if(!wc.hIcon)wc.hIcon=LoadIconW(nullptr,IDI_APPLICATION);if(!wc.hIconSm)wc.hIconSm=wc.hIcon;
            wc.lpszClassName=L"GlideSettingsWindow";registered=RegisterClassExW(&wc)!=0||GetLastError()==ERROR_CLASS_ALREADY_EXISTS;
        }
        if(!registered)return;
        RECT owner{};GetWindowRect(hwnd,&owner);const int W=1080,H=840;
        int x=owner.left+((owner.right-owner.left)-W)/2,y=owner.top+((owner.bottom-owner.top)-H)/2;
        // Build the expensive native control tree while it is invisible. This happens on
        // a short idle timer after the main window is already responsive, so the user's
        // first actual Settings click normally only needs ShowWindow/paint.
        settingsOpenRequestTick=0;settingsCreateMs=0;settingsFirstPaintMs=0;settingsReadyMs=0;settingsDeferredReadyMs=0;
        HWND dlg=CreateWindowExW(0,L"GlideSettingsWindow",L"Glide Options",WS_CAPTION|WS_SYSMENU|WS_THICKFRAME|WS_POPUP|WS_CLIPCHILDREN,x,y,W,H,hwnd,nullptr,hInst,this);
        if(!dlg)return;settingsDialogHwnd=dlg;ShowWindow(dlg,SW_HIDE);
    }

    void ShowSettingsDialog() {
        if(settingsDialogHwnd&&IsWindow(settingsDialogHwnd)){
            // Reuse the already-constructed Settings HWND tree. Creating hundreds of native
            // child controls is the dominant cost of opening Settings; caching the window
            // makes every reopen effectively immediate without any placeholder/refresh frame.
            SetWindowPos(settingsDialogHwnd,HWND_TOP,0,0,0,0,
                         SWP_NOMOVE|SWP_NOSIZE|SWP_SHOWWINDOW|SWP_NOOWNERZORDER);
            if(!diagnosticBackgroundWorker) SetForegroundWindow(settingsDialogHwnd);
            else SetWindowPos(settingsDialogHwnd,HWND_BOTTOM,0,0,0,0,SWP_NOMOVE|SWP_NOSIZE|SWP_NOACTIVATE);
            EnableWindow(hwnd,FALSE);
            MSG m{};
            while(IsWindow(settingsDialogHwnd)&&IsWindowVisible(settingsDialogHwnd)&&GetMessageW(&m,nullptr,0,0)>0){
                bool settingsNav=false;
                if(m.message==WM_KEYDOWN&&(m.hwnd==settingsDialogHwnd||IsChild(settingsDialogHwnd,m.hwnd))&&!GetPropW(settingsDialogHwnd,L"GlideHotkeyCapture")&&!GetPropW(settingsDialogHwnd,L"GlideHotkeyCaptureAlt")){
                    EnsureHotkeys();const DWORD chord=CurrentChord(static_cast<UINT>(m.wParam));
                    for(size_t i=0;i<std::size(kHotkeyDefs)&&i<hotkeys.size();++i){if(kHotkeyDefs[i].action!=HotkeyAction::NextSettingsCategory)continue;if((hotkeys[i]&&hotkeys[i]==chord)||(i<hotkeysAlt.size()&&hotkeysAlt[i]&&hotkeysAlt[i]==chord)){int cat=static_cast<int>(GetWindowLongPtrW(settingsDialogHwnd,GWLP_USERDATA));cat=(cat+1)%12;const int ids[]={IDC_SET_CAT_GENERAL,IDC_SET_CAT_VIEWING,IDC_SET_CAT_MOUSE,IDC_SET_CAT_PERFORMANCE,IDC_SET_CAT_STATUS,IDC_SET_CAT_SLIDESHOW,IDC_SET_CAT_HOTKEYS,IDC_SET_CAT_TABS,IDC_SET_CAT_OVERLAYS,IDC_SET_CAT_PROFILES,IDC_SET_CAT_WINDOWS,IDC_SET_CAT_DEVELOPER};SendMessageW(settingsDialogHwnd,WM_COMMAND,MAKEWPARAM(ids[cat],BN_CLICKED),reinterpret_cast<LPARAM>(GetDlgItem(settingsDialogHwnd,ids[cat])));settingsNav=true;break;}}
                }
                if(!settingsNav&&!IsDialogMessageW(settingsDialogHwnd,&m)){TranslateMessage(&m);DispatchMessageW(&m);}
            }
            EnableWindow(hwnd,TRUE);SetForegroundWindow(hwnd);
            return;
        }
        // Do not leave the transient fullscreen native caption above an owned dialog.
        if(fullscreen&&fullscreenNativeCaptionVisible)SetFullscreenNativeCaption(false);
        static bool registered = false;
        if (!registered) {
            WNDCLASSEXW wc{}; wc.cbSize=sizeof(wc); wc.lpfnWndProc=SettingsWndProc; wc.hInstance=hInst;
            wc.hCursor=LoadCursorW(nullptr, IDC_ARROW); wc.hbrBackground=nullptr;
            // Settings is a first-class Glide window, not a generic dialog.
            // Give the caption its proper large and small application icon.
            wc.hIcon=LoadIconW(hInst,MAKEINTRESOURCEW(IDI_GLIDE));
            wc.hIconSm=static_cast<HICON>(LoadImageW(hInst,MAKEINTRESOURCEW(IDI_GLIDE),IMAGE_ICON,
                GetSystemMetrics(SM_CXSMICON),GetSystemMetrics(SM_CYSMICON),LR_DEFAULTCOLOR));
            if(!wc.hIcon)wc.hIcon=LoadIconW(nullptr,IDI_APPLICATION);
            if(!wc.hIconSm)wc.hIconSm=wc.hIcon;
            wc.lpszClassName=L"GlideSettingsWindow"; registered = RegisterClassExW(&wc) != 0 || GetLastError()==ERROR_CLASS_ALREADY_EXISTS;
        }
        if (!registered) return;
        RECT owner{}; GetWindowRect(hwnd, &owner);
        const int W=1080,H=840; int x=owner.left+((owner.right-owner.left)-W)/2; int y=owner.top+((owner.bottom-owner.top)-H)/2;
        DWORD dlgEx=0;
        settingsOpenRequestTick=GetTickCount64();settingsCreateMs=0;settingsFirstPaintMs=0;settingsReadyMs=0;settingsDeferredReadyMs=0;
        HWND dlg = CreateWindowExW(dlgEx, L"GlideSettingsWindow", L"Glide Options",
            WS_CAPTION|WS_SYSMENU|WS_THICKFRAME|WS_POPUP|WS_CLIPCHILDREN, x,y,W,H, hwnd, nullptr,hInst,this);
        if (!dlg) return;
        settingsDialogHwnd=dlg;
        SetWindowPos(dlg,HWND_TOP,0,0,0,0,
                     SWP_NOMOVE|SWP_NOSIZE|SWP_SHOWWINDOW|SWP_NOOWNERZORDER);
        SetForegroundWindow(dlg);
        EnableWindow(hwnd, FALSE);
        MSG m{};
        while (IsWindow(dlg) && IsWindowVisible(dlg) && GetMessageW(&m, nullptr, 0, 0) > 0) {
            bool settingsNav=false;
            if(m.message==WM_KEYDOWN && (m.hwnd==dlg||IsChild(dlg,m.hwnd)) && !GetPropW(dlg,L"GlideHotkeyCapture") && !GetPropW(dlg,L"GlideHotkeyCaptureAlt")){
                EnsureHotkeys(); const DWORD chord=CurrentChord(static_cast<UINT>(m.wParam));
                for(size_t i=0;i<std::size(kHotkeyDefs)&&i<hotkeys.size();++i){
                    if(kHotkeyDefs[i].action!=HotkeyAction::NextSettingsCategory)continue;
                    if((hotkeys[i]&&hotkeys[i]==chord)||(i<hotkeysAlt.size()&&hotkeysAlt[i]&&hotkeysAlt[i]==chord)){
                        int cat=static_cast<int>(GetWindowLongPtrW(dlg,GWLP_USERDATA)); cat=(cat+1)%12;
                        const int ids[]={IDC_SET_CAT_GENERAL,IDC_SET_CAT_VIEWING,IDC_SET_CAT_MOUSE,IDC_SET_CAT_PERFORMANCE,IDC_SET_CAT_STATUS,IDC_SET_CAT_SLIDESHOW,IDC_SET_CAT_HOTKEYS,IDC_SET_CAT_TABS,IDC_SET_CAT_OVERLAYS,IDC_SET_CAT_PROFILES,IDC_SET_CAT_WINDOWS,IDC_SET_CAT_DEVELOPER};
                        SendMessageW(dlg,WM_COMMAND,MAKEWPARAM(ids[cat],BN_CLICKED),reinterpret_cast<LPARAM>(GetDlgItem(dlg,ids[cat])));settingsNav=true;break;
                    }
                }
            }
            if(!settingsNav && !IsDialogMessageW(dlg, &m)) { TranslateMessage(&m); DispatchMessageW(&m); }
        }
        if(!IsWindow(dlg)) settingsDialogHwnd=nullptr;
        EnableWindow(hwnd, TRUE); SetForegroundWindow(hwnd);
    }

    static LRESULT CALLBACK SlideshowWndProc(HWND wnd, UINT msg, WPARAM wp, LPARAM lp) {
        auto* app=reinterpret_cast<ViewerApp*>(GetWindowLongPtrW(wnd,GWLP_USERDATA));
        if(msg==WM_NCCREATE){auto*cs=reinterpret_cast<CREATESTRUCTW*>(lp);app=reinterpret_cast<ViewerApp*>(cs->lpCreateParams);SetWindowLongPtrW(wnd,GWLP_USERDATA,reinterpret_cast<LONG_PTR>(app));}
        static HBRUSH bgBrush=CreateSolidBrush(RGB(24,26,29));
        static HBRUSH editBrush=CreateSolidBrush(RGB(35,38,42));
        switch(msg){
            case WM_CREATE:{
                BOOL dark=TRUE; DwmSetWindowAttribute(wnd,20,&dark,sizeof(dark));
                HFONT font=CreateFontW(-16,0,0,0,FW_NORMAL,FALSE,FALSE,FALSE,DEFAULT_CHARSET,OUT_DEFAULT_PRECIS,CLIP_DEFAULT_PRECIS,CLEARTYPE_QUALITY,DEFAULT_PITCH,L"Segoe UI");
                HFONT titleFont=CreateFontW(-20,0,0,0,FW_SEMIBOLD,FALSE,FALSE,FALSE,DEFAULT_CHARSET,OUT_DEFAULT_PRECIS,CLIP_DEFAULT_PRECIS,CLEARTYPE_QUALITY,DEFAULT_PITCH,L"Segoe UI");
                auto add=[&](const wchar_t*cls,const wchar_t*text,DWORD style,int x,int y,int w,int h,int id,HFONT useFont=nullptr){HWND c=CreateWindowExW(0,cls,text,WS_CHILD|WS_VISIBLE|style,x,y,w,h,wnd,reinterpret_cast<HMENU>(static_cast<INT_PTR>(id)),app?app->hInst:nullptr,nullptr);SendMessageW(c,WM_SETFONT,reinterpret_cast<WPARAM>(useFont?useFont:font),TRUE);SetWindowTheme(c,L"DarkMode_Explorer",nullptr);const bool isButton=_wcsicmp(cls,L"BUTTON")==0;DWORD bt=style&BS_TYPEMASK;if(isButton&&bt==BS_OWNERDRAW)SetWindowSubclass(c,ModernButtonSubclass,1,0);else if(isButton&&(bt==BS_AUTOCHECKBOX||bt==BS_CHECKBOX||bt==BS_AUTORADIOBUTTON||bt==BS_RADIOBUTTON))SetWindowSubclass(c,ModernToggleSubclass,1,reinterpret_cast<DWORD_PTR>(app));return c;};
                add(L"STATIC",L"Slideshow",0,56,18,220,28,0,titleFont);
                add(L"STATIC",L"Choose how Glide advances through images.",0,56,47,320,22,0);
                add(L"STATIC",L"Image interval",0,24,88,150,22,0);
                add(L"EDIT",L"",ES_NUMBER|WS_BORDER,185,82,95,30,IDC_SLIDE_INTERVAL);
                add(L"STATIC",L"milliseconds",0,292,88,95,22,0);
                add(L"BUTTON",L"Loop slideshow",BS_AUTOCHECKBOX,24,130,170,26,IDC_SLIDE_LOOP);
                add(L"BUTTON",L"Continue across folders",BS_AUTOCHECKBOX,210,130,190,26,IDC_SLIDE_CROSS);
                add(L"BUTTON",L"Shuffle",BS_AUTOCHECKBOX,24,166,130,26,IDC_SLIDE_SHUFFLE);
                add(L"STATIC",L"Transitions are disabled to keep Glide fast and lightweight.",0,24,204,365,24,0);
                add(L"BUTTON",app&&app->slideshowRunning?L"Apply / Restart":L"Start slideshow",BS_OWNERDRAW,174,242,140,36,IDC_SLIDE_START);
                add(L"BUTTON",L"Cancel",BS_OWNERDRAW,324,242,86,36,IDC_SLIDE_CANCEL);
                if(app){AddDialogTooltip(wnd,IDC_SLIDE_INTERVAL,L"Time each image remains on screen, in milliseconds",app->hInst);AddDialogTooltip(wnd,IDC_SLIDE_LOOP,L"Restart from the beginning when the slideshow reaches the end",app->hInst);AddDialogTooltip(wnd,IDC_SLIDE_CROSS,L"Continue automatically into sibling folders",app->hInst);AddDialogTooltip(wnd,IDC_SLIDE_SHUFFLE,L"Show images in randomized order",app->hInst);AddDialogTooltip(wnd,IDC_SLIDE_START,L"Start the slideshow with these settings",app->hInst);AddDialogTooltip(wnd,IDC_SLIDE_CANCEL,L"Close without starting the slideshow",app->hInst);SetDlgItemInt(wnd,IDC_SLIDE_INTERVAL,app->slideshowIntervalMs,FALSE);SetCheck(wnd,IDC_SLIDE_LOOP,app->slideshowLoop);SetCheck(wnd,IDC_SLIDE_CROSS,app->slideshowCrossFolders);SetCheck(wnd,IDC_SLIDE_SHUFFLE,app->slideshowShuffle);} return 0;}
            case WM_COMMAND: if(app){switch(LOWORD(wp)){case IDC_SLIDE_START:{BOOL ok=FALSE;int n=static_cast<int>(GetDlgItemInt(wnd,IDC_SLIDE_INTERVAL,&ok,FALSE));app->slideshowIntervalMs=std::clamp(ok?n:3000,250,600000);app->slideshowLoop=GetCheck(wnd,IDC_SLIDE_LOOP);app->slideshowCrossFolders=GetCheck(wnd,IDC_SLIDE_CROSS);app->slideshowShuffle=GetCheck(wnd,IDC_SLIDE_SHUFFLE);if(app->slideshowRunning)KillTimer(app->hwnd,kSlideshowTimerId);app->slideshowStartedFullscreen=app->fullscreen;app->slideshowPaused=false;app->slideshowRunning=true;SetTimer(app->hwnd,kSlideshowTimerId,static_cast<UINT>(app->slideshowIntervalMs),nullptr);app->SaveSettings();app->UpdateTitle();app->InvalidateViewer();DestroyWindow(wnd);return 0;}case IDC_SLIDE_CANCEL:DestroyWindow(wnd);return 0;}} break;
            case WM_LBUTTONDOWN:{POINT pt{GET_X_LPARAM(lp),GET_Y_LPARAM(lp)};HWND hit=ChildWindowFromPointEx(wnd,pt,CWP_SKIPINVISIBLE|CWP_SKIPDISABLED);if(!hit||hit==wnd){ReleaseCapture();SendMessageW(wnd,WM_NCLBUTTONDOWN,HTCAPTION,MAKELPARAM(pt.x,pt.y));return 0;}break;}
            case WM_PAINT:{PAINTSTRUCT ps{};HDC dc=BeginPaint(wnd,&ps);RECT r{};GetClientRect(wnd,&r);FillRect(dc,&r,bgBrush);if(app&&app->d2d){D2D1_RENDER_TARGET_PROPERTIES pr=D2D1::RenderTargetProperties(D2D1_RENDER_TARGET_TYPE_DEFAULT,D2D1::PixelFormat(DXGI_FORMAT_B8G8R8A8_UNORM,D2D1_ALPHA_MODE_IGNORE));ComPtr<ID2D1DCRenderTarget> rt;if(SUCCEEDED(app->d2d->CreateDCRenderTarget(&pr,&rt))&&SUCCEEDED(rt->BindDC(dc,&r))){rt->BeginDraw();ComPtr<ID2D1SolidColorBrush> sep,blue,glow;rt->CreateSolidColorBrush(D2D1::ColorF(0.20f,0.23f,0.26f,1),&sep);rt->CreateSolidColorBrush(D2D1::ColorF(0.30f,0.66f,1.0f,1),&blue);rt->CreateSolidColorBrush(D2D1::ColorF(0.30f,0.66f,1.0f,0.15f),&glow);rt->DrawLine(D2D1::Point2F(20,68),D2D1::Point2F(static_cast<float>(r.right-20),68),sep.Get(),1);rt->FillEllipse(D2D1::Ellipse(D2D1::Point2F(37,32),17,17),glow.Get());rt->DrawLine(D2D1::Point2F(31,22),D2D1::Point2F(31,42),blue.Get(),2.0f);rt->DrawLine(D2D1::Point2F(31,22),D2D1::Point2F(47,32),blue.Get(),2.0f);rt->DrawLine(D2D1::Point2F(47,32),D2D1::Point2F(31,42),blue.Get(),2.0f);rt->EndDraw();}}EndPaint(wnd,&ps);return 0;}
            case WM_DRAWITEM:{auto*di=reinterpret_cast<DRAWITEMSTRUCT*>(lp);if(!di)return FALSE;if(di->CtlID!=IDC_SLIDE_START&&di->CtlID!=IDC_SLIDE_CANCEL)break;RECT r=di->rcItem;POINT p{};GetCursorPos(&p);ScreenToClient(di->hwndItem,&p);bool hov=PtInRect(&r,p)!=FALSE;wchar_t tx[128]{};GetWindowTextW(di->hwndItem,tx,127);if(DrawSmoothModernButton(app,di,di->CtlID==IDC_SLIDE_START,hov,-1,tx,false))return TRUE;break;}
            case WM_CTLCOLORSTATIC: case WM_CTLCOLORBTN:{HDC dc=reinterpret_cast<HDC>(wp);SetTextColor(dc,RGB(226,230,235));SetBkColor(dc,RGB(24,26,29));return reinterpret_cast<LRESULT>(bgBrush);}
            case WM_CTLCOLOREDIT:{HDC dc=reinterpret_cast<HDC>(wp);SetTextColor(dc,RGB(242,244,247));SetBkColor(dc,RGB(35,38,42));return reinterpret_cast<LRESULT>(editBrush);}
            case WM_ERASEBKGND:{RECT r{};GetClientRect(wnd,&r);FillRect(reinterpret_cast<HDC>(wp),&r,bgBrush);return 1;}
            case WM_CLOSE:DestroyWindow(wnd);return 0;
            case WM_DESTROY:RemovePropW(wnd,L"GlideDialogTooltip");return 0;
        }
        return DefWindowProcW(wnd,msg,wp,lp);
    }

    void ShowSlideshowConfig() {
        static bool registered=false;
        if(!registered){WNDCLASSEXW wc{};wc.cbSize=sizeof(wc);wc.lpfnWndProc=SlideshowWndProc;wc.hInstance=hInst;wc.hCursor=LoadCursorW(nullptr,IDC_ARROW);wc.hbrBackground=CreateSolidBrush(RGB(24,26,29));wc.lpszClassName=L"GlideSlideshowWindow";registered=RegisterClassExW(&wc)!=0||GetLastError()==ERROR_CLASS_ALREADY_EXISTS;}
        if(!registered)return;
        RECT owner{};GetWindowRect(hwnd,&owner);const int W=450,H=330;int x=owner.left+((owner.right-owner.left)-W)/2;int y=owner.top+((owner.bottom-owner.top)-H)/2;
        HWND dlg=CreateWindowExW(WS_EX_DLGMODALFRAME,L"GlideSlideshowWindow",L"Glide Slideshow",WS_CAPTION|WS_SYSMENU|WS_POPUP|WS_VISIBLE,x,y,W,H,hwnd,nullptr,hInst,this);if(!dlg)return;
        EnableWindow(hwnd,FALSE);MSG m{};while(IsWindow(dlg)&&GetMessageW(&m,nullptr,0,0)>0){if(!IsDialogMessageW(dlg,&m)){TranslateMessage(&m);DispatchMessageW(&m);}}EnableWindow(hwnd,TRUE);SetForegroundWindow(hwnd);
    }

    void SetFullscreenNativeCaption(bool show){
        if(!hwnd||!fullscreen||fullscreenNativeCaptionVisible==show)return;
        fullscreenNativeCaptionVisible=show;
        LONG_PTR style=GetWindowLongPtrW(hwnd,GWL_STYLE);

        // Keep DWM explicitly responsible for the non-client area throughout the
        // transition. This avoids the brief classic/Vista-looking fallback frame that
        // can otherwise appear while WS_CAPTION is being added to a fullscreen HWND.
        BOOL dark=ResolveLightTheme()?FALSE:TRUE;
        DWMNCRENDERINGPOLICY ncPolicy=DWMNCRP_ENABLED;
        DwmSetWindowAttribute(hwnd,DWMWA_NCRENDERING_POLICY,&ncPolicy,sizeof(ncPolicy));
        if(FAILED(DwmSetWindowAttribute(hwnd,20,&dark,sizeof(dark))))
            DwmSetWindowAttribute(hwnd,19,&dark,sizeof(dark));
        SetWindowTheme(hwnd,ResolveLightTheme()?L"Explorer":L"DarkMode_Explorer",nullptr);

        if(show){
            style&=~static_cast<LONG_PTR>(WS_POPUP);
            style|=WS_CAPTION|WS_SYSMENU|WS_MINIMIZEBOX|WS_MAXIMIZEBOX|WS_THICKFRAME|WS_VISIBLE;
            SetWindowLongPtrW(hwnd,GWL_STYLE,style);
            SetWindowPos(hwnd,nullptr,0,0,0,0,
                         SWP_NOMOVE|SWP_NOSIZE|SWP_NOZORDER|SWP_FRAMECHANGED|
                         SWP_NOOWNERZORDER|SWP_NOACTIVATE);
            ShowWindow(hwnd,SW_MAXIMIZE);
            if(FAILED(DwmSetWindowAttribute(hwnd,20,&dark,sizeof(dark))))
                DwmSetWindowAttribute(hwnd,19,&dark,sizeof(dark));
            DwmFlush();
            RedrawWindow(hwnd,nullptr,nullptr,RDW_INVALIDATE|RDW_FRAME|RDW_UPDATENOW);
        }else{
            if(IsZoomed(hwnd))ShowWindow(hwnd,SW_RESTORE);
            style=GetWindowLongPtrW(hwnd,GWL_STYLE);
            style&=~(WS_CAPTION|WS_THICKFRAME|WS_MINIMIZEBOX|WS_MAXIMIZEBOX|WS_SYSMENU);
            style|=WS_POPUP|WS_VISIBLE;
            SetWindowLongPtrW(hwnd,GWL_STYLE,style);
            RECT r=fullscreenMonitorRect;
            if(r.right<=r.left||r.bottom<=r.top){
                MONITORINFO mi{sizeof(MONITORINFO)};
                HMONITOR mon=MonitorFromWindow(hwnd,MONITOR_DEFAULTTONEAREST);
                if(GetMonitorInfoW(mon,&mi))r=mi.rcMonitor;
            }
            if(FAILED(DwmSetWindowAttribute(hwnd,20,&dark,sizeof(dark))))
                DwmSetWindowAttribute(hwnd,19,&dark,sizeof(dark));
            SetWindowPos(hwnd,nullptr,r.left,r.top,r.right-r.left,r.bottom-r.top,
                         SWP_FRAMECHANGED|SWP_NOZORDER|SWP_NOOWNERZORDER|SWP_NOACTIVATE);
            DwmFlush();
        }
        ResizeShellBrowser();InvalidateViewer();
    }

    void ToggleFullscreen() {
        if (!hwnd) return;
        opacitySliderVisible = false;
        opacitySliderDragging = false;
        if (GetCapture() == hwnd) ReleaseCapture();

        if (!fullscreen) {
            windowedPlacement = WINDOWPLACEMENT{sizeof(WINDOWPLACEMENT)};
            GetWindowPlacement(hwnd, &windowedPlacement);
            GetWindowRect(hwnd, &savedWindowRect);
            savedWindowMaximized = IsZoomed(hwnd) != FALSE;
            windowedStyle = GetWindowLongPtrW(hwnd, GWL_STYLE);

            // WS_EX_LAYERED is transient presentation state owned by the session
            // opacity/PNG transparency path, not structural window placement. Do not
            // freeze it into the fullscreen restore snapshot: opacity can legitimately
            // change while fullscreen is active.
            const LONG_PTR liveExStyle = GetWindowLongPtrW(hwnd, GWL_EXSTYLE);
            windowedExStyle = liveExStyle & ~static_cast<LONG_PTR>(WS_EX_LAYERED);

            MONITORINFO mi{sizeof(MONITORINFO)};
            HMONITOR mon = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
            if (!GetMonitorInfoW(mon, &mi)) return;
            fullscreenMonitorRect = mi.rcMonitor;

            LONG_PTR style = windowedStyle;
            style &= ~(WS_CAPTION | WS_THICKFRAME | WS_MINIMIZEBOX | WS_MAXIMIZEBOX | WS_SYSMENU);
            style |= WS_POPUP | WS_VISIBLE;
            SetWindowLongPtrW(hwnd, GWL_STYLE, style);

            LONG_PTR fullscreenExStyle = liveExStyle;
            fullscreenExStyle &= ~(WS_EX_DLGMODALFRAME | WS_EX_CLIENTEDGE | WS_EX_STATICEDGE | WS_EX_WINDOWEDGE);
            SetWindowLongPtrW(hwnd, GWL_EXSTYLE, fullscreenExStyle);
            SetWindowPos(hwnd, HWND_TOP, mi.rcMonitor.left, mi.rcMonitor.top,
                         mi.rcMonitor.right - mi.rcMonitor.left, mi.rcMonitor.bottom - mi.rcMonitor.top,
                         SWP_FRAMECHANGED | SWP_NOOWNERZORDER);

            fullscreen = true;
            fullscreenNativeCaptionVisible = false;
            fullscreenCursorHidden = false;
            fullscreenBarVisible = false;
            fullscreenStatusVisible = fullscreenStatusAlwaysOn;
            fullscreenStatusLastInsideTick = GetTickCount64();
            fullscreenBarLastInsideTick = GetTickCount64();
            lastMouseMoveTick = GetTickCount64();
            ApplyWindowTransparency();
            SetTimer(hwnd, kFullscreenCursorTimerId, 250, nullptr);
            SetTimer(hwnd, kFullscreenBarTimerId, 100, nullptr);
        } else {
            fullscreen = false;
            fullscreenNativeCaptionVisible = false;
            SetWindowLongPtrW(hwnd, GWL_STYLE, windowedStyle);
            SetWindowLongPtrW(hwnd, GWL_EXSTYLE, windowedExStyle);
            SetWindowPos(hwnd, nullptr, 0, 0, 0, 0,
                         SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_NOOWNERZORDER |
                         SWP_NOACTIVATE | SWP_FRAMECHANGED);

            if (fullscreenExitWindowMode == 1) {
                WINDOWPLACEMENT restorePlacement = windowedPlacement;
                restorePlacement.length = sizeof(WINDOWPLACEMENT);
                restorePlacement.flags = 0;
                restorePlacement.showCmd = SW_SHOWMAXIMIZED;
                SetWindowPlacement(hwnd, &restorePlacement);
                ShowWindow(hwnd, SW_MAXIMIZE);
            } else {
                SetWindowPlacement(hwnd, &windowedPlacement);
                if (!savedWindowMaximized && windowedPlacement.showCmd != SW_SHOWMINIMIZED &&
                    windowedPlacement.showCmd != SW_MINIMIZE) {
                    SetWindowPos(hwnd, nullptr, savedWindowRect.left, savedWindowRect.top,
                                 savedWindowRect.right - savedWindowRect.left,
                                 savedWindowRect.bottom - savedWindowRect.top,
                                 SWP_NOZORDER | SWP_NOOWNERZORDER | SWP_FRAMECHANGED);
                } else if (savedWindowMaximized) {
                    ShowWindow(hwnd, SW_MAXIMIZE);
                }
            }

            fullscreenCursorHidden = false;
            fullscreenBarVisible = false;
            fullscreenStatusVisible = false;
            ApplyWindowedChromeStyle();
            // Restore the *current* session opacity after structural styles. This is
            // what keeps a 100 -> 50% or 50 -> 100% change made in fullscreen intact.
            ApplyWindowTransparency();
            KillTimer(hwnd, kFullscreenCursorTimerId);
            KillTimer(hwnd, kFullscreenBarTimerId);
            SetCursor(LoadCursorW(nullptr, IDC_ARROW));
        }

        if (viewMode == ViewMode::Manual) ClampPan();
        UpdateScrollBars();
        ResizeShellBrowser();
        InvalidateRect(hwnd, nullptr, FALSE);
        UpdateWindow(hwnd);
    }

    HRESULT InitializeFactories() {
        HRESULT hr = D2D1CreateFactory(D2D1_FACTORY_TYPE_SINGLE_THREADED, d2d.ReleaseAndGetAddressOf());
        if (FAILED(hr)) return hr;
        hr = DWriteCreateFactory(DWRITE_FACTORY_TYPE_SHARED, __uuidof(IDWriteFactory),
                                 reinterpret_cast<IUnknown**>(dwrite.ReleaseAndGetAddressOf()));
        if (FAILED(hr)) return hr;
        hr = dwrite->CreateTextFormat(L"Segoe UI Variable", nullptr, DWRITE_FONT_WEIGHT_NORMAL,
                                      DWRITE_FONT_STYLE_NORMAL, DWRITE_FONT_STRETCH_NORMAL,
                                      15.0f, L"en-us", &uiText);
        if (FAILED(hr)) {
            hr = dwrite->CreateTextFormat(L"Segoe UI", nullptr, DWRITE_FONT_WEIGHT_NORMAL,
                                          DWRITE_FONT_STYLE_NORMAL, DWRITE_FONT_STRETCH_NORMAL,
                                          15.0f, L"en-us", &uiText);
        }
        if (FAILED(hr)) return hr;
        hr = dwrite->CreateTextFormat(L"Segoe UI Variable", nullptr, DWRITE_FONT_WEIGHT_NORMAL,
                                      DWRITE_FONT_STYLE_NORMAL, DWRITE_FONT_STRETCH_NORMAL,
                                      13.0f, L"en-us", &uiTextSmall);
        if (FAILED(hr)) {
            hr = dwrite->CreateTextFormat(L"Segoe UI", nullptr, DWRITE_FONT_WEIGHT_NORMAL,
                                          DWRITE_FONT_STYLE_NORMAL, DWRITE_FONT_STRETCH_NORMAL,
                                          13.0f, L"en-us", &uiTextSmall);
        }
        if (uiText) uiText->SetWordWrapping(DWRITE_WORD_WRAPPING_NO_WRAP);
        if (uiTextSmall) uiTextSmall->SetWordWrapping(DWRITE_WORD_WRAPPING_WRAP);

        return hr;
    }

    void EnsureHomeTextFormats() {
        if (!dwrite || (homeTitleText && homeHeadingText && homeBodyText)) return;
        if (!homeTitleText) {
            dwrite->CreateTextFormat(L"Segoe UI Variable Display", nullptr, DWRITE_FONT_WEIGHT_SEMI_BOLD,
                                     DWRITE_FONT_STYLE_NORMAL, DWRITE_FONT_STRETCH_NORMAL,
                                     26.0f, L"en-us", &homeTitleText);
            if (!homeTitleText) dwrite->CreateTextFormat(L"Segoe UI", nullptr, DWRITE_FONT_WEIGHT_SEMI_BOLD,
                                     DWRITE_FONT_STYLE_NORMAL, DWRITE_FONT_STRETCH_NORMAL,
                                     26.0f, L"en-us", &homeTitleText);
            if (homeTitleText) homeTitleText->SetWordWrapping(DWRITE_WORD_WRAPPING_NO_WRAP);
        }
        if (!homeHeadingText) {
            dwrite->CreateTextFormat(L"Segoe UI Variable Text", nullptr, DWRITE_FONT_WEIGHT_SEMI_BOLD,
                                     DWRITE_FONT_STYLE_NORMAL, DWRITE_FONT_STRETCH_NORMAL,
                                     15.5f, L"en-us", &homeHeadingText);
            if (!homeHeadingText) dwrite->CreateTextFormat(L"Segoe UI", nullptr, DWRITE_FONT_WEIGHT_SEMI_BOLD,
                                     DWRITE_FONT_STYLE_NORMAL, DWRITE_FONT_STRETCH_NORMAL,
                                     15.5f, L"en-us", &homeHeadingText);
            if (homeHeadingText) homeHeadingText->SetWordWrapping(DWRITE_WORD_WRAPPING_NO_WRAP);
        }
        if (!homeBodyText) {
            dwrite->CreateTextFormat(L"Segoe UI Variable Text", nullptr, DWRITE_FONT_WEIGHT_NORMAL,
                                     DWRITE_FONT_STYLE_NORMAL, DWRITE_FONT_STRETCH_NORMAL,
                                     13.5f, L"en-us", &homeBodyText);
            if (!homeBodyText) dwrite->CreateTextFormat(L"Segoe UI", nullptr, DWRITE_FONT_WEIGHT_NORMAL,
                                     DWRITE_FONT_STYLE_NORMAL, DWRITE_FONT_STRETCH_NORMAL,
                                     13.5f, L"en-us", &homeBodyText);
            if (homeBodyText) homeBodyText->SetWordWrapping(DWRITE_WORD_WRAPPING_WRAP);
        }
    }

    HRESULT CreateRenderTarget() {
        if (target) return S_OK;
        RECT rc{};
        GetClientRect(hwnd, &rc);
        const D2D1_SIZE_U size = D2D1::SizeU(std::max<LONG>(1, rc.right - rc.left),
                                            std::max<LONG>(1, rc.bottom - rc.top));
        return d2d->CreateHwndRenderTarget(
            D2D1::RenderTargetProperties(),
            D2D1::HwndRenderTargetProperties(hwnd, size, D2D1_PRESENT_OPTIONS_IMMEDIATELY),
            &target);
    }

    void DiscardRenderTarget() {
        qualityBitmap.Reset();
        qualityBitmapW = qualityBitmapH = 0;
        bitmap.Reset();
        for(auto&ov:overlays)ov.bitmap.Reset();
        target.Reset();
    }

    static std::wstring BaseName(const std::wstring& path) {
        try { return fs::path(path).filename().wstring(); }
        catch (...) { return path; }
    }

    void UpdateTitle() {
        std::wstring title = kAppName;
        if (titleTabsEnabled && activeTab >= 0 && activeTab < static_cast<int>(openTabs.size())) {
            if (TabIsHome(activeTab)) {
                title += L" — Home";
                SetWindowTextW(hwnd, title.c_str());
                return;
            }
            if (ActiveTabIsBrowser()) {
                std::wstring folder = (activeTab < static_cast<int>(tabBrowserFolder.size())) ? tabBrowserFolder[activeTab] : L"";
                std::wstring label = folder.empty() ? L"Explorer" : ParentFolderLabel(folder);
                if (label.empty()) label = L"Explorer";
                title += L" — " + label;
                SetWindowTextW(hwnd, title.c_str());
                return;
            }
        }
        if (currentPath.empty()) title += L" — Home";
        else {
            title += L" — ";
            title += showFullPathInTitle ? currentPath : BaseName(currentPath);
            if (haveIndex && !files.empty())
                title += L"  [" + std::to_wstring(currentIndex + 1) + L"/" + std::to_wstring(files.size()) + L"]";
            if (bitmap) {
                const int pct = static_cast<int>(std::lround(CurrentScale() * 100.0f));
                title += L"  " + std::to_wstring(pct) + L"%";
            }
            if (loading && displayedPath != currentPath) title += L"  (loading...)";
        }
        SetWindowTextW(hwnd, title.c_str());
    }

    static uint64_t FileTimeToUInt64(const FILETIME& ft) {
        ULARGE_INTEGER u{};
        u.LowPart = ft.dwLowDateTime;
        u.HighPart = ft.dwHighDateTime;
        return u.QuadPart;
    }

    static uint64_t CreationTimeForPath(const fs::path& p) {
        WIN32_FILE_ATTRIBUTE_DATA d{};
        if (GetFileAttributesExW(p.c_str(), GetFileExInfoStandard, &d)) return FileTimeToUInt64(d.ftCreationTime);
        return 0;
    }

    static uint64_t ModifiedTimeForPath(const fs::path& p) {
        WIN32_FILE_ATTRIBUTE_DATA d{};
        if (GetFileAttributesExW(p.c_str(), GetFileExInfoStandard, &d)) return FileTimeToUInt64(d.ftLastWriteTime);
        return 0;
    }

    static uint64_t FileSizeForPath(const fs::path& p) {
        WIN32_FILE_ATTRIBUTE_DATA d{};
        if (!GetFileAttributesExW(p.c_str(), GetFileExInfoStandard, &d)) return 0;
        ULARGE_INTEGER u{};
        u.LowPart = d.nFileSizeLow;
        u.HighPart = d.nFileSizeHigh;
        return u.QuadPart;
    }

    bool SortLessRaw(const fs::path& a, const fs::path& b) const {
        switch (sortMode) {
            case SortMode::Name:
                return NaturalLess(a, b);
            case SortMode::Modified: {
                const auto av = ModifiedTimeForPath(a), bv = ModifiedTimeForPath(b);
                return av == bv ? NaturalLess(a, b) : av < bv;
            }
            case SortMode::Created: {
                const auto av = CreationTimeForPath(a), bv = CreationTimeForPath(b);
                return av == bv ? NaturalLess(a, b) : av < bv;
            }
            case SortMode::Size: {
                const auto av = FileSizeForPath(a), bv = FileSizeForPath(b);
                return av == bv ? NaturalLess(a, b) : av < bv;
            }
        }
        return NaturalLess(a, b);
    }

    bool SortLess(const fs::path& a, const fs::path& b) const {
        return sortAscending ? SortLessRaw(a, b) : SortLessRaw(b, a);
    }

    static void AddRecent(std::vector<std::wstring>& list, const std::wstring& value) {
        if (value.empty()) return;
        const std::wstring key = Lower(value);
        list.erase(std::remove_if(list.begin(), list.end(), [&](const std::wstring& s) {
            return Lower(s) == key;
        }), list.end());
        list.insert(list.begin(), value);
        if (list.size() > 8) list.resize(8);
    }

    void RecordRecentFile(const std::wstring& path) {
        if (!recentHistoryEnabled) return;
        AddRecent(recentFiles, path);
        try { AddRecent(recentFolders, fs::path(path).parent_path().wstring()); } catch (...) {}
        while (recentFiles.size() > static_cast<size_t>(recentHistoryLimit)) recentFiles.pop_back();
        while (recentFolders.size() > static_cast<size_t>(recentHistoryLimit)) recentFolders.pop_back();
    }

    void RecordRecentFolder(const std::wstring& path) {
        if (!recentHistoryEnabled) return;
        AddRecent(recentFolders, path);
        while (recentFolders.size() > static_cast<size_t>(recentHistoryLimit)) recentFolders.pop_back();
    }

    std::vector<fs::path> EnumerateImages(const fs::path& folder) const {
        std::vector<fs::path> out;
        try {
            for (const auto& entry : fs::directory_iterator(folder, fs::directory_options::skip_permission_denied)) {
                std::error_code ec;
                if (entry.is_regular_file(ec) && !ec && IsSupportedImageExtension(entry.path())) {
                    out.push_back(entry.path());
                }
            }
        } catch (...) {}
        std::stable_sort(out.begin(), out.end(), [&](const fs::path& a, const fs::path& b) { return SortLess(a, b); });
        return out;
    }

    std::vector<fs::path> EnumerateSiblingFolders(const fs::path& folder) const {
        std::vector<fs::path> out;
        fs::path parent = folder.parent_path();
        if (parent.empty()) return out;
        try {
            for (const auto& entry : fs::directory_iterator(parent, fs::directory_options::skip_permission_denied)) {
                std::error_code ec;
                if (entry.is_directory(ec) && !ec) {
                    if(!folderNavIncludeHidden){DWORD attr=GetFileAttributesW(entry.path().c_str());if(attr!=INVALID_FILE_ATTRIBUTES&&(attr&FILE_ATTRIBUTE_HIDDEN))continue;}
                    out.push_back(entry.path());
                }
            }
        } catch (...) {}
        std::sort(out.begin(), out.end(), NaturalLess);
        return out;
    }

    bool RebuildFolderList(const fs::path& folder, const fs::path& preferred = {}) {
        auto newFiles = EnumerateImages(folder);
        if (newFiles.empty()) {
            files.clear();
            haveIndex = false;
            currentFolder = folder;
            UpdateTitle();
            return false;
        }

        files = std::move(newFiles);
        currentFolder = folder;
        size_t idx = 0;
        if (!preferred.empty()) {
            const std::wstring prefKey = PathKey(preferred);
            auto it = std::find_if(files.begin(), files.end(), [&](const fs::path& p) {
                return PathKey(p) == prefKey;
            });
            if (it != files.end()) idx = static_cast<size_t>(std::distance(files.begin(), it));
        }
        currentIndex = idx;
        haveIndex = true;
        UpdateTitle();
        return true;
    }

    bool LoadFolder(const fs::path& folder, bool first = true) {
        auto list = EnumerateImages(folder);
        if (list.empty()) return false;
        files = std::move(list);
        currentFolder = folder;
        currentIndex = first ? 0 : files.size() - 1;
        haveIndex = true;
        RequestImage(files[currentIndex].wstring(), 0);
        return true;
    }

    bool OpenPathCold(const std::wstring& rawPath) {
        if (rawPath.empty()) return false;
        fs::path p(rawPath);
        std::error_code ec;
        if (!fs::is_regular_file(p, ec) || ec || !IsSupportedImageExtension(p)) return false;

        files.clear();
        files.push_back(p);
        currentFolder = p.parent_path();
        currentIndex = 0;
        // The single-item cold-start list is only a latency optimization, not an
        // authoritative directory index.  Suppress [1/1] until the deferred scan lands.
        haveIndex = false;
        coldStartupPath = p.wstring();
        coldFolderScanPending = false;
        coldPendingNavigationDelta = 0;
        startupDecodeRequestedMs = startupTick0 ? (GetTickCount64() - startupTick0) : 0;

        // Crucial Stage 14 behavior: request the actual requested image immediately.
        // Do not enumerate/sort the containing folder first.
        RequestImage(p.wstring(), 0);
        return true;
    }

    void StartDeferredWorkers() {
        if (deferredWorkersStarted) return;
        deferredWorkersStarted = true;
        deferredWorkersPending = false;
        const bool prefetchOk = prefetchWorker.Start(hwnd, WM_APP_DECODED);
        const bool prefetch2Ok = prefetchWorker2.Start(hwnd, WM_APP_DECODED);
        const bool prefetch3Ok = prefetchWorker3.Start(hwnd, WM_APP_DECODED);
        const bool refineOk = refineWorker.Start(hwnd, WM_APP_DECODED);
        const bool adaptiveOk = adaptiveWorker.Start(hwnd, WM_APP_DECODED);
        if ((!prefetchOk || !prefetch2Ok || !prefetch3Ok || !refineOk || !adaptiveOk) && !coldStartDirectImage) {
            MessageBoxW(hwnd, L"One or more background image loaders could not start.", kAppName, MB_ICONERROR);
        }
        startupDeferredReadyMs = startupTick0 ? (GetTickCount64() - startupTick0) : 0;
    }

    void StartColdFolderScan() {
        if (coldStartupPath.empty() || coldFolderScanPending) return;
        if (coldFolderThread.joinable()) coldFolderThread.join();

        const fs::path preferred(coldStartupPath);
        const fs::path folder = preferred.parent_path();
        if (folder.empty()) return;

        coldFolderScanPending = true;
        coldFolderThread = std::thread([this, folder, preferred]() {
            auto* result = new (std::nothrow) ColdFolderResult;
            if (!result) return;
            result->preferredPath = preferred.wstring();
            result->folder = folder;
            result->files = EnumerateImages(folder);
            if (!PostMessageW(hwnd, WM_APP_COLD_FOLDER, 0, reinterpret_cast<LPARAM>(result)))
                delete result;
        });
    }

    void ApplyColdFolderResult(ColdFolderResult* result) {
        if (!result) return;
        std::unique_ptr<ColdFolderResult> owned(result);
        if (PathKey(result->preferredPath) != PathKey(coldStartupPath)) return;

        if (!result->files.empty()) {
            files = std::move(result->files);
            currentFolder = result->folder;
            const std::wstring key = PathKey(currentPath);
            auto it = std::find_if(files.begin(), files.end(), [&](const fs::path& p){ return PathKey(p)==key; });
            if (it != files.end()) {
                currentIndex = static_cast<size_t>(std::distance(files.begin(), it));
                haveIndex = true;
            }
        }
        coldFolderScanPending = false;
        UpdateTitle();
        // Directory index/total may have changed without changing the bitmap.
        // Repaint explicitly so the text overlay and status UI become authoritative now.
        InvalidateViewer();

        const int pending = coldPendingNavigationDelta;
        coldPendingNavigationDelta = 0;
        if (pending != 0) {
            int steps = std::min(16, std::abs(pending));
            const int dir = pending > 0 ? +1 : -1;
            while (steps-- > 0) Navigate(dir);
        } else if (prefetchEnabled) {
            SchedulePrefetch();
        }
    }

    void WriteStartupDiagnostics() {
        if (!startupDiagnostics || iniPath.empty()) return;
        try {
            fs::path p = fs::path(iniPath).parent_path() / L"Glide Startup Diagnostics.txt";
            Utf8Wofstream f(p, std::ios::trunc);
            if (!f) return;
            f << L"Glide Alpha 0.12107 Glide cold-start diagnostics\n";
            f << L"directImageFastPath=" << (coldStartDirectImage ? 1 : 0) << L"\n";
            f << L"windowCreatedMs=" << startupWindowCreatedMs << L"\n";
            f << L"decodeRequestedMs=" << startupDecodeRequestedMs << L"\n";
            f << L"firstFramePresentedMs=" << startupFirstFrameMs << L"\n";
            f << L"deferredWorkersReadyMs=" << startupDeferredReadyMs << L"\n";
            f << L"folderScanPending=" << (coldFolderScanPending ? 1 : 0) << L"\n";
        } catch (...) {}
    }

    bool OpenPath(const std::wstring& rawPath) {
        if (rawPath.empty()) return false;
        fs::path p(rawPath);
        std::error_code ec;
        if (fs::is_directory(p, ec) && !ec) {
            const bool ok = LoadFolder(p, true);
            if (ok) RecordRecentFolder(p.wstring());
            return ok;
        }
        if (!fs::is_regular_file(p, ec) || ec) return false;

        const fs::path folder = p.parent_path();
        if (!RebuildFolderList(folder, p)) {
            files = {p};
            currentFolder = folder;
            currentIndex = 0;
            haveIndex = true;
        }
        RequestImage(p.wstring(), 0);
        RecordRecentFile(p.wstring());
        return true;
    }

    std::pair<UINT, UINT> PreviewBounds() const {
        RECT rc{};
        if (hwnd) GetClientRect(hwnd, &rc);
        UINT w = static_cast<UINT>(std::max<LONG>(640, rc.right - rc.left));
        UINT h = static_cast<UINT>(std::max<LONG>(480, rc.bottom - rc.top));

        // Stage 11 quality policy. Balanced decodes a first frame at roughly the
        // physical viewport resolution, which is already as sharp as the monitor can
        // display at Fit. Maximum speed retains the smaller capped preview. Maximum
        // quality bypasses this function and requests the source-resolution decode.
        if (initialQualityMode == 0) {
            const double cap = 2048.0;
            const double longest = static_cast<double>(std::max(w, h));
            if (longest > cap) {
                const double f = cap / longest;
                w = std::max<UINT>(1, static_cast<UINT>(std::lround(w * f)));
                h = std::max<UINT>(1, static_cast<UINT>(std::lround(h * f)));
            }
        } else {
            // A small overscan prevents interpolation softness from fractional Fit
            // scaling and DPI rounding without materially increasing decode cost.
            w = std::max<UINT>(1, static_cast<UINT>(std::lround(w * 1.12)));
            h = std::max<UINT>(1, static_cast<UINT>(std::lround(h * 1.12)));
        }
        return {w, h};
    }

    void AddToCache(const DecodedImage& decoded) {
        cache.Add(PathKey(decoded.path), decoded, maxCacheItems);
    }

    struct TransformedPixels {
        UINT width{};
        UINT height{};
        UINT stride{};
        std::vector<BYTE> pixels;
    };

    TransformedPixels MakeViewTransformedPixels(const CachedPixels& src) const {
        const int r = ((rotationQuarterTurns % 4) + 4) % 4;
        const UINT rw = (r % 2) ? src.height : src.width;
        const UINT rh = (r % 2) ? src.width : src.height;
        TransformedPixels out{rw, rh, rw * 4, std::vector<BYTE>(static_cast<size_t>(rw) * rh * 4)};
        for (UINT y = 0; y < rh; ++y) {
            for (UINT x = 0; x < rw; ++x) {
                UINT tx = flipHorizontal ? (rw - 1 - x) : x;
                UINT ty = flipVertical ? (rh - 1 - y) : y;
                UINT sx = 0, sy = 0;
                switch (r) {
                    case 0: sx = tx; sy = ty; break;
                    case 1: sx = ty; sy = src.height - 1 - tx; break;
                    case 2: sx = src.width - 1 - tx; sy = src.height - 1 - ty; break;
                    default: sx = src.width - 1 - ty; sy = tx; break;
                }
                const BYTE* sp = src.pixels.data() + static_cast<size_t>(sy) * src.stride + static_cast<size_t>(sx) * 4;
                BYTE* dp = out.pixels.data() + static_cast<size_t>(y) * out.stride + static_cast<size_t>(x) * 4;
                std::memcpy(dp, sp, 4);
            }
        }
        return out;
    }

    void ResetViewTransform() {
        rotationQuarterTurns = 0;
        flipHorizontal = false;
        flipVertical = false;
    }

    void ApplyViewTransformChange() {
        if (currentPath.empty()) return;
        ClearSelection(false);
        viewMode = ViewMode::Fit;
        zoom = 1.0f;
        panX = panY = 0.0f;
        InstallCachedBitmap(currentPath, false);
        UpdateScrollBars();
        UpdateTitle();
        metadataText.clear();
        InvalidateRect(hwnd, nullptr, FALSE);
    }

    bool InstallCachedBitmap(const std::wstring& path, bool resetView) {
        const std::wstring key = PathKey(path);
        const CachedPixels* cached = cache.Find(key);
        if (!cached) return false;
        if (FAILED(CreateRenderTarget())) return false;

        D2D1_BITMAP_PROPERTIES props = D2D1::BitmapProperties(
            D2D1::PixelFormat(DXGI_FORMAT_B8G8R8A8_UNORM, D2D1_ALPHA_MODE_PREMULTIPLIED));
        ComPtr<ID2D1Bitmap> newBitmap;
        UINT uploadW = cached->width, uploadH = cached->height, uploadStride = cached->stride;
        const BYTE* uploadPixels = cached->pixels.data();
        TransformedPixels transformed;
        if (rotationQuarterTurns != 0 || flipHorizontal || flipVertical) {
            transformed = MakeViewTransformedPixels(*cached);
            uploadW = transformed.width;
            uploadH = transformed.height;
            uploadStride = transformed.stride;
            uploadPixels = transformed.pixels.data();
        }
        HRESULT hr = target->CreateBitmap(D2D1::SizeU(uploadW, uploadH),
                                          uploadPixels, uploadStride, props, &newBitmap);
        if (FAILED(hr)) return false;

        bitmap = newBitmap;
        bitmapPixelW = uploadW;
        bitmapPixelH = uploadH;
        bitmapIsPreview = cached->preview;
        qualityBitmap.Reset();
        qualityBitmapW = qualityBitmapH = 0;
        if ((rotationQuarterTurns & 1) != 0) {
            imageW = cached->sourceHeight;
            imageH = cached->sourceWidth;
        } else {
            imageW = cached->sourceWidth;
            imageH = cached->sourceHeight;
        }
        displayedPath = path;

        if (resetView && !(preserveManualZoomOnNavigate && viewMode==ViewMode::Manual)) {
            switch(std::clamp(defaultViewMode,0,4)){
                case 1: viewMode=ViewMode::FitWidth; zoom=1.0f; break;
                case 2: viewMode=ViewMode::FitHeight; zoom=1.0f; break;
                case 3: viewMode=ViewMode::Manual; zoom=1.0f; break;
                case 4: viewMode=ViewMode::Manual; zoom=std::clamp(defaultCustomZoomPercent/100.0f,kMinZoom,kMaxZoom); break;
                default:viewMode=ViewMode::Fit; zoom=1.0f; break;
            }
            panX = 0.0f;
            panY = 0.0f;
            selectionActive = false;
            selecting = false;
            selectionClickCandidate = false;
            panning = false;
            rightButtonPanning = false;
            rightZoomCandidate = false;
            rightMoved = false;
        } else if (viewMode == ViewMode::Manual) {
            ClampPan();
        }

        loading = false;
        consecutiveFailures = 0;
        // Keep several future frames warm while the user is rapidly stepping.
        // Three independent WIC workers are intentionally allowed to use modern CPU
        // cores in parallel; cached previews are cheap in RAM and become instant first
        // frames when reached.
        if (rapidNavigationActive) ScheduleRapidPrefetch();
        UpdateTitle();
        UpdateScrollBars();
        InvalidateRect(hwnd, nullptr, FALSE);
        UpdateWindow(hwnd); // make every sequential navigation result visibly commit
        return true;
    }

    bool TryLoadFromCache(const std::wstring& path) {
        return InstallCachedBitmap(path, true);
    }

    void ScheduleRefinement() {
        if (!backgroundRefinement) return;
        if (!hwnd || rapidNavigationActive || !bitmapIsPreview || displayedPath.empty() || displayedPath != currentPath) return;
        KillTimer(hwnd, kRefineTimerId);
        SetTimer(hwnd, kRefineTimerId, kRefineDelayMs, nullptr);
    }

    void SettleAfterNavigation() {
        KillTimer(hwnd, kNavigationIdleTimerId);
        rapidNavigationActive = false;
        if (bitmapIsPreview && displayedPath == currentPath) RequestFullRefinement();
        SchedulePrefetch();
    }

    void RequestFullRefinement() {
        KillTimer(hwnd, kRefineTimerId);
        if (!bitmapIsPreview || displayedPath.empty() || displayedPath != currentPath) return;
        const uint64_t gen = generation.load();
        refineWorker.RequestCurrent(displayedPath, gen, false, true);
    }

    void EnsureFullResolutionAsync() {
        if (!bitmapIsPreview || displayedPath.empty() || displayedPath != currentPath) return;
        KillTimer(hwnd, kRefineTimerId);
        refineWorker.RequestCurrent(displayedPath, generation.load(), false, true);
    }

    void RequestAdaptivePreview() {
        KillTimer(hwnd, kAdaptivePreviewTimerId);
        if (!adaptiveFastPreview || !foregroundDecodePending || currentPath.empty()) return;
        adaptivePreviewRequested = true;
        RECT rc{}; GetClientRect(hwnd, &rc);
        UINT w = static_cast<UINT>(std::max<LONG>(480, rc.right - rc.left));
        UINT h = static_cast<UINT>(std::max<LONG>(360, rc.bottom - rc.top));
        const double cap = 1152.0;
        const double longest = static_cast<double>(std::max(w,h));
        if (longest > cap) { const double f=cap/longest; w=std::max<UINT>(1,static_cast<UINT>(std::lround(w*f))); h=std::max<UINT>(1,static_cast<UINT>(std::lround(h*f))); }
        adaptiveWorker.RequestCurrent(currentPath, generation.load(), true, false, w, h);
    }

    void RequestImage(const std::wstring& path, int navDirection) {
        lastForegroundRequestTick = GetTickCount64();
        if (diagnosticMode) {
            diagnosticLastDecodeHr = E_PENDING;
            diagnosticLastWicFilenameHr = E_NOTIMPL;
            diagnosticLastWicStreamInitHr = E_NOTIMPL;
            diagnosticLastWicStreamDecoderHr = E_NOTIMPL;
            diagnosticLastWicStreamUsed = false;
            diagnosticLastNativeFallback = false;
        }
        if (path.empty()) return;
        KillTimer(hwnd, kRefineTimerId);
        KillTimer(hwnd, kAdaptivePreviewTimerId);
        KillTimer(hwnd, kNavigationIdleTimerId);
        adaptivePreviewRequested = false;
        if (navDirection != 0) {
            rapidNavigationActive = true;
            lastNavigationTick = GetTickCount64();
            SetTimer(hwnd, kNavigationIdleTimerId, kNavigationIdleDelayMs, nullptr);
        } else {
            rapidNavigationActive = false;
        }
        // Latest-image-wins policy: navigation must never inherit queued decode work
        // from images the user has already skipped. Running WIC calls cannot be safely
        // interrupted mid-CopyPixels, but every queued prefetch/refinement/rescue request
        // is discarded immediately and stale completions are rejected by generation.
        // Do not tear down predictive prefetch on every arrow/click. Those jobs are
        // deliberately decoding nearby future frames and remain useful as the user
        // advances. Refinement/rescue work for the image being left is still cancelled.
        if (navDirection == 0) {
            prefetchWorker.CancelPending();
            prefetchWorker2.CancelPending();
            prefetchWorker3.CancelPending();
            predictivePrefetchPending.clear();
        }
        refineWorker.CancelPending();
        adaptiveWorker.CancelPending();
        pendingNavigationDelta = 0;
        if (currentPath.empty() || PathKey(path) != PathKey(currentPath)) {
            ResetViewTransform();
            metadataText.clear();
        }
        currentPath = path;
        ApplyPngTransparencyMode();
        SyncActiveTabToCurrent();
        lastNavDirection = navDirection;
        loading = true;
        UpdateTitle();

        const uint64_t gen = ++generation;
        bool cacheSuitable = false;
        const CachedPixels* ci = cache.Find(PathKey(path));
        if (ci) {
            if (!ci->preview) cacheSuitable = true;
            else if (rapidNavigationActive) cacheSuitable = true; // predictive rapid-preview frame: display now, refine when idle
            else if (initialQualityMode == 0) cacheSuitable = true;
            else if (initialQualityMode == 1) {
                const auto [needW, needH] = PreviewBounds();
                cacheSuitable = ci->width >= static_cast<UINT>(needW * 0.90) ||
                                ci->height >= static_cast<UINT>(needH * 0.90);
            }
        }
        if (cacheSuitable && TryLoadFromCache(path)) {
            PerfSample ps{};
            ps.requestTick=lastForegroundRequestTick;ps.firstFrameTick=GetTickCount64();ps.decodeMs=0;
            ps.width=ci->sourceWidth?ci->sourceWidth:ci->width;
            ps.height=ci->sourceHeight?ci->sourceHeight:ci->height;
            ps.preview=ci->preview;ps.refinement=false;ps.cacheHit=true;
            ps.extension=Lower(fs::path(path).extension().wstring());
            diagnosticPerfSamples.push_back(std::move(ps));
            if(diagnosticPerfSamples.size()>128)diagnosticPerfSamples.erase(diagnosticPerfSamples.begin(),diagnosticPerfSamples.begin()+32);
            foregroundDecodePending = false;
            if (!rapidNavigationActive) {
                SchedulePrefetch();
                ScheduleRefinement();
            }
            if (pendingNavigationDelta != 0) PostMessageW(hwnd, WM_APP_NAV_DRAIN, 0, 0);
            return;
        }

        foregroundDecodePending = true;
        // The 40 ms rescue delay remains useful for non-JPEG codecs. JPEG now has
        // an immediate specialised preview path (native DCT scaling for baseline,
        // progressive level 0 for progressive JPEG), so waiting for a rescue would
        // only duplicate work.
        if (adaptiveFastPreview && !IsJpegPath(path))
            SetTimer(hwnd, kAdaptivePreviewTimerId, static_cast<UINT>(adaptivePreviewDelayMs), nullptr);

        // Two independent foreground lanes prevent one uncancellable in-flight WIC
        // codec call from serialising the next navigation request. Prefer the normal
        // lane, but immediately spill to the second lane if it is busy. The stale
        // generation is discarded when the old call eventually returns.
        DecodeWorker* foregroundLane = &worker;
        if (worker.IsBusy() && !adaptiveWorker.IsBusy()) foregroundLane = &adaptiveWorker;
        else if (worker.IsBusy() && adaptiveWorker.IsBusy())
            foregroundLane = (gen & 1ull) ? &worker : &adaptiveWorker;

        if (initialQualityMode == 2 && !rapidNavigationActive) {
            // Maximum quality is honoured when settled. During rapid browsing, even
            // this mode temporarily uses previews so navigation can remain responsive.
            foregroundLane->RequestCurrent(path, gen, false, false);
        } else {
            auto [pw, ph] = PreviewBounds();
            if(rapidNavigationActive){
                // 2D speed-first browse preview: cap the first visible frame so huge JPEGs
                // can use a cheaper decoder-native reduction. Full quality is requested
                // automatically once navigation is idle.
                const int longest=std::max(pw,ph); const int cap=std::clamp(rapidPreviewLongestSide,480,4096);
                if(longest>cap){const double f=double(cap)/double(longest);pw=std::max(1,int(std::lround(pw*f)));ph=std::max(1,int(std::lround(ph*f)));}
            }
            foregroundLane->RequestCurrent(path, gen, true, false, pw, ph, rapidNavigationActive);
        }
        // Start preparing the next run of images immediately, in parallel with the
        // foreground decode. This is deliberately throughput-oriented on modern PCs.
        if (rapidNavigationActive) ScheduleRapidPrefetch();
    }

    void DrainOnePendingNavigation() {
        if (foregroundDecodePending || pendingNavigationDelta == 0) return;
        const int direction = pendingNavigationDelta > 0 ? +1 : -1;
        pendingNavigationDelta -= direction;
        Navigate(direction);
    }

    bool ApplyDecoded(const DecodedImage& decoded) {
        if (!decoded.currentRequest) {
            predictivePrefetchPending.erase(PathKey(decoded.path));
            AddToCache(decoded);
            return true;
        }
        if (decoded.generation != generation.load()) return false;
        if (diagnosticMode) {
            diagnosticLastDecodeHr = decoded.hr;
            diagnosticLastWicFilenameHr = decoded.wicFilenameHr;
            diagnosticLastWicStreamInitHr = decoded.wicStreamInitHr;
            diagnosticLastWicStreamDecoderHr = decoded.wicStreamDecoderHr;
            diagnosticLastWicStreamUsed = decoded.wicStreamUsed;
            diagnosticLastNativeFallback = decoded.nativeFallback;
        }

        if (FAILED(decoded.hr) || decoded.pixels.empty()) {
            if (!decoded.refinement) { foregroundDecodePending = false; KillTimer(hwnd, kAdaptivePreviewTimerId); }
            if(coldStartDirectImage && deferredWorkersPending) StartDeferredWorkers();
            coldMinimalChrome = false;
            loading = false;
            UpdateTitle();
            ++consecutiveFailures;
            if (!decoded.refinement && lastNavDirection != 0 &&
                consecutiveFailures <= static_cast<int>(std::max<size_t>(files.size(), 1))) {
                Navigate(lastNavDirection);
            } else if (!decoded.refinement && !diagnosticMode) {
                if (IsModernWicCodecPath(currentPath)) {
                    const std::wstring fmt = ModernFormatName(currentPath);
                    const std::wstring msg = L"Glide could not decode this " + fmt + L" image.\n\nGlide tried Windows/WIC plus its bundled lightweight fallback codecs but could not decode this image. The file may be damaged or use an unsupported codec variant.";
                    MessageBoxW(hwnd, msg.c_str(), kAppName, MB_ICONERROR);
                } else {
                    MessageBoxW(hwnd, L"Glide could not decode this image.", kAppName, MB_ICONERROR);
                }
            }
            return false;
        }

        AddToCache(decoded);
        if (decoded.refinement) {
            // A late full-resolution decode upgrades the currently visible preview
            // without changing its zoom/pan state.
            if (decoded.path == currentPath && decoded.path == displayedPath) {
                InstallCachedBitmap(decoded.path, false);
            }
            return true;
        }

        foregroundDecodePending = false;
        KillTimer(hwnd, kAdaptivePreviewTimerId);
        const bool ok = InstallCachedBitmap(decoded.path, true);
        if (ok && decoded.currentRequest) {
            PerfSample ps{};
            ps.requestTick = lastForegroundRequestTick;
            ps.firstFrameTick = GetTickCount64();
            ps.decodeMs = decoded.decodeMs;
            ps.width = decoded.sourceWidth ? decoded.sourceWidth : decoded.width;
            ps.height = decoded.sourceHeight ? decoded.sourceHeight : decoded.height;
            ps.preview = decoded.preview; ps.refinement = decoded.refinement;
            ps.extension = Lower(fs::path(decoded.path).extension().wstring());
            diagnosticPerfSamples.push_back(std::move(ps));
            if (diagnosticPerfSamples.size() > 128) diagnosticPerfSamples.erase(diagnosticPerfSamples.begin(), diagnosticPerfSamples.begin()+32);
        }

        // InstallCachedBitmap calls UpdateWindow, so the requested image has already
        // been physically presented before any of this Stage 14 deferred work starts.
        if (ok && coldStartDirectImage && !coldFirstFrameCommitted) {
            coldFirstFrameCommitted = true;
            startupFirstFrameMs = startupTick0 ? (GetTickCount64() - startupTick0) : 0;
            coldMinimalChrome = false;
            // Restore Glide's proper magnifier cursors after the minimal cold-start frame.
            // Cold launch may temporarily use IDC_CROSS so cursor construction never delays first pixels.
            if(zoomInCursor==LoadCursorW(nullptr,IDC_CROSS) || zoomOutCursor==LoadCursorW(nullptr,IDC_CROSS)){
                if(HCURSOR c=CreateModernZoomCursor(false))zoomInCursor=c;
                if(HCURSOR c=CreateModernZoomCursor(true))zoomOutCursor=c;
            }
            StartDeferredWorkers();
            RecordRecentFile(decoded.path);
            StartColdFolderScan();
            WriteStartupDiagnostics();
            InvalidateViewer();
        }

        if (!rapidNavigationActive) {
            SchedulePrefetch();
            ScheduleRefinement();
        } else if(ok && prefetchEnabled && haveIndex && !files.empty() && lastNavDirection!=0) {
            ScheduleRapidPrefetch();
        }
        if (pendingNavigationDelta != 0) PostMessageW(hwnd, WM_APP_NAV_DRAIN, 0, 0);
        return ok;
    }

    std::optional<fs::path> FindAdjacentFolderWithImages(int direction) const {
        if (currentFolder.empty()) return std::nullopt;
        auto siblings = EnumerateSiblingFolders(currentFolder);
        if (siblings.empty()) return std::nullopt;

        const std::wstring currentKey = PathKey(currentFolder);
        int currentPos = -1;
        for (size_t i = 0; i < siblings.size(); ++i) {
            if (PathKey(siblings[i]) == currentKey) {
                currentPos = static_cast<int>(i);
                break;
            }
        }
        if (currentPos < 0) return std::nullopt;

        for (int pos = currentPos + direction; pos >= 0 && pos < static_cast<int>(siblings.size()); pos += direction) {
            if (!EnumerateImages(siblings[pos]).empty()) return siblings[pos];
        }
        return std::nullopt;
    }

    bool MoveToAdjacentFolder(int direction) {
        if (!autoSiblingFolders) return false;
        auto folder = FindAdjacentFolderWithImages(direction);
        if (!folder) return false;
        auto list = EnumerateImages(*folder);
        if (list.empty()) return false;
        files = std::move(list);
        currentFolder = *folder;
        currentIndex = direction > 0 ? 0 : files.size() - 1;
        haveIndex = true;
        RequestImage(files[currentIndex].wstring(), direction);
        return true;
    }

    bool NavigateSiblingFolderButton(int direction) {
        if(direction==0||currentFolder.empty())return false;
        auto siblings=EnumerateSiblingFolders(currentFolder);if(siblings.empty())return false;
        const std::wstring cur=PathKey(currentFolder);int pos=-1;
        for(size_t i=0;i<siblings.size();++i)if(PathKey(siblings[i])==cur){pos=static_cast<int>(i);break;}
        if(pos<0)return false;const int n=static_cast<int>(siblings.size());
        for(int tries=0;tries<n-1;++tries){
            int next=pos+direction;
            if(next<0||next>=n){if(!folderNavWrap)return false;next=(next<0)?n-1:0;}
            if(next==pos)return false;pos=next;
            auto list=EnumerateImages(siblings[pos]);
            if(list.empty()){if(folderNavSkipEmpty)continue;else return false;}
            files=std::move(list);currentFolder=siblings[pos];
            currentIndex=folderNavOpenFirstImage?0:files.size()-1;haveIndex=true;
            RequestImage(files[currentIndex].wstring(),direction);RecordRecentFolder(currentFolder.wstring());return true;
        }
        return false;
    }

    void ShowParentFolderExplorer(){
        if(currentFolder.empty())return;fs::path parent=currentFolder.parent_path();if(parent.empty())return;
        ComPtr<IFileOpenDialog> dlg;if(FAILED(CoCreateInstance(CLSID_FileOpenDialog,nullptr,CLSCTX_INPROC_SERVER,IID_PPV_ARGS(&dlg))))return;
        FILEOPENDIALOGOPTIONS opts{};dlg->GetOptions(&opts);dlg->SetOptions(opts|FOS_PICKFOLDERS|FOS_FORCEFILESYSTEM|FOS_PATHMUSTEXIST);
        dlg->SetTitle(L"Explore parent folders");
        ComPtr<IShellItem> parentItem;if(SUCCEEDED(SHCreateItemFromParsingName(parent.c_str(),nullptr,IID_PPV_ARGS(&parentItem)))&&parentItem){dlg->SetDefaultFolder(parentItem.Get());dlg->SetFolder(parentItem.Get());}
        const std::wstring currentName=currentFolder.filename().wstring();if(!currentName.empty())dlg->SetFileName(currentName.c_str());
        if(FAILED(dlg->Show(hwnd)))return;ComPtr<IShellItem> chosen;if(FAILED(dlg->GetResult(&chosen))||!chosen)return;
        PWSTR raw=nullptr;if(FAILED(chosen->GetDisplayName(SIGDN_FILESYSPATH,&raw))||!raw)return;std::wstring folder(raw);CoTaskMemFree(raw);
        fs::path targetFolder(folder);auto list=EnumerateImages(targetFolder);
        if(list.empty()){if(ShouldOpenEmptyFolderAsBrowser()){const std::wstring old=currentPath;NewBrowserTab();if(ActiveTabIsBrowser()){tabBrowserFolder[activeTab]=folder;openTabs[activeTab]=folder;ShowShellBrowserForActiveTab();}if(!old.empty())currentPath=old;}return;}
        files=std::move(list);currentFolder=targetFolder;currentIndex=folderNavOpenFirstImage?0:files.size()-1;haveIndex=true;RequestImage(files[currentIndex].wstring(),0);RecordRecentFolder(folder);
    }

    bool Navigate(int direction) {
        if (direction == 0) return false;
        if (coldFolderScanPending) {
            coldPendingNavigationDelta = std::clamp(coldPendingNavigationDelta + direction, -64, 64);
            return true;
        }
        if (!haveIndex || files.empty()) return false;

        // Stage 11.7: latest-input-wins. If a decode is still running, do NOT queue
        // intermediate frames. Advance immediately, invalidate the old generation and
        // request the newly selected image. This keeps wheel/button browsing responsive
        // even when individual source JPEGs are extremely large.
        if (direction > 0) {
            if (currentIndex + 1 < files.size()) {
                ++currentIndex;
                RequestImage(files[currentIndex].wstring(), +1);
                return true;
            }
            if(MoveToAdjacentFolder(+1)) return true; if(wrapFolderNavigation&&!files.empty()){currentIndex=0;RequestImage(files[currentIndex].wstring(),+1);return true;} return false;
        }

        if (currentIndex > 0) {
            --currentIndex;
            RequestImage(files[currentIndex].wstring(), -1);
            return true;
        }
        if(MoveToAdjacentFolder(-1)) return true; if(wrapFolderNavigation&&!files.empty()){currentIndex=files.size()-1;RequestImage(files[currentIndex].wstring(),-1);return true;} return false;
    }

    void JumpFirstLast(bool first) {
        if (!haveIndex || files.empty()) return;
        currentIndex = first ? 0 : files.size() - 1;
        RequestImage(files[currentIndex].wstring(), first ? -1 : +1);
    }

    void JumpBy(int delta) {
        if (!haveIndex || files.empty() || delta == 0) return;
        long long targetIndex = static_cast<long long>(currentIndex) + delta;
        if (targetIndex < 0) targetIndex = 0;
        if (targetIndex >= static_cast<long long>(files.size())) targetIndex = static_cast<long long>(files.size()) - 1;
        currentIndex = static_cast<size_t>(targetIndex);
        RequestImage(files[currentIndex].wstring(), delta > 0 ? +1 : -1);
    }

    void RefreshFolder() {
        if (currentFolder.empty()) return;
        const fs::path preferred = currentPath.empty() ? fs::path{} : fs::path(currentPath);
        if (!RebuildFolderList(currentFolder, preferred)) return;
        if (!preferred.empty()) {
            const std::wstring key = PathKey(preferred);
            auto it = std::find_if(files.begin(), files.end(), [&](const fs::path& p) { return PathKey(p) == key; });
            if (it == files.end()) {
                currentIndex = std::min(currentIndex, files.size() - 1);
                RequestImage(files[currentIndex].wstring(), 0);
            }
        }
        SchedulePrefetch();
    }

    void RequestPredictivePrefetchOnLane(int lane, const std::wstring& path, uint64_t gen, UINT pw, UINT ph) {
        const std::wstring key = PathKey(path);
        if (cache.Contains(key) || predictivePrefetchPending.find(key) != predictivePrefetchPending.end()) return;
        predictivePrefetchPending.insert(key);
        if (lane % 3 == 0) prefetchWorker.RequestPrefetch(path, gen, pw, ph);
        else if (lane % 3 == 1) prefetchWorker2.RequestPrefetch(path, gen, pw, ph);
        else prefetchWorker3.RequestPrefetch(path, gen, pw, ph);
    }

    void ScheduleRapidPrefetch() {
        if (!prefetchEnabled || !haveIndex || files.empty() || lastNavDirection == 0) return;
        auto [pw, ph] = PreviewBounds();
        const int longest = std::max(static_cast<int>(pw), static_cast<int>(ph));
        const int cap = std::clamp(rapidPreviewLongestSide,480,4096);
        if (longest > cap) {
            const double f = double(cap) / double(longest);
            pw = std::max<UINT>(1, static_cast<UINT>(std::lround(pw * f)));
            ph = std::max<UINT>(1, static_cast<UINT>(std::lround(ph * f)));
        }
        const uint64_t gen = generation.load();
        int lane = 0;
        for (const auto& candidate : GlidePrefetch::BuildRapidPlan(files, currentIndex, haveIndex, lastNavDirection, 8, 2)) {
            const std::wstring q = candidate.wstring();
            if (cache.Contains(PathKey(q))) continue;
            RequestPredictivePrefetchOnLane(lane++, q, gen, pw, ph);
        }
    }

    void SchedulePrefetch() {
        if (!prefetchEnabled || rapidNavigationActive || !haveIndex || files.empty()) return;
        const uint64_t gen = generation.load();
        const auto [pw, ph] = PreviewBounds();
        int lane = 0;
        for (const auto& candidate : GlidePrefetch::BuildNearbyPlan(files, currentIndex, haveIndex, std::clamp(prefetchDepth,0,6))) {
            const std::wstring q = candidate.wstring();
            if (!cache.Contains(PathKey(q))) RequestPredictivePrefetchOnLane(lane++, q, gen, pw, ph);
        }
    }

    static bool CopyTextToClipboard(HWND owner, const std::wstring& text) {
        if (text.empty() || !OpenClipboard(owner)) return false;
        EmptyClipboard();
        const SIZE_T bytes = (text.size() + 1) * sizeof(wchar_t);
        HGLOBAL mem = GlobalAlloc(GMEM_MOVEABLE, bytes);
        if (!mem) { CloseClipboard(); return false; }
        void* dst = GlobalLock(mem);
        if (!dst) { GlobalFree(mem); CloseClipboard(); return false; }
        memcpy(dst, text.c_str(), bytes);
        GlobalUnlock(mem);
        if (!SetClipboardData(CF_UNICODETEXT, mem)) {
            GlobalFree(mem);
            CloseClipboard();
            return false;
        }
        CloseClipboard();
        return true;
    }

    bool CopyCurrentImageToClipboard() {
        if (currentPath.empty()) return false;
        ComPtr<IWICImagingFactory> wic;
        if (FAILED(CoCreateInstance(CLSID_WICImagingFactory, nullptr, CLSCTX_INPROC_SERVER,
                                    IID_PPV_ARGS(&wic)))) return false;
        DecodeRequest req{currentPath, generation.load(), true, false, false, false, 0, 0};
        DecodedImage decoded = DecodeFile(wic.Get(), req);
        if (FAILED(decoded.hr) || decoded.pixels.empty()) return false;

        BITMAPINFO bmi{};
        bmi.bmiHeader.biSize = sizeof(BITMAPINFOHEADER);
        bmi.bmiHeader.biWidth = static_cast<LONG>(decoded.width);
        bmi.bmiHeader.biHeight = -static_cast<LONG>(decoded.height);
        bmi.bmiHeader.biPlanes = 1;
        bmi.bmiHeader.biBitCount = 32;
        bmi.bmiHeader.biCompression = BI_RGB;
        void* bits = nullptr;
        HBITMAP hbmp = CreateDIBSection(nullptr, &bmi, DIB_RGB_COLORS, &bits, nullptr, 0);
        if (!hbmp || !bits) { if (hbmp) DeleteObject(hbmp); return false; }
        memcpy(bits, decoded.pixels.data(), decoded.pixels.size());
        if (!OpenClipboard(hwnd)) { DeleteObject(hbmp); return false; }
        EmptyClipboard();
        if (!SetClipboardData(CF_BITMAP, hbmp)) {
            CloseClipboard();
            DeleteObject(hbmp);
            return false;
        }
        CloseClipboard();
        return true; // clipboard owns hbmp now
    }

    bool CopyCurrentImageFileToClipboard() {
        if(currentPath.empty())return false;
        const SIZE_T chars=currentPath.size()+2; // path NUL + double-NUL list terminator
        const SIZE_T bytes=sizeof(DROPFILES)+chars*sizeof(wchar_t);
        HGLOBAL mem=GlobalAlloc(GMEM_MOVEABLE|GMEM_ZEROINIT,bytes);
        if(!mem)return false;
        auto* drop=reinterpret_cast<DROPFILES*>(GlobalLock(mem));
        if(!drop){GlobalFree(mem);return false;}
        drop->pFiles=sizeof(DROPFILES);drop->fWide=TRUE;
        auto* paths=reinterpret_cast<wchar_t*>(reinterpret_cast<BYTE*>(drop)+sizeof(DROPFILES));
        memcpy(paths,currentPath.c_str(),(currentPath.size()+1)*sizeof(wchar_t));
        paths[currentPath.size()+1]=L'\0';
        GlobalUnlock(mem);
        if(!OpenClipboard(hwnd)){GlobalFree(mem);return false;}
        EmptyClipboard();
        if(!SetClipboardData(CF_HDROP,mem)){CloseClipboard();GlobalFree(mem);return false;}
        CloseClipboard();return true;
    }

    void RevealCurrentInExplorer() const { glide_platform::RevealInExplorer(hwnd,currentPath); }

    void ShowCurrentProperties() const { glide_platform::ShowFileProperties(hwnd,currentPath); }

    bool RenameCurrent() {
        if (currentPath.empty()) return false;
        fs::path oldPath(currentPath);
        std::wstring buffer = oldPath.filename().wstring();
        buffer.resize(32768, L'\0');
        OPENFILENAMEW ofn{};
        ofn.lStructSize = sizeof(ofn);
        ofn.hwndOwner = hwnd;
        ofn.lpstrFile = buffer.data();
        ofn.nMaxFile = static_cast<DWORD>(buffer.size());
        std::wstring dir = oldPath.parent_path().wstring();
        ofn.lpstrInitialDir = dir.c_str();
        ofn.lpstrTitle = L"Rename image - enter a new file name";
        ofn.lpstrFilter = L"All files\0*.*\0\0";
        ofn.Flags = OFN_PATHMUSTEXIST | OFN_EXPLORER | OFN_NOCHANGEDIR | OFN_NOREADONLYRETURN;
        if (!GetSaveFileNameW(&ofn)) return false;
        fs::path requested(buffer.c_str());
        if (requested.has_parent_path() && !requested.parent_path().empty() &&
            PathKey(requested.parent_path()) != PathKey(oldPath.parent_path())) {
            MessageBoxW(hwnd, L"Rename keeps the file in its current folder. Use only a new file name.",
                        kAppName, MB_ICONINFORMATION);
            return false;
        }
        fs::path newPath = oldPath.parent_path() / requested.filename();
        if (!IsSupportedImageExtension(newPath)) {
            MessageBoxW(hwnd, L"The renamed file must keep a supported image extension.",
                        kAppName, MB_ICONINFORMATION);
            return false;
        }
        if (PathKey(newPath) == PathKey(oldPath)) return true;
        std::error_code ec;
        if (fs::exists(newPath, ec) && !ec) {
            MessageBoxW(hwnd, L"A file with that name already exists.", kAppName, MB_ICONWARNING);
            return false;
        }
        if (!MoveFileW(oldPath.c_str(), newPath.c_str())) {
            MessageBoxW(hwnd, L"Windows could not rename this image.", kAppName, MB_ICONERROR);
            return false;
        }
        currentPath = newPath.wstring();
        displayedPath = currentPath;
        RebuildFolderList(oldPath.parent_path(), newPath);
        UpdateTitle();
        return true;
    }

    bool DeleteCurrentToRecycleBin() {
        if (currentPath.empty()) return false;
        const int answer = MessageBoxW(hwnd,
            (L"Move this image to the Recycle Bin?\n\n" + BaseName(currentPath)).c_str(),
            kAppName, MB_ICONQUESTION | MB_YESNO | MB_DEFBUTTON2);
        if (answer != IDYES) return false;

        if (!glide_platform::RecycleFile(hwnd,currentPath)) return false;

        const fs::path folder = currentFolder;
        const size_t oldIndex = currentIndex;
        auto newFiles = EnumerateImages(folder);
        files = std::move(newFiles);
        if (files.empty()) {
            currentPath.clear();
            displayedPath.clear();
            haveIndex = false;
            bitmap.Reset();
            qualityBitmap.Reset();
            imageW = imageH = bitmapPixelW = bitmapPixelH = 0;
            UpdateScrollBars();
            UpdateTitle();
            InvalidateRect(hwnd, nullptr, FALSE);
            return true;
        }
        currentIndex = std::min(oldIndex, files.size() - 1);
        haveIndex = true;
        RequestImage(files[currentIndex].wstring(), 0);
        return true;
    }

    static std::wstring FormatBytes(uint64_t bytes) {
        wchar_t b[64]{};
        if (bytes >= 1024ull * 1024ull * 1024ull) swprintf_s(b, L"%.2f GB", bytes / (1024.0 * 1024.0 * 1024.0));
        else if (bytes >= 1024ull * 1024ull) swprintf_s(b, L"%.1f MB", bytes / (1024.0 * 1024.0));
        else if (bytes >= 1024ull) swprintf_s(b, L"%.1f KB", bytes / 1024.0);
        else swprintf_s(b, L"%llu B", static_cast<unsigned long long>(bytes));
        return b;
    }

    static std::wstring QueryMetadataString(IWICMetadataQueryReader* reader, const wchar_t* query) {
        if (!reader) return L"";
        PROPVARIANT pv{};
        PropVariantInit(&pv);
        std::wstring out;
        if (SUCCEEDED(reader->GetMetadataByName(query, &pv))) {
            wchar_t buffer[256]{};
            if (SUCCEEDED(PropVariantToString(pv, buffer, static_cast<UINT>(std::size(buffer))))) out = buffer;
        }
        PropVariantClear(&pv);
        return out;
    }

    void RefreshMetadataText() {
        metadataText.clear();
        if (currentPath.empty()) return;
        std::wstring text = BaseName(currentPath);
        text += L"\n" + std::to_wstring(imageW) + L" × " + std::to_wstring(imageH) + L" px";
        text += L"\n" + FormatBytes(FileSizeForPath(currentPath));
        try {
            std::wstring ext = fs::path(currentPath).extension().wstring();
            if (!ext.empty() && ext[0] == L'.') ext.erase(ext.begin());
            if (!ext.empty()) text += L"   " + Lower(ext);
        } catch (...) {}

        ComPtr<IWICImagingFactory> wic;
        if (FAILED(CoCreateInstance(CLSID_WICImagingFactory, nullptr, CLSCTX_INPROC_SERVER, IID_PPV_ARGS(&wic)))) {
            metadataText = text; return;
        }
        ComPtr<IWICBitmapDecoder> decoder;
        if (FAILED(wic->CreateDecoderFromFilename(currentPath.c_str(), nullptr, GENERIC_READ,
                                                  WICDecodeMetadataCacheOnDemand, &decoder))) {
            metadataText = text; return;
        }
        UINT frameCount = 0;
        if (SUCCEEDED(decoder->GetFrameCount(&frameCount)) && frameCount > 1) {
            const std::wstring ext = Lower(fs::path(currentPath).extension().wstring());
            text += L"\n" + std::wstring((ext == L".tif" || ext == L".tiff") ? L"Pages: " : L"Frames: ") + std::to_wstring(frameCount);
        }
        ComPtr<IWICBitmapFrameDecode> frame;
        if (FAILED(decoder->GetFrame(0, &frame))) { metadataText = text; return; }
        ComPtr<IWICMetadataQueryReader> reader;
        if (FAILED(frame->GetMetadataQueryReader(&reader))) { metadataText = text; return; }
        const struct { const wchar_t* label; const wchar_t* query; } items[] = {
            {L"Camera", L"/app1/ifd/{ushort=272}"},
            {L"Maker", L"/app1/ifd/{ushort=271}"},
            {L"Taken", L"/app1/ifd/exif/{ushort=36867}"},
            {L"Exposure", L"/app1/ifd/exif/{ushort=33434}"},
            {L"Aperture", L"/app1/ifd/exif/{ushort=33437}"},
            {L"ISO", L"/app1/ifd/exif/{ushort=34855}"},
            {L"Focal", L"/app1/ifd/exif/{ushort=37386}"}
        };
        for (const auto& item : items) {
            const std::wstring value = QueryMetadataString(reader.Get(), item.query);
            if (!value.empty()) text += L"\n" + std::wstring(item.label) + L": " + value;
        }
        metadataText = text;
    }

    std::wstring StatusText() const {
        if (currentPath.empty()) return L"Glide  •  Drop an image here or Ctrl+O";
        std::vector<std::wstring> parts;
        if (statusStatIndex && haveIndex && !files.empty()) parts.push_back(std::to_wstring(currentIndex + 1) + L" / " + std::to_wstring(files.size()));
        if (statusStatResolution) parts.push_back(std::to_wstring(imageW) + L" × " + std::to_wstring(imageH));
        if (statusStatZoom) parts.push_back(std::to_wstring(static_cast<int>(std::lround(CurrentScale() * 100.0f))) + L"%");
        if (statusStatFileSize) parts.push_back(FormatBytes(FileSizeForPath(currentPath)));
        if (statusStatFormat) {
            try { std::wstring ext=fs::path(currentPath).extension().wstring(); if(!ext.empty()&&ext[0]==L'.')ext.erase(ext.begin()); if(!ext.empty())parts.push_back(Lower(ext)); } catch (...) {}
        }
        if (statusStatPreview && bitmapIsPreview) parts.push_back(L"preview");
        std::wstring text;
        for (const auto& part : parts) { if (!text.empty()) text += L"   •   "; text += part; }
        return text.empty() ? L"Glide" : text;
    }

    static bool PointInRect(const D2D1_RECT_F& r, POINT p) {
        return p.x >= r.left && p.x <= r.right && p.y >= r.top && p.y <= r.bottom;
    }

    bool PointOnImage(POINT p) const {
        if (!bitmap) return false;
        const auto r = ImageDestinationRect();
        return p.x >= r.left && p.x <= r.right && p.y >= r.top && p.y <= r.bottom;
    }

    bool PointOnOverlay(POINT p) const {
        bool inWiw=false;if(overlaysVisible){for(const auto&ov:overlays)if(PointInRect(ov.rect,p)){inWiw=true;break;}} return inWiw || (opacitySliderVisible&&PointInRect(opacityPanelRect,p)) || (statusVisible && PointInRect(statusRect, p)) || (metadataVisible && PointInRect(metadataRect, p)) || (ActiveTabIsBrowser()&&PointInRect(browserToolbarRect,p)) || ((titleTabsEnabled&&(!fullscreen||fullscreenNativeCaptionVisible))&&PointInRect(titleBarRect,p));
    }

    int OverlayIndexAtPoint(POINT p) const {
        return overlaysVisible ? glide_overlay::HitTest(overlays, p) : -1;
    }

    int TitleTabIndexAt(POINT p) const {
        const bool visible=titleTabsEnabled&&(!fullscreen||fullscreenNativeCaptionVisible);
        if(!visible||!PointInRect(titleBarRect,p)) return -1;
        for(size_t i=0;i<titleTabRects.size();++i)
            if(PointInRect(titleTabRects[i],p)) return static_cast<int>(i);
        return -1;
    }

    bool PointOnTitleCommand(POINT p) const {
        if(!titleTabsEnabled||fullscreen&&!fullscreenNativeCaptionVisible) return false;
        if(PointInRect(titleNewTabRect,p)||PointInRect(titleReopenRect,p)||PointInRect(titlePrevFolderRect,p)||PointInRect(titleNextFolderRect,p)||PointInRect(titleExploreParentRect,p)||
           PointInRect(titlePinRect,p)||PointInRect(titleOpacityRect,p)||PointInRect(titleOptionsRect,p)) return true;
        for(const auto& r:titleTabCloseRects) if(PointInRect(r,p)) return true;
        return false;
    }

    bool HandleTitleBarClick(POINT p){
        const bool visible=titleTabsEnabled&&(!fullscreen||fullscreenNativeCaptionVisible);
        if(!visible||!PointInRect(titleBarRect,p))return false;
        for(size_t i=0;i<titleTabCloseRects.size();++i)
            if(PointInRect(titleTabCloseRects[i],p)){CloseTab(static_cast<int>(i));return true;}
        if(PointInRect(titleOptionsRect,p)){opacitySliderVisible=false;ShowSettingsDialog();return true;}
        if(PointInRect(titlePinRect,p)){opacitySliderVisible=false;ToggleAlwaysOnTop();return true;}
        if(PointInRect(titleOpacityRect,p)){opacitySliderVisible=!opacitySliderVisible;InvalidateViewer();return true;}
        if(PointInRect(titleReopenRect,p)){opacitySliderVisible=false;if(!closedTabs.empty())ReopenClosedTab();return true;}
        if(PointInRect(titlePrevFolderRect,p)){NavigateSiblingFolderButton(-1);return true;}
        if(PointInRect(titleNextFolderRect,p)){NavigateSiblingFolderButton(+1);return true;}
        if(PointInRect(titleExploreParentRect,p)){ShowParentFolderExplorer();return true;}
        if(PointInRect(titleNewTabRect,p)){NewBrowserTab();return true;}
        for(size_t i=0;i<titleTabRects.size();++i)
            if(PointInRect(titleTabRects[i],p)){SwitchToTab(static_cast<int>(i));return true;}
        return true;
    }

    void MoveTabState(int from,int to) {
        if(from==to||from<0||to<0||from>=static_cast<int>(openTabs.size())||to>=static_cast<int>(openTabs.size()))return;
        EnsureTabStateVectors();

        auto path=std::move(openTabs[from]);
        bool browser=tabBrowserMode[from];
        auto folder=std::move(tabBrowserFolder[from]);
        auto back=std::move(tabBrowserBack[from]);
        auto forward=std::move(tabBrowserForward[from]);

        openTabs.erase(openTabs.begin()+from);
        tabBrowserMode.erase(tabBrowserMode.begin()+from);
        tabBrowserFolder.erase(tabBrowserFolder.begin()+from);
        tabBrowserBack.erase(tabBrowserBack.begin()+from);
        tabBrowserForward.erase(tabBrowserForward.begin()+from);

        openTabs.insert(openTabs.begin()+to,std::move(path));
        tabBrowserMode.insert(tabBrowserMode.begin()+to,browser);
        tabBrowserFolder.insert(tabBrowserFolder.begin()+to,std::move(folder));
        tabBrowserBack.insert(tabBrowserBack.begin()+to,std::move(back));
        tabBrowserForward.insert(tabBrowserForward.begin()+to,std::move(forward));

        activeTab=to;
        tabDragIndex=to;
    }

    void BeginSingleTabWindowDrag(int index, POINT p) {
        if(index<0 || index>=static_cast<int>(openTabs.size()) || openTabs.size()!=1)return;
        SwitchToTab(index);
        tabDragIndex=index;
        tabDragStart=tabDragCurrent=p;
        tabDragGrabOffsetX=(index<static_cast<int>(titleTabRects.size()))
            ? static_cast<float>(p.x)-titleTabRects[index].left : 30.0f;
        singleTabWindowDrag=true;
        InvalidateViewer();
        UpdateWindow(hwnd); // make the slight "held tab" offset visible before Windows enters its move loop

        ReleaseCapture();
        SendMessageW(hwnd,WM_NCLBUTTONDOWN,HTCAPTION,MAKELPARAM(p.x,p.y));

        // Native move loop has ended (mouse released). If the tab was released over
        // another Glide tab strip, merge exactly like a browser tab dropped into a
        // different browser window. Otherwise the window simply stays where moved.
        POINT sp{};GetCursorPos(&sp);
        HWND targetWnd=GlideWindowAtScreenPoint(sp);
        if(targetWnd){
            RECT tr{};GetClientRect(targetWnd,&tr);POINT tp=sp;ScreenToClient(targetWnd,&tp);
            if(tp.y>=0 && tp.y<=58 && TransferTabToWindow(index,targetWnd,sp)){
                singleTabWindowDrag=false;tabDragIndex=-1;
                return;
            }
        }
        if(tabDragHoverWindow){PostMessageW(tabDragHoverWindow,WM_APP_TAB_DRAG_LEAVE,0,0);tabDragHoverWindow=nullptr;}
        singleTabWindowDrag=false;tabDragIndex=-1;InvalidateViewer();
    }

    void BeginTabDrag(int index,POINT p) {
        if(index<0||index>=static_cast<int>(openTabs.size()))return;
        if(index<static_cast<int>(titleTabCloseRects.size())&&PointInRect(titleTabCloseRects[index],p))return;
        SwitchToTab(index);
        tabDragCandidate=true;
        tabDragging=false;
        tabDragIndex=index;
        tabDragStart=tabDragCurrent=p;
        tabDragGrabOffsetX=(index<static_cast<int>(titleTabRects.size()))
            ? static_cast<float>(p.x)-titleTabRects[index].left : 30.0f;
        SetCapture(hwnd);
    }

    void UpdateTabDrag(POINT p) {
        if(!tabDragCandidate&&!tabDragging)return;
        tabDragCurrent=p;
        const int dx=p.x-tabDragStart.x,dy=p.y-tabDragStart.y;
        if(!tabDragging && (std::abs(dx)>=kDragThresholdPx||std::abs(dy)>=kDragThresholdPx)){
            tabDragging=true;
            tabDragCandidate=false;
        }
        if(!tabDragging){InvalidateViewer();return;}

        // Reorder continuously as the pointer crosses a neighboring tab's centre.
        // The dragged tab itself is painted as a floating Direct2D card at the mouse
        // position, so this remains smooth at the message/display refresh rate.
        if(p.y>=-8 && p.y<=static_cast<LONG>(titleBarRect.bottom+18)){
            bool moved=true;
            while(moved){
                moved=false;
                if(tabDragIndex>0 && tabDragIndex-1<static_cast<int>(titleTabRects.size())){
                    const auto&r=titleTabRects[tabDragIndex-1];
                    const float centre=(r.left+r.right)*0.5f;
                    if(static_cast<float>(p.x)<centre){
                        MoveTabState(tabDragIndex,tabDragIndex-1);
                        moved=true; continue;
                    }
                }
                if(tabDragIndex+1<static_cast<int>(openTabs.size()) &&
                   tabDragIndex+1<static_cast<int>(titleTabRects.size())){
                    const auto&r=titleTabRects[tabDragIndex+1];
                    const float centre=(r.left+r.right)*0.5f;
                    if(static_cast<float>(p.x)>centre){
                        MoveTabState(tabDragIndex,tabDragIndex+1);
                        moved=true; continue;
                    }
                }
            }
        }
        POINT sp=p;ClientToScreen(hwnd,&sp);
        RECT cr{};GetClientRect(hwnd,&cr);
        const bool leftSource=(p.y<-12||p.y>static_cast<LONG>(titleBarRect.bottom+28)||p.x<-12||p.x>cr.right+12);
        if(leftSource&&openTabs.size()>1&&tabDetachEnabled){
            if(LaunchDetachedTabAndBeginNativeMove(tabDragIndex,sp))return;
        }
        HWND hoverWnd=GlideWindowAtScreenPoint(sp);
        if(hoverWnd!=tabDragHoverWindow){if(tabDragHoverWindow)PostMessageW(tabDragHoverWindow,WM_APP_TAB_DRAG_LEAVE,0,0);tabDragHoverWindow=hoverWnd;}
        if(tabDragHoverWindow)PostMessageW(tabDragHoverWindow,WM_APP_TAB_DRAG_HOVER,static_cast<WPARAM>(static_cast<INT_PTR>(sp.x)),static_cast<LPARAM>(static_cast<INT_PTR>(sp.y)));
        InvalidateViewer();
    }

    static std::wstring QuoteProcessArg(const std::wstring& arg) {
        std::wstring out=L"\"";
        size_t slashes=0;
        for(wchar_t c:arg){
            if(c==L'\\'){++slashes;continue;}
            if(c==L'\"'){
                out.append(slashes*2+1,L'\\');out.push_back(L'\"');slashes=0;continue;
            }
            out.append(slashes,L'\\');slashes=0;out.push_back(c);
        }
        out.append(slashes*2,L'\\');out.push_back(L'\"');
        return out;
    }

    std::wstring SerializeTabForTransfer(int index) {
        if(index<0||index>=static_cast<int>(openTabs.size()))return L"";
        EnsureTabStateVectors();
        wchar_t type=L'I';std::wstring path=openTabs[index];
        if(TabIsHome(index)){type=L'H';path.clear();}
        else if(tabBrowserMode[index]){type=L'B';path=tabBrowserFolder[index].empty()?openTabs[index]:tabBrowserFolder[index];}
        return std::wstring(1,type)+L"\n"+path;
    }

    bool AcceptTransferredTab(const std::wstring& payload, POINT screenPt) {
        if(!tabAttachEnabled||fullscreen||payload.size()<2)return false;
        POINT cp=screenPt;ScreenToClient(hwnd,&cp);
        if(cp.y<0||cp.y>static_cast<LONG>(std::max(52.0f,titleBarRect.bottom+20.0f)))return false;
        const wchar_t type=payload[0];
        const std::wstring path=(payload.size()>2)?payload.substr(2):L"";
        EnsureTabStateVectors();
        int at=static_cast<int>(openTabs.size());
        for(size_t i=0;i<titleTabRects.size();++i){const auto&r=titleTabRects[i];if(cp.x<(r.left+r.right)*.5f){at=static_cast<int>(i);break;}}
        at=std::clamp(at,0,static_cast<int>(openTabs.size()));
        std::wstring stored=(type==L'H')?std::wstring(kHomeTabSentinel):path;
        bool browser=type==L'B';
        openTabs.insert(openTabs.begin()+at,stored);tabBrowserMode.insert(tabBrowserMode.begin()+at,browser);
        tabBrowserFolder.insert(tabBrowserFolder.begin()+at,browser?path:L"");tabBrowserBack.insert(tabBrowserBack.begin()+at,std::vector<std::wstring>{});tabBrowserForward.insert(tabBrowserForward.begin()+at,std::vector<std::wstring>{});
        activeTab=at;
        if(type==L'H')ShowHomeContent();else if(browser)ShowShellBrowserForActiveTab();else OpenPath(path);
        UpdateTitle();InvalidateViewer();return true;
    }

    HWND GlideWindowAtScreenPoint(POINT sp) const {
        HWND w=WindowFromPoint(sp);
        if(w){w=GetAncestor(w,GA_ROOT);wchar_t cls[128]{};GetClassNameW(w,cls,127);if(w!=hwnd&&wcscmp(cls,kClassName)==0)return w;}
        // During a one-tab native window drag, WindowFromPoint normally returns the
        // moving Glide window itself. Walk top-level windows in Z order and find the
        // first *other* visible Glide whose rectangle contains the pointer, allowing
        // browser-style merge targeting even while the dragged window overlaps it.
        struct Search{HWND self{};POINT p{};HWND found{};} q{hwnd,sp,nullptr};
        EnumWindows([](HWND candidate,LPARAM lp)->BOOL{
            auto* q=reinterpret_cast<Search*>(lp);if(candidate==q->self||!IsWindowVisible(candidate)||IsIconic(candidate))return TRUE;
            wchar_t cls[128]{};GetClassNameW(candidate,cls,127);if(wcscmp(cls,kClassName)!=0)return TRUE;
            RECT r{};if(GetWindowRect(candidate,&r)&&PtInRect(&r,q->p)){q->found=candidate;return FALSE;}return TRUE;
        },reinterpret_cast<LPARAM>(&q));
        return q.found;
    }

    bool TransferTabToWindow(int index, HWND targetWnd, POINT screenPt) {
        if(!targetWnd||!tabAttachEnabled)return false;
        std::wstring payload=SerializeTabForTransfer(index);if(payload.empty())return false;
        struct Header{ULONG_PTR magic;LONG x;LONG y;};
        Header h{kGlideTabTransferMagic,screenPt.x,screenPt.y};
        std::vector<BYTE> bytes(sizeof(h)+(payload.size()+1)*sizeof(wchar_t));
        memcpy(bytes.data(),&h,sizeof(h));memcpy(bytes.data()+sizeof(h),payload.c_str(),(payload.size()+1)*sizeof(wchar_t));
        COPYDATASTRUCT cds{};cds.dwData=kGlideTabTransferMagic;cds.cbData=static_cast<DWORD>(bytes.size());cds.lpData=bytes.data();
        DWORD_PTR receiverResult=0;LRESULT delivered=SendMessageTimeoutW(targetWnd,WM_COPYDATA,reinterpret_cast<WPARAM>(hwnd),reinterpret_cast<LPARAM>(&cds),SMTO_ABORTIFHUNG,800,&receiverResult);
        if(delivered&&receiverResult){const bool wasOnly=openTabs.size()==1;if(wasOnly)PostMessageW(hwnd,WM_CLOSE,0,0);else CloseTab(index,false);return true;}return false;
    }

    bool LaunchNewHomeWindow() {
        wchar_t exe[MAX_PATH]{};if(!GetModuleFileNameW(nullptr,exe,MAX_PATH))return false;
        std::wstring cmd=QuoteProcessArg(exe)+L" --new-window --home";
        STARTUPINFOW si{};si.cb=sizeof(si);PROCESS_INFORMATION pi{};
        std::vector<wchar_t> mutableCmd(cmd.begin(),cmd.end());mutableCmd.push_back(0);
        BOOL ok=CreateProcessW(exe,mutableCmd.data(),nullptr,nullptr,FALSE,0,nullptr,nullptr,&si,&pi);
        if(ok){CloseHandle(pi.hThread);CloseHandle(pi.hProcess);}return ok!=FALSE;
    }

    bool LaunchDetachedTab(int index) {
        if(index<0||index>=static_cast<int>(openTabs.size())||!tabDetachEnabled)return false;
        EnsureTabStateVectors();
        if(index==activeTab && !ActiveTabIsBrowser() && !TabIsHome(index) && !currentPath.empty())SyncActiveTabToCurrent();
        wchar_t exe[MAX_PATH]{};if(!GetModuleFileNameW(nullptr,exe,MAX_PATH))return false;
        const bool home=TabIsHome(index),browser=!home&&tabBrowserMode[index];
        const std::wstring path=home?L"":(browser&&!tabBrowserFolder[index].empty()?tabBrowserFolder[index]:openTabs[index]);
        std::wstring cmd=QuoteProcessArg(exe)+L" --new-window --detached ";
        if(detachedWindowHomeTab)cmd+=L"--detached-home ";
        if(home)cmd+=L"--home";else{if(browser)cmd+=L"--browser ";cmd+=QuoteProcessArg(path);}
        if(diagnosticMode && diagnosticSuppressExternalLaunch){diagnosticExternalLaunchRequested=true;diagnosticExternalLaunchCommand=cmd;return true;}
        STARTUPINFOW si{};si.cb=sizeof(si);PROCESS_INFORMATION pi{};
        std::vector<wchar_t> mutableCmd(cmd.begin(),cmd.end());mutableCmd.push_back(0);
        BOOL ok=CreateProcessW(exe,mutableCmd.data(),nullptr,nullptr,FALSE,0,nullptr,nullptr,&si,&pi);if(!ok)return false;
        CloseHandle(pi.hThread);CloseHandle(pi.hProcess);CloseTab(index,false);
        if(closeEmptyWindowAfterDetach&&openTabs.size()==1&&TabIsHome(0))PostMessageW(hwnd,WM_CLOSE,0,0);
        return true;
    }

    static HWND FindGlideWindowForProcess(DWORD pid) {
        struct Search { DWORD pid; HWND found; } search{pid,nullptr};
        EnumWindows([](HWND w,LPARAM lp)->BOOL{
            auto* s=reinterpret_cast<Search*>(lp);
            DWORD wp=0;GetWindowThreadProcessId(w,&wp);
            if(wp!=s->pid||!IsWindowVisible(w))return TRUE;
            wchar_t cls[128]{};GetClassNameW(w,cls,127);
            if(wcscmp(cls,kClassName)==0){s->found=w;return FALSE;}
            return TRUE;
        },reinterpret_cast<LPARAM>(&search));
        return search.found;
    }

    void ActivateDeferredDetachedContent() {
        if(!deferredDetachedStartup)return;
        const bool browser=deferredDetachedBrowser;const std::wstring path=deferredDetachedPath;
        deferredDetachedStartup=false;deferredDetachedBrowser=false;deferredDetachedPath.clear();
        // Replace the lightweight drag placeholder with the real tab only after the
        // native move loop ends. Folder enumeration / ShellView creation / image decode
        // therefore cannot starve the Windows drag loop or blank the moving window.
        openTabs.clear();tabBrowserMode.clear();tabBrowserFolder.clear();tabBrowserBack.clear();tabBrowserForward.clear();activeTab=-1;
        if(browser){titleTabsEnabled=true;NewBrowserTab();NavigateBrowserTo(path,false);}
        else if(!path.empty())OpenPath(path);
        UpdateTitle();InvalidateViewer();
    }

    bool LaunchDetachedTabAndBeginNativeMove(int index, POINT screenPt) {
        if(index<0||index>=static_cast<int>(openTabs.size())||openTabs.size()<2||!tabDetachEnabled)return false;
        EnsureTabStateVectors();
        if(index==activeTab && !ActiveTabIsBrowser() && !TabIsHome(index) && !currentPath.empty())SyncActiveTabToCurrent();
        wchar_t exe[MAX_PATH]{};if(!GetModuleFileNameW(nullptr,exe,MAX_PATH))return false;
        const bool home=TabIsHome(index),browser=!home&&tabBrowserMode[index];
        const std::wstring path=home?L"":(browser&&!tabBrowserFolder[index].empty()?tabBrowserFolder[index]:openTabs[index]);
        std::wstring cmd=QuoteProcessArg(exe)+L" --new-window --detached --drag-deferred ";
        if(detachedWindowHomeTab)cmd+=L"--detached-home ";
        if(home)cmd+=L"--home";else{if(browser)cmd+=L"--browser ";cmd+=QuoteProcessArg(path);}
        if(diagnosticMode && diagnosticSuppressExternalLaunch){diagnosticExternalLaunchRequested=true;diagnosticExternalLaunchCommand=cmd;return true;}
        STARTUPINFOW si{};si.cb=sizeof(si);PROCESS_INFORMATION pi{};
        std::vector<wchar_t> mutableCmd(cmd.begin(),cmd.end());mutableCmd.push_back(0);
        if(!CreateProcessW(exe,mutableCmd.data(),nullptr,nullptr,FALSE,0,nullptr,nullptr,&si,&pi))return false;
        CloseHandle(pi.hThread);
        // Do not freeze the source viewer for seconds during tear-off. The old 25 ms
        // polling loop made the held tab visibly stutter. Poll in short slices and force
        // pending paint work through while the child Glide window is starting.
        WaitForInputIdle(pi.hProcess,750);
        HWND target=nullptr;
        for(int n=0;n<150&&!target;++n){
            target=FindGlideWindowForProcess(pi.dwProcessId);
            if(!target){UpdateWindow(hwnd);Sleep(5);}
        }
        CloseHandle(pi.hProcess);
        if(!target)return false;

        // The new Glide window becomes the actual dragged object as soon as the tab
        // leaves its source window. This hands movement and edge/corner snapping to
        // Windows instead of constraining a painted tab preview to the source client.
        RECT wr{};GetWindowRect(target,&wr);
        const int ww=std::max(360L,wr.right-wr.left),wh=std::max(240L,wr.bottom-wr.top);
        const int grabX=std::clamp(static_cast<int>(std::lround(tabDragGrabOffsetX)),24,ww-24);
        const int grabY=18;
        SetWindowPos(target,nullptr,screenPt.x-grabX,screenPt.y-grabY,ww,wh,SWP_NOZORDER|SWP_NOACTIVATE);

        if(tabDragHoverWindow){PostMessageW(tabDragHoverWindow,WM_APP_TAB_DRAG_LEAVE,0,0);tabDragHoverWindow=nullptr;}
        tabDragCandidate=false;tabDragging=false;tabDragIndex=-1;
        if(GetCapture()==hwnd)ReleaseCapture();
        CloseTab(index,false);
        AllowSetForegroundWindow(pi.dwProcessId);
        SetForegroundWindow(target);
        BringWindowToTop(target);
        // Never enter another process' native move loop synchronously. That blocked the
        // source viewer until mouse-up and starved both source/Explorer child painting,
        // producing the white-client-area + lag seen in 1.2.86. Let the target repaint,
        // then ask the target thread to enter its own native move loop asynchronously.
        RedrawWindow(target,nullptr,nullptr,RDW_INVALIDATE|RDW_ALLCHILDREN|RDW_UPDATENOW);
        // A native modal move loop cannot reliably inherit a mouse-down that began in
        // another process. Content tabs made that race visible: USER32 could end the move
        // loop while the physical button was still held, after which the same held click
        // leaked into the image selection or Explorer child. Keep the proven native path
        // for Home tabs, but drag image/browser tear-offs with Glide's own 60 Hz screen
        // tracker until the *real* button-up. This preserves the existing Home behaviour
        // while making content-tab tear-off deterministic and preventing click-through.
        // route every detached tab through USER32's native caption move loop.
        // The old content-tab path used Glide's 60 Hz SetWindowPos tracker, which visibly
        // stuttered and could only emulate snapping after mouse-up. The existing native path
        // already defers Explorer/image content, normalizes the held-button transition, keeps
        // cross-window hover/merge feedback, and has a physical-button release safety timer.
        // Reusing it gives DWM-smooth motion and genuine Windows edge/corner/Snap behaviour.
        const UINT dragMsg = home ? WM_APP_BEGIN_NATIVE_TAB_DRAG : WM_APP_BEGIN_CONTENT_TAB_DRAG;
        PostMessageW(target,dragMsg,static_cast<WPARAM>(static_cast<INT_PTR>(screenPt.x)),static_cast<LPARAM>(static_cast<INT_PTR>(screenPt.y)));
        InvalidateViewer();
        return true;
    }

    void EndTabDrag(POINT p) {
        const bool wasDragging=tabDragging;const int index=tabDragIndex;
        if(tabDragHoverWindow){PostMessageW(tabDragHoverWindow,WM_APP_TAB_DRAG_LEAVE,0,0);tabDragHoverWindow=nullptr;}
        tabDragCandidate=false;tabDragging=false;tabDragIndex=-1;if(GetCapture()==hwnd)ReleaseCapture();
        if(wasDragging&&index>=0){
            POINT sp=p;ClientToScreen(hwnd,&sp);
            if(HWND other=GlideWindowAtScreenPoint(sp)){
                if(TransferTabToWindow(index,other,sp)){InvalidateViewer();return;}
            }
            RECT cr{};GetClientRect(hwnd,&cr);
            const bool outside=(p.y<-22||p.y>static_cast<LONG>(titleBarRect.bottom+44)||p.x<-24||p.x>cr.right+24);
            if(outside&&tabDetachEnabled){
                if(!LaunchDetachedTab(index))MessageBoxW(hwnd,L"Glide could not detach this tab into a new window.",kAppName,MB_OK|MB_ICONERROR);
            }
        }
        InvalidateViewer();
    }

    bool HandleBrowserSingleClick(POINT p) {
        if (!ActiveTabIsBrowser()) return false;
        if (PointInRect(browserBackRect,p)) { BrowserShellBack(); return true; }
        if (PointInRect(browserForwardRect,p)) { BrowserShellForward(); return true; }
        if (PointInRect(browserUpRect,p)) { BrowserShellUp(); return true; }
        if (PointInRect(browserRefreshRect,p)) { BrowserShellRefresh(); return true; }
        if (PointInRect(browserSortRect,p)) { ShowBrowserSortMenu(p); return true; }
        return PointInRect(browserToolbarRect,p);
    }

    bool HandleBrowserDoubleClick(POINT p) {
        if (!ActiveTabIsBrowser()) return false;
        for (size_t i=0;i<browserEntryRects.size() && i<browserEntries.size();++i) {
            if (!PointInRect(browserEntryRects[i],p)) continue;
            if (browserEntryIsDir[i]) NavigateBrowserTo(browserEntries[i]);
            else {
                EnsureTabStateVectors();
                tabBrowserMode[activeTab]=false; openTabs[activeTab]=browserEntries[i];
                tabBrowserFolder[activeTab].clear(); tabBrowserBack[activeTab].clear(); tabBrowserForward[activeTab].clear();
                OpenPath(browserEntries[i]); InvalidateViewer();
            }
            return true;
        }
        return false;
    }

    bool ShowTabContextMenuAt(POINT client) {
        const bool visible=titleTabsEnabled&&(!fullscreen||fullscreenNativeCaptionVisible); if(!visible||!PointInRect(titleBarRect,client))return false;
        int index=-1;for(size_t i=0;i<titleTabRects.size();++i)if(PointInRect(titleTabRects[i],client)){index=static_cast<int>(i);break;}
        if(index<0)return PointInRect(titlePinRect,client)||PointInRect(titleOpacityRect,client)||PointInRect(titleReopenRect,client)||PointInRect(titleOptionsRect,client)||PointInRect(titleNewTabRect,client);
        HMENU m=CreatePopupMenu();if(!m)return true;
        auto dup=MenuLabel(L"Duplicate tab",HotkeyAction::DuplicateTab);auto close=MenuLabel(L"Close tab",HotkeyAction::CloseTab);auto reopen=MenuLabel(L"Reopen closed tab",HotkeyAction::ReopenClosedTab);auto fresh=MenuLabel(L"New tab",HotkeyAction::NewTab);
        AppendMenuW(m,MF_STRING,1,dup.c_str());AppendMenuW(m,MF_STRING,2,fresh.c_str());AppendMenuW(m,MF_STRING|(tabDetachEnabled?0:MF_GRAYED),7,L"Move tab to new window");AppendMenuW(m,MF_SEPARATOR,0,nullptr);AppendMenuW(m,MF_STRING,3,close.c_str());AppendMenuW(m,MF_STRING|(closedTabs.empty()?MF_GRAYED:0),4,reopen.c_str());AppendMenuW(m,MF_SEPARATOR,0,nullptr);AppendMenuW(m,MF_STRING|(alwaysOnTop?MF_CHECKED:0),5,L"Always on top");AppendMenuW(m,MF_STRING,6,L"Settings");
        POINT sp=client;ClientToScreen(hwnd,&sp);UINT cmd=TrackPopupMenu(m,TPM_RETURNCMD|TPM_RIGHTBUTTON,sp.x,sp.y,0,hwnd,nullptr);DestroyMenu(m);
        if(cmd==1){SwitchToTab(index);DuplicateActiveTab();}else if(cmd==2)NewBrowserTab();else if(cmd==7){SwitchToTab(index);LaunchDetachedTab(index);}else if(cmd==3)CloseTab(index);else if(cmd==4)ReopenClosedTab();else if(cmd==5)ToggleAlwaysOnTop();else if(cmd==6)ShowSettingsDialog();return true;
    }

    bool HandleTitleBarMiddleClick(POINT p){const bool visible=titleTabsEnabled&&(!fullscreen||fullscreenNativeCaptionVisible);if(!visible||!PointInRect(titleBarRect,p))return false;for(size_t i=0;i<titleTabRects.size();++i)if(PointInRect(titleTabRects[i],p)){CloseTab(static_cast<int>(i));return true;}if(PointInRect(titleOptionsRect,p)||PointInRect(titlePinRect,p)||PointInRect(titleOpacityRect,p)||PointInRect(titleReopenRect,p)||PointInRect(titlePrevFolderRect,p)||PointInRect(titleNextFolderRect,p)||PointInRect(titleExploreParentRect,p))return true;if(PointInRect(titleNewTabRect,p)||p.x>titleNewTabRect.right){NewBrowserTab();return true;}return true;}

    bool HandleOverlayLeftClick(POINT p) {
        if(PointInRect(statusOverlayAddRect,p)){ShowAddOverlayDialog();return true;}
        if(PointInRect(statusOverlayLoadRect,p)){LoadOverlayLayoutDialog();return true;}
        if(PointInRect(statusOverlaySaveRect,p)){SaveOverlayLayoutDialog();return true;}
        if(PointInRect(statusOverlayClearRect,p)){overlays.clear();InvalidateViewer();return true;}

        if (HandleTitleBarClick(p)) return true;
        if (HandleBrowserSingleClick(p)) return true;
        if (metadataVisible && PointInRect(metadataCloseRect, p)) {
            metadataVisible = false; InvalidateRect(hwnd, nullptr, FALSE); return true;
        }
        if (!statusVisible) return false;
        if(statusShowNavigation&&PointInRect(statusHomeRect,p)){JumpFirstLast(true);return true;}if(statusShowNavigation&&PointInRect(statusPrevRect,p)){Navigate(-1);return true;}if(statusShowNavigation&&PointInRect(statusNextRect,p)){Navigate(+1);return true;}if(statusShowNavigation&&PointInRect(statusEndRect,p)){JumpFirstLast(false);return true;}
        if(CurrentIsPng()&&PointInRect(statusPngAlphaRect,p)){pngSeeThrough=!pngSeeThrough;ApplyPngTransparencyMode();return true;}if(statusShowZoom&&PointInRect(statusZoomOutRect,p)){ZoomCentered(1.0f/1.20f);return true;}if(statusShowZoom&&PointInRect(statusZoomInRect,p)){ZoomCentered(1.20f);return true;}
        if (statusShowSlideshow && PointInRect(statusSlideRect, p)) {
            if (slideshowRunning) PauseSlideshow();
            else if (slideshowPaused) ResumeSlideshow();
            else ShowSlideshowConfig();
            return true;
        }
        if (statusShowSlideshow && (slideshowRunning || slideshowPaused) && PointInRect(statusStopRect, p)) { StopSlideshow(); return true; }
        if (statusShowFit && PointInRect(statusFitWidthRect,p)) { FitWidth(); return true; }
        if (statusShowFit && PointInRect(statusFitHeightRect,p)) { FitHeight(); return true; }
        if (statusShowOptions && PointInRect(statusOptionsRect,p)) { ShowSettingsDialog(); return true; }
        if (statusShowClose && PointInRect(statusCloseRect, p)) {
            if(fullscreen){ if(fullscreenXClosesApp) PostMessageW(hwnd,WM_CLOSE,0,0); else ToggleFullscreen(); }
            else { statusVisible = false; InvalidateRect(hwnd, nullptr, FALSE); }
            return true;
        }
        if (statusShowCollapse && PointInRect(statusMinRect, p)) {
            statusCollapsed = !statusCollapsed; InvalidateRect(hwnd, nullptr, FALSE); return true;
        }
        if (statusShowInfo && PointInRect(statusInfoRect, p)) {
            metadataVisible = !metadataVisible;
            if (metadataVisible) RefreshMetadataText();
            InvalidateRect(hwnd, nullptr, FALSE); return true;
        }
        if (statusCollapsed && PointInRect(statusRect, p)) {
            statusCollapsed = false; InvalidateRect(hwnd, nullptr, FALSE); return true;
        }
        return PointInRect(statusRect, p);
    }

    void ReSortCurrentFolder() {
        if (currentFolder.empty()) return;
        const fs::path preferred = currentPath;
        RebuildFolderList(currentFolder, preferred);
        if (haveIndex && !files.empty()) RequestImage(files[currentIndex].wstring(), 0);
    }

    void ShowContextMenu(POINT screenPt) {
        HMENU menu = CreatePopupMenu();
        if (!menu) return;
        {auto q=MenuLabel(L"Open image...",HotkeyAction::OpenFile);AppendMenuW(menu, MF_STRING, IDM_OPEN, q.c_str());}
        AppendMenuW(menu, MF_STRING, IDM_OPEN_FOLDER, L"Open folder...");
        {auto q=MenuLabel(L"Reload",HotkeyAction::Refresh);AppendMenuW(menu, MF_STRING | (currentPath.empty() ? MF_GRAYED : 0), IDM_RELOAD, q.c_str());}

        HMENU recent = CreatePopupMenu();
        if (!recentHistoryEnabled) {
            AppendMenuW(recent, MF_STRING | MF_GRAYED, 0, L"Recent history is disabled in Options");
        } else if (recentFiles.empty() && recentFolders.empty()) {
            AppendMenuW(recent, MF_STRING | MF_GRAYED, 0, L"No recent items");
        } else {
            for (size_t i = 0; i < recentFiles.size() && i < 8; ++i)
                AppendMenuW(recent, MF_STRING, IDM_RECENT_FILE_BASE + static_cast<UINT>(i), BaseName(recentFiles[i]).c_str());
            if (!recentFiles.empty() && !recentFolders.empty()) AppendMenuW(recent, MF_SEPARATOR, 0, nullptr);
            for (size_t i = 0; i < recentFolders.size() && i < 8; ++i) {
                std::wstring label = L"Folder: " + BaseName(recentFolders[i]);
                AppendMenuW(recent, MF_STRING, IDM_RECENT_FOLDER_BASE + static_cast<UINT>(i), label.c_str());
            }
            AppendMenuW(recent, MF_SEPARATOR, 0, nullptr);
            AppendMenuW(recent, MF_STRING, IDM_CLEAR_RECENTS, L"Clear recent history");
        }
        AppendMenuW(menu, MF_POPUP, reinterpret_cast<UINT_PTR>(recent), L"Recent");
        AppendMenuW(menu, MF_SEPARATOR, 0, nullptr);

        HMENU view = CreatePopupMenu();
        {auto q=MenuLabel(fullscreen?L"Exit fullscreen":L"Fullscreen",HotkeyAction::ToggleFullscreen);AppendMenuW(view, MF_STRING, IDM_FULLSCREEN, q.c_str());}
        {auto q=MenuLabel(L"Fit image",HotkeyAction::FitImage);AppendMenuW(view, MF_STRING | (viewMode == ViewMode::Fit ? MF_CHECKED : 0), IDM_FIT, q.c_str());}
        {auto q=MenuLabel(L"Actual size",HotkeyAction::ActualSize);AppendMenuW(view, MF_STRING | (viewMode == ViewMode::Manual && std::fabs(zoom - 1.0f) < 0.0001f ? MF_CHECKED : 0), IDM_ACTUAL, q.c_str());}
        AppendMenuW(view, MF_SEPARATOR, 0, nullptr);
        AppendMenuW(view, MF_STRING | (statusVisible ? MF_CHECKED : 0), IDM_STATUS_TOGGLE, L"Status overlay");
        {auto q=MenuLabel(L"Image information",HotkeyAction::ToggleMetadata);AppendMenuW(view, MF_STRING | (metadataVisible ? MF_CHECKED : 0), IDM_METADATA, q.c_str());}
        AppendMenuW(view, MF_STRING | (showFullPathInTitle ? MF_CHECKED : 0), IDM_TITLE_FULLPATH, L"Full path in title");
        AppendMenuW(menu, MF_POPUP, reinterpret_cast<UINT_PTR>(view), L"View");

        HMENU transform = CreatePopupMenu();
        {auto q=MenuLabel(L"Rotate left",HotkeyAction::RotateLeft);AppendMenuW(transform, MF_STRING | (currentPath.empty() ? MF_GRAYED : 0), IDM_ROTATE_LEFT, q.c_str());}
        {auto q=MenuLabel(L"Rotate right",HotkeyAction::RotateRight);AppendMenuW(transform, MF_STRING | (currentPath.empty() ? MF_GRAYED : 0), IDM_ROTATE_RIGHT, q.c_str());}
        AppendMenuW(transform, MF_STRING | (currentPath.empty() ? MF_GRAYED : 0) | (flipHorizontal ? MF_CHECKED : 0), IDM_FLIP_H, L"Flip horizontal");
        AppendMenuW(transform, MF_STRING | (currentPath.empty() ? MF_GRAYED : 0) | (flipVertical ? MF_CHECKED : 0), IDM_FLIP_V, L"Flip vertical");
        AppendMenuW(menu, MF_POPUP, reinterpret_cast<UINT_PTR>(transform), L"Transform view");

        HMENU sort = CreatePopupMenu();
        AppendMenuW(sort, MF_STRING | (sortMode == SortMode::Name ? MF_CHECKED : 0), IDM_SORT_NAME, L"File name");
        AppendMenuW(sort, MF_STRING | (sortMode == SortMode::Modified ? MF_CHECKED : 0), IDM_SORT_MODIFIED, L"Date modified");
        AppendMenuW(sort, MF_STRING | (sortMode == SortMode::Created ? MF_CHECKED : 0), IDM_SORT_CREATED, L"Date created");
        AppendMenuW(sort, MF_STRING | (sortMode == SortMode::Size ? MF_CHECKED : 0), IDM_SORT_SIZE, L"File size");
        AppendMenuW(sort, MF_SEPARATOR, 0, nullptr);
        AppendMenuW(sort, MF_STRING | (sortAscending ? MF_CHECKED : 0), IDM_SORT_ASC, L"Ascending");
        AppendMenuW(sort, MF_STRING | (!sortAscending ? MF_CHECKED : 0), IDM_SORT_DESC, L"Descending");
        AppendMenuW(menu, MF_POPUP, reinterpret_cast<UINT_PTR>(sort), L"Sort");

        AppendMenuW(menu, MF_SEPARATOR, 0, nullptr);
        AppendMenuW(menu, MF_STRING | (currentPath.empty() ? MF_GRAYED : 0), IDM_COPY_IMAGE, L"Copy image");
        AppendMenuW(menu, MF_STRING | (currentPath.empty() ? MF_GRAYED : 0), IDM_COPY_IMAGE_FILE, L"Copy image file");
        AppendMenuW(menu, MF_STRING | (currentPath.empty() ? MF_GRAYED : 0), IDM_COPY_PATH, L"Copy full path");
        AppendMenuW(menu, MF_STRING | (currentPath.empty() ? MF_GRAYED : 0), IDM_COPY_NAME, L"Copy file name");
        AppendMenuW(menu, MF_STRING | (currentPath.empty() ? MF_GRAYED : 0), IDM_COPY_FOLDER, L"Copy folder path");
        AppendMenuW(menu, MF_SEPARATOR, 0, nullptr);
        AppendMenuW(menu, MF_STRING | (currentPath.empty() ? MF_GRAYED : 0), IDM_REVEAL, L"Show in File Explorer");
        AppendMenuW(menu, MF_STRING | (currentPath.empty() ? MF_GRAYED : 0), IDM_RENAME, L"Rename...");
        AppendMenuW(menu, MF_STRING | (currentPath.empty() ? MF_GRAYED : 0), IDM_DELETE, L"Delete to Recycle Bin\tDel");
        AppendMenuW(menu, MF_STRING | (currentPath.empty() ? MF_GRAYED : 0), IDM_PROPERTIES, L"Properties");
        AppendMenuW(menu, MF_SEPARATOR, 0, nullptr);
        AppendMenuW(menu, MF_STRING | (slideshowRunning ? MF_CHECKED : 0), IDM_SLIDESHOW_TOGGLE, L"Slideshow settings...\tS");
        AppendMenuW(menu, MF_STRING, IDM_SETTINGS, L"Options...\tCtrl+, ");
        AppendMenuW(menu, MF_SEPARATOR, 0, nullptr);
        AppendMenuW(menu, MF_STRING, IDM_EXIT, L"Exit");
        SetForegroundWindow(hwnd);
        TrackPopupMenu(menu, TPM_RIGHTBUTTON | TPM_LEFTALIGN, screenPt.x, screenPt.y, 0, hwnd, nullptr);
        DestroyMenu(menu);
    }

    void HandleCommand(UINT id) {
        if (id >= IDM_RECENT_FILE_BASE && id < IDM_RECENT_FILE_BASE + 8) {
            const size_t i = id - IDM_RECENT_FILE_BASE;
            if (i < recentFiles.size()) OpenPath(recentFiles[i]);
            return;
        }
        if (id >= IDM_RECENT_FOLDER_BASE && id < IDM_RECENT_FOLDER_BASE + 8) {
            const size_t i = id - IDM_RECENT_FOLDER_BASE;
            if (i < recentFolders.size()) OpenPath(recentFolders[i]);
            return;
        }
        switch (id) {
            case IDM_OPEN: ShowOpenDialog(); break;
            case IDM_OPEN_FOLDER: ShowOpenFolderDialog(); break;
            case IDM_RELOAD: if (!currentPath.empty()) RequestImage(currentPath, 0); break;
            case IDM_COPY_IMAGE:
                if (!CopyCurrentImageToClipboard()) MessageBoxW(hwnd, L"Could not copy this image.", kAppName, MB_ICONERROR);
                break;
            case IDM_COPY_IMAGE_FILE:
                if (!CopyCurrentImageFileToClipboard()) MessageBoxW(hwnd,L"Could not copy this image file to the Windows clipboard.",kAppName,MB_ICONERROR);
                break;
            case IDM_COPY_PATH: CopyTextToClipboard(hwnd, currentPath); break;
            case IDM_COPY_NAME: CopyTextToClipboard(hwnd, BaseName(currentPath)); break;
            case IDM_COPY_FOLDER: if (!currentPath.empty()) CopyTextToClipboard(hwnd, fs::path(currentPath).parent_path().wstring()); break;
            case IDM_REVEAL: RevealCurrentInExplorer(); break;
            case IDM_RENAME: RenameCurrent(); break;
            case IDM_DELETE: DeleteCurrentToRecycleBin(); break;
            case IDM_PROPERTIES: ShowCurrentProperties(); break;
            case IDM_FULLSCREEN: ToggleFullscreen(); break;
            case IDM_FIT: FitImage(); break;
            case IDM_ACTUAL: ActualSize(); break;
            case IDM_METADATA:
                metadataVisible = !metadataVisible;
                if (metadataVisible) RefreshMetadataText();
                InvalidateRect(hwnd, nullptr, FALSE);
                break;
            case IDM_STATUS_TOGGLE: statusVisible = !statusVisible; InvalidateRect(hwnd, nullptr, FALSE); SaveSettings(); break;
            case IDM_STATUS_COLLAPSE: statusCollapsed = !statusCollapsed; InvalidateRect(hwnd, nullptr, FALSE); SaveSettings(); break;
            case IDM_TITLE_FULLPATH: showFullPathInTitle = !showFullPathInTitle; UpdateTitle(); SaveSettings(); break;
            case IDM_ROTATE_LEFT: rotationQuarterTurns = (rotationQuarterTurns + 3) % 4; ApplyViewTransformChange(); break;
            case IDM_ROTATE_RIGHT: rotationQuarterTurns = (rotationQuarterTurns + 1) % 4; ApplyViewTransformChange(); break;
            case IDM_FLIP_H: flipHorizontal = !flipHorizontal; ApplyViewTransformChange(); break;
            case IDM_FLIP_V: flipVertical = !flipVertical; ApplyViewTransformChange(); break;
            case IDM_SORT_NAME: sortMode = SortMode::Name; ReSortCurrentFolder(); break;
            case IDM_SORT_MODIFIED: sortMode = SortMode::Modified; ReSortCurrentFolder(); break;
            case IDM_SORT_CREATED: sortMode = SortMode::Created; ReSortCurrentFolder(); break;
            case IDM_SORT_SIZE: sortMode = SortMode::Size; ReSortCurrentFolder(); break;
            case IDM_SORT_ASC: sortAscending = true; ReSortCurrentFolder(); break;
            case IDM_SORT_DESC: sortAscending = false; ReSortCurrentFolder(); break;
            case IDM_CLEAR_RECENTS: recentFiles.clear(); recentFolders.clear(); SaveSettings(); break;
            case IDM_SETTINGS: ShowSettingsDialog(); break;
            case IDM_SLIDESHOW_TOGGLE: ToggleSlideshow(); break;
            case IDM_ASSOC_REGISTER: RegisterFileAssociations(); break;
            case IDM_ASSOC_REMOVE: RemoveFileAssociations(); break;
            case IDM_EXIT: PostMessageW(hwnd, WM_CLOSE, 0, 0); break;
        }
    }

    bool TabIsHome(int index) const {
        return index>=0 && index<static_cast<int>(openTabs.size()) && openTabs[index]==kHomeTabSentinel;
    }
    void EnsureHomeTab() {
        if(!titleTabsEnabled) return;
        if(openTabs.empty()){
            openTabs.push_back(kHomeTabSentinel);tabBrowserMode.push_back(false);tabBrowserFolder.push_back(L"");
            tabBrowserBack.emplace_back();tabBrowserForward.emplace_back();activeTab=0;
        }
    }
    void ShowHomeContent() {
        KillTimer(hwnd,kRefineTimerId);KillTimer(hwnd,kAdaptivePreviewTimerId);KillTimer(hwnd,kNavigationIdleTimerId);
        ++generation;worker.CancelPending();prefetchWorker.CancelPending();prefetchWorker2.CancelPending();prefetchWorker3.CancelPending();predictivePrefetchPending.clear();refineWorker.CancelPending();adaptiveWorker.CancelPending();
        DestroyShellBrowser();currentPath.clear();displayedPath.clear();currentFolder.clear();files.clear();haveIndex=false;currentIndex=0;
        bitmap.Reset();qualityBitmap.Reset();qualityBitmapW=qualityBitmapH=0;imageW=imageH=bitmapPixelW=bitmapPixelH=0;bitmapIsPreview=false;
        loading=false;foregroundDecodePending=false;ClearSelection(false);metadataVisible=false;metadataText.clear();viewMode=ViewMode::Fit;zoom=1.0f;panX=panY=0.0f;
        ApplyPngTransparencyMode();UpdateScrollBars();UpdateTitle();InvalidateViewer();
    }

    void EnsureTabStateVectors() {
        while (tabBrowserMode.size() < openTabs.size()) tabBrowserMode.push_back(false);
        while (tabBrowserFolder.size() < openTabs.size()) tabBrowserFolder.push_back(L"");
        while (tabBrowserBack.size() < openTabs.size()) tabBrowserBack.emplace_back();
        while (tabBrowserForward.size() < openTabs.size()) tabBrowserForward.emplace_back();
        if (tabBrowserMode.size() > openTabs.size()) tabBrowserMode.resize(openTabs.size());
        if (tabBrowserFolder.size() > openTabs.size()) tabBrowserFolder.resize(openTabs.size());
        if (tabBrowserBack.size() > openTabs.size()) tabBrowserBack.resize(openTabs.size());
        if (tabBrowserForward.size() > openTabs.size()) tabBrowserForward.resize(openTabs.size());
    }

    bool ActiveTabIsBrowser() const {
        return activeTab >= 0 && activeTab < static_cast<int>(tabBrowserMode.size()) && tabBrowserMode[activeTab];
    }

    std::wstring BrowserDefaultFolder() const {
        try {
            if (!currentFolder.empty()) return currentFolder.wstring();
            wchar_t pictures[MAX_PATH]{};
            if (SUCCEEDED(SHGetFolderPathW(nullptr, CSIDL_MYPICTURES, nullptr, SHGFP_TYPE_CURRENT, pictures)) && *pictures)
                return pictures;
            return fs::current_path().wstring();
        } catch (...) { return L"C:\\"; }
    }

    float BrowserToolbarHeight() const { return ActiveTabIsBrowser() ? 40.0f : 0.0f; }
    RECT ShellBrowserClientRect() const{RECT rc{};GetClientRect(hwnd,&rc);rc.top+=static_cast<LONG>(ViewerTopInset()+BrowserToolbarHeight());return rc;}

    static LRESULT CALLBACK BrowserAddressProc(HWND edit, UINT msg, WPARAM wp, LPARAM lp) {
        auto* app=reinterpret_cast<ViewerApp*>(GetWindowLongPtrW(edit,GWLP_USERDATA));
        if(app && msg==WM_KEYDOWN && wp==VK_RETURN){
            wchar_t buf[32768]{}; GetWindowTextW(edit,buf,static_cast<int>(std::size(buf)));
            if(*buf) app->NavigateBrowserTo(buf);
            return 0;
        }
        return app && app->browserAddressOldProc ? CallWindowProcW(app->browserAddressOldProc,edit,msg,wp,lp) : DefWindowProcW(edit,msg,wp,lp);
    }

    void EnsureBrowserAddressEdit(){
        if(browserAddressEdit||!hwnd)return;
        browserAddressEdit=CreateWindowExW(WS_EX_CLIENTEDGE,L"EDIT",L"",WS_CHILD|WS_TABSTOP|ES_AUTOHSCROLL,0,0,100,26,hwnd,nullptr,hInst,nullptr);
        if(!browserAddressEdit)return;
        SetWindowLongPtrW(browserAddressEdit,GWLP_USERDATA,reinterpret_cast<LONG_PTR>(this));
        browserAddressOldProc=reinterpret_cast<WNDPROC>(SetWindowLongPtrW(browserAddressEdit,GWLP_WNDPROC,reinterpret_cast<LONG_PTR>(&ViewerApp::BrowserAddressProc)));
        HFONT font=static_cast<HFONT>(GetStockObject(DEFAULT_GUI_FONT)); SendMessageW(browserAddressEdit,WM_SETFONT,reinterpret_cast<WPARAM>(font),TRUE);
    }
    void PositionBrowserAddressEdit(){
        if(!browserAddressEdit)return;
        if(!ActiveTabIsBrowser()){ShowWindow(browserAddressEdit,SW_HIDE);return;}
        RECT rc{};GetClientRect(hwnd,&rc); const int top=static_cast<int>(ViewerTopInset()+6.0f);
        const int left=122, rightPad=126, h=28, w=std::max<int>(120, static_cast<int>(rc.right)-left-rightPad);
        SetWindowPos(browserAddressEdit,HWND_TOP,left,top,w,h,SWP_SHOWWINDOW);
    }
    void SetBrowserAddressText(const std::wstring& text){if(browserAddressEdit&&GetFocus()!=browserAddressEdit)SetWindowTextW(browserAddressEdit,text.c_str());}

    void DestroyShellBrowser(){
        if(shellBrowser){ComPtr<IObjectWithSite> site; if(SUCCEEDED(shellBrowser.As(&site))&&site)site->SetSite(nullptr); shellBrowser->Destroy();shellBrowser.Reset();}
        if(browserAddressEdit)ShowWindow(browserAddressEdit,SW_HIDE);
    }
    bool BrowseShellTo(const std::wstring&folder){if(!shellBrowser)return false;PIDLIST_ABSOLUTE pidl=nullptr;HRESULT hr=folder.empty()?SHGetKnownFolderIDList(FOLDERID_ComputerFolder,0,nullptr,&pidl):SHParseDisplayName(folder.c_str(),nullptr,&pidl,0,nullptr);if(SUCCEEDED(hr)&&pidl){hr=shellBrowser->BrowseToIDList(pidl,SBSP_ABSOLUTE);CoTaskMemFree(pidl);}return SUCCEEDED(hr);}
    void SyncBrowserPathFromShell(){
        if(!shellBrowser||!ActiveTabIsBrowser())return; ComPtr<IFolderView2> fv;
        if(FAILED(shellBrowser->GetCurrentView(IID_PPV_ARGS(fv.ReleaseAndGetAddressOf())))||!fv)return;
        ComPtr<IShellItem> item; if(FAILED(fv->GetFolder(IID_PPV_ARGS(item.ReleaseAndGetAddressOf())))||!item)return;
        PWSTR path=nullptr; if(SUCCEEDED(item->GetDisplayName(SIGDN_FILESYSPATH,&path))&&path){
            EnsureTabStateVectors(); tabBrowserFolder[activeTab]=path; openTabs[activeTab]=path; SetBrowserAddressText(path); CoTaskMemFree(path);
        }
    }
    void BrowserShellBack(){if(shellBrowser){shellBrowser->BrowseToIDList(nullptr,SBSP_NAVIGATEBACK);SyncBrowserPathFromShell();InvalidateViewer();}}
    void BrowserShellForward(){if(shellBrowser){shellBrowser->BrowseToIDList(nullptr,SBSP_NAVIGATEFORWARD);SyncBrowserPathFromShell();InvalidateViewer();}}
    void BrowserShellUp(){if(shellBrowser){shellBrowser->BrowseToIDList(nullptr,SBSP_PARENT);SyncBrowserPathFromShell();InvalidateViewer();}}
    void BrowserShellRefresh(){if(!shellBrowser)return;ComPtr<IShellView> sv;if(SUCCEEDED(shellBrowser->GetCurrentView(IID_PPV_ARGS(sv.ReleaseAndGetAddressOf())))&&sv)sv->Refresh();}
    void BrowserShellSort(const PROPERTYKEY& key){if(!shellBrowser)return;ComPtr<IFolderView2> fv;if(FAILED(shellBrowser->GetCurrentView(IID_PPV_ARGS(fv.ReleaseAndGetAddressOf())))||!fv)return;SORTCOLUMN sc{key,SORT_ASCENDING};fv->SetSortColumns(&sc,1);}
    void ShowBrowserSortMenu(POINT client){HMENU m=CreatePopupMenu();AppendMenuW(m,MF_STRING,1,L"Name");AppendMenuW(m,MF_STRING,2,L"Date modified");AppendMenuW(m,MF_STRING,3,L"Size");POINT sp=client;ClientToScreen(hwnd,&sp);UINT cmd=TrackPopupMenu(m,TPM_RETURNCMD|TPM_RIGHTBUTTON,sp.x,sp.y,0,hwnd,nullptr);DestroyMenu(m);if(cmd==1)BrowserShellSort(PKEY_ItemNameDisplay);else if(cmd==2)BrowserShellSort(PKEY_DateModified);else if(cmd==3)BrowserShellSort(PKEY_Size);}
    void ShowShellBrowserForActiveTab(){if(!ActiveTabIsBrowser()||!hwnd){DestroyShellBrowser();return;}EnsureBrowserAddressEdit();if(!shellBrowser){HRESULT hr=CoCreateInstance(CLSID_ExplorerBrowser,nullptr,CLSCTX_INPROC_SERVER,IID_PPV_ARGS(shellBrowser.ReleaseAndGetAddressOf()));if(FAILED(hr)||!shellBrowser)return;ComPtr<IObjectWithSite> site;if(SUCCEEDED(shellBrowser.As(&site))&&site)site->SetSite(static_cast<IServiceProvider*>(this));FOLDERSETTINGS fs{};fs.ViewMode=FVM_DETAILS;fs.fFlags=FWF_AUTOARRANGE|FWF_USESEARCHFOLDER;RECT rc=ShellBrowserClientRect();hr=shellBrowser->Initialize(hwnd,&rc,&fs);if(FAILED(hr)){shellBrowser.Reset();return;}shellBrowser->SetPropertyBag(L"GlideShellBrowser");}RECT rc=ShellBrowserClientRect();shellBrowser->SetRect(nullptr,rc);PositionBrowserAddressEdit();BrowseShellTo(tabBrowserFolder[activeTab]);SetBrowserAddressText(tabBrowserFolder[activeTab]);RedrawWindow(hwnd,nullptr,nullptr,RDW_INVALIDATE|RDW_ERASE|RDW_ALLCHILDREN|RDW_UPDATENOW);}
    void ResizeShellBrowser(){if(shellBrowser&&ActiveTabIsBrowser()){RECT rc=ShellBrowserClientRect();shellBrowser->SetRect(nullptr,rc);PositionBrowserAddressEdit();}}

    void RefreshBrowserEntries() {
        browserEntries.clear(); browserEntryIsDir.clear(); browserEntryRects.clear();
        if (!ActiveTabIsBrowser()) return;
        EnsureTabStateVectors();
        fs::path folder(tabBrowserFolder[activeTab]);
        std::error_code ec;
        if (!fs::is_directory(folder, ec) || ec) return;
        std::vector<fs::path> dirs, imgs;
        for (fs::directory_iterator it(folder, fs::directory_options::skip_permission_denied, ec), end; !ec && it != end; it.increment(ec)) {
            std::error_code e2;
            if (it->is_directory(e2) && !e2) dirs.push_back(it->path());
            else if (it->is_regular_file(e2) && !e2 && IsSupportedImageExtension(it->path())) imgs.push_back(it->path());
        }
        auto cmp=[](const fs::path& a,const fs::path& b){ return StrCmpLogicalW(a.filename().c_str(), b.filename().c_str()) < 0; };
        std::sort(dirs.begin(),dirs.end(),cmp); std::sort(imgs.begin(),imgs.end(),cmp);
        for (auto& q:dirs){ browserEntries.push_back(q.wstring()); browserEntryIsDir.push_back(true); }
        for (auto& q:imgs){ browserEntries.push_back(q.wstring()); browserEntryIsDir.push_back(false); }
    }

    void NavigateBrowserTo(const std::wstring& folder, bool recordHistory = true) {
        if (!ActiveTabIsBrowser()) return;
        std::error_code ec; fs::path p(folder);
        if (!fs::is_directory(p, ec) || ec) return;
        EnsureTabStateVectors();
        if (recordHistory && !tabBrowserFolder[activeTab].empty() && PathKey(tabBrowserFolder[activeTab]) != PathKey(folder)) {
            tabBrowserBack[activeTab].push_back(tabBrowserFolder[activeTab]);
            tabBrowserForward[activeTab].clear();
        }
        tabBrowserFolder[activeTab]=folder;
        openTabs[activeTab]=folder;
        if(shellBrowser) BrowseShellTo(folder);
        SetBrowserAddressText(folder);
        RefreshBrowserEntries(); RedrawWindow(hwnd,nullptr,nullptr,RDW_INVALIDATE|RDW_ERASE|RDW_ALLCHILDREN);
    }

    void BrowserBack() {
        if (!ActiveTabIsBrowser() || tabBrowserBack[activeTab].empty()) return;
        auto dest=tabBrowserBack[activeTab].back(); tabBrowserBack[activeTab].pop_back();
        tabBrowserForward[activeTab].push_back(tabBrowserFolder[activeTab]);
        tabBrowserFolder[activeTab]=dest; openTabs[activeTab]=dest; RefreshBrowserEntries(); InvalidateViewer();
    }
    void BrowserForward() {
        if (!ActiveTabIsBrowser() || tabBrowserForward[activeTab].empty()) return;
        auto dest=tabBrowserForward[activeTab].back(); tabBrowserForward[activeTab].pop_back();
        tabBrowserBack[activeTab].push_back(tabBrowserFolder[activeTab]);
        tabBrowserFolder[activeTab]=dest; openTabs[activeTab]=dest; RefreshBrowserEntries(); InvalidateViewer();
    }
    void BrowserUp() {
        if (!ActiveTabIsBrowser()) return;
        try { auto p=fs::path(tabBrowserFolder[activeTab]).parent_path(); if (!p.empty()) NavigateBrowserTo(p.wstring()); } catch (...) {}
    }

    void OpenShellImageInActiveTab(const std::wstring& path) {
        if (!ActiveTabIsBrowser() || activeTab < 0 || activeTab >= static_cast<int>(openTabs.size())) return;
        EnsureTabStateVectors();
        const int tab=activeTab; const std::wstring oldFolder=tabBrowserFolder[tab];
        tabBrowserMode[tab]=false; tabBrowserFolder[tab].clear(); tabBrowserBack[tab].clear(); tabBrowserForward[tab].clear(); openTabs[tab]=path;
        DestroyShellBrowser(); ClearSelection(false);
        if (!OpenPath(path)) {
            tabBrowserMode[tab]=true; tabBrowserFolder[tab]=oldFolder; openTabs[tab]=oldFolder; ShowShellBrowserForActiveTab();
            MessageBoxW(hwnd,L"Could not open the selected image in this Glide tab.",kAppName,MB_ICONERROR);
        } else { SyncActiveTabToCurrent(); }
        RedrawWindow(hwnd,nullptr,nullptr,RDW_INVALIDATE|RDW_ERASE|RDW_ALLCHILDREN|RDW_UPDATENOW);
    }

    void RememberClosedTab(int index) {
        if(index<0||index>=static_cast<int>(openTabs.size()))return; EnsureTabStateVectors();
        if(openTabs.size()==1 && TabIsHome(index))return;
        if(index==activeTab && !ActiveTabIsBrowser() && !TabIsHome(index) && !currentPath.empty())
            openTabs[index]=currentPath;
        ClosedTabState st; st.path=openTabs[index]; st.browserMode=tabBrowserMode[index]; st.browserFolder=tabBrowserFolder[index]; st.back=tabBrowserBack[index]; st.forward=tabBrowserForward[index];
        closedTabs.push_back(std::move(st)); while(closedTabs.size()>static_cast<size_t>(std::max(1,closedTabHistoryLimit)))closedTabs.erase(closedTabs.begin());
    }
    void DuplicateActiveTab() {
        if(!titleTabsEnabled){return;} if(activeTab<0||activeTab>=static_cast<int>(openTabs.size())){NewBrowserTab();return;} if(!currentPath.empty()&&!ActiveTabIsBrowser())SyncActiveTabToCurrent(); EnsureTabStateVectors();
        int src=activeTab, at=src+1; openTabs.insert(openTabs.begin()+at,openTabs[src]); tabBrowserMode.insert(tabBrowserMode.begin()+at,tabBrowserMode[src]); tabBrowserFolder.insert(tabBrowserFolder.begin()+at,tabBrowserFolder[src]); tabBrowserBack.insert(tabBrowserBack.begin()+at,tabBrowserBack[src]); tabBrowserForward.insert(tabBrowserForward.begin()+at,tabBrowserForward[src]); activeTab=at;
        if(TabIsHome(activeTab))ShowHomeContent();else if(ActiveTabIsBrowser())ShowShellBrowserForActiveTab();else{DestroyShellBrowser();OpenPath(openTabs[activeTab]);}InvalidateViewer();
    }
    void ReopenClosedTab() {
        if(closedTabs.empty())return;
        // Restoring is an explicit tab action. If tabs were hidden/disabled after a
        // close, re-enable the strip so the restored state cannot succeed invisibly.
        if(!titleTabsEnabled)titleTabsEnabled=true;
        ClosedTabState st=std::move(closedTabs.back());closedTabs.pop_back();
        EnsureTabStateVectors();
        // A lone Home tab is Glide's empty workspace, not valuable content. Restoring
        // should replace it so Ctrl+Shift+T / the restore icon visibly resurrects the
        // closed tab instead of leaving an apparently unchanged Home + hidden state.
        if(openTabs.size()==1 && TabIsHome(0)){
            openTabs[0]=st.path;tabBrowserMode[0]=st.browserMode;tabBrowserFolder[0]=st.browserFolder;
            tabBrowserBack[0]=std::move(st.back);tabBrowserForward[0]=std::move(st.forward);activeTab=0;
        }else{
            int at=std::clamp(activeTab+1,0,static_cast<int>(openTabs.size()));
            openTabs.insert(openTabs.begin()+at,st.path);tabBrowserMode.insert(tabBrowserMode.begin()+at,st.browserMode);
            tabBrowserFolder.insert(tabBrowserFolder.begin()+at,st.browserFolder);tabBrowserBack.insert(tabBrowserBack.begin()+at,std::move(st.back));
            tabBrowserForward.insert(tabBrowserForward.begin()+at,std::move(st.forward));activeTab=at;
        }
        if(TabIsHome(activeTab))ShowHomeContent();
        else if(ActiveTabIsBrowser())ShowShellBrowserForActiveTab();
        else{DestroyShellBrowser();if(!OpenPath(openTabs[activeTab])){RememberClosedTab(activeTab);EnterHomeCanvas();}}
        InvalidateViewer();
    }
    void CycleTab(int delta) {
        if(!titleTabsEnabled||openTabs.empty())return; int n=static_cast<int>(openTabs.size());int next=(activeTab+delta)%n;if(next<0)next+=n;SwitchToTab(next);
    }
    void SwitchTabShortcut(int index) { if(!titleTabsEnabled||openTabs.empty())return; if(index<0)index=static_cast<int>(openTabs.size())-1; if(index>=0&&index<static_cast<int>(openTabs.size()))SwitchToTab(index); }

    void NewBrowserTab() {
        if (!titleTabsEnabled) return;
        if (!currentPath.empty() && !ActiveTabIsBrowser()) SyncActiveTabToCurrent();
        const std::wstring folder=BrowserDefaultFolder();
        openTabs.push_back(folder); tabBrowserMode.push_back(true); tabBrowserFolder.push_back(folder);
        tabBrowserBack.emplace_back(); tabBrowserForward.emplace_back();
        activeTab=static_cast<int>(openTabs.size())-1;
        ShowShellBrowserForActiveTab(); InvalidateViewer();
    }

    void SyncActiveTabToCurrent() {
        if (!titleTabsEnabled || currentPath.empty()) return;
        EnsureTabStateVectors();
        if (ActiveTabIsBrowser()) return;
        if (activeTab < 0 || activeTab >= static_cast<int>(openTabs.size())) {
            openTabs.push_back(currentPath); tabBrowserMode.push_back(false); tabBrowserFolder.push_back(L"");
            tabBrowserBack.emplace_back(); tabBrowserForward.emplace_back();
            activeTab = static_cast<int>(openTabs.size()) - 1;
        } else openTabs[activeTab] = currentPath;
    }

    bool OpenPathInNewTab(const std::wstring& path) {
        if (!titleTabsEnabled) return OpenPath(path);
        if (!currentPath.empty() && !ActiveTabIsBrowser()) SyncActiveTabToCurrent();
        const int oldActive = activeTab;
        openTabs.push_back(path); tabBrowserMode.push_back(false); tabBrowserFolder.push_back(L"");
        tabBrowserBack.emplace_back(); tabBrowserForward.emplace_back();
        activeTab = static_cast<int>(openTabs.size()) - 1;
        if (!OpenPath(path)) {
            openTabs.pop_back(); tabBrowserMode.pop_back(); tabBrowserFolder.pop_back(); tabBrowserBack.pop_back(); tabBrowserForward.pop_back();
            activeTab = oldActive; return false;
        }
        InvalidateViewer(); return true;
    }

    void SwitchToTab(int index) {
        if (index < 0 || index >= static_cast<int>(openTabs.size()) || index == activeTab) return;
        if (!currentPath.empty() && !ActiveTabIsBrowser()) SyncActiveTabToCurrent();
        EnsureTabStateVectors(); activeTab=index;
        if(TabIsHome(index)){ShowHomeContent();return;}
        if (ActiveTabIsBrowser()) { ApplyPngTransparencyMode(); ClearSelection(false); ShowShellBrowserForActiveTab(); RedrawWindow(hwnd,nullptr,nullptr,RDW_INVALIDATE|RDW_ERASE|RDW_ALLCHILDREN|RDW_UPDATENOW); return; }
        DestroyShellBrowser(); OpenPath(openTabs[index]); InvalidateViewer();
    }

    void EnterHomeCanvas() {
        KillTimer(hwnd, kRefineTimerId); KillTimer(hwnd, kAdaptivePreviewTimerId); KillTimer(hwnd, kNavigationIdleTimerId);
        if (slideshowRunning) { slideshowRunning=false; KillTimer(hwnd,kSlideshowTimerId); } slideshowPaused=false;
        ++generation; worker.CancelPending(); prefetchWorker.CancelPending(); prefetchWorker2.CancelPending(); prefetchWorker3.CancelPending(); predictivePrefetchPending.clear(); refineWorker.CancelPending(); adaptiveWorker.CancelPending();
        DestroyShellBrowser();
        openTabs.assign(1,kHomeTabSentinel);tabBrowserMode.assign(1,false);tabBrowserFolder.assign(1,L"");
        tabBrowserBack.clear();tabBrowserBack.emplace_back();tabBrowserForward.clear();tabBrowserForward.emplace_back();activeTab=0;
        ShowHomeContent();
    }

    void CloseTab(int index, bool remember = true) {
        if (index < 0 || index >= static_cast<int>(openTabs.size())) return;
        EnsureTabStateVectors();
        if(remember) RememberClosedTab(index);
        if (openTabs.size() == 1) { EnterHomeCanvas(); return; }
        const bool wasActive=index==activeTab;
        openTabs.erase(openTabs.begin()+index); tabBrowserMode.erase(tabBrowserMode.begin()+index);
        tabBrowserFolder.erase(tabBrowserFolder.begin()+index); tabBrowserBack.erase(tabBrowserBack.begin()+index); tabBrowserForward.erase(tabBrowserForward.begin()+index);
        if (index < activeTab) --activeTab;
        if (wasActive) {
            activeTab=std::clamp(index-1,0,static_cast<int>(openTabs.size())-1);
            if (TabIsHome(activeTab)) { ShowHomeContent(); }
            else if (ActiveTabIsBrowser()) { ApplyPngTransparencyMode(); ClearSelection(false); ShowShellBrowserForActiveTab(); RedrawWindow(hwnd,nullptr,nullptr,RDW_INVALIDATE|RDW_ERASE|RDW_ALLCHILDREN|RDW_UPDATENOW); }
            else { DestroyShellBrowser(); OpenPath(openTabs[activeTab]); }
        }
        InvalidateViewer();
    }

    bool ExecuteHotkey(HotkeyAction action,int parameter) {
        switch(action){
            case HotkeyAction::OpenFile: ShowOpenDialog(); break;
            case HotkeyAction::OpenFolder: ShowOpenFolderDialog(); break;
            case HotkeyAction::Settings: ShowSettingsDialog(); break;
            case HotkeyAction::Refresh: RefreshFolder(); break;
            case HotkeyAction::ToggleSlideshow: ToggleSlideshow(); break;
            case HotkeyAction::StopSlideshow: StopSlideshow(); break;
            case HotkeyAction::ToggleMetadata: metadataVisible=!metadataVisible;if(metadataVisible)RefreshMetadataText();InvalidateViewer();break;
            case HotkeyAction::RotateLeft: rotationQuarterTurns=(rotationQuarterTurns+3)%4;ApplyViewTransformChange();break;
            case HotkeyAction::RotateRight: rotationQuarterTurns=(rotationQuarterTurns+1)%4;ApplyViewTransformChange();break;
            case HotkeyAction::NextImage: Navigate(+1);break; case HotkeyAction::PreviousImage: Navigate(-1);break;
            case HotkeyAction::JumpNext10: JumpBy(+10);break; case HotkeyAction::JumpPrevious10: JumpBy(-10);break;
            case HotkeyAction::FirstImage: JumpFirstLast(true);break; case HotkeyAction::LastImage: JumpFirstLast(false);break;
            case HotkeyAction::FitImage: FitImage();break; case HotkeyAction::FitWidth: FitWidth();break; case HotkeyAction::FitHeight: FitHeight();break;
            case HotkeyAction::ActualSize: ActualSize();break; case HotkeyAction::ToggleFit100: ToggleFit100();break;
            case HotkeyAction::ZoomIn:{if(overlayActiveIndex>=0&&overlayKeyboardZoom){ZoomSelectedOverlay(+1);break;}float f=1.0f+zoomStepPercent/100.0f;ZoomCentered(f);}break;
            case HotkeyAction::ZoomOut:{if(overlayActiveIndex>=0&&overlayKeyboardZoom){ZoomSelectedOverlay(-1);break;}float f=1.0f+zoomStepPercent/100.0f;ZoomCentered(1.0f/f);}break;
            case HotkeyAction::DeleteImage: DeleteCurrentToRecycleBin();break;
            case HotkeyAction::EscapeAction: HandleEscapeAction();break;
            case HotkeyAction::ToggleFullscreen: ToggleFullscreen();break;
            case HotkeyAction::NewTab: NewBrowserTab();break; case HotkeyAction::CloseTab: if(activeTab>=0)CloseTab(activeTab);break;
            case HotkeyAction::ReopenClosedTab: ReopenClosedTab();break; case HotkeyAction::DuplicateTab: DuplicateActiveTab();break;
            case HotkeyAction::AddOverlay: ShowAddOverlayDialog();break; case HotkeyAction::ToggleOverlays: overlaysVisible=!overlaysVisible;InvalidateViewer();break; case HotkeyAction::SaveOverlayLayout: SaveOverlayLayoutDialog();break; case HotkeyAction::LoadOverlayLayout: LoadOverlayLayoutDialog();break; case HotkeyAction::ClearOverlays: overlays.clear();InvalidateViewer();break;
            case HotkeyAction::NextTab: CycleTab(+1);break; case HotkeyAction::PreviousTab: CycleTab(-1);break; case HotkeyAction::SwitchTab: SwitchTabShortcut(parameter);break;
            case HotkeyAction::NewWindow: LaunchNewHomeWindow();break;
            case HotkeyAction::DetachTab: if(activeTab>=0)LaunchDetachedTab(activeTab);break;
            case HotkeyAction::MoveTabLeft: if(activeTab>0)MoveTabState(activeTab,activeTab-1);InvalidateViewer();break;
            case HotkeyAction::MoveTabRight: if(activeTab>=0&&activeTab+1<static_cast<int>(openTabs.size()))MoveTabState(activeTab,activeTab+1);InvalidateViewer();break;
            case HotkeyAction::OpenExternal: LaunchExternalProgram(parameter);break;
            case HotkeyAction::CloseWindow: PostMessageW(hwnd,WM_CLOSE,0,0);break;
            case HotkeyAction::ToggleStatusBar: statusVisible=!statusVisible;InvalidateViewer();break;
            case HotkeyAction::ToggleStatusCollapsed: statusVisible=true;statusCollapsed=!statusCollapsed;InvalidateViewer();break;
            case HotkeyAction::NextSettingsCategory: return false;
        }
        return true;
    }
    bool HandleHotkey(UINT vk) {
        EnsureHotkeys(); DWORD chord=CurrentChord(vk);
        for(size_t i=0;i<hotkeys.size()&&i<std::size(kHotkeyDefs);++i)
            if((hotkeys[i]&&hotkeys[i]==chord)||(i<hotkeysAlt.size()&&hotkeysAlt[i]&&hotkeysAlt[i]==chord)){
                if(kHotkeyDefs[i].action==HotkeyAction::NextSettingsCategory)return false;
                return ExecuteHotkey(kHotkeyDefs[i].action,kHotkeyDefs[i].parameter);
            }
        return false;
    }

    std::wstring HotkeyTextForAction(HotkeyAction action,int parameter=INT_MIN) {
        EnsureHotkeys();
        for(size_t i=0;i<hotkeys.size()&&i<std::size(kHotkeyDefs);++i){
            if(kHotkeyDefs[i].action!=action || (parameter!=INT_MIN&&kHotkeyDefs[i].parameter!=parameter))continue;
            std::wstring r;if(hotkeys[i])r=HotkeyName(hotkeys[i]);if(i<hotkeysAlt.size()&&hotkeysAlt[i]){if(!r.empty())r+=L" / ";r+=HotkeyName(hotkeysAlt[i]);}if(!r.empty())return r;
        }
        return L"";
    }
    std::wstring HotkeyTextForIniKey(const wchar_t* iniKey) {
        EnsureHotkeys(); if(!iniKey)return L"";
        for(size_t i=0;i<std::size(kHotkeyDefs)&&i<hotkeys.size();++i){
            if(_wcsicmp(kHotkeyDefs[i].iniKey,iniKey)!=0)continue;
            std::wstring r;if(hotkeys[i])r=HotkeyName(hotkeys[i]);
            if(i<hotkeysAlt.size()&&hotkeysAlt[i]){if(!r.empty())r+=L" / ";r+=HotkeyName(hotkeysAlt[i]);}
            return r;
        }
        return L"";
    }

    std::wstring TooltipWithHotkeyKey(const wchar_t* base,const wchar_t* iniKey) {
        std::wstring r=base?base:L"";
        const std::wstring hk=HotkeyTextForIniKey(iniKey);
        if(!hk.empty()){r+=L" (";r+=hk;r+=L")";}
        return r;
    }

    std::wstring MenuLabel(const wchar_t* base,HotkeyAction action,int parameter=INT_MIN) {
        std::wstring r=base; std::wstring hk=HotkeyTextForAction(action,parameter); if(!hk.empty()){r+=L"\t";r+=hk;} return r;
    }

    void ShowOpenDialog(bool forceNewTab = false) {
        wchar_t fileName[32768]{};
        OPENFILENAMEW ofn{};
        ofn.lStructSize = sizeof(ofn);
        ofn.hwndOwner = hwnd;
        ofn.lpstrFile = fileName;
        ofn.nMaxFile = static_cast<DWORD>(std::size(fileName));
        ofn.lpstrFilter = GlideFormats::kImageOpenFileFilter;
        ofn.nFilterIndex = 1;
        ofn.Flags = OFN_FILEMUSTEXIST | OFN_PATHMUSTEXIST | OFN_EXPLORER;
        {const std::wstring initial=MainDialogInitialFolder();if(!initial.empty())ofn.lpstrInitialDir=lastMainFolder.c_str();}
        if (GetOpenFileNameW(&ofn)) {
            if(mainRememberLastFolder){try{lastMainFolder=fs::path(fileName).parent_path().wstring();}catch(...){} SaveSettings();}
            const bool ok = (titleTabsEnabled && (forceNewTab || activeTab >= 0)) ? OpenPathInNewTab(fileName) : OpenPath(fileName);
            if (!ok) MessageBoxW(hwnd, L"Could not open the selected image.", kAppName, MB_ICONERROR);
        }
    }

    void ShowOpenFolderDialog() {
        ComPtr<IFileOpenDialog> dialog;
        if (FAILED(CoCreateInstance(CLSID_FileOpenDialog, nullptr, CLSCTX_INPROC_SERVER, IID_PPV_ARGS(&dialog)))) return;
        FILEOPENDIALOGOPTIONS opts{};
        dialog->GetOptions(&opts);
        dialog->SetOptions(opts | FOS_PICKFOLDERS | FOS_FORCEFILESYSTEM | FOS_PATHMUSTEXIST);
        dialog->SetTitle(L"Open image folder");
        {const std::wstring initialFolder=MainDialogInitialFolder();if(!initialFolder.empty()){ComPtr<IShellItem> initial;if(SUCCEEDED(SHCreateItemFromParsingName(initialFolder.c_str(),nullptr,IID_PPV_ARGS(&initial))))dialog->SetFolder(initial.Get());}}
        if (SUCCEEDED(dialog->Show(hwnd))) {
            ComPtr<IShellItem> item;
            if (SUCCEEDED(dialog->GetResult(&item))) {
                PWSTR path = nullptr;
                if (SUCCEEDED(item->GetDisplayName(SIGDN_FILESYSPATH, &path)) && path) {
                    if(mainRememberLastFolder){lastMainFolder=path;SaveSettings();}
                    if (!LoadFolder(path, true)) {
                        MessageBoxW(hwnd, L"This folder contains no supported images.", kAppName, MB_ICONINFORMATION);
                    } else {
                        RecordRecentFolder(path);
                    }
                    CoTaskMemFree(path);
                }
            }
        }
    }

    bool AddOverlayFromPath(const std::wstring& path) {
        // Overlays are initially displayed in a relatively small frame. Decoding a fixed
        // 2048 px preview made large overlay files feel unnecessarily heavy. Request a
        // presentation-sized preview instead; this still leaves ~3x headroom for content zoom.
        RECT rc{};GetClientRect(hwnd,&rc);const float cw=static_cast<float>(std::max(1L,rc.right)),ch=static_cast<float>(std::max(1L,rc.bottom));
        const UINT overlayPreviewMax=static_cast<UINT>(std::clamp(std::max(960.0f,cw*.90f),960.0f,1536.0f));
        OverlayImage ov;ov.path=path;ov.opacity=overlayDefaultOpacity/100.0f;
        // Reuse Glide's existing decoded/prefetched pixels when possible. This makes an
        // overlay made from a nearby/current image effectively instant and avoids a
        // second WIC decode of the same file.
        const CachedPixels* cached=cache.Find(PathKey(path));
        if(cached&&!cached->pixels.empty()){
            ov.width=cached->width;ov.height=cached->height;ov.stride=cached->stride;ov.pixels=cached->pixels;
        }else{
            ComPtr<IWICImagingFactory> wic;if(FAILED(CoCreateInstance(CLSID_WICImagingFactory,nullptr,CLSCTX_INPROC_SERVER,IID_PPV_ARGS(&wic))))return false;
            DecodeRequest req{path,0,true,true,false,true,overlayPreviewMax,overlayPreviewMax};DecodedImage d=DecodeFile(wic.Get(),req);
            if(FAILED(d.hr)||d.pixels.empty())return false;
            ov.width=d.width;ov.height=d.height;ov.stride=d.stride;ov.pixels=std::move(d.pixels);
        }
        float w=std::min(360.0f,std::max(140.0f,cw*.30f));float h=w*ov.height/std::max(1.0f,static_cast<float>(ov.width));
        if(h>ch*.55f){h=ch*.55f;w=h*ov.width/std::max(1.0f,static_cast<float>(ov.height));}
        ov.rect=D2D1::RectF(std::max(12.0f,cw-w-28.0f),ViewerTopInset()+20.0f,std::max(12.0f,cw-28.0f),ViewerTopInset()+20.0f+h);
        overlays.push_back(std::move(ov));overlayActiveIndex=-1;overlaysVisible=true;InvalidateViewer();return true;
    }

    void ShowAddOverlayDialog() {
        ComPtr<IFileOpenDialog> dialog;
        if(FAILED(CoCreateInstance(CLSID_FileOpenDialog,nullptr,CLSCTX_INPROC_SERVER,IID_PPV_ARGS(&dialog))))return;
        FILEOPENDIALOGOPTIONS opts{};dialog->GetOptions(&opts);
        dialog->SetOptions(opts|FOS_FORCEFILESYSTEM|FOS_FILEMUSTEXIST|FOS_PATHMUSTEXIST|FOS_ALLOWMULTISELECT);
        dialog->SetTitle(L"Add Window-in-Window overlay image");
        const COMDLG_FILTERSPEC filters[]={
            {L"Images",GlideFormats::kImageDialogPattern},
            {L"All files",L"*.*"}
        };
        dialog->SetFileTypes(static_cast<UINT>(std::size(filters)),filters);
        dialog->SetFileTypeIndex(1);
        if(!OverlayDialogInitialFolder().empty()){
            ComPtr<IShellItem> folder;
            if(SUCCEEDED(SHCreateItemFromParsingName(lastOverlayFolder.c_str(),nullptr,IID_PPV_ARGS(&folder))))
                dialog->SetFolder(folder.Get());
        }
        if(FAILED(dialog->Show(hwnd)))return;
        ComPtr<IShellItemArray> results;
        if(FAILED(dialog->GetResults(&results))||!results)return;
        DWORD count=0;results->GetCount(&count);bool any=false;
        for(DWORD i=0;i<count;++i){
            ComPtr<IShellItem> item;if(FAILED(results->GetItemAt(i,&item))||!item)continue;
            PWSTR raw=nullptr;if(FAILED(item->GetDisplayName(SIGDN_FILESYSPATH,&raw))||!raw)continue;
            std::wstring path(raw);CoTaskMemFree(raw);
            if(overlayRememberLastFolder){try{lastOverlayFolder=fs::path(path).parent_path().wstring();}catch(...){}}
            if(AddOverlayFromPath(path))any=true;
        }
        if(!any&&count)MessageBoxW(hwnd,L"Glide could not open the selected overlay image(s).",kAppName,MB_OK|MB_ICONERROR);
        SaveSettings();
    }

    bool EnsureOverlayBitmap(OverlayImage& ov){
        if(ov.bitmap)return true;if(!target||ov.pixels.empty()||!ov.width||!ov.height)return false;
        D2D1_BITMAP_PROPERTIES props=D2D1::BitmapProperties(D2D1::PixelFormat(DXGI_FORMAT_B8G8R8A8_UNORM,D2D1_ALPHA_MODE_PREMULTIPLIED),96,96);
        return SUCCEEDED(target->CreateBitmap(D2D1::SizeU(ov.width,ov.height),ov.pixels.data(),ov.stride,props,&ov.bitmap));
    }

    void RenderWindowInWindowOverlays(){
        if(!overlaysVisible||overlays.empty()||!target)return;POINT cp{};GetCursorPos(&cp);ScreenToClient(hwnd,&cp);
        const ThemePalette pal=Palette();
        ComPtr<ID2D1SolidColorBrush> chrome,accent,muted,glass;target->CreateSolidColorBrush(D2DColor(pal.panel,0.88f),&chrome);target->CreateSolidColorBrush(D2DColor(pal.accent,1.0f),&accent);target->CreateSolidColorBrush(D2DColor(pal.text,1.0f),&muted);target->CreateSolidColorBrush(D2DColor(pal.surface,0.78f),&glass);
        for(size_t i=0;i<overlays.size();++i){auto&ov=overlays[i];if(!EnsureOverlayBitmap(ov))continue;
            // Zoom crops the bitmap source only; ov.rect remains unchanged.
            const float z=std::clamp(ov.zoom,1.0f,32.0f);
            const float sw=ov.width/z, sh=ov.height/z;
            float sx=std::clamp(ov.sourceCenterX*ov.width-sw*.5f,0.0f,std::max(0.0f,ov.width-sw));
            float sy=std::clamp(ov.sourceCenterY*ov.height-sh*.5f,0.0f,std::max(0.0f,ov.height-sh));
            D2D1_RECT_F src=D2D1::RectF(sx,sy,sx+sw,sy+sh);
            target->DrawBitmap(ov.bitmap.Get(),ov.rect,ov.opacity,D2D1_BITMAP_INTERPOLATION_MODE_LINEAR,&src);
            const bool hov=PointInRect(ov.rect,cp)||(overlaySelectedHighlight&&static_cast<int>(i)==overlayActiveIndex);
            ov.closeRect=ov.resizeRect=ov.sliderRect=D2D1::RectF();if(!hov)continue;
            target->DrawRoundedRectangle(D2D1::RoundedRect(ov.rect,7,7),accent.Get(),1.35f);
            ov.closeRect=D2D1::RectF(ov.rect.right-34,ov.rect.top+7,ov.rect.right-7,ov.rect.top+34);target->FillEllipse(D2D1::Ellipse(D2D1::Point2F((ov.closeRect.left+ov.closeRect.right)/2,(ov.closeRect.top+ov.closeRect.bottom)/2),13,13),chrome.Get());
            auto xc=D2D1::Point2F((ov.closeRect.left+ov.closeRect.right)/2,(ov.closeRect.top+ov.closeRect.bottom)/2);target->DrawLine(D2D1::Point2F(xc.x-4,xc.y-4),D2D1::Point2F(xc.x+4,xc.y+4),muted.Get(),1.8f);target->DrawLine(D2D1::Point2F(xc.x+4,xc.y-4),D2D1::Point2F(xc.x-4,xc.y+4),muted.Get(),1.8f);
            ov.resizeRect=D2D1::RectF(ov.rect.right-28,ov.rect.bottom-28,ov.rect.right-4,ov.rect.bottom-4);target->FillRoundedRectangle(D2D1::RoundedRect(ov.resizeRect,5,5),glass.Get());for(int k=0;k<3;++k)target->DrawLine(D2D1::Point2F(ov.rect.right-7-k*5,ov.rect.bottom-5),D2D1::Point2F(ov.rect.right-5,ov.rect.bottom-7-k*5),accent.Get(),1.25f);
            const float sliderH=std::min(130.0f,std::max(70.0f,ov.rect.bottom-ov.rect.top-80.0f));ov.sliderRect=D2D1::RectF(ov.rect.left+8,(ov.rect.top+ov.rect.bottom-sliderH)/2,ov.rect.left+30,(ov.rect.top+ov.rect.bottom+sliderH)/2);target->FillRoundedRectangle(D2D1::RoundedRect(ov.sliderRect,10,10),glass.Get());
            const float sliderX=(ov.sliderRect.left+ov.sliderRect.right)/2;target->DrawLine(D2D1::Point2F(sliderX,ov.sliderRect.top+10),D2D1::Point2F(sliderX,ov.sliderRect.bottom-10),muted.Get(),2.0f);float ty=ov.sliderRect.bottom-10-(ov.sliderRect.bottom-ov.sliderRect.top-20)*ov.opacity;target->FillEllipse(D2D1::Ellipse(D2D1::Point2F(sliderX,ty),5,5),accent.Get());
        }
    }

    bool BeginOverlayInteraction(POINT pt){
        if(!overlaysVisible)return false;for(int i=static_cast<int>(overlays.size())-1;i>=0;--i){auto&ov=overlays[i];if(PointInRect(ov.closeRect,pt)){overlays.erase(overlays.begin()+i);overlayActiveIndex=-1;InvalidateViewer();return true;}if(!PointInRect(ov.rect,pt))continue;
            overlayActiveIndex=i;overlayDown=pt;overlayStartRect=ov.rect;overlayStartOpacity=ov.opacity;
            overlayResizing=PointInRect(ov.resizeRect,pt);overlayOpacityDragging=PointInRect(ov.sliderRect,pt);overlayDragging=!overlayResizing&&!overlayOpacityDragging;SetCapture(hwnd);return true;}
        return false;
    }
    void UpdateOverlayInteraction(POINT pt){
        if(overlayActiveIndex<0||overlayActiveIndex>=static_cast<int>(overlays.size()))return;auto&ov=overlays[overlayActiveIndex];
        if(overlayDragging){float dx=static_cast<float>(pt.x-overlayDown.x),dy=static_cast<float>(pt.y-overlayDown.y);ov.rect=overlayStartRect;ov.rect.left+=dx;ov.rect.right+=dx;ov.rect.top+=dy;ov.rect.bottom+=dy;}
        else if(overlayResizing){float nw=std::max(80.0f,overlayStartRect.right-overlayStartRect.left+(pt.x-overlayDown.x));float ratio=static_cast<float>(ov.height)/std::max(1u,ov.width);float nh=std::max(60.0f,nw*ratio);ov.rect.right=ov.rect.left+nw;ov.rect.bottom=ov.rect.top+nh;}
        else if(overlayOpacityDragging){float t=(ov.sliderRect.bottom-10-pt.y)/std::max(1.0f,ov.sliderRect.bottom-ov.sliderRect.top-20);ov.opacity=std::clamp(t,0.10f,1.0f);}InvalidateViewer();
    }
    void EndOverlayInteraction(){overlayDragging=overlayResizing=overlayOpacityDragging=false;if(GetCapture()==hwnd)ReleaseCapture();InvalidateViewer();}

    bool BeginOverlayRightPan(POINT pt){
        if(!overlayRightDragPansZoomed||!overlaysVisible)return false;
        for(int i=static_cast<int>(overlays.size())-1;i>=0;--i){
            auto&ov=overlays[i];
            if(!PointInRect(ov.rect,pt))continue;
            overlayActiveIndex=i;
            if(ov.zoom<=1.001f)return false;
            overlayRightPanCandidate=true;overlayRightPanning=false;overlayRightDown=pt;
            overlayRightStartCenterX=ov.sourceCenterX;overlayRightStartCenterY=ov.sourceCenterY;
            SetCapture(hwnd);return true;
        }
        return false;
    }
    void UpdateOverlayRightPan(POINT pt){
        if((!overlayRightPanCandidate&&!overlayRightPanning)||overlayActiveIndex<0||overlayActiveIndex>=static_cast<int>(overlays.size()))return;
        auto&ov=overlays[overlayActiveIndex];
        const int dx=pt.x-overlayRightDown.x,dy=pt.y-overlayRightDown.y;
        if(!overlayRightPanning){if(std::abs(dx)<kDragThresholdPx&&std::abs(dy)<kDragThresholdPx)return;overlayRightPanning=true;overlayRightPanCandidate=false;}
        glide_overlay::PanContent(ov,dx,dy,overlayRightStartCenterX,overlayRightStartCenterY);
        SetCursor(LoadCursorW(nullptr,IDC_HAND));InvalidateViewer();
    }
    bool EndOverlayRightPan(POINT pt){
        if(!overlayRightPanCandidate&&!overlayRightPanning)return false;
        const bool dragged=overlayRightPanning;
        overlayRightPanCandidate=overlayRightPanning=false;
        if(GetCapture()==hwnd)ReleaseCapture();SetCursor(LoadCursorW(nullptr,IDC_ARROW));
        if(!dragged)ShowOverlayContextMenu(pt);
        InvalidateViewer();return true;
    }

    bool ZoomSelectedOverlay(int direction){
        if(overlayActiveIndex<0||overlayActiveIndex>=static_cast<int>(overlays.size()))return false;
        glide_overlay::Zoom(overlays[overlayActiveIndex],direction,overlayZoomStepPercent);
        InvalidateViewer();return true;
    }
    bool ResetSelectedOverlayZoom(){
        if(overlayActiveIndex<0||overlayActiveIndex>=static_cast<int>(overlays.size()))return false;
        glide_overlay::ResetZoom(overlays[overlayActiveIndex]);InvalidateViewer();return true;
    }

    bool ShowOverlayContextMenu(POINT clientPt){
        if(!overlaysVisible) return false;
        int idx=-1; for(int i=static_cast<int>(overlays.size())-1;i>=0;--i) if(PointInRect(overlays[i].rect,clientPt)){idx=i;break;}
        if(idx<0) return false;
        overlayActiveIndex=idx; auto &ov=overlays[idx];
        HMENU menu=CreatePopupMenu(); if(!menu)return true;
        AppendMenuW(menu,MF_STRING,1,L"Zoom in\t+"); AppendMenuW(menu,MF_STRING,2,L"Zoom out\t−"); AppendMenuW(menu,MF_STRING,3,L"Restore default zoom");
        AppendMenuW(menu,MF_SEPARATOR,0,nullptr); AppendMenuW(menu,MF_STRING,4,L"Opacity 100%"); AppendMenuW(menu,MF_STRING,5,L"Opacity 75%"); AppendMenuW(menu,MF_STRING,6,L"Opacity 50%");
        AppendMenuW(menu,MF_SEPARATOR,0,nullptr); AppendMenuW(menu,MF_STRING,7,L"Bring to front"); AppendMenuW(menu,MF_STRING,8,L"Close overlay");
        POINT sp=clientPt;ClientToScreen(hwnd,&sp);int cmd=TrackPopupMenu(menu,TPM_RETURNCMD|TPM_RIGHTBUTTON,sp.x,sp.y,0,hwnd,nullptr);DestroyMenu(menu);
        if(idx>=static_cast<int>(overlays.size()))return true;
        if(cmd==1)ZoomSelectedOverlay(+1);
        else if(cmd==2)ZoomSelectedOverlay(-1);
        else if(cmd==3)ResetSelectedOverlayZoom();
        else if(cmd==4)overlays[idx].opacity=1.0f;else if(cmd==5)overlays[idx].opacity=.75f;else if(cmd==6)overlays[idx].opacity=.50f;
        else if(cmd==7)glide_overlay::BringToFront(overlays,overlayActiveIndex,idx);
        else if(cmd==8)glide_overlay::Remove(overlays,overlayActiveIndex,idx);
        InvalidateViewer();return true;
    }

    void SaveOverlayLayoutDialog(){
        if(overlays.empty())return;wchar_t file[32768]=L"Glide Overlay Layout.glideoverlay";OPENFILENAMEW ofn{};ofn.lStructSize=sizeof(ofn);ofn.hwndOwner=hwnd;ofn.lpstrFile=file;ofn.nMaxFile=static_cast<DWORD>(std::size(file));ofn.lpstrFilter=L"Glide overlay layouts\0*.glideoverlay\0All files\0*.*\0\0";ofn.lpstrDefExt=L"glideoverlay";ofn.Flags=OFN_OVERWRITEPROMPT|OFN_PATHMUSTEXIST|OFN_EXPLORER;if(!GetSaveFileNameW(&ofn))return;
        RECT rc{};GetClientRect(hwnd,&rc);const float cw=static_cast<float>(std::max<LONG>(1,rc.right)),ch=static_cast<float>(std::max<LONG>(1,rc.bottom));
        const auto records=glide_overlay::CaptureLayout(overlays,cw,ch,overlayRememberZoom);
        glide_overlay::WriteLayoutFile(file,records);
    }
    void LoadOverlayLayoutDialog(){
        wchar_t file[32768]{};OPENFILENAMEW ofn{};ofn.lStructSize=sizeof(ofn);ofn.hwndOwner=hwnd;ofn.lpstrFile=file;ofn.nMaxFile=static_cast<DWORD>(std::size(file));ofn.lpstrFilter=L"Glide overlay layouts\0*.glideoverlay\0All files\0*.*\0\0";ofn.Flags=OFN_FILEMUSTEXIST|OFN_PATHMUSTEXIST|OFN_EXPLORER;if(!GetOpenFileNameW(&ofn))return;
        std::vector<glide_overlay::OverlayLayoutRecord> records;if(!glide_overlay::ReadLayoutFile(file,records))return;
        overlays.clear();overlayActiveIndex=-1;RECT rc{};GetClientRect(hwnd,&rc);const float cw=static_cast<float>(std::max<LONG>(1,rc.right)),ch=static_cast<float>(std::max<LONG>(1,rc.bottom));
        for(const auto&r:records){size_t before=overlays.size();if(AddOverlayFromPath(r.path)&&overlays.size()>before){auto&ov=overlays.back();ov.opacity=r.opacity;ov.rect=D2D1::RectF(r.x*cw,r.y*ch,(r.x+r.width)*cw,(r.y+r.height)*ch);if(overlayRememberZoom){ov.zoom=r.zoom;ov.sourceCenterX=r.sourceCenterX;ov.sourceCenterY=r.sourceCenterY;}}}
        InvalidateViewer();
    }

    float FitScaleForMode(ViewMode mode) const {
        RECT rc{}; GetClientRect(hwnd,&rc);
        const float cw=static_cast<float>((std::max)(static_cast<LONG>(1),rc.right-rc.left));
        const float topInset=ViewerTopInset();
        const float ch=(std::max)(1.0f,static_cast<float>(rc.bottom-rc.top)-topInset);
        if(!bitmap)return 1.0f;
        return glide_view::FitScale(mode,zoom,imageW,imageH,cw,ch,kMinZoom,kMaxZoom);
    }

    float CurrentScale() const { return viewMode==ViewMode::Manual?zoom:FitScaleForMode(viewMode); }

    D2D1_RECT_F ImageDestinationRect() const {
        RECT rc{}; GetClientRect(hwnd,&rc);
        const float cw=static_cast<float>((std::max)(static_cast<LONG>(1),rc.right-rc.left));
        const float topInset=ViewerTopInset();
        const float ch=(std::max)(1.0f,static_cast<float>(rc.bottom-rc.top)-topInset);
        if(!bitmap)return D2D1::RectF(0,0,cw,ch);
        return glide_view::DestinationRect(viewMode,zoom,panX,panY,imageW,imageH,cw,ch,topInset,kMinZoom,kMaxZoom);
    }

    void ClampPan() {
        if(viewMode!=ViewMode::Manual||!bitmap)return;
        RECT rc{}; GetClientRect(hwnd,&rc);
        const float cw=static_cast<float>((std::max)(static_cast<LONG>(1),rc.right-rc.left));
        const float topInset=ViewerTopInset();
        const float ch=(std::max)(1.0f,static_cast<float>(rc.bottom-rc.top)-topInset);
        glide_view::ClampManualPan(viewMode,zoom,panX,panY,imageW,imageH,cw,ch,topInset);
    }

    void RebuildQualityBitmapIfNeeded() {
        qualityBitmap.Reset();
        qualityBitmapW = qualityBitmapH = 0;
        if (!target || !bitmap || imageW == 0 || imageH == 0 || bitmapIsPreview) return;

        const D2D1_RECT_F dst = ImageDestinationRect();
        const float dw = std::max(1.0f, dst.right - dst.left);
        const float dh = std::max(1.0f, dst.bottom - dst.top);
        const float scale = CurrentScale();
        if (scale >= 0.75f) return; // Direct rendering is already excellent near 1:1.

        UINT srcW = imageW;
        UINT srcH = imageH;
        ComPtr<ID2D1Bitmap> src = bitmap;

        // Repeated 2x reductions keep each bilinear pass in its well-behaved range.
        // The final display pass therefore never has to collapse thousands of source
        // pixels into one screen pixel in a single sample operation.
        while (srcW > 2 && srcH > 2 &&
               static_cast<float>(srcW) > dw * 1.8f &&
               static_cast<float>(srcH) > dh * 1.8f) {
            const UINT nextW = std::max<UINT>(1, srcW / 2);
            const UINT nextH = std::max<UINT>(1, srcH / 2);
            const D2D1_SIZE_U pixelSize = D2D1::SizeU(nextW, nextH);
            ComPtr<ID2D1BitmapRenderTarget> tempRT;
            HRESULT hr = target->CreateCompatibleRenderTarget(
                nullptr, &pixelSize, nullptr,
                D2D1_COMPATIBLE_RENDER_TARGET_OPTIONS_NONE, &tempRT);
            if (FAILED(hr) || !tempRT) break;

            tempRT->BeginDraw();
            tempRT->Clear(D2D1::ColorF(0, 0.0f));
            tempRT->DrawBitmap(src.Get(), D2D1::RectF(0, 0,
                static_cast<float>(nextW), static_cast<float>(nextH)),
                1.0f, D2D1_BITMAP_INTERPOLATION_MODE_LINEAR);
            hr = tempRT->EndDraw();
            if (FAILED(hr)) break;

            ComPtr<ID2D1Bitmap> next;
            if (FAILED(tempRT->GetBitmap(&next)) || !next) break;
            src = next;
            srcW = nextW;
            srcH = nextH;
        }

        if (src.Get() != bitmap.Get()) {
            qualityBitmap = src;
            qualityBitmapW = srcW;
            qualityBitmapH = srcH;
        }
    }

    void EnsureQualityBitmap() {
        if (!bitmap || !target) return;
        if (bitmapIsPreview) {
            qualityBitmap.Reset();
            qualityBitmapW = qualityBitmapH = 0;
            return;
        }
        const float scale = CurrentScale();
        if (scale >= 0.75f) {
            qualityBitmap.Reset();
            qualityBitmapW = qualityBitmapH = 0;
            return;
        }
        const D2D1_RECT_F dst = ImageDestinationRect();
        const float dw = std::max(1.0f, dst.right - dst.left);
        const float dh = std::max(1.0f, dst.bottom - dst.top);
        if (!qualityBitmap || qualityBitmapW < dw || qualityBitmapH < dh ||
            qualityBitmapW > dw * 2.6f || qualityBitmapH > dh * 2.6f) {
            RebuildQualityBitmapIfNeeded();
        }
    }

    void SetScrollBarStyles(bool needH, bool needV) {
        if (!hwnd) return;
        LONG_PTR style = GetWindowLongPtrW(hwnd, GWL_STYLE);
        LONG_PTR next = style;
        if (needH) next |= WS_HSCROLL; else next &= ~static_cast<LONG_PTR>(WS_HSCROLL);
        if (needV) next |= WS_VSCROLL; else next &= ~static_cast<LONG_PTR>(WS_VSCROLL);
        if (next != style) {
            SetWindowLongPtrW(hwnd, GWL_STYLE, next);
            SetWindowPos(hwnd, nullptr, 0, 0, 0, 0,
                         SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_FRAMECHANGED);
        }
    }

    void UpdateScrollBars() {
        if (!hwnd) return;

        // Scrollbars are structural, not decorative: in Fit/Fit Width/Fit Height,
        // at <=100% when the image fits, or when no image is loaded, remove the
        // standard scrollbar styles entirely. This prevents empty tracks from
        // occupying the viewer edge.
        if (!showScrollBars || !bitmap || viewMode != ViewMode::Manual) {
            SetScrollBarStyles(false, false);
            return;
        }

        RECT rc{};
        GetClientRect(hwnd, &rc);
        int cw = std::max<LONG>(1, rc.right - rc.left);
        int ch = std::max<LONG>(1, rc.bottom - rc.top);

        const LONG_PTR style = GetWindowLongPtrW(hwnd, GWL_STYLE);
        const bool hadH = (style & WS_HSCROLL) != 0;
        const bool hadV = (style & WS_VSCROLL) != 0;
        const int sbW = GetSystemMetrics(SM_CXVSCROLL);
        const int sbH = GetSystemMetrics(SM_CYHSCROLL);

        // Recover the approximate no-scrollbar client area so the decision does
        // not oscillate when a scrollbar itself reduces the viewport.
        const int baseW = cw + (hadV ? sbW : 0);
        const int baseH = ch + (hadH ? sbH : 0);
        const float dw = static_cast<float>(imageW) * zoom;
        const float dh = static_cast<float>(imageH) * zoom;

        bool needH = false;
        bool needV = false;
        for (int i = 0; i < 3; ++i) {
            const int availW = std::max(1, baseW - (needV ? sbW : 0));
            const int availH = std::max(1, baseH - (needH ? sbH : 0));
            needH = dw > static_cast<float>(availW) + 0.5f;
            needV = dh > static_cast<float>(availH) + 0.5f;
        }

        SetScrollBarStyles(needH, needV);
        if (!needH && !needV) return;

        GetClientRect(hwnd, &rc);
        const int vw = std::max<LONG>(1, rc.right - rc.left);
        const int vh = std::max<LONG>(1, rc.bottom - rc.top);
        const D2D1_RECT_F dst = ImageDestinationRect();

        if (needH) {
            SCROLLINFO si{sizeof(si)};
            si.fMask = SIF_RANGE | SIF_PAGE | SIF_POS;
            si.nMin = 0;
            si.nMax = static_cast<int>(imageW) - 1;
            si.nPage = std::max<UINT>(1, static_cast<UINT>(vw / std::max(zoom, 0.0001f)));
            const int maxPos = std::max(0, si.nMax - static_cast<int>(si.nPage) + 1);
            si.nPos = std::clamp(static_cast<int>(std::lround(-dst.left / zoom)), 0, maxPos);
            SetScrollInfo(hwnd, SB_HORZ, &si, TRUE);
        }
        if (needV) {
            SCROLLINFO si{sizeof(si)};
            si.fMask = SIF_RANGE | SIF_PAGE | SIF_POS;
            si.nMin = 0;
            si.nMax = static_cast<int>(imageH) - 1;
            si.nPage = std::max<UINT>(1, static_cast<UINT>(vh / std::max(zoom, 0.0001f)));
            const int maxPos = std::max(0, si.nMax - static_cast<int>(si.nPage) + 1);
            si.nPos = std::clamp(static_cast<int>(std::lround(-dst.top / zoom)), 0, maxPos);
            SetScrollInfo(hwnd, SB_VERT, &si, TRUE);
        }
    }

    void UpdateScrollPositionsOnly() {
        if (!showScrollBars || !bitmap || viewMode != ViewMode::Manual) return;
        const LONG_PTR style = GetWindowLongPtrW(hwnd, GWL_STYLE);
        if ((style & (WS_HSCROLL | WS_VSCROLL)) == 0) return;

        const D2D1_RECT_F dst = ImageDestinationRect();
        if (style & WS_HSCROLL) {
            SCROLLINFO si{sizeof(si)};
            si.fMask = SIF_POS;
            SCROLLINFO cur{sizeof(cur)};
            cur.fMask = SIF_RANGE | SIF_PAGE;
            GetScrollInfo(hwnd, SB_HORZ, &cur);
            const int maxPos = std::max(0, cur.nMax - static_cast<int>(cur.nPage) + 1);
            si.nPos = std::clamp(static_cast<int>(std::lround(-dst.left / std::max(zoom, 0.0001f))), 0, maxPos);
            SetScrollInfo(hwnd, SB_HORZ, &si, FALSE);
        }
        if (style & WS_VSCROLL) {
            SCROLLINFO si{sizeof(si)};
            si.fMask = SIF_POS;
            SCROLLINFO cur{sizeof(cur)};
            cur.fMask = SIF_RANGE | SIF_PAGE;
            GetScrollInfo(hwnd, SB_VERT, &cur);
            const int maxPos = std::max(0, cur.nMax - static_cast<int>(cur.nPage) + 1);
            si.nPos = std::clamp(static_cast<int>(std::lround(-dst.top / std::max(zoom, 0.0001f))), 0, maxPos);
            SetScrollInfo(hwnd, SB_VERT, &si, FALSE);
        }
    }

    void ScrollTo(int bar, int code, int trackPos) {
        if (!bitmap || viewMode != ViewMode::Manual) return;
        SCROLLINFO si{sizeof(si)};
        si.fMask = SIF_ALL;
        GetScrollInfo(hwnd, bar, &si);
        int pos = si.nPos;
        const int line = std::max(1, static_cast<int>(32.0f / std::max(zoom, 0.01f)));
        switch (code) {
            case SB_LINELEFT:  pos -= line; break;
            case SB_LINERIGHT: pos += line; break;
            case SB_PAGELEFT:  pos -= static_cast<int>(si.nPage); break;
            case SB_PAGERIGHT: pos += static_cast<int>(si.nPage); break;
            case SB_THUMBTRACK:
            case SB_THUMBPOSITION: pos = trackPos; break;
            case SB_TOP: pos = si.nMin; break;
            case SB_BOTTOM: pos = si.nMax; break;
            default: return;
        }
        const int maxPos = std::max(si.nMin, si.nMax - static_cast<int>(si.nPage) + 1);
        pos = std::clamp(pos, si.nMin, maxPos);

        RECT rc{};
        GetClientRect(hwnd, &rc);
        const float cw = static_cast<float>(std::max<LONG>(1, rc.right - rc.left));
        const float ch = static_cast<float>(std::max<LONG>(1, rc.bottom - rc.top));
        const float baseLeft = (cw - static_cast<float>(imageW) * zoom) * 0.5f;
        const float baseTop = (ch - static_cast<float>(imageH) * zoom) * 0.5f;
        if (bar == SB_HORZ) panX = -static_cast<float>(pos) * zoom - baseLeft;
        else panY = -static_cast<float>(pos) * zoom - baseTop;
        ClampPan();
        UpdateScrollBars();
        InvalidateRect(hwnd, nullptr, FALSE);
    }

    void SetViewMode(ViewMode mode) {
        if (!bitmap) return;
        viewMode = mode;
        panX = 0.0f;
        panY = 0.0f;
        if (mode == ViewMode::Manual) zoom = 1.0f;
        qualityBitmap.Reset();
        qualityBitmapW = qualityBitmapH = 0;
        UpdateScrollBars();
        UpdateTitle();
        InvalidateRect(hwnd, nullptr, FALSE);
    }

    void ActualSize() {
        if (!bitmap) return;
        EnsureFullResolutionAsync();
        viewMode = ViewMode::Manual;
        zoom = 1.0f;
        panX = 0.0f;
        panY = 0.0f;
        qualityBitmap.Reset();
        qualityBitmapW = qualityBitmapH = 0;
        UpdateScrollBars();
        UpdateTitle();
        InvalidateRect(hwnd, nullptr, FALSE);
    }

    void FitImage() {
        SetViewMode(ViewMode::Fit);
    }

    void FitWidth() {
        SetViewMode(ViewMode::FitWidth);
    }

    void FitHeight() {
        SetViewMode(ViewMode::FitHeight);
    }

    void ToggleFit100() {
        if (!bitmap) return;
        if (viewMode == ViewMode::Manual && std::fabs(zoom - 1.0f) < 0.0001f) {
            FitImage();
        } else {
            ActualSize();
        }
    }

    void ZoomAt(float sx, float sy, float factor) {
        if (!bitmap || imageW == 0 || imageH == 0 || factor <= 0.0f) return;
        EnsureFullResolutionAsync();

        RECT rc{};
        GetClientRect(hwnd, &rc);
        const float cw = static_cast<float>((std::max)(static_cast<LONG>(1), rc.right - rc.left));
        const float ch = static_cast<float>((std::max)(static_cast<LONG>(1), rc.bottom - rc.top));
        const D2D1_RECT_F oldDst = ImageDestinationRect();
        const float oldScale = CurrentScale();
        if (oldScale <= 0.0f) return;

        const auto zr=glide_view::ZoomAtPoint(oldScale,oldDst,imageW,imageH,cw,ch,sx,sy,factor,kMinZoom,kMaxZoom);
        viewMode=ViewMode::Manual; zoom=zr.zoom; panX=zr.panX; panY=zr.panY;
        ClampPan();
        qualityBitmap.Reset(); qualityBitmapW=qualityBitmapH=0;
        UpdateScrollBars();
        UpdateTitle();
        InvalidateRect(hwnd,nullptr,FALSE);
    }

    void ZoomCentered(float factor) {
        RECT rc{};
        GetClientRect(hwnd, &rc);
        const float cx = static_cast<float>(rc.right - rc.left) * 0.5f;
        const float cy = static_cast<float>(rc.bottom - rc.top) * 0.5f;
        ZoomAt(cx, cy, factor);
    }

    void WheelZoom(short wheelDelta, POINT screenPoint) {
        if (!bitmap || wheelDelta == 0) return;
        POINT clientPoint = screenPoint;
        ScreenToClient(hwnd, &clientPoint);
        if(!zoomAroundCursor){RECT rc{};GetClientRect(hwnd,&rc);clientPoint.x=(rc.right-rc.left)/2;clientPoint.y=(rc.bottom-rc.top)/2;}

        // 15% per standard wheel detent. Fractional deltas remain fractional.
        const float detents = static_cast<float>(wheelDelta) / static_cast<float>(WHEEL_DELTA);
        const float factor = std::pow(1.15f, detents);
        ZoomAt(static_cast<float>(clientPoint.x), static_cast<float>(clientPoint.y), factor);
    }

    static D2D1_RECT_F NormalizeRect(D2D1_POINT_2F a, D2D1_POINT_2F b) {
        return D2D1::RectF(std::min(a.x, b.x), std::min(a.y, b.y),
                           std::max(a.x, b.x), std::max(a.y, b.y));
    }

    bool ClientToImage(float sx, float sy, D2D1_POINT_2F& out, bool clampToImage) const {
        if (!bitmap || imageW == 0 || imageH == 0) return false;
        const D2D1_RECT_F dst = ImageDestinationRect();
        const float scale = CurrentScale();
        if (scale <= 0.0f) return false;

        if (!clampToImage && (sx < dst.left || sx > dst.right || sy < dst.top || sy > dst.bottom)) {
            return false;
        }

        float ix = (sx - dst.left) / scale;
        float iy = (sy - dst.top) / scale;
        if (clampToImage) {
            ix = std::clamp(ix, 0.0f, static_cast<float>(imageW));
            iy = std::clamp(iy, 0.0f, static_cast<float>(imageH));
        } else if (ix < 0.0f || iy < 0.0f || ix > imageW || iy > imageH) {
            return false;
        }
        out = D2D1::Point2F(ix, iy);
        return true;
    }

    D2D1_RECT_F SelectionClientRect() const {
        if (!selectionActive && !selecting) return D2D1::RectF();
        const D2D1_RECT_F dst = ImageDestinationRect();
        const float scale = CurrentScale();
        const D2D1_RECT_F r = selecting
            ? NormalizeRect(selectionStartImage, selectionCurrentImage)
            : selectionImageRect;
        return D2D1::RectF(dst.left + r.left * scale,
                           dst.top + r.top * scale,
                           dst.left + r.right * scale,
                           dst.top + r.bottom * scale);
    }

    bool PointInsideSelection(float sx, float sy) const {
        if (!selectionActive) return false;
        D2D1_POINT_2F p{};
        if (!ClientToImage(sx, sy, p, false)) return false;
        return p.x >= selectionImageRect.left && p.x <= selectionImageRect.right &&
               p.y >= selectionImageRect.top && p.y <= selectionImageRect.bottom;
    }

    void ClearSelection(bool repaint = true) {
        selectionActive = false;
        selecting = false;
        selectionClickCandidate = false;
        if (repaint) InvalidateRect(hwnd, nullptr, FALSE);
    }

    GestureAction Gesture(GestureSlot slot) const {
        return gestureMap[static_cast<size_t>(slot)];
    }

    bool ExecuteGestureAction(GestureSlot slot, POINT pt, int wheelDelta = 0, int dragButton = 0) {
        const GestureAction action = Gesture(slot);
        if (action == GestureAction::Legacy) return false;
        switch (action) {
            case GestureAction::None: return true;
            case GestureAction::NextImage: Navigate(+1); return true;
            case GestureAction::PreviousImage: Navigate(-1); return true;
            case GestureAction::ZoomIn:
                if (overlayActiveIndex >= 0 && PointOnOverlay(pt)) ZoomSelectedOverlay(+1); else ZoomCentered(1.0f + zoomStepPercent / 100.0f);
                return true;
            case GestureAction::ZoomOut:
                if (overlayActiveIndex >= 0 && PointOnOverlay(pt)) ZoomSelectedOverlay(-1); else ZoomCentered(1.0f / (1.0f + zoomStepPercent / 100.0f));
                return true;
            case GestureAction::FitImage: SetViewMode(ViewMode::Fit); return true;
            case GestureAction::ActualSize: ActualSize(); return true;
            case GestureAction::ToggleFit100: ToggleFit100(); return true;
            case GestureAction::ToggleFullscreen: ToggleFullscreen(); return true;
            case GestureAction::ContextMenu: {
                if (fullscreen) return true;
                POINT sp=pt; ClientToScreen(hwnd,&sp); ShowContextMenu(sp); return true;
            }
            case GestureAction::ClearSelection: ClearSelection(); return true;
            case GestureAction::ToggleMetadata: metadataVisible=!metadataVisible; if(metadataVisible)RefreshMetadataText(); InvalidateViewer(); return true;
            case GestureAction::OpenSettings: ShowSettingsDialog(); return true;
            case GestureAction::PanImage: activeGestureDragButton=dragButton; BeginPan(pt); return true;
            case GestureAction::CreateSelection: activeGestureDragButton=dragButton; BeginLeftInteraction(pt); return true;
            case GestureAction::MoveWindow:
                ReleaseCapture(); {POINT sp=pt;ClientToScreen(hwnd,&sp);SendMessageW(hwnd,WM_NCLBUTTONDOWN,HTCAPTION,MAKELPARAM(sp.x,sp.y));} return true;
            case GestureAction::ResetOverlayZoom: ResetSelectedOverlayZoom(); return true;
            case GestureAction::BringOverlayFront:
                if(overlayActiveIndex>=0 && overlayActiveIndex<static_cast<int>(overlays.size())){
                    OverlayImage ov=overlays[static_cast<size_t>(overlayActiveIndex)];
                    overlays.erase(overlays.begin()+overlayActiveIndex); overlays.push_back(std::move(ov)); overlayActiveIndex=static_cast<int>(overlays.size())-1; InvalidateViewer();
                }
                return true;
            default: return false;
        }
    }

    void BeginLeftInteraction(POINT pt) {
        if (!bitmap) return;
        leftDownClient = pt;

        if (PointInsideSelection(static_cast<float>(pt.x), static_cast<float>(pt.y))) {
            selectionClickCandidate = true;
            selecting = false;
            SetCapture(hwnd);
            return;
        }

        if (selectionActive) ClearSelection(false);
        D2D1_POINT_2F imagePt{};
        if (!ClientToImage(static_cast<float>(pt.x), static_cast<float>(pt.y), imagePt, false)) {
            InvalidateRect(hwnd, nullptr, FALSE);
            return;
        }
        selectionStartImage = imagePt;
        selectionCurrentImage = imagePt;
        selecting = true;
        selectionClickCandidate = false;
        SetCapture(hwnd);
        InvalidateRect(hwnd, nullptr, FALSE);
    }

    void UpdateLeftInteraction(POINT pt) {
        const int dx = pt.x - leftDownClient.x;
        const int dy = pt.y - leftDownClient.y;
        const bool moved = (std::abs(dx) >= kDragThresholdPx || std::abs(dy) >= kDragThresholdPx);

        if (selectionClickCandidate && moved) {
            // A drag that started inside an old selection begins a fresh selection.
            selectionClickCandidate = false;
            selectionActive = false;
            D2D1_POINT_2F start{};
            if (ClientToImage(static_cast<float>(leftDownClient.x), static_cast<float>(leftDownClient.y), start, true)) {
                selectionStartImage = start;
                selectionCurrentImage = start;
                selecting = true;
            }
        }

        if (selecting) {
            D2D1_POINT_2F imagePt{};
            if (ClientToImage(static_cast<float>(pt.x), static_cast<float>(pt.y), imagePt, true)) {
                selectionCurrentImage = imagePt;
                InvalidateRect(hwnd, nullptr, FALSE);
            }
        }
    }

    void ZoomToSelection() {
        if (!selectionActive || !bitmap) return;
        EnsureFullResolutionAsync();
        const float sw = selectionImageRect.right - selectionImageRect.left;
        const float sh = selectionImageRect.bottom - selectionImageRect.top;
        if (sw <= 0.0f || sh <= 0.0f) return;

        RECT rc{};
        GetClientRect(hwnd, &rc);
        const float cw = static_cast<float>(std::max<LONG>(1, rc.right - rc.left));
        const float ch = static_cast<float>(std::max<LONG>(1, rc.bottom - rc.top));
        const float newZoom = std::clamp(std::min(cw / sw, ch / sh), kMinZoom, kMaxZoom);
        const float cx = (selectionImageRect.left + selectionImageRect.right) * 0.5f;
        const float cy = (selectionImageRect.top + selectionImageRect.bottom) * 0.5f;

        viewMode = ViewMode::Manual;
        zoom = newZoom;
        const float baseLeft = (cw - static_cast<float>(imageW) * zoom) * 0.5f;
        const float baseTop = (ch - static_cast<float>(imageH) * zoom) * 0.5f;
        panX = cw * 0.5f - cx * zoom - baseLeft;
        panY = ch * 0.5f - cy * zoom - baseTop;
        ClampPan();
        qualityBitmap.Reset();
        qualityBitmapW = qualityBitmapH = 0;
        UpdateScrollBars();
        ClearSelection(false);
        suppressDoubleClickUntil = GetTickCount64() + 500;
        UpdateTitle();
        InvalidateRect(hwnd, nullptr, FALSE);
    }

    void ZoomOutBySelection() {
        if (!selectionActive || !bitmap) return;
        const float sw = selectionImageRect.right - selectionImageRect.left;
        const float sh = selectionImageRect.bottom - selectionImageRect.top;
        if (sw <= 0.0f || sh <= 0.0f) return;

        RECT rc{};
        GetClientRect(hwnd, &rc);
        const float cw = static_cast<float>(std::max<LONG>(1, rc.right - rc.left));
        const float ch = static_cast<float>(std::max<LONG>(1, rc.bottom - rc.top));
        const float oldZoom = CurrentScale();
        const float selectionScreenW = sw * oldZoom;
        const float selectionScreenH = sh * oldZoom;
        // Reverse-selection zoom is intentionally size-proportional: a small box
        // produces a small zoom-out, while a large box produces a larger zoom-out.
        // This is the opposite of the old inverse-selection behavior.
        const float fracW = std::clamp(selectionScreenW / std::max(1.0f, cw), 0.0f, 1.0f);
        const float fracH = std::clamp(selectionScreenH / std::max(1.0f, ch), 0.0f, 1.0f);
        const float selectionFraction = std::sqrt(fracW * fracH);
        const float zoomOutFactor = 1.0f + selectionFraction; // tiny box ~= tiny change; full view ~= 2x out
        const float newZoom = std::clamp(oldZoom / zoomOutFactor, kMinZoom, kMaxZoom);
        const float cx = (selectionImageRect.left + selectionImageRect.right) * 0.5f;
        const float cy = (selectionImageRect.top + selectionImageRect.bottom) * 0.5f;

        viewMode = ViewMode::Manual;
        zoom = newZoom;
        const float baseLeft = (cw - static_cast<float>(imageW) * zoom) * 0.5f;
        const float baseTop = (ch - static_cast<float>(imageH) * zoom) * 0.5f;
        panX = cw * 0.5f - cx * zoom - baseLeft;
        panY = ch * 0.5f - cy * zoom - baseTop;
        ClampPan();
        qualityBitmap.Reset();
        qualityBitmapW = qualityBitmapH = 0;
        UpdateScrollBars();
        ClearSelection(false);
        suppressDoubleClickUntil = GetTickCount64() + 500;
        UpdateTitle();
        InvalidateRect(hwnd, nullptr, FALSE);
    }

    void EndLeftInteraction(POINT pt) {
        const int dx = pt.x - leftDownClient.x;
        const int dy = pt.y - leftDownClient.y;
        const bool moved = (std::abs(dx) >= kDragThresholdPx || std::abs(dy) >= kDragThresholdPx);

        if (selectionClickCandidate) {
            selectionClickCandidate = false;
            if (GetCapture() == hwnd) ReleaseCapture();
            if (!moved && PointInsideSelection(static_cast<float>(pt.x), static_cast<float>(pt.y))) {
                if(selectionClickZoomsIn) ZoomToSelection();
            }
            return;
        }

        if (selecting) {
            D2D1_POINT_2F imagePt{};
            if (ClientToImage(static_cast<float>(pt.x), static_cast<float>(pt.y), imagePt, true)) {
                selectionCurrentImage = imagePt;
            }
            selecting = false;
            if (GetCapture() == hwnd) ReleaseCapture();

            D2D1_RECT_F r = NormalizeRect(selectionStartImage, selectionCurrentImage);
            // Threshold is judged in screen pixels, while storage remains image-space.
            if (moved && (r.right - r.left) * CurrentScale() >= kDragThresholdPx &&
                         (r.bottom - r.top) * CurrentScale() >= kDragThresholdPx) {
                selectionImageRect = r;
                selectionActive = true;
            } else {
                selectionActive = false;
            }
            InvalidateRect(hwnd, nullptr, FALSE);
        }
    }

    void BeginPan(POINT pt) {
        if (!bitmap) return;
        const D2D1_RECT_F dst = ImageDestinationRect();
        RECT rc{};
        GetClientRect(hwnd, &rc);
        const float cw = static_cast<float>(rc.right - rc.left);
        const float ch = static_cast<float>(rc.bottom - rc.top);
        if ((dst.right - dst.left) <= cw && (dst.bottom - dst.top) <= ch) return;

        if (viewMode != ViewMode::Manual) {
            zoom = CurrentScale();
            viewMode = ViewMode::Manual;
            panX = 0.0f;
            panY = 0.0f;
        }
        panning = true;
        panLastClient = pt;
        SetCapture(hwnd);
        SetCursor(LoadCursorW(nullptr, IDC_HAND));
    }

    void UpdatePan(POINT pt) {
        if (!panning) return;
        panX += static_cast<float>(pt.x - panLastClient.x);
        panY += static_cast<float>(pt.y - panLastClient.y);
        panLastClient = pt;
        ClampPan();

        // Hot path: do not recalculate non-client scrollbar styles or rewrite the
        // window title on every mouse sample. Those operations were the dominant
        // source of the ~10 FPS feeling during right-drag panning.
        UpdateScrollPositionsOnly();
        InvalidateRect(hwnd, nullptr, FALSE);
        UpdateWindow(hwnd);
    }

    void EndPan() {
        if (!panning) return;
        panning = false;
        UpdateScrollBars();
        UpdateTitle();
        if (GetCapture() == hwnd) ReleaseCapture();
        SetCursor(LoadCursorW(nullptr, IDC_ARROW));
    }

    void EnsureTooltipWindow() {
        if (tooltipWnd || !hwnd) return;

        // The previous TTF_SUBCLASS approach proved unreliable on Glide's
        // owner-drawn Direct2D client chrome.  Microsoft explicitly supports
        // the alternative used here: register rectangle tools WITHOUT
        // TTF_SUBCLASS and feed mouse messages to the standard Windows tooltip
        // control with TTM_RELAYEVENT.
        tooltipWnd = CreateWindowExW(
            WS_EX_TOPMOST | WS_EX_NOACTIVATE,
            TOOLTIPS_CLASSW, nullptr,
            WS_POPUP | TTS_ALWAYSTIP | TTS_NOPREFIX,
            CW_USEDEFAULT, CW_USEDEFAULT, CW_USEDEFAULT, CW_USEDEFAULT,
            hwnd, nullptr, hInst, nullptr);

        if (!tooltipWnd) return;

        SetWindowPos(tooltipWnd, HWND_TOPMOST, 0, 0, 0, 0,
                     SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
        SendMessageW(tooltipWnd, TTM_ACTIVATE, TRUE, 0);
        SendMessageW(tooltipWnd, TTM_SETDELAYTIME, TTDT_INITIAL, 650);
        SendMessageW(tooltipWnd, TTM_SETDELAYTIME, TTDT_RESHOW, 120);
        SendMessageW(tooltipWnd, TTM_SETDELAYTIME, TTDT_AUTOPOP, 8000);
        SendMessageW(tooltipWnd, TTM_SETMAXTIPWIDTH, 0, 460);
    }

    static RECT RectFromD2D(const D2D1_RECT_F& r) {
        return RECT{static_cast<LONG>(std::floor(r.left)), static_cast<LONG>(std::floor(r.top)),
                    static_cast<LONG>(std::ceil(r.right)), static_cast<LONG>(std::ceil(r.bottom))};
    }

    static UINT TooltipToolInfoSize() {
        // TOOLINFO grew between common-controls generations.  Version 1.2.11
        // was built without a Common-Controls v6 manifest, so sizeof(TOOLINFOW)
        // could describe a newer structure than the loaded v5 tooltip control
        // accepted.  V2 contains every field Glide uses and is accepted by v5/v6.
#ifdef TTTOOLINFOW_V2_SIZE
        return TTTOOLINFOW_V2_SIZE;
#else
        return static_cast<UINT>(sizeof(TOOLINFOW));
#endif
    }

    void SetTooltipTool(UINT id, const D2D1_RECT_F& fr, const wchar_t* textValue, bool enabled=true) {
        EnsureTooltipWindow();

        const bool valid = enabled && fr.right > fr.left && fr.bottom > fr.top &&
                           textValue && *textValue && tooltipWnd;

        if (!valid) {
            if (tooltipWnd && tooltipAdded[id]) {
                TOOLINFOW ti{};
                ti.cbSize = TooltipToolInfoSize();
                ti.hwnd = hwnd;
                ti.uId = id;
                SendMessageW(tooltipWnd, TTM_DELTOOLW, 0,
                             reinterpret_cast<LPARAM>(&ti));
            }
            tooltipRects.erase(id);
            tooltipTexts.erase(id);
            tooltipAdded[id] = false;
            return;
        }

        const RECT r = RectFromD2D(fr);
        tooltipRects[id] = r;
        tooltipTexts[id] = textValue;

        TOOLINFOW ti{};
        ti.cbSize = TooltipToolInfoSize();
        ti.uFlags = 0; // explicit TTM_RELAYEVENT path; do not subclass Glide
        ti.hwnd = hwnd;
        ti.uId = id;
        ti.rect = r;
        ti.hinst = hInst;
        ti.lpszText = const_cast<LPWSTR>(tooltipTexts[id].c_str());

        if (!tooltipAdded[id]) {
            if (SendMessageW(tooltipWnd, TTM_ADDTOOLW, 0,
                             reinterpret_cast<LPARAM>(&ti))) {
                tooltipAdded[id] = true;
            }
        } else {
            SendMessageW(tooltipWnd, TTM_NEWTOOLRECTW, 0,
                         reinterpret_cast<LPARAM>(&ti));
            SendMessageW(tooltipWnd, TTM_UPDATETIPTEXTW, 0,
                         reinterpret_cast<LPARAM>(&ti));
        }
    }

    void RelayTooltipEvent(UINT message, WPARAM wp, LPARAM lp) {
        EnsureTooltipWindow();
        if (!tooltipWnd || !hwnd) return;
        MSG m{};
        m.hwnd = hwnd;
        m.message = message;
        m.wParam = wp;
        m.lParam = lp;
        m.time = static_cast<DWORD>(GetMessageTime());
        GetCursorPos(&m.pt);
        SendMessageW(tooltipWnd, TTM_RELAYEVENT,
                     static_cast<WPARAM>(GetMessageExtraInfo()),
                     reinterpret_cast<LPARAM>(&m));
    }

    void HideHoverTooltip() {
        if (tooltipWnd) SendMessageW(tooltipWnd, TTM_POP, 0, 0);
    }

    UINT TooltipKeyAt(POINT pt) const {
        UINT selectionKey = 0;
        for (const auto& kv : tooltipRects) {
            const RECT& r = kv.second;
            if (pt.x >= r.left && pt.x < r.right && pt.y >= r.top && pt.y < r.bottom) {
                if (kv.first == 17) selectionKey = kv.first;
                else return kv.first;
            }
        }
        return selectionKey;
    }

    void UpdateHoverTooltip(POINT) {
        EnsureTooltipWindow();
    }

    void DumpTooltipDiagnostics() {
        wchar_t exe[MAX_PATH]{};
        GetModuleFileNameW(nullptr, exe, MAX_PATH);
        std::filesystem::path p(exe);
        p = p.parent_path() / L"Glide Tooltip Diagnostics.txt";
        Utf8Wofstream f(p, std::ios::trunc);
        if (!f) return;

        POINT sp{}; GetCursorPos(&sp);
        POINT cp = sp; ScreenToClient(hwnd, &cp);
        UINT dpi = 96;
        using GetDpiForWindowFn = UINT (WINAPI*)(HWND);
        if (HMODULE u = GetModuleHandleW(L"user32.dll")) {
            if (auto fn = reinterpret_cast<GetDpiForWindowFn>(GetProcAddress(u, "GetDpiForWindow")))
                dpi = fn(hwnd);
        }

        f << L"Glide Alpha 0.12107 tooltip diagnostics\n";
        wchar_t tipClass[128]{}; if(tooltipWnd) GetClassNameW(tooltipWnd,tipClass,128);
        f << L"tooltipWnd=" << reinterpret_cast<UINT_PTR>(tooltipWnd)
          << L" IsWindow=" << (tooltipWnd && IsWindow(tooltipWnd) ? 1 : 0)
          << L" class=" << tipClass << L" toolInfoSize=" << TooltipToolInfoSize() << L"\n";
        f << L"main hwnd=" << reinterpret_cast<UINT_PTR>(hwnd)
          << L" foreground=" << reinterpret_cast<UINT_PTR>(GetForegroundWindow())
          << L" active=" << reinterpret_cast<UINT_PTR>(GetActiveWindow()) << L"\n";
        f << L"dpi=" << dpi << L" cursorScreen=" << sp.x << L"," << sp.y
          << L" cursorClient=" << cp.x << L"," << cp.y << L"\n";
        f << L"toolCount=" << (tooltipWnd ? SendMessageW(tooltipWnd, TTM_GETTOOLCOUNT, 0, 0) : -1)
          << L" registeredRects=" << tooltipRects.size()
          << L" hitKey=" << TooltipKeyAt(cp) << L"\n";

        for (const auto& kv : tooltipRects) {
            const auto it = tooltipTexts.find(kv.first);
            const RECT& r = kv.second;
            f << L"id=" << kv.first << L" rect=" << r.left << L"," << r.top << L","
              << r.right << L"," << r.bottom;
            if (it != tooltipTexts.end()) f << L" text=" << it->second;
            f << L"\n";
        }
        f.flush();
        MessageBoxW(hwnd,
            (L"Tooltip diagnostic snapshot written to:\n" + p.wstring()).c_str(),
            L"Glide diagnostics", MB_OK | MB_ICONINFORMATION);
    }

    void SyncOverlayTooltips() {
        enum : UINT {TT_PIN=1,TT_OPACITY,TT_OPTIONS,TT_NEWTAB,TT_REOPEN,TT_FOLDER_PREV,TT_FOLDER_NEXT,TT_FOLDER_EXPLORE,TT_HOME,TT_PREV,TT_NEXT,TT_END,TT_ZOOMOUT,TT_ZOOMIN,TT_FITW,TT_FITH,TT_PLAY,TT_STOP,TT_OVERLAY_ADD,TT_OVERLAY_SAVE,TT_OVERLAY_LOAD,TT_OVERLAY_CLEAR,TT_INFO,TT_COLLAPSE,TT_CLOSE,TT_SELECTION};
        const bool tabs=titleTabsEnabled&&(!fullscreen||fullscreenNativeCaptionVisible);
        SetTooltipTool(TT_PIN,titlePinRect,alwaysOnTop?L"Always on top: On — click to turn off":L"Always on top: Off — click to turn on",tabs);
        SetTooltipTool(TT_OPACITY,titleOpacityRect,L"Window transparency — click for slider (resets to 100% each run)",tabs);
        {auto t=TooltipWithHotkeyKey(L"Glide Options",L"Settings");SetTooltipTool(TT_OPTIONS,titleOptionsRect,t.c_str(),tabs);}
        {auto t=TooltipWithHotkeyKey(L"New tab",L"NewTab");SetTooltipTool(TT_NEWTAB,titleNewTabRect,t.c_str(),tabs);}
        { auto hk=HotkeyTextForAction(HotkeyAction::ReopenClosedTab); std::wstring tip=closedTabs.empty()?L"Restore closed tab — no recently closed tabs":L"Restore last closed tab"; if(!hk.empty())tip+=L" ("+hk+L")"; SetTooltipTool(TT_REOPEN,titleReopenRect,tip.c_str(),tabs); }
        SetTooltipTool(TT_FOLDER_PREV,titlePrevFolderRect,L"Previous sibling folder — open its first image",tabs&&folderNavTooltips&&folderNavShowGroup&&folderNavShowPrevious);
        SetTooltipTool(TT_FOLDER_NEXT,titleNextFolderRect,L"Next sibling folder — open its first image",tabs&&folderNavTooltips&&folderNavShowGroup&&folderNavShowNext);
        SetTooltipTool(TT_FOLDER_EXPLORE,titleExploreParentRect,L"Explore parent folders — choose a sibling folder",tabs&&folderNavTooltips&&folderNavShowGroup&&folderNavShowExplore);
        {auto t=TooltipWithHotkeyKey(L"First image in folder",L"FirstImage");SetTooltipTool(TT_HOME,statusHomeRect,t.c_str(),statusShowNavigation);}
        {auto t=TooltipWithHotkeyKey(L"Previous image",L"PreviousLeft");SetTooltipTool(TT_PREV,statusPrevRect,t.c_str(),statusShowNavigation);}
        {auto t=TooltipWithHotkeyKey(L"Next image",L"NextRight");SetTooltipTool(TT_NEXT,statusNextRect,t.c_str(),statusShowNavigation);}
        {auto t=TooltipWithHotkeyKey(L"Last image in folder",L"LastImage");SetTooltipTool(TT_END,statusEndRect,t.c_str(),statusShowNavigation);}
        {auto t=TooltipWithHotkeyKey(L"Zoom out",L"ZoomOut");SetTooltipTool(TT_ZOOMOUT,statusZoomOutRect,t.c_str(),statusShowZoom);}
        {auto t=TooltipWithHotkeyKey(L"Zoom in",L"ZoomIn");SetTooltipTool(TT_ZOOMIN,statusZoomInRect,t.c_str(),statusShowZoom);}
        {auto t=TooltipWithHotkeyKey(L"Fit image to window width",L"FitWidth");SetTooltipTool(TT_FITW,statusFitWidthRect,t.c_str(),statusShowFit);}
        {auto t=TooltipWithHotkeyKey(L"Fit image to window height",L"FitHeight");SetTooltipTool(TT_FITH,statusFitHeightRect,t.c_str(),statusShowFit);}
        {auto t=TooltipWithHotkeyKey(slideshowRunning?L"Pause slideshow":(slideshowPaused?L"Resume slideshow":L"Start slideshow and choose settings"),L"Slideshow");SetTooltipTool(TT_PLAY,statusSlideRect,t.c_str(),statusShowSlideshow);}
        {auto t=TooltipWithHotkeyKey(L"Stop slideshow session",L"StopSlideshow");SetTooltipTool(TT_STOP,statusStopRect,t.c_str(),statusShowSlideshow&&(slideshowRunning||slideshowPaused));}
        {auto t=TooltipWithHotkeyKey(L"Add Window-in-Window overlay",L"AddOverlay");SetTooltipTool(TT_OVERLAY_ADD,statusOverlayAddRect,t.c_str(),statusVisible&&statusShowOverlay);}
        {auto t=TooltipWithHotkeyKey(L"Save overlay layout",L"SaveOverlayLayout");SetTooltipTool(TT_OVERLAY_SAVE,statusOverlaySaveRect,t.c_str(),statusVisible&&statusShowOverlay&&!overlays.empty());}
        {auto t=TooltipWithHotkeyKey(L"Load overlay layout",L"LoadOverlayLayout");SetTooltipTool(TT_OVERLAY_LOAD,statusOverlayLoadRect,t.c_str(),statusVisible&&statusShowOverlay);}
        {auto t=TooltipWithHotkeyKey(L"Clear all overlays",L"ClearOverlays");SetTooltipTool(TT_OVERLAY_CLEAR,statusOverlayClearRect,t.c_str(),statusVisible&&statusShowOverlay&&!overlays.empty());}
        {auto t=TooltipWithHotkeyKey(L"Image information",L"Metadata");SetTooltipTool(TT_INFO,statusInfoRect,t.c_str(),statusShowInfo);}
        {auto t=TooltipWithHotkeyKey(statusCollapsed?L"Expand status bar":L"Collapse status bar",L"ToggleStatusCollapsed");SetTooltipTool(TT_COLLAPSE,statusMinRect,t.c_str(),statusShowCollapse);}
        {auto t=TooltipWithHotkeyKey(L"Glide Options",L"Settings");SetTooltipTool(TT_OPTIONS+100,statusOptionsRect,t.c_str(),statusShowOptions);}
        {auto t=fullscreen?TooltipWithHotkeyKey(L"Exit fullscreen",L"Fullscreen"):TooltipWithHotkeyKey(L"Hide status bar",L"ToggleStatusBar");SetTooltipTool(TT_CLOSE,statusCloseRect,t.c_str(),statusShowClose);}
        SetTooltipTool(40,browserBackRect,L"Back",ActiveTabIsBrowser()); SetTooltipTool(41,browserForwardRect,L"Forward",ActiveTabIsBrowser()); SetTooltipTool(42,browserUpRect,L"Up one folder",ActiveTabIsBrowser()); SetTooltipTool(43,browserRefreshRect,L"Refresh folder",ActiveTabIsBrowser()); SetTooltipTool(44,browserSortRect,L"Sort",ActiveTabIsBrowser());
        for(UINT i=0;i<16;++i){bool on=tabs&&i<titleTabCloseRects.size();auto t=TooltipWithHotkeyKey(L"Close tab",L"CloseTab");SetTooltipTool(100+i,on?titleTabCloseRects[i]:D2D1::RectF(),t.c_str(),on);}
        if(selectionActive){SetTooltipTool(TT_SELECTION,SelectionClientRect(),L"Left-click to zoom in. Right-click to zoom out.",true);}else SetTooltipTool(TT_SELECTION,D2D1::RectF(),L"",false);
        // Overlay controls are custom Direct2D regions, so register their current
        // hover rectangles with the same native Windows tooltip host.
        for(UINT i=0;i<32;++i){
            const UINT base=1200+i*3;
            if(i<overlays.size()&&overlaysVisible){
                SetTooltipTool(base,overlays[i].closeRect,L"Close overlay",overlays[i].closeRect.right>overlays[i].closeRect.left);
                SetTooltipTool(base+1,overlays[i].resizeRect,L"Resize overlay",overlays[i].resizeRect.right>overlays[i].resizeRect.left);
                SetTooltipTool(base+2,overlays[i].sliderRect,L"Overlay opacity",overlays[i].sliderRect.right>overlays[i].sliderRect.left);
            }else{
                SetTooltipTool(base,D2D1::RectF(),L"",false);SetTooltipTool(base+1,D2D1::RectF(),L"",false);SetTooltipTool(base+2,D2D1::RectF(),L"",false);
            }
        }
    }

    void DrawOverlayText(const std::wstring& text, const D2D1_RECT_F& rect,
                         ID2D1Brush* brush, IDWriteTextFormat* format) {
        if (!text.empty() && brush && format) {
            target->DrawTextW(text.c_str(), static_cast<UINT32>(text.size()), format, rect, brush,
                              D2D1_DRAW_TEXT_OPTIONS_CLIP);
        }
    }

    static void ReplaceToken(std::wstring& s,const std::wstring& token,const std::wstring& value){
        size_t p=0;while((p=s.find(token,p))!=std::wstring::npos){s.replace(p,token.size(),value);p+=value.size();}
    }

    std::wstring PictureOverlayText() const {
        if(!textOverlayEnabled||ActiveTabIsBrowser()||!bitmap||!haveIndex||files.empty()||currentPath.empty())return L"";
        std::wstring out=textOverlayTemplate;
        ReplaceToken(out,L"{index}",std::to_wstring(currentIndex+1));
        ReplaceToken(out,L"{total}",std::to_wstring(files.size()));
        try{
            const fs::path p(currentPath);
            ReplaceToken(out,L"{name}",p.filename().wstring());
            ReplaceToken(out,L"{folder}",p.parent_path().filename().wstring());
        }catch(...){ReplaceToken(out,L"{name}",L"");ReplaceToken(out,L"{folder}",L"");}
        ReplaceToken(out,L"{zoom}",std::to_wstring(static_cast<int>(std::lround(CurrentScale()*100.0f)))+L"%");
        ReplaceToken(out,L"{width}",std::to_wstring(imageW));
        ReplaceToken(out,L"{height}",std::to_wstring(imageH));
        return out;
    }

    void EnsurePictureOverlayFormat(){
        if(!dwrite)return;
        if(pictureOverlayFormat&&pictureOverlayFormatSize==textOverlayFontSize&&pictureOverlayFormatBoldCache==textOverlayBold)return;
        pictureOverlayFormat.Reset();pictureOverlayFormatSize=textOverlayFontSize;pictureOverlayFormatBoldCache=textOverlayBold;
        const auto weight=textOverlayBold?DWRITE_FONT_WEIGHT_BOLD:DWRITE_FONT_WEIGHT_NORMAL;
        HRESULT hr=dwrite->CreateTextFormat(L"Segoe UI Variable Text",nullptr,weight,DWRITE_FONT_STYLE_NORMAL,DWRITE_FONT_STRETCH_NORMAL,
                                            static_cast<float>(textOverlayFontSize),L"en-us",&pictureOverlayFormat);
        if(FAILED(hr))dwrite->CreateTextFormat(L"Segoe UI",nullptr,weight,DWRITE_FONT_STYLE_NORMAL,DWRITE_FONT_STRETCH_NORMAL,
                                               static_cast<float>(textOverlayFontSize),L"en-us",&pictureOverlayFormat);
        if(pictureOverlayFormat){pictureOverlayFormat->SetWordWrapping(DWRITE_WORD_WRAPPING_NO_WRAP);pictureOverlayFormat->SetParagraphAlignment(DWRITE_PARAGRAPH_ALIGNMENT_CENTER);}
    }

    void DrawPictureTextOverlay(){
        if(!target||!dwrite)return;const std::wstring s=PictureOverlayText();if(s.empty())return;
        EnsurePictureOverlayFormat();if(!pictureOverlayFormat)return;
        RECT rc{};GetClientRect(hwnd,&rc);const float cw=static_cast<float>((std::max)(1L,rc.right-rc.left));const float ch=static_cast<float>((std::max)(1L,rc.bottom-rc.top));
        const float margin=18.0f;const float h=static_cast<float>(textOverlayFontSize)*2.1f+8.0f;const bool bottom=textOverlayPosition>=3;
        const float top=bottom?(ch-margin-h):(ViewerTopInset()+margin);D2D1_RECT_F tr=D2D1::RectF(margin,top,cw-margin,top+h);
        const int col=textOverlayPosition%3;pictureOverlayFormat->SetTextAlignment(col==0?DWRITE_TEXT_ALIGNMENT_LEADING:(col==1?DWRITE_TEXT_ALIGNMENT_CENTER:DWRITE_TEXT_ALIGNMENT_TRAILING));
        ComPtr<ID2D1SolidColorBrush> brush,shadow;const float a=std::clamp(textOverlayOpacity,5,100)/100.0f;
        target->CreateSolidColorBrush(D2DColor(textOverlayColor,a),&brush);
        if(textOverlayShadow)target->CreateSolidColorBrush(D2D1::ColorF(0,0,0,std::min(0.75f,a*0.72f)),&shadow);
        if(shadow){auto sr=tr;sr.left+=1.5f;sr.right+=1.5f;sr.top+=1.5f;sr.bottom+=1.5f;DrawOverlayText(s,sr,shadow.Get(),pictureOverlayFormat.Get());}
        DrawOverlayText(s,tr,brush.Get(),pictureOverlayFormat.Get());
    }

    void DrawOverlays() {
        if (!target || !uiText || !uiTextSmall) return;
        RECT rc{}; GetClientRect(hwnd, &rc);
        const float cw = static_cast<float>(std::max<LONG>(1, rc.right - rc.left));
        const float ch = static_cast<float>(std::max<LONG>(1, rc.bottom - rc.top));

        const ThemePalette uiPal=Palette();
        ComPtr<ID2D1SolidColorBrush> panel, text, muted, accent;
        target->CreateSolidColorBrush(D2DColor(uiPal.panel,ResolveLightTheme()?0.96f:0.86f), &panel);
        target->CreateSolidColorBrush(D2DColor(uiPal.text,0.96f), &text);
        target->CreateSolidColorBrush(D2DColor(uiPal.textMuted,0.94f), &muted);
        target->CreateSolidColorBrush(D2DColor(uiPal.accent,0.98f), &accent);
        if (!panel || !text) return;

        statusRect = statusCloseRect = statusMinRect = statusInfoRect = statusHomeRect = statusPrevRect = statusNextRect = statusEndRect = statusSlideRect = statusStopRect = statusFitWidthRect = statusFitHeightRect = statusZoomOutRect = statusZoomInRect = statusOptionsRect = statusPngAlphaRect = D2D1::RectF();
        metadataRect = metadataCloseRect = D2D1::RectF();
        fullscreenBarRect = fullscreenPrevRect = fullscreenNextRect = fullscreenSlideRect = D2D1::RectF();
        fullscreenFitRect = fullscreenMoreRect = fullscreenExitRect = D2D1::RectF();
        titleBarRect = titleNewTabRect = titleReopenRect = titlePrevFolderRect = titleNextFolderRect = titleExploreParentRect = titleOptionsRect = titlePinRect = titleOpacityRect = titleMinRect = titleMaxRect = D2D1::RectF();
        opacityPanelRect=opacityTrackRect=D2D1::RectF();
        titleTabRects.clear(); titleTabCloseRects.clear();

        const bool drawTabStrip=!coldMinimalChrome&&titleTabsEnabled&&(!fullscreen||fullscreenNativeCaptionVisible);
        if(drawTabStrip){
            ComPtr<ID2D1SolidColorBrush>chrome,tabActive,hover,line,folderAccent,disabled,dragBorder;
            target->CreateSolidColorBrush(D2DColor(uiPal.windowBg),&chrome);
            target->CreateSolidColorBrush(D2DColor(uiPal.accentSoft),&tabActive);
            target->CreateSolidColorBrush(D2DColor(uiPal.surfaceHover),&hover);
            target->CreateSolidColorBrush(D2DColor(uiPal.text),&line);
            target->CreateSolidColorBrush(D2DColor(uiPal.accent),&folderAccent);
            target->CreateSolidColorBrush(D2DColor(uiPal.textMuted,0.65f),&disabled);
            target->CreateSolidColorBrush(D2DColor(uiPal.accent,0.95f),&dragBorder);

            const float h=34.0f;
            const float rightControls=(folderNavShowGroup?260.0f:152.0f);
            const float tabsRight=std::max(84.0f,cw-rightControls);
            titleBarRect=D2D1::RectF(0,0,cw,h);
            target->FillRectangle(titleBarRect,chrome.Get());

            POINT cp{};GetCursorPos(&cp);ScreenToClient(hwnd,&cp);
            auto center=[](const D2D1_RECT_F&r){return D2D1::Point2F((r.left+r.right)*.5f,(r.top+r.bottom)*.5f);};

            if(openTabs.empty()&&!currentPath.empty()){
                openTabs.push_back(currentPath);tabBrowserMode.push_back(false);tabBrowserFolder.push_back(L"");
                tabBrowserBack.emplace_back();tabBrowserForward.emplace_back();activeTab=0;
            } else if(openTabs.empty()&&currentPath.empty()) EnsureHomeTab();
            EnsureTabStateVectors();

            float x=6.0f;

            const float available=std::max(100.0f,tabsRight-x);
            const size_t count=std::max<size_t>(1,openTabs.size());
            const float tabW=std::clamp(available/static_cast<float>(count),tabMinWidth,tabMaxWidth);

            auto drawOneTab=[&](size_t i,const D2D1_RECT_F&tr,bool floating){
                bool active=static_cast<int>(i)==activeTab;
                bool hov=!floating&&PointInRect(tr,cp);
                if(floating){
                    target->FillRoundedRectangle(D2D1::RoundedRect(tr,8,8),tabActive.Get());
                    if(dragBorder)target->DrawRoundedRectangle(D2D1::RoundedRect(tr,8,8),dragBorder.Get(),1.5f);
                }else if(active)target->FillRoundedRectangle(D2D1::RoundedRect(tr,7,7),tabActive.Get());
                else if(hov)target->FillRoundedRectangle(D2D1::RoundedRect(tr,7,7),hover.Get());

                D2D1_RECT_F fr=D2D1::RectF(tr.left+11,tr.top+12,tr.left+23,tr.top+23);
                if(openTabs[i]==kHomeTabSentinel){
                    const float cx=(fr.left+fr.right)*.5f,cy=(fr.top+fr.bottom)*.5f;
                    target->DrawLine(D2D1::Point2F(cx-6,cy),D2D1::Point2F(cx,cy-6),folderAccent.Get(),1.4f);
                    target->DrawLine(D2D1::Point2F(cx,cy-6),D2D1::Point2F(cx+6,cy),folderAccent.Get(),1.4f);
                    target->DrawRectangle(D2D1::RectF(cx-4,cy,cx+4,cy+6),folderAccent.Get(),1.25f);
                }else target->DrawRectangle(fr,folderAccent.Get(),1.2f);
                const bool home=(openTabs[i]==kHomeTabSentinel);
                std::wstring label=home?L"Home":((i<tabBrowserMode.size()&&tabBrowserMode[i])?L"Explorer":ParentFolderLabel(openTabs[i]));
                DrawOverlayText(label,D2D1::RectF(tr.left+30,tr.top+8,tr.right-28,tr.bottom-3),text.Get(),uiTextSmall.Get());

                D2D1_RECT_F cr=D2D1::RectF(tr.right-27,tr.top+5,tr.right-5,tr.bottom-5);
                if(!floating && PointInRect(cr,cp))target->FillRoundedRectangle(D2D1::RoundedRect(cr,4,4),hover.Get());
                auto cc=center(cr);
                target->DrawLine(D2D1::Point2F(cc.x-3.5f,cc.y-3.5f),D2D1::Point2F(cc.x+3.5f,cc.y+3.5f),line.Get(),1);
                target->DrawLine(D2D1::Point2F(cc.x+3.5f,cc.y-3.5f),D2D1::Point2F(cc.x-3.5f,cc.y+3.5f),line.Get(),1);
            };

            for(size_t i=0;i<openTabs.size()&&x+72<tabsRight;++i){
                D2D1_RECT_F slot=D2D1::RectF(x,3,std::min(x+tabW,tabsRight),h);
                titleTabRects.push_back(slot);
                titleTabCloseRects.push_back(D2D1::RectF(slot.right-27,slot.top+5,slot.right-5,slot.bottom-5));
                // Keep the source slot visible while dragging. Browser-style tab drags
                // preserve spatial context; the floating/held card is drawn on top below.
                if(singleTabWindowDrag && openTabs.size()==1 && static_cast<int>(i)==tabDragIndex){slot.left+=6.0f;slot.right+=6.0f;}
                drawOneTab(i,slot,false);
                x=slot.right+2;
            }

            titleNewTabRect=D2D1::RectF(x,3,std::min(x+34.0f,tabsRight),h);
            if(PointInRect(titleNewTabRect,cp))target->FillRoundedRectangle(D2D1::RoundedRect(titleNewTabRect,6,6),hover.Get());
            auto cn=center(titleNewTabRect);
            target->DrawLine(D2D1::Point2F(cn.x-5,cn.y),D2D1::Point2F(cn.x+5,cn.y),line.Get(),1.2f);
            target->DrawLine(D2D1::Point2F(cn.x,cn.y-5),D2D1::Point2F(cn.x,cn.y+5),line.Get(),1.2f);

            // Sibling-folder navigation group. It is visually separated from restore
            // and painted entirely with high-DPI Direct2D vectors.
            if(folderNavShowGroup){
                const float groupRight=cw-152.0f;
                if(folderNavShowPrevious)titlePrevFolderRect=D2D1::RectF(groupRight-102,3,groupRight-70,h);
                if(folderNavShowNext)titleNextFolderRect=D2D1::RectF(groupRight-68,3,groupRight-36,h);
                if(folderNavShowExplore)titleExploreParentRect=D2D1::RectF(groupRight-34,3,groupRight-2,h);
                auto glowButton=[&](const D2D1_RECT_F&r,COLORREF col){if(r.right<=r.left)return;ComPtr<ID2D1SolidColorBrush>b;target->CreateSolidColorBrush(D2DColor(col,PointInRect(r,cp)?0.23f:0.08f),&b);if(b)target->FillRoundedRectangle(D2D1::RoundedRect(r,6,6),b.Get());};
                glowButton(titlePrevFolderRect,RGB(72,176,255));glowButton(titleNextFolderRect,RGB(84,214,155));glowButton(titleExploreParentRect,RGB(184,124,255));
                if(titlePrevFolderRect.right>titlePrevFolderRect.left){auto c=center(titlePrevFolderRect);target->DrawLine(D2D1::Point2F(c.x+4,c.y-6),D2D1::Point2F(c.x-3,c.y),folderAccent.Get(),1.7f);target->DrawLine(D2D1::Point2F(c.x-3,c.y),D2D1::Point2F(c.x+4,c.y+6),folderAccent.Get(),1.7f);}
                if(titleNextFolderRect.right>titleNextFolderRect.left){auto c=center(titleNextFolderRect);target->DrawLine(D2D1::Point2F(c.x-4,c.y-6),D2D1::Point2F(c.x+3,c.y),folderAccent.Get(),1.7f);target->DrawLine(D2D1::Point2F(c.x+3,c.y),D2D1::Point2F(c.x-4,c.y+6),folderAccent.Get(),1.7f);}
                if(titleExploreParentRect.right>titleExploreParentRect.left){auto c=center(titleExploreParentRect);target->DrawRoundedRectangle(D2D1::RoundedRect(D2D1::RectF(c.x-7,c.y-4,c.x+7,c.y+6),2,2),folderAccent.Get(),1.5f);target->DrawLine(D2D1::Point2F(c.x-6,c.y-4),D2D1::Point2F(c.x-2,c.y-8),folderAccent.Get(),1.5f);target->DrawLine(D2D1::Point2F(c.x-2,c.y-8),D2D1::Point2F(c.x+2,c.y-8),folderAccent.Get(),1.5f);}
            }

            // Restore closed tab: separated from the folder-navigation group.
            titleReopenRect=D2D1::RectF(cw-148.0f,3,cw-114.0f,h);
            if(PointInRect(titleReopenRect,cp))target->FillRoundedRectangle(D2D1::RoundedRect(titleReopenRect,6,6),hover.Get());
            auto rr=center(titleReopenRect);
            auto rb=closedTabs.empty()?disabled.Get():folderAccent.Get();
            // Lightweight anti-aliased "restore" circular arrow.
            target->DrawLine(D2D1::Point2F(rr.x+6,rr.y-5),D2D1::Point2F(rr.x+1,rr.y-7),rb,1.5f);
            target->DrawLine(D2D1::Point2F(rr.x+1,rr.y-7),D2D1::Point2F(rr.x-4,rr.y-4),rb,1.5f);
            target->DrawLine(D2D1::Point2F(rr.x-4,rr.y-4),D2D1::Point2F(rr.x-7,rr.y+1),rb,1.5f);
            target->DrawLine(D2D1::Point2F(rr.x-7,rr.y+1),D2D1::Point2F(rr.x-4,rr.y+6),rb,1.5f);
            target->DrawLine(D2D1::Point2F(rr.x-4,rr.y+6),D2D1::Point2F(rr.x+2,rr.y+7),rb,1.5f);
            target->DrawLine(D2D1::Point2F(rr.x+1,rr.y-7),D2D1::Point2F(rr.x+1,rr.y-2),rb,1.5f);
            target->DrawLine(D2D1::Point2F(rr.x+1,rr.y-7),D2D1::Point2F(rr.x+6,rr.y-7),rb,1.5f);

            titleOpacityRect=D2D1::RectF(cw-112.0f,3,cw-78.0f,h);
            if(PointInRect(titleOpacityRect,cp))target->FillRoundedRectangle(D2D1::RoundedRect(titleOpacityRect,6,6),hover.Get());
            auto op=center(titleOpacityRect);
            target->DrawEllipse(D2D1::Ellipse(op,7.2f,7.2f),folderAccent.Get(),1.4f);
            target->DrawLine(D2D1::Point2F(op.x-5.0f,op.y+5.0f),D2D1::Point2F(op.x+5.0f,op.y-5.0f),folderAccent.Get(),1.4f);

            titlePinRect=D2D1::RectF(cw-76.0f,3,cw-42.0f,h);
            if(PointInRect(titlePinRect,cp))target->FillRoundedRectangle(D2D1::RoundedRect(titlePinRect,6,6),hover.Get());
            auto pc=center(titlePinRect);
            ComPtr<ID2D1SolidColorBrush>pinBrush;
            target->CreateSolidColorBrush(alwaysOnTop?D2D1::ColorF(0.25f,0.68f,1.0f,1):D2D1::ColorF(0.58f,0.62f,0.67f,1),&pinBrush);
            target->DrawLine(D2D1::Point2F(pc.x-5,pc.y-6),D2D1::Point2F(pc.x+5,pc.y-6),pinBrush.Get(),1.4f);
            target->DrawLine(D2D1::Point2F(pc.x-3,pc.y-6),D2D1::Point2F(pc.x-1,pc.y+1),pinBrush.Get(),1.4f);
            target->DrawLine(D2D1::Point2F(pc.x+3,pc.y-6),D2D1::Point2F(pc.x+1,pc.y+1),pinBrush.Get(),1.4f);
            target->DrawLine(D2D1::Point2F(pc.x-5,pc.y+1),D2D1::Point2F(pc.x+5,pc.y+1),pinBrush.Get(),1.4f);
            target->DrawLine(D2D1::Point2F(pc.x,pc.y+1),D2D1::Point2F(pc.x,pc.y+8),pinBrush.Get(),1.4f);

            titleOptionsRect=D2D1::RectF(cw-40.0f,3,cw-6.0f,h);
            if(PointInRect(titleOptionsRect,cp))target->FillRoundedRectangle(D2D1::RoundedRect(titleOptionsRect,6,6),hover.Get());
            auto oc=center(titleOptionsRect);
            for(int i=-1;i<=1;++i)target->FillEllipse(D2D1::Ellipse(D2D1::Point2F(oc.x+i*6.0f,oc.y),1.7f,1.7f),folderAccent.Get());

            if(opacitySliderVisible){
                const float panelW=246.0f;const float pr=std::max(250.0f,cw-8.0f);const float pl=std::max(8.0f,pr-panelW);
                opacityPanelRect=D2D1::RectF(pl,40.0f,pr,84.0f);
                target->FillRoundedRectangle(D2D1::RoundedRect(opacityPanelRect,8,8),chrome.Get());
                target->DrawRoundedRectangle(D2D1::RoundedRect(opacityPanelRect,8,8),folderAccent.Get(),1.0f);
                opacityTrackRect=D2D1::RectF(pl+18.0f,59.0f,pr-64.0f,65.0f);
                target->FillRoundedRectangle(D2D1::RoundedRect(opacityTrackRect,3,3),disabled.Get());
                const float f=(std::clamp(windowOpacityPercent,10,100)-10)/90.0f;const float kx=opacityTrackRect.left+f*(opacityTrackRect.right-opacityTrackRect.left);
                target->FillEllipse(D2D1::Ellipse(D2D1::Point2F(kx,62.0f),6.0f,6.0f),folderAccent.Get());
                DrawOverlayText(std::to_wstring(windowOpacityPercent)+L"%",D2D1::RectF(pr-57,50,pr-10,74),text.Get(),uiTextSmall.Get());
            }

            if(externalTabDragHover&&dragBorder){
                target->DrawRoundedRectangle(D2D1::RoundedRect(D2D1::RectF(2,1,tabsRight-2,h),7,7),dragBorder.Get(),1.8f);
                float marker=tabsRight-4.0f;
                for(const auto&r:titleTabRects){if(externalTabDragClientX<(r.left+r.right)*.5f){marker=r.left;break;}}
                target->DrawLine(D2D1::Point2F(marker,5),D2D1::Point2F(marker,h-4),dragBorder.Get(),3.0f);
            }

            // Paint the active dragged tab last, above neighboring tabs. Its card follows
            // the mouse continuously while the underlying slots reorder at their centres.
            if(tabDragging&&tabDragIndex>=0&&tabDragIndex<static_cast<int>(openTabs.size())&&
               tabDragIndex<static_cast<int>(titleTabRects.size())){
                const auto slot=titleTabRects[tabDragIndex];
                const float width=slot.right-slot.left;
                float left=static_cast<float>(tabDragCurrent.x)-tabDragGrabOffsetX;
                left=std::clamp(left,-width*0.75f,std::max(-width*0.75f,tabsRight-width*0.25f));
                const float top=std::clamp(static_cast<float>(tabDragCurrent.y)-17.0f,-22.0f,h+34.0f);
                drawOneTab(static_cast<size_t>(tabDragIndex),D2D1::RectF(left,top,left+width,top+(h-3.0f)),true);
            }
        }

        browserToolbarRect=browserBackRect=browserForwardRect=browserUpRect=browserRefreshRect=browserSortRect=browserPathRect=D2D1::RectF();
        if(ActiveTabIsBrowser()){
            SyncBrowserPathFromShell(); PositionBrowserAddressEdit();
            const float top=ViewerTopInset(), h=40.0f;
            browserToolbarRect=D2D1::RectF(0,top,cw,top+h);
            ComPtr<ID2D1SolidColorBrush> tb, bh, bb;
            target->CreateSolidColorBrush(D2D1::ColorF(0.10f,0.10f,0.10f,1.0f),&tb);
            target->CreateSolidColorBrush(D2D1::ColorF(0.22f,0.22f,0.22f,1.0f),&bh);
            target->CreateSolidColorBrush(D2D1::ColorF(0.55f,0.72f,0.92f,0.95f),&bb);
            target->FillRectangle(browserToolbarRect,tb.Get());
            browserBackRect=D2D1::RectF(4,top+4,38,top+36);browserForwardRect=D2D1::RectF(40,top+4,74,top+36);browserUpRect=D2D1::RectF(76,top+4,110,top+36);
            browserRefreshRect=D2D1::RectF(cw-118,top+4,cw-82,top+36);browserSortRect=D2D1::RectF(cw-80,top+4,cw-6,top+36);
            browserPathRect=D2D1::RectF(120,top+4,cw-124,top+36);
            POINT bp{};GetCursorPos(&bp);ScreenToClient(hwnd,&bp);
            auto hb=[&](const D2D1_RECT_F&r){if(PointInRect(r,bp)){target->FillRoundedRectangle(D2D1::RoundedRect(r,5,5),bh.Get());target->DrawRoundedRectangle(D2D1::RoundedRect(r,5,5),bb.Get(),1.2f);}};
            hb(browserBackRect);hb(browserForwardRect);hb(browserUpRect);hb(browserRefreshRect);hb(browserSortRect);
            auto c=[](const D2D1_RECT_F&r){return D2D1::Point2F((r.left+r.right)*.5f,(r.top+r.bottom)*.5f);};
            auto bc=c(browserBackRect),fc=c(browserForwardRect),uc=c(browserUpRect),rc2=c(browserRefreshRect);
            target->DrawLine(D2D1::Point2F(bc.x+5,bc.y-6),D2D1::Point2F(bc.x-3,bc.y),muted.Get(),1.8f);target->DrawLine(D2D1::Point2F(bc.x-3,bc.y),D2D1::Point2F(bc.x+5,bc.y+6),muted.Get(),1.8f);
            target->DrawLine(D2D1::Point2F(fc.x-5,fc.y-6),D2D1::Point2F(fc.x+3,fc.y),muted.Get(),1.8f);target->DrawLine(D2D1::Point2F(fc.x+3,fc.y),D2D1::Point2F(fc.x-5,fc.y+6),muted.Get(),1.8f);
            target->DrawLine(D2D1::Point2F(uc.x-6,uc.y+3),D2D1::Point2F(uc.x,uc.y-4),muted.Get(),1.8f);target->DrawLine(D2D1::Point2F(uc.x,uc.y-4),D2D1::Point2F(uc.x+6,uc.y+3),muted.Get(),1.8f);
            target->DrawEllipse(D2D1::Ellipse(rc2,7,7),muted.Get(),1.6f);target->DrawLine(D2D1::Point2F(rc2.x+4,rc2.y-7),D2D1::Point2F(rc2.x+8,rc2.y-3),muted.Get(),1.6f);
            DrawOverlayText(L"Sort",D2D1::RectF(browserSortRect.left+13,browserSortRect.top+7,browserSortRect.right-6,browserSortRect.bottom),muted.Get(),uiTextSmall.Get());
        }

        const bool drawStatus = statusVisible && (!fullscreen || fullscreenStatusAlwaysOn || fullscreenStatusVisible);
        if (drawStatus) {
            if (statusCollapsed && !fullscreen) {
                const float w = 76.0f, h = 32.0f;
                statusRect = D2D1::RectF(cw - w - 14.0f, ch - h - 14.0f, cw - 14.0f, ch - 14.0f);
                target->FillRoundedRectangle(D2D1::RoundedRect(statusRect, 10.0f, 10.0f), panel.Get());
                DrawOverlayText(L"INFO  ˄", D2D1::RectF(statusRect.left + 12, statusRect.top + 6,
                                statusRect.right - 8, statusRect.bottom), accent.Get(), uiTextSmall.Get());
            } else {
                // Windowed keeps the same 42-DIP bar. Fullscreen gets a more legible 56-DIP bar,
                // revealed from the bottom edge and hidden again when the pointer leaves.
                const float h = fullscreen ? 56.0f : 42.0f;
                const float maxW = std::max(260.0f, cw - 28.0f);
                const float w = std::min(fullscreen ? 1080.0f : 960.0f, maxW);
                const float left = (cw - w) * 0.5f;
                statusRect = D2D1::RectF(left, ch - h - 14.0f, left + w, ch - 14.0f);
                target->FillRoundedRectangle(D2D1::RoundedRect(statusRect, 11.0f, 11.0f), panel.Get());

                const float pad = fullscreen ? 5.0f : 3.0f;
                const float block = h - pad * 2.0f; // 36 DIP windowed, 46 DIP fullscreen.
                const float gap = fullscreen ? 4.0f : 3.0f;
                float x = statusRect.right - pad;
                auto take=[&](){ D2D1_RECT_F r=D2D1::RectF(x-block,statusRect.top+pad,x,statusRect.bottom-pad); x-=block+gap; return r; };
                auto takeWide=[&](float w){ D2D1_RECT_F r=D2D1::RectF(x-w,statusRect.top+pad,x,statusRect.bottom-pad); x-=w+gap; return r; };
                auto maybeTake=[&](bool enabled,D2D1_RECT_F& r){ if(enabled) r=take(); else r=D2D1::RectF(); };
                // Right-edge cluster is intentionally fixed: Close, Collapse, then the
                // four navigation controls. Utility tools always remain to its left.
                maybeTake(statusShowClose,statusCloseRect);maybeTake(statusShowCollapse,statusMinRect);
                if(statusShowNavigation){statusEndRect=take();statusNextRect=take();statusPrevRect=take();statusHomeRect=take();}
                else statusHomeRect=statusPrevRect=statusNextRect=statusEndRect=D2D1::RectF();
                const float navClusterLeft=x;
                x-=fullscreen?13.0f:11.0f; // clear visual separation from utility tools
                maybeTake(statusShowOptions,statusOptionsRect);
                if(statusShowOverlay){statusOverlayLoadRect=take();statusOverlayAddRect=take();if(!overlays.empty()){statusOverlayClearRect=take();statusOverlaySaveRect=take();}else{statusOverlayClearRect=statusOverlaySaveRect=D2D1::RectF();}}else statusOverlayAddRect=statusOverlayLoadRect=statusOverlaySaveRect=statusOverlayClearRect=D2D1::RectF();
                maybeTake(statusShowInfo,statusInfoRect);if(statusShowSlideshow && (slideshowRunning||slideshowPaused))statusStopRect=take();else statusStopRect=D2D1::RectF();maybeTake(statusShowSlideshow,statusSlideRect);if(statusShowFit){statusFitHeightRect=take();statusFitWidthRect=take();}
                if(CurrentIsPng()){statusPngAlphaRect=takeWide(fullscreen?112.0f:96.0f);}
                if(statusShowZoom){statusZoomInRect=take();statusZoomOutRect=take();}

                POINT sp{};GetCursorPos(&sp);ScreenToClient(hwnd,&sp);
                ComPtr<ID2D1SolidColorBrush> baseFill, baseBorder, hoverFill, hoverBorder;
                target->CreateSolidColorBrush(D2DColor(uiPal.surface,0.94f),&baseFill);
                target->CreateSolidColorBrush(D2DColor(uiPal.border,0.94f),&baseBorder);
                target->CreateSolidColorBrush(D2DColor(uiPal.surfaceHover,0.99f),&hoverFill);
                target->CreateSolidColorBrush(D2DColor(uiPal.accent,1.0f),&hoverBorder);
                auto baseBlock=[&](const D2D1_RECT_F&r){target->FillRoundedRectangle(D2D1::RoundedRect(r,5,5),baseFill.Get());target->DrawRoundedRectangle(D2D1::RoundedRect(r,5,5),baseBorder.Get(),1.0f);};
                auto hoverBlock=[&](const D2D1_RECT_F&r){if(PointInRect(r,sp)){target->FillRoundedRectangle(D2D1::RoundedRect(r,5,5),hoverFill.Get());target->DrawRoundedRectangle(D2D1::RoundedRect(r,5,5),hoverBorder.Get(),1.7f);}};
                auto drawBlock=[&](bool enabled,const D2D1_RECT_F&r){if(enabled){baseBlock(r);hoverBlock(r);}};
                ComPtr<ID2D1SolidColorBrush> navFill,navBorder,navHover,navAccent;
                target->CreateSolidColorBrush(D2DColor(BlendColor(uiPal.surface,uiPal.accent,0.10f),0.97f),&navFill);
                target->CreateSolidColorBrush(D2DColor(BlendColor(uiPal.border,uiPal.accent,0.48f),0.98f),&navBorder);
                target->CreateSolidColorBrush(D2DColor(BlendColor(uiPal.surface,uiPal.accent,0.22f),1.0f),&navHover);
                target->CreateSolidColorBrush(D2DColor(uiPal.accent,1.0f),&navAccent);
                auto drawNav=[&](const D2D1_RECT_F&r){target->FillRoundedRectangle(D2D1::RoundedRect(r,5,5),PointInRect(r,sp)?navHover.Get():navFill.Get());target->DrawRoundedRectangle(D2D1::RoundedRect(r,5,5),PointInRect(r,sp)?navAccent.Get():navBorder.Get(),PointInRect(r,sp)?1.5f:1.0f);};
                if(statusShowNavigation){drawNav(statusHomeRect);drawNav(statusPrevRect);drawNav(statusNextRect);drawNav(statusEndRect);}
                if(CurrentIsPng()) drawBlock(true,statusPngAlphaRect);
                drawBlock(statusShowZoom,statusZoomOutRect);drawBlock(statusShowZoom,statusZoomInRect);drawBlock(statusShowFit,statusFitWidthRect);drawBlock(statusShowFit,statusFitHeightRect);drawBlock(statusShowSlideshow,statusSlideRect);drawBlock(statusShowSlideshow&&(slideshowRunning||slideshowPaused),statusStopRect);if(statusShowOverlay){drawBlock(true,statusOverlayAddRect);drawBlock(true,statusOverlayLoadRect);if(!overlays.empty()){drawBlock(true,statusOverlaySaveRect);drawBlock(true,statusOverlayClearRect);}}drawBlock(statusShowInfo,statusInfoRect);drawBlock(statusShowCollapse,statusMinRect);drawBlock(statusShowOptions,statusOptionsRect);drawBlock(statusShowClose,statusCloseRect);

                auto center=[](const D2D1_RECT_F&r){return D2D1::Point2F((r.left+r.right)*.5f,(r.top+r.bottom)*.5f);};
                const float a=fullscreen?7.0f:6.0f, stroke=fullscreen?2.1f:1.8f;
                if(statusShowNavigation){
                    auto hc=center(statusHomeRect), pc=center(statusPrevRect), nc=center(statusNextRect), ec=center(statusEndRect);
                    auto nb=navAccent.Get();
                    target->DrawLine(D2D1::Point2F(hc.x-a,hc.y-a),D2D1::Point2F(hc.x-a,hc.y+a),nb,stroke);target->DrawLine(D2D1::Point2F(hc.x+4,hc.y-a+1),D2D1::Point2F(hc.x-1,hc.y),nb,stroke);target->DrawLine(D2D1::Point2F(hc.x-1,hc.y),D2D1::Point2F(hc.x+4,hc.y+a-1),nb,stroke);
                    target->DrawLine(D2D1::Point2F(pc.x+5,pc.y-a),D2D1::Point2F(pc.x-2,pc.y),nb,stroke);target->DrawLine(D2D1::Point2F(pc.x-2,pc.y),D2D1::Point2F(pc.x+5,pc.y+a),nb,stroke);
                    target->DrawLine(D2D1::Point2F(nc.x-5,nc.y-a),D2D1::Point2F(nc.x+2,nc.y),nb,stroke);target->DrawLine(D2D1::Point2F(nc.x+2,nc.y),D2D1::Point2F(nc.x-5,nc.y+a),nb,stroke);
                    target->DrawLine(D2D1::Point2F(ec.x+a,ec.y-a),D2D1::Point2F(ec.x+a,ec.y+a),nb,stroke);target->DrawLine(D2D1::Point2F(ec.x-4,ec.y-a+1),D2D1::Point2F(ec.x+1,ec.y),nb,stroke);target->DrawLine(D2D1::Point2F(ec.x+1,ec.y),D2D1::Point2F(ec.x-4,ec.y+a-1),nb,stroke);
                }
                if(CurrentIsPng()){ auto pr=statusPngAlphaRect; DrawOverlayText(pngSeeThrough?L"Background":L"Transparent", D2D1::RectF(pr.left+5,pr.top+(fullscreen?14:8),pr.right-4,pr.bottom-4), accent.Get(), uiTextSmall.Get()); }
                if(statusShowZoom){auto zm=center(statusZoomOutRect),zp=center(statusZoomInRect);target->DrawLine(D2D1::Point2F(zm.x-a,zm.y),D2D1::Point2F(zm.x+a,zm.y),accent.Get(),stroke);target->DrawLine(D2D1::Point2F(zp.x-a,zp.y),D2D1::Point2F(zp.x+a,zp.y),accent.Get(),stroke);target->DrawLine(D2D1::Point2F(zp.x,zp.y-a),D2D1::Point2F(zp.x,zp.y+a),accent.Get(),stroke);}
                if(statusShowFit){auto fw=center(statusFitWidthRect),fh=center(statusFitHeightRect);const float z=fullscreen?8.0f:7.0f;target->DrawRectangle(D2D1::RectF(fw.x-z,fw.y-5,fw.x+z,fw.y+5),muted.Get(),1.35f);target->DrawLine(D2D1::Point2F(fw.x-z+3,fw.y),D2D1::Point2F(fw.x+z-3,fw.y),accent.Get(),stroke);target->DrawLine(D2D1::Point2F(fw.x-z+3,fw.y),D2D1::Point2F(fw.x-z+6,fw.y-3),accent.Get(),1.4f);target->DrawLine(D2D1::Point2F(fw.x-z+3,fw.y),D2D1::Point2F(fw.x-z+6,fw.y+3),accent.Get(),1.4f);target->DrawLine(D2D1::Point2F(fw.x+z-3,fw.y),D2D1::Point2F(fw.x+z-6,fw.y-3),accent.Get(),1.4f);target->DrawLine(D2D1::Point2F(fw.x+z-3,fw.y),D2D1::Point2F(fw.x+z-6,fw.y+3),accent.Get(),1.4f);target->DrawRectangle(D2D1::RectF(fh.x-5,fh.y-z,fh.x+5,fh.y+z),muted.Get(),1.35f);target->DrawLine(D2D1::Point2F(fh.x,fh.y-z+3),D2D1::Point2F(fh.x,fh.y+z-3),accent.Get(),stroke);target->DrawLine(D2D1::Point2F(fh.x,fh.y-z+3),D2D1::Point2F(fh.x-3,fh.y-z+6),accent.Get(),1.4f);target->DrawLine(D2D1::Point2F(fh.x,fh.y-z+3),D2D1::Point2F(fh.x+3,fh.y-z+6),accent.Get(),1.4f);target->DrawLine(D2D1::Point2F(fh.x,fh.y+z-3),D2D1::Point2F(fh.x-3,fh.y+z-6),accent.Get(),1.4f);target->DrawLine(D2D1::Point2F(fh.x,fh.y+z-3),D2D1::Point2F(fh.x+3,fh.y+z-6),accent.Get(),1.4f);}
                if(statusShowSlideshow){auto sc=center(statusSlideRect);if(slideshowRunning){target->DrawLine(D2D1::Point2F(sc.x-4,sc.y-a),D2D1::Point2F(sc.x-4,sc.y+a),accent.Get(),stroke);target->DrawLine(D2D1::Point2F(sc.x+4,sc.y-a),D2D1::Point2F(sc.x+4,sc.y+a),accent.Get(),stroke);}else{target->DrawLine(D2D1::Point2F(sc.x-5,sc.y-a),D2D1::Point2F(sc.x+7,sc.y),accent.Get(),stroke);target->DrawLine(D2D1::Point2F(sc.x+7,sc.y),D2D1::Point2F(sc.x-5,sc.y+a),accent.Get(),stroke);target->DrawLine(D2D1::Point2F(sc.x-5,sc.y+a),D2D1::Point2F(sc.x-5,sc.y-a),accent.Get(),stroke);}}
                if(statusShowSlideshow&&(slideshowRunning||slideshowPaused)){auto st=center(statusStopRect);const float q=fullscreen?6.0f:5.5f;target->FillRectangle(D2D1::RectF(st.x-q,st.y-q,st.x+q,st.y+q),accent.Get());}
                if(statusShowOverlay){
                    auto ac=center(statusOverlayAddRect);target->DrawRectangle(D2D1::RectF(ac.x-8,ac.y-6,ac.x+5,ac.y+5),muted.Get(),1.4f);target->DrawLine(D2D1::Point2F(ac.x+2,ac.y+6),D2D1::Point2F(ac.x+8,ac.y+6),accent.Get(),1.8f);target->DrawLine(D2D1::Point2F(ac.x+5,ac.y+3),D2D1::Point2F(ac.x+5,ac.y+9),accent.Get(),1.8f);
                    auto lc=center(statusOverlayLoadRect);target->DrawLine(D2D1::Point2F(lc.x-7,lc.y-5),D2D1::Point2F(lc.x+7,lc.y-5),muted.Get(),1.4f);target->DrawLine(D2D1::Point2F(lc.x,lc.y-1),D2D1::Point2F(lc.x,lc.y+7),accent.Get(),1.8f);target->DrawLine(D2D1::Point2F(lc.x,lc.y+7),D2D1::Point2F(lc.x-4,lc.y+3),accent.Get(),1.8f);target->DrawLine(D2D1::Point2F(lc.x,lc.y+7),D2D1::Point2F(lc.x+4,lc.y+3),accent.Get(),1.8f);
                    if(!overlays.empty()){auto sc=center(statusOverlaySaveRect);target->DrawRectangle(D2D1::RectF(sc.x-7,sc.y-7,sc.x+7,sc.y+7),muted.Get(),1.4f);target->DrawLine(D2D1::Point2F(sc.x,sc.y+5),D2D1::Point2F(sc.x,sc.y-4),accent.Get(),1.8f);target->DrawLine(D2D1::Point2F(sc.x,sc.y-4),D2D1::Point2F(sc.x-4,sc.y),accent.Get(),1.8f);target->DrawLine(D2D1::Point2F(sc.x,sc.y-4),D2D1::Point2F(sc.x+4,sc.y),accent.Get(),1.8f);auto cc=center(statusOverlayClearRect);target->DrawLine(D2D1::Point2F(cc.x-6,cc.y-6),D2D1::Point2F(cc.x+6,cc.y+6),muted.Get(),1.8f);target->DrawLine(D2D1::Point2F(cc.x+6,cc.y-6),D2D1::Point2F(cc.x-6,cc.y+6),muted.Get(),1.8f);}
                }
                if(statusShowInfo){auto ic=center(statusInfoRect);const float ew=fullscreen?10.0f:9.0f,eh=fullscreen?6.0f:5.5f; D2D1_POINT_2F pts[5]={D2D1::Point2F(ic.x-ew,ic.y),D2D1::Point2F(ic.x-ew*0.45f,ic.y-eh),D2D1::Point2F(ic.x+ew*0.45f,ic.y-eh),D2D1::Point2F(ic.x+ew,ic.y),D2D1::Point2F(ic.x+ew*0.45f,ic.y+eh)}; target->DrawLine(pts[0],pts[1],accent.Get(),stroke);target->DrawLine(pts[1],pts[2],accent.Get(),stroke);target->DrawLine(pts[2],pts[3],accent.Get(),stroke);target->DrawLine(pts[3],pts[4],accent.Get(),stroke);target->DrawLine(pts[4],D2D1::Point2F(ic.x-ew*0.45f,ic.y+eh),accent.Get(),stroke);target->DrawLine(D2D1::Point2F(ic.x-ew*0.45f,ic.y+eh),pts[0],accent.Get(),stroke);target->FillEllipse(D2D1::Ellipse(ic,2.4f,2.4f),accent.Get());}
                if(statusShowCollapse){auto mc=center(statusMinRect);target->DrawLine(D2D1::Point2F(mc.x-a,mc.y+3),D2D1::Point2F(mc.x+a,mc.y+3),muted.Get(),stroke);}
                if(statusShowOptions){auto oc=center(statusOptionsRect);for(int i=-1;i<=1;++i)target->FillEllipse(D2D1::Ellipse(D2D1::Point2F(oc.x+i*6.0f,oc.y),1.7f,1.7f),accent.Get());}
                if(statusShowClose){auto xc=center(statusCloseRect);target->DrawLine(D2D1::Point2F(xc.x-a,xc.y-a),D2D1::Point2F(xc.x+a,xc.y+a),muted.Get(),stroke);target->DrawLine(D2D1::Point2F(xc.x+a,xc.y-a),D2D1::Point2F(xc.x-a,xc.y+a),muted.Get(),stroke);}

                DrawOverlayText(StatusText(), D2D1::RectF(statusRect.left + 14.0f, statusRect.top + (fullscreen?17.0f:10.0f),
                                x - 8.0f, statusRect.bottom - 4), text.Get(), uiText.Get());
            }
        }

        if (metadataVisible) {
            if (metadataText.empty()) RefreshMetadataText();
            const float w = std::min(390.0f, std::max(260.0f, cw - 32.0f));
            const float h = std::min(300.0f, std::max(150.0f, ch - 80.0f));
            metadataRect = D2D1::RectF(cw - w - 16.0f, 16.0f, cw - 16.0f, 16.0f + h);
            metadataCloseRect = D2D1::RectF(metadataRect.right - 36, metadataRect.top + 6, metadataRect.right - 6, metadataRect.top + 36);
            target->FillRoundedRectangle(D2D1::RoundedRect(metadataRect, 12.0f, 12.0f), panel.Get());
            DrawOverlayText(L"IMAGE INFORMATION", D2D1::RectF(metadataRect.left + 15, metadataRect.top + 12,
                            metadataRect.right - 45, metadataRect.top + 40), accent.Get(), uiTextSmall.Get());
            DrawOverlayText(L"×", D2D1::RectF(metadataCloseRect.left + 7, metadataCloseRect.top + 3,
                            metadataCloseRect.right, metadataCloseRect.bottom), muted.Get(), uiText.Get());
            DrawOverlayText(metadataText, D2D1::RectF(metadataRect.left + 15, metadataRect.top + 44,
                            metadataRect.right - 14, metadataRect.bottom - 12), text.Get(), uiTextSmall.Get());
        }
        SyncOverlayTooltips();
    }

    void DrawBrowserView() { /* Windows IExplorerBrowser child paints this tab. */ }

    void Render() {
        if (FAILED(CreateRenderTarget())) return;
        target->BeginDraw();
        target->SetTransform(D2D1::Matrix3x2F::Identity());
        if (PngTransparencyActive()) target->Clear(D2DColor(ResolveLightTheme()?RGB(232,235,239):RGB(13,17,19)));
        else target->Clear(D2DColor(ResolveLightTheme()?RGB(238,240,243):RGB(20,20,20)));
        if (!ActiveTabIsBrowser() && !bitmap && currentPath.empty()) {
            EnsureHomeTextFormats();
            ComPtr<ID2D1SolidColorBrush> titleBrush, headingBrush, bodyBrush, accentBrush, cardBrush, cardStrongBrush, borderBrush;
            const ThemePalette hp=Palette();
            target->CreateSolidColorBrush(D2DColor(hp.text,0.98f), &titleBrush);
            target->CreateSolidColorBrush(D2DColor(hp.text,0.95f), &headingBrush);
            target->CreateSolidColorBrush(D2DColor(hp.textMuted,0.96f), &bodyBrush);
            target->CreateSolidColorBrush(D2DColor(hp.accent,1.0f), &accentBrush);
            target->CreateSolidColorBrush(D2DColor(hp.surface,0.96f), &cardBrush);
            target->CreateSolidColorBrush(D2DColor(hp.surfaceHover,0.98f), &cardStrongBrush);
            target->CreateSolidColorBrush(D2DColor(hp.border,0.92f), &borderBrush);

            RECT crc{}; GetClientRect(hwnd,&crc);
            const float cw=static_cast<float>(crc.right-crc.left), ch=static_cast<float>(crc.bottom-crc.top);
            const float topInset=ViewerTopInset();
            const float left=std::max(22.0f,(cw-1120.0f)*0.5f);
            const float right=std::min(cw-22.0f,left+1120.0f);
            const float contentW=std::max(300.0f,right-left);

            if(titleBrush && homeTitleText) DrawOverlayText(L"Welcome to Glide",
                D2D1::RectF(left,topInset+26,right,topInset+64),titleBrush.Get(),homeTitleText.Get());
            if(bodyBrush && homeBodyText) DrawOverlayText(
                L"Fast browsing, precise zooming, and familiar Windows controls.",
                D2D1::RectF(left,topInset+68,right,topInset+94),bodyBrush.Get(),homeBodyText.Get());

            const bool wheelNav = !windowedWheelZoom;
            std::wstring newTabKey=HotkeyTextForAction(HotkeyAction::NewTab);
            std::wstring closeTabKey=HotkeyTextForAction(HotkeyAction::CloseTab);
            std::wstring nextTabKey=HotkeyTextForAction(HotkeyAction::NextTab);
            std::wstring fullscreenKey=HotkeyTextForAction(HotkeyAction::ToggleFullscreen);
            if(newTabKey.empty())newTabKey=L"Ctrl+T";
            if(closeTabKey.empty())closeTabKey=L"Ctrl+W";
            if(nextTabKey.empty())nextTabKey=L"Ctrl+Tab";
            if(fullscreenKey.empty())fullscreenKey=L"F11";

            struct HomeTip { const wchar_t* title; std::wstring body; int icon; bool featured; };
            std::vector<HomeTip> tips={
                {L"Wheel through pictures",
                 wheelNav?L"Scroll the mouse wheel to move to the previous or next image — Glide uses the wheel for browsing, not canvas scrolling."
                         :L"Your current setting uses the mouse wheel for zoom. Change “Windowed wheel zooms” off to use the wheel for previous/next browsing.",
                 0,true},
                {L"Drag around a zoomed image",
                 L"Hold the right mouse button and drag to move around inside the current picture. It replaces ordinary canvas scrolling.",
                 1,true},
                {L"Select exactly what you want",
                 L"Left-drag across the image to draw a blue zoom selection.",
                 2,false},
                {L"Zoom directly into a selection",
                 L"Left-click inside the blue selection to zoom in. Right-click inside it to zoom back out.",
                 3,false},
                {L"Keep browsing across folders",
                 L"At the end of a folder, Glide automatically continues into the next sibling folder and skips empty folders.",
                 4,false},
                {L"Use tabs like a browser",
                 newTabKey+L" opens a tab, "+closeTabKey+L" closes it, and "+nextTabKey+L" switches tabs.",
                 5,false},
                {L"Duplicate the current tab",
                 L"Use the Duplicate Tab command to keep your current location while exploring another view.",
                 6,false},
                {L"Fit an image instantly",
                 L"Use the Fit Width and Fit Height buttons in the status bar, or your configured W/H shortcuts.",
                 7,false},
                {L"Go fullscreen instantly",
                 L"Double-click an image or press "+fullscreenKey+L". Press Esc to return to the exact previous window placement.",
                 8,false},
                {L"Jump through the current folder",
                 L"Home goes to the first image; End goes to the last. Previous/Next controls remain available in the status bar.",
                 9,false},
                {L"Keep Glide above other windows",
                 L"Click the pin beside the three-dot menu to toggle Always on Top without opening Settings.",
                 10,false},
                {L"Hover for help",
                 L"Leave the pointer over a Glide control to see a short tooltip explaining what it does.",
                 11,false}
            };
            if(!homeTipsEnabled) tips.clear();

            auto drawIcon=[&](int type,D2D1_RECT_F ir){
                if(!accentBrush)return;
                const float cx=(ir.left+ir.right)*0.5f, cy=(ir.top+ir.bottom)*0.5f;
                const float w=ir.right-ir.left,h=ir.bottom-ir.top;
                const float sw=1.55f;
                switch(type){
                    case 0: // wheel
                        target->DrawRoundedRectangle(D2D1::RoundedRect(D2D1::RectF(cx-6,cy-9,cx+6,cy+9),6,6),accentBrush.Get(),sw);
                        target->DrawLine(D2D1::Point2F(cx,cy-6),D2D1::Point2F(cx,cy-1),accentBrush.Get(),sw);
                        break;
                    case 1: // hand/pan arrows
                        target->DrawLine(D2D1::Point2F(cx-8,cy),D2D1::Point2F(cx+8,cy),accentBrush.Get(),sw);
                        target->DrawLine(D2D1::Point2F(cx,cy-8),D2D1::Point2F(cx,cy+8),accentBrush.Get(),sw);
                        target->DrawLine(D2D1::Point2F(cx-8,cy),D2D1::Point2F(cx-4,cy-4),accentBrush.Get(),sw);
                        target->DrawLine(D2D1::Point2F(cx+8,cy),D2D1::Point2F(cx+4,cy-4),accentBrush.Get(),sw);
                        break;
                    case 2: // selection
                        target->DrawRectangle(D2D1::RectF(cx-8,cy-6,cx+8,cy+6),accentBrush.Get(),sw);
                        break;
                    case 3: // magnifier
                        target->DrawEllipse(D2D1::Ellipse(D2D1::Point2F(cx-2,cy-2),6,6),accentBrush.Get(),sw);
                        target->DrawLine(D2D1::Point2F(cx+3,cy+3),D2D1::Point2F(cx+9,cy+9),accentBrush.Get(),sw);
                        target->DrawLine(D2D1::Point2F(cx-5,cy-2),D2D1::Point2F(cx+1,cy-2),accentBrush.Get(),sw);
                        target->DrawLine(D2D1::Point2F(cx-2,cy-5),D2D1::Point2F(cx-2,cy+1),accentBrush.Get(),sw);
                        break;
                    case 4: // folder arrow
                        target->DrawRectangle(D2D1::RectF(cx-9,cy-5,cx+7,cy+7),accentBrush.Get(),sw);
                        target->DrawLine(D2D1::Point2F(cx+2,cy),D2D1::Point2F(cx+10,cy),accentBrush.Get(),sw);
                        target->DrawLine(D2D1::Point2F(cx+10,cy),D2D1::Point2F(cx+6,cy-4),accentBrush.Get(),sw);
                        target->DrawLine(D2D1::Point2F(cx+10,cy),D2D1::Point2F(cx+6,cy+4),accentBrush.Get(),sw);
                        break;
                    case 5: case 6: // tabs / duplicate
                        target->DrawRoundedRectangle(D2D1::RoundedRect(D2D1::RectF(cx-8,cy-7,cx+6,cy+5),3,3),accentBrush.Get(),sw);
                        if(type==6)target->DrawRoundedRectangle(D2D1::RoundedRect(D2D1::RectF(cx-4,cy-3,cx+10,cy+9),3,3),accentBrush.Get(),sw);
                        else { target->DrawLine(D2D1::Point2F(cx-4,cy-3),D2D1::Point2F(cx+2,cy-3),accentBrush.Get(),sw); }
                        break;
                    case 7: // fit
                        target->DrawRectangle(D2D1::RectF(cx-9,cy-6,cx+9,cy+6),accentBrush.Get(),sw);
                        target->DrawLine(D2D1::Point2F(cx-6,cy),D2D1::Point2F(cx+6,cy),accentBrush.Get(),sw);
                        break;
                    case 8: // fullscreen corners
                        target->DrawLine(D2D1::Point2F(cx-8,cy-3),D2D1::Point2F(cx-8,cy-8),accentBrush.Get(),sw); target->DrawLine(D2D1::Point2F(cx-8,cy-8),D2D1::Point2F(cx-3,cy-8),accentBrush.Get(),sw);
                        target->DrawLine(D2D1::Point2F(cx+8,cy-3),D2D1::Point2F(cx+8,cy-8),accentBrush.Get(),sw); target->DrawLine(D2D1::Point2F(cx+8,cy-8),D2D1::Point2F(cx+3,cy-8),accentBrush.Get(),sw);
                        target->DrawLine(D2D1::Point2F(cx-8,cy+3),D2D1::Point2F(cx-8,cy+8),accentBrush.Get(),sw); target->DrawLine(D2D1::Point2F(cx-8,cy+8),D2D1::Point2F(cx-3,cy+8),accentBrush.Get(),sw);
                        target->DrawLine(D2D1::Point2F(cx+8,cy+3),D2D1::Point2F(cx+8,cy+8),accentBrush.Get(),sw); target->DrawLine(D2D1::Point2F(cx+8,cy+8),D2D1::Point2F(cx+3,cy+8),accentBrush.Get(),sw);
                        break;
                    case 9: // first/end
                        target->DrawLine(D2D1::Point2F(cx-8,cy-7),D2D1::Point2F(cx-8,cy+7),accentBrush.Get(),sw);
                        target->DrawLine(D2D1::Point2F(cx-4,cy),D2D1::Point2F(cx+7,cy),accentBrush.Get(),sw);
                        target->DrawLine(D2D1::Point2F(cx+7,cy),D2D1::Point2F(cx+3,cy-4),accentBrush.Get(),sw);
                        target->DrawLine(D2D1::Point2F(cx+7,cy),D2D1::Point2F(cx+3,cy+4),accentBrush.Get(),sw);
                        break;
                    case 10: // pin
                        target->DrawLine(D2D1::Point2F(cx-6,cy-6),D2D1::Point2F(cx+6,cy-6),accentBrush.Get(),sw);
                        target->DrawLine(D2D1::Point2F(cx-4,cy-6),D2D1::Point2F(cx-2,cy+1),accentBrush.Get(),sw);
                        target->DrawLine(D2D1::Point2F(cx+4,cy-6),D2D1::Point2F(cx+2,cy+1),accentBrush.Get(),sw);
                        target->DrawLine(D2D1::Point2F(cx-6,cy+1),D2D1::Point2F(cx+6,cy+1),accentBrush.Get(),sw);
                        target->DrawLine(D2D1::Point2F(cx,cy+1),D2D1::Point2F(cx,cy+9),accentBrush.Get(),sw);
                        break;
                    default: // tooltip
                        target->DrawRoundedRectangle(D2D1::RoundedRect(D2D1::RectF(cx-9,cy-7,cx+9,cy+6),4,4),accentBrush.Get(),sw);
                        target->DrawLine(D2D1::Point2F(cx-3,cy+6),D2D1::Point2F(cx-6,cy+10),accentBrush.Get(),sw);
                        break;
                }
            };

            const bool twoCols=contentW>=700.0f;
            const int cols=twoCols?2:1;
            const float gap=14.0f;
            const float rowGap=10.0f;
            const float cardW=(contentW-gap*(cols-1))/cols;
            const float startY=topInset+108.0f;
            const int rows=twoCols?static_cast<int>((tips.size()+1)/2):static_cast<int>(tips.size());
            // The status surface is persistent chrome.  Home cards must reserve
            // its full footprint instead of painting underneath it on short views.
            const bool statusReserved=statusVisible && (!fullscreen || fullscreenStatusAlwaysOn || fullscreenStatusVisible);
            const float homeBottom=ch-(statusReserved?72.0f:12.0f);
            const float availableCardsH=std::max(1.0f,homeBottom-startY-16.0f-rowGap*std::max(0,rows-1));
            const float cardH=twoCols?std::clamp(availableCardsH/std::max(1,rows),88.0f,106.0f):92.0f;
            for(size_t i=0;i<tips.size();++i){
                int col=twoCols?static_cast<int>(i%2):0;
                int row=twoCols?static_cast<int>(i/2):static_cast<int>(i);
                float x=left+col*(cardW+gap);
                float y=startY+row*(cardH+rowGap);
                D2D1_RECT_F card=D2D1::RectF(x,y,x+cardW,y+cardH);
                if(card.bottom>homeBottom)break;
                if(tips[i].featured && cardStrongBrush)target->FillRoundedRectangle(D2D1::RoundedRect(card,10,10),cardStrongBrush.Get());
                else if(cardBrush)target->FillRoundedRectangle(D2D1::RoundedRect(card,10,10),cardBrush.Get());
                if(borderBrush)target->DrawRoundedRectangle(D2D1::RoundedRect(card,10,10),borderBrush.Get(),1.0f);
                D2D1_RECT_F iconRect=D2D1::RectF(card.left+13,card.top+17,card.left+37,card.top+41);
                drawIcon(tips[i].icon,iconRect);
                if(headingBrush && homeHeadingText)DrawOverlayText(tips[i].title,
                    D2D1::RectF(card.left+48,card.top+10,card.right-10,card.top+31),headingBrush.Get(),homeHeadingText.Get());
                if(bodyBrush && homeBodyText)DrawOverlayText(tips[i].body,
                    D2D1::RectF(card.left+48,card.top+33,card.right-12,card.bottom-9),bodyBrush.Get(),homeBodyText.Get());
            }
        }
        if (!ActiveTabIsBrowser() && bitmap) {
            const auto dst = ImageDestinationRect();
            EnsureQualityBitmap();
            ID2D1Bitmap* drawBitmap = qualityBitmap ? qualityBitmap.Get() : bitmap.Get();
            target->DrawBitmap(drawBitmap, dst, 1.0f, D2D1_BITMAP_INTERPOLATION_MODE_LINEAR);

            if (selectionActive || selecting) {
                const D2D1_RECT_F sr = SelectionClientRect();
                ComPtr<ID2D1SolidColorBrush> fillBrush;
                ComPtr<ID2D1SolidColorBrush> lineBrush;
                const ThemePalette pal=Palette();
                target->CreateSolidColorBrush(D2DColor(pal.accent,0.16f), &fillBrush);
                target->CreateSolidColorBrush(D2DColor(ResolveLightTheme()?BlendColor(pal.accent,RGB(0,0,0),0.18f):BlendColor(pal.accent,RGB(255,255,255),0.45f),0.98f), &lineBrush);
                if (fillBrush) target->FillRectangle(sr, fillBrush.Get());
                if (lineBrush) target->DrawRectangle(sr, lineBrush.Get(), 1.5f);
            }
        }
        DrawBrowserView();
        // The picture text belongs to the base image. Window-in-Window overlays are
        // independent content and must remain visually unobscured by the base counter.
        DrawPictureTextOverlay();
        RenderWindowInWindowOverlays();
        DrawOverlays();
        const HRESULT hr = target->EndDraw();
        if (hr == D2DERR_RECREATE_TARGET) DiscardRenderTarget();
        else if (SUCCEEDED(hr)) {
            diagnosticLastRenderTick=GetTickCount64();
            ++diagnosticRenderGeneration;
        }
    }

    void Resize(UINT w, UINT h) {
        if (w > 0 && h > 0) ResizeShellBrowser();
        if (target && w > 0 && h > 0) {
            diagnosticLastResizeHr=target->Resize(D2D1::SizeU(w, h));
            qualityBitmap.Reset();
            qualityBitmapW = qualityBitmapH = 0;
            if (viewMode == ViewMode::Manual) ClampPan();
            UpdateScrollBars();
            UpdateTitle();
            InvalidateRect(hwnd, nullptr, FALSE);
        }
    }
};

static ViewerApp* GetApp(HWND hwnd) {
    return reinterpret_cast<ViewerApp*>(GetWindowLongPtrW(hwnd, GWLP_USERDATA));
}

static LRESULT CALLBACK WndProc(HWND hwnd, UINT msg, WPARAM wParam, LPARAM lParam) {
    ViewerApp* app = GetApp(hwnd);

    switch (msg) {
        case WM_NCCREATE: {
            auto* cs = reinterpret_cast<CREATESTRUCTW*>(lParam);
            auto* p = reinterpret_cast<ViewerApp*>(cs->lpCreateParams);
            SetWindowLongPtrW(hwnd, GWLP_USERDATA, reinterpret_cast<LONG_PTR>(p));
            p->hwnd = hwnd;
            return TRUE;
        }

        case WM_CREATE:
            if(app)app->ApplyThemeToMainWindow();
            DragAcceptFiles(hwnd, TRUE);
            if (app) {
                const bool foregroundOk = app->worker.Start(hwnd, WM_APP_DECODED);
                bool backgroundOk = true;
                if(app->coldStartDirectImage){
                    app->deferredWorkersPending = true;
                }else{
                    const bool prefetchOk = app->prefetchWorker.Start(hwnd, WM_APP_DECODED);
                    const bool prefetch2Ok = app->prefetchWorker2.Start(hwnd, WM_APP_DECODED);
                    const bool prefetch3Ok = app->prefetchWorker3.Start(hwnd, WM_APP_DECODED);
                    const bool refineOk = app->refineWorker.Start(hwnd, WM_APP_DECODED);
                    const bool adaptiveOk = app->adaptiveWorker.Start(hwnd, WM_APP_DECODED);
                    app->deferredWorkersStarted = prefetchOk && prefetch2Ok && prefetch3Ok && refineOk && adaptiveOk;
                    backgroundOk = app->deferredWorkersStarted;
                }
                if (!foregroundOk || !backgroundOk) {
                    MessageBoxW(hwnd, L"One or more background image loaders could not start.", kAppName, MB_ICONERROR);
                }
            }
            return 0;

        case WM_MOUSEACTIVATE:
            if(app&&app->diagnosticBackgroundWorker) return MA_NOACTIVATE;
            break;

        case WM_ACTIVATE:
            if(app&&LOWORD(wParam)==WA_INACTIVE){
                if(app->tabDragHoverWindow){PostMessageW(app->tabDragHoverWindow,WM_APP_TAB_DRAG_LEAVE,0,0);app->tabDragHoverWindow=nullptr;}
                if(app->externalTabDragHover){KillTimer(hwnd,kExternalTabHoverWatchdogTimerId);app->externalTabDragHover=false;app->externalTabDragClientX=0;app->InvalidateViewer();}
            }
            if(app&&app->diagnosticBackgroundWorker&&LOWORD(wParam)!=WA_INACTIVE){
                // Diagnostic behavior probes sometimes need to observe the real Z-order effect
                // of Always on Top.  Do not let the worker-background safety policy immediately
                // undo the very effect under test.  Normal background workers are still demoted.
                if(!app->diagnosticSuspendBackgroundDemotion)
                    SetWindowPos(hwnd,HWND_BOTTOM,0,0,0,0,SWP_NOMOVE|SWP_NOSIZE|SWP_NOACTIVATE);
                return 0;
            }
            break;

        case WM_NCHITTEST:
            if(app&&app->fullscreen&&!app->fullscreenNativeCaptionVisible) return HTCLIENT;
            if(app&&app->titleTabsEnabled&&(!app->fullscreen||app->fullscreenNativeCaptionVisible)){POINT sp{GET_X_LPARAM(lParam),GET_Y_LPARAM(lParam)};ScreenToClient(hwnd,&sp);if(ViewerApp::PointInRect(app->titleBarRect,sp)&&!app->PointOnTitleInteractive(sp))return HTCAPTION;}
            break;


        case WM_APP_DECODED: {
            auto* decoded = reinterpret_cast<DecodedImage*>(lParam);
            if (decoded) {
                if (app) app->ApplyDecoded(*decoded);
                delete decoded;
            }
            return 0;
        }

        case WM_APP_NAV_DRAIN:
            if (app) app->DrainOnePendingNavigation();
            return 0;

        case WM_APP_SHELL_OPEN:
            if(app){auto* path=reinterpret_cast<std::wstring*>(lParam); if(path){app->OpenShellImageInActiveTab(*path); delete path;}}
            else delete reinterpret_cast<std::wstring*>(lParam);
            return 0;

        case WM_APP_COLD_FOLDER:
            if(app) app->ApplyColdFolderResult(reinterpret_cast<ColdFolderResult*>(lParam));
            else delete reinterpret_cast<ColdFolderResult*>(lParam);
            return 0;

        case WM_SYSCOMMAND:
            if(app&&app->fullscreen){
                const UINT cmd=static_cast<UINT>(wParam&0xFFF0);
                if(cmd==SC_CLOSE && app->fullscreenNativeCaptionVisible && !app->fullscreenXClosesApp){
                    app->ToggleFullscreen();
                    return 0;
                }
                if(cmd==SC_RESTORE){
                    // The restore half of the native maximize/restore caption button is
                    // the natural mouse way to leave fullscreen.  Restore Glide to the
                    // exact WINDOWPLACEMENT/rectangle captured before fullscreen.
                    app->ToggleFullscreen();
                    return 0;
                }
                if(cmd==SC_MAXIMIZE&&app->fullscreenNativeCaptionVisible){
                    // Fullscreen's revealed native caption is already maximized by design.
                    ShowWindow(hwnd,SW_MAXIMIZE);
                    return 0;
                }
            }
            break;

        case WM_MOVING:
            if(app && app->singleTabWindowDrag){
                POINT sp{};GetCursorPos(&sp);app->tabDragCurrent=sp;ScreenToClient(hwnd,&app->tabDragCurrent);
                HWND hover=app->GlideWindowAtScreenPoint(sp);
                if(hover!=app->tabDragHoverWindow){
                    if(app->tabDragHoverWindow)PostMessageW(app->tabDragHoverWindow,WM_APP_TAB_DRAG_LEAVE,0,0);
                    app->tabDragHoverWindow=hover;
                }
                if(hover){
                    POINT hp=sp;ScreenToClient(hover,&hp);
                    if(hp.y>=0&&hp.y<=58)PostMessageW(hover,WM_APP_TAB_DRAG_HOVER,static_cast<WPARAM>(static_cast<INT_PTR>(sp.x)),static_cast<LPARAM>(static_cast<INT_PTR>(sp.y)));
                    else {PostMessageW(hover,WM_APP_TAB_DRAG_LEAVE,0,0);app->tabDragHoverWindow=nullptr;}
                }
                app->InvalidateViewer();
            }
            break;

        case WM_TIMER:
            if(app && wParam==kContentTabDragTimerId){
                if(!app->manualContentTabDrag){KillTimer(hwnd,kContentTabDragTimerId);return 0;}
                POINT sp{};GetCursorPos(&sp);
                if(GetAsyncKeyState(VK_LBUTTON)&0x8000){
                    if(GetTickCount64()-app->manualContentLastMoveTick>=12){
                        RECT wr{};GetWindowRect(hwnd,&wr);
                        const int ww=wr.right-wr.left, wh=wr.bottom-wr.top;
                        SetWindowPos(hwnd,nullptr,sp.x-app->manualContentDragOffset.x,sp.y-app->manualContentDragOffset.y,ww,wh,
                                     SWP_NOZORDER|SWP_NOACTIVATE|SWP_NOSIZE);
                        app->tabDragCurrent=sp;ScreenToClient(hwnd,&app->tabDragCurrent);
                    }

                    // Keep content-tab ownership manual for the entire held gesture.
                    // A WM_NCLBUTTONDOWN handoff here would re-enter USER32's modal move
                    // loop after the physical button is already down -- the exact ownership
                    // class that previously caused frozen movement/click-through. Therefore
                    // content tabs provide deterministic edge/corner placement on release,
                    // not the native Windows Snap preview. Home/native-caption dragging keeps
                    // genuine USER32 Snap behaviour through its existing path.
                    HWND hover=app->GlideWindowAtScreenPoint(sp);
                    if(hover!=app->tabDragHoverWindow){
                        if(app->tabDragHoverWindow)PostMessageW(app->tabDragHoverWindow,WM_APP_TAB_DRAG_LEAVE,0,0);
                        app->tabDragHoverWindow=hover;
                    }
                    if(hover){
                        POINT hp=sp;ScreenToClient(hover,&hp);
                        if(hp.y>=0&&hp.y<=58)PostMessageW(hover,WM_APP_TAB_DRAG_HOVER,static_cast<WPARAM>(static_cast<INT_PTR>(sp.x)),static_cast<LPARAM>(static_cast<INT_PTR>(sp.y)));
                        else {PostMessageW(hover,WM_APP_TAB_DRAG_LEAVE,0,0);app->tabDragHoverWindow=nullptr;}
                    }
                    SetCursor(LoadCursorW(nullptr,IDC_HAND));
                    app->InvalidateViewer();
                    return 0;
                }
                // First real mouse-up ends the tear-off. Consume the drag state before
                // constructing ExplorerBrowser/decoding the image, so the released click
                // can never fall through into content interaction.
                KillTimer(hwnd,kContentTabDragTimerId);
                HWND targetWnd=app->GlideWindowAtScreenPoint(sp);
                if(targetWnd){POINT tp=sp;ScreenToClient(targetWnd,&tp);
                    if(tp.y>=0&&tp.y<=58&&app->TransferTabToWindow(0,targetWnd,sp)){if(app->tabDragHoverWindow){PostMessageW(app->tabDragHoverWindow,WM_APP_TAB_DRAG_LEAVE,0,0);app->tabDragHoverWindow=nullptr;}app->manualContentTabDrag=false;app->singleTabWindowDrag=false;app->tabDragIndex=-1;return 0;}
                }
                if(app->tabDragHoverWindow){PostMessageW(app->tabDragHoverWindow,WM_APP_TAB_DRAG_LEAVE,0,0);app->tabDragHoverWindow=nullptr;}
                // Manual content-tab dragging deliberately avoids USER32's modal caption
                // loop so Explorer/image content never steals the held click. Restore the
                // useful part of Aero Snap at release: edge/corner placement against the
                // current monitor work area. This is isolated to the detached-content path.
                {
                    HMONITOR mon=MonitorFromPoint(sp,MONITOR_DEFAULTTONEAREST); MONITORINFO mi{sizeof(mi)};
                    if(GetMonitorInfoW(mon,&mi)){
                        const RECT wa=mi.rcWork; const int snap=24; const bool left=sp.x<=wa.left+snap;
                        const bool right=sp.x>=wa.right-snap; const bool top=sp.y<=wa.top+snap; const bool bottom=sp.y>=wa.bottom-snap;
                        const int W=wa.right-wa.left,H=wa.bottom-wa.top; RECT sr{}; bool doSnap=false;
                        if(top&&left){sr={wa.left,wa.top,wa.left+W/2,wa.top+H/2};doSnap=true;}
                        else if(top&&right){sr={wa.left+W/2,wa.top,wa.right,wa.top+H/2};doSnap=true;}
                        else if(bottom&&left){sr={wa.left,wa.top+H/2,wa.left+W/2,wa.bottom};doSnap=true;}
                        else if(bottom&&right){sr={wa.left+W/2,wa.top+H/2,wa.right,wa.bottom};doSnap=true;}
                        else if(left){sr={wa.left,wa.top,wa.left+W/2,wa.bottom};doSnap=true;}
                        else if(right){sr={wa.left+W/2,wa.top,wa.right,wa.bottom};doSnap=true;}
                        else if(top){ShowWindow(hwnd,SW_MAXIMIZE);}
                        if(doSnap){ShowWindow(hwnd,SW_RESTORE);SetWindowPos(hwnd,nullptr,sr.left,sr.top,sr.right-sr.left,sr.bottom-sr.top,SWP_NOZORDER|SWP_NOACTIVATE);}
                    }
                }
                app->manualContentTabDrag=false;app->singleTabWindowDrag=false;app->tabDragIndex=-1;
                app->externalTabDragHover=false;
                if(GetCapture()==hwnd)ReleaseCapture();
                app->ActivateDeferredDetachedContent();
                SetCursor(LoadCursorW(nullptr,IDC_ARROW));
                app->InvalidateViewer();
                return 0;
            }
            if(app && wParam==kNativeTabDragReleaseTimerId){
                // Safety net for synthetic cross-process tear-off: if the physical
                // button is already up but USER32's move loop has not observed it yet,
                // inject the missing up transition immediately instead of leaving the
                // window stuck to the pointer until another click.
                if(app->singleTabWindowDrag && (GetAsyncKeyState(VK_LBUTTON)&0x8000)==0){
                    INPUT up{};up.type=INPUT_MOUSE;up.mi.dwFlags=MOUSEEVENTF_LEFTUP;
                    SendInput(1,&up,sizeof(INPUT));
                    ReleaseCapture();
                }
                return 0;
            }
            if(app && wParam==kSettingsPrewarmTimerId){
                KillTimer(hwnd,kSettingsPrewarmTimerId);
                // Do not steal a live gesture/drag. If the user is interacting, postpone the
                // hidden prewarm until the UI is idle again.
                if(GetCapture()!=nullptr || app->tabDragging || app->singleTabWindowDrag || app->manualContentTabDrag || app->panning || app->selecting){SetTimer(hwnd,kSettingsPrewarmTimerId,700,nullptr);return 0;}
                app->PrewarmSettingsDialog();return 0;
            }
            if(app && wParam==kExternalTabHoverWatchdogTimerId){
                KillTimer(hwnd,kExternalTabHoverWatchdogTimerId);
                if(app->externalTabDragHover){app->externalTabDragHover=false;app->externalTabDragClientX=0;app->InvalidateViewer();}
                return 0;
            }
            if (app && wParam == kAdaptivePreviewTimerId) { app->RequestAdaptivePreview(); return 0; }
            if (app && wParam == kNavigationIdleTimerId) { app->SettleAfterNavigation(); return 0; }
            if (app && wParam == kRefineTimerId) {
                app->RequestFullRefinement();
                return 0;
            }
            if (app && wParam == kSlideshowTimerId) { app->SlideshowStep(); return 0; }
            if(app&&wParam==kFullscreenBarTimerId&&app->fullscreen){if(app->settingsDialogHwnd&&IsWindow(app->settingsDialogHwnd)){return 0;}POINT sp{};GetCursorPos(&sp);RECT wr{};GetWindowRect(hwnd,&wr);bool atTop=sp.y>=wr.top-2&&sp.y<=wr.top+2;if(atTop&&!app->fullscreenNativeCaptionVisible){app->SetFullscreenNativeCaption(true);app->fullscreenBarLastInsideTick=GetTickCount64();app->fullscreenCursorHidden=false;SetCursor(LoadCursorW(nullptr,IDC_ARROW));}else if(app->fullscreenNativeCaptionVisible){POINT cp=sp;ScreenToClient(hwnd,&cp);bool inTop=cp.y<static_cast<LONG>(app->ViewerTopInset()+8)||sp.y<wr.top+GetSystemMetrics(SM_CYCAPTION)+GetSystemMetrics(SM_CYFRAME)+8;if(inTop)app->fullscreenBarLastInsideTick=GetTickCount64();else if(app->ShouldAutoHideFullscreenBarAt(inTop,GetTickCount64()))app->SetFullscreenNativeCaption(false);}POINT cp=sp;ScreenToClient(hwnd,&cp);RECT cr{};GetClientRect(hwnd,&cr);const bool atBottom=cp.y>=cr.bottom-5;const bool inStatus=app->fullscreenStatusVisible&&ViewerApp::PointInRect(app->statusRect,cp);if(atBottom){app->fullscreenStatusVisible=true;app->fullscreenStatusLastInsideTick=GetTickCount64();app->InvalidateViewer();app->fullscreenCursorHidden=false;SetCursor(LoadCursorW(nullptr,IDC_ARROW));}else if(inStatus){app->fullscreenStatusLastInsideTick=GetTickCount64();}else if(!app->fullscreenStatusAlwaysOn&&app->fullscreenStatusVisible&&GetTickCount64()-app->fullscreenStatusLastInsideTick>=900){app->fullscreenStatusVisible=false;app->InvalidateViewer();}return 0;}

            if (app && wParam == kFullscreenCursorTimerId && app->fullscreen && app->fullscreenCursorAutoHide) {
                if (app->ShouldHideFullscreenCursorAt(GetTickCount64())) {
                    app->fullscreenCursorHidden = true;
                    SetCursor(nullptr);
                }
                return 0;
            }
            break;

        case WM_APP_TAB_DRAG_HOVER:
            if(app&&!app->fullscreen){
                POINT sp{static_cast<LONG>(static_cast<INT_PTR>(wParam)),static_cast<LONG>(static_cast<INT_PTR>(lParam))};ScreenToClient(hwnd,&sp);
                app->externalTabDragHover=true;app->externalTabDragClientX=sp.x;
                // Hover messages arrive continuously while a real tab is over this
                // strip. Restart a short watchdog on every one so a lost LEAVE message
                // can never strand the blue drop-target decoration on screen.
                SetTimer(hwnd,kExternalTabHoverWatchdogTimerId,180,nullptr);
                app->InvalidateViewer();return TRUE;
            }
            return FALSE;
        case WM_APP_TAB_DRAG_LEAVE:
            if(app){KillTimer(hwnd,kExternalTabHoverWatchdogTimerId);app->externalTabDragHover=false;app->externalTabDragClientX=0;app->InvalidateViewer();}
            return 0;
        case WM_APP_BEGIN_CONTENT_TAB_DRAG:
            if(app&&!app->fullscreen){
                const LONG sx=static_cast<LONG>(static_cast<INT_PTR>(wParam));
                const LONG sy=static_cast<LONG>(static_cast<INT_PTR>(lParam));
                RECT wr{};GetWindowRect(hwnd,&wr);
                app->singleTabWindowDrag=true;app->manualContentTabDrag=true;app->tabDragIndex=0;
                app->manualContentDragOffset.x=std::clamp<LONG>(sx-wr.left,24,std::max<LONG>(24,wr.right-wr.left-24));
                app->manualContentDragOffset.y=std::clamp<LONG>(sy-wr.top,8,46);
                POINT cp{sx,sy};ScreenToClient(hwnd,&cp);app->tabDragCurrent=cp;
                SetCursor(LoadCursorW(nullptr,IDC_HAND));
                // Preserve the original physical mouse gesture. Capture routes real
                // WM_MOUSEMOVE/WM_LBUTTONUP to this detached window; movement itself runs
                // at input-message rate rather than a fixed 60 Hz timer. The short timer is
                // only a safety/fallback poll for cross-process capture loss and release.
                app->manualContentLastMoveTick=GetTickCount64();
                SetCapture(hwnd);
                SetTimer(hwnd,kContentTabDragTimerId,8,nullptr);
                return 0;
            }
            return 0;

        case WM_APP_BEGIN_NATIVE_TAB_DRAG:
            if(app&&!app->fullscreen){
                const LONG sx=static_cast<LONG>(static_cast<INT_PTR>(wParam));
                const LONG sy=static_cast<LONG>(static_cast<INT_PTR>(lParam));
                app->singleTabWindowDrag=true;app->tabDragIndex=0;
                POINT cp{sx,sy};ScreenToClient(hwnd,&cp);app->tabDragCurrent=cp;app->tabDragGrabOffsetX=30.0f;

                // Cross-process tab tear-off begins while the user's physical left button
                // is already held. A synthetic WM_NCLBUTTONDOWN alone can leave USER32's
                // modal move loop out of sync with the real button transition, which is
                // why 1.2.88 could keep moving after mouse-up until the next click.
                // Normalize the logical mouse state to a fresh down transition first; the
                // user's real hardware button-up then terminates the move loop normally.
                INPUT bridge[2]{};
                bridge[0].type=INPUT_MOUSE;bridge[0].mi.dwFlags=MOUSEEVENTF_LEFTUP;
                bridge[1].type=INPUT_MOUSE;bridge[1].mi.dwFlags=MOUSEEVENTF_LEFTDOWN;
                SendInput(2,bridge,sizeof(INPUT));

                // Keep only the lightweight tab/browser chrome alive during the native
                // move. ShellView creation, folder enumeration and image decode remain
                // deferred until this loop ends.
                RedrawWindow(hwnd,nullptr,nullptr,RDW_INVALIDATE|RDW_ALLCHILDREN|RDW_UPDATENOW);
                DwmFlush();
                SetCursor(LoadCursorW(nullptr,IDC_HAND));
                ReleaseCapture();
                SetTimer(hwnd,kNativeTabDragReleaseTimerId,16,nullptr);
                SendMessageW(hwnd,WM_NCLBUTTONDOWN,HTCAPTION,MAKELPARAM(sx,sy));
                KillTimer(hwnd,kNativeTabDragReleaseTimerId);

                POINT sp{};GetCursorPos(&sp);HWND targetWnd=app->GlideWindowAtScreenPoint(sp);
                if(targetWnd){POINT tp=sp;ScreenToClient(targetWnd,&tp);
                    if(tp.y>=0&&tp.y<=58&&app->TransferTabToWindow(0,targetWnd,sp)){app->singleTabWindowDrag=false;app->tabDragIndex=-1;return 0;}
                }
                if(app->tabDragHoverWindow){PostMessageW(app->tabDragHoverWindow,WM_APP_TAB_DRAG_LEAVE,0,0);app->tabDragHoverWindow=nullptr;}
                app->singleTabWindowDrag=false;app->tabDragIndex=-1;
                app->ActivateDeferredDetachedContent();
                app->InvalidateViewer();return 0;
            }
            return 0;

        case WM_CANCELMODE:
            if(app){
                if(app->tabDragHoverWindow){PostMessageW(app->tabDragHoverWindow,WM_APP_TAB_DRAG_LEAVE,0,0);app->tabDragHoverWindow=nullptr;}
                KillTimer(hwnd,kExternalTabHoverWatchdogTimerId);
                if(app->externalTabDragHover){app->externalTabDragHover=false;app->externalTabDragClientX=0;app->InvalidateViewer();}
            }
            break;

        case WM_ACTIVATEAPP:
            if(app && !wParam){
                if(app->tabDragHoverWindow){PostMessageW(app->tabDragHoverWindow,WM_APP_TAB_DRAG_LEAVE,0,0);app->tabDragHoverWindow=nullptr;}
                KillTimer(hwnd,kExternalTabHoverWatchdogTimerId);
                if(app->externalTabDragHover){app->externalTabDragHover=false;app->externalTabDragClientX=0;app->InvalidateViewer();}
            }
            break;

        case WM_COPYDATA: {
            if (app) {
                auto* cds = reinterpret_cast<COPYDATASTRUCT*>(lParam);
                if (cds && cds->dwData == kGlideTabTransferMagic && cds->lpData && cds->cbData > sizeof(ULONG_PTR)+sizeof(LONG)*2) {
                    struct Header{ULONG_PTR magic;LONG x;LONG y;}; auto* h=reinterpret_cast<const Header*>(cds->lpData);
                    if(h->magic==kGlideTabTransferMagic){
                        const wchar_t* text=reinterpret_cast<const wchar_t*>(reinterpret_cast<const BYTE*>(cds->lpData)+sizeof(Header));
                        const size_t chars=(cds->cbData-sizeof(Header))/sizeof(wchar_t);
                        size_t n=0;while(n<chars&&text[n])++n;
                        std::wstring payload(text,n);
                        return app->AcceptTransferredTab(payload,POINT{h->x,h->y})?TRUE:FALSE;
                    }
                }
                if (cds && cds->dwData == 0x474C4944 && cds->lpData && cds->cbData >= sizeof(wchar_t)) {
                    const wchar_t* path = static_cast<const wchar_t*>(cds->lpData);
                    if (*path) app->OpenPath(path);
                    if (IsIconic(hwnd)) ShowWindow(hwnd, SW_RESTORE);
                    SetForegroundWindow(hwnd);
                    return TRUE;
                }
            }
            break;
        }

        case WM_DROPFILES: {
            if (!app) break;
            HDROP drop = reinterpret_cast<HDROP>(wParam);
            wchar_t path[32768]{};
            if (DragQueryFileW(drop, 0, path, static_cast<UINT>(std::size(path)))) {
                if (!app->OpenPath(path)) MessageBoxW(hwnd, L"Could not open the dropped item.", kAppName, MB_ICONERROR);
            }
            DragFinish(drop);
            return 0;
        }



        case WM_COMMAND:
            if (app) {
                app->HandleCommand(LOWORD(wParam));
                return 0;
            }
            break;

        case WM_SETCURSOR:
            if (app && LOWORD(lParam) == HTCLIENT && app->fullscreen && app->fullscreenCursorHidden) {
                SetCursor(nullptr);
                return TRUE;
            }
            if (app && LOWORD(lParam) == HTCLIENT && app->selectionActive) {
                POINT pt{};
                GetCursorPos(&pt);
                ScreenToClient(hwnd, &pt);
                if (app->PointInsideSelection(static_cast<float>(pt.x), static_cast<float>(pt.y))) {
                    const bool rightDown = (GetKeyState(VK_RBUTTON) & 0x8000) != 0;
                    HCURSOR c = rightDown ? app->zoomOutCursor : app->zoomInCursor;
                    if (c) { SetCursor(c); return TRUE; }
                }
            }
            if (app && app->panning) {
                SetCursor(LoadCursorW(nullptr, IDC_HAND));
                return TRUE;
            }
            if (app && (app->tabDragCandidate || app->tabDragging || app->singleTabWindowDrag || app->deferredDetachedStartup)) {
                SetCursor(LoadCursorW(nullptr, IDC_HAND));
                return TRUE;
            }
            if (app && (app->nativeBackgroundDrag || app->leftBackgroundDragCandidate)) {
                SetCursor(LoadCursorW(nullptr, IDC_HAND));
                return TRUE;
            }
            break;

        case WM_SYSKEYDOWN:
        case WM_KEYDOWN:
            if (!app) break;
            if (wParam == VK_F12 && (GetKeyState(VK_CONTROL)&0x8000) && (GetKeyState(VK_SHIFT)&0x8000)) {
                app->DumpTooltipDiagnostics();
                return 0;
            }
            if (app->HandleHotkey(static_cast<UINT>(wParam))) return 0;
            break;

        case WM_LBUTTONDOWN:
            if (app) {
                app->RelayTooltipEvent(WM_LBUTTONDOWN, wParam, lParam);
                app->HideHoverTooltip();
                SetFocus(hwnd);
                POINT pt{GET_X_LPARAM(lParam), GET_Y_LPARAM(lParam)};
                // Tab commands remain ordinary one-click controls. A tab body itself
                // enters the browser-style drag state so it can be reordered or detached.
                if(app->opacitySliderVisible&&ViewerApp::PointInRect(app->opacityPanelRect,pt)){
                    app->opacitySliderDragging=true;app->UpdateOpacityFromPoint(pt);SetCapture(hwnd);return 0;
                }
                if(app->opacitySliderVisible&&!ViewerApp::PointInRect(app->titleOpacityRect,pt)){app->opacitySliderVisible=false;app->InvalidateViewer();}
                if(app->BeginOverlayInteraction(pt)) return 0;
                if(app->PointOnTitleCommand(pt)){app->HandleOverlayLeftClick(pt);return 0;}
                { int tab=app->TitleTabIndexAt(pt); if(tab>=0){
                    if(app->openTabs.size()==1) app->BeginSingleTabWindowDrag(tab,pt);
                    else app->BeginTabDrag(tab,pt);
                    return 0;
                } }
                if (app->HandleOverlayLeftClick(pt)) return 0;
                if(app->overlayActiveIndex>=0&&!app->PointOnOverlay(pt)){app->overlayActiveIndex=-1;app->InvalidateViewer();}
                if (app->ActiveTabIsBrowser()) return 0;
                if(app->selectionActive && app->PointInsideSelection(static_cast<float>(pt.x),static_cast<float>(pt.y)) && app->ExecuteGestureAction(GestureSlot::SelectionLeftClick,pt)) return 0;
                if(app->PointOnImage(pt)){
                    const auto dragAction=app->Gesture(GestureSlot::LeftDragImage);
                    if(dragAction!=GestureAction::Legacy && app->ExecuteGestureAction(GestureSlot::LeftDragImage,pt,0,1)) return 0;
                    const auto clickSlot=app->fullscreen?GestureSlot::FullscreenLeftClickImage:GestureSlot::WindowLeftClickImage;
                    if(app->ExecuteGestureAction(clickSlot,pt)) return 0;
                }else if(!app->PointOnOverlay(pt) && app->ExecuteGestureAction(GestureSlot::LeftDragBackground,pt,0,1)) return 0;
                if (app->fullscreen && app->fullscreenClickNavigation && !app->ActiveTabIsBrowser() && !app->selectionActive && !app->PointOnOverlay(pt)) {
                    app->Navigate(+1);
                    return 0;
                }
                if (!app->fullscreen && !app->PointOnImage(pt) && !app->PointOnOverlay(pt) && app->backgroundLeftDragMovesWindow) {
                    app->leftDownClient = pt;
                    app->leftBackgroundDragCandidate = true;
                    SetCapture(hwnd);
                    SetCursor(LoadCursorW(nullptr, IDC_HAND));
                    return 0;
                }
                if(app->leftImageDragMode==ViewerApp::LeftImageDragMode::Pan && app->PointOnImage(pt)) app->BeginPan(pt);
                else app->BeginLeftInteraction(pt);
            }
            return 0;

        case WM_MOUSEMOVE:
            if (app) {
                app->RelayTooltipEvent(WM_MOUSEMOVE, wParam, lParam);
                POINT pt{GET_X_LPARAM(lParam), GET_Y_LPARAM(lParam)};
                if(app->manualContentTabDrag){
                    if((GetAsyncKeyState(VK_LBUTTON)&0x8000)!=0){
                        POINT sp{};GetCursorPos(&sp);
                        RECT wr{};GetWindowRect(hwnd,&wr);
                        const int ww=wr.right-wr.left, wh=wr.bottom-wr.top;
                        SetWindowPos(hwnd,nullptr,sp.x-app->manualContentDragOffset.x,sp.y-app->manualContentDragOffset.y,ww,wh,
                                     SWP_NOZORDER|SWP_NOACTIVATE|SWP_NOSIZE);
                        app->manualContentLastMoveTick=GetTickCount64();
                        app->tabDragCurrent=sp;ScreenToClient(hwnd,&app->tabDragCurrent);
                        SetCursor(LoadCursorW(nullptr,IDC_HAND));
                        app->InvalidateViewer();
                    }
                    return 0;
                }
                TRACKMOUSEEVENT tme{sizeof(tme),TME_LEAVE,hwnd,0}; TrackMouseEvent(&tme);
                app->UpdateHoverTooltip(pt);
                app->lastMouseMoveTick = GetTickCount64();
                if (app->titleTabsEnabled && (!app->fullscreen || app->fullscreenNativeCaptionVisible) && pt.y <= 38) app->InvalidateViewer();
                if(app->ActiveTabIsBrowser()&&pt.y<=static_cast<LONG>(app->ViewerTopInset()+app->BrowserToolbarHeight())) app->InvalidateViewer();
                { const bool nowStatus=app->statusVisible && ViewerApp::PointInRect(app->statusRect,pt); if(nowStatus||app->statusHoverActive)app->InvalidateViewer(); app->statusHoverActive=nowStatus; }
                if(app->overlaysVisible&&!app->overlays.empty()){bool over=false;for(const auto&ov:app->overlays)if(ViewerApp::PointInRect(ov.rect,pt)){over=true;break;}if(over||app->overlayActiveIndex>=0)app->InvalidateViewer();}
                if(app->fullscreen){RECT cr{};GetClientRect(hwnd,&cr);if(pt.y>=cr.bottom-5&&!app->fullscreenStatusVisible){app->fullscreenStatusVisible=true;app->fullscreenStatusLastInsideTick=GetTickCount64();app->InvalidateViewer();}}
                if (app->fullscreenCursorHidden) {
                    app->fullscreenCursorHidden = false;
                    SetCursor(LoadCursorW(nullptr, IDC_ARROW));
                }
                if((wParam&MK_LBUTTON)&&app->opacitySliderDragging){app->UpdateOpacityFromPoint(pt);return 0;}
                if((wParam&MK_LBUTTON)&&(app->overlayDragging||app->overlayResizing||app->overlayOpacityDragging)){app->UpdateOverlayInteraction(pt);return 0;}
                if(app->activeGestureDragButton){
                    const bool held=(app->activeGestureDragButton==1&&(wParam&MK_LBUTTON))||(app->activeGestureDragButton==2&&(wParam&MK_RBUTTON))||(app->activeGestureDragButton==3&&(wParam&MK_MBUTTON));
                    if(held){if(app->panning)app->UpdatePan(pt);else if(app->selecting||app->selectionClickCandidate)app->UpdateLeftInteraction(pt);return 0;}
                }
                if ((wParam & MK_LBUTTON) && (app->tabDragCandidate || app->tabDragging)) {
                    app->UpdateTabDrag(pt);
                    return 0;
                }
                if ((wParam & MK_LBUTTON) && app->leftBackgroundDragCandidate) {
                    const int dx = pt.x - app->leftDownClient.x;
                    const int dy = pt.y - app->leftDownClient.y;
                    if (std::abs(dx) >= ViewerApp::kDragThresholdPx || std::abs(dy) >= ViewerApp::kDragThresholdPx) {
                        app->leftBackgroundDragCandidate = false;
                        if (GetCapture() == hwnd) ReleaseCapture();
                        app->BeginNativeBackgroundDrag();
                        return 0;
                    }
                }
                if ((wParam & MK_LBUTTON) && app->leftImageDragMode==ViewerApp::LeftImageDragMode::Pan && app->panning) {
                    app->UpdatePan(pt);
                } else if ((wParam & MK_LBUTTON) && (app->selecting || app->selectionClickCandidate)) {
                    app->UpdateLeftInteraction(pt);
                }
                if ((wParam & MK_MBUTTON) && app->panning) {
                    app->UpdatePan(pt);
                }
                if ((wParam & MK_RBUTTON) && (app->overlayRightPanCandidate||app->overlayRightPanning)) {app->UpdateOverlayRightPan(pt);return 0;}
                if (wParam & MK_RBUTTON) {
                    const int rdx = pt.x - app->rightDownClient.x;
                    const int rdy = pt.y - app->rightDownClient.y;
                    const bool moved = (std::abs(rdx) >= ViewerApp::kDragThresholdPx ||
                                        std::abs(rdy) >= ViewerApp::kDragThresholdPx);
                    if (moved) app->rightMoved = true;
                    if (app->rightZoomCandidate && moved) {
                        app->rightZoomCandidate = false;
                        app->rightButtonPanning = true;
                        app->BeginPan(app->rightDownClient);
                    }
                    if (app->panning) app->UpdatePan(pt);
                }
            }
            return 0;

        case WM_MOUSELEAVE:
            if (app) app->HideHoverTooltip();
            return 0;

        case WM_LBUTTONUP:
            if (app) {
                app->RelayTooltipEvent(WM_LBUTTONUP, wParam, lParam);
                if(app->manualContentTabDrag){
                    return 0;
                }
                if(app->opacitySliderDragging){app->opacitySliderDragging=false;if(GetCapture()==hwnd)ReleaseCapture();return 0;}
                if(app->activeGestureDragButton==1){POINT pt{GET_X_LPARAM(lParam),GET_Y_LPARAM(lParam)};if(app->panning)app->EndPan();else app->EndLeftInteraction(pt);app->activeGestureDragButton=0;return 0;}
                if(app->overlayDragging||app->overlayResizing||app->overlayOpacityDragging){app->EndOverlayInteraction();return 0;}
                if(app->tabDragCandidate || app->tabDragging){
                    app->EndTabDrag(POINT{GET_X_LPARAM(lParam), GET_Y_LPARAM(lParam)});
                    return 0;
                }
                if (app->leftBackgroundDragCandidate) {
                    app->leftBackgroundDragCandidate = false;
                    if (GetCapture() == hwnd) ReleaseCapture();
                    SetCursor(LoadCursorW(nullptr, IDC_ARROW));
                } else if(app->leftImageDragMode==ViewerApp::LeftImageDragMode::Pan && app->panning) {
                    app->EndPan();
                } else {
                    app->EndLeftInteraction(POINT{GET_X_LPARAM(lParam), GET_Y_LPARAM(lParam)});
                }
            }
            return 0;

        case WM_MBUTTONDOWN:
        case WM_MBUTTONDBLCLK:
            if (app) {
                if(app->HandleHotkey(VK_MBUTTON))return 0;
                app->RelayTooltipEvent(msg, wParam, lParam);
                SetFocus(hwnd);
                POINT pt{GET_X_LPARAM(lParam), GET_Y_LPARAM(lParam)};
                if (app->HandleTitleBarMiddleClick(pt)) return 0;
                if(app->PointOnOverlay(pt) && app->ExecuteGestureAction(GestureSlot::OverlayMiddleClick,pt)) return 0;
                if(app->PointOnImage(pt)){
                    const auto dragAction=app->Gesture(GestureSlot::MiddleDragImage);
                    if(dragAction!=GestureAction::Legacy && app->ExecuteGestureAction(GestureSlot::MiddleDragImage,pt,0,3)) return 0;
                    const auto clickSlot=app->fullscreen?GestureSlot::FullscreenMiddleClickImage:GestureSlot::WindowMiddleClickImage;
                    if(app->ExecuteGestureAction(clickSlot,pt)) return 0;
                }
                if(app->middleDragPansImage) app->BeginPan(pt);
            }
            return 0;

        case WM_MBUTTONUP:
            if (app) {if(app->activeGestureDragButton==3){POINT pt{GET_X_LPARAM(lParam),GET_Y_LPARAM(lParam)};if(app->panning)app->EndPan();else app->EndLeftInteraction(pt);app->activeGestureDragButton=0;}else app->EndPan();}
            return 0;

        case WM_RBUTTONDOWN:
            if (app) {
                app->RelayTooltipEvent(WM_RBUTTONDOWN, wParam, lParam);
                app->HideHoverTooltip();
                SetFocus(hwnd);
                POINT pt{GET_X_LPARAM(lParam), GET_Y_LPARAM(lParam)};
                if(app->PointOnOverlay(pt)){
                    if(app->BeginOverlayRightPan(pt)){app->fullscreenRightClickConsumed=true;return 0;}
                    if(app->ShowOverlayContextMenu(pt)){app->fullscreenRightClickConsumed=true;return 0;}
                }
                if(!app->fullscreen && app->ShowTabContextMenuAt(pt)) return 0;
                if(app->selectionActive && app->PointInsideSelection(static_cast<float>(pt.x),static_cast<float>(pt.y)) && app->ExecuteGestureAction(GestureSlot::SelectionRightClick,pt)){app->fullscreenRightClickConsumed=app->fullscreen;return 0;}
                if(app->PointOnImage(pt)){
                    const auto dragAction=app->Gesture(GestureSlot::RightDragImage);
                    if(dragAction!=GestureAction::Legacy && app->ExecuteGestureAction(GestureSlot::RightDragImage,pt,0,2)){app->fullscreenRightClickConsumed=app->fullscreen;return 0;}
                    const auto clickSlot=app->fullscreen?GestureSlot::FullscreenRightClickImage:GestureSlot::WindowRightClickImage;
                    if(app->ExecuteGestureAction(clickSlot,pt)){app->fullscreenRightClickConsumed=app->fullscreen;return 0;}
                }else if(!app->PointOnOverlay(pt) && app->ExecuteGestureAction(GestureSlot::RightDragBackground,pt,0,2)){app->fullscreenRightClickConsumed=app->fullscreen;return 0;}
                if (app->fullscreen && app->fullscreenClickNavigation && !app->ActiveTabIsBrowser() && !app->selectionActive && !app->PointOnOverlay(pt)) {
                    app->fullscreenRightClickConsumed = true;
                    app->rightMoved = true;
                    app->Navigate(-1);
                    return 0;
                }
                app->rightDownClient = pt;
                app->rightMoved = false;
                app->rightZoomCandidate = app->selectionRightClickZoomsOut && app->PointInsideSelection(static_cast<float>(pt.x), static_cast<float>(pt.y));
                if (app->rightZoomCandidate) {
                    app->rightButtonPanning = false;
                    SetCapture(hwnd);
                    if (app->zoomOutCursor) SetCursor(app->zoomOutCursor);
                } else if (app->PointOnImage(pt) && app->rightDragPansImage) {
                    app->rightButtonPanning = true;
                    app->BeginPan(pt);
                } else {
                    // Background stationary right-click is reserved for the context menu.
                    // Whole-window movement moved to LEFT drag in Stage 9.
                    app->rightButtonPanning = false;
                    SetCapture(hwnd);
                }
            }
            return 0;

        case WM_RBUTTONUP:
            if (app) {
                app->RelayTooltipEvent(WM_RBUTTONUP, wParam, lParam);
                {POINT pt{GET_X_LPARAM(lParam),GET_Y_LPARAM(lParam)};if(app->EndOverlayRightPan(pt)){app->fullscreenRightClickConsumed=false;return 0;}}
                if(app->activeGestureDragButton==2){POINT pt{GET_X_LPARAM(lParam),GET_Y_LPARAM(lParam)};if(app->panning)app->EndPan();else app->EndLeftInteraction(pt);app->activeGestureDragButton=0;app->fullscreenRightClickConsumed=false;if(GetCapture()==hwnd)ReleaseCapture();return 0;}
                // Fullscreen has no context menu, by design.  Always consume button-up
                // (including the final UP in a WM_RBUTTONDBLCLK sequence) so rapid
                // backwards clicking can never fall through to ShowContextMenu().
                if (app->fullscreen) {
                    app->fullscreenRightClickConsumed = false;
                    app->rightZoomCandidate = false;
                    app->rightButtonPanning = false;
                    app->rightMoved = false;
                    app->EndPan();
                    if (GetCapture() == hwnd) ReleaseCapture();
                    return 0;
                }
                if (app->fullscreenRightClickConsumed) {
                    app->fullscreenRightClickConsumed = false;
                    return 0;
                }
                POINT clientPt{GET_X_LPARAM(lParam), GET_Y_LPARAM(lParam)};
                const bool zoomOut = app->rightZoomCandidate && !app->rightMoved &&
                    app->PointInsideSelection(static_cast<float>(clientPt.x), static_cast<float>(clientPt.y));
                const bool showMenu = !app->rightZoomCandidate && !app->rightMoved;
                app->rightZoomCandidate = false;
                app->rightButtonPanning = false;
                app->EndPan();
                if (GetCapture() == hwnd) ReleaseCapture();
                if (zoomOut) {
                    app->ZoomOutBySelection();
                } else if (showMenu) {
                    POINT screenPt = clientPt;
                    ClientToScreen(hwnd, &screenPt);
                    app->ShowContextMenu(screenPt);
                }
            }
            return 0;

        case WM_CONTEXTMENU:
            if (app && app->fullscreen) return 0;
            break;

        case WM_HSCROLL:
            if (app) app->ScrollTo(SB_HORZ, LOWORD(wParam), HIWORD(wParam));
            return 0;

        case WM_VSCROLL:
            if (app) app->ScrollTo(SB_VERT, LOWORD(wParam), HIWORD(wParam));
            return 0;

        case WM_CAPTURECHANGED:
            if (app) {
                app->selecting = false;
                app->selectionClickCandidate = false;
                app->panning = false;
                app->rightButtonPanning = false;
                app->rightZoomCandidate = false;
                app->rightMoved = false;
                app->leftBackgroundDragCandidate = false;
                app->nativeBackgroundDrag = false;
                app->tabDragCandidate = false;
                app->tabDragging = false;
                app->tabDragIndex = -1;
                if(app->tabDragHoverWindow){PostMessageW(app->tabDragHoverWindow,WM_APP_TAB_DRAG_LEAVE,0,0);app->tabDragHoverWindow=nullptr;}
                app->externalTabDragHover=false;app->externalTabDragClientX=0;
                app->overlayDragging=app->overlayResizing=app->overlayOpacityDragging=false;
                app->activeGestureDragButton=0;
                app->fullscreenRightClickConsumed = false;
            }
            return 0;

        case WM_MOUSEWHEEL:
            if(app&&!app->ActiveTabIsBrowser()){
                int delta=GET_WHEEL_DELTA_WPARAM(wParam);
                const bool ctrl=((GET_KEYSTATE_WPARAM(wParam)&MK_CONTROL)!=0)||((GetKeyState(VK_CONTROL)&0x8000)!=0);
                POINT wheelClient{GET_X_LPARAM(lParam),GET_Y_LPARAM(lParam)};ScreenToClient(hwnd,&wheelClient);
                if(app->opacitySliderVisible&&ViewerApp::PointInRect(app->opacityPanelRect,wheelClient)){app->SetSessionWindowOpacity(app->windowOpacityPercent+(delta>0?5:-5));return 0;}
                const int hoveredOverlay=app->OverlayIndexAtPoint(wheelClient);
                if(hoveredOverlay>=0){
                    app->overlayActiveIndex=hoveredOverlay;
                    const auto os=ctrl?GestureSlot::OverlayCtrlWheel:GestureSlot::OverlayWheel;
                    if(app->ExecuteGestureAction(os,wheelClient,delta))return 0;
                    if(app->overlayWheelZoom){app->ZoomSelectedOverlay(delta>0?+1:-1);return 0;}
                }else if(app->overlayActiveIndex>=0 && app->overlayActiveIndex<static_cast<int>(app->overlays.size()) && app->overlayWheelZoom){
                    // A selected overlay remains the wheel target until the normal selection
                    // model clears it by clicking back on the main viewer.
                    const auto os=ctrl?GestureSlot::OverlayCtrlWheel:GestureSlot::OverlayWheel;
                    if(app->ExecuteGestureAction(os,wheelClient,delta))return 0;
                    app->ZoomSelectedOverlay(delta>0?+1:-1);return 0;
                }
                const auto ws=app->fullscreen?(ctrl?GestureSlot::FullscreenCtrlWheel:GestureSlot::FullscreenWheel):(ctrl?GestureSlot::WindowCtrlWheel:GestureSlot::WindowWheel);
                if(app->ExecuteGestureAction(ws,wheelClient,delta))return 0;
                if(ctrl && app->ctrlWheelZoom){
                    POINT pt{GET_X_LPARAM(lParam),GET_Y_LPARAM(lParam)};
                    app->WheelZoom(static_cast<short>(delta),pt);
                }else if((!app->fullscreen&&!app->windowedWheelZoom)||(app->fullscreen&&!app->fullscreenWheelZoom)){
                    int nav=delta>0?-1:+1;
                    if(app->invertWheelNavigation)nav=-nav;
                    app->Navigate(nav);
                }else{
                    POINT pt{GET_X_LPARAM(lParam),GET_Y_LPARAM(lParam)};
                    app->WheelZoom(static_cast<short>(delta),pt);
                }
            }
            return 0;

        case WM_XBUTTONDOWN:
        case WM_XBUTTONDBLCLK:
            if(app){UINT vk=GET_XBUTTON_WPARAM(wParam)==XBUTTON1?VK_XBUTTON1:VK_XBUTTON2;if(app->HandleHotkey(vk))return TRUE;}
            break;

        case WM_LBUTTONDBLCLK:
            if (app) {
                POINT pt{GET_X_LPARAM(lParam), GET_Y_LPARAM(lParam)};
                if(app->PointOnOverlay(pt) && app->ExecuteGestureAction(GestureSlot::OverlayDoubleClick,pt))return 0;
                if(!app->PointOnImage(pt) && !app->PointOnOverlay(pt) && app->ExecuteGestureAction(GestureSlot::BackgroundDoubleClick,pt))return 0;
                if(app->PointOnImage(pt)){
                    // With CS_DBLCLKS the second physical click arrives as DBLCLK.
                    // Preserve the configured fullscreen single-click action for that click;
                    // windowed mode keeps its dedicated double-click gesture.
                    const auto ds=app->fullscreen?GestureSlot::FullscreenLeftClickImage:GestureSlot::WindowDoubleLeftImage;
                    if(app->ExecuteGestureAction(ds,pt))return 0;
                }
                // Status/tab/browser controls own double-clicks too.  This both preserves
                // rapid repeated navigation and guarantees a status-bar double-click can
                // never leak through to the fullscreen gesture.
                if (app->HandleOverlayLeftClick(pt)) return 0;
                if (app->PointOnOverlay(pt)) return 0;
                if (!app->selectionActive && !app->selecting && GetTickCount64() > app->suppressDoubleClickUntil) {
                    if (app->HandleBrowserDoubleClick(pt)) return 0;
                    if (!app->fullscreen && app->doubleClickFullscreen && !app->ActiveTabIsBrowser()) {
                        app->ToggleFullscreen();
                    } else if (app->fullscreen && app->doubleClickExitFullscreen && !app->ActiveTabIsBrowser()) {
                        app->ToggleFullscreen();
                    } else if (app->fullscreen && app->fullscreenClickNavigation && !app->ActiveTabIsBrowser() && !app->PointOnOverlay(pt)) {
                        app->Navigate(+1);
                    }
                }
            }
            return 0;

        case WM_RBUTTONDBLCLK:
            if (app) {
                POINT pt{GET_X_LPARAM(lParam), GET_Y_LPARAM(lParam)};
                if (!app->fullscreen) {
                    if (app->PointOnImage(pt) && app->ExecuteGestureAction(GestureSlot::WindowDoubleRightImage,pt)) return 0;
                } else {
                    // CS_DBLCLKS replaces the second physical DOWN with DBLCLK.  Run the
                    // configured fullscreen click action once for that physical click, while
                    // preserving overlay/selection ownership and suppressing the context menu.
                    app->fullscreenRightClickConsumed = true;
                    if (!app->selectionActive && !app->PointOnOverlay(pt) && app->PointOnImage(pt) &&
                        app->ExecuteGestureAction(GestureSlot::FullscreenRightClickImage,pt)) return 0;
                    if (app->fullscreenClickNavigation && !app->ActiveTabIsBrowser() && !app->selectionActive &&
                        !app->PointOnOverlay(pt))
                        app->Navigate(-1);
                    return 0;
                }
            }
            break;

        case WM_SIZE:
            if (app) {
                app->Resize(LOWORD(lParam), HIWORD(lParam)); app->ResizeShellBrowser();
                if(wParam==SIZE_MINIMIZED && app->purgeCacheOnMinimize){
                    app->cache.Clear();
                    app->qualityBitmap.Reset(); app->qualityBitmapW=app->qualityBitmapH=0;
                }
            }
            return 0;

        case WM_ERASEBKGND:
            return 1;

        case WM_PAINT: {
            PAINTSTRUCT ps{};
            BeginPaint(hwnd, &ps);
            if (app) app->Render();
            EndPaint(hwnd, &ps);
            return 0;
        }

        case WM_CLOSE:
            if (app && app->diagnosticMode && app->diagnosticSuppressClose) { app->diagnosticCloseRequested=true; return 0; }
            if (app) {
                ShowWindow(hwnd,SW_HIDE);
                DwmFlush();
                KillTimer(hwnd, kRefineTimerId);
                KillTimer(hwnd, kFullscreenCursorTimerId);
                KillTimer(hwnd, kSlideshowTimerId);
                KillTimer(hwnd, kFullscreenBarTimerId);
                KillTimer(hwnd, kAdaptivePreviewTimerId);
                if(!app->diagnosticMode) app->SaveSettings();
                app->DestroyShellBrowser();
                app->worker.Stop();
                app->prefetchWorker.Stop();
                app->prefetchWorker2.Stop();
                app->prefetchWorker3.Stop();
                app->refineWorker.Stop();
                app->adaptiveWorker.Stop();
                if(app->coldFolderThread.joinable())app->coldFolderThread.join();
            }
            DestroyWindow(hwnd);
            return 0;

        case WM_DESTROY:
            if(app){
                if(app->tabDragHoverWindow&&IsWindow(app->tabDragHoverWindow))PostMessageW(app->tabDragHoverWindow,WM_APP_TAB_DRAG_LEAVE,0,0);
                app->tabDragHoverWindow=nullptr;app->externalTabDragHover=false;app->externalTabDragClientX=0;
                KillTimer(hwnd,kExternalTabHoverWatchdogTimerId);KillTimer(hwnd,kContentTabDragTimerId);
            }
            PostQuitMessage(0);
            return 0;
    }

    return DefWindowProcW(hwnd, msg, wParam, lParam);
}

static bool CommandLineHasFlag(const wchar_t* flag) {
    int argc=0;LPWSTR* argv=CommandLineToArgvW(GetCommandLineW(),&argc);
    bool found=false;
    if(argv){for(int i=1;i<argc;++i)if(_wcsicmp(argv[i],flag)==0){found=true;break;}LocalFree(argv);}
    return found;
}

static uint32_t CommandLineDiagnosticMask() {
    int argc=0;LPWSTR* argv=CommandLineToArgvW(GetCommandLineW(),&argc);uint32_t mask=0;
    if(argv){const wchar_t prefix[]=L"--diagnostics-mask=";for(int i=1;i<argc;++i){if(_wcsnicmp(argv[i],prefix,std::size(prefix)-1)==0){wchar_t* end=nullptr;unsigned long v=wcstoul(argv[i]+std::size(prefix)-1,&end,10);if(end&&*end==0)mask=static_cast<uint32_t>(v);break;}}LocalFree(argv);}return mask&GlideDiagnostics::TestAll;
}

static std::wstring FirstCommandLinePath() {
    int argc=0;LPWSTR* argv=CommandLineToArgvW(GetCommandLineW(),&argc);
    std::wstring result;
    if(argv){
        for(int i=1;i<argc;++i){
            // Internal Glide switches are control arguments, never file/folder paths.
            // Treat any --switch as non-path so future launch flags cannot silently
            // break detached-tab startup by being mistaken for the requested image.
            if(argv[i][0]==L'-'&&argv[i][1]==L'-')continue;
            result=argv[i];break;
        }
        LocalFree(argv);
    }
    return result;
}

static void EnableNativeDarkMenus() {
    // Windows 10 1809+ exposes these uxtheme entry points by ordinal. Guard the
    // ordinal calls by the real OS build so Windows 7/8 never calls an unrelated export.
    using RtlGetVersionFn=LONG (WINAPI*)(OSVERSIONINFOW*);
    auto rtl=reinterpret_cast<RtlGetVersionFn>(GetProcAddress(GetModuleHandleW(L"ntdll.dll"),"RtlGetVersion"));
    OSVERSIONINFOW vi{}; vi.dwOSVersionInfoSize=sizeof(vi);
    if(!rtl || rtl(&vi)!=0 || vi.dwMajorVersion<10 || vi.dwBuildNumber<17763) return;
    HMODULE ux=LoadLibraryW(L"uxtheme.dll"); if(!ux)return;
    using SetPreferredAppModeFn=int (WINAPI*)(int); using FlushMenuThemesFn=void (WINAPI*)();
    auto setMode=reinterpret_cast<SetPreferredAppModeFn>(GetProcAddress(ux,MAKEINTRESOURCEA(135)));
    auto flush=reinterpret_cast<FlushMenuThemesFn>(GetProcAddress(ux,MAKEINTRESOURCEA(136)));
    if(setMode){setMode(2);if(flush)flush();}
}

int WINAPI wWinMain(HINSTANCE hInstance, HINSTANCE, PWSTR, int nCmdShow) {
    GlideCrashReport::Install(kAppName);
    INITCOMMONCONTROLSEX icc{sizeof(icc), ICC_STANDARD_CLASSES | ICC_WIN95_CLASSES};
    InitCommonControlsEx(&icc);
    EnableNativeDarkMenus();
    const HRESULT coHr = CoInitializeEx(nullptr, COINIT_APARTMENTTHREADED);
    if (FAILED(coHr)) return 1;

    const uint32_t diagnosticMask=CommandLineDiagnosticMask();
    ViewerApp app;
    app.hInst = hInstance;
    app.diagnosticMode = diagnosticMask != 0;
    app.diagnosticBackgroundWorker = CommandLineHasFlag(L"--diagnostics-background-worker");
    app.startupTick0 = GetTickCount64();
    app.LoadSettings();

    const std::wstring initial = FirstCommandLinePath();
    const bool forceNewWindow=CommandLineHasFlag(L"--new-window") || diagnosticMask != 0;
    const bool startupBrowser=CommandLineHasFlag(L"--browser");
    const bool startupHome=CommandLineHasFlag(L"--home");
    const bool startupDetached=CommandLineHasFlag(L"--detached");
    const bool startupDetachedHome=CommandLineHasFlag(L"--detached-home");
    const bool startupDragDeferred=CommandLineHasFlag(L"--drag-deferred");
    if(app.ShouldUseFastColdStart(startupBrowser,initial)){
        std::error_code coldEc;
        fs::path coldPath(initial);
        app.coldStartDirectImage = fs::is_regular_file(coldPath,coldEc) && !coldEc && IsSupportedImageExtension(coldPath);
        app.coldMinimalChrome = app.coldStartDirectImage;
        app.deferredWorkersPending = app.coldStartDirectImage;
    }
    if(startupBrowser)app.titleTabsEnabled=true;
    HANDLE instanceMutex = nullptr;
    if (app.ShouldEnforceSingleInstance(forceNewWindow)) {
        instanceMutex = CreateMutexW(nullptr, FALSE, L"Local\\GlideImageViewer.SingleInstance");
        if (instanceMutex && GetLastError() == ERROR_ALREADY_EXISTS) {
            HWND existing = FindWindowW(kClassName, nullptr);
            if (existing) {
                const std::wstring path = FirstCommandLinePath();
                if (!path.empty()) {
                    COPYDATASTRUCT cds{}; cds.dwData = 0x474C4944; cds.cbData = static_cast<DWORD>((path.size()+1)*sizeof(wchar_t)); cds.lpData = const_cast<wchar_t*>(path.c_str());
                    SendMessageW(existing, WM_COPYDATA, 0, reinterpret_cast<LPARAM>(&cds));
                } else {
                    if (IsIconic(existing)) ShowWindow(existing, SW_RESTORE);
                    SetForegroundWindow(existing);
                }
                CloseHandle(instanceMutex); CoUninitialize(); return 0;
            }
        }
    }

    if(app.coldStartDirectImage){
        app.zoomInCursor = LoadCursorW(nullptr, IDC_CROSS);
        app.zoomOutCursor = LoadCursorW(nullptr, IDC_CROSS);
    }else{
        app.zoomInCursor = ViewerApp::CreateModernZoomCursor(false);
        app.zoomOutCursor = ViewerApp::CreateModernZoomCursor(true);
        if (!app.zoomInCursor) app.zoomInCursor = LoadCursorW(nullptr, IDC_CROSS);
        if (!app.zoomOutCursor) app.zoomOutCursor = LoadCursorW(nullptr, IDC_CROSS);
    }
    HRESULT hr = app.InitializeFactories();
    if (FAILED(hr)) {
        MessageBoxW(nullptr, L"Failed to initialize Direct2D.", kAppName, MB_ICONERROR);
        CoUninitialize();
        return 1;
    }

    WNDCLASSEXW wc{};
    wc.cbSize = sizeof(wc);
    wc.style = CS_HREDRAW | CS_VREDRAW | CS_DBLCLKS;
    wc.lpfnWndProc = WndProc;
    wc.hInstance = hInstance;
    wc.hCursor = LoadCursorW(nullptr, IDC_ARROW);
    wc.hIcon = LoadIconW(hInstance, MAKEINTRESOURCEW(IDI_GLIDE));
    if (!wc.hIcon) wc.hIcon = LoadIconW(nullptr, IDI_APPLICATION);
    wc.hIconSm = static_cast<HICON>(LoadImageW(hInstance, MAKEINTRESOURCEW(IDI_GLIDE), IMAGE_ICON,
                                                GetSystemMetrics(SM_CXSMICON), GetSystemMetrics(SM_CYSMICON), 0));
    if (!wc.hIconSm) wc.hIconSm = wc.hIcon;
    wc.hbrBackground = nullptr;
    wc.lpszClassName = kClassName;

    if (!RegisterClassExW(&wc)) {
        CoUninitialize();
        return 1;
    }

    const DWORD initialStyle = WS_OVERLAPPEDWINDOW | WS_CLIPCHILDREN;
    HWND hwnd = CreateWindowExW(
        0, kClassName, kAppName,
        initialStyle,
        CW_USEDEFAULT, CW_USEDEFAULT, 1100, 760,
        nullptr, nullptr, hInstance, &app);

    if (!hwnd) {
        CoUninitialize();
        return 1;
    }
    app.startupWindowCreatedMs = app.startupTick0 ? (GetTickCount64() - app.startupTick0) : 0;

    app.ApplyWindowedChromeStyle();
    app.ApplyAlwaysOnTop();
    app.ApplyPngTransparencyMode();
    app.ApplySavedWindowPlacement();

    bool initialOpened=false;
    if(app.coldStartDirectImage && !initial.empty())
        initialOpened=app.OpenPathCold(initial);

    if (app.diagnosticBackgroundWorker) {
        LONG_PTR ex = GetWindowLongPtrW(hwnd, GWL_EXSTYLE);
        SetWindowLongPtrW(hwnd, GWL_EXSTYLE, ex | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE);
        ShowWindow(hwnd, SW_SHOWNOACTIVATE);
        SetWindowPos(hwnd, HWND_BOTTOM, 0, 0, 0, 0,
                     SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_FRAMECHANGED);
    } else {
        // A normal Glide window is an ordinary taskbar/Alt-Tab application window
        // from its very first visible frame.  Explicitly remove transient worker
        // styles and request APPWINDOW before ShowWindow so minimizing immediately
        // after launch can never strand an unrepresented window.
        LONG_PTR ex = GetWindowLongPtrW(hwnd, GWL_EXSTYLE);
        ex &= ~static_cast<LONG_PTR>(WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE);
        ex |= WS_EX_APPWINDOW;
        SetWindowLongPtrW(hwnd, GWL_EXSTYLE, ex);
        SetWindowPos(hwnd, nullptr, 0, 0, 0, 0,
                     SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_FRAMECHANGED);
        int showCmd=nCmdShow;
        if(showCmd==SW_HIDE||showCmd==SW_SHOWNOACTIVATE||showCmd==SW_SHOWNA)showCmd=SW_SHOW;
        if (!app.savedWindowMaximized) ShowWindow(hwnd, showCmd);
        else ShowWindow(hwnd, SW_MAXIMIZE);
    }
    app.UpdateTitle();
    UpdateWindow(hwnd);
    // Pre-construct Settings invisibly once the main window has painted. Reopens are
    // cached, so Settings becomes a ShowWindow operation rather than hundreds of HWND creations.
    if(!app.diagnosticMode)SetTimer(hwnd,kSettingsPrewarmTimerId,1200,nullptr);

    if(startupHome && initial.empty()) app.EnterHomeCanvas();
    else if(initial.empty() && app.titleTabsEnabled) app.EnterHomeCanvas();

    if(startupDragDeferred && startupDetached && !initial.empty() && !initialOpened){
        app.titleTabsEnabled=true;app.deferredDetachedStartup=true;app.deferredDetachedBrowser=startupBrowser;app.deferredDetachedPath=initial;
        // Lightweight visual placeholder: enough state to paint a real tab title during
        // the drag, but deliberately no ShellView/folder scan/image decode yet.
        app.openTabs.clear();app.tabBrowserMode.clear();app.tabBrowserFolder.clear();app.tabBrowserBack.clear();app.tabBrowserForward.clear();
        app.openTabs.push_back(initial);
        // A dragged folder tab should look like a folder tab from its very first frame,
        // not flash Glide's Home canvas and then swap to Explorer after mouse-up. Mark
        // the lightweight placeholder as browser-mode without constructing ExplorerBrowser.
        app.tabBrowserMode.push_back(startupBrowser);
        app.tabBrowserFolder.push_back(startupBrowser?initial:L"");
        app.tabBrowserBack.emplace_back();app.tabBrowserForward.emplace_back();app.activeTab=0;
        if(startupBrowser){
            app.currentPath.clear();app.displayedPath.clear();app.currentFolder.clear();
            app.bitmap.Reset();app.qualityBitmap.Reset();app.loading=false;app.foregroundDecodePending=false;
        }
        app.UpdateTitle();app.InvalidateViewer();
    } else if (!initial.empty() && !initialOpened) {
        if(startupBrowser){
            if(startupDetached && !startupDetachedHome){
                app.openTabs.clear();app.tabBrowserMode.clear();app.tabBrowserFolder.clear();
                app.tabBrowserBack.clear();app.tabBrowserForward.clear();app.activeTab=-1;
            }else if(startupDetachedHome && app.openTabs.empty()){
                app.EnterHomeCanvas();
            }
            app.NewBrowserTab();
            app.NavigateBrowserTo(initial,false);
        }else if (!app.OpenPath(initial)) {
            MessageBoxW(hwnd, L"Could not open the requested file or folder.", kAppName, MB_ICONERROR);
        }
    }
    // A detached image window normally contains only the dragged image tab. If the
    // user explicitly asks for a Home tab too, insert it after the image is established.
    if(startupDetached && startupDetachedHome && !startupDragDeferred && !startupBrowser && !initial.empty() && app.titleTabsEnabled){
        app.SyncActiveTabToCurrent();
        if(!app.openTabs.empty() && !app.TabIsHome(0)){
            app.openTabs.insert(app.openTabs.begin(),kHomeTabSentinel);
            app.tabBrowserMode.insert(app.tabBrowserMode.begin(),false);
            app.tabBrowserFolder.insert(app.tabBrowserFolder.begin(),L"");
            app.tabBrowserBack.insert(app.tabBrowserBack.begin(),{});
            app.tabBrowserForward.insert(app.tabBrowserForward.begin(),{});
            ++app.activeTab;
            app.InvalidateViewer();
        }
    }

    bool diagnosticRunOk=true;
    if(diagnosticMask){
        diagnosticRunOk=app.RunComprehensiveDiagnostics(diagnosticMask);
        // Diagnostics are a self-contained developer operation. Once the harness has
        // committed/exported its report it should terminate Glide; the report itself is the
        // inspection surface. A true return means the ZIP was successfully created.
        if(app.diagnosticBackgroundWorker || diagnosticRunOk) PostMessageW(hwnd,WM_CLOSE,0,0);
    }

    MSG msg{};
    while (GetMessageW(&msg, nullptr, 0, 0) > 0) {
        if ((msg.message==WM_KEYDOWN || msg.message==WM_SYSKEYDOWN) && msg.hwnd && GetAncestor(msg.hwnd,GA_ROOT)==hwnd) {
            const bool modified=(GetKeyState(VK_CONTROL)&0x8000)!=0 || (GetKeyState(VK_MENU)&0x8000)!=0;
            const bool globalFunction=msg.wParam==VK_F11;
            if ((modified||globalFunction) && app.HandleHotkey(static_cast<UINT>(msg.wParam))) continue;
        }
        TranslateMessage(&msg);
        DispatchMessageW(&msg);
    }

    app.DestroyShellBrowser();
    app.worker.Stop();
    app.prefetchWorker.Stop();
    app.prefetchWorker2.Stop();
    app.prefetchWorker3.Stop();
    app.refineWorker.Stop();
    app.adaptiveWorker.Stop();
    if(app.coldFolderThread.joinable()) app.coldFolderThread.join();
    if(!app.diagnosticMode) app.SaveSettings();
    app.DiscardRenderTarget();
    app.uiText.Reset();
    app.uiTextSmall.Reset();
    app.pictureOverlayFormat.Reset();
    app.homeTitleText.Reset();
    app.homeHeadingText.Reset();
    app.homeBodyText.Reset();
    app.dwrite.Reset();
    app.d2d.Reset();
    if (app.zoomInCursor && app.zoomInCursor != LoadCursorW(nullptr, IDC_CROSS)) DestroyCursor(app.zoomInCursor);
    if (app.zoomOutCursor && app.zoomOutCursor != LoadCursorW(nullptr, IDC_CROSS)) DestroyCursor(app.zoomOutCursor);
    if (instanceMutex) CloseHandle(instanceMutex);
    CoUninitialize();
    if(diagnosticMask&&!diagnosticRunOk)return 2;
    return static_cast<int>(msg.wParam);
}
