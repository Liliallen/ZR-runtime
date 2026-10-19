// BOTW-PC NGX bridge implementation. SPDX-License-Identifier: MIT
#include "ngx_bridge.h"

#include <nvsdk_ngx_helpers.h>
#include <nvsdk_ngx_helpers_vk.h>
#include <nvsdk_ngx_vk.h>

#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <string>
#include <vector>

namespace {

// Arbitrary but fixed project id; NGX requires one for custom engines.
constexpr const char* kProjectId = "9b2c1f4e-6a7d-4e8b-a1c3-5f0d2e7b9c41";
constexpr const char* kEngineVersion = "1.0";

NVSDK_NGX_Parameter* g_capabilities = nullptr;
NVSDK_NGX_Parameter* g_params = nullptr;
NVSDK_NGX_Handle* g_feature = nullptr;

std::vector<const char*> g_instanceExtensions;
std::vector<const char*> g_deviceExtensions;
std::vector<std::wstring> g_snippetPathsW;
std::vector<const wchar_t*> g_snippetPathPtrs;
std::wstring g_dataPathW = L".";
NVSDK_NGX_FeatureCommonInfo g_commonInfo = {};

void NVSDK_CONV LogCallback(const char* message, NVSDK_NGX_Logging_Level, NVSDK_NGX_Feature)
{
    std::fprintf(stderr, "[NGX] %s", message);
}

std::wstring Widen(const char* s)
{
    std::wstring w;
    for (; s && *s; ++s) {
        w.push_back(static_cast<unsigned char>(*s));  // paths are expected to be ASCII
    }
    return w;
}

NVSDK_NGX_FeatureDiscoveryInfo DiscoveryInfo()
{
    NVSDK_NGX_FeatureDiscoveryInfo info = {};
    info.SDKVersion = NVSDK_NGX_Version_API;
    info.FeatureID = NVSDK_NGX_Feature_SuperSampling;
    info.Identifier.IdentifierType = NVSDK_NGX_Application_Identifier_Type_Project_Id;
    info.Identifier.v.ProjectDesc.ProjectId = kProjectId;
    info.Identifier.v.ProjectDesc.EngineType = NVSDK_NGX_ENGINE_TYPE_CUSTOM;
    info.Identifier.v.ProjectDesc.EngineVersion = kEngineVersion;
    info.ApplicationDataPath = g_dataPathW.c_str();  // NGX dereferences this even for queries
    info.FeatureInfo = &g_commonInfo;
    return info;
}

NVSDK_NGX_Resource_VK ToResource(const BotwNgxImage* img, bool readWrite)
{
    VkImageSubresourceRange range = {VK_IMAGE_ASPECT_COLOR_BIT, 0, 1, 0, 1};
    return NVSDK_NGX_Create_ImageView_Resource_VK(img->view, img->image, range, img->format, img->width, img->height, readWrite);
}

}  // namespace

extern "C" {

void botw_ngx_configure(const char* snippetDir, const char* dataDir)
{
    g_snippetPathsW = {Widen(snippetDir)};
    g_snippetPathPtrs = {g_snippetPathsW[0].c_str()};
    g_dataPathW = Widen(dataDir);
    g_commonInfo = {};
    g_commonInfo.PathListInfo.Path = g_snippetPathPtrs.data();
    g_commonInfo.PathListInfo.Length = 1;
    g_commonInfo.LoggingInfo.LoggingCallback = LogCallback;
    // BOTWPC_NGX_LOG=1: NGX's verbose log (stderr and NGX's own log files in the data directory), e.g. to confirm the
    // render preset NGX selected.
    const char* log = std::getenv("BOTWPC_NGX_LOG");
    g_commonInfo.LoggingInfo.MinimumLoggingLevel = log && log[0] == '1' ? NVSDK_NGX_LOGGING_LEVEL_VERBOSE : NVSDK_NGX_LOGGING_LEVEL_OFF;
}

int botw_ngx_instance_extensions(const char*** names, uint32_t* count)
{
    NVSDK_NGX_FeatureDiscoveryInfo info = DiscoveryInfo();
    uint32_t n = 0;
    VkExtensionProperties* props = nullptr;
    NVSDK_NGX_Result r = NVSDK_NGX_VULKAN_GetFeatureInstanceExtensionRequirements(&info, &n, &props);
    g_instanceExtensions.clear();
    for (uint32_t i = 0; NVSDK_NGX_SUCCEED(r) && i < n; ++i) {
        g_instanceExtensions.push_back(props[i].extensionName);
    }
    *names = g_instanceExtensions.data();
    *count = static_cast<uint32_t>(g_instanceExtensions.size());
    return r;
}

int botw_ngx_device_extensions(VkInstance instance, VkPhysicalDevice physicalDevice, const char*** names, uint32_t* count)
{
    NVSDK_NGX_FeatureDiscoveryInfo info = DiscoveryInfo();
    uint32_t n = 0;
    VkExtensionProperties* props = nullptr;
    NVSDK_NGX_Result r = NVSDK_NGX_VULKAN_GetFeatureDeviceExtensionRequirements(instance, physicalDevice, &info, &n, &props);
    g_deviceExtensions.clear();
    for (uint32_t i = 0; NVSDK_NGX_SUCCEED(r) && i < n; ++i) {
        g_deviceExtensions.push_back(props[i].extensionName);
    }
    *names = g_deviceExtensions.data();
    *count = static_cast<uint32_t>(g_deviceExtensions.size());
    return r;
}

int botw_ngx_init(VkInstance instance, VkPhysicalDevice physicalDevice, VkDevice device,
                  PFN_vkGetInstanceProcAddr gipa, PFN_vkGetDeviceProcAddr gdpa, int* available)
{
    *available = 0;

    NVSDK_NGX_Result r = NVSDK_NGX_VULKAN_Init_with_ProjectID(kProjectId, NVSDK_NGX_ENGINE_TYPE_CUSTOM, kEngineVersion,
                                                              g_dataPathW.c_str(), instance, physicalDevice, device, gipa, gdpa,
                                                              &g_commonInfo, NVSDK_NGX_Version_API);
    if (NVSDK_NGX_FAILED(r)) {
        return r;
    }

    r = NVSDK_NGX_VULKAN_GetCapabilityParameters(&g_capabilities);
    if (NVSDK_NGX_FAILED(r)) {
        return r;  // drivers too old for this API are not supported
    }

    int supported = 0;
    int needsDriver = 0;
    NVSDK_NGX_Parameter_GetI(g_capabilities, NVSDK_NGX_Parameter_SuperSampling_Available, &supported);
    NVSDK_NGX_Parameter_GetI(g_capabilities, NVSDK_NGX_Parameter_SuperSampling_NeedsUpdatedDriver, &needsDriver);
    *available = supported && !needsDriver;

    return NVSDK_NGX_VULKAN_AllocateParameters(&g_params);
}

int botw_ngx_optimal_render_size(uint32_t outWidth, uint32_t outHeight, int quality, uint32_t* renderWidth, uint32_t* renderHeight)
{
    unsigned int w = 0, h = 0, maxW = 0, maxH = 0, minW = 0, minH = 0;
    float sharpness = 0;
    NVSDK_NGX_Result r = NGX_DLSS_GET_OPTIMAL_SETTINGS(g_capabilities, outWidth, outHeight, static_cast<NVSDK_NGX_PerfQuality_Value>(quality),
                                                       &w, &h, &maxW, &maxH, &minW, &minH, &sharpness);
    *renderWidth = w;
    *renderHeight = h;
    return r;
}

int botw_ngx_create(VkCommandBuffer cmd, uint32_t renderWidth, uint32_t renderHeight, uint32_t outWidth, uint32_t outHeight, int quality, int flags)
{
    botw_ngx_release();

    NVSDK_NGX_DLSS_Create_Params create = {};
    create.Feature.InWidth = renderWidth;
    create.Feature.InHeight = renderHeight;
    create.Feature.InTargetWidth = outWidth;
    create.Feature.InTargetHeight = outHeight;
    create.Feature.InPerfQualityValue = static_cast<NVSDK_NGX_PerfQuality_Value>(quality);
    create.InFeatureCreateFlags = flags;

    // Optional render-preset override for every mode (BOTWPC_DLSS_PRESET=J|K|L|M, SDK 310 letters); unset keeps NGX's
    // default per mode (K for DLAA/Quality/Balanced, M for Performance).
    const char* preset = std::getenv("BOTWPC_DLSS_PRESET");
    if (preset && preset[0] >= 'A' && preset[0] <= 'O' && preset[1] == 0) {
        unsigned int value = static_cast<unsigned int>(preset[0] - 'A' + 1);  // A = 1 ... K = 11, M = 13
        for (const char* name : {NVSDK_NGX_Parameter_DLSS_Hint_Render_Preset_DLAA, NVSDK_NGX_Parameter_DLSS_Hint_Render_Preset_Quality,
                                 NVSDK_NGX_Parameter_DLSS_Hint_Render_Preset_Balanced, NVSDK_NGX_Parameter_DLSS_Hint_Render_Preset_Performance,
                                 NVSDK_NGX_Parameter_DLSS_Hint_Render_Preset_UltraPerformance}) {
            NVSDK_NGX_Parameter_SetUI(g_params, name, value);
        }
    }

    return NGX_VULKAN_CREATE_DLSS_EXT(cmd, 1, 1, &g_feature, g_params, &create);
}

int botw_ngx_evaluate(VkCommandBuffer cmd, const BotwNgxImage* color, const BotwNgxImage* depth,
                      const BotwNgxImage* motionVectors, const BotwNgxImage* output, const BotwNgxImage* biasCurrentColorMask,
                      float sampleOffsetX, float sampleOffsetY, float mvScaleX, float mvScaleY, int reset, float frameTimeMs)
{
    if (!g_feature) {
        return NVSDK_NGX_Result_FAIL_FeatureNotFound;
    }

    NVSDK_NGX_Resource_VK colorRes = ToResource(color, false);
    NVSDK_NGX_Resource_VK depthRes = ToResource(depth, false);
    NVSDK_NGX_Resource_VK mvRes = ToResource(motionVectors, false);
    NVSDK_NGX_Resource_VK outRes = ToResource(output, true);
    NVSDK_NGX_Resource_VK maskRes = biasCurrentColorMask ? ToResource(biasCurrentColorMask, false) : NVSDK_NGX_Resource_VK{};

    NVSDK_NGX_VK_DLSS_Eval_Params eval = {};
    eval.Feature.pInColor = &colorRes;
    eval.Feature.pInOutput = &outRes;
    eval.pInDepth = &depthRes;
    eval.pInMotionVectors = &mvRes;
    eval.pInBiasCurrentColorMask = biasCurrentColorMask ? &maskRes : nullptr;
    // NGX expects the negated sample offset (measured: the opposite sign loses ~8 dB in test_ngx_bridge).
    eval.InJitterOffsetX = -sampleOffsetX;
    eval.InJitterOffsetY = -sampleOffsetY;
    eval.InRenderSubrectDimensions = {color->width, color->height};
    eval.InReset = reset;
    eval.InMVScaleX = mvScaleX;
    eval.InMVScaleY = mvScaleY;
    eval.InFrameTimeDeltaInMsec = frameTimeMs;

    return NGX_VULKAN_EVALUATE_DLSS_EXT(cmd, g_feature, g_params, &eval);
}

void botw_ngx_release(void)
{
    if (g_feature) {
        NVSDK_NGX_VULKAN_ReleaseFeature(g_feature);
        g_feature = nullptr;
    }
}

void botw_ngx_shutdown(VkDevice device)
{
    botw_ngx_release();
    if (g_params) {
        NVSDK_NGX_VULKAN_DestroyParameters(g_params);
        g_params = nullptr;
    }
    if (g_capabilities) {
        NVSDK_NGX_VULKAN_DestroyParameters(g_capabilities);
        g_capabilities = nullptr;
    }
    NVSDK_NGX_VULKAN_Shutdown1(device);
}

const char* botw_ngx_result_string(int result)
{
    static thread_local char buf[256];
    const wchar_t* w = GetNGXResultAsString(static_cast<NVSDK_NGX_Result>(result));
    size_t i = 0;
    for (; w && w[i] && i + 1 < sizeof(buf); ++i) {
        buf[i] = static_cast<char>(w[i]);
    }
    buf[i] = 0;
    return buf;
}

}  // extern "C"
