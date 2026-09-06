# Underwater Horrors

A Vintage Story mod by Soareverix. Deep saltwater is no longer empty: a stalking Sea Serpent, a rarer aggressive Rust Serpent, and a Kraken whose tentacles grab swimmers and drag them down. Sunken ruins, wrecks and a drowned city generate on the sea floor with loot and creature spawners.

Mod DB: https://mods.vintagestory.at/show/mod/42270

## Documents

- `CONFIG.md`: every config setting, its default and what it does. Generated from the source comments by `tools/gen_config_doc.py`.
- In game: the handbook has a guide page, "Guide: Underwater Horrors", and entries for the Sea Serpent, Rust Serpent and Kraken.

## Building

`dotnet build -c Release` builds the dll, zips it with `modinfo.json` and `assets/`, and copies the zip to `%APPDATA%\VintagestoryData\Mods\UnderwaterHorrors_<version>.zip`.

## Changelog

### 0.20.0

- Fixed a worldgen crash. Ghostlights placed outside the chunk column being generated were scheduled for a light bake that chunk could not do, which threw inside the game's chunk illuminator and aborted the whole TerrainFeatures pass for that column, taking every other mod's features with it. Lights outside the column are now re-lit when a player arrives, and the ruin generator is guarded so a bad ruin can never stop the pass again.
- Ruin loot cut to about a third for the big structures. A huge wreck now defaults to 10 chests and 8 ingot piles (was 30 and 24), the city to 16 and 6 (was 49 and 18). Gold piles are 6 to 12 ingots (was 24 to 32). Existing configs still holding the old defaults are moved to the new ones once.
- Ingot piles are rolled from the current config when a player first comes near the ruin, not when the chunk generates. Changing the loot config now affects every ruin nobody has reached yet.
- Ghostlights are full cubes now instead of a small floating orb, so ruins read as lit stonework rather than glowing dice in the water.
- New `SerpentCorpseFloats` setting (off by default): on, a killed serpent rises to the surface so a boat kill can be harvested from the boat. Serpents still sink unless a server turns it on.
- Creative and spectator players are ignored by default, as the 0.12 changelog said. Existing configs are moved once.
- The surface screech is quieter and fades further (volume 1.0, edge factor 0.2; was 1.5 and 0.5). Existing configs at the old values are moved once.
- A tentacle stalling next to a boat now orbits at 7 blocks instead of 4, clear of the hull.
- New `AllowFreshwaterSpawns` setting lets deep lakes host serpents. Ruins stay in the ocean.
- Handbook: a guide page plus entries for the Sea Serpent, Rust Serpent and Kraken.
- Removed the leftover `giantshiver` test entity and the `/spawn giant-shiver` command.
- Added `CONFIG.md`, a full config reference.
