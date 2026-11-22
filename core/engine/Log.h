#pragma once
#include <string>

namespace PeakMode {

// printf-style logging to stdout and to <state dir>\peakmode.log.
// The tray app has no console, so the file is the only record there.
void Log(const char* fmt, ...);

// Wide-string convenience for registry / service names.
void LogW(const wchar_t* fmt, ...);

} // namespace PeakMode
