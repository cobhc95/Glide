#pragma once
#define NOMINMAX
#include <windows.h>

// Immediately applies the same non-destructive repair used by Glide's restore hook.
// Exposed for Glide automated diagnostics.
bool GlideRepairRestoredWindowNow(HWND hwnd) noexcept;
