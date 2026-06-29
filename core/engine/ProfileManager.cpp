#include "ProfileManager.h"
// TODO: add nlohmann/json or similar (single-header, drop into /core/third_party/)
// #include "../third_party/json.hpp"

namespace PeakMode {

Profile ProfileManager::load(const std::string& jsonPath) {
    Profile p;
    // TODO: open jsonPath, parse fields into p
    // For now returns a fully-enabled default profile
    return p;
}

void ProfileManager::save(const Profile& profile, const std::string& jsonPath) {
    // TODO: serialize profile fields to JSON and write to jsonPath
    (void)profile;
    (void)jsonPath;
}

} // namespace PeakMode
