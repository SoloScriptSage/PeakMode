cmake_minimum_required(VERSION 3.20)
project(PeakModeCore VERSION 0.1.0 LANGUAGES CXX)

set(CMAKE_CXX_STANDARD 20)
set(CMAKE_CXX_STANDARD_REQUIRED ON)

set(NVAPI_DIR ${CMAKE_CURRENT_SOURCE_DIR}/third_party/nvapi)

# ── Core DLL ──────────────────────────────────────────────────────────────────
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
    ntdll
    powrprof
    winmm
    ws2_32
    iphlpapi
    setupapi
    ${NVAPI_DIR}/amd64/nvapi64.lib
)

set_target_properties(PeakModeCore PROPERTIES
    WINDOWS_EXPORT_ALL_SYMBOLS ON
)

# ── Test / CLI binary ─────────────────────────────────────────────────────────
add_executable(peakmode_test main_test.cpp)

target_link_libraries(peakmode_test PRIVATE PeakModeCore)

target_include_directories(peakmode_test PRIVATE
    ${CMAKE_CURRENT_SOURCE_DIR}
)

if (MSVC)
    set_target_properties(peakmode_test PROPERTIES
        LINK_FLAGS "/MANIFESTUAC:\"level='requireAdministrator' uiAccess='false'\""
    )
endif()