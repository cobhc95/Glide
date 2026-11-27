# Code signing Glide releases

Glide releases are not code-signed yet. Signing lets Windows (SmartScreen, Smart App Control, antivirus
reputation) and users verify that `Glide.exe`, its DLLs and `Glide Setup.exe` really come from you and were
not changed after the build. This page explains how to sign with a real certificate and how the local
development script behaves.

## 1. Get a code-signing certificate (owner action)

Glide's scripts never create, buy or download certificates. Options, roughly from cheapest:

- **Azure Trusted Signing** (Microsoft's managed signing service, identity-validated, monthly fee). It
  signs through `signtool` with the Trusted Signing dlib; the certificate never leaves Microsoft's HSM.
- **OV (organisation validated) or individual code-signing certificate** from a public CA. Since June 2023
  the private key must live on a hardware token or a cloud HSM; the CA ships the token or the cloud signing tool.
- **EV certificate**: same as OV with stricter validation; SmartScreen reputation builds faster.

Whatever you choose, the certificate must have the *Code Signing* extended key usage and be issued by a CA
that Windows trusts. A self-signed certificate is not suitable for releases (see section 4).

## 2. Sign a release with build.cmd

Set these environment variables in the terminal before running `build.cmd`:

| Variable | Meaning |
|---|---|
| `GLIDE_SIGN_CERT_THUMBPRINT` | SHA-1 thumbprint of the certificate in `CurrentUser\My` or `LocalMachine\My` (hardware tokens and cloud HSM tools expose their certificate there). Preferred. |
| `GLIDE_SIGN_PFX` | Alternatively, the path to a `.pfx` file. Its password goes in `GLIDE_SIGN_PFX_PASSWORD` (set it only in the current terminal, never in a file). |
| `GLIDE_SIGN_TIMESTAMP_URL` | Your CA's RFC 3161 timestamp server. **Set it**: without a timestamp the signatures stop validating when the certificate expires. |
| `GLIDE_SIGNTOOL` | Optional full path to `signtool.exe`. Otherwise PATH and the Windows 10/11 SDK folders are searched; if no signtool is found, PowerShell's `Set-AuthenticodeSignature` is used. |

Example (PowerShell):

```powershell
$env:GLIDE_SIGN_CERT_THUMBPRINT = '0123456789ABCDEF0123456789ABCDEF01234567'
$env:GLIDE_SIGN_TIMESTAMP_URL   = 'http://timestamp.example-ca.com'   # from your CA's documentation
.\build.cmd
```

With either `GLIDE_SIGN_CERT_THUMBPRINT` or `GLIDE_SIGN_PFX` set, `build.cmd` signs every `Glide*.exe` and
`Glide*.dll` in `.artifacts\stage\` right after publishing (before the fixtures and the installer are
made, so all of them contain the signed files), and `tools\build-installer.cmd` signs `Glide Setup.exe`
before writing `Glide Setup.sha256`. Without those variables the build is unchanged.

You can also sign by hand at any time:

```powershell
powershell -ExecutionPolicy Bypass -File tools\sign-local.ps1 -Path .artifactsstage, dist-installer
```

Check a signature with `Get-AuthenticodeSignature dist\Glide.exe` (Status should be `Valid`) or
`signtool verify /pa /v dist\Glide.exe`.

Not covered yet: the Inno Setup uninstaller (`unins000.exe`) is generated at install time and is signed
only if the `.iss` file is given a `SignTool=` entry; add one (Inno Setup's `SignTool` directive) when
you have a certificate.

## 3. Publishing

Publish the SHA-256 of `Glide Setup.exe` and the portable ZIP next to the download (the build writes
`dist-installer\Glide Setup.sha256`). Keep the certificate's token or HSM credentials off the build
machine's disk when it is not in use.

## 4. Local self-signed signing (development only)

`tools\sign-local.ps1 -SelfSigned` signs a local build with a self-signed certificate created once in
`CurrentUser\My`. Windows does not trust that certificate, which is the safe default; the signature only
shows that the file has not changed since you signed it.

`-SelfSigned -TrustOnThisPc` additionally adds the certificate to your `CurrentUser\Root` and
`CurrentUser\TrustedPublisher` stores after a warning and a typed confirmation. **Avoid this** unless you
need your own builds to look trusted on this one PC: the private key stays in `CurrentUser\My`, so any
program running as you could use it to sign code that this account then trusts.

Before 5.0 the script added the certificate to Root and TrustedPublisher automatically. To remove that
trust (and the certificate) from a PC where an older version of the script ran:

```powershell
powershell -ExecutionPolicy Bypass -File tools\sign-local.ps1 -RemoveLocalTrust
```

Windows asks for confirmation when a certificate is removed from Root. To check by hand, open
`certmgr.msc` and look for *Glide Image Viewer (local build)* under *Trusted Root Certification
Authorities*, *Trusted Publishers* and *Personal*.
