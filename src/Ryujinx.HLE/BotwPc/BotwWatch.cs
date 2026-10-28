using Ryujinx.Common.Logging;
using Ryujinx.Memory;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;

namespace Ryujinx.HLE.BotwPc
{
    /// <summary>
    /// Test aid: logs guest values once per frame, so a behaviour can be compared between frame rates.
    ///
    /// BOTWPC_WATCH="name=chain:type;..." with chain = "main+0x2d1e170,0x3c" (start address, then each further term
    /// dereferences a u64 pointer and adds the offset) and type f32, f64, u32, s32 or u64; BOTWPC_WATCH_LOG=&lt;csv&gt;.
    /// A null pointer on the way logs an empty cell. Off unless both are set.
    /// </summary>
    public sealed class BotwWatch
    {
        private sealed record Watch(string Name, ulong Start, ulong[] Offsets, string Type);

        private readonly IVirtualMemoryManager _memory;
        private readonly List<Watch> _watches = [];
        private readonly StreamWriter _log;
        private readonly long _t0 = Stopwatch.GetTimestamp();
        private long _frames;

        private BotwWatch(IVirtualMemoryManager memory, StreamWriter log)
        {
            _memory = memory;
            _log = log;
        }

        public static BotwWatch FromEnvironment(IVirtualMemoryManager memory, ulong mainBase)
        {
            string spec = Environment.GetEnvironmentVariable("BOTWPC_WATCH");
            string path = Environment.GetEnvironmentVariable("BOTWPC_WATCH_LOG");

            if (string.IsNullOrEmpty(spec) || string.IsNullOrEmpty(path))
            {
                return null;
            }

            BotwWatch watch = new(memory, new StreamWriter(path) { AutoFlush = false });

            foreach (string item in spec.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                string[] nameRest = item.Split('=', 2);
                string[] chainType = nameRest[1].Split(':', 2);
                string[] terms = chainType[0].Split(',');
                string first = terms[0].Trim();
                ulong start = first.StartsWith("main+", StringComparison.Ordinal) ? mainBase + Parse(first[5..]) : Parse(first);
                ulong[] offsets = Array.ConvertAll(terms[1..], t => Parse(t.Trim()));

                watch._watches.Add(new Watch(nameRest[0].Trim(), start, offsets, chainType.Length > 1 ? chainType[1].Trim() : "f32"));
            }

            watch._log.WriteLine("time_s," + string.Join(",", watch._watches.ConvertAll(w => w.Name)));
            Logger.Info?.Print(LogClass.Application, $"BOTW-PC watch: {watch._watches.Count} values per frame to {path}.");

            return watch;
        }

        /// <summary>Called once per queued game frame.</summary>
        public void OnFrame()
        {
            double t = (Stopwatch.GetTimestamp() - _t0) / (double)Stopwatch.Frequency;
            List<string> cells = [t.ToString("F4", CultureInfo.InvariantCulture)];

            foreach (Watch w in _watches)
            {
                cells.Add(ReadValue(w));
            }

            lock (_log)
            {
                _log.WriteLine(string.Join(",", cells));

                if (++_frames % 15 == 0)
                {
                    _log.Flush();
                }
            }
        }

        private string ReadValue(Watch w)
        {
            try
            {
                ulong address = w.Start;

                foreach (ulong offset in w.Offsets)
                {
                    ulong pointer = _memory.Read<ulong>(address);

                    if (pointer == 0)
                    {
                        return "";
                    }

                    address = pointer + offset;
                }

                return w.Type switch
                {
                    "f64" => _memory.Read<double>(address).ToString("R", CultureInfo.InvariantCulture),
                    "u32" => _memory.Read<uint>(address).ToString(CultureInfo.InvariantCulture),
                    "s32" => _memory.Read<int>(address).ToString(CultureInfo.InvariantCulture),
                    "u64" => "0x" + _memory.Read<ulong>(address).ToString("x"),
                    _ => _memory.Read<float>(address).ToString("R", CultureInfo.InvariantCulture),
                };
            }
            catch (Exception)
            {
                return ""; // unmapped on the way
            }
        }

        private static ulong Parse(string text) =>
            text.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? ulong.Parse(text[2..], NumberStyles.HexNumber) : ulong.Parse(text);
    }
}
