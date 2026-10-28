using ARMeilleure.Memory;
using ARMeilleure.State;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using ExecutionContext = ARMeilleure.State.ExecutionContext;
using Interlocked = System.Threading.Interlocked;

namespace ARMeilleure.Diagnostics
{
    /// <summary>
    /// BOTW-PC test aid: logs guest registers whenever execution reaches chosen instructions, so a value can be followed
    /// through game code without a debugger. Translations (PPTC off) of a probed address call <see cref="Hit"/> before the
    /// instruction runs.
    ///
    /// BOTWPC_PROBE="addr[=label][:xN*k|xN+off*k],..." with addr the absolute guest address; ":x1*3" also logs 3 floats at [x1],
    /// ":x0+0x40*3" 3 floats at [x0+0x40]. BOTWPC_PROBE_LOG=&lt;file&gt;. BOTWPC_PROBE_COUNT=&lt;file of addresses&gt; adds
    /// count-only probes, written as "t addr hits" per 0.25 s window. Off (and the delegate unregistered) unless set.
    /// </summary>
    public static class GuestProbe
    {
        private sealed record Probe(string Label, int MemRegister, ulong MemOffset, int MemCount, bool CountOnly, ulong[] Derefs = null);

        private static readonly Dictionary<ulong, Probe> _probes = Load();
        private static readonly StreamWriter _log;
        private static readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();
        private static readonly Dictionary<ulong, int> _counts = [];
        private static double _countWindow;

        public static bool Enabled => _probes.Count > 0;

        static GuestProbe()
        {
            string path = Environment.GetEnvironmentVariable("BOTWPC_PROBE_LOG");

            if (Enabled && !string.IsNullOrEmpty(path))
            {
                _log = new StreamWriter(path, append: false) { AutoFlush = false };
            }
        }

        public static bool IsProbed(ulong address) => _probes.ContainsKey(address);

        private static Dictionary<ulong, Probe> Load()
        {
            Dictionary<ulong, Probe> probes = [];
            string spec = Environment.GetEnvironmentVariable("BOTWPC_PROBE");

            if (!string.IsNullOrEmpty(spec))
            {
                foreach (string item in spec.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    string[] addrMem = item.Split(':', 2);
                    string[] addrLabel = addrMem[0].Split('=', 2);
                    ulong address = ParseHex(addrLabel[0]);
                    int register = -1, count = 0;
                    ulong offset = 0;
                    ulong[] derefs = null;

                    if (addrMem.Length == 2)
                    {
                        // xN[+off[>off...]]*k: each '>' reads a u64 pointer at the address so far and adds the next offset.
                        string[] regCount = addrMem[1].TrimStart('x').Split('*');
                        string[] regOffset = regCount[0].Split('+', 2);
                        register = int.Parse(regOffset[0], CultureInfo.InvariantCulture);

                        if (regOffset.Length > 1)
                        {
                            string[] chain = regOffset[1].Split('>');
                            offset = ParseHex(chain[0]);
                            derefs = Array.ConvertAll(chain[1..], ParseHex);
                        }

                        count = regCount.Length > 1 ? int.Parse(regCount[1], CultureInfo.InvariantCulture) : 1;
                    }

                    probes[address] = new Probe(addrLabel.Length > 1 ? addrLabel[1] : $"{address:x}", register, offset, count, false, derefs);
                }
            }

            string countPath = Environment.GetEnvironmentVariable("BOTWPC_PROBE_COUNT");

            if (!string.IsNullOrEmpty(countPath) && File.Exists(countPath))
            {
                foreach (string line in File.ReadAllLines(countPath))
                {
                    string text = line.Split('#')[0].Trim();

                    if (text.Length > 0)
                    {
                        probes.TryAdd(ParseHex(text), new Probe(text, -1, 0, 0, true));
                    }
                }
            }

            return probes;
        }

        private static ulong ParseHex(string text) =>
            ulong.Parse(text.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? text[2..] : text, NumberStyles.HexNumber);

        /// <summary>Called by translated code before the probed instruction at <paramref name="address"/>.</summary>
        public static void Hit(ExecutionContext context, IMemoryManager memory, ulong address)
        {
            if (_log == null || !_probes.TryGetValue(address, out Probe probe))
            {
                return;
            }

            double t = _clock.Elapsed.TotalSeconds;

            if (probe.CountOnly)
            {
                lock (_counts)
                {
                    _counts[address] = _counts.GetValueOrDefault(address) + 1;

                    if (t - _countWindow >= 0.25)
                    {
                        lock (_log)
                        {
                            foreach ((ulong a, int n) in _counts)
                            {
                                _log.WriteLine($"C {_countWindow:F2} {a:x} {n}");
                            }

                            _log.Flush();
                        }

                        _counts.Clear();
                        _countWindow = t;
                    }
                }

                return;
            }

            var line = new System.Text.StringBuilder(256);
            line.Append(CultureInfo.InvariantCulture, $"P {t:F4} {Environment.CurrentManagedThreadId} {probe.Label}");

            foreach (int x in (ReadOnlySpan<int>)[0, 1, 2, 3, 4, 19, 20, 30])
            {
                line.Append(CultureInfo.InvariantCulture, $" x{x}={context.GetX(x):x}");
            }

            for (int s = 0; s < 6; s++)
            {
                line.Append(CultureInfo.InvariantCulture, $" s{s}={context.GetV(s).As<float>():G6}");
            }

            if (probe.MemRegister >= 0)
            {
                line.Append(" m=").Append(ReadChain(memory, context.GetX(probe.MemRegister) + probe.MemOffset, probe.Derefs, probe.MemCount));
            }

            if (_track != null && probe.Label == _track.Label)
            {
                lock (_tracked)
                {
                    _trackMemory = memory;
                    _tracked[context.GetX(_track.MemRegister)] = t;
                }
            }

            lock (_log)
            {
                _log.WriteLine(line.ToString());

                if (Interlocked.Increment(ref _lines) % 8 == 0)
                {
                    _log.Flush();
                }
            }
        }

        private static long _lines;

        // BOTWPC_PROBE_TRACK="label:xN+off>off*k": when the probe named label hits, keeps logging the chain from that hit's xN
        // once per frame (OnFrame) for 8 s, as "T time base values" (e.g. the position of an enemy that was just hit).
        private static readonly Probe _track = LoadTrack();
        private static readonly Dictionary<ulong, double> _tracked = [];
        private static IMemoryManager _trackMemory;

        private static Probe LoadTrack()
        {
            string spec = Environment.GetEnvironmentVariable("BOTWPC_PROBE_TRACK");

            if (string.IsNullOrEmpty(spec))
            {
                return null;
            }

            string[] labelChain = spec.Split(':', 2);
            string[] regCount = labelChain[1].TrimStart('x').Split('*');
            string[] regOffset = regCount[0].Split('+', 2);
            string[] chain = regOffset.Length > 1 ? regOffset[1].Split('>') : ["0"];

            return new Probe(labelChain[0], int.Parse(regOffset[0], CultureInfo.InvariantCulture), ParseHex(chain[0]),
                regCount.Length > 1 ? int.Parse(regCount[1], CultureInfo.InvariantCulture) : 1, false, Array.ConvertAll(chain[1..], ParseHex));
        }

        /// <summary>Called once per game frame: logs the tracked chains.</summary>
        public static void OnFrame()
        {
            if (_track == null || _log == null)
            {
                return;
            }

            double t = _clock.Elapsed.TotalSeconds;

            lock (_tracked)
            {
                foreach ((ulong baseAddress, double since) in _tracked)
                {
                    if (t - since > 8)
                    {
                        _tracked.Remove(baseAddress);
                        continue;
                    }

                    string values = ReadChain(_trackMemory, baseAddress + _track.MemOffset, _track.Derefs, _track.MemCount);

                    lock (_log)
                    {
                        _log.WriteLine(string.Create(CultureInfo.InvariantCulture, $"T {t:F4} {baseAddress:x} {values}"));
                    }
                }
            }

            Flush();
        }

        private static string ReadChain(IMemoryManager memory, ulong va, ulong[] derefs, int count)
        {
            var text = new System.Text.StringBuilder();

            try
            {
                foreach (ulong next in derefs ?? [])
                {
                    va = memory.Read<ulong>(va) + next;
                }
            }
            catch (Exception)
            {
                va = 0;
            }

            for (int i = 0; i < count; i++)
            {
                try
                {
                    text.Append(memory.Read<float>(va + (ulong)i * 4).ToString("G6", CultureInfo.InvariantCulture)).Append(';');
                }
                catch (Exception)
                {
                    text.Append("?;");
                }
            }

            return text.ToString();
        }

        public static void Flush()
        {
            if (_log != null)
            {
                lock (_log)
                {
                    _log.Flush();
                }
            }
        }
    }
}
