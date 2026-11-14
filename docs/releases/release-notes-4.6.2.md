# Glide 4.6.2

A much smaller, faster Glide that no longer needs .NET installed.

## Smaller and easier to install

- **No .NET required.** Glide is now a native Windows app, so there is no separate runtime to download. This also removes the .NET prompt that could leave Setup appearing to hang on PCs without .NET.
- **Much smaller downloads.** The installer is down from 54.7 MB to 17.3 MB, and the portable archive from 70.9 MB to about 22.5 MB.
- **Faster launch.** In a test on one PC, Glide reached its image window in about 156 ms, down from about 370 ms.
- **FFmpeg is now optional.** Videos play with Windows' own codecs first. If Windows can't play a video, Glide offers to download its video decoder once (51 MB). The download is verified before it is installed, and you are never asked unless you open a video that needs it.

## Fixes

- **Mouse wheel direction.** Scrolling down now goes to the next picture and scrolling up to the previous one. On default settings the wheel was backwards in 4.6.0 and 4.6.1. If you had turned on "Reverse mouse-wheel direction" to compensate, it is switched off once when you upgrade, so your direction stays the same. Older saved profiles or exported settings may still have it on.
- **Window buttons and icons.** Minimize, maximize and close are now drawn by Glide, and other icons fall back to the Windows 10 icon font, so they should no longer appear as empty squares on PCs without Segoe Fluent Icons.
- **Diagnostics self-test.** Fixed a crash caused by the two folder-counter settings added in 4.6.1.

## Downloads

- `Glide.Setup.exe`: installer (requires admin)
- `Glide-4.6.2-Portable.zip`: portable build
- `Glide.Setup.sha256`: installer checksum

Windows x64 only. The GitHub source tree at this tag does not yet contain the 4.6.x changes.
