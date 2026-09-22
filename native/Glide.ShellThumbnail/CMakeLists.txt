cmake_minimum_required(VERSION 3.20)
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
add_subdirectory("${CMAKE_CURRENT_SOURCE_DIR}/../third_party/libwebp"
                 "${CMAKE_CURRENT_BINARY_DIR}/libwebp"
                 EXCLUDE_FROM_ALL)

add_library(Glide.ShellThumbnail SHARED
    GlideThumbnailProvider.cpp
    ThumbnailCore.cpp
    Glide.ShellThumbnail.def
)

target_compile_features(Glide.ShellThumbnail PRIVATE cxx_std_20)
target_compile_definitions(Glide.ShellThumbnail PRIVATE UNICODE _UNICODE WIN32_LEAN_AND_MEAN NOMINMAX)
# libwebp headers are consumed as <webp/decode.h> from the vendored src/ tree.
target_include_directories(Glide.ShellThumbnail PRIVATE
    "${CMAKE_CURRENT_SOURCE_DIR}/../third_party/libwebp/src")
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
)

set_target_properties(Glide.ShellThumbnail PROPERTIES OUTPUT_NAME "Glide.ShellThumbnail")
