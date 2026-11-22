#define WIN32_LEAN_AND_MEAN
#include <Windows.h>
#include <cstdio>
#include <string>

#include "engine/Orchestrator.h"
#include "engine/ProfileManager.h"
#include "engine/Snapshot.h"

static const char* kDefaultConfigPath = "../config/peakmode.json";

// ── selftest: no system changes, no admin needed (runs in CI) ───────────────
static int g_failures = 0;
#define CHECK(cond) do { if (!(cond)) { \
    printf("  FAIL %s:%d  %s\n", __FILE__, __LINE__, #cond); ++g_failures; } } while (0)

static void testProfileParsing() {
    using PeakMode::ProfileManager;
    printf("[selftest] profile parsing\n");

    // Empty object -> defaults
    auto r = ProfileManager::parse("{}");
    CHECK(r.ok);
    CHECK(r.profile.windowsTweaks && r.profile.disableNagle);
    CHECK(r.profile.targetProcess.empty());

    // Flags and strings are read
    r = ProfileManager::parse(R"({
        "name": "csgo", "nvidiaTweaks": false,
        "windows": { "targetProcess": "cs2.exe", "disableSysMain": false },
        "nvidia":  { "threadedOptimization": false },
        "interrupt": { "redirectNicIrq": false, "nicInstanceId": "PCI\\X" }
    })");
    CHECK(r.ok);
    CHECK(r.profile.name == "csgo");
    CHECK(!r.profile.nvidiaTweaks);
    CHECK(r.profile.targetProcess == "cs2.exe");
    CHECK(!r.profile.disableSysMain && r.profile.disableSearchIndex);
    CHECK(!r.profile.threadedOptimization);
    CHECK(!r.profile.redirectNicIrq && r.profile.disableNicInterruptMod);
    CHECK(r.profile.nicInstanceId == "PCI\\X");
    CHECK(r.warnings.empty());

    // Unknown + deprecated keys warn, do not fail
    r = ProfileManager::parse(R"({ "bogus": 1, "nvidia": { "enableLowLatency": true } })");
    CHECK(r.ok);
    CHECK(r.warnings.size() == 2);

    // Type errors and invalid JSON fail
    CHECK(!ProfileManager::parse(R"({ "windows": { "disableNagle": "yes" } })").ok);
    CHECK(!ProfileManager::parse(R"({ "windows": [] })").ok);
    CHECK(!ProfileManager::parse("{ not json").ok);
    CHECK(!ProfileManager::parse("[]").ok);

    // Comments allowed
    CHECK(ProfileManager::parse("{ // c\n \"windowsTweaks\": false }").ok);

    // serialize -> parse round trip
    PeakMode::Profile p;
    p.targetProcess = "game.exe";
    p.disableNagle  = false;
    r = ProfileManager::parse(ProfileManager::serialize(p));
    CHECK(r.ok && r.warnings.empty());
    CHECK(r.profile.targetProcess == "game.exe" && !r.profile.disableNagle);
}

static void testShippedConfig(const char* path) {
    printf("[selftest] shipped config: %s\n", path);
    auto r = PeakMode::ProfileManager::load(path);
    if (!r.ok) printf("  error: %s\n", r.error.c_str());
    CHECK(r.ok);
    for (auto& w : r.warnings) printf("  warning: %s\n", w.c_str());
    CHECK(r.warnings.empty());
}

static int runSelftest(const char* configPath) {
    testProfileParsing();
    testShippedConfig(configPath);
    printf(g_failures ? "[selftest] %d FAILURE(S)\n" : "[selftest] all passed\n", g_failures);
    return g_failures ? 1 : 0;
}

static bool isElevated() {
    HANDLE token = nullptr;
    if (!OpenProcessToken(GetCurrentProcess(), TOKEN_QUERY, &token)) return false;
    TOKEN_ELEVATION elev{};
    DWORD sz = sizeof(elev);
    GetTokenInformation(token, TokenElevation, &elev, sz, &sz);
    CloseHandle(token);
    return elev.TokenIsElevated != 0;
}

static void usage() {
    printf("Usage: peakmode_test <command> [config.json]\n"
           "  on        activate, wait for Enter, then restore\n"
           "  off       restore (this process only)\n"
           "  selftest  run checks that make no system changes\n");
}

int main(int argc, char* argv[]) {
    SetConsoleOutputCP(CP_UTF8);

    std::string cmd        = (argc > 1) ? argv[1] : "";
    const char* configPath = (argc > 2) ? argv[2] : kDefaultConfigPath;

    if (cmd == "selftest") return runSelftest(configPath);
    if (cmd != "on" && cmd != "off") { usage(); return 1; }

    // Must run as admin for power plan, services, registry writes
    if (!isElevated()) {
        fprintf(stderr,
            "[!] PeakMode needs Administrator privileges.\n"
            "    Right-click terminal -> 'Run as administrator'\n");
        return 1;
    }

    auto& orch = PeakMode::Orchestrator::instance();
    if (cmd == "on") {
        printf("=== PeakMode: ACTIVATING ===\n");
        orch.activate(configPath);
        if (orch.state() != PeakMode::ProfileState::ACTIVE) return 1;
        printf("\nPress Enter to restore and exit...\n");
        getchar();
        orch.restore();
    } else {
        printf("=== PeakMode: RESTORING ===\n");
        orch.restore();
    }
    return 0;
}
