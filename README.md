# Underwater Horrors

A Vintage Story mod by Soareverix. Deep saltwater is no longer empty: a stalking Sea Serpent, a rarer aggressive Rust Serpent, and a Kraken whose tentacles grab swimmers and drag them down. Sunken ruins, wrecks and a drowned city generate on the sea floor with loot and creature spawners.

Mod DB: https://mods.vintagestory.at/show/mod/42270

## Documents

- `CONFIG.md`: every config setting, its default and what it does. Generated from the source comments by `tools/gen_config_doc.py`.
- In game: the handbook has a guide page, "Guide: Underwater Horrors", and entries for the Sea Serpent, Rust Serpent, Rust Leviathan and Kraken.

## Building

`dotnet build -c Release` builds the dll, zips it with `modinfo.json` and `assets/`, and copies the zip to `%APPDATA%\VintagestoryData\Mods\UnderwaterHorrors_<version>.zip`.

## Changelog

### 0.22.3

- New variant: the Glowing Rust Leviathan. Some 150 patches of cyan bioluminescence grow over its hide like coral on a wreck. They bend with the body, show through the murk and the dark, and four real lights along its spine light the water around it. Three natural giants in ten glow (`GiantSerpentGlowChance`); `/uh spawn giantserpentglow` brings one. A dead one goes dark over 40 seconds.
- The Rust Leviathan's deep shape is lighter: a murky outline a little darker than the water, like a far wreck, instead of a near-black cutout.

### 0.22.2

- The Rust Leviathan's deep shape is its own model now, posed exactly as it is drawn: head, jaws, spikes, fins and tail, in one even dark layer. It used to be a rough tube along the spine.
- It stays in the water. In a strike the mouth comes up to the surface and no further, then it rolls straight over and dives; only the head and the arch of its back break the waves. It looks up to 60 blocks ahead and turns away from shallows and coasts, instead of following the sea floor up a beach.
- When its prey goes ashore or over shallow water, it swims off into the deep, and comes back if they return to deep water. It is only removed once its whole body is out of sight of every player, or after its prey has been ashore for two minutes (`GiantSerpentDespawnAfterLandSeconds`, default 120). It used to vanish 30 seconds after you stepped ashore.

### 0.22.1

- The Rust Leviathan is five times a rust serpent's size now (about 220 blocks), not ten.
- It swims smoothly. Its position is dead-reckoned between server updates instead of using the stock interpolation, which assumed packets 1/15 s apart and stuttered on the 30 a second this entity gets. Over an uneven sea floor it eases up and down instead of snapping to the floor height, and the swimming wave no longer twitches the segments against each other.
- It shows deep down: a dark, soft-edged shape where the water would hide it, from above the surface and through the underwater murk.

### 0.22.0

- New creature: the Rust Leviathan (`giantserpent`), a rust serpent ten times the size, about 440 blocks from jaw to tail. Very rare: one natural serpent spawn in fifty (`GiantSerpentSpawnChance`). `/uh spawn giantserpent` brings one 100 to 140 blocks out.
- It swims like a snake: its body follows the path its head took, with a side-to-side swimming wave, instead of swinging behind it as one keyframed piece. The model is the rust serpent's own, re-rooted at the mouth by `tools/giantserpent/build_shape.py`.
- It never surfaces to hiss or show its fins. It circles far below its prey, dives, then rises mouth first and breaches. It swallows a raft in one gulp, breaks a sailboat in three bites (`GiantSerpentBitesToSinkBoat`) and drops the crew in the water with some damage (`GiantSerpentRiderDamage`). Swimmers take a 20 damage bite. It strikes only over water at least 20 blocks deep, so the shallows still save you.
- 1500 health. Its carcass sinks, keeps its last curve, and harvests for six times a rust serpent's loot, with a trail of bone piles along its body.
- New config section "Giant serpent" (see `CONFIG.md`).

### 0.21.0

- Per-spawner settings for the serpent and kraken spawner blocks. In creative, shift + right click a spawner to edit them: trigger radius (0 = the config's `SpawnerTriggerRange`), whether the player must be in the water, and how long the serpent stays (instead of retreating) while its target stands in shallow water or on land, as long as they are within a given range. Looking at a spawner in creative shows its settings. They stay with the spawner through every spawn and return.

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
