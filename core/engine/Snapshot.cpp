#define WIN32_LEAN_AND_MEAN
#include <Windows.h>
#include <aclapi.h>
#include <sddl.h>
#include <shlobj.h>

#include <filesystem>
#include <fstream>
#include <mutex>
#include <sstream>

#include "Log.h"
#include "Snapshot.h"
#include "Utf8.h"

#pragma comment(lib, "advapi32.lib")
#pragma comment(lib, "shell32.lib")

namespace PeakMode {

using nlohmann::json;

namespace {

std::recursive_mutex g_mutex;
std::wstring         g_dir;          // cached resolved state dir
std::string          g_dirError;
bool                 g_dirResolved = false;
bool                 g_testMode    = false;
json                 g_doc;          // in-memory copy of the journal being written
bool                 g_open        = false;
int                  g_dieAfter    = 0;

// SYSTEM + Administrators: full control. Nobody else. Protected (P) so
// nothing is inherited from %ProgramData%, which lets Users create files.
constexpr wchar_t kDirSddl[]  = L"D:P(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)";
constexpr wchar_t kFileSddl[] = L"D:P(A;;FA;;;SY)(A;;FA;;;BA)";

struct SecDesc {
    PSECURITY_DESCRIPTOR sd = nullptr;
    explicit SecDesc(const wchar_t* sddl = kFileSddl) {
        ConvertStringSecurityDescriptorToSecurityDescriptorW(sddl, SDDL_REVISION_1, &sd, nullptr);
    }
    ~SecDesc() { if (sd) LocalFree(sd); }
    SECURITY_ATTRIBUTES attrs() {
        SECURITY_ATTRIBUTES sa{ sizeof(sa), sd, FALSE };
        return sa;
    }
};

bool ownerIsAdminOrSystem(const std::wstring& path) {
    PSID owner = nullptr;
    PSECURITY_DESCRIPTOR sd = nullptr;
    if (GetNamedSecurityInfoW(path.c_str(), SE_FILE_OBJECT, OWNER_SECURITY_INFORMATION,
                              &owner, nullptr, nullptr, nullptr, &sd) != ERROR_SUCCESS)
        return false;
    bool ok = IsWellKnownSid(owner, WinBuiltinAdministratorsSid) ||
              IsWellKnownSid(owner, WinLocalSystemSid);
    LocalFree(sd);
    return ok;
}

DWORD applyProtectedDacl(const std::wstring& path, const wchar_t* sddl) {
    SecDesc s(sddl);
    if (!s.sd) return GetLastError();
    BOOL present = FALSE, defaulted = FALSE;
    PACL dacl = nullptr;
    GetSecurityDescriptorDacl(s.sd, &present, &dacl, &defaulted);
    return SetNamedSecurityInfoW(const_cast<wchar_t*>(path.c_str()), SE_FILE_OBJECT,
                                 DACL_SECURITY_INFORMATION | PROTECTED_DACL_SECURITY_INFORMATION,
                                 nullptr, nullptr, dacl, nullptr);
}

std::string g_repairNote;   // logged once the state dir is usable

void resolveDir() {
    if (g_dirResolved) return;
    g_dirResolved = true;
    if (g_testMode) return; // g_dir set by setStateDirForTests

    PWSTR programData = nullptr;
    if (FAILED(SHGetKnownFolderPath(FOLDERID_ProgramData, 0, nullptr, &programData))) {
        g_dirError = "cannot locate %ProgramData%";
        return;
    }
    std::wstring dir = std::wstring(programData) + L"\\PeakMode";
    CoTaskMemFree(programData);

    SecDesc s(kDirSddl);
    SECURITY_ATTRIBUTES sa = s.attrs();
    if (!s.sd || (!CreateDirectoryW(dir.c_str(), &sa) &&
                  GetLastError() != ERROR_ALREADY_EXISTS)) {
        g_dirError = "cannot create " + Narrow(dir) + " (need Administrator?)";
        return;
    }
    // A folder pre-created by a non-admin could be used to plant a snapshot
    // that an elevated PeakMode would then "restore". Refuse it rather than
    // "repair" it: its contents cannot be trusted.
    if (!ownerIsAdminOrSystem(dir)) {
        g_dirError = Narrow(dir) + " is not owned by Administrators/SYSTEM; "
                     "delete it from an elevated prompt and start PeakMode again";
        return;
    }
    bool wasOk = Snapshot::isAdminOnly(dir, true);
    std::string err;
    if (!Snapshot::secureStateDir(dir, err)) {
        g_dirError = err;
        return;
    }
    if (!wasOk) g_repairNote = "[Snapshot] Repaired ACL of " + Narrow(dir) +
                               " (now Administrators and SYSTEM only)";
    g_dir = dir;
}

bool writeAtomic(const std::wstring& target, const std::string& content) {
    std::wstring tmp = target + L".tmp";
    SecDesc s;
    SECURITY_ATTRIBUTES sa = s.attrs();
    HANDLE h = CreateFileW(tmp.c_str(), GENERIC_WRITE, 0,
                           (!g_testMode && s.sd) ? &sa : nullptr,
                           CREATE_ALWAYS, FILE_ATTRIBUTE_NORMAL, nullptr);
    if (h == INVALID_HANDLE_VALUE) return false;
    DWORD written = 0;
    BOOL ok = WriteFile(h, content.data(), static_cast<DWORD>(content.size()), &written, nullptr)
              && written == content.size()
              && FlushFileBuffers(h);
    CloseHandle(h);
    if (!ok) { DeleteFileW(tmp.c_str()); return false; }
    if (!MoveFileExW(tmp.c_str(), target.c_str(),
                     MOVEFILE_REPLACE_EXISTING | MOVEFILE_WRITE_THROUGH)) {
        DeleteFileW(tmp.c_str());
        return false;
    }
    return true;
}

bool flush() {
    std::wstring path = Snapshot::filePath();
    return !path.empty() && writeAtomic(path, g_doc.dump(2));
}

std::string nowUtc() {
    SYSTEMTIME st;
    GetSystemTime(&st);
    char buf[32];
    snprintf(buf, sizeof(buf), "%04d-%02d-%02dT%02d:%02d:%02dZ",
             st.wYear, st.wMonth, st.wDay, st.wHour, st.wMinute, st.wSecond);
    return buf;
}

} // namespace

// ── state dir ────────────────────────────────────────────────────────────────
std::wstring Snapshot::stateDir() {
    std::lock_guard<std::recursive_mutex> lock(g_mutex);
    resolveDir();
    if (!g_repairNote.empty()) {
        std::string note;
        note.swap(g_repairNote);
        Log("%s", note.c_str());
    }
    return g_dir;
}

// ── ACL helpers (public for selftest) ───────────────────────────────────────
bool Snapshot::isAdminOnly(const std::wstring& path, bool isDirectory) {
    PACL dacl = nullptr;
    PSECURITY_DESCRIPTOR sd = nullptr;
    if (GetNamedSecurityInfoW(path.c_str(), SE_FILE_OBJECT, DACL_SECURITY_INFORMATION,
                              nullptr, nullptr, &dacl, nullptr, &sd) != ERROR_SUCCESS)
        return false;
    bool ok = false;
    SECURITY_DESCRIPTOR_CONTROL ctl = 0;
    DWORD rev = 0;
    if (dacl && GetSecurityDescriptorControl(sd, &ctl, &rev) && (ctl & SE_DACL_PROTECTED) &&
        dacl->AceCount == 2) {
        bool sawSystem = false, sawAdmins = false;
        ok = true;
        for (DWORD i = 0; i < dacl->AceCount && ok; ++i) {
            ACE_HEADER* h = nullptr;
            if (!GetAce(dacl, i, reinterpret_cast<void**>(&h)) ||
                h->AceType != ACCESS_ALLOWED_ACE_TYPE || (h->AceFlags & INHERITED_ACE)) {
                ok = false;
                break;
            }
            auto* ace = reinterpret_cast<ACCESS_ALLOWED_ACE*>(h);
            PSID sid  = &ace->SidStart;
            const BYTE inherit = OBJECT_INHERIT_ACE | CONTAINER_INHERIT_ACE;
            if ((ace->Mask & FILE_ALL_ACCESS) != FILE_ALL_ACCESS ||
                (isDirectory && (h->AceFlags & inherit) != inherit))
                ok = false;
            else if (IsWellKnownSid(sid, WinLocalSystemSid))          sawSystem = true;
            else if (IsWellKnownSid(sid, WinBuiltinAdministratorsSid)) sawAdmins = true;
            else ok = false;
        }
        ok = ok && sawSystem && sawAdmins;
    }
    LocalFree(sd);
    return ok;
}

bool Snapshot::secureStateDir(const std::wstring& dir, std::string& error) {
    DWORD r = applyProtectedDacl(dir, kDirSddl);
    if (r != ERROR_SUCCESS) {
        error = "cannot set ACL on " + Narrow(dir) + " (error " + std::to_string(r) + ")";
        return false;
    }
    // Files with their own (explicit) ACLs are not reached by the folder
    // change, so reset every file directly.
    WIN32_FIND_DATAW fd;
    HANDLE f = FindFirstFileW((dir + L"\\*").c_str(), &fd);
    if (f != INVALID_HANDLE_VALUE) {
        do {
            if (fd.dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY) continue;
            std::wstring p = dir + L"\\" + fd.cFileName;
            r = applyProtectedDacl(p, kFileSddl);
            if (r != ERROR_SUCCESS || !isAdminOnly(p, false)) {
                FindClose(f);
                error = "cannot secure " + Narrow(p) + " (error " + std::to_string(r) + ")";
                return false;
            }
        } while (FindNextFileW(f, &fd));
        FindClose(f);
    }
    if (!isAdminOnly(dir, true)) {
        error = "ACL of " + Narrow(dir) + " is still not Administrators/SYSTEM only";
        return false;
    }
    return true;
}

std::string Snapshot::stateDirError() {
    std::lock_guard<std::recursive_mutex> lock(g_mutex);
    resolveDir();
    return g_dirError;
}

std::wstring Snapshot::filePath() {
    std::wstring dir = stateDir();
    return dir.empty() ? std::wstring() : dir + L"\\snapshot.json";
}

void Snapshot::setStateDirForTests(const std::wstring& dir) {
    std::lock_guard<std::recursive_mutex> lock(g_mutex);
    g_testMode    = !dir.empty();
    g_dir         = dir;
    g_dirError.clear();
    g_dirResolved = false;
    g_doc         = json();
    g_open        = false;
}

bool Snapshot::exists() {
    std::wstring path = filePath();
    return !path.empty() && GetFileAttributesW(path.c_str()) != INVALID_FILE_ATTRIBUTES;
}

// ── writing ──────────────────────────────────────────────────────────────────
bool Snapshot::begin(const std::string& profileName, std::string& error) {
    std::lock_guard<std::recursive_mutex> lock(g_mutex);
    if (filePath().empty()) {
        error = "state folder unavailable: " + stateDirError();
        return false;
    }
    if (exists()) {
        error = "a snapshot from a previous session still exists; restore it first";
        return false;
    }
    g_doc = json{
        { "version",    1 },
        { "createdUtc", nowUtc() },
        { "profile",    profileName },
        { "pid",        GetCurrentProcessId() },
        { "state",      "applying" },
        { "entries",    json::array() },
    };
    if (!flush()) {
        error = "cannot write " + Narrow(filePath());
        g_doc  = json();
        return false;
    }
    g_open = true;
    return true;
}

void Snapshot::setDieAfterRecordsForTests(int n) { g_dieAfter = n; }

bool Snapshot::record(const Entry& entry) {
    std::lock_guard<std::recursive_mutex> lock(g_mutex);
    if (!g_open) return false;
    g_doc["entries"].push_back(entry);
    if (flush()) {
        if (g_dieAfter > 0 && static_cast<int>(g_doc["entries"].size()) >= g_dieAfter) {
            fprintf(stdout, "[test] simulated crash after %d snapshot entries\n", g_dieAfter);
            fflush(stdout);
            TerminateProcess(GetCurrentProcess(), 99);
        }
        return true;
    }
    g_doc["entries"].erase(g_doc["entries"].size() - 1);
    return false;
}

bool Snapshot::markActive() {
    std::lock_guard<std::recursive_mutex> lock(g_mutex);
    if (!g_open) return false;
    g_doc["state"] = "active";
    return flush();
}

// ── reading / restoring ──────────────────────────────────────────────────────
bool Snapshot::load(Entry& out, std::string& error) {
    std::wstring path = filePath();
    if (path.empty()) { error = "state folder unavailable: " + stateDirError(); return false; }
    if (GetFileAttributesW(path.c_str()) == INVALID_FILE_ATTRIBUTES) {
        error = "no snapshot file";
        return false;
    }
    if (!g_testMode && !ownerIsAdminOrSystem(path)) {
        error = "snapshot file is not owned by Administrators/SYSTEM; refusing to use it";
        return false;
    }
    std::ifstream in(std::filesystem::path(path), std::ios::binary);
    if (!in) { error = "no snapshot file"; return false; }
    std::stringstream ss;
    ss << in.rdbuf();
    try {
        out = json::parse(ss.str());
    } catch (const json::exception& e) {
        error = std::string("snapshot file is corrupt: ") + e.what();
        return false;
    }
    if (!out.is_object() || out.value("version", 0) != 1 ||
        !out.contains("entries") || !out["entries"].is_array()) {
        error = "snapshot file has an unknown format";
        return false;
    }
    return true;
}

Snapshot::RestoreReport Snapshot::restoreAll(const Handler& handler) {
    std::lock_guard<std::recursive_mutex> lock(g_mutex);
    RestoreReport rep;

    json doc;
    std::string err;
    if (!load(doc, err)) {
        rep.errors.push_back(err);
        rep.failed = 1;
        return rep;
    }

    const json& entries = doc["entries"];
    const size_t n = entries.size();
    std::vector<bool> failed(n, false);

    auto run = [&](size_t i) {
        std::string e;
        bool ok = false;
        try {
            ok = handler(entries[i], e);
        } catch (const std::exception& ex) {
            e = std::string("exception: ") + ex.what();
        }
        if (ok) { ++rep.restored; return; }
        failed[i] = true;
        ++rep.failed;
        rep.errors.push_back(entries[i].value("kind", std::string("?")) + ": " + e);
    };

    // Undo in reverse order of application, deferred entries last.
    for (size_t i = n; i-- > 0;)
        if (!entries[i].value("deferred", false)) run(i);
    for (size_t i = n; i-- > 0;)
        if (entries[i].value("deferred", false)) run(i);

    g_open = false;
    g_doc  = json();

    std::wstring path = filePath();
    if (rep.failed == 0) {
        rep.ok = DeleteFileW(path.c_str()) || GetLastError() == ERROR_FILE_NOT_FOUND;
        if (!rep.ok) rep.errors.push_back("restored, but could not delete " + Narrow(path));
        return rep;
    }

    // Keep only what still needs restoring
    json remaining = json::array();
    for (size_t i = 0; i < n; ++i)
        if (failed[i]) remaining.push_back(entries[i]);
    doc["entries"] = remaining;
    doc["state"]   = "restore_failed";
    if (!writeAtomic(path, doc.dump(2)))
        rep.errors.push_back("could not update " + Narrow(path));
    return rep;
}

// ── hex ──────────────────────────────────────────────────────────────────────
std::string ToHex(const unsigned char* data, size_t len) {
    static const char* digits = "0123456789abcdef";
    std::string s;
    s.reserve(len * 2);
    for (size_t i = 0; i < len; ++i) {
        s.push_back(digits[data[i] >> 4]);
        s.push_back(digits[data[i] & 0xF]);
    }
    return s;
}

std::vector<unsigned char> FromHex(const std::string& hex) {
    auto val = [](char c) -> int {
        if (c >= '0' && c <= '9') return c - '0';
        if (c >= 'a' && c <= 'f') return c - 'a' + 10;
        if (c >= 'A' && c <= 'F') return c - 'A' + 10;
        return -1;
    };
    std::vector<unsigned char> out;
    if (hex.size() % 2) return out;
    out.reserve(hex.size() / 2);
    for (size_t i = 0; i < hex.size(); i += 2) {
        int hi = val(hex[i]), lo = val(hex[i + 1]);
        if (hi < 0 || lo < 0) return {};
        out.push_back(static_cast<unsigned char>((hi << 4) | lo));
    }
    return out;
}

} // namespace PeakMode
