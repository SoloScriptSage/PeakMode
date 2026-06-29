#pragma once
#include "../engine/ProfileManager.h"

namespace PeakMode {

class InterruptTweaks {
public:
    static void apply(const Profile& profile);
    static void restore();
};

} // namespace PeakMode
