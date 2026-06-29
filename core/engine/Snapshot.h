#pragma once
#define WIN32_LEAN_AND_MEAN
#include <Windows.h>

namespace PeakMode {

struct SystemSnapshot {
    // Windows
    DWORD     originalProcessPriority  = NORMAL_PRIORITY_CLASS;
    DWORD_PTR originalAffinityMask     = 0;
    GUID      originalPowerPlan        = {};
    bool      nagleWasEnabled          = true;
    ULONG     originalTimerResolution  = 156000; // in 100-ns units (15.6ms default)

    // NVIDIA
    int  originalPowerMode = 0;
    bool vSyncWasOn        = true;

    // Interrupt
    DWORD nicInterruptAffinity = 0;
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
