#define NOMINMAX
#define WIN32_LEAN_AND_MEAN
#include "diagnostic_harness.h"
#include <windows.h>
#include <commctrl.h>
#include <shellapi.h>
#include <shlobj.h>
#include <algorithm>
#include <fstream>
#include <sstream>
#include <cwctype>
#include <array>
#include <map>
#include <system_error>
#include <cmath>
#include <iterator>

namespace fs = std::filesystem;

namespace GlideDiagnostics {
namespace {

constexpr wchar_t kSelectorClass[] = L"GlideDiagnosticSelector";
constexpr wchar_t kProgressClass[] = L"GlideDiagnosticProgress";

enum : int {
    IDC_DIAG_FORMATS = 51001,
    IDC_DIAG_DECODER,
    IDC_DIAG_PERFORMANCE,
    IDC_DIAG_WINDOW,
    IDC_DIAG_SETTINGS_UI,
    IDC_DIAG_INVARIANTS,
    IDC_DIAG_SELECT_ALL,
    IDC_DIAG_CLEAR_ALL,
    IDC_DIAG_RUN,
    IDC_DIAG_CANCEL,
    IDC_DIAG_PROGRESS_TEXT,
    IDC_DIAG_PROGRESS_BAR,
    IDC_DIAG_PROGRESS_LOG,
    IDC_DIAG_PROGRESS_PAUSE,
    IDC_DIAG_PROGRESS_CANCEL,
    IDC_DIAG_WORKER_BASE = 51100
};

struct SelectorState {
    Selection* selection{};
    bool accepted{};
};

void SetDefaultFont(HWND parent) {
    HFONT font = static_cast<HFONT>(GetStockObject(DEFAULT_GUI_FONT));
    for (HWND c = GetWindow(parent, GW_CHILD); c; c = GetWindow(c, GW_HWNDNEXT))
        SendMessageW(c, WM_SETFONT, reinterpret_cast<WPARAM>(font), TRUE);
}

HWND AddControl(HWND parent, const wchar_t* cls, const wchar_t* text,
                DWORD style, int x, int y, int w, int h, int id) {
    return CreateWindowExW(0, cls, text, WS_CHILD | WS_VISIBLE | style,
                           x, y, w, h, parent,
                           reinterpret_cast<HMENU>(static_cast<INT_PTR>(id)),
                           reinterpret_cast<HINSTANCE>(GetWindowLongPtrW(parent, GWLP_HINSTANCE)), nullptr);
}

void CheckAll(HWND wnd, bool checked) {
    const int ids[] = {IDC_DIAG_FORMATS, IDC_DIAG_PERFORMANCE,
                       IDC_DIAG_WINDOW, IDC_DIAG_SETTINGS_UI, IDC_DIAG_INVARIANTS};
    for (int id : ids)
        SendDlgItemMessageW(wnd, id, BM_SETCHECK, checked ? BST_CHECKED : BST_UNCHECKED, 0);
}

uint32_t ReadMask(HWND wnd) {
    uint32_t mask = 0;
    if (IsDlgButtonChecked(wnd, IDC_DIAG_FORMATS) == BST_CHECKED) mask |= TestFormats;
    if (IsDlgButtonChecked(wnd, IDC_DIAG_PERFORMANCE) == BST_CHECKED) mask |= (TestDecoderPrefs | TestPerformance);
    if (IsDlgButtonChecked(wnd, IDC_DIAG_WINDOW) == BST_CHECKED) mask |= TestWindowState;
    if (IsDlgButtonChecked(wnd, IDC_DIAG_SETTINGS_UI) == BST_CHECKED) mask |= TestSettingsUi;
    if (IsDlgButtonChecked(wnd, IDC_DIAG_INVARIANTS) == BST_CHECKED) mask |= TestInvariants;
    return mask;
}

LRESULT CALLBACK SelectorProc(HWND wnd, UINT msg, WPARAM wp, LPARAM lp) {
    auto* state = reinterpret_cast<SelectorState*>(GetWindowLongPtrW(wnd, GWLP_USERDATA));
    switch (msg) {
        case WM_NCCREATE: {
            auto* cs = reinterpret_cast<CREATESTRUCTW*>(lp);
            SetWindowLongPtrW(wnd, GWLP_USERDATA, reinterpret_cast<LONG_PTR>(cs->lpCreateParams));
            return TRUE;
        }
        case WM_CREATE: {
            AddControl(wnd, L"STATIC", L"Glide Essential Diagnostics", SS_LEFT,
                       24, 18, 620, 28, 0);
            AddControl(wnd, L"STATIC",
                       L"Fast regression suite: formats/codecs, performance, window behaviour and complete GUI visuals. Passed legacy setting probes are not rerun unless a related feature changes.",
                       SS_LEFT, 24, 50, 640, 42, 0);
            struct Row { int id; const wchar_t* title; const wchar_t* detail; };
            const Row rows[] = {
                {IDC_DIAG_FORMATS, L"Codecs, extensions and rendering",
                 L"Open every bundled format fixture through Glide's real pipeline, record decode/open speed and capture representative renders."},
                {IDC_DIAG_PERFORMANCE, L"Performance and performance-tab behaviour",
                 L"Measure cold/warm open, close/reopen, quality modes, rapid preview, cache, prefetch, refinement and navigation timing."},
                {IDC_DIAG_WINDOW, L"Viewer visuals and window behaviour",
                 L"Exercise resize/minimize/restore/transparency/off-screen recovery and capture real viewer screenshots."},
                {IDC_DIAG_SETTINGS_UI, L"Complete GUI visual audit",
                 L"Capture every Settings page and compact/large layouts; detect overlap, clipping, stale paint and separator intrusions. No exhaustive legacy setting behaviour sweep."},
                {IDC_DIAG_INVARIANTS, L"Core format/runtime invariants",
                 L"Audit extension registry, fixture coverage and critical runtime consistency."}
            };
            int y = 100;
            for (const auto& row : rows) {
                AddControl(wnd, L"BUTTON", row.title, BS_AUTOCHECKBOX | WS_TABSTOP,
                           30, y, 310, 24, row.id);
                AddControl(wnd, L"STATIC", row.detail, SS_LEFT,
                           58, y + 25, 590, 34, 0);
                y += 64;
            }
            AddControl(wnd, L"BUTTON", L"Select all", BS_PUSHBUTTON | WS_TABSTOP,
                       28, 486, 110, 32, IDC_DIAG_SELECT_ALL);
            AddControl(wnd, L"BUTTON", L"Clear all", BS_PUSHBUTTON | WS_TABSTOP,
                       148, 486, 110, 32, IDC_DIAG_CLEAR_ALL);
            AddControl(wnd, L"BUTTON", L"Cancel", BS_PUSHBUTTON | WS_TABSTOP,
                       470, 486, 90, 32, IDC_DIAG_CANCEL);
            AddControl(wnd, L"BUTTON", L"Run Diagnostics", BS_DEFPUSHBUTTON | WS_TABSTOP,
                       570, 486, 132, 32, IDC_DIAG_RUN);
            SetDefaultFont(wnd);
            CheckAll(wnd, true);
            return 0;
        }
        case WM_COMMAND: {
            const int id = LOWORD(wp);
            if (id == IDC_DIAG_SELECT_ALL) { CheckAll(wnd, true); return 0; }
            if (id == IDC_DIAG_CLEAR_ALL) { CheckAll(wnd, false); return 0; }
            if (id == IDC_DIAG_CANCEL) { DestroyWindow(wnd); return 0; }
            if (id == IDC_DIAG_RUN) {
                const uint32_t mask = ReadMask(wnd);
                if (!mask) {
                    MessageBoxW(wnd, L"Select at least one diagnostic group.", L"Glide Diagnostics", MB_OK | MB_ICONINFORMATION);
                    return 0;
                }
                if (state && state->selection) {
                    state->selection->mask = mask;
                    state->accepted = true;
                }
                DestroyWindow(wnd);
                return 0;
            }
            break;
        }
        case WM_CLOSE:
            DestroyWindow(wnd);
            return 0;
    }
    return DefWindowProcW(wnd, msg, wp, lp);
}

LRESULT CALLBACK ProgressProc(HWND wnd, UINT msg, WPARAM wp, LPARAM lp) {
    if (msg == WM_COMMAND) {
        const int id = LOWORD(wp);
        if (id == IDC_DIAG_PROGRESS_PAUSE) {
            const bool paused = GetPropW(wnd, L"GlideDiagPaused") != nullptr;
            if (paused) {
                RemovePropW(wnd, L"GlideDiagPaused");
                SetWindowTextW(GetDlgItem(wnd, IDC_DIAG_PROGRESS_PAUSE), L"Pause");
            } else {
                SetPropW(wnd, L"GlideDiagPaused", reinterpret_cast<HANDLE>(1));
                SetWindowTextW(GetDlgItem(wnd, IDC_DIAG_PROGRESS_PAUSE), L"Resume");
            }
            return 0;
        }
        if (id == IDC_DIAG_PROGRESS_CANCEL) {
            SetPropW(wnd, L"GlideDiagCancel", reinterpret_cast<HANDLE>(1));
            EnableWindow(GetDlgItem(wnd, IDC_DIAG_PROGRESS_CANCEL), FALSE);
            SetWindowTextW(GetDlgItem(wnd, IDC_DIAG_PROGRESS_CANCEL), L"Cancelling...");
            return 0;
        }
    }
    if (msg == WM_CLOSE) {
        SetPropW(wnd, L"GlideDiagCancel", reinterpret_cast<HANDLE>(1));
        return 0;
    }
    if (msg == WM_DESTROY) {
        RemovePropW(wnd, L"GlideDiagPaused");
        RemovePropW(wnd, L"GlideDiagCancel");
    }
    return DefWindowProcW(wnd, msg, wp, lp);
}

bool EnsureClass(HINSTANCE inst, const wchar_t* name, WNDPROC proc) {
    WNDCLASSEXW wc{};
    wc.cbSize = sizeof(wc);
    wc.lpfnWndProc = proc;
    wc.hInstance = inst;
    wc.hCursor = LoadCursorW(nullptr, IDC_ARROW);
    wc.hbrBackground = reinterpret_cast<HBRUSH>(COLOR_WINDOW + 1);
    wc.lpszClassName = name;
    if (RegisterClassExW(&wc)) return true;
    return GetLastError() == ERROR_CLASS_ALREADY_EXISTS;
}

std::vector<std::wstring> ParseCsvLine(const std::wstring& line) {
    std::vector<std::wstring> out;
    std::wstring field;
    bool quoted = false;
    for (size_t i = 0; i < line.size(); ++i) {
        const wchar_t c = line[i];
        if (quoted) {
            if (c == L'"') {
                if (i + 1 < line.size() && line[i + 1] == L'"') { field.push_back(L'"'); ++i; }
                else quoted = false;
            } else field.push_back(c);
        } else {
            if (c == L'"') quoted = true;
            else if (c == L',') { out.push_back(field); field.clear(); }
            else field.push_back(c);
        }
    }
    out.push_back(field);
    return out;
}

std::wstring FromUtf8(const std::string& s) {
    if (s.empty()) return {};
    const int n = MultiByteToWideChar(CP_UTF8, MB_ERR_INVALID_CHARS, s.data(), static_cast<int>(s.size()), nullptr, 0);
    if (n <= 0) return {};
    std::wstring w(static_cast<size_t>(n), L'\0');
    MultiByteToWideChar(CP_UTF8, MB_ERR_INVALID_CHARS, s.data(), static_cast<int>(s.size()), w.data(), n);
    return w;
}

} // namespace

bool SelectTests(HINSTANCE instance, HWND owner, Selection& selection) {
    if (!EnsureClass(instance, kSelectorClass, SelectorProc)) return false;
    SelectorState state{&selection, false};
    RECT ownerRect{};
    if (owner) GetWindowRect(owner, &ownerRect);
    const int w = 740, h = 570;
    int x = CW_USEDEFAULT, y = CW_USEDEFAULT;
    if (owner && IsWindow(owner)) {
        x = ownerRect.left + ((ownerRect.right - ownerRect.left) - w) / 2;
        y = ownerRect.top + ((ownerRect.bottom - ownerRect.top) - h) / 2;
    }
    HWND wnd = CreateWindowExW(WS_EX_DLGMODALFRAME, kSelectorClass, L"Glide Diagnostics",
                               WS_POPUP | WS_CAPTION | WS_SYSMENU,
                               x, y, w, h, owner, nullptr, instance, &state);
    if (!wnd) return false;
    if (owner && IsWindow(owner)) EnableWindow(owner, FALSE);
    ShowWindow(wnd, SW_SHOW);
    UpdateWindow(wnd);
    MSG msg{};
    while (IsWindow(wnd) && GetMessageW(&msg, nullptr, 0, 0) > 0) {
        if (!IsDialogMessageW(wnd, &msg)) { TranslateMessage(&msg); DispatchMessageW(&msg); }
    }
    if (owner && IsWindow(owner)) {
        EnableWindow(owner, TRUE);
        SetForegroundWindow(owner);
    }
    return state.accepted;
}

fs::path ExecutableDirectory() {
    wchar_t buf[32768]{};
    const DWORD n = GetModuleFileNameW(nullptr, buf, static_cast<DWORD>(std::size(buf)));
    if (!n || n >= std::size(buf)) return fs::current_path();
    return fs::path(std::wstring(buf, n)).parent_path();
}

fs::path FixtureDirectory() {
    return ExecutableDirectory() / L"diagnostic_fixtures";
}

bool LoadFixtureManifest(const fs::path& manifestPath, std::vector<FixtureRow>& rows, std::wstring* error) {
    rows.clear();
    std::ifstream f(manifestPath, std::ios::binary);
    if (!f) {
        if (error) *error = L"Could not open fixture_manifest.csv.";
        return false;
    }
    std::string bytes((std::istreambuf_iterator<char>(f)), std::istreambuf_iterator<char>());
    std::wstring text = FromUtf8(bytes);
    if (text.empty() && !bytes.empty()) {
        if (error) *error = L"Fixture manifest is not valid UTF-8.";
        return false;
    }
    std::wstringstream ss(text);
    std::wstring line;
    bool first = true;
    while (std::getline(ss, line)) {
        if (!line.empty() && line.back() == L'\r') line.pop_back();
        if (first) { first = false; continue; }
        if (line.empty()) continue;
        auto c = ParseCsvLine(line);
        if (c.size() < 7) continue;
        FixtureRow r{};
        r.file = c[0]; r.family = c[1]; r.actualFormat = c[2]; r.kind = c[3];
        r.expected = c[4]; r.screenshot = (c[5] == L"1"); r.notes = c[6];
        rows.push_back(std::move(r));
    }
    if (rows.empty()) {
        if (error) *error = L"Fixture manifest contains no test rows.";
        return false;
    }
    return true;
}

std::wstring SafeFileStem(std::wstring text) {
    for (wchar_t& c : text) {
        if (!(iswalnum(c) || c == L'-' || c == L'_' || c == L'.')) c = L'_';
    }
    while (!text.empty() && (text.back() == L'.' || text.back() == L' ')) text.pop_back();
    if (text.empty()) text = L"item";
    return text;
}

std::wstring MaskDescription(uint32_t mask) {
    std::vector<std::wstring> names;
    if (mask & TestFormats) names.push_back(L"codecs+formats");
    if ((mask & (TestDecoderPrefs|TestPerformance))==(TestDecoderPrefs|TestPerformance)) names.push_back(L"performance+quality");
    else { if (mask & TestDecoderPrefs) names.push_back(L"quality"); if (mask & TestPerformance) names.push_back(L"performance"); }
    if (mask & TestWindowState) names.push_back(L"viewer+window");
    if (mask & TestSettingsUi) names.push_back(L"gui-visuals");
    if (mask & TestInvariants) names.push_back(L"core-invariants");
    std::wstring out;
    for (size_t i = 0; i < names.size(); ++i) {
        if (i) out += L", ";
        out += names[i];
    }
    return out.empty() ? L"none" : out;
}

namespace {

std::string ToUtf8(const std::wstring& w) {
    if (w.empty()) return {};
    const int n = WideCharToMultiByte(CP_UTF8, 0, w.data(), static_cast<int>(w.size()), nullptr, 0, nullptr, nullptr);
    std::string out(static_cast<size_t>(std::max(0, n)), '\0');
    if (n > 0) WideCharToMultiByte(CP_UTF8, 0, w.data(), static_cast<int>(w.size()), out.data(), n, nullptr, nullptr);
    return out;
}

bool WriteUtf8File(const fs::path& path, const std::wstring& text) {
    std::ofstream f(path, std::ios::binary | std::ios::trunc);
    if (!f) return false;
    const std::string u = ToUtf8(text);
    f.write(u.data(), static_cast<std::streamsize>(u.size()));
    return static_cast<bool>(f);
}

std::wstring ReadUtf8File(const fs::path& path) {
    std::ifstream f(path, std::ios::binary);
    if (!f) return {};
    const std::string bytes((std::istreambuf_iterator<char>(f)), std::istreambuf_iterator<char>());
    return FromUtf8(bytes);
}

std::map<std::wstring, std::wstring> ParseKeyValues(const std::wstring& text) {
    std::map<std::wstring, std::wstring> out;
    std::wstringstream ss(text);
    std::wstring line;
    while (std::getline(ss, line)) {
        if (!line.empty() && line.back() == L'\r') line.pop_back();
        const size_t eq = line.find(L'=');
        if (eq != std::wstring::npos) out[line.substr(0, eq)] = line.substr(eq + 1);
    }
    return out;
}

std::wstring QuoteCommandArg(const std::wstring& arg) {
    if (arg.empty()) return L"\"\"";
    if (arg.find_first_of(L" \t\n\v\"") == std::wstring::npos) return arg;
    std::wstring out = L"\"";
    size_t slashes = 0;
    for (wchar_t c : arg) {
        if (c == L'\\') { ++slashes; continue; }
        if (c == L'\"') {
            out.append(slashes * 2 + 1, L'\\');
            out.push_back(L'\"');
            slashes = 0;
            continue;
        }
        out.append(slashes, L'\\');
        slashes = 0;
        out.push_back(c);
    }
    out.append(slashes * 2, L'\\');
    out.push_back(L'\"');
    return out;
}

uint32_t Crc32(const std::vector<BYTE>& data) {
    uint32_t crc = 0xffffffffu;
    for (BYTE b : data) { crc ^= b; for (int i = 0; i < 8; ++i) crc = (crc >> 1) ^ ((crc & 1) ? 0xedb88320u : 0u); }
    return crc ^ 0xffffffffu;
}
void Zip16(std::ofstream& f, uint16_t v) { f.put(static_cast<char>(v & 255)); f.put(static_cast<char>((v >> 8) & 255)); }
void Zip32(std::ofstream& f, uint32_t v) { Zip16(f, static_cast<uint16_t>(v & 0xffff)); Zip16(f, static_cast<uint16_t>((v >> 16) & 0xffff)); }

bool ReadBytes(const fs::path& p, std::vector<BYTE>& out) {
    std::ifstream f(p, std::ios::binary); if (!f) return false;
    f.seekg(0, std::ios::end); const auto n = f.tellg(); if (n < 0) return false;
    f.seekg(0, std::ios::beg); out.resize(static_cast<size_t>(n));
    if (!out.empty()) f.read(reinterpret_cast<char*>(out.data()), static_cast<std::streamsize>(out.size()));
    return f.good() || f.eof();
}

bool WriteStoreZip(const fs::path& folder, const fs::path& zipPath) {
    struct Entry { std::string name; uint32_t crc{}, size{}, offset{}; };
    std::vector<Entry> entries;
    std::error_code ec;
    if (!zipPath.parent_path().empty()) {
        fs::create_directories(zipPath.parent_path(), ec);
        if (ec) return false;
    }
    ec.clear();
    std::ofstream z(zipPath, std::ios::binary | std::ios::trunc); if (!z) return false;
    for (auto it = fs::recursive_directory_iterator(folder, ec); !ec && it != fs::recursive_directory_iterator(); it.increment(ec)) {
        if (!it->is_regular_file()) continue;
        std::vector<BYTE> data;
        if (!ReadBytes(it->path(), data) || data.size() > UINT32_MAX) return false;
        std::wstring rel = fs::relative(it->path(), folder, ec).generic_wstring(); if (ec) return false;
        std::string name = ToUtf8(rel);
        if (name.empty() || name.size() > UINT16_MAX) return false;
        const std::streamoff offset = static_cast<std::streamoff>(z.tellp());
        if (offset < 0 || static_cast<uint64_t>(offset) > UINT32_MAX) return false;
        Entry e{name, Crc32(data), static_cast<uint32_t>(data.size()), static_cast<uint32_t>(offset)};
        Zip32(z, 0x04034b50); Zip16(z, 20); Zip16(z, 0x0800); Zip16(z, 0); Zip16(z, 0); Zip16(z, 0);
        Zip32(z, e.crc); Zip32(z, e.size); Zip32(z, e.size); Zip16(z, static_cast<uint16_t>(name.size())); Zip16(z, 0);
        z.write(name.data(), static_cast<std::streamsize>(name.size())); if (!data.empty()) z.write(reinterpret_cast<const char*>(data.data()), static_cast<std::streamsize>(data.size()));
        entries.push_back(e);
    }
    const uint32_t centralOffset = static_cast<uint32_t>(static_cast<std::streamoff>(z.tellp()));
    for (const auto& e : entries) {
        Zip32(z, 0x02014b50); Zip16(z, 20); Zip16(z, 20); Zip16(z, 0x0800); Zip16(z, 0); Zip16(z, 0); Zip16(z, 0);
        Zip32(z, e.crc); Zip32(z, e.size); Zip32(z, e.size); Zip16(z, static_cast<uint16_t>(e.name.size()));
        Zip16(z, 0); Zip16(z, 0); Zip16(z, 0); Zip16(z, 0); Zip32(z, 0); Zip32(z, e.offset);
        z.write(e.name.data(), static_cast<std::streamsize>(e.name.size()));
    }
    if (ec || entries.size() > UINT16_MAX || !z.good()) return false;
    const uint32_t centralSize = static_cast<uint32_t>(static_cast<std::streamoff>(z.tellp())) - centralOffset;
    Zip32(z, 0x06054b50); Zip16(z, 0); Zip16(z, 0); Zip16(z, static_cast<uint16_t>(entries.size())); Zip16(z, static_cast<uint16_t>(entries.size()));
    Zip32(z, centralSize); Zip32(z, centralOffset); Zip16(z, 0);
    z.flush();
    const bool good = z.good();
    z.close();
    return good && !z.fail();
}

fs::path DownloadsDirectory() {
    PWSTR raw = nullptr;
    fs::path p;
    if (SUCCEEDED(SHGetKnownFolderPath(FOLDERID_Downloads, 0, nullptr, &raw)) && raw) { p = raw; CoTaskMemFree(raw); }
    if (p.empty()) p = ExecutableDirectory();
    return p;
}

void PumpMasterMessages(HWND dialog, DWORD ms) {
    const ULONGLONG end = GetTickCount64() + ms;
    while (GetTickCount64() < end) {
        MSG m{};
        while (PeekMessageW(&m, nullptr, 0, 0, PM_REMOVE)) {
            if (m.message == WM_QUIT) { PostQuitMessage(static_cast<int>(m.wParam)); return; }
            if (!dialog || !IsDialogMessageW(dialog, &m)) { TranslateMessage(&m); DispatchMessageW(&m); }
        }
        const ULONGLONG now = GetTickCount64();
        if (now >= end) break;
        MsgWaitForMultipleObjectsEx(0, nullptr,
                                    static_cast<DWORD>(std::min<ULONGLONG>(10, end - now)),
                                    QS_ALLINPUT, MWMO_INPUTAVAILABLE);
    }
}

struct MasterWorkerSpec {
    uint32_t mask{};
    int shardIndex{};
    int shardCount{1};
    std::wstring label;
};
struct MasterWorkerState {
    MasterWorkerSpec spec;
    PROCESS_INFORMATION pi{};
    fs::path dir;
    bool launched{};
    bool finished{};
    DWORD exitCode{STILL_ACTIVE};
    int step{};
    int total{1};
    std::wstring text{L"starting"};
    std::wstring lastLogged;
};

long ParseLong(const std::map<std::wstring, std::wstring>& kv, const wchar_t* key, long fallback = 0) {
    const auto it = kv.find(key); if (it == kv.end()) return fallback;
    wchar_t* end = nullptr; const long v = wcstol(it->second.c_str(), &end, 10);
    return (end && *end == 0) ? v : fallback;
}

std::wstring CsvCell(const std::wstring& value) {
    if (value.find_first_of(L",\"\r\n") == std::wstring::npos) return value;
    std::wstring out = L"\"";
    for (wchar_t c : value) { if (c == L'\"') out += L"\"\""; else out.push_back(c); }
    out.push_back(L'\"');
    return out;
}

void MergeWorkerCsv(const fs::path& session,
                    const std::vector<MasterWorkerState>& workers,
                    const wchar_t* sourceName,
                    const wchar_t* outputName,
                    const wchar_t* emptyHeader) {
    std::wostringstream merged;
    bool wroteHeader = false;
    std::wstring header;
    for (size_t i = 0; i < workers.size(); ++i) {
        const std::wstring text = ReadUtf8File(workers[i].dir / sourceName);
        if (text.empty()) continue;
        std::wstringstream ss(text);
        std::wstring line;
        bool first = true;
        bool compatible = true;
        while (std::getline(ss, line)) {
            if (!line.empty() && line.back() == L'\r') line.pop_back();
            if (line.empty()) continue;
            if (first) {
                first = false;
                if (!wroteHeader) {
                    header = line;
                    merged << L"worker,label," << header << L"\n";
                    wroteHeader = true;
                } else if (line != header) {
                    compatible = false;
                }
                continue;
            }
            if (!compatible) continue;
            merged << (i + 1) << L"," << CsvCell(workers[i].spec.label) << L"," << line << L"\n";
        }
    }
    if (wroteHeader) WriteUtf8File(session / outputName, merged.str());
    else if (emptyHeader) WriteUtf8File(session / outputName, L"worker,label," + std::wstring(emptyHeader) + L"\n");
}

void ReconcileSettingsCoverage(const fs::path& session,
                               const std::vector<MasterWorkerState>& workers,
                               uint32_t masterMask,
                               long& behaviorPass,
                               long& behaviorFail,
                               long& missingCoverage,
                               long& notApplicable) {
    std::map<int, std::wstring> behaviorByControl;
    for (const auto& worker : workers) {
        const std::wstring text = ReadUtf8File(worker.dir / L"behavior_results.csv");
        std::wstringstream ss(text);
        std::wstring line;
        bool first = true;
        while (std::getline(ss, line)) {
            if (!line.empty() && line.back() == L'\r') line.pop_back();
            if (line.empty()) continue;
            if (first) { first = false; continue; }
            const auto fields = ParseCsvLine(line);
            if (fields.size() < 2) continue;
            wchar_t* end = nullptr;
            const long id = wcstol(fields[0].c_str(), &end, 10);
            if (end && *end == 0 && id > 0) behaviorByControl[static_cast<int>(id)] = fields[1];
        }
    }

    const fs::path coveragePath = session / L"combined_settings_coverage.csv";
    const std::wstring text = ReadUtf8File(coveragePath);
    if (text.empty()) return;
    std::wstringstream input(text);
    std::wostringstream output;
    std::wstring line;
    bool first = true;
    // The specialist worker records the authoritative effect-level results.
    // Worker summaries are written before the master reconciles those results,
    // so deriving the master counts here prevents a misleading zero total.
    behaviorPass = 0;
    behaviorFail = 0;
    for (const auto& item : behaviorByControl) {
        if (item.second == L"BEHAVIOR_PASS") ++behaviorPass;
        else if (item.second == L"BEHAVIOR_FAIL") ++behaviorFail;
    }
    missingCoverage = 0;
    notApplicable = 0;
    while (std::getline(input, line)) {
        if (!line.empty() && line.back() == L'\r') line.pop_back();
        if (line.empty()) continue;
        if (first) { output << line << L"\n"; first = false; continue; }
        auto fields = ParseCsvLine(line);
        if (fields.size() >= 13) {
            wchar_t* end = nullptr;
            const long id = wcstol(fields[2].c_str(), &end, 10);
            if (end && *end == 0) {
                const auto found = behaviorByControl.find(static_cast<int>(id));
                if (found != behaviorByControl.end()) fields[5] = found->second;
                else if (fields[5] == L"BEHAVIOR_DEFERRED") {
                    fields[5] = (masterMask & (TestDecoderPrefs | TestPerformance)) ? L"MISSING_TEST_COVERAGE" : L"SKIP_NOT_APPLICABLE";
                }
                if (fields[5] == L"MISSING_TEST_COVERAGE") ++missingCoverage;
                else if (fields[5] == L"SKIP_NOT_APPLICABLE") ++notApplicable;
            }
        }
        for (size_t i = 0; i < fields.size(); ++i) {
            if (i) output << L',';
            output << CsvCell(fields[i]);
        }
        output << L"\n";
    }
    WriteUtf8File(coveragePath, output.str());
}

} // namespace

bool RunParallelMaster(HINSTANCE instance, HWND owner, const std::wstring& executable, uint32_t mask) {
    if (executable.empty() || !fs::is_regular_file(executable)) return false;
    std::vector<MasterWorkerSpec> specs;
    // Glide.1.73: strict Settings behavior validation is the long pole, so split it across
    // a conservative number of fully isolated processes. Six is intentionally below
    // the previously problematic 20-worker experiment: every worker owns a disjoint
    // behavioral/wiring shard and its own temporary settings/report directory. Only
    // shard 0 performs the heavyweight full visual-page sweep. This keeps global UI
    // interactions isolated while overlapping independent waits, renders and timers.
    if (mask & TestFormats) {
        specs.push_back({TestFormats, 0, 1, L"Formats 1/1"});
    }
    if (mask & TestSettingsUi) {
        // Glide codec layer: legacy exhaustive setting-behaviour sweeps are retired.
        // One isolated worker captures the complete GUI visual matrix only.
        specs.push_back({TestSettingsUi, 0, 1, L"GUI visuals"});
    }
    const uint32_t decodePerf = mask & (TestDecoderPrefs | TestPerformance);
    if (decodePerf) specs.push_back({decodePerf, 0, 1, L"Decoder + performance"});
    const uint32_t windowInv = mask & (TestWindowState | TestInvariants);
    if (windowInv) specs.push_back({windowInv, 0, 1, L"Window + invariants"});
    if (specs.empty()) return false;
    // Full run: one formats worker, one GUI-visual worker and up to two specialist
    // workers. Routine diagnostics stay fast; retired legacy behavior probes are kept
    // in source only as historical/targeted checks when a related feature changes.

    SYSTEMTIME st{}; GetLocalTime(&st);
    wchar_t stamp[64]{}; swprintf_s(stamp, L"%04u-%02u-%02u_%02u%02u%02u", st.wYear, st.wMonth, st.wDay, st.wHour, st.wMinute, st.wSecond);
    const DWORD masterPid = GetCurrentProcessId();
    const fs::path session = fs::temp_directory_path() / (std::wstring(L"Glide_Diagnostics_Parallel_") + stamp + L"_" + std::to_wstring(masterPid));
    std::error_code ec; fs::remove_all(session, ec); ec.clear(); fs::create_directories(session / L"workers", ec);
    if (ec) return false;

    const std::wstring pauseName = L"Local\\GlideDiagPause_" + std::to_wstring(masterPid) + L"_" + std::to_wstring(GetTickCount64());
    const std::wstring cancelName = L"Local\\GlideDiagCancel_" + std::to_wstring(masterPid) + L"_" + std::to_wstring(GetTickCount64());
    HANDLE pauseEvent = CreateEventW(nullptr, TRUE, FALSE, pauseName.c_str());
    HANDLE cancelEvent = CreateEventW(nullptr, TRUE, FALSE, cancelName.c_str());
    if (!pauseEvent || !cancelEvent) { if (pauseEvent) CloseHandle(pauseEvent); if (cancelEvent) CloseHandle(cancelEvent); fs::remove_all(session, ec); return false; }

    ProgressWindow progress;
    if(!progress.Create(instance, owner, 100, static_cast<int>(specs.size()))){CloseHandle(pauseEvent);CloseHandle(cancelEvent);fs::remove_all(session,ec);return false;}
    std::wostringstream masterLogText;
    auto masterLog=[&](const std::wstring& text){
        SYSTEMTIME now{};GetLocalTime(&now);wchar_t ts[32]{};swprintf_s(ts,L"%02u:%02u:%02u",now.wHour,now.wMinute,now.wSecond);
        masterLogText<<L"["<<ts<<L"] "<<text<<L"\n";progress.AppendLog(text);
    };
    masterLog(L"Essential diagnostics started with " + std::to_wstring(specs.size()) + L" isolated workers.");
    masterLog(L"Workers are non-activating and may remain behind other applications.");

    std::vector<MasterWorkerState> workers;
    workers.reserve(specs.size());
    bool launchFailure = false;
    for (size_t i = 0; i < specs.size(); ++i) {
        MasterWorkerState w{}; w.spec = specs[i];
        wchar_t wn[32]{}; swprintf_s(wn, L"worker_%02u", static_cast<unsigned>(i + 1));
        w.dir = session / L"workers" / wn;
        std::wstring cmd = QuoteCommandArg(executable) +
            L" --new-window --diagnostics-background-worker" +
            L" --diagnostics-mask=" + std::to_wstring(w.spec.mask) +
            L" --diagnostics-master-mask=" + std::to_wstring(mask) +
            L" --diagnostics-worker=" + std::to_wstring(i + 1) +
            L" --diagnostics-shard-index=" + std::to_wstring(w.spec.shardIndex) +
            L" --diagnostics-shard-count=" + std::to_wstring(w.spec.shardCount) +
            L" --diagnostics-session=" + QuoteCommandArg(session.wstring()) +
            L" --diagnostics-pause-event=" + QuoteCommandArg(pauseName) +
            L" --diagnostics-cancel-event=" + QuoteCommandArg(cancelName);
        std::vector<wchar_t> mutableCmd(cmd.begin(), cmd.end()); mutableCmd.push_back(L'\0');
        STARTUPINFOW si{}; si.cb = sizeof(si); si.dwFlags = STARTF_USESHOWWINDOW; si.wShowWindow = SW_SHOWNOACTIVATE;
        if (CreateProcessW(executable.c_str(), mutableCmd.data(), nullptr, nullptr, FALSE,
                           CREATE_NO_WINDOW | CREATE_NEW_PROCESS_GROUP, nullptr, nullptr, &si, &w.pi)) {
            w.launched = true; CloseHandle(w.pi.hThread); w.pi.hThread = nullptr;
            progress.SetWorkerStatus(static_cast<int>(i), L"W" + std::to_wstring(i + 1) + L"  " + w.spec.label + L"  running");
            masterLog(L"[W" + std::to_wstring(i + 1) + L"] started " + w.spec.label);
        } else {
            launchFailure = true; w.finished = true; w.exitCode = GetLastError();
            progress.SetWorkerStatus(static_cast<int>(i), L"W" + std::to_wstring(i + 1) + L"  launch failed");
            masterLog(L"[W" + std::to_wstring(i + 1) + L"] launch failed, error " + std::to_wstring(w.exitCode));
        }
        workers.push_back(std::move(w));
    }

    bool lastPaused = false;
    bool cancelling = false;
    ULONGLONG cancelStart = 0;
    const ULONGLONG start = GetTickCount64();
    for (;;) {
        PumpMasterMessages(progress.hwnd(), 40);
        const bool paused = progress.IsPaused();
        if (paused != lastPaused) {
            if (paused) { SetEvent(pauseEvent); masterLog(L"Master paused all workers cooperatively."); }
            else { ResetEvent(pauseEvent); masterLog(L"Master resumed all workers."); }
            lastPaused = paused;
        }
        if (progress.CancelRequested() && !cancelling) {
            cancelling = true; cancelStart = GetTickCount64(); SetEvent(cancelEvent); ResetEvent(pauseEvent);
            masterLog(L"Cancellation requested. Workers are stopping and partial evidence will be preserved.");
        }

        int finished = 0;
        double fraction = 0.0;
        for (size_t i = 0; i < workers.size(); ++i) {
            auto& w = workers[i];
            if (w.launched && !w.finished) {
                DWORD code = STILL_ACTIVE;
                if (GetExitCodeProcess(w.pi.hProcess, &code) && code != STILL_ACTIVE) {
                    w.finished = true; w.exitCode = code;
                } else {
                    // The worker writes COMPLETE only after all CSV/report streams are committed.
                    // Some Windows cleanup paths can then linger while joining image-loader threads;
                    // do not let that post-report cleanup deadlock the master indefinitely.
                    const std::wstring workerStatus = ReadUtf8File(w.dir / L"worker_status.txt");
                    if (workerStatus.find(L"COMPLETE") != std::wstring::npos) {
                        if (WaitForSingleObject(w.pi.hProcess, 500) == WAIT_TIMEOUT) {
                            TerminateProcess(w.pi.hProcess, 0);
                            WaitForSingleObject(w.pi.hProcess, 1000);
                        }
                        w.finished = true; w.exitCode = 0;
                    }
                }
            }
            const fs::path progressPath = w.dir / L"progress.txt";
            const auto kv = ParseKeyValues(ReadUtf8File(progressPath));
            if (!kv.empty()) {
                w.step = static_cast<int>(ParseLong(kv, L"step", w.step));
                w.total = std::max(1, static_cast<int>(ParseLong(kv, L"total", w.total)));
                auto it = kv.find(L"text"); if (it != kv.end() && !it->second.empty()) w.text = it->second;
            }
            if (w.finished) {
                ++finished; fraction += 1.0;
                std::wstring end = w.exitCode == 0 ? L"complete" : (cancelling ? L"cancelled" : L"failed (exit " + std::to_wstring(w.exitCode) + L")");
                progress.SetWorkerStatus(static_cast<int>(i), L"W" + std::to_wstring(i + 1) + L"  " + w.spec.label + L"  " + end);
            } else {
                fraction += std::clamp(static_cast<double>(w.step) / std::max(1, w.total), 0.0, 1.0);
                progress.SetWorkerStatus(static_cast<int>(i), L"W" + std::to_wstring(i + 1) + L"  " + w.spec.label + L"  " + std::to_wstring(w.step) + L"/" + std::to_wstring(w.total));
            }
            if (!w.text.empty() && w.text != w.lastLogged) {
                w.lastLogged = w.text;
                masterLog(L"[W" + std::to_wstring(i + 1) + L"] " + w.text);
            }
        }
        const int percent = static_cast<int>(std::lround(100.0 * fraction / std::max<size_t>(1, workers.size())));
        const ULONGLONG elapsedMs = GetTickCount64() - start;
        const ULONGLONG elapsedSec = elapsedMs / 1000;
        const ULONGLONG elapsedMin = elapsedSec / 60;
        const ULONGLONG elapsedRem = elapsedSec % 60;
        std::wstring headline = cancelling ? L"Cancelling diagnostics..." : (paused ? L"Diagnostics paused" : L"Running diagnostics in parallel");
        headline += L"  -  " + std::to_wstring(percent) + L"%  -  " + std::to_wstring(finished) + L"/" + std::to_wstring(workers.size()) + L" workers complete";
        headline += L"  -  elapsed " + std::to_wstring(elapsedMin) + L":" + (elapsedRem < 10 ? L"0" : L"") + std::to_wstring(elapsedRem);
        progress.Update(percent, 100, headline);
        if (finished == static_cast<int>(workers.size())) break;

        if (cancelling && cancelStart && GetTickCount64() - cancelStart > 2500) {
            // Cooperative cancellation is preferred. If a worker is stuck in an external codec,
            // the master guarantees Cancel remains bounded by terminating it after a short grace period.
            for (auto& w : workers) if (w.launched && !w.finished) TerminateProcess(w.pi.hProcess, 1223);
        }
    }

    for (auto& w : workers) if (w.pi.hProcess) { CloseHandle(w.pi.hProcess); w.pi.hProcess = nullptr; }
    CloseHandle(pauseEvent); CloseHandle(cancelEvent);

    long pass = 0, fail = 0, skip = 0, warn = 0;
    long wiringPass = 0, wiringFail = 0, behaviorPass = 0, behaviorFail = 0;
    long missingCoverage = 0, notApplicable = 0;
    std::wostringstream index;
    index << L"worker,label,mask,shard,status,pass,fail,skip,warn,wiring_pass,wiring_fail,behavior_pass,behavior_fail,missing_test_coverage,skip_not_applicable,duration_ms\n";
    for (size_t i = 0; i < workers.size(); ++i) {
        const auto& w = workers[i];
        auto kv = ParseKeyValues(ReadUtf8File(w.dir / L"summary.txt"));
        const long wp = ParseLong(kv, L"PASS"), wf = ParseLong(kv, L"FAIL"), ws = ParseLong(kv, L"SKIP"), ww = ParseLong(kv, L"WARN"), dur = ParseLong(kv, L"DurationMs");
        const long wwp = ParseLong(kv, L"WIRING_PASS"), wwf = ParseLong(kv, L"WIRING_FAIL");
        const long wbp = ParseLong(kv, L"BEHAVIOR_PASS"), wbf = ParseLong(kv, L"BEHAVIOR_FAIL");
        const long wmc = ParseLong(kv, L"MISSING_TEST_COVERAGE"), wna = ParseLong(kv, L"SKIP_NOT_APPLICABLE");
        pass += wp; fail += wf; skip += ws; warn += ww;
        wiringPass += wwp; wiringFail += wwf; behaviorPass += wbp; behaviorFail += wbf;
        missingCoverage += wmc; notApplicable += wna;
        index << (i + 1) << L",\"" << w.spec.label << L"\"," << w.spec.mask << L",\"" << w.spec.shardIndex + 1 << L"/" << w.spec.shardCount << L"\"," << (w.exitCode == 0 ? L"complete" : (cancelling ? L"cancelled" : L"failed")) << L"," << wp << L"," << wf << L"," << ws << L"," << ww << L"," << wwp << L"," << wwf << L"," << wbp << L"," << wbf << L"," << wmc << L"," << wna << L"," << dur << L"\n";
    }
    WriteUtf8File(session / L"worker_index.csv", index.str());
    // Root-level combined CSVs make the final ZIP analysis-ready while preserving
    // every worker's raw evidence under workers/worker_XX.
    MergeWorkerCsv(session, workers, L"format_results.csv", L"combined_format_results.csv",
                   L"fixture,family,actual_format,extension,status,expected,request_ms,decode_ms,hresult,wic_filename_hr,wic_stream_init_hr,wic_stream_decoder_hr,wic_stream_used,native_fallback,wic_advertised,source_w,source_h,rendered_w,rendered_h,preview,cache_hit,screenshot,notes");
    MergeWorkerCsv(session, workers, L"settings_results.csv", L"combined_settings_results.csv",
                   L"test,status,baseline_or_input,observed,expectation,notes");
    MergeWorkerCsv(session, workers, L"settings_coverage.csv", L"combined_settings_coverage.csv",
                   L"control_id,setting,wiring_status,behavior_status,baseline,test_input,reopened_runtime_value,main_screenshot,settings_screenshot,duration_ms,method");
    MergeWorkerCsv(session, workers, L"controlled_performance.csv", L"combined_controlled_performance.csv",
                   L"sample,extension,request_to_first_frame_ms,decode_ms,width,height,preview,refinement,cache_hit");
    MergeWorkerCsv(session, workers, L"visual_results.csv", L"combined_visual_results.csv",
                   L"area,scenario,status,screenshot,geometry_or_state,automated_assertion,ai_review_required,notes");
    MergeWorkerCsv(session, workers, L"behavior_results.csv", L"combined_behavior_results.csv",
                   L"control_id,behavior_status,source_test,evidence,duration_ms");
    ReconcileSettingsCoverage(session, workers, mask, behaviorPass, behaviorFail, missingCoverage, notApplicable);
    std::wostringstream summary;
    summary << L"Glide Glide codec layer Essential Diagnostics\nGenerated=" << stamp
            << L"\nSelected groups=" << MaskDescription(mask)
            << L"\nParallelWorkers=" << workers.size()
            << L"\nDurationMs=" << (GetTickCount64() - start)
            << L"\nStatus=" << (cancelling ? L"CANCELLED_PARTIAL" : (launchFailure ? L"WORKER_LAUNCH_FAILURE" : ((fail>0||behaviorFail>0||wiringFail>0) ? L"FAILED" : (missingCoverage>0 ? L"INCOMPLETE_BEHAVIOR_COVERAGE" : L"COMPLETE"))))
            << L"\nPASS=" << pass << L"\nFAIL=" << fail << L"\nSKIP=" << skip << L"\nWARN=" << warn
            << L"\nWIRING_PASS=" << wiringPass << L"\nWIRING_FAIL=" << wiringFail
            << L"\nBEHAVIOR_PASS=" << behaviorPass << L"\nBEHAVIOR_FAIL=" << behaviorFail
            << L"\nMISSING_TEST_COVERAGE=" << missingCoverage << L"\nSKIP_NOT_APPLICABLE=" << notApplicable
            << L"\n\nEach worker is isolated. Evidence is stored under workers/worker_XX.\n"
            << L"Routine suite intentionally omits the retired exhaustive legacy Settings behavior sweep. Performance-related settings, codecs, window behavior and all GUI visuals remain automated; new/touched features should add targeted diagnostics.\n";
    WriteUtf8File(session / L"summary.txt", summary.str());
    WriteUtf8File(session / L"README_UPLOAD_TO_CHAT.txt",
                  L"Upload this complete ZIP to the Glide development chat. It contains the combined master summary plus isolated worker reports, screenshots, timings and live progress evidence. No manual regression testing is required.\n");

    const fs::path zip = DownloadsDirectory() / (std::wstring(L"Glide Diagnostics ") + stamp + (cancelling ? L" CANCELLED" : L"") + L".zip");
    progress.Update(99, 100, L"Compiling worker reports into one ZIP...");
    masterLog(L"Compiling final report ZIP...");
    WriteUtf8File(session / L"master_log.txt", masterLogText.str());
    const bool zipped = WriteStoreZip(session, zip);
    progress.Update(100, 100, zipped ? L"Diagnostics complete" : L"Diagnostics complete, but ZIP creation failed");
    PumpMasterMessages(progress.hwnd(), 250);
    progress.Close();
    fs::remove_all(session, ec);
    if (!zipped) {
        MessageBoxW(owner, L"Diagnostics finished, but the master could not create the final report ZIP.", L"Glide Diagnostics", MB_OK | MB_ICONERROR);
        return false;
    }
    // Do not launch Explorer or otherwise steal focus on completion. The report is
    // already committed to Downloads; the caller closes Glide immediately afterwards.
    return true;
}

ProgressWindow::~ProgressWindow() { Close(); }

bool ProgressWindow::Create(HINSTANCE instance, HWND owner, int totalSteps, int workerCount) {
    if (!EnsureClass(instance, kProgressClass, ProgressProc)) return false;
    RECT orc{};
    if (owner) GetWindowRect(owner, &orc);
    const int w = workerCount > 0 ? 1120 : 610;
    const int h = workerCount > 0 ? 650 : 168;
    int x = CW_USEDEFAULT, y = CW_USEDEFAULT;
    if (owner && IsWindow(owner)) {
        x = orc.left + ((orc.right - orc.left) - w) / 2;
        y = orc.top + ((orc.bottom - orc.top) - h) / 2;
    }
    const DWORD exStyle = workerCount > 0 ? WS_EX_APPWINDOW : (WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE);
    const DWORD style = workerCount > 0 ? (WS_OVERLAPPED | WS_CAPTION | WS_SYSMENU | WS_MINIMIZEBOX)
                                        : (WS_POPUP | WS_CAPTION | WS_SYSMENU);
    // The master is a normal minimizable taskbar window. It is initially shown
    // without activation, but can be clicked/minimized/restored normally by the user.
    // Child workers remain non-activating tool windows.
    hwnd_ = CreateWindowExW(exStyle, kProgressClass,
                            workerCount > 0 ? L"Glide Diagnostics - Master" : L"Glide Diagnostics",
                            style,
                            x, y, w, h, workerCount > 0 ? nullptr : owner, nullptr, instance, nullptr);
    if (!hwnd_) return false;
    label_ = AddControl(hwnd_, L"STATIC", L"Preparing diagnostics...", SS_LEFT,
                        22, 20, w - 64, 42, IDC_DIAG_PROGRESS_TEXT);
    progress_ = AddControl(hwnd_, PROGRESS_CLASSW, L"", 0,
                           22, 68, w - 64, 22, IDC_DIAG_PROGRESS_BAR);
    SendMessageW(progress_, PBM_SETRANGE32, 0, std::max(1, totalSteps));
    SendMessageW(progress_, PBM_SETPOS, 0, 0);

    if (workerCount > 0) {
        AddControl(hwnd_, L"STATIC", L"Parallel workers", SS_LEFT, 22, 103, 180, 22, 0);
        workerLabels_.clear();
        const int cols = workerCount > 20 ? 5 : (workerCount > 12 ? 4 : 2);
        const int colW = (w - 64) / cols;
        const int rows = (workerCount + cols - 1) / cols;
        for (int i = 0; i < workerCount; ++i) {
            const int col = i % cols, row = i / cols;
            HWND label = AddControl(hwnd_, L"STATIC", (L"W" + std::to_wstring(i + 1) + L"  queued").c_str(),
                                    SS_LEFT, 22 + col * colW, 129 + row * 22, colW - 10, 20,
                                    IDC_DIAG_WORKER_BASE + i);
            workerLabels_.push_back(label);
        }
        const int workersBottom = 129 + rows * 22;
        AddControl(hwnd_, L"STATIC", L"Live log", SS_LEFT, 22, workersBottom + 8, 120, 22, 0);
        log_ = AddControl(hwnd_, L"EDIT", L"", ES_MULTILINE | ES_AUTOVSCROLL | ES_READONLY | WS_VSCROLL | WS_BORDER,
                          22, workersBottom + 32, w - 64, 250, IDC_DIAG_PROGRESS_LOG);
        pause_ = AddControl(hwnd_, L"BUTTON", L"Pause", BS_PUSHBUTTON | WS_TABSTOP,
                            w - 230, h - 72, 90, 32, IDC_DIAG_PROGRESS_PAUSE);
        cancel_ = AddControl(hwnd_, L"BUTTON", L"Cancel", BS_PUSHBUTTON | WS_TABSTOP,
                             w - 130, h - 72, 90, 32, IDC_DIAG_PROGRESS_CANCEL);
    }
    SetDefaultFont(hwnd_);
    ShowWindow(hwnd_, SW_SHOWNOACTIVATE);
    UpdateWindow(hwnd_);
    return true;
}

void ProgressWindow::Update(int step, int totalSteps, const std::wstring& text) {
    if (!hwnd_) return;
    if (progress_) {
        SendMessageW(progress_, PBM_SETRANGE32, 0, std::max(1, totalSteps));
        SendMessageW(progress_, PBM_SETPOS, std::clamp(step, 0, std::max(1, totalSteps)), 0);
    }
    if (label_) SetWindowTextW(label_, text.c_str());
    UpdateWindow(hwnd_);
}

void ProgressWindow::SetWorkerStatus(int workerIndex, const std::wstring& text) {
    if (workerIndex < 0 || static_cast<size_t>(workerIndex) >= workerLabels_.size()) return;
    SetWindowTextW(workerLabels_[static_cast<size_t>(workerIndex)], text.c_str());
}

void ProgressWindow::AppendLog(const std::wstring& text) {
    if (!log_ || text.empty()) return;
    int len = GetWindowTextLengthW(log_);
    SendMessageW(log_, EM_SETSEL, len, len);
    std::wstring line = text;
    if (line.size() < 2 || line.substr(line.size() - 2) != L"\r\n") line += L"\r\n";
    SendMessageW(log_, EM_REPLACESEL, FALSE, reinterpret_cast<LPARAM>(line.c_str()));
    SendMessageW(log_, EM_SCROLLCARET, 0, 0);
}

bool ProgressWindow::IsPaused() const noexcept {
    return hwnd_ && GetPropW(hwnd_, L"GlideDiagPaused") != nullptr;
}

bool ProgressWindow::CancelRequested() const noexcept {
    return hwnd_ && GetPropW(hwnd_, L"GlideDiagCancel") != nullptr;
}

void ProgressWindow::Close() {
    if (hwnd_ && IsWindow(hwnd_)) DestroyWindow(hwnd_);
    hwnd_ = label_ = progress_ = log_ = pause_ = cancel_ = nullptr;
    workerLabels_.clear();
}

} // namespace GlideDiagnostics
