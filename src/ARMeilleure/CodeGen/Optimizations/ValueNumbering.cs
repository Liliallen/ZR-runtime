using ARMeilleure.IntermediateRepresentation;
using ARMeilleure.Translation;
using System;
using System.Collections.Generic;

namespace ARMeilleure.CodeGen.Optimizations
{
    /// <summary>
    /// BOTW-PC CPU V2 (<see cref="CpuV2Features.ValueNumbering"/>): dominator-tree scoped value numbering on the SSA
    /// form. A pure operation whose instruction and operands equal those of an operation in a dominating position is
    /// turned into a copy of that operation's result (the following copy propagation removes it). It also folds the
    /// truncation of an extension, which the A64 front end emits around every 32-bit operation:
    /// ConvertI64ToI32(ZeroExtend32(x)) and ConvertI64ToI32(SignExtend32(x)) are the low 32 bits of x.
    /// Loads, stores, calls, atomics, barriers and floating-point conversions (which depend on the rounding mode) are
    /// never merged, so the guest-visible memory order and every architectural value stay as in the legacy code.
    /// </summary>
    static class ValueNumbering
    {
        // Debugging aid for the differential tests: 1 = no truncation folding, 2 = no merging, 4 = no LoadArgument,
        // 8 = check that every substituted value is defined in a dominating position (violations in DebugLog).
        internal static int DebugMode;
        internal static readonly List<string> DebugLog = [];

        [ThreadStatic]
        private static Dictionary<Operation, BasicBlock> _blockOf;

        private static bool Dominates(BasicBlock a, BasicBlock b)
        {
            for (BasicBlock x = b; x != null; x = x.ImmediateDominator)
            {
                if (x == a)
                {
                    return true;
                }

                if (x.ImmediateDominator == x)
                {
                    break;
                }
            }

            return false;
        }

        private static void CheckAvailable(Operation node, Operand value, string what)
        {
            if ((DebugMode & 8) == 0 || value.Kind != OperandKind.LocalVariable || value.AssignmentsCount != 1)
            {
                return;
            }

            Operation def = value.Assignments[0];

            if (!_blockOf.TryGetValue(def, out BasicBlock defBlock) || !_blockOf.TryGetValue(node, out BasicBlock useBlock))
            {
                DebugLog.Add($"{what}: block unknown");

                return;
            }

            if (!Dominates(defBlock, useBlock))
            {
                DebugLog.Add($"{what}: def {def.Instruction} in block{defBlock.Index} does not dominate use {node.Instruction} in block{useBlock.Index}");
            }
            else if (defBlock == useBlock)
            {
                for (Operation n = node; n != default; n = n.ListNext)
                {
                    if (n == def)
                    {
                        DebugLog.Add($"{what}: def {def.Instruction} after use {node.Instruction} in block{useBlock.Index}");
                        break;
                    }
                }
            }
        }

        private readonly record struct Source(byte Kind, byte Type, ulong Value, Operand Local);

        private readonly record struct Key(Instruction Instruction, OperandType Type, Source A, Source B, Source C);

        public static void RunPass(ControlFlowGraph cfg)
        {
            if ((DebugMode & 8) != 0)
            {
                _blockOf = [];

                for (BasicBlock block = cfg.Blocks.First; block != null; block = block.ListNext)
                {
                    for (Operation node = block.Operations.First; node != default; node = node.ListNext)
                    {
                        _blockOf[node] = block;
                    }
                }
            }

            // Dominator tree children (dominators were computed for the SSA construction and the CFG is unchanged).
            Dictionary<BasicBlock, List<BasicBlock>> children = [];

            for (BasicBlock block = cfg.Blocks.First; block != null; block = block.ListNext)
            {
                BasicBlock idom = block.ImmediateDominator;

                if (idom != null && idom != block)
                {
                    if (!children.TryGetValue(idom, out List<BasicBlock> list))
                    {
                        children[idom] = list = [];
                    }

                    list.Add(block);
                }
            }

            Dictionary<Key, Operand> table = [];
            List<Key> undo = [];
            Stack<(BasicBlock Block, int UndoMark, bool Exit)> stack = new();
            stack.Push((cfg.Entry, 0, false));

            while (stack.TryPop(out (BasicBlock Block, int UndoMark, bool Exit) item))
            {
                if (item.Exit)
                {
                    for (int i = undo.Count - 1; i >= item.UndoMark; i--)
                    {
                        table.Remove(undo[i]);
                    }

                    undo.RemoveRange(item.UndoMark, undo.Count - item.UndoMark);

                    continue;
                }

                stack.Push((item.Block, undo.Count, true));

                for (Operation node = item.Block.Operations.First; node != default; node = node.ListNext)
                {
                    Visit(node, table, undo);
                }

                if (children.TryGetValue(item.Block, out List<BasicBlock> list))
                {
                    foreach (BasicBlock child in list)
                    {
                        stack.Push((child, 0, false));
                    }
                }
            }
        }

        private static void Visit(Operation node, Dictionary<Key, Operand> table, List<Key> undo)
        {
            // Only single-assignment values: locals created by the instruction emitters (not guest registers) may be
            // assigned on several paths even in SSA form.
            if (node.DestinationsCount != 1 || node.Destination.Kind != OperandKind.LocalVariable || node.Destination.AssignmentsCount != 1)
            {
                return;
            }

            if (node.Instruction == Instruction.ConvertI64ToI32 && (DebugMode & 1) == 0)
            {
                FoldTruncation(node);

                if (node.Instruction == Instruction.Copy)
                {
                    return;
                }
            }

            if (!IsPure(node.Instruction) || node.SourcesCount > 3 || (DebugMode & 2) != 0 || ((DebugMode & 4) != 0 && node.Instruction == Instruction.LoadArgument))
            {
                return;
            }

            // Floating-point arithmetic and comparisons depend on the FP control state (rounding mode, flush to zero,
            // denormals are zero), which guest code can change between two otherwise identical operations: only
            // integer results and exact vector element moves are merged.
            if (!IsStateIndependent(node))
            {
                return;
            }

            Source a = default, b = default, c = default;

            for (int i = 0; i < node.SourcesCount; i++)
            {
                Operand src = node.GetSource(i);
                Source s;

                if (src.Kind == OperandKind.LocalVariable && src.AssignmentsCount == 1)
                {
                    s = new Source(1, (byte)src.Type, 0, src);
                }
                else if (src.Kind == OperandKind.Constant && !src.Relocatable)
                {
                    s = new Source(2, (byte)src.Type, src.Value, default);
                }
                else
                {
                    return;
                }

                if (i == 0)
                {
                    a = s;
                }
                else if (i == 1)
                {
                    b = s;
                }
                else
                {
                    c = s;
                }
            }

            if (IsCommutative(node.Instruction) && Order(a) > Order(b))
            {
                (a, b) = (b, a);
            }

            Key key = new(node.Instruction, node.Destination.Type, a, b, c);

            if (table.TryGetValue(key, out Operand existing))
            {
                if (existing.Type == node.Destination.Type)
                {
                    CheckAvailable(node, existing, "merge " + node.Instruction);
                    node.TurnIntoCopy(existing);
                }

                return;
            }

            table.Add(key, node.Destination);
            undo.Add(key);
        }

        private static void FoldTruncation(Operation node)
        {
            // Low 32 bits of an extension: the extended value's own low 32 bits.
            for (int depth = 0; depth < 4; depth++)
            {
                Operand x = node.GetSource(0);

                if (x.Kind != OperandKind.LocalVariable || x.AssignmentsCount != 1)
                {
                    return;
                }

                Operation def = x.Assignments[0];

                if (def.Instruction is not (Instruction.ZeroExtend32 or Instruction.SignExtend32 or Instruction.Copy) || def.SourcesCount != 1)
                {
                    return;
                }

                Operand y = def.GetSource(0);

                if ((y.Kind == OperandKind.LocalVariable && y.AssignmentsCount != 1) ||
                    (y.Kind == OperandKind.Constant && y.Relocatable) ||
                    y.Kind is not (OperandKind.LocalVariable or OperandKind.Constant) ||
                    (def.Instruction == Instruction.Copy && def.Destination.Type != y.Type))
                {
                    return;
                }

                if (y.Type == OperandType.I32)
                {
                    CheckAvailable(node, y, "fold to i32 via " + def.Instruction);
                    node.TurnIntoCopy(y);

                    return;
                }

                if (y.Type != OperandType.I64)
                {
                    return;
                }

                // ConvertI64ToI32(ext(y)) == ConvertI64ToI32(y) for a 64-bit y.
                CheckAvailable(node, y, "fold to i64 via " + def.Instruction);
                node.SetSource(0, y);
            }
        }

        private static ulong Order(Source s)
        {
            return s.Kind == 2 ? s.Value ^ 0x8000_0000_0000_0000UL : (ulong)(uint)s.Local.GetHashCode();
        }

        private static bool IsStateIndependent(Operation node)
        {
            bool elementMove = node.Instruction is
                Instruction.VectorExtract or
                Instruction.VectorExtract16 or
                Instruction.VectorExtract8 or
                Instruction.VectorInsert or
                Instruction.VectorInsert16 or
                Instruction.VectorInsert8 or
                Instruction.VectorCreateScalar or
                Instruction.VectorZeroUpper64 or
                Instruction.VectorZeroUpper96;

            if (elementMove)
            {
                return true;
            }

            if (!node.Destination.Type.IsInteger())
            {
                return false;
            }

            for (int i = 0; i < node.SourcesCount; i++)
            {
                if (!node.GetSource(i).Type.IsInteger())
                {
                    return false;
                }
            }

            return true;
        }

        private static bool IsCommutative(Instruction inst)
        {
            return inst is Instruction.Add or
                Instruction.BitwiseAnd or
                Instruction.BitwiseOr or
                Instruction.BitwiseExclusiveOr or
                Instruction.Multiply or
                Instruction.Multiply64HighSI or
                Instruction.Multiply64HighUI;
        }

        private static bool IsPure(Instruction inst)
        {
            return inst is Instruction.Add or
                Instruction.BitwiseAnd or
                Instruction.BitwiseExclusiveOr or
                Instruction.BitwiseNot or
                Instruction.BitwiseOr or
                Instruction.ByteSwap or
                Instruction.Compare or
                Instruction.ConditionalSelect or
                Instruction.ConvertI64ToI32 or
                Instruction.CountLeadingZeros or
                Instruction.Divide or
                Instruction.DivideUI or
                Instruction.LoadArgument or
                Instruction.Multiply or
                Instruction.Multiply64HighSI or
                Instruction.Multiply64HighUI or
                Instruction.Negate or
                Instruction.RotateRight or
                Instruction.ShiftLeft or
                Instruction.ShiftRightSI or
                Instruction.ShiftRightUI or
                Instruction.SignExtend16 or
                Instruction.SignExtend32 or
                Instruction.SignExtend8 or
                Instruction.Subtract or
                Instruction.ZeroExtend16 or
                Instruction.ZeroExtend32 or
                Instruction.ZeroExtend8 or
                Instruction.VectorExtract or
                Instruction.VectorExtract16 or
                Instruction.VectorExtract8 or
                Instruction.VectorCreateScalar or
                Instruction.VectorInsert or
                Instruction.VectorInsert16 or
                Instruction.VectorInsert8 or
                Instruction.VectorZeroUpper64 or
                Instruction.VectorZeroUpper96;
        }
    }
}
