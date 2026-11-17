# Builds Glide's bundled optional codec provider (codecs\glidecodecs.dll + glidecodecs.glidecodec.json).
#
# One statically linked, size-optimised ABI v2 provider for:
#   AVIF/AVIFS  libheif + dav1d          HEIC/HEIF  libheif + libde265 (optional, -NoHevc drops it)
#   JPEG XL     libjxl (decoder only)    JPEG 2000  OpenJPEG (decoder, plus encoder for Save as)
#   WebP        vendored libwebp 1.5.0 (native\third_party\libwebp; encoder only, for Save as)
#
# Sources are fetched from the official upstream git repositories at pinned commits; git verifies
# every object against the pinned commit hash, so a moved tag or tampered mirror fails the fetch.
# NASM (dav1d x86 assembly) is a pinned, SHA-256-verified download from nasm.us. Meson runs from a
# private venv. Everything is cached under %LOCALAPPDATA%\Glide-build-cache\codecs so rebuilds are
# offline and incremental. Sources never enter the repository; only native\Glide.Codecs (glue) does.
#
#   powershell -ExecutionPolicy Bypass -File tools\build-codecs.ps1            # build if stale
#   ... -Force                 rebuild every dependency from scratch
#   ... -NoHevc                build without libde265 (drops .heic/.heif/.heics/.heifs/.hif)
#   ... -NoAsm                 build dav1d without NASM assembly (smaller, slower AV1)
#   ... -Dav1dBitdepths 8      8-bit AV1 only (smaller; 10/12-bit AVIF then fails to decode)
#   ... -MapFile               also write native\Glide.Codecs\build\glidecodecs.map for size audits
#   ... -Optimize size         former size-first /O1 build (default speed: /O2, 5.0.7)
#   ... -MaxThreads <n>        cap decoder worker threads (default 64 = every logical processor)
#   ... -OutDir <dir>          write the DLL/manifest/NOTICE somewhere other than codecs\ (experiments)
param(
    [switch]$Force,
    [switch]$NoHevc,
    [switch]$NoAsm,
    [ValidateSet('8', '8,16')][string]$Dav1dBitdepths = '8,16',
    [switch]$MapFile,
    [ValidateSet('speed', 'size')][string]$Optimize = 'speed',
    [int]$MaxThreads = 64,
    [string]$OutDir,
    [switch]$FetchOnly
)
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

# ---- Pins -----------------------------------------------------------------------------------------
$Sources = [ordered]@{
    dav1d    = @{ Url = 'https://code.videolan.org/videolan/dav1d.git'; Mirror = 'https://github.com/videolan/dav1d.git'; Tag = '1.5.4';   Commit = '54706fc6bc0cdecab7e9593974a4039cc038fca7' }
    libde265 = @{ Url = 'https://github.com/strukturag/libde265.git'; Tag = 'v1.1.3';  Commit = 'ba62bf4cfb3242f3bf0a45617ff09e35236e4d82' }
    libheif  = @{ Url = 'https://github.com/strukturag/libheif.git';  Tag = 'v1.23.7'; Commit = '12a259a5882dd99d45ec5fc77e296a3b620d1e2e' }
    libjxl   = @{ Url = 'https://github.com/libjxl/libjxl.git';       Tag = 'v0.13.0'; Commit = '5a7da83447212fe4900fc29ce97430b881caa67e'; Submodules = @('third_party/highway', 'third_party/brotli', 'third_party/skcms') }
    openjpeg = @{ Url = 'https://github.com/uclouvain/openjpeg.git';  Tag = 'v2.5.4';  Commit = '6c4a29b00211eb0430fa0e5e890f1ce5c80f409f' }
}
$NasmVersion = '2.16.03'
$NasmSha256  = '3ee4782247bcb874378d02f7eab4e294a84d3d15f3f6ee2de2f47a46aa7226e6'
$MesonVersion = '1.12.1'
$NinjaVersion = '1.13.2'

$root = Split-Path -Parent $PSScriptRoot
$cache = Join-Path $env:LOCALAPPDATA 'Glide-build-cache\codecs'
$srcRoot = Join-Path $cache 'src'
New-Item -ItemType Directory -Force $srcRoot | Out-Null

function Invoke-Native([string]$exe, [string[]]$arguments, [string]$workDir = $null) {
    # Native tools write progress to stderr; Windows PowerShell 5.1 would turn that into terminating
    # errors under 'Stop'. Judge success by exit code only, and keep tool output off the pipeline.
    if ($workDir) { Push-Location $workDir }
    $previous = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        & $exe @arguments 2>&1 | ForEach-Object { "$_" } | Out-Host
        if ($LASTEXITCODE -ne 0) { throw "$exe $($arguments -join ' ') failed with exit code $LASTEXITCODE" }
    } finally {
        $ErrorActionPreference = $previous
        if ($workDir) { Pop-Location }
    }
}

function Get-PinnedSource([string]$name) {
    $pin = $Sources[$name]
    $dir = Join-Path $srcRoot ("$name-" + $pin.Commit.Substring(0, 12))
    $stamp = Join-Path $dir '.glide-verified'
    if ((Test-Path $stamp) -and ((Get-Content $stamp -Raw).Trim() -eq $pin.Commit)) { return $dir }
    if (Test-Path $dir) { Remove-Item -Recurse -Force $dir }
    New-Item -ItemType Directory -Force $dir | Out-Null
    Invoke-Native git @('init', '-q') $dir
    $urls = @($pin.Url); if ($pin.Mirror) { $urls += $pin.Mirror }
    $fetched = $false
    foreach ($url in $urls) {
        Write-Host "Fetching $name $($pin.Tag) ($($pin.Commit)) from $url"
        try { Invoke-Native git @('-C', $dir, 'fetch', '-q', '--depth', '1', $url, $pin.Commit); $fetched = $true; break }
        catch { Write-Warning $_ }
    }
    if (-not $fetched) { throw "Could not fetch $name at $($pin.Commit)" }
    Invoke-Native git @('-c', 'advice.detachedHead=false', 'checkout', '-q', 'FETCH_HEAD') $dir
    $head = (& git -C $dir rev-parse HEAD).Trim()
    if ($head -ne $pin.Commit) { throw "$name commit mismatch: expected $($pin.Commit), got $head" }
    if ($pin.Submodules) {
        # Submodule commits are pinned by the verified superproject tree, so git verifies them too.
        Invoke-Native git (@('-c', 'protocol.file.allow=never', 'submodule', 'update', '--init', '--depth', '1', '--') + $pin.Submodules) $dir
    }
    Set-Content -Encoding ASCII $stamp $pin.Commit
    return $dir
}

function Get-Nasm {
    $dir = Join-Path $cache "nasm-$NasmVersion"
    $exe = Join-Path $dir 'nasm.exe'
    if (Test-Path $exe) { return $dir }
    $dl = Join-Path $cache 'dl'
    New-Item -ItemType Directory -Force $dl | Out-Null
    $zip = Join-Path $dl "nasm-$NasmVersion-win64.zip"
    if (-not (Test-Path $zip) -or (Get-FileHash $zip -Algorithm SHA256).Hash -ne $NasmSha256) {
        $url = "https://www.nasm.us/pub/nasm/releasebuilds/$NasmVersion/win64/nasm-$NasmVersion-win64.zip"
        Write-Host "Downloading $url"
        Invoke-WebRequest -Uri $url -OutFile "$zip.partial" -UseBasicParsing
        Move-Item -Force "$zip.partial" $zip
    }
    $actual = (Get-FileHash $zip -Algorithm SHA256).Hash
    if ($actual -ne $NasmSha256) { throw "NASM archive hash mismatch: expected $NasmSha256, got $actual" }
    $staging = Join-Path $cache ('nasm-extract-' + [guid]::NewGuid().ToString('N'))
    Expand-Archive -Path $zip -DestinationPath $staging
    New-Item -ItemType Directory -Force $dir | Out-Null
    Copy-Item (Join-Path $staging "nasm-$NasmVersion\nasm.exe") $dir
    Copy-Item (Join-Path $staging "nasm-$NasmVersion\LICENSE") $dir
    Remove-Item -Recurse -Force $staging
    return $dir
}

function Get-Meson {
    # A private venv keeps the user's Python untouched and pins the Meson/Ninja versions.
    $venv = Join-Path $cache "venv-meson-$MesonVersion"
    $scripts = Join-Path $venv 'Scripts'
    if (-not (Test-Path (Join-Path $scripts 'meson.exe'))) {
        $python = Get-Command python -ErrorAction SilentlyContinue
        if (-not $python) { $python = Get-Command py -ErrorAction Stop }
        if (Test-Path $venv) { Remove-Item -Recurse -Force $venv }
        Invoke-Native $python.Source @('-m', 'venv', $venv)
        Invoke-Native (Join-Path $scripts 'python.exe') @('-m', 'pip', 'install', '--disable-pip-version-check', '-q', "meson==$MesonVersion", "ninja==$NinjaVersion")
    }
    return $scripts
}

function Import-MsvcEnvironment {
    if ($env:VCToolsInstallDir -and (Get-Command cl.exe -ErrorAction SilentlyContinue)) { return }
    $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
    if (-not (Test-Path $vswhere)) { throw 'vswhere.exe not found; install Visual Studio Build Tools with the C++ x64 toolset.' }
    $vcvars = $null
    foreach ($path in (& $vswhere -all -prerelease -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -sort -property installationPath)) {
        $candidate = Join-Path $path 'VC\Auxiliary\Build\vcvars64.bat'
        if (Test-Path $candidate) { $vcvars = $candidate; break }
    }
    if (-not $vcvars) { throw 'No Visual Studio installation with vcvars64.bat and the x64 C++ toolset was found.' }
    Write-Host "MSVC environment: $vcvars"
    # vcvars64.bat itself looks for vswhere.exe on PATH.
    $env:PATH = "$(Split-Path $vswhere);$env:PATH"
    $lines = & cmd.exe /d /s /c "`"$vcvars`" >nul && set"
    foreach ($line in $lines) {
        if ($line -match '^([^=]+)=(.*)$') { Set-Item -Path "Env:$($Matches[1])" -Value $Matches[2] }
    }
    if (-not (Get-Command cl.exe -ErrorAction SilentlyContinue)) { throw 'cl.exe is not available after vcvars64.bat.' }
}

$paths = @{}
foreach ($name in $Sources.Keys) {
    if ($NoHevc -and $name -eq 'libde265') { continue }
    $paths[$name] = Get-PinnedSource $name
}
if ($FetchOnly) { $paths.GetEnumerator() | ForEach-Object { Write-Host ("{0,-9} {1}" -f $_.Key, $_.Value) }; exit 0 }

Import-MsvcEnvironment
$mesonScripts = Get-Meson
$env:PATH = "$mesonScripts;$env:PATH"
if (-not $NoAsm) { $env:PATH = "$(Get-Nasm);$env:PATH" }

# ---- Shared size-first flags ----------------------------------------------------------------------
# /O1 (minimise size) + whole-program /GL so the final /LTCG link can inline across libraries and
# discard everything the decoder glue never reaches. Static CRT (/MT, incl. dav1d via b_vscrt=mt)
# in every library, like Glide.Native.dll: Glide ships no VC++ redistributable.
# 5.0.7: speed by default (/O2; dav1d meson optimization=2). Measured in benchmark-report-5.0.7.md section 6.
$OptFlag = if ($Optimize -eq 'size') { '/O1' } else { '/O2' }
$CFlags = "$OptFlag /Gw /GL /Gy /guard:cf /DNDEBUG"
$flavor = (@($(if ($NoHevc) { 'nohevc' } else { 'hevc' }), $(if ($NoAsm) { 'noasm' } else { 'asm' }),
    $(if ($Dav1dBitdepths -eq '8') { 'av1-8bit' } else { 'av1-hbd' })) + $(if ($Optimize -eq 'speed') { @('o2') } else { @() }) -join '-')
$buildRoot = Join-Path $cache "build-$flavor"
$prefix = Join-Path $cache "prefix-$flavor"
if ($Force) { foreach ($dir in $buildRoot, $prefix) { if (Test-Path $dir) { Remove-Item -Recurse -Force $dir } } }
New-Item -ItemType Directory -Force $buildRoot, $prefix | Out-Null
# Bump $RecipeRevision whenever a dependency's configure options change so cached builds are redone.
$RecipeRevision = 3
$recipeText = "$RecipeRevision|$CFlags|$Dav1dBitdepths|$flavor"
$recipeHash = [BitConverter]::ToString([Security.Cryptography.SHA256]::Create().ComputeHash([Text.Encoding]::UTF8.GetBytes($recipeText))).Replace('-', '').Substring(0, 16)

function Test-Stamp([string]$name) {
    $stamp = Join-Path $prefix ".built-$name"
    return (Test-Path $stamp) -and ((Get-Content $stamp -Raw).Trim() -eq "$($Sources[$name].Commit)/$recipeHash")
}
function Set-Stamp([string]$name) { Set-Content -Encoding ASCII (Join-Path $prefix ".built-$name") "$($Sources[$name].Commit)/$recipeHash" }

function Invoke-CMakeBuild([string]$name, [string]$sourceDir, [string[]]$options, [string[]]$targets = @(), [switch]$NoInstall) {
    $build = Join-Path $buildRoot $name
    if (Test-Path $build) { Remove-Item -Recurse -Force $build }
    $common = @(
        '-G', 'Ninja', '-S', $sourceDir, '-B', $build,
        '-DCMAKE_BUILD_TYPE=Release',
        "-DCMAKE_INSTALL_PREFIX=$prefix",
        "-DCMAKE_PREFIX_PATH=$prefix",
        '-DCMAKE_POLICY_DEFAULT_CMP0091=NEW',
        '-DCMAKE_POLICY_VERSION_MINIMUM=3.5',
        '-DCMAKE_MSVC_RUNTIME_LIBRARY=MultiThreaded',
        "-DCMAKE_C_FLAGS_RELEASE=$CFlags",
        "-DCMAKE_CXX_FLAGS_RELEASE=$CFlags",
        '-DCMAKE_STATIC_LINKER_FLAGS_RELEASE=/LTCG',
        '-DBUILD_SHARED_LIBS=OFF',
        '-DBUILD_TESTING=OFF'
    )
    Write-Host "==== $name"
    Invoke-Native cmake ($common + $options)
    $buildArgs = @('--build', $build, '--parallel')
    if ($targets.Count -gt 0) { $buildArgs += @('--target') + $targets }
    Invoke-Native cmake $buildArgs
    if (-not $NoInstall) { Invoke-Native cmake @('--install', $build) }
    return $build
}

# ---- dav1d (AV1) ----------------------------------------------------------------------------------
if (-not (Test-Stamp 'dav1d')) {
    $build = Join-Path $buildRoot 'dav1d'
    if (Test-Path $build) { Remove-Item -Recurse -Force $build }
    Write-Host '==== dav1d'
    $asm = if ($NoAsm) { 'false' } else { 'true' }
    Invoke-Native meson @('setup', $build, $paths.dav1d,
        '--buildtype=release', "-Doptimization=$(if ($Optimize -eq 'size') { 's' } else { '2' })", '-Db_ndebug=true', '-Db_vscrt=mt',
        '--default-library=static', "--prefix=$prefix", '--libdir=lib',
        "-Dbitdepths=$Dav1dBitdepths", "-Denable_asm=$asm", '-Denable_tools=false', '-Denable_examples=false',
        '-Denable_tests=false', '-Denable_docs=false', '-Dlogging=false', '-Dtrim_dsp=true',
        "-Dc_args=$($CFlags -replace ' ', ',')")
    Invoke-Native meson @('compile', '-C', $build)
    Invoke-Native meson @('install', '-C', $build, '--quiet')
    Set-Stamp 'dav1d'
}

# ---- libde265 (HEVC, optional) --------------------------------------------------------------------
if (-not $NoHevc -and -not (Test-Stamp 'libde265')) {
    Invoke-CMakeBuild 'libde265' $paths.libde265 @(
        '-DENABLE_SDL=OFF', '-DENABLE_ENCODER=OFF', '-DENABLE_SHERLOCK265=OFF',
        '-DENABLE_INTERNAL_DEVELOPMENT_TOOLS=OFF', '-DENABLE_AVX512=OFF') | Out-Null
    Set-Stamp 'libde265'
}

# ---- libheif (HEIF/AVIF container) ----------------------------------------------------------------
if (-not (Test-Stamp 'libheif')) {
    $hevc = if ($NoHevc) { 'OFF' } else { 'ON' }
    $de265Lib = Get-ChildItem (Join-Path $prefix 'lib') -Filter '*de265*.lib' -ErrorAction SilentlyContinue | Select-Object -First 1
    $de265Path = if ($de265Lib) { $de265Lib.FullName } else { '' }
    Invoke-CMakeBuild 'libheif' $paths.libheif @(
        '-DCMAKE_C_FLAGS=/DLIBDE265_STATIC_BUILD /DLIBHEIF_STATIC_BUILD', '-DCMAKE_CXX_FLAGS=/DLIBDE265_STATIC_BUILD /DLIBHEIF_STATIC_BUILD /EHsc',
        '-DENABLE_PLUGIN_LOADING=OFF', '-DWITH_EXAMPLES=OFF', '-DWITH_EXAMPLE_HEIF_THUMB=OFF', '-DWITH_EXAMPLE_HEIF_VIEW=OFF',
        '-DWITH_GDK_PIXBUF=OFF', '-DBUILD_DOCUMENTATION=OFF', '-DWITH_FUZZERS=OFF',
        "-DWITH_LIBDE265=$hevc", '-DWITH_LIBDE265_PLUGIN=OFF', '-DWITH_DAV1D=ON', '-DWITH_DAV1D_PLUGIN=OFF',
        '-DWITH_X265=OFF', '-DWITH_KVAZAAR=OFF', '-DWITH_UVG266=OFF', '-DWITH_VVDEC=OFF', '-DWITH_VVENC=OFF',
        '-DWITH_X264=OFF', '-DWITH_OpenH264_DECODER=OFF', '-DWITH_AOM_DECODER=OFF', '-DWITH_AOM_ENCODER=OFF',
        '-DWITH_SvtEnc=OFF', '-DWITH_RAV1E=OFF', '-DWITH_JPEG_DECODER=OFF', '-DWITH_JPEG_ENCODER=OFF',
        '-DWITH_OpenJPEG_ENCODER=OFF', '-DWITH_OpenJPEG_DECODER=OFF', '-DWITH_FFMPEG_DECODER=OFF',
        '-DWITH_OPENJPH_ENCODER=OFF', '-DWITH_UNCOMPRESSED_CODEC=OFF', '-DWITH_HEADER_COMPRESSION=OFF',
        '-DWITH_LIBSHARPYUV=OFF', '-DWITH_WEBCODECS=OFF',
        "-DDAV1D_INCLUDE_DIR=$prefix\include", "-DDAV1D_LIBRARY=$prefix\lib\libdav1d.a",
        "-DLIBDE265_INCLUDE_DIR=$prefix\include", "-DLIBDE265_LIBRARY=$de265Path") | Out-Null
    Set-Stamp 'libheif'
}

# ---- libjxl (decoder only) ------------------------------------------------------------------------
if (-not (Test-Stamp 'libjxl') -or -not (Test-Path (Join-Path $prefix 'lib\jxl_cms.lib'))) {
    # Highway: keep the SSE2 baseline (static target) plus AVX2 dynamic dispatch only; every further
    # SIMD target duplicates the hot decoder kernels in the DLL.
    $build = Invoke-CMakeBuild 'libjxl' $paths.libjxl @(
        '-DJPEGXL_ENABLE_TOOLS=OFF', '-DJPEGXL_ENABLE_DEVTOOLS=OFF', '-DJPEGXL_ENABLE_DOXYGEN=OFF',
        '-DJPEGXL_ENABLE_MANPAGES=OFF', '-DJPEGXL_ENABLE_BENCHMARK=OFF', '-DJPEGXL_ENABLE_EXAMPLES=OFF',
        '-DJPEGXL_ENABLE_JNI=OFF', '-DJPEGXL_ENABLE_SJPEG=OFF', '-DJPEGXL_ENABLE_OPENEXR=OFF',
        '-DJPEGXL_ENABLE_JPEGLI=OFF', '-DJPEGXL_ENABLE_TRANSCODE_JPEG=OFF', '-DJPEGXL_ENABLE_BOXES=OFF',
        '-DJPEGXL_ENABLE_FUZZERS=OFF', '-DJPEGXL_ENABLE_TCMALLOC=OFF', '-DJPEGXL_ENABLE_VIEWERS=OFF',
        '-DJPEGXL_ENABLE_PLUGINS=OFF', '-DJPEGXL_BUNDLE_LIBPNG=OFF', '-DJPEGXL_ENABLE_SKCMS=ON',
        '-DJPEGXL_ENABLE_HWY_SSSE3=OFF', '-DJPEGXL_ENABLE_HWY_SSE4=OFF', '-DJPEGXL_ENABLE_HWY_AVX3=OFF',
        '-DJPEGXL_ENABLE_HWY_AVX3_DL=OFF', '-DJPEGXL_ENABLE_HWY_AVX3_ZEN4=OFF', '-DJPEGXL_ENABLE_HWY_AVX3_SPR=OFF',
        '-DJPEGXL_ENABLE_HWY_EMU128=OFF', '-DJPEGXL_ENABLE_HWY_SCALAR=OFF') @('jxl_dec', 'jxl_cms', 'jxl_threads', 'hwy') -NoInstall
    # libjxl installs only the combined encoder+decoder library; stage the decoder-only pieces by hand.
    $inc = Join-Path $prefix 'include\jxl'
    New-Item -ItemType Directory -Force $inc, (Join-Path $prefix 'lib') | Out-Null
    Copy-Item (Join-Path $paths.libjxl 'lib\include\jxl\*.h') $inc -Force
    Get-ChildItem $build -Recurse -File -Filter '*.h' | Where-Object { $_.Directory.Name -eq 'jxl' -and $_.FullName -like '*include*' } |
        ForEach-Object { Copy-Item $_.FullName $inc -Force }
    foreach ($lib in 'jxl_dec', 'jxl_cms', 'jxl_threads', 'hwy') {
        $file = Get-ChildItem $build -Recurse -File -Filter "$lib.lib" | Select-Object -First 1
        if (-not $file) { throw "libjxl build did not produce $lib.lib" }
        Copy-Item $file.FullName (Join-Path $prefix "lib\$lib.lib") -Force
    }
    Set-Stamp 'libjxl'
}

# ---- OpenJPEG (JPEG 2000) -------------------------------------------------------------------------
if (-not (Test-Stamp 'openjpeg')) {
    Invoke-CMakeBuild 'openjpeg' $paths.openjpeg @(
        '-DBUILD_STATIC_LIBS=ON', '-DBUILD_CODEC=OFF', '-DBUILD_DOC=OFF', '-DBUILD_JPIP=OFF',
        '-DBUILD_LUTS_GENERATOR=OFF', '-DBUILD_UNIT_TESTS=OFF', '-DBUILD_THIRDPARTY=OFF') | Out-Null
    Set-Stamp 'openjpeg'
}

# ---- Provider DLL ---------------------------------------------------------------------------------
$glueSource = Join-Path $root 'native\Glide.Codecs'
$glueBuild = Join-Path $glueSource 'build'
$hevcOption = if ($NoHevc) { 'OFF' } else { 'ON' }
$mapOption = if ($MapFile) { 'ON' } else { 'OFF' }
if (Test-Path $glueBuild) { Remove-Item -Recurse -Force $glueBuild }
Write-Host '==== glidecodecs'
Invoke-Native cmake @('-G', 'Ninja', '-S', $glueSource, '-B', $glueBuild, '-DCMAKE_BUILD_TYPE=Release',
    "-DGLIDE_CODECS_PREFIX=$prefix", "-DGLIDE_CODECS_OPT=$($OptFlag.TrimStart('/'))", "-DGLIDE_CODECS_MAX_THREADS=$MaxThreads", "-DGLIDE_CODECS_HEVC=$hevcOption", "-DGLIDE_CODECS_MAP=$mapOption",
    '-DCMAKE_MSVC_RUNTIME_LIBRARY=MultiThreaded')
Invoke-Native cmake @('--build', $glueBuild, '--parallel')

$dll = Join-Path $glueBuild 'glidecodecs.dll'
$manifest = Join-Path $glueBuild 'glidecodecs.glidecodec.json'
if (-not (Test-Path $dll) -or -not (Test-Path $manifest)) { throw 'glidecodecs build did not produce the DLL and manifest.' }
# Only the provider ABI may be exported: a stray dllexport in a dependency pins its whole API (and
# everything that API reaches) past /OPT:REF. Exact allow-list; EncodeSurface is the ABI v2 encode
# extension (SupportsEncode) behind Save as JPEG 2000 / WebP.
$allowedExports = @('GlideCodecProvider_GetInfo', 'GlideCodecProvider_Create', 'GlideCodecProvider_Destroy',
    'GlideCodecProvider_Probe', 'GlideCodecProvider_FreeBuffer', 'GlideCodecProvider_DecodePreviewSurface',
    'GlideCodecProvider_DecodeFullSurface', 'GlideCodecProvider_EncodeSurface')
$exports = @(& dumpbin /nologo /exports $dll | ForEach-Object { if ($_ -match '^\s+\d+\s+[0-9A-F]+\s+[0-9A-F]+\s+(\S+)') { $Matches[1] } })
$stray = @($exports | Where-Object { $allowedExports -notcontains $_ })
if ($stray.Count -gt 0) { throw "glidecodecs.dll exports non-ABI symbols ($($stray.Count)), e.g. $($stray[0..4] -join ', ')" }
$missing = @($allowedExports | Where-Object { $exports -notcontains $_ })
if ($missing.Count -gt 0) { throw "glidecodecs.dll is missing ABI exports: $($missing -join ', ')" }
# Glide ships no VC++ redistributable: the provider must not depend on the shared MSVC/UCRT DLLs.
$imports = @(& dumpbin /nologo /dependents $dll | ForEach-Object { $_.Trim() } | Where-Object { $_ -match '\.dll$' -and $_ -notmatch '\\' })
$crt = @($imports | Where-Object { $_ -match '^(msvcp|vcruntime|concrt|api-ms-win-crt-)' })
if ($crt.Count -gt 0) { throw "glidecodecs.dll imports the shared CRT ($($crt -join ', ')); every library must be built with /MT." }
Write-Host "glidecodecs.dll imports: $($imports -join ', ')"
$codecDir = if ($OutDir) { $OutDir } else { Join-Path $root 'codecs' }
New-Item -ItemType Directory -Force $codecDir | Out-Null
Copy-Item $dll, $manifest $codecDir -Force

# ---- NOTICE: component list + full upstream license texts, shipped next to the DLL ----------------
$components = @(
    @{ Name = 'dav1d (AV1 decoder)'; Key = 'dav1d'; License = 'BSD-2-Clause'; Files = @('COPYING') },
    @{ Name = 'libheif (HEIF/AVIF container)'; Key = 'libheif'; License = 'LGPL-3.0-or-later'; Files = @('COPYING') },
    @{ Name = 'libde265 (HEVC decoder)'; Key = 'libde265'; License = 'LGPL-3.0-or-later'; Files = @('COPYING') },
    @{ Name = 'libjxl (JPEG XL decoder)'; Key = 'libjxl'; License = 'BSD-3-Clause, with patent grant'; Files = @('LICENSE', 'PATENTS') },
    @{ Name = 'Highway (SIMD library; libjxl submodule at the commit below)'; Key = 'libjxl'; License = 'Apache-2.0 OR BSD-3-Clause; used under BSD-3-Clause'; Files = @('third_party\highway\LICENSE-BSD3') },
    @{ Name = 'Brotli (compression library; libjxl submodule at the commit below)'; Key = 'libjxl'; License = 'MIT'; Files = @('third_party\brotli\LICENSE') },
    @{ Name = 'skcms (colour management; libjxl submodule at the commit below)'; Key = 'libjxl'; License = 'BSD-3-Clause'; Files = @('third_party\skcms\LICENSE') },
    @{ Name = 'OpenJPEG (JPEG 2000 decoder and encoder)'; Key = 'openjpeg'; License = 'BSD-2-Clause'; Files = @('LICENSE') },
    @{ Name = 'libwebp (WebP encoder; vendored in native\third_party\libwebp)'; Key = 'libwebp'; License = 'BSD-3-Clause, with patent grant'; Files = @('COPYING', 'PATENTS') }
)
# libwebp is vendored in the repository (the same trimmed 1.5.0 tree the Explorer thumbnail provider
# builds decode-only) rather than fetched, so it has no pin in $Sources.
$paths['libwebp'] = Join-Path $root 'native\third_party\libwebp'
$vendored = @{ libwebp = @{ Url = 'https://github.com/webmproject/libwebp.git'; Tag = 'v1.5.0'; Commit = 'vendored copy, see native\third_party\libwebp\GLIDE_VENDOR.md' } }
$nl = "`r`n"
$notice = New-Object System.Text.StringBuilder
[void]$notice.Append("Glide Image Viewer - bundled codec provider (glidecodecs.dll)$nl")
[void]$notice.Append("==============================================================$nl$nl")
[void]$notice.Append("glidecodecs.dll is a build of the open-source libraries below, statically linked with Glide's$nl")
[void]$notice.Append("glue code (native\Glide.Codecs). Glide loads it only when an AVIF, HEIC/HEIF, JPEG XL or JPEG 2000$nl")
[void]$notice.Append("file is opened, or when an image is saved as JPEG 2000 or WebP (its only encoders).$nl")
[void]$notice.Append("Build recipe: tools\build-codecs.ps1 (exact commits below).$nl$nl")
[void]$notice.Append("LGPL: libheif and libde265 are licensed under the GNU LGPL, version 3 or later. Their complete$nl")
[void]$notice.Append("corresponding source is available at the upstream URLs and commits below, and the glue source and$nl")
[void]$notice.Append("build script let you rebuild and relink glidecodecs.dll against modified versions of them.$nl$nl")
[void]$notice.Append("Patents: HEVC (HEIC/HEIF) and AV1 (AVIF) decoding may be subject to patent claims in some$nl")
[void]$notice.Append("jurisdictions. tools\build-codecs.ps1 -NoHevc builds the provider without HEVC.$nl$nl")
foreach ($c in $components) {
    if ($NoHevc -and $c.Key -eq 'libde265') { continue }
    $pin = if ($Sources.Contains($c.Key)) { $Sources[$c.Key] } else { $vendored[$c.Key] }
    [void]$notice.Append("  $($c.Name)$nl      $($pin.Url)  tag $($pin.Tag)  commit $($pin.Commit)$nl      License: $($c.License)$nl")
}
$seenTexts = @{}
foreach ($c in $components) {
    if ($NoHevc -and $c.Key -eq 'libde265') { continue }
    foreach ($f in $c.Files) {
        $rule = '-' * 100
        $text = (Get-Content -Raw -Encoding UTF8 (Join-Path $paths[$c.Key] $f)).TrimEnd()
        [void]$notice.Append("$nl$rule$nl$($c.Name): $f$nl$rule$nl")
        if ($seenTexts.ContainsKey($text)) { [void]$notice.Append("(Identical to the $($seenTexts[$text]) text above.)$nl"); continue }
        $seenTexts[$text] = "$($c.Name): $f"
        [void]$notice.Append($text)
        [void]$notice.Append($nl)
    }
}
[IO.File]::WriteAllText((Join-Path $codecDir 'glidecodecs.NOTICE.txt'), $notice.ToString(), (New-Object System.Text.UTF8Encoding($false)))
& (Join-Path $PSScriptRoot 'generate-codec-index.ps1') -CodecDirectory $codecDir
$size = (Get-Item (Join-Path $codecDir 'glidecodecs.dll')).Length
Write-Host ("glidecodecs.dll: {0:N0} bytes ({1:N2} MB) [{2}] -> {3}" -f $size, ($size / 1MB), $flavor, $codecDir)

