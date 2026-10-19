using Ryujinx.Common.Configuration;
using Ryujinx.Common.Logging;
using Silk.NET.Vulkan;
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;

namespace Ryujinx.Graphics.Vulkan
{
    /// <summary>
    /// BOTW-PC: NVIDIA DLSS Super Resolution through the NGX bridge (botw/native/ngx_bridge). This part loads the
    /// bridge, adds the Vulkan extensions NGX needs and initialises it on Ryujinx's device; <see cref="DlssPass"/> does
    /// the per-frame work. Selected by Settings > BotW-PC (graphics.dlss) or BOTWPC_DLSS=performance|balanced|quality|dlaa,
    /// which overrides the setting; off by default. Everything falls back to the game's own upscale if a step fails.
    /// </summary>
    static unsafe class Dlss
    {
        public const int QualityPerformance = 0, QualityBalanced = 1, QualityQuality = 2, QualityDlaa = 5;

        /// <summary>NGX wanted at start (DLSS selected, or the in-game overlay enabled so it can be switched on live).</summary>
        public static bool Requested { get; }

        /// <summary>Selected DLSS preset (-1 = off). Follows the setting live (overlay / settings window).</summary>
        public static int Quality => Parse(Common.BotwPc.GraphicsSettings.EffectiveDlss);

        /// <summary>DLSS initialised and currently selected: jitter and the upscale replacement run.</summary>
        public static bool Active => Available && Quality >= 0;

        private static int Parse(string mode) => mode switch
        {
            "performance" => QualityPerformance,
            "balanced" => QualityBalanced,
            "quality" or "on" => QualityQuality,
            "dlaa" => QualityDlaa,
            _ => -1,
        };

        /// <summary>NGX initialised and DLSS available on this device.</summary>
        public static bool Available { get; private set; }

        private static readonly nint _library;

        private static readonly delegate* unmanaged<byte*, byte*, void> _configure;
        private static readonly delegate* unmanaged<byte***, uint*, int> _instanceExtensions;
        private static readonly delegate* unmanaged<Instance, PhysicalDevice, byte***, uint*, int> _deviceExtensions;
        private static readonly delegate* unmanaged<Instance, PhysicalDevice, Device, nint, nint, int*, int> _init;
        internal static readonly delegate* unmanaged<uint, uint, int, uint*, uint*, int> OptimalRenderSize;
        internal static readonly delegate* unmanaged<CommandBuffer, uint, uint, uint, uint, int, int, int> Create;
        internal static readonly delegate* unmanaged<CommandBuffer, NgxImage*, NgxImage*, NgxImage*, NgxImage*, NgxImage*, float, float, float, float, int, float, int> Evaluate;
        private static readonly delegate* unmanaged<int, nint> _resultString;

        [StructLayout(LayoutKind.Sequential)]
        internal struct NgxImage
        {
            public Image Image;
            public ImageView View;
            public Format Format;
            public uint Width;
            public uint Height;
        }

        static Dlss()
        {
            if (Quality < 0 && !Common.BotwPc.BotwPcSettings.Instance.Overlay.Enabled)
            {
                return;
            }

            if (OperatingSystem.IsMacOS())
            {
                Logger.Info?.Print(LogClass.Gpu, "BOTW-PC DLSS: NVIDIA-only, not available on macOS; the game's own upscale is used.");
                Common.BotwPc.Overlay.DlssRuntime.Status = "NVIDIA-only, not available on macOS";
                return;
            }

            string dir = Environment.GetEnvironmentVariable("BOTWPC_NGX_DIR") ?? Path.Combine(AppContext.BaseDirectory, "ngx");
            string path = Path.Combine(dir, OperatingSystem.IsWindows() ? "botw_ngx_bridge.dll" : "libbotw_ngx_bridge.so");

            if (!NativeLibrary.TryLoad(path, out _library))
            {
                Logger.Warning?.Print(LogClass.Gpu, $"BOTW-PC DLSS: bridge not found at {path}; DLSS off.");
                Common.BotwPc.Overlay.DlssRuntime.Status = "DLSS bridge not installed";
                return;
            }

            nint F(string name) => NativeLibrary.GetExport(_library, name);
            _configure = (delegate* unmanaged<byte*, byte*, void>)F("botw_ngx_configure");
            _instanceExtensions = (delegate* unmanaged<byte***, uint*, int>)F("botw_ngx_instance_extensions");
            _deviceExtensions = (delegate* unmanaged<Instance, PhysicalDevice, byte***, uint*, int>)F("botw_ngx_device_extensions");
            _init = (delegate* unmanaged<Instance, PhysicalDevice, Device, nint, nint, int*, int>)F("botw_ngx_init");
            OptimalRenderSize = (delegate* unmanaged<uint, uint, int, uint*, uint*, int>)F("botw_ngx_optimal_render_size");
            Create = (delegate* unmanaged<CommandBuffer, uint, uint, uint, uint, int, int, int>)F("botw_ngx_create");
            Evaluate = (delegate* unmanaged<CommandBuffer, NgxImage*, NgxImage*, NgxImage*, NgxImage*, NgxImage*, float, float, float, float, int, float, int>)F("botw_ngx_evaluate");
            _resultString = (delegate* unmanaged<int, nint>)F("botw_ngx_result_string");

            string data = Path.Combine(AppDataManager.BaseDirPath ?? ".", "botw", "ngx");
            Directory.CreateDirectory(data);
            nint snippet = Marshal.StringToHGlobalAnsi(dir), dataDir = Marshal.StringToHGlobalAnsi(data);
            _configure((byte*)snippet, (byte*)dataDir);
            Marshal.FreeHGlobal(snippet);
            Marshal.FreeHGlobal(dataDir);
            Requested = true;
        }

        public static string ResultString(int result) => _library == 0 ? result.ToString() : Marshal.PtrToStringAnsi(_resultString(result));

        public static bool Succeeded(int result) => (result & unchecked((int)0xFFF00000)) != unchecked((int)0xBAD00000);

        private static string[] Names(byte** names, uint count)
        {
            string[] result = new string[count];

            for (int i = 0; i < count; i++)
            {
                result[i] = Marshal.PtrToStringAnsi((nint)names[i]);
            }

            return result;
        }

        public static string[] InstanceExtensions()
        {
            if (!Requested)
            {
                return [];
            }

            byte** names;
            uint count;
            int r = _instanceExtensions(&names, &count);

            return Succeeded(r) ? Names(names, count) : [];
        }

        public static string[] DeviceExtensions(Instance instance, PhysicalDevice physicalDevice)
        {
            if (!Requested)
            {
                return [];
            }

            byte** names;
            uint count;
            int r = _deviceExtensions(instance, physicalDevice, &names, &count);

            return Succeeded(r) ? Names(names, count) : [];
        }

        /// <summary>Initialises NGX on the created device; logs and stays off on any failure.</summary>
        public static void Init(Vk api, Instance instance, PhysicalDevice physicalDevice, Device device, IReadOnlyCollection<string> enabledDeviceExtensions)
        {
            if (!Requested)
            {
                return;
            }

            foreach (string extension in DeviceExtensions(instance, physicalDevice))
            {
                if (!System.Linq.Enumerable.Contains(enabledDeviceExtensions, extension))
                {
                    Logger.Warning?.Print(LogClass.Gpu, $"BOTW-PC DLSS: device extension {extension} not enabled; DLSS off.");
                    return;
                }
            }

            nint gipa = api.Context.GetProcAddress("vkGetInstanceProcAddr");
            nint gdpa = api.Context.GetProcAddress("vkGetDeviceProcAddr");
            int available;
            int r = _init(instance, physicalDevice, device, gipa, gdpa, &available);
            Available = Succeeded(r) && available != 0;
            Common.BotwPc.Overlay.DlssRuntime.Available = Available;
            Common.BotwPc.Overlay.DlssRuntime.Status = Available ? "ready" : $"not available ({ResultString(r)})";
            Logger.Info?.Print(LogClass.Gpu, $"BOTW-PC DLSS: NGX init {ResultString(r)}, Super Resolution {(Available ? "available" : "NOT available")} (quality {Quality}).");
        }
    }
}
