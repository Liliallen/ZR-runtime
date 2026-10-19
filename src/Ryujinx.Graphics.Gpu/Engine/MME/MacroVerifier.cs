using Ryujinx.Graphics.Device;
using Ryujinx.Graphics.Gpu.Engine.Threed;
using System;
using System.Collections.Generic;

namespace Ryujinx.Graphics.Gpu.Engine.MME
{
    /// <summary>
    /// BOTW-PC GPU V2 differential verification of HLE macros (BOTWPC_GPU_VERIFY=1, GPU_FRONTEND_V2.md). Before an HLE
    /// macro runs, the guest's own macro code is executed by the reference <see cref="MacroInterpreter"/> with the same
    /// arguments, against a recording view of the 3D register state (reads see the real registers or the interpreter's
    /// own earlier writes; writes are only recorded, so no method side effect happens twice). After the HLE function has
    /// run, every register without a write callback that the guest macro would have written must hold the same value in
    /// the real state. Registers with a write callback (draw triggers, bind/update methods) are counted separately: their
    /// effect is what the HLE function performs directly, not a stored value.
    /// </summary>
    static class MacroVerifier
    {
        private sealed class RecordingState : IDeviceState
        {
            private readonly IDeviceState _real;
            public readonly Dictionary<int, int> Writes = [];
            public int WriteCount;

            public RecordingState(IDeviceState real)
            {
                _real = real;
            }

            public int Read(int offset)
            {
                return Writes.TryGetValue(offset, out int value) ? value : _real.Read(offset);
            }

            public void Write(int offset, int data)
            {
                Writes[offset] = data;
                WriteCount++;
            }
        }

        private static string[] _names;

        /// <summary>Name of the 3D register field at a byte offset (reflection over the register layout, verify mode).</summary>
        private static string RegisterName(int offset)
        {
            if (_names == null)
            {
                string[] names = new string[0x4000 / 4];

                try
                {
                    Type type = typeof(ThreedClassState);
                    System.Collections.Generic.List<(int Offset, string Name)> fields = [];

                    foreach (System.Reflection.FieldInfo field in type.GetFields(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic))
                    {
                        fields.Add(((int)System.Runtime.InteropServices.Marshal.OffsetOf(type, field.Name), field.Name));
                    }

                    fields.Sort((a, b) => a.Offset.CompareTo(b.Offset));

                    // A field spans up to the next field's offset (generic array fields have no marshalling size).
                    for (int f = 0; f < fields.Count; f++)
                    {
                        int start = fields[f].Offset;
                        int end = f + 1 < fields.Count ? fields[f + 1].Offset : start + 4;

                        for (int o = start; o < end && o / 4 < names.Length; o += 4)
                        {
                            names[o / 4] = o == start ? fields[f].Name : $"{fields[f].Name}+0x{o - start:x}";
                        }
                    }
                }
                catch (Exception)
                {
                    // Names are only a diagnostic aid.
                }

                _names = names;
            }

            return (uint)offset / 4 < (uint)_names.Length ? _names[offset / 4] ?? "?" : "?";
        }

        private static long _calls;
        private static long _plainRegisters;
        private static long _callbackRegisters;

        /// <summary>
        /// Runs <paramref name="hle"/> (the HLE function) with the reference check around it, when the state is the 3D
        /// engine; otherwise just runs it.
        /// </summary>
        public static void Execute(IMacroEE hle, MacroHLEFunctionName function, ReadOnlySpan<int> code, IDeviceState state, int argument)
        {
            if (state is not ThreedClass threed)
            {
                hle.Execute(code, state, argument);

                return;
            }

            MacroInterpreter reference = new();

            foreach (FifoWord word in hle.Fifo)
            {
                reference.Fifo.Enqueue(word);
            }

            RecordingState recording = new(state);
            reference.Execute(code, recording, argument);

            hle.Execute(code, state, argument);

            _calls++;

            foreach ((int offset, int expected) in recording.Writes)
            {
                if (threed.HasWriteCallback(offset))
                {
                    _callbackRegisters++;

                    continue;
                }

                _plainRegisters++;

                int actual = threed.Read(offset);

                FrontendVerify.Compare(FrontendVerify.Check.MacroHle, actual == expected,
                    () => $"HLE macro {function}: register 0x{offset:x} ({RegisterName(offset)}) = 0x{actual:x8}, guest macro code writes 0x{expected:x8} " +
                        $"({recording.WriteCount} writes, {_calls} calls, {_plainRegisters} plain / {_callbackRegisters} callback registers compared)");
            }
        }
    }
}
