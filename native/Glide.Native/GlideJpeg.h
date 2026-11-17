#pragma once
// 5.0.7 libjpeg-turbo JPEG path (docs/perf/jpeg-first-frame-plan-5.0.7.md, report section 6).
// Every function returns false when the file is not a JPEG this path handles (CMYK/YCCK, 12/16-bit,
// lossless, unknown subsampling, any libjpeg-turbo warning or error); the caller then decodes it with WIC
// exactly as before, so behaviour for those files is unchanged.
#include <cstdint>
#include <windows.h>

struct GlideDecodedImage;
typedef void* (__cdecl* GlideSurfaceAllocator)(void* context, std::uint32_t width, std::uint32_t height, std::uint32_t* stride);

// Shared with GlideNative.cpp (same EXIF mapping, so turbo and WIC results are interchangeable).
void GlideOrientBgra(const std::uint8_t* source, std::uint32_t width, std::uint32_t height,
                     std::uint16_t orientation, std::uint8_t* destination, std::uint32_t destinationWidth);
void GlideOrientIntoSurface(const std::uint8_t* source, std::uint32_t width, std::uint32_t height, std::uint16_t orientation,
                            std::uint8_t* destination, std::uint32_t destinationStride);
bool GlideWithinDecodeCap(std::uint64_t width, std::uint64_t height);

// Bounded first view: only the camera's embedded MPF large preview (when big enough); every other JPEG returns
// false and keeps WIC's scaled first view. Output is 32bpp BGRA (opaque) in CoTaskMem, EXIF orientation applied.
bool GlideTurboDecodeBounded(const wchar_t* path, std::uint32_t maxWidth, std::uint32_t maxHeight, GlideDecodedImage* image);

// Full-resolution decode into the caller's surface. Baseline: parallel horizontal bands; progressive: one
// thread (banding a progressive file repeats the whole multi-scan decode per band and multiplies memory).
// *surface / *stride report a surface already obtained from the allocator, so a WIC fallback can reuse it.
bool GlideTurboDecodeInto(const wchar_t* path, GlideSurfaceAllocator allocate, void* context,
                          std::uint8_t** surface, std::uint32_t* stride, std::uint32_t* surfaceWidth, std::uint32_t* surfaceHeight);

// True when the bounded first view should be tried with libjpeg-turbo (path has a JPEG suffix).
bool GlideIsJpegPath(const wchar_t* path);

// Diagnostics/tests: true when the libjpeg-turbo path accepts this file's header (false = WIC decodes it).
bool GlideTurboAccepts(const wchar_t* path);
