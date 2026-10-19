# HLE / scheduler V2 (Runtime V2)

## Where the guest threads' non-JIT time goes (W2, CPU V2, `v2-guest-managed1`, 2 000 Hz, 40 s, managed symbols)

Guest threads (`HLE.GuestThread.*`) were running 5 % of all sampled time (the rest is waiting); of their running
time 88.1 % is JIT-compiled guest code. The remaining 11.9 %:

| Share of guest running time | What |
|---|---|
| 6.48 % | native code inside `ryujinx.exe` (the single-file host embeds CoreCLR: managed/unmanaged transitions of SVC and fallback calls, runtime helpers; not symbolized) |
| ~0.25 % each | `SyscallDispatch.SleepThread64`, `KScheduler.YieldWithLoadBalancing`, `KCriticalSection.Leave`, `ManualResetEventSlim.Wait`, SVC handler entry (`SupervisorCallHandler`, `NativeInterface.SupervisorCall`), `MemoryTracking.VirtualMemoryEvent`, scheduler (`EnableScheduling`, `SelectThreadsImpl`, `Schedule`) |
| < 0.1 % each | IPC/kernel transfer, logging checks, LINQ in the kernel, reprotection from faults |

So HLE is not where the guest's frame time goes; JIT code is (`CPU_V2_JIT.md`). Earlier rounds measured guest wake-up
latency at 4–7 µs per wake (0.15–0.3 ms per busy thread per frame).

## Component: Horizon yield schedule count

A game thread spins on `svcSleepThread(0)`; counters (`BOTWPC_PROFILE=1`, `v2-prof-yield1`) show **2 958 yields per
frame**. Ryujinx answered each one by entering the kernel critical section and re-queuing the thread even when nothing
else could run. Horizon's kernel (as re-implemented by mesosphere and used by yuzu/Eden) keeps a per-process
*scheduled count* and a per-thread *yield schedule count*: a yield that finds nothing to switch to records the process
count, and the next yield returns at once while the count is unchanged.

| | |
|---|---|
| Code | `KProcess.ScheduledCount`, `KThread.YieldScheduleCount`, `KScheduler.Yield` / `YieldWithLoadBalancing` / `YieldToAnyThread`, count incremented in `KScheduler.SelectThread` when a core's selected thread changes (Horizon increments it when a core's highest-priority thread changes) |
| svcSleepThread(0) | fast path as in Horizon |
| svcSleepThread(-1) | fast path only when no thread is suggested for migration at all (Horizon also skips threads it will re-check; this is stricter) |
| svcSleepThread(-2) | counts only, no fast path |
| Selection | `runtime.hle_scheduler = V2` (`BOTWPC_HLE_SCHEDULER=v2`); counting always runs under V2, `BOTWPC_HLE_YIELD_COUNT` switches only the early return (in-run A/B without stale counts) |
| Effect | 2 939 of 2 958 yields per frame (99.3 %) return without the critical section |
| In-run A/B (`v2-ab-yield1`) | +0.36 FPS (95 % CI −0.80 .. +1.51), frame time −0.01 ms, 1 % low +2.8 (n.s.) |
| Status | **kept, default off.** Faithful to Horizon, removes work, but no measurable frame-time effect in W2: the spinning thread is waiting either way |

The round-3 attempt at this idea (`OPTIMIZATIONS.md`, "yield fast path") was never validly measured (the benchmark
switch leak, KNOWN_ISSUES #29); this round's measurement is an in-run A/B with the switch inside one process.

## Semantics and safety

- The fast path only skips yields that would not change the scheduling state; thread priorities, core masks, wait
  queues, wake-ups and SVC results are unchanged.
- A yield's only guest-visible effect is timing. Horizon itself behaves this way, so the change brings the HLE kernel
  closer to the console.
- No deadlock or missing wake-up was observed in the A/B, the regression scenario or the final matrix runs
  (`PLAYTHROUGH_VALIDATION.md`); a thread that becomes runnable at the same priority on the spinning thread's core
  changes no core's selected thread, so — as on the console — the spinning thread keeps the core until something is
  scheduled in the process (any context switch on any core does that).

## Not changed

SVC dispatch, IPC (`Ryujinx.Horizon` services and HLE services), synchronization objects, the critical section and the
host-thread-per-guest-thread model. Their measured share is below 0.3 % each; the CoreCLR transition cost (6.5 %) would
need SVCs handled without leaving JIT code, which ARMeilleure does not support.
