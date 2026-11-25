# Casualties: Unknown — QoL &amp; Original Mods

Source code for the twelve mods shipped in the **Casualties: Unknown QoL Pack**, plus the helper scripts used by the installer.

This repository exists so that anyone — players, other mod authors, or platform reviewers — can read what the mods do and rebuild the exact DLLs from source. Nothing here is obfuscated, packed, or downloaded at runtime.

---

## Contents

| Folder | Mod | Version | Type |
|---|---|---|---|
| `Mods/HealthBarLite` | Health Bar Lite | 1.4.2 | UI |
| `Mods/MiniMapLite` | MiniMap Lite | 1.1.1 | UI |
| `Mods/CrosshairLite` | Crosshair Lite | 1.4.0 | UI |
| `Mods/SerumCN` | Serum CN | 1.1.0 | Localisation |
| `Mods/RshLibCompat` | RshLib (compatibility build) | 6.2.0 | Prerequisite fix |
| `Mods/CuGuard` | CU Guard | 1.0.1 | Crash guard |
| `Mods/TraderTweak` | Trader Tweak | 1.0.0 | Balance tweak |
| `Mods/DietDrug` | Drug Expansion | 21.6.0 | Content |
| `Mods/IlmeniteExtraction` | Ilmenite Extraction | 1.0.1 | Content |
| `Mods/GunPack` | Gun Pack Integration | 1.1.0 | Content |
| `Mods/AmmoPress` | Bulk Ammo Loading | 2.3.0 | Content |
| `Mods/RecipeBan` | Recipe Ban | 1.1.0 | Utility |
| `Installer/Source` | Installer scripts | — | Tooling |

Build instructions: **[BUILD_GUIDE.md](BUILD_GUIDE.md)**.

---

## What each mod does

### Health Bar Lite
Draws health bars over entities and three status bars (brain health, hunger, hydration) for the player.

The two popular alternatives were unusable on game 1.0.2: one scanned every entity every frame (measured 8 FPS), the other read a field that no longer exists and spammed exceptions until the log reached 466 MB. This version resolves members through reflection, tolerating both fields and properties, walks the inheritance chain, and scans at a configurable interval (0.1 s by default). No Harmony patches.

### MiniMap Lite
A draggable minimap with wheel zoom and fog of war.

Dirty-flag rendering (textures rebuild only when the player crosses a tile), 5 Hz refresh ceiling, and a colour lookup table instead of per-tile queries — the original rebuilt a 220×220 texture every frame.

### Crosshair Lite
Four thin strokes and a centre dot, replacing the stock crosshair that is too thick and overlaps the system cursor.

The stock crosshair renderer is located via reflection (`PlayerCamera.gunCrosshair`) and suppressed each frame. The system cursor is hidden only while a gun is held.

### Serum CN
Chinese names and descriptions for Re-Growth Serums, whose text is hardcoded into its DLL with no localisation hook.

Text is rewritten at runtime on `ItemRegistry.RegisteredItems`, and the translations live in an external `translations.txt` so they can be edited without recompiling.

### RshLib (compatibility build)
A drop-in replacement for the **RshLib** gun-mod prerequisite, which is broken on game 1.0.2.

The stock library takes over `Utils.Create` unconditionally and reimplements it, so every mod that builds UI through it ends up with black boxes (save manager, hotbar). This build only intercepts item IDs that were registered by custom mods and returns `true` for everything else, letting vanilla run. Six unrelated patches bundled in the original are not included.

The assembly name and plugin GUID are identical to the original, so New Gun Loader and gun packs recognise it without changes.

Two additions: the item loot pool is rebuilt after registration (the game builds it once at startup, so late-registered items never appear in trader stock or containers), and custom items are weighted ×4 in that pool.

### CU Guard
Enhanced Tools calls `CUCoreUtils.TalkElectronic(dialogue, null)` every frame from `AutoChargerStatus`, throwing a `NullReferenceException` each time. A Harmony prefix skips the call when the item is null. No business logic is changed.

### Trader Tweak
`TraderScript.MAX_VALUE_GIVEN` is a static field fixed at 60, which caps both single-trade and total trader value. This mod rewrites it at startup (300 by default, configurable).

### Drug Expansion
Three consumable drugs, five tier-10 recipes. All results are liquids: the game spawns them into a temporary `craftingbottle` that despawns after 300 seconds.

| Drug | Recipe | Weight lost |
|---|---|---|
| Diet pills | mushpear ×1 + aquapple ×2 + biochem 10 mL | 3.0 kg over 480 s |
| Fat flusher | opiate 10 mL + naltrexone 20 mL + biochem 20 mL + any liquid 50 mL | 3.5 kg |
| Metabolism booster ×3 | energy drink / sports drink / coffee 90 mL + biochem 10 mL | 4.0 kg over 720 s |

Effects are implemented through the game's own systems rather than by writing derived stats:

- the fat flusher calls `body.vomiter.Vomit()`, followed 1.2 s later by `VomitBlood()` (blood in this game is yellow, so it reads as expelled fat);
- the metabolism booster drives `caffeinated`, which the game already uses to halve energy drain and double stamina regeneration, and holds wetness at 24 so evaporative cooling brings core temperature to 35.5 °C;
- weight changes go through `weightOffset` with `weightVisualOffset` kept in sync, because the stock UI panel reads the latter while mood conditions read the former.

Fat oxidation is credited back as hydration, but never above the amount lost to sweat, so net hydration can never rise — an earlier version that inverted this caused water intoxication.

### Ilmenite Extraction
Two crafting steps at levels 9 and 10: scrap metal + biochem fluid becomes ilmenite powder, two powders press into one chunk. Item stats are cloned from the vanilla scrap metal entry so category, qualities and stacking behave identically.

---

## Repository rules

- No third-party mod binaries, game assemblies, or decompiled code are stored here.
- No build output. `bin/` and `obj/` are ignored.
- Third-party dependencies are referenced from the player's own installation at build time and are never redistributed.

See [LICENSES.md](LICENSES.md) for dependency licensing and [CHANGELOG.md](CHANGELOG.md) for version history.
