#pragma once
#include <functional>
#include <string>
#include <vector>

#include "../third_party/nlohmann/json.hpp"

namespace PeakMode {

// Write-ahead journal of every system change PeakMode makes.
//
// Rule for modules: call Snapshot::record() with the ORIGINAL value BEFORE
// changing anything. If record() returns false, do not make the change.
// The journal lives on disk (<state dir>\snapshot.json) so that a crash,
// kill or reboot while active can still be undone from a fresh process.
//
// Entry format: a JSON object with at least "kind". Entries with
// "deferred": true are restored after all others (e.g. NIC restart after
// its registry values are back). Restore handlers must be idempotent:
// "already in the original state" counts as success.
class Snapshot {
public:
    using Entry   = nlohmann::json;
    using Handler = std::function<bool(const Entry&, std::string& error)>;

    struct RestoreReport {
        bool                     ok = false;   // everything restored, file removed
        int                      restored = 0;
        int                      failed   = 0;
        std::vector<std::string> errors;
    };

    // %ProgramData%\PeakMode: Administrators/SYSTEM-only ACL (created that way,
    // repaired on first use if not), must be owned by Administrators or
    // SYSTEM. Empty on failure.
    static std::wstring stateDir();
    static std::string  stateDirError();

    // True if `path` has a protected DACL with exactly two explicit
    // full-control ACEs: SYSTEM and Administrators (inheritable for a dir).
    static bool isAdminOnly(const std::wstring& path, bool isDirectory);

    // Apply that ACL to `dir` and every file in it, then verify.
    // Called on startup for %ProgramData%\PeakMode (repairs a wrong ACL).
    static bool secureStateDir(const std::wstring& dir, std::string& error);
    static std::wstring filePath();

    static bool exists();

    // Start a new journal. Fails if one already exists (leftover).
    static bool begin(const std::string& profileName, std::string& error);

    // Append and flush to disk. Returns false if the write failed.
    static bool record(const Entry& entry);

    static bool markActive();

    // Restore every entry from the on-disk journal using `handler`.
    // Removes the file if all entries succeed; otherwise rewrites it with
    // only the failed entries so a later retry can finish the job.
    static RestoreReport restoreAll(const Handler& handler);

    // Read-only view of the on-disk journal (for status / diagnostics).
    static bool load(Entry& out, std::string& error);

    // Test hook: use `dir` instead of %ProgramData%\PeakMode and skip the
    // ACL/owner checks. Pass an empty string to go back to the default.
    static void setStateDirForTests(const std::wstring& dir);

    // Test hook: terminate the process right after the Nth successful
    // record() (simulates a crash mid-activation). 0 = off.
    static void setDieAfterRecordsForTests(int n);
};

// Hex helpers for binary registry data in journal entries.
std::string ToHex(const unsigned char* data, size_t len);
std::vector<unsigned char> FromHex(const std::string& hex);

} // namespace PeakMode
