#pragma once
#include "../engine/ProfileManager.h"

namespace PeakMode {

class WindowsTweaks {
public:
    static void apply(const Profile& profile);
    static void restore();
};

} // namespace PeakMode
