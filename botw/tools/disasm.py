#!/usr/bin/env python3
"""Disassemble a window of a dumped NSO image (from BOTWPC_DUMP_IMAGES).

usage: disasm.py <image.bin> <offset_hex> [count] [--base 0x...]
Offsets are image-relative (text starts at 0). Requires capstone (use .tools/venv).
"""
import sys
from capstone import Cs, CS_ARCH_ARM64, CS_MODE_ARM

img = open(sys.argv[1], "rb").read()
off = int(sys.argv[2], 16)
count = int(sys.argv[3]) if len(sys.argv) > 3 and not sys.argv[3].startswith("--") else 40
base = int(sys.argv[sys.argv.index("--base") + 1], 16) if "--base" in sys.argv else 0
md = Cs(CS_ARCH_ARM64, CS_MODE_ARM)
for ins in md.disasm(img[off:off + count * 4], base + off):
    print(f"{ins.address:#010x}  {ins.bytes.hex()}  {ins.mnemonic:8} {ins.op_str}")
