param(
    [string]$CodecDirectory = (Join-Path (Split-Path -Parent $PSScriptRoot) 'codecs')
)
$ErrorActionPreference = 'Stop'

if (-not (Test-Path $CodecDirectory)) {
    Write-Host "Codec index: no codecs directory; nothing to index."
    exit 0
}

$providers = @()
Get-ChildItem $CodecDirectory -Filter '*.glidecodec.json' -File | Sort-Object Name | ForEach-Object {
    $manifestPath = $_.FullName
    $manifest = Get-Content $manifestPath -Raw | ConvertFrom-Json
    $dllPath = [System.IO.Path]::ChangeExtension($manifestPath, '.dll')
    if (-not (Test-Path $dllPath)) { throw "Codec manifest has no matching DLL: $manifestPath" }
    $hash = (Get-FileHash $dllPath -Algorithm SHA256).Hash.ToLowerInvariant()
    if ([string]::IsNullOrWhiteSpace([string]$manifest.sha256) -or $hash -ne ([string]$manifest.sha256).ToLowerInvariant()) {
        throw "Codec SHA-256 mismatch while generating index: $($manifest.id)"
    }
    $providers += [ordered]@{
        id = [string]$manifest.id
        version = [string]$manifest.version
        license = [string]$manifest.license
        dll = [System.IO.Path]::GetFileName($dllPath)
        manifest = $_.Name
        sha256 = $hash
        abiVersion = [uint32]$manifest.abiVersion
        structSize = [uint32]$manifest.structSize
        extensions = @($manifest.extensions)
        preview = [bool]$manifest.preview
        full = [bool]$manifest.full
        metadata = [bool]$manifest.metadata
        frames = [bool]$manifest.frames
    }
}

$document = [ordered]@{
    schemaVersion = 1
    generatedUtc = [DateTime]::UtcNow.ToString('o')
    providers = $providers
}
$outPath = Join-Path $CodecDirectory 'codecs.index.json'
$document | ConvertTo-Json -Depth 8 | Set-Content $outPath -Encoding UTF8
Write-Host "Codec index: $($providers.Count) provider(s) -> $outPath"
