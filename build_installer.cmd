@echo off
setlocal EnableExtensions
cd /d "%~dp0"

echo ==============================================
echo   Glide Alpha 0.12107 - Installer Builder
echo ==============================================
echo.

set "ISCC="

rem 1) PATH
where ISCC.exe >nul 2>nul
if not errorlevel 1 for /f "delims=" %%I in ('where ISCC.exe') do if not defined ISCC set "ISCC=%%I"

rem 2) Standard machine-wide locations
if not defined ISCC if exist "%ProgramFiles%\Inno Setup 7\ISCC.exe" set "ISCC=%ProgramFiles%\Inno Setup 7\ISCC.exe"
if not defined ISCC if defined ProgramFiles(x86) if exist "%ProgramFiles(x86)%\Inno Setup 7\ISCC.exe" set "ISCC=%ProgramFiles(x86)%\Inno Setup 7\ISCC.exe"

rem 3) Common per-user locations
if not defined ISCC if exist "%LOCALAPPDATA%\Programs\Inno Setup 7\ISCC.exe" set "ISCC=%LOCALAPPDATA%\Programs\Inno Setup 7\ISCC.exe"
if not defined ISCC if exist "%LOCALAPPDATA%\Inno Setup 7\ISCC.exe" set "ISCC=%LOCALAPPDATA%\Inno Setup 7\ISCC.exe"

rem 4) Ask Inno Setup's uninstall registration for its actual install directory.
for %%R in (
    "HKCU\Software\Microsoft\Windows\CurrentVersion\Uninstall\Inno Setup 7_is1"
    "HKLM\Software\Microsoft\Windows\CurrentVersion\Uninstall\Inno Setup 7_is1"
    "HKLM\Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\Inno Setup 7_is1"
) do (
    if not defined ISCC (
        for /f "tokens=2,*" %%A in ('reg query %%R /v InstallLocation 2^>nul ^| find /I "InstallLocation"') do (
            if exist "%%B\ISCC.exe" set "ISCC=%%B\ISCC.exe"
        )
    )
)

if not defined ISCC (
    echo ERROR: Inno Setup 7 appears to be installed, but the compiler ISCC.exe was not found in the usual locations.
    echo.
    echo The builder checked PATH, Program Files, per-user AppData, and the Inno Setup registry entries.
    echo.
    echo Please run this command in Command Prompt to locate it:
    echo   where /r "%LOCALAPPDATA%" ISCC.exe
    echo.
    echo If that finds nothing, also run:
    echo   where /r "%ProgramFiles%" ISCC.exe
    echo.
    echo Paste the resulting path here if it is still not detected.
    echo.
    pause
    exit /b 1
)

echo Found Inno Setup compiler:
echo   "%ISCC%"
echo.

echo [1/4] Building Glide.exe...
call "%~dp0build.cmd" --no-pause
if errorlevel 1 goto :fail

if not exist "%~dp0dist\Glide.exe" (
    echo ERROR: dist\Glide.exe was not produced.
    goto :fail
)

echo [2/4] Generating installer file-association mappings...
powershell.exe -NoLogo -NoProfile -File "%~dp0installer\generate_associations.ps1" ^
    -HeaderPath "%~dp0image_formats.h" ^
    -OutputPath "%~dp0installer\associations.generated.iss"
if errorlevel 1 goto :fail

findstr /I /c:"GENERATED FILE" "%~dp0installer\associations.generated.iss" >nul 2>nul
if errorlevel 1 (
    echo ERROR: Installer association map was not generated correctly.
    goto :fail
)

if not exist "%~dp0installer_output" mkdir "%~dp0installer_output"

echo [3/4] Compiling the x64 installer with Inno Setup...
"%ISCC%" "%~dp0installer\Glide.iss"
if errorlevel 1 goto :fail

set "SETUP=%~dp0installer_output\Glide_Setup_Alpha_0.12107_x64.exe"
if not exist "%SETUP%" (
    echo ERROR: The expected setup executable was not produced.
    goto :fail
)

echo [4/4] Verifying installer output...
for %%F in ("%SETUP%") do (
    if %%~zF LSS 100000 (
        echo ERROR: Installer output looks unexpectedly small.
        goto :fail
    )
    echo.
    echo SUCCESS - Glide installer created.
    echo Installer: "%%~fF"
    echo Size: %%~zF bytes
)

echo.
echo Installer includes the built-in diagnostic fixture library.
echo After install, use Settings ^> Run Diagnostics for automated acceptance testing.
 echo.
pause
exit /b 0

:fail
echo.
echo INSTALLER BUILD FAILED - see the output above.
echo.
pause
exit /b 1
