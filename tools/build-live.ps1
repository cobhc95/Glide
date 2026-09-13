param(
    [Parameter(Mandatory = $true)][string]$BuildScript,
    [Parameter(Mandatory = $true)][string]$LogPath,
    [string]$ForwardArgsLine = ''
)

$ErrorActionPreference = 'Stop'
$nativeRc = $null

# Keep the argument hand-off deliberately simple and robust. build.cmd only supports
# token-style switches (fast, aot, --no-pause), so passing one quoted string avoids
# PowerShell treating a bare `--` or forwarded `--no-pause` as script parameters.
$forwardArgs = @()
if (-not [string]::IsNullOrWhiteSpace($ForwardArgsLine)) {
    $forwardArgs = $ForwardArgsLine -split '\s+' | Where-Object { $_ -ne '' }
}

try {
    if (Test-Path -LiteralPath $LogPath) { Remove-Item -LiteralPath $LogPath -Force }

    # Invoke the inner batch normally so its console title/progress updates remain live,
    # while Tee-Object mirrors every output line to build-output.txt.
    $innerArgs = @('--glide-captured', '--no-pause') + $forwardArgs
    # Windows PowerShell can surface native stderr as error records when the caller uses
    # ErrorActionPreference=Stop. Keep compiler stderr in the transcript without converting it
    # into a wrapper exception; the native exit code remains authoritative.
    $oldPreference = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        & $BuildScript @innerArgs 2>&1 | Tee-Object -FilePath $LogPath
        $nativeRc = $LASTEXITCODE
    }
    finally { $ErrorActionPreference = $oldPreference }
    $rc = if ($null -eq $nativeRc) { 0 } else { [int]$nativeRc }
}
catch {
    # Logger/wrapper failures must themselves become a persistent diagnostic transcript.
    # Preserve a non-zero native exit code if one was already observed. A later wrapper,
    # clipboard or logging failure must never relabel the compiler result as success.
    $rc = if ($null -ne $nativeRc -and [int]$nativeRc -ne 0) { [int]$nativeRc } else { 1 }
    $msg = @(
        '============================================================',
        '  GLIDE BUILD WRAPPER FAILURE',
        '============================================================',
        $_.Exception.ToString(),
        '',
        'PowerShell error record:',
        ($_ | Out-String)
    ) -join [Environment]::NewLine

    try { Add-Content -LiteralPath $LogPath -Value $msg -Encoding UTF8 }
    catch { Write-Host $msg }
}

if ($rc -ne 0) {
    # Copy here as well as in build.cmd. This makes failure capture resilient even if
    # the outer batch wrapper itself later encounters a problem.
    try {
        if (Test-Path -LiteralPath $LogPath) {
            Get-Content -LiteralPath $LogPath -Raw | Set-Clipboard
        }
    }
    catch {
        # Clipboard failure must never hide or replace the original compiler error.
    }
}

exit [int]$rc
