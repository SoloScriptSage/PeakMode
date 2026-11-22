#include "ProfileManager.h"
#include "Utf8.h"
#include "../third_party/nlohmann/json.hpp"

#include <cstring>
#include <filesystem>
#include <fstream>
#include <sstream>

using nlohmann::json;

namespace PeakMode {

namespace {

struct BoolField   { const char* section; const char* key; bool Profile::*member; };
struct StringField { const char* section; const char* key; std::string Profile::*member; };

// section == nullptr means top level
const BoolField kBoolFields[] = {
    { nullptr,     "windowsTweaks",          &Profile::windowsTweaks },
    { nullptr,     "nvidiaTweaks",           &Profile::nvidiaTweaks },
    { nullptr,     "interruptTweaks",        &Profile::interruptTweaks },

    { "windows",   "setProcPriorityHigh",    &Profile::setProcPriorityHigh },
    { "windows",   "pinToPCores",            &Profile::pinToPCores },
    { "windows",   "setUltimatePerf",        &Profile::setUltimatePerf },
    { "windows",   "disableNagle",           &Profile::disableNagle },
    { "windows",   "disableSysMain",         &Profile::disableSysMain },
    { "windows",   "disableSearchIndex",     &Profile::disableSearchIndex },
    { "windows",   "setTimerResolution",     &Profile::setTimerResolution },

    { "nvidia",    "maxPerfMode",            &Profile::maxPerfMode },
    { "nvidia",    "disableVSync",           &Profile::disableVSync },
    { "nvidia",    "disablePrerenderFrames", &Profile::disablePrerenderFrames },
    { "nvidia",    "threadedOptimization",   &Profile::threadedOptimization },

    { "interrupt", "redirectNicIrq",         &Profile::redirectNicIrq },
    { "interrupt", "disableNicInterruptMod", &Profile::disableNicInterruptMod },
};

const StringField kStringFields[] = {
    { nullptr,     "name",          &Profile::name },
    { "windows",   "targetProcess", &Profile::targetProcess },
    { "interrupt", "nicInstanceId", &Profile::nicInstanceId },
};

// Keys from older configs: accepted but ignored, with a hint.
struct Deprecated { const char* section; const char* key; const char* hint; };
const Deprecated kDeprecated[] = {
    { "nvidia",    "enableLowLatency", "removed; use nvidia.disablePrerenderFrames" },
    { "interrupt", "disableHpet",      "not implemented; removed" },
};

const char* kSections[] = { "windows", "nvidia", "interrupt" };

std::string qualified(const char* section, const std::string& key) {
    return section ? std::string(section) + "." + key : key;
}

bool isKnown(const char* section, const std::string& key) {
    for (auto& f : kBoolFields)
        if (((!section && !f.section) || (section && f.section && !strcmp(section, f.section)))
            && key == f.key) return true;
    for (auto& f : kStringFields)
        if (((!section && !f.section) || (section && f.section && !strcmp(section, f.section)))
            && key == f.key) return true;
    if (!section)
        for (auto s : kSections) if (key == s) return true;
    return false;
}

const char* deprecatedHint(const char* section, const std::string& key) {
    for (auto& d : kDeprecated)
        if (section && !strcmp(section, d.section) && key == d.key) return d.hint;
    return nullptr;
}

const json* findObj(const json& root, const char* section) {
    if (!section) return &root;
    auto it = root.find(section);
    if (it == root.end()) return nullptr;
    return &*it;
}

} // namespace

ProfileLoadResult ProfileManager::parse(const std::string& text) {
    ProfileLoadResult r;
    json root;
    try {
        root = json::parse(text, nullptr, true, /*ignore_comments*/ true);
    } catch (const json::parse_error& e) {
        r.error = std::string("invalid JSON: ") + e.what();
        return r;
    }
    if (!root.is_object()) {
        r.error = "top-level JSON value must be an object";
        return r;
    }
    for (auto s : kSections) {
        auto it = root.find(s);
        if (it != root.end() && !it->is_object()) {
            r.error = std::string("\"") + s + "\" must be an object";
            return r;
        }
    }

    Profile p;
    for (auto& f : kBoolFields) {
        const json* obj = findObj(root, f.section);
        if (!obj) continue;
        auto it = obj->find(f.key);
        if (it == obj->end()) continue;
        if (!it->is_boolean()) {
            r.error = qualified(f.section, f.key) + " must be true or false";
            return r;
        }
        p.*f.member = it->get<bool>();
    }
    for (auto& f : kStringFields) {
        const json* obj = findObj(root, f.section);
        if (!obj) continue;
        auto it = obj->find(f.key);
        if (it == obj->end()) continue;
        if (!it->is_string()) {
            r.error = qualified(f.section, f.key) + " must be a string";
            return r;
        }
        p.*f.member = it->get<std::string>();
    }

    // Unknown / deprecated keys -> warnings, not errors
    for (auto& [k, v] : root.items())
        if (!isKnown(nullptr, k))
            r.warnings.push_back("unknown key \"" + k + "\" ignored");
    for (auto s : kSections) {
        const json* obj = findObj(root, s);
        if (!obj) continue;
        for (auto& [k, v] : obj->items()) {
            if (isKnown(s, k)) continue;
            if (auto hint = deprecatedHint(s, k))
                r.warnings.push_back(qualified(s, k) + ": " + hint);
            else
                r.warnings.push_back("unknown key \"" + qualified(s, k) + "\" ignored");
        }
    }

    r.profile = p;
    r.ok      = true;
    return r;
}

ProfileLoadResult ProfileManager::load(const std::string& jsonPath) {
    std::ifstream in(std::filesystem::path(Widen(jsonPath)), std::ios::binary);
    if (!in) {
        ProfileLoadResult r;
        r.error = "cannot open config file: " + jsonPath;
        return r;
    }
    std::stringstream ss;
    ss << in.rdbuf();
    std::string text = ss.str();
    if (text.size() >= 3 && (unsigned char)text[0] == 0xEF &&
        (unsigned char)text[1] == 0xBB && (unsigned char)text[2] == 0xBF)
        text.erase(0, 3); // strip UTF-8 BOM (Notepad)
    return parse(text);
}

std::string ProfileManager::serialize(const Profile& p) {
    // ordered_json keeps keys in insertion order so the file stays readable
    nlohmann::ordered_json root = nlohmann::ordered_json::object();
    auto emit = [&](const char* section, nlohmann::ordered_json& obj) {
        for (auto& f : kStringFields)
            if ((!section && !f.section) || (section && f.section && !strcmp(section, f.section)))
                obj[f.key] = p.*f.member;
        for (auto& f : kBoolFields)
            if ((!section && !f.section) || (section && f.section && !strcmp(section, f.section)))
                obj[f.key] = p.*f.member;
    };
    emit(nullptr, root);
    for (auto s : kSections) {
        root[s] = nlohmann::ordered_json::object();
        emit(s, root[s]);
    }
    return root.dump(2) + "\n";
}

bool ProfileManager::save(const Profile& profile, const std::string& jsonPath,
                          std::string& error) {
    std::wstring target = Widen(jsonPath);
    std::wstring tmp    = target + L".tmp";
    {
        std::ofstream out(std::filesystem::path(tmp), std::ios::binary | std::ios::trunc);
        if (!out) { error = "cannot write " + jsonPath + ".tmp"; return false; }
        out << serialize(profile);
        out.flush();
        if (!out) { error = "write failed: " + jsonPath + ".tmp"; return false; }
    }
    if (!MoveFileExW(tmp.c_str(), target.c_str(),
                     MOVEFILE_REPLACE_EXISTING | MOVEFILE_WRITE_THROUGH)) {
        error = "cannot replace " + jsonPath + " (error " + std::to_string(GetLastError()) + ")";
        DeleteFileW(tmp.c_str());
        return false;
    }
    return true;
}

} // namespace PeakMode
