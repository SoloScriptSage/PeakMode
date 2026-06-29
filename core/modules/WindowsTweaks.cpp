#include "WindowsTweaks.h"
#include "../engine/Snapshot.h"
#include <Windows.h>
#include <powrprof.h>
#include <timeapi.h>
#include <winsock2.h>

// Registry key for Nagle's algorithm (per-adapter — simplified to global TCP/IP key)
static constexpr wchar_t kTcpipKey[] =
    L"SYSTEM\\CurrentControlSet\\Services\\Tcpip\\Parameters\\Interfaces";

namespace PeakMode {

void WindowsTweaks::apply(const Profile& p) {
    const auto& snap = Snapshot::get();

    // 1. Process priority → HIGH
    if (p.setProcPriorityHigh)
        SetPriorityClass(GetCurrentProcess(), HIGH_PRIORITY_CLASS);

    // 2. CPU affinity — pin to P-cores (cores 0,2,4,6 on typical 12th-gen hybrid)
    //    TODO: detect P-core mask properly via GetSystemCpuSetInformation()
    if (p.pinToPCores) {
        DWORD_PTR pCoreMask = 0x5555; // placeholder — 0,2,4,6,8,10,12,14
        SetProcessAffinityMask(GetCurrentProcess(), pCoreMask);
    }

    // 3. Power plan → Ultimate Performance
    //    GUID for Ultimate Performance (may need to be enabled first via powercfg)
    if (p.setUltimatePerf) {
        GUID ultimateGuid = {
            0xe9a42b02, 0xd5df, 0x448d,
            {0xaa, 0x00, 0x03, 0xf1, 0x4d, 0xde, 0x8b, 0x4c}
        };
        PowerSetActiveScheme(nullptr, &ultimateGuid);
    }

    // 4. Timer resolution → 0.5ms
    if (p.setTimerResolution)
        timeBeginPeriod(1); // 1ms minimum via WinAPI; 0.5ms needs NtSetTimerResolution

    // 5. Disable Nagle (TcpAckFrequency + TCPNoDelay in registry)
    if (p.disableNagle) {
        // TODO: iterate adapter subkeys under kTcpipKey and set TcpAckFrequency=1
        // Requires RegOpenKeyEx / RegSetValueEx with admin rights
    }

    // 6. Stop SysMain (Superfetch)
    if (p.disableSysMain)
        system("sc stop SysMain >nul 2>&1");

    // 7. Stop Windows Search indexer
    if (p.disableSearchIndex)
        system("sc stop WSearch >nul 2>&1");
}

void WindowsTweaks::restore() {
    const auto& snap = Snapshot::get();

    // Restore process priority
    SetPriorityClass(GetCurrentProcess(), snap.originalProcessPriority);

    // Restore CPU affinity
    if (snap.originalAffinityMask)
        SetProcessAffinityMask(GetCurrentProcess(), snap.originalAffinityMask);

    // Restore power plan
    PowerSetActiveScheme(nullptr, &snap.originalPowerPlan);

    // Restore timer resolution
    timeEndPeriod(1);

    // Re-enable Nagle
    // TODO: restore registry values from snapshot

    // Restart services
    system("sc start SysMain >nul 2>&1");
    system("sc start WSearch >nul 2>&1");
}

} // namespace PeakMode
