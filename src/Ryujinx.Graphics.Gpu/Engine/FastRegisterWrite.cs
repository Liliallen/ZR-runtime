using System;
using System.Runtime.CompilerServices;

namespace Ryujinx.Graphics.Gpu.Engine
{
    /// <summary>
    /// BOTW-PC optimized front end: register writes from the command stream without the general device-state path
    /// (GPU_FRONTEND_REWRITE.md, "Method dispatch"). A legacy write (ThreedClass.Write → DeviceStateWithShadow →
    /// DeviceState → StateUpdateTracker) looks up the register's write callback in one table and its state group in
    /// another, re-reads the shadow RAM mode through two wrappers and compares the value. Here one byte per register
    /// (<see cref="BuildKinds"/>) says whether the register needs the legacy path (it has a write callback) and which
    /// group to mark dirty, and <see cref="Apply"/> does the shadow RAM handling, the store and the change test.
    /// </summary>
    static class FastRegisterWrite
    {
        /// <summary>No state group.</summary>
        public const byte Plain = 0;

        /// <summary>The register needs the legacy path (write callback, out of range, or a diagnostics probe).</summary>
        public const byte Slow = 0xff;

        /// <summary>Registers below this index are never redirected to the shadow RAM (DeviceStateWithShadow: 0x200 bytes).</summary>
        public const int FirstShadowedRegister = 0x80;

        /// <summary>
        /// One entry per register: <see cref="Slow"/>, <see cref="Plain"/>, or state group index + 1.
        /// </summary>
        /// <param name="count">Number of entries (registers that the command stream can address)</param>
        /// <param name="registerCount">Registers of the state; writes beyond are ignored by the legacy path</param>
        /// <param name="hasWriteCallback">Whether the legacy write runs a callback for a register (byte offset)</param>
        /// <param name="groupOf">State group marked dirty by a changed register (byte offset), or -1</param>
        /// <param name="allSlow">Use the legacy path for every register</param>
        public static byte[] BuildKinds(int count, int registerCount, Func<int, bool> hasWriteCallback, Func<int, int> groupOf, bool allSlow)
        {
            byte[] kinds = new byte[count];

            for (int index = 0; index < count; index++)
            {
                int offset = index * 4;

                if (allSlow || index >= registerCount || hasWriteCallback(offset))
                {
                    kinds[index] = Slow;
                    continue;
                }

                int group = groupOf(offset);

                kinds[index] = group < 0 ? Plain : group < Slow - 1 ? (byte)(group + 1) : Slow;
            }

            return kinds;
        }

        /// <summary>
        /// Performs a register write of a register whose kind is not <see cref="Slow"/>, as the legacy path does:
        /// from <see cref="FirstShadowedRegister"/> on, the track modes also store the value in the shadow state and the
        /// replay mode writes the shadow value instead; the value is stored.
        /// </summary>
        /// <returns>The state group to mark dirty, or -1 when the register value did not change or has no group</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int Apply(ref int register, ref int shadow, int index, SetMmeShadowRamControlMode mode, int data, byte kind)
        {
            if (index >= FirstShadowedRegister && mode != SetMmeShadowRamControlMode.MethodPassthrough)
            {
                if (mode == SetMmeShadowRamControlMode.MethodReplay)
                {
                    data = shadow;
                }
                else
                {
                    shadow = data;
                }
            }

            if (register == data)
            {
                return -1;
            }

            register = data;

            return kind - 1;
        }
    }
}
