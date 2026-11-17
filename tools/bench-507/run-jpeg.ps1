<#
.SYNOPSIS
  5.0.7 JPEG first-frame round (docs\perf\jpeg-first-frame-plan-5.0.7.md). One serial harness run of Glide's
  production open path over the JPEG corpus. -Bridge picks the Glide.Native.dll under test (copied next to the
  harness first); -JpegMode sets GLIDE_JPEG_BENCH for the prototype bridge's bench-only switches.
  Short idle check (500 ms at <= 30 % CPU, at most 5 s), then the case runs anyway and the busy % is recorded.
#>
param([Parameter(Mandatory)][string]$Label, [string]$Bridge = "", [string]$JpegMode = "",
      [string]$Sets = "synthetic,real,derived", [string]$Modes = "preview,full", [string]$Engines = "glide",
      [int]$Reps = 5, [int]$RepsLarge = 3, [string]$Variant = "", [string]$Extra = "")
$ErrorActionPreference = "Stop"
$repo = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$bin = Join-Path $repo "tools\bench-507\.out\bin\Glide.DecodeBench507\release"
$exe = Join-Path $bin "Glide.DecodeBench507.exe"
$real = "$env:USERPROFILE\Downloads\Glide-bench-corpus"
$corp = @()
foreach ($s in $Sets -split ',') {
    switch ($s) {
        "synthetic" { $corp += "synthetic:$env:USERPROFILE\Downloads\Glide-bench-synthetic" }
        "real"      { $corp += "real:$real" }
        "derived"   { $corp += "real:$real\derived" }
    }
}
if ($Bridge) { Copy-Item $Bridge (Join-Path $bin "Glide.Native.dll") -Force }
$env:GLIDE_JPEG_BENCH = $JpegMode
$args2 = @("run", "--corpus", ($corp -join ';'), "--format", "jpeg,jpg", "--engines", $Engines, "--modes", $Modes,
    "--box", "2560x1369", "--out", "$env:USERPROFILE\Downloads\Glide-bench-results", "--label", $Label,
    "--idle-check", "case", "--idle-threshold", "30", "--idle-window", "500", "--idle-timeout", "5000",
    "--shuffle", "507", "--warmup", "1", "--reps", "$Reps", "--reps-large", "$RepsLarge", "--confirm")
if ($Variant) { $args2 += @("--variant", $Variant) }
if ($Extra) { $args2 += ($Extra -split ' ') }
& $exe @args2
exit $LASTEXITCODE
