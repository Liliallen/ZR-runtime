// BOTW-PC guest CPU sampler (Windows, diagnostics only; the Linux equivalent is `perf record` with BOTWPC_GUEST_PERFMAP).
//
// Samples the instruction pointer of every thread of a running Ryujinx process at a fixed rate from OUTSIDE the
// process (SuspendThread / GetThreadContext / ResumeThread; works for the user's own processes without administrator
// rights, unlike kernel ETW sampling). Nothing is injected and the emulator is not modified.
//
//   GuestSampler <pid> <out.csv> [--delay s] [--secs s] [--hz n] [--threads prefix,prefix]
//   GuestSampler <pid> <out.csv> --cputime [--delay s] [--secs s]
//   GuestSampler <pid> <out.csv> [--delay s] [--secs s] --jit <BOTWPC_GUEST_PERFMAP file> [--top n]
//
// --jit: after sampling, disassembles the hottest JIT-compiled guest functions of the guest threads from the live process
// (out.csv.jit.txt: samples per host instruction, host/guest instruction ratio per function). Does not suspend the
// process beyond the sampling itself (no symbol resolution), so the run's FPS stays valid.
//
// --cputime: no sampling; the CPU time (user + kernel, GetThreadTimes) every named thread used between --delay and
// --delay + --secs, one line per thread ("thread,tid,user_ms,kernel_ms,window_s"). Exact per-thread CPU time without
// suspending anything, e.g. the guest CPU threads (HLE.GuestThread.*) per frame.
//
// Output: out.csv with "thread,tid,rip,samples" (aggregated) and out.csv.modules with "base,size,path" of every loaded
// module. botw/scripts/guest_profile.py symbolizes the samples with the BOTWPC_GUEST_PERFMAP file (JIT-compiled guest
// functions and JIT stubs) and the module list (native code: waits in ntdll, driver, CoreCLR, ...).
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

static unsafe class Program
{
    private const uint ThreadSuspendResume = 0x0002, ThreadGetContext = 0x0008, ThreadQueryLimitedInformation = 0x0800;
    private const uint ContextControl = 0x00100001; // CONTEXT_AMD64 | CONTEXT_CONTROL (Rip, Rsp, EFlags, segments)
    private const int ContextSize = 1232, ContextFlagsOffset = 0x30, RipOffset = 0xF8;

    [DllImport("kernel32.dll", SetLastError = true)] private static extern nint OpenThread(uint access, bool inherit, uint tid);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(nint h);
    [DllImport("kernel32.dll")] private static extern uint SuspendThread(nint h);
    [DllImport("kernel32.dll")] private static extern uint ResumeThread(nint h);
    [DllImport("kernel32.dll")] private static extern bool GetThreadContext(nint h, byte* context);
    [DllImport("kernel32.dll")] private static extern int GetThreadDescription(nint h, out nint description);
    [DllImport("kernel32.dll")] private static extern nint LocalFree(nint p);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern nint OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool ReadProcessMemory(nint process, nint address, byte* buffer, nint size, out nint read);
    [DllImport("kernel32.dll")] private static extern bool GetThreadTimes(nint h, out long creation, out long exit, out long kernel, out long user);
    [DllImport("winmm.dll")] private static extern uint timeBeginPeriod(uint ms);
    [DllImport("winmm.dll")] private static extern uint timeEndPeriod(uint ms);

    private sealed class Target
    {
        public uint Tid;
        public nint Handle;
        public string Name;
        public long Samples, Failed;
    }

    static int Main(string[] args)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine("usage: GuestSampler <pid> <out.csv> [--delay s] [--secs s] [--hz n] [--threads HLE.,GPU.,GUI.]");
            return 2;
        }

        int pid = int.Parse(args[0], CultureInfo.InvariantCulture);
        string outPath = args[1];

        if (Array.IndexOf(args, "--cputime") >= 0)
        {
            return ThreadCpuTime(pid, outPath, Option(args, "--delay", 0), Option(args, "--secs", 30));
        }

        if (Array.IndexOf(args, "--stacks") is int si and >= 0)
        {
            return SampleStacks(pid, outPath, args[si + 1], Option(args, "--delay", 0), Option(args, "--secs", 30), Option(args, "--hz", 100),
                Array.IndexOf(args, "--dac") is int sd and >= 0 ? args[sd + 1] : null);
        }
        string[] prefixes = (Array.IndexOf(args, "--threads") is int ti and >= 0 ? args[ti + 1] : "HLE.,GPU.,GUI.").Split(',');
        double delay = Option(args, "--delay", 0), secs = Option(args, "--secs", 30), hz = Option(args, "--hz", 1000);

        Process process = Process.GetProcessById(pid);
        Thread.Sleep(TimeSpan.FromSeconds(delay));

        Dictionary<uint, Target> targets = [];
        Dictionary<(uint, ulong), long> counts = [];
        byte* context = (byte*)NativeMemory.AlignedAlloc(ContextSize, 64);
        Thread.CurrentThread.Priority = ThreadPriority.Highest;
        timeBeginPeriod(1);

        Stopwatch clock = Stopwatch.StartNew();
        long nextRefresh = 0, rounds = 0, period = (long)(Stopwatch.Frequency / hz);
        long next = Stopwatch.GetTimestamp();

        while (clock.Elapsed.TotalSeconds < secs && !process.HasExited)
        {
            if (clock.ElapsedMilliseconds >= nextRefresh)
            {
                Refresh(process, targets);
                nextRefresh = clock.ElapsedMilliseconds + 1000;
            }

            foreach (Target t in targets.Values)
            {
                if (!Array.Exists(prefixes, p => t.Name.StartsWith(p, StringComparison.Ordinal)))
                {
                    continue;
                }

                if (t.Handle == 0 || SuspendThread(t.Handle) == uint.MaxValue)
                {
                    t.Failed++;
                    continue;
                }

                *(uint*)(context + ContextFlagsOffset) = ContextControl;
                bool ok = GetThreadContext(t.Handle, context);
                ResumeThread(t.Handle);

                if (!ok)
                {
                    t.Failed++;
                    continue;
                }

                ulong rip = *(ulong*)(context + RipOffset);
                counts.TryGetValue((t.Tid, rip), out long c);
                counts[(t.Tid, rip)] = c + 1;
                t.Samples++;
            }

            rounds++;
            next += period;
            long wait = next - Stopwatch.GetTimestamp();

            if (wait > Stopwatch.Frequency / 1000)
            {
                Thread.Sleep((int)(wait * 1000 / Stopwatch.Frequency));
            }
            else if (wait < -10 * period)
            {
                next = Stopwatch.GetTimestamp(); // fell behind (e.g. many threads): do not burst
            }
        }

        timeEndPeriod(1);
        double elapsed = clock.Elapsed.TotalSeconds;

        using (StreamWriter w = new(outPath))
        {
            w.WriteLine("thread,tid,rip,samples");

            foreach (((uint tid, ulong rip), long c) in counts)
            {
                string name = targets.TryGetValue(tid, out Target t) ? t.Name : "?";
                w.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{name.Replace(',', ';')},{tid},{rip:x},{c}"));
            }
        }

        using (StreamWriter w = new(outPath + ".modules"))
        {
            w.WriteLine("base,size,path");
            process.Refresh();

            foreach (ProcessModule m in process.Modules)
            {
                w.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{(ulong)m.BaseAddress:x},{m.ModuleMemorySize:x},{m.FileName}"));
            }
        }

        if (Array.IndexOf(args, "--jit") is int ji and >= 0 && !process.HasExited)
        {
            Dictionary<ulong, long> guestCounts = [];

            foreach (((uint tid, ulong rip), long c) in counts)
            {
                if (targets.TryGetValue(tid, out Target t) && t.Name.StartsWith("HLE.GuestThread", StringComparison.Ordinal))
                {
                    guestCounts.TryGetValue(rip, out long n);
                    guestCounts[rip] = n + c;
                }
            }

            WriteJitDisassembly(pid, args[ji + 1], guestCounts, outPath + ".jit.txt", (int)Option(args, "--top", 60));
        }

        if (Array.IndexOf(args, "--managed") >= 0 && !process.HasExited)
        {
            string disasmThread = Array.IndexOf(args, "--disasm") is int dt and >= 0 ? args[dt + 1] : null;
            Dictionary<ulong, long> threadCounts = [];

            foreach (((uint tid, ulong rip), long c) in counts)
            {
                if (disasmThread != null && targets.TryGetValue(tid, out Target t) && t.Name == disasmThread)
                {
                    threadCounts.TryGetValue(rip, out long n);
                    threadCounts[rip] = n + c;
                }
            }

            WriteManagedMethods(pid, counts.Keys, outPath + ".methods", Array.IndexOf(args, "--dac") is int di and >= 0 ? args[di + 1] : null,
                threadCounts, outPath + ".disasm.txt");
        }

        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"{rounds} rounds in {elapsed:F1} s ({rounds / elapsed:F0}/s), {targets.Count} threads, {counts.Count} distinct (thread, rip)"));

        foreach (Target t in targets.Values)
        {
            CloseHandle(t.Handle);
        }

        return 0;
    }

    private static int ThreadCpuTime(int pid, string outPath, double delay, double secs)
    {
        Process process = Process.GetProcessById(pid);
        Thread.Sleep(TimeSpan.FromSeconds(delay));

        Dictionary<uint, Target> targets = [];
        Refresh(process, targets);
        Dictionary<uint, (long User, long Kernel)> start = [];

        foreach (Target t in targets.Values)
        {
            if (t.Handle != 0 && GetThreadTimes(t.Handle, out _, out _, out long k, out long u))
            {
                start[t.Tid] = (u, k);
            }
        }

        long t0 = Stopwatch.GetTimestamp();
        Thread.Sleep(TimeSpan.FromSeconds(secs));
        double window = (Stopwatch.GetTimestamp() - t0) / (double)Stopwatch.Frequency;

        using StreamWriter w = new(outPath);
        w.WriteLine("thread,tid,user_ms,kernel_ms,window_s");

        foreach (Target t in targets.Values)
        {
            if (t.Handle != 0 && start.TryGetValue(t.Tid, out var s) && GetThreadTimes(t.Handle, out _, out _, out long k, out long u))
            {
                // FILETIME units: 100 ns.
                w.WriteLine(string.Create(CultureInfo.InvariantCulture,
                    $"{t.Name.Replace(',', ' ')},{t.Tid},{(u - s.User) / 1e4:F1},{(k - s.Kernel) / 1e4:F1},{window:F3}"));
            }
        }

        return 0;
    }

    private static void Refresh(Process process, Dictionary<uint, Target> targets)
    {
        process.Refresh();
        HashSet<uint> alive = [];

        foreach (ProcessThread pt in process.Threads)
        {
            uint tid = (uint)pt.Id;
            alive.Add(tid);

            if (!targets.TryGetValue(tid, out Target t))
            {
                nint h = OpenThread(ThreadSuspendResume | ThreadGetContext | ThreadQueryLimitedInformation, false, tid);
                targets[tid] = t = new Target { Tid = tid, Handle = h, Name = $"tid {tid}" };
            }

            // Names can be set after the thread starts; re-read them.
            if (t.Handle != 0 && GetThreadDescription(t.Handle, out nint d) >= 0 && d != 0)
            {
                string name = Marshal.PtrToStringUni(d);
                LocalFree(d);

                if (!string.IsNullOrEmpty(name))
                {
                    t.Name = name;
                }
            }
        }

        foreach (uint tid in new List<uint>(targets.Keys))
        {
            if (!alive.Contains(tid))
            {
                CloseHandle(targets[tid].Handle);
                targets[tid].Handle = 0;
            }
        }
    }

    // --stacks <thread name>: call stacks of one thread. The thread is suspended and its managed stack walked through
    // ClrMD (the runtime's own unwinder, via the DAC) at --hz (default 100; each walk keeps the thread suspended for
    // roughly 0.1-1 ms, so the run is slowed a little; use it for attribution, not for timing). Output: collapsed stacks,
    // "outer;...;inner count" per line (flame graph format); native frames are omitted, so a sample in native code
    // (a wait, VirtualProtect) is attributed to the managed method that called it.
    private static int SampleStacks(int pid, string outPath, string threadName, double delay, double secs, double hz, string dacPath)
    {
        Process process = Process.GetProcessById(pid);
        Thread.Sleep(TimeSpan.FromSeconds(delay));

        Dictionary<uint, Target> targets = [];
        Refresh(process, targets);
        Target target = null;

        foreach (Target t in targets.Values)
        {
            if (t.Name == threadName)
            {
                target = t;
            }
        }

        if (target == null)
        {
            Console.Error.WriteLine($"thread {threadName} not found");
            return 1;
        }

        using Microsoft.Diagnostics.Runtime.DataTarget dataTarget = Microsoft.Diagnostics.Runtime.DataTarget.AttachToProcess(pid, suspend: false);
        Microsoft.Diagnostics.Runtime.ClrInfo clr = dataTarget.ClrVersions[0];
        using Microsoft.Diagnostics.Runtime.ClrRuntime runtime = dacPath != null ? clr.CreateRuntime(dacPath) : clr.CreateRuntime();

        Dictionary<string, long> stacks = [];
        List<string> frames = [];
        long taken = 0, failed = 0;
        Stopwatch clock = Stopwatch.StartNew();
        long period = (long)(Stopwatch.Frequency / hz), next = Stopwatch.GetTimestamp();
        timeBeginPeriod(1);

        while (clock.Elapsed.TotalSeconds < secs && !process.HasExited)
        {
            if (SuspendThread(target.Handle) == uint.MaxValue)
            {
                failed++;
                break;
            }

            try
            {
                runtime.FlushCachedData();
                frames.Clear();
                Microsoft.Diagnostics.Runtime.ClrThread thread = null;

                foreach (Microsoft.Diagnostics.Runtime.ClrThread t in runtime.Threads)
                {
                    if (t.OSThreadId == target.Tid)
                    {
                        thread = t;
                    }
                }

                foreach (Microsoft.Diagnostics.Runtime.ClrStackFrame frame in thread?.EnumerateStackTrace() ?? [])
                {
                    if (frame.Method == null)
                    {
                        continue;
                    }

                    frames.Add($"{frame.Method.Type?.Name}.{frame.Method.Name}".Replace(';', ':'));

                    if (frames.Count >= 48)
                    {
                        break;
                    }
                }
            }
            catch (Exception)
            {
                failed++;
            }
            finally
            {
                ResumeThread(target.Handle);
            }

            if (frames.Count != 0)
            {
                frames.Reverse();
                string key = string.Join(';', frames);
                stacks.TryGetValue(key, out long c);
                stacks[key] = c + 1;
                taken++;
            }

            next += period;
            long wait = next - Stopwatch.GetTimestamp();

            if (wait > Stopwatch.Frequency / 1000)
            {
                Thread.Sleep((int)(wait * 1000 / Stopwatch.Frequency));
            }
            else if (wait < -10 * period)
            {
                next = Stopwatch.GetTimestamp();
            }
        }

        timeEndPeriod(1);

        using (StreamWriter w = new(outPath))
        {
            foreach ((string key, long c) in stacks)
            {
                w.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{key} {c}"));
            }
        }

        Console.WriteLine($"{taken} stacks of {threadName} in {clock.Elapsed.TotalSeconds:F1} s, {failed} failed, {stacks.Count} distinct");

        return 0;
    }

    // --managed: resolve every sampled instruction pointer that lies in CoreCLR JIT code to its method (and IL offset)
    // through ClrMD, attached to the still-running process after sampling. Output "rip,il,method". Methods jitted again
    // (tiering) keep their old code until it is freed, so addresses sampled earlier normally still resolve.
    private static void WriteManagedMethods(int pid, IEnumerable<(uint, ulong)> keys, string path, string dacPath,
        Dictionary<ulong, long> threadCounts, string disasmPath)
    {
        HashSet<ulong> rips = [];

        foreach ((_, ulong rip) in keys)
        {
            rips.Add(rip);
        }

        using Microsoft.Diagnostics.Runtime.DataTarget target = Microsoft.Diagnostics.Runtime.DataTarget.AttachToProcess(pid, suspend: true);
        Microsoft.Diagnostics.Runtime.ClrInfo clr = target.ClrVersions[0];
        using Microsoft.Diagnostics.Runtime.ClrRuntime runtime = dacPath != null ? clr.CreateRuntime(dacPath) : clr.CreateRuntime();
        int resolved = 0;

        using StreamWriter w = new(path);
        w.WriteLine("rip,il,method");

        foreach (ulong rip in rips)
        {
            Microsoft.Diagnostics.Runtime.ClrMethod method = runtime.GetMethodByInstructionPointer(rip);

            if (method == null)
            {
                continue;
            }

            resolved++;
            int il = method.GetILOffset(rip);
            string name = $"{method.Type?.Name}.{method.Name}".Replace(',', ';');
            w.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{rip:x},{il},{name}"));
        }

        Console.WriteLine($"runtime {clr.Version}: {resolved} of {rips.Count} addresses in managed code");

        if (threadCounts.Count != 0)
        {
            WriteDisassembly(pid, runtime, threadCounts, disasmPath);
        }
    }

    // --disasm <thread name>: the 30 methods with the most samples of that thread, disassembled (Iced) from the code in
    // the process, each instruction with its sample count. Samples land on the instruction AFTER a slow one in many
    // cases (skid of the interrupt point), so read a hot line together with the one above it.
    private static void WriteDisassembly(int pid, Microsoft.Diagnostics.Runtime.ClrRuntime runtime,
        Dictionary<ulong, long> threadCounts, string path)
    {
        Dictionary<ulong, (Microsoft.Diagnostics.Runtime.ClrMethod Method, long Samples)> methods = [];
        long total = 0;

        foreach ((ulong rip, long c) in threadCounts)
        {
            total += c;
            Microsoft.Diagnostics.Runtime.ClrMethod method = runtime.GetMethodByInstructionPointer(rip);

            if (method == null)
            {
                continue;
            }

            ulong key = method.HotColdInfo.HotStart != 0 && rip >= method.HotColdInfo.HotStart && rip < method.HotColdInfo.HotStart + method.HotColdInfo.HotSize
                ? method.HotColdInfo.HotStart
                : method.NativeCode;
            methods.TryGetValue(key, out var entry);
            methods[key] = (method, entry.Samples + c);
        }

        List<(ulong Start, Microsoft.Diagnostics.Runtime.ClrMethod Method, long Samples)> top = [];

        foreach ((ulong start, var entry) in methods)
        {
            top.Add((start, entry.Method, entry.Samples));
        }

        top.Sort((a, b) => b.Samples.CompareTo(a.Samples));

        using StreamWriter w = new(path);
        Iced.Intel.NasmFormatter formatter = new();
        Iced.Intel.StringOutput output = new();

        foreach ((ulong start, Microsoft.Diagnostics.Runtime.ClrMethod method, long samples) in top.GetRange(0, Math.Min(30, top.Count)))
        {
            uint size = start == method.HotColdInfo.HotStart ? method.HotColdInfo.HotSize : method.HotColdInfo.ColdSize;

            if (size == 0 || size > 1 << 20)
            {
                continue;
            }

            byte[] code = new byte[size];
            int read = ReadProcessMemory(pid, start, code, out int error);
            w.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"==== {method.Type?.Name}.{method.Name}  {samples} samples ({100.0 * samples / total:F2} %)  {start:x} +{size:x}  read {read} (error {error})"));

            Iced.Intel.Decoder decoder = Iced.Intel.Decoder.Create(64, new Iced.Intel.ByteArrayCodeReader(code, 0, read), start);

            while (decoder.IP < start + (ulong)read)
            {
                decoder.Decode(out Iced.Intel.Instruction instruction);
                threadCounts.TryGetValue(instruction.IP, out long c);
                formatter.Format(instruction, output);
                w.WriteLine(string.Create(CultureInfo.InvariantCulture,
                    $"{(c == 0 ? "" : c.ToString(CultureInfo.InvariantCulture)),7} {instruction.IP - start,6:x}  {output.ToStringAndReset()}"));
            }
        }
    }

    /// <summary>
    /// --jit &lt;perfmap&gt;: the hottest JIT-compiled guest functions of the guest threads (BOTWPC_GUEST_PERFMAP lines
    /// "host size guest_&lt;address&gt; [guest size]"), disassembled from the live process with samples per host instruction,
    /// and per function the host bytes, host instructions and guest instructions (guest size / 4), for the CPU JIT work.
    /// </summary>
    private static void WriteJitDisassembly(int pid, string perfMapPath, Dictionary<ulong, long> counts, string path, int top)
    {
        SortedDictionary<ulong, (uint Size, string Name, uint GuestSize)> map = [];

        // The emulator keeps the perf map open for writing.
        using StreamReader reader = new(new FileStream(perfMapPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite));

        for (string line; (line = reader.ReadLine()) != null;)
        {
            string[] parts = line.Split(' ');

            if (parts.Length < 3)
            {
                continue;
            }

            ulong host = ulong.Parse(parts[0], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            uint size = uint.Parse(parts[1], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            uint guestSize = parts.Length > 3 ? uint.Parse(parts[3], NumberStyles.HexNumber, CultureInfo.InvariantCulture) : 0;
            map[host] = (size, parts[2], guestSize); // later lines win (re-translation can reuse freed space)
        }

        ulong[] starts = [.. map.Keys];
        Dictionary<ulong, long> perFunction = [];
        long total = 0, inJit = 0;

        foreach ((ulong rip, long c) in counts)
        {
            total += c;
            int i = Array.BinarySearch(starts, rip);
            i = i >= 0 ? i : ~i - 1;

            if (i >= 0 && rip < starts[i] + map[starts[i]].Size)
            {
                inJit += c;
                perFunction.TryGetValue(starts[i], out long n);
                perFunction[starts[i]] = n + c;
            }
        }

        List<(ulong Start, long Samples)> ranked = [];

        foreach ((ulong start, long c) in perFunction)
        {
            ranked.Add((start, c));
        }

        ranked.Sort((a, b) => b.Samples.CompareTo(a.Samples));

        using StreamWriter w = new(path);
        Iced.Intel.NasmFormatter formatter = new();
        Iced.Intel.StringOutput output = new();
        w.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"guest-thread samples {total}, in JIT code {inJit} ({100.0 * inJit / Math.Max(1, total):F1} %), {perFunction.Count} functions sampled"));
        w.WriteLine("rank samples share host_bytes host_insts guest_insts host/guest name");
        StringWriter body = new();
        int rank = 0;

        foreach ((ulong start, long samples) in ranked.GetRange(0, Math.Min(top, ranked.Count)))
        {
            (uint size, string name, uint guestSize) = map[start];
            byte[] code = new byte[size];
            int read = ReadProcessMemory(pid, start, code, out _);
            Iced.Intel.Decoder decoder = Iced.Intel.Decoder.Create(64, new Iced.Intel.ByteArrayCodeReader(code, 0, read), start);
            int instructions = 0;
            body.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"==== #{++rank} {name}  {samples} samples ({100.0 * samples / Math.Max(1, inJit):F2} % of JIT)  {start:x} +{size:x}  guest {guestSize / 4} insts"));

            while (decoder.IP < start + (ulong)read)
            {
                decoder.Decode(out Iced.Intel.Instruction instruction);
                instructions++;
                counts.TryGetValue(instruction.IP, out long c);
                formatter.Format(instruction, output);
                body.WriteLine(string.Create(CultureInfo.InvariantCulture,
                    $"{(c == 0 ? "" : c.ToString(CultureInfo.InvariantCulture)),7} {instruction.IP - start,6:x}  {output.ToStringAndReset()}"));
            }

            w.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"{rank} {samples} {100.0 * samples / Math.Max(1, inJit):F2} {size} {instructions} {guestSize / 4} {(guestSize == 0 ? 0 : instructions / (guestSize / 4.0)):F2} {name}"));
        }

        w.WriteLine();
        w.Write(body.ToString());
    }

    private static int ReadProcessMemory(int pid, ulong address, byte[] buffer, out int error)
    {
        nint process = OpenProcess(0x0010 /* PROCESS_VM_READ */, false, pid);

        fixed (byte* p = buffer)
        {
            // A range crossing into an unreadable page fails with ERROR_PARTIAL_COPY: read what is readable, in pieces.
            bool ok = ReadProcessMemory(process, (nint)address, p, buffer.Length, out nint read);
            error = ok ? 0 : Marshal.GetLastPInvokeError();
            int total = ok ? buffer.Length : 0;

            while (!ok && total < buffer.Length)
            {
                int chunk = Math.Min(64, buffer.Length - total);

                if (!ReadProcessMemory(process, (nint)(address + (ulong)total), p + total, chunk, out read) || read == 0)
                {
                    break;
                }

                total += (int)read;
            }

            CloseHandle(process);

            return total;
        }
    }

    private static double Option(string[] args, string name, double fallback)
    {
        int i = Array.IndexOf(args, name);

        return i >= 0 && i + 1 < args.Length ? double.Parse(args[i + 1], CultureInfo.InvariantCulture) : fallback;
    }
}
