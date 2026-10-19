// iPadOS JIT support for the runtime's code arena (Ryujinx.Cpu IosJitArena: RX mapping + RW alias).
#include "Jit.h"
#include <TargetConditionals.h>
#include <sys/sysctl.h>
#include <unistd.h>
#include <stdio.h>
#include <stdint.h>
#include <string.h>
#include <stdlib.h>
#include <setjmp.h>
#include <signal.h>
#include <sys/mman.h>
#include <mach/mach.h>
#include <libkern/OSCacheControl.h>

int csops(pid_t pid, unsigned int ops, void* useraddr, size_t usersize);

// StikDebug / lldb "JIT26" protocol: command in x16, arguments in x0/x1, then `brk #0xf00d`; the debugger serves it and
// resumes after the brk. 1 = prepare [x0, x0 + x1) (the debugger writes the pages, which makes them executable on TXM
// devices), 0 = detach. botw/ipados/jit26_lldb.py is the Mac-side implementation.
__attribute__((naked, noinline)) static void jit26_prepare_region(void* address, size_t size) {
    __asm__ volatile("mov x16, #1\n brk #0xf00d\n ret");
}

__attribute__((naked, noinline)) static void jit26_detach(void) {
    __asm__ volatile("mov x16, #0\n brk #0xf00d\n ret");
}

bool jit_cs_debugged(void) {
#if TARGET_OS_SIMULATOR
    return true; // simulator apps are macOS processes: executable mappings need no debugger
#endif
    uint32_t flags = 0;
    return csops(getpid(), 0 /* CS_OPS_STATUS */, &flags, sizeof flags) == 0 && (flags & 0x10000000 /* CS_DEBUGGED */);
}

bool jit_traced(void) {
    struct kinfo_proc info = {0};
    size_t size = sizeof info;
    int mib[4] = {CTL_KERN, KERN_PROC, KERN_PROC_PID, getpid()};
    return sysctl(mib, 4, &info, &size, NULL, 0) == 0 && (info.kp_proc.p_flag & P_TRACED);
}

static sigjmp_buf g_probe_jmp;
static void probe_fault(int sig) { siglongjmp(g_probe_jmp, 1); }

// Non-TXM route: with CS_DEBUGGED, a fresh RX mapping written through a RW alias runs as is. Tried on a scratch page
// (a refusal is a SIGBUS/SIGSEGV here, caught); prepared pages would all be dirty, i.e. count against the app's memory
// limit, so this route is preferred when it works.
static bool alias_route_works(void) {
    const size_t page = 16384;
    void* rx = mmap(NULL, page, PROT_READ | PROT_EXEC, MAP_PRIVATE | MAP_ANON, -1, 0);
    if (rx == MAP_FAILED) return false;
    vm_address_t rw = 0;
    vm_prot_t cur, max;
    bool ok = vm_remap(mach_task_self(), &rw, page, 0, VM_FLAGS_ANYWHERE, mach_task_self(), (vm_address_t)rx, FALSE, &cur, &max,
                       VM_INHERIT_NONE) == KERN_SUCCESS && mprotect((void*)rw, page, PROT_READ | PROT_WRITE) == 0;
    if (ok) {
        static const uint32_t code[] = {0xd2800540 /* mov x0, #42 */, 0xd65f03c0 /* ret */};
        memcpy((void*)rw, code, sizeof code);
        sys_icache_invalidate(rx, sizeof code);
        struct sigaction sa = {0}, old_segv, old_bus;
        sa.sa_handler = probe_fault;
        sigaction(SIGSEGV, &sa, &old_segv);
        sigaction(SIGBUS, &sa, &old_bus);
        long r = -1;
        if (!sigsetjmp(g_probe_jmp, 1)) r = ((long (*)(void))rx)();
        sigaction(SIGSEGV, &old_segv, NULL);
        sigaction(SIGBUS, &old_bus, NULL);
        ok = r == 42;
        munmap((void*)rw, page);
    }
    munmap(rx, page);
    return ok;
}

int jit_prepare(void* address, size_t size) {
    if (!jit_cs_debugged()) {
        return -1;
    }
    bool traced = jit_traced();
#if !TARGET_OS_SIMULATOR
    // BOTWPC_JIT_ROUTE=prepare skips the probe (should a device kill the process on the unprepared page instead of
    // raising a catchable fault); =alias trusts CS_DEBUGGED alone.
    const char* route = getenv("BOTWPC_JIT_ROUTE");
    bool alias = route && !strcmp(route, "alias") ? true : route && !strcmp(route, "prepare") ? false : alias_route_works();
    if (!alias) {
        // TXM-style: the attached debugger must write the pages (JIT26 protocol). The whole arena is prepared at once so
        // the debugger can leave right after: attached, every fault the runtime handles itself (guest memory tracking)
        // would first stop in it.
        if (!traced) {
            fprintf(stderr, "BOTW-iPad: JIT needs a debugger that prepares code memory (StikDebug script / run.sh)\n");
            return -1;
        }
        jit26_prepare_region(address, size);
        fprintf(stderr, "BOTW-iPad: JIT arena %zu MB at %p prepared by the debugger\n", size >> 20, address);
    } else {
        fprintf(stderr, "BOTW-iPad: JIT arena %zu MB at %p (CS_DEBUGGED, RW alias)\n", size >> 20, address);
    }
    if (traced) jit26_detach();
#endif
    return 0;
}
