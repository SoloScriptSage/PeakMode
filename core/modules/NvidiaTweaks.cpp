#include "NvidiaTweaks.h"
#include "../engine/Snapshot.h"

// Uncomment once NVAPI SDK is placed in core/third_party/nvapi/
// #include "nvapi.h"

namespace PeakMode {

void NvidiaTweaks::apply(const Profile& p) {
    // TODO: NvAPI_Initialize()

    // Set GPU to Maximum Performance power state
    // NvAPI_GPU_SetPerfPstate(hPhysicalGpu, NVAPI_GPU_PERF_PSTATE_P0);

    // Disable adaptive vsync / force off
    // NvAPI_DRS_SetSetting(...)

    // Set prerendered frames to 1 (lowest input latency)
    // NvAPI_DRS_SetSetting(...)

    // Enable NVIDIA Reflex / Low Latency mode
    // NvAPI_DRS_SetSetting(...)

    // Note: DRS = Driver Registry Settings — this is how NVAPI
    // writes per-profile driver settings (same as NVIDIA Control Panel)

    (void)p; // suppress unused warning until NVAPI wired in
}

void NvidiaTweaks::restore() {
    // TODO: restore NVAPI DRS settings from Snapshot
    // NvAPI_DRS_RestoreProfileDefaultSetting(...)
    // NvAPI_Unload()
}

} // namespace PeakMode
