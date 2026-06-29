#include "Snapshot.h"
#include <Windows.h>

namespace PeakMode {

SystemSnapshot Snapshot::s_snap  = {};
bool           Snapshot::s_captured = false;

void Snapshot::capture() {
    if (s_captured) return;

    // Timer resolution
    TIMECAPS tc;
    if (timeGetDevCaps(&tc, sizeof(tc)) == MMSYSERR_NOERROR) {
        s_snap.originalTimerResolution = tc.wPeriodMin;
    }

    // Current process priority
    s_snap.originalProcessPriority = GetPriorityClass(GetCurrentProcess());

    // CPU affinity
    DWORD_PTR procMask, sysMask;
    GetProcessAffinityMask(GetCurrentProcess(), &procMask, &sysMask);
    s_snap.originalAffinityMask = procMask;

    // TODO: capture power plan GUID via PowerGetActiveScheme()
    // TODO: capture NVAPI power state
    // TODO: capture NIC interrupt affinity via SetupAPI / registry

    s_captured = true;
}

void Snapshot::clear() {
    s_snap     = {};
    s_captured = false;
}

const SystemSnapshot& Snapshot::get() {
    return s_snap;
}

} // namespace PeakMode
