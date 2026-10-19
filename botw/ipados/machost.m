// Mac test host of the embedding API (Cemu-Switch, BOTW-iPad): hosts .NET via hostfxr, or loads the NativeAOT dylib and runs BOTW-PC's Switch runtime
// into this window's CAMetalLayer. Usage: switchhost <dotnet root | Ryujinx.dylib> <managed dir> <data dir> <game.nsp> [seconds]
#import <Cocoa/Cocoa.h>
#import <QuartzCore/CAMetalLayer.h>
#import <Metal/Metal.h>
#include <dlfcn.h>
#include <stdint.h>
#include "SignalHandler.h"

typedef void* hostfxr_handle;
typedef struct { size_t size; const char* host_path; const char* dotnet_root; } hostfxr_initialize_parameters;
typedef int (*init_cmdline_fn)(int, const char**, const hostfxr_initialize_parameters*, hostfxr_handle*);
typedef int (*get_delegate_fn)(hostfxr_handle, int, void**);
typedef int (*get_function_pointer_fn)(const char*, const char*, const char*, void*, void*, void**);
typedef struct { const char* dataDir; const char* gamePath; void* nsView; void* metalLayer; int32_t width; int32_t height; void* runOnMainThread; } StartArgs;
static void runOnMainThread(void (*fn)(void*), void* ctx) { if ([NSThread isMainThread]) fn(ctx); else dispatch_sync(dispatch_get_main_queue(), ^{ fn(ctx); }); }
typedef struct { double fps; double frameMs; double gameSeconds; int32_t running; } Stats;
enum { hdt_get_function_pointer = 6 };
#define UNMANAGEDCALLERSONLY_METHOD ((const char*)-1)

int main(int argc, const char** argv) {
  @autoreleasepool {
    if (argc < 5) { fprintf(stderr, "usage: switchhost <dotnet root> <managed dir> <data dir> <game> [seconds]\n"); return 2; }
    NSString* dotnetRoot = @(argv[1]); NSString* managed = @(argv[2]);
    int seconds = argc > 5 ? atoi(argv[5]) : 120;
    [NSApplication sharedApplication];
    [NSApp setActivationPolicy:NSApplicationActivationPolicyRegular];
    NSWindow* window = [[NSWindow alloc] initWithContentRect:NSMakeRect(100, 100, 1280, 720)
        styleMask:NSWindowStyleMaskTitled | NSWindowStyleMaskClosable | NSWindowStyleMaskResizable backing:NSBackingStoreBuffered defer:NO];
    window.title = @"Cemu-Switch embedding test";
    NSView* view = window.contentView; view.wantsLayer = YES;
    CAMetalLayer* layer = [CAMetalLayer layer]; layer.device = MTLCreateSystemDefaultDevice();
    layer.contentsScale = window.backingScaleFactor; view.layer = layer;
    [window makeKeyAndOrderFront:nil]; [NSApp activateIgnoringOtherApps:YES];

    int (*start)(StartArgs*) = NULL; void (*stop)(void) = NULL; void (*getStats)(Stats*) = NULL;
    if ([dotnetRoot hasSuffix:@".dylib"]) {
      // NativeAOT build of the runtime: plain C exports, no .NET host.
      void* lib = dlopen(dotnetRoot.UTF8String, RTLD_NOW | RTLD_LOCAL);
      if (!lib) { fprintf(stderr, "dlopen: %s\n", dlerror()); return 1; }
      start = dlsym(lib, "botwpc_start"); stop = dlsym(lib, "botwpc_stop"); getStats = dlsym(lib, "botwpc_get_stats");
      if (!start || !stop || !getStats) { fprintf(stderr, "missing exports\n"); return 1; }
      // BOTWPC_AOT_DYLIB: ahead-of-time image (aot_build.py output linked as a dylib); BOTWPC_NO_JIT=1 forbids
      // generated code, as on an iPad without a debugger. The memory-tracking handler then comes from SignalHandler.c.
      const char* aotPath = getenv("BOTWPC_AOT_DYLIB");
      void (*setAot)(void*, void*, int, void*, int) = dlsym(lib, "botwpc_set_aot_image");
      void (*setSignal)(void*, void*) = dlsym(lib, "botwpc_set_signal_handler");
      int noJit = getenv("BOTWPC_NO_JIT") && getenv("BOTWPC_NO_JIT")[0] == '1';
      if (aotPath || noJit) {
        void* aot = aotPath ? dlopen(aotPath, RTLD_NOW | RTLD_LOCAL) : NULL;
        if (aotPath && !aot) { fprintf(stderr, "dlopen aot: %s\n", dlerror()); return 1; }
        setAot(aot ? dlsym(aot, "botwpc_aot_code") : NULL, aot ? dlsym(aot, "botwpc_aot_entries") : NULL,
               aot ? *(int*)dlsym(aot, "botwpc_aot_count") : 0, aot ? dlsym(aot, "botwpc_aot_stubs") : NULL, !noJit);
        void (*setSimd)(void*, unsigned, int) = dlsym(lib, "botwpc_set_aot_simd");
        if (aot && dlsym(aot, "botwpc_aot_simd_count"))
          setSimd(dlsym(aot, "botwpc_aot_code"), *(unsigned*)dlsym(aot, "botwpc_aot_simd_offset"), *(int*)dlsym(aot, "botwpc_aot_simd_count"));
        setSignal((void*)botw_signal_handler, (void*)botw_signal_set_config);
      }
    } else {
      NSString* fxrDir = [dotnetRoot stringByAppendingPathComponent:@"host/fxr"];
      NSString* ver = [[[NSFileManager defaultManager] contentsOfDirectoryAtPath:fxrDir error:nil] firstObject];
      NSString* fxr = [[fxrDir stringByAppendingPathComponent:ver] stringByAppendingPathComponent:@"libhostfxr.dylib"];
      void* lib = dlopen(fxr.UTF8String, RTLD_NOW | RTLD_LOCAL);
      if (!lib) { fprintf(stderr, "dlopen hostfxr: %s\n", dlerror()); return 1; }
      init_cmdline_fn init = (init_cmdline_fn)dlsym(lib, "hostfxr_initialize_for_dotnet_command_line");
      get_delegate_fn getDelegate = (get_delegate_fn)dlsym(lib, "hostfxr_get_runtime_delegate");
      NSString* app = [managed stringByAppendingPathComponent:@"Ryujinx.dll"];
      const char* appArgv[] = { app.UTF8String };
      hostfxr_initialize_parameters params = { sizeof(params), app.UTF8String, dotnetRoot.UTF8String };
      hostfxr_handle ctx = NULL;
      int rc = init(1, appArgv, &params, &ctx);
      if (rc != 0 || !ctx) { fprintf(stderr, "hostfxr init failed: 0x%x\n", rc); return 1; }
      get_function_pointer_fn getFn = NULL;
      rc = getDelegate(ctx, hdt_get_function_pointer, (void**)&getFn);
      if (rc != 0) { fprintf(stderr, "get delegate failed: 0x%x\n", rc); return 1; }
      const char* type = "Ryujinx.Headless.CemuSwitchEntry, Ryujinx";
      rc = getFn(type, "Start", UNMANAGEDCALLERSONLY_METHOD, NULL, NULL, (void**)&start);
      rc |= getFn(type, "Stop", UNMANAGEDCALLERSONLY_METHOD, NULL, NULL, (void**)&stop);
      rc |= getFn(type, "GetStats", UNMANAGEDCALLERSONLY_METHOD, NULL, NULL, (void**)&getStats);
      if (rc != 0) { fprintf(stderr, "get function pointers failed: 0x%x\n", rc); return 1; }
    }
    NSSize px = [view convertSizeToBacking:view.bounds.size];
    StartArgs sa = { argv[3], argv[4], (__bridge void*)view, (__bridge void*)layer, (int32_t)px.width, (int32_t)px.height, (void*)runOnMainThread };
    printf("start -> %d\n", start(&sa)); fflush(stdout);
    NSDate* end = [NSDate dateWithTimeIntervalSinceNow:seconds];
    while ([end timeIntervalSinceNow] > 0) {
      NSEvent* e;
      while ((e = [NSApp nextEventMatchingMask:NSEventMaskAny untilDate:[NSDate dateWithTimeIntervalSinceNow:0.05] inMode:NSDefaultRunLoopMode dequeue:YES])) [NSApp sendEvent:e];
      static int tick = 0;
      if (++tick % 100 == 0) { Stats s; getStats(&s); printf("stats running=%d fps=%.1f frame=%.2fms game_s=%.1f\n", s.running, s.fps, s.frameMs, s.gameSeconds); fflush(stdout); }
    }
    stop();
    for (int i = 0; i < 600; i++) {
      NSEvent* e; while ((e = [NSApp nextEventMatchingMask:NSEventMaskAny untilDate:[NSDate dateWithTimeIntervalSinceNow:0.05] inMode:NSDefaultRunLoopMode dequeue:YES])) [NSApp sendEvent:e];
      Stats s; getStats(&s); if (!s.running) break;
    }
    printf("stopped\n");
  }
  return 0;
}
// Build: clang -fobjc-arc -O1 -I botw/ipados -framework Cocoa -framework QuartzCore -framework Metal botw/ipados/machost.m \
//   botw/ipados/SignalHandler.c -o machost   (copy next to Ryujinx.dylib so its dependencies resolve)
