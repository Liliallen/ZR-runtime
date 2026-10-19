#import "GameViewController.h"
#import "BotwPc.h"
#import "GameData.h"
#import "Jit.h"
#import "SettingsView.h"
#import "TouchControls.h"
#import "SignalHandler.h"
#import <CoreMotion/CoreMotion.h>
#import <GameController/GameController.h>
#import <QuartzCore/CAMetalLayer.h>
#import <sys/resource.h>
#import <mach/mach.h>
#import <Metal/Metal.h>
#import <mach/mach.h>
#import <os/proc.h>
#import <unistd.h>

@interface MetalView : UIView
@end
@implementation MetalView
+ (Class)layerClass { return [CAMetalLayer class]; }
@end

static void RunOnMainThread(void (*fn)(void*), void* ctx) {
    if (NSThread.isMainThread) fn(ctx);
    else dispatch_sync(dispatch_get_main_queue(), ^{ fn(ctx); });
}

static UIImpactFeedbackGenerator* g_haptics;

static void Rumble(float low, float high, uint32_t durationMs) {
    float strength = MAX(low, high);
    if (strength < 0.05f) return;
    dispatch_async(dispatch_get_main_queue(), ^{ [g_haptics impactOccurredWithIntensity:MIN(1.0, strength)]; });
}

#if BOTWPC_AOT
// Guest code compiled ahead of time and linked into the app (botw/ipados/aot_build.py, botw_aot.s).
extern const uint8_t botwpc_aot_code[];
extern const uint8_t botwpc_aot_entries[];
extern const int32_t botwpc_aot_count;
extern const uint32_t botwpc_aot_stubs[];
extern const uint32_t botwpc_aot_simd_offset;
extern const int32_t botwpc_aot_simd_count;
#endif

static __weak UILabel* g_message;

// A function missing from the AOT image while running without JIT: show why the game waits and continue as soon as
// JIT is enabled (StikDebug attaching sets CS_DEBUGGED at run time). Runs on a guest thread.
static int RequestJit(uint64_t guestAddress) {
    dispatch_async(dispatch_get_main_queue(), ^{
        g_message.text = [NSString stringWithFormat:@"This part of the game (0x%llx) is not compiled into the app.\n\nEnable JIT for "
                          @"BOTW-iPad with StikDebug to continue; the game resumes by itself.", guestAddress];
    });
    while (!jit_cs_debugged()) usleep(250000);
    dispatch_async(dispatch_get_main_queue(), ^{ g_message.text = @""; });
    return 1;
}

static double FootprintGB(void) {
    task_vm_info_data_t info;
    mach_msg_type_number_t count = TASK_VM_INFO_COUNT;
    return task_info(mach_task_self(), TASK_VM_INFO, (task_info_t)&info, &count) == KERN_SUCCESS ? info.phys_footprint / 1073741824.0 : 0;
}

@implementation GameViewController {
    MetalView* _gameView;
    TouchControls* _touch;
    SettingsView* _settings;
    UILabel* _stats;
    UILabel* _message;
    NSTimer* _timer;
    CMMotionManager* _motion;
    BOOL _started;
    double _lastGameSeconds;
    double _lastCpuSeconds;
    CFTimeInterval _lastTime;
    NSFileHandle* _statsLog;
    double _landscapeSign; // +1 landscape left, -1 landscape right (read by the motion queue)
    NSTimer* _jitTimer;
    NSString* _pendingGame;
}

- (BOOL)prefersStatusBarHidden { return YES; }
- (BOOL)prefersHomeIndicatorAutoHidden { return YES; }
- (UIRectEdge)preferredScreenEdgesDeferringSystemGestures { return UIRectEdgeAll; }

- (void)loadView {
    _gameView = [[MetalView alloc] initWithFrame:UIScreen.mainScreen.bounds];
    _gameView.backgroundColor = UIColor.blackColor;
    _gameView.contentScaleFactor = UIScreen.mainScreen.nativeScale;
    ((CAMetalLayer*)_gameView.layer).contentsScale = UIScreen.mainScreen.nativeScale;
    self.view = _gameView;

    _touch = [[TouchControls alloc] initWithFrame:_gameView.bounds];
    _touch.autoresizingMask = UIViewAutoresizingFlexibleWidth | UIViewAutoresizingFlexibleHeight;
    __weak GameViewController* weakSelf = self;
    _touch.onSettings = ^{ [weakSelf toggleSettings]; };
    [_gameView addSubview:_touch];

    _stats = [UILabel new];
    _stats.font = [UIFont monospacedSystemFontOfSize:13 weight:UIFontWeightMedium];
    _stats.textColor = UIColor.whiteColor;
    _stats.backgroundColor = [UIColor colorWithWhite:0 alpha:0.5];
    _stats.frame = CGRectMake(8, 8, 520, 20);
    [_gameView addSubview:_stats];

    _message = [UILabel new];
    _message.textColor = UIColor.whiteColor;
    _message.numberOfLines = 0;
    _message.textAlignment = NSTextAlignmentCenter;
    _message.font = [UIFont systemFontOfSize:20 weight:UIFontWeightMedium];
    _message.frame = CGRectInset(_gameView.bounds, 80, 120);
    _message.autoresizingMask = UIViewAutoresizingFlexibleWidth | UIViewAutoresizingFlexibleHeight;
    [_gameView addSubview:_message];

    [self applySettings];
}

// Fills the game layer with one color through Metal: shows that the layer itself is on screen (BOTWPC_LAYER_TEST=1).
- (void)fillLayerForTest {
    CAMetalLayer* layer = (CAMetalLayer*)_gameView.layer;
    layer.device = layer.device ?: MTLCreateSystemDefaultDevice();
    layer.drawableSize = CGSizeMake(_gameView.bounds.size.width * _gameView.contentScaleFactor, _gameView.bounds.size.height * _gameView.contentScaleFactor);
    id<CAMetalDrawable> drawable = [layer nextDrawable];
    id<MTLCommandQueue> queue = [layer.device newCommandQueue];
    MTLRenderPassDescriptor* pass = [MTLRenderPassDescriptor renderPassDescriptor];
    pass.colorAttachments[0].texture = drawable.texture;
    pass.colorAttachments[0].loadAction = MTLLoadActionClear;
    pass.colorAttachments[0].clearColor = MTLClearColorMake(0.1, 0.2, 0.6, 1);
    pass.colorAttachments[0].storeAction = MTLStoreActionStore;
    id<MTLCommandBuffer> cb = [queue commandBuffer];
    [[cb renderCommandEncoderWithDescriptor:pass] endEncoding];
    [cb presentDrawable:drawable];
    [cb commit];
}

- (void)viewDidAppear:(BOOL)animated {
    [super viewDidAppear:animated];
    if (_started) return;
    if (getenv("BOTWPC_LAYER_TEST")) [self fillLayerForTest];
    [[NSNotificationCenter defaultCenter] addObserver:self selector:@selector(controllersChanged) name:GCControllerDidConnectNotification object:nil];
    [[NSNotificationCenter defaultCenter] addObserver:self selector:@selector(controllersChanged) name:GCControllerDidDisconnectNotification object:nil];
    [self controllersChanged];
    [self waitForJitThenBoot];
}

- (void)applySettings {
    _stats.hidden = !GameSettings.showStats;
    _touch.alpha = GameSettings.touchOpacity;
    _touch.hidden = GameSettings.touchOpacity < 0.02 || GCController.controllers.count > 0;
}

- (void)waitForJitThenBoot {
    NSString* error = nil;
    NSString* game = [GameData prepare:&error];
    if (!game) { _message.text = error; return; }
    if (!jit_cs_debugged()) {
#if BOTWPC_AOT
        _message.text = @"Waiting for JIT…\n\nEnable JIT with StikDebug (Universal JIT script) or botw/ipados/run.sh on a Mac, "
                        @"or tap to start without JIT: the game then runs only the code compiled into the app and stops "
                        @"where it reaches code that was not.";
        _message.userInteractionEnabled = YES;
        [_message addGestureRecognizer:[[UITapGestureRecognizer alloc] initWithTarget:self action:@selector(bootWithoutJit)]];
        _pendingGame = game;
        // BOTWPC_AUTOSTART_NOJIT=1 (scripted runs, devicectl --environment-variables): no tap needed.
        if (getenv("BOTWPC_AUTOSTART_NOJIT") && getenv("BOTWPC_AUTOSTART_NOJIT")[0] == '1') {
            dispatch_async(dispatch_get_main_queue(), ^{ [self bootWithoutJit]; });
            return;
        }
#else
        _message.text = @"Waiting for JIT…\n\nEnable JIT for BOTW-iPad with StikDebug (Universal JIT script), or from a Mac run "
                        @"botw/ipados/run.sh. The game starts by itself once JIT is on.";
#endif
        _jitTimer = [NSTimer scheduledTimerWithTimeInterval:0.5 repeats:YES block:^(NSTimer* t) {
            if (jit_cs_debugged()) { [t invalidate]; [self boot:game]; }
        }];
        return;
    }
    [self boot:game];
}

- (void)bootWithoutJit {
    if (_started) return;
    [_jitTimer invalidate];
    [self boot:_pendingGame];
}

- (void)boot:(NSString*)game {
    if (_started) return;
    _started = YES;
    // Scripted runs pass files relative to the app's container (devicectl cannot know its path).
    for (NSString* key in @[@"BOTWPC_INPUT_SCRIPT", @"BOTWPC_FRAMELOG"]) {
        const char* value = getenv(key.UTF8String);
        if (value && value[0] && value[0] != '/')
            setenv(key.UTF8String, [NSHomeDirectory() stringByAppendingPathComponent:@(value)].UTF8String, 1);
    }
    _message.text = @"";
    _message.userInteractionEnabled = NO;
    BOOL jit = jit_cs_debugged();
#if BOTWPC_AOT
    botwpc_set_aot_image(botwpc_aot_code, botwpc_aot_entries, botwpc_aot_count, botwpc_aot_stubs, jit);
    botwpc_set_aot_simd(botwpc_aot_code, botwpc_aot_simd_offset, botwpc_aot_simd_count);
#else
    botwpc_set_aot_image(NULL, NULL, 0, NULL, jit);
#endif
    // The memory-tracking handler is compiled in, so no code has to be generated for it (needed without JIT).
    botwpc_set_signal_handler((void*)botw_signal_handler, botw_signal_set_config);
    botwpc_set_jit_request(RequestJit);
    g_message = _message;
    g_haptics = [[UIImpactFeedbackGenerator alloc] initWithStyle:UIImpactFeedbackStyleMedium];
    botwpc_set_jit_prepare(jit_prepare);
    botwpc_set_rumble_callback(Rumble);

    CGSize px = CGSizeMake(_gameView.bounds.size.width * _gameView.contentScaleFactor, _gameView.bounds.size.height * _gameView.contentScaleFactor);
    BotwPcStartArgs args = {
        GameData.dataDir.UTF8String, game.UTF8String, (__bridge void*)_gameView, (__bridge void*)_gameView.layer,
        (int32_t)px.width, (int32_t)px.height, RunOnMainThread,
    };
    if (botwpc_start(&args) != 0) { _message.text = @"The Switch runtime failed to start (see Documents/BOTW-PC/Logs)."; return; }

    [self startMotion];
    // Pause when the app stops being active (app switcher, Control Center, Settings to pair a controller).
    NSNotificationCenter* center = NSNotificationCenter.defaultCenter;
    [center addObserverForName:UISceneWillDeactivateNotification object:nil queue:NSOperationQueue.mainQueue
                    usingBlock:^(NSNotification* n) { botwpc_set_paused(1); }];
    [center addObserverForName:UISceneDidActivateNotification object:nil queue:NSOperationQueue.mainQueue
                    usingBlock:^(NSNotification* n) { botwpc_set_paused(0); }];
    UIDevice.currentDevice.batteryMonitoringEnabled = YES;
    NSString* logPath = [GameData.documents stringByAppendingPathComponent:@"botw_stats.csv"];
    [[NSFileManager defaultManager] createFileAtPath:logPath contents:[@"time_s,fps,frame_ms,game_speed,footprint_gb,available_gb,thermal,battery,charging,cpu_cores\n" dataUsingEncoding:NSUTF8StringEncoding] attributes:nil];
    _statsLog = [NSFileHandle fileHandleForWritingAtPath:logPath];
    [_statsLog seekToEndOfFile];
    _lastTime = CACurrentMediaTime();
    _timer = [NSTimer scheduledTimerWithTimeInterval:1.0 target:self selector:@selector(tick) userInfo:nil repeats:YES];
}

- (void)viewDidLayoutSubviews {
    [super viewDidLayoutSubviews];
    _landscapeSign = self.view.window.windowScene.interfaceOrientation == UIInterfaceOrientationLandscapeLeft ? 1 : -1;
    if (_started) botwpc_set_size((int)(_gameView.bounds.size.width * _gameView.contentScaleFactor), (int)(_gameView.bounds.size.height * _gameView.contentScaleFactor));
}

// BOTWPC_THREADLOG=1: every 20 s, the threads that used the most CPU since the last report (power and thermal work).
static void LogThreadCpu(void) {
    static NSMutableDictionary<NSNumber*, NSNumber*>* last;
    static CFTimeInterval lastTime;
    if (!last) last = [NSMutableDictionary new];
    CFTimeInterval now = CACurrentMediaTime();
    thread_act_array_t threads;
    mach_msg_type_number_t count;
    if (task_threads(mach_task_self(), &threads, &count) != KERN_SUCCESS) return;
    NSMutableArray* rows = [NSMutableArray new];
    for (mach_msg_type_number_t i = 0; i < count; i++) {
        thread_identifier_info_data_t ident;
        thread_extended_info_data_t ext;
        mach_msg_type_number_t n = THREAD_IDENTIFIER_INFO_COUNT, m = THREAD_EXTENDED_INFO_COUNT;
        if (thread_info(threads[i], THREAD_IDENTIFIER_INFO, (thread_info_t)&ident, &n) == KERN_SUCCESS &&
            thread_info(threads[i], THREAD_EXTENDED_INFO, (thread_info_t)&ext, &m) == KERN_SUCCESS) {
            double cpu = (ext.pth_user_time + ext.pth_system_time) / 1e9;
            NSNumber* key = @(ident.thread_id);
            double delta = cpu - last[key].doubleValue;
            last[key] = @(cpu);
            if (lastTime > 0 && delta > 0.01) [rows addObject:@[@(delta), @(ext.pth_name)]];
        }
        mach_port_deallocate(mach_task_self(), threads[i]);
    }
    vm_deallocate(mach_task_self(), (vm_address_t)threads, count * sizeof(thread_act_t));
    if (lastTime > 0) {
        [rows sortUsingComparator:^NSComparisonResult(NSArray* a, NSArray* b) { return [b[0] compare:a[0]]; }];
        NSMutableString* line = [NSMutableString stringWithFormat:@"Threads (%% of a core):"];
        for (NSUInteger i = 0; i < MIN(rows.count, 14); i++)
            [line appendFormat:@" %@=%.0f", [rows[i][1] length] ? rows[i][1] : @"?", 100 * [rows[i][0] doubleValue] / (now - lastTime)];
        NSLog(@"%@", line);
    }
    lastTime = now;
}

- (void)tick {
    BotwPcStats s = {0};
    botwpc_get_stats(&s);
    CFTimeInterval now = CACurrentMediaTime();
    double speed = now > _lastTime ? (s.gameSeconds - _lastGameSeconds) / (now - _lastTime) : 0;
    struct rusage usage;
    getrusage(RUSAGE_SELF, &usage);
    double cpuSeconds = usage.ru_utime.tv_sec + usage.ru_stime.tv_sec + (usage.ru_utime.tv_usec + usage.ru_stime.tv_usec) / 1e6;
    double cpuCores = now > _lastTime ? (cpuSeconds - _lastCpuSeconds) / (now - _lastTime) : 0;
    _lastCpuSeconds = cpuSeconds;
    static int threadLogTicks;
    if (getenv("BOTWPC_THREADLOG") && threadLogTicks++ % 20 == 0) LogThreadCpu();
    _lastTime = now;
    _lastGameSeconds = s.gameSeconds;
    double footprint = FootprintGB(), available = os_proc_available_memory() / 1073741824.0;
    NSInteger thermal = NSProcessInfo.processInfo.thermalState;
    static const char* thermalNames[] = {"nominal", "fair", "serious", "critical"};
    _stats.text = [NSString stringWithFormat:@"%.1f FPS  %.1f ms  speed %.2f  mode %d  mem %.2f GB (+%.2f)  %s", s.fps, s.frameMs, speed,
                   s.frameRate, footprint, available, thermalNames[MIN(thermal, 3)]];
    UIDevice* device = UIDevice.currentDevice;
    [_statsLog writeData:[[NSString stringWithFormat:@"%.1f,%.2f,%.2f,%.3f,%.3f,%.3f,%ld,%.2f,%d,%.2f\n", now, s.fps, s.frameMs, speed, footprint, available, (long)thermal,
                           device.batteryLevel, device.batteryState == UIDeviceBatteryStateCharging || device.batteryState == UIDeviceBatteryStateFull, cpuCores]
                          dataUsingEncoding:NSUTF8StringEncoding]];
    if (!s.running && _started) _message.text = @"The game stopped.";
}

- (void)startMotion {
    _motion = [CMMotionManager new];
    if (!_motion.deviceMotionAvailable) return;
    _motion.deviceMotionUpdateInterval = 1.0 / 120;
    [_motion startDeviceMotionUpdatesToQueue:[NSOperationQueue new] withHandler:^(CMDeviceMotion* m, NSError* e) {
        if (!m) return;
        // Device axes (portrait: +X right, +Y up, +Z out of the screen) turned into the landscape frame the player
        // holds, then into the controller frame the HID layer uses (+X right, +Y forward/up the screen, +Z towards the
        // player), in G and degrees per second.
        double ax = m.gravity.x + m.userAcceleration.x, ay = m.gravity.y + m.userAcceleration.y, az = m.gravity.z + m.userAcceleration.z;
        double gx = m.rotationRate.x * 180 / M_PI, gy = m.rotationRate.y * 180 / M_PI, gz = m.rotationRate.z * 180 / M_PI;
        double sx = self->_landscapeSign; // landscape left: device +Y points right
        botwpc_motion((float)(sx * ay), (float)(-sx * ax), (float)az, (float)(sx * gy), (float)(-sx * gx), (float)gz);
    }];
}

- (void)controllersChanged {
    [self applySettings];
    for (GCController* c in GCController.controllers) {
        GCExtendedGamepad* pad = c.extendedGamepad;
        if (!pad) continue;
        __weak GameViewController* weakSelf = self;
        // L3 + R3: settings (the game sees both clicks too).
        pad.valueChangedHandler = ^(GCExtendedGamepad* g, GCControllerElement* element) {
            static BOOL chord;
            BOOL both = g.leftThumbstickButton.pressed && g.rightThumbstickButton.pressed;
            if (both && !chord) dispatch_async(dispatch_get_main_queue(), ^{ [weakSelf toggleSettings]; });
            chord = both;
        };
    }
}

- (void)toggleSettings {
    if (_settings) {
        [_settings removeFromSuperview];
        _settings = nil;
        return;
    }
    _settings = [[SettingsView alloc] initWithFrame:_gameView.bounds];
    _settings.autoresizingMask = UIViewAutoresizingFlexibleWidth | UIViewAutoresizingFlexibleHeight;
    __weak GameViewController* weakSelf = self;
    _settings.onClose = ^{ [weakSelf toggleSettings]; };
    _settings.onChange = ^{ [weakSelf applySettings]; };
    [_gameView addSubview:_settings];
}
@end
