#pragma once
#define WIN32_LEAN_AND_MEAN
#include <Windows.h>
#include "../engine/ProfileManager.h"

namespace PeakMode {

class WindowsTweaks {
public:
    static void apply(const Profile& profile);
    static void restore();

private:
    // Power plan helpers
    static bool enableUltimatePerf();
    static bool activatePowerPlan(const GUID& planGuid);

    // Nagle helpers
    static void setNagle(bool enabled);

    // Service helpers
    static void setServiceState(const wchar_t* serviceName, bool start);

    // Timer resolution
    static void setTimerResolution(bool highRes);

    // CPU affinity — AMD Zen 2: avoid HT siblings (threads 8-15)
    static DWORD_PTR getPhysicalCoreMask();
};

} // namespace PeakMode
