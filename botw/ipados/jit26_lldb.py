# lldb side of the iOS 26 JIT protocol (StikDebug/StikJIT): the app's jit26_prepare_region(addr, size) asks the debugger
# to write one byte per 16 KB page of an RX region, which is what makes the page executable for a debugged process.
# Like StikDebug, this catches the function's `brk #0xf00d` (a stop hook); faults the app handles itself bypass lldb
# (run.sh sets platform.plugin.darwin.ignored-exceptions). Load after attaching:
#   command script import jit26_lldb.py ; then jit26_service whenever the process may have stopped (run.sh)
import lldb

PAGE = 0x4000


def prepare(frame, bp_loc, extra_args, internal_dict):
    # The regions are fresh anonymous memory (zeros), so the debugger writes zeros in big blocks: one write per
    # 16 KB page (read + write each) took over 15 minutes for 256 MB over USB.
    process = frame.GetThread().GetProcess()
    addr = frame.FindRegister("x0").GetValueAsUnsigned()
    size = frame.FindRegister("x1").GetValueAsUnsigned()
    err = lldb.SBError()
    start, end, block = addr & ~(PAGE - 1), addr + size, 1 << 20
    zeros = bytes(block)
    for p in range(start, end, block):
        n = min(block, end - p)
        if process.WriteMemory(p, zeros[:n], err) != n or err.Fail():
            print("jit26: prepare %#x failed at %#x: %s" % (addr, p, err.GetCString()))
            return True
    print("jit26: prepared %#x + %#x" % (addr, size))
    frame.SetPC(frame.FindRegister("lr").GetValueAsUnsigned())
    return False


def service(debugger, command, result, internal_dict):
    """jit26_service: if the process stopped on the protocol's `brk #0xf00d`, serve it (x16 = 1: prepare x0/x1), step
    past it and continue. run.sh sends this every 2 s (lldb fed through a pipe does not run stop hooks)."""
    process = debugger.GetSelectedTarget().GetProcess()
    if process.GetState() != lldb.eStateStopped:
        return
    err = lldb.SBError()
    for thread in process:
        frame = thread.GetFrameAtIndex(0)
        pc = frame.GetPC()
        if process.ReadUnsignedFromMemory(pc, 4, err) != 0xD43E01A0 or err.Fail():
            continue
        cmd = frame.FindRegister("x16").GetValueAsUnsigned()
        if cmd == 1:
            prepare(frame, None, None, None)  # (pc := lr: the brk sits in a leaf that just returns)
        elif cmd == 0:  # JIT26Detach: all code memory is prepared, leave (faults no longer round-trip to lldb)
            frame.SetPC(pc + 4)
            print("jit26: detaching")
            process.Detach()
            return
        else:
            frame.SetPC(pc + 4)
        process.Continue()
        return


def __lldb_init_module(debugger, internal_dict):
    debugger.HandleCommand("command script add -f jit26_lldb.service jit26_service")
