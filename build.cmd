@echo off

rem Outer wrapper: stream the inner build live while mirroring the transcript to build-output.txt.
rem PowerShell Tee-Object preserves live progress and a complete failure transcript at the same time.
rem On failure the transcript is copied to the Windows clipboard for one-paste diagnostics.
if /I "%~1"=="--glide-captured" (
  shift
  goto :captured_entry
)

setlocal EnableExtensions EnableDelayedExpansion
set "GLIDE_WRAPPER_NO_PAUSE=0"
for %%A in (%*) do if /I "%%~A"=="--no-pause" set "GLIDE_WRAPPER_NO_PAUSE=1"
set "GLIDE_BUILD_LOG=%~dp0build-output.txt"
if exist "%GLIDE_BUILD_LOG%" del /q "%GLIDE_BUILD_LOG%" >nul 2>nul

echo Glide build running. Output is live and mirrored to build-output.txt...
powershell -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%~dp0tools\build-live.ps1" -BuildScript "%~f0" -LogPath "%GLIDE_BUILD_LOG%" -ForwardArgsLine "%*"
set "GLIDE_WRAPPER_RC=%errorlevel%"

if not "%GLIDE_WRAPPER_RC%"=="0" (
  if not exist "%GLIDE_BUILD_LOG%" (
    >"%GLIDE_BUILD_LOG%" echo Glide build wrapper failed before the normal transcript could be created.
    >>"%GLIDE_BUILD_LOG%" echo Exit code: %GLIDE_WRAPPER_RC%
    >>"%GLIDE_BUILD_LOG%" echo Command: powershell build-live.ps1
  )
  where clip >nul 2>nul
  if not errorlevel 1 (
    type "%GLIDE_BUILD_LOG%" | clip
    set "GLIDE_CLIPBOARD_STATUS=copied to clipboard"
  ) else (
    set "GLIDE_CLIPBOARD_STATUS=NOT copied - clip.exe was not found"
  )
  echo.
  echo ============================================================
  echo   FULL FAILURE TRANSCRIPT: build-output.txt
  echo   Transcript: !GLIDE_CLIPBOARD_STATUS!
  echo   Paste build-output.txt directly back into ChatGPT for diagnostics.
  echo ============================================================
  if not "%GLIDE_WRAPPER_NO_PAUSE%"=="1" pause
)
exit /b %GLIDE_WRAPPER_RC%

:captured_entry
setlocal EnableExtensions EnableDelayedExpansion
cd /d "%~dp0"
title Glide 3.0

set "GLIDE_AOT=0"
set "GLIDE_FAST=0"
set "GLIDE_NO_PAUSE=0"
set "DOTNET_CLI_TELEMETRY_OPTOUT=1"
set "DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1"
set "NUGET_XMLDOC_MODE=skip"
set "GLIDE_BUILD_RC="

:parse_args
if "%~1"=="" goto :args_done
if /I "%~1"=="aot" set "GLIDE_AOT=1"
if /I "%~1"=="fast" set "GLIDE_FAST=1"
if /I "%~1"=="--no-pause" set "GLIDE_NO_PAUSE=1"
shift
goto :parse_args

:args_done
echo ============================================================
echo   Glide 3.0
echo ============================================================
echo.
if "%GLIDE_AOT%"=="1" (
  echo ERROR: The optional AOT build is currently disabled.
  echo Reason: the embedded Windows Explorer host uses built-in COM interop ^(ComImport/Activator/Marshal^) which Windows NativeAOT does not support.
  echo Use the ordinary non-AOT build while Explorer COM is migrated to an AOT-compatible ComWrappers path.
  set "GLIDE_BUILD_RC=2"
  goto :fail
)
if "%GLIDE_FAST%"=="1" echo   FAST DEV MODE: compile + publish; tests/fixture regeneration skipped when safe.
echo   Incremental caches: native\Glide.Native\build + managed bin/obj + NuGet global cache
echo.
call :progress 2 "Preflight"

where dotnet >nul 2>nul
if errorlevel 1 (
  echo ERROR: .NET SDK not found. Install .NET 8 SDK x64, then rerun.
  set "GLIDE_BUILD_RC=1"
  goto :fail
)

if exist codecs (
  echo Generating lazy codec routing index...
  powershell -NoLogo -NoProfile -ExecutionPolicy Bypass -File "tools\generate-codec-index.ps1" -CodecDirectory "codecs"
  if errorlevel 1 (
    set "GLIDE_BUILD_RC=!errorlevel!"
    goto :fail
  )
)

if exist native\Glide.Native\CMakeLists.txt (
  where cmake >nul 2>nul
  if errorlevel 1 (
    echo ERROR: CMake is required for the TIFF decoder bridge. The declared TIFF support cannot ship without Glide.Native.dll.
    set "GLIDE_BUILD_RC=1"
    goto :fail
  ) else (
    call :progress 8 "Native configure/toolchain validation"

    rem Validate the reusable native cache by generator/platform, not by looking for
    rem CMAKE_CXX_COMPILER in CMakeCache.txt. Visual Studio generators do not reliably
    rem expose that variable there; compiler metadata is normally stored under CMakeFiles.
    rem A mismatched copied cache is reset, while a valid VS x64 cache stays incremental.
    set "GLIDE_NATIVE_CACHE=native\Glide.Native\build\CMakeCache.txt"
    if exist "!GLIDE_NATIVE_CACHE!" (
      set "GLIDE_CACHED_GENERATOR="
      set "GLIDE_CACHED_PLATFORM="
      for /f "tokens=1,* delims==" %%A in ('findstr /b /c:"CMAKE_GENERATOR:INTERNAL=" "!GLIDE_NATIVE_CACHE!" 2^>nul') do set "GLIDE_CACHED_GENERATOR=%%B"
      for /f "tokens=1,* delims==" %%A in ('findstr /b /c:"CMAKE_GENERATOR_PLATFORM:INTERNAL=" "!GLIDE_NATIVE_CACHE!" 2^>nul') do set "GLIDE_CACHED_PLATFORM=%%B"
      set "GLIDE_RESET_NATIVE_CACHE=0"
      if defined GLIDE_CACHED_GENERATOR (
        echo !GLIDE_CACHED_GENERATOR! | findstr /i /c:"Visual Studio" >nul
        if errorlevel 1 set "GLIDE_RESET_NATIVE_CACHE=1"
      )
      if defined GLIDE_CACHED_PLATFORM if /I not "!GLIDE_CACHED_PLATFORM!"=="x64" set "GLIDE_RESET_NATIVE_CACHE=1"
      if "!GLIDE_RESET_NATIVE_CACHE!"=="1" (
        echo [1/8] Native CMake cache targets a different generator/platform; resetting it...
        rmdir /s /q "native\Glide.Native\build"
      )
    )

    echo [1/8] Configuring/refreshing native bridge for x64...
    cmake -S native\Glide.Native -B native\Glide.Native\build -A x64
    if errorlevel 1 (
      echo ERROR: Native configure failed. If Visual Studio Build Tools was recently upgraded, delete native\Glide.Native\build and rerun.
      set "GLIDE_BUILD_RC=!errorlevel!"
      goto :fail
    )

    rem A successful Visual Studio configure already proves that CMake resolved the
    rem requested x64 toolchain. The following build is the authoritative compiler/linker
    rem validation and gives the real MSVC diagnostic if that toolchain is incomplete.
    echo       CMake accepted the Visual Studio x64 native toolchain.

    call :progress 18 "Native incremental build"
    echo [2/8] Building native bridge ^(parallel, incremental^)...
    cmake --build native\Glide.Native\build --config Release --parallel %NUMBER_OF_PROCESSORS%
    if errorlevel 1 (
      set "GLIDE_BUILD_RC=!errorlevel!"
      goto :fail
    )
    if not exist "native\Glide.Native\build\Release\Glide.Native.dll" (
      echo ERROR: Native Release build completed without native\Glide.Native\build\Release\Glide.Native.dll.
      set "GLIDE_BUILD_RC=1"
      goto :fail
    )
  )
)

call :progress 32 "Managed restore/build"
echo [3/8] Restoring and building managed solution ^(incremental / multiprocess^)...
dotnet restore Glide.sln
if errorlevel 1 (
  set "GLIDE_BUILD_RC=!errorlevel!"
  goto :fail
)
dotnet build Glide.sln -c Release --no-restore -m -p:BuildInParallel=true
if errorlevel 1 (
  set "GLIDE_BUILD_RC=!errorlevel!"
  goto :fail
)

if "%GLIDE_FAST%"=="1" (
  call :progress 52 "Fast mode: validation skipped"
  echo [4/8] Core tests skipped in fast mode.
  echo [5/8] Input tests skipped in fast mode.
  echo [6/8] Headless diagnostics skipped in fast mode.
) else (
  call :progress 48 "Core + Input tests (parallel)"
  echo [4-5/8] Running Core and Input test projects in parallel...
  dotnet test Glide.sln -c Release --no-build -m -p:BuildInParallel=true
  if errorlevel 1 (
    set "GLIDE_BUILD_RC=!errorlevel!"
    goto :fail
  )

  call :progress 66 "Headless diagnostics"
  echo [6/8] Running headless diagnostics self-test...
  dotnet run --project src\Glide.App\Glide.App.csproj -c Release --no-build -- --diagnostics self-test
  if errorlevel 1 (
    set "GLIDE_BUILD_RC=!errorlevel!"
    goto :fail
  )
)

call :progress 78 "Publishing"
echo [7/8] Publishing...
if not "%GLIDE_FAST%"=="1" if exist dist rmdir /s /q dist
if "%GLIDE_FAST%"=="1" if not exist dist mkdir dist
dotnet publish src\Glide.App\Glide.App.csproj -c Release -r win-x64 --self-contained false -o dist
if errorlevel 1 (
  set "GLIDE_BUILD_RC=!errorlevel!"
  goto :fail
)

if not exist "native\Glide.Native\build\Release\Glide.Native.dll" (
  echo ERROR: Required native bridge missing; refusing to produce a TIFF-capable Glide release.
  set "GLIDE_BUILD_RC=1"
  goto :fail
)
copy /y "native\Glide.Native\build\Release\Glide.Native.dll" "dist\Glide.Native.dll" >nul
if not exist "dist\Glide.Native.dll" (
  echo ERROR: Native bridge copy to dist failed.
  set "GLIDE_BUILD_RC=1"
  goto :fail
)
call :progress 90 "Diagnostic fixtures"
if "%GLIDE_FAST%"=="1" (
  powershell -NoLogo -NoProfile -ExecutionPolicy Bypass -File "tools\fixture-manifest.ps1" -Mode Validate -Directory "artifacts\diagnostic-fixtures" -Generator "dist\Glide.exe" -SourceIdentity "Glide-3.5" >nul 2>&1
  if errorlevel 1 (
    echo [8/8] Fast fixture cache is absent/stale/partial; regenerating...
    call :generate_diagnostic_fixtures
    if errorlevel 1 goto :fail
  ) else (
    echo [8/8] Reusing manifest-validated diagnostic fixtures in fast mode...
  )
) else (
  echo [8/8] Normal build: regenerating diagnostic fixtures from this exact candidate...
  call :generate_diagnostic_fixtures
  if errorlevel 1 goto :fail
)

rem Optional decoder-provider payloads are copied when present; no runtime download.
if exist codecs xcopy /e /i /y /q codecs dist\codecs >nul

if /I "%GLIDE_SKIP_INSTALLER%"=="1" (
  call :progress 94 "Installer package skipped"
  echo [9/9] Installer build skipped for nested/portable build.
) else (
  call :progress 94 "Installer package"
  echo [9/9] Building Inno Setup installer...
  set "GLIDE_INSTALLER_PARENT=1"
  call build-installer.cmd --from-build
  set "GLIDE_INSTALLER_PARENT="
  if errorlevel 1 (
    set "GLIDE_BUILD_RC=!errorlevel!"
    goto :fail
  )
)

call :progress 100 "Complete"
echo.
echo ============================================================
echo   BUILD COMPLETE - portable: dist\
if /I "%GLIDE_SKIP_INSTALLER%"=="1" (
  echo   INSTALLER: skipped by caller
) else (
  echo   INSTALLER: dist-installer\Glide Setup.exe
)
echo ============================================================
echo Run: dist\Glide.exe
exit /b 0

:generate_diagnostic_fixtures
if not exist artifacts mkdir artifacts
if exist "artifacts\diagnostic-fixtures" rmdir /s /q "artifacts\diagnostic-fixtures"
dist\Glide.exe --write-diagnostic-fixtures "artifacts\diagnostic-fixtures"
if errorlevel 1 (
  set "GLIDE_BUILD_RC=!errorlevel!"
  exit /b !GLIDE_BUILD_RC!
)
if not exist "artifacts\diagnostic-fixtures" (
  echo ERROR: Diagnostic fixture generation returned success but produced no output directory.
  set "GLIDE_BUILD_RC=1"
  exit /b 1
)
powershell -NoLogo -NoProfile -ExecutionPolicy Bypass -File "tools\fixture-manifest.ps1" -Mode Write -Directory "artifacts\diagnostic-fixtures" -Generator "dist\Glide.exe" -SourceIdentity "Glide-3.5"
if errorlevel 1 (
  echo ERROR: Diagnostic fixture identity manifest creation/validation failed.
  set "GLIDE_BUILD_RC=!errorlevel!"
  exit /b !GLIDE_BUILD_RC!
)
powershell -NoLogo -NoProfile -ExecutionPolicy Bypass -File "tools\fixture-manifest.ps1" -Mode Validate -Directory "artifacts\diagnostic-fixtures" -Generator "dist\Glide.exe" -SourceIdentity "Glide-3.5"
if errorlevel 1 (
  echo ERROR: Generated diagnostic fixtures did not pass identity validation.
  set "GLIDE_BUILD_RC=!errorlevel!"
  exit /b !GLIDE_BUILD_RC!
)
exit /b 0

:progress
set "GLIDE_PROGRESS=%~1"
set "GLIDE_PROGRESS_LABEL=%~2"
title Glide 3.5 Build - %GLIDE_PROGRESS%%% - %GLIDE_PROGRESS_LABEL%
echo [ %GLIDE_PROGRESS%%% ] %GLIDE_PROGRESS_LABEL%
exit /b 0

:fail
if not defined GLIDE_BUILD_RC set "GLIDE_BUILD_RC=1"
echo.
echo ============================================================
echo   BUILD FAILED - error code %GLIDE_BUILD_RC%
echo ============================================================
echo.
echo The window will stay open so the error output can be copied.
echo Run build.cmd --no-pause from an existing terminal/CI to disable this pause.
if not "%GLIDE_NO_PAUSE%"=="1" pause
exit /b %GLIDE_BUILD_RC%
