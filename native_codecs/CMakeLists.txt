cmake_minimum_required(VERSION 3.21)
project(GlideNativeCodecs VERSION 0.12.107 LANGUAGES C CXX)

if(NOT WIN32)
  message(STATUS "Glide native codecs: non-Windows configure/build is for source validation only")
endif()

set(CMAKE_C_STANDARD 11)
set(CMAKE_C_STANDARD_REQUIRED ON)
set(CMAKE_CXX_STANDARD 17)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
set(CMAKE_CXX_EXTENSIONS OFF)
set(CMAKE_POSITION_INDEPENDENT_CODE ON)

# Keep all generated runtime DLLs together so build_codecs.cmd can stage only
# the actual runtime artifacts and not whole upstream build trees.
set(GLIDE_RUNTIME_DIR "${CMAKE_BINARY_DIR}/runtime")
set(GLIDE_ARCHIVE_DIR "${CMAKE_BINARY_DIR}/lib")
set(CMAKE_RUNTIME_OUTPUT_DIRECTORY "${GLIDE_RUNTIME_DIR}")
set(CMAKE_RUNTIME_OUTPUT_DIRECTORY_RELEASE "${GLIDE_RUNTIME_DIR}")
set(CMAKE_LIBRARY_OUTPUT_DIRECTORY "${GLIDE_RUNTIME_DIR}")
set(CMAKE_LIBRARY_OUTPUT_DIRECTORY_RELEASE "${GLIDE_RUNTIME_DIR}")
set(CMAKE_ARCHIVE_OUTPUT_DIRECTORY "${GLIDE_ARCHIVE_DIR}")
set(CMAKE_ARCHIVE_OUTPUT_DIRECTORY_RELEASE "${GLIDE_ARCHIVE_DIR}")

if(MSVC)
  # Match Glide itself and avoid mixing static/dynamic CRT heaps across the
  # codec C ABI boundary.  Pixel ownership always returns to the bridge before
  # crossing into Glide.exe, but a consistent /MD runtime is safer anyway.
  set(CMAKE_MSVC_RUNTIME_LIBRARY "MultiThreadedDLL")
  add_compile_definitions(_CRT_SECURE_NO_WARNINGS NOMINMAX WIN32_LEAN_AND_MEAN)
endif()

if(NOT DEFINED GLIDE_CODEC_VENDOR_DIR)
  set(GLIDE_CODEC_VENDOR_DIR "${CMAKE_CURRENT_LIST_DIR}/../codec_native_src")
endif()
get_filename_component(GLIDE_CODEC_VENDOR_DIR "${GLIDE_CODEC_VENDOR_DIR}" ABSOLUTE)

set(WEBP_SRC    "${GLIDE_CODEC_VENDOR_DIR}/libwebp-1.6.0")
set(HEIF_SRC    "${GLIDE_CODEC_VENDOR_DIR}/libheif-1.23.4")
set(DE265_SRC   "${GLIDE_CODEC_VENDOR_DIR}/libde265-1.1.1")
set(DAV1D_SRC   "${GLIDE_CODEC_VENDOR_DIR}/dav1d-1.5.1")
set(JXL_SRC     "${GLIDE_CODEC_VENDOR_DIR}/libjxl-0.12.0")
set(OPENJP2_SRC "${GLIDE_CODEC_VENDOR_DIR}/openjpeg-2.5.3")
set(TINYEXR_SRC "${GLIDE_CODEC_VENDOR_DIR}/tinyexr")

foreach(req IN ITEMS WEBP_SRC HEIF_SRC DE265_SRC DAV1D_SRC JXL_SRC OPENJP2_SRC TINYEXR_SRC)
  if(NOT EXISTS "${${req}}")
    message(FATAL_ERROR "Missing Glide codec source: ${${req}}")
  endif()
endforeach()

# ---------------------------------------------------------------------------
# dav1d - decoder-only, scalar C build.
# ---------------------------------------------------------------------------
# dav1d's upstream build is Meson-based. Requiring Python/Meson/NASM purely for
# one AVIF fallback would make Glide's offline build needlessly heavyweight.
# We therefore compile the exact upstream decoder C sources, both supported bit
# depths, with assembly disabled. This is intentionally conservative: AVIF is a
# fallback codec, and correctness/portability matter more than maximum SIMD here.
set(DAV1D_GEN "${CMAKE_BINARY_DIR}/dav1d_generated")
file(MAKE_DIRECTORY "${DAV1D_GEN}")

if(WIN32)
  file(WRITE "${DAV1D_GEN}/config.h" [=[
#pragma once
#define ARCH_AARCH64 0
#define ARCH_ARM 0
#define ARCH_LOONGARCH 0
#define ARCH_LOONGARCH64 0
#define ARCH_PPC64LE 0
#define ARCH_RISCV 0
#define ARCH_X86 0
#define ARCH_X86_32 0
#define ARCH_X86_64 0
#define CONFIG_8BPC 1
#define CONFIG_16BPC 1
#define CONFIG_LOG 0
#define CONFIG_MACOS_KPERF 0
#define HAVE_ASM 0
#define TRIM_DSP_FUNCTIONS 0
#define HAVE_CLOCK_GETTIME 0
#define HAVE_POSIX_MEMALIGN 0
#define HAVE_MEMALIGN 0
#define HAVE_ALIGNED_ALLOC 0
#define HAVE_DLSYM 0
#define HAVE_SYS_TYPES_H 1
#define HAVE_UNISTD_H 0
#define HAVE_IO_H 1
#define HAVE_PTHREAD_NP_H 0
#define HAVE_GETAUXVAL 0
#define HAVE_ELF_AUX_INFO 0
#define HAVE_PTHREAD_GETAFFINITY_NP 0
#define HAVE_PTHREAD_SETAFFINITY_NP 0
#define HAVE_PTHREAD_SETNAME_NP 0
#define HAVE_PTHREAD_SET_NAME_NP 0
#define HAVE_C11_GENERIC 0
#define ENDIANNESS_BIG 0
#define _WIN32_WINNT 0x0601
#define UNICODE 1
#define _UNICODE 1
#define _CRT_DECLARE_NONSTDC_NAMES 1
#define fseeko _fseeki64
#define ftello _ftelli64
]=])
else()
  include(CheckIncludeFile)
  include(CheckSymbolExists)
  check_include_file("unistd.h" GLIDE_HAVE_UNISTD_H)
  check_include_file("pthread_np.h" GLIDE_HAVE_PTHREAD_NP_H)
  file(WRITE "${DAV1D_GEN}/config.h" "#pragma once\n")
  file(APPEND "${DAV1D_GEN}/config.h" "#define ARCH_AARCH64 0\n#define ARCH_ARM 0\n#define ARCH_LOONGARCH 0\n#define ARCH_LOONGARCH64 0\n#define ARCH_PPC64LE 0\n#define ARCH_RISCV 0\n#define ARCH_X86 0\n#define ARCH_X86_32 0\n#define ARCH_X86_64 0\n#define CONFIG_8BPC 1\n#define CONFIG_16BPC 1\n#define CONFIG_LOG 0\n#define CONFIG_MACOS_KPERF 0\n#define HAVE_ASM 0\n#define TRIM_DSP_FUNCTIONS 0\n#define HAVE_CLOCK_GETTIME 1\n#define HAVE_POSIX_MEMALIGN 1\n#define HAVE_MEMALIGN 0\n#define HAVE_ALIGNED_ALLOC 1\n#define HAVE_DLSYM 0\n#define HAVE_SYS_TYPES_H 1\n#define HAVE_UNISTD_H 1\n#define HAVE_IO_H 0\n#define HAVE_PTHREAD_NP_H 0\n#define HAVE_GETAUXVAL 0\n#define HAVE_ELF_AUX_INFO 0\n#define HAVE_PTHREAD_GETAFFINITY_NP 0\n#define HAVE_PTHREAD_SETAFFINITY_NP 0\n#define HAVE_PTHREAD_SETNAME_NP 0\n#define HAVE_PTHREAD_SET_NAME_NP 0\n#define HAVE_C11_GENERIC 1\n#define ENDIANNESS_BIG 0\n")
endif()
file(WRITE "${DAV1D_GEN}/vcs_version.h" "#pragma once\n#define DAV1D_VERSION \"1.5.1\"\n")

set(DAV1D_COMMON_SOURCES
  cdf.c cpu.c ctx.c data.c decode.c dequant_tables.c getbits.c intra_edge.c
  itx_1d.c lf_mask.c lib.c log.c mem.c msac.c obu.c pal.c picture.c qm.c
  ref.c refmvs.c scan.c tables.c thread_task.c warpmv.c wedge.c)
list(TRANSFORM DAV1D_COMMON_SOURCES PREPEND "${DAV1D_SRC}/src/")
if(WIN32)
  list(APPEND DAV1D_COMMON_SOURCES "${DAV1D_SRC}/src/win32/thread.c")
endif()
set(DAV1D_TMPL_SOURCES
  cdef_apply_tmpl.c cdef_tmpl.c fg_apply_tmpl.c filmgrain_tmpl.c
  ipred_prepare_tmpl.c ipred_tmpl.c itx_tmpl.c lf_apply_tmpl.c
  loopfilter_tmpl.c looprestoration_tmpl.c lr_apply_tmpl.c mc_tmpl.c recon_tmpl.c)
list(TRANSFORM DAV1D_TMPL_SOURCES PREPEND "${DAV1D_SRC}/src/")

add_library(dav1d_8bpc OBJECT ${DAV1D_TMPL_SOURCES})
target_compile_definitions(dav1d_8bpc PRIVATE BITDEPTH=8)
add_library(dav1d_16bpc OBJECT ${DAV1D_TMPL_SOURCES})
target_compile_definitions(dav1d_16bpc PRIVATE BITDEPTH=16)
foreach(tgt IN ITEMS dav1d_8bpc dav1d_16bpc)
  target_include_directories(${tgt} PRIVATE
    "${DAV1D_GEN}" "${DAV1D_SRC}" "${DAV1D_SRC}/include")
  if(MSVC)
    target_include_directories(${tgt} BEFORE PRIVATE "${DAV1D_SRC}/include/compat/msvc")
    target_compile_options(${tgt} PRIVATE /wd4028 /wd4090 /wd4996)
  endif()
endforeach()

add_library(dav1d_glide STATIC ${DAV1D_COMMON_SOURCES}
  $<TARGET_OBJECTS:dav1d_8bpc> $<TARGET_OBJECTS:dav1d_16bpc>)
target_include_directories(dav1d_glide PUBLIC "${DAV1D_SRC}/include" PRIVATE "${DAV1D_GEN}" "${DAV1D_SRC}")
if(MSVC)
  target_include_directories(dav1d_glide BEFORE PRIVATE "${DAV1D_SRC}/include/compat/msvc")
  target_compile_options(dav1d_glide PRIVATE /wd4028 /wd4090 /wd4996)
else()
  find_package(Threads REQUIRED)
  target_link_libraries(dav1d_glide PRIVATE Threads::Threads m)
endif()
set_target_properties(dav1d_glide PROPERTIES OUTPUT_NAME "dav1d_glide")

# ---------------------------------------------------------------------------
# libde265 - HEVC decoder only. Keep it as a separate shared DLL because its
# LGPL terms are clearest when the user can replace the library independently.
# ---------------------------------------------------------------------------
set(ENABLE_DECODER ON CACHE BOOL "" FORCE)
set(ENABLE_ENCODER OFF CACHE BOOL "" FORCE)
set(ENABLE_SDL OFF CACHE BOOL "" FORCE)
set(ENABLE_SHERLOCK265 OFF CACHE BOOL "" FORCE)
set(ENABLE_INTERNAL_DEVELOPMENT_TOOLS OFF CACHE BOOL "" FORCE)
set(ENABLE_AVX512 OFF CACHE BOOL "" FORCE)
set(BUILD_SHARED_LIBS ON)
add_subdirectory("${DE265_SRC}" libde265-build EXCLUDE_FROM_ALL)

# ---------------------------------------------------------------------------
# libheif - container/parser + the decoders Glide actually needs:
# libde265 for HEVC, dav1d for AV1, and the ISO uncompressed-image decoder.
# All unrelated tools/plugins remain disabled.
# ---------------------------------------------------------------------------
set(LIBDE265_INCLUDE_DIR "${DE265_SRC}" CACHE PATH "" FORCE)
set(LIBDE265_LIBRARY de265 CACHE STRING "" FORCE)
set(DAV1D_INCLUDE_DIR "${DAV1D_SRC}/include" CACHE PATH "" FORCE)
set(DAV1D_LIBRARY dav1d_glide CACHE STRING "" FORCE)
set(WITH_LIBDE265 ON CACHE BOOL "" FORCE)
set(WITH_LIBDE265_PLUGIN OFF CACHE BOOL "" FORCE)
set(WITH_DAV1D ON CACHE BOOL "" FORCE)
set(WITH_DAV1D_PLUGIN OFF CACHE BOOL "" FORCE)
foreach(opt IN ITEMS
  WITH_X265 WITH_KVAZAAR WITH_UVG266 WITH_VVDEC WITH_VVENC WITH_X264
  WITH_OpenH264_DECODER WITH_OpenH264_ENCODER WITH_AOM_DECODER WITH_AOM_ENCODER
  WITH_SvtEnc WITH_RAV1E WITH_JPEG_DECODER WITH_JPEG_ENCODER
  WITH_OpenJPEG_ENCODER WITH_OpenJPEG_DECODER WITH_FFMPEG_DECODER
  WITH_OPENJPH_ENCODER WITH_OPENJPH_DECODER)
  set(${opt} OFF CACHE BOOL "" FORCE)
endforeach()
set(ENABLE_PLUGIN_LOADING OFF CACHE BOOL "" FORCE)
set(WITH_LIBSHARPYUV OFF CACHE BOOL "" FORCE)
set(WITH_LIBSHARPYUV_INTERNAL OFF CACHE BOOL "" FORCE)
set(WITH_UNCOMPRESSED_CODEC ON CACHE BOOL "" FORCE)
set(WITH_HEADER_COMPRESSION OFF CACHE BOOL "" FORCE)
set(WITH_EXAMPLES OFF CACHE BOOL "" FORCE)
set(WITH_EXAMPLE_HEIF_THUMB OFF CACHE BOOL "" FORCE)
set(WITH_EXAMPLE_HEIF_VIEW OFF CACHE BOOL "" FORCE)
set(WITH_GDK_PIXBUF OFF CACHE BOOL "" FORCE)
set(BUILD_DEVELOPMENT_TOOLS OFF CACHE BOOL "" FORCE)
set(BUILD_DOCUMENTATION OFF CACHE BOOL "" FORCE)
set(BUILD_TESTING OFF CACHE BOOL "" FORCE)
set(WITH_FUZZERS OFF CACHE BOOL "" FORCE)
set(BUILD_SHARED_LIBS ON)
add_subdirectory("${HEIF_SRC}" libheif-build EXCLUDE_FROM_ALL)
# libde265 generates de265-version.h in its binary tree; libheif includes it
# transitively from the public source header, so expose that generated include.
target_include_directories(heif PRIVATE "${CMAKE_BINARY_DIR}/libde265-build")
add_dependencies(heif dav1d_glide de265)

# ---------------------------------------------------------------------------
# libwebp - decoder library only. No encoder, mux tools, examples or utilities.
# ---------------------------------------------------------------------------
set(BUILD_SHARED_LIBS OFF)
set(WEBP_BUILD_ANIM_UTILS OFF CACHE BOOL "" FORCE)
set(WEBP_BUILD_CWEBP OFF CACHE BOOL "" FORCE)
set(WEBP_BUILD_DWEBP OFF CACHE BOOL "" FORCE)
set(WEBP_BUILD_GIF2WEBP OFF CACHE BOOL "" FORCE)
set(WEBP_BUILD_IMG2WEBP OFF CACHE BOOL "" FORCE)
set(WEBP_BUILD_VWEBP OFF CACHE BOOL "" FORCE)
set(WEBP_BUILD_WEBPINFO OFF CACHE BOOL "" FORCE)
set(WEBP_BUILD_LIBWEBPMUX OFF CACHE BOOL "" FORCE)
set(WEBP_BUILD_WEBPMUX OFF CACHE BOOL "" FORCE)
set(WEBP_BUILD_EXTRAS OFF CACHE BOOL "" FORCE)
set(WEBP_BUILD_FUZZTEST OFF CACHE BOOL "" FORCE)
add_subdirectory("${WEBP_SRC}" libwebp-build EXCLUDE_FROM_ALL)
# libwebp's stock webpdemux target links the full webp encoder+decoder library.
# Glide only needs animation demux + decode, so build the two demux sources
# directly against webpdecoder and avoid carrying encoder code.
add_library(webpdemux_glide STATIC
  "${WEBP_SRC}/src/demux/anim_decode.c"
  "${WEBP_SRC}/src/demux/demux.c")
target_include_directories(webpdemux_glide PUBLIC "${WEBP_SRC}/src" PRIVATE "${WEBP_SRC}")
target_link_libraries(webpdemux_glide PUBLIC webpdecoder)

# ---------------------------------------------------------------------------
# OpenJPEG - core JPEG 2000 decoder library only.
# ---------------------------------------------------------------------------
set(BUILD_SHARED_LIBS OFF)
set(BUILD_STATIC_LIBS ON CACHE BOOL "" FORCE)
set(BUILD_CODEC OFF CACHE BOOL "" FORCE)
set(BUILD_JPIP OFF CACHE BOOL "" FORCE)
set(BUILD_VIEWER OFF CACHE BOOL "" FORCE)
set(BUILD_JAVA OFF CACHE BOOL "" FORCE)
set(BUILD_DOC OFF CACHE BOOL "" FORCE)
set(BUILD_UNIT_TESTS OFF CACHE BOOL "" FORCE)
set(BUILD_TESTING OFF CACHE BOOL "" FORCE)
add_subdirectory("${OPENJP2_SRC}" openjpeg-build EXCLUDE_FROM_ALL)

# ---------------------------------------------------------------------------
# TinyEXR - direct decoder source + miniz only. No examples/test images/tools.
# ---------------------------------------------------------------------------
add_library(tinyexr_glide STATIC
  "${TINYEXR_SRC}/tinyexr.cc"
  "${TINYEXR_SRC}/deps/miniz/miniz.c")
target_include_directories(tinyexr_glide PUBLIC "${TINYEXR_SRC}" "${TINYEXR_SRC}/deps/miniz")
target_compile_definitions(tinyexr_glide PUBLIC TINYEXR_USE_MINIZ=1 TINYEXR_USE_THREAD=0)

# ---------------------------------------------------------------------------
# JPEG XL - decoder-only public target. The source staging step places the exact
# pinned Highway/Brotli/minimal skcms revisions expected by libjxl into its
# third_party tree. JPEG reconstruction, tools, tests, examples and CMS library
# are deliberately not part of Glide's target graph.
# ---------------------------------------------------------------------------
set(BUILD_SHARED_LIBS OFF)
set(BUILD_TESTING OFF CACHE BOOL "" FORCE)
set(JPEGXL_ENABLE_TOOLS OFF CACHE BOOL "" FORCE)
set(JPEGXL_ENABLE_DEVTOOLS OFF CACHE BOOL "" FORCE)
set(JPEGXL_ENABLE_DOXYGEN OFF CACHE BOOL "" FORCE)
set(JPEGXL_ENABLE_MANPAGES OFF CACHE BOOL "" FORCE)
set(JPEGXL_ENABLE_BENCHMARK OFF CACHE BOOL "" FORCE)
set(JPEGXL_ENABLE_EXAMPLES OFF CACHE BOOL "" FORCE)
set(JPEGXL_ENABLE_JNI OFF CACHE BOOL "" FORCE)
set(JPEGXL_ENABLE_SJPEG OFF CACHE BOOL "" FORCE)
set(JPEGXL_ENABLE_OPENEXR OFF CACHE BOOL "" FORCE)
set(JPEGXL_ENABLE_SKCMS ON CACHE BOOL "" FORCE)
set(JPEGXL_ENABLE_VIEWERS OFF CACHE BOOL "" FORCE)
set(JPEGXL_ENABLE_PLUGINS OFF CACHE BOOL "" FORCE)
set(JPEGXL_ENABLE_TRANSCODE_JPEG OFF CACHE BOOL "" FORCE)
set(JPEGXL_ENABLE_BOXES ON CACHE BOOL "" FORCE)
set(JPEGXL_ENABLE_LTO OFF CACHE BOOL "" FORCE)
set(JPEGXL_BUNDLE_LIBPNG OFF CACHE BOOL "" FORCE)
set(JPEGXL_ENABLE_TCMALLOC OFF CACHE BOOL "" FORCE)
set(JPEGXL_FORCE_SYSTEM_BROTLI OFF CACHE BOOL "" FORCE)
set(JPEGXL_FORCE_SYSTEM_HWY OFF CACHE BOOL "" FORCE)
# Keep broadly useful SIMD, but omit the largest AVX-512 tier from this rare
# fallback path to reduce binary size and compile time without harming format support.
set(JPEGXL_ENABLE_HWY_AVX3 OFF CACHE BOOL "" FORCE)
add_subdirectory("${JXL_SRC}" libjxl-build EXCLUDE_FROM_ALL)

# ---------------------------------------------------------------------------
# Glide's stable C ABI bridge. Most permissive codecs are statically absorbed
# into this single DLL; LGPL HEIF/HEVC components stay replaceable DLLs.
# ---------------------------------------------------------------------------
add_library(GlideCodecBridge SHARED "${CMAKE_CURRENT_LIST_DIR}/../codec_bridge/codec_bridge.cpp")
target_include_directories(GlideCodecBridge PRIVATE
  "${CMAKE_CURRENT_LIST_DIR}/.."
  "${WEBP_SRC}/src"
  "${HEIF_SRC}/libheif/api"
  "${CMAKE_BINARY_DIR}/libheif-build"
  "${CMAKE_BINARY_DIR}/libheif-build/libheif"
  "${JXL_SRC}/lib/include"
  "${OPENJP2_SRC}/src/lib/openjp2"
  "${CMAKE_BINARY_DIR}/openjpeg-build/src/lib/openjp2"
  "${TINYEXR_SRC}")
target_compile_definitions(GlideCodecBridge PRIVATE UNICODE _UNICODE WIN32_LEAN_AND_MEAN NOMINMAX)
if(MSVC)
  target_compile_options(GlideCodecBridge PRIVATE /W4 /permissive- /EHsc)
endif()
target_link_libraries(GlideCodecBridge PRIVATE webpdecoder webpdemux_glide heif jxl_dec openjp2 tinyexr_glide)
set_target_properties(GlideCodecBridge PROPERTIES OUTPUT_NAME "GlideCodecBridge")

# A small marker generated alongside the runtime helps Glide diagnostics/users
# identify exactly which native codec set is present without executing helpers.
file(GENERATE OUTPUT "${GLIDE_RUNTIME_DIR}/GLIDE_NATIVE_CODEC_BUILD.txt" CONTENT
"Glide Native Codec Bridge Alpha 0.12107\nWebP=libwebp 1.6.0\nHEIF_HEIC=libheif 1.23.4 + libde265 1.1.1\nHEIF_UNCOMPRESSED=libheif ISO 23001-17 decoder enabled\nAVIF=libheif 1.23.4 + dav1d 1.5.1 scalar\nJPEG_XL=libjxl 0.12.0 decoder-only\nJPEG_2000=OpenJPEG 2.5.3\nOpenEXR=TinyEXR 3.2.0\n")
