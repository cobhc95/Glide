<#
.SYNOPSIS
  Time to all Explorer thumbnails of a folder (headless; never launches Glide.exe; the thumbnail disk cache is bypassed).

.DESCRIPTION
  Runs Glide's production thumbnail pipeline (I/O stage, decode lanes, the same decoders as the Explorer tab) over every picture in
  -Folder, all requested at once as the whole-folder preparation does, and prints "time to all thumbnails", the first tile, the decode
  time per file and the peak memory. Read-only on the folder: only timings are printed, never a file name or a pixel. The disk cache is
  off, so every run decodes (the operating system's file cache may still hold the files: the first run after a reboot is the cold one).
  -Lanes runs once per value ("Automatic" = this PC's automatic lane count). -Real also runs the real Explorer tab headlessly
  (tests\Glide.Explorer.Tests, ExplorerLargeProgressiveTimingTests) and prints its first screen, all-prepared and last-screen times.

.EXAMPLE
  powershell -NoProfile -ExecutionPolicy Bypass -File tools\perf\thumb-folder-time.ps1 -Folder "D:\Pictures\Some folder"
  powershell -NoProfile -ExecutionPolicy Bypass -File tools\perf\thumb-folder-time.ps1 -Folder D:\Pictures\X -Lanes Automatic,16,24,32 -Real
#>
param(
    [Parameter(Mandatory = $true)][string]$Folder,
    [string[]]$Lanes = @("Automatic"),
    [int]$Width = 152,
    [switch]$Real,
    [switch]$NoBuild
)
$ErrorActionPreference = "Stop"
$project = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$bench = Join-Path $project ".artifacts\bin\Glide.PerfBench\release\Glide.PerfBench.exe"
if (-not (Test-Path -LiteralPath $Folder -PathType Container)) { throw "Folder not found: $Folder" }
if (-not $NoBuild -or -not (Test-Path $bench)) {
    & dotnet build (Join-Path $project "tools\perf\Glide.PerfBench") -c Release -v q -nologo | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "Glide.PerfBench build failed" }
}
$lanesList = @($Lanes -split '[,; ]+' | Where-Object { $_ })
$autoLanes = [Math]::Min(12, [Math]::Max(2, [int]([Environment]::ProcessorCount * 3 / 4)))
foreach ($lane in $lanesList) {
    $n = if ($lane -eq "Automatic") { $autoLanes } else { [int]$lane }
    $json = & $bench thumb-pipeline --dir $Folder --all-formats 1 --lanes $n --width $Width --win-cache 1 --label $lane | Out-String
    if ($LASTEXITCODE -ne 0) { throw "benchmark failed: $json" }
    $r = $json | ConvertFrom-Json
    "{0,-10} lanes {1,2}: time to all thumbnails {2,7:N0} ms for {3} files ({4} shown), first {5,5:N0} ms, decode median {6,5:N1} ms, peak decodes {7}, peak memory +{8:N0} MB" -f `
        $lane, $n, $r.totalMs, $r.files, $r.tilesOk, $r.firstMs, $r.decodeMs.median, $r.peakDecodeInFlight, $r.peakPrivateMbAboveStart
}
if ($Real) {
    foreach ($lane in $lanesList) {
        $env:GLIDE_THUMB_TIMING_DIR = $Folder
        $env:GLIDE_THUMB_TIMING_LANES = $lane
        try {
            $out = & dotnet test (Join-Path $project "tests\Glide.Explorer.Tests") -c Release --filter "FullyQualifiedName~ExplorerLargeProgressiveTimingTests" --logger "console;verbosity=detailed" 2>&1 | Out-String
        } finally { Remove-Item Env:GLIDE_THUMB_TIMING_DIR, Env:GLIDE_THUMB_TIMING_LANES -ErrorAction SilentlyContinue }
        $line = ($out -split "`r?`n" | Where-Object { $_ -match "files=" } | Select-Object -First 1)
        "real tab   {0,-9}: {1}" -f $lane, ($(if ($line) { $line.Trim() } else { "no result (see dotnet test output)" }))
    }
}
