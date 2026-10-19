using ARMeilleure.Memory;
using Ryujinx.Common;
using Ryujinx.Common.Logging;
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

namespace Ryujinx.Cpu.LightningJit
{
    /// <summary>
    /// Guest code compiled ahead of time (iPadOS without JIT, botw/docs/IPADOS.md).
    ///
    /// Recording (any host, BOTWPC_AOT_RECORD=&lt;file&gt;, implies position-independent code): every function the
    /// translator compiles is appended to the file with its guest address, guest length and a hash of the guest code,
    /// as are the dispatch stubs. botw/ipados/aot_build.py turns recordings into an assembly file whose code goes into
    /// the signed binary's __TEXT, plus an index.
    ///
    /// Loading (host calls botwpc_set_aot_image): a function the translator does not have yet is looked up in the image
    /// first and used when the guest code still hashes the same; misses are compiled as usual when the process can run
    /// generated code, and are fatal (logged) when it cannot (<see cref="JitAvailable"/> false).
    /// </summary>
    public static unsafe class AotImage
    {
        public enum StubKind : uint
        {
            DispatchStub,
            SlowDispatchStub,
            DispatchLoop,
            GetFpFlags,
            SetFpFlags,
            SimdTrampoline,
            Count,
        }

        [StructLayout(LayoutKind.Sequential, Pack = 4)]
        public struct Entry
        {
            public ulong GuestAddress;
            public ulong HashLow;
            public ulong HashHigh;
            public uint CodeOffset;
            public uint GuestLength;
        }

        private const uint FunctionMagic = 0x31464142; // "BAF1"
        private const uint StubMagic = 0x31534142; // "BAS1"
        private const uint SimdMagic = 0x31564142; // "BAV1"

        private static readonly string _recordPath = Environment.GetEnvironmentVariable("BOTWPC_AOT_RECORD");
        private static FileStream _record;
        private static readonly Lock _recordLock = new();

        private static byte* _code;
        private static Dictionary<ulong, Entry> _index;
        private static readonly nint[] _stubs = new nint[(int)StubKind.Count];
        private static long _hits, _misses, _stale;

        [ThreadStatic]
        private static List<ulong> _exitTargets;

        /// <summary>Recording: compiles and records the function at an address, returning its constant exit targets.</summary>
        internal static Func<ulong, List<ulong>> Precompiler { get; set; }

        /// <summary>Recording: records the interpreter's FP/SIMD stubs for every instruction in a guest code range.</summary>
        internal static Action<ulong, ulong> SimdScanner { get; set; }

        // Interpreter FP/SIMD stubs of the image: pairs (normalized instruction, RET), sorted by instruction.
        private static uint* _simdCode;
        private static int _simdCount;
        private static readonly HashSet<uint> _recordedSimd = [];
        private static readonly HashSet<uint> _missedSimd = [];

        public static bool Recording => _recordPath != null;
        public static bool Loaded => _index != null;

        /// <summary>False when the process cannot execute generated code (iPadOS without a debugger, or BOTWPC_NO_JIT=1).</summary>
        public static bool JitAvailable { get; set; } = Environment.GetEnvironmentVariable("BOTWPC_NO_JIT") != "1";

        /// <summary>Called by the host with the image linked into it (aot_build.py output).</summary>
        public static void Load(byte* code, Entry* entries, int count, uint* stubOffsets)
        {
            Dictionary<ulong, Entry> index = new(count);

            for (int i = 0; i < count; i++)
            {
                index[entries[i].GuestAddress] = entries[i];
            }

            for (int i = 0; i < (int)StubKind.Count; i++)
            {
                _stubs[i] = stubOffsets[i] == uint.MaxValue ? 0 : (nint)(code + stubOffsets[i]);
            }

            _code = code;
            _index = index;
            Logger.Info?.Print(LogClass.Cpu, $"AOT image: {count} functions at 0x{(nint)code:x}; JIT {(JitAvailable ? "available for misses" : "unavailable")}.");
        }

        /// <summary>The image's interpreter FP/SIMD stubs (aot_build.py: _botwpc_aot_simd, _botwpc_aot_simd_count).</summary>
        public static void LoadSimd(uint* code, int count)
        {
            _simdCode = code;
            _simdCount = count;
            Logger.Info?.Print(LogClass.Cpu, $"AOT image: {count} interpreter FP/SIMD stubs");
        }

        internal static nint GetSimdStub(uint normalized)
        {
            int low = 0, high = _simdCount - 1;

            while (low <= high)
            {
                int middle = (low + high) >> 1;
                uint value = _simdCode[middle * 2];

                if (value == normalized)
                {
                    return (nint)(_simdCode + middle * 2);
                }

                if (value < normalized)
                {
                    low = middle + 1;
                }
                else
                {
                    high = middle - 1;
                }
            }

            return 0;
        }

        internal static void RecordSimd(uint normalized)
        {
            if (_recordPath == null)
            {
                return;
            }

            lock (_recordLock)
            {
                if (_recordedSimd.Add(normalized))
                {
                    BinaryWriter writer = Writer();
                    writer.Write(SimdMagic);
                    writer.Write(normalized);
                    writer.Flush();
                }
            }
        }

        /// <summary>An FP/SIMD instruction without a stub: noted for the next image, like function misses.</summary>
        internal static void NoteSimdMiss(uint normalized)
        {
            lock (_recordLock)
            {
                if (!_missedSimd.Add(normalized))
                {
                    return;
                }
            }

            Logger.Warning?.Print(LogClass.Cpu, $"Interpreter: no FP/SIMD stub for 0x{normalized:x8}");

            try
            {
                File.AppendAllText(Path.Combine(Ryujinx.Common.Configuration.AppDataManager.BaseDirPath, "aot_misses.txt"), $"simd:{normalized:x8}\n");
            }
            catch (IOException)
            {
            }
        }

        /// <summary>Memory-tracking signal handler compiled into the host instead of generated (see NativeSignalHandler).</summary>
        public static void SetHostSignalHandler(nint handler, delegate* unmanaged[Cdecl]<nint, void> setConfig)
        {
            Signal.NativeSignalHandler.HostHandler = handler;
            Signal.NativeSignalHandler.HostSetConfig = setConfig;
        }

        public static nint GetStub(StubKind kind) => Loaded ? _stubs[(int)kind] : 0;

        /// <summary>The image's code for the function at <paramref name="address"/>, if the guest code matches.</summary>
        internal static bool TryGet(IMemoryManager memory, ulong address, out TranslatedFunction function)
        {
            function = null;

            if (_index == null)
            {
                return false;
            }

            if (!_index.TryGetValue(address, out Entry entry))
            {
                Interlocked.Increment(ref _misses);
                return false;
            }

            Hash128 hash = Hash128.ComputeHash(memory.GetSpan(address, (int)entry.GuestLength));

            if (hash.Low != entry.HashLow || hash.High != entry.HashHigh)
            {
                Interlocked.Increment(ref _stale);
                return false;
            }

            long hits = Interlocked.Increment(ref _hits);

            if (hits % 5000 == 0)
            {
                Logger.Info?.Print(LogClass.Cpu, $"AOT: {hits} functions from the image, {Interlocked.Read(ref _misses)} misses, {Interlocked.Read(ref _stale)} stale");
            }

            function = new TranslatedFunction((nint)(_code + entry.CodeOffset), entry.GuestLength);

            return true;
        }

        /// <summary>
        /// Host callback for a miss without JIT: blocks until the process can run generated code (e.g. the user enabled
        /// JIT with StikDebug, which sets CS_DEBUGGED at run time) and returns 1, or returns 0 to give up.
        /// </summary>
        public static delegate* unmanaged[Cdecl]<ulong, int> RequestJit;

        private static readonly Lock _jitRequestLock = new();

        /// <summary>
        /// A miss without JIT: records the address for the next image (aot_misses.txt next to the configuration) and
        /// asks the host for JIT. True when generated code can run from now on.
        /// </summary>
        public static bool TryAcquireJit(ulong address)
        {
            lock (_jitRequestLock)
            {
                if (JitAvailable)
                {
                    return true;
                }

                Logger.Warning?.Print(LogClass.Cpu, $"AOT miss at 0x{address:x} without JIT: asking the host for JIT.");

                try
                {
                    File.AppendAllText(Path.Combine(Ryujinx.Common.Configuration.AppDataManager.BaseDirPath, "aot_misses.txt"), $"{address:x}\n");
                }
                catch (IOException)
                {
                }

                if (RequestJit != null && RequestJit(address) != 0)
                {
                    JitAvailable = true;
                    Logger.Info?.Print(LogClass.Cpu, "JIT available: misses are compiled from now on.");
                }

                return JitAvailable;
            }
        }

        /// <summary>A function that is neither in the image nor can be compiled: the guest cannot continue.</summary>
        public static Exception Miss(ulong address)
        {
            Logger.Error?.Print(LogClass.Cpu, $"AOT miss at 0x{address:x} without JIT ({Interlocked.Read(ref _hits)} hits so far): the image lacks this function. Record more coverage on a Mac (BOTWPC_AOT_RECORD).");

            return new InvalidOperationException($"AOT miss at 0x{address:x} and no JIT available.");
        }

        /// <summary>Called by the code generator for every constant guest target a function exits to.</summary>
        internal static void NoteExit(ulong target) => _exitTargets?.Add(target);

        internal static List<ulong> CollectExits(Action compile)
        {
            List<ulong> targets = [];
            _exitTargets = targets;

            try
            {
                compile();
            }
            finally
            {
                _exitTargets = null;
            }

            return targets;
        }

        /// <summary>
        /// BOTWPC_AOT_PRECOMPILE=&lt;file&gt; (with BOTWPC_AOT_RECORD): compiles and records, before the game starts, the
        /// closure of the entry points listed in the file (hex guest addresses, botw/ipados/aot_entries.py) under
        /// "constant exit target of a compiled function", i.e. every function the translator would create from those
        /// entries, split exactly as at run time. Indirect-branch targets outside the list stay uncovered.
        /// </summary>
        private static readonly bool _closure = Environment.GetEnvironmentVariable("BOTWPC_AOT_CLOSURE") == "1";

        public static void StartPrecompile(string listFile)
        {
            Func<ulong, List<ulong>> precompiler = Precompiler;

            if (precompiler == null)
            {
                Logger.Error?.Print(LogClass.Cpu, "AOT precompile needs BOTWPC_AOT_RECORD (and the LightningJit engine).");
                return;
            }

            Queue<ulong> work = new();
            HashSet<ulong> seen = [];

            foreach (string line in File.ReadAllLines(listFile))
            {
                if (ulong.TryParse(line.Trim(), System.Globalization.NumberStyles.HexNumber, null, out ulong address) && seen.Add(address))
                {
                    work.Enqueue(address);
                }
            }

            // Runs before the game starts (the caller waits): compiling while guest threads run made the game fail.
            Thread thread = new(() =>
            {
                int seeds = work.Count, done = 0, failed = 0;
                long start = System.Diagnostics.Stopwatch.GetTimestamp();

                while (work.TryDequeue(out ulong address))
                {
                    try
                    {
                        List<ulong> targets = precompiler(address);

                        // The closure over exit targets explodes (code compiled from mid-function entries duplicates whole
                        // regions: 362 000 functions, 30 GB for BotW); only BOTWPC_AOT_CLOSURE=1 follows them.
                        if (_closure)
                        {
                            foreach (ulong target in targets)
                            {
                                if (seen.Add(target))
                                {
                                    work.Enqueue(target);
                                }
                            }
                        }

                        done++;
                    }
                    catch (Exception)
                    {
                        failed++; // not code (a data pointer into .text, unmapped, ...)
                    }

                    if ((done + failed) % 10000 == 0)
                    {
                        Logger.Info?.Print(LogClass.Cpu, $"AOT precompile: {done} compiled, {failed} failed, {work.Count} queued");
                    }
                }

                Logger.Info?.Print(LogClass.Cpu, $"AOT precompile done: {seeds} seeds -> {done} functions ({failed} failed) in {System.Diagnostics.Stopwatch.GetElapsedTime(start).TotalSeconds:F0} s");

                // BOTWPC_AOT_SIMD_RANGES=start:size,... (hex): the interpreter's FP/SIMD stubs for all code in those ranges.
                string ranges = Environment.GetEnvironmentVariable("BOTWPC_AOT_SIMD_RANGES");

                if (!string.IsNullOrEmpty(ranges) && SimdScanner != null)
                {
                    foreach (string range in ranges.Split(','))
                    {
                        string[] parts = range.Split(':');
                        SimdScanner(Convert.ToUInt64(parts[0], 16), Convert.ToUInt64(parts[1], 16));
                    }

                    Logger.Info?.Print(LogClass.Cpu, $"AOT precompile: {_recordedSimd.Count} interpreter FP/SIMD stubs");
                }
            })
            {
                Name = "AotPrecompile",
                IsBackground = true,
            };
            thread.Start();
            thread.Join();
        }

        public static void RecordFunction(IMemoryManager memory, ulong address, ReadOnlySpan<byte> code, ulong guestLength)
        {
            if (_recordPath == null)
            {
                return;
            }

            Hash128 hash = Hash128.ComputeHash(memory.GetSpan(address, (int)guestLength));

            lock (_recordLock)
            {
                BinaryWriter writer = Writer();
                writer.Write(FunctionMagic);
                writer.Write(address);
                writer.Write((uint)guestLength);
                writer.Write(hash.Low);
                writer.Write(hash.High);
                writer.Write((uint)code.Length);
                writer.Write(code);
                writer.Flush();
            }
        }

        public static void RecordStub(StubKind kind, ReadOnlySpan<byte> code)
        {
            if (_recordPath == null)
            {
                return;
            }

            lock (_recordLock)
            {
                BinaryWriter writer = Writer();
                writer.Write(StubMagic);
                writer.Write((uint)kind);
                writer.Write((uint)code.Length);
                writer.Write(code);
                writer.Flush();
            }
        }

        private static BinaryWriter Writer()
        {
            _record ??= new FileStream(_recordPath, FileMode.Append, FileAccess.Write, FileShare.Read);

            return new BinaryWriter(_record, System.Text.Encoding.UTF8, leaveOpen: true);
        }
    }
}
