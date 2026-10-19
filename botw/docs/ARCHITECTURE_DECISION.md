# ADR-001: Runtime foundation for BOTW-PC

Status: **accepted (2026-10-13)** — revisit when the open measurement below exists.

## Decision
**Keep the Ryubing (Ryujinx 1.3.3) core as BOTW-PC's runtime, embedded in the standalone `botw-pc` application, and
specialise it incrementally.** Do not migrate to an Eden-derived runtime now; do not start static recompilation.
Keep Eden 0.2.1 set up as a measured reference (`BACKEND_COMPARISON.md`).

## Evidence
- Compatibility: BOTW-PC boots 1.6.0 with update, DLC, mods, Xbox prompts, virtual motion and correct game speed at
  30/60/90 FPS — all verified in this project. Eden boots the same build with update and DLC (verified) but has none of
  these BOTW-PC features and no high-frame-rate timing path (uncapped still 30 FPS, measured).
- Performance: the only like-for-like measurement (title menu, 30 FPS) shows both at a flat 30 FPS and Eden using
  ~half the CPU time. **In-game performance of Eden is not measured**, so there is no evidence that migrating would
  raise BOTW-PC's in-game frame rate (limited by per-draw work on the GPU command thread and a cross-thread dependency
  chain, `PERFORMANCE.md`).
- AOT/static recompilation: mk8-recomp reports parity with the JIT at best; ScarletNX has no BotW report; BOTW-PC's
  guest CPU threads are not saturated (busiest 59 %), so recompiling guest code would not move the current bottleneck.
- Cemu (Wii U) is a different game build and platform; its FPS++ shows the method for UI/animation timing (patch the
  individual per-frame constants), which applies to BOTW-PC's open UI-speed bug.

## Rejected alternatives
| Alternative | Why not now |
|---|---|
| Migrate to Eden | GPL-3.0 C++ codebase: every BOTW-PC feature (timing, prompts, motion, mods, DLC checks, launcher, profiling) would be rewritten; in-game benefit unproven. |
| Citron / Citron Neo | original project shut down; community fork without signed releases (governance and provenance risk). |
| ScarletNX | three-day-old research fork, BotW never reported running, GPL-3.0. |
| Hybrid JIT + AOT (mk8-recomp style) | measured parity, not speed-up; large tooling cost; guest CPU is not the bottleneck. |
| Cemu | not the Switch 1.6.0 game; saves/DLC/mods differ. |

## License implications
BOTW-PC stays MIT (Ryujinx). GPL-3.0 code (yuzu/Eden/ScarletNX/mk8-recomp) must not be copied into BOTW-PC; using it
would make the whole program GPL-3.0. Ideas and documented behaviour may be used; FPS++ code may not be copied
(its authors forbid redistribution). The NVIDIA DLSS SDK stays a build-time download.

## Migration risks / rollback
No migration is performed. If a later measurement shows Eden clearly faster in game, the plan is a proof-of-concept
branch (never a rewrite of `botw-pc`), starting with the renderer path only, and a fresh license decision.

## Required refactoring (this foundation)
Continue the specialisation already started: standalone launcher (done), BotW-only features gated on the title and
build id (done), cross-thread timeline profiler and GPU-thread work (Phase D), DLSS in the Vulkan backend (Phase E).

## Maintenance cost
Rebasing on newer Ryubing releases is the main recurring cost; BOTW-PC changes are mostly in new files plus small
hooks (listed in `ARCHITECTURE.md`), which keeps rebases cheap.

## Open item that can change this decision
Eden in-game S1 benchmark (`scripts/bench_eden.sh`, needs a person to press A once). If Eden's uncapped-equivalent
throughput in S1 is substantially above BOTW-PC's ~93 FPS, re-open this ADR.
