#define WIN32_LEAN_AND_MEAN
#include <Windows.h>
#include <timeapi.h>
#include <powrprof.h>
#include "Snapshot.h"

#pragma comment(lib, "powrprof.lib")
#pragma comment(lib, "winmm.lib")

namespace PeakMode {

SystemSnapshot Snapshot::s_snap     = {};
bool           Snapshot::s_captured = false;

void Snapshot::capture() {
    if (s_captured) return;

    // Timer resolution
    TIMECAPS tc;
    if (timeGetDevCaps(&tc, sizeof(tc)) == MMSYSERR_NOERROR)
        s_snap.originalTimerResolution = tc.wPeriodMin * 1000; // convert ms→100ns

    // Process priority
    s_snap.originalProcessPriority = GetPriorityClass(GetCurrentProcess());

    // CPU affinity
    DWORD_PTR procMask, sysMask;
    GetProcessAffinityMask(GetCurrentProcess(), &procMask, &sysMask);
    s_snap.originalAffinityMask = procMask;

    // Power plan
    GUID* current = nullptr;
    if (PowerGetActiveScheme(nullptr, &current) == ERROR_SUCCESS) {
        s_snap.originalPowerPlan = *current;
        LocalFree(current);
    }

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
