using ARMeilleure;
using ARMeilleure.CodeGen;
using ARMeilleure.Common;
using ARMeilleure.Memory;
using ARMeilleure.State;
using ARMeilleure.Translation;
using Iced.Intel;
using NUnit.Framework;
using Ryujinx.Cpu.Jit;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;

namespace Ryujinx.Tests.BotwPc
{
    /// <summary>
    /// BOTW-PC CPU JIT code-quality corpus: recompiles the game's own guest functions with the high-quality tier of
    /// ARMeilleure exactly as the emulator does with the PPTC cache on (HostMappedUnsafe memory, relocatable code), and
    /// classifies the emitted x86-64 instructions (register moves, stack spills/reloads, guest memory accesses, flag
    /// materialization, ...). With a sample-weight file from the guest profiler, every function counts by how hot it is
    /// in the W2 scene, so the totals approximate executed host instructions. Used to compare the legacy JIT with the
    /// CPU V2 code generator (BOTWPC_CPU_CORE) on identical input, without running the game.
    ///
    /// Inputs (local only; the game's code is never committed): BOTWPC_JIT_DUMP = runtime image of the main module
    /// (flat binary), BOTWPC_JIT_BASE = its guest address (hex, default 8504000), BOTWPC_JIT_FUNCS = a
    /// BOTWPC_GUEST_PERFMAP file (the translated functions), BOTWPC_JIT_WEIGHTS = optional "guest_hex samples" lines,
    /// BOTWPC_JIT_OUT = optional per-function CSV, BOTWPC_JIT_LIMIT = optional number of functions (hottest first),
    /// BOTWPC_JIT_V2 = CPU V2 feature bits (hex, <see cref="CpuV2Features"/>; default 0 = legacy code generator).
    /// </summary>
    public class JitCorpusTests
    {
        internal sealed class DumpMemory : IMemoryManager
        {
            private readonly byte[] _image;
            private readonly ulong _base;

            public DumpMemory(byte[] image, ulong baseAddress)
            {
                _image = image;
                _base = baseAddress;
            }

            public int AddressSpaceBits => 39;

            // Any non-zero constant: host-mapped code adds it to the guest address (the value is not dereferenced here).
            public nint PageTablePointer => unchecked((nint)0x27056290000);

            public MemoryManagerType Type => MemoryManagerType.HostMappedUnsafe;

            public event Action<ulong, ulong> UnmapEvent { add { } remove { } }

            public T Read<T>(ulong va) where T : unmanaged
            {
                int size = Marshal.SizeOf<T>();

                if (va < _base || va - _base + (ulong)size > (ulong)_image.Length)
                {
                    return default;
                }

                return MemoryMarshal.Read<T>(_image.AsSpan((int)(va - _base), size));
            }

            public T ReadTracked<T>(ulong va) where T : unmanaged => Read<T>(va);

            public void Write<T>(ulong va, T value) where T : unmanaged => throw new NotSupportedException();

            public ReadOnlySpan<byte> GetSpan(ulong va, int size, bool tracked = false)
            {
                if (va < _base || va - _base + (ulong)size > (ulong)_image.Length)
                {
                    return new byte[size];
                }

                return _image.AsSpan((int)(va - _base), size);
            }

            public ref T GetRef<T>(ulong va) where T : unmanaged => throw new NotSupportedException();

            public bool IsMapped(ulong va) => va >= _base && va - _base < (ulong)_image.Length;

            public void SignalMemoryTracking(ulong va, ulong size, bool write, bool precise = false, int? exemptId = null)
            {
            }
        }

        internal enum Kind
        {
            RegMove,
            StackLoad,
            StackStore,
            ContextLoad,
            ContextStore,
            GuestLoad,
            GuestStore,
            SetCC,
            BoolExtend,
            MovImm64,
            CmpTest,
            Branch,
            Call,
            PushPop,
            Simd,
            Alu,
            Count,
        }

        internal sealed class Stats
        {
            public readonly double[] Static = new double[(int)Kind.Count];
            public readonly double[] Weighted = new double[(int)Kind.Count];
            public double HostBytes, GuestInsts, WeightedGuestInsts, CompileMs;
            public int Functions;
        }

        /// <summary>Classifies x86-64 code emitted by ARMeilleure (context pointer: the register rcx is copied to).</summary>
        internal static int[] Classify(byte[] code, out int instructions)
        {
            int[] counts = new int[(int)Kind.Count];
            Decoder decoder = Decoder.Create(64, new ByteArrayCodeReader(code));
            Register context = Register.None;
            instructions = 0;

            while (decoder.IP < (ulong)code.Length)
            {
                decoder.Decode(out Instruction ins);
                instructions++;

                if (context == Register.None && ins.Mnemonic == Mnemonic.Mov && ins.Op1Kind == OpKind.Register && ins.Op1Register == Register.RCX &&
                    ins.Op0Kind == OpKind.Register)
                {
                    context = ins.Op0Register;
                }

                counts[(int)KindOf(in ins, context)]++;
            }

            return counts;
        }

        private static Kind KindOf(in Instruction ins, Register context)
        {
            Mnemonic m = ins.Mnemonic;

            if (ins.FlowControl is FlowControl.ConditionalBranch or FlowControl.UnconditionalBranch or FlowControl.IndirectBranch or FlowControl.Return)
            {
                return Kind.Branch;
            }

            if (ins.FlowControl is FlowControl.Call or FlowControl.IndirectCall)
            {
                return Kind.Call;
            }

            if (m is Mnemonic.Push or Mnemonic.Pop)
            {
                return Kind.PushPop;
            }

            if (ins.ConditionCode != ConditionCode.None && m.ToString().StartsWith("Set", StringComparison.Ordinal))
            {
                return Kind.SetCC;
            }

            bool hasMemory = false;
            bool memoryIsDest = false;

            for (int i = 0; i < ins.OpCount; i++)
            {
                if (ins.GetOpKind(i) == OpKind.Memory)
                {
                    hasMemory = true;
                    memoryIsDest = i == 0;
                }
            }

            if (hasMemory && m != Mnemonic.Lea)
            {
                if (ins.MemoryBase == Register.RSP)
                {
                    return memoryIsDest ? Kind.StackStore : Kind.StackLoad;
                }

                if (ins.MemoryBase == context && ins.MemoryIndex == Register.None)
                {
                    return memoryIsDest ? Kind.ContextStore : Kind.ContextLoad;
                }

                return memoryIsDest ? Kind.GuestStore : Kind.GuestLoad;
            }

            if (m == Mnemonic.Mov && ins.Op1Kind == OpKind.Immediate64)
            {
                return Kind.MovImm64;
            }

            if (m == Mnemonic.Movzx && ins.Op1Kind == OpKind.Register && ins.Op1Register.GetSize() == 1)
            {
                return Kind.BoolExtend;
            }

            if (m is Mnemonic.Mov or Mnemonic.Movzx or Mnemonic.Movsx or Mnemonic.Movsxd or Mnemonic.Movaps or Mnemonic.Movapd or Mnemonic.Movdqa or Mnemonic.Vmovaps or Mnemonic.Vmovdqa &&
                ins.OpCount == 2 && ins.Op0Kind == OpKind.Register && ins.Op1Kind == OpKind.Register)
            {
                return Kind.RegMove;
            }

            if (m is Mnemonic.Cmp or Mnemonic.Test)
            {
                return Kind.CmpTest;
            }

            for (int i = 0; i < ins.OpCount; i++)
            {
                if (ins.GetOpKind(i) == OpKind.Register && ins.GetOpRegister(i).IsVectorRegister())
                {
                    return Kind.Simd;
                }
            }

            return Kind.Alu;
        }

        /// <summary>For register moves: the instruction that follows when it writes the moved-to register (x86
        /// two-address form), or "other".</summary>
        internal static readonly Dictionary<string, long> MoveContext = [];

        /// <summary>Phis, phi sources, phi sources whose only use is the phi.</summary>
        internal static readonly long[] PhiStats = new long[3];

        /// <summary>
        /// Exact waste in emitted code: unconditional jumps to the next instruction, and register writes by moves that
        /// are overwritten before any read in the same straight-line run (no branch, call, return or branch target in
        /// between; reads from Iced's used-register information, including memory base/index registers).
        /// </summary>
        internal static void CountWaste(byte[] code)
        {
            Decoder decoder = Decoder.Create(64, new ByteArrayCodeReader(code));
            List<Instruction> list = [];

            while (decoder.IP < (ulong)code.Length)
            {
                decoder.Decode(out Instruction ins);
                list.Add(ins);
            }

            HashSet<ulong> targets = [];

            foreach (Instruction ins in list)
            {
                if (ins.FlowControl is FlowControl.ConditionalBranch or FlowControl.UnconditionalBranch && ins.Op0Kind is OpKind.NearBranch64 or OpKind.NearBranch32 or OpKind.NearBranch16)
                {
                    targets.Add(ins.NearBranchTarget);
                }
            }

            InstructionInfoFactory factory = new();
            Dictionary<Register, int> pending = []; // full register -> index of the move that wrote it, unread

            for (int i = 0; i < list.Count; i++)
            {
                Instruction ins = list[i];

                if (targets.Contains(ins.IP))
                {
                    pending.Clear();
                }

                if (ins.FlowControl == FlowControl.UnconditionalBranch && ins.Op0Kind is OpKind.NearBranch64 or OpKind.NearBranch32 or OpKind.NearBranch16 &&
                    ins.NearBranchTarget == ins.NextIP)
                {
                    Waste[0]++;
                }

                if (ins.FlowControl != FlowControl.Next)
                {
                    pending.Clear();
                    continue;
                }

                InstructionInfo info = factory.GetInfo(ins);
                bool isMove = ins.Mnemonic is Mnemonic.Mov && ins.Op0Kind == OpKind.Register && ins.Op0Register.IsGPR();
                Register written = isMove ? ins.Op0Register.GetFullRegister() : Register.None;

                foreach (UsedRegister used in info.GetUsedRegisters())
                {
                    if (used.Access is OpAccess.Read or OpAccess.ReadWrite or OpAccess.CondRead or OpAccess.ReadCondWrite)
                    {
                        pending.Remove(used.Register.GetFullRegister());
                    }
                }

                foreach (UsedMemory mem in info.GetUsedMemory())
                {
                    pending.Remove(mem.Base.GetFullRegister());
                    pending.Remove(mem.Index.GetFullRegister());
                }

                if (isMove)
                {
                    if (pending.ContainsKey(written))
                    {
                        Waste[1]++;
                    }

                    pending[written] = i;
                }
                else
                {
                    foreach (UsedRegister used in info.GetUsedRegisters())
                    {
                        if (used.Access is OpAccess.Write or OpAccess.CondWrite or OpAccess.ReadWrite or OpAccess.ReadCondWrite)
                        {
                            pending.Remove(used.Register.GetFullRegister());
                        }
                    }
                }
            }
        }

        /// <summary>Jumps to the next instruction, dead register moves.</summary>
        internal static readonly long[] Waste = new long[2];

        internal static void CountMoveContexts(byte[] code)
        {
            Decoder decoder = Decoder.Create(64, new ByteArrayCodeReader(code));
            Instruction prev = default;
            bool prevIsMove = false;

            while (decoder.IP < (ulong)code.Length)
            {
                decoder.Decode(out Instruction ins);

                if (prevIsMove)
                {
                    string key = ins.OpCount > 0 && ins.Op0Kind == OpKind.Register && ins.Op0Register.GetFullRegister() == prev.Op0Register.GetFullRegister()
                        ? (ins.OpCount > 1 && ins.Op1Kind == OpKind.Register && ins.Op1Register.GetFullRegister() == prev.Op0Register.GetFullRegister() ? "self:" : "2addr:") + ins.Mnemonic
                        : "other";
                    MoveContext[key] = MoveContext.GetValueOrDefault(key) + 1;
                }

                prevIsMove = ins.Mnemonic == Mnemonic.Mov && ins.OpCount == 2 && ins.Op0Kind == OpKind.Register && ins.Op1Kind == OpKind.Register;

                if (prevIsMove)
                {
                    string kind = ins.Op0Register == ins.Op1Register ? "kind:same" : ins.Op0Register.GetSize() == 4 ? "kind:r32" : ins.Op0Register.IsXMM() ? "kind:xmm" : "kind:r64";
                    MoveContext[kind] = MoveContext.GetValueOrDefault(kind) + 1;
                }
                else if (ins.OpCount == 2 && ins.Op0Kind == OpKind.Register && ins.Op1Kind == OpKind.Register && ins.Op0Register.IsXMM() &&
                    ins.Mnemonic is Mnemonic.Movaps or Mnemonic.Movapd or Mnemonic.Movdqu or Mnemonic.Movdqa or Mnemonic.Vmovaps or Mnemonic.Vmovdqu or Mnemonic.Vmovdqa)
                {
                    MoveContext["kind:xmm"] = MoveContext.GetValueOrDefault("kind:xmm") + 1;
                }
                prev = ins;
            }
        }

        internal static List<(ulong Address, double Weight)> LoadFunctions(string perfMap, string weights, int limit)
        {
            Dictionary<ulong, double> weight = [];

            if (!string.IsNullOrEmpty(weights) && File.Exists(weights))
            {
                foreach (string line in File.ReadLines(weights))
                {
                    string[] p = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);

                    if (p.Length >= 2 && ulong.TryParse(p[0].Replace("guest_", ""), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out ulong a))
                    {
                        weight[a] = double.Parse(p[1], CultureInfo.InvariantCulture);
                    }
                }
            }

            HashSet<ulong> seen = [];

            foreach (string line in File.ReadLines(perfMap))
            {
                string[] p = line.Split(' ');

                if (p.Length >= 3 && p[2].StartsWith("guest_", StringComparison.Ordinal))
                {
                    seen.Add(ulong.Parse(p[2].AsSpan(6), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
                }
            }

            List<(ulong, double)> list = seen.Select(a => (a, weight.TryGetValue(a, out double w) ? w : 0)).OrderByDescending(x => x.Item2).ThenBy(x => x.a).ToList();

            return limit > 0 && limit < list.Count ? list.GetRange(0, limit) : list;
        }

        internal static Translator CreateTranslator(IMemoryManager memory)
        {
            Optimizations.AllowLcqInFunctionTable = false;
            Optimizations.UseUnmanagedDispatchLoop = false;

            // BOTWPC_JIT_V2 = CPU V2 feature bits (hex) for the code generator under test; default legacy.
            CpuV2.Features = (CpuV2Features)int.Parse(Environment.GetEnvironmentVariable("BOTWPC_JIT_V2") ?? "0", NumberStyles.HexNumber, CultureInfo.InvariantCulture);

            if (int.TryParse(Environment.GetEnvironmentVariable("BOTWPC_JIT_SPILLDEF_MIN"), out int spillMin))
            {
                ARMeilleure.CodeGen.RegisterAllocators.LinearScanAllocator.SpillAtDefinitionThreshold = spillMin;
            }

            return new Translator(new JitMemoryAllocator(forJit: true), memory, AddressTable<ulong>.CreateForArm(true, memory.Type));
        }

        internal static Stats Run(Translator translator, List<(ulong Address, double Weight)> functions, TextWriter perFunction)
        {
            Stats stats = new();

            if (Environment.GetEnvironmentVariable("BOTWPC_JIT_PHIS") == "1")
            {
                // Phi statistics after optimization: each phi source becomes a copy at the end of its predecessor and
                // each phi a copy at the start of its block (SSA deconstruction).
                ARMeilleure.Translation.Compiler.Observer = (stage, cfg) =>
                {
                    if (stage != "optimized")
                    {
                        return;
                    }

                    for (ARMeilleure.IntermediateRepresentation.BasicBlock block = cfg.Blocks.First; block != null; block = block.ListNext)
                    {
                        for (ARMeilleure.IntermediateRepresentation.Operation node = block.Operations.First; node != default; node = node.ListNext)
                        {
                            if (node.Instruction == ARMeilleure.IntermediateRepresentation.Instruction.Phi)
                            {
                                PhiStats[0]++;
                                PhiStats[1] += node.SourcesCount;

                                for (int i = 0; i < node.SourcesCount; i++)
                                {
                                    ARMeilleure.IntermediateRepresentation.Operand src = node.GetSource(i);

                                    if (src.Kind == ARMeilleure.IntermediateRepresentation.OperandKind.LocalVariable && src.UsesCount == 1)
                                    {
                                        PhiStats[2]++;
                                    }
                                }
                            }
                        }
                    }
                };
            }

            perFunction?.WriteLine("guest,weight,guest_insts,host_bytes,host_insts," + string.Join(",", Enum.GetNames<Kind>().Take((int)Kind.Count)) + ",compile_us");

            foreach ((ulong address, double weight) in functions)
            {
                long t0 = Stopwatch.GetTimestamp();
                CompiledFunction func = translator.CompileForAnalysis(address, ExecutionMode.Aarch64, highCq: true, relocatable: true, out ulong guestSize);
                double us = Stopwatch.GetElapsedTime(t0).TotalMicroseconds;

                if (func.Code == null)
                {
                    continue;
                }

                int[] counts = Classify(func.Code, out int instructions);

                if (Environment.GetEnvironmentVariable("BOTWPC_JIT_MOVES") == "1")
                {
                    CountMoveContexts(func.Code);
                    CountWaste(func.Code);
                }
                double guestInsts = guestSize / 4.0;
                stats.Functions++;
                stats.HostBytes += func.Code.Length;
                stats.GuestInsts += guestInsts;
                stats.CompileMs += us / 1000;

                // Weight per guest instruction executed is unknown; samples per function scale its static mix.
                double w = guestInsts > 0 ? weight / Math.Max(1, instructions) : 0;

                for (int k = 0; k < counts.Length; k++)
                {
                    stats.Static[k] += counts[k];
                    stats.Weighted[k] += counts[k] * w;
                }

                stats.WeightedGuestInsts += guestInsts * w;
                perFunction?.WriteLine(string.Create(CultureInfo.InvariantCulture,
                    $"{address:x},{weight},{guestInsts},{func.Code.Length},{instructions},{string.Join(",", counts)},{us:F0}"));
            }

            return stats;
        }

        internal static string Report(Stats s)
        {
            double st = s.Static.Sum(), wt = s.Weighted.Sum();
            System.Text.StringBuilder sb = new();
            sb.AppendLine(string.Create(CultureInfo.InvariantCulture,
                $"functions {s.Functions}, guest insts {s.GuestInsts:F0}, host insts {st:F0} ({st / Math.Max(1, s.GuestInsts):F2}/guest), host bytes {s.HostBytes:F0}, compile {s.CompileMs:F0} ms"));

            if (wt > 0)
            {
                sb.AppendLine(string.Create(CultureInfo.InvariantCulture, $"weighted host/guest {wt / Math.Max(1e-9, s.WeightedGuestInsts):F2}"));
            }

            sb.AppendLine("kind            static%  weighted%   static");

            for (int k = 0; k < (int)Kind.Count; k++)
            {
                sb.AppendLine(string.Create(CultureInfo.InvariantCulture,
                    $"{(Kind)k,-14} {100 * s.Static[k] / st,7:F2}  {(wt > 0 ? 100 * s.Weighted[k] / wt : 0),8:F2}  {s.Static[k],8:F0}"));
            }

            return sb.ToString();
        }

        /// <summary>
        /// BOTWPC_JIT_DUMP_IR = comma-separated guest function addresses (hex): writes the IR after SSA construction,
        /// after optimization and after register allocation, and the disassembly, to BOTWPC_JIT_OUT_DIR/&lt;address&gt;.txt.
        /// </summary>
        [Test] // ignored unless the local inputs are given
        public void DumpFunctionIr()
        {
            string dump = Environment.GetEnvironmentVariable("BOTWPC_JIT_DUMP");
            string list = Environment.GetEnvironmentVariable("BOTWPC_JIT_DUMP_IR");
            string outDir = Environment.GetEnvironmentVariable("BOTWPC_JIT_OUT_DIR");

            if (string.IsNullOrEmpty(dump) || !File.Exists(dump) || string.IsNullOrEmpty(list) || string.IsNullOrEmpty(outDir))
            {
                Assert.Ignore("BOTWPC_JIT_DUMP / BOTWPC_JIT_DUMP_IR / BOTWPC_JIT_OUT_DIR not set");
            }

            ulong baseAddress = ulong.Parse(Environment.GetEnvironmentVariable("BOTWPC_JIT_BASE") ?? "8504000", NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            Translator translator = CreateTranslator(new DumpMemory(File.ReadAllBytes(dump), baseAddress));
            Directory.CreateDirectory(outDir);

            foreach (string item in list.Split(','))
            {
                ulong address = ulong.Parse(item.Replace("guest_", ""), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                System.Text.StringBuilder sb = new();
                ARMeilleure.Translation.Compiler.Observer = (stage, cfg) => sb.AppendLine($"===== {stage}").AppendLine(ARMeilleure.Diagnostics.IRDumper.GetDump(cfg));

                try
                {
                    CompiledFunction func = translator.CompileForAnalysis(address, ExecutionMode.Aarch64, highCq: true, relocatable: true, out _);
                    sb.AppendLine("===== code");
                    Decoder decoder = Decoder.Create(64, new ByteArrayCodeReader(func.Code));
                    NasmFormatter formatter = new();
                    StringOutput output = new();

                    while (decoder.IP < (ulong)func.Code.Length)
                    {
                        decoder.Decode(out Instruction ins);
                        formatter.Format(ins, output);
                        sb.AppendLine(string.Create(CultureInfo.InvariantCulture, $"{ins.IP,6:x}  {output.ToStringAndReset()}"));
                    }
                }
                finally
                {
                    ARMeilleure.Translation.Compiler.Observer = null;
                }

                File.WriteAllText(Path.Combine(outDir, $"{address:x}.txt"), sb.ToString());
            }
        }

        [Test] // ignored unless the local inputs are given
        public void CorpusStatistics()
        {
            string dump = Environment.GetEnvironmentVariable("BOTWPC_JIT_DUMP");
            string funcs = Environment.GetEnvironmentVariable("BOTWPC_JIT_FUNCS");

            if (string.IsNullOrEmpty(dump) || !File.Exists(dump) || string.IsNullOrEmpty(funcs) || !File.Exists(funcs))
            {
                Assert.Ignore("BOTWPC_JIT_DUMP / BOTWPC_JIT_FUNCS not set");
            }

            ulong baseAddress = ulong.Parse(Environment.GetEnvironmentVariable("BOTWPC_JIT_BASE") ?? "8504000", NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            int limit = int.TryParse(Environment.GetEnvironmentVariable("BOTWPC_JIT_LIMIT"), out int l) ? l : 0;
            List<(ulong, double)> functions = LoadFunctions(funcs, Environment.GetEnvironmentVariable("BOTWPC_JIT_WEIGHTS"), limit);
            Translator translator = CreateTranslator(new DumpMemory(File.ReadAllBytes(dump), baseAddress));

            string outPath = Environment.GetEnvironmentVariable("BOTWPC_JIT_OUT");
            using StreamWriter perFunction = string.IsNullOrEmpty(outPath) ? null : new StreamWriter(outPath);
            Stats stats = Run(translator, functions, perFunction);
            string report = Report(stats);

            if (!string.IsNullOrEmpty(outPath))
            {
                File.WriteAllText(outPath + ".txt", report);
            }

            TestContext.Progress.WriteLine(report);
            foreach ((string k, long v) in MoveContext.OrderByDescending(x => x.Value).Take(25))
            {
                TestContext.Progress.WriteLine($"movectx {k} {v}");
            }

            TestContext.Progress.WriteLine($"waste jumps-to-next {Waste[0]} dead-moves {Waste[1]}");
            TestContext.Progress.WriteLine($"phis {PhiStats[0]} sources {PhiStats[1]} single-use sources {PhiStats[2]}");
            ARMeilleure.Translation.Compiler.Observer = null;
            Assert.That(stats.Functions, Is.GreaterThan(0));
        }
    }
}
