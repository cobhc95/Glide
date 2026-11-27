@echo off
setlocal EnableExtensions EnableDelayedExpansion
cd /d "%~dp0.."
rem Single-source the version from Directory.Build.props (see build.cmd) so the installer banner
rem and any versioned text cannot drift from the assembly metadata.
set "GLIDE_VERSION="
for /f "tokens=3 delims=<>" %%V in ('findstr /r /c:"^ *<Version>" "Directory.Build.props"') do set "GLIDE_VERSION=%%V"
title Glide Installer Builder

set "GLIDE_FROM_BUILD=0"
if /I "%~1"=="--from-build" set "GLIDE_FROM_BUILD=1"
set "GLIDE_INSTALLER_LOG=%CD%\installer-output.txt"
if exist "%GLIDE_INSTALLER_LOG%" del /q "%GLIDE_INSTALLER_LOG%" >nul 2>nul

call :main > "%GLIDE_INSTALLER_LOG%" 2>&1
set "RC=%errorlevel%"
type "%GLIDE_INSTALLER_LOG%"
if "%RC%"=="0" del /q "%GLIDE_INSTALLER_LOG%" >nul 2>nul

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
echo   Glide %GLIDE_VERSION% Installer Builder
echo ============================================================
echo.

rem Direct invocation is supported. If there is no published app yet, build a
rem current fast candidate first while explicitly suppressing build.cmd's own
rem installer step to avoid recursion.
if not exist ".artifacts\stage\Glide.exe" (
  echo .artifacts\stage\Glide.exe not found. Building the application first...
  set "GLIDE_SKIP_INSTALLER=1"
  set "GLIDE_FORCE_FAST=1"
  call build.cmd --glide-captured fast --no-pause
  set "BUILD_RC=!errorlevel!"
  set "GLIDE_FORCE_FAST="
  set "GLIDE_SKIP_INSTALLER="
  if not "!BUILD_RC!"=="0" (
    echo ERROR: Application build failed with exit code !BUILD_RC!.
    exit /b !BUILD_RC!
  )
)

if not exist ".artifacts\stage\Glide.exe" (
  echo ERROR: .artifacts\stage\Glide.exe still does not exist after the application build.
  exit /b 1
)

rem Keep the licence files in .artifacts\stage\ (normally staged by build.cmd) when invoked against an older .artifacts\stage\.
copy /y "LICENSE" ".artifacts\stage\LICENSE" >nul 2>nul
copy /y "THIRD-PARTY-NOTICES.md" ".artifacts\stage\THIRD-PARTY-NOTICES.md" >nul 2>nul
if exist "licenses" xcopy /e /i /y /q "licenses" ".artifacts\stage\licenses" >nul 2>nul

rem The installer packages .artifacts\stage\ recursively. When it is invoked directly against an older .artifacts\stage\,
rem refresh the bundled codec provider payload so Setup and the portable ZIP stay identical.
if exist "codecs\glidecodecs.dll" if exist "codecs\codecs.index.json" (
  if not exist ".artifacts\stage\codecs" mkdir ".artifacts\stage\codecs"
  copy /y "codecs\glidecodecs.dll" ".artifacts\stage\codecs\" >nul
  copy /y "codecs\glidecodecs.glidecodec.json" ".artifacts\stage\codecs\" >nul
  copy /y "codecs\codecs.index.json" ".artifacts\stage\codecs\" >nul
  if exist "codecs\glidecodecs.NOTICE.txt" copy /y "codecs\glidecodecs.NOTICE.txt" ".artifacts\stage\codecs\" >nul
)
if not exist ".artifacts\stage\codecs\glidecodecs.dll" echo WARNING: .artifacts\stage\codecs\glidecodecs.dll is missing; AVIF/HEIC/JXL/JPEG 2000 will not open in this installer build.

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
"!ISCC!" /DMyAppVersion=%GLIDE_VERSION% "installer\Glide.iss"
set "ISCC_RC=!errorlevel!"
if not "!ISCC_RC!"=="0" (
  echo ERROR: Inno Setup compiler failed with exit code !ISCC_RC!.
  exit /b !ISCC_RC!
)

if not exist "dist-installer\Glide Setup.exe" (
  echo ERROR: Inno Setup returned success but dist-installer\Glide Setup.exe was not created.
  exit /b 1
)

rem Optional code signing hook (docs\code-signing.md): only when a real certificate is configured. It runs
rem before the fingerprint below so Glide Setup.sha256 describes the signed file.
set "GLIDE_SIGN_SETUP=0"
if defined GLIDE_SIGN_CERT_THUMBPRINT set "GLIDE_SIGN_SETUP=1"
if defined GLIDE_SIGN_PFX set "GLIDE_SIGN_SETUP=1"
if "!GLIDE_SIGN_SETUP!"=="1" (
  echo Signing dist-installer\Glide Setup.exe...
  powershell -NoLogo -NoProfile -ExecutionPolicy Bypass -File "tools\sign-local.ps1" -Path dist-installer
  if errorlevel 1 (
    echo ERROR: Signing the installer failed.
    exit /b 1
  )
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
