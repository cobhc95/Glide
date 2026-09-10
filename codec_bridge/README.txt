Glide modular codec pack
========================

Purpose
-------
Keeps Glide.exe small while providing first-party bundled decode paths for the
modern formats that Windows WIC did not reliably decode on the test machine.
The bridge and its dependencies live only in dist\codecs and are loaded on
demand after WIC/Glide-native decoding fails.

Covered by this module
----------------------
WebP, AVIF/AVIFS, HEIF/HEIC/HIF, JPEG XL, JPEG 2000 codestream/container
(J2K/J2C/JPC/JP2/JPF/JPX), and OpenEXR.

Dependencies
------------
Built through vcpkg from upstream libraries: libwebp, libheif (+AOM/libde265),
libjxl, OpenJPEG, and TinyEXR. Third-party DLLs remain modular and are not
linked into Glide.exe. The first codec build needs Git/network access and may
take substantially longer than normal Glide rebuilds. vcpkg's binary cache
makes later builds much faster.

Licensing
---------
The build copies vcpkg package copyright notices into dist\codecs\licenses.
libheif is LGPL and therefore remains dynamically linked in the codec pack.
Glide's core remains separate from that library.
