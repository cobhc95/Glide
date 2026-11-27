<#
.SYNOPSIS
  All-format speed audit driver (5.2.0). Headless: never launches Glide.exe, never touches an installed Glide.

.DESCRIPTION
  Per fixture in <Data>\single (tools\perf\generate_format_fixtures.py), writes one JSON cell per phase to <Out>\cells\<phase>\<file>.json:
    route        Explorer thumbnail route + whether the Explorer tab asks for a thumbnail for that extension      (Glide.FormatAudit route)
    thumb-warm   Explorer thumbnail 236 px, median of -Runs in one process + disk-cache hit                        (Glide.PerfBench thumb)
    thumb-cold   first thumbnail in a fresh process, median of -ColdRuns processes                                 (Glide.PerfBench thumb-one)
    view-cold    viewer first frame / full decode, first decode in a fresh process, median of -ColdRuns            (Glide.FormatAudit view-one: the app's backend, codec plugins included)
    view-warm    viewer first frame / settled / full decode / route, median of -Runs                               (Glide.FormatAudit view)
    vsmall-warm  viewer first frame bounded to 480 px ("as-if thumbnail" for any format), median of -Runs         (Glide.FormatAudit vsmall)
    vsmall-cold  the same, first decode in a fresh process
    prov-warm / prov-cold   the codec plug-in's preview call at -Width px (what the Explorer pipeline asks for HEIC / AVIF / JPEG XL / JPEG 2000), median of -Runs / first call in a fresh process
    inbox-warm / inbox-cold   Windows' own Photo Thumbnail Provider (WIC), 236 px, in-process IThumbnailProvider
    glide-warm / glide-cold   Glide's shell thumbnail provider DLL (repo build), 236 px, in-process IThumbnailProvider
    factory      IShellItemImageFactory on fresh copies (only for types whose registered handler is not Glide's DLL)
  Every phase records machine load; a cell started above -BusyPercent is flagged. Re-run flagged files with -Only a.jpg,b.png (cells are overwritten).

.EXAMPLE
  powershell -NoProfile -ExecutionPolicy Bypass -File tools\perf\run-format-audit.ps1 -Data D:\data -Out D:\results -Bin D:\art\bin
#>
param(
    [Parameter(Mandatory = $true)][string]$Data,
    [Parameter(Mandatory = $true)][string]$Out,
    [Parameter(Mandatory = $true)][string]$Bin,           # folder holding Glide.PerfBench\release and Glide.FormatAudit\release
    [string]$GlideShellDll = "",                          # Glide.ShellThumbnail.dll (repo build) with a codecs\ folder next to it
    [int]$Runs = 7,
    [int]$ColdRuns = 5,
    [int]$Width = 236,
    [int]$BusyPercent = 35,
    [string]$Phases = "route,thumb-warm,thumb-cold,view-cold,view-warm,vsmall-warm,vsmall-cold,prov-warm,prov-cold,inbox-warm,inbox-cold,glide-warm,glide-cold,factory",
    [string]$Only = "",                                   # comma list of file names to (re)measure
    [string]$Label = "main"
)
$ErrorActionPreference = "Stop"
$perf = Join-Path $Bin "Glide.PerfBench\release\Glide.PerfBench.exe"
$audit = Join-Path $Bin "Glide.FormatAudit\release\Glide.FormatAudit.exe"
$phaseList = @($Phases -split '[,; ]+' | Where-Object { $_ })
$cells = Join-Path $Out "cells"
New-Item -ItemType Directory -Force $Out, $cells | Out-Null
$all = Get-ChildItem (Join-Path $Data "single") -File | Where-Object { $_.Extension -ne ".tmp" } | Sort-Object Name
$onlyList = @($Only -split '[,;]' | Where-Object { $_ })
$files = if ($onlyList.Count) { @($all | Where-Object { $onlyList -contains $_.Name }) } else { $all }
$loadLog = New-Object System.Collections.Generic.List[object]
$scratch = Join-Path $Out "tmp"; New-Item -ItemType Directory -Force $scratch | Out-Null

function Get-CpuLoad { [int]((Get-CimInstance Win32_Processor | Measure-Object LoadPercentage -Average).Average) }
function Median([double[]]$v) {
    $v = @($v | Where-Object { -not [double]::IsNaN($_) } | Sort-Object)
    if ($v.Count -eq 0) { return $null }
    $m = [int][math]::Floor($v.Count / 2)
    if ($v.Count % 2 -eq 1) { return $v[$m] } else { return ($v[$m - 1] + $v[$m]) / 2 }
}
function Run-Exe([string]$exe, [string[]]$a, [int]$timeoutSec = 900) {
    $q = $a | ForEach-Object { if ($_ -match '[\s"]') { '"' + $_.Replace('"', '\"') + '"' } else { $_ } }
    $o = Join-Path $scratch "out.txt"; $e = Join-Path $scratch "err.txt"
    $p = Start-Process $exe -ArgumentList ($q -join ' ') -NoNewWindow -PassThru -RedirectStandardOutput $o -RedirectStandardError $e
    if (-not $p.WaitForExit($timeoutSec * 1000)) { Stop-Process -Id $p.Id -Force; return @{ code = -1; out = "timeout" } }
    return @{ code = $p.ExitCode; out = (Get-Content $o -Raw -ErrorAction SilentlyContinue) }
}
function Cell-Path([string]$phase, [string]$name) { $d = Join-Path $cells $phase; New-Item -ItemType Directory -Force $d | Out-Null; Join-Path $d ($name + ".json") }
function Save-Cell([string]$phase, [string]$name, $obj, [int]$cpu) {
    $obj | Add-Member -NotePropertyName cpuAtStart -NotePropertyValue $cpu -Force
    $obj | Add-Member -NotePropertyName busy -NotePropertyValue ($cpu -ge $BusyPercent) -Force
    $obj | Add-Member -NotePropertyName label -NotePropertyValue $Label -Force
    $obj | ConvertTo-Json -Depth 5 | Set-Content (Cell-Path $phase $name) -Encoding utf8
}
function Note-Load([string]$what) { $c = Get-CpuLoad; $loadLog.Add([pscustomobject]@{ at = $what; cpu = $c; busy = ($c -ge $BusyPercent); t = (Get-Date).ToString("HH:mm:ss") }); return $c }
function Cold-Runs([string]$exe, [string[]]$prefixArgs, [string]$file, [string[]]$suffixArgs, [string[]]$fields) {
    $series = @{}; foreach ($f in $fields) { $series[$f] = @() }
    $meta = @{}
    for ($i = 0; $i -lt $ColdRuns; $i++) {
        $json = Join-Path $scratch "one.json"; Remove-Item $json -ErrorAction SilentlyContinue
        $null = Run-Exe $exe (@($prefixArgs) + @($file) + @($suffixArgs) + @("--json", $json)) 300
        if (Test-Path $json) {
            $n = Get-Content $json -Raw | ConvertFrom-Json
            foreach ($f in $fields) { if ($null -ne $n.$f) { $series[$f] += [double]$n.$f } }
            foreach ($p in $n.PSObject.Properties) { if ($p.Name -notin $fields) { $meta[$p.Name] = $p.Value } }
        }
    }
    $r = [ordered]@{ file = (Split-Path $file -Leaf) }
    foreach ($f in $fields) { $r[$f] = (Median $series[$f]); $r[$f + "Samples"] = $series[$f] }
    foreach ($k in $meta.Keys) { $r["meta_$k"] = $meta[$k] }
    return [pscustomobject]$r
}
function Split-WarmJson([string]$phase, [string]$jsonFile, [int]$cpu) {
    if (-not (Test-Path $jsonFile)) { return }
    $doc = Get-Content $jsonFile -Raw | ConvertFrom-Json
    foreach ($it in $doc.items) { Save-Cell $phase $it.file $it $cpu }
}
function Filter-Arg { if ($onlyList.Count -eq 1) { return @("--filter", $onlyList[0]) } else { return @() } }

# Warm modes take a corpus folder, so a partial re-run gets a folder of hard links / copies of just the requested files.
function Corpus-For-Files {
    if (-not $onlyList.Count) { return $Data }
    $d = Join-Path $scratch "subset"; Remove-Item $d -Recurse -Force -ErrorAction SilentlyContinue
    New-Item -ItemType Directory -Force (Join-Path $d "single") | Out-Null
    foreach ($f in $files) { New-Item -ItemType HardLink -Path (Join-Path $d "single\$($f.Name)") -Target $f.FullName | Out-Null }
    return $d
}

$corpus = Corpus-For-Files
foreach ($phase in $phaseList) {
    $cpu = Note-Load "$phase-start"
    Write-Host "== $phase (cpu $cpu %)"
    switch ($phase) {
        "route" {
            foreach ($f in $files) {
                $r = Run-Exe $audit @("route", $f.FullName, "--width", "$Width") 120
                if ($r.out) { $n = ($r.out.Trim() -split "`n")[-1] | ConvertFrom-Json; Save-Cell "route" $f.Name $n $cpu }
            }
        }
        "thumb-warm" {
            $o = Join-Path $Out "thumb-warm-$Label"
            $null = Run-Exe $perf @("thumb", "--data", $corpus, "--out", $o, "--label", "w", "--runs", "$Runs", "--widths", "$Width") 3600
            Split-WarmJson "thumb-warm" (Join-Path $o "thumb-w.json") $cpu
        }
        "thumb-cold" {
            foreach ($f in $files) { $c = Get-CpuLoad; Save-Cell "thumb-cold" $f.Name (Cold-Runs $perf @("thumb-one") $f.FullName @("--width", "$Width") @("coldMs")) $c }
        }
        "thumb-cold-preload" {
            # 5.2.0: first tile in a fresh process after the folder listing warmed the codec plug-in / the idle warm-up ran (--preload).
            foreach ($f in ($files | Where-Object { $_.Extension -in '.heic','.heif','.avif','.jxl','.jp2','.j2k','.svg','.svgz' })) {
                $c = Get-CpuLoad; Save-Cell "thumb-cold-preload" $f.Name (Cold-Runs $perf @("thumb-one") $f.FullName @("--width", "$Width", "--preload") @("coldMs")) $c
            }
        }
        "view-cold" {
            foreach ($f in $files) { $c = Get-CpuLoad; Save-Cell "view-cold" $f.Name (Cold-Runs $audit @("view-one") $f.FullName @() @("coldFirstMs", "coldSettledMs", "coldFullMs")) $c }
        }
        "view-cold-preload" {
            # 5.2.0: the first open after the folder listing warmed the codec plug-in / the idle warm-up initialised Svg.Skia (--preload).
            foreach ($f in ($files | Where-Object { $_.Extension -in '.heic','.heif','.avif','.jxl','.jp2','.j2k','.svg','.svgz' })) {
                $c = Get-CpuLoad; Save-Cell "view-cold-preload" $f.Name (Cold-Runs $audit @("view-one") $f.FullName @("--preload") @("coldFirstMs", "coldSettledMs", "coldFullMs")) $c
            }
        }
        "view-warm" {
            $o = Join-Path $Out "view-warm-$Label"
            $null = Run-Exe $audit @("view", "--data", $corpus, "--out", $o, "--label", "w", "--runs", "$Runs") 7200
            Split-WarmJson "view-warm" (Join-Path $o "view-w.json") $cpu
        }
        "vsmall-warm" {
            $o = Join-Path $Out "vsmall-$Label"
            $null = Run-Exe $audit @("vsmall", "--data", $corpus, "--out", $o, "--label", "w", "--runs", "$Runs") 3600
            Split-WarmJson "vsmall-warm" (Join-Path $o "vsmall-w.json") $cpu
        }
        "vsmall-cold" {
            foreach ($f in $files) { $c = Get-CpuLoad; Save-Cell "vsmall-cold" $f.Name (Cold-Runs $audit @("vsmall-one") $f.FullName @() @("coldMs")) $c }
        }
        "prov-warm" {
            $o = Join-Path $Out "provider-$Label"
            $null = Run-Exe $audit @("provider", "--data", $corpus, "--out", $o, "--label", "w", "--runs", "$Runs", "--width", "$Width") 3600
            Split-WarmJson "prov-warm" (Join-Path $o "provider-w.json") $cpu
        }
        "prov-cold" {
            foreach ($f in $files) {
                if ($f.Extension -notin @(".heic", ".heif", ".heics", ".heifs", ".hif", ".avif", ".avifs", ".jxl", ".jp2", ".j2k", ".j2c", ".jpc", ".jpx", ".jpf", ".jpm", ".mj2")) { continue }
                $c = Get-CpuLoad; Save-Cell "prov-cold" $f.Name (Cold-Runs $audit @("provider-one") $f.FullName @("--width", "$Width") @("coldMs")) $c
            }
        }
        "inbox-warm" {
            $o = Join-Path $Out "shell-$Label"
            $null = Run-Exe $audit @("shell", "inbox", "--data", $corpus, "--out", $o, "--label", "w", "--runs", "$Runs", "--width", "$Width") 3600
            Split-WarmJson "inbox-warm" (Join-Path $o "shell-inbox-w.json") $cpu
        }
        "inbox-cold" {
            foreach ($f in $files) {
                $w = Cell-Path "inbox-warm" $f.Name
                if ((Test-Path $w) -and ((Get-Content $w -Raw | ConvertFrom-Json).warmMs -eq $null)) { continue }  # the provider cannot do this type
                $c = Get-CpuLoad; Save-Cell "inbox-cold" $f.Name (Cold-Runs $audit @("shell-one", "inbox") $f.FullName @("--width", "$Width") @("getMs", "createMs", "initMs")) $c
            }
        }
        "glide-warm" {
            if (-not $GlideShellDll) { Write-Warning "no -GlideShellDll"; continue }
            $o = Join-Path $Out "shell-$Label"
            $null = Run-Exe $audit @("shell", $GlideShellDll, "--data", $corpus, "--out", $o, "--label", "w", "--runs", "$Runs", "--width", "$Width") 3600
            Split-WarmJson "glide-warm" (Join-Path $o "shell-glide-w.json") $cpu
        }
        "glide-cold" {
            if (-not $GlideShellDll) { continue }
            foreach ($f in $files) {
                $w = Cell-Path "glide-warm" $f.Name
                if ((Test-Path $w) -and ((Get-Content $w -Raw | ConvertFrom-Json).warmMs -eq $null)) { continue }
                $c = Get-CpuLoad; Save-Cell "glide-cold" $f.Name (Cold-Runs $audit @("shell-one", $GlideShellDll) $f.FullName @("--width", "$Width") @("getMs", "createMs", "initMs")) $c
            }
        }
        "factory" {
            foreach ($f in $files) {
                $r = Run-Exe $audit @("factory", $f.FullName, "--width", "$Width", "--runs", "$Runs") 300
                if ($r.out) { $n = ($r.out.Trim() -split "`n")[-1] | ConvertFrom-Json; if (-not $n.skipped) { Save-Cell "factory" $f.Name $n $cpu } }
            }
        }
    }
    $null = Note-Load "$phase-end"
}
$stamp = Get-Date -Format "yyyyMMdd-HHmmss"
$loadLog | ConvertTo-Json | Set-Content (Join-Path $Out "load-$Label-$stamp.json") -Encoding utf8
$busy = @($loadLog | Where-Object { $_.busy })
if ($busy.Count) { Write-Warning ("BUSY samples ({0}): {1}" -f $busy.Count, (($busy | ForEach-Object { "$($_.at)=$($_.cpu)%" }) -join ", ")) }
Write-Host "done: $Out"
