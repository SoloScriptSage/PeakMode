#pragma once
#include "../engine/ProfileManager.h"

namespace PeakMode {

class NvidiaTweaks {
public:
    static void apply(const Profile& profile);
    static void restore();
};

} // namespace PeakMode
