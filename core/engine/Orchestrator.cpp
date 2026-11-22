#include "Orchestrator.h"
#include "Snapshot.h"
#include "ProfileManager.h"
#include "../modules/WindowsTweaks.h"
#include "../modules/NvidiaTweaks.h"
#include "../modules/InterruptTweaks.h"
#include <cstdio>

namespace PeakMode {

Orchestrator& Orchestrator::instance() {
    static Orchestrator inst;
    return inst;
}

void Orchestrator::activate(const std::string& profilePath) {
    if (m_state == ProfileState::ACTIVE) return;

    auto loaded = ProfileManager::load(profilePath);
    for (const auto& w : loaded.warnings)
        printf("[Config] warning: %s\n", w.c_str());
    if (!loaded.ok) {
        printf("[Config] error: %s\n", loaded.error.c_str());
        printf("[Orchestrator] Not activating: fix the config first.\n");
        return;
    }
    const Profile& profile = loaded.profile;
    m_applied = profile;
    Snapshot::capture();

    if (profile.windowsTweaks)   WindowsTweaks::apply(profile);
    if (profile.nvidiaTweaks)    NvidiaTweaks::apply(profile);
    if (profile.interruptTweaks) InterruptTweaks::apply(profile);

    m_state = ProfileState::ACTIVE;
}

void Orchestrator::restore() {
    if (m_state == ProfileState::IDLE) return;

    if (m_applied.interruptTweaks) InterruptTweaks::restore(m_applied);
    if (m_applied.nvidiaTweaks)    NvidiaTweaks::restore();
    if (m_applied.windowsTweaks)   WindowsTweaks::restore(m_applied);

    Snapshot::clear();
    m_state = ProfileState::IDLE;
}

} // namespace PeakMode

// ── C exports ─────────────────────────────────────────────────────────────────
PeakMode::Orchestrator* Orchestrator_GetInstance() {
    return &PeakMode::Orchestrator::instance();
}

void Orchestrator_Activate(PeakMode::Orchestrator* inst, const char* profilePath) {
    if (inst) inst->activate(profilePath);
}

void Orchestrator_Restore(PeakMode::Orchestrator* inst) {
    if (inst) inst->restore();
}

int Orchestrator_GetState(PeakMode::Orchestrator* inst) {
    if (!inst) return 0;
    return static_cast<int>(inst->state());
}
