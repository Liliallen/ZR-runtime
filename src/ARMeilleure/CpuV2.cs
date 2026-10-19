using System;

namespace ARMeilleure
{
    /// <summary>
    /// BOTW-PC CPU V2: code generator changes of the high-quality (SSA) tier, each selectable on its own so that it can
    /// be measured and verified against the legacy code generator (<see cref="None"/>). Every feature preserves the
    /// architectural ARM64 state exactly, except <see cref="AbiCallLiveness"/>, which relies on the AArch64 procedure
    /// call standard and is therefore not part of <see cref="Exact"/>.
    /// </summary>
    [Flags]
    public enum CpuV2Features
    {
        None = 0,

        /// <summary>Dominator-scoped value numbering of pure IR operations, with exact folding of 32/64-bit
        /// extension and truncation chains (every 32-bit ARM operation reads a truncated 64-bit register and writes a
        /// zero-extended one).</summary>
        ValueNumbering = 1 << 0,

        /// <summary>Linear-scan register allocation: a value spilled at several places is written to its stack slot
        /// once, after its definition, instead of at every register-to-stack split point.</summary>
        SpillAtDefinition = 1 << 1,

        /// <summary>Collapses a conditional branch whose two successors are the same block before compilation. Fixes a
        /// latent defect of the legacy allocator (edge resolution moves emitted twice on one path), found by the CPU V2
        /// differential tests.</summary>
        DuplicateEdgeFix = 1 << 2,

        /// <summary>SSA deconstruction: the copies of a phi go on the edge that needs them (a new block on a forward
        /// critical edge) instead of the end of a predecessor that also branches elsewhere.</summary>
        PhiCopyOnEdge = 1 << 3,

        /// <summary>Linear-scan allocation: a value copied into an already allocated interval prefers its register,
        /// so the copy disappears (complements the existing copy-source hint).</summary>
        ReverseCopyHints = 1 << 6,

        /// <summary>After allocation: register moves and fills overwritten before any read within a run of moves
        /// (split and edge resolution sequences) are removed, and no jump is emitted to a successor reached by falling
        /// through empty blocks.</summary>
        CodeCleanup = 1 << 7,

        Exact = ValueNumbering | SpillAtDefinition | DuplicateEdgeFix | PhiCopyOnEdge | ReverseCopyHints | CodeCleanup,

        /// <summary>
        /// Treats the condition flags (NZCV) as dead at BL/BLR call sites and at RET: they are neither kept alive nor
        /// written back for a callee or caller that cannot read them (AAPCS64: undefined on entry to and return from a
        /// public interface; no AArch64 calling convention, including TLS descriptor and outline-atomic helpers, passes
        /// or preserves them). Not architecturally exact for code that reads NZCV across a call.
        /// </summary>
        AbiFlagLiveness = 1 << 16,

        /// <summary>
        /// Additionally treats the temporary registers X9-X17 (and X8 at RET) and V16-V31 as dead at BL/BLR and RET.
        /// Correct for the standard convention only: special conventions (TLS descriptor resolvers, some runtime
        /// helpers) preserve temporaries that their callers then keep live across the call.
        /// </summary>
        AbiTemporaryLiveness = 1 << 17,

        AbiCallLiveness = AbiFlagLiveness | AbiTemporaryLiveness,
    }

    public static class CpuV2
    {
        /// <summary>Selected features; set once at startup, before any guest code is translated.</summary>
        public static CpuV2Features Features { get; set; }

        public static bool Has(CpuV2Features feature) => (Features & feature) != 0;

        /// <summary>Suffix that keeps PPTC caches of different code generators apart ("" for the legacy one).</summary>
        public static string CacheSuffix => Features == CpuV2Features.None ? "" : $"-v2-{(int)Features:x}";
    }
}
