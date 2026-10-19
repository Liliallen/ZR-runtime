using ARMeilleure.IntermediateRepresentation;
using ARMeilleure.Translation;
using System;

namespace ARMeilleure.CodeGen.Optimizations
{
    /// <summary>
    /// BOTW-PC CPU V2 (<see cref="CpuV2Features.CodeCleanup"/>): after register allocation, the split and edge
    /// resolution of the linear-scan allocator can emit, in one run of moves, a register write that a later move of the
    /// same run overwrites before anything reads it (a fill for a split interval that ends at a block edge whose
    /// resolution then moves another value into the same register). Inside a run of consecutive Copy, Fill and Spill
    /// operations (nothing else, so no implicit register use), such a write is removed. A register still pending at
    /// the end of the run is kept: anything after the run may read it.
    /// </summary>
    static class DeadMoveElimination
    {
        private const int MaxRegisters = 32;

        public static void RunPass(ControlFlowGraph cfg)
        {
            Operation[] pending = new Operation[MaxRegisters * 2];

            for (BasicBlock block = cfg.Blocks.First; block != null; block = block.ListNext)
            {
                Array.Clear(pending);

                for (Operation node = block.Operations.First; node != default;)
                {
                    Operation next = node.ListNext;

                    if (node.Instruction is not (Instruction.Copy or Instruction.Fill or Instruction.Spill))
                    {
                        Array.Clear(pending);
                        node = next;

                        continue;
                    }

                    // Reads of this move (Copy and Spill read a register or nothing else).
                    for (int i = 0; i < node.SourcesCount; i++)
                    {
                        Operand source = node.GetSource(i);

                        if (source.Kind == OperandKind.Register)
                        {
                            pending[Slot(source)] = default;
                        }
                        else if (source.Kind == OperandKind.Memory)
                        {
                            Array.Clear(pending);
                        }
                    }

                    if (node.Instruction != Instruction.Spill && node.DestinationsCount == 1 &&
                        node.Destination.Kind == OperandKind.Register &&
                        (node.Instruction == Instruction.Fill || node.GetSource(0).Kind is OperandKind.Register or OperandKind.Constant))
                    {
                        int slot = Slot(node.Destination);
                        Operation previous = pending[slot];

                        if (previous != default)
                        {
                            block.Operations.Remove(previous);

                            for (int i = 0; i < previous.SourcesCount; i++)
                            {
                                previous.SetSource(i, default);
                            }

                            previous.Destination = default;
                        }

                        // A copy to the same register writes nothing new (it is not emitted).
                        bool selfCopy = node.Instruction == Instruction.Copy &&
                            node.GetSource(0).Kind == OperandKind.Register &&
                            Slot(node.GetSource(0)) == slot;

                        pending[slot] = selfCopy ? default : node;
                    }

                    node = next;
                }
            }
        }

        private static int Slot(Operand register)
        {
            Register reg = register.GetRegister();

            return (reg.Index & (MaxRegisters - 1)) * 2 + (reg.Type == RegisterType.Vector ? 1 : 0);
        }
    }
}
