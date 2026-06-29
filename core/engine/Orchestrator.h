#pragma once
#include <string>

namespace PeakMode {

enum class ProfileState { IDLE = 0, ACTIVE = 1 };

class Orchestrator {
public:
    static Orchestrator& instance();

    void activate(const std::string& profilePath);
    void restore();
    ProfileState state() const { return m_state; }

private:
    Orchestrator() = default;
    ProfileState m_state = ProfileState::IDLE;
};

} // namespace PeakMode

// ── C exports for P/Invoke from C# tray app ───────────────────────────────────
extern "C" {
    __declspec(dllexport) PeakMode::Orchestrator* Orchestrator_GetInstance();
    __declspec(dllexport) void Orchestrator_Activate(PeakMode::Orchestrator* inst,
                                                      const char* profilePath);
    __declspec(dllexport) void Orchestrator_Restore(PeakMode::Orchestrator* inst);
    __declspec(dllexport) int  Orchestrator_GetState(PeakMode::Orchestrator* inst);
}
