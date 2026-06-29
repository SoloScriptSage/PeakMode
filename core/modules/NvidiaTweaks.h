#pragma once
#define WIN32_LEAN_AND_MEAN
#include <Windows.h>
#include "../engine/ProfileManager.h"

namespace PeakMode {

class NvidiaTweaks {
public:
    static bool apply(const Profile& profile);
    static void restore();

private:
    static bool initNvAPI();
    static void shutdownNvAPI();

    // DRS = Driver Registry Settings (same as NVIDIA Control Panel writes)
    static bool applyDRSSettings(const Profile& p);
    static void restoreDRSSettings();

    static bool s_initialized;
};

} // namespace PeakMode