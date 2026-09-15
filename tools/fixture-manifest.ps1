param(
    [Parameter(Mandatory=$true)][ValidateSet('Write','Validate')][string]$Mode,
    [Parameter(Mandatory=$true)][string]$Directory,
    [Parameter(Mandatory=$true)][string]$Generator,
    [string]$SourceIdentity = 'Glide-3.0-4',
    [int]$Schema = 1
)
$ErrorActionPreference = 'Stop'
$Directory = [IO.Path]::GetFullPath($Directory)
$Generator = [IO.Path]::GetFullPath($Generator)
$manifestPath = Join-Path $Directory 'fixture-manifest.json'
$completePath = Join-Path $Directory 'fixture-complete.marker'

function Get-Relative([string]$base,[string]$path) {
    # Windows PowerShell 5.1 runs on .NET Framework, which has no Path.GetRelativePath.
    # URI relative calculation keeps this helper compatible with the build.cmd baseline.
    $baseFull = [IO.Path]::GetFullPath($base).TrimEnd('\') + '\'
    $pathFull = [IO.Path]::GetFullPath($path)
    $baseUri = New-Object System.Uri($baseFull)
    $pathUri = New-Object System.Uri($pathFull)
    [Uri]::UnescapeDataString($baseUri.MakeRelativeUri($pathUri).ToString()).Replace('\','/')
}
function Get-PayloadFiles {
    @(Get-ChildItem -LiteralPath $Directory -File -Recurse | Where-Object {
        $_.FullName -ne $manifestPath -and $_.FullName -ne $completePath
    } | Sort-Object FullName)
}
function Get-Sha256([string]$path) {
    $sha = [Security.Cryptography.SHA256]::Create()
    $stream = [IO.File]::OpenRead($path)
    try {
        ([BitConverter]::ToString($sha.ComputeHash($stream))).Replace('-', '')
    }
    finally {
        $stream.Dispose()
        $sha.Dispose()
    }
}

if ($Mode -eq 'Write') {
    if (-not (Test-Path -LiteralPath $Directory -PathType Container)) { throw "Fixture directory missing: $Directory" }
    if (-not (Test-Path -LiteralPath $Generator -PathType Leaf)) { throw "Generator missing: $Generator" }
    Remove-Item -LiteralPath $completePath -Force -ErrorAction SilentlyContinue
    Remove-Item -LiteralPath $manifestPath -Force -ErrorAction SilentlyContinue
    $files = @(Get-PayloadFiles)
    if ($files.Count -eq 0) { throw 'Fixture generation produced no files.' }
    $rows = @($files | ForEach-Object {
        [ordered]@{ path=(Get-Relative $Directory $_.FullName); bytes=$_.Length; sha256=(Get-Sha256 $_.FullName) }
    })
    $manifest = [ordered]@{
        schema=$Schema
        source_identity=$SourceIdentity
        generator_path=$Generator
        generator_sha256=(Get-Sha256 $Generator)
        created_utc=[DateTime]::UtcNow.ToString('O')
        expected_files=$rows
    }
    $tmp = "$manifestPath.$PID.$([Guid]::NewGuid().ToString('N')).tmp"
    $manifest | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $tmp -Encoding UTF8
    Move-Item -LiteralPath $tmp -Destination $manifestPath -Force
    # The completion marker is deliberately last. Reuse is forbidden without it.
    "schema=$Schema`nmanifest_sha256=$(Get-Sha256 $manifestPath)" | Set-Content -LiteralPath $completePath -Encoding Ascii
    exit 0
}

if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) { throw 'Fixture manifest missing.' }
if (-not (Test-Path -LiteralPath $completePath -PathType Leaf)) { throw 'Fixture completion marker missing.' }
$m = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
if ([int]$m.schema -ne $Schema) { throw "Fixture schema mismatch: $($m.schema) != $Schema" }
if ([string]$m.source_identity -ne $SourceIdentity) { throw "Fixture source identity mismatch: $($m.source_identity) != $SourceIdentity" }
if (-not (Test-Path -LiteralPath $Generator -PathType Leaf)) { throw 'Current generator missing.' }
$generatorHash=Get-Sha256 $Generator
if ([string]$m.generator_sha256 -ne $generatorHash) { throw 'Fixture generator hash is stale.' }
$marker = Get-Content -LiteralPath $completePath -Raw
$currentManifestHash=Get-Sha256 $manifestPath
if ($marker -notmatch [regex]::Escape("manifest_sha256=$currentManifestHash")) { throw 'Fixture completion marker does not match manifest.' }
$expected=@($m.expected_files)
if ($expected.Count -eq 0) { throw 'Fixture manifest has no expected files.' }
foreach($row in $expected) {
    $file=Join-Path $Directory ([string]$row.path)
    if (-not (Test-Path -LiteralPath $file -PathType Leaf)) { throw "Fixture missing: $($row.path)" }
    $info=Get-Item -LiteralPath $file
    if ([long]$info.Length -ne [long]$row.bytes) { throw "Fixture length mismatch: $($row.path)" }
    if ((Get-Sha256 $file) -ne [string]$row.sha256) { throw "Fixture hash mismatch: $($row.path)" }
}
# Reject partial/extra payloads as well; a stale directory is not reusable.
$actual=@(Get-PayloadFiles | ForEach-Object { Get-Relative $Directory $_.FullName })
$listed=@($expected | ForEach-Object { [string]$_.path })
if (@(Compare-Object $listed $actual).Count -ne 0) { throw 'Fixture payload set differs from manifest.' }
exit 0
