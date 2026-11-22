#include "Orchestrator.h"
#include "Log.h"
#include "Registry.h"
#include "Snapshot.h"
#include "ProfileManager.h"
#include "../modules/WindowsTweaks.h"
#include "../modules/NvidiaTweaks.h"
#include "../modules/InterruptTweaks.h"

#include <cstring>

namespace PeakMode {

Orchestrator& Orchestrator::instance() {
    static Orchestrator inst;
    return inst;
}

int Orchestrator::fail(int code, const std::string& msg) {
    m_lastError = msg;
    Log("[Orchestrator] %s", msg.c_str());
    return code;
}

ProfileState Orchestrator::state() const {
    std::lock_guard<std::recursive_mutex> lock(m_mutex);
    if (m_active)           return ProfileState::ACTIVE;
    if (Snapshot::exists()) return ProfileState::RESTORE_PENDING;
    return ProfileState::IDLE;
}

std::string Orchestrator::lastError() const {
    std::lock_guard<std::recursive_mutex> lock(m_mutex);
    return m_lastError;
}

bool Orchestrator::restoreEntry(const Snapshot::Entry& e, std::string& error) {
    const std::string kind = e.value("kind", std::string());
    if (kind == "registryValue" || kind == "registryKey")
        return Registry::RestoreEntry(e, error);
    if (WindowsTweaks::ownsKind(kind))   return WindowsTweaks::restoreEntry(e, error);
    if (NvidiaTweaks::ownsKind(kind))    return NvidiaTweaks::restoreEntry(e, error);
    if (InterruptTweaks::ownsKind(kind)) return InterruptTweaks::restoreEntry(e, error);
    error = "unknown entry kind \"" + kind + "\"";
    return false;
}

int Orchestrator::activate(const std::string& profilePath) {
    std::lock_guard<std::recursive_mutex> lock(m_mutex);
    m_lastError.clear();
    if (m_active) return PM_OK;

    auto loaded = ProfileManager::load(profilePath);
    for (const auto& w : loaded.warnings)
        Log("[Config] warning: %s", w.c_str());
    if (!loaded.ok)
        return fail(PM_ERR_CONFIG, "Config error: " + loaded.error);

    if (Snapshot::exists())
        return fail(PM_ERR_LEFTOVER,
                    "A snapshot from a previous session still exists. Restore it first.");

    const Profile& profile = loaded.profile;
    std::string err;
    if (!Snapshot::begin(profile.name, err))
        return fail(PM_ERR_SNAPSHOT, "Cannot save snapshot, nothing was changed: " + err);

    Log("[Orchestrator] Activating profile \"%s\"", profile.name.c_str());
    m_applied = profile;
    m_active  = true;
    try {
        if (profile.windowsTweaks)   WindowsTweaks::apply(profile);
        if (profile.nvidiaTweaks)    NvidiaTweaks::apply(profile);
        if (profile.interruptTweaks) InterruptTweaks::apply(profile);
    } catch (const std::exception& ex) {
        Log("[Orchestrator] exception during activation: %s; rolling back", ex.what());
        restore();
        return fail(PM_ERR_INTERNAL, std::string("Activation failed and was rolled back: ")
                                     + ex.what());
    }
    if (!Snapshot::markActive())
        Log("[Orchestrator] warning: could not mark snapshot active");
    Log("[Orchestrator] Active");
    return PM_OK;
}

int Orchestrator::restore() {
    std::lock_guard<std::recursive_mutex> lock(m_mutex);
    m_lastError.clear();

    // In-process state that is not journaled (released automatically if the
    // process dies): timer resolution, NVAPI handle.
    if (m_active) {
        if (m_applied.windowsTweaks) WindowsTweaks::releaseRuntime();
        if (m_applied.nvidiaTweaks)  NvidiaTweaks::releaseRuntime();
    }

    if (!Snapshot::exists()) {
        bool wasActive = m_active;
        m_active = false;
        return wasActive ? PM_OK : fail(PM_ERR_NOTHING, "Nothing to restore.");
    }

    Log("[Orchestrator] Restoring from snapshot");
    auto rep = Snapshot::restoreAll(&Orchestrator::restoreEntry);
    m_active = false;
    for (const auto& e : rep.errors) Log("[Orchestrator] restore error: %s", e.c_str());
    Log("[Orchestrator] Restore: %d restored, %d failed", rep.restored, rep.failed);

    if (!rep.ok) {
        std::string msg = "Restore incomplete (" + std::to_string(rep.failed) +
                          " item(s) failed). The snapshot was kept; try Restore again.";
        if (!rep.errors.empty()) msg += "\n\nFirst error: " + rep.errors.front();
        return fail(PM_ERR_PARTIAL, msg);
    }
    return PM_OK;
}

} // namespace PeakMode

// ── C exports ─────────────────────────────────────────────────────────────────
PeakMode::Orchestrator* Orchestrator_GetInstance() {
    return &PeakMode::Orchestrator::instance();
}

int Orchestrator_Activate(PeakMode::Orchestrator* inst, const char* profilePath) {
    if (!inst || !profilePath) return PeakMode::PM_ERR_INTERNAL;
    try { return inst->activate(profilePath); }
    catch (...) { return PeakMode::PM_ERR_INTERNAL; }
}

int Orchestrator_Restore(PeakMode::Orchestrator* inst) {
    if (!inst) return PeakMode::PM_ERR_INTERNAL;
    try { return inst->restore(); }
    catch (...) { return PeakMode::PM_ERR_INTERNAL; }
}

int Orchestrator_GetState(PeakMode::Orchestrator* inst) {
    if (!inst) return 0;
    return static_cast<int>(inst->state());
}

int PeakMode_HasLeftoverSnapshot() {
    auto& o = PeakMode::Orchestrator::instance();
    return o.state() == PeakMode::ProfileState::RESTORE_PENDING ? 1 : 0;
}

int PeakMode_GetLastError(char* buf, int bufLen) {
    std::string e = PeakMode::Orchestrator::instance().lastError();
    if (buf && bufLen > 0) {
        size_t n = (std::min)(e.size(), static_cast<size_t>(bufLen - 1));
        memcpy(buf, e.data(), n);
        buf[n] = '\0';
    }
    return static_cast<int>(e.size());
}
