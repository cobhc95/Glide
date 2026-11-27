#pragma once
// 5.2.0 CMYK -> BGRA for the libjpeg-turbo JPEG path and the native TIFF reader.
//
// Windows' own CMYK converter (the one WIC's JPEG / TIFF decoders hand a CMYK frame to, ICC-managed through the default CMYK
// profile) costs about 40 ns per pixel and is single-threaded: 340 ms of the 428 ms a 4K CMYK JPEG took to open. To keep the
// colours exactly as Windows shows them, but fast, the converter is sampled once per process: a 17 x 17 x 17 x 17 lattice of
// CMYK values is pushed through IWICFormatConverter (83521 pixels, a few milliseconds) and every later pixel is interpolated
// from that lattice (tetrahedral in C, M, Y, linear in K), on as many threads as the caller likes.
#include <cstddef>
#include <cstdint>

// True when the lattice is available (built on the first call, thread-safe). False: WIC could not convert CMYK on this PC and
// the caller must decline the file so WIC decodes it as before.
bool GlideCmykReady();

// `pixels` CMYK pixels (4 bytes each) -> opaque BGRA. inverted: the samples are Adobe style (255 = no ink), which is how every
// CMYK JPEG is stored and what WIC assumes; false: natural ink amounts (TIFF "separated", 0 = no ink).
// GlideCmykReady() must have returned true.
void GlideCmykToBgra(const std::uint8_t* cmyk, std::size_t pixels, std::uint8_t* bgra, bool inverted);

// Tests/diagnostics: converts one natural-ink CMYK value with the lattice (BGRA out).
void GlideCmykConvertOne(std::uint8_t c, std::uint8_t m, std::uint8_t y, std::uint8_t k, std::uint8_t* bgra);
