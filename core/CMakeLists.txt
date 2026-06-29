cmake_minimum_required(VERSION 3.20)
project(PeakModeCore VERSION 0.1.0 LANGUAGES CXX)

set(CMAKE_CXX_STANDARD 20)
set(CMAKE_CXX_STANDARD_REQUIRED ON)

# NVAPI — set NVAPI_DIR in your environment or pass via -DNVAPI_DIR=...
# Download from: https://developer.nvidia.com/rtx/path-tracing/nvapi/get-started
if(DEFINED ENV{NVAPI_DIR})
    set(NVAPI_DIR $ENV{NVAPI_DIR})
endif()

add_library(PeakModeCore SHARED
    engine/ProfileManager.cpp
    engine/Snapshot.cpp
    engine/Orchestrator.cpp
    modules/WindowsTweaks.cpp
    modules/NvidiaTweaks.cpp
    modules/InterruptTweaks.cpp
)

target_include_directories(PeakModeCore PRIVATE
    ${CMAKE_CURRENT_SOURCE_DIR}
    ${NVAPI_DIR}
)

target_link_libraries(PeakModeCore PRIVATE
    # NVAPI static lib — add when NVAPI_DIR is set
    # ${NVAPI_DIR}/amd64/nvapi64.lib

    # Windows system libs
    ntdll
    powrprof    # power plan API
    winmm       # timeBeginPeriod (timer resolution)
    ws2_32      # winsock (Nagle)
    setupapi    # device/interrupt enumeration
)

# Export a .lib for the C# UI to P/Invoke against
set_target_properties(PeakModeCore PROPERTIES
    WINDOWS_EXPORT_ALL_SYMBOLS ON
)
