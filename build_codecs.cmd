@echo off
setlocal EnableExtensions EnableDelayedExpansion
cd /d "%~dp0"

echo ==============================================
echo   Glide Alpha 0.12107 - Native Offline Codec Builder
echo ==============================================
echo.

rem Native decoder-only codec build. No ImageMagick, no PowerShell, no network.
rem All upstream sources are already bundled as pinned archives under codec_sources.

where cmake.exe >nul 2>nul || (echo ERROR: CMake was not found in PATH.& exit /b 1)
where tar.exe >nul 2>nul || (echo ERROR: Windows tar.exe was not found.& exit /b 1)
where cl.exe >nul 2>nul || (echo ERROR: MSVC x64 environment is not initialized.& exit /b 1)
where nmake.exe >nul 2>nul || (echo ERROR: nmake.exe was not found in the active MSVC environment.& exit /b 1)

set "ARCH=%~dp0codec_sources"
set "SRC=%~dp0codec_native_src"
set "BUILD=%~dp0codec_native_build"
set "OUT=%~dp0prebuilt_codecs"
set "READY=%SRC%\.glide_native_sources_v1"
if not exist "%READY%" if exist "%SRC%\.glide_native_sources_ready" copy /y "%SRC%\.glide_native_sources_ready" "%READY%" >nul

where certutil.exe >nul 2>nul || (echo ERROR: certutil.exe was not found; source integrity cannot be verified.& exit /b 1)
if not exist "%ARCH%\SHA256SUMS.txt" (echo ERROR: Missing codec source hash manifest.& exit /b 1)

for %%F in (
  libwebp-v1.6.0.tar.gz
  libheif-v1.23.4.tar.gz
  libde265-v1.1.1.tar.gz
  dav1d-1.5.1.tar.xz
  libjxl-v0.12.0.tar.gz
  openjpeg-v2.5.3.tar.gz
  tinyexr-minimal-3.2.0.tar.gz
  highway-457c891775a7397bdb0376bb1031e6e027af1c48.tar.gz
  brotli-028fb5a23661f123017c060daa546b55cf4bde29.tar.gz
  skcms-minimal-96d9171.tar.gz
) do if not exist "%ARCH%\%%F" (
  echo ERROR: Missing bundled codec source archive: %%F
  exit /b 1
)

echo [codecs] Verifying pinned source archive hashes...
for /f "usebackq tokens=1,2" %%H in ("%ARCH%\SHA256SUMS.txt") do (
  set "EXPECTED=%%H"
  set "HASHFILE=%%I"
  set "HASHFILE=!HASHFILE:~1!"
  set "ACTUAL="
  for /f "skip=1 tokens=*" %%A in ('certutil -hashfile "%ARCH%\!HASHFILE!" SHA256') do if not defined ACTUAL set "ACTUAL=%%A"
  set "ACTUAL=!ACTUAL: =!"
  if /I not "!ACTUAL!"=="!EXPECTED!" (echo ERROR: SHA-256 mismatch for !HASHFILE!& exit /b 1)
)

if not exist "%READY%" (
  echo [codecs] Extracting pinned decoder sources...
  if exist "%SRC%" rmdir /s /q "%SRC%"
  mkdir "%SRC%" || exit /b 1
  for %%F in (
    libwebp-v1.6.0.tar.gz
    libheif-v1.23.4.tar.gz
    libde265-v1.1.1.tar.gz
    dav1d-1.5.1.tar.xz
    libjxl-v0.12.0.tar.gz
    openjpeg-v2.5.3.tar.gz
    tinyexr-minimal-3.2.0.tar.gz
    highway-457c891775a7397bdb0376bb1031e6e027af1c48.tar.gz
    brotli-028fb5a23661f123017c060daa546b55cf4bde29.tar.gz
    skcms-minimal-96d9171.tar.gz
  ) do (
    tar.exe -xf "%ARCH%\%%F" -C "%SRC%"
    if errorlevel 1 (
      echo ERROR: Failed to extract %%F
      exit /b 1
    )
  )

  rem libjxl release archives intentionally omit git submodule contents. Populate
  rem only the three source dependencies used by Glide's decoder-only build.
  if exist "%SRC%\libjxl-0.12.0\third_party\brotli" rmdir /s /q "%SRC%\libjxl-0.12.0\third_party\brotli"
  if exist "%SRC%\libjxl-0.12.0\third_party\highway" rmdir /s /q "%SRC%\libjxl-0.12.0\third_party\highway"
  if exist "%SRC%\libjxl-0.12.0\third_party\skcms" rmdir /s /q "%SRC%\libjxl-0.12.0\third_party\skcms"
  xcopy "%SRC%\brotli-028fb5a23661f123017c060daa546b55cf4bde29" "%SRC%\libjxl-0.12.0\third_party\brotli\" /E /I /Q /Y >nul
  if errorlevel 2 exit /b 1
  xcopy "%SRC%\highway-457c891775a7397bdb0376bb1031e6e027af1c48" "%SRC%\libjxl-0.12.0\third_party\highway\" /E /I /Q /Y >nul
  if errorlevel 2 exit /b 1
  xcopy "%SRC%\skcms" "%SRC%\libjxl-0.12.0\third_party\skcms\" /E /I /Q /Y >nul
  if errorlevel 2 exit /b 1

  >"%READY%" echo Glide native codec sources - hashes verified and JXL dependencies staged
) else (
  echo [codecs] Reusing already-extracted pinned sources.
)

if not exist "%SRC%\libjxl-0.12.0\third_party\brotli\CMakeLists.txt" (
  echo ERROR: libjxl Brotli dependency was not staged correctly.
  exit /b 1
)
if not exist "%SRC%\libjxl-0.12.0\third_party\highway\CMakeLists.txt" (
  echo ERROR: libjxl Highway dependency was not staged correctly.
  exit /b 1
)
if not exist "%SRC%\libjxl-0.12.0\third_party\skcms\skcms.cc" (
  echo ERROR: libjxl skcms dependency was not staged correctly.
  exit /b 1
)

rem Keep repeat Glide builds fast, but never let a stale CMake cache hide
rem integration changes. Hash the active native CMakeLists and reconfigure in-place
rem when it changes. Reconfiguration preserves compiled objects and is far cheaper
rem than deleting the codec build tree.
set "CONFIGSTAMP=%BUILD%\.glide_cmake_sha256"
set "CMAKEHASH="
for /f "skip=1 tokens=*" %%A in ('certutil -hashfile "%~dp0native_codecs\CMakeLists.txt" SHA256') do if not defined CMAKEHASH set "CMAKEHASH=%%A"
set "CMAKEHASH=!CMAKEHASH: =!"
set "OLDHASH="
if exist "%CONFIGSTAMP%" set /p OLDHASH=<"%CONFIGSTAMP%"
if /I not "!OLDHASH!"=="!CMAKEHASH!" (
  echo [codecs] Native integration changed; refreshing CMake configuration...
  if not exist "%BUILD%" mkdir "%BUILD%"
  cmake.exe -S "%~dp0native_codecs" -B "%BUILD%" -G "NMake Makefiles" ^
    -DCMAKE_BUILD_TYPE=Release ^
    -DGLIDE_CODEC_VENDOR_DIR="%SRC%"
  if errorlevel 1 (
    echo ERROR: Native codec CMake configuration failed.
    exit /b 1
  )
  >"%CONFIGSTAMP%" echo !CMAKEHASH!
) else (
  echo [codecs] Reusing matching native codec CMake configuration.
)

echo [codecs] Building native decoder bridge...
cmake.exe --build "%BUILD%" --target GlideCodecBridge
if errorlevel 1 (
  echo ERROR: Native codec compilation failed.
  exit /b 1
)

if not exist "%BUILD%\runtime\GlideCodecBridge.dll" (
  echo ERROR: GlideCodecBridge.dll was not produced.
  exit /b 1
)

if exist "%OUT%" rmdir /s /q "%OUT%"
mkdir "%OUT%" || exit /b 1
copy /y "%BUILD%\runtime\*.dll" "%OUT%\" >nul
if errorlevel 1 (
  echo ERROR: Failed to stage native codec DLLs.
  exit /b 1
)
if exist "%BUILD%\runtime\GLIDE_NATIVE_CODEC_BUILD.txt" copy /y "%BUILD%\runtime\GLIDE_NATIVE_CODEC_BUILD.txt" "%OUT%\" >nul
mkdir "%OUT%\licenses" >nul 2>nul

rem Runtime notices. Text is tiny and keeps the modular LGPL components clearly
rem replaceable and attributable while the permissive decoders remain in the bridge.
if exist "%SRC%\libheif-1.23.4\COPYING" copy /y "%SRC%\libheif-1.23.4\COPYING" "%OUT%\licenses\libheif-LGPL.txt" >nul
if exist "%SRC%\libde265-1.1.1\COPYING" copy /y "%SRC%\libde265-1.1.1\COPYING" "%OUT%\licenses\libde265-LGPL.txt" >nul
if exist "%SRC%\libwebp-1.6.0\COPYING" copy /y "%SRC%\libwebp-1.6.0\COPYING" "%OUT%\licenses\libwebp.txt" >nul
if exist "%SRC%\libjxl-0.12.0\LICENSE" copy /y "%SRC%\libjxl-0.12.0\LICENSE" "%OUT%\licenses\libjxl.txt" >nul
if exist "%SRC%\openjpeg-2.5.3\LICENSE" copy /y "%SRC%\openjpeg-2.5.3\LICENSE" "%OUT%\licenses\OpenJPEG.txt" >nul
if exist "%SRC%\tinyexr\LICENSE" copy /y "%SRC%\tinyexr\LICENSE" "%OUT%\licenses\TinyEXR.txt" >nul
if exist "%SRC%\dav1d-1.5.1\COPYING" copy /y "%SRC%\dav1d-1.5.1\COPYING" "%OUT%\licenses\dav1d.txt" >nul
if exist "%SRC%\highway-457c891775a7397bdb0376bb1031e6e027af1c48\LICENSE" copy /y "%SRC%\highway-457c891775a7397bdb0376bb1031e6e027af1c48\LICENSE" "%OUT%\licenses\Highway.txt" >nul
if exist "%SRC%\brotli-028fb5a23661f123017c060daa546b55cf4bde29\LICENSE" copy /y "%SRC%\brotli-028fb5a23661f123017c060daa546b55cf4bde29\LICENSE" "%OUT%\licenses\Brotli.txt" >nul
if exist "%SRC%\skcms\LICENSE" copy /y "%SRC%\skcms\LICENSE" "%OUT%\licenses\skcms.txt" >nul
if exist "%SRC%\tinyexr\deps\miniz\LICENSE" copy /y "%SRC%\tinyexr\deps\miniz\LICENSE" "%OUT%\licenses\miniz.txt" >nul

for /f %%N in ('dir /a-d /s /b "%OUT%" 2^>nul ^| find /c /v ""') do set "FILECOUNT=%%N"
set /a TOTALBYTES=0
for /r "%OUT%" %%F in (*) do set /a TOTALBYTES+=%%~zF

echo [codecs] Runtime staged: !FILECOUNT! files, !TOTALBYTES! bytes.
echo [codecs] WebP / HEIF / AVIF / JXL / JPEG 2000 / EXR native bridge ready.
echo [codecs] No network access or downloaded executable was used.
exit /b 0
