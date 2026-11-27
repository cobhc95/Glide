#pragma once
// 5.2.0 native TIFF reader (docs/audits/format-speed-audit-5.2.0.md, improvement #2).
//
// WIC decodes a tiled TIFF about six times slower than the same picture in strips, cannot read BigTIFF at all, and ignores the
// reduced-resolution pyramid (SubIFD / IFD pages with NewSubfileType 1) that a pyramid TIFF carries for exactly this purpose.
// This reader parses the container itself (classic TIFF and BigTIFF, both byte orders), decodes strips and tiles in parallel
// (own inflate / LZW / PackBits, horizontal predictor, 8 and 16 bit gray / RGB / RGBA / CMYK), picks the smallest pyramid level
// that covers a request and box-reduces during the decode when no level does. Anything else (JPEG-in-TIFF, palette, bilevel,
// float, planar, YCbCr, Lab, damaged files) is declined: every entry point returns false and the caller keeps WIC's decode.
// Nothing here throws across the C boundary, and every offset and count read from the file is bounds-checked.
#include <cstdint>
#include <windows.h>

struct GlideDecodedImage;
typedef void* (__cdecl* GlideSurfaceAllocator)(void* context, std::uint32_t width, std::uint32_t height, std::uint32_t* stride);

bool GlideIsTiffPath(const wchar_t* path);

// Bounded decode of the first image (EXIF orientation applied, 32bpp premultiplied BGRA in CoTaskMem). The result covers the
// request: it is the smallest pyramid level whose size is at least the fitted size, box-reduced by a power of two while it still
// covers it; it can be up to about twice the fitted size (the caller area-averages, or the viewer shows it as is).
// thumbnail: Explorer tab request (fewer threads, same output). A request that is not smaller than the image decodes it at full
// size only when this reader is the better choice for the file (tiled, BigTIFF, pyramid); otherwise false (WIC does it).
// forceOwn (tests / diagnostics): decode with this reader even where WIC is the better choice for the file; maxWidth / maxHeight of 0 = full size.
bool GlideTiffDecodeBounded(const wchar_t* path, std::uint32_t maxWidth, std::uint32_t maxHeight, bool thumbnail, GlideDecodedImage* image,
                            bool forceOwn = false);

// Full resolution into the caller's surface (EXIF orientation applied). The allocator is only called once the whole file has been
// validated; *surface etc. report the surface so a fallback can reuse it. False without calling the allocator: not handled here.
bool GlideTiffDecodeInto(const wchar_t* path, GlideSurfaceAllocator allocate, void* context, std::uint8_t** surface, std::uint32_t* stride,
                         std::uint32_t* surfaceWidth, std::uint32_t* surfaceHeight);

// Dimensions of the first image as stored (orientation not applied) for files WIC cannot probe (BigTIFF). False when not a TIFF.
bool GlideTiffProbe(const wchar_t* path, std::uint32_t* width, std::uint32_t* height, std::uint32_t* orientation);

// Planning (tests and the viewer's first-view gate): what GlideTiffDecodeBounded would do for a box.
//   levelWidth/levelHeight: size of the chosen IFD; reduction: power-of-two box reduction applied to it; levels: number of
//   usable pyramid levels besides the first image; flags: bit0 tiled, bit1 BigTIFF, bit2 chosen level is a pyramid level.
// False when the file is not handled by this reader.
bool GlideTiffPlan(const wchar_t* path, std::uint32_t maxWidth, std::uint32_t maxHeight, std::uint32_t* levelWidth, std::uint32_t* levelHeight,
                   std::uint32_t* reduction, std::uint32_t* levels, std::uint32_t* flags);
