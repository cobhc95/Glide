<#
.SYNOPSIS
  Repeatable thumbnail + first-paint benchmark (headless; never launches Glide.exe).

.DESCRIPTION
  1. generates the fixtures (tools\perf\generate_thumb_fixtures.py) when the data folder is empty,
  2. `Glide.PerfBench thumb`      warm Explorer thumbnails per file and width (median of -Runs) plus disk-cache miss/hit,
  3. `Glide.PerfBench thumb-one`  cold thumbnail = first decode in a fresh process, median of -ColdRuns processes,
  4. `Glide.PerfBench decode-one` / `decode`  full-image first paint: cold (median of -ColdRuns processes) and warm.
  The output folder gets thumb-<label>.json/.md, thumb-cold-<label>.json, decode-<label>.json/.md and a load log.
  Every sample's machine load (total CPU %) is recorded; a phase that ran above -BusyPercent is flagged BUSY.

.EXAMPLE
  powershell -NoProfile -ExecutionPolicy Bypass -File tools\perf\run-thumb-bench.ps1 -Data $env:TEMP\glide-thumb-bench\data -Out $env:TEMP\glide-thumb-bench\results -Label before
#>
param(
    [Parameter(Mandatory = $true)][string]$Data,
    [Parameter(Mandatory = $true)][string]$Out,
    [string]$Label = "run",
    [string]$BenchExe = "",
    [int]$Runs = 7,
    [int]$ColdRuns = 5,
    [int]$BusyPercent = 35,
    [string]$Widths = "152,236",
    [string]$Phases = "thumb,cold,firstpaint"
)
$ErrorActionPreference = "Stop"
$project = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
if (-not $BenchExe) { $BenchExe = Join-Path $project ".artifacts\bin\Glide.PerfBench\release\Glide.PerfBench.exe" }
$phaseList = @($Phases -split '[,; ]+' | Where-Object { $_ })
New-Item -ItemType Directory -Force $Data, $Out | Out-Null
if (-not (Test-Path (Join-Path $Data "single") -PathType Container) -or -not (Get-ChildItem (Join-Path $Data "single") -ErrorAction SilentlyContinue)) {
    & python (Join-Path $PSScriptRoot "generate_thumb_fixtures.py") $Data
    if ($LASTEXITCODE -ne 0) { throw "fixture generation failed" }
}
$files = Get-ChildItem (Join-Path $Data "single") -File | Where-Object { $_.Extension -ne ".tmp" } | Sort-Object Name
$widthList = @($Widths -split ',' | ForEach-Object { [int]$_ })
$loadLog = New-Object System.Collections.Generic.List[object]

function Get-CpuLoad { [int]((Get-CimInstance Win32_Processor | Measure-Object LoadPercentage -Average).Average) }
function Median([double[]]$v) {
    $v = @($v | Where-Object { -not [double]::IsNaN($_) } | Sort-Object)
    if ($v.Count -eq 0) { return [double]::NaN }
    $m = [int][math]::Floor($v.Count / 2)
    if ($v.Count % 2 -eq 1) { return $v[$m] } else { return ($v[$m - 1] + $v[$m]) / 2 }
}
function Invoke-Bench([string[]]$BenchArgs, [int]$TimeoutSec = 600) {
    $quoted = $BenchArgs | ForEach-Object { if ($_ -match '[\s"]') { '"' + $_.Replace('"', '\"') + '"' } else { $_ } }
    $stdout = Join-Path $Out "last.out.txt"; $stderr = Join-Path $Out "last.err.txt"
    $p = Start-Process $BenchExe -ArgumentList ($quoted -join ' ') -NoNewWindow -PassThru -RedirectStandardOutput $stdout -RedirectStandardError $stderr
    if (-not $p.WaitForExit($TimeoutSec * 1000)) { Stop-Process -Id $p.Id -Force; throw "bench timeout: $($BenchArgs -join ' ')" }
    return $p.ExitCode
}
function Note-Load([string]$phase) {
    $cpu = Get-CpuLoad
    $loadLog.Add([pscustomobject]@{ phase = $phase; cpu = $cpu; busy = ($cpu -ge $BusyPercent); at = (Get-Date).ToString("HH:mm:ss") })
}

if ($phaseList -contains "thumb") {
    Note-Load "thumb-start"
    $null = Invoke-Bench @("thumb", "--data", $Data, "--out", $Out, "--label", $Label, "--runs", "$Runs", "--widths", $Widths) 3000
    Note-Load "thumb-end"
}

if ($phaseList -contains "cold") {
    $cold = @()
    foreach ($file in $files) {
        foreach ($w in $widthList) {
            $samples = @(); $size = ""
            for ($i = 0; $i -lt $ColdRuns; $i++) {
                $json = Join-Path $Out "cold-one.json"
                Remove-Item $json -ErrorAction SilentlyContinue
                $null = Invoke-Bench @("thumb-one", $file.FullName, "--width", "$w", "--json", $json) 300
                if (Test-Path $json) {
                    $node = Get-Content $json -Raw | ConvertFrom-Json
                    $size = $node.size
                    if ($null -ne $node.coldMs) { $samples += [double]$node.coldMs }
                }
            }
            $min = if ($samples.Count) { ($samples | Measure-Object -Minimum).Minimum } else { $null }
            $max = if ($samples.Count) { ($samples | Measure-Object -Maximum).Maximum } else { $null }
            $cold += [pscustomobject]@{ file = $file.Name; width = $w; coldMs = (Median $samples); minMs = $min; maxMs = $max; size = $size; samples = $samples }
            Write-Host ("cold thumb {0}@{1}: {2:N1} ms {3}" -f $file.Name, $w, (Median $samples), $size)
        }
        Note-Load "cold-$($file.Name)"
    }
    $cold | ConvertTo-Json -Depth 4 | Set-Content (Join-Path $Out "thumb-cold-$Label.json") -Encoding utf8
}

if ($phaseList -contains "firstpaint") {
    $coldDir = Join-Path $Out "firstpaint-cold-$Label"
    New-Item -ItemType Directory -Force $coldDir | Out-Null
    foreach ($file in $files) {
        $first = @(); $settled = @(); $full = @()
        for ($i = 0; $i -lt $ColdRuns; $i++) {
            $json = Join-Path $coldDir "one.json"
            Remove-Item $json -ErrorAction SilentlyContinue
            $null = Invoke-Bench @("decode-one", $file.FullName, "--json", $json) 300
            if (Test-Path $json) {
                $node = Get-Content $json -Raw | ConvertFrom-Json
                if ($null -ne $node.coldFirstMs) { $first += [double]$node.coldFirstMs }
                if ($null -ne $node.coldFullMs) { $full += [double]$node.coldFullMs }
            }
        }
        $obj = [ordered]@{ file = $file.Name; coldFirstMs = (Median $first); coldFullMs = (Median $full) }
        ($obj | ConvertTo-Json) | Set-Content (Join-Path $coldDir ($file.Name + ".json")) -Encoding utf8
        Write-Host ("cold first paint {0}: {1:N1} ms" -f $file.Name, (Median $first))
        Note-Load "firstpaint-cold-$($file.Name)"
    }
    Note-Load "decode-start"
    $null = Invoke-Bench @("decode", "--data", $Data, "--out", $Out, "--label", $Label, "--runs", "$Runs", "--cold-dir", $coldDir) 3000
    Note-Load "decode-end"
}

$loadLog | ConvertTo-Json | Set-Content (Join-Path $Out "load-$Label.json") -Encoding utf8
$busy = @($loadLog | Where-Object { $_.busy })
if ($busy.Count -gt 0) { Write-Warning ("BUSY samples ({0}): {1}" -f $busy.Count, (($busy | ForEach-Object { "$($_.phase)=$($_.cpu)%" }) -join ", ")) }
Write-Host "done: $Out"
