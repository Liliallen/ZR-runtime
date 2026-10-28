using Avalonia.Threading;
using Ryujinx.Ava.Systems.Configuration;
using Ryujinx.Common.BotwPc;
using Ryujinx.Common.BotwPc.Overlay;
using Ryujinx.Common.Configuration;
using Ryujinx.Common.Logging;
using Ryujinx.HLE.BotwPc.Mods;
using Ryujinx.HLE.BotwPc.Overlay;
using Ryujinx.Ava.Input;
using Ryujinx.Input;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

namespace Ryujinx.Ava.Systems
{
    /// <summary>
    /// BOTW-PC in-game overlay host: opens/closes the menu (L3 + R3 on the physical controller, Insert on the keyboard),
    /// routes controller / keyboard / mouse input to it, pauses the game while it is open (optional), applies settings
    /// to the shared configuration (botw/settings.json and Ryujinx's Config.json, the same objects the launcher and the
    /// settings window use) and paints the menu and the performance HUD for the presenter. Runs on the host input thread
    /// (AppHost.MainLoop), never on the render thread.
    /// </summary>
    internal sealed class BotwOverlayHost : IDisposable
    {
        public sealed class Callbacks
        {
            public Func<HLE.Switch> Device { get; init; }
            public Action Pause { get; init; }
            public Action Resume { get; init; }
            public Action ReloadInput { get; init; }
            public Func<bool> IsFullscreen { get; init; }
            public Action ToggleFullscreen { get; init; }
            public Action OpenControllerTest { get; init; }
            public Func<string> ControllerName { get; init; }
        }

        private readonly Callbacks _cb;
        private readonly OverlayPainter _painter = new();
        private readonly NavRepeat _padNav = new(), _keyNav = new();
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private readonly bool _uiZh;

        private OverlayMenu _menu;
        private bool _open, _closing, _pausedByUs, _keyInsertDown, _mouseDown;
        private int _paintedVersion = -1;
        private long _nextHudPaint;
        private BotwPcSettings _snapshot;
        private readonly HudSampler _hud = new();

        public bool IsOpen => _open;

        public BotwOverlayHost(Callbacks callbacks)
        {
            _cb = callbacks;
            _uiZh = (ConfigurationState.Instance.UI.LanguageCode.Value ?? "").StartsWith("zh", StringComparison.OrdinalIgnoreCase);
            OverlayInputGate.Enabled = Settings.Overlay.Enabled;
            OverlayInputGate.ChordWindowMs = Settings.Overlay.ChordWindowMs;
        }

        private static BotwPcSettings Settings => BotwPcSettings.Instance;

        private string T(string en, string zh) => Chinese ? zh : en;

        // Menu language: BOTWPC_OVERLAY_LANG (en|zh, e.g. from a launcher), else the setting, else Ryujinx's UI language.
        private static readonly string _languageOverride = Environment.GetEnvironmentVariable("BOTWPC_OVERLAY_LANG")?.ToLowerInvariant();

        private bool Chinese => _languageOverride switch
        {
            "en" => false,
            "zh" => true,
            _ => Settings.Overlay.Language switch
            {
                MenuLanguage.English => false,
                MenuLanguage.Chinese => true,
                _ => _uiZh,
            },
        };

        // ------------------------------------------------------------------ per poll

        /// <summary>Host input thread, every poll (~1 ms).</summary>
        public void Update(IKeyboard keyboard, AvaloniaMouseDriver mouse, HLE.BotwPc.InputScript script)
        {
            double now = _clock.Elapsed.TotalMilliseconds;
            HashSet<string> scriptKeys = script == null ? [] : [.. script.Keys()];

            bool Key(Key key) => (keyboard?.IsPressed(key) ?? false) || scriptKeys.Contains(key.ToString());

            // Toggle: L3 + R3 (gate), Insert (edge).
            long toggles = OverlayInputGate.TakeToggleRequests();
            bool insert = Key(Ryujinx.Input.Key.Insert);

            if (insert && !_keyInsertDown && Settings.Overlay.Enabled)
            {
                toggles++;
            }

            _keyInsertDown = insert;

            if (toggles % 2 == 1)
            {
                if (_open)
                {
                    Close();
                }
                else
                {
                    Open();
                }
            }

            if (_closing && OverlayInputGate.Held == PadButtons.None && !AnyNavKey(Key))
            {
                // Released everything that closed the menu: only now does the game get input again (no leaked B).
                _closing = false;
                OverlayInputGate.Captures = false;
            }

            if (_open)
            {
                PadButtons keys = PadButtons.None;

                if (Key(Ryujinx.Input.Key.Up)) keys |= PadButtons.Up;
                if (Key(Ryujinx.Input.Key.Down)) keys |= PadButtons.Down;
                if (Key(Ryujinx.Input.Key.Left)) keys |= PadButtons.Left;
                if (Key(Ryujinx.Input.Key.Right)) keys |= PadButtons.Right;
                if (Key(Ryujinx.Input.Key.Enter) || Key(Ryujinx.Input.Key.KeypadEnter) || Key(Ryujinx.Input.Key.Space)) keys |= PadButtons.A;
                if (Key(Ryujinx.Input.Key.Escape) || Key(Ryujinx.Input.Key.BackSpace)) keys |= PadButtons.B;
                if (Key(Ryujinx.Input.Key.PageUp)) keys |= PadButtons.LB;
                if (Key(Ryujinx.Input.Key.PageDown) || Key(Ryujinx.Input.Key.Tab)) keys |= PadButtons.RB;

                // D-pad and left stick (already folded into Held by the gate) navigate; the right stick scrolls rows.
                PadButtons pad = OverlayInputGate.Held;
                (float _, float ry) = OverlayInputGate.RightStick;

                if (ry > 0.6f) pad |= PadButtons.Up;
                if (ry < -0.6f) pad |= PadButtons.Down;

                bool close = _menu.HandlePresses(_padNav.Update(pad, now)) | _menu.HandlePresses(_keyNav.Update(keys, now));
                HandleMouse(mouse, script?.Mouse());

                if (close)
                {
                    Close();
                }
            }

            Paint();
        }

        private static bool AnyNavKey(Func<Key, bool> key) =>
            key(Ryujinx.Input.Key.Escape) || key(Ryujinx.Input.Key.BackSpace) || key(Ryujinx.Input.Key.Enter) || key(Ryujinx.Input.Key.Insert);

        private (float X, float Y) _lastMouse = (float.NaN, float.NaN);

        private void HandleMouse(AvaloniaMouseDriver mouse, (float X, float Y, bool Click)? scripted)
        {
            float x, y;
            bool down;

            if (scripted is { } s)
            {
                (x, y, down) = (s.X * OverlayFrame.TargetWidth, s.Y * OverlayFrame.TargetHeight, s.Click);
            }
            else
            {
                System.Drawing.Size client = mouse?.GetClientSize() ?? default;

                if (client.Width <= 0 || client.Height <= 0)
                {
                    return;
                }

                x = mouse.CurrentPosition.X * OverlayFrame.TargetWidth / client.Width;
                y = mouse.CurrentPosition.Y * OverlayFrame.TargetHeight / client.Height;
                down = mouse.IsButtonPressed(MouseButton.Button1);
            }

            // Hover selects only when the mouse moves, so it does not fight the controller.
            bool moved = x != _lastMouse.X || y != _lastMouse.Y;
            _lastMouse = (x, y);

            foreach ((int row, SkiaSharp.SKRect rect) in _painter.RowRects)
            {
                if (rect.Contains(x, y))
                {
                    if (moved || (down && !_mouseDown))
                    {
                        _menu.Select(row);
                    }

                    if (down && !_mouseDown)
                    {
                        _menu.Confirm();
                    }
                }
            }

            if (down && !_mouseDown)
            {
                foreach ((int page, SkiaSharp.SKRect rect) in _painter.TabRects)
                {
                    if (rect.Contains(x, y))
                    {
                        _menu.SetPage(page);
                    }
                }
            }

            _mouseDown = down;

            if (scripted == null && mouse.Scroll.Y != 0)
            {
                _menu.Move(mouse.Scroll.Y > 0 ? -1 : 1);
            }
        }

        // ------------------------------------------------------------------ open / close

        private void Open()
        {
            _menu = BuildMenu();
            _menu.SetPage(0);
            _snapshot = Settings.Clone();
            _open = true;
            _closing = false;
            OverlayInputGate.Captures = true;

            if (Settings.Overlay.PauseGame && _cb.Device()?.System?.IsPaused == false)
            {
                _cb.Pause();
                _pausedByUs = true;
            }

            Logger.Info?.Print(LogClass.Application, $"BOTW-PC overlay: opened (pause game: {Settings.Overlay.PauseGame}).");
        }

        private void Close()
        {
            _open = false;
            _closing = true; // input stays with the overlay until the closing buttons are released

            if (_pausedByUs)
            {
                _cb.Resume();
                _pausedByUs = false;
            }

            if (_menu.Changed)
            {
                Save();
            }

            _paintedVersion = -1;
            Logger.Info?.Print(LogClass.Application, "BOTW-PC overlay: closed.");
        }

        private void Save()
        {
            try
            {
                Settings.Save();
                Dispatcher.UIThread.Post(() => UI.ViewModels.MainWindowViewModel.SaveConfig());
                _menu.ResetChanged();
            }
            catch (Exception e) when (e is System.IO.IOException or UnauthorizedAccessException)
            {
                Logger.Error?.Print(LogClass.Application, $"BOTW-PC overlay: settings not saved: {e.Message}");
            }
        }

        // ------------------------------------------------------------------ painting

        private void Paint()
        {
            long nowTicks = Stopwatch.GetTimestamp();
            PerformanceHud hudMode = Settings.Overlay.Hud;
            bool hudDue = nowTicks >= _nextHudPaint;
            HudData hud = null;

            if ((hudMode != PerformanceHud.Hidden || _open) && hudDue)
            {
                _nextHudPaint = nowTicks + Stopwatch.Frequency / 4; // 4 Hz
                hud = _hud.Sample(_cb.Device(), (HudMode)hudMode, TargetFps());
                _menu?.Touch(); // measured values on the open page change
            }

            if (_open)
            {
                if (_menu.Version == _paintedVersion && !hudDue)
                {
                    return;
                }

                hud = hudMode == PerformanceHud.Hidden ? null : _hud.Last;
                _paintedVersion = _menu.Version;
                byte[] pixels = _painter.PaintMenu(OverlayFrame.TargetWidth, OverlayFrame.TargetHeight, _menu, hud, Settings.Overlay.Scale, Hints());
                OverlayFrame.Publish(pixels, OverlayFrame.TargetWidth, OverlayFrame.TargetHeight);

                if (hudDue)
                {
                    _nextHudPaint = nowTicks + Stopwatch.Frequency / 4;
                }

                return;
            }

            if (hudMode == PerformanceHud.Hidden)
            {
                OverlayFrame.Hide();

                return;
            }

            if (hud != null)
            {
                byte[] pixels = _painter.PaintHud(OverlayFrame.TargetHeight, hud, Settings.Overlay.Scale, out int w, out int h, out int x, out int y);
                OverlayFrame.Publish(pixels, w, h, x, y);
            }
        }

        private string Hints() => T("A  Select     B  Back / Close     LB / RB  Page     ← →  Change     L3 + R3  Close",
                                    "A 選擇　　B 返回／關閉　　LB／RB 切換頁面　　← → 調整　　L3 + R3 關閉");

        private float TargetFps()
        {
            ConfigurationState.GraphicsSection g = ConfigurationState.Instance.Graphics;

            return g.VSyncMode.Value switch
            {
                VSyncMode.Switch => 30,
                VSyncMode.Custom when g.EnableCustomVSyncInterval.Value => g.CustomVSyncInterval.Value / 2f,
                _ => 0,
            };
        }

        // ------------------------------------------------------------------ pages

        private OverlayItem Choice<T>(string label, T[] options, Func<T> get, Action<T> set, Func<T, string> name, Func<string> note,
            ApplyKind apply = ApplyKind.Live, Func<bool> enabled = null) => new()
            {
                Label = label,
                Value = () => name(get()),
                Change = d => set(OverlayMenu.Cycle(options, get(), d)),
                Note = note,
                Apply = apply,
                Enabled = enabled ?? (() => true),
            };

        private OverlayItem Toggle(string label, Func<bool> get, Action<bool> set, Func<string> note, ApplyKind apply = ApplyKind.Live) =>
            Choice(label, [false, true], get, set, v => v ? T("On", "開") : T("Off", "關"), note, apply);

        private OverlayItem Slider(string label, float min, float max, float step, Func<float> get, Action<float> set, string format, Func<string> note) => new()
        {
            Label = label,
            Value = () => get().ToString(format),
            Change = d => set(MathF.Round(Math.Clamp(get() + d * step, min, max) / step) * step),
            Note = note,
        };

        private OverlayItem Info(string label, Func<string> value, Func<string> note = null) => new()
        {
            Label = label,
            Value = value,
            Note = note ?? (() => ""),
            Apply = ApplyKind.Info,
        };

        private OverlayItem Header(string label) => new() { Label = label, IsHeader = true, Apply = ApplyKind.Info };

        private OverlayMenu BuildMenu()
        {
            OverlayMenu menu = new();
            menu.Pages.Add(GraphicsPage());
            menu.Pages.Add(FrameRatePage());
            menu.Pages.Add(PerformancePage());
            menu.Pages.Add(ControllerPage());
            menu.Pages.Add(MotionPage());
            menu.Pages.Add(ModsPage());
            menu.Pages.Add(SystemPage(menu));

            return menu;
        }

        private static ConfigurationState.GraphicsSection Graphics => ConfigurationState.Instance.Graphics;

        /// <summary>Ryujinx configuration values raise events that touch the UI, so they are set on the UI thread.</summary>
        private static void OnUi(Action action) => Dispatcher.UIThread.Invoke(action);

        private OverlayPage GraphicsPage()
        {
            DlssMode[] modes = [DlssMode.Off, DlssMode.Quality, DlssMode.Balanced, DlssMode.Performance];

            string DlssName(DlssMode m) => m switch
            {
                DlssMode.Off => T("Off (game's upscale)", "關閉（遊戲原本的放大）"),
                _ => "DLSS " + m switch
                {
                    DlssMode.Quality => T("Quality", "品質"),
                    DlssMode.Balanced => T("Balanced", "平衡"),
                    _ => T("Performance", "效能"),
                },
            };

            string DlssNote()
            {
                if (GraphicsSettings.DlssOverridden)
                {
                    return T("Fixed by the BOTWPC_DLSS environment variable.", "由環境變數 BOTWPC_DLSS 固定。");
                }

                if (!DlssRuntime.Available)
                {
                    return T($"Not available: {DlssRuntime.Status}.", $"無法使用：{DlssRuntime.Status}。");
                }

                string state = Settings.Graphics.Dlss == DlssMode.Off ? T("off", "關閉")
                    : DlssRuntime.RanRecently ? T($"running, {DlssRuntime.Sizes}", $"運作中，{DlssRuntime.Sizes}")
                    : T($"selected, not running now ({DlssRuntime.Status}; e.g. menus, or a 1080p patch removed the upscale pass)",
                        $"已選擇但目前沒有運作（{DlssRuntime.Status}；例如選單畫面，或 1080p 補丁移除了放大步驟）");

                return T($"Experimental: trails behind moving foliage/Link on fast turns. Input is the game's own render size. Now: {state}.",
                         $"實驗性：快速轉鏡頭時植物和 Link 身後會有殘影。輸入是遊戲自己的渲染解析度。目前：{state}。");
            }

            FrameGenerationMode[] fgModes = [FrameGenerationMode.Off, FrameGenerationMode.X2, FrameGenerationMode.X3, FrameGenerationMode.X4];
            int[] fgLimits = [0, 120, 144, 160, 180, 240];

            // Live while Streamline is loaded (it has to be loaded before Vulkan, i.e. at game start); saved either way.
            FrameGenerationMode CurrentFrameGeneration() => DlssRuntime.FrameGenAvailable
                ? (FrameGenerationMode)DlssRuntime.FrameGenFrames
                : Settings.Graphics.FrameGeneration;

            void SetFrameGeneration(FrameGenerationMode v)
            {
                Settings.Graphics.FrameGeneration = v;

                if (DlssRuntime.FrameGenAvailable)
                {
                    DlssRuntime.FrameGenFrames = (int)v;
                }
            }

            string FrameGenerationState()
            {
                string status = DlssRuntime.FrameGenStatus ?? "";

                return status.StartsWith("on") ? T($"running ({DlssRuntime.FrameGenFrames + 1}×)", $"運作中（{DlssRuntime.FrameGenFrames + 1} 倍）")
                    : status.StartsWith("paused") ? T("paused (menu, map or loading screen)", "暫停中（選單、地圖或讀取畫面）")
                    : status == "off" ? T("off", "關閉")
                    : status == "ready" ? T("ready", "待命")
                    : status;
            }

            string FrameGenerationNote()
            {
                string start = GraphicsSettings.FrameGenerationOverridden
                    ? T(" The launcher sets BOTWPC_DLSSG, which decides the mode at the next start.", "啟動檔設定了 BOTWPC_DLSSG，下次啟動時以它為準。")
                    : "";

                if (!DlssRuntime.FrameGenAvailable)
                {
                    return T($"Takes effect at the next start (frame generation was not loaded: {DlssRuntime.FrameGenStatus}).{start}",
                             $"下次啟動生效（這次啟動時沒有載入補幀：{DlssRuntime.FrameGenStatus}）。{start}");
                }

                return T($"NVIDIA DLSS inserts generated frames between game frames (RTX 50: up to 4×). Smoother, about one frame more input lag. Now: {FrameGenerationState()}.{start}",
                         $"NVIDIA DLSS 在遊戲幀之間插入 AI 生成的畫面（RTX 50 最多 4 倍）。更順，但按鍵延遲約多一幀；倍數越高，快速移動的物體越容易扭曲。目前：{FrameGenerationState()}。{start}");
            }

            ScalingFilter[] filters = [ScalingFilter.Bilinear, ScalingFilter.Nearest, ScalingFilter.Fsr, ScalingFilter.Area];
            AntiAliasing[] aa = [AntiAliasing.None, AntiAliasing.Fxaa, AntiAliasing.SmaaLow, AntiAliasing.SmaaMedium, AntiAliasing.SmaaHigh, AntiAliasing.SmaaUltra];

            return new OverlayPage
            {
                Title = T("Graphics", "圖形"),
                Items =
                [
                    Header(T("UPSCALING", "放大")),
                    Choice(T("Upscaler", "放大方式"), modes, () => Settings.Graphics.Dlss,
                        v => { Settings.Graphics.Dlss = v; GraphicsSettings.NotifyChanged(); }, DlssName, DlssNote,
                        enabled: () => DlssRuntime.Available && !GraphicsSettings.DlssOverridden),
                    Info("DLAA", () => T("not applicable", "不適用"),
                        () => T("BotW never renders at the output size, so there is no input for DLAA.", "BotW 不會以輸出解析度渲染，DLAA 沒有可用的輸入。")),
                    Choice(T("Output filter", "輸出縮放濾鏡"), filters, () => Graphics.ScalingFilter.Value, v => OnUi(() => Graphics.ScalingFilter.Value = v),
                        v => v switch { ScalingFilter.Fsr => "AMD FSR 1", ScalingFilter.Nearest => T("Nearest", "最近鄰"), ScalingFilter.Area => T("Area", "區域"), _ => T("Bilinear", "雙線性") },
                        () => T("Scales the finished frame to the window (Ryujinx). FSR 1 is a spatial filter, not temporal reconstruction.",
                                "把完成的畫面縮放到視窗大小（Ryujinx 內建）。FSR 1 是空間濾鏡，不是時間性重建。")),
                    new OverlayItem
                    {
                        Label = T("FSR sharpness", "FSR 銳利度"),
                        Value = () => $"{Graphics.ScalingFilterLevel.Value} %",
                        Change = d => OnUi(() => Graphics.ScalingFilterLevel.Value = Math.Clamp(Graphics.ScalingFilterLevel.Value + d * 10, 0, 100)),
                        Enabled = () => Graphics.ScalingFilter.Value == ScalingFilter.Fsr,
                        Note = () => T("Only with the FSR 1 output filter.", "只在輸出濾鏡為 FSR 1 時有效。"),
                    },
                    Header(T("FRAME GENERATION", "補幀")),
                    Choice(T("DLSS Frame Generation", "DLSS 補幀"), fgModes, CurrentFrameGeneration, SetFrameGeneration,
                        v => v switch { FrameGenerationMode.X2 => "2×", FrameGenerationMode.X3 => "3×", FrameGenerationMode.X4 => "4×", _ => T("Off", "關閉") },
                        FrameGenerationNote),
                    Choice(T("Shown-frame limit", "畫面幀數上限"), fgLimits, () => DlssRuntime.FrameGenAvailable ? DlssRuntime.FrameGenFpsLimit : Settings.Graphics.FrameGenerationFpsLimit,
                        v => { Settings.Graphics.FrameGenerationFpsLimit = v; DlssRuntime.FrameGenFpsLimit = v; },
                        v => v == 0 ? T("None", "不限制") : $"{v} FPS",
                        () => T("Vulkan frame generation has no VSync: keep this under the display's refresh rate or the image tears. Game FPS = limit ÷ multiplier (160 at 2× = 80 game FPS).",
                                "Vulkan 的補幀不能開 VSync：設在螢幕更新率以下，否則畫面會撕裂。遊戲幀數 = 上限 ÷ 倍數（160、2 倍 = 遊戲 80 幀）。")),
                    Info(T("Frames", "幀數"), () => _hud.FrameGenerationText(T),
                        () => T("Generation pauses in menus (including this one), the map, loading screens and when the game window is not in front.",
                                "選單（包括這個選單）、地圖、讀取畫面，以及遊戲視窗不在最前面時，補幀會暫停。")),
                    Header(T("IMAGE", "畫面")),
                    Choice(T("Resolution scale", "解析度倍率"), [1, 2, 3, 4], () => Graphics.ResScale.Value, v => OnUi(() => Graphics.ResScale.Value = v), v => $"{v}×",
                        () => T("Ryujinx renders the game's targets at this multiple (DLSS input grows with it). Costs GPU time.",
                                "Ryujinx 以這個倍數渲染遊戲畫面（DLSS 的輸入也會跟著變大），會增加 GPU 負擔。")),
                    Choice(T("Anti-aliasing (post)", "反鋸齒（後處理）"), aa, () => Graphics.AntiAliasing.Value, v => OnUi(() => Graphics.AntiAliasing.Value = v),
                        v => v.ToString(), () => T("Ryujinx's FXAA/SMAA on the finished frame.", "Ryujinx 對完成畫面套用的 FXAA／SMAA。")),
                    Choice(T("Display mode", "顯示模式"), [false, true], () => _cb.IsFullscreen(), _ => _cb.ToggleFullscreen(),
                        v => v ? T("Fullscreen", "全螢幕") : T("Windowed", "視窗"), () => T("Window or fullscreen on the current display.", "在目前螢幕上切換視窗或全螢幕。")),
                    Info(T("Output resolution", "輸出解析度"), () => $"{OverlayFrame.TargetWidth} × {OverlayFrame.TargetHeight}",
                        () => T("The window's size in pixels (change it with the window or display mode).", "視窗的實際像素大小（隨視窗或顯示模式改變）。")),
                    Info(T("Shader compilation", "Shader 編譯"), () => _hud.ShaderActivity(T, OverlayFrame.Visible),
                        () => T("Pipelines/shaders compiled in the last seconds; stutter while this is busy is the cache filling.",
                                "最近幾秒編譯的 shader／管線；這裡忙碌時的卡頓是快取正在建立。")),
                ],
            };
        }

        private OverlayPage FrameRatePage()
        {
            // BotW presents every 2nd emulated VBlank: custom refresh 120/180/240 Hz = 60/90/120 FPS.
            int[] targets = [30, 60, 90, 120, 0];

            int Current() => Graphics.VSyncMode.Value switch
            {
                VSyncMode.Switch => 30,
                VSyncMode.Unbounded => 0,
                _ => Graphics.CustomVSyncInterval.Value / 2,
            };

            void Set(int fps) => OnUi(() =>
            {
                switch (fps)
                {
                    case 30:
                        Graphics.EnableCustomVSyncInterval.Value = false;
                        Graphics.VSyncMode.Value = VSyncMode.Switch;
                        break;
                    case 0:
                        Graphics.EnableCustomVSyncInterval.Value = false;
                        Graphics.VSyncMode.Value = VSyncMode.Unbounded;
                        break;
                    default:
                        Graphics.CustomVSyncInterval.Value = fps * 2;
                        Graphics.EnableCustomVSyncInterval.Value = true;
                        Graphics.VSyncMode.Value = VSyncMode.Custom;
                        break;
                }
            });

            return new OverlayPage
            {
                Title = T("Frame rate", "幀率"),
                Items =
                [
                    Choice(T("Target", "目標"), targets, Current, Set,
                        v => v switch { 0 => T("Unbounded", "不限制"), 120 => T("120 (experimental)", "120（實驗性）"), _ => v.ToString() },
                        () => T("Game timing and UI animations follow real time at every setting (no speed-up). 120 is not reached in all scenes yet.",
                                "所有設定下遊戲時間與介面動畫都依實際時間計算（不會加速）。120 目前還不是所有場景都達得到。")),
                    Info(T("Measured", "實測"), () => _hud.MeasuredText(T),
                        () => _hud.BelowTarget(TargetFps())
                            ? T("This scene does not sustain the selected rate.", "這個場景達不到選擇的幀率。")
                            : T("Game frames per second (the game's own presents).", "遊戲每秒實際送出的畫面數。")),
                    Info(T("Game speed", "遊戲速度"), () => _hud.GameSpeedText(),
                        () => T("Game time per real second (1.00 = correct). Paused: no change.", "每一秒實際時間對應的遊戲時間（1.00 為正確）。"
                            )),
                ],
            };
        }

        private OverlayPage PerformancePage() => new()
        {
            Title = T("Performance", "效能"),
            Items =
            [
                Choice(T("Performance HUD", "效能顯示"), Enum.GetValues<PerformanceHud>(), () => Settings.Overlay.Hud, v => Settings.Overlay.Hud = v,
                    v => v switch
                    {
                        PerformanceHud.Basic => T("FPS", "FPS"),
                        PerformanceHud.Detailed => T("Detailed", "詳細"),
                        PerformanceHud.Developer => T("Developer", "開發者"),
                        _ => T("Hidden", "隱藏"),
                    },
                    () => T("Top-left corner. Detailed adds the frame-time graph and GPU/CPU times; Developer adds raw counters.",
                            "顯示在左上角。詳細模式加上幀時間圖與 GPU／CPU 時間；開發者模式再加上原始計數。")),
                Slider(T("Overlay size", "介面大小"), 0.75f, 1.5f, 0.125f, () => Settings.Overlay.Scale, v => Settings.Overlay.Scale = v, "0.00×",
                    () => T("Relative to 1080p; the overlay always renders at the output resolution.", "相對於 1080p；介面永遠以輸出解析度繪製。")),
            ],
        };

        private OverlayPage ControllerPage()
        {
            ControllerSettings c = Settings.Controller;

            void Live(Action change)
            {
                change();
                _cb.ReloadInput();
            }

            return new OverlayPage
            {
                Title = T("Controller", "手把"),
                Items =
                [
                    Info(T("Player 1", "玩家 1"), () => _cb.ControllerName() ?? T("keyboard", "鍵盤")),
                    Choice(T("Button layout", "按鍵配置"), [ButtonLayout.Nintendo, ButtonLayout.Xbox], () => c.Layout, v => Live(() => c.Layout = v),
                        v => v == ButtonLayout.Nintendo ? T("Position (A = bottom)", "依位置（A 在下）") : T("Xbox labels", "依 Xbox 標示"),
                        () => ButtonPrompts.Stale
                            ? T("Input changed now. The on-screen button glyphs still show the old mapping: restart the game to update them.",
                                "輸入已立即改變，但畫面上的按鍵圖示仍是舊的配置：重新啟動遊戲才會更新。")
                            : T("Position: the physical bottom button is the game's B. Input changes now; glyphs follow at the next game start.",
                                "依位置：實體下方按鍵就是遊戲的 B。輸入立即改變；按鍵圖示在下次啟動遊戲時更新。")),
                    Choice(T("Button prompts", "按鍵提示圖示"), [PromptStyle.Auto, PromptStyle.Xbox, PromptStyle.Original], () => c.Prompts, v => c.Prompts = v,
                        v => v.ToString(), () => T("Which glyphs the game shows.", "遊戲裡顯示哪一種按鍵圖示。"), ApplyKind.GameRestart),
                    Info(T("Prompt status", "提示狀態"), () => ButtonPrompts.Stale ? T("restart needed", "需要重新啟動") : T("matches the mapping", "與配置一致")),
                    Slider(T("Left stick deadzone", "左搖桿死區"), 0f, 0.5f, 0.02f, () => c.DeadzoneLeft, v => Live(() => c.DeadzoneLeft = v), "0.00", () => ""),
                    Slider(T("Right stick deadzone", "右搖桿死區"), 0f, 0.5f, 0.02f, () => c.DeadzoneRight, v => Live(() => c.DeadzoneRight = v), "0.00", () => ""),
                    Slider(T("Camera response curve", "鏡頭反應曲線"), 0.5f, 3f, 0.1f, () => c.CameraCurve, v => Live(() => c.CameraCurve = v), "0.0",
                        () => T("1.0 = linear; higher = finer control near the centre.", "1.0 為線性；越高中心附近越細膩。")),
                    Slider(T("Camera speed (stick range)", "鏡頭速度（搖桿幅度）"), 0.5f, 1.5f, 0.05f, () => c.RangeRight, v => Live(() => c.RangeRight = v), "0.00",
                        () => T("Scales the right stick before the game reads it.", "在遊戲讀取前縮放右搖桿。")),
                    Toggle(T("Invert camera X", "鏡頭 X 反轉"), () => c.InvertCameraX, v => Live(() => c.InvertCameraX = v), () => ""),
                    Toggle(T("Invert camera Y", "鏡頭 Y 反轉"), () => c.InvertCameraY, v => Live(() => c.InvertCameraY = v), () => ""),
                    Slider(T("Trigger threshold", "扳機觸發點"), 0.1f, 0.9f, 0.05f, () => c.TriggerThreshold, v => Live(() => c.TriggerThreshold = v), "0.00", () => ""),
                    Toggle(T("Vibration", "震動"), () => c.Rumble, v => Live(() => c.Rumble = v), () => ""),
                    Slider(T("Vibration strength", "震動強度"), 0f, 1f, 0.1f, () => c.RumbleStrength, v => Live(() => c.RumbleStrength = v), "0.0", () => ""),
                    new OverlayItem
                    {
                        Label = T("Open controller test window", "開啟手把測試視窗"),
                        Activate = () => _cb.OpenControllerTest(),
                        Note = () => T("Opens on the desktop (outside the game window).", "會在桌面上開啟（遊戲視窗之外）。"),
                        Apply = ApplyKind.Info,
                    },
                ],
            };
        }

        private OverlayPage MotionPage()
        {
            MotionSettings m = Settings.Motion;

            return new OverlayPage
            {
                Title = T("Motion", "體感"),
                Items =
                [
                    Choice(T("Motion source", "體感來源"), Enum.GetValues<MotionSource>(), () => m.Source, v => m.Source = v, v => v.ToString(),
                        () => T("Auto: the controller's gyroscope if it has one, else the virtual gyroscope.", "自動：手把有陀螺儀就用，否則使用虛擬陀螺儀。")),
                    Toggle(T("Keyboard virtual gyroscope", "鍵盤虛擬陀螺儀"), () => m.Keyboard, v => m.Keyboard = v,
                        () => T($"F8 toggles; arrows pitch/roll, Q/E yaw, R recenter, Shift fine.", "F8 切換；方向鍵俯仰／翻滾，Q／E 偏擺，R 回正，Shift 微調。")),
                    Toggle(T("Xbox virtual gyroscope (hold View)", "Xbox 虛擬陀螺儀（按住 View）"), () => m.Controller, v => m.Controller = v,
                        () => T("Hold View: right stick pitch/roll, LB/RB yaw, R3 recenter, LT fine. A short tap is still Minus.",
                                "按住 View：右搖桿俯仰／翻滾，LB／RB 偏擺，R3 回正，LT 微調。短按仍是減號鍵。")),
                    Slider(T("Rotation speed", "旋轉速度"), 15f, 360f, 15f, () => m.RateDegreesPerSecond, v => m.RateDegreesPerSecond = v, "0 °/s", () => ""),
                    Slider(T("Fine adjustment factor", "微調倍率"), 0.05f, 1f, 0.05f, () => m.FineFactor, v => m.FineFactor = v, "0.00", () => ""),
                    Toggle(T("Invert pitch", "俯仰反轉"), () => m.InvertPitch, v => m.InvertPitch = v, () => ""),
                    Toggle(T("Invert roll", "翻滾反轉"), () => m.InvertRoll, v => m.InvertRoll = v, () => ""),
                    Toggle(T("Invert yaw", "偏擺反轉"), () => m.InvertYaw, v => m.InvertYaw = v, () => ""),
                    Slider(T("View hold threshold", "View 長按判定"), 100f, 800f, 50f, () => m.HoldThresholdMs, v => m.HoldThresholdMs = (int)v, "0 ms", () => ""),
                    new OverlayItem
                    {
                        Label = T("Recenter now", "立即回正"),
                        Activate = () => VirtualMotionRecenter?.Invoke(),
                        Note = () => T("Resets the virtual controller to flat.", "把虛擬手把回到水平。"),
                        Apply = ApplyKind.Info,
                    },
                ],
            };
        }

        /// <summary>Set by AppHost: recenters the virtual motion controller.</summary>
        public Action VirtualMotionRecenter { get; init; }

        private OverlayPage ModsPage()
        {
            ModStore store = null;
            List<ModStore.InstalledMod> mods = [];
            int conflicts = 0;

            try
            {
                store = ModStore.ForCurrentUser();
                mods = store.List();
                conflicts = store.Conflicts().Count;
            }
            catch (Exception e) when (e is System.IO.IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
            {
                Logger.Warning?.Print(LogClass.Application, $"BOTW-PC overlay: mod list unavailable: {e.Message}");
            }

            List<OverlayItem> items =
            [
                Info(T("Profile", "設定檔"), () => Settings.Mods.Profile),
                Info(T("Safe mode", "安全模式"), () => Settings.Mods.SafeMode ? T("on (no mods loaded)", "開（不載入 Mod）") : T("off", "關")),
                Info(T("Conflicting files", "衝突檔案"), () => conflicts.ToString(),
                    () => T("Files shipped by more than one enabled mod; the higher mod wins. Details: Manage Mods.", "多個已啟用 Mod 提供的同一檔案；優先序較高者生效。詳情見 Mod 管理。")),
                Header(T("INSTALLED MODS", "已安裝的 Mod")),
            ];

            foreach (ModStore.InstalledMod mod in mods)
            {
                string name = mod.Name;
                bool enabled = mod.Enabled;
                items.Add(new OverlayItem
                {
                    Label = name,
                    Value = () => enabled ? T("enabled", "啟用") : T("disabled", "停用"),
                    Change = _ =>
                    {
                        enabled = !enabled;
                        store?.SetEnabled(name, enabled);
                    },
                    Note = () => $"{mod.Manifest?.Category ?? "?"} · {string.Join(", ", mod.Parts.Keys.Select(t => $"{t:X16}"))}",
                    Apply = ApplyKind.GameRestart,
                });
            }

            if (mods.Count == 0)
            {
                items.Add(Info(T("No mods installed", "沒有安裝 Mod"), () => ""));
            }

            items.Add(Header(T("DOWNLOADABLE CONTENT", "追加內容")));
            IList<ulong> aoc = HLE.BotwPc.BotwDlc.EnabledInRunningGame(_cb.Device());

            foreach ((ulong id, string name) in HLE.BotwPc.BotwDlc.Names)
            {
                items.Add(Info(name, () => aoc.Contains(id) ? T("enabled", "已啟用") : T("not enabled", "未啟用"),
                    () => T("Install or enable add-on content in the DLC manager (game library → right click → Manage DLC).",
                            "在 DLC 管理（遊戲清單 → 右鍵 → 管理 DLC）安裝或啟用追加內容。")));
            }

            return new OverlayPage { Title = T("Mods & DLC", "Mod 與 DLC"), Items = items };
        }

        private OverlayPage SystemPage(OverlayMenu menu) => new()
        {
            Title = T("System", "系統"),
            Items =
            [
                Choice(T("Menu language", "選單語言"), [MenuLanguage.Auto, MenuLanguage.English, MenuLanguage.Chinese], () => Settings.Overlay.Language,
                    v => Settings.Overlay.Language = v,
                    v => v switch { MenuLanguage.English => "English", MenuLanguage.Chinese => "中文", _ => T("Auto (UI language)", "自動（跟隨介面語言）") },
                    () => _languageOverride is "en" or "zh"
                        ? T("Fixed by the launcher (BOTWPC_OVERLAY_LANG).", "由啟動檔固定（BOTWPC_OVERLAY_LANG）。")
                        : T("Takes effect the next time the menu opens. The game's own language does not change.", "下次開啟選單時生效。遊戲本身的語言不會改變。"),
                    enabled: () => _languageOverride is not ("en" or "zh")),
                Toggle(T("Pause game while open", "開啟時暫停遊戲"), () => Settings.Overlay.PauseGame, v => Settings.Overlay.PauseGame = v,
                    () => T("Uses Ryujinx's pause (game threads and audio stop). Takes effect the next time the menu opens.",
                            "使用 Ryujinx 的暫停（遊戲執行緒與音訊停止）。下次開啟選單時生效。")),
                Slider(T("Shortcut chord window", "組合鍵判定時間"), 40f, 200f, 10f, () => Settings.Overlay.ChordWindowMs,
                    v => { Settings.Overlay.ChordWindowMs = (int)v; OverlayInputGate.ChordWindowMs = v; }, "0 ms",
                    () => T("How long a single stick click waits for the other one. Shorter = less delay for L3/R3 in the game.",
                            "單獨按下一個搖桿鍵時，等待另一個的時間。越短，遊戲裡 L3／R3 的延遲越小。")),
                new OverlayItem
                {
                    Label = T("Revert changes since opening", "復原這次開啟後的變更"),
                    Activate = () => Revert(menu),
                    Note = () => T("Restores the BOTW-PC settings as they were when the menu opened.", "把 BOTW-PC 設定還原成開啟選單時的狀態。"),
                    Apply = ApplyKind.Info,
                },
                new OverlayItem
                {
                    Label = T("Restore defaults (controller, motion, overlay)", "恢復預設值（手把、體感、介面）"),
                    Activate = () =>
                    {
                        Settings.Controller = new ControllerSettings { Bindings = Settings.Controller.Bindings };
                        Settings.Motion = new MotionSettings();
                        Settings.Overlay = new OverlaySettings();
                        OverlayInputGate.ChordWindowMs = Settings.Overlay.ChordWindowMs;
                        _cb.ReloadInput();
                        menu.MarkChanged();
                    },
                    Note = () => T("Custom button bindings are kept. Graphics and frame rate are not changed.", "自訂按鍵綁定會保留，不變更圖形與幀率。"),
                    Apply = ApplyKind.Info,
                },
                new OverlayItem
                {
                    Label = T("Close", "關閉"),
                    Activate = () => Close(),
                    Note = () => T("Changes are saved when the menu closes. Settings marked RESTART apply at the next game start: save in the game, then restart BOTW-PC.",
                                   "關閉選單時儲存變更。標示 RESTART 的設定在下次啟動遊戲時生效：請先在遊戲內存檔，再重新啟動 BOTW-PC。"),
                    Apply = ApplyKind.Info,
                },
            ],
        };

        private void Revert(OverlayMenu menu)
        {
            BotwPcSettings old = _snapshot.Clone();
            BotwPcSettings s = Settings;
            s.Controller = old.Controller;
            s.Motion = old.Motion;
            s.Overlay = old.Overlay;
            s.Graphics = old.Graphics;
            GraphicsSettings.NotifyChanged();
            OverlayInputGate.ChordWindowMs = s.Overlay.ChordWindowMs;
            _cb.ReloadInput();
            menu.MarkChanged();
        }

        public void Dispose()
        {
            if (_pausedByUs)
            {
                _cb.Resume();
            }

            OverlayInputGate.Captures = false;
            OverlayFrame.Hide();
            _painter.Dispose();
        }
    }
}
