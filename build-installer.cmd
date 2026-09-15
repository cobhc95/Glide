@echo off
setlocal EnableExtensions EnableDelayedExpansion
cd /d "%~dp0"
title Glide Installer Builder

set "GLIDE_FROM_BUILD=0"
if /I "%~1"=="--from-build" set "GLIDE_FROM_BUILD=1"
set "GLIDE_INSTALLER_LOG=%~dp0installer-output.txt"
if exist "%GLIDE_INSTALLER_LOG%" del /q "%GLIDE_INSTALLER_LOG%" >nul 2>nul

call :main > "%GLIDE_INSTALLER_LOG%" 2>&1
set "RC=%errorlevel%"
type "%GLIDE_INSTALLER_LOG%"

if not "%RC%"=="0" (
  echo.
  echo ============================================================
  echo   INSTALLER BUILD FAILED
  echo   Full transcript: installer-output.txt
  echo ============================================================
  where clip >nul 2>nul
  if not errorlevel 1 type "%GLIDE_INSTALLER_LOG%" | clip
  if "%GLIDE_FROM_BUILD%"=="0" pause
  exit /b %RC%
)

echo.
echo ============================================================
echo   INSTALLER READY: dist-installer\Glide Setup.exe
 echo ============================================================
if "%GLIDE_FROM_BUILD%"=="0" pause
exit /b 0

:main
echo ============================================================
echo   Glide 4.1 Installer Builder
 echo ============================================================
echo.

rem Direct invocation is supported. If there is no published app yet, build a
rem current fast candidate first while explicitly suppressing build.cmd's own
rem installer step to avoid recursion.
if not exist "dist\Glide.exe" (
  echo dist\Glide.exe not found. Building the application first...
  set "GLIDE_SKIP_INSTALLER=1"
  call build.cmd fast --no-pause
  set "BUILD_RC=!errorlevel!"
  set "GLIDE_SKIP_INSTALLER="
  if not "!BUILD_RC!"=="0" (
    echo ERROR: Application build failed with exit code !BUILD_RC!.
    exit /b !BUILD_RC!
  )
)

if not exist "dist\Glide.exe" (
  echo ERROR: dist\Glide.exe still does not exist after the application build.
  exit /b 1
)

set "ISCC="
for /f "delims=" %%I in ('where ISCC.exe 2^>nul') do if not defined ISCC set "ISCC=%%I"

rem Support both current Inno Setup 7 and widely installed Inno Setup 6.
if not defined ISCC if exist "%LOCALAPPDATA%\Programs\Inno Setup 7\ISCC.exe" set "ISCC=%LOCALAPPDATA%\Programs\Inno Setup 7\ISCC.exe"
if not defined ISCC if exist "%ProgramFiles%\Inno Setup 7\ISCC.exe" set "ISCC=%ProgramFiles%\Inno Setup 7\ISCC.exe"
if not defined ISCC if exist "%ProgramFiles(x86)%\Inno Setup 7\ISCC.exe" set "ISCC=%ProgramFiles(x86)%\Inno Setup 7\ISCC.exe"
if not defined ISCC if exist "%LOCALAPPDATA%\Programs\Inno Setup 6\ISCC.exe" set "ISCC=%LOCALAPPDATA%\Programs\Inno Setup 6\ISCC.exe"
if not defined ISCC if exist "%ProgramFiles%\Inno Setup 6\ISCC.exe" set "ISCC=%ProgramFiles%\Inno Setup 6\ISCC.exe"
if not defined ISCC if exist "%ProgramFiles(x86)%\Inno Setup 6\ISCC.exe" set "ISCC=%ProgramFiles(x86)%\Inno Setup 6\ISCC.exe"

if not defined ISCC (
  echo ERROR: Inno Setup compiler ISCC.exe was not found.
  echo Install Inno Setup 6 or 7, or add its folder to PATH, then rerun.
  exit /b 1
)

echo Using Inno Setup compiler:
echo   !ISCC!
echo.

if exist dist-installer rmdir /s /q dist-installer
"!ISCC!" "installer\Glide.iss"
set "ISCC_RC=!errorlevel!"
if not "!ISCC_RC!"=="0" (
  echo ERROR: Inno Setup compiler failed with exit code !ISCC_RC!.
  exit /b !ISCC_RC!
)

if not exist "dist-installer\Glide Setup.exe" (
  echo ERROR: Inno Setup returned success but dist-installer\Glide Setup.exe was not created.
  exit /b 1
)

rem Do not call an installer READY just because ISCC returned zero. Capture a stable
rem fingerprint and verify Windows can parse the generated PE before handing it out.
for %%F in ("dist-installer\Glide Setup.exe") do set "SETUP_SIZE=%%~zF"
if !SETUP_SIZE! LSS 1048576 (
  echo ERROR: Generated setup is implausibly small: !SETUP_SIZE! bytes.
  exit /b 1
)

powershell -NoLogo -NoProfile -ExecutionPolicy Bypass -Command ^
  "$p=(Resolve-Path 'dist-installer\Glide Setup.exe').Path; $sha=[Security.Cryptography.SHA256]::Create(); try { $stream=[IO.File]::OpenRead($p); try { $h=([BitConverter]::ToString($sha.ComputeHash($stream))).Replace('-','') } finally { $stream.Dispose() } } finally { $sha.Dispose() }; $s=(Get-Item $p).Length; 'SHA256='+$h; 'Bytes='+$s; Set-Content -Encoding ASCII 'dist-installer\Glide Setup.sha256' ($h+'  Glide Setup.exe')"
if errorlevel 1 (
  echo ERROR: Could not fingerprint generated installer.
  exit /b 1
)

rem Ask Windows to load the PE image without executing the installer. This catches
rem truncation/damaged headers immediately; Inno's own runtime checksum remains the
rem final guard when the user starts setup.
powershell -NoLogo -NoProfile -ExecutionPolicy Bypass -Command ^
  "$p=(Resolve-Path 'dist-installer\Glide Setup.exe').Path; try { $fs=[IO.File]::OpenRead($p); $br=New-Object IO.BinaryReader($fs); if($br.ReadUInt16() -ne 0x5A4D){throw 'Missing MZ header'}; $fs.Position=0x3C; $pe=$br.ReadInt32(); if($pe -lt 64 -or $pe -gt ($fs.Length-4)){throw 'Invalid PE offset'}; $fs.Position=$pe; if($br.ReadUInt32() -ne 0x4550){throw 'Missing PE signature'}; $br.Close(); $fs.Close(); exit 0 } catch { Write-Error $_; exit 1 }"
if errorlevel 1 (
  echo ERROR: Generated setup failed PE integrity validation.
  exit /b 1
)

echo Installer fingerprint written to dist-installer\Glide Setup.sha256
exit /b 0
