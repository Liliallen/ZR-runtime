using Ryujinx.Common.Logging;
using System;
using System.IO;

namespace Ryujinx.Graphics.Gpu.Engine
{
    /// <summary>
    /// BOTW-PC diagnostics (BOTWPC_FE_TRACE=&lt;file&gt;): records the 3D register writes of the command stream (method
    /// below 0xe00 on sub-channel 0, in order) as little-endian (byte offset, value) pairs of 32-bit integers, for the
    /// differential unit tests of the optimized front end (GpuFrontendTests, BOTWPC_FE_TRACE_REPLAY). Starts
    /// BOTWPC_FE_TRACE_START seconds after the GPU starts (default 110, in gameplay rather than loading) and stops
    /// after BOTWPC_FE_TRACE_COUNT writes (default 8 million, about 50 frames of BotW). The file holds register values of the running game: keep it
    /// local, never commit it.
    /// </summary>
    static class FrontendTrace
    {
        private static readonly string _path = Environment.GetEnvironmentVariable("BOTWPC_FE_TRACE");
        public static readonly bool Enabled = !string.IsNullOrEmpty(_path);

        private static readonly long _limit = long.TryParse(Environment.GetEnvironmentVariable("BOTWPC_FE_TRACE_COUNT"), out long n) ? n : 8_000_000;
        private static readonly long _start = System.Diagnostics.Stopwatch.GetTimestamp() +
            (long)((double.TryParse(Environment.GetEnvironmentVariable("BOTWPC_FE_TRACE_START"), System.Globalization.CultureInfo.InvariantCulture, out double s) ? s : 110) *
            System.Diagnostics.Stopwatch.Frequency);
        private static BinaryWriter _writer;
        private static long _count;

        public static void Record(int offset, int value)
        {
            if (_count >= _limit || (_writer == null && System.Diagnostics.Stopwatch.GetTimestamp() < _start))
            {
                return;
            }

            _writer ??= new BinaryWriter(File.Create(_path));
            _writer.Write(offset);
            _writer.Write(value);

            if (++_count == _limit)
            {
                _writer.Dispose();
                Logger.Info?.Print(LogClass.Gpu, $"BOTW-PC FE trace: {_count} 3D register writes written to {_path}");
            }
        }
    }
}
