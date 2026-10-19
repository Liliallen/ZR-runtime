using System;
using System.IO;

namespace ARMeilleure.Diagnostics
{
    /// <summary>
    /// BOTW-PC: with BOTWPC_GUEST_PERFMAP=&lt;file&gt;, writes "host-address size guest_&lt;address&gt; guest-size" for every translated
    /// guest function and JIT stub (perf map format), so Linux perf, or botw/scripts/guest_profile.py with the Windows sampler
    /// (botw/tools/GuestSampler), can attribute JIT code to guest functions. Off by default.
    /// </summary>
    static class GuestPerfMap
    {
        private static readonly StreamWriter _writer = Open();

        private static StreamWriter Open()
        {
            string path = Environment.GetEnvironmentVariable("BOTWPC_GUEST_PERFMAP");

            return string.IsNullOrEmpty(path) ? null : new StreamWriter(path, append: true) { AutoFlush = true };
        }

        public static void Write(nint host, int size, ulong guest, ulong guestSize)
        {
            if (_writer == null)
            {
                return;
            }

            lock (_writer)
            {
                _writer.WriteLine($"{(ulong)host:x} {size:x} guest_{guest:x} {guestSize:x}");
            }
        }

        /// <summary>Named JIT code that is not a guest function (dispatch stubs, wrappers).</summary>
        public static void Write(nint host, int size, string name)
        {
            if (_writer == null)
            {
                return;
            }

            lock (_writer)
            {
                _writer.WriteLine($"{(ulong)host:x} {size:x} {name}");
            }
        }
    }
}
