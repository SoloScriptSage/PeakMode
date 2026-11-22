#define WIN32_LEAN_AND_MEAN
#include <Windows.h>
#include <cstdarg>
#include <cstdio>
#include <mutex>

#include "Log.h"
#include "Snapshot.h"
#include "Utf8.h"

namespace PeakMode {

namespace {
std::mutex g_logMutex;

void writeLine(const std::string& line) {
    std::lock_guard<std::mutex> lock(g_logMutex);
    fputs(line.c_str(), stdout);
    fputc('\n', stdout);
    fflush(stdout);

    std::wstring dir = Snapshot::stateDir();
    if (dir.empty()) return;
    std::wstring path = dir + L"\\peakmode.log";

    // Keep the log bounded: rotate at 1 MB
    WIN32_FILE_ATTRIBUTE_DATA fa{};
    if (GetFileAttributesExW(path.c_str(), GetFileExInfoStandard, &fa) &&
        fa.nFileSizeLow > 1024 * 1024)
        MoveFileExW(path.c_str(), (path + L".1").c_str(), MOVEFILE_REPLACE_EXISTING);

    HANDLE h = CreateFileW(path.c_str(), FILE_APPEND_DATA, FILE_SHARE_READ, nullptr,
                           OPEN_ALWAYS, FILE_ATTRIBUTE_NORMAL, nullptr);
    if (h == INVALID_HANDLE_VALUE) return;

    SYSTEMTIME st;
    GetLocalTime(&st);
    char ts[32];
    snprintf(ts, sizeof(ts), "%04d-%02d-%02d %02d:%02d:%02d ",
             st.wYear, st.wMonth, st.wDay, st.wHour, st.wMinute, st.wSecond);
    std::string out = std::string(ts) + line + "\r\n";
    DWORD written = 0;
    WriteFile(h, out.data(), static_cast<DWORD>(out.size()), &written, nullptr);
    CloseHandle(h);
}
} // namespace

void Log(const char* fmt, ...) {
    char buf[2048];
    va_list ap;
    va_start(ap, fmt);
    vsnprintf(buf, sizeof(buf), fmt, ap);
    va_end(ap);
    writeLine(buf);
}

void LogW(const wchar_t* fmt, ...) {
    wchar_t buf[2048];
    va_list ap;
    va_start(ap, fmt);
    _vsnwprintf_s(buf, _countof(buf), _TRUNCATE, fmt, ap);
    va_end(ap);
    writeLine(Narrow(buf));
}

} // namespace PeakMode
