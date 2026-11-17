<#
.SYNOPSIS
  Blocks until no build/test process is alive (dotnet, MSBuild, VBCSCompiler, ISCC, testhost, cmd running build.cmd)
  and total CPU has stayed under -Threshold % for -QuietSeconds. Exit 0 = idle, 1 = timed out.
  Our own benchmark harness (Glide.DecodeBench507) is ignored.
#>
param([int]$QuietSeconds = 120, [int]$Threshold = 15, [int]$TimeoutMinutes = 120)
$deadline = (Get-Date).AddMinutes($TimeoutMinutes); $quiet = 0
while ((Get-Date) -lt $deadline) {
    $procs = @(Get-CimInstance Win32_Process | Where-Object {
        $_.Name -match '^(dotnet|MSBuild|VBCSCompiler|ISCC|testhost|vstest\.console)\.exe$' -or
        ($_.Name -eq 'cmd.exe' -and $_.CommandLine -match 'build\.cmd')
    })
    $cpu = (Get-CimInstance Win32_Processor | Measure-Object LoadPercentage -Average).Average
    if ($procs.Count -eq 0 -and $cpu -lt $Threshold) { $quiet += 10 } else { $quiet = 0 }
    if ($quiet -ge $QuietSeconds) { "IDLE $(Get-Date -Format HH:mm:ss) cpu=$cpu"; exit 0 }
    Start-Sleep 10
}
"TIMEOUT procs=" + (($procs | ForEach-Object Name) -join ',') + " cpu=$cpu"; exit 1
