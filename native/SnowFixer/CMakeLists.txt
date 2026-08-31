#
# Includes and Sources
#
include_directories("include")
file(GLOB_RECURSE HEADERS CONFIGURE_DEPENDS include/*.hpp)
file(GLOB_RECURSE SOURCES CONFIGURE_DEPENDS src/*.cpp)

#
# Executable
#
set(EXE_NAME "SnowFixer")
add_executable(${EXE_NAME}
    ${SOURCES}
    ${HEADERS}
    ${CMAKE_CURRENT_SOURCE_DIR}/resources/icon.rc
)
target_compile_options(${EXE_NAME} PRIVATE /utf-8)
target_include_directories(${EXE_NAME} PUBLIC include)

#
# VCPKG Dependencies
#
find_package(spdlog REQUIRED CONFIG)
find_package(nlohmann_json REQUIRED CONFIG)
find_package(cpptrace REQUIRED CONFIG)
find_package(wxWidgets REQUIRED COMPONENTS core base)

target_link_libraries(${EXE_NAME} PRIVATE
    spdlog::spdlog
    nlohmann_json::nlohmann_json
    cpptrace::cpptrace
    ${wxWidgets_LIBRARIES}
    Shlwapi
)

#
# SnowFixer.NativeExport (C#/DNNE) build
#
# The whole Mutagen/niflysharp extraction pipeline lives in SnowFixer.Core (C#) - this
# executable never touches a NIF or an ESP directly. DNNE turns SnowFixer.NativeExport's
# [UnmanagedCallersOnly] exports (Exports.cs: start_extract_run/get_mo2_profiles/get_progress/free_string) into a
# native shim (SnowFixer.NativeExportNE.lib/.dll/.h) this executable links against and
# delay-loads - the exact same pattern AutoBlend's own native/AutoBlend/CMakeLists.txt uses for its
# AutoBlend.NativeExport bridge.
#
set(NATIVEEXPORT_PROJECT_DIR "${CMAKE_CURRENT_SOURCE_DIR}/../../src/SnowFixer.NativeExport")
set(NATIVEEXPORT_CSPROJ "${NATIVEEXPORT_PROJECT_DIR}/SnowFixer.NativeExport.csproj")
set(NATIVEEXPORT_BUILD_DIR "${CMAKE_BINARY_DIR}/SnowFixer.NativeExport")
set(NATIVEEXPORT_BIN_DIR "${NATIVEEXPORT_BUILD_DIR}/bin")
set(NATIVEEXPORT_OBJ_DIR "${NATIVEEXPORT_BUILD_DIR}/obj")
set(NATIVEEXPORT_NUGET_DIR "${NATIVEEXPORT_BUILD_DIR}/.nuget/packages")

set(NATIVEEXPORT_LIB "${NATIVEEXPORT_BIN_DIR}/SnowFixer.NativeExportNE.lib")

target_link_libraries(${EXE_NAME} PRIVATE "${NATIVEEXPORT_LIB}")
target_include_directories(${EXE_NAME} PRIVATE "${NATIVEEXPORT_BIN_DIR}")

if(MSVC)
    target_link_options(${EXE_NAME} PRIVATE "/DELAYLOAD:SnowFixer.NativeExportNE.dll")
    target_link_libraries(${EXE_NAME} PRIVATE delayimp)
endif()

# NuGet restore during configure step
execute_process(
    COMMAND dotnet restore "${NATIVEEXPORT_CSPROJ}"
        --packages "${NATIVEEXPORT_NUGET_DIR}"
        -r win-x64
        /p:BaseIntermediateOutputPath=${NATIVEEXPORT_OBJ_DIR}/
        /p:BaseOutputPath=${NATIVEEXPORT_BIN_DIR}/
    WORKING_DIRECTORY "${NATIVEEXPORT_PROJECT_DIR}"
    RESULT_VARIABLE _nuget_restore_result
)

if(NOT _nuget_restore_result EQUAL 0)
    message(FATAL_ERROR "NuGet restore failed for SnowFixer.NativeExport.csproj")
endif()

if(${CMAKE_BUILD_TYPE} STREQUAL "Release" OR ${CMAKE_BUILD_TYPE} STREQUAL "RelWithDebInfo")
    set(MSBUILD_CONFIGURATION "Release")
elseif(${CMAKE_BUILD_TYPE} STREQUAL "Debug")
    set(MSBUILD_CONFIGURATION "Debug")
else()
    set(MSBUILD_CONFIGURATION "Release")
endif()

file(GLOB_RECURSE CSHARP_SOURCES CONFIGURE_DEPENDS
    "${NATIVEEXPORT_PROJECT_DIR}/*.cs"
    "${NATIVEEXPORT_PROJECT_DIR}/../SnowFixer.Core/*.cs"
)
list(APPEND CSHARP_SOURCES "${NATIVEEXPORT_CSPROJ}")

add_custom_command(
    OUTPUT ${NATIVEEXPORT_LIB}
    # OutputPath controls where DNNE emits SnowFixer.NativeExportNE.lib (used by the C++
    # linker at build time). --output (PublishDir) controls where the runtime and managed DLLs
    # land for shipping. Both point to the same directory so all artifacts end up in one place.
    #
    # Deliberately WITHOUT --no-restore (unlike AutoBlend's own equivalent step, which has this
    # same latent bug but has never hit it since its build tree has stayed warm all session):
    # confirmed directly, with --no-restore, DNNE's native-export codegen/compile step (the
    # "Generated exports written to..." / "Building native export: cl.exe ..." lines) is silently
    # skipped entirely from a cold BaseIntermediateOutputPath, regardless of the earlier
    # `dotnet restore` above already having populated it - even running the exact same --no-restore
    # publish command twice in a row never produces SnowFixer.NativeExportNE.{h,lib,dll}.
    # Dropping --no-restore (an implicit restore inside publish itself) makes it work reliably on
    # the very first invocation, every time.
    COMMAND dotnet publish "${NATIVEEXPORT_CSPROJ}"
        -c ${MSBUILD_CONFIGURATION}
        -r win-x64
        --self-contained true
        -p:Platform=x64
        -p:OutputPath=${NATIVEEXPORT_BIN_DIR}/
        -p:BaseIntermediateOutputPath=${NATIVEEXPORT_OBJ_DIR}/
        --output ${NATIVEEXPORT_BIN_DIR}/
    DEPENDS
        ${CSHARP_SOURCES}
    WORKING_DIRECTORY ${NATIVEEXPORT_PROJECT_DIR}
    COMMENT "Building SnowFixer.NativeExportNE.lib (self-contained)"
)

add_custom_target(SnowFixerNativeExportNE
    DEPENDS ${NATIVEEXPORT_LIB}
)

add_dependencies(${EXE_NAME} SnowFixerNativeExportNE)

#
# C# DLLs Copy
#
# Deliberately named SnowFixer_dotnetlib rather than the generic "dotnetlib" - AutoBlend
# ships its own AutoBlend_dotnetlib/ folder full of similar self-contained .NET runtime files, so a
# generic name would collide almost entirely when both tools are installed as MO2 mods in the same
# modlist. A unique folder name means no overlapping paths, so no MO2 conflict.
#
add_custom_target(CopySnowFixerNativeExportDLLs ALL
    COMMAND ${CMAKE_COMMAND} -E make_directory "${CMAKE_BINARY_DIR}/bin/SnowFixer_dotnetlib"
    COMMAND ${CMAKE_COMMAND} -E copy_directory "${NATIVEEXPORT_BIN_DIR}/" "${CMAKE_BINARY_DIR}/bin/SnowFixer_dotnetlib"
    COMMENT "Copying SnowFixer.NativeExport DLLs to build/bin/SnowFixer_dotnetlib"
)

add_dependencies(CopySnowFixerNativeExportDLLs SnowFixerNativeExportNE)
add_dependencies(${EXE_NAME} CopySnowFixerNativeExportDLLs)

#
# snowfixer.ini
#
# Same convention as AutoBlend's own autoblend.ini: an otherwise-unused marker file (content
# "Empty") sitting next to the exe, so a mod manager sees a recognizable per-mod ini file to attach
# to this folder instead of nothing at all.
#
add_custom_command(
    TARGET ${EXE_NAME} POST_BUILD
    COMMAND ${CMAKE_COMMAND} -E copy
        "${CMAKE_CURRENT_SOURCE_DIR}/resources/snowfixer.ini"
        "${CMAKE_BINARY_DIR}/bin/snowfixer.ini"
    COMMENT "Copying snowfixer.ini to build/bin"
)

#
# GUI translations
#
# Same convention as AutoBlend's own AutoBlend_translations/ - one JSON file per language, read by
# SFLocale at startup from a folder named after this tool specifically (same collision-avoidance
# reasoning as SnowFixer_dotnetlib above).
#
add_custom_command(
    TARGET ${EXE_NAME} POST_BUILD
    COMMAND ${CMAKE_COMMAND} -E make_directory "${CMAKE_BINARY_DIR}/bin/SnowFixer_translations"
    COMMAND ${CMAKE_COMMAND} -E copy_directory
        "${CMAKE_CURRENT_SOURCE_DIR}/resources/SnowFixer_translations"
        "${CMAKE_BINARY_DIR}/bin/SnowFixer_translations"
    COMMENT "Copying SnowFixer_translations to build/bin"
)
