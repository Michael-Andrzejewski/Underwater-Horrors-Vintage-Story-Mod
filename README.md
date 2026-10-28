# Underwater Horrors

A Vintage Story mod by Soareverix. Deep saltwater is no longer empty: a stalking Sea Serpent, a rarer aggressive Rust Serpent, and a Kraken whose tentacles grab swimmers and drag them down. Sunken ruins, wrecks and a drowned city generate on the sea floor with loot and creature spawners.

Mod DB: https://mods.vintagestory.at/show/mod/42270

## Documents

- `CONFIG.md`: every config setting, its default and what it does. Generated from the source comments by `tools/gen_config_doc.py`.
- In game: the handbook has a guide page, "Guide: Underwater Horrors", and entries for the Sea Serpent, Rust Serpent, Rust Leviathan and Kraken.

## Building

`dotnet build -c Release` builds the dll, zips it with `modinfo.json` and `assets/`, and copies the zip to `%APPDATA%\VintagestoryData\Mods\UnderwaterHorrors_<version>.zip`.

## Changelog

### 0.25.0

- Carcasses are harvested the game's way: hold shift and right click with a knife, then take the loot from the carcass dialog. A dead serpent is harvested from any point on its body, as one carcass; the dialog stays with the part you clicked. Emptied, the carcass goes and leaves its bones on the sea floor.
- The Rust Leviathan's 22 segments are each harvested on their own, holding the loot they used to spill when the body broke apart (the `GiantSerpentPiece...` settings). An emptied segment crumbles into bones. `GiantSerpentLootMinutes` is gone, since nothing is dropped loose any more.
- The segments keep the leviathan's glow: pale cyan spots baked into their shapes (`tools/giantserpent/build_shape.py`).
- Looking at any of the leviathan's hitboxes shows its name and health, in every game mode. The serpent writes its health onto its hitboxes, so this works even when its mouth is beyond your view of entities.
- Giant anglerfish:
  - 100 health instead of 200. Killed, it sinks onto the floor and is harvested like a carcass: 20 to 40 raw fish, 10 to 20 bones and a temporal gear.
  - Its gear shows its 10 health to whoever looks at it. Broken, it drops one or two temporal gears, and the fish flees at 30 blocks a second instead of 10.
  - A player right over its back, led there by the lure, gets a strike from below however deep the fish lies: it rises, faster for a long rise, strikes and goes back down.
  - At night a player standing on land by deep coastal water may get one lying off the shore. About once a minute (`AnglerfishShoreCheckSeconds`) each such player is checked: with `AnglerfishShoreSpawnChance` (25%) up to four columns of water around them are tested, one scan down each, and the fish is laid in the first deep enough. Between checks it costs one lookup. An anglerfish now stays while its target is within reach of its lure, ashore or not.
- Kraken:
  - Each arm is 36 pieces instead of 96: three of the old 0.84-block trunks fused into one 2.53-block piece (`tools/kraken/build_glow_parts.py`), as the anglerfish's line uses long pieces. A kraken is 297 entities instead of 777. Measured with Pilot's kraken_perf in steady state, a kraken now costs the server about 0.05 ms a tick instead of 1.0 to 1.5, and about 63 KB/s of network instead of about 165.
  - A piece that has hardly moved since its last update is not updated again, so the server sends nothing for it.
  - Glowing spots like the anglerfish's, on its arms (three patterns, so the arm does not look striped) and its body.

### 0.24.3

- New admin command `/uh spawn anglerfish coast` lays an anglerfish off the nearest coast, the way a natural one comes, for testing coastal spots.
- The giant anglerfish lies on the sea floor off steep coasts too. On a slope its long body only lies down at some headings, so it now turns toward a distant player only as far as it can still rest on the floor, instead of lifting off and hanging in the water half turned. It still lifts off to face someone beside it or prey nearly in reach, and lies back down along the slope once they leave. A fish waiting on a turn no longer counts as creeping, so it settles meanwhile.

### 0.24.2

- The Rust Leviathan flees again when badly hurt, but only for a while. Below a quarter of its health (`GiantSerpentFleeHealthFraction`) it swims off to 200 blocks from its prey (`GiantSerpentFleeDistance`) and circles there, then after a minute (`GiantSerpentFleeSeconds`) comes back and fights to the death. It flees once in its life, waits for a strike under way to finish first, and never goes far enough to be removed: the flee distance stays under 80% of `DespawnMaxDistance`.

### 0.24.1

- The Rust Leviathan has 1500 health again (`GiantSerpentMaxHealth`; a config still at 0.23.0's 600 moves up once). Giants already out keep the health they came with.

### 0.24.0

- Temporal storms make the sea deadly. While a storm rages (new config section "Temporal storms"):
  - The chance of nothing spawning is divided by five (`StormSpawnOddsFactor`), for the spawn roll and the extra-creature roll: a 10% roll becomes 82%, a 75% roll 95%. A roll set to 0 stays off. Which creature comes keeps its usual odds.
  - Serpents come in water only 15 blocks deep (`StormMinSaltwaterDepth`), so they can find you near the shore as long as there is room to swim. The kraken and the anglerfish still need the usual 50.
  - Every serpent that spawns is a rust serpent (`StormRustSerpentsOnly`), and every serpent moves 1.5 times as fast (`StormSerpentSpeedMultiplier`). Kraken tentacles move 3 times as fast (`StormTentacleSpeedMultiplier`), dragging included.
  - Up to five creatures live at once in the whole world, a hard cap (`StormMaxLivingCreatures`): nothing spawns past it, not even the rare extras.
  - The Rust Leviathan comes for one serpent spawn in four instead of one in twenty (`StormGiantSerpentSpawnChance`). It still needs open ocean, keeps its two-hour wait after a kill, and behaves as always.
- The storm rules follow the game's own storm, including storms another mod lengthens. `/uh stormfrenzy on|off|auto` forces them for the session; `/uh status` shows them.

### 0.23.0

- The Rust Leviathan can be killed. Its hitboxes now follow the model: 49 boxes from the snout tip (almost 9 blocks past the mouth, which no box reached before) to the tip of the tail fin, each as wide and tall as what is drawn there: the body with its frills, side fins, dorsal spikes and tail fin, but not the whiskers. They run from 4 blocks wide at the snout and 27 across the side fins to 2.5 at the tail tip. They used to be 37 boxes all 7 by 7, which missed the fins, the snout and the end of the tail. Each box gets its size on the client too, which Combat Overhaul tests its swings and arrows against, and clients move the boxes with the body's own smoothing, so they keep up with it in a strike. The box table is measured from the model by `tools/giantserpent/build_shape.py`.
- It fights to the death: it no longer flees at a quarter health, and it does not swim off at the end of its stay (`GiantSerpentMaxStaySeconds`) while a player has hurt it in the last two minutes.
- 600 health instead of 1500 (`GiantSerpentMaxHealth`; a config still at 1500 moves down once). It shows itself only for the 20 seconds or so its body takes to pass the surface after each strike, and a sailboat lasts three strikes, so 1500 could not be finished in practice.
- Killed, it goes limp for two seconds, then breaks apart with a crack of rusted metal into its 22 body segments, each where it lay: the head with its jaws and teeth, every neck and tail segment with its own fins and spikes. The segments drift apart, sink, roll onto a side or their back and come to rest on the sea floor. After 48 in-game hours each crumbles into a pile of bones (`GiantSerpentPieceDecayHours`).
- Every segment spills its share of the hoard: 10 to 15 rusty gears, 1 to 4 temporal gears, 1 to 3 of each of the seven Jonas parts, 20 to 50 raw fish and 20 to 50 bones (the `GiantSerpentPiece...` settings). Fish and bone float up; gears and Jonas parts sink with the segments. The loot lasts 60 minutes (`GiantSerpentLootMinutes`) instead of the game's 10. The knife harvest is gone for the giant.
- After a player kills one, no natural Rust Leviathan comes anywhere on the server for two hours of real time (`GiantSerpentKillCooldownMinutes`). The time is kept in the save, so it counts on through a restart. `/uh giantcooldown` shows the time left and `/uh giantcooldown clear` ends it; `/uh spawn giantserpent` ignores it.
- A new entity type, `giantserpentpiece-<bone>`, with its 22 shapes in `shapes/entity/giantserpentpiece`.

### 0.22.5

- The Rust Leviathan only comes out in the open ocean: the player's own column and four random columns within 50 blocks must all be saltwater at least 50 deep (`GiantSerpentOpenOceanRadius`, `GiantSerpentOpenOceanDepth`). There, one natural serpent spawn in twenty is the giant (`GiantSerpentSpawnChance`, now 0.05; a config still at the old 0.02 is moved up once).
- Every Rust Leviathan glows now. The plain variant, `GiantSerpentGlowChance` and `/uh spawn giantserpentglow` are gone; `/uh spawn giantserpent` brings the glowing one.
- Its bite has a sound of its own: the rust serpent's bite without the silent lead-in, so it lands about half a second sooner, with a softer attack and a longer, echoing tail.
- A boat it sinks loses its cargo: crates, baskets and bags aboard are destroyed and what they held spills into the sea. Light goods such as wood and fiber float; metal and stone sink. The game on its own drops such a boat's crates whole, contents sealed inside.

### 0.22.4

- The Glowing Rust Leviathan's patches sit only on opaque parts of its hide. Dots used to land on see-through texels of the fins and whiskers, and on the edges past a face, and floated in the water beside the body. Each dot is now placed on its own face and checked against the texture.
- The Rust Leviathan's bite and other sounds play deeper and carry further (`GiantSerpentSoundPitch`, default 0.6). Its jaws also snap audibly on a near miss, such as the deliberate miss a creative player gets.

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

### Anglerfish encounter (working build)

- A giant anglerfish, about 100 blocks long with a head some 35 tall and 30
  wide (entity size 6). `/uh spawn anglerfish` lays one on the floor in
  nearby deep water, facing you; `/uhangler show` places a still model.
- It lies off coasts like a wobbegong. At night, now and then, a natural
  spawn is the anglerfish (`AnglerfishNightSpawnChance`, default 0.05): it
  finds the nearest coast within `AnglerfishCoastRange` (160) of the player
  and lies on the floor in the first water off it at least
  `AnglerfishMinWaterDepth` (40) deep. A natural one sinks back into the
  deep at daybreak once nobody is near it.
- It creeps along the floor toward its prey, over slopes and among blocks,
  but never into water too shallow for it and never far from its lair. It
  turns to face any player within 15 blocks of its body, ashore or afloat.
- Its lure is a pole on its forehead with an upscaled temporal gear at the
  tip. With nobody to bait the gear sits docked on the pole; otherwise it
  plays out on a rope. For a player at the surface, in a boat or on the
  shore it hangs 2.5 blocks above the waves between them and the fish, and
  never past it, so following it leads them over the mouth. A diver is led
  on down underwater.
- Once its prey is within 28 blocks of its mouth (`AnglerfishStrikeRange`),
  or that far sideways and up to 60 above it (`AnglerfishStrikeReachUp`),
  it swoops at 40 blocks a second (`AnglerfishStrikeSpeed`) and gulps for 50
  damage at armor tier 3 (`AnglerfishBiteDamage`, `AnglerfishDamageTier`).
  It only strikes prey roughly ahead of it, turning first, and the bite
  clip is timed to snap shut as the swoop arrives.
- The gear has 10 health. Any hit startles the fish: it reels the gear in
  and swims off into deeper water, away from the attacker, and is removed
  once nobody is within 128 blocks (or after a minute). Breaking the gear
  drops two temporal gears at the attacker's feet. It also flees once it is
  down to a third of its health.
- Fixed: the anglerfish and its gear could stay invisible while it still
  bit. Its first ticks started a clip while the server wrote its spawn
  packet on another thread; that threw "Collection was modified" and the
  client never got the fish. Its animations now go through a locked manager
  (`ThreadSafeAnimationManager`), as do the Rust Leviathan's.
- The model: a gigantic deep-sea angler after MikeZilla_'s rough and
  Soareverix's sketch, with rust-brown hide, faint glowing cyan patches, a
  jutting jaw of crooked fangs, milky eyes that glow faintly in the dark,
  and spiny fins. See `art/anglerfish/README.md`.

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
