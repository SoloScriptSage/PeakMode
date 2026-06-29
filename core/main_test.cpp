#define WIN32_LEAN_AND_MEAN
#include <Windows.h>
#include <cstdio>
#include <string>

#include "engine/Orchestrator.h"
#include "engine/Snapshot.h"

static const char* kConfigPath = "../config/peakmode.json";

int main(int argc, char* argv[]) {
    SetConsoleOutputCP(CP_UTF8);

    // Must run as admin for power plan, services, registry writes
    HANDLE token = nullptr;
    OpenProcessToken(GetCurrentProcess(), TOKEN_QUERY, &token);
    TOKEN_ELEVATION elev{};
    DWORD sz = sizeof(elev);
    GetTokenInformation(token, TokenElevation, &elev, sz, &sz);
    CloseHandle(token);

    if (!elev.TokenIsElevated) {
        fprintf(stderr,
            "[!] PeakMode needs Administrator privileges.\n"
            "    Right-click terminal -> 'Run as administrator'\n");
        return 1;
    }

    auto& orch = PeakMode::Orchestrator::instance();
    std::string cmd = (argc > 1) ? argv[1] : "";

    if (cmd == "on" ||
       (cmd.empty() && orch.state() == PeakMode::ProfileState::IDLE)) {
        printf("=== PeakMode: ACTIVATING ===\n");
        orch.activate(kConfigPath);
        printf("\nPress Enter to restore and exit...\n");
        getchar();
        orch.restore();
    }
    else if (cmd == "off" ||
            (cmd.empty() && orch.state() == PeakMode::ProfileState::ACTIVE)) {
        printf("=== PeakMode: RESTORING ===\n");
        orch.restore();
    }
    else {
        printf("Usage: peakmode_test.exe [on|off]\n");
        return 1;
    }

    return 0;
}