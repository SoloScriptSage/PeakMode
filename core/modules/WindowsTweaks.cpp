#define WIN32_LEAN_AND_MEAN
#include <Windows.h>
#include <winternl.h>
#include <powrprof.h>
#include <tlhelp32.h>
#include <winsvc.h>
#include <objbase.h>

#include <string>
#include <vector>

#include "WindowsTweaks.h"
#include "../engine/Log.h"
#include "../engine/Registry.h"
#include "../engine/Utf8.h"

#pragma comment(lib, "powrprof.lib")
#pragma comment(lib, "ntdll.lib")
#pragma comment(lib, "ole32.lib")

// Undocumented but stable since XP. Resolution in 100-ns units.
extern "C" {
    NTSTATUS NTAPI NtSetTimerResolution(ULONG DesiredResolution, BOOLEAN SetResolution,
                                        PULONG CurrentResolution);
    NTSTATUS NTAPI NtQueryTimerResolution(PULONG MinimumResolution, PULONG MaximumResolution,
                                          PULONG CurrentResolution);
}

#ifndef PROCESS_POWER_THROTTLING_IGNORE_TIMER_RESOLUTION
#define PROCESS_POWER_THROTTLING_IGNORE_TIMER_RESOLUTION 0x4
#endif

namespace PeakMode {

namespace {

bool g_timerResHeld = false;

constexpr ULONG kTargetTimerRes = 5000; // 0.5 ms

const GUID kUltimatePerfGuid = { 0xe9a42b02, 0xd5df, 0x448d,
                                 { 0xaa, 0x00, 0x03, 0xf1, 0x4d, 0xde, 0x8b, 0x4c } };
const GUID kHighPerfGuid     = { 0x8c5e7fda, 0xe8bf, 0x4a96,
                                 { 0x9a, 0x85, 0xa6, 0xe2, 0x3a, 0x8c, 0x63, 0x5c } };

std::string guidToString(const GUID& g) {
    wchar_t buf[64];
    StringFromGUID2(g, buf, 64);
    return Narrow(buf);
}

bool stringToGuid(const std::string& s, GUID& g) {
    return SUCCEEDED(CLSIDFromString(Widen(s).c_str(), &g));
}

bool activeScheme(GUID& out) {
    GUID* cur = nullptr;
    if (PowerGetActiveScheme(nullptr, &cur) != ERROR_SUCCESS) return false;
    out = *cur;
    LocalFree(cur);
    return true;
}

ULONGLONG creationTime(HANDLE h) {
    FILETIME c{}, e{}, k{}, u{};
    if (!GetProcessTimes(h, &c, &e, &k, &u)) return 0;
    return (static_cast<ULONGLONG>(c.dwHighDateTime) << 32) | c.dwLowDateTime;
}

DWORD findProcess(const std::wstring& exeName) {
    HANDLE snap = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
    if (snap == INVALID_HANDLE_VALUE) return 0;
    PROCESSENTRY32W pe{ sizeof(pe) };
    DWORD pid = 0;
    for (BOOL ok = Process32FirstW(snap, &pe); ok; ok = Process32NextW(snap, &pe)) {
        if (_wcsicmp(pe.szExeFile, exeName.c_str()) == 0) { pid = pe.th32ProcessID; break; }
    }
    CloseHandle(snap);
    return pid;
}

struct Svc {
    SC_HANDLE scm = nullptr, svc = nullptr;
    ~Svc() { if (svc) CloseServiceHandle(svc); if (scm) CloseServiceHandle(scm); }
    bool open(const wchar_t* name, DWORD access) {
        scm = OpenSCManagerW(nullptr, nullptr, SC_MANAGER_CONNECT);
        if (!scm) return false;
        svc = OpenServiceW(scm, name, access);
        return svc != nullptr;
    }
    DWORD state() {
        SERVICE_STATUS_PROCESS ss{};
        DWORD needed = 0;
        if (!QueryServiceStatusEx(svc, SC_STATUS_PROCESS_INFO,
                                  reinterpret_cast<BYTE*>(&ss), sizeof(ss), &needed))
            return 0;
        return ss.dwCurrentState;
    }
    bool waitFor(DWORD target, DWORD timeoutMs) {
        for (DWORD waited = 0; waited < timeoutMs; waited += 200) {
            if (state() == target) return true;
            Sleep(200);
        }
        return state() == target;
    }
};

const wchar_t* kTcpipInterfaces =
    L"SYSTEM\\CurrentControlSet\\Services\\Tcpip\\Parameters\\Interfaces";

} // namespace

// ─────────────────────────────────────────────────────────────────────────────
DWORD_PTR WindowsTweaks::physicalCoreMask() {
    DWORD len = 0;
    GetLogicalProcessorInformationEx(RelationProcessorCore, nullptr, &len);
    std::vector<BYTE> buf(len);
    auto* base = reinterpret_cast<SYSTEM_LOGICAL_PROCESSOR_INFORMATION_EX*>(buf.data());
    if (!GetLogicalProcessorInformationEx(RelationProcessorCore, base, &len)) return 0;

    struct Core { KAFFINITY mask; BYTE eff; };
    std::vector<Core> cores;
    BYTE maxEff = 0;
    for (DWORD off = 0; off < len;) {
        auto* info = reinterpret_cast<SYSTEM_LOGICAL_PROCESSOR_INFORMATION_EX*>(buf.data() + off);
        const auto& pr = info->Processor;
        if (pr.GroupCount >= 1 && pr.GroupMask[0].Group == 0) {
            cores.push_back({ pr.GroupMask[0].Mask, pr.EfficiencyClass });
            if (pr.EfficiencyClass > maxEff) maxEff = pr.EfficiencyClass;
        }
        off += info->Size;
    }
    DWORD_PTR mask = 0;
    for (const auto& c : cores) {
        if (c.eff != maxEff || !c.mask) continue;
        mask |= c.mask & (~c.mask + 1); // lowest set bit = first SMT thread
    }
    return mask;
}

// ── 1/2. Priority + affinity on the target game process ─────────────────────
void WindowsTweaks::applyTargetProcess(const Profile& p) {
    if (!p.setProcPriorityHigh && !p.pinToPCores) return;
    if (p.targetProcess.empty()) {
        Log("[WindowsTweaks] Priority/affinity: no windows.targetProcess set, skipped");
        return;
    }
    std::wstring exe = Widen(p.targetProcess);
    DWORD pid = findProcess(exe);
    if (!pid) {
        Log("[WindowsTweaks] Priority/affinity: %s is not running, skipped",
            p.targetProcess.c_str());
        return;
    }
    HANDLE h = OpenProcess(PROCESS_SET_INFORMATION | PROCESS_QUERY_INFORMATION, FALSE, pid);
    if (!h) {
        Log("[WindowsTweaks] Priority/affinity: cannot open %s (pid %lu, error %lu)",
            p.targetProcess.c_str(), pid, GetLastError());
        return;
    }
    DWORD_PTR procMask = 0, sysMask = 0;
    GetProcessAffinityMask(h, &procMask, &sysMask);
    DWORD prio = GetPriorityClass(h);

    Snapshot::Entry e = {
        { "kind",         "process" },
        { "exe",          p.targetProcess },
        { "pid",          pid },
        { "createTime",   creationTime(h) },
        { "prevPriority", prio },
        { "prevAffinity", static_cast<unsigned long long>(procMask) },
    };
    if (!Snapshot::record(e)) {
        Log("[WindowsTweaks] snapshot write failed; priority/affinity not changed");
        CloseHandle(h);
        return;
    }
    if (p.setProcPriorityHigh) {
        if (SetPriorityClass(h, HIGH_PRIORITY_CLASS))
            Log("[WindowsTweaks] %s priority -> HIGH", p.targetProcess.c_str());
        else
            Log("[WindowsTweaks] %s priority: failed (%lu)", p.targetProcess.c_str(), GetLastError());
    }
    if (p.pinToPCores) {
        DWORD_PTR mask = physicalCoreMask() & procMask;
        if (mask && SetProcessAffinityMask(h, mask))
            Log("[WindowsTweaks] %s affinity 0x%llX -> 0x%llX", p.targetProcess.c_str(),
                static_cast<unsigned long long>(procMask), static_cast<unsigned long long>(mask));
        else
            Log("[WindowsTweaks] %s affinity: failed (%lu)", p.targetProcess.c_str(), GetLastError());
    }
    CloseHandle(h);
}

// ── 3. Timer resolution (per-process, released on exit: not journaled) ─────
void WindowsTweaks::applyTimerResolution() {
    // Windows 11 may ignore timer requests from processes without a visible
    // window (the tray app). Opt out of that throttling.
    PROCESS_POWER_THROTTLING_STATE pt{};
    pt.Version     = PROCESS_POWER_THROTTLING_CURRENT_VERSION;
    pt.ControlMask = PROCESS_POWER_THROTTLING_IGNORE_TIMER_RESOLUTION;
    pt.StateMask   = 0;
    SetProcessInformation(GetCurrentProcess(), ProcessPowerThrottling, &pt, sizeof(pt));

    ULONG mn = 0, mx = 0, before = 0, actual = 0;
    NtQueryTimerResolution(&mn, &mx, &before);
    NtSetTimerResolution(kTargetTimerRes, TRUE, &actual);
    g_timerResHeld = true;
    Log("[WindowsTweaks] Timer resolution %.2fms -> %.2fms (held while PeakMode runs)",
        before / 10000.0, actual / 10000.0);
}

void WindowsTweaks::releaseRuntime() {
    if (!g_timerResHeld) return;
    ULONG actual = 0;
    NtSetTimerResolution(kTargetTimerRes, FALSE, &actual); // drop our request
    g_timerResHeld = false;
    Log("[WindowsTweaks] Timer resolution request released (now %.2fms)", actual / 10000.0);
}

// ── 4. Power plan ────────────────────────────────────────────────────────────
void WindowsTweaks::applyPowerPlan() {
    GUID prev{};
    if (!activeScheme(prev)) {
        Log("[WindowsTweaks] Power plan: cannot read active scheme, skipped");
        return;
    }
    if (IsEqualGUID(prev, kUltimatePerfGuid) || IsEqualGUID(prev, kHighPerfGuid)) {
        Log("[WindowsTweaks] Power plan: already high performance, skipped");
        return;
    }
    if (!Snapshot::record({ { "kind", "powerScheme" }, { "prevActive", guidToString(prev) } })) {
        Log("[WindowsTweaks] snapshot write failed; power plan not changed");
        return;
    }
    // Existing schemes first
    for (const GUID* g : { &kUltimatePerfGuid, &kHighPerfGuid }) {
        if (PowerSetActiveScheme(nullptr, g) == ERROR_SUCCESS) {
            Log("[WindowsTweaks] Power plan -> %s", guidToString(*g).c_str());
            return;
        }
    }
    // Otherwise create one from the templates (removed again on restore)
    for (const GUID* tmpl : { &kUltimatePerfGuid, &kHighPerfGuid }) {
        GUID* dup = nullptr;
        if (PowerDuplicateScheme(nullptr, tmpl, &dup) != ERROR_SUCCESS || !dup) continue;
        GUID created = *dup;
        LocalFree(dup);
        // Deferred: delete only after the previous scheme is active again.
        Snapshot::record({ { "kind", "powerSchemeCreated" }, { "guid", guidToString(created) },
                           { "deferred", true } });
        const wchar_t* name = L"PeakMode (temporary)";
        PowerWriteFriendlyName(nullptr, &created, nullptr, nullptr,
                               reinterpret_cast<UCHAR*>(const_cast<wchar_t*>(name)),
                               static_cast<DWORD>((wcslen(name) + 1) * sizeof(wchar_t)));
        if (PowerSetActiveScheme(nullptr, &created) == ERROR_SUCCESS) {
            Log("[WindowsTweaks] Power plan -> temporary copy of %s",
                guidToString(*tmpl).c_str());
            return;
        }
    }
    Log("[WindowsTweaks] Power plan: no high performance plan available on this system "
        "(common on Modern Standby laptops); left unchanged");
}

// ── 5. Nagle (per interface, journaled registry writes) ─────────────────────
void WindowsTweaks::applyNagle() {
    HKEY h;
    if (RegOpenKeyExW(HKEY_LOCAL_MACHINE, kTcpipInterfaces, 0,
                      KEY_ENUMERATE_SUB_KEYS | KEY_WOW64_64KEY, &h) != ERROR_SUCCESS) {
        Log("[WindowsTweaks] Nagle: cannot open Tcpip interfaces key (need admin?)");
        return;
    }
    std::vector<std::wstring> ifaces;
    wchar_t name[256];
    for (DWORD i = 0;; ++i) {
        DWORD len = 256;
        if (RegEnumKeyExW(h, i, name, &len, nullptr, nullptr, nullptr, nullptr) != ERROR_SUCCESS)
            break;
        ifaces.emplace_back(name);
    }
    RegCloseKey(h);

    int ok = 0;
    for (const auto& id : ifaces) {
        std::wstring path = std::wstring(kTcpipInterfaces) + L"\\" + id;
        if (Registry::SetDword(path, L"TcpAckFrequency", 1) &&
            Registry::SetDword(path, L"TCPNoDelay", 1))
            ++ok;
    }
    Log("[WindowsTweaks] Nagle disabled on %d of %zu interface(s) "
        "(applies to new connections)", ok, ifaces.size());
}

// ── 6. Services ──────────────────────────────────────────────────────────────
void WindowsTweaks::stopService(const wchar_t* name) {
    Svc s;
    if (!s.open(name, SERVICE_QUERY_STATUS | SERVICE_STOP)) {
        LogW(L"[WindowsTweaks] Service %s: cannot open (error %lu)", name, GetLastError());
        return;
    }
    if (s.state() != SERVICE_RUNNING) {
        LogW(L"[WindowsTweaks] Service %s: not running, skipped", name);
        return;
    }
    if (!Snapshot::record({ { "kind", "service" }, { "name", Narrow(name) },
                            { "wasRunning", true } })) {
        LogW(L"[WindowsTweaks] snapshot write failed; %s not stopped", name);
        return;
    }
    SERVICE_STATUS st{};
    if (!ControlService(s.svc, SERVICE_CONTROL_STOP, &st)) {
        LogW(L"[WindowsTweaks] Service %s: stop failed (error %lu)", name, GetLastError());
        return;
    }
    bool stopped = s.waitFor(SERVICE_STOPPED, 10000);
    LogW(L"[WindowsTweaks] Service %s: %s", name, stopped ? L"stopped" : L"stop pending");
}

// ─────────────────────────────────────────────────────────────────────────────
void WindowsTweaks::apply(const Profile& p) {
    Log("[WindowsTweaks] Applying...");
    applyTargetProcess(p);
    if (p.setTimerResolution) applyTimerResolution();
    if (p.setUltimatePerf)    applyPowerPlan();
    if (p.disableNagle)       applyNagle();
    if (p.disableSysMain)     stopService(L"SysMain");
    if (p.disableSearchIndex) stopService(L"WSearch");
    Log("[WindowsTweaks] Done.");
}

bool WindowsTweaks::ownsKind(const std::string& k) {
    return k == "process" || k == "powerScheme" || k == "powerSchemeCreated" || k == "service";
}

bool WindowsTweaks::restoreEntry(const Snapshot::Entry& e, std::string& error) {
    const std::string kind = e.value("kind", std::string());

    if (kind == "process") {
        DWORD pid = e.value("pid", 0u);
        HANDLE h = OpenProcess(PROCESS_SET_INFORMATION | PROCESS_QUERY_INFORMATION, FALSE, pid);
        if (!h) return true; // process has exited: its priority/affinity went with it
        bool same = creationTime(h) == e.value("createTime", 0ull);
        bool ok = true;
        if (same) {
            ok &= SetPriorityClass(h, e.value("prevPriority", static_cast<DWORD>(NORMAL_PRIORITY_CLASS))) != 0;
            DWORD_PTR aff = static_cast<DWORD_PTR>(e.value("prevAffinity", 0ull));
            if (aff) ok &= SetProcessAffinityMask(h, aff) != 0;
            Log("[WindowsTweaks] %s priority/affinity restored",
                e.value("exe", std::string()).c_str());
        }
        CloseHandle(h);
        if (!ok) error = "cannot restore priority/affinity (error " + std::to_string(GetLastError()) + ")";
        return ok;
    }

    if (kind == "powerScheme") {
        GUID prev{};
        if (!stringToGuid(e.value("prevActive", std::string()), prev)) {
            error = "bad GUID in snapshot";
            return false;
        }
        DWORD r = PowerSetActiveScheme(nullptr, &prev);
        if (r != ERROR_SUCCESS) { error = "cannot activate previous plan (" + std::to_string(r) + ")"; return false; }
        Log("[WindowsTweaks] Power plan restored -> %s", guidToString(prev).c_str());
        return true;
    }

    if (kind == "powerSchemeCreated") {
        GUID g{};
        if (!stringToGuid(e.value("guid", std::string()), g)) { error = "bad GUID"; return false; }
        GUID active{};
        if (activeScheme(active) && IsEqualGUID(active, g)) {
            error = "temporary plan is still active; not deleting";
            return false;
        }
        DWORD r = PowerDeleteScheme(nullptr, &g);
        if (r == ERROR_SUCCESS || r == ERROR_FILE_NOT_FOUND || r == ERROR_INVALID_PARAMETER) {
            Log("[WindowsTweaks] Temporary power plan removed");
            return true;
        }
        error = "cannot delete temporary plan (" + std::to_string(r) + ")";
        return false;
    }

    if (kind == "service") {
        std::wstring name = Widen(e.value("name", std::string()));
        if (name != L"SysMain" && name != L"WSearch") { error = "unexpected service"; return false; }
        if (!e.value("wasRunning", false)) return true;
        Svc s;
        if (!s.open(name.c_str(), SERVICE_QUERY_STATUS | SERVICE_START)) {
            error = "cannot open service (" + std::to_string(GetLastError()) + ")";
            return false;
        }
        if (s.state() == SERVICE_RUNNING) return true;
        if (s.state() == SERVICE_STOP_PENDING) s.waitFor(SERVICE_STOPPED, 10000);
        if (!StartServiceW(s.svc, 0, nullptr) && GetLastError() != ERROR_SERVICE_ALREADY_RUNNING) {
            error = "cannot start service (" + std::to_string(GetLastError()) + ")";
            return false;
        }
        s.waitFor(SERVICE_RUNNING, 10000);
        LogW(L"[WindowsTweaks] Service %s: started", name.c_str());
        return true;
    }

    error = "not a WindowsTweaks entry";
    return false;
}

void WindowsTweaks::dump() {
    GUID g{};
    if (activeScheme(g)) Log("power.active = %s", guidToString(g).c_str());
    ULONG mn = 0, mx = 0, cur = 0;
    NtQueryTimerResolution(&mn, &mx, &cur);
    Log("timer.current = %lu (100ns)", cur);
    Log("cpu.physicalCoreMask = 0x%llX", static_cast<unsigned long long>(physicalCoreMask()));
    for (const wchar_t* n : { L"SysMain", L"WSearch" }) {
        Svc s;
        DWORD st = s.open(n, SERVICE_QUERY_STATUS) ? s.state() : 0;
        LogW(L"service.%s = %lu", n, st);
    }
}

} // namespace PeakMode
