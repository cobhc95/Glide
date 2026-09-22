cmake_minimum_required(VERSION 3.20)
project(GlideShellThumbnail LANGUAGES CXX)

# Glide.ShellThumbnail — the Windows Explorer thumbnail provider.
#
# This is a COM in-proc server loaded by Explorer's isolated thumbnail host (dllhost.exe). It is
# deliberately self-contained: it never loads .NET, Avalonia, Glide.exe or any Glide UI/settings
# component. It only touches the Windows Imaging Component, Direct2D (for SVG) and the compact
# decoders that ship in ThumbnailCore.cpp.
add_library(Glide.ShellThumbnail SHARED
    GlideThumbnailProvider.cpp
    ThumbnailCore.cpp
    Glide.ShellThumbnail.def
)

target_compile_features(Glide.ShellThumbnail PRIVATE cxx_std_20)
target_compile_definitions(Glide.ShellThumbnail PRIVATE UNICODE _UNICODE WIN32_LEAN_AND_MEAN NOMINMAX)
target_link_libraries(Glide.ShellThumbnail PRIVATE
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
