using System;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Ryujinx.Memory
{
    /// <summary>
    /// Shared memory on iOS/iPadOS, where the sandbox refuses shm_open: a named Mach memory entry (pages are created zeroed
    /// on first touch) mapped with mach_vm_map, both as the block itself and as views at fixed addresses.
    /// </summary>
    [SupportedOSPlatform("ios")]
    static partial class MachSharedMemory
    {
        private const int VmProtRead = 1, VmProtWrite = 2;
        private const int MapMemNamedCreate = 0x020000;
        private const int VmFlagsFixed = 0, VmFlagsAnywhere = 1, VmFlagsOverwrite = 0x4000;
        private const int VmInheritNone = 2;

        public static nint Create(ulong size)
        {
            ulong entrySize = size;
            int kr = MachMakeMemoryEntry64(MachTaskSelf(), ref entrySize, 0, MapMemNamedCreate | VmProtRead | VmProtWrite, out uint entry, 0);

            if (kr != 0 || entrySize < size)
            {
                throw new SystemException($"mach_make_memory_entry_64 ({size >> 20} MB) failed: {kr}");
            }

            return (nint)entry;
        }

        public static void Destroy(nint entry)
        {
            MachPortDeallocate(MachTaskSelf(), (uint)entry);
        }

        /// <summary>Maps [offset, offset + size) of the entry at <paramref name="location"/> (0: anywhere).</summary>
        public static nint Map(nint entry, ulong offset, nint location, ulong size)
        {
            ulong address = (ulong)location;
            int flags = location == 0 ? VmFlagsAnywhere : VmFlagsFixed | VmFlagsOverwrite;
            int kr = MachVmMap(MachTaskSelf(), ref address, size, 0, flags, (uint)entry, offset, 0, VmProtRead | VmProtWrite, VmProtRead | VmProtWrite, VmInheritNone);

            if (kr != 0)
            {
                throw new SystemException($"mach_vm_map (0x{location:x}, {size >> 10} KB at offset 0x{offset:x}) failed: {kr}");
            }

            return (nint)address;
        }

        [LibraryImport("libc", EntryPoint = "mach_task_self")]
        private static partial uint MachTaskSelf();

        [LibraryImport("libc", EntryPoint = "mach_make_memory_entry_64")]
        private static partial int MachMakeMemoryEntry64(uint task, ref ulong size, ulong offset, int permission, out uint handle, uint parent);

        [LibraryImport("libc", EntryPoint = "mach_vm_map")]
        private static partial int MachVmMap(uint task, ref ulong address, ulong size, ulong mask, int flags, uint entry, ulong offset,
            int copy, int curProtection, int maxProtection, int inheritance);

        [LibraryImport("libc", EntryPoint = "mach_port_deallocate")]
        private static partial int MachPortDeallocate(uint task, uint name);
    }
}
