#pragma once
#include <string>

namespace PeakMode {

// MoveFileExW(from, to, REPLACE_EXISTING | WRITE_THROUGH), retried while
// another process briefly has `to` (or `from`) open: a reader without
// FILE_SHARE_DELETE makes the rename fail with ERROR_SHARING_VIOLATION,
// and Windows returns ERROR_ACCESS_DENIED while a handle is still closing.
// Those two errors are retried with growing sleeps (about half a second in
// total); any other error fails at once. On failure `from` is left in place
// for the caller to delete. `error` gets the last Win32 error (0 on
// success), `attempts` the number of MoveFileExW calls.
bool ReplaceFileRetrying(const std::wstring& from, const std::wstring& to,
                         unsigned long* error = nullptr, int* attempts = nullptr);

} // namespace PeakMode
