using ARMeilleure.CodeGen;
using ARMeilleure.CodeGen.Optimizations;
using ARMeilleure.Diagnostics;
using ARMeilleure.IntermediateRepresentation;
using System;
using System.Runtime.InteropServices;

namespace ARMeilleure.Translation
{
    static class Compiler
    {
        /// <summary>
        /// BOTW-PC: observes the IR between passes ("ssa", "optimized", "allocated"); set only by JIT analysis tests.
        /// </summary>
        internal static Action<string, ControlFlowGraph> Observer;

        /// <summary>
        /// BOTW-PC CPU V2: a conditional branch whose target is also its fall-through block (e.g. A64 B.cond/CBZ/TBZ
        /// to the next instruction) gives the block two edges to the same successor. The linear-scan allocator then
        /// splits "both" edges into a chain of two blocks that each run the edge's resolution moves, which corrupts
        /// values when the sequence is not idempotent (spill, then fill into the spilled register). Found by the CPU V2
        /// differential tests; the legacy code generator has the same latent defect. The branch has no side effect and
        /// both outcomes go to the same place, so it is removed and the edge kept once.
        /// </summary>
        private static void RemoveDuplicateEdges(ControlFlowGraph cfg)
        {
            bool changed = false;

            for (BasicBlock block = cfg.Blocks.First; block != null; block = block.ListNext)
            {
                if (block.SuccessorsCount == 2 && block.GetSuccessor(0) == block.GetSuccessor(1))
                {
                    Operation last = block.Operations.Last;

                    if (last != default && last.Instruction == Instruction.BranchIf)
                    {
                        block.Operations.Remove(last);

                        for (int index = 0; index < last.SourcesCount; index++)
                        {
                            last.SetSource(index, default);
                        }

                        block.RemoveSuccessor(1);
                        changed = true;
                    }
                }
            }

            if (changed)
            {
                cfg.Update();
            }
        }

        public static CompiledFunction Compile(
            ControlFlowGraph cfg,
            OperandType[] argTypes,
            OperandType retType,
            CompilerOptions options,
            Architecture target)
        {
            CompilerContext cctx = new(cfg, argTypes, retType, options);

            if (CpuV2.Has(CpuV2Features.DuplicateEdgeFix))
            {
                RemoveDuplicateEdges(cfg);
            }

            if (options.HasFlag(CompilerOptions.Optimize))
            {
                Logger.StartPass(PassName.TailMerge);

                TailMerge.RunPass(cctx);

                Logger.EndPass(PassName.TailMerge, cfg);
            }

            if (options.HasFlag(CompilerOptions.SsaForm))
            {
                Logger.StartPass(PassName.Dominance);

                Dominance.FindDominators(cfg);
                Dominance.FindDominanceFrontiers(cfg);

                Logger.EndPass(PassName.Dominance);

                Logger.StartPass(PassName.SsaConstruction);

                Ssa.Construct(cfg);

                Logger.EndPass(PassName.SsaConstruction, cfg);

                Observer?.Invoke("ssa", cfg);
            }
            else
            {
                Logger.StartPass(PassName.RegisterToLocal);

                RegisterToLocal.Rename(cfg);

                Logger.EndPass(PassName.RegisterToLocal, cfg);
            }

            if (target == Architecture.X64)
            {
                return CodeGen.X86.CodeGenerator.Generate(cctx);
            }
            else if (target == Architecture.Arm64)
            {
                return CodeGen.Arm64.CodeGenerator.Generate(cctx);
            }
            else
            {
                throw new NotImplementedException(target.ToString());
            }
        }
    }
}
