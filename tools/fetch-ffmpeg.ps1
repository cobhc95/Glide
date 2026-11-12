# Fetches the pinned LGPL FFmpeg shared build used by Glide's fallback video decoder.
#
# Output: native\third_party\ffmpeg\{include,lib,bin} (git-ignored). The archive is verified against a
# pinned SHA-256 and cached under %LOCALAPPDATA%\Glide-build-cache so rebuilds work offline and
# survive the upstream autobuild being rotated out. To update FFmpeg, change the three values below.
param([switch]$Force)
$ErrorActionPreference = 'Stop'

$Tag    = 'autobuild-2026-10-27-18-38'
$Asset  = 'ffmpeg-n9.0.2-36-g6882969ce9-win64-lgpl-shared-9.0.zip'
$Sha256 = 'f70e43c4f5670fbbf2dc4eb667b0d6b3743d68386f766c08bb2e0ec3edece398'

$root = Split-Path -Parent $PSScriptRoot
$target = Join-Path $root 'native\third_party\ffmpeg'
$stamp = Join-Path $target '.glide-ffmpeg-sha256'
if (-not $Force -and (Test-Path $stamp) -and ((Get-Content $stamp -Raw).Trim() -eq $Sha256)) {
    Write-Host "FFmpeg already present: $target"
    exit 0
}

$cacheDir = Join-Path $env:LOCALAPPDATA 'Glide-build-cache'
New-Item -ItemType Directory -Force $cacheDir | Out-Null
$zip = Join-Path $cacheDir $Asset
if (-not (Test-Path $zip) -or (Get-FileHash $zip -Algorithm SHA256).Hash -ne $Sha256) {
    $url = "https://github.com/BtbN/FFmpeg-Builds/releases/download/$Tag/$Asset"
    Write-Host "Downloading $url"
    $ProgressPreference = 'SilentlyContinue'
    Invoke-WebRequest -Uri $url -OutFile "$zip.partial" -UseBasicParsing
    Move-Item -Force "$zip.partial" $zip
}
$actual = (Get-FileHash $zip -Algorithm SHA256).Hash
if ($actual -ne $Sha256) { throw "FFmpeg archive hash mismatch: expected $Sha256, got $actual" }

$staging = Join-Path $cacheDir ('ffmpeg-extract-' + [guid]::NewGuid().ToString('N'))
Expand-Archive -Path $zip -DestinationPath $staging
$inner = Get-ChildItem $staging -Directory | Select-Object -First 1
if (Test-Path $target) { Remove-Item -Recurse -Force $target }
New-Item -ItemType Directory -Force $target | Out-Null
foreach ($part in 'include', 'lib', 'bin', 'LICENSE.txt') {
    $source = Join-Path $inner.FullName $part
    if (Test-Path $source) { Copy-Item -Recurse $source $target }
}
# Glide only needs the libraries, never the command-line tools.
Get-ChildItem (Join-Path $target 'bin') -Filter '*.exe' | Remove-Item -Force
Remove-Item -Recurse -Force $staging
Set-Content -Encoding ASCII $stamp $Sha256
Write-Host "FFmpeg ready: $target"
