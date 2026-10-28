// BOTW-PC Streamline bridge: a flat C API over NVIDIA Streamline (DLSS Frame Generation + Reflex + PCL) for P/Invoke
// from the Vulkan backend (Ryujinx.Graphics.Vulkan/DlssG.cs). SPDX-License-Identifier: MIT
#pragma once

#include <stdint.h>

#include <vulkan/vulkan.h>

#ifdef _WIN32
#define BOTW_SL_API extern "C" __declspec(dllexport)
#else
#define BOTW_SL_API extern "C" __attribute__((visibility("default")))
#endif

// One tagged image. layout is the VkImageLayout it is in when DLSS-G reads it (at present).
// (x, y, w, h) is the valid region (extent); w = 0 means the whole image.
struct BotwSlImage {
    VkImage image;
    VkImageView view;
    VkFormat format;
    uint32_t width, height;
    uint32_t usage;   // VkImageUsageFlags
    uint32_t layout;  // VkImageLayout
    uint32_t x, y, w, h;
};

// Per-frame camera constants. Matrices are row-major in Streamline's row-vector convention (v' = v * M) and contain no
// jitter. Everything is in the clip space of the tagged depth and motion vectors.
struct BotwSlConstants {
    float viewToClip[16];
    float clipToView[16];
    float clipToPrevClip[16];
    float prevClipToClip[16];
    float jitterX, jitterY;            // render pixels
    float mvecScaleX, mvecScaleY;      // motion vector buffer value * scale = [-1, 1] screen fraction
    float cameraPos[3], cameraUp[3], cameraRight[3], cameraFwd[3];
    float cameraNear, cameraFar, cameraFov, cameraAspect;
    int32_t depthInverted;
    int32_t reset;
};

// Log messages (type 0 info, 1 warning, 2 error), from any thread.
typedef void (*BotwSlLogCallback)(int type, const char* message);

// slInit for Vulkan with DLSS-G (+ Reflex, PCL). pluginDir holds sl.*.dll and nvngx_dlssg.dll; logDir may be null.
// Must be called before any Vulkan call made through sl.interposer.dll. Returns sl::Result (0 = ok).
BOTW_SL_API int botw_sl_init(const wchar_t* pluginDir, const wchar_t* logDir, int verbose, BotwSlLogCallback log);
BOTW_SL_API int botw_sl_shutdown(void);

// slIsFeatureSupported(DLSS-G) for the device's physical device; also loads the feature functions. 0 = supported.
BOTW_SL_API int botw_sl_check(VkPhysicalDevice physicalDevice);

// DLSS-G options: mode 0 off / 1 on; framesToGenerate 1..3 (2x..4x). Resources are retained when off.
BOTW_SL_API int botw_sl_set_options(int mode, uint32_t framesToGenerate);

// Reflex: mode 0 off / 1 low latency / 2 low latency + boost; frameLimitUs 0 = no cap.
BOTW_SL_API int botw_sl_set_reflex(int mode, uint32_t frameLimitUs);

// Starts frame `index` (new frame token), sleeps in Reflex (pacing / frame-rate limit) and marks simulation start.
BOTW_SL_API int botw_sl_begin_frame(uint32_t index);

// PCL marker for the current frame (sl::PCLMarker: 0 sim start, 1 sim end, 2 render submit start, 3 render submit end,
// 4 present start, 5 present end).
BOTW_SL_API int botw_sl_marker(int marker);

// Common constants for the current frame (once per frame).
BOTW_SL_API int botw_sl_set_constants(const BotwSlConstants* constants);

// Tags depth, motion vectors and (optional, may be null) hud-less colour for the current frame, valid until present.
// Passing null depth and mvec clears all tags (frame without valid inputs).
BOTW_SL_API int botw_sl_tag(VkCommandBuffer commandBuffer, const BotwSlImage* depth, const BotwSlImage* mvec, const BotwSlImage* hudless);

// DLSS-G state: status (sl::DLSSGStatus bits), frames presented per app frame since the last call, maximum frames to
// generate, minimum output width/height.
BOTW_SL_API int botw_sl_get_state(uint32_t* status, uint32_t* framesPresented, uint32_t* maxFramesToGenerate, uint32_t* minSize);

BOTW_SL_API const char* botw_sl_result_string(int result);
