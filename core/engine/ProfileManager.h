#pragma once
#include <string>

namespace PeakMode {

struct Profile {
    std::string name           = "default";
    bool windowsTweaks         = true;
    bool nvidiaTweaks          = true;
    bool interruptTweaks       = true;

    // Windows options
    bool setProcPriorityHigh   = true;
    bool pinToPCores           = true;      // Intel hybrid only
    bool setUltimatePerf       = true;
    bool disableNagle          = true;
    bool disableSysMain        = true;
    bool disableSearchIndex    = true;
    bool setTimerResolution    = true;      // 0.5ms via timeBeginPeriod

    // NVIDIA options
    bool maxPerfMode           = true;
    bool disableVSync          = true;
    bool disablePrerenderFrames = true;
    bool enableLowLatency      = true;

    // Interrupt options
    bool redirectNicIrq        = true;
    bool disableNicInterruptMod = true;
    bool disableHpet           = false;     // requires reboot — off by default
};

class ProfileManager {
public:
    static Profile load(const std::string& jsonPath);
    static void    save(const Profile& profile, const std::string& jsonPath);
};

} // namespace PeakMode
