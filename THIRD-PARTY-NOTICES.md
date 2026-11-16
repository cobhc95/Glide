# Third-party notices

Glide Image Viewer is free software licensed under the **GNU General Public License, version 3 or
(at your option) any later version** (`GPL-3.0-or-later`). The full text is in [LICENSE](LICENSE).

Glide includes or uses the third-party components below. Each keeps its own licence; none of them is
relicensed by Glide. They are licensed so that they can be combined with and distributed as part of a
GPL-3.0-or-later program; Svg.Custom (Ms-PL) is covered by Glide's additional permission below.

Where the full licence text lives:

* `licenses\` (next to this file in the installer and the portable ZIP; `licenses/` in the repository)
  holds the SkiaSharp, HarfBuzzSharp, ANGLE and libwebp notices.
* `codecs\glidecodecs.NOTICE.txt` holds the licence texts for the bundled codec module.
* The MIT licence used by the .NET packages is reproduced at the end of this file.

## Shipped in the installer and the portable ZIP

| Component | Version | SPDX licence | Upstream | Licence text / notes |
|---|---|---|---|---|
| Avalonia (Avalonia, Avalonia.Skia, Avalonia.Win32, Avalonia.Themes.Fluent, Avalonia.Remote.Protocol, Avalonia.Controls.ItemsRepeater) | 11.3.20 (ItemsRepeater 11.1.5) | MIT | https://github.com/AvaloniaUI/Avalonia | MIT text below |
| MicroCom.Runtime | 0.11.0 | MIT | https://github.com/kekekeks/MicroCom | MIT text below |
| SkiaSharp (managed) and SkiaSharp.HarfBuzz | 2.88.9 | MIT | https://github.com/mono/SkiaSharp | `licenses\SkiaSharp-LICENSE.txt` |
| libSkiaSharp.dll (native: Skia and the libraries it embeds, such as FreeType, libpng, libjpeg-turbo, zlib, expat, libwebp, ICU, piex) | from SkiaSharp.NativeAssets.Win32 2.88.9 | BSD-3-Clause (Skia) plus the licences listed in the notices file (FreeType License, Apache-2.0, MIT, zlib, BSD) | https://skia.org | `licenses\SkiaSharp-THIRD-PARTY-NOTICES.txt` |
| HarfBuzzSharp (managed) | 8.3.1.1 | MIT | https://github.com/mono/SkiaSharp | `licenses\HarfBuzzSharp-LICENSE.txt` |
| libHarfBuzzSharp.dll (native HarfBuzz) | from HarfBuzzSharp.NativeAssets.Win32 8.3.1.1 | MIT ("Old MIT") | https://github.com/harfbuzz/harfbuzz | notice inside `licenses\SkiaSharp-THIRD-PARTY-NOTICES.txt` |
| ANGLE (av_libglesv2.dll) | from Avalonia.Angle.Windows.Natives 2.1.25547.20250602 | BSD-3-Clause | https://github.com/AvaloniaUI/angle | `licenses\ANGLE-LICENSE.txt` |
| Svg.Skia, Svg.Model, ShimSkiaSharp | 2.0.0.8 | MIT | https://github.com/wieslawsoltes/Svg.Skia | MIT text below |
| Svg.Custom (a custom build of SVG.NET) | 2.0.0.8 | **MS-PL** | https://github.com/wieslawsoltes/Svg.Skia | https://opensource.org/license/ms-pl-html ; see the note below |
| ExCSS | 4.3.0 | MIT | https://github.com/TylerBrinks/ExCSS | MIT text below |
| System.Drawing.Common | 10.0.15 | MIT | https://github.com/dotnet/runtime | MIT text below |
| .NET 10 runtime and base class libraries (self-contained, NativeAOT) | 10.x | MIT | https://github.com/dotnet/runtime | MIT text below |
| libwebp (statically linked in `Glide.ShellThumbnail.dll`, decode only) | 1.5.0 | BSD-3-Clause plus the WebM patent grant | https://github.com/webmproject/libwebp | `licenses\libwebp-COPYING.txt`, `licenses\libwebp-PATENTS.txt` |
| dav1d (in `codecs\glidecodecs.dll`) | 1.5.4 | BSD-2-Clause | https://code.videolan.org/videolan/dav1d | `codecs\glidecodecs.NOTICE.txt` |
| libheif (in `codecs\glidecodecs.dll`) | 1.23.7 | LGPL-3.0-or-later | https://github.com/strukturag/libheif | `codecs\glidecodecs.NOTICE.txt` |
| libde265 (in `codecs\glidecodecs.dll`) | 1.1.3 | LGPL-3.0-or-later | https://github.com/strukturag/libde265 | `codecs\glidecodecs.NOTICE.txt` |
| libjxl (in `codecs\glidecodecs.dll`) | 0.13.0 | BSD-3-Clause with patent grant | https://github.com/libjxl/libjxl | `codecs\glidecodecs.NOTICE.txt` |
| Highway (libjxl submodule) | with libjxl 0.13.0 | Apache-2.0 OR BSD-3-Clause (used under BSD-3-Clause) | https://github.com/google/highway | `codecs\glidecodecs.NOTICE.txt` |
| Brotli and skcms (libjxl submodules) | with libjxl 0.13.0 | MIT (Brotli), BSD-3-Clause (skcms) | https://github.com/google/brotli , https://skia.googlesource.com/skcms | upstream repositories |
| OpenJPEG (in `codecs\glidecodecs.dll`) | 2.5.4 | BSD-2-Clause | https://github.com/uclouvain/openjpeg | `codecs\glidecodecs.NOTICE.txt` |
| `codecs\glidemanaged.dll` | 5.0.4 | GPL-3.0-or-later | this repository (`src\Glide.ThumbnailCodecs`) | Glide's own decoders, built from this repository |

The LGPL libraries (libheif, libde265) are statically linked into `codecs\glidecodecs.dll`. Their
complete corresponding source is available at the upstream URLs and exact commits listed in
`codecs\glidecodecs.NOTICE.txt`, and the glue code (`native\Glide.Codecs`) and build script
(`tools\build-codecs.ps1`) in this repository let you rebuild the module against modified versions.

Svg.Custom note: the Free Software Foundation lists the Microsoft Public License (MS-PL) as a free
software licence that is not compatible with the GPL. Svg.Custom is used unmodified, as a separate
package, under its own MS-PL terms (full text: `licenses\Svg.Custom-MS-PL.txt`). Glide's author grants
an additional permission under GPLv3 section 7 to combine Glide with Svg.Custom / Svg.Skia; see
`licenses\GLIDE-ADDITIONAL-PERMISSION.txt`.

## Video playback

Glide ships no video decoders and downloads none. Video overlays play with the codecs already installed in
Windows (Media Foundation, including any Microsoft Store video or audio extensions you install yourself);
when one is missing, Glide names it. Versions 4.6 to 5.0.3 could download an optional FFmpeg decoder pack;
5.0.4 removed it and deletes a previously downloaded copy from `%LOCALAPPDATA%\Glide`.

## Development and packaging only (not distributed with Glide)

| Component | Version | SPDX licence | Notes |
|---|---|---|---|
| Inno Setup | 6 / 7 | Inno Setup licence (free to use; the generated setup may be distributed) | https://jrsoftware.org/isinfo.php ; builds `Glide Setup.exe`; not linked into Glide |
| xunit, xunit.runner.visualstudio, Microsoft.NET.Test.Sdk, Avalonia.Headless | 2.9.2, 2.8.2, 17.11.1, 11.3.20 | Apache-2.0 (xunit), MIT (the others) | test projects only |
| pypdn test data (`tests\Glide.Core.Tests\Fixtures\Pdn`) | n/a | MIT | https://github.com/addisonElliott/pypdn ; licence text in `tests\Glide.Core.Tests\Fixtures\Pdn\LICENSE-pypdn.txt` |

## MIT licence (applies to the MIT-licensed .NET packages above)

Copyright holders: The AvaloniaUI Project (Avalonia); Nikita Tsukanov (MicroCom.Runtime); Wiesław
Šoltés (Svg.Skia, Svg.Model, ShimSkiaSharp); the ExCSS authors; Microsoft Corporation and the .NET
Foundation (System.Drawing.Common, .NET).

```
Permission is hereby granted, free of charge, to any person obtaining a copy of this software and
associated documentation files (the "Software"), to deal in the Software without restriction,
including without limitation the rights to use, copy, modify, merge, publish, distribute,
sublicense, and/or sell copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all copies or
substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT
NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND
NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM,
DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT
OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
```
