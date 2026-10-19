# Testing with real saves (without touching them)

Automated runs never use a save in place. They run on a disposable copy, and the originals are hashed before and
after.

## Saves on this machine (2026-10-14)

| Location | Owner | Use in tests |
|---|---|---|
| `~/.var/app/io.github.ryubing.Ryujinx/config/Ryujinx/bis/user/save` | Flatpak Ryubing (original) | read only, hashed |
| `~/.local/share/BOTW-PC/bis/user/save` | the user's BOTW-PC install (imported from the Flatpak, then played) | read only, hashed; **source of the progressed fixture** |
| `../devdata`, `../standalone-test` | development copies of the opening save | free to use |
| `../progress-test` | disposable copy of the progressed save | all outdoor tests; reset before every run |

BotW 1.6.0 keeps four autosave slots per user (`0000000000000001/<user>/<0..3>/game_data.sav`). In the progressed copy,
slots 1 to 3 are newer than slot 0, which is still the opening save.

## Progress in the copied save (decoded, not guessed)
`game_data.sav` is a list of 8-byte entries `(crc32(flag name), value)` after a 12-byte header. The flags were read with
`zlib.crc32` names; `PlayerSavePos` is a float3:

| Slot | Position | Flags set |
|---|---|---|
| 0 | (-1130, 237, 1914) | `Location_StartPoint=2` (opening) |
| 1–3 | (-1025, 250, 1820) | `Open_StartPoint=1`, `IsPlayed_Demo103_0=1` (left the Shrine of Resurrection), `Location_StartPoint=3` |

No paraglider (`IsGet_PlayerStole2`), runes or towers are set, so this save reaches **the Great Plateau outdoors only**.
It has no motion shrine, no Divine Beasts and no DLC areas. The save was created by the user playing in BOTW-PC itself,
which also covers taking the Sheikah Slate and leaving the shrine.

## Procedure
1. `find <save dir> -type f -exec sha256sum {} +` → `bench/save-hashes/*.before.sha256` (local, not committed).
2. Copy with the launcher's copy-only import: `Ryujinx --botw-setup -r ../progress-test --import ~/.local/share/BOTW-PC
   --import-saves --game … --update … --dlc …`. It never overwrites existing files. The copy was verified identical to the
   hash list.
3. Keep a pristine snapshot of the copied save directory (`bench/save-hashes/progress-fixture-save`) and restore it
   before every run. Autosaves made during a test only change the copy.
4. Run: `BOTWPC_DATA=../progress-test BOTWPC_AUTO_CONTROLLER=0 BOTWPC_INPUT_SCRIPT=botw/bench/<scenario>.input
   botw/scripts/run.sh <s> <label>`. Use keyboard as Player 1 (`BOTWPC_AUTO_CONTROLLER=0`) so a physical controller in
   use elsewhere cannot steer the test.
5. Afterwards, compare the originals with the hash lists.

## Scenarios on the progressed save (`botw/bench/`)
| Scenario | What it does |
|---|---|
| `outdoor.input` | Continue, walk, sprint and jump, fast camera pans, open the map, open the inventory |
| `pan.input` | Stand still and turn the camera right, then left (DLSS motion checks) |
| `durability.input` | Walk to a tree and swing the equipped Tree Branch (durability mod check; see MOD_COMPATIBILITY.md) |
| `scope-motion.input` | Open the scope (R3), then aim it only with virtual motion |

## Pitfall found
A test with the Xbox pad auto-assigned picked up input from a person using the same controller, while that person
played their own BOTW-PC session at the same time (SDL delivers controller events to every process). Such runs are
discarded. Benchmarks taken while another game instance runs are also invalid (shared CPU/GPU).
