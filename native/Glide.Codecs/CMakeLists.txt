cmake_minimum_required(VERSION 3.20)
project(GlideCodecs LANGUAGES C CXX)

# Glide bundled optional codec provider (ABI v2). Built by tools/build-codecs.ps1, which first builds
# the pinned decoder libraries (dav1d, libde265, libheif, libjxl, OpenJPEG) as size-optimised /GL
# static libraries into GLIDE_CODECS_PREFIX. This project only links them with the provider glue, plus
# the vendored libwebp (native/third_party/libwebp) whose encoder backs the Save as WebP option.
set(GLIDE_CODECS_PREFIX "" CACHE PATH "Install prefix holding the static decoder libraries")
option(GLIDE_CODECS_HEVC "Include HEIC/HEIF (HEVC via libde265; patent-encumbered, may be dropped)" ON)
option(GLIDE_CODECS_MAP "Write a linker map (glidecodecs.map) for size audits" OFF)
set(GLIDE_CODECS_VERSION "1.0.0")
set(GLIDE_CODECS_ID "glide.codecs")
set(GLIDE_CODECS_LICENSE "BSD-2-Clause AND BSD-3-Clause AND LGPL-3.0-or-later")

if(NOT GLIDE_CODECS_PREFIX OR NOT EXISTS "${GLIDE_CODECS_PREFIX}/include/libheif/heif.h")
  message(FATAL_ERROR "GLIDE_CODECS_PREFIX must point at the prefix produced by tools/build-codecs.ps1")
endif()

# Single source of truth for the routed suffixes: compiled into GlideCodecProvider_GetInfo and written
# into the sibling manifest, so the two can never disagree (the runtime rejects any mismatch).
set(GLIDE_CODECS_EXTENSIONS .avif .avifs .jxl .jp2 .j2k .j2c .jpc .jpx .jpf .jpm .mj2)
if(GLIDE_CODECS_HEVC)
  list(APPEND GLIDE_CODECS_EXTENSIONS .heic .heif .heics .heifs .hif)
endif()
set(_ext_c "")
foreach(_ext IN LISTS GLIDE_CODECS_EXTENSIONS)
  string(APPEND _ext_c "\"${_ext}\", ")
endforeach()
configure_file(glide_codecs_config.h.in "${CMAKE_CURRENT_BINARY_DIR}/glide_codecs_config.h" @ONLY)

# Vendored libwebp 1.5.0 (encoder for Save as WebP; Glide decodes WebP through Skia/WIC, so the decoder
# half is dropped by /OPT:REF). Same size-first /GL flags as the other static libraries. The Explorer
# thumbnail provider builds its own decode-only copy (webpdecoder) and is not affected.
foreach(_opt ANIM_UTILS CWEBP DWEBP GIF2WEBP IMG2WEBP VWEBP WEBPINFO LIBWEBPMUX WEBPMUX EXTRAS WEBP_JS)
  set(WEBP_BUILD_${_opt} OFF CACHE BOOL "" FORCE)
endforeach()
set(WEBP_USE_THREAD OFF CACHE BOOL "" FORCE)
set(WEBP_NEAR_LOSSLESS OFF CACHE BOOL "" FORCE)
set(_glide_c_flags_release "${CMAKE_C_FLAGS_RELEASE}")
set(_glide_static_flags_release "${CMAKE_STATIC_LINKER_FLAGS_RELEASE}")
if(MSVC)
  set(CMAKE_C_FLAGS_RELEASE "/O1 /Gw /GL /Gy /guard:cf /DNDEBUG")
  set(CMAKE_STATIC_LINKER_FLAGS_RELEASE "/LTCG")
endif()
add_subdirectory("${CMAKE_CURRENT_SOURCE_DIR}/../third_party/libwebp" "${CMAKE_CURRENT_BINARY_DIR}/libwebp" EXCLUDE_FROM_ALL)
set(CMAKE_C_FLAGS_RELEASE "${_glide_c_flags_release}")
set(CMAKE_STATIC_LINKER_FLAGS_RELEASE "${_glide_static_flags_release}")

add_library(glidecodecs SHARED GlideCodecs.cpp)
target_compile_features(glidecodecs PRIVATE cxx_std_20)
target_include_directories(glidecodecs PRIVATE "${CMAKE_CURRENT_BINARY_DIR}" "${GLIDE_CODECS_PREFIX}/include"
  "${GLIDE_CODECS_PREFIX}/include/openjpeg-2.5" "${CMAKE_CURRENT_SOURCE_DIR}/.."
  "${CMAKE_CURRENT_SOURCE_DIR}/../third_party/libwebp/src")
target_compile_definitions(glidecodecs PRIVATE UNICODE _UNICODE WIN32_LEAN_AND_MEAN NOMINMAX
  LIBHEIF_STATIC_BUILD LIBDE265_STATIC_BUILD JXL_STATIC_DEFINE JXL_THREADS_STATIC_DEFINE JXL_CMS_STATIC_DEFINE OPJ_STATIC)
set_target_properties(glidecodecs PROPERTIES OUTPUT_NAME "glidecodecs" PREFIX "")

set(_libs "${GLIDE_CODECS_PREFIX}/lib/heif.lib" "${GLIDE_CODECS_PREFIX}/lib/libdav1d.a"
  "${GLIDE_CODECS_PREFIX}/lib/jxl_dec.lib" "${GLIDE_CODECS_PREFIX}/lib/jxl_cms.lib" "${GLIDE_CODECS_PREFIX}/lib/jxl_threads.lib"
  "${GLIDE_CODECS_PREFIX}/lib/hwy.lib" "${GLIDE_CODECS_PREFIX}/lib/openjp2.lib")
if(GLIDE_CODECS_HEVC)
  file(GLOB _de265 "${GLIDE_CODECS_PREFIX}/lib/*de265*.lib")
  list(APPEND _libs ${_de265})
endif()
target_link_libraries(glidecodecs PRIVATE ${_libs} webp)

if(MSVC)
  string(REPLACE "/O2" "" CMAKE_CXX_FLAGS_RELEASE "${CMAKE_CXX_FLAGS_RELEASE}")
  string(REPLACE "/Ob2" "" CMAKE_CXX_FLAGS_RELEASE "${CMAKE_CXX_FLAGS_RELEASE}")
  # Size first: /O1 + whole-program optimisation across the /GL static libraries, identical-COMDAT
  # folding and unreferenced-section removal (drops every decoder/encoder/tool path the glue never calls).
  # Hardening matches Glide.Native: CFG, CET shadow stack compatibility, ASLR and DEP. /sdl applies
  # to the glue only; the third-party libraries are compiled with their upstream warning settings.
  target_compile_options(glidecodecs PRIVATE /O1 /Gw /GL /Gy /guard:cf /sdl /W4 /EHsc /DNDEBUG)
  target_link_options(glidecodecs PRIVATE /LTCG /OPT:REF /OPT:ICF /INCREMENTAL:NO /DEBUG:NONE /Brepro
    /guard:cf /CETCOMPAT /DYNAMICBASE /HIGHENTROPYVA /NXCOMPAT /IGNORE:4099)
  if(GLIDE_CODECS_MAP)
    target_link_options(glidecodecs PRIVATE "/MAP:${CMAKE_CURRENT_BINARY_DIR}/glidecodecs.map")
  endif()
endif()

# Write the signed-by-hash manifest next to the DLL after every link.
add_custom_command(TARGET glidecodecs POST_BUILD
  COMMAND "${CMAKE_COMMAND}" "-DDLL=$<TARGET_FILE:glidecodecs>"
          "-DOUT=$<TARGET_FILE_DIR:glidecodecs>/glidecodecs.glidecodec.json"
          "-DID=${GLIDE_CODECS_ID}" "-DVERSION=${GLIDE_CODECS_VERSION}" "-DLICENSE=${GLIDE_CODECS_LICENSE}"
          "-DEXTENSIONS=${GLIDE_CODECS_EXTENSIONS}"
          -P "${CMAKE_CURRENT_SOURCE_DIR}/write-manifest.cmake"
  VERBATIM)
