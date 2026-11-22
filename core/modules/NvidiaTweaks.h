#pragma once
#include <string>
#include "../engine/ProfileManager.h"
#include "../engine/Snapshot.h"

namespace PeakMode {

// Changes settings in the NVIDIA driver's global (base) DRS profile, the
// same place NVIDIA Control Panel "Global Settings" writes.
// Snapshot entry kind owned here: "drs".
class NvidiaTweaks {
public:
    static void apply(const Profile& profile);
    static void releaseRuntime();          // NvAPI_Unload

    static bool ownsKind(const std::string& kind);
    static bool restoreEntry(const Snapshot::Entry& e, std::string& error);

    static void dump();                    // print current DRS values
};

} // namespace PeakMode
