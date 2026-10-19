// BOTW-PC NGX bridge: a small flat C API over the NVIDIA DLSS (NGX) Vulkan SDK so managed code
// (Ryujinx, C#) can use DLSS Super Resolution through P/Invoke without NVIDIA headers.
// SPDX-License-Identifier: MIT
// The NVIDIA SDK itself is NOT part of this source; it is linked from a local download (see build.sh).
#pragma once

#include <stdint.h>
#include <vulkan/vulkan.h>

#ifdef __cplusplus
extern "C" {
#endif

#ifdef _WIN32
#define BOTW_NGX_API __declspec(dllexport)
#else
#define BOTW_NGX_API __attribute__((visibility("default")))
#endif

typedef struct BotwNgxImage {
    VkImage image;
    VkImageView view;
    VkFormat format;
    uint32_t width;
    uint32_t height;
} BotwNgxImage;

// DLSS quality modes, values match NVSDK_NGX_PerfQuality_Value.
enum {
    BOTW_NGX_QUALITY_PERFORMANCE = 0,
    BOTW_NGX_QUALITY_BALANCED = 1,
    BOTW_NGX_QUALITY_QUALITY = 2,
    BOTW_NGX_QUALITY_ULTRA_PERFORMANCE = 3,
    BOTW_NGX_QUALITY_DLAA = 5,
};

// Create flags, values match NVSDK_NGX_DLSS_Feature_Flags.
enum {
    BOTW_NGX_FLAG_HDR = 1 << 0,
    BOTW_NGX_FLAG_MV_LOW_RES = 1 << 1,
    BOTW_NGX_FLAG_MV_JITTERED = 1 << 2,
    BOTW_NGX_FLAG_DEPTH_INVERTED = 1 << 3,
    BOTW_NGX_FLAG_AUTO_EXPOSURE = 1 << 6,
};

// All functions return an NVSDK_NGX_Result code (1 = success; failures have the 0xBAD00000 bit set)
// unless stated otherwise. botw_ngx_result_string() turns it into text.

// Must be called first. snippetDir holds libnvidia-ngx-dlss.so.* (Windows: nvngx_dlss.dll); dataDir must be writable (NGX logs/cache).
BOTW_NGX_API void botw_ngx_configure(const char* snippetDir, const char* dataDir);

// Extensions NGX needs. Call before creating the VkInstance / VkDevice and enable them.
// The returned array stays valid until the next call of the same function.
BOTW_NGX_API int botw_ngx_instance_extensions(const char*** names, uint32_t* count);
BOTW_NGX_API int botw_ngx_device_extensions(VkInstance instance, VkPhysicalDevice physicalDevice, const char*** names, uint32_t* count);

// Initializes NGX on an existing device (created with the extensions above).
// Afterwards *available is 1 if DLSS Super Resolution can be used on this device/driver.
BOTW_NGX_API int botw_ngx_init(VkInstance instance, VkPhysicalDevice physicalDevice, VkDevice device,
                               PFN_vkGetInstanceProcAddr gipa, PFN_vkGetDeviceProcAddr gdpa, int* available);

// Recommended render resolution for an output size and quality mode.
BOTW_NGX_API int botw_ngx_optimal_render_size(uint32_t outWidth, uint32_t outHeight, int quality,
                                              uint32_t* renderWidth, uint32_t* renderHeight);

// Creates (replacing any previous) the DLSS feature. Records into cmd; submit and wait before evaluating.
BOTW_NGX_API int botw_ngx_create(VkCommandBuffer cmd, uint32_t renderWidth, uint32_t renderHeight,
                                 uint32_t outWidth, uint32_t outHeight, int quality, int flags);

// Records DLSS evaluation into cmd. Inputs must be in VK_IMAGE_LAYOUT_GENERAL with SAMPLED usage;
// output in GENERAL with STORAGE usage.
// motionVectors: previous-frame position minus current position, in render pixels (scaled by mvScale).
// sampleOffsetX/Y: where this frame was sampled relative to the pixel centre, in render pixels ([-0.5, 0.5]);
// the bridge converts it to NGX's jitter convention (negated; verified by test_ngx_bridge).
// biasCurrentColorMask: optional (NULL), render size, 1 = no valid history, use the current frame (e.g. disocclusion).
BOTW_NGX_API int botw_ngx_evaluate(VkCommandBuffer cmd, const BotwNgxImage* color, const BotwNgxImage* depth,
                                   const BotwNgxImage* motionVectors, const BotwNgxImage* output,
                                   const BotwNgxImage* biasCurrentColorMask,
                                   float sampleOffsetX, float sampleOffsetY, float mvScaleX, float mvScaleY,
                                   int reset, float frameTimeMs);

BOTW_NGX_API void botw_ngx_release(void);
BOTW_NGX_API void botw_ngx_shutdown(VkDevice device);

BOTW_NGX_API const char* botw_ngx_result_string(int result);

#ifdef __cplusplus
}
#endif
