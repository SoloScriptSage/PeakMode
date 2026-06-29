#pragma once
#include <string>

namespace PeakMode {

// Captures current system state before any tweaks are applied.
// All modules write their "before" values here so Orchestrator
// can do a full rollback on restore().
struct SystemSnapshot {
    // Windows
    DWORD  originalProcessPriority  = NORMAL_PRIORITY_CLASS;
    DWORD_PTR originalAffinityMask  = 0;
    GUID   originalPowerPlan        = {};
    bool   naggleWasEnabled         = true;
    UINT   originalTimerResolution  = 156; // in 100-ns units (15.6ms default)

    // NVIDIA
    int    originalPowerMode        = 0;   // NVAPI NV_GPU_PERF_PSTATE_ID
    bool   vSyncWasOn               = true;

    // Interrupt
    DWORD  nicInterruptAffinity     = 0;
};

class Snapshot {
public:
    static void capture();
    static void clear();
    static const SystemSnapshot& get();

private:
    static SystemSnapshot s_snap;
    static bool           s_captured;
};

} // namespace PeakMode
