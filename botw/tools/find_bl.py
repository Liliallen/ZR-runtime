#!/usr/bin/env python3
"""List BL call sites to a target in a dumped NSO image's text segment.
usage: find_bl.py <image.bin> <text_size_hex> <target_hex>"""
import struct, sys
img = open(sys.argv[1], "rb").read()
size, target = int(sys.argv[2], 16), int(sys.argv[3], 16)
words = struct.unpack_from(f"<{size // 4}I", img, 0)
for i, w in enumerate(words):
    if w >> 26 == 0b100101:  # BL imm26
        imm = w & 0x3FFFFFF
        if imm & 0x2000000:
            imm -= 0x4000000
        if i * 4 + imm * 4 == target:
            print(hex(i * 4))
