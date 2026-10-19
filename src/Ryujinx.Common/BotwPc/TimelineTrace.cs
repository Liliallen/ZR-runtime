using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;

namespace Ryujinx.Common.BotwPc
{
    /// <summary>
    /// Cross-thread timeline for finding the frame-time critical path. With BOTWPC_TIMELINE=&lt;file&gt;:&lt;start s&gt;:&lt;duration s&gt;
    /// (seconds since process start) every instrumented region is recorded as a complete event with thread, timestamp,
    /// duration and the current guest frame number, and written as Chrome trace JSON (chrome://tracing, ui.perfetto.dev).
    /// Off by default; when off, each hook is one static bool check.
    /// </summary>
    public static class TimelineTrace
    {
        public static readonly bool Configured;
        private static readonly string _path;
        private static readonly long _start, _end;
        private static readonly long _origin = Stopwatch.GetTimestamp();

        private readonly record struct Event(string Name, long Start, long End, int Frame, string Arg);

        private sealed class ThreadLog
        {
            public readonly int Tid = Environment.CurrentManagedThreadId;
            public readonly string Name = Thread.CurrentThread.Name ?? $"thread {Environment.CurrentManagedThreadId}";
            public readonly List<Event> Events = new(4096);
        }

        [ThreadStatic]
        private static ThreadLog _log;

        private static readonly List<ThreadLog> _logs = [];
        private static int _frame;
        private static int _written;

        static TimelineTrace()
        {
            // <file>:<start>:<duration>; split from the right so a Windows drive letter ("C:\...") stays in the path.
            string value = Environment.GetEnvironmentVariable("BOTWPC_TIMELINE");
            int last = value?.LastIndexOf(':') ?? -1;
            int middle = last > 0 ? value.LastIndexOf(':', last - 1) : -1;
            string[] parts = middle > 0 ? [value[..middle], value[(middle + 1)..last], value[(last + 1)..]] : null;

            if (parts is { Length: 3 } &&
                double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double start) &&
                double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out double duration))
            {
                _path = parts[0];
                _start = _origin + (long)(start * Stopwatch.Frequency);
                _end = _start + (long)(duration * Stopwatch.Frequency);
                Configured = true;
            }
        }

        /// <summary>Guest frame number (incremented when the game queues a frame).</summary>
        public static int Frame => Volatile.Read(ref _frame);

        public static void OnGuestFrame()
        {
            int frame = Interlocked.Increment(ref _frame);

            if (Configured)
            {
                long now = Stopwatch.GetTimestamp();
                Record("GuestFrameQueued", now, now, frame.ToString(CultureInfo.InvariantCulture));
            }
        }

        /// <summary>Records a region from <paramref name="startTimestamp"/> to now on the calling thread.</summary>
        public static void Region(string name, long startTimestamp, string arg = null)
        {
            if (Configured)
            {
                Record(name, startTimestamp, Stopwatch.GetTimestamp(), arg);
            }
        }

        private static void Record(string name, long start, long end, string arg)
        {
            if (end < _start)
            {
                return;
            }

            if (start > _end)
            {
                Flush();
                return;
            }

            ThreadLog log = _log;

            if (log == null)
            {
                log = _log = new ThreadLog();

                lock (_logs)
                {
                    _logs.Add(log);
                }
            }

            lock (log)
            {
                log.Events.Add(new Event(name, start, end, Frame, arg));
            }
        }

        private static void Flush()
        {
            if (Interlocked.Exchange(ref _written, 1) != 0)
            {
                return;
            }

            StringBuilder json = new("{\"traceEvents\":[\n");
            double us = 1e6 / Stopwatch.Frequency;
            bool first = true;

            lock (_logs)
            {
                foreach (ThreadLog log in _logs)
                {
                    json.Append(first ? "" : ",\n").Append(CultureInfo.InvariantCulture,
                        $"{{\"name\":\"thread_name\",\"ph\":\"M\",\"pid\":1,\"tid\":{log.Tid},\"args\":{{\"name\":\"{log.Name}\"}}}}");
                    first = false;

                    lock (log)
                    {
                        foreach (Event e in log.Events)
                        {
                            double ts = (e.Start - _origin) * us, dur = (e.End - e.Start) * us;
                            json.Append(",\n").Append(CultureInfo.InvariantCulture,
                                $"{{\"name\":\"{e.Name}\",\"ph\":\"X\",\"pid\":1,\"tid\":{log.Tid},\"ts\":{ts:F1},\"dur\":{dur:F1},\"args\":{{\"frame\":{e.Frame}{(e.Arg == null ? "" : $",\"arg\":\"{e.Arg}\"")}}}}}");
                        }
                    }
                }
            }

            json.Append("\n]}\n");
            File.WriteAllText(_path, json.ToString());
            Logging.Logger.Info?.Print(Logging.LogClass.Application, $"BOTW-PC timeline written to {_path}");
        }
    }
}
