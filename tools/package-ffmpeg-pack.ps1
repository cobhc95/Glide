# Builds the optional video decoder pack that Glide downloads when Windows can't play a video.
# Output: artifacts\ffmpeg-pack\glide-ffmpeg-9.0.2-win64.zip, published as a release asset under the
# tag named in src\Glide.Imaging\VideoDecoderPack.cs. Rebuilding changes the hash: after uploading a
# new pack, update PackSha256 there. Run tools\fetch-ffmpeg.ps1 first.
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$bin = Join-Path $root 'native\third_party\ffmpeg\bin'
$out = Join-Path $root 'artifacts\ffmpeg-pack'
$staging = Join-Path $out 'staging'
if (Test-Path $out) { Remove-Item -Recurse -Force $out }
New-Item -ItemType Directory -Force $staging | Out-Null
foreach ($dll in 'avutil-61.dll', 'swresample-7.dll', 'swscale-10.dll', 'avcodec-63.dll', 'avformat-63.dll') {
    Copy-Item (Join-Path $bin $dll) $staging
}
Copy-Item (Join-Path $root 'native\third_party\ffmpeg\LICENSE.txt') (Join-Path $staging 'LICENSE.txt')
Copy-Item (Join-Path $root 'native\third_party\FFMPEG-NOTICE.txt') (Join-Path $staging 'NOTICE.txt')
$zip = Join-Path $out 'glide-ffmpeg-9.0.2-win64.zip'
Compress-Archive -Path (Join-Path $staging '*') -DestinationPath $zip -CompressionLevel Optimal
Remove-Item -Recurse -Force $staging
$item = Get-Item $zip
Write-Host "Pack:   $zip"
Write-Host "Bytes:  $($item.Length)"
Write-Host "SHA256: $((Get-FileHash $zip -Algorithm SHA256).Hash)"
