#pragma once
#define WIN32_LEAN_AND_MEAN
#include <Windows.h>
#include "../engine/ProfileManager.h"

namespace PeakMode {

class InterruptTweaks {
public:
    static void apply(const Profile& profile);
    static void restore();
};

} // namespace PeakMode