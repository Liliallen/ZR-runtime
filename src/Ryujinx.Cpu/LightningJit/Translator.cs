using ARMeilleure.Common;
using ARMeilleure.Memory;
using Ryujinx.Cpu.Jit;
using Ryujinx.Cpu.LightningJit.Cache;
using Ryujinx.Cpu.LightningJit.CodeGen.Arm64;
using Ryujinx.Cpu.LightningJit.State;
using Ryujinx.Cpu.Signal;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;

namespace Ryujinx.Cpu.LightningJit
{
    class Translator : IDisposable
    {
        // Should be enabled on platforms that enforce W^X.
        private static bool IsNoWxPlatform => false;

        private readonly ConcurrentQueue<KeyValuePair<ulong, TranslatedFunction>> _oldFuncs;
        private readonly NoWxCache _noWxCache;
        private bool _disposed;

        internal TranslatorCache<TranslatedFunction> Functions { get; }
        internal AddressTable<ulong> FunctionTable { get; }
        internal TranslatorStubs Stubs { get; }
        internal IMemoryManager Memory { get; }

        /// <summary>
        /// Every function runs in the interpreter (BOTWPC_INTERPRET=1), or one in N (BOTWPC_INTERPRET=N, chosen by
        /// address): validates it against the compiled code on hosts with JIT. Without it, the interpreter only runs code
        /// that is neither in the AOT image nor can be compiled.
        /// </summary>
        internal bool ForceInterpreter { get; set; } = _interpretRatio == 1;

        private static readonly int _interpretRatio = int.TryParse(Environment.GetEnvironmentVariable("BOTWPC_INTERPRET"), out int n) ? n : 0;

        private bool ForceInterpreterAt(ulong address) =>
            ForceInterpreter || (_interpretRatio > 1 && (uint)((address >> 2) * 0x9E3779B97F4A7C15UL >> 40) % (uint)_interpretRatio == 0);

        // Bumped when guest code is invalidated; interpreters drop their cached code and branch targets.
        internal static int CodeGeneration;

        public Translator(IMemoryManager memory, AddressTable<ulong> functionTable)
        {
            Memory = memory;

            _oldFuncs = new ConcurrentQueue<KeyValuePair<ulong, TranslatedFunction>>();

            if (IsNoWxPlatform)
            {
                _noWxCache = new(new JitMemoryAllocator(), CreateStackWalker(), this);
            }
            else
            {
                JitCache.Initialize(new JitMemoryAllocator(forJit: true));
            }

            Functions = new TranslatorCache<TranslatedFunction>();
            FunctionTable = functionTable;
            Stubs = new TranslatorStubs(FunctionTable, _noWxCache);

            FunctionTable.Fill = (ulong)Stubs.SlowDispatchStub;

            if (AotImage.Recording)
            {
                // All stubs into the recording now (they are otherwise made lazily, the dispatch loop only once code runs).
                _ = Stubs.DispatchStub;
                _ = Stubs.SlowDispatchStub;
                _ = Stubs.DispatchLoop;
                _ = Stubs.GetFpFlagsStub;
                _ = Stubs.SetFpFlagsStub;
                _ = Stubs.SimdTrampolineStub;

                AotImage.SimdScanner = (start, size) =>
                {
                    for (ulong address = start; address < start + size; address += 4)
                    {
                        uint inst = Memory.Read<uint>(address);

                        if ((inst & 0x0E000000) == 0x0E000000 && Interpreter.A64Interpreter.TryGetSimdForm(inst, out var form))
                        {
                            AotImage.RecordSimd(form.Normalized);
                        }
                    }
                };

                AotImage.Precompiler = address => AotImage.CollectExits(() =>
                {
                    CompiledFunction func = Compile(address, ExecutionMode.Aarch64);
                    AotImage.RecordFunction(Memory, address, func.Code, (ulong)func.GuestCodeLength);
                });
            }

            if (memory.Type.IsHostMappedOrTracked())
            {
                NativeSignalHandler.InitializeSignalHandler();
            }
        }

        private static StackWalker CreateStackWalker()
        {
            if (RuntimeInformation.ProcessArchitecture == Architecture.Arm64)
            {
                return new StackWalker();
            }
            else
            {
                throw new PlatformNotSupportedException();
            }
        }

        public void Execute(State.ExecutionContext context, ulong address)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            NativeInterface.RegisterThread(context, Memory, this);
            HostPointers.Fill(context.NativeContextPtr, this);

            Stubs.DispatchLoop(context.NativeContextPtr, address);

            NativeInterface.UnregisterThread();
            _noWxCache?.ClearEntireThreadLocalCache();
        }

        internal nint GetOrTranslatePointer(nint framePointer, ulong address, ExecutionMode mode)
        {
            if (_noWxCache != null)
            {
                CompiledFunction func = Compile(address, mode);

                return _noWxCache.Map(framePointer, func.Code, address, (ulong)func.GuestCodeLength);
            }

            return GetOrTranslate(address, mode).FuncPointer;
        }

        private TranslatedFunction GetOrTranslate(ulong address, ExecutionMode mode)
        {
            if (!Functions.TryGetValue(address, out TranslatedFunction func))
            {
                bool force = ForceInterpreterAt(address);
                bool fromImage = !force && AotImage.TryGet(Memory, address, out func);
                bool interpreted = !fromImage && (force || (!AotImage.JitAvailable && mode == ExecutionMode.Aarch64));

                if (interpreted)
                {
                    // Neither in the image nor compilable: the interpreter runs it (entered like a translated function).
                    func = new TranslatedFunction(Interpreter.A64Interpreter.EntryPointer, 4);
                }
                else if (!fromImage)
                {
                    func = Translate(address, mode);
                }

                TranslatedFunction oldFunc = Functions.GetOrAdd(address, func.GuestSize, func);

                if (oldFunc != func)
                {
                    if (!fromImage && !interpreted)
                    {
                        JitCache.Unmap(func.FuncPointer);
                    }

                    func = oldFunc;
                }

                RegisterFunction(address, func);
            }

            return func;
        }

        private readonly ConcurrentDictionary<ulong, nint> _compiledForInterpreter = new();

        /// <summary>The interpreter's fallback for instructions it lacks (once JIT is available): compiled code there.</summary>
        internal nint GetCompiledFunction(ulong address)
        {
            return _compiledForInterpreter.GetOrAdd(address, a => JitCache.Map(Compile(a, ExecutionMode.Aarch64).Code));
        }

        /// <summary>
        /// For the interpreter's branches: native code for an address (already known, from the image, or compiled when the
        /// process can), or 0 when it would be interpreted.
        /// </summary>
        internal nint TryGetNativeFunction(ulong address)
        {
            if (ForceInterpreter)
            {
                return 0;
            }

            if (!Functions.TryGetValue(address, out TranslatedFunction func) && (AotImage.TryGet(Memory, address, out _) || AotImage.JitAvailable))
            {
                func = GetOrTranslate(address, ExecutionMode.Aarch64);
            }

            return func == null || func.FuncPointer == Interpreter.A64Interpreter.EntryPointer ? 0 : func.FuncPointer;
        }

        internal void RegisterFunction(ulong guestAddress, TranslatedFunction func)
        {
            if (FunctionTable.IsValid(guestAddress))
            {
                Volatile.Write(ref FunctionTable.GetValue(guestAddress), (ulong)func.FuncPointer);
            }
        }

        // BOTWPC_JIT_STATS=1: translated functions and host code bytes, logged every 1000 functions (AOT/memory sizing).
        private static readonly bool _jitStats = System.Environment.GetEnvironmentVariable("BOTWPC_JIT_STATS") == "1";
        private static long _statFunctions, _statCodeBytes, _statGuestBytes;

        private TranslatedFunction Translate(ulong address, ExecutionMode mode)
        {
            if (!AotImage.JitAvailable && !AotImage.TryAcquireJit(address))
            {
                throw AotImage.Miss(address);
            }

            CompiledFunction func = Compile(address, mode);
            nint funcPointer = JitCache.Map(func.Code);

            AotImage.RecordFunction(Memory, address, func.Code, (ulong)func.GuestCodeLength);

            if (_jitStats)
            {
                long n = Interlocked.Increment(ref _statFunctions);
                long code = Interlocked.Add(ref _statCodeBytes, func.Code.Length);
                long guest = Interlocked.Add(ref _statGuestBytes, func.GuestCodeLength);

                if (n % 1000 == 0)
                {
                    Common.Logging.Logger.Info?.Print(Common.Logging.LogClass.Cpu, $"JIT stats: {n} functions, host code {code >> 10} KB, guest code {guest >> 10} KB");
                }
            }

            return new TranslatedFunction(funcPointer, (ulong)func.GuestCodeLength);
        }

        private CompiledFunction Compile(ulong address, ExecutionMode mode)
        {
            return AarchCompiler.Compile(CpuPresets.CortexA57, Memory, address, FunctionTable, Stubs.DispatchStub, mode, RuntimeInformation.ProcessArchitecture);
        }

        public void InvalidateJitCacheRegion(ulong address, ulong size)
        {
            Interlocked.Increment(ref CodeGeneration);

            ulong[] overlapAddresses = [];

            int overlapsCount = Functions.GetOverlaps(address, size, ref overlapAddresses);

            for (int index = 0; index < overlapsCount; index++)
            {
                ulong overlapAddress = overlapAddresses[index];

                if (Functions.TryGetValue(overlapAddress, out TranslatedFunction overlap))
                {
                    Functions.Remove(overlapAddress);
                    Volatile.Write(ref FunctionTable.GetValue(overlapAddress), FunctionTable.Fill);
                    EnqueueForDeletion(overlapAddress, overlap);
                }
            }

            // TODO: Remove overlapping functions from the JitCache aswell.
            // This should be done safely, with a mechanism to ensure the function is not being executed.
        }

        private void EnqueueForDeletion(ulong guestAddress, TranslatedFunction func)
        {
            _oldFuncs.Enqueue(new(guestAddress, func));
        }

        private void ClearJitCache()
        {
            List<TranslatedFunction> functions = Functions.AsList();

            foreach (TranslatedFunction func in functions)
            {
                JitCache.Unmap(func.FuncPointer);
            }

            Functions.Clear();

            while (_oldFuncs.TryDequeue(out KeyValuePair<ulong, TranslatedFunction> kv))
            {
                JitCache.Unmap(kv.Value.FuncPointer);
            }
        }

        protected virtual void Dispose(bool disposing)
        {
            if (!_disposed)
            {
                if (disposing)
                {
                    if (_noWxCache != null)
                    {
                        _noWxCache.Dispose();
                    }
                    else
                    {
                        ClearJitCache();
                    }

                    Stubs.Dispose();
                    FunctionTable.Dispose();
                }

                _disposed = true;
            }
        }

        public void Dispose()
        {
            Dispose(disposing: true);
            GC.SuppressFinalize(this);
        }
    }
}
