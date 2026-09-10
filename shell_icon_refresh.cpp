#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <shellapi.h>
#include <shlobj.h>
#include <cstdio>
int wmain(int argc, wchar_t** argv) {
    if(argc != 2) return 2;
    SHChangeNotify(SHCNE_UPDATEITEM, SHCNF_PATHW | SHCNF_FLUSH, argv[1], nullptr);
    SHChangeNotify(SHCNE_ASSOCCHANGED, SHCNF_IDLIST | SHCNF_FLUSH, nullptr, nullptr);
    wprintf(L"Windows Shell icon refresh requested for: %ls\n", argv[1]);
    return 0;
}
