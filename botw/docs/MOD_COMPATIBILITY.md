# Community mods tested in BOTW-PC

Real community packages, downloaded from the authors' pages (2026-10-14) into a local folder outside the repository,
and installed with the BOTW-PC mod CLI into a disposable data directory (`../progress-test`, copy of the progressed save).
Archives were listed and their text files read before installing. No bundled script or binary was run, and nothing was
modified except where the author's own instructions say how to place the files.

## Results

| Mod (source) | Install | In game | Verdict |
|---|---|---|---|
| **Wollnashorn 1080p** pchtxt (GitHub `Wollnashorn/switch-mods`) | installed; build ID 1.6.0 accepted with the "main executable patch" warning | patch applied at `0x1821674` (log); the same scene has about 5× more high-frequency detail (Laplacian variance about 128 vanilla → 650–700 with 1080p + no FXAA) and visibly sharper hair and grass; DLSS stays idle because the game no longer runs its upscale | **COMPATIBLE** |
| **Wollnashorn Disable FXAA** pchtxt | same | patch applied at `0x1420FD8`; combined with 1080p as above (aliasing visible on grass) | **COMPATIBLE** |
| **Infinite Durability** (GameBanana 49134, `atmosphere/contents/<tid>/romfs` layout) | installed (269 weapon/shield packs) | files replaced in the RomFS (loader log); the durability effect was **not observed**: scripted swings missed the tree, and the vanilla branch never broke either | loads; effect NOT VERIFIED |
| **No Friction Shields**, old build (GameBanana 49081, bare files) | folder layout built per the author's note ("place in romfs/Actor/Pack"), then installed | loads | loads; effect NOT VERIFIED |
| Durability + No Friction Shields together | the conflict list shows all 33 `Weapon_Shield_*.sbactorpack` files; the higher mod wins whole files (log: "Skipped duplicate … from 'InfiniteDurability'") | as reported | **REQUIRES MERGING**: whole-file priority loses one mod's shield change, so a real merge needs UKMM (parameter-level merge of the actor packs) |
| **No Loot Boxes** (GameBanana 444097, MIT) | installed | files replaced; amiibo drops not testable here | loads |
| **Better Link House** (GameBanana 148829, DLC 1 `AocMainField.pack`) | installed to the DLC 1 mount; the stray Atmosphere `romfs_metadata.bin` is ignored | when the game opens DLC 1: "Applying RomFS mods for Application 01007EF00011F001 … Replaced 1 file(s)"; Hateno house not reachable from this save | loads into the DLC mount; effect NOT VERIFIED |
| **Xbox One UI SL** (GameBanana 33340) | installed (`Font_US/EU.sbfarc`) | Traditional Chinese: no effect (only English fonts). **English: the game crashes about 10 s after start** (guest null access at `U-King.nss+0x1844510`). Reproduced 4 times; with only this mod enabled it still crashes, with it disabled it does not. Cause: the archives contain whole copies of every font from an older game version (`Normal`, `NormalS`, `Caption`, `Special` differ in size from 1.6.0), not only the button glyphs | **INCOMPATIBLE** with 1.6.0 |
| Prompt conflict handling with a real prompt mod (same mod, Xbox pad as Player 1) | — | `BuiltIn`: "conflict: mod 'XboxOneUI-SL' replaces /Font/Font_US.sbfarc/External_00.bffnt …; drawn over by BOTW-PC prompts"; `Mod`: "kept the mod's version" | detection **works**; visual result not observable because the mod crashes the game |

## Mod Manager checks with these mods

| Check | Result |
|---|---|
| Layout detection: `atmosphere/contents/<tid>/romfs`, `contents/<tid>/romfs` under a wrapper folder, `<name>/romfs`, `exefs/*.pchtxt` | all installed into the right title (game or DLC 1) |
| Vanilla profile → no mods, no patches at the next start; the image matches vanilla again (Laplacian variance 115–123) | pass, after a fix: a DLC-only mod (Better Link House) used to read as enabled and could be re-enabled by the next save (fixed, regression test) |
| Saved custom profile restores the previous set | pass |
| Safe mode (`BOTWPC_SAFE_MODE=1`) | no mods, patches or cheats; vanilla image |

## Not covered
Merging the conflicting pair in UKMM (needs an unpacked RomFS dump); the full Xbox UI pack 651656 (`.rar`, must be
extracted first); DLC-area and amiibo effects; long sessions with mods.
