#define WIN32_LEAN_AND_MEAN
#include <Windows.h>
#include <aclapi.h>
#include <sddl.h>
#include <cstdio>
#include <cstring>
#include <stdexcept>
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

// Activation steps and progress events, with fake steps and fake snapshot
// entries in a temp state dir. No module code runs, nothing is changed.
struct Ev { int event, step, status, entries; };
static bool operator==(const Ev& a, const Ev& b) {
    return a.event == b.event && a.step == b.step && a.status == b.status && a.entries == b.entries;
}
static void __cdecl collectEvent(int event, int step, int status, int entries, void* user) {
    static_cast<std::vector<Ev>*>(user)->push_back({ event, step, status, entries });
}

static void testSteps() {
    using namespace PeakMode;
    using O = Orchestrator;
    printf("[selftest] activation steps and progress\n");

    // Order and enabled flags come from the profile
    Profile p = ProfileManager::parse("{}").profile;
    auto steps = O::stepsFor(p);
    CHECK(steps.size() == STEP_COUNT);
    for (int i = 0; i < static_cast<int>(steps.size()); ++i) {
        CHECK(steps[i].id == i);
        CHECK(steps[i].enabled);
        CHECK(steps[i].appliedWithoutEntries == (i == STEP_TIMER));
    }
    p.windowsTweaks = false;
    p.redirectNicIrq = p.disableNicInterruptMod = false;
    steps = O::stepsFor(p);
    for (const auto& s : steps) CHECK(s.enabled == (s.id == STEP_NVIDIA));
    p = ProfileManager::parse(R"({ "windows": { "setProcPriorityHigh": false, "pinToPCores": false,
                                               "disableSysMain": false } })").profile;
    steps = O::stepsFor(p);
    CHECK(!steps[STEP_PROCESS].enabled && steps[STEP_SERVICES].enabled);
    CHECK(std::string(O::stepName(STEP_NAGLE)) == "nagle" && std::string(O::stepName(99)).empty());

    wchar_t tmp[MAX_PATH];
    GetTempPathW(MAX_PATH, tmp);
    std::wstring dir = std::wstring(tmp) + L"peakmode-steptest-" +
                       std::to_wstring(GetCurrentProcessId());
    CreateDirectoryW(dir.c_str(), nullptr);
    Snapshot::setStateDirForTests(dir);
    DeleteFileW(Snapshot::filePath().c_str());
    std::string err;

    // runSteps: events, status, entry tagging
    CHECK(Snapshot::begin("test", err));
    std::vector<Ev> ev;
    O::Emit emit = [&](int e, int s, int st, int n) { ev.push_back({ e, s, st, n }); };
    std::vector<O::StepDef> fake = {
        { STEP_NAGLE, true, [] {
            Snapshot::record({ { "kind", "t" }, { "id", 1 } });
            Snapshot::record({ { "kind", "t" }, { "id", 2 }, { "module", "own" } });
        } },
        { STEP_TIMER,    true,  [] {}, true },
        { STEP_POWER,    false, [] { Snapshot::record({ { "kind", "t" }, { "id", 9 } }); } },
        { STEP_SERVICES, true,  [] {} },
    };
    O::runSteps(fake, emit);
    CHECK((ev == std::vector<Ev>{
        { PM_EV_STEP_BEGIN, STEP_NAGLE, 0, 0 },    { PM_EV_STEP_END, STEP_NAGLE, PM_STEP_APPLIED, 2 },
        { PM_EV_STEP_BEGIN, STEP_TIMER, 0, 2 },    { PM_EV_STEP_END, STEP_TIMER, PM_STEP_APPLIED, 2 },
        { PM_EV_STEP_BEGIN, STEP_SERVICES, 0, 2 }, { PM_EV_STEP_END, STEP_SERVICES, PM_STEP_SKIPPED, 2 },
    }));
    CHECK(Snapshot::record({ { "kind", "t" }, { "id", 3 } }));   // after the steps: untagged
    Snapshot::Entry doc;
    CHECK(Snapshot::load(doc, err) && doc["entries"].size() == 3);
    if (doc["entries"].size() == 3) {
        CHECK(doc["entries"][0]["module"] == "nagle");
        CHECK(doc["entries"][1]["module"] == "own");             // explicit tag kept
        CHECK(!doc["entries"][2].contains("module"));
    }

    // A throwing step is reported as failed, rethrown, and its tag cleared
    ev.clear();
    bool threw = false;
    try {
        O::runSteps({ { STEP_NVIDIA, true, [] {
            Snapshot::record({ { "kind", "t" }, { "id", 4 } });
            throw std::runtime_error("boom");
        } } }, emit);
    } catch (const std::runtime_error&) { threw = true; }
    CHECK(threw);
    CHECK((ev == std::vector<Ev>{ { PM_EV_STEP_BEGIN, STEP_NVIDIA, 0, 3 },
                                  { PM_EV_STEP_END, STEP_NVIDIA, PM_STEP_FAILED, 4 } }));
    CHECK(Snapshot::record({ { "kind", "t" }, { "id", 5 } }));
    CHECK(Snapshot::load(doc, err) && !doc["entries"].back().contains("module"));

    // restoreAll reports each entry in restore order (reverse, deferred last)
    std::vector<int> seen;
    std::vector<bool> oks;
    auto rep = Snapshot::restoreAll(
        [](const Snapshot::Entry& e, std::string& er) {
            if (e["id"] == 4) { er = "no"; return false; }
            return true;
        },
        [&](const Snapshot::Entry& e, bool ok) { seen.push_back(e["id"].get<int>()); oks.push_back(ok); });
    CHECK(!rep.ok && rep.restored == 4 && rep.failed == 1);
    CHECK((seen == std::vector<int>{ 5, 4, 3, 2, 1 }));
    CHECK((oks == std::vector<bool>{ true, false, true, true, true }));
    DeleteFileW(Snapshot::filePath().c_str());

    // Entry -> step: tag first, then kind / registry path (v0.1.0 snapshots)
    CHECK(O::stepOfEntry({ { "kind", "service" }, { "module", "nvidia" } }) == STEP_NVIDIA);
    CHECK(O::stepOfEntry({ { "kind", "service" }, { "module", "bogus" } }) == -1);
    CHECK(O::stepOfEntry({ { "kind", "process" } }) == STEP_PROCESS);
    CHECK(O::stepOfEntry({ { "kind", "powerSchemeCreated" } }) == STEP_POWER);
    CHECK(O::stepOfEntry({ { "kind", "service" } }) == STEP_SERVICES);
    CHECK(O::stepOfEntry({ { "kind", "drs" } }) == STEP_NVIDIA);
    CHECK(O::stepOfEntry({ { "kind", "nicRestart" } }) == STEP_INTERRUPT);
    CHECK(O::stepOfEntry({ { "kind", "registryValue" }, { "path",
        "SYSTEM\\CurrentControlSet\\Services\\Tcpip\\Parameters\\Interfaces\\{x}" } }) == STEP_NAGLE);
    CHECK(O::stepOfEntry({ { "kind", "registryValue" }, { "path",
        "SYSTEM\\CurrentControlSet\\Control\\Class\\{4d36e972-e325-11ce-bfc1-08002be10318}\\0001" } })
          == STEP_INTERRUPT);
    CHECK(O::stepOfEntry({ { "kind", "registryKey" }, { "path",
        "SYSTEM\\CurrentControlSet\\Enum\\PCI\\X\\Device Parameters\\Interrupt Management" } })
          == STEP_INTERRUPT);
    CHECK(O::stepOfEntry({ { "kind", "t" } }) == -1);

    // C callback through the real Orchestrator::restore(). The entries have
    // an unknown kind, so no handler touches the system; they fail and stay.
    auto& orch = O::instance();
    std::vector<Ev> cev;
    Orchestrator_SetProgressCallback(&orch, &collectEvent, &cev);
    CHECK(orch.restore() == PM_ERR_NOTHING);
    CHECK((cev == std::vector<Ev>{ { PM_EV_DONE, -1, PM_ERR_NOTHING, 0 } }));
    cev.clear();
    CHECK(Snapshot::begin("test", err));
    CHECK(Snapshot::record({ { "kind", "selftest" }, { "module", "power" } }));
    CHECK(Snapshot::record({ { "kind", "selftest" }, { "module", "nagle" } }));
    CHECK(orch.restore() == PM_ERR_PARTIAL);
    CHECK((cev == std::vector<Ev>{
        { PM_EV_RESTORE_BEGIN, -1, 0, 2 },
        { PM_EV_ENTRY_RESTORED, STEP_NAGLE, PM_STEP_FAILED, 1 },
        { PM_EV_ENTRY_RESTORED, STEP_POWER, PM_STEP_FAILED, 2 },
        { PM_EV_DONE, -1, PM_ERR_PARTIAL, 0 },
    }));
    Orchestrator_SetProgressCallback(&orch, nullptr, nullptr);
    cev.clear();
    DeleteFileW(Snapshot::filePath().c_str());
    CHECK(orch.restore() == PM_ERR_NOTHING && cev.empty());     // callback removed

    Snapshot::setStateDirForTests(L"");
    std::wstring log = dir + L"\\peakmode.log";
    DeleteFileW(log.c_str());
    RemoveDirectoryW(dir.c_str());
}

static bool isElevated();

static bool setDacl(const std::wstring& path, const wchar_t* sddl) {
    PSECURITY_DESCRIPTOR sd = nullptr;
    if (!ConvertStringSecurityDescriptorToSecurityDescriptorW(sddl, SDDL_REVISION_1, &sd, nullptr))
        return false;
    BOOL present = FALSE, def = FALSE;
    PACL dacl = nullptr;
    GetSecurityDescriptorDacl(sd, &present, &dacl, &def);
    DWORD r = SetNamedSecurityInfoW(const_cast<wchar_t*>(path.c_str()), SE_FILE_OBJECT,
                                    DACL_SECURITY_INFORMATION | PROTECTED_DACL_SECURITY_INFORMATION,
                                    nullptr, nullptr, dacl, nullptr);
    LocalFree(sd);
    return r == ERROR_SUCCESS;
}

// The real state dir must end up Administrators/SYSTEM-only, both when
// freshly created and when it already exists with a wrong ACL.
static void testStateDirAcl() {
    using PeakMode::Snapshot;
    printf("[selftest] state dir ACL\n");
    if (!isElevated()) {
        printf("  skipped: needs an elevated prompt (CI runners are elevated)\n");
        return;
    }
    wchar_t tmp[MAX_PATH];
    GetTempPathW(MAX_PATH, tmp);
    std::wstring dir  = std::wstring(tmp) + L"peakmode-acltest-" +
                        std::to_wstring(GetCurrentProcessId());
    std::wstring file = dir + L"\\snapshot.json";
    CreateDirectoryW(dir.c_str(), nullptr);               // inherits %TEMP% ACL
    HANDLE h = CreateFileW(file.c_str(), GENERIC_WRITE, 0, nullptr, CREATE_ALWAYS, 0, nullptr);
    CHECK(h != INVALID_HANDLE_VALUE);
    if (h != INVALID_HANDLE_VALUE) CloseHandle(h);

    // 1. Inherited, user-writable ACL is detected and repaired
    CHECK(!Snapshot::isAdminOnly(dir, true));
    std::string err;
    CHECK(Snapshot::secureStateDir(dir, err));
    if (!err.empty()) printf("  error: %s\n", err.c_str());
    CHECK(Snapshot::isAdminOnly(dir, true));
    CHECK(Snapshot::isAdminOnly(file, false));

    // 2. One extra ACE (Users read, the previous design) is not accepted
    CHECK(setDacl(dir, L"D:P(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)(A;OICI;GRGX;;;BU)"));
    CHECK(!Snapshot::isAdminOnly(dir, true));
    // 3. A file with its own explicit loose ACL (Everyone) is repaired too
    CHECK(setDacl(file, L"D:P(A;;FA;;;SY)(A;;FA;;;BA)(A;;FA;;;WD)"));
    CHECK(!Snapshot::isAdminOnly(file, false));
    CHECK(Snapshot::secureStateDir(dir, err));
    CHECK(Snapshot::isAdminOnly(dir, true));
    CHECK(Snapshot::isAdminOnly(file, false));

    DeleteFileW(file.c_str());
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
    testSteps();
    testStateDirAcl();
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
