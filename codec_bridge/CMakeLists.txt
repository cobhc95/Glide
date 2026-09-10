cmake_minimum_required(VERSION 3.21)
project(GlideCodecBridge LANGUAGES CXX)

set(CMAKE_CXX_STANDARD 17)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
set(CMAKE_WINDOWS_EXPORT_ALL_SYMBOLS OFF)

add_library(GlideCodecBridge SHARED codec_bridge.cpp)
target_compile_definitions(GlideCodecBridge PRIVATE WIN32_LEAN_AND_MEAN NOMINMAX UNICODE _UNICODE)
target_compile_options(GlideCodecBridge PRIVATE /W4 /permissive- /EHsc)

find_path(WEBP_INCLUDE_DIR webp/decode.h REQUIRED)
find_library(WEBP_LIBRARY NAMES webpdecoder webp REQUIRED)
find_path(HEIF_INCLUDE_DIR libheif/heif.h REQUIRED)
find_library(HEIF_LIBRARY NAMES heif libheif REQUIRED)
find_path(JXL_INCLUDE_DIR jxl/decode.h REQUIRED)
find_library(JXL_LIBRARY NAMES jxl REQUIRED)
find_path(OPENJPEG_INCLUDE_DIR openjpeg.h PATH_SUFFIXES openjpeg-2.6 openjpeg-2.5 openjpeg-2.4 REQUIRED)
find_library(OPENJPEG_LIBRARY NAMES openjp2 REQUIRED)
find_path(TINYEXR_INCLUDE_DIR tinyexr.h REQUIRED)
find_library(TINYEXR_LIBRARY NAMES tinyexr REQUIRED)

# libjxl's public headers may reference threads headers; vcpkg resolves its
# transitive DLL dependencies at runtime from the codecs directory.
target_include_directories(GlideCodecBridge PRIVATE
    ${WEBP_INCLUDE_DIR} ${HEIF_INCLUDE_DIR} ${JXL_INCLUDE_DIR}
    ${OPENJPEG_INCLUDE_DIR} ${TINYEXR_INCLUDE_DIR}
    ${CMAKE_CURRENT_LIST_DIR}/..)

target_link_libraries(GlideCodecBridge PRIVATE
    ${WEBP_LIBRARY} ${HEIF_LIBRARY} ${JXL_LIBRARY}
    ${OPENJPEG_LIBRARY} ${TINYEXR_LIBRARY})

set_target_properties(GlideCodecBridge PROPERTIES
    OUTPUT_NAME "GlideCodecBridge"
    RUNTIME_OUTPUT_DIRECTORY "${CMAKE_BINARY_DIR}/bin")
