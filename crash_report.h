#pragma once

#include <string>

namespace GlideCrashReport {

// Installs a lightweight native crash recorder. On an unhandled exception Glide writes
// a small text report and a Windows minidump to the current user's Downloads folder.
void Install(const wchar_t* versionLabel);

// Stable "last crash" paths used by Export Diagnostics on the next launch.
std::wstring LastTextPath();
std::wstring LastDumpPath();

} // namespace GlideCrashReport
