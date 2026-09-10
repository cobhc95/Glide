#pragma once
#define NOMINMAX
#include <windows.h>
#include <filesystem>
#include <string>
#include <vector>
#include <cstdint>

namespace GlideDiagnostics {

enum TestGroup : uint32_t {
    TestFormats       = 1u << 0,
    TestDecoderPrefs  = 1u << 1,
    TestPerformance   = 1u << 2,
    TestWindowState   = 1u << 3,
    TestSettingsUi    = 1u << 4,
    TestInvariants    = 1u << 5,
    TestAll           = TestFormats | TestDecoderPrefs | TestPerformance |
                        TestWindowState | TestSettingsUi | TestInvariants
};

struct Selection {
    uint32_t mask{TestAll};
};

struct FixtureRow {
    std::wstring file;
    std::wstring family;
    std::wstring actualFormat;
    std::wstring kind;
    std::wstring expected;
    bool screenshot{};
    std::wstring notes;
};

// Modal selector. Every group is selected by default.
bool SelectTests(HINSTANCE instance, HWND owner, Selection& selection);

// Launches a small set of isolated Glide workers, keeps them non-activating/background,
// shows one master progress/log window, and produces one combined report ZIP.
bool RunParallelMaster(HINSTANCE instance, HWND owner, const std::wstring& executable, uint32_t mask);

std::filesystem::path ExecutableDirectory();
std::filesystem::path FixtureDirectory();
bool LoadFixtureManifest(const std::filesystem::path& manifestPath,
                         std::vector<FixtureRow>& rows,
                         std::wstring* error = nullptr);
std::wstring SafeFileStem(std::wstring text);
std::wstring MaskDescription(uint32_t mask);

class ProgressWindow {
public:
    ProgressWindow() = default;
    ~ProgressWindow();
    ProgressWindow(const ProgressWindow&) = delete;
    ProgressWindow& operator=(const ProgressWindow&) = delete;

    bool Create(HINSTANCE instance, HWND owner, int totalSteps, int workerCount = 0);
    void Update(int step, int totalSteps, const std::wstring& text);
    void SetWorkerStatus(int workerIndex, const std::wstring& text);
    void AppendLog(const std::wstring& text);
    bool IsPaused() const noexcept;
    bool CancelRequested() const noexcept;
    void Close();
    HWND hwnd() const noexcept { return hwnd_; }

private:
    HWND hwnd_{};
    HWND label_{};
    HWND progress_{};
    HWND log_{};
    HWND pause_{};
    HWND cancel_{};
    std::vector<HWND> workerLabels_;
};

} // namespace GlideDiagnostics
