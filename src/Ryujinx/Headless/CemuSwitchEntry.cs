using Ryujinx.Ava.Systems.Configuration;
using Ryujinx.Common;
using Ryujinx.Common.Configuration;
using Ryujinx.Common.Logging;
using Ryujinx.SDL2.Common;
using System;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;

namespace Ryujinx.Headless
{
    /// <summary>
    /// Cemu-Switch embedding API: the Cemu-derived macOS application loads the .NET runtime in its own process
    /// (hostfxr) and drives BOTW-PC's Switch runtime (HLE, ARM64 JIT, Maxwell GPU, Vulkan/MoltenVK) through these
    /// unmanaged entry points. The game renders into the CAMetalLayer of a Cemu canvas; settings, saves, DLC and mods come
    /// from the given BOTW-PC data directory (its Config.json, as with --use-main-config).
    /// </summary>
    public static unsafe class CemuSwitchEntry
    {
        [StructLayout(LayoutKind.Sequential)]
        public struct StartArgs
        {
            public byte* DataDir;    // UTF-8
            public byte* GamePath;   // UTF-8
            public nint NsView;
            public nint MetalLayer;
            public int Width;
            public int Height;
            // void RunOnMainThread(void (*fn)(void*), void* ctx): runs fn(ctx) synchronously on the host's main thread.
            public nint RunOnMainThread;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct Stats
        {
            public double Fps;
            public double FrameMs;
            public double GameSeconds;
            public int Running;
            public int FrameRate; // 30 (console), 60/90/120 (custom refresh rate), -1 (unbounded), 0 (not running yet)
        }

        public sealed record HostSurface(nint NsView, nint MetalLayer, int Width, int Height);

        internal static HostSurface Surface { get; private set; }

        internal static HostKeyboardDriver Keyboard { get; } = new();

        internal static HostTouchPad TouchPad { get; } = new();

        private static Thread _thread;
        private static nint _runOnMainThread;
        private static Timer _memLog;

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
        private static void InvokeAction(nint handle)
        {
            ((Action)GCHandle.FromIntPtr(handle).Target)();
        }

        /// <summary>SDL on macOS must run on the main thread: forward to the host's main thread.</summary>
        private static void RunOnMainThread(Action action)
        {
            GCHandle handle = GCHandle.Alloc(action);

            try
            {
                ((delegate* unmanaged[Cdecl]<delegate* unmanaged[Cdecl]<nint, void>, nint, void>)_runOnMainThread)(&InvokeAction, GCHandle.ToIntPtr(handle));
            }
            finally
            {
                handle.Free();
            }
        }

        [UnmanagedCallersOnly(EntryPoint = "botwpc_start", CallConvs = [typeof(CallConvCdecl)])]
        public static int Start(StartArgs* args)
        {
            try
            {
                if (_thread is { IsAlive: true })
                {
                    return 1;
                }

                // Hosted without running Main: give code that reads the entry assembly (version, paths) the app assembly.
                System.Reflection.Assembly.SetEntryAssembly(typeof(CemuSwitchEntry).Assembly);

                string dataDir = Marshal.PtrToStringUTF8((nint)args->DataDir);
                string gamePath = Marshal.PtrToStringUTF8((nint)args->GamePath);
                Surface = new HostSurface(args->NsView, args->MetalLayer, args->Width, args->Height);
                _runOnMainThread = args->RunOnMainThread;

                // SDL reads the controller database from the data directory.
                Ryujinx.Common.Configuration.AppDataManager.Initialize(dataDir);

                // The host owns the window and the Cocoa event loop: SDL serves controllers and audio only.
                SDL2Driver.InitFlagsOverride = global::SDL2.SDL.SDL_INIT_EVENTS | global::SDL2.SDL.SDL_INIT_GAMECONTROLLER | global::SDL2.SDL.SDL_INIT_JOYSTICK | global::SDL2.SDL.SDL_INIT_AUDIO;

                // iOS: SDL expects its own main(); the host app has one.
                if (OperatingSystem.IsIOS())
                {
                    global::SDL2.SDL.SDL_SetMainReady();
                }

                if (_runOnMainThread != 0)
                {
                    SDL2Driver.MainThreadDispatcher = RunOnMainThread;
                    RunOnMainThread(SDL2Driver.Instance.Initialize);
                }

                _thread = new Thread(() =>
                {
                    try
                    {
                        HeadlessRyujinx.Entrypoint(["--root-data-dir", dataDir, "--use-main-config", gamePath]);
                    }
                    catch (Exception e)
                    {
                        Logger.Error?.Print(LogClass.Application, $"Cemu-Switch: Switch runtime stopped with an exception: {e}");
                    }
                })
                {
                    Name = "CemuSwitch.Main",
                    IsBackground = true,
                };
                _thread.Start();

                // BOTWPC_MEMLOG=1: managed heap every 10 s (memory budgeting on 8 GB iPads).
                if (Environment.GetEnvironmentVariable("BOTWPC_MEMLOG") == "1")
                {
                    _memLog = new Timer(_ =>
                    {
                        GCMemoryInfo gc = GC.GetGCMemoryInfo();
                        Logger.Info?.Print(LogClass.Application, $"Memory: GC heap {gc.HeapSizeBytes >> 20} MB, committed {gc.TotalCommittedBytes >> 20} MB, fragmented {gc.FragmentedBytes >> 20} MB, process {Environment.WorkingSet >> 20} MB");
                    }, null, 10000, 10000);
                }

                return 0;
            }
            catch (Exception e)
            {
                Console.Error.WriteLine($"Cemu-Switch: start failed: {e}");
                return -1;
            }
        }

        /// <summary>Requests the game to stop and returns at once; the host polls GetStats until Running is 0 (the
        /// shutdown may need the host's main thread, which therefore must not block here).</summary>
        [UnmanagedCallersOnly(EntryPoint = "botwpc_stop", CallConvs = [typeof(CallConvCdecl)])]
        public static void Stop()
        {
            try
            {
                new Thread(() => HeadlessRyujinx.Window?.Exit()) { Name = "CemuSwitch.Stop", IsBackground = true }.Start();
            }
            catch (Exception e)
            {
                Console.Error.WriteLine($"Cemu-Switch: stop failed: {e}");
            }
        }

        /// <summary>Pauses (1) or resumes (0) the game, e.g. while the iPad app is not active.</summary>
        [UnmanagedCallersOnly(EntryPoint = "botwpc_set_paused", CallConvs = [typeof(CallConvCdecl)])]
        public static void SetPaused(int paused)
        {
            try
            {
                HeadlessRyujinx.Window?.SetPaused(paused != 0);
            }
            catch (Exception e)
            {
                Console.Error.WriteLine($"Cemu-Switch: pause failed: {e}");
            }
        }

        [UnmanagedCallersOnly(EntryPoint = "botwpc_set_size", CallConvs = [typeof(CallConvCdecl)])]
        public static void SetSize(int width, int height)
        {
            if (HeadlessRyujinx.Window is ExternalMetalWindow window && width > 0 && height > 0)
            {
                window.Resize(width, height);
            }
        }

        /// <summary>Key event of the host's game canvas: macOS virtual key code (NSEvent.keyCode), down = 1 / up = 0.</summary>
        [UnmanagedCallersOnly(EntryPoint = "botwpc_key_event", CallConvs = [typeof(CallConvCdecl)])]
        public static void KeyEvent(int macKeyCode, int down)
        {
            Keyboard.OnHostKey(macKeyCode, down != 0);
        }

        /// <summary>Frame-rate mode of the running game: 30 is the console's (VSync mode Switch); 60/90/120 use a custom
        /// refresh rate of twice that (BotW presents every second VBlank). Kept in the data folder's Config.json.</summary>
        [UnmanagedCallersOnly(EntryPoint = "botwpc_set_frame_rate", CallConvs = [typeof(CallConvCdecl)])]
        public static void SetFrameRate(int fps)
        {
            try
            {
                HLE.Switch device = HeadlessRyujinx.EmulationContext;

                if (device == null || fps is not (30 or 60 or 90 or 120))
                {
                    return;
                }

                VSyncMode mode = fps == 30 ? VSyncMode.Switch : VSyncMode.Custom;

                device.CustomVSyncIntervalEnabled = true;
                device.CustomVSyncInterval = fps * 2;
                device.VSyncMode = mode;
                device.UpdateVSyncInterval();
                HeadlessRyujinx.Window?.Renderer?.Window.ChangeVSyncMode(mode);

                ConfigurationState.Instance.Graphics.VSyncMode.Value = mode;
                ConfigurationState.Instance.Graphics.EnableCustomVSyncInterval.Value = true;
                ConfigurationState.Instance.Graphics.CustomVSyncInterval.Value = fps * 2;
                ConfigurationState.Instance.ToFileFormat().SaveConfig(Path.Combine(AppDataManager.BaseDirPath, ReleaseInformation.ConfigName));
            }
            catch (Exception e)
            {
                Logger.Error?.Print(LogClass.Application, $"Cemu-Switch: frame-rate change failed: {e}");
            }
        }

        /// <summary>iPadOS: makes the JIT code arena executable (debugger "prepare region" protocol), 0 on success.
        /// Must be set before botwpc_start.</summary>
        [UnmanagedCallersOnly(EntryPoint = "botwpc_set_jit_prepare", CallConvs = [typeof(CallConvCdecl)])]
        public static void SetJitPrepare(delegate* unmanaged[Cdecl]<nint, nuint, int> prepare)
        {
            Cpu.LightningJit.Cache.IosJitArena.Prepare = prepare;
        }

        /// <summary>
        /// Guest code compiled ahead of time and linked into the host (botw/ipados/aot_build.py): code base, index entries,
        /// count, offsets of the dispatch stubs. jitAvailable = 0: the process cannot run generated code, misses are fatal.
        /// Must be called before botwpc_start.
        /// </summary>
        [UnmanagedCallersOnly(EntryPoint = "botwpc_set_aot_image", CallConvs = [typeof(CallConvCdecl)])]
        public static void SetAotImage(byte* code, Cpu.LightningJit.AotImage.Entry* entries, int count, uint* stubOffsets, int jitAvailable)
        {
            Cpu.LightningJit.AotImage.JitAvailable = jitAvailable != 0;

            if (code != null && count > 0)
            {
                Cpu.LightningJit.AotImage.Load(code, entries, count, stubOffsets);
            }
        }

        /// <summary>
        /// The image's interpreter FP/SIMD stubs: (instruction, RET) pairs at code + offset, sorted (aot_build.py
        /// _botwpc_aot_simd_offset / _count). Without them, the interpreter needs JIT for FP/SIMD instructions.
        /// </summary>
        [UnmanagedCallersOnly(EntryPoint = "botwpc_set_aot_simd", CallConvs = [typeof(CallConvCdecl)])]
        public static void SetAotSimd(byte* code, uint offset, int count)
        {
            if (code != null && count > 0)
            {
                Cpu.LightningJit.AotImage.LoadSimd((uint*)(code + offset), count);
            }
        }

        /// <summary>int request(ulong guestAddress): called when code missing from the AOT image needs JIT; blocks until JIT is
        /// usable (1) or gives up (0). Runs on a guest thread.</summary>
        [UnmanagedCallersOnly(EntryPoint = "botwpc_set_jit_request", CallConvs = [typeof(CallConvCdecl)])]
        public static void SetJitRequest(delegate* unmanaged[Cdecl]<ulong, int> request)
        {
            Cpu.LightningJit.AotImage.RequestJit = request;
        }

        /// <summary>Memory-tracking signal handler compiled into the host (no generated handler code), and the function that
        /// receives its configuration.</summary>
        [UnmanagedCallersOnly(EntryPoint = "botwpc_set_signal_handler", CallConvs = [typeof(CallConvCdecl)])]
        public static void SetSignalHandler(nint handler, delegate* unmanaged[Cdecl]<nint, void> setConfig)
        {
            Cpu.LightningJit.AotImage.SetHostSignalHandler(handler, setConfig);
        }

        /// <summary>Touch controls: Switch buttons (bit = GamepadButtonInputId), sticks -1..1, +Y up.</summary>
        [UnmanagedCallersOnly(EntryPoint = "botwpc_touch", CallConvs = [typeof(CallConvCdecl)])]
        public static void Touch(ulong buttons, float lx, float ly, float rx, float ry)
        {
            TouchPad.Set(buttons, lx, ly, rx, ry);
        }

        /// <summary>Device motion for the touch controls: acceleration in G, rotation rate in degrees per second.</summary>
        [UnmanagedCallersOnly(EntryPoint = "botwpc_motion", CallConvs = [typeof(CallConvCdecl)])]
        public static void Motion(float ax, float ay, float az, float gx, float gy, float gz)
        {
            TouchPad.SetMotion(new System.Numerics.Vector3(ax, ay, az), new System.Numerics.Vector3(gx, gy, gz));
        }

        /// <summary>void rumble(float low, float high, uint durationMs): haptics for the touch controls.</summary>
        [UnmanagedCallersOnly(EntryPoint = "botwpc_set_rumble_callback", CallConvs = [typeof(CallConvCdecl)])]
        public static void SetRumbleCallback(delegate* unmanaged[Cdecl]<float, float, uint, void> callback)
        {
            HostTouchPad.RumbleRequested += (low, high, ms) => callback(low, high, ms);
        }

        [UnmanagedCallersOnly(EntryPoint = "botwpc_get_stats", CallConvs = [typeof(CallConvCdecl)])]
        public static void GetStats(Stats* stats)
        {
            // Running: the runtime thread is alive, including while it boots (the host must keep the surface until it
            // stops). The numbers exist once the emulated Switch does.
            HLE.Switch device = HeadlessRyujinx.EmulationContext;
            bool running = _thread is { IsAlive: true };
            bool hasDevice = running && device != null;

            stats->Running = running ? 1 : 0;
            stats->Fps = hasDevice ? device.Statistics.GetGameFrameRate() : 0;
            stats->FrameMs = hasDevice && stats->Fps > 0 ? device.Statistics.GetGameFrameTime() : 0;
            stats->GameSeconds = hasDevice ? device.BotwTiming?.TotalGameSeconds ?? 0 : 0;
            stats->FrameRate = !hasDevice ? 0 : device.VSyncMode switch
            {
                VSyncMode.Switch => 30,
                VSyncMode.Custom => device.CustomVSyncInterval / 2,
                _ => -1,
            };
        }
    }
}
