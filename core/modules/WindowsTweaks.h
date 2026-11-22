#pragma once
#define WIN32_LEAN_AND_MEAN
#include <Windows.h>
#include <string>
#include "../engine/ProfileManager.h"
#include "../engine/Snapshot.h"

namespace PeakMode {

// Snapshot entry kinds owned here: "process", "powerScheme",
// "powerSchemeCreated", "service". Nagle uses journaled registry writes.
class WindowsTweaks {
public:
    static void apply(const Profile& profile);

    // Release in-process state that is not journaled (timer resolution).
    static void releaseRuntime();

    static bool ownsKind(const std::string& kind);
    static bool restoreEntry(const Snapshot::Entry& e, std::string& error);

    // First logical processor of each physical core in group 0. On hybrid
    // CPUs only the highest efficiency class (P-cores) is included.
    static DWORD_PTR physicalCoreMask();

    static void dump();   // print current state (diagnostics)

private:
    static void applyTargetProcess(const Profile& p);
    static void applyTimerResolution();
    static void applyPowerPlan();
    static void applyNagle();
    static void stopService(const wchar_t* name);
};

} // namespace PeakMode
