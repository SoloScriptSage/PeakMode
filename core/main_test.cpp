#define WIN32_LEAN_AND_MEAN
#include <Windows.h>
#include <cstdio>
#include <cstring>
#include <string>
#include <vector>

#include "engine/Orchestrator.h"
#include "engine/ProfileManager.h"
#include "engine/Registry.h"
#include "engine/Snapshot.h"
#include "engine/Utf8.h"
#include "modules/InterruptTweaks.h"
#include "modules/NvidiaTweaks.h"
#include "modules/WindowsTweaks.h"

static const char* kDefaultConfigPath = "../config/peakmode.json";

// ═════════════════════════════════════════════════════════════════════════════
// selftest: no system changes, no admin needed (runs in CI)
// ═════════════════════════════════════════════════════════════════════════════
static int g_failures = 0;
#define CHECK(cond) do { if (!(cond)) { \
    printf("  FAIL %s:%d  %s\n", __FILE__, __LINE__, #cond); ++g_failures; } } while (0)

static void testProfileParsing() {
    using PeakMode::ProfileManager;
    printf("[selftest] profile parsing\n");

    auto r = ProfileManager::parse("{}");
    CHECK(r.ok);
    CHECK(r.profile.windowsTweaks && r.profile.disableNagle);
    CHECK(r.profile.targetProcess.empty());

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

    r = ProfileManager::parse(R"({ "bogus": 1, "nvidia": { "enableLowLatency": true } })");
    CHECK(r.ok);
    CHECK(r.warnings.size() == 2);

    CHECK(!ProfileManager::parse(R"({ "windows": { "disableNagle": "yes" } })").ok);
    CHECK(!ProfileManager::parse(R"({ "windows": [] })").ok);
    CHECK(!ProfileManager::parse("{ not json").ok);
    CHECK(!ProfileManager::parse("[]").ok);
    CHECK(ProfileManager::parse("{ // c\n \"windowsTweaks\": false }").ok);

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

static void testSnapshot() {
    using PeakMode::Snapshot;
    printf("[selftest] snapshot journal\n");

    wchar_t tmp[MAX_PATH];
    GetTempPathW(MAX_PATH, tmp);
    std::wstring dir = std::wstring(tmp) + L"peakmode-selftest-" +
                       std::to_wstring(GetCurrentProcessId());
    CreateDirectoryW(dir.c_str(), nullptr);
    Snapshot::setStateDirForTests(dir);
    DeleteFileW(Snapshot::filePath().c_str());

    std::string err;
    CHECK(!Snapshot::exists());
    CHECK(!Snapshot::record({ { "kind", "t" } }));       // not begun
    CHECK(Snapshot::begin("test", err));
    CHECK(Snapshot::exists());
    CHECK(Snapshot::record({ { "kind", "t" }, { "id", 1 } }));
    CHECK(Snapshot::record({ { "kind", "t" }, { "id", 2 }, { "deferred", true } }));
    CHECK(Snapshot::record({ { "kind", "t" }, { "id", 3 } }));
    CHECK(Snapshot::markActive());

    // Persisted to disk and readable by a "fresh process"
    Snapshot::Entry doc;
    CHECK(Snapshot::load(doc, err));
    CHECK(doc["entries"].size() == 3 && doc["state"] == "active");

    // A second activation is refused while a snapshot exists
    CHECK(!Snapshot::begin("again", err));

    // Partial failure: id 3 fails, file kept with only id 3
    std::vector<int> order;
    auto rep = Snapshot::restoreAll([&](const Snapshot::Entry& e, std::string& er) {
        order.push_back(e["id"].get<int>());
        if (e["id"] == 3) { er = "boom"; return false; }
        return true;
    });
    CHECK(!rep.ok && rep.restored == 2 && rep.failed == 1);
    CHECK((order == std::vector<int>{ 3, 1, 2 }));      // reverse, deferred last
    CHECK(Snapshot::exists());
    CHECK(Snapshot::load(doc, err) && doc["entries"].size() == 1 && doc["entries"][0]["id"] == 3);

    // Retry succeeds and removes the file
    rep = Snapshot::restoreAll([](const Snapshot::Entry&, std::string&) { return true; });
    CHECK(rep.ok && rep.restored == 1);
    CHECK(!Snapshot::exists());

    // Corrupt file is reported and kept
    {
        FILE* f = _wfopen(Snapshot::filePath().c_str(), L"wb");
        fputs("{ broken", f);
        fclose(f);
    }
    rep = Snapshot::restoreAll([](const Snapshot::Entry&, std::string&) { return true; });
    CHECK(!rep.ok && Snapshot::exists());
    DeleteFileW(Snapshot::filePath().c_str());

    // Hex round trip
    unsigned char bytes[] = { 0x00, 0x7f, 0xf0, 0xff };
    auto back = PeakMode::FromHex(PeakMode::ToHex(bytes, sizeof(bytes)));
    CHECK(back.size() == 4 && memcmp(back.data(), bytes, 4) == 0);
    CHECK(PeakMode::FromHex("zz").empty());

    Snapshot::setStateDirForTests(L"");
    std::wstring log = dir + L"\\peakmode.log";
    DeleteFileW(log.c_str());
    RemoveDirectoryW(dir.c_str());
}

static void testRegistryAllowlist() {
    using PeakMode::Registry::IsAllowedPath;
    printf("[selftest] registry allowlist\n");
    CHECK(IsAllowedPath(L"SYSTEM\\CurrentControlSet\\Services\\Tcpip\\Parameters\\Interfaces\\{abc}"));
    CHECK(IsAllowedPath(L"SYSTEM\\CurrentControlSet\\Control\\Class\\"
                        L"{4d36e972-e325-11ce-bfc1-08002be10318}\\0001"));
    CHECK(IsAllowedPath(L"SYSTEM\\CurrentControlSet\\Enum\\PCI\\VEN_1\\x\\Device Parameters\\"
                        L"Interrupt Management\\Affinity Policy"));
    CHECK(!IsAllowedPath(L"SYSTEM\\CurrentControlSet\\Services\\Tcpip\\Parameters\\Interfaces"));
    CHECK(!IsAllowedPath(L"SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Run"));
    CHECK(!IsAllowedPath(L"SYSTEM\\CurrentControlSet\\Enum\\PCI\\VEN_1\\x\\Device Parameters"));
    CHECK(!IsAllowedPath(L"SYSTEM\\CurrentControlSet\\Control\\Class\\"
                         L"{4d36e968-e325-11ce-bfc1-08002be10318}\\0000"));
}

static int runSelftest(const char* configPath) {
    testProfileParsing();
    testShippedConfig(configPath);
    testSnapshot();
    testRegistryAllowlist();
    printf(g_failures ? "[selftest] %d FAILURE(S)\n" : "[selftest] all passed\n", g_failures);
    return g_failures ? 1 : 0;
}

// ═════════════════════════════════════════════════════════════════════════════
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
    printf("Usage: peakmode_test <command> [config.json] [--die-after N]\n"
           "  on                activate, wait for Enter, then restore\n"
           "  activate          activate and exit WITHOUT restoring (snapshot stays on disk)\n"
           "  restore           restore from the snapshot on disk\n"
           "  status            show state and the snapshot on disk\n"
           "  dump              print the current values PeakMode would touch\n"
           "  selftest          checks that make no system changes\n"
           "  --die-after N     (activate) simulate a crash after N snapshot entries\n");
}

static int report(int rc) {
    if (rc != PeakMode::PM_OK)
        printf("Result %d: %s\n", rc, PeakMode::Orchestrator::instance().lastError().c_str());
    return rc;
}

int main(int argc, char* argv[]) {
    SetConsoleOutputCP(CP_UTF8);

    std::string cmd        = (argc > 1) ? argv[1] : "";
    const char* configPath = kDefaultConfigPath;
    int dieAfter = 0;
    for (int i = 2; i < argc; ++i) {
        if (!strcmp(argv[i], "--die-after") && i + 1 < argc) dieAfter = atoi(argv[++i]);
        else configPath = argv[i];
    }

    if (cmd == "selftest") return runSelftest(configPath);
    if (cmd != "on" && cmd != "activate" && cmd != "restore" && cmd != "status" && cmd != "dump") {
        usage();
        return 1;
    }
    if (!isElevated()) {
        fprintf(stderr, "[!] PeakMode needs Administrator privileges.\n");
        return 1;
    }

    auto& orch = PeakMode::Orchestrator::instance();

    if (cmd == "status") {
        static const char* names[] = { "IDLE", "ACTIVE", "RESTORE_PENDING" };
        printf("state: %s\n", names[static_cast<int>(orch.state())]);
        printf("snapshot: %s\n", PeakMode::Narrow(PeakMode::Snapshot::filePath()).c_str());
        PeakMode::Snapshot::Entry doc;
        std::string err;
        if (PeakMode::Snapshot::load(doc, err)) printf("%s\n", doc.dump(2).c_str());
        else printf("(%s)\n", err.c_str());
        return 0;
    }
    if (cmd == "dump") {
        auto loaded = PeakMode::ProfileManager::load(configPath);
        PeakMode::WindowsTweaks::dump();
        PeakMode::NvidiaTweaks::dump();
        PeakMode::InterruptTweaks::dump(loaded.ok ? loaded.profile.nicInstanceId : "");
        return 0;
    }
    if (cmd == "restore") {
        printf("=== PeakMode: RESTORING ===\n");
        return report(orch.restore());
    }
    if (cmd == "activate") {
        printf("=== PeakMode: ACTIVATING (no auto-restore) ===\n");
        PeakMode::Snapshot::setDieAfterRecordsForTests(dieAfter);
        return report(orch.activate(configPath));
    }

    // on
    printf("=== PeakMode: ACTIVATING ===\n");
    int rc = orch.activate(configPath);
    if (rc != PeakMode::PM_OK) return report(rc);
    printf("\nPress Enter to restore and exit...\n");
    getchar();
    return report(orch.restore());
}
