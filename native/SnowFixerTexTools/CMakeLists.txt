#
# SnowFixerTexTools - small native DLL wrapping DirectXTex for the DirtCliffsRoots01 snow-variant
# composite SnowFixer.Core.Pipeline.DirtCliffsSnowVariantGenerator needs. Mirrors AutoBlend's own
# AutoBlendTexTools (same DirectXTex wrapper pattern) under this project's own name/DLL, since
# SnowFixer is deliberately standalone from AutoBlend rather than depending on its DLL. Landed in
# SnowFixer_dotnetlib alongside SnowFixer.Core.dll (see the copy step below) rather than bin/
# directly, since .NET's default P/Invoke DLL search path includes the calling assembly's own
# directory.
#
set(TEXTOOLS_NAME "SnowFixerTexTools")

find_package(directxtex CONFIG REQUIRED)

add_library(${TEXTOOLS_NAME} SHARED
    src/TexTools.cpp
)
target_compile_options(${TEXTOOLS_NAME} PRIVATE /utf-8)
# d3d11.lib is part of the Windows SDK (no vcpkg dependency) - used for DirectXTex's optional
# DirectCompute-based GPU compression path, with an automatic CPU fallback if no D3D11 device is
# available at runtime.
target_link_libraries(${TEXTOOLS_NAME} PRIVATE Microsoft::DirectXTex d3d11)

#
# vcomp140.dll - the one genuine dynamic dependency left after CMAKE_MSVC_RUNTIME_LIBRARY (set
# globally in the root CMakeLists.txt) statically links everything else: DirectXTex's vcpkg build
# uses OpenMP for parallelized (de)compression, and Microsoft doesn't ship a static OpenMP runtime.
# Copied from the toolset's own Redist folder (populated by vcvars64.bat, which build.bat always
# calls first) rather than assuming the target machine already has it.
#
if(NOT DEFINED ENV{VCToolsRedistDir})
    message(FATAL_ERROR "VCToolsRedistDir is not set - configure/build from a Developer Command Prompt (or via vcvars64.bat) so vcomp140.dll can be located.")
endif()
file(GLOB TEXTOOLS_OPENMP_DLL "$ENV{VCToolsRedistDir}x64/Microsoft.VC*.OpenMP/vcomp140.dll")
list(GET TEXTOOLS_OPENMP_DLL 0 TEXTOOLS_OPENMP_DLL)

add_custom_command(
    TARGET ${TEXTOOLS_NAME} POST_BUILD
    COMMAND ${CMAKE_COMMAND} -E make_directory "${CMAKE_BINARY_DIR}/bin/SnowFixer_dotnetlib"
    COMMAND ${CMAKE_COMMAND} -E copy_if_different
        "$<TARGET_FILE:${TEXTOOLS_NAME}>"
        "${TEXTOOLS_OPENMP_DLL}"
        "${CMAKE_BINARY_DIR}/bin/SnowFixer_dotnetlib/"
    COMMENT "Copying SnowFixerTexTools.dll + vcomp140.dll to build/bin/SnowFixer_dotnetlib"
)
