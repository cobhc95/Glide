<#
.SYNOPSIS
  Builds before/after markdown tables from two run-perf.ps1 result folders.
.EXAMPLE
  powershell -File tools\perf\compare-perf.ps1 -Results <perf-results> -Before baseline -After optimized -Out <file.md>
#>
param(
    [Parameter(Mandatory = $true)][string]$Results,
    [string]$Before = "baseline",
    [string]$After = "optimized",
    [string]$Out = ""
)
$ErrorActionPreference = "Stop"

function Load-Json([string]$label, [string]$kind) {
    $path = Join-Path $Results "$label\$kind-$label.json"
    if (Test-Path $path) { return Get-Content $path -Raw | ConvertFrom-Json }
    return $null
}
function Fmt($v) {
    if ($null -eq $v -or "$v" -eq "") { return "n/a" }
    $d = [double]$v
    if ([double]::IsNaN($d)) { return "n/a" }
    if ([math]::Abs($d) -lt 10) { return $d.ToString("0.00", [Globalization.CultureInfo]::InvariantCulture) }
    return $d.ToString("0.0", [Globalization.CultureInfo]::InvariantCulture)
}
function Delta($b, $a) {
    if ($null -eq $b -or $null -eq $a -or "$b" -eq "" -or "$a" -eq "") { return "" }
    $bd = [double]$b; $ad = [double]$a
    if ($bd -le 0 -or [double]::IsNaN($bd) -or [double]::IsNaN($ad)) { return "" }
    $pct = ($ad - $bd) / $bd * 100
    return ($pct.ToString("+0;-0;0", [Globalization.CultureInfo]::InvariantCulture) + " %")
}
function Median([double[]]$values) {
    $v = @($values | Where-Object { -not [double]::IsNaN($_) } | Sort-Object)
    if ($v.Count -eq 0) { return $null }
    if ($v.Count % 2) { return $v[[int][math]::Floor($v.Count / 2)] }
    return ($v[$v.Count / 2 - 1] + $v[$v.Count / 2]) / 2
}

$md = New-Object System.Collections.Generic.List[string]
$md.Add("# Glide performance: $Before vs $After")
$md.Add("")

# ---- single image open -------------------------------------------------------------------------
$db = Load-Json $Before "decode"; $da = Load-Json $After "decode"
if ($db -and $da) {
    $md.Add("## Single-image open (ms, median; peak = private-bytes delta MB)")
    $md.Add("")
    $md.Add("| File | Cold first frame | | Warm first frame | | Settled (Fit) | | Full decode | | Peak MB | | First frame px before -> after |")
    $md.Add("|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---|")
    $md.Add("| | before | after | before | after | before | after | before | after | before | after | |")
    foreach ($b in $db.items) {
        $a = $da.items | Where-Object { $_.file -eq $b.file } | Select-Object -First 1
        if (-not $a) { continue }
        $md.Add("| $($b.file) | $(Fmt $b.coldFirstMs) | $(Fmt $a.coldFirstMs) $(Delta $b.coldFirstMs $a.coldFirstMs) | $(Fmt $b.firstMs) | $(Fmt $a.firstMs) $(Delta $b.firstMs $a.firstMs) | $(Fmt $b.settledMs) | $(Fmt $a.settledMs) $(Delta $b.settledMs $a.settledMs) | $(Fmt $b.fullMs) | $(Fmt $a.fullMs) $(Delta $b.fullMs $a.fullMs) | $(Fmt $b.peakDeltaMb) | $(Fmt $a.peakDeltaMb) | $($b.firstSize) -> $($a.firstSize)$(if ($a.errors) { ' (' + $a.errors + ')' }) |")
    }
    $md.Add("")
}

# ---- navigation --------------------------------------------------------------------------------
$nb = Load-Json $Before "navigate"; $na = Load-Json $After "navigate"
if ($nb -and $na) {
    $md.Add("## Navigation (ms per step; CPU = process CPU ms per scenario run; settle = last press to full frame)")
    $md.Add("")
    $md.Add("| Folder | Scenario | Median step before | after | p95 before | after | Max before | after | Settle before | after | CPU before | after | Peak MB before | after |")
    $md.Add("|---|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|")
    foreach ($b in $nb.items) {
        $a = $na.items | Where-Object { $_.folder -eq $b.folder -and $_.scenario -eq $b.scenario } | Select-Object -First 1
        if (-not $a) { continue }
        $md.Add("| $($b.folder) | $($b.scenario) | $(Fmt $b.medianMs) | $(Fmt $a.medianMs) | $(Fmt $b.p95Ms) | $(Fmt $a.p95Ms) | $(Fmt $b.maxMs) | $(Fmt $a.maxMs) | $(Fmt $b.settleMs) | $(Fmt $a.settleMs) | $(Fmt $b.cpuMsPerRun) | $(Fmt $a.cpuMsPerRun) $(Delta $b.cpuMsPerRun $a.cpuMsPerRun) | $(Fmt $b.peakDeltaMb) | $(Fmt $a.peakDeltaMb) |")
    }
    $md.Add("")
}

# ---- render ------------------------------------------------------------------------------------
$rb = Load-Json $Before "render"; $ra = Load-Json $After "render"
if ($rb -and $ra) {
    $md.Add("## Zoom / pan frame cost (headless CPU Skia, ms per frame)")
    $md.Add("")
    $md.Add("| Image | Bitmap | Scenario | Median before | after | p95 before | after |")
    $md.Add("|---|---|---|---:|---:|---:|---:|")
    foreach ($b in $rb.items) {
        $a = $ra.items | Where-Object { $_.file -eq $b.file -and $_.kind -eq $b.kind -and $_.scenario -eq $b.scenario } | Select-Object -First 1
        if (-not $a) { continue }
        $md.Add("| $($b.file) | $($b.kind) | $($b.scenario) | $(Fmt $b.medianMs) | $(Fmt $a.medianMs) | $(Fmt $b.p95Ms) | $(Fmt $a.p95Ms) |")
    }
    $md.Add("")
}

# ---- UI ----------------------------------------------------------------------------------------
$ub = Load-Json $Before "ui"; $ua = Load-Json $After "ui"
if ($ub -and $ua) {
    $md.Add("## UI snappiness (headless; first = cold incl. JIT, warm = median of the rest)")
    $md.Add("")
    $md.Add("| Metric | First before | after | Warm before | after | |")
    $md.Add("|---|---:|---:|---:|---:|---:|")
    foreach ($p in $ub.results.PSObject.Properties) {
        $bv = @($p.Value | ForEach-Object { [double]$_ })
        $av = @($ua.results.($p.Name) | ForEach-Object { [double]$_ })
        if ($av.Count -eq 0) { continue }
        $bw = Median ($bv | Select-Object -Skip 1); $aw = Median ($av | Select-Object -Skip 1)
        $md.Add("| $($p.Name) | $(Fmt $bv[0]) | $(Fmt $av[0]) | $(Fmt $bw) | $(Fmt $aw) | $(Delta $bw $aw) |")
    }
    $md.Add("")
}

# ---- app launch --------------------------------------------------------------------------------
$cb = Join-Path $Results "$Before\app-first-frame.csv"; $ca = Join-Path $Results "$After\app-first-frame.csv"
if ((Test-Path $cb) -and (Test-Path $ca)) {
    $rowsB = Import-Csv $cb; $rowsA = Import-Csv $ca
    $md.Add("## App launch -> first composed frame (ms, median of launches; Release build, real Win32 window + GPU)")
    $md.Add("")
    $md.Add("| File | Before | After | |")
    $md.Add("|---|---:|---:|---:|")
    foreach ($g in $rowsB | Group-Object File) {
        $bm = Median (@($g.Group | Where-Object { $_.FirstFrameMs } | ForEach-Object { [double]$_.FirstFrameMs }))
        $am = Median (@($rowsA | Where-Object { $_.File -eq $g.Name -and $_.FirstFrameMs } | ForEach-Object { [double]$_.FirstFrameMs }))
        $md.Add("| $($g.Name) | $(Fmt $bm) | $(Fmt $am) | $(Delta $bm $am) |")
    }
    $md.Add("")
}

$text = $md -join "`n"
if ($Out) { $text | Set-Content -Path $Out -Encoding utf8 }
$text
