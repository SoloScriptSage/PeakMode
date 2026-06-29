#define WIN32_LEAN_AND_MEAN
#include <Windows.h>
#include <cstdio>

#include "../third_party/nvapi/nvapi.h"
#include "../third_party/nvapi/NvApiDriverSettings.h"

#include "NvidiaTweaks.h"
#include "../engine/ProfileManager.h"

#pragma comment(lib, "../third_party/nvapi/amd64/nvapi64.lib")

// LOW_LATENCY_MODE is not in this SDK version — use raw setting ID
// 0x0000108B = Ultra Low Latency (confirmed NVIDIA DRS setting ID)
#define ULTRA_LOW_LATENCY_ID    0x0000108B
#define ULTRA_LOW_LATENCY_OFF   0x00000000
#define ULTRA_LOW_LATENCY_ON    0x00000001
#define ULTRA_LOW_LATENCY_ULTRA 0x00000002

namespace PeakMode {

bool NvidiaTweaks::s_initialized = false;

// ── Saved state for restore ───────────────────────────────────────────────────
static NvU32 s_prevPowerPolicy    = PREFERRED_PSTATE_ADAPTIVE;
static NvU32 s_prevPrerenderLimit = 3;
static NvU32 s_prevVsync          = VSYNCMODE_PASSIVE;
static NvU32 s_prevLowLatency     = ULTRA_LOW_LATENCY_OFF;
static NvU32 s_prevThreadedOpt    = OGL_THREAD_CONTROL_DEFAULT;
static bool  s_drsSettingsSaved   = false;

// ─────────────────────────────────────────────────────────────────────────────
// Init / shutdown
// ─────────────────────────────────────────────────────────────────────────────
bool NvidiaTweaks::initNvAPI() {
    if (s_initialized) return true;

    NvAPI_Status status = NvAPI_Initialize();
    if (status != NVAPI_OK) {
        NvAPI_ShortString err;
        NvAPI_GetErrorMessage(status, err);
        printf("[NvidiaTweaks] NvAPI_Initialize failed: %s\n", err);
        return false;
    }

    s_initialized = true;
    printf("[NvidiaTweaks] NVAPI initialized\n");
    return true;
}

void NvidiaTweaks::shutdownNvAPI() {
    if (!s_initialized) return;
    NvAPI_Unload();
    s_initialized = false;
}

// ─────────────────────────────────────────────────────────────────────────────
// DRS helpers
// ─────────────────────────────────────────────────────────────────────────────
static bool getDRSSetting(NvDRSSessionHandle hSession,
                          NvDRSProfileHandle hProfile,
                          NvU32              settingId,
                          NvU32&             outValue) {
    NVDRS_SETTING setting = {};
    setting.version = NVDRS_SETTING_VER;
    if (NvAPI_DRS_GetSetting(hSession, hProfile, settingId, &setting) != NVAPI_OK)
        return false;
    outValue = setting.u32CurrentValue;
    return true;
}

static bool setDRSSetting(NvDRSSessionHandle hSession,
                          NvDRSProfileHandle hProfile,
                          NvU32              settingId,
                          NvU32              value) {
    NVDRS_SETTING setting = {};
    setting.version         = NVDRS_SETTING_VER;
    setting.settingId       = settingId;
    setting.settingType     = NVDRS_DWORD_TYPE;
    setting.u32CurrentValue = value;

    NvAPI_Status s = NvAPI_DRS_SetSetting(hSession, hProfile, &setting);
    if (s != NVAPI_OK) {
        NvAPI_ShortString err;
        NvAPI_GetErrorMessage(s, err);
        printf("[NvidiaTweaks] Failed to set DRS 0x%X: %s\n", settingId, err);
        return false;
    }
    return true;
}

// ─────────────────────────────────────────────────────────────────────────────
// Apply / restore DRS settings
// ─────────────────────────────────────────────────────────────────────────────
bool NvidiaTweaks::applyDRSSettings(const Profile& p) {
    NvDRSSessionHandle hSession = nullptr;
    if (NvAPI_DRS_CreateSession(&hSession) != NVAPI_OK) {
        printf("[NvidiaTweaks] Failed to create DRS session\n");
        return false;
    }
    if (NvAPI_DRS_LoadSettings(hSession) != NVAPI_OK) {
        printf("[NvidiaTweaks] Failed to load DRS settings\n");
        NvAPI_DRS_DestroySession(hSession);
        return false;
    }

    NvDRSProfileHandle hProfile = nullptr;
    if (NvAPI_DRS_GetBaseProfile(hSession, &hProfile) != NVAPI_OK) {
        printf("[NvidiaTweaks] Failed to get base profile\n");
        NvAPI_DRS_DestroySession(hSession);
        return false;
    }

    // Snapshot before touching anything
    if (!s_drsSettingsSaved) {
        getDRSSetting(hSession, hProfile, PREFERRED_PSTATE_ID,    s_prevPowerPolicy);
        getDRSSetting(hSession, hProfile, PRERENDERLIMIT_ID,       s_prevPrerenderLimit);
        getDRSSetting(hSession, hProfile, VSYNCMODE_ID,            s_prevVsync);
        getDRSSetting(hSession, hProfile, ULTRA_LOW_LATENCY_ID,    s_prevLowLatency);
        getDRSSetting(hSession, hProfile, OGL_THREAD_CONTROL_ID,   s_prevThreadedOpt);
        s_drsSettingsSaved = true;
        printf("[NvidiaTweaks] DRS snapshot saved\n");
    }

    bool ok = true;

    // 1. Power policy -> Maximum Performance (never let GPU clock down)
    if (p.maxPerfMode) {
        ok &= setDRSSetting(hSession, hProfile,
                            PREFERRED_PSTATE_ID,
                            PREFERRED_PSTATE_PREFER_MAX);
        printf("[NvidiaTweaks] Power policy -> Maximum Performance\n");
    }

    // 2. Pre-rendered frames -> 1 (minimum CPU-to-GPU queue depth)
    if (p.disablePrerenderFrames) {
        ok &= setDRSSetting(hSession, hProfile, PRERENDERLIMIT_ID, 1);
        printf("[NvidiaTweaks] Pre-rendered frames -> 1\n");
    }

    // 3. VSync -> Force off
    if (p.disableVSync) {
        ok &= setDRSSetting(hSession, hProfile,
                            VSYNCMODE_ID,
                            VSYNCMODE_FORCEOFF);
        printf("[NvidiaTweaks] VSync -> Force off\n");
    }

    // 4. Ultra Low Latency -> Ultra (reduces render queue, cuts input lag)
    if (p.enableLowLatency) {
        ok &= setDRSSetting(hSession, hProfile,
                            ULTRA_LOW_LATENCY_ID,
                            ULTRA_LOW_LATENCY_ULTRA);
        printf("[NvidiaTweaks] Ultra Low Latency -> Ultra\n");
    }

    // 5. Threaded optimization -> On
    ok &= setDRSSetting(hSession, hProfile,
                        OGL_THREAD_CONTROL_ID,
                        OGL_THREAD_CONTROL_ENABLE);
    printf("[NvidiaTweaks] Threaded optimization -> On\n");

    // Commit
    if (NvAPI_DRS_SaveSettings(hSession) != NVAPI_OK) {
        printf("[NvidiaTweaks] Failed to save DRS settings\n");
        ok = false;
    } else {
        printf("[NvidiaTweaks] DRS settings committed\n");
    }

    NvAPI_DRS_DestroySession(hSession);
    return ok;
}

void NvidiaTweaks::restoreDRSSettings() {
    if (!s_drsSettingsSaved) return;

    NvDRSSessionHandle hSession = nullptr;
    if (NvAPI_DRS_CreateSession(&hSession) != NVAPI_OK) return;
    if (NvAPI_DRS_LoadSettings(hSession) != NVAPI_OK) {
        NvAPI_DRS_DestroySession(hSession);
        return;
    }

    NvDRSProfileHandle hProfile = nullptr;
    if (NvAPI_DRS_GetBaseProfile(hSession, &hProfile) != NVAPI_OK) {
        NvAPI_DRS_DestroySession(hSession);
        return;
    }

    setDRSSetting(hSession, hProfile, PREFERRED_PSTATE_ID,  s_prevPowerPolicy);
    setDRSSetting(hSession, hProfile, PRERENDERLIMIT_ID,     s_prevPrerenderLimit);
    setDRSSetting(hSession, hProfile, VSYNCMODE_ID,          s_prevVsync);
    setDRSSetting(hSession, hProfile, ULTRA_LOW_LATENCY_ID,  s_prevLowLatency);
    setDRSSetting(hSession, hProfile, OGL_THREAD_CONTROL_ID, s_prevThreadedOpt);

    NvAPI_DRS_SaveSettings(hSession);
    NvAPI_DRS_DestroySession(hSession);

    s_drsSettingsSaved = false;
    printf("[NvidiaTweaks] DRS settings restored\n");
}

// ─────────────────────────────────────────────────────────────────────────────
// Log GPU info
// ─────────────────────────────────────────────────────────────────────────────
static void logGPUInfo() {
    NvPhysicalGpuHandle gpus[NVAPI_MAX_PHYSICAL_GPUS] = {};
    NvU32 gpuCount = 0;
    if (NvAPI_EnumPhysicalGPUs(gpus, &gpuCount) != NVAPI_OK) return;

    for (NvU32 i = 0; i < gpuCount; i++) {
        NvAPI_ShortString name = {};
        NvAPI_GPU_GetFullName(gpus[i], name);
        printf("[NvidiaTweaks] GPU[%u]: %s\n", i, name);
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// APPLY
// ─────────────────────────────────────────────────────────────────────────────
bool NvidiaTweaks::apply(const Profile& p) {
    printf("[NvidiaTweaks] Applying tweaks...\n");

    if (!initNvAPI()) return false;

    logGPUInfo();
    applyDRSSettings(p);

    printf("[NvidiaTweaks] Done.\n");
    return true;
}

// ─────────────────────────────────────────────────────────────────────────────
// RESTORE
// ─────────────────────────────────────────────────────────────────────────────
void NvidiaTweaks::restore() {
    printf("[NvidiaTweaks] Restoring...\n");
    restoreDRSSettings();
    shutdownNvAPI();
    printf("[NvidiaTweaks] Restored.\n");
}

} // namespace PeakMode