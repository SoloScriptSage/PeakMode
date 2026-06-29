#pragma once
#include <string>

namespace PeakMode {

enum class ProfileState { IDLE, ACTIVE };

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
