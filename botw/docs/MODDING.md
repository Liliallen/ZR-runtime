# Mods

BOTW-PC manages BotW mods inside the directories and files Ryujinx's `ModLoader` already uses, so nothing is loaded
by a second system:

| What | Where (in the data dir, e.g. `../devdata`) |
|---|---|
| installed mods | `mods/contents/<title id>/<mod name>/{romfs,exefs,cheats}` (game `01007ef00011e000`, DLC `01007ef00011f001`/`…f002`) |
| enabled state + **priority order** | `games/<title id>/mods.json` (first entry wins a file conflict) |
| per-mod manifest | `<mod dir>/botwpc-mod.json` (name, version, category, target titles, files, source, warnings) |
| staging, backups, profiles, settings | `botw/staging`, `botw/backup/<mod>/<time>`, `botw/profiles/*.json`, `botw/settings.json` |

Mods placed by hand (or by the stock "Add" into `sdcard/atmosphere/contents`) are listed and managed too.
The original game, update and DLC files are never modified: mods are layered at load time.

## Supported formats
| Input | Result |
|---|---|
| Switch LayeredFS folder or `.zip`: `romfs/`, `exefs/`, `cheats/` at the root, under `<title id>/`, `contents/<title id>/` or `atmosphere/contents/<title id>/` (one wrapping folder is fine) | **installed**; DLC parts (`01007EF00011F00x`) go to the DLC's own mount |
| **UKMM deploy output** (Switch mode, BotW 1.6.0) | **installed** like any LayeredFS layout — this is the recommended way to combine several mods |
| ExeFS `.ips` / `.pchtxt` patches | installed only if they target a BotW **1.6.0** module build id; refused if they patch `VFR::updateInterval` (main+0x1C01180–0x1C01398), the frame-timing code BOTW-PC's timing correction relies on; other main patches install with a warning |
| ExeFS executable replacement (`main`, `subsdk*`, …) | installed with a warning: the build id changes, so BOTW-PC's timing correction stays off |
| unmerged UKMM package (`meta.yml`) | refused: install in UKMM and import its deploy folder |
| BCML `.bnp` / extracted BCML package | refused: merge with BCML/UKMM first |
| Wii U mods (`content/`, `aoc/`), Cemu graphic packs (`rules.txt`) | refused: not loadable on the Switch version |
| other titles, `.7z`/`.rar` | refused with a reason (extract `.7z`/`.rar` and import the folder) |

## Installer safety
Folder or `.zip` → staging → inspection → atomic move into place. Rejected before anything is written: absolute or `..`
paths, symlinks (zip entries and folder contents), more than 200 000 files, files > 2 GiB, total > 8 GiB,
compression ratio > 200:1; extraction also stops if an entry is larger than declared. A failed install or update
leaves the installed version untouched (tested). Updates and removals move the previous files to `botw/backup`
(last 2 kept per mod) and can be restored ("Restore backup…" / `rollback`). Installing/removing is refused while a
game runs (RomFS files are read lazily); enable/disable/order changes take effect at the next start.

## Load order and conflicts
List order = priority. New mods are inserted at the top. `ModLoader` now sorts mods by `mods.json` order (it used to
follow filesystem enumeration order, which is unspecified); unlisted mods follow by name. When two enabled mods ship
the same RomFS file, the higher one wins and the conflict is listed; files that BotW mods usually need *merged*
(RSTB, ActorInfo, Bootup.pack, gamedata, map units, …) are flagged "needs merge": file priority cannot combine them —
merge those mods in UKMM and install the deploy output instead.

## Profiles and safe mode
Vanilla (all off), Performance (category `patch`/`performance`), Visual (`visual`/`ui`), Custom (saved set) or any saved
name. Categories are guessed from the files (Layout/Font → ui, Sound → audio, Model/Effect/Terrain → visual, ExeFS-only
→ patch, otherwise gameplay). Safe mode (checkbox, `safe-mode on`, or `BOTWPC_SAFE_MODE=1`) starts the game with no mods,
ExeFS patches or cheats.

## UI and CLI
Game list → right click BotW → **Manage Mods**: install `.zip`/folder, priority arrows per mod, enable checkboxes,
remove (to backup), restore backup, profile + apply / save as Custom, safe mode, conflict summary + details, "needs DLC n"
note when a mod targets DLC content that is not installed/enabled. Other games keep the stock dialog.

```
../build-botwpc/Ryujinx --botw-mods -r ../devdata list | install <zip|dir> [name] | update <zip|dir> <name> |
    uninstall <name> | rollback <name> | enable|disable <name> | up|down <name> | conflicts |
    profile <name> | save-profile <name> | safe-mode on|off
```

## Verification
| Test | Result |
|---|---|
| 12 unit tests (`ModSystemTests`, synthetic files): traversal/absolute/symlink/size limits, layouts incl. UKMM deploy with DLC part, Wii U/Cemu/BCML/UKMM-package refusal, ExeFS version + timing-code checks, case-insensitive build ids, conflicts/merge flags, priority, enable, update, failed update keeps old version, uninstall + rollback, profiles | **pass** |
| CLI on the real `devdata`: install folder + `.zip`, path-traversal zip refused, duplicate name refused, conflict listed | **pass** |
| Game start with two test mods (synthetic extra file): loader applies them in `mods.json` order (`results/mods-on/loader.log`), game runs (speed 1.00) | **pass** |
| Priority swapped → the other mod's duplicate is skipped (`results/mods-swap/loader.log`) | **pass** |
| Safe mode → no mods loaded, title screen reached (`results/mods-safe`) | **pass** |
| Real community mods in game (exefs patches, romfs gameplay mods, a DLC-mount mod, a prompt mod, conflicts, profiles) | **PARTIAL**, see MOD_COMPATIBILITY.md; UKMM deploy of several mods NOT TESTED |
| Mod manager window layout | built and compiled; **not visually checked** (no desktop screenshots by design) |

## Controller prompt mods (Xbox/PlayStation glyph packs)
BOTW-PC's own prompt overlay (`CONTROLLER_PROMPTS.md`) is generated from the files the game would load **after** RomFS
mods, so a mod's other changes are kept. If an installed mod replaces the same glyph data (`External_00.bffnt` in
`Font/Font_*.sbfarc`, or the `Nt_KeyTex*`/D-pad/± textures in `Pack/Bootup.pack`), the log reports a conflict naming
the mod and file, and `controller.prompt_priority` decides: `BuiltIn` (default) draws BOTW-PC's glyphs over the mod's
so prompts keep matching the active mapping; `Mod` leaves that file as the mod made it. Wii U/Cemu prompt packs
(`.bflim`, `content/` layouts) are not Switch-format and are rejected by the installer like other Wii U mods.
Tested with a real prompt mod (GameBanana 33340): conflicts are detected and both priorities act as described, but that mod
crashes 1.6.0 in English (old font archives), see MOD_COMPATIBILITY.md. Catalog of community mods: MOD_DISCOVERY.md.
