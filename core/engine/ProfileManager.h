#pragma once
#include <string>
#include <vector>

namespace PeakMode {

struct Profile {
    std::string name           = "default";
    bool windowsTweaks         = true;
    bool nvidiaTweaks          = true;
    bool interruptTweaks       = true;

    // Windows options
    std::string targetProcess;              // game exe name, e.g. "cs2.exe"; empty = skip priority/affinity
    bool setProcPriorityHigh   = true;      // applies to targetProcess
    bool pinToPCores           = true;      // applies to targetProcess
    bool setUltimatePerf       = true;
    bool disableNagle          = true;
    bool disableSysMain        = true;
    bool disableSearchIndex    = true;
    bool setTimerResolution    = false;     // 0.5ms via NtSetTimerResolution; opt-in: does not reach other processes (#9)

    // NVIDIA options (global DRS base profile)
    bool maxPerfMode            = true;
    bool disableVSync           = true;
    bool disablePrerenderFrames = true;     // max pre-rendered frames = 1 ("Low Latency: On")
    bool threadedOptimization   = true;

    // Interrupt options
    std::string nicInstanceId;              // optional PnP instance id; empty = auto-detect
    bool redirectNicIrq         = true;
    bool disableNicInterruptMod = true;
};

struct ProfileLoadResult {
    bool                     ok = false;
    Profile                  profile;       // defaults if !ok
    std::string              error;
    std::vector<std::string> warnings;      // unknown keys etc.
};

class ProfileManager {
public:
    // Paths are UTF-8.
    static ProfileLoadResult load(const std::string& jsonPath);
    static ProfileLoadResult parse(const std::string& jsonText);
    static bool              save(const Profile& profile, const std::string& jsonPath,
                                  std::string& error);
    static std::string       serialize(const Profile& profile);
};

} // namespace PeakMode
