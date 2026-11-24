#define WIN32_LEAN_AND_MEAN
#include <Windows.h>

#include <iterator>

#include "FileUtil.h"
#include "Log.h"
#include "Utf8.h"

namespace PeakMode {

namespace {
// Sleeps between attempts: 8 attempts, 485 ms in total
constexpr DWORD kDelaysMs[] = { 10, 25, 50, 100, 100, 100, 100 };
constexpr int   kAttempts   = static_cast<int>(std::size(kDelaysMs)) + 1;

bool retryable(DWORD e) { return e == ERROR_SHARING_VIOLATION || e == ERROR_ACCESS_DENIED; }
} // namespace

bool ReplaceFileRetrying(const std::wstring& from, const std::wstring& to,
                         unsigned long* error, int* attempts) {
    DWORD err = 0;
    int n = 0;
    while (true) {
        ++n;
        if (MoveFileExW(from.c_str(), to.c_str(), MOVEFILE_REPLACE_EXISTING | MOVEFILE_WRITE_THROUGH)) {
            if (n > 1) Log("[File] replaced %s after %d attempts", Narrow(to).c_str(), n);
            err = 0;
            break;
        }
        err = GetLastError();
        if (!retryable(err) || n >= kAttempts) {
            Log("[File] cannot replace %s (error %lu, %d attempt%s)", Narrow(to).c_str(),
                err, n, n == 1 ? "" : "s");
            break;
        }
        Sleep(kDelaysMs[n - 1]);
    }
    if (error)    *error    = err;
    if (attempts) *attempts = n;
    return err == 0;
}

} // namespace PeakMode
