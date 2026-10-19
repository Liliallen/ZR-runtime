# DLC (add-on content)

## How it works (Ryujinx's own DLC system, reused)
Game list → right click BotW → **Manage DLC** → **Add** → pick the DLC `.nsp`. Ryujinx opens the package with your
keys, finds the add-on content (PublicData) NCA, checks its title id against the game, refuses duplicates, and stores a
**reference** in `<data>/games/01007ef00011e000/dlc.json` (enabled flag per package). Nothing is copied, extracted or
modified: the original package stays where it is, read-only; removing a DLC only removes the reference. At game start
enabled packages are registered with the content manager; the game finds them through its own add-on content service
(`aoc:u` List/Prepare) and mounts them via `OpenDataStorageByDataId` — a separate add-on mount, not merged into the
game's RomFS. Mods for a DLC title (`mods/contents/01007ef00011f00x/`) are layered onto that mount (`MODDING.md`).

BOTW-PC adds, for BotW only:
| Addition | Where |
|---|---|
| Name, version and **minimum game version** of each package, read from its content metadata (cnmt), compared with the selected update | DLC dialog status area, `--botw-dlc` |
| **Full content verification**: every block of the add-on data is read with hash checking (`IntegrityCheckLevel.ErrorOnInvalid`); a corrupted or wrongly decrypted package fails | "Verify DLC" button, `--botw-dlc --verify` |
| Duplicate add-on detection across packages | status area |
| Log of what the game's own add-on content query returns (`BOTW-PC: game listed add-on content indices [...]`) | game log |
| "needs DLC n" note on mods that target DLC content that is not enabled | mod manager |

```
../build-botwpc/Ryujinx --botw-dlc -r ../devdata [--verify]
```

## Results on this machine (US packages, v196608 each; game update v786432 = 1.6.0)
| Test | Result | Evidence |
|---|---|---|
| Read metadata of both packages | Pack 1 *The Master Trials* `01007EF00011F001` v196608 (#3); Pack 2 *The Champions' Ballad* `01007EF00011F002` v196608 (#3); both need game ≥ v655360 (#10): **compatible** | `--botw-dlc` |
| Full hash verification | Pack 1: 7 094 files, 1 325.5 MiB, all valid; Pack 2: 1 file (Pack 2 is tiny: BotW keeps the DLC assets in Pack 1) | `--botw-dlc --verify` |
| Negative control: copy of Pack 1 with one byte flipped | **fails** (`ResultFsNonRealDataVerificationFailed: Hash error`) | scratch copy, deleted |
| Missing update | reported as "needs game update v655360" | same control run |
| Game start, both enabled | game lists add-on indices **[1, 2]**, mounts Pack 1; title screen shows **"Play Master Mode"** and **"DLC Ver. 3.0"**; the in-game "Purchased add-on content" page lists *Trial of the Sword* and *Master Mode* | `results/dlc-both/aoc.log`, captures (local) |
| Game start, all disabled | no add-on content listed; no Master Mode entry, no DLC version on the title screen | `results/dlc-none` |
| Game start, only Pack 1 enabled | game lists **[1]** but does **not** mount it and shows **no** Master Mode / DLC version | `results/dlc-pack1/aoc.log` — see limitations |
| Existing save with DLC on/off | the dev save loads in both cases (S1 benchmark runs use DLC enabled) | benchmark runs |

## Not tested / limitations
- **The Master Trials content in play** (Trial of the Sword, Master Mode run, Hero's Path, Travel Medallion) and
  **The Champions' Ballad content** (One-Hit Obliterator quest, Master Cycle Zero, shrines): **NOT TESTED** — the only
  save is at the start of the game; recognition is shown only by the title screen, the DLC page and the add-on queries.
- **Pack 1 alone is not recognised — this is the game's own rule, not an emulation gap.** BotW's
  `ksys::aoc::Manager::init` (zeldaret/botw decomp, `src/KingSystem/AOC/aocManager.cpp`, 1.5.0) asks
  `nn::aoc::ListAddOnContent` for 2 entries and mounts add-on content only if **exactly 2** are returned and one of
  them is index 1 (then it mounts index 1, which holds all DLC assets; index 2 is a 1-file package). The 1.6.0 game
  behaves the same here (lists [1] → no mount; lists [1, 2] → `PrepareAddOnContent(1)` + mount). On Switch the two
  packs were sold together as the Expansion Pass, so owning both is the normal case. BOTW-PC does not change this
  (no faking of add-on content or entitlements).
- Packages are referenced, not imported: moving or deleting the `.nsp` makes the DLC disappear (Ryujinx then drops it
  from the list on the next scan). This keeps installs atomic (only `dlc.json` changes) and never duplicates gigabytes.
- `.xci` bundles and multi-content NSPs work through the same Ryujinx code but were not tested here.
- The dialog layout is compiled but was not visually checked (no desktop screenshots by design).
