# Generates a clean zero-context handover ZIP package for Glide
param(
    [string]$OutputPath = "Glide-Zero-Context-Handover.zip"
)

Add-Type -AssemblyName System.IO.Compression.FileSystem

$rootDir = (Resolve-Path "$PSScriptRoot\..").Path
$resolvedOut = [System.IO.Path]::GetFullPath((Join-Path $rootDir $OutputPath))

Write-Host "Creating Zero-Context Handover ZIP at: $resolvedOut"

# Exclude generated binaries and transient caches
$excludePatterns = @(
    "*\bin\*",
    "*\obj\*",
    "*\.artifacts\*",
    "*\dist\*",
    "*\dist-installer\*",
    "*\.vs\*",
    "*\.git\*",
    "*.log",
    "*.user",
    "*\installer-output.txt",
    "*\build-output.txt",
    "*.zip"
)

$tempFolder = Join-Path $env:TEMP ("GlideHandover_" + [System.Guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Path $tempFolder | Out-Null

try {
    Get-ChildItem -Path $rootDir -Recurse | ForEach-Object {
        $rel = $_.FullName.Substring($rootDir.Length).TrimStart('\', '/')
        if (-not $rel) { return }

        $excluded = $false
        foreach ($pat in $excludePatterns) {
            if ($_.FullName -like $pat) {
                $excluded = $true
                break
            }
        }

        if (-not $excluded) {
            $dest = Join-Path $tempFolder $rel
            if ($_.PSIsContainer) {
                if (-not (Test-Path $dest)) {
                    New-Item -ItemType Directory -Path $dest -Force | Out-Null
                }
            } else {
                $parentDir = Split-Path $dest
                if (-not (Test-Path $parentDir)) {
                    New-Item -ItemType Directory -Path $parentDir -Force | Out-Null
                }
                Copy-Item -Path $_.FullName -Destination $dest -Force
            }
        }
    }

    if (Test-Path $resolvedOut) {
        Remove-Item $resolvedOut -Force
    }

    [System.IO.Compression.ZipFile]::CreateFromDirectory($tempFolder, $resolvedOut, [System.IO.Compression.CompressionLevel]::Optimal, $false)
    $zipSize = (Get-Item $resolvedOut).Length / 1MB
    Write-Host ("Successfully generated zero-context handover ZIP: {0:N2} MB" -f $zipSize)
}
finally {
    Remove-Item -Path $tempFolder -Recurse -Force -ErrorAction SilentlyContinue
}
