#pragma once
#include <mutex>
#include <string>
#include "ProfileManager.h"
#include "Snapshot.h"

namespace PeakMode {

enum class ProfileState {
    IDLE            = 0,
    ACTIVE          = 1,   // activated by this process
    RESTORE_PENDING = 2,   // snapshot on disk from a crash/reboot/failed restore
};

// Return codes of the C API (keep in sync with ui/PeakModeTray/PeakModeCore.cs)
enum Result : int {
    PM_OK                 = 0,
    PM_ERR_CONFIG         = 1,  // config missing or invalid
    PM_ERR_LEFTOVER       = 2,  // previous snapshot must be restored first
    PM_ERR_SNAPSHOT       = 3,  // cannot write snapshot: nothing was changed
    PM_ERR_PARTIAL        = 4,  // restore incomplete; snapshot kept for retry
    PM_ERR_NOTHING        = 5,  // nothing to restore
    PM_ERR_INTERNAL       = 6,
};

class Orchestrator {
public:
    static Orchestrator& instance();

    int          activate(const std::string& utf8ProfilePath);
    int          restore();      // this session's activation OR a leftover snapshot
    ProfileState state() const;
    std::string  lastError() const;

    // Dispatches one snapshot entry to the module that owns its kind.
    static bool restoreEntry(const Snapshot::Entry& e, std::string& error);

private:
    Orchestrator() = default;
    int fail(int code, const std::string& msg);

    mutable std::recursive_mutex m_mutex;
    bool        m_active = false;
    Profile     m_applied;          // flags used for the current activation
    std::string m_lastError;
};

} // namespace PeakMode

// ── C exports for P/Invoke from the C# tray app ───────────────────────────────
// Strings are UTF-8. Functions returning int return a PeakMode::Result.
extern "C" {
    __declspec(dllexport) PeakMode::Orchestrator* Orchestrator_GetInstance();
    __declspec(dllexport) int  Orchestrator_Activate(PeakMode::Orchestrator* inst,
                                                     const char* profilePathUtf8);
    __declspec(dllexport) int  Orchestrator_Restore(PeakMode::Orchestrator* inst);
    __declspec(dllexport) int  Orchestrator_GetState(PeakMode::Orchestrator* inst);

    __declspec(dllexport) int  PeakMode_HasLeftoverSnapshot();
    // Copies the last error (UTF-8, NUL-terminated) into buf.
    // Returns the full length in bytes excluding the NUL.
    __declspec(dllexport) int  PeakMode_GetLastError(char* buf, int bufLen);
}
