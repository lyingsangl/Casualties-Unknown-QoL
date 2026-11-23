# Changelog

Versions follow the plugin versions in `[BepInPlugin]`, which are independent per mod.

---

## Drug Expansion (DietDrug)

### 7.5.0
- Recipes: water requirements changed from the specific `water` item to the **liquid** quality (`cqwater`), matching how vanilla medicine recipes ask for volume of any liquid.
- Diet pills no longer use water at all.

### 7.4.1
- Diet pill recipe: `mushpear ×1 + aquapple ×2 + biochem 10 mL` (was opiate + mushpear + biochem + water).
- Fixed a build where the recipe log string had been updated but the item list had not.

### 7.3.0
- Diet pill thirst drain halved (0.15 → 0.075/s).
- Wetness target raised 20 → 24, keeping a 6-point margin below the shiver threshold at 30.
- Fat flusher now follows its vomit with `VomitBlood()` after 1.2 s (blood is yellow in this game, reads as expelled fat).

### 7.2.0
- Fat fraction of weight loss set to 0.25 (beast-folk body composition: more fur, claw and bone mass, less fat).

### 7.1.0
- Fat oxidation water is now capped by actual sweat loss, so net hydration can never rise.
- Diet pill recipe dropped opiate: mushpear is itself the opium source.

### 7.0.0
- Weight loss rebalanced to 3.0 / 3.5 / 4.0 kg per dose.

### 6.9.0
- Sweat target exposed as `WetnessCap`.

### 6.8.1
- Fat water recovery percentage exposed and hard-capped at 100 % of sweat loss.

### 6.8.0
- Sweat water model: evaporation (from latent heat of vaporisation) plus wet-film accumulation.

### 6.7.0
- Added an alternative model that inverts the vanilla wetness formula to an equivalent body temperature; selectable in config.

### 6.6.1
- Fat water now limited relative to actual sweat output.

### 6.6.0
- Water loss per second derived from wetness instead of a flat constant.

### 6.5.0
- Weight loss doubled, since vanilla food adds weight much faster than the drugs removed it.

### 6.4.0
- Thirst drain reduced (diet 0.5 → 0.15, booster 0.9 → 0.35), hunger drain 0.10 → 0.04.
- Config sanitiser no longer forces low values back up to 0.9.

### 6.3.0
- `weightVisualOffset` synced to `weightOffset` every tick so the UI panel matches real weight.

### 6.2.0
- Stopped per-frame visual-offset writes (panel froze because the game refreshes it in coarse steps).

### 6.1.0
- Added `weightdbg` console command.

### 6.0.0
- **Fixed water intoxication**: v5.5 had set sweat drain to 0 while keeping fat oxidation water at +0.35/s, giving a net gain of +0.35/s.
- Repeated doses no longer stack effects (one coroutine per drug).
- Config sanitiser corrects stale values from older config files.

### 5.8.0
- Overdose detection restored (re-dosing during an active effect, or a single dose over 150 mL).

### 5.4.0
- Metabolism booster recipe dropped the water filler.

### 4.0.0
- Rewritten around the game's `LiquidType.onDrink` instead of a `DrinkLiquid` Harmony patch.
- Vomiting uses `body.vomiter.Vomit()`.

### 3.x and earlier
- Initial recipes, liquids and effect tuning; several registration bugs fixed (see below).

---

## RshLib (compatibility build)

### 3.3.0
- Custom items are weighted ×4 in the loot pool.

### 3.2.0
- Item loot pool is rebuilt after registration, so custom guns can appear in trader stock and containers.

### 3.0.0
- Rewritten to intercept only registered custom item IDs instead of taking over `Utils.Create` from every caller.
- Removed six unrelated patches bundled in the original.

---

## Fixed across the pack

Registration and reliability issues found while building the content mods:

| Symptom | Cause | Fix |
|---|---|---|
| Recipes never appeared | item table is empty at the main menu; the old code waited 20 s and gave up | wait indefinitely with `WaitForSecondsRealtime` |
| Liquid recipes produced 1 mL | `amount` controls loop count; the volume lives in `resultCondition` | `amount = 1`, `resultCondition = 100` |
| Liquid names showed raw IDs | custom liquids do not create locale entries | register into `LocaleRegistry` section `other` |
| Quality requirements showed `cqopiate` | `CraftingQuality` names carry no `cq` prefix | use `"opiate"` |
| Player configs kept old defaults | BepInEx never updates an existing cfg | validate and correct values at startup |
| Trader stock ignored custom guns | `ItemLootPool` is built once at startup | rebuild the pool after registering items |
