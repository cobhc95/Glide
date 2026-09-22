# Glide Explorer thumbnail provider — benchmark corpus and measurement
#
# Generates a corpus, registers the provider per-user, measures per-item thumbnail cost through the
# real COM provider (one instance per file, activation included), then removes the registration.
#
# Usage:  powershell -ExecutionPolicy Bypass -File tools\thumbnail-benchmark.ps1 [-Count 300]

param([int]$Count = 300)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$clsid = [Guid]"6E3C1B2A-9F41-4E7C-9B1E-2C7A5D8F0A31"
$dll = (Resolve-Path "native\Glide.ShellThumbnail\build\Release\Glide.ShellThumbnail.dll").Path
$corpus = "artifacts\thumbnail-benchmark\corpus"
New-Item -ItemType Directory -Force -Path $corpus | Out-Null

$rng = [Random]::new(1234)
$sizes = @(@(640, 480), @(1920, 1080), @(4000, 3000), @(8000, 6000))
for ($i = 0; $i -lt $Count; $i++) {
    $size = $sizes[$i % $sizes.Count]
    $path = Join-Path $corpus ("photo-{0:D3}.jpg" -f $i)
    if (Test-Path $path) { continue }
    $bmp = New-Object System.Drawing.Bitmap($size[0], $size[1])
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.Clear([System.Drawing.Color]::FromArgb(255, $rng.Next(256), $rng.Next(256), $rng.Next(256)))
    $g.Dispose()
    $bmp.Save($path, [System.Drawing.Imaging.ImageFormat]::Jpeg)
    $bmp.Dispose()
}

# Include the non-WIC formats the provider handles itself.
$files = Get-ChildItem $corpus -Filter *.jpg | Select-Object -ExpandProperty FullName
foreach ($extra in @("artifacts\format-acceptance\sample-gradient.tga",
                     "artifacts\format-acceptance\sample-vector.svg",
                     "artifacts\diagnostic-fixtures\fixture.webp")) {
    if (Test-Path $extra) { $files += (Resolve-Path $extra).Path }
}

$clsidKey = "HKCU:\Software\Classes\CLSID\{$clsid}"
New-Item -Path "$clsidKey\InprocServer32" -Force | Out-Null
Set-ItemProperty -Path $clsidKey -Name "(default)" -Value "Glide Explorer Thumbnail Provider"
Set-ItemProperty -Path "$clsidKey\InprocServer32" -Name "(default)" -Value $dll
Set-ItemProperty -Path "$clsidKey\InprocServer32" -Name "ThreadingModel" -Value "Apartment"

try {
    Add-Type -TypeDefinition @"
using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;

public static class Bench
{
    [ComImport, Guid("E357FCCD-A995-4576-B01F-234630154E96"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IThumbnailProvider { void GetThumbnail(uint cx, out IntPtr phbmp, out uint pdwAlpha); }
    [ComImport, Guid("B824B49D-22AC-4161-AC8A-9916E8FA3F7F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IInitializeWithStream { void Initialize(IStream pstream, uint grfMode); }
    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
    static extern IStream SHCreateStreamOnFileEx(string file, uint mode, uint attributes, bool create, IntPtr template);
    [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr hObject);

    public static long[] Run(Guid clsid, string[] files, uint cx)
    {
        var times = new long[files.Length];
        var watch = new Stopwatch();
        for (int i = 0; i < files.Length; i++)
        {
            watch.Restart();
            object instance = Activator.CreateInstance(Type.GetTypeFromCLSID(clsid));
            try
            {
                var stream = SHCreateStreamOnFileEx(files[i], 0, 0, false, IntPtr.Zero);
                try { ((IInitializeWithStream)instance).Initialize(stream, 0); }
                finally { Marshal.ReleaseComObject(stream); }
                IntPtr hbmp;
                uint alpha;
                ((IThumbnailProvider)instance).GetThumbnail(cx, out hbmp, out alpha);
                if (hbmp != IntPtr.Zero) DeleteObject(hbmp);
            }
            finally { Marshal.ReleaseComObject(instance); }
            times[i] = watch.ElapsedMilliseconds;
        }
        return times;
    }
}
"@

    [Bench]::Run($clsid, @($files[0]), 256) | Out-Null
    $times = [Bench]::Run($clsid, $files, 256)
    $sorted = $times | Sort-Object
    "files           : $($times.Count)"
    "median          : $($sorted[[int]($sorted.Count / 2)]) ms"
    "p95             : $($sorted[[int]($sorted.Count * 0.95)]) ms"
    "mean            : $([math]::Round(($times | Measure-Object -Average).Average, 2)) ms"
    "min / max       : $($sorted[0]) ms / $($sorted[-1]) ms"
    "sub-20ms share  : $([math]::Round(100.0 * ($times | Where-Object { $_ -lt 20 }).Count / $times.Count, 1))%"
}
finally {
    Remove-Item -Path $clsidKey -Recurse -Force -ErrorAction SilentlyContinue
}
