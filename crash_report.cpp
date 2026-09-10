#define NOMINMAX
#define WIN32_LEAN_AND_MEAN

#include "crash_report.h"

#include <windows.h>
#include <shlobj.h>
#include <dbghelp.h>
#include <string>

namespace GlideCrashReport {
namespace {

wchar_t gDirectory[32768]{};
wchar_t gVersion[128]{L"Glide"};
wchar_t gLastText[32768]{};
wchar_t gLastDump[32768]{};

void JoinPath(const wchar_t* dir, const wchar_t* name, wchar_t* out, size_t outCount) {
    if (!out || outCount == 0) return;
    out[0] = 0;
    if (!dir || !*dir) return;
    wcsncpy_s(out, outCount, dir, _TRUNCATE);
    const size_t len = wcslen(out);
    if (len && out[len - 1] != L'\\' && out[len - 1] != L'/') wcscat_s(out, outCount, L"\\");
    wcscat_s(out, outCount, name ? name : L"");
}

void WriteUtf16(HANDLE file, const wchar_t* text) {
    if (!file || file == INVALID_HANDLE_VALUE || !text) return;
    DWORD written = 0;
    const DWORD bytes = static_cast<DWORD>(wcslen(text) * sizeof(wchar_t));
    WriteFile(file, text, bytes, &written, nullptr);
}

LONG WINAPI CrashFilter(EXCEPTION_POINTERS* ep) {
    SYSTEMTIME st{};
    GetLocalTime(&st);

    wchar_t stamp[64]{};
    swprintf_s(stamp, L"%04u-%02u-%02u_%02u%02u%02u",
               st.wYear, st.wMonth, st.wDay, st.wHour, st.wMinute, st.wSecond);

    wchar_t txtName[160]{}, dmpName[160]{};
    swprintf_s(txtName, L"Glide Crash %s.txt", stamp);
    swprintf_s(dmpName, L"Glide Crash %s.dmp", stamp);

    wchar_t txtPath[32768]{}, dmpPath[32768]{};
    JoinPath(gDirectory, txtName, txtPath, _countof(txtPath));
    JoinPath(gDirectory, dmpName, dmpPath, _countof(dmpPath));

    HANDLE txt = CreateFileW(txtPath, GENERIC_WRITE, FILE_SHARE_READ, nullptr, CREATE_ALWAYS, FILE_ATTRIBUTE_NORMAL, nullptr);
    if (txt != INVALID_HANDLE_VALUE) {
        const wchar_t bom = 0xFEFF;
        DWORD written = 0;
        WriteFile(txt, &bom, sizeof(bom), &written, nullptr);

        wchar_t body[2048]{};
        const DWORD code = ep && ep->ExceptionRecord ? ep->ExceptionRecord->ExceptionCode : 0;
        const void* address = ep && ep->ExceptionRecord ? ep->ExceptionRecord->ExceptionAddress : nullptr;
        swprintf_s(body,
                   L"%s crash report\r\n"
                   L"Timestamp=%s\r\n"
                   L"PID=%lu\r\n"
                   L"ThreadID=%lu\r\n"
                   L"ExceptionCode=0x%08lX\r\n"
                   L"ExceptionAddress=%p\r\n"
                   L"A matching .dmp file is written beside this report when possible.\r\n",
                   gVersion, stamp, GetCurrentProcessId(), GetCurrentThreadId(), code, address);
        WriteUtf16(txt, body);
        CloseHandle(txt);
        CopyFileW(txtPath, gLastText, FALSE);
    }

    HANDLE dump = CreateFileW(dmpPath, GENERIC_WRITE, FILE_SHARE_READ, nullptr, CREATE_ALWAYS, FILE_ATTRIBUTE_NORMAL, nullptr);
    if (dump != INVALID_HANDLE_VALUE) {
        MINIDUMP_EXCEPTION_INFORMATION mei{};
        mei.ThreadId = GetCurrentThreadId();
        mei.ExceptionPointers = ep;
        mei.ClientPointers = FALSE;
        MiniDumpWriteDump(GetCurrentProcess(), GetCurrentProcessId(), dump,
                          static_cast<MINIDUMP_TYPE>(MiniDumpNormal | MiniDumpWithThreadInfo),
                          ep ? &mei : nullptr, nullptr, nullptr);
        CloseHandle(dump);
        CopyFileW(dmpPath, gLastDump, FALSE);
    }

    return EXCEPTION_EXECUTE_HANDLER;
}

} // namespace

void Install(const wchar_t* versionLabel) {
    if (versionLabel && *versionLabel) wcsncpy_s(gVersion, versionLabel, _TRUNCATE);

    PWSTR downloads = nullptr;
    if (SUCCEEDED(SHGetKnownFolderPath(FOLDERID_Downloads, 0, nullptr, &downloads)) && downloads) {
        wcsncpy_s(gDirectory, downloads, _TRUNCATE);
        CoTaskMemFree(downloads);
    }
    if (!gDirectory[0]) {
        DWORD n = GetTempPathW(_countof(gDirectory), gDirectory);
        if (!n || n >= _countof(gDirectory)) wcsncpy_s(gDirectory, L".", _TRUNCATE);
    }

    JoinPath(gDirectory, L"Glide Crash Last.txt", gLastText, _countof(gLastText));
    JoinPath(gDirectory, L"Glide Crash Last.dmp", gLastDump, _countof(gLastDump));

    // Preserve enough stack for the exception handler even if the fault is a stack overflow.
    ULONG stackGuarantee = 64u * 1024u;
    SetThreadStackGuarantee(&stackGuarantee);
    SetUnhandledExceptionFilter(CrashFilter);
}

std::wstring LastTextPath() { return gLastText; }
std::wstring LastDumpPath() { return gLastDump; }

} // namespace GlideCrashReport
