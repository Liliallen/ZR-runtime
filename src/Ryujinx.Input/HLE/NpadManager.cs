using Ryujinx.Common.BotwPc.Overlay;
using Ryujinx.Common.Configuration.Hid;
using Ryujinx.Common.Configuration.Hid.Controller;
using Ryujinx.Common.Configuration.Hid.Keyboard;
using Ryujinx.Common.Logging;
using Ryujinx.HLE.HOS.Services.Hid;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Threading;
using CemuHookClient = Ryujinx.Input.Motion.CemuHook.Client;
using ControllerType = Ryujinx.Common.Configuration.Hid.ControllerType;
using PlayerIndex = Ryujinx.HLE.HOS.Services.Hid.PlayerIndex;
using Switch = Ryujinx.HLE.Switch;

namespace Ryujinx.Input.HLE
{
    public class NpadManager : IDisposable
    {
        private readonly CemuHookClient _cemuHookClient;

        private readonly Lock _lock = new();

        private bool _blockInputUpdates;

        private const int MaxControllers = 9;

        private readonly NpadController[] _controllers;

        private readonly IGamepadDriver _keyboardDriver;
        private readonly IGamepadDriver _gamepadDriver;
        private readonly IGamepadDriver _mouseDriver;
        private bool _isDisposed;

        private List<InputConfig> _inputConfig;

        // BOTW-PC: the configuration as saved by the user; _inputConfig is what runs (see AutoController).
        private List<InputConfig> _savedInputConfig = [];
        private string _autoControllerId;
        private bool _enableKeyboard;

        // BOTW-PC: optional timed input for reproducible benchmark runs.
        private readonly Ryujinx.HLE.BotwPc.InputScript _inputScript = Ryujinx.HLE.BotwPc.InputScript.FromEnvironment();
        private static readonly System.Diagnostics.Stopwatch _overlayClock = System.Diagnostics.Stopwatch.StartNew();

        /// <summary>The benchmark/test input script (BOTWPC_INPUT_SCRIPT), if any; the overlay host reads its key steps.</summary>
        public Ryujinx.HLE.BotwPc.InputScript InputScript => _inputScript;
        private bool _enableMouse;
        private Switch _device;

        public NpadManager(IGamepadDriver keyboardDriver, IGamepadDriver gamepadDriver, IGamepadDriver mouseDriver)
        {
            _controllers = new NpadController[MaxControllers];
            _cemuHookClient = new CemuHookClient(this);

            _keyboardDriver = keyboardDriver;
            _gamepadDriver = gamepadDriver;
            _mouseDriver = mouseDriver;
            _inputConfig = [];

            _gamepadDriver.OnGamepadConnected += HandleOnGamepadConnected;
            _gamepadDriver.OnGamepadDisconnected += HandleOnGamepadDisconnected;
        }

        private void RefreshInputConfigForHLE()
        {
            lock (_lock)
            {
                List<InputConfig> validInputs = [];
                foreach (InputConfig inputConfigEntry in _inputConfig)
                {
                    if (_controllers[(int)inputConfigEntry.PlayerIndex] != null)
                    {
                        validInputs.Add(inputConfigEntry);
                    }
                }

                _device.Hid.RefreshInputConfig(validInputs);
            }
        }

        private void HandleOnGamepadDisconnected(string obj)
        {
            // Force input reload
            lock (_lock)
            {
                // Forcibly disconnect any controllers with this ID.
                for (int i = 0; i < _controllers.Length; i++)
                {
                    if (_controllers[i]?.Id == obj)
                    {
                        _controllers[i]?.Dispose();
                        _controllers[i] = null;
                    }
                }

                if (_device != null)
                {
                    ReloadConfiguration(_savedInputConfig, _enableKeyboard, _enableMouse);
                }
            }
        }

        private void HandleOnGamepadConnected(string id)
        {
            // A pad that is on when the app starts can connect before Initialize; Initialize applies it.
            if (_device == null)
            {
                return;
            }

            // Force input reload
            ReloadConfiguration(_savedInputConfig, _enableKeyboard, _enableMouse);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private bool DriverConfigurationUpdate(ref NpadController controller, InputConfig config)
        {
            IGamepadDriver targetDriver = _gamepadDriver;

            if (config is StandardControllerInputConfig)
            {
                targetDriver = _gamepadDriver;
            }
            else if (config is StandardKeyboardInputConfig)
            {
                targetDriver = _keyboardDriver;
            }

            Debug.Assert(targetDriver != null, "Unknown input configuration!");

            if (controller.GamepadDriver != targetDriver || controller.Id != config.Id)
            {
                return controller.UpdateDriverConfiguration(targetDriver, config);
            }

            return controller.GamepadDriver != null;
        }

        public void ReloadConfiguration(List<InputConfig> inputConfig, bool enableKeyboard, bool enableMouse)
        {
            lock (_lock)
            {
                _savedInputConfig = inputConfig;
                inputConfig = AutoController.Apply(inputConfig, _gamepadDriver, Ryujinx.Common.BotwPc.BotwPcSettings.Instance.Controller);

                string autoId = ReferenceEquals(inputConfig, _savedInputConfig) ? null : inputConfig[0].Id;

                if (autoId != _autoControllerId)
                {
                    _autoControllerId = autoId;
                    Logger.Info?.Print(LogClass.Hid, autoId == null
                        ? "BOTW-PC: Player 1 uses the saved input configuration."
                        : $"BOTW-PC: Player 1 auto-assigned to \"{inputConfig[0].Name}\" ({autoId}).");

                    if (autoId != null)
                    {
                        AutoController.RememberController(inputConfig[0].Name);
                    }
                }

                NpadController[] oldControllers = _controllers.ToArray();

                List<InputConfig> validInputs = [];

                foreach (InputConfig inputConfigEntry in inputConfig)
                {
                    NpadController controller;
                    int index = (int)inputConfigEntry.PlayerIndex;

                    if (oldControllers[index] != null)
                    {
                        // Try reuse the existing controller.
                        controller = oldControllers[index];
                        oldControllers[index] = null;
                    }
                    else
                    {
                        controller = new(_cemuHookClient);
                    }

                    bool isValid = DriverConfigurationUpdate(ref controller, inputConfigEntry);

                    if (!isValid)
                    {
                        _controllers[index] = null;
                        controller.Dispose();
                    }
                    else
                    {
                        _controllers[index] = controller;
                        validInputs.Add(inputConfigEntry);
                    }
                }

                for (int i = 0; i < oldControllers.Length; i++)
                {
                    // Disconnect any controllers that weren't reused by the new configuration.

                    oldControllers[i]?.Dispose();
                    oldControllers[i] = null;
                }

                _inputConfig = inputConfig;
                WarnIfPromptsStale(inputConfig);
                _enableKeyboard = enableKeyboard;
                _enableMouse = enableMouse;

                _device.Hid.RefreshInputConfig(validInputs);
            }
        }

        /// <summary>BOTW-PC: virtual motion for Player 1 (keyboard keys / View-hold on the controller).</summary>
        public Motion.VirtualMotionController VirtualMotion { get; } = new();

        private IKeyboard _motionKeyboard;
        private bool _sharePressed, _captureRequested;

        /// <summary>BOTW-PC: true once after the controller's Share button was pressed.</summary>
        public bool ConsumeCaptureRequest()
        {
            lock (_lock)
            {
                bool requested = _captureRequested;
                _captureRequested = false;
                return requested;
            }
        }

        private static bool IsBound(StandardControllerInputConfig pad, GamepadInputId input) =>
            Ryujinx.Common.BotwPc.ButtonPrompts.FromConfig(pad, Ryujinx.Common.BotwPc.PromptStyle.Xbox, out _)?.Physical.Values.Contains(input) == true;

        private static readonly bool _logMotion = Environment.GetEnvironmentVariable("BOTWPC_MOTION_LOG") == "1";
        private long _lastMotionLog;

        /// <summary>BOTW-PC: BOTWPC_MOTION_LOG=1 logs Player 1's six-axis data as sent to the game, once per second.</summary>
        private void LogMotion(SixAxisInput state)
        {
            if (!_logMotion || Stopwatch.GetElapsedTime(_lastMotionLog).TotalSeconds < 1)
            {
                return;
            }

            _lastMotionLog = Stopwatch.GetTimestamp();
            float[] o = state.Orientation ?? new float[9];
            Logger.Info?.Print(LogClass.Hid, $"BOTW-PC motion: accel {state.Accelerometer} gyro {state.Gyroscope} rotation {state.Rotation} " +
                $"orientation [{o[0]:0.00} {o[1]:0.00} {o[2]:0.00}; {o[3]:0.00} {o[4]:0.00} {o[5]:0.00}; {o[6]:0.00} {o[7]:0.00} {o[8]:0.00}] " +
                $"virtual={VirtualMotion.InUse} pitch/roll/yaw {VirtualMotion.Motion.EulerDegrees}");
        }

        /// <summary>BOTW-PC: picks Player 1's motion source and applies virtual motion when it is used.</summary>
        private void UpdateMotionSource(NpadController controller, InputConfig inputConfig)
        {
            Ryujinx.Common.BotwPc.MotionSettings settings = Ryujinx.Common.BotwPc.BotwPcSettings.Instance.Motion;
            StandardControllerInputConfig pad = inputConfig as StandardControllerInputConfig;

            // Share (SDL Misc1) works as Capture unless it is bound to a Switch button.
            bool share = pad != null && controller.RawState.IsPressed(GamepadButtonInputId.Misc1) && !IsBound(pad, GamepadInputId.Misc1);
            _captureRequested |= share && !_sharePressed;
            _sharePressed = share;
            bool realSensors = pad != null && pad.Motion.EnableMotion &&
                (pad.Motion.MotionBackend == Common.Configuration.Hid.Controller.Motion.MotionInputBackendType.CemuHook || controller.HasMotionSensor);

            if (settings.Source == Ryujinx.Common.BotwPc.MotionSource.Off)
            {
                VirtualMotion.InUse = false;
                controller.ClearMotion();
                return;
            }

            VirtualMotion.InUse = settings.Source == Ryujinx.Common.BotwPc.MotionSource.Virtual ||
                                  (settings.Source == Ryujinx.Common.BotwPc.MotionSource.Auto && !realSensors);

            if (!VirtualMotion.InUse)
            {
                return;
            }

            _motionKeyboard ??= _keyboardDriver?.GetGamepad("0") as IKeyboard;
            bool invertX = pad?.RightJoyconStick.Joystick == Common.Configuration.Hid.Controller.StickInputId.Right && pad.RightJoyconStick.InvertStickX;
            bool invertY = pad?.RightJoyconStick.Joystick == Common.Configuration.Hid.Controller.StickInputId.Right && pad.RightJoyconStick.InvertStickY;

            VirtualMotion.ScriptedRate = _inputScript?.MotionRate() ?? Vector3.Zero;
            VirtualMotion.Update(_motionKeyboard?.GetKeyboardStateSnapshot(), pad != null ? controller.RawState : null, invertX, invertY,
                settings, System.Diagnostics.Stopwatch.GetTimestamp() / (double)System.Diagnostics.Stopwatch.Frequency);
            controller.ApplyVirtualMotion(VirtualMotion);
        }

        /// <summary>
        /// BOTW-PC: in-game prompts are generated when the game starts; a mapping change while it runs needs a restart.
        /// </summary>
        private void WarnIfPromptsStale(List<InputConfig> inputConfig)
        {
            Ryujinx.Common.BotwPc.PromptMapping launch = Ryujinx.Common.BotwPc.ButtonPrompts.LaunchMapping;
            Ryujinx.Common.BotwPc.PromptMapping now = Ryujinx.Common.BotwPc.ButtonPrompts.FromConfig(
                inputConfig.Find(c => c.PlayerIndex == Common.Configuration.Hid.PlayerIndex.Player1),
                Ryujinx.Common.BotwPc.BotwPcSettings.Instance.Controller.Prompts, out _);

            bool stale = _device?.Processes?.ActiveApplication?.ProgramId == Ryujinx.HLE.BotwPc.BotwProfile.TitleId && launch?.Key != now?.Key;

            if (stale && !Ryujinx.Common.BotwPc.ButtonPrompts.Stale)
            {
                Logger.Warning?.Print(LogClass.Hid, "BOTW-PC: the controller mapping changed; in-game button prompts still show the mapping the game " +
                    $"was started with ({Ryujinx.Common.BotwPc.ButtonPrompts.Describe(launch)}). Restart the game to update them.");
            }

            Ryujinx.Common.BotwPc.ButtonPrompts.Stale = stale;
        }

        public void UnblockInputUpdates()
        {
            lock (_lock)
            {
                foreach (InputConfig inputConfig in _inputConfig)
                {
                    _controllers[(int)inputConfig.PlayerIndex]?.GamepadDriver?.Clear();
                }

                _blockInputUpdates = false;
            }
        }

        public bool InputUpdatesBlocked
        {
            get
            {
                lock (_lock)
                    return _blockInputUpdates;
            }
        }

        public void BlockInputUpdates()
        {
            lock (_lock)
            {
                _blockInputUpdates = true;
            }
        }

        public void Initialize(Switch device, List<InputConfig> inputConfig, bool enableKeyboard, bool enableMouse)
        {
            _device = device;
            _device.Configuration.RefreshInputConfig = RefreshInputConfigForHLE;

            ReloadConfiguration(inputConfig, enableKeyboard, enableMouse);
        }

        /// <summary>
        /// BOTW-PC overlay: reports Player 1's physical buttons to the overlay shortcut (L3 + R3) and, while the overlay
        /// has focus, gives the game neutral input. A single stick click is held back for the chord window and then
        /// passed (or replayed), so L3 and R3 keep working in the game.
        /// </summary>
        private void ApplyOverlayGate(NpadController controller, ref GamepadInput inputState)
        {
            GamepadStateSnapshot raw = controller.RawState;
            PadButtons held = _inputScript?.PadButtons() ?? 0;

            foreach ((GamepadButtonInputId id, PadButtons button) in _overlayButtons)
            {
                if (raw.IsPressed(id))
                {
                    held |= button;
                }
            }

            (float lx, float ly) = raw.GetStick(StickInputId.Left);
            held |= (lx > 0.6f ? PadButtons.Right : 0) | (lx < -0.6f ? PadButtons.Left : 0) | (ly > 0.6f ? PadButtons.Up : 0) | (ly < -0.6f ? PadButtons.Down : 0);
            (float rx, float ry) = raw.GetStick(StickInputId.Right);
            PadButtons sticks = OverlayInputGate.Process(held, (rx, ry), _overlayClock.Elapsed.TotalMilliseconds);

            if (OverlayInputGate.Captures)
            {
                inputState = default;

                return;
            }

            // Held back (possible chord) → hidden from the game; replayed tap → shown. Assumes the stick clicks are bound
            // to the game's stick buttons (the default); other bindings only lose the hold-back.
            foreach ((PadButtons button, ControllerKeys key) in ((PadButtons, ControllerKeys)[])[(PadButtons.LS, ControllerKeys.LStick), (PadButtons.RS, ControllerKeys.RStick)])
            {
                if ((held & button) != 0 && (sticks & button) == 0)
                {
                    inputState.Buttons &= ~key;
                }
                else if ((held & button) == 0 && (sticks & button) != 0)
                {
                    inputState.Buttons |= key;
                }
            }
        }

        private static readonly (GamepadButtonInputId, PadButtons)[] _overlayButtons =
        [
            (GamepadButtonInputId.DpadUp, PadButtons.Up), (GamepadButtonInputId.DpadDown, PadButtons.Down),
            (GamepadButtonInputId.DpadLeft, PadButtons.Left), (GamepadButtonInputId.DpadRight, PadButtons.Right),
            (GamepadButtonInputId.A, PadButtons.A), (GamepadButtonInputId.B, PadButtons.B),
            (GamepadButtonInputId.X, PadButtons.X), (GamepadButtonInputId.Y, PadButtons.Y),
            (GamepadButtonInputId.LeftShoulder, PadButtons.LB), (GamepadButtonInputId.RightShoulder, PadButtons.RB),
            (GamepadButtonInputId.LeftStick, PadButtons.LS), (GamepadButtonInputId.RightStick, PadButtons.RS),
            (GamepadButtonInputId.Plus, PadButtons.Menu), (GamepadButtonInputId.Minus, PadButtons.View),
        ];

        public void Update(float aspectRatio = 1)
        {
            lock (_lock)
            {
                List<GamepadInput> hleInputStates = [];
                List<SixAxisInput> hleMotionStates = new(NpadDevices.MaxControllers);

                KeyboardInput? hleKeyboardInput = null;

                foreach (InputConfig inputConfig in _inputConfig)
                {
                    GamepadInput inputState = default;
                    (SixAxisInput, SixAxisInput) motionState = default;

                    NpadController controller = _controllers[(int)inputConfig.PlayerIndex];
                    PlayerIndex playerIndex = (PlayerIndex)inputConfig.PlayerIndex;

                    bool isJoyconPair = false;

                    // Do we allow input updates and is a controller connected?
                    if (!_blockInputUpdates && controller != null)
                    {
                        DriverConfigurationUpdate(ref controller, inputConfig);

                        controller.UpdateUserConfiguration(inputConfig);
                        controller.Update();

                        if (playerIndex == PlayerIndex.Player1)
                        {
                            UpdateMotionSource(controller, inputConfig);
                        }

                        // Paused emulation sends no new vibration values, so stop any running effect instead of leaving it on.
                        controller.UpdateRumble(_device.Hid.Npads.GetRumbleQueue(playerIndex), _device.System.IsPaused);

                        inputState = controller.GetHLEInputState();

                        inputState.Buttons |= _device.Hid.UpdateStickButtons(inputState.LStick, inputState.RStick);

                        if (playerIndex == PlayerIndex.Player1)
                        {
                            ApplyOverlayGate(controller, ref inputState);
                        }

                        isJoyconPair = inputConfig.ControllerType == ControllerType.JoyconPair;

                        SixAxisInput altMotionState = isJoyconPair ? controller.GetHLEMotionState(true) : default;

                        motionState = (controller.GetHLEMotionState(), altMotionState);
                    }
                    else
                    {
                        // Ensure that orientation isn't null
                        motionState.Item1.Orientation = new float[9];
                    }

                    if (playerIndex == PlayerIndex.Player1)
                    {
                        if (!OverlayInputGate.Captures)
                        {
                            _inputScript?.Apply(ref inputState);
                        }
                        LogMotion(motionState.Item1);
                    }

                    inputState.PlayerId = playerIndex;
                    motionState.Item1.PlayerId = playerIndex;

                    hleInputStates.Add(inputState);
                    hleMotionStates.Add(motionState.Item1);

                    if (isJoyconPair && !motionState.Item2.Equals(default))
                    {
                        motionState.Item2.PlayerId = playerIndex;

                        hleMotionStates.Add(motionState.Item2);
                    }
                }

                if (!_blockInputUpdates && _enableKeyboard && !OverlayInputGate.Captures)
                {
                    hleKeyboardInput = NpadController.GetHLEKeyboardInput(_keyboardDriver);
                }

                _device.Hid.Npads.Update(hleInputStates);
                _device.Hid.Npads.UpdateSixAxis(hleMotionStates);

                if (hleKeyboardInput.HasValue)
                {
                    _device.Hid.Keyboard.Update(hleKeyboardInput.Value);
                }

                if (_enableMouse && !OverlayInputGate.Captures)
                {
                    IMouse mouse = _mouseDriver.GetGamepad("0") as IMouse;

                    MouseStateSnapshot mouseInput = IMouse.GetMouseStateSnapshot(mouse);

                    uint buttons = 0;

                    if (mouseInput.IsPressed(MouseButton.Button1))
                    {
                        buttons |= 1 << 0;
                    }

                    if (mouseInput.IsPressed(MouseButton.Button2))
                    {
                        buttons |= 1 << 1;
                    }

                    if (mouseInput.IsPressed(MouseButton.Button3))
                    {
                        buttons |= 1 << 2;
                    }

                    if (mouseInput.IsPressed(MouseButton.Button4))
                    {
                        buttons |= 1 << 3;
                    }

                    if (mouseInput.IsPressed(MouseButton.Button5))
                    {
                        buttons |= 1 << 4;
                    }

                    Vector2 position = IMouse.GetScreenPosition(mouseInput.Position, mouse.ClientSize, aspectRatio);

                    _device.Hid.Mouse.Update((int)position.X, (int)position.Y, buttons, (int)mouseInput.Scroll.X, (int)mouseInput.Scroll.Y, true);
                }
                else
                {
                    _device.Hid.Mouse.Update(0, 0);
                }

                _device.TamperMachine.UpdateInput(hleInputStates);
            }
        }

        internal InputConfig GetPlayerInputConfigByIndex(int index)
        {
            lock (_lock)
            {
                return _inputConfig.FirstOrDefault(x => x.PlayerIndex == (Common.Configuration.Hid.PlayerIndex)index);
            }
        }

        protected virtual void Dispose(bool disposing)
        {
            if (disposing)
            {
                lock (_lock)
                {
                    if (!_isDisposed)
                    {
                        _cemuHookClient.Dispose();

                        _gamepadDriver.OnGamepadConnected -= HandleOnGamepadConnected;
                        _gamepadDriver.OnGamepadDisconnected -= HandleOnGamepadDisconnected;

                        for (int i = 0; i < _controllers.Length; i++)
                        {
                            _controllers[i]?.Dispose();
                        }

                        _isDisposed = true;
                    }
                }
            }
        }

        public void Dispose()
        {
            GC.SuppressFinalize(this);
            Dispose(true);
        }
    }
}
