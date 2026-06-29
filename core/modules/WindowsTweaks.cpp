#define WIN32_LEAN_AND_MEAN
#include <Windows.h>
#include <winternl.h>   // NTSTATUS, NtSetTimerResolution forward decls
#include <powrprof.h>
#include <timeapi.h>
#include <winsvc.h>
#include <winreg.h>
#include <winsock2.h>
#include <iphlpapi.h>
#include <cstdio>

#include "WindowsTweaks.h"
#include "../engine/Snapshot.h"

#pragma comment(lib, "powrprof.lib")
#pragma comment(lib, "winmm.lib")
#pragma comment(lib, "ws2_32.lib")
#pragma comment(lib, "iphlpapi.lib")
#pragma comment(lib, "ntdll.lib")

// ── NtSetTimerResolution (undocumented, stable since XP) ─────────────────────
// winternl.h defines NTSTATUS but not these functions — declare manually
extern "C" {
    NTSTATUS NTAPI NtSetTimerResolution(
        ULONG  DesiredResolution,  // 100-ns units; 5000 = 0.5ms
        BOOLEAN SetResolution,
        PULONG  CurrentResolution
    );
    NTSTATUS NTAPI NtQueryTimerResolution(
        PULONG MinimumResolution,
        PULONG MaximumResolution,
        PULONG CurrentResolution
    );
}

namespace PeakMode {

// ── Module-local saved state ──────────────────────────────────────────────────
static GUID  s_prevPowerPlan   = {};
static bool  s_hadPrevPlan     = false;
static ULONG s_prevTimerRes    = 156000;
static bool  s_timerResChanged = false;

// ─────────────────────────────────────────────────────────────────────────────
// AMD Ryzen 7 4800H: 8 physical cores, 16 logical threads
// Thread layout on Zen 2: core N = logical threads N and N+8
// Pin to threads 0-7 (one per physical core) to avoid HT cache thrash
// ─────────────────────────────────────────────────────────────────────────────
DWORD_PTR WindowsTweaks::getPhysicalCoreMask() {
    SYSTEM_INFO si;
    GetSystemInfo(&si);
    DWORD physical = si.dwNumberOfProcessors / 2; // 8 on 4800H
    DWORD_PTR mask = 0;
    for (DWORD i = 0; i < physical; i++)
        mask |= (static_cast<DWORD_PTR>(1) << i);
    return mask; // 0x00FF
}

// ─────────────────────────────────────────────────────────────────────────────
// Power plan — Ultimate Performance
// On laptops the GUID exists in some Win11 builds; if not, we duplicate
// High Performance and activate that instead (same effect)
// ─────────────────────────────────────────────────────────────────────────────
static const GUID kUltimatePerfGuid = {
    0xe9a42b02, 0xd5df, 0x448d,
    { 0xaa, 0x00, 0x03, 0xf1, 0x4d, 0xde, 0x8b, 0x4c }
};
static const GUID kHighPerfGuid = {
    0x8c5e7fda, 0xe8bf, 0x4a96,
    { 0x9a, 0x85, 0xa6, 0xe2, 0x3a, 0x8c, 0x63, 0x5c }
};

bool WindowsTweaks::enableUltimatePerf() {
    // Try the well-known GUID first
    if (PowerSetActiveScheme(nullptr, &kUltimatePerfGuid) == ERROR_SUCCESS)
        return true;

    // Not present — duplicate High Performance
    GUID* duped = nullptr;
    if (PowerDuplicateScheme(nullptr, &kHighPerfGuid, &duped) != ERROR_SUCCESS)
        return false;

    // Rename so it shows up cleanly in control panel
    const wchar_t* name = L"Ultimate Performance (PeakMode)";
    PowerWriteFriendlyName(nullptr, duped, nullptr, nullptr,
        const_cast<UCHAR*>(reinterpret_cast<const UCHAR*>(name)),
        static_cast<DWORD>((wcslen(name) + 1) * sizeof(wchar_t)));

    bool ok = (PowerSetActiveScheme(nullptr, duped) == ERROR_SUCCESS);
    LocalFree(duped);
    return ok;
}

bool WindowsTweaks::activatePowerPlan(const GUID& planGuid) {
    return PowerSetActiveScheme(nullptr, &planGuid) == ERROR_SUCCESS;
}

// ─────────────────────────────────────────────────────────────────────────────
// Timer resolution — 0.5ms via NtSetTimerResolution
// ─────────────────────────────────────────────────────────────────────────────
void WindowsTweaks::setTimerResolution(bool highRes) {
    if (highRes) {
        ULONG min, max, cur;
        NtQueryTimerResolution(&min, &max, &cur);
        s_prevTimerRes = cur;

        ULONG actual = 0;
        NtSetTimerResolution(5000, TRUE, &actual); // 5000 × 100ns = 0.5ms
        s_timerResChanged = true;
        printf("[WindowsTweaks] Timer resolution: %.2fms -> %.2fms\n",
               cur / 10000.0, actual / 10000.0);
    } else {
        if (s_timerResChanged) {
            ULONG actual = 0;
            NtSetTimerResolution(s_prevTimerRes, TRUE, &actual);
            s_timerResChanged = false;
            printf("[WindowsTweaks] Timer resolution restored: %.2fms\n",
                   actual / 10000.0);
        }
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// Nagle's algorithm — per adapter via registry
// HKLM\SYSTEM\CurrentControlSet\Services\Tcpip\Parameters\Interfaces\{guid}
// TcpAckFrequency=1 + TCPNoDelay=1 -> Nagle off
// ─────────────────────────────────────────────────────────────────────────────
void WindowsTweaks::setNagle(bool enabled) {
    HKEY hInterfaces;
    const wchar_t* kPath =
        L"SYSTEM\\CurrentControlSet\\Services\\Tcpip\\Parameters\\Interfaces";

    if (RegOpenKeyExW(HKEY_LOCAL_MACHINE, kPath, 0,
                      KEY_READ | KEY_WRITE, &hInterfaces) != ERROR_SUCCESS) {
        printf("[WindowsTweaks] Nagle: failed to open registry key (need admin?)\n");
        return;
    }

    DWORD ackFreq = enabled ? 2 : 1;
    DWORD noDelay = enabled ? 0 : 1;

    wchar_t subkeyName[256];
    DWORD subkeyLen = 256;
    DWORD idx = 0;
    int   count = 0;

    while (RegEnumKeyExW(hInterfaces, idx++, subkeyName, &subkeyLen,
                         nullptr, nullptr, nullptr, nullptr) == ERROR_SUCCESS) {
        HKEY hIface;
        if (RegOpenKeyExW(hInterfaces, subkeyName, 0,
                          KEY_WRITE, &hIface) == ERROR_SUCCESS) {
            RegSetValueExW(hIface, L"TcpAckFrequency", 0, REG_DWORD,
                           reinterpret_cast<const BYTE*>(&ackFreq), sizeof(DWORD));
            RegSetValueExW(hIface, L"TCPNoDelay", 0, REG_DWORD,
                           reinterpret_cast<const BYTE*>(&noDelay), sizeof(DWORD));
            RegCloseKey(hIface);
            count++;
        }
        subkeyLen = 256;
    }
    RegCloseKey(hInterfaces);
    printf("[WindowsTweaks] Nagle %s across %d adapter(s)\n",
           enabled ? "enabled" : "disabled", count);
}

// ─────────────────────────────────────────────────────────────────────────────
// Services — stop/start SysMain (Superfetch) and WSearch (indexer)
// ─────────────────────────────────────────────────────────────────────────────
void WindowsTweaks::setServiceState(const wchar_t* serviceName, bool start) {
    SC_HANDLE hSCM = OpenSCManagerW(nullptr, nullptr, SC_MANAGER_CONNECT);
    if (!hSCM) return;

    SC_HANDLE hSvc = OpenServiceW(hSCM, serviceName,
                                  SERVICE_START | SERVICE_STOP |
                                  SERVICE_QUERY_STATUS);
    if (hSvc) {
        if (start) {
            StartServiceW(hSvc, 0, nullptr);
        } else {
            SERVICE_STATUS ss{};
            ControlService(hSvc, SERVICE_CONTROL_STOP, &ss);
        }
        CloseServiceHandle(hSvc);
        wprintf(L"[WindowsTweaks] Service %s: %s\n",
                serviceName, start ? L"started" : L"stopped");
    } else {
        wprintf(L"[WindowsTweaks] Service %s: could not open (need admin?)\n",
                serviceName);
    }
    CloseServiceHandle(hSCM);
}

// ─────────────────────────────────────────────────────────────────────────────
// APPLY
// ─────────────────────────────────────────────────────────────────────────────
void WindowsTweaks::apply(const Profile& p) {
    printf("[WindowsTweaks] Applying tweaks...\n");

    // Snapshot current power plan
    GUID* current = nullptr;
    if (PowerGetActiveScheme(nullptr, &current) == ERROR_SUCCESS) {
        s_prevPowerPlan = *current;
        s_hadPrevPlan   = true;
        LocalFree(current);
    }

    // 1. Process priority -> HIGH
    if (p.setProcPriorityHigh) {
        SetPriorityClass(GetCurrentProcess(), HIGH_PRIORITY_CLASS);
        printf("[WindowsTweaks] Process priority -> HIGH\n");
    }

    // 2. CPU affinity — physical cores only (Zen 2: threads 0-7)
    if (p.pinToPCores) {
        DWORD_PTR mask = getPhysicalCoreMask();
        SetProcessAffinityMask(GetCurrentProcess(), mask);
        printf("[WindowsTweaks] CPU affinity mask -> 0x%llX\n",
               static_cast<unsigned long long>(mask));
    }

    // 3. Timer resolution -> 0.5ms
    if (p.setTimerResolution)
        setTimerResolution(true);

    // 4. Power plan -> Ultimate Performance
    if (p.setUltimatePerf) {
        if (enableUltimatePerf())
            printf("[WindowsTweaks] Power plan -> Ultimate Performance\n");
        else
            printf("[WindowsTweaks] Power plan: failed (need admin?)\n");
    }

    // 5. Disable Nagle
    if (p.disableNagle)
        setNagle(false);

    // 6. Stop background services
    if (p.disableSysMain)
        setServiceState(L"SysMain", false);
    if (p.disableSearchIndex)
        setServiceState(L"WSearch", false);

    printf("[WindowsTweaks] Done.\n");
}

// ─────────────────────────────────────────────────────────────────────────────
// RESTORE
// ─────────────────────────────────────────────────────────────────────────────
void WindowsTweaks::restore() {
    printf("[WindowsTweaks] Restoring...\n");

    // Restore process priority
    SetPriorityClass(GetCurrentProcess(), NORMAL_PRIORITY_CLASS);
    printf("[WindowsTweaks] Process priority -> NORMAL\n");

    // Restore CPU affinity (all 16 threads)
    SYSTEM_INFO si;
    GetSystemInfo(&si);
    DWORD_PTR fullMask = (static_cast<DWORD_PTR>(1) << si.dwNumberOfProcessors) - 1;
    SetProcessAffinityMask(GetCurrentProcess(), fullMask);
    printf("[WindowsTweaks] CPU affinity restored (all cores)\n");

    // Restore timer resolution
    setTimerResolution(false);

    // Restore power plan
    if (s_hadPrevPlan) {
        activatePowerPlan(s_prevPowerPlan);
        s_hadPrevPlan = false;
        printf("[WindowsTweaks] Power plan restored\n");
    }

    // Re-enable Nagle
    setNagle(true);

    // Restart services
    setServiceState(L"SysMain", true);
    setServiceState(L"WSearch", true);

    printf("[WindowsTweaks] Restored.\n");
}

} // namespace PeakMode