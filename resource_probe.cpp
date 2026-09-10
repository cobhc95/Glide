#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <shellapi.h>
#include <cstdio>

int wmain(int argc, wchar_t** argv) {
    if (argc != 2) {
        fwprintf(stderr, L"usage: resource_probe.exe <exe>\n");
        return 2;
    }

    HMODULE module = LoadLibraryExW(argv[1], nullptr,
        LOAD_LIBRARY_AS_DATAFILE | LOAD_LIBRARY_AS_IMAGE_RESOURCE);
    if (!module) {
        fwprintf(stderr, L"ERROR: cannot open final EXE as a resource module (%lu).\n", GetLastError());
        return 3;
    }

    HRSRC group = FindResourceW(module, MAKEINTRESOURCEW(1), RT_GROUP_ICON);
    if (!group) {
        fwprintf(stderr, L"ERROR: primary RT_GROUP_ICON 1 is missing from final EXE.\n");
        FreeLibrary(module);
        return 4;
    }

    HICON largeIcon = static_cast<HICON>(LoadImageW(
        module, MAKEINTRESOURCEW(1), IMAGE_ICON,
        GetSystemMetrics(SM_CXICON), GetSystemMetrics(SM_CYICON),
        LR_DEFAULTCOLOR));
    HICON smallIcon = static_cast<HICON>(LoadImageW(
        module, MAKEINTRESOURCEW(1), IMAGE_ICON,
        GetSystemMetrics(SM_CXSMICON), GetSystemMetrics(SM_CYSMICON),
        LR_DEFAULTCOLOR));

    if (!largeIcon || !smallIcon) {
        if (largeIcon) DestroyIcon(largeIcon);
        if (smallIcon) DestroyIcon(smallIcon);
        FreeLibrary(module);
        fwprintf(stderr, L"ERROR: USER32 could not instantiate Glide's embedded icon resource.\n");
        return 5;
    }

    DestroyIcon(largeIcon);
    DestroyIcon(smallIcon);
    FreeLibrary(module);

    UINT count = ExtractIconExW(argv[1], -1, nullptr, nullptr, 0);
    HICON shellLarge = nullptr, shellSmall = nullptr;
    UINT extracted = ExtractIconExW(argv[1], 0, &shellLarge, &shellSmall, 1);
    if (shellLarge) DestroyIcon(shellLarge);
    if (shellSmall) DestroyIcon(shellSmall);

    wprintf(L"Embedded application icon verified: RT_GROUP_ICON 1 and USER32 LoadImage succeeded.\n");
    if (count >= 1 && extracted == 1) {
        wprintf(L"Shell extraction check: ExtractIconExW succeeded.\n");
    } else {
        wprintf(L"WARNING: ExtractIconExW did not extract index 0; embedded USER32 icon remains valid.\n");
    }
    return 0;
}
