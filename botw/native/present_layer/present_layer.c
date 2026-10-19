// Minimal Vulkan layer that logs a CLOCK_MONOTONIC timestamp per vkQueuePresentKHR, so different emulators can be
// measured the same way (presented frames). Enable with VK_LAYER_PATH=<dir with the json> and
// VK_INSTANCE_LAYERS=VK_LAYER_BOTWPC_present_log; output: $PRESENT_LOG (one "seconds" line per present).
#include <vulkan/vk_layer.h>
#include <pthread.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <time.h>

#define EXPORT __attribute__((visibility("default")))
#define MAX_OBJ 16

typedef struct { void *key; PFN_vkGetInstanceProcAddr gipa; } InstanceEntry;
typedef struct { void *key; PFN_vkGetDeviceProcAddr gdpa; PFN_vkQueuePresentKHR present; } DeviceEntry;
static InstanceEntry instances[MAX_OBJ];
static DeviceEntry devices[MAX_OBJ];
static pthread_mutex_t lock = PTHREAD_MUTEX_INITIALIZER;
static FILE *out;

static void *key_of(const void *handle) { return *(void **)handle; }

static InstanceEntry *find_instance(const void *h) {
    for (int i = 0; i < MAX_OBJ; i++) if (instances[i].key == key_of(h)) return &instances[i];
    return NULL;
}
static DeviceEntry *find_device(const void *h) {
    for (int i = 0; i < MAX_OBJ; i++) if (devices[i].key == key_of(h)) return &devices[i];
    return NULL;
}

static VKAPI_ATTR VkResult VKAPI_CALL layer_QueuePresentKHR(VkQueue queue, const VkPresentInfoKHR *info) {
    DeviceEntry *d = find_device(queue); // queues share the device's dispatch key
    struct timespec ts;
    clock_gettime(CLOCK_MONOTONIC, &ts);
    pthread_mutex_lock(&lock);
    if (out) { fprintf(out, "%ld.%09ld\n", (long)ts.tv_sec, ts.tv_nsec); fflush(out); }
    pthread_mutex_unlock(&lock);
    return d->present(queue, info);
}

static VKAPI_ATTR VkResult VKAPI_CALL layer_CreateInstance(const VkInstanceCreateInfo *ci, const VkAllocationCallbacks *a, VkInstance *inst) {
    VkLayerInstanceCreateInfo *chain = (VkLayerInstanceCreateInfo *)ci->pNext;
    while (chain && !(chain->sType == VK_STRUCTURE_TYPE_LOADER_INSTANCE_CREATE_INFO && chain->function == VK_LAYER_LINK_INFO))
        chain = (VkLayerInstanceCreateInfo *)chain->pNext;
    if (!chain) return VK_ERROR_INITIALIZATION_FAILED;
    PFN_vkGetInstanceProcAddr gipa = chain->u.pLayerInfo->pfnNextGetInstanceProcAddr;
    chain->u.pLayerInfo = chain->u.pLayerInfo->pNext;
    PFN_vkCreateInstance create = (PFN_vkCreateInstance)gipa(VK_NULL_HANDLE, "vkCreateInstance");
    VkResult r = create(ci, a, inst);
    if (r != VK_SUCCESS) return r;
    pthread_mutex_lock(&lock);
    for (int i = 0; i < MAX_OBJ; i++) if (!instances[i].key) { instances[i].key = key_of(*inst); instances[i].gipa = gipa; break; }
    if (!out) { const char *p = getenv("PRESENT_LOG"); out = fopen(p ? p : "/dev/null", "a"); }
    pthread_mutex_unlock(&lock);
    return r;
}

static VKAPI_ATTR VkResult VKAPI_CALL layer_CreateDevice(VkPhysicalDevice pd, const VkDeviceCreateInfo *ci, const VkAllocationCallbacks *a, VkDevice *dev) {
    VkLayerDeviceCreateInfo *chain = (VkLayerDeviceCreateInfo *)ci->pNext;
    while (chain && !(chain->sType == VK_STRUCTURE_TYPE_LOADER_DEVICE_CREATE_INFO && chain->function == VK_LAYER_LINK_INFO))
        chain = (VkLayerDeviceCreateInfo *)chain->pNext;
    if (!chain) return VK_ERROR_INITIALIZATION_FAILED;
    PFN_vkGetInstanceProcAddr gipa = chain->u.pLayerInfo->pfnNextGetInstanceProcAddr;
    PFN_vkGetDeviceProcAddr gdpa = chain->u.pLayerInfo->pfnNextGetDeviceProcAddr;
    chain->u.pLayerInfo = chain->u.pLayerInfo->pNext;
    PFN_vkCreateDevice create = (PFN_vkCreateDevice)gipa(VK_NULL_HANDLE, "vkCreateDevice");
    VkResult r = create(pd, ci, a, dev);
    if (r != VK_SUCCESS) return r;
    pthread_mutex_lock(&lock);
    for (int i = 0; i < MAX_OBJ; i++) if (!devices[i].key) {
        devices[i].key = key_of(*dev); devices[i].gdpa = gdpa;
        devices[i].present = (PFN_vkQueuePresentKHR)gdpa(*dev, "vkQueuePresentKHR"); break; }
    pthread_mutex_unlock(&lock);
    return r;
}

EXPORT VKAPI_ATTR PFN_vkVoidFunction VKAPI_CALL vkGetDeviceProcAddr(VkDevice dev, const char *name) {
    if (!strcmp(name, "vkGetDeviceProcAddr")) return (PFN_vkVoidFunction)vkGetDeviceProcAddr;
    if (!strcmp(name, "vkQueuePresentKHR")) return (PFN_vkVoidFunction)layer_QueuePresentKHR;
    DeviceEntry *d = find_device(dev);
    return d ? d->gdpa(dev, name) : NULL;
}

EXPORT VKAPI_ATTR PFN_vkVoidFunction VKAPI_CALL vkGetInstanceProcAddr(VkInstance inst, const char *name) {
    if (!strcmp(name, "vkGetInstanceProcAddr")) return (PFN_vkVoidFunction)vkGetInstanceProcAddr;
    if (!strcmp(name, "vkCreateInstance")) return (PFN_vkVoidFunction)layer_CreateInstance;
    if (!strcmp(name, "vkCreateDevice")) return (PFN_vkVoidFunction)layer_CreateDevice;
    if (!strcmp(name, "vkGetDeviceProcAddr")) return (PFN_vkVoidFunction)vkGetDeviceProcAddr;
    if (!strcmp(name, "vkQueuePresentKHR")) return (PFN_vkVoidFunction)layer_QueuePresentKHR;
    if (!inst) return NULL;
    InstanceEntry *e = find_instance(inst);
    return e ? e->gipa(inst, name) : NULL;
}
