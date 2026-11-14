<#
  Code-signs Glide's binaries (Glide*.exe / Glide*.dll in dist, dist-installer and the root Glide-*.exe).
  See docs\code-signing.md for the full guide.

  REAL SIGNING (releases). Supply an existing code-signing certificate through environment variables; this
  script never creates or downloads one:
    GLIDE_SIGN_CERT_THUMBPRINT  SHA-1 thumbprint of a code-signing certificate in CurrentUser\My or
                                LocalMachine\My (hardware tokens and cloud HSM providers expose theirs here).
    GLIDE_SIGN_PFX              Path to a .pfx file instead (its password in GLIDE_SIGN_PFX_PASSWORD).
    GLIDE_SIGN_TIMESTAMP_URL    RFC 3161 timestamp server of your certificate vendor. Strongly recommended:
                                without a timestamp the signature stops validating when the certificate expires.
    GLIDE_SIGNTOOL              Optional full path to signtool.exe (otherwise PATH and the Windows SDK are searched;
                                if signtool is missing, PowerShell's Set-AuthenticodeSignature is used).

  LOCAL SELF-SIGNED SIGNING (development only, opt-in):  -SelfSigned
    Creates (once) a self-signed certificate in CurrentUser\My and signs with it. Windows does NOT trust it,
    which is the safe default. Adding it to your trust stores is a separate, explicit step:  -SelfSigned -TrustOnThisPc
    WARNING: a trusted self-signed root lets any program running as you sign code that this account's Windows
    then trusts (its private key sits in CurrentUser\My). Only do this on a development PC, and undo it with:
        -RemoveLocalTrust     (removes the certificate from CurrentUser Root and TrustedPublisher, and from My)

  Usage:
    powershell -ExecutionPolicy Bypass -File tools\sign-local.ps1                 # real certificate from env vars
    powershell -ExecutionPolicy Bypass -File tools\sign-local.ps1 -SelfSigned     # dev build, untrusted signature
    powershell -ExecutionPolicy Bypass -File tools\sign-local.ps1 -RemoveLocalTrust
#>
param(
    [string[]]$Path = @('dist', 'dist-installer', 'Glide-*.exe'),
    [switch]$SelfSigned,
    [switch]$TrustOnThisPc,
    [switch]$RemoveLocalTrust,
    [switch]$Force,
    [string]$Subject = 'CN=Glide Image Viewer (local build), O=cobhc95'
)
$ErrorActionPreference = 'Stop'
Set-Location (Split-Path $PSScriptRoot -Parent)

function Get-LocalCertificates([string]$store) {
    Get-ChildItem "Cert:\CurrentUser\$store" -ErrorAction SilentlyContinue | Where-Object { $_.Subject -eq $Subject }
}

if ($RemoveLocalTrust) {
    $removed = 0
    foreach ($store in 'Root', 'TrustedPublisher', 'My') {
        foreach ($c in Get-LocalCertificates $store) {
            # Removing from Root shows a Windows confirmation prompt.
            Remove-Item -LiteralPath "Cert:\CurrentUser\$store\$($c.Thumbprint)" -Force
            Write-Host "Removed $($c.Thumbprint) from CurrentUser\$store"
            $removed++
        }
    }
    if ($removed -eq 0) { Write-Host "No '$Subject' certificate found in CurrentUser Root, TrustedPublisher or My." }
    return
}

$files = foreach ($p in $Path) {
    if (Test-Path $p -PathType Container) { Get-ChildItem $p -Include *.exe, *.dll -Recurse -File | Where-Object { $_.Name -like 'Glide*' } }
    else { Get-ChildItem $p -File -ErrorAction SilentlyContinue }
}
if (-not $files) { Write-Host 'Nothing to sign.'; return }

$timestamp = $env:GLIDE_SIGN_TIMESTAMP_URL
$thumbprint = ($env:GLIDE_SIGN_CERT_THUMBPRINT -replace '[^0-9A-Fa-f]', '')
$pfx = $env:GLIDE_SIGN_PFX

if ($thumbprint -or $pfx) {
    # ---------------------------------------------------------------- real certificate (release) ---
    if (-not $timestamp) { Write-Warning 'GLIDE_SIGN_TIMESTAMP_URL is not set: signatures will not be timestamped and stop validating when the certificate expires.' }
    $signtool = $env:GLIDE_SIGNTOOL
    if (-not $signtool) { $signtool = (Get-Command signtool.exe -ErrorAction SilentlyContinue).Source }
    if (-not $signtool) {
        $signtool = Get-ChildItem "${env:ProgramFiles(x86)}\Windows Kits\10\bin\*\x64\signtool.exe" -ErrorAction SilentlyContinue |
            Sort-Object FullName -Descending | Select-Object -First 1 -ExpandProperty FullName
    }
    if ($signtool) {
        $arguments = @('sign', '/fd', 'SHA256', '/v')
        if ($thumbprint) { $arguments += @('/sha1', $thumbprint) }
        else {
            $arguments += @('/f', $pfx)
            if ($env:GLIDE_SIGN_PFX_PASSWORD) { $arguments += @('/p', $env:GLIDE_SIGN_PFX_PASSWORD) }
        }
        if ($timestamp) { $arguments += @('/tr', $timestamp, '/td', 'SHA256') }
        foreach ($f in $files) {
            & $signtool @arguments $f.FullName | Out-Null
            if ($LASTEXITCODE -ne 0) { throw "signtool failed ($LASTEXITCODE) for $($f.FullName)" }
            Write-Host ("{0,-14} {1}" -f 'Signed', $f.FullName)
        }
        return
    }
    # No signtool: PowerShell's Authenticode signer with the same certificate.
    if ($thumbprint) {
        $cert = Get-ChildItem Cert:\CurrentUser\My, Cert:\LocalMachine\My -CodeSigningCert -ErrorAction SilentlyContinue |
            Where-Object { $_.Thumbprint -eq $thumbprint } | Select-Object -First 1
    } else {
        $password = if ($env:GLIDE_SIGN_PFX_PASSWORD) { ConvertTo-SecureString $env:GLIDE_SIGN_PFX_PASSWORD -AsPlainText -Force } else { $null }
        $cert = Get-PfxCertificate -FilePath $pfx -Password $password
    }
    if (-not $cert) { throw 'The code-signing certificate named by GLIDE_SIGN_CERT_THUMBPRINT / GLIDE_SIGN_PFX was not found.' }
    foreach ($f in $files) {
        if ($timestamp) { $r = Set-AuthenticodeSignature -FilePath $f.FullName -Certificate $cert -HashAlgorithm SHA256 -TimestampServer $timestamp }
        else { $r = Set-AuthenticodeSignature -FilePath $f.FullName -Certificate $cert -HashAlgorithm SHA256 }
        if ($r.Status -ne 'Valid') { throw "Signing failed for $($f.FullName): $($r.StatusMessage)" }
        Write-Host ("{0,-14} {1}" -f $r.Status, $f.FullName)
    }
    return
}

if (-not $SelfSigned) {
    Write-Host 'No signing certificate configured (GLIDE_SIGN_CERT_THUMBPRINT or GLIDE_SIGN_PFX); nothing was signed.'
    Write-Host 'For a development build, -SelfSigned signs with a local, untrusted certificate. See docs\code-signing.md.'
    return
}

# ------------------------------------------------------------------------ self-signed (development) ---
$cert = Get-ChildItem Cert:\CurrentUser\My -CodeSigningCert |
    Where-Object { $_.Subject -eq $Subject -and $_.NotAfter -gt (Get-Date).AddDays(30) } |
    Sort-Object NotAfter -Descending | Select-Object -First 1
if (-not $cert) {
    Write-Host 'Creating a self-signed development code-signing certificate in CurrentUser\My (not trusted by Windows)...'
    $cert = New-SelfSignedCertificate -Type CodeSigningCert -Subject $Subject -CertStoreLocation Cert:\CurrentUser\My `
        -KeyUsage DigitalSignature -KeyExportPolicy NonExportable -NotAfter (Get-Date).AddYears(1)
}

if ($TrustOnThisPc) {
    Write-Warning ("Trusting '$Subject' adds it to your CurrentUser Root and TrustedPublisher stores. Any program running " +
        'as you could then use its key (CurrentUser\My) to sign code that this account trusts. Use this only on a ' +
        'development PC and undo it with: tools\sign-local.ps1 -RemoveLocalTrust')
    if (-not $Force) {
        $answer = Read-Host 'Type TRUST to continue'
        if ($answer -ne 'TRUST') { Write-Host 'Not trusted. Files are signed with an untrusted certificate.'; $TrustOnThisPc = $false }
    }
    if ($TrustOnThisPc) {
        $cer = Join-Path ([IO.Path]::GetTempPath()) ('glide-local-signing-' + [Guid]::NewGuid().ToString('N') + '.cer')
        try {
            Export-Certificate -Cert $cert -FilePath $cer | Out-Null
            foreach ($store in 'Root', 'TrustedPublisher') {
                Import-Certificate -FilePath $cer -CertStoreLocation "Cert:\CurrentUser\$store" | Out-Null
            }
        } finally { Remove-Item $cer -Force -ErrorAction SilentlyContinue }
    }
}

foreach ($f in $files) {
    Unblock-File -Path $f.FullName
    $r = Set-AuthenticodeSignature -FilePath $f.FullName -Certificate $cert -HashAlgorithm SHA256
    Write-Host ("{0,-14} {1}" -f $r.Status, $f.FullName)
}
