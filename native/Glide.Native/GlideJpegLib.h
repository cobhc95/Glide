#pragma once
// Minimal declarations of the libjpeg (jpeglib.h) API that libjpeg-turbo 3.2.1's static library exports, for the one place
// Glide needs it: buffered-image decoding of a progressive JPEG that stops after the first scans (GlideJpegProgressive.cpp).
// The vendored package ships only turbojpeg.h, and TurboJPEG has no way to stop a progressive decode early
// (TJPARAM_SCANLIMIT only rejects files). These declarations are written for the libjpeg 6.2 ABI the library is built with
// (JPEG_LIB_VERSION 62, 8-bit samples, x64): jpeg_CreateDecompress compares the structure size and the library version with
// what is passed here and fails (JERR_BAD_STRUCT_SIZE / JERR_BAD_LIBVERSION) when they differ, so a mismatching library is
// refused at run time, never misread. Only the leading fields that are used are named; the rest is opaque padding.
#include <cstddef>
#include <cstdint>

namespace gjpeg
{
constexpr int kLibVersion = 62;
constexpr int kDctSize2 = 64;

enum ColorSpace : int { Unknown = 0, Grayscale = 1, Rgb = 2, YCbCr = 3, Cmyk = 4, Ycck = 5, ExtBgra = 13 };
enum DctMethod : int { Islow = 0, Ifast = 1, Float = 2 };
enum ConsumeResult : int { Suspended = 0, ReachedSos = 1, ReachedEoi = 2, RowCompleted = 3, ScanCompleted = 4 };

struct Source
{
    const std::uint8_t* next_input_byte;
    std::size_t bytes_in_buffer;
    void (*init_source)(void* cinfo);
    int (*fill_input_buffer)(void* cinfo);
    void (*skip_input_data)(void* cinfo, long num_bytes);
    int (*resync_to_restart)(void* cinfo, int desired);
    void (*term_source)(void* cinfo);
};

// Size of struct jpeg_decompress_struct in the vendored library (64-bit, JPEG_LIB_VERSION 62); checked by the library itself.
constexpr std::size_t kDecompressSize = 600;

struct Decompress
{
    void* err;
    void* mem;
    void* progress;
    void* client_data;
    int is_decompressor;
    int global_state;
    Source* src;
    unsigned image_width;
    unsigned image_height;
    int num_components;
    int jpeg_color_space;
    int out_color_space;
    unsigned scale_num;
    unsigned scale_denom;
    double output_gamma;
    int buffered_image;
    int raw_data_out;
    int dct_method;
    int do_fancy_upsampling;
    int do_block_smoothing;
    int quantize_colors;
    int dither_mode;
    // (libjpeg-turbo 3.2.1 keeps the colour-quantisation fields elsewhere: output_width follows dither_mode; offsets verified
    // against the library, see the self test in JpegProgressiveThumbnailTests)
    unsigned output_width;
    unsigned output_height;
    int out_color_components;
    int output_components;
    int rec_outbuf_height;
    int actual_number_of_colors;
    unsigned char** colormap;
    unsigned output_scanline;
    int input_scan_number;
    unsigned input_iMCU_row;
    int output_scan_number;
    unsigned output_iMCU_row;
    int (*coef_bits)[kDctSize2];
    // ... quantisation / Huffman tables, component info and decoder state follow in the library's structure (not used here):
    // the structure is always allocated as kDecompressSize bytes of storage and viewed through this prefix.
};

// Leading members of struct jpeg_error_mgr (the library fills the rest in jpeg_std_error).
struct ErrorMgr
{
    void (*error_exit)(void* cinfo);
    void (*emit_message)(void* cinfo, int msg_level);
    void (*output_message)(void* cinfo);
    void (*format_message)(void* cinfo, char* buffer);
    void (*reset_error_mgr)(void* cinfo);
    int msg_code;
    union { int i[8]; char s[80]; } msg_parm;
    unsigned char rest[256];
};
}

extern "C"
{
void* jpeg_std_error(void* err);
void jpeg_CreateDecompress(void* cinfo, int version, std::size_t structsize);
void jpeg_destroy_decompress(void* cinfo);
int jpeg_read_header(void* cinfo, int require_image);
int jpeg_start_decompress(void* cinfo);
int jpeg_consume_input(void* cinfo);
int jpeg_start_output(void* cinfo, int scan_number);
unsigned jpeg_read_scanlines(void* cinfo, unsigned char** scanlines, unsigned max_lines);
int jpeg_finish_output(void* cinfo);
int jpeg_finish_decompress(void* cinfo);
int jpeg_resync_to_restart(void* cinfo, int desired);
}
