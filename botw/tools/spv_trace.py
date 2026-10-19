#!/usr/bin/env python3
"""Print the expression tree that feeds a SPIR-V store target (default: gl_Position), from spirv-dis text.

usage: spv_trace.py <file.spvasm> [--target gl_PerVertex] [--depth N]
Uniform loads are shown as name[index...]; used to find which constant-buffer words form the transform.
"""
import re, sys

def parse(path):
    defs, names, stores = {}, {}, []
    for line in open(path):
        line = line.strip()
        m = re.match(r'OpName (%\w+) "(.*)"', line)
        if m:
            names[m.group(1)] = m.group(2)
            continue
        m = re.match(r'(%\w+) = (Op\w+) (.*)', line)
        if m:
            defs[m.group(1)] = (m.group(2), m.group(3).split())
            continue
        m = re.match(r'OpStore (%\w+) (%\w+)', line)
        if m:
            stores.append((m.group(1), m.group(2)))
    return defs, names, stores

def expr(v, defs, names, depth, stores_by_ptr):
    if v in names:
        return names[v]
    if v not in defs:
        return v
    op, args = defs[v]
    if op.startswith('OpConstant'):
        return args[-1] if op == 'OpConstant' else op
    if depth == 0:
        return f'{v}:{op}'
    sub = lambda a: expr(a, defs, names, depth - 1, stores_by_ptr)
    ops = [a for a in args[1:] if a.startswith('%')]
    if op == 'OpLoad':
        ptr = args[1]
        if ptr in stores_by_ptr:  # local variable: follow its last store
            return sub(stores_by_ptr[ptr])
        return sub(ptr)
    if op == 'OpAccessChain':
        return f'{sub(args[1])}[{",".join(sub(a) for a in args[2:])}]'
    if op == 'OpExtInst':
        return f'{args[2]}({", ".join(sub(a) for a in args[3:])})'
    short = op[2:]
    return f'{short}({", ".join(sub(a) for a in ops)})'

def main(argv):
    path = argv[1]
    target = argv[argv.index('--target') + 1] if '--target' in argv else 'gl_PerVertex'
    depth = int(argv[argv.index('--depth') + 1]) if '--depth' in argv else 12
    defs, names, stores = parse(path)
    stores_by_ptr = {p: v for p, v in stores}
    pointee = {k: a[-1] for k, (op, a) in defs.items() if op == 'OpTypePointer'}
    var_types = {k: pointee.get(a[0], '') for k, (op, a) in defs.items() if op == 'OpVariable'}
    for ptr, val in stores:
        chain = defs.get(ptr)
        base = chain[1][1] if chain and chain[0] == 'OpAccessChain' else None
        # Output blocks are often unnamed variables: match the variable id, its name, or its pointee type name.
        if base and (base == target or names.get(base) == target or names.get(var_types.get(base, '')) == target):
            print(f'{target}{[expr(a, defs, names, 1, {}) for a in chain[1][2:]]} = {expr(val, defs, names, depth, stores_by_ptr)}\n')

if __name__ == '__main__':
    main(sys.argv)
