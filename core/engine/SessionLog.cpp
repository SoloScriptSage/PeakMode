#define WIN32_LEAN_AND_MEAN
#include <Windows.h>

#include <filesystem>
#include <fstream>
#include <mutex>

#include "Log.h"
#include "SessionLog.h"
#include "Snapshot.h"
#include "Utf8.h"

namespace PeakMode {
namespace SessionLog {

using nlohmann::json;

namespace {

constexpr size_t kDefaultMaxBytes = 1024 * 1024;
size_t     g_maxBytes = kDefaultMaxBytes;
std::mutex g_mutex;

std::string nowUtc() {
    SYSTEMTIME st;
    GetSystemTime(&st);
    char buf[32];
    snprintf(buf, sizeof(buf), "%04d-%02d-%02dT%02d:%02d:%02dZ",
             st.wYear, st.wMonth, st.wDay, st.wHour, st.wMinute, st.wSecond);
    return buf;
}

json event(const char* type, const std::string& session) {
    return json{
        { "v",       1 },
        { "type",    type },
        { "session", session },
        { "time",    nowUtc() },
        { "pid",     GetCurrentProcessId() },
    };
}

bool append(const json& ev) {
    std::lock_guard<std::mutex> lock(g_mutex);
    std::wstring path = Snapshot::stateFile(kFile);
    if (path.empty()) {
        Log("[SessionLog] state folder unavailable; session event not written");
        return false;
    }
    WIN32_FILE_ATTRIBUTE_DATA fa{};
    if (GetFileAttributesExW(path.c_str(), GetFileExInfoStandard, &fa)) {
        ULONGLONG size = (static_cast<ULONGLONG>(fa.nFileSizeHigh) << 32) | fa.nFileSizeLow;
        if (size > g_maxBytes)
            MoveFileExW(path.c_str(), Snapshot::stateFile(kOldFile).c_str(),
                        MOVEFILE_REPLACE_EXISTING | MOVEFILE_WRITE_THROUGH);
    }
    // error_handler::replace: a bad UTF-8 byte in a value must not throw
    std::string line = ev.dump(-1, ' ', false, json::error_handler_t::replace) + "\n";
    if (!Snapshot::appendToStateFile(kFile, line)) {
        Log("[SessionLog] cannot write %s", Narrow(path).c_str());
        return false;
    }
    return true;
}

void readFile(const std::wstring& path, json& out) {
    if (path.empty()) return;
    std::ifstream in(std::filesystem::path(path), std::ios::binary);
    if (!in) return;
    std::string line;
    while (std::getline(in, line)) {
        if (!line.empty() && line.back() == '\r') line.pop_back();
        if (line.empty()) continue;
        json ev = json::parse(line, nullptr, false);
        if (ev.is_discarded() || !ev.is_object() || !ev.contains("type")) continue;
        out.push_back(std::move(ev));
    }
}

} // namespace

bool begin(const std::string& session, const std::string& profile) {
    json ev = event("begin", session);
    ev["profile"] = profile;
    return append(ev);
}

bool active(const std::string& session, const json& entries, const std::string& powerScheme) {
    json ev = event("active", session);
    ev["entries"]     = entries.is_array() ? entries : json::array();
    ev["powerScheme"] = powerScheme;
    return append(ev);
}

bool end(const std::string& session, const char* reason, const char* result,
         int restored, int failed) {
    json ev = event("end", session);
    ev["reason"]   = reason;
    ev["result"]   = result;
    ev["restored"] = restored;
    ev["failed"]   = failed;
    return append(ev);
}

json readAll() {
    std::lock_guard<std::mutex> lock(g_mutex);
    json out = json::array();
    readFile(Snapshot::stateFile(kOldFile), out);
    readFile(Snapshot::stateFile(kFile), out);
    return out;
}

void setMaxBytesForTests(size_t bytes) {
    std::lock_guard<std::mutex> lock(g_mutex);
    g_maxBytes = bytes ? bytes : kDefaultMaxBytes;
}

} // namespace SessionLog
} // namespace PeakMode
