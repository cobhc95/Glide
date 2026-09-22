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
rem Single-source the product version from Directory.Build.props so build banners, deliverable
rem names and fixture identities can never drift from the assembly metadata again.
set "GLIDE_VERSION=4.2.2"
for /f "tokens=3 delims=<>" %%V in ('findstr /r /c:"^ *<Version>" "Directory.Build.props"') do set "GLIDE_VERSION=%%V"
title Glide %GLIDE_VERSION%

set "GLIDE_AOT=0"
if /I "%GLIDE_FORCE_FAST%"=="1" (set "GLIDE_FAST=1") else (set "GLIDE_FAST=0")
set "GLIDE_NO_PAUSE=0"
set "DOTNET_CLI_TELEMETRY_OPTOUT=1"
set "DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1"
set "NUGET_XMLDOC_MODE=skip"
set "GLIDE_BUILD_RC="

rem The solution is authored for Any CPU. Normalize these properties so an inherited
rem developer-shell Platform/Configuration value cannot select the invalid Debug|x64
rem solution configuration during restore, build or test.
set "Platform=Any CPU"
set "Configuration=Release"

:parse_args
if "%~1"=="" goto :args_done
if /I "%~1"=="aot" set "GLIDE_AOT=1"
if /I "%~1"=="fast" set "GLIDE_FAST=1"
if /I "%~1"=="--no-pause" set "GLIDE_NO_PAUSE=1"
shift
goto :parse_args

:args_done
rem Installer/diagnostic builds may force fast mode through the environment.
rem This avoids argument-forwarding quirks through the PowerShell live-log wrapper.
if /I "%GLIDE_FORCE_FAST%"=="1" set "GLIDE_FAST=1"
echo.
echo ============================================================
echo   Glide %GLIDE_VERSION%
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
echo Cleaning generated build outputs and caches...
for %%D in (".artifacts" "artifacts\diagnostic-fixtures" "native\Glide.Native\build" "dist" "dist-fixed" "dist-installer") do (
  if exist "%%~D" rmdir /s /q "%%~D"
  if exist "%%~D" (
    echo ERROR: Could not remove generated directory %%~D. Close any running Glide/build process and retry.
    set "GLIDE_BUILD_RC=1"
    goto :fail
  )
)
if exist "codecs\codecs.index.json" del /q "codecs\codecs.index.json"
rem Remove every prior portable deliverable (any version) so stale, mislabeled archives can
rem never accumulate next to the current release.
del /q "Glide-*.exe" >nul 2>nul
del /q "Glide-*-Portable.zip" >nul 2>nul
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

    rem The Visual Studio generator is the normal Windows path. Some managed developer
    rem environments expose PATH and Path as duplicate case variants; MSBuild then refuses
    rem to launch CL.exe. Allow the caller to select the installed Ninja toolchain for that
    rem environment without changing the shipped native target or output layout.
    set "GLIDE_NATIVE_NINJA=0"
    if /I "%GLIDE_NATIVE_GENERATOR%"=="Ninja" set "GLIDE_NATIVE_NINJA=1"
    if "!GLIDE_NATIVE_NINJA!"=="1" (
      where ninja >nul 2>nul
      if errorlevel 1 (
        echo ERROR: GLIDE_NATIVE_GENERATOR=Ninja was requested but ninja.exe was not found on PATH.
        set "GLIDE_BUILD_RC=1"
        goto :fail
      )
    )

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
        if "!GLIDE_NATIVE_NINJA!"=="1" (
          if /I not "!GLIDE_CACHED_GENERATOR!"=="Ninja" set "GLIDE_RESET_NATIVE_CACHE=1"
        ) else (
          echo !GLIDE_CACHED_GENERATOR! | findstr /i /c:"Visual Studio" >nul
          if errorlevel 1 set "GLIDE_RESET_NATIVE_CACHE=1"
        )
      )
      if "!GLIDE_NATIVE_NINJA!"=="0" if defined GLIDE_CACHED_PLATFORM if /I not "!GLIDE_CACHED_PLATFORM!"=="x64" set "GLIDE_RESET_NATIVE_CACHE=1"
      if "!GLIDE_RESET_NATIVE_CACHE!"=="1" (
        echo [1/8] Native CMake cache targets a different generator/platform; resetting it...
        rmdir /s /q "native\Glide.Native\build"
      )
    )

    echo [1/8] Configuring/refreshing native bridge for x64...
    if "!GLIDE_NATIVE_NINJA!"=="1" (
      cmake -G Ninja -S native\Glide.Native -B native\Glide.Native\build -DCMAKE_BUILD_TYPE=Release
    ) else (
      cmake -S native\Glide.Native -B native\Glide.Native\build -A x64
    )
    if errorlevel 1 (
      echo ERROR: Native configure failed. If Visual Studio Build Tools was recently upgraded, delete native\Glide.Native\build and rerun.
      set "GLIDE_BUILD_RC=!errorlevel!"
      goto :fail
    )

    rem A successful Visual Studio configure already proves that CMake resolved the
    rem requested x64 toolchain. The following build is the authoritative compiler/linker
    rem validation and gives the real MSVC diagnostic if that toolchain is incomplete.
    if "!GLIDE_NATIVE_NINJA!"=="1" (echo       CMake accepted the Ninja x64 native toolchain.) else (echo       CMake accepted the Visual Studio x64 native toolchain.)

    call :progress 18 "Native incremental build"
    echo [2/8] Building native bridge ^(parallel, incremental^)...
    cmake --build native\Glide.Native\build --config Release --parallel %NUMBER_OF_PROCESSORS%
    if errorlevel 1 (
      set "GLIDE_BUILD_RC=!errorlevel!"
      goto :fail
    )
    rem Resolve the native DLL from the actual CMake output directory. Different
    rem generators/toolchains may place Release output under build\Release or
    rem directly under build. Do not fail a successful link because of layout.
    set "GLIDE_NATIVE_DLL="
    if exist "native\Glide.Native\build\Release\Glide.Native.dll" set "GLIDE_NATIVE_DLL=%CD%\native\Glide.Native\build\Release\Glide.Native.dll"
    if not defined GLIDE_NATIVE_DLL if exist "native\Glide.Native\build\Glide.Native.dll" set "GLIDE_NATIVE_DLL=%CD%\native\Glide.Native\build\Glide.Native.dll"
    if not defined GLIDE_NATIVE_DLL (
      for /f "delims=" %%F in ('dir /b /s /a-d "native\Glide.Native\build\Glide.Native.dll" 2^>nul') do if not defined GLIDE_NATIVE_DLL set "GLIDE_NATIVE_DLL=%%~fF"
    )
    if not defined GLIDE_NATIVE_DLL (
      echo ERROR: Native build reported success but Glide.Native.dll could not be located under native\Glide.Native\build.
      set "GLIDE_BUILD_RC=1"
      goto :fail
    )
    echo       Native bridge: !GLIDE_NATIVE_DLL!
  )
)

if exist native\Glide.ShellThumbnail\CMakeLists.txt (
  where cmake >nul 2>nul
  if errorlevel 1 (
    echo ERROR: CMake is required for the Explorer thumbnail provider.
    set "GLIDE_BUILD_RC=1"
    goto :fail
  ) else (
    call :progress 24 "Explorer thumbnail provider"
    echo [2b/8] Building Explorer thumbnail provider ^(parallel, incremental^)...
    cmake -S native\Glide.ShellThumbnail -B native\Glide.ShellThumbnail\build -A x64
    if errorlevel 1 (
      echo ERROR: Explorer thumbnail provider configure failed.
      set "GLIDE_BUILD_RC=!errorlevel!"
      goto :fail
    )
    cmake --build native\Glide.ShellThumbnail\build --config Release --parallel %NUMBER_OF_PROCESSORS%
    if errorlevel 1 (
      echo ERROR: Explorer thumbnail provider build failed.
      set "GLIDE_BUILD_RC=!errorlevel!"
      goto :fail
    )
    if not exist "native\Glide.ShellThumbnail\build\Release\Glide.ShellThumbnail.dll" (
      echo ERROR: Explorer thumbnail provider reported success but Glide.ShellThumbnail.dll is missing.
      set "GLIDE_BUILD_RC=1"
      goto :fail
    )
    echo       Thumbnail provider: native\Glide.ShellThumbnail\build\Release\Glide.ShellThumbnail.dll
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

if not defined GLIDE_NATIVE_DLL (
  if exist "native\Glide.Native\build\Release\Glide.Native.dll" set "GLIDE_NATIVE_DLL=%CD%\native\Glide.Native\build\Release\Glide.Native.dll"
  if not defined GLIDE_NATIVE_DLL if exist "native\Glide.Native\build\Glide.Native.dll" set "GLIDE_NATIVE_DLL=%CD%\native\Glide.Native\build\Glide.Native.dll"
  if not defined GLIDE_NATIVE_DLL for /f "delims=" %%F in ('dir /b /s /a-d "native\Glide.Native\build\Glide.Native.dll" 2^>nul') do if not defined GLIDE_NATIVE_DLL set "GLIDE_NATIVE_DLL=%%~fF"
)
if not defined GLIDE_NATIVE_DLL (
  echo ERROR: Required native bridge missing; refusing to produce a TIFF-capable Glide release.
  set "GLIDE_BUILD_RC=1"
  goto :fail
)
copy /y "!GLIDE_NATIVE_DLL!" "dist\Glide.Native.dll" >nul
if not exist "dist\Glide.Native.dll" (
  echo ERROR: Native bridge copy to dist failed.
  set "GLIDE_BUILD_RC=1"
  goto :fail
)

rem Explorer thumbnail provider ships alongside the native bridge. It is optional at runtime: the app
rem simply reports the provider as unavailable when the DLL is absent.
if exist "native\Glide.ShellThumbnail\build\Release\Glide.ShellThumbnail.dll" (
  copy /y "native\Glide.ShellThumbnail\build\Release\Glide.ShellThumbnail.dll" "dist\Glide.ShellThumbnail.dll" >nul
  echo       Thumbnail provider staged in dist.
) else (
  echo WARNING: Glide.ShellThumbnail.dll was not built; Explorer thumbnails will be unavailable.
)

rem Release deliverables: a clean portable archive and a directly testable EXE.
if exist "Glide-%GLIDE_VERSION%-Portable.zip" del /q "Glide-%GLIDE_VERSION%-Portable.zip"
if exist "Glide-%GLIDE_VERSION%.exe" del /q "Glide-%GLIDE_VERSION%.exe"
copy /y "dist\Glide.exe" "Glide-%GLIDE_VERSION%.exe" >nul
powershell -NoLogo -NoProfile -ExecutionPolicy Bypass -Command "Compress-Archive -Path 'dist\*' -DestinationPath 'Glide-%GLIDE_VERSION%-Portable.zip' -CompressionLevel Optimal"
if errorlevel 1 (
  echo ERROR: Portable ZIP creation failed.
  set "GLIDE_BUILD_RC=!errorlevel!"
  goto :fail
)
call :progress 90 "Diagnostic fixtures"
if "%GLIDE_FAST%"=="1" (
  powershell -NoLogo -NoProfile -ExecutionPolicy Bypass -File "tools\fixture-manifest.ps1" -Mode Validate -Directory "artifacts\diagnostic-fixtures" -Generator "dist\Glide.exe" -SourceIdentity "Glide-%GLIDE_VERSION%" >nul 2>&1
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

echo Packaging clean zero-context handover zip...
powershell -NoLogo -NoProfile -ExecutionPolicy Bypass -File "package-source.ps1"
if errorlevel 1 (
  echo WARNING: package-source.ps1 failed, continuing build completion.
)

call :progress 100 "Complete"
echo.
echo ============================================================
echo   BUILD COMPLETE - portable: dist\
echo   PORTABLE ZIP: Glide-%GLIDE_VERSION%-Portable.zip
echo   EXE:          Glide-%GLIDE_VERSION%.exe
if /I "%GLIDE_SKIP_INSTALLER%"=="1" (
  echo   INSTALLER: skipped by caller
) else (
  echo   INSTALLER: dist-installer\Glide Setup.exe
)
echo   HANDOVER:  Glide-Zero-Context-Handover.zip
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
powershell -NoLogo -NoProfile -ExecutionPolicy Bypass -File "tools\fixture-manifest.ps1" -Mode Write -Directory "artifacts\diagnostic-fixtures" -Generator "dist\Glide.exe" -SourceIdentity "Glide-%GLIDE_VERSION%"
if errorlevel 1 (
  echo ERROR: Diagnostic fixture identity manifest creation/validation failed.
  set "GLIDE_BUILD_RC=!errorlevel!"
  exit /b !GLIDE_BUILD_RC!
)
powershell -NoLogo -NoProfile -ExecutionPolicy Bypass -File "tools\fixture-manifest.ps1" -Mode Validate -Directory "artifacts\diagnostic-fixtures" -Generator "dist\Glide.exe" -SourceIdentity "Glide-%GLIDE_VERSION%"
if errorlevel 1 (
  echo ERROR: Generated diagnostic fixtures did not pass identity validation.
  set "GLIDE_BUILD_RC=!errorlevel!"
  exit /b !GLIDE_BUILD_RC!
)
exit /b 0

:progress
set "GLIDE_PROGRESS=%~1"
set "GLIDE_PROGRESS_LABEL=%~2"
title Glide %GLIDE_VERSION% Build - %GLIDE_PROGRESS%%% - %GLIDE_PROGRESS_LABEL%
echo [ %GLIDE_PROGRESS%%% ] %GLIDE_PROGRESS_LABEL%
exit /b 0

:clean_generated
echo Cleaning generated build outputs and caches...
for %%D in (".artifacts" "artifacts\diagnostic-fixtures" "native\Glide.Native\build" "dist" "dist-fixed" "dist-installer") do (
  if exist "%%~D" rmdir /s /q "%%~D"
  if exist "%%~D" (
    echo ERROR: Could not remove generated directory %%~D. Close any running Glide/build process and retry.
    exit /b 1
  )
)
if exist "codecs\codecs.index.json" del /q "codecs\codecs.index.json"
rem Remove every prior portable deliverable (any version) so stale, mislabeled archives can
rem never accumulate next to the current release.
del /q "Glide-*.exe" >nul 2>nul
del /q "Glide-*-Portable.zip" >nul 2>nul
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
