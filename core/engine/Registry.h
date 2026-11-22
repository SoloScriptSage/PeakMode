#pragma once
#define WIN32_LEAN_AND_MEAN
#include <Windows.h>
#include <string>

#include "Snapshot.h"

namespace PeakMode {

// Journaled HKLM registry writes. Each call records the original state
// (value data/type, or "did not exist") in the Snapshot BEFORE writing.
// Only paths under the subtrees PeakMode is allowed to modify are accepted,
// both when writing and when restoring from a snapshot file.
namespace Registry {

// Create any missing keys along `path`, journaling each one it creates
// so restore can delete them again.
bool EnsureKey(const std::wstring& path);

bool SetDword (const std::wstring& path, const wchar_t* name, DWORD value);
bool SetString(const std::wstring& path, const wchar_t* name, const std::wstring& value);
bool SetBinary(const std::wstring& path, const wchar_t* name, const void* data, DWORD size);

// Undo one "registryValue" or "registryKey" entry. Idempotent.
bool RestoreEntry(const Snapshot::Entry& e, std::string& error);

bool IsAllowedPath(const std::wstring& path);

} // namespace Registry
} // namespace PeakMode
