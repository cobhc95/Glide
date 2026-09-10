#include "platform_shell.h"
#include "image_formats.h"
#include <commdlg.h>
#include <shellapi.h>
#include <shlobj.h>
#include <array>
#include <filesystem>
#include <system_error>

namespace glide_platform {

namespace {
bool RegSetText(HKEY root, const std::wstring& path, const wchar_t* valueName, const std::wstring& value) {
    HKEY key{};
    if (RegCreateKeyExW(root, path.c_str(), 0, nullptr, 0, KEY_SET_VALUE, nullptr, &key, nullptr) != ERROR_SUCCESS) return false;
    const DWORD bytes = static_cast<DWORD>((value.size() + 1) * sizeof(wchar_t));
    const LONG r = RegSetValueExW(key, valueName, 0, REG_SZ, reinterpret_cast<const BYTE*>(value.c_str()), bytes);
    RegCloseKey(key);
    return r == ERROR_SUCCESS;
}

}


RuntimePaths ResolveRuntimePaths() {
    RuntimePaths paths{};
    wchar_t exe[32768]{};
    constexpr DWORD exeChars = static_cast<DWORD>(sizeof(exe) / sizeof(exe[0]));
    const DWORD len = GetModuleFileNameW(nullptr, exe, exeChars);
    if (!len || len >= exeChars) {
        paths.settingsIniPath = L"Glide.ini";
        return paths;
    }

    paths.executablePath.assign(exe, len);
    try {
        const std::filesystem::path exePath(paths.executablePath);
        const std::filesystem::path exeDir = exePath.parent_path();
        paths.executableDirectory = exeDir.wstring();

        std::error_code ec;
        paths.installedMode = std::filesystem::exists(exeDir / L"installed.flag", ec) && !ec;
        if (paths.installedMode) {
            std::filesystem::path settingsDir;

            PWSTR localAppData = nullptr;
            if (SUCCEEDED(SHGetKnownFolderPath(FOLDERID_LocalAppData, 0, nullptr, &localAppData)) && localAppData) {
                const std::wstring localCopy(localAppData);
                CoTaskMemFree(localAppData);
                localAppData = nullptr;
                settingsDir = std::filesystem::path(localCopy) / L"Glide";
            } else {
                if (localAppData) CoTaskMemFree(localAppData);
                wchar_t localEnv[32768]{};
                const DWORD envLen = GetEnvironmentVariableW(L"LOCALAPPDATA", localEnv, static_cast<DWORD>(sizeof(localEnv) / sizeof(localEnv[0])));
                if (envLen && envLen < (sizeof(localEnv) / sizeof(localEnv[0]))) settingsDir = std::filesystem::path(localEnv) / L"Glide";
            }

            if (settingsDir.empty()) settingsDir = std::filesystem::temp_directory_path() / L"Glide";
            ec.clear();
            std::filesystem::create_directories(settingsDir, ec);
            if (!ec) {
                paths.settingsIniPath = (settingsDir / L"Glide.ini").wstring();
                return paths;
            }
        }

        paths.settingsIniPath = (exeDir / L"Glide.ini").wstring();
    } catch (...) {
        paths.settingsIniPath = L"Glide.ini";
    }
    return paths;
}

bool BrowseForExecutable(HWND owner, const std::wstring& currentPath, std::wstring& selectedPath) {
    wchar_t file[32768]{};
    if (!currentPath.empty()) wcsncpy_s(file, currentPath.c_str(), _TRUNCATE);
    OPENFILENAMEW ofn{};
    ofn.lStructSize = sizeof(ofn);
    ofn.hwndOwner = owner;
    ofn.lpstrFile = file;
    ofn.nMaxFile = 32768;
    ofn.lpstrFilter = L"Programs (*.exe)\0*.exe\0All files\0*.*\0\0";
    ofn.lpstrTitle = L"Choose external program";
    ofn.Flags = OFN_FILEMUSTEXIST | OFN_PATHMUSTEXIST | OFN_NOCHANGEDIR;
    if (!GetOpenFileNameW(&ofn)) return false;
    selectedPath = file;
    return true;
}

bool LaunchExternalProgram(HWND owner, const std::wstring& executable, const std::wstring& imagePath) {
    if (executable.empty() || imagePath.empty()) return false;
    const std::wstring arg = L"\"" + imagePath + L"\"";
    const auto r = reinterpret_cast<INT_PTR>(ShellExecuteW(owner, L"open", executable.c_str(), arg.c_str(), nullptr, SW_SHOWNORMAL));
    return r > 32;
}

bool RegisterImageAssociations(const std::wstring& executablePath) {
    if (executablePath.empty()) return false;
    const std::wstring prog = L"Software\\Classes\\Glide.Image";
    bool ok = RegSetText(HKEY_CURRENT_USER, prog, nullptr, L"Glide Image") &&
              RegSetText(HKEY_CURRENT_USER, prog + L"\\DefaultIcon", nullptr, L"\"" + executablePath + L"\",0") &&
              RegSetText(HKEY_CURRENT_USER, prog + L"\\shell\\open\\command", nullptr, L"\"" + executablePath + L"\" \"%1\"");
    for (const auto* ext : GlideFormats::kImageExtensions) {
        const std::wstring p = L"Software\\Classes\\" + std::wstring(ext) + L"\\OpenWithProgids";
        HKEY key{};
        if (RegCreateKeyExW(HKEY_CURRENT_USER, p.c_str(), 0, nullptr, 0, KEY_SET_VALUE, nullptr, &key, nullptr) == ERROR_SUCCESS) {
            if (RegSetValueExW(key, L"Glide.Image", 0, REG_NONE, nullptr, 0) != ERROR_SUCCESS) ok = false;
            RegCloseKey(key);
        } else ok = false;
    }
    SHChangeNotify(SHCNE_ASSOCCHANGED, SHCNF_IDLIST, nullptr, nullptr);
    return ok;
}

bool RemoveImageAssociations() {
    for (const auto* ext : GlideFormats::kImageExtensions) {
        const std::wstring p = L"Software\\Classes\\" + std::wstring(ext) + L"\\OpenWithProgids";
        HKEY key{};
        if (RegOpenKeyExW(HKEY_CURRENT_USER, p.c_str(), 0, KEY_SET_VALUE, &key) == ERROR_SUCCESS) {
            RegDeleteValueW(key, L"Glide.Image");
            RegCloseKey(key);
        }
    }
    RegDeleteTreeW(HKEY_CURRENT_USER, L"Software\\Classes\\Glide.Image");
    SHChangeNotify(SHCNE_ASSOCCHANGED, SHCNF_IDLIST, nullptr, nullptr);
    return true;
}

bool OpenDefaultAppsSettings() {
    const auto r = reinterpret_cast<INT_PTR>(ShellExecuteW(
        nullptr, L"open", L"ms-settings:defaultapps?registeredAppMachine=Glide%20Image%20Viewer",
        nullptr, nullptr, SW_SHOWNORMAL));
    if (r > 32) return true;
    return reinterpret_cast<INT_PTR>(ShellExecuteW(nullptr, L"open", L"ms-settings:defaultapps", nullptr, nullptr, SW_SHOWNORMAL)) > 32;
}

void RevealInExplorer(HWND owner, const std::wstring& path) {
    if (path.empty()) return;
    const std::wstring args = L"/select,\"" + path + L"\"";
    ShellExecuteW(owner, L"open", L"explorer.exe", args.c_str(), nullptr, SW_SHOWNORMAL);
}

void ShowFileProperties(HWND owner, const std::wstring& path) {
    if (path.empty()) return;
    SHELLEXECUTEINFOW sei{};
    sei.cbSize = sizeof(sei);
    sei.fMask = SEE_MASK_INVOKEIDLIST;
    sei.hwnd = owner;
    sei.lpVerb = L"properties";
    sei.lpFile = path.c_str();
    sei.nShow = SW_SHOWNORMAL;
    ShellExecuteExW(&sei);
}

bool RecycleFile(HWND owner, const std::wstring& path) {
    if (path.empty()) return false;
    std::wstring from = path;
    from.push_back(L'\0');
    SHFILEOPSTRUCTW op{};
    op.hwnd = owner;
    op.wFunc = FO_DELETE;
    op.pFrom = from.c_str();
    op.fFlags = FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_SILENT;
    return SHFileOperationW(&op) == 0 && !op.fAnyOperationsAborted;
}

} // namespace glide_platform
