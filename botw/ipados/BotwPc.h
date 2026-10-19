#pragma once
// C API of the BOTW-PC runtime (src/Ryujinx/Headless/CemuSwitchEntry.cs), exported by the NativeAOT Ryujinx.dylib.
#include <stdint.h>

typedef struct {
    const char* dataDir;     // UTF-8
    const char* gamePath;    // UTF-8
    void* view;              // UIView (NSView on macOS)
    void* metalLayer;        // CAMetalLayer
    int32_t width, height;   // drawable size in pixels
    void (*runOnMainThread)(void (*fn)(void*), void* ctx);
} BotwPcStartArgs;

typedef struct {
    double fps;
    double frameMs;
    double gameSeconds;
    int32_t running;
    int32_t frameRate;       // 30, 60, 90, 120; -1 unbounded; 0 not running yet
} BotwPcStats;

// Switch buttons for botwpc_touch (bit = Ryujinx GamepadButtonInputId).
enum {
    BotwPcA = 1, BotwPcB = 2, BotwPcX = 3, BotwPcY = 4, BotwPcLStick = 5, BotwPcRStick = 6, BotwPcL = 7, BotwPcR = 8,
    BotwPcZL = 9, BotwPcZR = 10, BotwPcUp = 11, BotwPcDown = 12, BotwPcLeft = 13, BotwPcRight = 14, BotwPcMinus = 15,
    BotwPcPlus = 16,
};

int botwpc_start(const BotwPcStartArgs* args);
void botwpc_stop(void);
void botwpc_set_size(int width, int height);
void botwpc_set_frame_rate(int fps);
void botwpc_set_paused(int paused); // 1 while the app is not active (iOS fails GPU work from the background)
void botwpc_get_stats(BotwPcStats* stats);
void botwpc_set_jit_prepare(int (*prepare)(void* address, size_t size));
void botwpc_touch(uint64_t buttons, float lx, float ly, float rx, float ry);
void botwpc_motion(float ax, float ay, float az, float gx, float gy, float gz);
void botwpc_set_rumble_callback(void (*callback)(float low, float high, uint32_t durationMs));
// Ahead-of-time image (NULL/0: none); jitAvailable = 0 when the process cannot run generated code. Before botwpc_start.
void botwpc_set_aot_image(const void* code, const void* entries, int count, const uint32_t* stubOffsets, int jitAvailable);
void botwpc_set_aot_simd(const void* code, uint32_t offset, int count); // interpreter FP/SIMD stubs of the image
void botwpc_set_signal_handler(void* handler, void (*setConfig)(void* config));
// Code missing from the AOT image without JIT: request(guestAddress) blocks until JIT is usable (1) or gives up (0).
void botwpc_set_jit_request(int (*request)(uint64_t guestAddress));
