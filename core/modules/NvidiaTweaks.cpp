#define WIN32_LEAN_AND_MEAN
#include <Windows.h>

#include "nvapi.h"
#include "NvApiDriverSettings.h"

#include "NvidiaTweaks.h"
#include "../engine/Log.h"

// nvapi64.lib is linked by CMake (absolute path), not via #pragma comment

namespace PeakMode {

namespace {

bool g_initialized = false;

bool ensureInit() {
    if (g_initialized) return true;
    NvAPI_Status s = NvAPI_Initialize();
    if (s != NVAPI_OK) {
        NvAPI_ShortString err;
        NvAPI_GetErrorMessage(s, err);
        Log("[NvidiaTweaks] NVAPI unavailable (%s): no NVIDIA GPU/driver?", err);
        return false;
    }
    g_initialized = true;
    return true;
}

std::string errText(NvAPI_Status s) {
    NvAPI_ShortString err;
    NvAPI_GetErrorMessage(s, err);
    return err;
}

// RAII DRS session on the global base profile
struct Drs {
    NvDRSSessionHandle session = nullptr;
    NvDRSProfileHandle profile = nullptr;
    std::string        error;

    bool open() {
        NvAPI_Status s = NvAPI_DRS_CreateSession(&session);
        if (s != NVAPI_OK) { error = "CreateSession: " + errText(s); return false; }
        s = NvAPI_DRS_LoadSettings(session);
        if (s != NVAPI_OK) { error = "LoadSettings: " + errText(s); return false; }
        s = NvAPI_DRS_GetBaseProfile(session, &profile);
        if (s != NVAPI_OK) { error = "GetBaseProfile: " + errText(s); return false; }
        return true;
    }
    // existed = explicitly set in the base profile (not just a predefined default)
    bool get(NvU32 id, bool& existed, NvU32& value) {
        NVDRS_SETTING st = {};
        st.version = NVDRS_SETTING_VER;
        NvAPI_Status s = NvAPI_DRS_GetSetting(session, profile, id, &st);
        if (s == NVAPI_SETTING_NOT_FOUND) { existed = false; value = 0; return true; }
        if (s != NVAPI_OK) { error = "GetSetting: " + errText(s); return false; }
        existed = !st.isCurrentPredefined;
        value   = st.u32CurrentValue;
        return true;
    }
    bool set(NvU32 id, NvU32 value) {
        NVDRS_SETTING st = {};
        st.version         = NVDRS_SETTING_VER;
        st.settingId       = id;
        st.settingType     = NVDRS_DWORD_TYPE;
        st.u32CurrentValue = value;
        NvAPI_Status s = NvAPI_DRS_SetSetting(session, profile, &st);
        if (s != NVAPI_OK) { error = "SetSetting: " + errText(s); return false; }
        return true;
    }
    bool remove(NvU32 id) {
        NvAPI_Status s = NvAPI_DRS_DeleteProfileSetting(session, profile, id);
        if (s == NVAPI_OK || s == NVAPI_SETTING_NOT_FOUND) return true;
        // Settings with a predefined value cannot be deleted; reset them instead.
        s = NvAPI_DRS_RestoreProfileDefaultSetting(session, profile, id);
        if (s == NVAPI_OK || s == NVAPI_SETTING_NOT_FOUND) return true;
        error = "Delete/RestoreDefault: " + errText(s);
        return false;
    }
    bool save() {
        NvAPI_Status s = NvAPI_DRS_SaveSettings(session);
        if (s != NVAPI_OK) { error = "SaveSettings: " + errText(s); return false; }
        return true;
    }
    ~Drs() { if (session) NvAPI_DRS_DestroySession(session); }
};

struct Target { const char* flagName; bool Profile::*flag; NvU32 id; NvU32 value; const char* label; };

const Target kTargets[] = {
    { "maxPerfMode",            &Profile::maxPerfMode,            PREFERRED_PSTATE_ID,
      PREFERRED_PSTATE_PREFER_MAX,  "Power management -> Prefer maximum performance" },
    { "disablePrerenderFrames", &Profile::disablePrerenderFrames, PRERENDERLIMIT_ID,
      1,                            "Max pre-rendered frames -> 1 (Low Latency: On)" },
    { "disableVSync",           &Profile::disableVSync,           VSYNCMODE_ID,
      VSYNCMODE_FORCEOFF,           "Vertical sync -> Force off" },
    { "threadedOptimization",   &Profile::threadedOptimization,   OGL_THREAD_CONTROL_ID,
      OGL_THREAD_CONTROL_ENABLE,    "Threaded optimization -> On" },
};

bool isKnownId(NvU32 id) {
    for (const auto& t : kTargets) if (t.id == id) return true;
    return false;
}

void logGpus() {
    NvPhysicalGpuHandle gpus[NVAPI_MAX_PHYSICAL_GPUS] = {};
    NvU32 n = 0;
    if (NvAPI_EnumPhysicalGPUs(gpus, &n) != NVAPI_OK) return;
    for (NvU32 i = 0; i < n; ++i) {
        NvAPI_ShortString name = {};
        NvAPI_GPU_GetFullName(gpus[i], name);
        Log("[NvidiaTweaks] GPU[%u]: %s", i, name);
    }
}

} // namespace

void NvidiaTweaks::apply(const Profile& p) {
    Log("[NvidiaTweaks] Applying...");
    if (!ensureInit()) return;
    logGpus();

    Drs drs;
    if (!drs.open()) { Log("[NvidiaTweaks] %s", drs.error.c_str()); return; }

    int changed = 0;
    for (const auto& t : kTargets) {
        if (!(p.*t.flag)) continue;
        bool existed = false;
        NvU32 cur = 0;
        if (!drs.get(t.id, existed, cur)) { Log("[NvidiaTweaks] %s", drs.error.c_str()); continue; }
        if (existed && cur == t.value) { Log("[NvidiaTweaks] %s (already set)", t.label); continue; }

        Snapshot::Entry e = { { "kind", "drs" }, { "settingId", t.id }, { "existed", existed } };
        if (existed) e["value"] = cur;
        if (!Snapshot::record(e)) {
            Log("[NvidiaTweaks] snapshot write failed; %s not changed", t.flagName);
            continue;
        }
        if (drs.set(t.id, t.value)) { Log("[NvidiaTweaks] %s", t.label); ++changed; }
        else Log("[NvidiaTweaks] %s: %s", t.flagName, drs.error.c_str());
    }
    if (changed && !drs.save()) Log("[NvidiaTweaks] %s", drs.error.c_str());
    Log("[NvidiaTweaks] Done (%d setting(s) changed).", changed);
}

void NvidiaTweaks::releaseRuntime() {
    if (!g_initialized) return;
    NvAPI_Unload();
    g_initialized = false;
}

bool NvidiaTweaks::ownsKind(const std::string& k) { return k == "drs"; }

bool NvidiaTweaks::restoreEntry(const Snapshot::Entry& e, std::string& error) {
    NvU32 id = e.value("settingId", 0u);
    if (!isKnownId(id)) { error = "unexpected DRS setting id"; return false; }
    if (!ensureInit())  { error = "NVAPI unavailable"; return false; }

    Drs drs;
    bool ok = drs.open();
    if (ok) {
        ok = e.value("existed", false) ? drs.set(id, e.value("value", 0u)) : drs.remove(id);
        if (ok) ok = drs.save();
    }
    if (!ok) { error = drs.error; return false; }
    Log("[NvidiaTweaks] DRS 0x%08X restored", id);
    return true;
}

void NvidiaTweaks::dump() {
    if (!ensureInit()) return;
    Drs drs;
    if (!drs.open()) { Log("drs.error = %s", drs.error.c_str()); return; }
    for (const auto& t : kTargets) {
        bool existed = false;
        NvU32 v = 0;
        if (drs.get(t.id, existed, v))
            Log("drs.0x%08X = %s%u", t.id, existed ? "" : "default:", v);
    }
}

} // namespace PeakMode
