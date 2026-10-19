using Ryujinx.Common.Logging;
using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace Ryujinx.Cpu.LightningJit.Cache
{
    /// <summary>
    /// iOS/iPadOS code memory. Writable+executable memory (MAP_JIT) needs an entitlement only Apple's own apps get, and
    /// pages cannot be flipped between writable and executable either. Instead the arena is one read+execute mapping and
    /// a second, read+write mapping of the same pages: code is written through the alias and runs from the RX side.
    /// Executing those pages requires a debugger to have attached to the process once (CS_DEBUGGED); on devices with
    /// TXM the debugger must in addition prepare the RX range itself, which the host app does in
    /// <see cref="Prepare"/> (StikDebug / lldb "JIT26" protocol). The whole arena is prepared at startup so that the
    /// debugger can detach before the game runs.
    /// </summary>
    public static unsafe partial class IosJitArena
    {
        private const int ProtRead = 1, ProtWrite = 2, ProtExec = 4;
        private const int MapPrivate = 2, MapAnon = 0x1000;
        private const int VmFlagsAnywhere = 1;

        /// <summary>Host callback: makes [address, address + size) executable (0 = done). Set before the game starts.</summary>
        public static delegate* unmanaged[Cdecl]<nint, nuint, int> Prepare;

        public const ulong DefaultSize = 272 * 1024 * 1024; // JitCache (256 MB) + signal handler and spare pages

        private static nint _rx;
        private static long _rwDelta;
        private static ulong _size;
        private static ulong _next;
        private static readonly Lock _lock = new();

        public static bool IsActive => _rx != 0;

        public static nint Allocate(ulong size)
        {
            lock (_lock)
            {
                if (_rx == 0)
                {
                    Initialize(DefaultSize);
                }

                ulong page = (ulong)Environment.SystemPageSize;
                size = (size + page - 1) & ~(page - 1);

                if (_next + size > _size)
                {
                    throw new OutOfMemoryException($"iOS JIT arena exhausted ({_size >> 20} MB).");
                }

                nint address = _rx + (nint)_next;
                _next += size;

                return address;
            }
        }

        /// <summary>Copies code to <paramref name="rx"/> (an address returned by <see cref="Allocate"/>) through the writable alias.</summary>
        public static void Write(nint rx, ReadOnlySpan<byte> code)
        {
            code.CopyTo(new Span<byte>((void*)(rx + (nint)_rwDelta), code.Length));
            SysIcacheInvalidate(rx, code.Length);
        }

        private static void Initialize(ulong size)
        {
            nint rx = Mmap(0, (nuint)size, ProtRead | ProtExec, MapPrivate | MapAnon, -1, 0);

            if (rx == -1)
            {
                throw new OutOfMemoryException($"iOS JIT arena: no address space for {size >> 20} MB.");
            }

            long start = System.Diagnostics.Stopwatch.GetTimestamp();

            if (Prepare != null && Prepare(rx, (nuint)size) != 0)
            {
                throw new InvalidOperationException("iOS JIT arena: the host could not make the code memory executable (no debugger attached?).");
            }

            double prepareSeconds = System.Diagnostics.Stopwatch.GetElapsedTime(start).TotalSeconds;

            ulong rw = 0;
            int cur, max;
            int kr = MachVmRemap(MachTaskSelf(), ref rw, size, 0, VmFlagsAnywhere, MachTaskSelf(), (ulong)rx, 0, out cur, out max, 2);

            if (kr != 0 || Mprotect((nint)rw, (nuint)size, ProtRead | ProtWrite) != 0)
            {
                throw new InvalidOperationException($"iOS JIT arena: writable alias failed (kern_return {kr}).");
            }

            _rx = rx;
            _rwDelta = (long)rw - rx;
            _size = size;

            // A refused route crashes here (SIGBUS/SIGKILL on the first executed page), not later inside the game.
            ReadOnlySpan<byte> probe = [0x40, 0x05, 0x80, 0xD2, 0xC0, 0x03, 0x5F, 0xD6]; // mov x0, #42; ret
            Write(rx, probe);

            if (((delegate* unmanaged<long>)rx)() != 42)
            {
                throw new InvalidOperationException("iOS JIT arena: probe returned a wrong value.");
            }

            _next = (ulong)Environment.SystemPageSize;

            Logger.Info?.Print(LogClass.Cpu, $"iOS JIT arena: {size >> 20} MB at 0x{rx:x} (RW alias 0x{rw:x}), prepared in {prepareSeconds:F1} s.");
        }

        [LibraryImport("libc", EntryPoint = "mmap")]
        private static partial nint Mmap(nint address, nuint length, int prot, int flags, int fd, long offset);

        [LibraryImport("libc", EntryPoint = "mprotect")]
        private static partial int Mprotect(nint address, nuint length, int prot);

        [LibraryImport("libc", EntryPoint = "mach_task_self")]
        private static partial uint MachTaskSelf();

        [LibraryImport("libc", EntryPoint = "mach_vm_remap")]
        private static partial int MachVmRemap(uint targetTask, ref ulong targetAddress, ulong size, ulong mask, int flags,
            uint srcTask, ulong srcAddress, int copy, out int curProtection, out int maxProtection, int inheritance);

        [LibraryImport("libc", EntryPoint = "sys_icache_invalidate")]
        private static partial void SysIcacheInvalidate(nint start, nint length);
    }
}
