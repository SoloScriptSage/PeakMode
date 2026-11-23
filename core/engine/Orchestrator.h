#pragma once
#include <functional>
#include <mutex>
#include <string>
#include <vector>
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

// Activation steps in apply order. The name of each step is stored as
// "module" in the snapshot entries it records. Keep in sync with
// ui/PeakModeTray/PeakModeCore.cs.
enum Step : int {
    STEP_PROCESS   = 0,   // priority + affinity of windows.targetProcess
    STEP_TIMER     = 1,   // 0.5 ms timer resolution (not journaled)
    STEP_POWER     = 2,
    STEP_NAGLE     = 3,
    STEP_SERVICES  = 4,   // SysMain, WSearch
    STEP_NVIDIA    = 5,
    STEP_INTERRUPT = 6,
    STEP_COUNT     = 7,
};

// Progress events: callback(event, step, status, entries, user).
//   STEP_BEGIN      step, status 0, entries = journal size before the step
//   STEP_END        step, StepStatus, entries = journal size after the step
//   RESTORE_BEGIN   step -1, status 0, entries = entries in the journal
//   ENTRY_RESTORED  step of the entry (-1 unknown), 0 ok / PM_STEP_FAILED,
//                   entries = entries handled so far (restore order)
//   DONE            step -1, status = Result of activate/restore,
//                   entries = journal size (activate) or restored (restore)
// Only steps enabled in the profile get STEP_BEGIN/STEP_END. If a step throws,
// the rollback emits RESTORE_BEGIN/ENTRY_RESTORED before the activation's DONE.
enum ProgressEvent : int {
    PM_EV_STEP_BEGIN     = 1,
    PM_EV_STEP_END       = 2,
    PM_EV_RESTORE_BEGIN  = 3,
    PM_EV_ENTRY_RESTORED = 4,
    PM_EV_DONE           = 5,
};

enum StepStatus : int {
    PM_STEP_APPLIED = 0,   // recorded at least one change (or holds the timer)
    PM_STEP_SKIPPED = 1,   // ran, nothing to change (already set, not running, no GPU...)
    PM_STEP_FAILED  = 2,   // threw; activation is rolled back
};

class Orchestrator {
public:
    using ProgressFn = void(__cdecl*)(int event, int step, int status, int entries, void* user);
    using Emit       = std::function<void(int event, int step, int status, int entries)>;

    struct StepDef {
        int                   id;
        bool                  enabled;
        std::function<void()> run;
        bool                  appliedWithoutEntries = false;   // timer
    };

    static Orchestrator& instance();

    int          activate(const std::string& utf8ProfilePath);
    int          restore();      // this session's activation OR a leftover snapshot
    ProfileState state() const;
    std::string  lastError() const;

    // Called synchronously on the thread running activate()/restore().
    void setProgressCallback(ProgressFn fn, void* user);

    // Dispatches one snapshot entry to the module that owns its kind.
    static bool restoreEntry(const Snapshot::Entry& e, std::string& error);

    // Steps for a profile, in apply order (all of them; `enabled` from flags).
    static std::vector<StepDef> stepsFor(const Profile& p);
    // Runs the enabled steps, tagging journal entries with the step name.
    // An exception from a step is reported as PM_STEP_FAILED and rethrown.
    static void        runSteps(const std::vector<StepDef>& steps, const Emit& emit);
    static const char* stepName(int step);          // "" if out of range
    static int         stepOfEntry(const Snapshot::Entry& e);   // -1 if unknown

private:
    Orchestrator() = default;
    int  fail(int code, const std::string& msg);
    int  activateImpl(const std::string& profilePath, int& entries);
    int  restoreImpl(int& restored);
    void emit(int event, int step, int status, int entries) const;

    mutable std::recursive_mutex m_mutex;
    bool        m_active = false;
    Profile     m_applied;          // flags used for the current activation
    std::string m_lastError;

    mutable std::mutex m_cbMutex;
    ProgressFn  m_progress     = nullptr;
    void*       m_progressUser = nullptr;
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
    // fn may be null to remove it. The caller keeps fn valid while set.
    __declspec(dllexport) void Orchestrator_SetProgressCallback(PeakMode::Orchestrator* inst,
                                                                PeakMode::Orchestrator::ProgressFn fn,
                                                                void* user);

    __declspec(dllexport) int  PeakMode_HasLeftoverSnapshot();
    // Copies the last error (UTF-8, NUL-terminated) into buf.
    // Returns the full length in bytes excluding the NUL.
    __declspec(dllexport) int  PeakMode_GetLastError(char* buf, int bufLen);
}
