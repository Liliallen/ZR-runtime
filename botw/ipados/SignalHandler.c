// Memory-tracking fault handler for processes that cannot run generated code (iPadOS without JIT). A C version of the
// handler ARMeilleure generates at run time (src/ARMeilleure/Signal/NativeSignalHandlerGenerator.cs, Unix/arm64 path),
// reading the same configuration (Ryujinx.Cpu NativeSignalHandler: SignalHandlerConfig, packed).
#include "SignalHandler.h"
#include <signal.h>
#include <stdint.h>
#include <sys/ucontext.h>

#define MAX_TRACKED_RANGES 16

#pragma pack(push, 1)
typedef struct {
    int32_t isActive;
    uintptr_t start, end;
    uint64_t (*action)(uint64_t offset, uint64_t size, uint64_t isWrite);
} Range;

typedef struct {
    int32_t structAddressOffset, structWriteOffset; // Windows only
    uintptr_t oldSigaction;
    int32_t oldSigaction3Arg;
    Range ranges[MAX_TRACKED_RANGES];
} Config;
#pragma pack(pop)

static Config* volatile g_config;

void botw_signal_set_config(void* config) { g_config = config; }

void botw_signal_handler(int sig, siginfo_t* info, void* ucontext) {
    Config* config = g_config;
    uint64_t fault = (uint64_t)info->si_addr;
    mcontext_t mc = ((ucontext_t*)ucontext)->uc_mcontext;
    uint64_t isWrite = (mc->__es.__esr & 0x40) != 0;

    for (int i = 0; i < MAX_TRACKED_RANGES; i++) {
        Range* r = &config->ranges[i];
        if (!r->isActive || fault < r->start || fault >= r->end) continue;
        uint64_t result = r->action ? r->action(fault - r->start, 1, isWrite) : 0;
        if (!result) break;
        if (result != fault) {
            // The access was redirected (e.g. to a mirror): rebase the faulting instruction's address register.
            uint32_t inst = *(uint32_t*)mc->__ss.__pc;
            uint32_t reg = (inst & 0xFFF80000) == 0xD5080000 ? (inst & 0x1F) : ((inst >> 5) & 0x1F);
            uint64_t* regs = (uint64_t*)&mc->__ss.__x[0]; // x0..x28, fp, lr, sp
            regs[reg] = result + (regs[reg] - fault);
        }
        return;
    }

    if (config->oldSigaction3Arg) ((void (*)(int, siginfo_t*, void*))config->oldSigaction)(sig, info, ucontext);
    else ((void (*)(int))config->oldSigaction)(sig);
}
