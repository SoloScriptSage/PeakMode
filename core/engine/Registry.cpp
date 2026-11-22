#include "Registry.h"
#include "Log.h"
#include "Utf8.h"

#include <algorithm>
#include <cwctype>
#include <vector>

namespace PeakMode {
namespace Registry {

namespace {

constexpr REGSAM kView = KEY_WOW64_64KEY;

std::wstring lower(std::wstring s) {
    std::transform(s.begin(), s.end(), s.begin(),
                   [](wchar_t c) { return static_cast<wchar_t>(std::towlower(c)); });
    return s;
}

bool startsWith(const std::wstring& s, const std::wstring& prefix) {
    return s.size() > prefix.size() && s.compare(0, prefix.size(), prefix) == 0;
}

struct Key {
    HKEY h = nullptr;
    ~Key() { if (h) RegCloseKey(h); }
};

bool readValue(HKEY h, const wchar_t* name, DWORD& type, std::vector<unsigned char>& data) {
    DWORD size = 0;
    LONG r = RegQueryValueExW(h, name, nullptr, &type, nullptr, &size);
    if (r != ERROR_SUCCESS) return false;
    data.resize(size);
    r = RegQueryValueExW(h, name, nullptr, &type, data.data(), &size);
    if (r != ERROR_SUCCESS) return false;
    data.resize(size);
    return true;
}

bool setValue(const std::wstring& path, const wchar_t* name, DWORD type,
              const unsigned char* data, DWORD size) {
    if (!IsAllowedPath(path)) {
        LogW(L"[Registry] refused write outside allowed paths: %s", path.c_str());
        return false;
    }
    Key k;
    if (RegOpenKeyExW(HKEY_LOCAL_MACHINE, path.c_str(), 0,
                      KEY_QUERY_VALUE | KEY_SET_VALUE | kView, &k.h) != ERROR_SUCCESS) {
        LogW(L"[Registry] cannot open HKLM\\%s (need admin?)", path.c_str());
        return false;
    }

    DWORD oldType = 0;
    std::vector<unsigned char> oldData;
    bool existed = readValue(k.h, name, oldType, oldData);
    if (existed && oldType == type && oldData.size() == size &&
        std::equal(oldData.begin(), oldData.end(), data))
        return true; // already the desired value: nothing to change or undo

    Snapshot::Entry e = {
        { "kind",    "registryValue" },
        { "path",    Narrow(path) },
        { "name",    Narrow(name) },
        { "existed", existed },
    };
    if (existed) {
        e["type"] = oldType;
        e["data"] = ToHex(oldData.data(), oldData.size());
    }
    if (!Snapshot::record(e)) {
        Log("[Registry] snapshot write failed; not changing %s", Narrow(name).c_str());
        return false;
    }
    return RegSetValueExW(k.h, name, 0, type, data, size) == ERROR_SUCCESS;
}

} // namespace

bool IsAllowedPath(const std::wstring& path) {
    const std::wstring p = lower(path);
    if (p.find(L"\\\\") != std::wstring::npos || p.back() == L'\\') return false;

    if (startsWith(p, L"system\\currentcontrolset\\services\\tcpip\\parameters\\interfaces\\"))
        return true;
    if (startsWith(p, L"system\\currentcontrolset\\control\\class\\"
                      L"{4d36e972-e325-11ce-bfc1-08002be10318}\\"))
        return true;
    if (startsWith(p, L"system\\currentcontrolset\\enum\\") &&
        p.find(L"\\device parameters\\interrupt management") != std::wstring::npos)
        return true;
    return false;
}

bool EnsureKey(const std::wstring& path) {
    if (!IsAllowedPath(path)) {
        LogW(L"[Registry] refused key outside allowed paths: %s", path.c_str());
        return false;
    }
    // Walk from the root, creating (and journaling) only missing keys.
    size_t pos = 0;
    while (pos != std::wstring::npos) {
        pos = path.find(L'\\', pos + 1);
        std::wstring sub = path.substr(0, pos);
        Key k;
        if (RegOpenKeyExW(HKEY_LOCAL_MACHINE, sub.c_str(), 0, KEY_READ | kView, &k.h)
            == ERROR_SUCCESS)
            continue;
        if (!IsAllowedPath(sub)) {
            LogW(L"[Registry] parent key missing and not ours to create: %s", sub.c_str());
            return false;
        }
        if (!Snapshot::record({ { "kind", "registryKey" }, { "path", Narrow(sub) } }))
            return false;
        Key created;
        if (RegCreateKeyExW(HKEY_LOCAL_MACHINE, sub.c_str(), 0, nullptr,
                            REG_OPTION_NON_VOLATILE, KEY_WRITE | kView, nullptr,
                            &created.h, nullptr) != ERROR_SUCCESS) {
            LogW(L"[Registry] cannot create HKLM\\%s", sub.c_str());
            return false;
        }
    }
    return true;
}

bool SetDword(const std::wstring& path, const wchar_t* name, DWORD value) {
    return setValue(path, name, REG_DWORD,
                    reinterpret_cast<const unsigned char*>(&value), sizeof(value));
}

bool SetString(const std::wstring& path, const wchar_t* name, const std::wstring& value) {
    return setValue(path, name, REG_SZ,
                    reinterpret_cast<const unsigned char*>(value.c_str()),
                    static_cast<DWORD>((value.size() + 1) * sizeof(wchar_t)));
}

bool SetBinary(const std::wstring& path, const wchar_t* name, const void* data, DWORD size) {
    return setValue(path, name, REG_BINARY, static_cast<const unsigned char*>(data), size);
}

bool RestoreEntry(const Snapshot::Entry& e, std::string& error) {
    const std::wstring path = Widen(e.value("path", std::string()));
    if (path.empty() || !IsAllowedPath(path)) {
        error = "path not allowed: " + Narrow(path);
        return false;
    }
    const std::string kind = e.value("kind", std::string());

    if (kind == "registryKey") {
        LONG r = RegDeleteKeyExW(HKEY_LOCAL_MACHINE, path.c_str(), kView, 0);
        if (r == ERROR_SUCCESS || r == ERROR_FILE_NOT_FOUND) return true;
        error = "cannot delete key " + Narrow(path) + " (error " + std::to_string(r) + ")";
        return false;
    }

    if (kind != "registryValue") { error = "unknown kind " + kind; return false; }
    const std::wstring name = Widen(e.value("name", std::string()));
    const bool existed      = e.value("existed", false);

    Key k;
    LONG r = RegOpenKeyExW(HKEY_LOCAL_MACHINE, path.c_str(), 0, KEY_SET_VALUE | kView, &k.h);
    if (r == ERROR_FILE_NOT_FOUND && !existed) return true; // key gone, value gone
    if (r != ERROR_SUCCESS) {
        error = "cannot open " + Narrow(path) + " (error " + std::to_string(r) + ")";
        return false;
    }
    if (!existed) {
        r = RegDeleteValueW(k.h, name.c_str());
        if (r == ERROR_SUCCESS || r == ERROR_FILE_NOT_FOUND) return true;
        error = "cannot delete " + Narrow(name) + " (error " + std::to_string(r) + ")";
        return false;
    }
    auto data = FromHex(e.value("data", std::string()));
    DWORD type = e.value("type", static_cast<DWORD>(REG_NONE));
    r = RegSetValueExW(k.h, name.c_str(), 0, type, data.data(), static_cast<DWORD>(data.size()));
    if (r == ERROR_SUCCESS) return true;
    error = "cannot set " + Narrow(name) + " (error " + std::to_string(r) + ")";
    return false;
}

} // namespace Registry
} // namespace PeakMode
