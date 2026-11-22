#define WIN32_LEAN_AND_MEAN
#include <Windows.h>
#include <winsock2.h>
#include <ws2ipdef.h>
#include <iphlpapi.h>
#include <netioapi.h>
#include <cfgmgr32.h>

#include <string>
#include <vector>

#include "InterruptTweaks.h"
#include "WindowsTweaks.h"
#include "../engine/Log.h"
#include "../engine/Registry.h"
#include "../engine/Utf8.h"

#pragma comment(lib, "iphlpapi.lib")
#pragma comment(lib, "cfgmgr32.lib")

namespace PeakMode {

namespace {

const wchar_t* kNetClass =
    L"SYSTEM\\CurrentControlSet\\Control\\Class\\{4d36e972-e325-11ce-bfc1-08002be10318}";

// Interrupt affinity policy (see "Interrupt Affinity and Priority" in the
// Windows driver docs). Value names are DevicePolicy + AssignmentSetOverride.
constexpr DWORD kIrqPolicySpecifiedProcessors = 4;

struct RegKey {
    HKEY h = nullptr;
    ~RegKey() { if (h) RegCloseKey(h); }
};

bool readString(HKEY h, const wchar_t* sub, const wchar_t* name, std::wstring& out) {
    wchar_t buf[512] = {};
    DWORD size = sizeof(buf) - sizeof(wchar_t);
    DWORD type = 0;
    if (RegGetValueW(h, sub, name, RRF_RT_REG_SZ, &type, buf, &size) != ERROR_SUCCESS)
        return false;
    out = buf;
    return true;
}

// Adapter properties changed for gaming. Only keywords the driver already
// exposes are touched, and only with values the driver declares valid.
struct PropTarget { const wchar_t* keyword; const wchar_t* value; };
const PropTarget kProps[] = {
    { L"*InterruptModeration", L"0"    },
    { L"*EEE",                 L"0"    },
    { L"GreenEthernet",        L"0"    },
    { L"PowerSavingMode",      L"0"    },
    { L"GigaLite",             L"0"    },
    { L"*WakeOnMagicPacket",   L"0"    },
    { L"*WakeOnPattern",       L"0"    },
    { L"*ReceiveBuffers",      L"2048" },
    { L"*TransmitBuffers",     L"512"  },
};

// Check value against <driverKey>\Ndi\Params\<keyword> (enum entries or int min/max)
bool driverAccepts(const std::wstring& driverKey, const wchar_t* keyword, const wchar_t* value) {
    std::wstring params = driverKey + L"\\Ndi\\Params\\" + keyword;
    RegKey k;
    if (RegOpenKeyExW(HKEY_LOCAL_MACHINE, params.c_str(), 0, KEY_READ | KEY_WOW64_64KEY, &k.h)
        != ERROR_SUCCESS)
        return false;
    std::wstring type;
    readString(k.h, nullptr, L"type", type);
    if (_wcsicmp(type.c_str(), L"enum") == 0) {
        std::wstring label;
        return readString(k.h, L"enum", value, label);
    }
    if (_wcsicmp(type.c_str(), L"int") == 0 || _wcsicmp(type.c_str(), L"long") == 0 ||
        _wcsicmp(type.c_str(), L"dword") == 0 || _wcsicmp(type.c_str(), L"word") == 0) {
        std::wstring mn, mx;
        if (!readString(k.h, nullptr, L"min", mn) || !readString(k.h, nullptr, L"max", mx))
            return false;
        long v = wcstol(value, nullptr, 10);
        return v >= wcstol(mn.c_str(), nullptr, 10) && v <= wcstol(mx.c_str(), nullptr, 10);
    }
    return false;
}

bool restartDevice(const std::wstring& instanceId, std::string& error) {
    DEVINST dn = 0;
    CONFIGRET cr = CM_Locate_DevNodeW(&dn, const_cast<DEVINSTID_W>(instanceId.c_str()),
                                      CM_LOCATE_DEVNODE_NORMAL);
    if (cr != CR_SUCCESS) {
        // A disabled device is still locatable with PHANTOM
        cr = CM_Locate_DevNodeW(&dn, const_cast<DEVINSTID_W>(instanceId.c_str()),
                                CM_LOCATE_DEVNODE_PHANTOM);
    }
    if (cr != CR_SUCCESS) { error = "device not found (CR " + std::to_string(cr) + ")"; return false; }

    CONFIGRET dis = CM_Disable_DevNode(dn, CM_DISABLE_UI_NOT_OK);
    if (dis == CR_SUCCESS) Sleep(1000);
    CONFIGRET en = CM_Enable_DevNode(dn, 0);
    if (en != CR_SUCCESS) {
        error = "enable failed (CR " + std::to_string(en) + ")";
        return false;
    }
    if (dis != CR_SUCCESS) {
        error = "disable failed (CR " + std::to_string(dis) + "); changes apply after reboot";
        return false;
    }
    return true;
}

} // namespace

bool InterruptTweaks::findNic(const std::string& configured, Nic& out, std::string& error) {
    // Map class subkeys: NetCfgInstanceId / DeviceInstanceID -> driver key
    RegKey cls;
    if (RegOpenKeyExW(HKEY_LOCAL_MACHINE, kNetClass, 0, KEY_READ | KEY_WOW64_64KEY, &cls.h)
        != ERROR_SUCCESS) {
        error = "cannot open network class key";
        return false;
    }
    struct Entry { std::wstring sub, cfgId, instId, desc; };
    std::vector<Entry> entries;
    wchar_t name[64];
    for (DWORD i = 0;; ++i) {
        DWORD len = 64;
        if (RegEnumKeyExW(cls.h, i, name, &len, nullptr, nullptr, nullptr, nullptr) != ERROR_SUCCESS)
            break;
        Entry e{ name };
        readString(cls.h, name, L"NetCfgInstanceId", e.cfgId);
        readString(cls.h, name, L"DeviceInstanceID", e.instId);
        readString(cls.h, name, L"DriverDesc", e.desc);
        if (!e.instId.empty()) entries.push_back(e);
    }

    auto fill = [&](const Entry& e) {
        out.instanceId  = e.instId;
        out.driverKey   = std::wstring(kNetClass) + L"\\" + e.sub;
        out.description = e.desc;
        return true;
    };

    if (!configured.empty()) {
        std::wstring want = Widen(configured);
        for (const auto& e : entries)
            if (_wcsicmp(e.instId.c_str(), want.c_str()) == 0) return fill(e);
        error = "configured nicInstanceId not found: " + configured;
        return false;
    }

    // Auto-detect: first connected, physical (hardware, non-filter) Ethernet adapter
    ULONG size = 16 * 1024;
    std::vector<BYTE> buf(size);
    auto* aa = reinterpret_cast<IP_ADAPTER_ADDRESSES*>(buf.data());
    ULONG r = GetAdaptersAddresses(AF_UNSPEC, GAA_FLAG_SKIP_ANYCAST | GAA_FLAG_SKIP_MULTICAST |
                                   GAA_FLAG_SKIP_DNS_SERVER, nullptr, aa, &size);
    if (r == ERROR_BUFFER_OVERFLOW) {
        buf.resize(size);
        aa = reinterpret_cast<IP_ADAPTER_ADDRESSES*>(buf.data());
        r = GetAdaptersAddresses(AF_UNSPEC, GAA_FLAG_SKIP_ANYCAST | GAA_FLAG_SKIP_MULTICAST |
                                 GAA_FLAG_SKIP_DNS_SERVER, nullptr, aa, &size);
    }
    if (r != NO_ERROR) { error = "GetAdaptersAddresses failed"; return false; }

    for (auto* a = aa; a; a = a->Next) {
        if (a->IfType != IF_TYPE_ETHERNET_CSMACD || a->OperStatus != IfOperStatusUp) continue;
        MIB_IF_ROW2 row{};
        row.InterfaceLuid = a->Luid;
        if (GetIfEntry2(&row) != NO_ERROR) continue;
        if (!row.InterfaceAndOperStatusFlags.HardwareInterface ||
            row.InterfaceAndOperStatusFlags.FilterInterface) continue;
        std::wstring guid = Widen(a->AdapterName);
        for (const auto& e : entries)
            if (_wcsicmp(e.cfgId.c_str(), guid.c_str()) == 0) return fill(e);
    }
    error = "no connected physical Ethernet adapter found";
    return false;
}

void InterruptTweaks::apply(const Profile& p) {
    Log("[InterruptTweaks] Applying...");
    if (!p.redirectNicIrq && !p.disableNicInterruptMod) return;

    Nic nic;
    std::string err;
    if (!findNic(p.nicInstanceId, nic, err)) {
        Log("[InterruptTweaks] Skipped: %s", err.c_str());
        return;
    }
    LogW(L"[InterruptTweaks] Adapter: %s (%s)", nic.description.c_str(), nic.instanceId.c_str());

    bool changed = false;

    if (p.redirectNicIrq) {
        // First thread of each physical core except core 0
        DWORD_PTR cores = WindowsTweaks::physicalCoreMask();
        KAFFINITY mask  = cores & (cores - 1); // clear lowest bit (core 0)
        std::wstring path = L"SYSTEM\\CurrentControlSet\\Enum\\" + nic.instanceId +
                            L"\\Device Parameters\\Interrupt Management\\Affinity Policy";
        if (mask && Registry::EnsureKey(path) &&
            Registry::SetDword(path, L"DevicePolicy", kIrqPolicySpecifiedProcessors) &&
            Registry::SetBinary(path, L"AssignmentSetOverride", &mask, sizeof(mask))) {
            Log("[InterruptTweaks] IRQ affinity -> 0x%llX (all physical cores except core 0)",
                static_cast<unsigned long long>(mask));
            changed = true;
        } else {
            Log("[InterruptTweaks] IRQ affinity: not changed");
        }
    }

    if (p.disableNicInterruptMod) {
        RegKey dk;
        RegOpenKeyExW(HKEY_LOCAL_MACHINE, nic.driverKey.c_str(), 0, KEY_READ | KEY_WOW64_64KEY, &dk.h);
        for (const auto& prop : kProps) {
            std::wstring cur;
            if (!dk.h || !readString(dk.h, nullptr, prop.keyword, cur)) continue; // not supported
            if (cur == prop.value) continue;
            if (!driverAccepts(nic.driverKey, prop.keyword, prop.value)) {
                LogW(L"[InterruptTweaks] %s: value %s not supported by driver, skipped",
                     prop.keyword, prop.value);
                continue;
            }
            if (Registry::SetString(nic.driverKey, prop.keyword, prop.value)) {
                LogW(L"[InterruptTweaks] %s: %s -> %s", prop.keyword, cur.c_str(), prop.value);
                changed = true;
            }
        }
    }

    if (!changed) {
        Log("[InterruptTweaks] Nothing changed, adapter not restarted");
        return;
    }
    // Journal the restart first: if we crash while the adapter is disabled,
    // restore re-enables it (deferred = after registry values are restored).
    if (!Snapshot::record({ { "kind", "nicRestart" }, { "instanceId", Narrow(nic.instanceId) },
                            { "deferred", true } })) {
        Log("[InterruptTweaks] snapshot write failed; adapter not restarted (applies after reboot)");
        return;
    }
    Log("[InterruptTweaks] Restarting adapter (network drops for a few seconds)...");
    if (restartDevice(nic.instanceId, err)) Log("[InterruptTweaks] Adapter restarted");
    else Log("[InterruptTweaks] Adapter restart: %s", err.c_str());
    Log("[InterruptTweaks] Done.");
}

bool InterruptTweaks::ownsKind(const std::string& k) { return k == "nicRestart"; }

bool InterruptTweaks::restoreEntry(const Snapshot::Entry& e, std::string& error) {
    std::wstring inst = Widen(e.value("instanceId", std::string()));
    if (inst.rfind(L"PCI\\", 0) != 0 && inst.rfind(L"USB\\", 0) != 0) {
        error = "unexpected device id";
        return false;
    }
    Log("[InterruptTweaks] Restarting adapter to apply restored settings...");
    if (!restartDevice(inst, error)) return false;
    Log("[InterruptTweaks] Adapter restarted");
    return true;
}

void InterruptTweaks::dump(const std::string& configured) {
    Nic nic;
    std::string err;
    if (!findNic(configured, nic, err)) { Log("nic.error = %s", err.c_str()); return; }
    Log("nic.instanceId = %s", Narrow(nic.instanceId).c_str());
    Log("nic.driverKey = %s", Narrow(nic.driverKey).c_str());
}

} // namespace PeakMode
