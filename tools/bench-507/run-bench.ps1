<#
.SYNOPSIS
  Glide 5.0.7 decode/open-speed benchmark round (plan: docs\perf\benchmark-plan-5.0.7.md).

.DESCRIPTION
  Without -Confirm this only builds the harness (isolated output, below-normal priority) and prints the case
  matrix plus the estimated run time. With -Confirm it generates the synthetic corpus (if missing) and measures,
  serially, one harness process per group. The PC must be otherwise idle: the harness waits for idle CPU before
  every case and records the busy percentage of every sample.

  Groups (separate processes: two different libvips-42.dll builds cannot share one process):
    A  synthetic  glide, wic, skia, turbojpeg(+fast, bands, mmap), vips, libraw, djxl, dwebp, avifdec, opj
    B  synthetic  vips-all (JPEG XL, JPEG 2000 through libvips' libjxl/OpenJPEG)
    C  real       same engines as A on $env:USERPROFILE\Downloads\Glide-bench-corpus

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File tools\bench-507\run-bench.ps1                       # plan only
  powershell -ExecutionPolicy Bypass -File tools\bench-507\run-bench.ps1 -Tier quick -Confirm  # ~15-20 min
  powershell -ExecutionPolicy Bypass -File tools\bench-507\run-bench.ps1 -Tier full -Confirm   # ~1.5-2 h + generation
#>
param(
    [ValidateSet("quick", "full")][string]$Tier = "quick",
    [string]$Synthetic = "$env:USERPROFILE\Downloads\Glide-bench-synthetic",
    [string]$Real = "$env:USERPROFILE\Downloads\Glide-bench-corpus",
    [string]$Codecs = "$env:USERPROFILE\Downloads\Glide-bench-codecs",
    [string]$Out = "$env:USERPROFILE\Downloads\Glide-bench-results",
    [string]$Label = ("5.0.7-" + (Get-Date -Format "yyyyMMdd-HHmm")),
    [string]$Groups = "A,B,C",
    [string]$Engines = "glide,wic,skia,turbojpeg,turbojpeg-fast,turbojpeg-bands,turbojpeg-mmap,vips,libraw,djxl,dwebp,avifdec,opj",
    [string]$Box = "2560x1369",
    [string]$Affinity = "",
    [int]$GenerateJobs = 2,
    [switch]$NoBuild,
    [switch]$Confirm
)
$ErrorActionPreference = "Stop"
$repo = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$art = Join-Path $repo "tools\bench-507\.out"
$exe = Join-Path $art "bin\Glide.DecodeBench507\release\Glide.DecodeBench507.exe"

if (-not $NoBuild) {
    # Isolated ArtifactsPath: never shares obj/bin with a concurrent build of src\ or build.cmd.
    $p = Start-Process dotnet -NoNewWindow -PassThru -ArgumentList ("build `"$repo\tools\bench-507\Glide.DecodeBench507\Glide.DecodeBench507.csproj`" -c Release -m:1 -nologo -v q -clp:ErrorsOnly `"-p:ArtifactsPath=$art`"")
    $null = $p.Handle # PowerShell 5.1: cache the handle or ExitCode stays null
    try { $p.PriorityClass = "BelowNormal" } catch { }
    $p.WaitForExit()
    if ($p.ExitCode -ne 0) { throw "harness build failed" }
}

$tierArgs = if ($Tier -eq "quick") {
    @("--reps", "5", "--reps-large", "3", "--warmup", "1", "--cold-reps", "0")
} else {
    @("--reps", "7", "--reps-large", "5", "--warmup", "1", "--cold-reps", "3", "--cold-max-mp", "30")
}
$common = @("--box", $Box, "--baseline", "glide", "--engines-config", (Join-Path (Split-Path $exe) "engines.json"), "--out", $Out, "--idle-check", "case", "--idle-threshold", "15", "--shuffle", "507")
if ($Affinity) { $common += @("--affinity", $Affinity) }
$realLimit = if ($Tier -eq "quick") { @("--max-per-format", "2") } else { @() }
$synthLimit = if ($Tier -eq "quick") { @("--mp", "24") } else { @() }

$groupList = @($Groups -split "," | ForEach-Object { $_.Trim().ToUpperInvariant() } | Where-Object { $_ })
$runs = @()
if ($groupList -contains "A") { $runs += , @("A-synthetic", "synthetic:$Synthetic", $Engines, $synthLimit) }
if ($groupList -contains "B") { $runs += , @("B-synthetic-vipsall", "synthetic:$Synthetic", "vips-all", (@("--format", "jxl,jp2") + $synthLimit)) }
if ($groupList -contains "C") { $runs += , @("C-real", "real:$Real", $Engines, $realLimit) }

# ---- synthetic corpus ----
$manifest = Join-Path $Synthetic "manifest.csv"
$genArgs = @("$repo\tools\bench-507\generate_synthetic.py", $Synthetic, "--tier", $Tier, "--jobs", "$GenerateJobs",
    "--cjxl", (Join-Path $Codecs "libjxl\jxl-x64-windows-x\x64-windows\bin\cjxl.exe"),
    "--vips", (Join-Path $Codecs "libvips\vips-dev-x64-all-8.18.7-x\vips-dev-8.18\bin\vips.exe"))
if (-not (Test-Path $manifest)) {
    Write-Host "Synthetic corpus missing ($Synthetic). Generation matrix:"
    & python @genArgs --list
    if ($Confirm) {
        & python @genArgs --confirm
        if ($LASTEXITCODE -ne 0) { throw "synthetic generation failed" }
    }
}

foreach ($r in $runs) {
    $name, $corpus, $engineList, $extra = $r
    if ($corpus -like "synthetic:*" -and -not (Test-Path $manifest)) { Write-Host "[$name] skipped: synthetic corpus not generated yet"; continue }
    Write-Host "==== $name ($Tier) ===="
    $benchArgs = @(if ($Confirm) { "run" } else { "plan" }) + @("--corpus", $corpus, "--engines", $engineList, "--label", "$Label-$name") + $tierArgs + $common + @($extra)
    if ($Confirm) { $benchArgs += "--confirm" }
    & $exe @benchArgs
    if ($LASTEXITCODE -ne 0) { Write-Warning "$name exited with $LASTEXITCODE" }
}
if (-not $Confirm) { Write-Host "`nPlan only. Re-run with -Confirm once the user agrees (PC idle, plugged in, Best performance power mode)." }
