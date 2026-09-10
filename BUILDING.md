# Building Glide

## Requirements

- Windows 10/11 x64
- Visual Studio 2022 or newer, or Visual Studio Build Tools, with **Desktop development with C++**
- Windows SDK
- CMake
- Inno Setup 7 for installer creation

The codec source archives are pinned and included under `codec_sources/`. The build verifies them against `codec_sources/SHA256SUMS.txt`.

## Build the portable application

Open a Visual Studio Developer Command Prompt and run:

```bat
build.cmd
```

The resulting portable runtime is written to:

```text
dist\
```

Keep the `codecs` subdirectory with `Glide.exe`. WIC-supported formats may work from the executable alone, but the bundled HEIF/AVIF/JXL/JPEG 2000/EXR/WebP paths depend on the staged codec runtime.

## Build codecs only

```bat
build_codecs.cmd
```

This uses the bundled source archives and CMake. No network download is required.

## Build the installer

Install Inno Setup 7, then run:

```bat
build_installer.cmd
```

Expected output:

```text
installer_output\Glide_Setup_Alpha_0.12107_x64.exe
```

## Diagnostics

The application includes a built-in diagnostics command under Settings. The test harness exercises decoder coverage, cache/prefetch behaviour, viewer state and UI evidence captures using the bundled `diagnostic_fixtures` directory.

## Build security

The public build intentionally avoids antivirus exclusions, runtime dependency downloads and PowerShell execution-policy bypasses. Third-party source versions are pinned and checksummed.
