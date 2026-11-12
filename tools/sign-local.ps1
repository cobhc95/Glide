<#
  Signs a locally built Glide with a self-signed code-signing certificate and clears the
  "downloaded from the internet" mark, so Windows stops treating your own build as unknown software.

  First run (once, as the user who runs Glide): creates the certificate and adds it to your
  CurrentUser Trusted Root + Trusted Publishers stores (Windows shows one confirmation prompt).
  Later runs just reuse it.

  Usage: powershell -ExecutionPolicy Bypass -File tools\sign-local.ps1 [-Path dist] [-Installer dist-installer]
#>
param(
    [string[]]$Path = @('dist', 'dist-installer', 'Glide-*.exe'),
    [string]$Subject = 'CN=Glide Image Viewer (local build), O=cobhc95'
)
$ErrorActionPreference = 'Stop'
Set-Location (Split-Path $PSScriptRoot -Parent)

$cert = Get-ChildItem Cert:\CurrentUser\My -CodeSigningCert |
    Where-Object { $_.Subject -eq $Subject -and $_.NotAfter -gt (Get-Date).AddDays(30) } |
    Sort-Object NotAfter -Descending | Select-Object -First 1
if (-not $cert) {
    Write-Host 'Creating self-signed code-signing certificate...'
    $cert = New-SelfSignedCertificate -Type CodeSigningCert -Subject $Subject -CertStoreLocation Cert:\CurrentUser\My `
        -KeyUsage DigitalSignature -KeyExportPolicy NonExportable -NotAfter (Get-Date).AddYears(5)
    $cer = Join-Path $env:TEMP 'glide-local-signing.cer'
    Export-Certificate -Cert $cert -FilePath $cer | Out-Null
    foreach ($store in 'Root', 'TrustedPublisher') {
        Import-Certificate -FilePath $cer -CertStoreLocation "Cert:\CurrentUser\$store" | Out-Null
    }
    Remove-Item $cer -Force
}

$files = foreach ($p in $Path) {
    if (Test-Path $p -PathType Container) { Get-ChildItem $p -Include *.exe, *.dll -Recurse -File | Where-Object { $_.Name -like 'Glide*' } }
    else { Get-ChildItem $p -File -ErrorAction SilentlyContinue }
}
foreach ($f in $files) {
    Unblock-File -Path $f.FullName
    $r = Set-AuthenticodeSignature -FilePath $f.FullName -Certificate $cert -HashAlgorithm SHA256
    Write-Host ("{0,-14} {1}" -f $r.Status, $f.FullName)
}
