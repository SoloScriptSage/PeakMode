#define WIN32_LEAN_AND_MEAN
#include <Windows.h>
#include <winreg.h>
#include <cstdio>
#include <cstring>
#include <string>
#include <vector>

#include "InterruptTweaks.h"
#include "../engine/ProfileManager.h"

// ─────────────────────────────────────────────────────────────────────────────
// Realtek PCIe GbE (RTL8168) — device instance path from your machine:
// PCI\VEN_10EC&DEV_8168&SUBSYS_208F1043&REV_15\01000000684CE00000
//
// Two registry locations we touch:
//
// 1. IRQ affinity policy:
//    HKLM\SYSTEM\CurrentControlSet\Enum\<InstanceId>\Device Parameters\
//           Interrupt Management\Affinity Policy
//    AffinityPolicy         REG_DWORD  = 4  (IrqPolicySpecifiedProcessors)
//    AssignmentSetOverride  REG_BINARY = bitmask of target CPUs
//
// 2. Adapter advanced properties (same as Device Manager → Advanced tab):
//    HKLM\SYSTEM\CurrentControlSet\Enum\<InstanceId>\Device Parameters\
//           Ndi\params   (read display names)
//    The actual values live under the driver's software key:
//    HKLM\SYSTEM\CurrentControlSet\Control\Class\{4d36e972-e325-11ce-bfc1-08002be10318}\<index>
//    We find the right index via DriverKeyName in the device's enum key.
// ─────────────────────────────────────────────────────────────────────────────

static constexpr wchar_t kNicInstanceId[] =
    L"PCI\\VEN_10EC&DEV_8168&SUBSYS_208F1043&REV_15\\01000000684CE00000";

static constexpr wchar_t kNetClassGuid[] =
    L"SYSTEM\\CurrentControlSet\\Control\\Class\\"
    L"{4d36e972-e325-11ce-bfc1-08002be10318}";

// IRQ affinity policy values
static constexpr DWORD kAffinityPolicySpecified = 4;
static constexpr DWORD kAffinityPolicyDefault   = 0;

// Target cores for NIC interrupts: cores 4-7 (away from core 0 and 1)
// Bitmask: bits 4,5,6,7 set = 0xF0
static constexpr DWORD kNicAffinityMask = 0x000000F0;

namespace PeakMode {

// ── Saved state ───────────────────────────────────────────────────────────────
static DWORD s_prevAffinityPolicy   = kAffinityPolicyDefault;
static DWORD s_prevAffinityMask     = 0;
static bool  s_affinitySaved        = false;

// Adapter property saves — store previous values as strings
struct AdapterPropSave {
    std::wstring keyword;
    std::wstring prevValue;
};
static std::vector<AdapterPropSave> s_savedAdapterProps;

// ─────────────────────────────────────────────────────────────────────────────
// Find the driver software key index for our NIC
// Reads DriverKeyName from the device enum key, e.g.
// "{4d36e972-e325-11ce-bfc1-08002be10318}\0005"
// ─────────────────────────────────────────────────────────────────────────────
static std::wstring getDriverKeyPath() {
    std::wstring enumPath = std::wstring(L"SYSTEM\\CurrentControlSet\\Enum\\")
                          + kNicInstanceId;

    HKEY hKey;
    if (RegOpenKeyExW(HKEY_LOCAL_MACHINE, enumPath.c_str(), 0,
                      KEY_READ, &hKey) != ERROR_SUCCESS)
        return {};

    wchar_t driverKey[256] = {};
    DWORD sz = sizeof(driverKey);
    LONG r = RegQueryValueExW(hKey, L"Driver", nullptr, nullptr,
                              reinterpret_cast<BYTE*>(driverKey), &sz);
    RegCloseKey(hKey);

    if (r != ERROR_SUCCESS) return {};

    // driverKey looks like "{4d36e972-...}\0005" — build full path
    return std::wstring(L"SYSTEM\\CurrentControlSet\\Control\\Class\\") + driverKey;
}

// ─────────────────────────────────────────────────────────────────────────────
// Read/write a named adapter property from the driver software key
// ─────────────────────────────────────────────────────────────────────────────
static bool getAdapterProp(HKEY hDriverKey,
                           const wchar_t* keyword,
                           std::wstring& outValue) {
    wchar_t buf[256] = {};
    DWORD sz   = sizeof(buf);
    DWORD type = 0;
    if (RegQueryValueExW(hDriverKey, keyword, nullptr, &type,
                         reinterpret_cast<BYTE*>(buf), &sz) != ERROR_SUCCESS)
        return false;
    outValue = buf;
    return true;
}

static bool setAdapterProp(HKEY hDriverKey,
                           const wchar_t* keyword,
                           const wchar_t* value) {
    DWORD sz = static_cast<DWORD>((wcslen(value) + 1) * sizeof(wchar_t));
    return RegSetValueExW(hDriverKey, keyword, 0, REG_SZ,
                          reinterpret_cast<const BYTE*>(value), sz) == ERROR_SUCCESS;
}

// ─────────────────────────────────────────────────────────────────────────────
// IRQ affinity — pin NIC interrupts to cores 4-7
// ─────────────────────────────────────────────────────────────────────────────
static void setNicIrqAffinity(bool gaming) {
    std::wstring affinityPath =
        std::wstring(L"SYSTEM\\CurrentControlSet\\Enum\\") +
        kNicInstanceId +
        L"\\Device Parameters\\Interrupt Management\\Affinity Policy";

    HKEY hKey;
    // KEY_ALL_ACCESS needed to create subkeys if they don't exist
    DWORD disp = 0;
    if (RegCreateKeyExW(HKEY_LOCAL_MACHINE, affinityPath.c_str(), 0,
                        nullptr, REG_OPTION_NON_VOLATILE,
                        KEY_READ | KEY_WRITE, nullptr,
                        &hKey, &disp) != ERROR_SUCCESS) {
        printf("[InterruptTweaks] IRQ affinity: failed to open registry key\n");
        return;
    }

    if (gaming) {
        // Save previous policy
        DWORD sz = sizeof(DWORD);
        RegQueryValueExW(hKey, L"AffinityPolicy", nullptr, nullptr,
                         reinterpret_cast<BYTE*>(&s_prevAffinityPolicy), &sz);

        DWORD maskSz = sizeof(DWORD);
        RegQueryValueExW(hKey, L"AssignmentSetOverride", nullptr, nullptr,
                         reinterpret_cast<BYTE*>(&s_prevAffinityMask), &maskSz);
        s_affinitySaved = true;

        // Set policy = IrqPolicySpecifiedProcessors (4)
        DWORD policy = kAffinityPolicySpecified;
        RegSetValueExW(hKey, L"AffinityPolicy", 0, REG_DWORD,
                       reinterpret_cast<const BYTE*>(&policy), sizeof(DWORD));

        // Set target cores 4-7 (0xF0)
        DWORD mask = kNicAffinityMask;
        RegSetValueExW(hKey, L"AssignmentSetOverride", 0, REG_BINARY,
                       reinterpret_cast<const BYTE*>(&mask), sizeof(DWORD));

        printf("[InterruptTweaks] NIC IRQ affinity -> cores 4-7 (0xF0)\n");
        printf("[InterruptTweaks] Note: IRQ affinity takes effect after driver reload or reboot\n");
    } else {
        if (s_affinitySaved) {
            RegSetValueExW(hKey, L"AffinityPolicy", 0, REG_DWORD,
                           reinterpret_cast<const BYTE*>(&s_prevAffinityPolicy),
                           sizeof(DWORD));
            if (s_prevAffinityMask != 0) {
                RegSetValueExW(hKey, L"AssignmentSetOverride", 0, REG_BINARY,
                               reinterpret_cast<const BYTE*>(&s_prevAffinityMask),
                               sizeof(DWORD));
            }
            s_affinitySaved = false;
            printf("[InterruptTweaks] NIC IRQ affinity restored\n");
        }
    }

    RegCloseKey(hKey);
}

// ─────────────────────────────────────────────────────────────────────────────
// Adapter properties — gaming profile vs default
// Uses SetupAPI-style registry approach via driver software key
//
// Properties we change for gaming:
//   *InterruptModeration    0  (off  — reduces DPC latency)
//   *EEE                    0  (off  — Energy Efficient Ethernet adds wake latency)
//   GreenEthernet           0  (off  — same reason)
//   PowerSavingMode         0  (off  — keeps link at full speed)
//   GigaLite                0  (off  — allows full Gigabit)
//   *WakeOnMagicPacket      0  (off  — not needed during gaming)
//   *WakeOnPattern          0  (off)
//   ReceiveBuffers          2048 (up from 512 — more headroom)
//   TransmitBuffers         512  (up from 128)
// ─────────────────────────────────────────────────────────────────────────────
struct AdapterPropTarget {
    const wchar_t* keyword;
    const wchar_t* gamingValue;
};

static const AdapterPropTarget kGamingProps[] = {
    { L"*InterruptModeration", L"0"    },
    { L"*EEE",                 L"0"    },
    { L"GreenEthernet",        L"0"    },
    { L"PowerSavingMode",      L"0"    },
    { L"GigaLite",             L"0"    },
    { L"*WakeOnMagicPacket",   L"0"    },
    { L"*WakeOnPattern",       L"0"    },
    { L"ReceiveBuffers",       L"2048" },
    { L"TransmitBuffers",      L"512"  },
};

static void setAdapterProperties(bool gaming) {
    std::wstring driverPath = getDriverKeyPath();
    if (driverPath.empty()) {
        printf("[InterruptTweaks] Could not find NIC driver key\n");
        return;
    }

    HKEY hKey;
    if (RegOpenKeyExW(HKEY_LOCAL_MACHINE, driverPath.c_str(), 0,
                      KEY_READ | KEY_WRITE, &hKey) != ERROR_SUCCESS) {
        printf("[InterruptTweaks] Could not open driver key (need admin?)\n");
        return;
    }

    if (gaming) {
        s_savedAdapterProps.clear();
        for (const auto& prop : kGamingProps) {
            // Save current value
            std::wstring prev;
            if (getAdapterProp(hKey, prop.keyword, prev)) {
                s_savedAdapterProps.push_back({ prop.keyword, prev });
            }
            // Apply gaming value
            if (setAdapterProp(hKey, prop.keyword, prop.gamingValue)) {
                wprintf(L"[InterruptTweaks] %s -> %s\n",
                        prop.keyword, prop.gamingValue);
            }
        }
    } else {
        for (const auto& saved : s_savedAdapterProps) {
            setAdapterProp(hKey, saved.keyword.c_str(), saved.prevValue.c_str());
            wprintf(L"[InterruptTweaks] %s restored -> %s\n",
                    saved.keyword.c_str(), saved.prevValue.c_str());
        }
        s_savedAdapterProps.clear();
    }

    RegCloseKey(hKey);
}

// ─────────────────────────────────────────────────────────────────────────────
// Restart NIC driver to apply adapter property changes immediately
// (without this, changes take effect on next reboot)
// ─────────────────────────────────────────────────────────────────────────────
static void restartNicDriver() {
    // netsh is the cleanest way to do this without DeviceIoControl complexity
    printf("[InterruptTweaks] Restarting NIC driver to apply changes...\n");
    system("netsh interface set interface \"Ethernet\" admin=disable >nul 2>&1");
    Sleep(1000);
    system("netsh interface set interface \"Ethernet\" admin=enable >nul 2>&1");
    Sleep(500);
    printf("[InterruptTweaks] NIC driver restarted\n");
}

// ─────────────────────────────────────────────────────────────────────────────
// APPLY
// ─────────────────────────────────────────────────────────────────────────────
void InterruptTweaks::apply(const Profile& p) {
    printf("[InterruptTweaks] Applying tweaks...\n");

    if (p.redirectNicIrq)
        setNicIrqAffinity(true);

    if (p.disableNicInterruptMod)
        setAdapterProperties(true);

    // Restart NIC to apply adapter property changes immediately
    restartNicDriver();

    printf("[InterruptTweaks] Done.\n");
}

// ─────────────────────────────────────────────────────────────────────────────
// RESTORE
// ─────────────────────────────────────────────────────────────────────────────
void InterruptTweaks::restore() {
    printf("[InterruptTweaks] Restoring...\n");

    setNicIrqAffinity(false);
    setAdapterProperties(false);
    restartNicDriver();

    printf("[InterruptTweaks] Restored.\n");
}

} // namespace PeakMode