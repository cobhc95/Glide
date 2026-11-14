<#
.SYNOPSIS
  Repeatable Glide performance harness (decode, navigation, zoom/pan, UI snappiness, app first frame).

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File tools\perf\run-perf.ps1 -Data <corpus> -Out <results> -Label baseline

  The corpus comes from tools\perf\generate_perf_data.py. Every Glide process launched here is this
  project's own Release build, runs with an isolated GLIDE_SETTINGS_DIRECTORY and
  --force-new-instance --benchmark-exit-after-first-frame (no Open-With registration, no single-instance
  forwarding to an installed Glide), and is killed if it outlives its timeout.
#>
param(
    [Parameter(Mandatory = $true)][string]$Data,
    [Parameter(Mandatory = $true)][string]$Out,
    [string]$Label = "run",
    [string]$Modes = "cold,decode,navigate,render,ui,app",
    [int]$Runs = 5,
    [int]$AppRuns = 5,
    [string]$AppExe = "",
    [string]$BenchExe = "",
    [switch]$NoBuild
)
$ErrorActionPreference = "Stop"
$ModeList = @($Modes -split '[,; ]+' | Where-Object { $_ })
$project = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$Data = (Resolve-Path $Data).Path
$resultDir = Join-Path $Out $Label
New-Item -ItemType Directory -Force $resultDir | Out-Null
$bench = if ($BenchExe) { $BenchExe } else { Join-Path $project ".artifacts\bin\Glide.PerfBench\release\Glide.PerfBench.exe" }
if (-not $AppExe) { $AppExe = Join-Path $project ".artifacts\bin\Glide.App\release\Glide.exe" }

if (-not $NoBuild) {
    & dotnet build (Join-Path $project "tools\perf\Glide.PerfBench\Glide.PerfBench.csproj") -c Release -nologo -v q -clp:ErrorsOnly
    if ($LASTEXITCODE -ne 0) { throw "bench build failed" }
}

function Invoke-Bench([string[]]$BenchArgs, [int]$TimeoutSec = 1800) {
    $quoted = $BenchArgs | ForEach-Object { if ($_ -match '[\s"]') { '"' + $_.Replace('"', '\"') + '"' } else { $_ } }
    $tag = ($BenchArgs[0] + "-" + [IO.Path]::GetFileName([string]$BenchArgs[1])) -replace '[^\w\.-]', '_'; $stdout = Join-Path $resultDir "log-$tag.out.txt"; $stderr = Join-Path $resultDir "log-$tag.err.txt"
    $p = Start-Process $bench -ArgumentList ($quoted -join ' ') -NoNewWindow -PassThru -RedirectStandardOutput $stdout -RedirectStandardError $stderr
    if (-not $p.WaitForExit($TimeoutSec * 1000)) { Stop-Process -Id $p.Id -Force; throw "bench timeout: $($BenchArgs -join ' ')" }
    Get-Content $stderr | Where-Object { $_ -match 'FAILED|Exception' } | ForEach-Object { Write-Warning $_ }
    return $p.ExitCode
}

$coldDir = Join-Path $resultDir "cold"
if ($ModeList -contains "cold") {
    New-Item -ItemType Directory -Force $coldDir | Out-Null
    foreach ($file in Get-ChildItem (Join-Path $Data "single") -File | Where-Object { $_.Extension -ne ".tmp" }) {
        $null = Invoke-Bench @("decode-one", $file.FullName, "--json", (Join-Path $coldDir ($file.Name + ".json"))) 300
        Write-Host "cold $($file.Name)"
    }
}
if ($ModeList -contains "decode") { $null = Invoke-Bench @("decode", "--data", $Data, "--out", $resultDir, "--label", $Label, "--runs", "$Runs", "--cold-dir", $coldDir) }
if ($ModeList -contains "navigate") { $null = Invoke-Bench @("navigate", "--data", $Data, "--out", $resultDir, "--label", $Label, "--repeats", "3") }
if ($ModeList -contains "render") { $null = Invoke-Bench @("render", "--data", $Data, "--out", $resultDir, "--label", $Label) }
if ($ModeList -contains "ui") { $null = Invoke-Bench @("ui", "--data", $Data, "--out", $resultDir, "--label", $Label, "--trace", (Join-Path $resultDir "ui-trace.tsv")) 900 }

if ($ModeList -contains "app") {
    # End-to-end: process launch -> first composed frame of the image (the app signals a named event
    # after its presentation fence), i.e. what the user sees when double-clicking a file.
    $settings = Join-Path $resultDir ("isolated-settings-" + [guid]::NewGuid().ToString("N"))
    New-Item -ItemType Directory -Force $settings | Out-Null
    $prev = @{}; foreach ($k in "GLIDE_SETTINGS_DIRECTORY", "GLIDE_BENCHMARK_DISABLE_REUSE") { $prev[$k] = [Environment]::GetEnvironmentVariable($k, "Process") }
    [Environment]::SetEnvironmentVariable("GLIDE_SETTINGS_DIRECTORY", $settings, "Process")
    [Environment]::SetEnvironmentVariable("GLIDE_BENCHMARK_DISABLE_REUSE", "1", "Process")
    $files = @("jpeg-base-small.jpg", "jpeg-base-24mp.jpg", "jpeg-prog-24mp.jpg", "jpeg-base-100mp.jpg", "png-24mp.png", "webp-24mp.webp", "tiff-lzw-24mp.tif", "gif-anim-1080p-60f.gif")
    $rows = @()
    try {
        # One untimed launch warms the OS file cache for the executable and its DLLs.
        $warm = Start-Process $AppExe -ArgumentList @("--force-new-instance", "--benchmark-exit-after-first-frame", ('"' + (Join-Path $Data "single\jpeg-base-small.jpg") + '"')) -PassThru
        if (-not $warm.WaitForExit(8000)) { Stop-Process -Id $warm.Id -Force -ErrorAction SilentlyContinue }
        foreach ($name in $files) {
            $image = Join-Path $Data "single\$name"
            if (-not (Test-Path $image)) { continue }
            for ($i = 1; $i -le $AppRuns; $i++) {
                $eventName = "GlidePerf_" + [guid]::NewGuid().ToString("N")
                $ready = New-Object System.Threading.EventWaitHandle($false, [System.Threading.EventResetMode]::ManualReset, $eventName)
                $trace = Join-Path $resultDir ("app-trace-{0}-{1}.tsv" -f $name, $i)
                $argList = @("--force-new-instance", "--perf-trace", ('"' + $trace + '"'), "--notify-first-frame", $eventName, "--benchmark-exit-after-first-frame", ('"' + $image + '"'))
                $sw = [Diagnostics.Stopwatch]::StartNew()
                $p = Start-Process $AppExe -ArgumentList ($argList -join ' ') -PassThru
                $signalled = $ready.WaitOne(30000)
                $firstFrame = $sw.Elapsed.TotalMilliseconds
                # With Speed Boost on (the default) closing the last window parks the process in standby instead
                # of exiting, so a benchmark copy is terminated as soon as its first frame has been signalled.
                if (-not $p.WaitForExit(1500)) { Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue }
                $ready.Dispose()
                $rows += [pscustomobject]@{ File = $name; Run = $i; FirstFrameMs = $(if ($signalled) { [math]::Round($firstFrame, 1) } else { $null }) }
                Start-Sleep -Milliseconds 300
            }
        }
    }
    finally {
        foreach ($k in $prev.Keys) { [Environment]::SetEnvironmentVariable($k, $prev[$k], "Process") }
        Get-Process Glide -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $AppExe } | Stop-Process -Force -ErrorAction SilentlyContinue
        Remove-Item -Recurse -Force $settings -ErrorAction SilentlyContinue
    }
    $rows | Export-Csv -NoTypeInformation (Join-Path $resultDir "app-first-frame.csv")
    $md = @("## App launch to first composed frame ($Label) - $AppRuns launches per file, median", "", "| File | Median ms | Min ms | Max ms |", "|---|---:|---:|---:|")
    foreach ($group in $rows | Group-Object File) {
        $v = @($group.Group | Where-Object { $_.FirstFrameMs } | ForEach-Object { [double]$_.FirstFrameMs } | Sort-Object)
        if ($v.Count -eq 0) { $md += "| $($group.Name) | timeout | | |"; continue }
        $median = if ($v.Count % 2) { $v[[int][math]::Floor($v.Count / 2)] } else { ($v[$v.Count / 2 - 1] + $v[$v.Count / 2]) / 2 }
        $md += "| $($group.Name) | $([math]::Round($median,1)) | $($v[0]) | $($v[-1]) |"
    }
    $md -join "`n" | Set-Content (Join-Path $resultDir "app-$Label.md") -Encoding utf8
    $md -join "`n" | Write-Host
}

# Single combined summary for the label.
$summary = @("# Glide perf results: $Label", "", "Machine: $env:PROCESSOR_IDENTIFIER, logical CPUs $([Environment]::ProcessorCount), RAM $([math]::Round((Get-CimInstance Win32_ComputerSystem).TotalPhysicalMemory / 1GB, 1)) GB", "")
foreach ($part in "decode", "navigate", "render", "ui", "app") {
    $file = Join-Path $resultDir "$part-$Label.md"
    if (Test-Path $file) { $summary += Get-Content $file -Raw; $summary += "" }
}
$summary -join "`n" | Set-Content (Join-Path $resultDir "summary.md") -Encoding utf8
Write-Host "Summary: $(Join-Path $resultDir 'summary.md')"
