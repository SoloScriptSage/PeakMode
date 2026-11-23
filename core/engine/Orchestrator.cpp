#include "Orchestrator.h"
#include "Log.h"
#include "Registry.h"
#include "SessionLog.h"
#include "Snapshot.h"
#include "ProfileManager.h"
#include "../modules/WindowsTweaks.h"
#include "../modules/NvidiaTweaks.h"
#include "../modules/InterruptTweaks.h"

#include <cstring>
#include <iterator>

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

// ── steps and progress ──────────────────────────────────────────────────────
namespace {
const char* const kStepNames[STEP_COUNT] = {
    "process", "timer", "power", "nagle", "services", "nvidia", "interrupt",
};

bool startsWithI(const std::string& s, const char* prefix) {
    size_t n = strlen(prefix);
    return s.size() >= n && _strnicmp(s.c_str(), prefix, n) == 0;
}
} // namespace

const char* Orchestrator::stepName(int step) {
    return (step >= 0 && step < STEP_COUNT) ? kStepNames[step] : "";
}

int Orchestrator::stepOfEntry(const Snapshot::Entry& e) {
    if (e.contains("module") && e["module"].is_string()) {
        const std::string m = e["module"].get<std::string>();
        for (int i = 0; i < STEP_COUNT; ++i)
            if (m == kStepNames[i]) return i;
        return -1;
    }
    // Entries from before steps were tagged (v0.1.0 snapshots)
    const std::string kind = e.value("kind", std::string());
    if (kind == "process")                                     return STEP_PROCESS;
    if (kind == "powerScheme" || kind == "powerSchemeCreated") return STEP_POWER;
    if (kind == "service")                                     return STEP_SERVICES;
    if (kind == "drs")                                         return STEP_NVIDIA;
    if (kind == "nicRestart")                                  return STEP_INTERRUPT;
    if (kind == "registryValue" || kind == "registryKey") {
        const std::string path = e.value("path", std::string());
        if (startsWithI(path, "SYSTEM\\CurrentControlSet\\Services\\Tcpip\\")) return STEP_NAGLE;
        if (startsWithI(path, "SYSTEM\\CurrentControlSet\\Control\\Class\\") ||
            startsWithI(path, "SYSTEM\\CurrentControlSet\\Enum\\"))           return STEP_INTERRUPT;
    }
    return -1;
}

std::vector<Orchestrator::StepDef> Orchestrator::stepsFor(const Profile& p) {
    const bool win = p.windowsTweaks;
    return {
        { STEP_PROCESS,   win && (p.setProcPriorityHigh || p.pinToPCores),
          [&p] { WindowsTweaks::applyTargetProcess(p); } },
        { STEP_TIMER,     win && p.setTimerResolution,
          [] { WindowsTweaks::applyTimerResolution(); }, true },
        { STEP_POWER,     win && p.setUltimatePerf,
          [] { WindowsTweaks::applyPowerPlan(); } },
        { STEP_NAGLE,     win && p.disableNagle,
          [] { WindowsTweaks::applyNagle(); } },
        { STEP_SERVICES,  win && (p.disableSysMain || p.disableSearchIndex),
          [&p] { WindowsTweaks::applyServices(p); } },
        { STEP_NVIDIA,    p.nvidiaTweaks,
          [&p] { NvidiaTweaks::apply(p); } },
        { STEP_INTERRUPT, p.interruptTweaks && (p.redirectNicIrq || p.disableNicInterruptMod),
          [&p] { InterruptTweaks::apply(p); } },
    };
}

void Orchestrator::runSteps(const std::vector<StepDef>& steps, const Emit& emit) {
    for (const auto& s : steps) {
        if (!s.enabled) continue;
        const int before = Snapshot::entryCount();
        if (emit) emit(PM_EV_STEP_BEGIN, s.id, 0, before);
        Snapshot::setModuleTag(stepName(s.id));
        try {
            s.run();
        } catch (...) {
            Snapshot::setModuleTag("");
            Log("[Orchestrator] step %s: failed", stepName(s.id));
            if (emit) emit(PM_EV_STEP_END, s.id, PM_STEP_FAILED, Snapshot::entryCount());
            throw;
        }
        Snapshot::setModuleTag("");
        const int after = Snapshot::entryCount();
        const int status = (after > before || s.appliedWithoutEntries) ? PM_STEP_APPLIED
                                                                       : PM_STEP_SKIPPED;
        Log("[Orchestrator] step %s: %s (%d snapshot entries)", stepName(s.id),
            status == PM_STEP_APPLIED ? "applied" : "nothing to change", after - before);
        if (emit) emit(PM_EV_STEP_END, s.id, status, after);
    }
}

void Orchestrator::setProgressCallback(ProgressFn fn, void* user) {
    std::lock_guard<std::mutex> lock(m_cbMutex);
    m_progress     = fn;
    m_progressUser = user;
}

void Orchestrator::emit(int event, int step, int status, int entries) const {
    ProgressFn fn;
    void* user;
    {
        std::lock_guard<std::mutex> lock(m_cbMutex);
        fn   = m_progress;
        user = m_progressUser;
    }
    if (fn) fn(event, step, status, entries, user);
}

// ── activate / restore ──────────────────────────────────────────────────────
int Orchestrator::activate(const std::string& profilePath) {
    std::lock_guard<std::recursive_mutex> lock(m_mutex);
    int entries = 0;
    int rc = activateImpl(profilePath, entries);
    emit(PM_EV_DONE, -1, rc, entries);
    return rc;
}

int Orchestrator::restore(int reason) {
    std::lock_guard<std::recursive_mutex> lock(m_mutex);
    int restored = 0;
    int rc = restoreImpl(reason, restored);
    emit(PM_EV_DONE, -1, rc, restored);
    return rc;
}

int Orchestrator::activateImpl(const std::string& profilePath, int& entries) {
    m_lastError.clear();
    if (m_active) { entries = Snapshot::entryCount(); return PM_OK; }

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
    std::string session;
    {
        Snapshot::Entry doc;
        if (Snapshot::load(doc, err)) session = Snapshot::sessionIdOf(doc);
        if (!session.empty()) SessionLog::begin(session, profile.name);
    }
    std::string what;
    try {
        runSteps(stepsFor(profile), [this](int ev, int step, int status, int n) {
            emit(ev, step, status, n);
        });
    } catch (const std::exception& ex) {
        what = ex.what();
    } catch (...) {
        what = "unknown exception";
    }
    if (!what.empty()) {
        Log("[Orchestrator] exception during activation: %s; rolling back", what.c_str());
        int ignored = 0;
        restoreImpl(PM_REASON_ROLLBACK, ignored);
        return fail(PM_ERR_INTERNAL, "Activation failed and was rolled back: " + what);
    }
    entries = Snapshot::entryCount();
    if (!Snapshot::markActive())
        Log("[Orchestrator] warning: could not mark snapshot active");
    if (!session.empty()) {
        Snapshot::Entry doc;
        Snapshot::Entry list = Snapshot::load(doc, err) ? doc["entries"] : Snapshot::Entry::array();
        SessionLog::active(session, list, WindowsTweaks::activeSchemeGuid());
    }
    Log("[Orchestrator] Active (%d snapshot entries)", entries);
    return PM_OK;
}

const char* Orchestrator::reasonName(int reason) {
    static const char* const names[] = {
        "unknown", "tray", "window", "exit", "sessionEnd", "crashPrompt", "banner", "cli",
        "rollback",
    };
    return (reason >= 0 && reason < static_cast<int>(std::size(names))) ? names[reason]
                                                                       : names[0];
}

int Orchestrator::restoreImpl(int reason, int& restored) {
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

    Log("[Orchestrator] Restoring from snapshot (reason: %s)", reasonName(reason));
    std::string session;
    {
        Snapshot::Entry doc;
        std::string err;
        int total = 0;
        if (Snapshot::load(doc, err)) {
            total   = static_cast<int>(doc["entries"].size());
            session = Snapshot::sessionIdOf(doc);
        }
        emit(PM_EV_RESTORE_BEGIN, -1, 0, total);
    }
    int handled = 0;
    auto rep = Snapshot::restoreAll(&Orchestrator::restoreEntry,
        [this, &handled](const Snapshot::Entry& e, bool ok) {
            emit(PM_EV_ENTRY_RESTORED, stepOfEntry(e), ok ? 0 : PM_STEP_FAILED, ++handled);
        });
    m_active = false;
    restored = rep.restored;
    for (const auto& e : rep.errors) Log("[Orchestrator] restore error: %s", e.c_str());
    Log("[Orchestrator] Restore: %d restored, %d failed", rep.restored, rep.failed);
    // An unreadable snapshot has no session id: nothing to attach the end to
    if (!session.empty())
        SessionLog::end(session, reasonName(reason), rep.ok ? "ok" : "partial",
                        rep.restored, rep.failed);

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

int Orchestrator_RestoreWithReason(PeakMode::Orchestrator* inst, int reason) {
    if (!inst) return PeakMode::PM_ERR_INTERNAL;
    try { return inst->restore(reason); }
    catch (...) { return PeakMode::PM_ERR_INTERNAL; }
}

int PeakMode_ReadSessions(char* buf, int bufLen) {
    std::string s;
    try {
        s = PeakMode::SessionLog::readAll().dump(-1, ' ', false,
                                                 nlohmann::json::error_handler_t::replace);
    } catch (...) {
        s = "[]";
    }
    if (buf && bufLen > 0) {
        size_t n = (std::min)(s.size(), static_cast<size_t>(bufLen - 1));
        memcpy(buf, s.data(), n);
        buf[n] = '\0';
    }
    return static_cast<int>(s.size());
}

void Orchestrator_SetProgressCallback(PeakMode::Orchestrator* inst,
                                      PeakMode::Orchestrator::ProgressFn fn, void* user) {
    if (inst) inst->setProgressCallback(fn, user);
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
