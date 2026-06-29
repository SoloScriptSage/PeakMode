#include "InterruptTweaks.h"
#include "../engine/Snapshot.h"
#include <Windows.h>
#include <setupapi.h>

namespace PeakMode {

// IRQ affinity is set via registry under:
// HKLM\System\CurrentControlSet\Enum\<DeviceID>\Device Parameters\Interrupt Management\Affinity Policy
// AffinityPolicy REG_DWORD = 4 (IrqPolicySpecifiedProcessors)
// AssignmentSetOverride REG_BINARY = bitmask of target CPUs

void InterruptTweaks::apply(const Profile& p) {
    if (p.redirectNicIrq) {
        // TODO: enumerate NIC devices via SetupDiGetClassDevs()
        // Write AffinityPolicy=4 + AssignmentSetOverride to cores != 0
        // Requires admin + reboot or driver reload to take effect
    }

    if (p.disableNicInterruptMod) {
        // TODO: set "Interrupt Moderation" adapter property to Disabled
        // via OID_GEN_INTERRUPT_MODERATION through DeviceIoControl / NDIS
    }

    if (p.disableHpet) {
        // WARNING: requires reboot — disabled by default in Profile
        // system("bcdedit /deletevalue useplatformclock >nul 2>&1");
        // system("bcdedit /set disabledynamictick yes >nul 2>&1");
    }

    (void)p;
}

void InterruptTweaks::restore() {
    // TODO: restore NIC IRQ affinity policy from Snapshot
    // TODO: restore interrupt moderation setting
}

} // namespace PeakMode
