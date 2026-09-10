@echo off
setlocal EnableExtensions
set "NO_PAUSE="
if /I "%~1"=="--no-pause" set "NO_PAUSE=1"
cd /d "%~dp0"

echo ==============================================
echo   Glide Alpha 0.12107 - Glide - Automated Diagnostics
echo ==============================================
echo.

where cl.exe >nul 2>nul
if errorlevel 1 goto :findvs
goto :build

:findvs
set "VSWHERE=%ProgramFiles(x86)%\Microsoft Visual Studio\Installer\vswhere.exe"
if not exist "%VSWHERE%" (
    echo ERROR: MSVC compiler was not found.
    echo Install Visual Studio 2022/2026 with "Desktop development with C++".
    echo Or run this script from a Visual Studio Developer Command Prompt.
    if not defined NO_PAUSE pause
    exit /b 1
)
for /f "usebackq tokens=*" %%i in (`"%VSWHERE%" -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath`) do set "VSROOT=%%i"
if not defined VSROOT (
    echo ERROR: Visual C++ build tools were not found.
    if not defined NO_PAUSE pause
    exit /b 1
)
call "%VSROOT%\VC\Auxiliary\Build\vcvars64.bat"
if errorlevel 1 (
    echo ERROR: Failed to initialize the MSVC x64 environment.
    if not defined NO_PAUSE pause
    exit /b 1
)

:build
where rc.exe >nul 2>nul
if errorlevel 1 (
    echo ERROR: Windows Resource Compiler rc.exe was not found.
    echo Ensure the Windows SDK is installed with Visual Studio.
    if not defined NO_PAUSE pause
    exit /b 1
)

if not exist build mkdir build
if not exist dist mkdir dist

echo [1/6] Preparing minimal offline codec runtime...
call "%~dp0build_codecs.cmd"
if errorlevel 1 goto :fail

echo [2/6] Preparing clean build directory...
if exist "build" rmdir /s /q "build"
mkdir "build"

rc /nologo /fo "build\resources.res" "resources.rc"
if errorlevel 1 goto :fail

echo [3/6] Compiling Glide Glide modules in parallel...
rem Fast builder: compile the full translation-unit set in one CL invocation.
rem /MP lets MSVC schedule independent source files across available CPU cores.
rem image_decode_wic_base.cpp is a tiny build-only wrapper that applies the
rem DecodeFile=DecodeFileWicBase rename without changing image_decode.cpp.
cl /nologo /c /MP /std:c++17 /O2 /GL /EHsc /permissive- /W4 /DUNICODE /D_UNICODE /utf-8 /Fo"build\\" ^
    "main.cpp" ^
    "input_hotkeys.cpp" ^
    "ui_settings_layout.cpp" ^
    "ui_settings_shell.cpp" ^
    "crash_report.cpp" ^
    "image_decode_wic_base.cpp" ^
    "image_decode_extended.cpp" ^
    "image_decode_extended_simple.cpp" ^
    "image_decode_extended_misc.cpp" ^
    "image_decode_extended_dds.cpp" ^
    "image_cache.cpp" ^
    "image_prefetch.cpp" ^
    "overlay_state.cpp" ^
    "platform_shell.cpp" ^
    "viewer_view_state.cpp" ^
    "diagnostic_harness.cpp" ^
    "window_restore_guard.cpp"
if errorlevel 1 goto :fail

echo Verifying required object files before link...
for %%O in (
    main.obj input_hotkeys.obj ui_settings_layout.obj ui_settings_shell.obj crash_report.obj
    image_decode_wic_base.obj image_decode_extended.obj image_decode_extended_simple.obj image_decode_extended_misc.obj image_decode_extended_dds.obj
    image_cache.obj image_prefetch.obj overlay_state.obj platform_shell.obj viewer_view_state.obj diagnostic_harness.obj window_restore_guard.obj
) do (
    if not exist "build\%%O" (
        echo ERROR: Required object file was not produced: build\%%O
        goto :fail
    )
)
if not exist "build\resources.res" (
    echo ERROR: Required resource file was not produced: build\resources.res
    goto :fail
)

echo [4/6] Linking...
link /nologo /SUBSYSTEM:WINDOWS /LTCG /OUT:"build\Glide.exe" ^
   "build\main.obj" "build\input_hotkeys.obj" "build\ui_settings_layout.obj" "build\ui_settings_shell.obj" "build\crash_report.obj" ^
   "build\image_decode_wic_base.obj" "build\image_decode_extended.obj" "build\image_decode_extended_simple.obj" "build\image_decode_extended_misc.obj" "build\image_decode_extended_dds.obj" ^
   "build\image_cache.obj" "build\image_prefetch.obj" "build\overlay_state.obj" "build\platform_shell.obj" "build\viewer_view_state.obj" "build\diagnostic_harness.obj" "build\window_restore_guard.obj" "build\resources.res" ^
   user32.lib gdi32.lib advapi32.lib d2d1.lib dwrite.lib propsys.lib windowscodecs.lib ole32.lib oleaut32.lib shell32.lib comdlg32.lib shlwapi.lib dwmapi.lib uxtheme.lib dbghelp.lib
if errorlevel 1 goto :fail

echo Verifying embedded application icon in final EXE...
cl /nologo /std:c++17 /O2 /EHsc /DUNICODE /D_UNICODE /Fe"build\resource_probe.exe" "resource_probe.cpp" user32.lib shell32.lib
if errorlevel 1 goto :fail
"build\resource_probe.exe" "build\Glide.exe"
if errorlevel 1 goto :fail
del /q "build\resource_probe.exe" "build\resource_probe.obj" >nul 2>nul

echo [5/6] Packaging stable portable output...
copy /y "build\Glide.exe" "dist\Glide.exe" >nul

rem Ask Explorer to refresh the stable Glide.exe path without deleting its icon cache.
cl /nologo /std:c++17 /O2 /EHsc /DUNICODE /D_UNICODE /Fe"build\shell_icon_refresh.exe" "shell_icon_refresh.cpp" shell32.lib >nul 2>nul
if exist "build\shell_icon_refresh.exe" (
    "build\shell_icon_refresh.exe" "%~dp0dist\Glide.exe"
    del /q "build\shell_icon_refresh.exe" "build\shell_icon_refresh.obj" >nul 2>nul
) else (
    echo WARNING: Shell refresh helper could not be built; Glide itself was built successfully.
)
if exist "prebuilt_codecs" (
    if exist "dist\codecs" rmdir /s /q "dist\codecs"
    mkdir "dist\codecs"
    robocopy "prebuilt_codecs" "dist\codecs" /E /NFL /NDL /NJH /NJS /NP >nul
    if errorlevel 8 goto :fail
)
if not exist "diagnostic_fixtures\fixture_manifest.csv" (
    if exist "diagnostic_fixtures.zip" (
        echo Extracting bundled diagnostic fixtures...
        tar -xf "diagnostic_fixtures.zip"
        if errorlevel 1 goto :fail
    )
)
if not exist "diagnostic_fixtures\fixture_manifest.csv" (
    echo WARNING: Diagnostic fixtures are unavailable; Glide will build, but the bundled regression suite will be incomplete.
) else (
    rem Fast repeat packaging: Robocopy only changed/new fixture files instead of
    rem deleting and copying the complete diagnostic library every build.
    robocopy "diagnostic_fixtures" "dist\diagnostic_fixtures" /E /NFL /NDL /NJH /NJS /NP >nul
    if errorlevel 8 goto :fail
)

if not exist "dist\Glide.exe" goto :fail
for %%F in ("dist\Glide.exe") do if %%~zF LSS 10000 (
    echo ERROR: Output executable looks unexpectedly small.
    goto :fail
)

echo [6/6] Checking Glide source identity...
findstr /c:"Glide Alpha 0.12107" "main.cpp" >nul
if errorlevel 1 (
    echo ERROR: Alpha 0.12107 source identity check failed.
    goto :fail
)
findstr /c:"Comprehensive Glide image-format registry" "image_formats.h" >nul
if errorlevel 1 (
    echo ERROR: Glide image_formats.h was not found.
    goto :fail
)

echo.
echo SUCCESS - Glide Alpha 0.12107 - automated diagnostics build
echo Stable EXE path: "%~dp0dist\Glide.exe"
for %%F in ("dist\Glide.exe") do echo Size: %%~zF bytes
echo.
echo Build acceleration:
echo   - MSVC /MP parallel compilation uses all available CPU cores
echo   - Diagnostic fixtures use incremental copy on repeat builds
echo.
echo Built-in diagnostics:
echo   - Fast master progress window with Pause/Resume and Cancel
echo   - Isolated workers for formats, performance, viewer/window checks and GUI visuals
echo   - 100+ bundled image/configuration fixtures with real viewer opens and timing
echo   - Quality/rapid-preview/refinement plus cold/warm/cache/prefetch performance
echo   - Window restore/transparency/off-screen recovery and resize screenshots
echo   - Complete Settings-page visual sweep for overlap/clipping/stale paint
echo   - Legacy exhaustive per-setting behavior sweep retired after validation
echo   - New or modified features should add targeted diagnostics when developed
echo   - Codec-aware HRESULT evidence plus one combined report ZIP
echo.
echo NOTE: MSVC/runtime output remains authoritative; the diagnostic runner is designed to remove manual acceptance testing.
echo.
if not defined NO_PAUSE pause
exit /b 0


:fail
echo.
echo BUILD FAILED - see compiler/linker output above.
echo Existing dist\Glide.exe was not deliberately deleted.
echo.
if not defined NO_PAUSE pause
exit /b 1
