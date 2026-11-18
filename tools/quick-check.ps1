<#
.SYNOPSIS
  Fast inner-loop check for agents and developers (target: under 60 s on an incremental tree).

.DESCRIPTION
  1. Debug compile of Glide.App (and its references) with analyzers and code-style enforcement off, errors only.
  2. python tools/audit-settings-search.py, audit-action-registry.py and audit-setting-wiring.py.
  3. Optional: tests matching -Filter (a dotnet test --filter expression) and/or -Area (the [Trait("Area", ...)] on each
     test class) in Glide.Core.Tests, or -Project (Core, Explorer, Input, All).
     Areas: Settings, Explorer, Navigation, Decode, Tabs, StatusBar, Gestures, Hotkeys, Overlay, Update, Diagnostics,
     Data (backup/reset), Print, Thumbnails, Video, Window, Shell, Security, Regression.

  It does not run the native/CMake build, AOT publish or the full test suite. Use build.cmd before handing work over.

.EXAMPLE
  powershell -NoProfile -File tools\quick-check.ps1
  powershell -NoProfile -File tools\quick-check.ps1 -Filter "FullyQualifiedName~SettingsCatalog"
  powershell -NoProfile -File tools\quick-check.ps1 -Filter "FullyQualifiedName~ExplorerTab" -Project Explorer
  powershell -NoProfile -File tools\quick-check.ps1 -Area Settings
  powershell -NoProfile -File tools\quick-check.ps1 -Area Tabs -Project All
#>
param(
    [string]$Filter,
    [ValidateSet('Settings', 'Explorer', 'Navigation', 'Decode', 'Tabs', 'StatusBar', 'Gestures', 'Hotkeys', 'Overlay', 'Update',
        'Diagnostics', 'Data', 'Print', 'Thumbnails', 'Video', 'Window', 'Shell', 'Security', 'Regression')][string]$Area,
    [ValidateSet('Core', 'Explorer', 'Input', 'All')][string]$Project = 'Core'
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
$total = [Diagnostics.Stopwatch]::StartNew()
$fastProps = @('-p:RunAnalyzers=false', '-p:EnforceCodeStyleInBuild=false', '-p:TreatWarningsAsErrors=false', '-p:Platform=AnyCPU')

function Step([string]$name, [scriptblock]$body) {
    $sw = [Diagnostics.Stopwatch]::StartNew()
    Write-Host "== $name" -ForegroundColor Cyan
    & $body
    $code = $LASTEXITCODE
    Write-Host ("   {0:N1} s" -f $sw.Elapsed.TotalSeconds)
    if ($code) { Write-Host "FAILED: $name (exit $code)" -ForegroundColor Red; exit $code }
}

Step 'Compile Glide.App (Debug, errors only, analyzers off)' {
    dotnet build src/Glide.App/Glide.App.csproj -c Debug -m -nologo -v q '-clp:ErrorsOnly;Summary' @fastProps
}
Step 'Audit: settings search' { python tools/audit-settings-search.py }
Step 'Audit: action registry' { python tools/audit-action-registry.py }
Step 'Audit: setting wiring' { python tools/audit-setting-wiring.py }

if ($Area) { $Filter = if ($Filter) { "($Filter)&Area=$Area" } else { "Area=$Area" } }
if ($Filter) {
    $projects = if ($Project -eq 'All') { @('Core', 'Explorer', 'Input') } else { @($Project) }
    foreach ($name in $projects) {
        $testProject = "tests/Glide.$name.Tests/Glide.$name.Tests.csproj"
        Step "Tests: $name ($Filter)" {
            dotnet test $testProject -c Debug -nologo -v q '-clp:ErrorsOnly' @fastProps --filter $Filter
        }
    }
}

Write-Host ("quick-check passed in {0:N1} s" -f $total.Elapsed.TotalSeconds) -ForegroundColor Green
