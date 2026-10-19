using ARMeilleure.State;
using NUnit.Framework;
using Ryujinx.Cpu;
using Ryujinx.Cpu.Jit;
using Ryujinx.Cpu.LightningJit;
using Ryujinx.Memory;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

namespace Ryujinx.Tests.Cpu
{
    /// <summary>
    /// BOTW-PC: the A64 interpreter (iPadOS without JIT) against LightningJit on an Arm64 host. Each instruction runs once
    /// compiled and once interpreted (the translator's ForceInterpreter, entered through the dispatch stub as on the
    /// iPad) from the same random state; registers, NZCV, FPSR and data memory must match.
    /// BOTWPC_INTERP_TEST_ENCODINGS=&lt;file&gt; adds encodings (hex, one per line, e.g. sampled from the game's code);
    /// BOTWPC_INTERP_TEST_OUT=&lt;file&gt; receives every mismatch.
    /// </summary>
    [TestFixture]
    public class InterpreterTests
    {
        private const ulong CodeAddress = 0x10000;
        private const ulong DataAddress = 0x20000;
        private const int DataSize = 0x8000;
        private const ulong MemBase = DataAddress + 0x2000;
        private const int StatesPerEncoding = 4;

        private MemoryBlock _ram;
        private MemoryManagerHostTracked _memory;
        private LightningJitCpuContext _jit, _interpreter;
        private Random _random;

        [SetUp]
        public void Setup()
        {
            if (RuntimeInformation.ProcessArchitecture != Architecture.Arm64)
            {
                Assert.Ignore("LightningJit needs an Arm64 host.");
            }

            // The memory manager the game runs with on macOS and iPadOS (LightningJit only supports host-mapped ones).
            _ram = new MemoryBlock(0x20000, MemoryAllocationFlags.Mirrorable);
            _memory = new MemoryManagerHostTracked(_ram, 1UL << 30, false, null);
            _memory.IncrementReferenceCount();
            _memory.Map(CodeAddress, 0, 0x4000, MemoryMapFlags.Private);
            _memory.Map(DataAddress, 0x8000, DataSize, MemoryMapFlags.Private);

            TickSource ticks = new(19200000);
            _jit = new LightningJitCpuContext(ticks, _memory, true);
            _interpreter = new LightningJitCpuContext(ticks, _memory, true);
            _interpreter.Translator.ForceInterpreter = true;
            _random = new Random(1234);
        }

        [TearDown]
        public void Teardown()
        {
            _memory?.DecrementReferenceCount();
            _ram?.Dispose();
        }

        private static readonly uint[] _builtin =
        [
            // Data processing (immediate)
            0x10000020, 0x90000041, 0x91001020, 0xB1001020, 0xD1001020, 0xF1001020, 0x11400421, 0x31000C3F, 0x910003E0, 0x910003FF,
            0x92400C20, 0xB2400C20, 0xD2400C20, 0xF2400C20, 0x12001C20, 0x320003E0, 0x92781C20,
            0xD2800020, 0x92800020, 0xF2A00020, 0x52800020, 0x12800020, 0x72A00020,
            0x93407C20, 0xD3400C20, 0xB3400C20, 0x13001C20, 0x53001C20, 0x33001C20, 0xD37FF820, 0x53017C20, 0x93C20C20, 0x13820C20,
            // Data processing (register)
            0x8A020020, 0xAA020020, 0xCA020020, 0xEA020020, 0x8A220020, 0xAA220020, 0xCA220020, 0xEA220020, 0x0A421020, 0x2A821020, 0x4AC21020,
            0x8B020020, 0xAB020020, 0xCB020020, 0xEB020020, 0x0B421020, 0x6B821020, 0x8B22C020, 0xCB22E820, 0x8B3F63E0, 0xAB224020, 0x2B225020,
            0x9A020020, 0xBA020020, 0xDA020020, 0xFA020020, 0x1A020020,
            0xFA420024, 0xBA420824, 0x7A421A25, 0x3A4C1825,
            0x9A821020, 0x9A821420, 0xDA821020, 0xDA821420, 0x1A82B020,
            0x9AC20820, 0x9AC20C20, 0x1AC20820, 0x1AC20C20, 0x9AC22020, 0x9AC22420, 0x9AC22820, 0x9AC22C20, 0x1AC22C20,
            0x1AC24020, 0x1AC24420, 0x1AC24820, 0x9AC24C20, 0x1AC25020, 0x9AC25C20,
            0xDAC00020, 0xDAC00420, 0xDAC00820, 0xDAC00C20, 0xDAC01020, 0xDAC01420, 0x5AC00020, 0x5AC00820, 0x5AC01020, 0x5AC01420,
            0x9B020C20, 0x9B028C20, 0x1B020C20, 0x9B220C20, 0x9B228C20, 0x9B427C20, 0x9BA20C20, 0x9BA28C20, 0x9BC27C20,
            // Loads and stores
            0xF9400820, 0xF9000820, 0xB9400820, 0x39400820, 0x79400820, 0x39C00820, 0x39800820, 0x79C00820, 0xB9800820,
            0xF8408420, 0xF8408C20, 0xF85F8020, 0x38401420, 0xB8008C20, 0xF8626820, 0xF862D820, 0xB862C820, 0x3862F820, 0xF8627820,
            0xA9408820, 0xA9008820, 0xA9C08820, 0xA8C08820, 0x29408820, 0x69408820, 0xA9BF7BFD,
            0xFD400820, 0xFD000820, 0xBD400820, 0x3DC00820, 0x3D800820, 0xAD408820, 0x6D408820, 0x2D408820, 0xFC408420,
            0xC85F7C22, 0xC8027C23, 0x885F7C22, 0x88027C23, 0xC85FFC22, 0xC802FC23, 0xC8DFFC22, 0xC89FFC22, 0x085FFC22, 0x08DFFC22,
            0x4C407020, 0x0C407020, 0x4C40A020, 0x4C406020, 0x4C402020, 0x4C408020, 0x4C404020, 0x4C400020, 0x4CDF7020, 0x4CC27020,
            0x4C007020, 0x4C00A020, 0x4C008020, 0x4D40C020, 0x0D40C020, 0x4D408020, 0x4D400020, 0x4D404020, 0x4D000020, 0x4D008420, 0x4DDFC020,
            // FP / SIMD (stub path)
            0x1E220820, 0x1E622820, 0x1E223820, 0x1E621820, 0x1E224820, 0x1E225820, 0x1E20C020, 0x1E214020, 0x1E21C020, 0x1E22C020, 0x1E624020,
            0x1E222020, 0x1E222010, 0x1E202028, 0x1E622020, 0x1E220C20, 0x1E224C21, 0x1F020C20, 0x1F028C20, 0x1E220400, 0x1E271020,
            0x9E620020, 0x1E220020, 0x9E780020, 0x1E380020, 0x9E670020, 0x9E660020, 0x1E270020, 0x1E260020, 0x9EAF0020, 0x9EAE0020,
            0x4E22D420, 0x4E22DC20, 0x4EA2D420, 0x6E22DC20, 0x6E22FC20, 0x4E22CC20, 0x4EA2CC20, 0x4F821020, 0x4F829020, 0x5F829020,
            0x4E228420, 0x6E228420, 0x4E221C20, 0x4EA21C20, 0x6E221C20, 0x6E621C20, 0x4E22E420, 0x6E22E420, 0x4E223420, 0x4E229C20,
            0x4E080C20, 0x4E040420, 0x4E0C1C20, 0x0E0C3C20, 0x6E030420, 0x6E024000, 0x4E020000, 0x4E021020, 0x4E022020, 0x4E023820,
            0x4E205820, 0x6E205820, 0x4EA0B820, 0x4E20B820, 0x6EA1D820, 0x4EA1D820, 0x6EA1F820, 0x4EA0F820, 0x4EE0F820, 0x6EE1F820,
            0x0F000420, 0x4F07E7E0, 0x6F00E400, 0x2F00E400, 0x4F000420, 0x4F0004A0, 0x0E21B820, 0x6E30A820, 0x4EB1B820,
            0x4F0F0420, 0x4F210420, 0x6F210420, 0x4F215420, 0x0F088420, 0x0F0A8420, 0x2F08A420, 0x0F089C20, 0x4E284820, 0x4E285820,
            0x6E22C420, 0x4E21A820, 0x0E212820, 0x0EA16820, 0x5EE1B820, 0x4E61B820,
        ];

        [Test]
        public void BuiltinEncodings()
        {
            Check(_builtin);
        }

        [Test]
        public void GameEncodings()
        {
            string path = Environment.GetEnvironmentVariable("BOTWPC_INTERP_TEST_ENCODINGS");

            if (string.IsNullOrEmpty(path))
            {
                Assert.Ignore("BOTWPC_INTERP_TEST_ENCODINGS not set.");
            }

            Check(File.ReadAllLines(path).Where(l => l.Trim().Length > 0).Select(l => uint.Parse(l.Trim(), NumberStyles.HexNumber)).ToArray());
        }

        [Test]
        public void ControlFlow()
        {
            // A loop calling a function, an indirect branch and a return through a saved LR; X0 = 10 * 3 + 1.
            uint[] code =
            [
                0xAA1E03E4, // 00: mov x4, x30
                0xD2800000, // 04: mov x0, #0
                0xD2800141, // 08: mov x1, #10
                0x94000009, // 0c: bl 0x30
                0xF1000421, // 10: subs x1, x1, #1
                0x54FFFFC1, // 14: b.ne 0x0c
                0x10000082, // 18: adr x2, 0x28
                0xD61F0040, // 1c: br x2
                0xD2800000, // 20: mov x0, #0 (skipped)
                0xD503201F, // 24: nop
                0x91000400, // 28: add x0, x0, #1
                0xD65F0080, // 2c: ret x4
                0x91000C00, // 30: add x0, x0, #3
                0xD65F03C0, // 34: ret
            ];

            ulong[] jit = RunProgram(_jit, code, out _);
            ulong[] interpreted = RunProgram(_interpreter, code, out _);

            Assert.That(interpreted[0], Is.EqualTo(jit[0]));
            Assert.That(jit[0], Is.EqualTo(31UL));
        }

        private ulong[] RunProgram(LightningJitCpuContext cpu, uint[] code, out IExecutionContext context)
        {
            for (int i = 0; i < code.Length; i++)
            {
                _memory.Write(CodeAddress + (ulong)i * 4, code[i]);
            }

            cpu.InvalidateCacheRegion(CodeAddress, 0x4000);
            context = cpu.CreateExecutionContext(new ExceptionCallbacks());
            context.SetX(30, 0);
            context.SetX(31, MemBase + 0x1000);
            cpu.Execute(context, CodeAddress);

            ulong[] x = new ulong[32];

            for (int i = 0; i < 32; i++)
            {
                x[i] = context.GetX(i);
            }

            return x;
        }

        private sealed class State
        {
            public ulong[] X = new ulong[32];
            public V128[] V = new V128[32];
            public uint Nzcv, Fpcr, Fpsr;
            public byte[] Data;
        }

        private void Check(uint[] encodings)
        {
            List<string> failures = [];
            int run = 0, skipped = 0;

            string tracePath = Environment.GetEnvironmentVariable("BOTWPC_INTERP_TEST_TRACE");

            foreach (uint inst in encodings)
            {
                if (tracePath != null)
                {
                    File.AppendAllText(tracePath, $"{inst:x8}\n");
                }

                for (int n = 0; n < StatesPerEncoding; n++)
                {
                    State initial = RandomState(inst, out bool skip);

                    if (skip)
                    {
                        skipped++;
                        break;
                    }

                    string side = Environment.GetEnvironmentVariable("BOTWPC_INTERP_TEST_SIDE");
                    State a = side == "interpreter" ? initial : Execute(_jit, inst, initial);
                    State b = side == "jit" ? a : Execute(_interpreter, inst, initial);
                    string difference = Compare(a, b);

                    run++;

                    if (difference != null)
                    {
                        if (Environment.GetEnvironmentVariable("BOTWPC_INTERP_TEST_DUMP") == "1")
                        {
                            difference += $" | in: X0 {initial.X[0]:x16} X1 {initial.X[1]:x16} NZCV {initial.Nzcv >> 28:x} " +
                                string.Join(" ", Enumerable.Range(0, 4).Select(i => $"V{i} {initial.V[i].Extract<ulong>(1):x16}{initial.V[i].Extract<ulong>(0):x16}"));
                        }

                        failures.Add($"{inst:x8}: {difference}");
                        break;
                    }
                }
            }

            string report = string.Join("\n", failures);
            string outPath = Environment.GetEnvironmentVariable("BOTWPC_INTERP_TEST_OUT");

            if (!string.IsNullOrEmpty(outPath))
            {
                File.WriteAllText(outPath, report);
            }

            TestContext.Out.WriteLine($"{encodings.Length} encodings, {run} runs, {skipped} skipped, {failures.Count} mismatches");
            Assert.That(failures, Is.Empty, report.Length > 20000 ? report[..20000] : report);
        }

        /// <summary>A register none of the instruction's register fields name: it holds 0 and returns from the test code.</summary>
        private static uint ReturnRegister(uint inst)
        {
            for (uint k = 16; k < 29; k++)
            {
                if ((inst & 31) != k && ((inst >> 5) & 31) != k && ((inst >> 10) & 31) != k && ((inst >> 16) & 31) != k)
                {
                    return k;
                }
            }

            return 30;
        }

        private State Execute(LightningJitCpuContext cpu, uint inst, State initial)
        {
            uint ret = ReturnRegister(inst);

            _memory.Write(CodeAddress, inst);
            _memory.Write(CodeAddress + 4, 0xD65F0000u | (ret << 5)); // ret x<ret>
            _memory.Write(DataAddress, initial.Data);
            cpu.InvalidateCacheRegion(CodeAddress, 8);

            IExecutionContext context = cpu.CreateExecutionContext(new ExceptionCallbacks());

            for (int i = 0; i < 31; i++)
            {
                context.SetX(i, initial.X[i]);
            }

            context.SetX(31, initial.X[31]);
            context.SetX((int)ret, 0);

            for (int i = 0; i < 32; i++)
            {
                context.SetV(i, initial.V[i]);
            }

            context.Pstate = initial.Nzcv;
            context.Fpcr = initial.Fpcr;
            context.Fpsr = initial.Fpsr;

            cpu.Execute(context, CodeAddress);

            State result = new() { Nzcv = context.Pstate & 0xF0000000, Fpcr = context.Fpcr, Fpsr = context.Fpsr, Data = new byte[DataSize] };

            for (int i = 0; i < 32; i++)
            {
                result.X[i] = context.GetX(i);
                result.V[i] = context.GetV(i);
            }

            _memory.Read(DataAddress, result.Data);

            return result;
        }

        private static string Compare(State a, State b)
        {
            StringBuilder s = new();

            for (int i = 0; i < 32; i++)
            {
                if (a.X[i] != b.X[i])
                {
                    s.Append($"X{i} {a.X[i]:x16} vs {b.X[i]:x16}; ");
                }

                if (a.V[i] != b.V[i])
                {
                    s.Append($"V{i} {a.V[i].Extract<ulong>(1):x16}{a.V[i].Extract<ulong>(0):x16} vs {b.V[i].Extract<ulong>(1):x16}{b.V[i].Extract<ulong>(0):x16}; ");
                }
            }

            if (a.Nzcv != b.Nzcv)
            {
                s.Append($"NZCV {a.Nzcv >> 28:x} vs {b.Nzcv >> 28:x}; ");
            }

            if (a.Fpsr != b.Fpsr)
            {
                s.Append($"FPSR {a.Fpsr:x} vs {b.Fpsr:x}; ");
            }

            if (!a.Data.AsSpan().SequenceEqual(b.Data))
            {
                int at = a.Data.AsSpan().CommonPrefixLength(b.Data);
                s.Append($"memory at +0x{at:x}; ");
            }

            return s.Length == 0 ? null : s.ToString();
        }

        private ulong RandomValue()
        {
            return _random.Next(8) switch
            {
                0 => 0,
                1 => ulong.MaxValue,
                2 => (ulong)_random.Next(256),
                3 => 0x80000000UL,
                4 => 0x7FFFFFFFUL,
                5 => (ulong)BitConverter.DoubleToInt64Bits(_random.NextDouble() * 100 - 50),
                _ => (ulong)_random.NextInt64() ^ ((ulong)_random.Next() << 63),
            };
        }

        private V128 RandomVector()
        {
            return _random.Next(4) switch
            {
                0 => new V128(BitConverter.SingleToUInt32Bits((float)(_random.NextDouble() * 20 - 10)) | ((ulong)BitConverter.SingleToUInt32Bits((float)(_random.NextDouble() * 20 - 10)) << 32),
                              BitConverter.SingleToUInt32Bits((float)(_random.NextDouble() * 4)) | ((ulong)BitConverter.SingleToUInt32Bits(-1.5f) << 32)),
                1 => new V128((ulong)BitConverter.DoubleToInt64Bits(_random.NextDouble() * 1e6 - 5e5), (ulong)BitConverter.DoubleToInt64Bits(_random.NextDouble())),
                _ => new V128((ulong)_random.NextInt64() ^ ((ulong)_random.Next() << 63), (ulong)_random.NextInt64() ^ ((ulong)_random.Next() << 62)),
            };
        }

        private State RandomState(uint inst, out bool skip)
        {
            skip = false;
            State s = new() { Data = new byte[DataSize] };

            _random.NextBytes(s.Data);

            for (int i = 0; i < 31; i++)
            {
                s.X[i] = RandomValue();
            }

            for (int i = 0; i < 32; i++)
            {
                s.V[i] = RandomVector();
            }

            s.X[31] = MemBase + 0x1000; // SP
            s.Nzcv = (uint)_random.Next(16) << 28;
            s.Fpcr = _random.Next(4) switch { 0 => 1u << 24, 1 => 1u << 25, 2 => (uint)_random.Next(4) << 22, _ => 0 };
            s.Fpsr = 0;

            // Branches, exception generation, system: covered by ControlFlow. Undefined encodings: the compiled code
            // re-executes them forever.
            (Ryujinx.Cpu.LightningJit.Arm64.InstName name, _, _) = Ryujinx.Cpu.LightningJit.Arm64.Target.Arm64.InstTable.GetInstNameAndFlags(
                inst, CpuPresets.CortexA57.Version, CpuPresets.CortexA57.Features);

            if ((inst & 0x1C000000) == 0x14000000 || (inst & 0xFFFF0000) == 0 || name == Ryujinx.Cpu.LightningJit.Arm64.InstName.UdfPermUndef)
            {
                skip = true;
                return s;
            }

            if ((inst & 0x0A000000) == 0x08000000)
            {
                skip = !PrepareMemoryOperands(inst, s);
            }

            return s;
        }

        /// <summary>Points the address registers of a load/store at the data area; false for encodings to skip.</summary>
        private bool PrepareMemoryOperands(uint inst, State s)
        {
            uint rt = inst & 31, rn = (inst >> 5) & 31, rm = (inst >> 16) & 31, rt2 = (inst >> 10) & 31;

            void Set(uint register, ulong value)
            {
                s.X[register] = value;
            }

            bool writeback;

            if ((inst & 0xBF000000) == 0x0C000000 || (inst & 0xBF000000) == 0x0D000000) // SIMD structures
            {
                writeback = (inst & 0x00800000) != 0;
                Set(rn, MemBase);

                if (writeback && rm != 31)
                {
                    if (rm == rn)
                    {
                        return false;
                    }

                    Set(rm, (ulong)_random.Next(64));
                }

                return true;
            }

            if ((inst & 0x3F000000) == 0x08000000) // exclusive / ordered
            {
                if ((inst & 0x00200000) != 0 || rm == rt || rm == rn)
                {
                    return false;
                }

                Set(rn, MemBase);
                return true;
            }

            if ((inst & 0x3B000000) == 0x18000000) // literal
            {
                return false;
            }

            if ((inst & 0x3A000000) == 0x28000000) // pair
            {
                uint index = (inst >> 23) & 3;
                bool load = (inst & 0x00400000) != 0;
                writeback = index == 1 || index == 3;

                if ((writeback && (rt == rn || rt2 == rn) && rn != 31) || (load && rt == rt2))
                {
                    return false;
                }

                Set(rn, MemBase);
                return true;
            }

            if ((inst & 0x3B000000) == 0x39000000) // unsigned immediate
            {
                int scale = (inst & 0x04800000) == 0x04800000 ? 4 : (int)(inst >> 30);
                ulong offset = ((inst >> 10) & 0xFFF) << scale;

                Set(rn, MemBase - offset);
                return true;
            }

            if ((inst & 0x3B200000) == 0x38000000) // imm9
            {
                writeback = ((inst >> 10) & 1) != 0;

                if (writeback && rt == rn && rn != 31 && (inst & 0x04000000) == 0)
                {
                    return false;
                }

                Set(rn, MemBase);
                return true;
            }

            if ((inst & 0x3B200C00) == 0x38200800) // register offset
            {
                if (rm == rn || rm == 31)
                {
                    return false;
                }

                Set(rn, MemBase);
                Set(rm, (ulong)_random.Next(256));
                return true;
            }

            return false;
        }
    }
}
