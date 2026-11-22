#pragma once
#include <string>
#include "../engine/ProfileManager.h"
#include "../engine/Snapshot.h"

namespace PeakMode {

// NIC interrupt affinity + adapter advanced properties.
// Snapshot entry kind owned here: "nicRestart" (deferred). Registry
// changes go through the journaled Registry helper.
class InterruptTweaks {
public:
    static void apply(const Profile& profile);

    static bool ownsKind(const std::string& kind);
    static bool restoreEntry(const Snapshot::Entry& e, std::string& error);

    struct Nic {
        std::wstring instanceId;   // PCI\VEN_...\...
        std::wstring driverKey;    // SYSTEM\CurrentControlSet\Control\Class\{4d36e972...}\NNNN
        std::wstring description;
    };
    // Explicit instance id if given, else the first connected physical Ethernet adapter.
    static bool findNic(const std::string& configuredInstanceId, Nic& out, std::string& error);

    static void dump(const std::string& configuredInstanceId);
};

} // namespace PeakMode
