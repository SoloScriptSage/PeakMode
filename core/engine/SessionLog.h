#pragma once
#include <cstddef>
#include <string>

#include "../third_party/nlohmann/json.hpp"

namespace PeakMode {

// Append-only history of activations, one JSON object per line, in
// <state dir>\sessions.jsonl (same admin-only ACL as the snapshot). Read by
// the History screen. At 1 MB the file moves to sessions.1.jsonl, replacing
// the previous old copy. Writing is best effort: a failure is logged and
// never blocks activate or restore.
//
// Every event: { "v": 1, "type", "session", "time" (UTC), "pid" }
//   begin   + "profile"
//   active  + "entries" (copy of the snapshot entries), "powerScheme" (GUID
//             active after the steps, "" if unknown)
//   end     + "reason" (see RestoreReason), "result" ("ok" | "partial"),
//             "restored", "failed"
// A session restored by another process than the one that began it (crash,
// kill, reboot) has an end event with a different pid. A partial restore
// can be followed by more end events for the same session; the last wins.
namespace SessionLog {

constexpr const wchar_t* kFile    = L"sessions.jsonl";
constexpr const wchar_t* kOldFile = L"sessions.1.jsonl";

bool begin(const std::string& session, const std::string& profile);
bool active(const std::string& session, const nlohmann::json& entries,
            const std::string& powerScheme);
bool end(const std::string& session, const char* reason, const char* result,
         int restored, int failed);

// All events, oldest first (old file, then current). Unreadable lines are
// skipped. Always an array.
nlohmann::json readAll();

// Test hook: rotate above `bytes` instead of 1 MB (0 = default).
void setMaxBytesForTests(size_t bytes);

} // namespace SessionLog
} // namespace PeakMode
