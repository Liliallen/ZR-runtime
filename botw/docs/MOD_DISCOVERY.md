# Community mod discovery (Switch BotW 1.6.0)

Catalog of existing community work, checked before writing anything new. Research date 2026-10-14.
- **Sources:** GameBanana's public `apiv11` JSON, GitHub, the ZeldaMods wiki and the UKMM book. Nexus Mods pages returned
  HTTP 403, so Nexus facts come from search snippets only.
- **Tested** links to MOD_COMPATIBILITY.md. Everything else is web research only.
- **Mirroring:** BOTW-PC never mirrors mod files. Most GameBanana mods are CC BY-NC-ND 4.0, which allows downloading and
  installing for yourself, but redistribution needs the author's permission.

Facts that drive the choices:
- **GameBanana IDs:** the Switch hub is **game 6386** (about 720 mods). Game 5866 is the Wii U hub, and its mods
  (Cemu `content/`, `rules.txt`) are not Switch-compatible.
- **Game versions:** ZeldaMods lists Switch 1.8.x, and newer uploads target "1.9.0". Every exefs patch must target the
  1.6.0 main build `8E9978D50BDD20B4C8395A106C27FFDE`; BOTW-PC's installer already enforces this.
- **Unstated versions:** many romfs mods do not state a game version. A romfs file built from an older version can
  still crash 1.6.0 (see the Xbox One UI result below).

Classes: READY TO TEST (Switch package, public download, 1.6.0 stated or likely) · COMPATIBLE (tested here, works) ·
REQUIRES CONVERSION · REQUIRES MERGING (BCML `.bnp` / UKMM package → merge in UKMM, import the deploy folder) ·
INCOMPATIBLE · UNVERIFIED.

## Controller UI / button prompts

| Mod | Author | Source | Date | Type, licence | Class | Notes |
|---|---|---|---|---|---|---|
| Xbox One UI for Nintendo Switch | NoPyro (port), spauldhaliwal | https://gamebanana.com/mods/33340 | 2020-01 | romfs `Font_US/EU.sbfarc`; CC BY-NC-ND 4.0 | **INCOMPATIBLE (tested)** | Ships whole older-version font archives; 1.6.0 crashes in English (MOD_COMPATIBILITY.md). Only English fonts, so no effect in other languages. |
| PS4 UI for Nintendo Switch | NoPyro | https://gamebanana.com/mods/33341 | 2020-01 | romfs; CC BY-NC-ND 4.0 | UNVERIFIED (likely the same font issue) | |
| Full Xbox/PS UI (Switch) | bitasuperactive (reposter) | https://gamebanana.com/mods/651656 | 2026-02 | romfs incl. tips/tutorials, `.rar` 26.8 MB; CC BY-NC-ND 4.0 | READY TO TEST | Original author unknown. `.rar` must be extracted before import. |
| BOTW Xbox UI Mod v1, Western layout | Velkyn | https://gamebanana.com/mods/670522 | 2026-04 to 2026-08 | UKMM package; CC BY-NC-ND 4.0 | REQUIRES MERGING | Per its page, it does not replace the controller illustration |
| BOTW DS4 UI Mod v2, Western layout | Velkyn | https://gamebanana.com/mods/659253 | 2026-04 | UKMM package + standalone; CC BY-NC-ND 4.0 | READY TO TEST (standalone) | |

**Reuse decision:** BOTW-PC's own prompt overlay (CONTROLLER_PROMPTS.md) already follows the live mapping (Position or
Label layout, custom bindings). The community packs are fixed layouts, and none of them claims to replace the
controller illustration. They stay optional user mods, and the conflict handling (`prompt_priority`) lets them win.

## Performance / resolution (exefs)

| Mod | Author | Source | Target | Licence | Class | Notes |
|---|---|---|---|---|---|---|
| BotW 1080p | Wollnashorn | https://github.com/Wollnashorn/switch-mods (`01007EF00011E000/1080p/exefs/1.6.0.pchtxt`) | `@nsobid-8E9978D5…` (1.6.0) | none stated (public repo) | **COMPATIBLE (tested)** | Native 1080p render; DLSS then has no upscale to replace |
| BotW Disable FXAA | Wollnashorn | same repository | 1.6.0 | none stated | **COMPATIBLE (tested)** | Sharper image, visible aliasing |
| BotW Anisotropic Fix | Wollnashorn | same repository | 1.6.0 | none stated | READY TO TEST | Visible only with emulator anisotropic filtering 2x–16x |
| UltraCam (resolution, FPS, free camera) | MaxLastBreath | https://gamebanana.com/mods/581251 | "1.6" (build ID not stated) | CC BY-NC-ND 4.0, closed | UNVERIFIED | Changes FPS/timing itself, so likely conflicts with BOTW-PC's timing correction; the installer refuses patches to `VFR::updateInterval` |
| 60 FPS + FMV fix | theboy181 | https://github.com/theboy181/switch-ptchtxt-mods/issues/52 | 1.6.0 (second-hand) | none stated | INCOMPATIBLE by design | Known bugs (cutscene speed, a quest becomes impossible); BOTW-PC's own timing correction replaces it |
| "BotW 1.9.0 – 60 FPS" | unknown | https://www.nexusmods.com/legendofzeldabreathofthewild/mods/119 | 1.9.0 | — | INCOMPATIBLE | wrong game version |
| No-telemetry IPS | ZeldaMods | https://static.zeldamods.org/patch_switch160_no_telemetry.ips (https://zeldamods.org/wiki/Help:Using_mods) | 1.6.0 | not stated | READY TO TEST | IPS loader smoke test |

**Reuse decision:** for performance, BOTW-PC keeps its own time-correct frame-rate code (FRAME_TIMING.md). The
community 60 FPS patches change game speed or have known gameplay bugs. Resolution and FXAA patches are useful optional
user mods.

## Graphics / HUD

| Mod | Source | Class | Notes |
|---|---|---|---|
| Clean UI / ProHud3000 (Eloeri) | https://gamebanana.com/mods/33344 | UNVERIFIED | 2018, so 1.5.0-era; probably touches the same UI files as the prompt overlay |
| BotW HD Texture Pack | https://gamebanana.com/mods/478224 | UNVERIFIED | ~20 GB hosted off-site; "do not redistribute"; needs 8 GB DRAM emulation |
| 32:9 Super Ultrawide | https://gamebanana.com/mods/690060 | INCOMPATIBLE | targets 1.9.0 |
| Simple Camera UI Remover | https://gamebanana.com/mods/49151 | REQUIRES MERGING (`.bnp`) | |

## Quality of life

| Mod | Author | Source | Type, licence | Class | Notes |
|---|---|---|---|---|---|
| Infinite Durability (1.6.0) | pablo67340 | https://gamebanana.com/mods/49134 (source https://github.com/pablo67340/BoTW-Durability-Mod) | romfs `Actor/Pack` (269 weapon packs); CC BY-NC 4.0 | READY TO TEST (loads; effect not yet observed) | Conflicts with any mod that changes the same weapon/shield packs |
| No Loot Boxes | jordanbtucker | https://gamebanana.com/mods/444097 | romfs amiibo drop tables; **MIT** | READY TO TEST (loads; amiibo not testable here) | |
| No Friction Shields (old build) | BruhLookAtThis | https://gamebanana.com/mods/49081 | bare `.sbactorpack` files ("place in romfs/Actor/Pack"); CC BY-NC-ND 4.0 | REQUIRES CONVERSION (folder layout only, per the author's own instruction) | Same 33 shield packs as Infinite Durability |
| Increased Inventory Slots | Catzy123 | https://gamebanana.com/mods/701786 | exefs + `subsdk4`; has 1.6.0 and 1.9.0 variants; CC BY-NC-ND 4.0 | UNVERIFIED | `subsdk` replacement; author warns that uninstalling one part deletes the inventory |
| BetterBowTabs | Kinak | https://gamebanana.com/mods/716783 | pchtxt, 1.6.0 stated; CC BY-NC 4.0 | READY TO TEST | |
| 99 slots, Hylian Homeowner QoL, Pet The Dogs, ALoT | various | GameBanana 305388, 49148, 385298, 49119 | `.bnp` | REQUIRES MERGING | |

## Content and DLC

| Mod | Source | Class | Notes |
|---|---|---|---|
| Better Link House (pablo67340) | https://gamebanana.com/mods/148829 | READY TO TEST (loads in the DLC mount; area not reachable) | `AocMainField.pack` for DLC `01007EF00011F001`; DLC + 1.6.0 required |
| Second Wind (Switch) | https://github.com/Nitr4m12/Second-Wind-Switch | REQUIRES MERGING (BCML/UKMM) | CC BY-NC-ND 4.0; large overhaul, optional |
| Relics of the Past (dev) | https://gamebanana.com/mods/414823 | REQUIRES MERGING / UNVERIFIED | 310 MB map overhaul |
| The Linkle Mod 3.0 port | https://gamebanana.com/mods/505299 | REQUIRES MERGING | licence forbids redistribution |
| MasterCyclePlus, Infinite Master Cycle Fuel, Early Master Cycle | GameBanana 49145, 49146, 321704 | UNVERIFIED | DLC Pack 2 content |
| Trial of the Sword for Weenies | https://gamebanana.com/mods/49118 | REQUIRES MERGING | DLC Pack 1 content |

## Tools

| Tool | Source | Licence | Use in BOTW-PC |
|---|---|---|---|
| UKMM 0.17.1 (2026-07-01) | https://github.com/NiceneNerd/UKMM | GPL-3.0-or-later | **external tool**: merge mods there, import its Switch deploy folder (MODDING.md). No UKMM code is copied into BOTW-PC (MIT). |
| BCML | https://github.com/NiceneNerd/BCML | GPL-3.0-or-later | discontinued; its `.bnp` packages go through UKMM |

UKMM facts relevant to integration (from its book and `src/cli.rs`):
- **Switch input:** an unpacked base + update dump, and the DLC (`Pack/AocMainField.pack`) separately.
- **Deploy layouts:** "without name" gives `<out>/01007EF00011E000/romfs` + `<out>/01007EF00011F001/romfs`; "with name"
  gives `<out>/<tid>/BreathOfTheWild_UKMM/romfs`. BOTW-PC's installer accepts both. Deployment covers romfs only; exefs
  patches stay separate mods.
- **CLI:** `mode`, `install <path> [profile]` (refuses mods with options), `uninstall`, `remerge`, `deploy`,
  `package`. The dump and deploy paths must be configured once in the GUI. A one-click UKMM bridge would therefore
  need the user's unpacked RomFS dump (about 15 GB); not implemented.

## Optional online catalog: decision
GameBanana's public `apiv11` returns metadata, licence checklists and `/dl/<id>` URLs without a login. However, it is
undocumented, and the site's terms say nothing about API use. BOTW-PC therefore does **not** query it automatically.
The Mod Manager imports a file the user downloaded from the author's page; this catalog lists those pages.
