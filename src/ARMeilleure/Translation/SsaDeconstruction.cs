using ARMeilleure.IntermediateRepresentation;
using System.Collections.Generic;

using static ARMeilleure.IntermediateRepresentation.Operand.Factory;
using static ARMeilleure.IntermediateRepresentation.Operation.Factory;

namespace ARMeilleure.Translation
{
    static partial class Ssa
    {
        public static void Deconstruct(ControlFlowGraph cfg)
        {
            bool onEdges = CpuV2.Has(CpuV2Features.PhiCopyOnEdge);
            Dictionary<BasicBlock, BasicBlock> edgeBlocks = null;
            bool split = false;

            for (BasicBlock block = cfg.Blocks.First; block != null; block = block.ListNext)
            {
                Operation operation = block.Operations.First;

                edgeBlocks?.Clear();

                while (operation != default && operation.Instruction == Instruction.Phi)
                {
                    Operation nextNode = operation.ListNext;

                    Operand local = Local(operation.Destination.Type);

                    PhiOperation phi = operation.AsPhi();

                    for (int index = 0; index < phi.SourcesCount; index++)
                    {
                        BasicBlock predecessor = phi.GetBlock(cfg, index);

                        Operand source = phi.GetSource(index);

                        BasicBlock target = predecessor;

                        // BOTW-PC CPU V2: the copy is needed only on the edge to this block. A predecessor that also
                        // branches elsewhere would run it on its other path too (e.g. the hot path past an early-exit
                        // check), so a forward edge gets its own block for the copies. Loop back edges keep them in the
                        // predecessor, where they cost no extra jump per iteration.
                        if (onEdges && predecessor.SuccessorsCount > 1 && !Dominates(block, predecessor))
                        {
                            edgeBlocks ??= [];

                            if (!edgeBlocks.TryGetValue(predecessor, out target))
                            {
                                target = cfg.SplitEdge(predecessor, block);
                                edgeBlocks[predecessor] = target;
                                split = true;
                            }
                        }

                        target.Append(Operation(Instruction.Copy, local, source));

                        phi.SetSource(index, default);
                    }

                    Operation copyOp = Operation(Instruction.Copy, operation.Destination, local);

                    block.Operations.AddBefore(operation, copyOp);

                    operation.Destination = default;

                    block.Operations.Remove(operation);

                    operation = nextNode;
                }
            }

            if (split)
            {
                // The register allocator walks the blocks in post order.
                cfg.Update();
            }
        }

        private static bool Dominates(BasicBlock dominator, BasicBlock block)
        {
            for (BasicBlock current = block; current != null; current = current.ImmediateDominator)
            {
                if (current == dominator)
                {
                    return true;
                }

                if (current.ImmediateDominator == current)
                {
                    break;
                }
            }

            return false;
        }
    }
}
