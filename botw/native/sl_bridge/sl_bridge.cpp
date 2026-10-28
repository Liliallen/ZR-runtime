// BOTW-PC Streamline bridge implementation. SPDX-License-Identifier: MIT
#include "sl_bridge.h"

#include <sl.h>
#include <sl_consts.h>
#include <sl_dlss_g.h>
#include <sl_helpers.h>
#include <sl_pcl.h>
#include <sl_reflex.h>

#include <cstring>
#include <mutex>

namespace {

// Same fixed project id as the NGX bridge; NGX requires one for custom engines.
constexpr const char* kProjectId = "9b2c1f4e-6a7d-4e8b-a1c3-5f0d2e7b9c41";
constexpr const char* kEngineVersion = "1.0";

const sl::ViewportHandle kViewport{0};

BotwSlLogCallback g_log = nullptr;
sl::FrameToken* g_frame = nullptr;
sl::DLSSGOptions g_options{};
bool g_initialised = false;

void LogMessage(sl::LogType type, const char* message)
{
    if (g_log) {
        g_log(static_cast<int>(type), message);
    }
}

void Copy(sl::float4x4& dst, const float* src)
{
    std::memcpy(&dst, src, sizeof(float) * 16);
}

sl::Resource ToResource(const BotwSlImage* img)
{
    sl::Resource r(sl::ResourceType::eTex2d, reinterpret_cast<void*>(img->image), nullptr,
                   reinterpret_cast<void*>(img->view), img->layout);
    r.width = img->width;
    r.height = img->height;
    r.nativeFormat = static_cast<uint32_t>(img->format);
    r.mipLevels = 1;
    r.arrayLayers = 1;
    // Ryujinx creates every texture with these flags (TextureStorage).
    r.flags = VK_IMAGE_CREATE_MUTABLE_FORMAT_BIT | VK_IMAGE_CREATE_EXTENDED_USAGE_BIT;
    r.usage = img->usage;
    return r;
}

sl::Extent ToExtent(const BotwSlImage* img)
{
    sl::Extent e{};
    e.left = img->x;
    e.top = img->y;
    e.width = img->w != 0 ? img->w : img->width;
    e.height = img->h != 0 ? img->h : img->height;
    return e;
}

}  // namespace

BOTW_SL_API int botw_sl_init(const wchar_t* pluginDir, const wchar_t* logDir, int verbose, BotwSlLogCallback log)
{
    g_log = log;

    static const sl::Feature features[] = {sl::kFeatureDLSS_G, sl::kFeatureReflex, sl::kFeaturePCL};
    static const wchar_t* paths[1];
    paths[0] = pluginDir;

    sl::Preferences pref{};
    pref.showConsole = false;
    pref.logLevel = verbose ? sl::LogLevel::eVerbose : sl::LogLevel::eDefault;
    pref.pathsToPlugins = paths;
    pref.numPathsToPlugins = pluginDir ? 1 : 0;
    pref.pathToLogsAndData = logDir;
    pref.logMessageCallback = LogMessage;
    // No over-the-air plugin downloads: the plugins next to the bridge are the ones that were tested.
    pref.flags = sl::PreferenceFlags::eDisableCLStateTracking | sl::PreferenceFlags::eUseFrameBasedResourceTagging;
    pref.featuresToLoad = features;
    pref.numFeaturesToLoad = 3;
    pref.engine = sl::EngineType::eCustom;
    pref.engineVersion = kEngineVersion;
    pref.projectId = kProjectId;
    pref.renderAPI = sl::RenderAPI::eVulkan;

    sl::Result r = slInit(pref);
    g_initialised = r == sl::Result::eOk;
    return static_cast<int>(r);
}

BOTW_SL_API int botw_sl_shutdown(void)
{
    if (!g_initialised) {
        return 0;
    }

    g_initialised = false;
    return static_cast<int>(slShutdown());
}

BOTW_SL_API int botw_sl_check(VkPhysicalDevice physicalDevice)
{
    sl::AdapterInfo adapter{};
    adapter.vkPhysicalDevice = physicalDevice;

    sl::Result r = slIsFeatureSupported(sl::kFeatureDLSS_G, adapter);
    if (r != sl::Result::eOk) {
        return static_cast<int>(r);
    }

    bool loaded = false;
    slIsFeatureLoaded(sl::kFeatureDLSS_G, loaded);
    return loaded ? 0 : static_cast<int>(sl::Result::eErrorFeatureNotSupported);
}

BOTW_SL_API int botw_sl_set_options(int mode, uint32_t framesToGenerate)
{
    g_options.mode = mode != 0 ? sl::DLSSGMode::eOn : sl::DLSSGMode::eOff;
    g_options.numFramesToGenerate = framesToGenerate < 1 ? 1 : framesToGenerate;
    g_options.flags = sl::DLSSGFlags::eRetainResourcesWhenOff;
    return static_cast<int>(slDLSSGSetOptions(kViewport, g_options));
}

BOTW_SL_API int botw_sl_set_reflex(int mode, uint32_t frameLimitUs)
{
    sl::ReflexOptions options{};
    options.mode = mode == 2 ? sl::ReflexMode::eLowLatencyWithBoost : mode == 1 ? sl::ReflexMode::eLowLatency : sl::ReflexMode::eOff;
    options.frameLimitUs = frameLimitUs;
    return static_cast<int>(slReflexSetOptions(options));
}

BOTW_SL_API int botw_sl_begin_frame(uint32_t index)
{
    sl::Result r = slGetNewFrameToken(g_frame, &index);
    if (r != sl::Result::eOk) {
        g_frame = nullptr;
        return static_cast<int>(r);
    }

    // Reflex sleep (low-latency pacing and the frame-rate limiter) belongs right before the frame starts; Streamline
    // expects it every frame even with Reflex off.
    slReflexSleep(*g_frame);
    return static_cast<int>(slPCLSetMarker(sl::PCLMarker::eSimulationStart, *g_frame));
}

BOTW_SL_API int botw_sl_marker(int marker)
{
    if (!g_frame) {
        return static_cast<int>(sl::Result::eErrorMissingInputParameter);
    }

    return static_cast<int>(slPCLSetMarker(static_cast<sl::PCLMarker>(marker), *g_frame));
}

BOTW_SL_API int botw_sl_set_constants(const BotwSlConstants* c)
{
    if (!g_frame) {
        return static_cast<int>(sl::Result::eErrorMissingInputParameter);
    }

    sl::Constants k{};
    Copy(k.cameraViewToClip, c->viewToClip);
    Copy(k.clipToCameraView, c->clipToView);
    Copy(k.clipToPrevClip, c->clipToPrevClip);
    Copy(k.prevClipToClip, c->prevClipToClip);
    // No lens distortion: identity.
    for (uint32_t i = 0; i < 4; i++) {
        k.clipToLensClip[i] = sl::float4(i == 0 ? 1.0f : 0.0f, i == 1 ? 1.0f : 0.0f, i == 2 ? 1.0f : 0.0f, i == 3 ? 1.0f : 0.0f);
    }
    k.jitterOffset = {c->jitterX, c->jitterY};
    k.mvecScale = {c->mvecScaleX, c->mvecScaleY};
    k.cameraPinholeOffset = {0.0f, 0.0f};
    k.cameraPos = {c->cameraPos[0], c->cameraPos[1], c->cameraPos[2]};
    k.cameraUp = {c->cameraUp[0], c->cameraUp[1], c->cameraUp[2]};
    k.cameraRight = {c->cameraRight[0], c->cameraRight[1], c->cameraRight[2]};
    k.cameraFwd = {c->cameraFwd[0], c->cameraFwd[1], c->cameraFwd[2]};
    k.cameraNear = c->cameraNear;
    k.cameraFar = c->cameraFar;
    k.cameraFOV = c->cameraFov;
    k.cameraAspectRatio = c->cameraAspect;
    k.depthInverted = c->depthInverted ? sl::Boolean::eTrue : sl::Boolean::eFalse;
    k.cameraMotionIncluded = sl::Boolean::eTrue;
    k.motionVectors3D = sl::Boolean::eFalse;
    k.reset = c->reset ? sl::Boolean::eTrue : sl::Boolean::eFalse;
    k.orthographicProjection = sl::Boolean::eFalse;
    k.motionVectorsDilated = sl::Boolean::eFalse;
    k.motionVectorsJittered = sl::Boolean::eFalse;

    return static_cast<int>(slSetConstants(k, *g_frame, kViewport));
}

BOTW_SL_API int botw_sl_tag(VkCommandBuffer commandBuffer, const BotwSlImage* depth, const BotwSlImage* mvec, const BotwSlImage* hudless)
{
    if (!g_frame) {
        return static_cast<int>(sl::Result::eErrorMissingInputParameter);
    }

    if (!depth || !mvec) {
        sl::ResourceTag tags[] = {
            sl::ResourceTag(nullptr, sl::kBufferTypeDepth, sl::ResourceLifecycle::eValidUntilPresent),
            sl::ResourceTag(nullptr, sl::kBufferTypeMotionVectors, sl::ResourceLifecycle::eValidUntilPresent),
            sl::ResourceTag(nullptr, sl::kBufferTypeHUDLessColor, sl::ResourceLifecycle::eValidUntilPresent),
        };
        return static_cast<int>(slSetTagForFrame(*g_frame, kViewport, tags, 3, commandBuffer));
    }

    sl::Resource d = ToResource(depth), m = ToResource(mvec), h{};
    sl::Extent de = ToExtent(depth), me = ToExtent(mvec), he{};
    sl::ResourceTag tags[3] = {
        sl::ResourceTag(&d, sl::kBufferTypeDepth, sl::ResourceLifecycle::eValidUntilPresent, &de),
        sl::ResourceTag(&m, sl::kBufferTypeMotionVectors, sl::ResourceLifecycle::eValidUntilPresent, &me),
        sl::ResourceTag(nullptr, sl::kBufferTypeHUDLessColor, sl::ResourceLifecycle::eValidUntilPresent),
    };

    if (hudless) {
        h = ToResource(hudless);
        he = ToExtent(hudless);
        tags[2] = sl::ResourceTag(&h, sl::kBufferTypeHUDLessColor, sl::ResourceLifecycle::eValidUntilPresent, &he);
    }

    return static_cast<int>(slSetTagForFrame(*g_frame, kViewport, tags, 3, commandBuffer));
}

BOTW_SL_API int botw_sl_get_state(uint32_t* status, uint32_t* framesPresented, uint32_t* maxFramesToGenerate, uint32_t* minSize)
{
    sl::DLSSGState state{};
    sl::Result r = slDLSSGGetState(kViewport, state, nullptr);
    *status = static_cast<uint32_t>(state.status);
    *framesPresented = state.numFramesActuallyPresented;
    *maxFramesToGenerate = state.numFramesToGenerateMax;
    *minSize = state.minWidthOrHeight;
    return static_cast<int>(r);
}

BOTW_SL_API const char* botw_sl_result_string(int result)
{
    return sl::getResultAsStr(static_cast<sl::Result>(result));
}
