#!/usr/bin/env python3
"""Static guest entry points for the ahead-of-time image, from images dumped with BOTWPC_DUMP_IMAGES=<dir>.

usage: aot_entries.py <dump dir> <out list>      writes one hex guest address per line (BOTWPC_AOT_PRECOMPILE)
       aot_entries.py --selftest
       aot_entries.py <dump dir> --classify <recording>...   which category recorded functions fall into

Entry candidates per module: function starts from .eh_frame_hdr, targets of BL in .text, R_AARCH64_RELATIVE relocation targets inside .text
(function pointers: vtables, callbacks), exported function symbols, switch case targets (jump tables) and function
addresses computed in code (adrp/add, adr)."""
import bisect
import glob
import os
import struct
import sys


def load_modules(dump):
    mods = []
    for txt in sorted(glob.glob(os.path.join(dump, "*.txt"))):
        info = dict(line.split("=", 1) for line in open(txt).read().split())
        seg = lambda k: tuple(int(x, 16) for x in info[k].split("+"))
        mods.append({"name": os.path.basename(txt)[:-4], "base": int(info["base"], 16), "text": seg("text"),
                     "ro": seg("ro"), "data": seg("data"), "bin": open(txt[:-4] + ".bin", "rb").read()})
    return mods


def u32(b, o): return struct.unpack_from("<I", b, o)[0]
def u64(b, o): return struct.unpack_from("<Q", b, o)[0]
def sext(v, bits): return v - (1 << bits) if v & (1 << (bits - 1)) else v


def call_targets(m):
    """BL targets (module offsets) inside .text. (B targets are mostly inside the same function, which the translator
    follows; those that leave its region are found by the precompile closure.)"""
    b, (toff, tsize) = m["bin"], m["text"]
    out = set()
    words = struct.unpack_from(f"<{tsize // 4}I", b, toff)
    for i, w in enumerate(words):
        if w & 0xFC000000 == 0x94000000:  # BL
            t = toff + i * 4 + sext(w & 0x3FFFFFF, 26) * 4
            if toff <= t < toff + tsize:
                out.add(t)
    return out


def dynamic(m):
    b = m["bin"]
    mod0 = u32(b, 4)
    if b[mod0:mod0 + 4] != b"MOD0":
        return {}
    dyn = mod0 + struct.unpack_from("<i", b, mod0 + 4)[0]
    tags = {}
    while dyn + 16 <= len(b):
        tag, val = u64(b, dyn), u64(b, dyn + 8)
        if tag == 0:
            break
        tags.setdefault(tag, val)
        dyn += 16
    return tags


def reloc_and_symbol_targets(m):
    b, (toff, tsize) = m["bin"], m["text"]
    tags = dynamic(m)
    out = set()
    if 7 in tags:  # DT_RELA / DT_RELASZ: R_AARCH64_RELATIVE (1027) addends are module offsets
        for o in range(tags[7], tags[7] + tags.get(8, 0), 24):
            if o + 24 <= len(b) and u64(b, o + 8) & 0xFFFFFFFF == 1027:
                t = u64(b, o + 16)
                if toff <= t < toff + tsize:
                    out.add(t)
    if 6 in tags and 4 in tags:  # DT_SYMTAB with DT_HASH's nchain as the count
        nchain = u32(b, tags[4] + 4)
        for i in range(nchain):
            o = tags[6] + i * 24
            if o + 24 > len(b):
                break
            info, shndx, value = b[o + 4], struct.unpack_from("<H", b, o + 6)[0], u64(b, o + 8)
            if info & 0xF == 2 and shndx and toff <= value < toff + tsize:  # STT_FUNC, defined
                out.add(value)
    return out


WINDOW = 24


def base_regs(words, toff, i):
    """adrp / add #imm values over the 4096 instructions before the window: a table base kept in a callee-saved
    register is set once near the function start. Later writes by other instructions are not tracked; a wrong base
    yields entries outside the function, which the caller drops."""
    regs = {}
    for j in range(max(0, i - 4096), max(0, i - WINDOW)):
        x, pc = words[j], toff + j * 4
        d, n = x & 31, (x >> 5) & 31
        if x & 0x9F000000 == 0x90000000:
            regs[d] = (pc & ~0xFFF) + (sext(((x >> 5) & 0x7FFFF) << 2 | (x >> 29) & 3, 21) << 12)
        elif x & 0xFF800000 == 0x91000000 and n in regs:
            regs[d] = regs[n] + (((x >> 10) & 0xFFF) << (12 if x & 0x400000 else 0))
    return regs


def scan(words, toff, i, regs, loaded, target):
    cases = None
    for j in range(max(0, i - WINDOW), i):
        x, pc = words[j], toff + j * 4
        d, n, mm = x & 31, (x >> 5) & 31, (x >> 16) & 31
        if x & 0x9F000000 == 0x90000000:  # adrp xd
            regs[d] = (pc & ~0xFFF) + (sext(((x >> 5) & 0x7FFFF) << 2 | (x >> 29) & 3, 21) << 12)
        elif x & 0xFF800000 == 0x91000000:  # add xd, xn, #imm{, lsl 12}
            imm = ((x >> 10) & 0xFFF) << (12 if x & 0x400000 else 0)
            if n in regs:
                regs[d] = regs[n] + imm
            else:
                regs.pop(d, None)
        elif x & 0xFFE0FFE0 == 0xAA0003E0:  # mov xd, xm
            if mm in regs:
                regs[d] = regs[mm]
            else:
                regs.pop(d, None)
        elif x & 0xFFE0FC00 == 0xB8A07800:  # ldrsw xd, [xn, xm, lsl #2]
            loaded[d] = regs.get(n)
            regs.pop(d, None)
        elif x & 0xFFE0FC00 == 0x8B000000:  # add xd, xn, xm
            if loaded.get(n) is not None and regs.get(mm) == loaded[n]:
                target[d] = loaded[n]
            elif loaded.get(mm) is not None and regs.get(n) == loaded[mm]:
                target[d] = loaded[mm]
            regs.pop(d, None)
        elif x & 0x7FC0001F == 0x7100001F:  # cmp wn, #imm (the switch guard: the nearest one)
            cases = ((x >> 10) & 0xFFF) + 1
    return cases


def jump_table_targets(m):
    """Switch statements. BotW's compiler lowers them to
        cmp wX, #N ; b.hi default ; adrp xT, tbl ; add xT, xT, :lo12:tbl ; [mov xU, xT] ;
        ldrsw xE, [xT, xI, lsl #2] ; add xD, xE, xT|xU ; br xD
    with other instructions scheduled in between. A small constant propagation over the 24 instructions before each
    BR (adrp / add #imm / mov) finds the table; it holds N + 1 signed 32-bit offsets from its own address, and each
    case target is an entry the translator reaches only through the BR."""
    b, (toff, tsize) = m["bin"], m["text"]
    words = struct.unpack_from(f"<{tsize // 4}I", b, toff)
    tables = []
    for i, w in enumerate(words):
        if w & 0xFFFFFC1F != 0xD61F0000:  # BR Xd
            continue
        for far in (False, True):
            target = {}
            cases = scan(words, toff, i, base_regs(words, toff, i) if far else {}, {}, target)
            tbl = target.get((w >> 5) & 31)
            if tbl is not None:
                break
        if tbl is not None and (cases or 0) <= 4096:
            tables.append((tbl, i, 0 if far or not cases else cases))
    # The guard bounds the entries only on its own path (another path may jump in with a wider index, e.g. x & 7
    # past a "cmp #4"), so entries past it, or all of them without a guard or with a far base, are taken while they
    # land near the BR and before the next table starts.
    starts = sorted({t for t, _, _ in tables})
    out = set()
    for tbl, i, trusted in tables:
        nxt = starts[bisect.bisect_right(starts, tbl)] if bisect.bisect_right(starts, tbl) < len(starts) else tbl + 4096 * 4
        for c in range(min(4096, (nxt - tbl) // 4)):
            o = tbl + c * 4
            if not 0 <= o < len(b) - 4:
                break
            t = tbl + struct.unpack_from("<i", b, o)[0]
            if c >= trusted and not (abs(t - toff - i * 4) < 0x10000 and t % 4 == 0):
                break
            if toff <= t < toff + tsize:
                out.add(t)
    return out


def code_pointer_targets(m):
    """Function addresses materialized in code (callbacks, lambdas): adrp xd + add xd, xd, #lo12 (within 8 instructions)
    or adr xd, pointing into .text; filtered to function boundaries by the caller."""
    b, (toff, tsize) = m["bin"], m["text"]
    words = struct.unpack_from(f"<{tsize // 4}I", b, toff)
    out = set()
    for i, x in enumerate(words):
        pc = toff + i * 4
        if x & 0x9F000000 == 0x10000000:  # adr
            out.add(pc + sext(((x >> 5) & 0x7FFFF) << 2 | (x >> 29) & 3, 21))
        elif x & 0x9F000000 == 0x90000000:  # adrp
            d = x & 31
            page = (pc & ~0xFFF) + (sext(((x >> 5) & 0x7FFFF) << 2 | (x >> 29) & 3, 21) << 12)
            for j in range(i + 1, min(len(words), i + 9)):
                y = words[j]
                if y & 0xFFC00000 == 0x91000000 and (y >> 5) & 31 == d:  # add xT, xd, #lo12
                    out.add(page + ((y >> 10) & 0xFFF))
                    break
                if y & 31 == d and y & 0xFFC00000 != 0xF9400000:  # d redefined (other than a load through it)
                    break
    return {t for t in out if toff <= t < toff + tsize}


def at_boundary(m, off):
    """A pointer into .text that can be a function start: aligned, after a terminator (ret, br, b) or padding. Seeds
    in the middle of functions make the precompile closure duplicate whole regions."""
    if off & 3:
        return False
    if off < 4:
        return True
    p = u32(m["bin"], off - 4)
    return p & 0xFFFF0000 == 0 or p & 0xFFFFFC1F in (0xD65F0000, 0xD61F0000) or p & 0xFC000000 == 0x14000000


def fde_targets(m):
    """Function starts from .eh_frame_hdr (found through the MOD0 header): the compiler emits an FDE for essentially
    every function, so this is the most complete list there is (the approach of seedds/nxnative, MIT)."""
    b = m["bin"]
    mod0 = u32(b, 4)
    if b[mod0:mod0 + 4] != b"MOD0":
        return set()
    eh_start = struct.unpack_from("<i", b, mod0 + 16)[0]
    hdr = mod0 + eh_start
    version, _, count_enc, table_enc = b[hdr:hdr + 4]
    if version != 1 or count_enc != 0x03 or table_enc != 0x3B:  # udata4 count, datarel sdata4 table
        return set()
    count = u32(b, hdr + 8)
    toff, tsize = m["text"]
    out = set()
    for i in range(count):
        target = hdr + struct.unpack_from("<i", b, hdr + 12 + 8 * i)[0]
        if toff <= target < toff + tsize:
            out.add(target)
    return out


def entries(mods):
    cats = {}
    for m in mods:
        for name, fn in (("call", call_targets), ("reloc/symbol", reloc_and_symbol_targets), ("jumptable", jump_table_targets),
                         ("codeptr", code_pointer_targets), ("fde", fde_targets)):
            for off in fn(m):
                if name not in ("reloc/symbol", "codeptr") or at_boundary(m, off):
                    cats.setdefault(m["base"] + off, set()).add(name)
    return cats


def recorded(paths):
    out = []
    for p in paths:
        d = open(p, "rb").read()
        pos = 0
        while pos + 4 <= len(d):
            if u32(d, pos) == 0x31464142:
                a, gl, hl, hh, cl = struct.unpack_from("<QIQQI", d, pos + 4)
                out.append(a)
                pos += 36 + cl
            else:
                pos += 12 + u32(d, pos + 8)
    return out


def selftest():
    """A switch whose table base sits in x24, set 100 instructions before the BR, with and without the bound check."""
    for guard in (True, False):
        nop = 0xD503201F
        code = [0x90000018, 0x91000000 | 0 << 10 | 24 << 5 | 24] + [nop] * 100  # adrp x24, 0 ; add x24, x24, #tbl
        code += [0x7100001F | 2 << 10 | 8 << 5 if guard else nop, nop,
                 0xB8A07800 | 8 << 16 | 24 << 5 | 8, 0x8B000000 | 24 << 16 | 8 << 5 | 8, 0xD61F0000 | 8 << 5]
        tbl = len(code) * 4
        code[1] |= tbl << 10
        targets = [8, 12, 16]
        words = code + [(t - tbl) & 0xFFFFFFFF for t in targets] + [0x7FFFFFF0]  # then data that is no case
        m = {"bin": struct.pack(f"<{len(words)}I", *words), "text": (0, tbl)}
        assert jump_table_targets(m) == set(targets), (guard, jump_table_targets(m))
    # A guard for 2 cases on one path; the table has 3 (another path jumps in with a wider index).
    code = [0x90000018, 0x91000000 | 24 << 5 | 24, 0x7100001F | 1 << 10 | 8 << 5,
            0xB8A07800 | 8 << 16 | 24 << 5 | 8, 0x8B000000 | 24 << 16 | 8 << 5 | 8, 0xD61F0000 | 8 << 5]
    tbl = len(code) * 4
    code[1] |= tbl << 10
    words = code + [(t - tbl) & 0xFFFFFFFF for t in (8, 12, 16)] + [0x7FFFFFF0]
    m = {"bin": struct.pack(f"<{len(words)}I", *words), "text": (0, tbl)}
    assert jump_table_targets(m) == {8, 12, 16}, jump_table_targets(m)
    # .eh_frame_hdr through MOD0: two FDEs at text offsets 0x40 and 0x80, one outside .text.
    img = bytearray(0x200)
    struct.pack_into("<I", img, 4, 0x100)  # MOD0 offset
    img[0x100:0x104] = b"MOD0"
    struct.pack_into("<6i", img, 0x104, 0, 0, 0, 0x40, 0x60, 0)  # eh_frame_hdr at 0x140
    struct.pack_into("<4BiI", img, 0x140, 1, 0x1B, 0x03, 0x3B, 0, 3)
    struct.pack_into("<6i", img, 0x14C, 0x40 - 0x140, 0, 0x80 - 0x140, 0, 0x180 - 0x140, 0)
    assert fde_targets({"bin": bytes(img), "text": (0, 0x100)}) == {0x40, 0x80}
    print("selftest ok")


if __name__ == "__main__":
    if sys.argv[1:] == ["--selftest"]:
        sys.exit(selftest())
    if len(sys.argv) < 3:
        sys.exit(__doc__)
    mods = load_modules(sys.argv[1])
    cats = entries(mods)
    if sys.argv[2] == "--classify":
        from collections import Counter
        rec = recorded(sys.argv[3:])
        c = Counter("+".join(sorted(cats[a])) if a in cats else "none" for a in rec)
        print(len(rec), "recorded:", dict(c))
    else:
        with open(sys.argv[2], "w") as f:
            for a in sorted(cats):
                f.write(f"{a:x}\n")
        from collections import Counter
        print(len(cats), "entries:", dict(Counter(n for s in cats.values() for n in s)))
