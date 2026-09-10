#pragma once
#ifndef NOMINMAX
#define NOMINMAX
#endif
#include <windows.h>
#include <string>

namespace glide_platform {

struct RuntimePaths {
    std::wstring executablePath;
    std::wstring executableDirectory;
    std::wstring settingsIniPath;
    bool installedMode{};
};

RuntimePaths ResolveRuntimePaths();
bool BrowseForExecutable(HWND owner, const std::wstring& currentPath, std::wstring& selectedPath);
bool LaunchExternalProgram(HWND owner, const std::wstring& executable, const std::wstring& imagePath);
bool RegisterImageAssociations(const std::wstring& executablePath);
bool RemoveImageAssociations();
bool OpenDefaultAppsSettings();
void RevealInExplorer(HWND owner, const std::wstring& path);
void ShowFileProperties(HWND owner, const std::wstring& path);
bool RecycleFile(HWND owner, const std::wstring& path);

} // namespace glide_platform
