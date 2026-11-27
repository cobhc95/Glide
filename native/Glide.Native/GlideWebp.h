#pragma once
// 5.1.7 libwebp (vendored, decode only, multi-threaded) path for still WebP files: decoder-native reduced-size decode
// (libwebp rescales each output row as it decodes, so a 24 MP file never allocates a 96 MB frame for a thumbnail or first
// view). The full-resolution decode stays with Skia (measured 97 ms against libwebp's 177 ms for 24 MP). The function
// returns false when the file is not a still WebP this path handles (animated, damaged, over the pixel cap): the caller then
// keeps the previous route (WIC / Skia), so behaviour for those files is unchanged.
#include <cstdint>
#include <windows.h>

struct GlideDecodedImage;

bool GlideIsWebpPath(const wchar_t* path);

// Reduced decode that fits the box (aspect preserved, never upscaled). Output: 32bpp premultiplied BGRA in CoTaskMem.
// thumbnail: nearest-integer output size and the fast (non-fancy) chroma upsampling, which is invisible after a large
// reduction; the viewer's first view keeps fancy upsampling and floors the size like the WIC bounded decode.
bool GlideWebpDecodeBounded(const wchar_t* path, std::uint32_t maxWidth, std::uint32_t maxHeight, bool thumbnail, GlideDecodedImage* image);
