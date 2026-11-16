cmake_minimum_required(VERSION 3.20)
# Static CRT: Glide ships no VC++ redistributable, so a dynamic CRT (MSVCP140/VCRUNTIME140) would make
# this DLL fail to load on PCs without it.
set(CMAKE_MSVC_RUNTIME_LIBRARY "MultiThreaded$<$<CONFIG:Debug>:Debug>")
project(GlideNative LANGUAGES CXX)
add_library(Glide.Native SHARED GlideNative.cpp GlideVideo.cpp)
target_compile_features(Glide.Native PRIVATE cxx_std_20)
target_compile_definitions(Glide.Native PRIVATE UNICODE _UNICODE WIN32_LEAN_AND_MEAN NOMINMAX)
target_link_libraries(Glide.Native PRIVATE windowscodecs ole32 oleaut32 propsys shell32 mfplat mfuuid mfreadwrite d3d11 dxgi)
set_target_properties(Glide.Native PROPERTIES OUTPUT_NAME "Glide.Native")

# Security hardening (5.0): Control Flow Guard, CET shadow-stack compatibility, SDL checks and Spectre
# variant 1 mitigations. ASLR (/DYNAMICBASE, high entropy) and DEP (/NXCOMPAT) are the x64 defaults and
# are stated explicitly so a toolchain change cannot drop them. /Qspectre needs the Spectre-mitigated
# libraries for the active MSVC toolset (Visual Studio Installer > Individual components); MSBuild refuses
# the project without them (MSB8040), so it is added only when they are installed.
if(MSVC)
  set(GLIDE_SPECTRE_OPTION "")
  get_filename_component(_glide_cl_dir "${CMAKE_CXX_COMPILER}" DIRECTORY)
  if(EXISTS "${_glide_cl_dir}/../../../lib/spectre/x64")
    set(GLIDE_SPECTRE_OPTION /Qspectre)
  else()
    message(STATUS "Glide.Native: Spectre-mitigated libraries not installed for this toolset; building without /Qspectre")
  endif()
  target_compile_options(Glide.Native PRIVATE /guard:cf /sdl ${GLIDE_SPECTRE_OPTION})
  target_link_options(Glide.Native PRIVATE /guard:cf /CETCOMPAT /DYNAMICBASE /HIGHENTROPYVA /NXCOMPAT)
endif()
