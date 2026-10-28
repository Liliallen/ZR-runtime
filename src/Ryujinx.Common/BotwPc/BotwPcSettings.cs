using Ryujinx.Common.Configuration;
using Ryujinx.Common.Logging;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Ryujinx.Common.BotwPc
{
    /// <summary>Face-button layout for automatically configured non-Nintendo controllers.</summary>
    public enum ButtonLayout
    {
        /// <summary>Position-true: the bottom face button is Switch B (Xbox A → B, B → A, X → Y, Y → X), as on a Switch Pro Controller.</summary>
        Nintendo,

        /// <summary>Label-true: Xbox A → Switch A, B → B, X → X, Y → Y, so the bottom button confirms.</summary>
        Xbox,
    }

    public class ControllerSettings
    {
        /// <summary>Bind a connected game controller to Player 1 when Player 1 has no connected controller.</summary>
        public bool AutoAssign { get; set; } = true;

        /// <summary>Case-insensitive substring of the preferred controller name; empty picks Xbox controllers first.</summary>
        public string Preferred { get; set; } = "";

        public ButtonLayout Layout { get; set; } = ButtonLayout.Nintendo;
        public float DeadzoneLeft { get; set; } = 0.1f;
        public float DeadzoneRight { get; set; } = 0.1f;

        /// <summary>Stick output scale (Ryujinx "range"): above 1 reaches full deflection earlier.</summary>
        public float RangeLeft { get; set; } = 1.0f;
        public float RangeRight { get; set; } = 1.0f;

        /// <summary>Right-stick response exponent applied after the deadzone (1 = linear; &gt;1 finer aiming near centre).</summary>
        public float CameraCurve { get; set; } = 1.0f;
        public bool InvertCameraX { get; set; }
        public bool InvertCameraY { get; set; }
        public float TriggerThreshold { get; set; } = 0.5f;

        /// <summary>A pressed trigger releases below TriggerThreshold − TriggerHysteresis (0 = stock behaviour).</summary>
        public float TriggerHysteresis { get; set; } = 0.05f;
        public bool Rumble { get; set; } = true;
        public float RumbleStrength { get; set; } = 1.0f;

        /// <summary>
        /// Custom bindings for the automatic config, applied after <see cref="Layout"/>: Switch button name
        /// (A, B, X, Y, L, R, ZL, ZR, Plus, Minus, LeftStick, RightStick, DpadUp, ...) to SDL input name
        /// (A = bottom, B = right, X = left, Y = top, LeftShoulder, RightTrigger, Back, Start, Misc1, Paddle1, ...).
        /// </summary>
        public Dictionary<string, string> Bindings { get; set; } = new();

        /// <summary>Glyphs for in-game button prompts; they always follow the active mapping.</summary>
        public PromptStyle Prompts { get; set; } = PromptStyle.Auto;
        public PromptPriority PromptPriority { get; set; } = PromptPriority.BuiltIn;

        /// <summary>
        /// Name of the controller last auto-assigned to Player 1. When it is not connected yet at game start (a wireless
        /// pad switched on after launch, or SDL still enumerating), the prompts are generated for it, since the game reads
        /// them only once.
        /// </summary>
        public string LastController { get; set; } = "";
    }

    /// <summary>Where Player 1's motion (gyroscope/accelerometer) data comes from.</summary>
    public enum MotionSource
    {
        /// <summary>The controller's own sensors when it has them (and motion is enabled), otherwise virtual motion.</summary>
        Auto,

        /// <summary>Only the controller's sensors (or CemuHook/DSU as configured in Ryujinx).</summary>
        Real,

        /// <summary>Only virtual motion (keyboard and/or View-hold on the controller).</summary>
        Virtual,

        /// <summary>No motion data.</summary>
        Off,
    }

    /// <summary>Virtual motion: keyboard keys and the controller's right stick turn a simulated controller.</summary>
    public class MotionSettings
    {
        public MotionSource Source { get; set; } = MotionSource.Auto;

        /// <summary>Rotation speed at full key press / full stick deflection, degrees per second.</summary>
        public float RateDegreesPerSecond { get; set; } = 90f;

        /// <summary>Speed factor while the fine-adjustment key (Shift) or LT is held.</summary>
        public float FineFactor { get; set; } = 0.25f;

        public bool InvertPitch { get; set; }
        public bool InvertRoll { get; set; }
        public bool InvertYaw { get; set; }

        public bool Keyboard { get; set; } = true;

        /// <summary>Key names as in Ryujinx's key list (Up, Down, Left, Right, Q, E, R, F8, ShiftLeft, ...).</summary>
        public string KeyPitchForward { get; set; } = "Up";
        public string KeyPitchBackward { get; set; } = "Down";
        public string KeyRollLeft { get; set; } = "Left";
        public string KeyRollRight { get; set; } = "Right";
        public string KeyYawLeft { get; set; } = "Q";
        public string KeyYawRight { get; set; } = "E";
        public string KeyRecenter { get; set; } = "R";
        public string KeyToggle { get; set; } = "F8";
        public string KeyFine { get; set; } = "ShiftLeft";

        /// <summary>Hold View on the controller: right stick pitch/roll, LB/RB yaw, R3 recenter, LT fine.</summary>
        public bool Controller { get; set; } = true;

        /// <summary>View held at least this long enters motion mode; a shorter tap is sent to the game as View (Minus).</summary>
        public int HoldThresholdMs { get; set; } = 300;

        /// <summary>A long View press toggles motion mode on/off instead of only while held.</summary>
        public bool ControllerToggle { get; set; }

        public float StickDeadzone { get; set; } = 0.15f;
    }

    /// <summary>Standalone mode (<c>Ryujinx --botw</c> / <c>botw-pc</c>).</summary>
    public class GameSettings
    {
        /// <summary>Base game file or folder (NSP, XCI, ...). Empty: found in the configured game folders on first start.</summary>
        public string Path { get; set; } = "";

        public bool Fullscreen { get; set; } = true;

        /// <summary>Close BOTW-PC when the game stops, instead of showing the game library.</summary>
        public bool ExitWithGame { get; set; } = true;
    }

    public enum DlssMode
    {
        Off,
        Quality,
        Balanced,
        Performance,
    }

    /// <summary>GPU command front end (GPU_FRONTEND_ARCHITECTURE.md). BOTWPC_GPU_FRONTEND overrides it.</summary>
    public enum GpuFrontendMode
    {
        Legacy,
        Optimized,

        /// <summary>Runtime V2 GPU front end: the optimized components plus the GPU V2 ones (GPU_FRONTEND_V2.md).</summary>
        V2,

        /// <summary>Runtime V3 GPU front end: V2 plus the GPU V3 components (GPU_FRONTEND_V3.md).</summary>
        V3,
    }

    /// <summary>Legacy (Ryujinx) or Runtime V2 implementation of a runtime component.</summary>
    public enum RuntimeCoreMode
    {
        Legacy,
        V2,

        /// <summary>Runtime V3 (RUNTIME_V3_ARCHITECTURE.md): V2 plus the V3 changes of that component.</summary>
        V3,
    }

    /// <summary>How far CPU V2 may rely on the AArch64 procedure call standard (CPU_V2_JIT.md).</summary>
    public enum CpuV2AbiMode
    {
        /// <summary>Architecturally exact: every register and NZCV are kept as the legacy code generator keeps them.</summary>
        Exact,

        /// <summary>NZCV is dead at BL/BLR and RET (no AArch64 calling convention passes or preserves it).</summary>
        Flags,

        /// <summary>Also X8-X17 and V16-V31 (standard convention only; experimental).</summary>
        Temporaries,
    }

    /// <summary>
    /// Runtime V2 component selection (RUNTIME_V2_ARCHITECTURE.md). Every component takes effect at the next game start;
    /// environment variables override the file for benchmarks: BOTWPC_CPU_CORE, BOTWPC_CPU_V2_ABI,
    /// BOTWPC_MEMORY_TRACKING, BOTWPC_HLE_SCHEDULER (legacy|v2|v3), BOTWPC_CPU_V2_FEATURES (hex, experiments only).
    /// The GPU front end stays in <see cref="GraphicsSettings.GpuFrontend"/>.
    /// </summary>
    public class RuntimeSettings
    {
        /// <summary>ARMeilleure code generator of the high-quality tier: legacy or CPU V2.</summary>
        public RuntimeCoreMode CpuCore { get; set; } = RuntimeCoreMode.Legacy;

        public CpuV2AbiMode CpuV2Abi { get; set; } = CpuV2AbiMode.Flags;

        public RuntimeCoreMode MemoryTracking { get; set; } = RuntimeCoreMode.Legacy;

        public RuntimeCoreMode HleScheduler { get; set; } = RuntimeCoreMode.Legacy;
    }

    public class GraphicsSettings
    {
        /// <summary>
        /// GPU command front end: the Ryujinx code paths (legacy) or BOTW-PC's optimized components. Takes effect at the
        /// next game start.
        /// </summary>
        public GpuFrontendMode GpuFrontend { get; set; } = GpuFrontendMode.Legacy;

        /// <summary>
        /// NVIDIA DLSS in place of BotW's own upscale (experimental). The input is the game's render size either way;
        /// the mode only selects the NGX preset. BOTWPC_DLSS overrides it. Takes effect at the next game start.
        /// </summary>
        public DlssMode Dlss { get; set; } = DlssMode.Off;

        private static readonly string _dlssOverride = Environment.GetEnvironmentVariable("BOTWPC_DLSS")?.ToLowerInvariant();
        private static int _version, _cachedVersion = -1;
        private static string _cachedDlss;

        /// <summary>Call after changing <see cref="Dlss"/> on the active settings so the renderer picks it up.</summary>
        public static void NotifyChanged() => System.Threading.Interlocked.Increment(ref _version);

        /// <summary>
        /// Requested DLSS mode in lower case ("off", "quality", ...): BOTWPC_DLSS if set, else the active setting.
        /// Read per draw by the renderer, so cached until <see cref="NotifyChanged"/>.
        /// </summary>
        public static string EffectiveDlss
        {
            get
            {
                int version = System.Threading.Volatile.Read(ref _version);

                if (version != _cachedVersion)
                {
                    _cachedDlss = _dlssOverride ?? BotwPcSettings.Instance.Graphics.Dlss.ToString().ToLowerInvariant();
                    _cachedVersion = version;
                }

                return _cachedDlss;
            }
        }

        /// <summary>BOTWPC_DLSS overrides the setting (the overlay shows the option as fixed).</summary>
        public static bool DlssOverridden => _dlssOverride != null;

        /// <summary>
        /// NVIDIA DLSS Frame Generation through Streamline (experimental, RTX 40/50): 2x inserts one generated frame per
        /// game frame, 3x/4x (RTX 50) two or three. BOTWPC_DLSSG=off|2x|3x|4x overrides it. Takes effect at the next start
        /// (Streamline has to be loaded before Vulkan).
        /// </summary>
        public FrameGenerationMode FrameGeneration { get; set; } = FrameGenerationMode.Off;

        /// <summary>
        /// Shown-frame limit while frame generation runs (0 = none): DLSS-G on Vulkan has no VSync, so keep it under the
        /// display's refresh rate (e.g. 160 on 165 Hz). BOTWPC_DLSSG_FPS overrides it.
        /// </summary>
        public int FrameGenerationFpsLimit { get; set; }

        /// <summary>Shown-frame limit in effect: BOTWPC_DLSSG_FPS if set, else the setting.</summary>
        public static int EffectiveFrameGenerationFpsLimit =>
            int.TryParse(Environment.GetEnvironmentVariable("BOTWPC_DLSSG_FPS"), out int fps) && fps >= 0 ? fps : BotwPcSettings.Instance.Graphics.FrameGenerationFpsLimit;

        /// <summary>BOTWPC_DLSSG overrides the frame-generation setting at start (the overlay says so).</summary>
        public static bool FrameGenerationOverridden => !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("BOTWPC_DLSSG"));

        /// <summary>Generated frames per game frame (0 = off): BOTWPC_DLSSG if set, else the setting.</summary>
        public static int EffectiveFrameGeneration => (Environment.GetEnvironmentVariable("BOTWPC_DLSSG")?.ToLowerInvariant() switch
        {
            "off" or "0" => FrameGenerationMode.Off,
            "on" or "2x" or "2" => FrameGenerationMode.X2,
            "3x" or "3" => FrameGenerationMode.X3,
            "4x" or "4" => FrameGenerationMode.X4,
            _ => BotwPcSettings.Instance.Graphics.FrameGeneration,
        }) switch
        {
            FrameGenerationMode.X2 => 1,
            FrameGenerationMode.X3 => 2,
            FrameGenerationMode.X4 => 3,
            _ => 0,
        };
    }

    public enum FrameGenerationMode
    {
        Off,
        X2,
        X3,
        X4,
    }

    public enum PerformanceHud
    {
        Hidden,
        Basic,
        Detailed,
        Developer,
    }

    /// <summary>In-game overlay menu (L3 + R3 / Insert) and performance HUD.</summary>
    public class OverlaySettings
    {
        /// <summary>The shortcut opens the overlay. Off: L3 / R3 go to the game without any delay.</summary>
        public bool Enabled { get; set; } = true;

        /// <summary>Pause the game (Ryujinx's own pause: guest threads and audio) while the menu is open.</summary>
        public bool PauseGame { get; set; } = true;

        public PerformanceHud Hud { get; set; } = PerformanceHud.Hidden;

        /// <summary>UI size relative to 1080p.</summary>
        public float Scale { get; set; } = 1.0f;

        /// <summary>How long a single stick click waits for the other one (L3 + R3) before it reaches the game.</summary>
        public int ChordWindowMs { get; set; } = 80;

        /// <summary>Menu language: Auto follows Ryujinx's UI language. BOTWPC_OVERLAY_LANG=en|zh overrides it.</summary>
        public MenuLanguage Language { get; set; } = MenuLanguage.Auto;
    }

    public enum MenuLanguage
    {
        Auto,
        English,
        Chinese,
    }

    public class ModSettings
    {
        /// <summary>Start games without any mods, ExeFS patches or cheats.</summary>
        public bool SafeMode { get; set; }

        /// <summary>Name of the last applied mod profile (informational).</summary>
        public string Profile { get; set; } = "Custom";
    }

    /// <summary>
    /// BOTW-PC settings, stored separately from Ryujinx's versioned Config.json in &lt;data&gt;/botw/settings.json.
    /// </summary>
    public class BotwPcSettings
    {
        public ControllerSettings Controller { get; set; } = new();
        public ModSettings Mods { get; set; } = new();
        public MotionSettings Motion { get; set; } = new();
        public GameSettings Game { get; set; } = new();
        public GraphicsSettings Graphics { get; set; } = new();
        public OverlaySettings Overlay { get; set; } = new();
        public RuntimeSettings Runtime { get; set; } = new();

        private static BotwPcSettings _instance;
        private static readonly object _lock = new();

        public static string DirectoryPath => Path.Combine(AppDataManager.BaseDirPath ?? ".", "botw");
        public static string FilePath => Path.Combine(DirectoryPath, "settings.json");

        public static BotwPcSettings Instance
        {
            get
            {
                lock (_lock)
                {
                    return _instance ??= Load(FilePath);
                }
            }
        }

        public static BotwPcSettings Load(string path)
        {
            try
            {
                if (File.Exists(path))
                {
                    return JsonSerializer.Deserialize(File.ReadAllText(path), BotwPcSettingsContext.Default.BotwPcSettings) ?? new();
                }
            }
            catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
            {
                // A broken settings file must never prevent the emulator from starting.
                Logger.Warning?.Print(LogClass.Application, $"BOTW-PC settings unreadable, using defaults: {e.Message}");
            }

            return new();
        }

        /// <summary>A deep copy (through the same JSON form as the file).</summary>
        public BotwPcSettings Clone() =>
            JsonSerializer.Deserialize(JsonSerializer.Serialize(this, BotwPcSettingsContext.Default.BotwPcSettings), BotwPcSettingsContext.Default.BotwPcSettings);

        public void Save(string path = null)
        {
            bool isActive = path == null;
            path ??= FilePath;
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            string temp = path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(this, BotwPcSettingsContext.Default.BotwPcSettings));
            File.Move(temp, path, overwrite: true);

            if (isActive)
            {
                lock (_lock)
                {
                    _instance = this;
                }

                GraphicsSettings.NotifyChanged();
            }
        }
    }

    [JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
        UseStringEnumConverter = true, ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true)]
    [JsonSerializable(typeof(BotwPcSettings))]
    internal partial class BotwPcSettingsContext : JsonSerializerContext
    {
    }
}
