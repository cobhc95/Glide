cmake_minimum_required(VERSION 3.20)
# Static CRT (also applies to the vendored libwebp): Explorer's thumbnail host must be able to load this
# DLL on PCs without the VC++ redistributable, which Glide does not ship.
set(CMAKE_MSVC_RUNTIME_LIBRARY "MultiThreaded$<$<CONFIG:Debug>:Debug>")
project(GlideShellThumbnail LANGUAGES C CXX)

# Glide.ShellThumbnail — the Windows Explorer thumbnail provider.
#
# This is a COM in-proc server loaded by Explorer's isolated thumbnail host (dllhost.exe). It is
# deliberately self-contained: it never loads .NET, Avalonia, Glide.exe or any Glide UI/settings
# component. It decodes through the Windows Imaging Component, Direct2D (for SVG), a vendored
# libwebp decoder (WebP) and the compact decoders that ship in ThumbnailCore.cpp.

# ------------------------------------------------------------------------------------------------
# Vendored libwebp — decode only.
#
# Windows does not ship a WebP WIC codec; it comes from the optional "Webp Image Extensions" Store
# package. When that package is absent (the common case on clean Windows installs) the in-box
# "Photo Thumbnail Provider" fails for .webp, so Glide must own WebP itself. libwebp is bundled
# statically so the provider never depends on an optional codec being installed.
# ------------------------------------------------------------------------------------------------
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
set(WEBP_BUILD_WEBP_JS OFF CACHE BOOL "" FORCE)
set(WEBP_USE_THREAD OFF CACHE BOOL "" FORCE)
# Security hardening (5.0): Control Flow Guard and Spectre variant 1 mitigations for every object in the
# provider, including the vendored libwebp (CFG only protects indirect calls in code compiled with it).
# Explorer's thumbnail host loads this DLL, so it must never be the weakest module in that process.
# /Qspectre needs the Spectre-mitigated libraries for the active MSVC toolset (Visual Studio Installer >
# Individual components); MSBuild refuses the project without them (MSB8040), so it is conditional.
if(MSVC)
  set(GLIDE_SPECTRE_OPTION "")
  get_filename_component(_glide_cl_dir "${CMAKE_CXX_COMPILER}" DIRECTORY)
  if(EXISTS "${_glide_cl_dir}/../../../lib/spectre/x64")
    set(GLIDE_SPECTRE_OPTION /Qspectre)
  else()
    message(STATUS "Glide.ShellThumbnail: Spectre-mitigated libraries not installed for this toolset; building without /Qspectre")
  endif()
  add_compile_options(/guard:cf ${GLIDE_SPECTRE_OPTION})
endif()
add_subdirectory("${CMAKE_CURRENT_SOURCE_DIR}/../third_party/libwebp"
                 "${CMAKE_CURRENT_BINARY_DIR}/libwebp"
                 EXCLUDE_FROM_ALL)

# Single-source the provider's version from Directory.Build.props (like build.cmd does) so the DLL's
# version resource always matches Glide.exe. Settings shows it in the Explorer thumbnails status line.
set(GLIDE_VERSION "0.0.0")
file(STRINGS "${CMAKE_CURRENT_SOURCE_DIR}/../../Directory.Build.props" GLIDE_VERSION_LINE REGEX "<Version>[0-9]+\\.[0-9]+\\.[0-9]+</Version>")
if(GLIDE_VERSION_LINE)
    string(REGEX REPLACE ".*<Version>([0-9]+\\.[0-9]+\\.[0-9]+)</Version>.*" "\\1" GLIDE_VERSION "${GLIDE_VERSION_LINE}")
endif()
string(REPLACE "." ";" GLIDE_VERSION_PARTS "${GLIDE_VERSION}")
list(GET GLIDE_VERSION_PARTS 0 GLIDE_VERSION_MAJOR)
list(GET GLIDE_VERSION_PARTS 1 GLIDE_VERSION_MINOR)
list(GET GLIDE_VERSION_PARTS 2 GLIDE_VERSION_PATCH)
configure_file("${CMAKE_CURRENT_SOURCE_DIR}/Glide.ShellThumbnail.rc.in"
               "${CMAKE_CURRENT_BINARY_DIR}/Glide.ShellThumbnail.rc" @ONLY)
set_property(DIRECTORY APPEND PROPERTY CMAKE_CONFIGURE_DEPENDS "${CMAKE_CURRENT_SOURCE_DIR}/../../Directory.Build.props")

# Codec libraries the provider may load (CodecProviders.cpp): their SHA-256 is pinned into the DLL at build
# time, so the thumbnail host only ever maps the exact codecs\glidecodecs.dll / codecs\glidemanaged.dll this
# build shipped with. build.cmd builds both before this project; a library that is absent at configure time
# gets an empty pin and its formats simply get no thumbnails from it. Configure re-runs when either changes.
set(GLIDE_CODECS_DIR "${CMAKE_CURRENT_SOURCE_DIR}/../../codecs")
foreach(_glide_lib glidecodecs glidemanaged)
  string(TOUPPER "${_glide_lib}" _glide_upper)
  set(_glide_path "${GLIDE_CODECS_DIR}/${_glide_lib}.dll")
  if(EXISTS "${_glide_path}")
    file(SHA256 "${_glide_path}" GLIDE_PIN_${_glide_upper})
    set_property(DIRECTORY APPEND PROPERTY CMAKE_CONFIGURE_DEPENDS "${_glide_path}")
    message(STATUS "Glide.ShellThumbnail: pinned ${_glide_lib}.dll ${GLIDE_PIN_${_glide_upper}}")
  else()
    set(GLIDE_PIN_${_glide_upper} "")
    message(STATUS "Glide.ShellThumbnail: ${_glide_lib}.dll not found; its formats get no Glide thumbnails")
  endif()
endforeach()
file(WRITE "${CMAKE_CURRENT_BINARY_DIR}/GlideCodecPins.h.tmp"
  "// Generated by CMakeLists.txt: SHA-256 of the codec libraries this provider may load.\n"
  "#pragma once\n"
  "#define GLIDE_PIN_GLIDECODECS \"${GLIDE_PIN_GLIDECODECS}\"\n"
  "#define GLIDE_PIN_GLIDEMANAGED \"${GLIDE_PIN_GLIDEMANAGED}\"\n")
configure_file("${CMAKE_CURRENT_BINARY_DIR}/GlideCodecPins.h.tmp" "${CMAKE_CURRENT_BINARY_DIR}/GlideCodecPins.h" COPYONLY)

add_library(Glide.ShellThumbnail SHARED
    GlideThumbnailProvider.cpp
    ThumbnailCore.cpp
    CodecProviders.cpp
    Glide.ShellThumbnail.def
    "${CMAKE_CURRENT_BINARY_DIR}/Glide.ShellThumbnail.rc"
)

target_compile_features(Glide.ShellThumbnail PRIVATE cxx_std_20)
target_compile_definitions(Glide.ShellThumbnail PRIVATE UNICODE _UNICODE WIN32_LEAN_AND_MEAN NOMINMAX)
# libwebp headers are consumed as <webp/decode.h> from the vendored src/ tree; GlideCodecPins.h is generated.
target_include_directories(Glide.ShellThumbnail PRIVATE
    "${CMAKE_CURRENT_SOURCE_DIR}/../third_party/libwebp/src"
    "${CMAKE_CURRENT_BINARY_DIR}")
target_link_libraries(Glide.ShellThumbnail PRIVATE
    webpdecoder
    windowscodecs
    d2d1
    d3d11
    dxgi
    shlwapi
    ole32
    oleaut32
    propsys
    uuid
    bcrypt
)

set_target_properties(Glide.ShellThumbnail PROPERTIES OUTPUT_NAME "Glide.ShellThumbnail")

# SDL checks for Glide's own sources (libwebp is third-party C and keeps its upstream warning set), plus
# CFG, CET shadow-stack compatibility, ASLR (high entropy) and DEP at link time.
if(MSVC)
  target_compile_options(Glide.ShellThumbnail PRIVATE /sdl)
  target_link_options(Glide.ShellThumbnail PRIVATE /guard:cf /CETCOMPAT /DYNAMICBASE /HIGHENTROPYVA /NXCOMPAT)
endif()
