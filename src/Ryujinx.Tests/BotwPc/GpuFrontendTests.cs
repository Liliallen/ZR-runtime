using NUnit.Framework;
using Ryujinx.Graphics.Device;
using Ryujinx.Graphics.Gpu.Engine;
using Ryujinx.Graphics.Gpu.Engine.Threed;
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Ryujinx.Tests.BotwPc
{
    /// <summary>
    /// Differential tests of the optimized GPU front end's register writes (FastRegisterWrite) against the legacy
    /// path they replace: DeviceStateWithShadow.WriteWithRedundancyCheck plus StateUpdateTracker.SetDirty on the real
    /// 3D register layout (ThreedClassState), with write callbacks and state groups on real register fields. After
    /// every write, both register files, both shadow register files, every state group's dirty bit and the callbacks
    /// run must be identical. Inputs: random streams with frequent redundant writes and shadow RAM mode changes, and,
    /// when BOTWPC_FE_TRACE_REPLAY names a local capture (BOTWPC_FE_TRACE), the game's own register write stream.
    /// </summary>
    public class GpuFrontendTests
    {
        private const int Methods = 0xe00;

        // Real fields in different parts of the register file: some with callbacks, some in state groups.
        private static readonly string[] _callbackFields =
        [
            nameof(ThreedClassState.LaunchDma),
            nameof(ThreedClassState.SyncpointAction),
            nameof(ThreedClassState.DrawEnd),
            nameof(ThreedClassState.DrawBegin),
            nameof(ThreedClassState.IndexBufferCount),
            nameof(ThreedClassState.Clear),
            nameof(ThreedClassState.UniformBufferUpdateData),
            nameof(ThreedClassState.UniformBufferBindFragment),
        ];

        private static readonly string[][] _groups =
        [
            [nameof(ThreedClassState.VertexBufferDrawState), nameof(ThreedClassState.VertexBufferState), nameof(ThreedClassState.VertexBufferEndAddress)],
            [nameof(ThreedClassState.VertexAttribState)],
            [nameof(ThreedClassState.BlendEnable), nameof(ThreedClassState.BlendState)],
            [nameof(ThreedClassState.ScissorState)],
            [nameof(ThreedClassState.IndexBufferState), nameof(ThreedClassState.IndexBufferCount)],
            [nameof(ThreedClassState.ShaderBaseAddress), nameof(ThreedClassState.ShaderState)],
            [nameof(ThreedClassState.RtColorState), nameof(ThreedClassState.RtControl)],
        ];

        private sealed class Side
        {
            public readonly DeviceStateWithShadow<ThreedClassState> State;
            public readonly StateUpdateTracker<ThreedClassState> Tracker;
            public readonly List<(string Field, int Value)> Callbacks = [];

            public Side()
            {
                Dictionary<string, RwCallback> callbacks = [];

                foreach (string field in _callbackFields)
                {
                    callbacks[field] = new RwCallback(value => Callbacks.Add((field, value)), null);
                }

                State = new DeviceStateWithShadow<ThreedClassState>(callbacks);

                StateUpdateCallbackEntry[] entries = new StateUpdateCallbackEntry[_groups.Length];

                for (int i = 0; i < entries.Length; i++)
                {
                    entries[i] = new StateUpdateCallbackEntry(() => { }, _groups[i]);
                }

                Tracker = new StateUpdateTracker<ThreedClassState>(entries);
            }

            /// <summary>ThreedClass.Write without its diagnostics probes.</summary>
            public void LegacyWrite(int offset, int value)
            {
                State.WriteWithRedundancyCheck(offset, value, out bool changed);

                if (changed)
                {
                    Tracker.SetDirty(offset);
                }
            }
        }

        private static ReadOnlySpan<int> Words(ref ThreedClassState state) =>
            MemoryMarshal.Cast<ThreedClassState, int>(MemoryMarshal.CreateReadOnlySpan(ref state, 1));

        private static int ShadowControlOffset => (int)Marshal.OffsetOf<ThreedClassState>(nameof(ThreedClassState.SetMmeShadowRamControl));

        /// <summary>
        /// Feeds the same writes to both paths, comparing everything after each write.
        /// Returns null when they agree throughout, else the first difference.
        /// </summary>
        private static string Run(IEnumerable<(int Offset, int Value)> writes, Func<byte[], byte[]> mutateKinds = null)
        {
            Side legacy = new(), fast = new();
            byte[] kinds = FastRegisterWrite.BuildKinds(Methods, DeviceStateWithShadow<ThreedClassState>.RegisterCount,
                fast.State.HasWriteCallback, fast.Tracker.GroupOf, allSlow: false);
            kinds = mutateKinds?.Invoke(kinds) ?? kinds;
            long n = 0;

            foreach ((int offset, int value) in writes)
            {
                legacy.LegacyWrite(offset, value);

                int index = offset >> 2;
                byte kind = kinds[index];

                if (kind == FastRegisterWrite.Slow)
                {
                    fast.LegacyWrite(offset, value);
                }
                else
                {
                    int group = FastRegisterWrite.Apply(
                        ref Unsafe.Add(ref Unsafe.As<ThreedClassState, int>(ref fast.State.State), index),
                        ref Unsafe.Add(ref Unsafe.As<ThreedClassState, int>(ref fast.State.ShadowState), index),
                        index,
                        fast.State.State.SetMmeShadowRamControlMode,
                        value,
                        kind);

                    if (group >= 0)
                    {
                        fast.Tracker.SetDirtyGroup(group);
                    }
                }

                n++;

                if (!Words(ref legacy.State.State).SequenceEqual(Words(ref fast.State.State)) ||
                    !Words(ref legacy.State.ShadowState).SequenceEqual(Words(ref fast.State.ShadowState)))
                {
                    return ($"register file differs after write {n} (offset 0x{offset:x} value 0x{value:x}, kind {kind})");
                }

                for (int g = 0; g < _groups.Length; g++)
                {
                    if (legacy.Tracker.IsDirty(g) != fast.Tracker.IsDirty(g))
                    {
                        return ($"dirty state of group {g} differs after write {n} (offset 0x{offset:x} value 0x{value:x})");
                    }
                }

                if (legacy.Callbacks.Count != fast.Callbacks.Count || (legacy.Callbacks.Count != 0 && legacy.Callbacks[^1] != fast.Callbacks[^1]))
                {
                    return ($"callbacks differ after write {n} (offset 0x{offset:x})");
                }

                if ((n & 63) == 0)
                {
                    // A draw consumes the dirty groups.
                    legacy.Tracker.Update(ulong.MaxValue);
                    fast.Tracker.Update(ulong.MaxValue);
                }
            }

            return n > 0 ? null : "no writes";
        }

        private static IEnumerable<(int, int)> RandomWrites(int seed, int count)
        {
            Random random = new(seed);
            int[] values = [0, 1, 2, 0x100, -1, 0x12345678];

            for (int i = 0; i < count; i++)
            {
                if (random.Next(500) == 0)
                {
                    // Shadow RAM mode: track, track with filter, passthrough, replay.
                    yield return (ShadowControlOffset, random.Next(4));
                    continue;
                }

                // Mostly a small hot set of registers, like the game's stream, with few distinct values.
                int index = random.Next(4) == 0 ? random.Next(0x60, Methods) : 0x60 + random.Next(0x40) * 0x31 % (Methods - 0x60);

                yield return (index * 4, random.Next(3) == 0 ? random.Next() : values[random.Next(values.Length)]);
            }
        }

        [TestCase(1)]
        [TestCase(2)]
        [TestCase(3)]
        public void FastRegisterWritesMatchLegacyPath(int seed)
        {
            Assert.That(Run(RandomWrites(seed, 300_000)), Is.Null);
        }

        /// <summary>Negative controls: the comparison must catch a fast path that loses a dirty group or a callback.</summary>
        [Test]
        public void DetectsWrongFastPaths()
        {
            int groupRegister = (int)Marshal.OffsetOf<ThreedClassState>(nameof(ThreedClassState.ScissorState)) / 4;
            int callbackRegister = (int)Marshal.OffsetOf<ThreedClassState>(nameof(ThreedClassState.DrawEnd)) / 4;

            List<(int, int)> writes = [(groupRegister * 4, 5)];
            Assert.That(Run(writes, k => { k[groupRegister] = FastRegisterWrite.Plain; return k; }), Does.StartWith("dirty state"));

            writes = [(callbackRegister * 4, 7)];
            Assert.That(Run(writes, k => { k[callbackRegister] = FastRegisterWrite.Plain; return k; }), Does.StartWith("callbacks differ"));

            // Replay mode must take the shadow value.
            List<(int, int)> replay = [(ShadowControlOffset, 0), (0x400 * 4, 9), (ShadowControlOffset, 3), (0x400 * 4, 1)];
            Assert.That(Run(replay), Is.Null);
        }

        [Test]
        public void SlowKindsAreExactlyTheCallbackAndOutOfRangeRegisters()
        {
            Side side = new();
            byte[] kinds = FastRegisterWrite.BuildKinds(Methods, DeviceStateWithShadow<ThreedClassState>.RegisterCount,
                side.State.HasWriteCallback, side.Tracker.GroupOf, allSlow: false);

            for (int index = 0; index < Methods; index++)
            {
                bool slow = index >= DeviceStateWithShadow<ThreedClassState>.RegisterCount || side.State.HasWriteCallback(index * 4);
                int group = side.Tracker.GroupOf(index * 4);

                Assert.That(kinds[index] == FastRegisterWrite.Slow, Is.EqualTo(slow), $"register 0x{index * 4:x}");

                if (!slow)
                {
                    Assert.That(kinds[index] - 1, Is.EqualTo(group), $"register 0x{index * 4:x}");
                }
            }

            Assert.That(FastRegisterWrite.BuildKinds(Methods, 10, side.State.HasWriteCallback, side.Tracker.GroupOf, allSlow: true),
                Has.All.EqualTo(FastRegisterWrite.Slow));
        }

        /// <summary>The game's own 3D register write stream (local capture, see FrontendTrace).</summary>
        [Test]
        public void FastRegisterWritesMatchLegacyPathOnGameTrace()
        {
            string path = Environment.GetEnvironmentVariable("BOTWPC_FE_TRACE_REPLAY");

            if (string.IsNullOrEmpty(path) || !File.Exists(path))
            {
                Assert.Ignore("set BOTWPC_FE_TRACE_REPLAY to a capture made with BOTWPC_FE_TRACE");
            }

            Assert.That(Run(ReadTrace(path)), Is.Null);
        }

        private static IEnumerable<(int, int)> ReadTrace(string path)
        {
            using BinaryReader reader = new(File.OpenRead(path));

            while (reader.BaseStream.Position + 8 <= reader.BaseStream.Length)
            {
                yield return (reader.ReadInt32(), reader.ReadInt32());
            }
        }
    }
}
