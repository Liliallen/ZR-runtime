# In-game button prompts (Xbox glyphs)

The game draws Switch glyphs. BOTW-PC redraws them so that **every prompt shows the physical Xbox button that
produces that action under the active mapping**. Nothing is hard-coded per layout: the glyph for a Switch button is
the glyph of the controller input bound to it in the Player 1 configuration that actually runs.

```
Physical input (SDL, positional: A = bottom)          Player 1 config (layout, custom bindings,
        │                                              Ryujinx input settings, auto-assignment)
        ▼                                                      │
StandardControllerInputConfig  ──────────────► game input      │  same object
        │                                                      ▼
        └── ButtonPrompts.FromConfig ──► PromptMapping (Switch button → physical input)
                                              │
                                              ▼
                     PromptOverlay: redraw glyphs in the user's own font/layout files → RomFS overlay
```

## Where BotW draws prompts (v1.6.0, found by decoding the files)

| Where | File | What BOTW-PC changes |
|---|---|---|
| All text: dialogue, tips, tutorials, menus, pause/inventory bar, context actions, loading tips | `Font/Font_*.sbfarc` → `External_00.bffnt`: private-use glyphs U+E040–E08F on one 128×1024 BC4 sheet (stored bottom-up) | 17 glyphs: A B X Y L R ZL ZR + − D-pad (all / ↑ ↓ ← → / ↑↓ / ←→); stick-press glyphs only if L3/R3 are remapped |
| HUD action icons (`PaDoCommandA/B/X/Y/L`, NN key variants) | `Pack/Bootup.pack` → `Layout/Common.sblarc` → `timg/__Combined.bntx`: `Nt_KeyTex{A,B,X,Y,L,ZL}_00^d` (R8) | redrawn |
| HUD action diamond: one-letter names of the three idle face buttons | `PaDoCommand{A,B,X,Y}_00.bflyt` text panes `T_<letter>_0n` (literal "B"/"Y"/"X", font NormalS) | letter rewritten in place to the bound Xbox button (one character fits; longer names show `?`) |
| D-pad shortcut icon (`PaShortCutIconCrossNN`) | same archive: `ButtonIconL46NN_00^d`, `_01^s`, `ButtonIconL46ShCu_00^s` | Xbox-style cross (+ matching soft glow) |
| +/− on loading-tip pictures (`PaMessageTipsDrcImageNN`) | `Nt_ControllerNN{Plus,Minus}_00^s` | Menu / View icons |

Glyph code points: A E040, B E041, X E042, Y E043, L E044, R E045, ZL E046, ZR E047, power E048, D-pad E049,
Home E04A, + E04B, − E04C, stick moves E050–E05F (E056/E057 = press L/R stick), D-pad directions E060–E065,
console E066, settings E067, arrows E083–E08E, × E08F.

## Glyph table

| Switch button (logical) | Position layout (default) shows | Label layout shows |
|---|---|---|
| A | Xbox **B** | Xbox **A** |
| B | Xbox **A** | Xbox **B** |
| X | Xbox **Y** | Xbox **X** |
| Y | Xbox **X** | Xbox **Y** |
| L / R | LB / RB | LB / RB |
| ZL / ZR | LT / RT | LT / RT |
| + / − | Menu (≡) / View (two windows) | same |
| D-pad, D-pad directions | Xbox cross; the pressed arm solid, the others outlined | same |
| L3 / R3 (stick press) | game's own stick-press glyph (already reads "press left/right stick") | same |

With custom bindings the table follows the bindings, e.g. `X → A` shows the Xbox A glyph wherever the game asks for
Switch X. A Switch button without a binding shows a **?** glyph, never a wrong button. Remapping a stick press to a
button redraws that stick-press glyph. Paddles (P1–P4), Share and Guide have their own glyphs for such bindings.

BotW's own control options (e.g. swapping jump) change *which Switch button* the game asks for; the game then
shows that Switch button's glyph, which BOTW-PC has already replaced, so the two never contradict each other.

## Artwork and legal
All Xbox glyphs are drawn procedurally (`GlyphArt.cs`: circles, rounded shapes and a small stroke font) — original
artwork, no Nintendo, Microsoft or third-party mod assets. The patched files are generated at launch from the user's
own game files into `<data>/botw/prompts/<key>/romfs/` (local cache, never in the repository).

## When prompts are applied

| Setting (`<data>/botw/settings.json` → `controller`) | Effect |
|---|---|
| `prompts`: `Auto` (default) | Xbox glyphs when Player 1 is a game controller that is not Nintendo / PlayStation branded |
| `prompts`: `Xbox` | Xbox glyphs for any game controller |
| `prompts`: `Original` | never change the game |
| `prompt_priority`: `BuiltIn` (default) / `Mod` | who wins when a RomFS mod also changes these glyphs (see below) |
| `BOTWPC_PROMPTS=0` | environment kill switch |
| `BOTWPC_SAFE_MODE=1` / safe mode | prompts still apply (they are not a mod); use `prompts: Original` to disable |

Keyboard Player 1 keeps the original glyphs.

The overlay is generated when the game starts (≈1.6 s the first time). The cache key is the generator version, the
prompt priority, the full mapping (controller profile + bindings) and a SHA-256 of every source file as the game sees
it after mods (so a game update, DLC change or UI mod change rebuilds it). An entry is only marked complete
(`overlay.txt`) when every source was patched; a partial build is used once and regenerated at the next start.
**Settings > BotW-PC > Rebuild prompt glyphs** invalidates all entries (only the markers are removed, so a running game
keeps its files). **Changing the mapping while the game runs does not change the glyphs already loaded** (the game
reads its fonts and HUD archive at boot): the log then says `the controller mapping changed; ... Restart the game to
update them.` (`ButtonPrompts.Stale`), and the controller test window shows the same notice.

## Mods
The overlay is built from the files the game would load *after* RomFS mods, so a mod that ships its own
`Pack/Bootup.pack` (e.g. UKMM merged output) keeps all its changes and only the listed textures are redrawn on top.
If a mod changes the same glyph textures/font (an Xbox/PlayStation prompt mod), this is reported as a conflict in the
log; `prompt_priority: Mod` keeps the mod's glyphs for that file, `BuiltIn` draws BOTW-PC's glyphs over them so the
prompts stay consistent with the mapping. Within `Bootup.pack` only `Layout/Common.sblarc` changes; its decompressed
size is unchanged, so the resource size table needs no edit. Files that grow move later files in the pack by
multiples of 0x2000 bytes to keep their alignment.

## Verification

| Test | Result | Evidence |
|---|---|---|
| Unit tests (`ButtonPromptTests`): position/label layouts, no double swap, custom bindings, bad bindings, keyboard/Nintendo/PlayStation keep originals, font plan uses the bound input, unbound → ?, distinct glyphs, Yaz0, SARC grow/shrink/alignment, block-linear, BC4 | 12/12 pass | `dotnet test --filter BotwPc` (50 total) |
| Offline: overlay from the user's 1.6.0 files; decoded patched font sheet and HUD textures; every other texture in `__Combined.bntx` and every other file in `Bootup.pack` byte-identical | pass | local only (`scratchpad`) |
| **In game, Position layout**, real USB Xbox Series X pad auto-assigned: pause menu bar shows "返回遊戲 (back) [Xbox A]" and "決定 (confirm) [Xbox B]"; controls page labels jump = Y, action = B, cancel = A; target = LT; HUD "操作說明 (controls) [Menu ≡]" | pass | `bench/prompts-1` screenshots (local, game window only) |
| **In game, Label layout**: "返回遊戲 [B]", "決定 [A]", jump = X, action = A | pass | `bench/prompts-label` |
| **In game, HUD action diamond** (Position layout) at the Sheikah Slate pedestal: active icon "[B] 調查 (examine)", idle buttons Y (top), X (left), A (bottom), i.e. the Xbox face layout. Before the label fix the idle buttons still read the Switch letters X / Y / B | pass | `bench/explore4` (before), `bench/explore8` (after) |
| D-pad shortcut icon, loading-tip ± icons in game | **not seen yet**; verified offline only | — |
| A person pressing the shown physical button gets the action | **NOT TESTED by a person**. The prompt and the input come from the same config object, so they cannot disagree; SDL's positional naming of Xbox buttons is the remaining assumption (verified earlier for detection only) | — |

## Known limitations
- The controller **illustrations** (controls page, tips: `Nt_NNillust_*`, `Nt_GamePadIllust*`) still draw Switch
  hardware with tiny Nintendo letters on the face/shoulder buttons. The labels next to them are correct Xbox glyphs.
- Glyphs change only at game start (see above).
- The diamond's idle-button names hold one character: a Switch face button bound to LB, a paddle etc. shows `?` there
  (the active icon itself is a texture and shows the full glyph).
- Home (E04A) and the stick-move glyphs (E050–E05F) are unchanged (Home is not bound on Xbox; stick moves are the same).
- Only English (US/EU), Japanese, Chinese and Korean font archives were checked; all six `Font_*.sbfarc` contain the
  same `External_00.bffnt` layout and were patched in the test run.
