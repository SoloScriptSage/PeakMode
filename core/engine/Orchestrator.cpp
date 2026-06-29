#include "Orchestrator.h"
#include "Snapshot.h"
#include "ProfileManager.h"
#include "../modules/WindowsTweaks.h"
#include "../modules/NvidiaTweaks.h"
#include "../modules/InterruptTweaks.h"

namespace PeakMode {

Orchestrator& Orchestrator::instance() {
    static Orchestrator inst;
    return inst;
}

void Orchestrator::activate(const std::string& profilePath) {
    if (m_state == ProfileState::ACTIVE) return;

    // 1. Load profile config
    auto profile = ProfileManager::load(profilePath);

    // 2. Snapshot current system state (so we can restore it)
    Snapshot::capture();

    // 3. Apply tweaks in order
    if (profile.windowsTweaks)   WindowsTweaks::apply(profile);
    if (profile.nvidiaTweaks)    NvidiaTweaks::apply(profile);
    if (profile.interruptTweaks) InterruptTweaks::apply(profile);

    m_state = ProfileState::ACTIVE;
}

void Orchestrator::restore() {
    if (m_state == ProfileState::IDLE) return;

    // Rollback everything — reverse order matters
    InterruptTweaks::restore();
    NvidiaTweaks::restore();
    WindowsTweaks::restore();

    Snapshot::clear();
    m_state = ProfileState::IDLE;
}

} // namespace PeakMode
