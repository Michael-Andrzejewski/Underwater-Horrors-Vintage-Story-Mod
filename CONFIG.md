# Underwater Horrors config reference

File: `VintagestoryData/ModConfig/UnderwaterHorrorsConfig.json`. Created with defaults on first run. Edit it with the server stopped, or use the `/uh` commands where one exists and the mod writes the file for you.

Generated from the source comments by `tools/gen_config_doc.py`; do not edit by hand.

Properties ending in `Migrated` or `Applied` are one-shot upgrade markers. Leave them alone.

| Setting | Type | Default | What it does |
|---|---|---|---|
| **Debug** | | | |
| `DebugLogging` | bool | `false` | Sends the mod's debug messages to chat. /uh debug sets it. Noisy, so leave it off in normal play. |
| `DebugLoggingResetApplied` | bool | `false` | One-shot migration flag. Earlier mod versions shipped with DebugLogging=true by default, which spammed chat for everyone. The first load after upgrading flips DebugLogging to false and sets this flag to true; from then on we never touch DebugLogging again, so /uh debug on (or hand-editing the config) sticks across restarts. |
| `GlowDebugActive` | bool | `false` | Debug view from /uh glow: all the mod's creatures at full brightness. Kept across restarts. |
| `SpectralDebugActive` | bool | `false` | Debug view from /uh spectral: outlines through blocks around the mod's creatures and spawner blocks. Kept across restarts. |
| **Spawn system** | | | |
| `SpawnCheckIntervalSeconds` | float | `5f` | How often (seconds) each player in deep water is rolled for a spawn. |
| `MinSaltwaterDepth` | int | `50` | Water under the player must be at least this deep for anything to spawn. Counted straight down from the surface; 50 is open ocean. |
| `AllowFreshwaterSpawns` | bool | `false` | Normally only saltwater counts, so a big inland lake never grows a serpent. Turn this on and any water deep enough (MinSaltwaterDepth) qualifies, fresh or salt. Ruins still generate only in the ocean. |
| `SpawnChancePerCheck` | float | `0.1f` | Chance (0 to 1) that one spawn check produces a creature. At 0.1 and a 5 second interval a swimmer meets something within about a minute. |
| `MaxLivingCreatures` | int | `1` | Global soft cap on living serpents/krakens. Once this many are alive in the world, normal per-player spawning stops; instead at most ONE rare extra spawn is rolled per spawn check across the whole server (see OverCapSpawnChance). This keeps a second serpent very rare even with many players in the ocean or in boats, rather than one per player. |
| `OverCapSpawnChance` | float | `0.002f` | Single global per-check chance to spawn an extra creature while already at MaxLivingCreatures. One roll per check regardless of player count, so the rate does not scale up on busy servers. Very low on purpose. |
| `KrakenNaturalSpawnEnabled` | bool | `true` | Master switch for kraken in NATURAL spawn checks. /uh spawn kraken always works; this just gates the random per-player spawn roll from picking a kraken. The kraken is a heavyweight encounter (one body plus 8 tentacles, each with a chain of 36 pieces, roughly 300 entities) so it is kept rare via the day/night chances below and can be turned off entirely here if a server or client struggles near it. |
| `KrakenSpawnTuningApplied` | bool | `false` | One-shot migration flag: configs written before natural kraken spawning was enabled by default carry the old false value. The first load flips KrakenNaturalSpawnEnabled to true and sets this flag, after which user tuning is left alone. |
| `KrakenSpawnChanceDay` | float | `0.05f` | Chance that a successful natural hostile spawn is a kraken instead of a serpent. Rolled once per spawn, so serpent frequency is barely affected: 0.05 means 1 in 20 hostile spawns is a kraken by day, 0.10 means 1 in 10 at night. "Night" reuses the same DayKrakenStartHour/DayKrakenEndHour window as the night glow, so the more common night krakens are the bioluminescent ones. (These replace the old SerpentSpawnWeight split.) |
| `KrakenSpawnChanceNight` | float | `0.10f` | The same chance at night. |
| **Temporal storms** | | | |
| `StormFrenzyEnabled` | bool | `true` | While a temporal storm rages, the sea turns on anyone out on it. The settings below apply only during a storm; turn this off to keep storms like any other time. /uh stormfrenzy on\|off\|auto forces it for the session, for testing. |
| `StormSpawnOddsFactor` | float | `5f` | The chance of NOT spawning is divided by this, for the spawn roll (SpawnChancePerCheck) and the extra-creature roll (SecondCreatureSpawnChance): with 5, 0.1 becomes 0.82 and 0.75 becomes 0.95. A chance set to 0 stays 0. Which creature comes (kraken, anglerfish, serpent) keeps its usual odds, except the leviathan below. |
| `StormMinSaltwaterDepth` | int | `15` | Water under the player must be this deep for a serpent, instead of MinSaltwaterDepth. The kraken and the anglerfish still need MinSaltwaterDepth, and the leviathan open ocean. |
| `StormMaxLivingCreatures` | int | `5` | Hard cap on living creatures in the whole world during a storm, in place of MaxLivingCreatures. Nothing spawns past it. |
| `StormRustSerpentsOnly` | bool | `true` | Every serpent that spawns is a rust serpent. |
| `StormSerpentSpeedMultiplier` | float | `1.5f` | Every serpent and kraken tentacle moves this much faster while the storm lasts (on top of SerpentSpeedMultiplier and TentacleSpeedMultiplier). The leviathan and the anglerfish do not. |
| `StormTentacleSpeedMultiplier` | float | `3f` | The same for kraken tentacles. |
| `StormGiantSerpentSpawnChance` | float | `0.25f` | GiantSerpentSpawnChance during a storm. Its open-ocean test, its kill cooldown and its behavior stay the same. |
| **Server tuning: one-stop serpent knobs** | | | |
| `RustSerpentMaxHealth` | float | `200f` | Max health applied to serpents when they spawn (already-living creatures keep their current health; they despawn quickly anyway). The entity JSON defaults are 200 rust and 100 deep, matching these. |
| `DeepSerpentMaxHealth` | float | `100f` | The same for the deep sea serpent. |
| `SerpentSpeedMultiplier` | float | `1f` | Movement speed multiplier for both serpent variants. One knob that scales every serpent motion together: approach, attack lunge, orbit, rise, and retreat. 1 = default, 2 = twice as fast, 0.5 = half speed. Values far from 1 can make orbits and turns look a little odd since turn smoothing is tuned for the defaults. |
| `SerpentAggressionMultiplier` | float | `1f` | Aggression multiplier for both serpent variants. Above 1 the serpent closes its spiral in faster, waits less before striking, bites more often (attack cooldown shrinks), is more likely to re-attack after a strike and to turn on whoever hits it, and is less likely to flee when hurt. Below 1 gives a more skittish serpent. 1 = default. Attack damage and armor piercing are separate: SerpentAttackDamage and SerpentDamageTier further down. |
| `DayKrakenStartHour` | float | `6f` | Hour the kraken's day starts. Day/night threshold for kraken bioluminescence. Krakens spawning during [DayKrakenStartHour, DayKrakenEndHour) on the VS calendar get NO glow; outside this window they get a pulsing cyan glow matching biolumtest mode 10. Defaults: 6 AM to 10 PM = "day". |
| `DayKrakenEndHour` | float | `22f` | Hour the kraken's day ends. |
| `SerpentSpawnDepthMin` | int | `25` | Fewest blocks below the player a natural serpent spawns. The spot is then moved into open water (SerpentSpawnWaterClearance). |
| `SerpentSpawnDepthMax` | int | `40` | Most blocks below the player a natural serpent spawns. |
| `SerpentSpawnHorizontalRadiusMax` | int | `50` | Farthest a natural serpent spawns from the player, sideways, in blocks. |
| `SerpentGroundClearance` | int | `2` | How many blocks of clear water a serpent keeps under itself. A serpent is a single entity but its body reaches 12-17 blocks out along its facing axis, so a spawn point that merely happens to be water can still bury most of the animal in the sea floor. At spawn the whole body's footprint is checked and the point is raised until it clears; in flight the center is clamped so the AI cannot swim it back into the ground. Set to 0 to disable both. |
| `SerpentSpawnMaxRise` | int | `40` | How far a spawn point may be raised looking for that clearance. Spawner blocks sit in sea-floor ruins, so the lift is often most of the way to the surface; the search stops at the waterline regardless. |
| `SerpentSpawnWaterClearance` | int | `5` | Open-water requirement at the spawn point: the serpent's center must have this many blocks of water on every side (above, below, and all around). Spawn placement raycasts down from the sky to find the water column and prefers the highest position that satisfies this, so a serpent starts well clear of any sand or ruin walls that could trap it. Spawner blocks try nearby columns when their own is blocked, and only fall back to a best-effort spawn when every one is. |
| `KrakenSpawnHorizontalRadiusMax` | int | `20` | Kraken spawn horizontal radius. Spawns on the sea floor within this radius of the player (vs always directly below). |
| `SerpentProximityHeadTriggerRange` | float | `6f` | Proximity-based aggro: player getting near the serpent forces a transition to Attacking regardless of spiral state. HeadTriggerRange: head-distance that immediately triggers aggro. BodyTriggerRange: body-distance at which the dwell timer starts. BodyDwellDurationMin/Max: random dwell time (seconds) before aggro kicks in if the player stays within body range. |
| `SerpentProximityBodyTriggerRange` | float | `5f` | Blocks from its body at which the wait below starts. |
| `SerpentProximityBodyDwellMin` | float | `5f` | Shortest wait in seconds before it turns on a player who stays that close. |
| `SerpentProximityBodyDwellMax` | float | `15f` | Longest wait in seconds before it turns on a player who stays that close. |
| `SerpentNormalSubmergeDepth` | float | `5f` | The rust serpent's usual depth below the surface while it circles, in blocks. Deeper is harder to see. |
| `SerpentSurfaceSubmergeDepth` | float | `1f` | Depth during a surface peek, when a serpent briefly rises into view, and whenever its target rides a boat (both serpents rise to boat level). |
| `SerpentSurfacePeekChance` | float | `0.4f` | Chance (0 to 1), each spiral step, that the rust serpent circles at surface depth for that step. |
| `DeepSerpentSurfacePeekChance` | float | `0.1f` | The same chance for the deep sea serpent, which mostly stays deep. |
| `BoatBoredomMinSeconds` | float | `90f` | Boat boredom: while the player stays in a boat, the serpent circles for a random time in this range, then gives up and retreats so a single creature doesn't haunt forever (a new one can spawn later). The time is rolled once when the serpent starts circling the boat. |
| `BoatBoredomMaxSeconds` | float | `240f` | Longest it circles a boat before it gives up, in seconds. |
| `BoatSurfaceDurationMin` | float | `5f` | Boat surface/submerge oscillation: while the player is in a boat, the serpent surfaces and circles for a random SurfaceDuration, then sinks below the water for a random SubmergeDuration, looping. (This is the up/down cycle; the boat boredom above is the separate eventual give-up.) |
| `BoatSurfaceDurationMax` | float | `15f` | Longest surface phase, in seconds. |
| `BoatSubmergeDurationMin` | float | `15f` | Shortest time below the water between surface phases, in seconds. |
| `BoatSubmergeDurationMax` | float | `75f` | Longest time below the water between surface phases, in seconds. |
| `SecondCreatureSpawnChance` | float | `0.005f` | Chance per spawn check that a SECOND creature spawns even if the player already has one. It looks after itself: the mod does not count it as that player's creature. |
| `SerpentMaxVerticalSpeed` | float | `0.012f` | Fastest the rust serpent moves up or down while circling, in blocks per 1/60 second. Any faster looked jittery on the long body. |
| `SerpentVerticalSlewPerSec` | float | `0.04f` | Most that vertical speed may change per second, so it does not bob. |
| `SerpentTurnSpeedDegPerSec` | float | `45f` | Maximum turning speed (degrees per second) for both serpent variants. Caps how fast the body heading can sweep, so target jumps (spiral steps, state changes) produce a wide smooth arc instead of a snap turn. The attack value applies while the serpent is charging a player and needs to track them. |
| `SerpentAttackTurnSpeedDegPerSec` | float | `120f` | The same limit while it charges a player. |
| `SerpentFleeChancePerDamage` | float | `0.10f` | Flee-after-damage: every hit rolls (damage x chance-per-damage) to make the serpent break off, swim away, and resume circling at its wide initial orbit. 0.10 means 1 damage = 10% flee chance and 5 damage = 50%, so real weapons drive it off far more reliably than fists. The rust (aggressive surface) serpent uses the Rust value, which is halved so it presses the attack harder. |
| `RustSerpentFleeChancePerDamage` | float | `0.05f` | The same chance for the rust serpent, half as likely. |
| `SerpentFleeAggroSuppressSeconds` | float | `12f` | After a successful flee roll, proximity aggro and stalk-timeout attacks are suppressed for this many seconds so the serpent actually leaves instead of instantly re-aggroing off the player being inside head range. |
| `SerpentProvokeChance` | float | `0.5f` | Provoke-on-hit: when a hit neither kills nor triggers a flee and the serpent is NOT already attacking (circling, stalking, or showing off at the surface), it rolls this chance to turn on its attacker immediately. This also overrides the post-flee aggro suppression, so chasing a fleeing serpent and hitting it again can provoke a counterattack, which can then flee again. |
| `SerpentEnrageSeconds` | float | `10f` | How long a provoked serpent stays enraged. While enraged it will not roll flee-from-damage and will not disengage after a strike, so it keeps pressing repeat attacks for at least this long. |
| `IgnoreCreativePlayers` | bool | `true` | Observer mode for creative players: when true, the creatures never attack a player who is in creative or spectator mode. Serpents skip every attack trigger (spiral-complete strike, proximity aggro, stalk-timeout, provoke-on-hit) and break off mid-attack; the kraken deals no contact damage to such players and its attack tentacle sways in place instead of chasing and grabbing. Everything else stays natural: circling, surface peeks, ambient tentacles, sounds, and the usual despawn rules when the player leaves the water. On by default since 0.20.0 (the 0.12 changelog announced it as on, and every report since was a creative player who did not know it was a switch). Servers that want creative admins hunted can turn it off, or use /uh observer. |
| `IgnoreCreativePlayersMigrated` | bool | `false` | One-shot: configs written before 0.20.0 carry the old false default and are moved to true once; after that the value is never touched. |
| **Spawner blocks** | | | |
| `SpawnerTriggerRange` | float | `40f` | Serpent spawner block. A block (it looks like a vanilla locust nest cage) that watches for a player who is in the water and within SpawnerTriggerRange blocks, then spawns one rust serpent and removes itself. When that serpent despawns on its own (the player left the water, so it retreats) the block reappears in the exact same spot and re-arms. If the serpent is killed instead, the block stays gone. This gives persistent, place-anywhere serpent encounters in dedicated areas such as the /uh dungeon ruins. When IgnoreCreativePlayers is on, a creative or spectator player standing in range will not arm the spawner. |
| `SpawnerDespawnAfterLeaveSeconds` | float | `20f` | How long the target player must be out of the water before a spawner-spawned creature despawns (and its block reappears). Serpents already retreat and despawn on their own via their AI when the player leaves the water; the kraken has no such rule, so this timer is what makes a spawner-kraken sink away and its block return. Kept short so the encounter resets soon after the player gives up and leaves. |
| **Underwater ruins worldgen** | | | |
| `UnderwaterRuinsEnabled` | bool | `true` | Sunken ruins, portals, shipwrecks and a drowned city that generate rarely on deep ocean floors, each with collapsed loot chests and a chance of a serpent (or, rarely, kraken) spawner inside. |
| `RuinRarity` | int | `107` | Average of one ruin per this many ocean chunk columns. Higher is rarer: 107 is about one column in 107, and 10 gives about ten times as many ruins, not fewer. Only chunks generated after the change are affected. |
| `RuinRarityMigrated` | bool | `false` | One-shot migration: configs written with the old sparser default (320) are bumped to the new one on first load; see Validate(). |
| `RuinMinOceanDepth` | int | `12` | The sea floor under a ruin's center must be at least this many blocks below sea level. Each structure also needs enough depth to fit fully underwater, so the shipwrecks need 19 to 30 and the city 18. Water at least half this deep (4 at the least) must reach the structure's edge, checked out to 24 blocks at most. At the default a small ruin can sit on a steep coast. Raise this to keep ruins further out in new chunks. |
| `RuinGhostlightsEnabled` | bool | `true` | The glowing ghostlight orbs placed inside generated ruins. Turn this off to generate ruins without any lights (existing ruins keep the lights they already have; remove those by breaking the blocks). |
| `RuinLootChestsPerStructure` | Dictionary<string, MinMaxCount> | `DefaultChestCounts()` | Loot amount per structure type. A target count is rolled between Min and Max each time a structure generates and that many randomly chosen scripted spots are used; the rest stay empty. Counts are capped at how many spots the structure actually defines. Structures missing from the map use every spot. Fixed when the chunk generates. |
| `RuinIngotPilesPerStructure` | Dictionary<string, MinMaxCount> | `DefaultIngotPileCounts()` | Ingot piles per structure, in the same form as the chest counts above. See the table below. |
| `RuinIngotTypes` | Dictionary<string, IngotPileType> | `see table below` | What an ingot pile contains, per metal: relative pick weight plus the pile size range. Keys are vanilla ingot codes (game:ingot-<key>). Add or remove metals freely; setting a structure's pile count to 0 disables its ingots. |
| `RuinLootRebalanceMigrated` | bool | `false` | One-shot upgrade marker. |
| `RuinSpawnerChance` | float | `0.85f` | Chance (0 to 1) that an auto spawner spot in a generated structure gets a creature spawner block. Spots explicitly typed serpent or kraken in a script (the /uh dungeon uses those) always place. |
| `RuinKrakenVariantChance` | float | `0.05f` | Chance (0 to 1) that a structure rolls kraken mode, which makes every auto spawner in it a kraken spawner instead of a serpent spawner. |
| `CreatureMaxY` | double | `-1` | Movement limits. The highest Y the creatures may swim to. -1 (the default) means the world's actual sea level, whatever the map height. Earlier versions hardcoded 110, which is sea level only on a default 256-tall world; on taller worlds it pinned every serpent far below the surface (or inside the sea floor), which is the "serpents won't rise / stuck at one depth / stuck underground" bug. Set an absolute Y here only if you deliberately want a different ceiling. |
| `CreatureMaxYMigrated` | bool | `false` | One-shot migration: configs still carrying the old hardcoded 110 are moved to the sea-level default on first load; see Validate(). |
| **Despawn system** | | | |
| `DespawnCheckIntervalSeconds` | float | `2f` | Seconds between the sweeps that decide which creatures to remove. |
| `DespawnAfterLandSeconds` | float | `30f` | Seconds a creature stays after its target leaves the water. Then it is removed. |
| `DespawnMaxDistance` | float | `500f` | Despawn immediately if creature drifts farther than this from its target player (e.g. player escaped by boat, or respawned far after death). A new creature can then spawn naturally near the player. |
| `SerpentCorpseFloats` | bool | `false` | Off (default): a killed serpent sinks to the sea floor and has to be dived for. On: the corpse rises to just under the surface so a kill from a boat can be harvested from the boat. |
| **Sea serpents, rust and deep** | | | |
| `SerpentOrbitRadius` | float | `8f` | The rust serpent's closest circle, in blocks. When its spiral reaches this it attacks. |
| `SerpentOrbitSpeed` | float | `0.5f` | How fast both serpents circle at their closest orbit. Wider orbits turn more slowly in proportion. |
| `SerpentStalkDurationMin` | float | `15f` | Shortest time the rust serpent circles after backing off from a bite, in seconds. |
| `SerpentStalkDurationMax` | float | `45f` | Longest time the rust serpent circles after backing off from a bite, in seconds. |
| `SerpentRiseSpeed` | float | `0.04f` | How fast both serpents rise toward the surface, in blocks per 1/60 second. 0.04 is 2.4 blocks a second. |
| `SerpentApproachSpeed` | float | `0.03f` | How fast both serpents swim toward their surfacing spot, in the same units. Circling and retreating use twice this. |
| `SerpentAttackSpeed` | float | `0.08f` | How fast both serpents charge a player, in the same units. 0.08 is 4.8 blocks a second. |
| `SerpentAttackDamage` | float | `10f` | Damage of one bite, for both serpents. |
| `SerpentAttackCooldown` | float | `2f` | Seconds between bites, for both serpents. |
| `SerpentAttackRange` | float | `2.5f` | Not used yet. |
| `SerpentReStalkChance` | float | `0.5f` | Chance (0 to 1) that the rust serpent backs off to circle after a bite instead of pressing on. |
| `SerpentDamageTier` | int | `3` | Armor piercing tier of both serpents' bites. |
| `KrakenDamageTier` | int | `3` | Armor piercing tier of the kraken's contact damage. |
| **Rust serpent spiral** | | | |
| `SerpentInitialOrbitRadiusMin` | float | `30f` | It closes in on its prey in a shrinking spiral, one step at a time. Smallest circle it starts from, in blocks. |
| `SerpentInitialOrbitRadiusMax` | float | `50f` | Largest circle it starts from, in blocks. |
| `SerpentSpiralStepDurationMin` | float | `5f` | Shortest time for one step, in seconds. |
| `SerpentSpiralStepDurationMax` | float | `15f` | Longest time for one step, in seconds. |
| `SerpentSpiralReductionMin` | float | `5f` | Fewest blocks the circle tightens in one step. |
| `SerpentSpiralReductionMax` | float | `15f` | Most blocks the circle tightens in one step. |
| **Deep sea serpent** | | | |
| `DeepSerpentSpawnWeight` | float | `0.9f` | It stays deep, circles in huge arcs and rises only to strike. Share of natural serpents that are this one. 0.9 is 90% deep, 10% rust. |
| `RustSerpentTuningApplied` | bool | `false` | One-shot migration flag: configs written before the rust serpent rebalance carry the old 0.75 weight; the first load raises it to 0.9 and sets this flag, after which user tuning is left alone. |
| `RuinIngotIronMigrated` | bool | `false` | One-shot migration flag: configs whose ingot map still carries the exact old default steel entry get it swapped for iron; see Validate(). |
| `DeepSerpentStalkDepthMin` | float | `10f` | Shallowest it circles, in blocks below the surface. It picks one depth for its life. |
| `DeepSerpentStalkDepthMax` | float | `30f` | Deepest it circles, in blocks below the surface. |
| `DeepSerpentOrbitRadius` | float | `15f` | Its closest circle, in blocks. When its spiral reaches this it attacks. |
| `DeepSerpentInitialOrbitRadiusMin` | float | `50f` | Smallest circle it starts from, in blocks. |
| `DeepSerpentInitialOrbitRadiusMax` | float | `80f` | Largest circle it starts from, in blocks. |
| `DeepSerpentSpiralStepDurationMin` | float | `15f` | Shortest time for one spiral step, in seconds. |
| `DeepSerpentSpiralStepDurationMax` | float | `30f` | Longest time for one spiral step, in seconds. |
| `DeepSerpentSpiralReductionMin` | float | `5f` | Fewest blocks the circle tightens in one step. |
| `DeepSerpentSpiralReductionMax` | float | `15f` | Most blocks the circle tightens in one step. |
| `DeepSerpentMaxPitchRad` | float | `0.005f` | Not used yet. |
| `DeepSerpentPitchInterpRate` | float | `0.3f` | Not used yet. |
| `DeepSerpentMaxVerticalSpeed` | float | `0.012f` | Fastest it moves up or down while circling, in blocks per 1/60 second. Much slower than it swims, so the long body glides. |
| `DeepSerpentVerticalSlewPerSec` | float | `0.04f` | Most that vertical speed may change per second, so it does not snap as it crosses its target depth. |
| **Giant serpent** | | | |
| `GiantSerpentSpawnChance` | float | `0.05f` | A very rare rust serpent five times the size (the entity size in giantserpent.json). It never surfaces to hiss like the rust serpent: it circles far below its prey, then rises mouth first from the deep. It bites swimmers, swallows rafts whole and breaks bigger boats in a few bites. It never leaves the water: when it strikes, only its head and the arch of its back break the surface. Patches of cyan bioluminescence grow over its hide and light the water around it. Chance that a natural serpent spawn in open ocean is the giant instead. Open ocean: the player's own column and four random columns within GiantSerpentOpenOceanRadius blocks are all saltwater at least GiantSerpentOpenOceanDepth deep. 0 turns natural giants off; /uh spawn giantserpent always works. |
| `GiantSerpentOpenOceanRadius` | float | `50f` | How far around the player the open-ocean test looks, in blocks. |
| `GiantSerpentOpenOceanDepth` | int | `50` | How deep the saltwater must be at the player and at every tested column. |
| `GiantSerpentChanceMigrated` | bool | `false` | One-shot upgrade marker. |
| `GiantSerpentMaxHealth` | float | `1500f` | Its health. It can only be hit while its body passes the surface after a strike, about 20 seconds a pass; a player must finish it before it sinks their boat and kills them in the water. Giants already out keep the health they came with. |
| `GiantSerpentHealth1500Migrated` | bool | `false` | One-shot upgrade marker. |
| `GiantSerpentFleeHealthFraction` | float | `0.25f` | Once its health falls below this fraction it flees, once: it swims off to GiantSerpentFleeDistance blocks from its prey, circles there until GiantSerpentFleeSeconds have passed, then comes back and fights to the death. 0 turns fleeing off. |
| `GiantSerpentFleeSeconds` | float | `60f` | Seconds from the start of its flight until it comes back. |
| `GiantSerpentFleeDistance` | float | `200f` | How far from its prey it flees, in blocks. Kept under 80% of DespawnMaxDistance, so fleeing never gets it removed. At 200 its deep silhouette (drawn out to 230 blocks) stays in sight. |
| `GiantSerpentBiteDamage` | float | `20f` | Damage of a bite on a swimmer, and its armor piercing tier. |
| `GiantSerpentDamageTier` | int | `5` | Armor piercing tier of its bite. |
| `GiantSerpentTierMigrated` | bool | `false` | One-shot upgrade marker. |
| `GiantSerpentDestroysBoats` | bool | `true` | Whether the giant swallows rafts and breaks other boats. Off, boats are never harmed: every bite on a boat hurts its riders by GiantSerpentRiderDamage instead, and they stay aboard. |
| `GiantSerpentRiderDamage` | float | `8f` | Damage to each rider when the giant swallows or breaks their boat. They land in the water, where it can come back for them. With GiantSerpentDestroysBoats off, the damage of each bite on their boat. |
| `GiantSerpentBitesToSinkBoat` | int | `3` | Bites it takes to break a boat that is not a raft (the sailboat). A raft always goes down in one gulp. |
| `GiantSerpentCruiseSpeed` | float | `7f` | Swimming speeds in blocks per second: cruising and circling, and the final rush at its prey. |
| `GiantSerpentStrikeSpeed` | float | `24f` | Speed of the final rush at its prey, in blocks per second. |
| `GiantSerpentStalkSecondsMin` | float | `25f` | Seconds it circles below before the first strike, and between strikes. |
| `GiantSerpentStalkSecondsMax` | float | `50f` | Longest it circles before its first strike, in seconds. |
| `GiantSerpentStrikeCooldownMin` | float | `15f` | Shortest it circles between strikes, in seconds. |
| `GiantSerpentStrikeCooldownMax` | float | `30f` | Longest it circles between strikes, in seconds. |
| `GiantSerpentMinStrikeDepth` | int | `20` | It only strikes prey over water at least this deep. When its prey is over shallower water or ashore, it swims off into the deep, and comes back if its prey returns to deep water. |
| `GiantSerpentDespawnAfterLandSeconds` | float | `120f` | Seconds its prey can stay ashore before the giant is removed. It is also removed once it has swum out of sight of every player. |
| `GiantSerpentMaxStaySeconds` | float | `480f` | It swims off after this many seconds even if its prey is still at sea, unless a player has hurt it in the last two minutes: then it stays and fights. |
| `GiantSerpentSoundPitch` | float | `0.6f` | Pitch of its bite and other sounds: the rust serpent's, played slower and deeper. 1 is the rust serpent's own pitch. |
| `GiantSerpentKillCooldownMinutes` | float | `120f` | After a player kills one, no natural giant comes anywhere on the server for this many real minutes. The time is kept in the save, so it counts on while the server is off. /uh giantcooldown shows or clears it; /uh spawn giantserpent ignores it. |
| `GiantSerpentPieceRustyGearsMin` | int | `10` | A killed giant breaks apart into its 22 body segments, which sink to the sea floor. One right click with a knife cuts a segment open and spills its share into the water, a random count between the two numbers: rusty gears, temporal gears, 1 to 3 of every kind of Jonas part, raw fish and bones. |
| `GiantSerpentPieceRustyGearsMax` | int | `15` | Most rusty gears in one segment. |
| `GiantSerpentPieceTemporalGearsMin` | int | `1` | Fewest temporal gears in one segment. |
| `GiantSerpentPieceTemporalGearsMax` | int | `4` | Most temporal gears in one segment. |
| `GiantSerpentPieceJonasPartsMin` | int | `1` | Fewest of each kind of Jonas part in one segment. |
| `GiantSerpentPieceJonasPartsMax` | int | `3` | Most of each kind of Jonas part in one segment. |
| `GiantSerpentPieceFishMin` | int | `20` | Fewest raw fish in one segment. |
| `GiantSerpentPieceFishMax` | int | `50` | Most raw fish in one segment. |
| `GiantSerpentPieceBonesMin` | int | `20` | Fewest bones in one segment. |
| `GiantSerpentPieceBonesMax` | int | `50` | Most bones in one segment. |
| `GiantSerpentPieceDecayHours` | float | `48f` | In-game hours its segments lie on the sea floor before they crumble, each leaving a pile of bones. 0 keeps them for good. |
| **Giant anglerfish** | | | |
| `AnglerfishNightSpawnChance` | float | `0.05f` | A gigantic angler some 100 blocks long. Like a wobbegong it lies on the sea floor near a coast and creeps toward a player heading for land, casting a glowing temporal gear on a rope from the pole on its head. When its prey comes close to its mouth, it swoops and gulps. The first blow on the gear knocks a temporal gear loose and sends the fish away; breaking the gear (10 health) drops another. Chance that a natural spawn at night is the anglerfish instead. Night is outside DayKrakenStartHour..DayKrakenEndHour. It needs a coast within AnglerfishCoastRange blocks of the player with water at least AnglerfishMinWaterDepth deep off it. 0 turns natural anglerfish off; /uh spawn anglerfish always works. A natural one sinks back into the deep at daybreak. |
| `AnglerfishCoastRange` | float | `160f` | How far from the player it looks for a coast, in blocks. |
| `AnglerfishMinWaterDepth` | int | `40` | Water it needs to lie in, in blocks from the surface to the floor. |
| `AnglerfishShoreCheckSeconds` | float | `60f` | A player on land at night by deep coastal water is checked about this often (seconds): a few columns of water around them are tested, and with AnglerfishShoreSpawnChance an anglerfish comes to lie in one, baiting them from the shore. 0 turns these off. |
| `AnglerfishShoreSpawnChance` | float | `0.25f` | Chance (0 to 1) that one such check brings an anglerfish. 0 turns these off. |
| `AnglerfishStrikeRange` | float | `28f` | It swoops once its prey is this close to its mouth, in blocks, or within that range sideways and up to AnglerfishStrikeReachUp blocks above its mouth: it rises from the floor at prey on the surface. |
| `AnglerfishStrikeReachUp` | float | `60f` | How far above its mouth it can reach prey, in blocks. |
| `AnglerfishStrikeSpeed` | float | `40f` | Speed of the swoop, in blocks per second. |
| `AnglerfishBiteDamage` | float | `50f` | Damage of the gulp. |
| `AnglerfishDamageTier` | int | `3` | Armor piercing tier of the gulp. |
| `AnglerfishGiantMigrated` | bool | `false` | One-shot upgrade marker. |
| **Kraken body** | | | |
| `KrakenContactDamage` | float | `25f` | Damage each second to a player touching the kraken's body. |
| `KrakenContactRange` | float | `3f` | How close to the body counts as touching it, in blocks. |
| `KrakenAmbientTentacleCount` | int | `3` | Arms that rise to the surface and circle. With the ground tentacles below and the one attack tentacle, the default is 8 arms. |
| `KrakenGroundTentacleCount` | int | `4` | Ground tentacles slither across the sea floor instead of rising. They make the kraken's footprint feel huge and obviously alive. |
| `KrakenTentacleSpawnRadius` | float | `5f` | How far from the body its arms start, in blocks. |
| `TentacleSpeedMultiplier` | float | `1f` | Global speed dial for EVERY tentacle, attack and ambient alike: rising, orbiting, wandering, pursuing and dragging all scale by it. 2 makes the whole kraken twice as quick. Raise this rather than editing the individual speeds below if you just want a livelier kraken, since it keeps their relative pacing intact. |
| **Attack tentacle** | | | |
| `TentacleIdleDuration` | float | `2f` | Seconds a new attack tentacle waits before it starts to rise. |
| `TentacleReachSpeed` | float | `0.06f` | Pursuit speed: how fast the attack tentacle closes on the player once it has surfaced and started hunting. Scaled by TentacleSpeedMultiplier on top of this. |
| `TentacleGrabRange` | float | `2f` | How close to the player it grabs, in blocks. |
| `TentacleDragSpeed` | float | `2.0f` | How fast a grabbed player is dragged to the body, in blocks per second. |
| `TentacleGrabYOffset` | float | `-0.5f` | Height of the tentacle's head relative to the gripped player's feet, in blocks. |
| `TentacleSinkDuration` | float | `30f` | Seconds it sinks after letting go, before it is removed. |
| `TentacleGrabDamageEnabled` | bool | `true` | Damage dealt on a timer while the tentacle has hold of the player, so a grab is a countdown rather than just an inconvenience. The kraken body's own contact damage skips mounted players, so this is what hurts you while you are being dragged. |
| `TentacleGrabDamage` | float | `1f` | Damage of each squeeze. |
| `TentacleGrabDamageIntervalSeconds` | float | `2f` | Seconds between squeezes. |
| `TentacleProximityAggroEnabled` | bool | `true` | Proximity aggro. When the kraken picks its next attack tentacle, a player standing within this many blocks of a tentacle gets that one (nearest wins) instead of a random one, and it skips the rise and linger build-up to hunt immediately. Swim into a tentacle and it is the one that comes for you. The promotion cooldown below is still honoured first, so this changes WHICH tentacle moves in, not when. |
| `TentacleProximityAggroRange` | float | `3f` | How close a player must be to a tentacle for that, in blocks. |
| `TentacleRemainsEnabled` | bool | `true` | Where a dead tentacle's remains land, and what it leaves. Tentacles sink to the sea floor when the kraken body dies; on touching down (or on being killed outright) they leave a bone pile and rusty gears or scrap where they fell. |
| `TentacleSinkToFloorTimeout` | float | `30f` | Safety cap on the sink. If a tentacle somehow cannot reach the floor within this long it gives up, drops its remains on the floor beneath it anyway, and despawns. |
| `TentacleDeathSinkSpeed` | float | `0.08f` | Motion units, as with the other tentacle speeds: roughly 60x this many blocks per second, so 0.08 is a heavy limp arm coming down at about 5 blocks a second. From the surface to a deep floor that is ten to fifteen seconds, well inside the timeout above. |
| **Tentacle shape** | | | |
| `TentacleArchHeightFactor` | float | `0.4f` | How high an arm curves up between the body and its tip, as a share of the distance between them. |
| `TentacleTipLerpSpeed` | float | `5f` | Not used. |
| **Risers: arms that rise to the surface and circle** | | | |
| `AmbientTentacleRiseSpeed` | float | `0.04f` | How fast a riser climbs, in blocks per 1/60 second. |
| `AmbientTentacleOrbitRadius` | float | `4f` | Size of the circle a riser swims at the surface, in blocks. |
| `AmbientTentacleOrbitSpeed` | float | `0.4f` | How fast it circles. |
| `AmbientTentacleBobAmplitude` | float | `1.5f` | How far it bobs up and down, in blocks. |
| `AmbientTentacleBobSpeed` | float | `0.7f` | How fast it bobs. |
| `AmbientTentacleSurfaceRange` | float | `10f` | Not used yet. |
| **Attack tentacle at the surface** | | | |
| `TentacleRiseSpeed` | float | `0.025f` | How fast it rises, in blocks per 1/60 second. |
| `TentacleLingerDuration` | float | `7f` | Seconds it lingers at the surface before it hunts. |
| `TentacleSurfaceRange` | float | `10f` | How far from the player it comes up, at most, in blocks. |
| **Shallow water retreat** | | | |
| `ShallowWaterThreshold` | int | `3` | Water around the player counts as shallow when it is fewer than this many blocks deep. Serpents and tentacles turn back there. |
| `RetreatSpeed` | float | `0.06f` | How fast a retreating tentacle sinks, in blocks per 1/60 second. |
| `RetreatDuration` | float | `8f` | Seconds it sinks before it is removed. |
| `TentacleStallDespawnSeconds` | float | `30f` | Stalling state: the attack tentacle enters Stalling the moment the player leaves the water OR mounts a boat. While stalling it either drifts slowly back toward the kraken body (player on land) or wanders in a slow circle around the player (player on boat). It does NOT actively chase. If the player returns to a chase-able state (back in water, dismounted) before the timer expires the tentacle resumes Reaching from where it stalled. Otherwise the tentacle transitions to Retreating and despawns gracefully. |
| `TentacleStallOrbitRadius` | float | `7f` | 7 keeps the orbiting tentacle clear of a boat's hull (4 had it grinding along the side of any boat the player was sitting in). |
| `TentacleStallOrbitMigrated` | bool | `false` | One-shot upgrade marker. |
| `TentacleStallOrbitSpeed` | float | `0.3f` | How fast its circle around a boat turns. |
| `TentacleStallDriftSpeed` | float | `0.018f` | Drift speeds during Stalling. Slow on purpose so the visible intent reads as "lurking" rather than "leaving". |
| `TentacleStallBoatSpeed` | float | `0.03f` | How fast it swims on that circle, in blocks per 1/60 second. |
| `AmbientTentacleWanderRangeMin` | float | `5f` | How the ground tentacles crawl the sea floor, here and in the settings below. Nearest point they pick to crawl to, in blocks from the body. |
| `AmbientTentacleWanderRangeMax` | float | `80f` | Farthest point they pick, in blocks from the body. |
| `AmbientTentacleWanderSpeed` | float | `0.05f` | How fast they crawl, in blocks per 1/60 second. |
| `AmbientTentacleWanderIdleMin` | float | `1.5f` | Shortest pause between crawls, in seconds. |
| `AmbientTentacleWanderIdleMax` | float | `4f` | Longest pause between crawls, in seconds. |
| `AmbientTentacleVerticalStepMax` | float | `0.1f` | Most a ground tentacle may move up or down each tick. Where the floor jumps, this makes it climb smoothly (about 3 blocks a second) instead of leaping. |
| `AmbientScatterSinkDuration` | float | `10f` | When the attack tentacle starts hunting, the risers leave the surface. They sink toward midwater for this many seconds, then wander there. |
| `AmbientSurfaceWanderRangeMin` | float | `5f` | Nearest midwater point they pick, in blocks from the body. |
| `AmbientSurfaceWanderRangeMax` | float | `30f` | Farthest midwater point they pick, in blocks from the body. |
| `AmbientSurfaceWanderDepthMax` | float | `20f` | Deepest midwater point they pick, in blocks below the surface. |
| `AmbientPromoteToAttackDelayMin` | float | `5f` | Promotion: when the attack tentacle dies, kraken body waits this long, then picks a surviving ambient tentacle (any of them, risers and ground crawlers alike) and respawns a new attack tentacle at its position, killing the chosen ambient. This is the breathing room you get after killing a grabber; TentacleProximityAggroRange decides which tentacle steps up once it elapses. |
| `AmbientPromoteToAttackDelayMax` | float | `20f` | Longest wait in seconds. The setting above is the shortest. |
| `PromoteDelayMigrated` | bool | `false` | One-shot migration: configs written with the old 30/120 defaults are moved to the new, much shorter ones on first load; see Validate(). |
| **Kraken glow** | | | |
| `BiolumActive` | bool | `false` | A pulsing glow that travels along the tentacles. /uh kraken biolum sets it. |
| `BiolumPulsing` | bool | `false` | Whether the glow pulses. /uh kraken biolum pulse sets it. |
| `BiolumPulseSpeed` | float | `1.4f` | How fast it pulses. |
| `BiolumGlowMin` | int | `32` | Not used yet. |
| `BiolumGlowMax` | int | `200` | Not used yet. |
| `BiolumBodyGlowMin` | int | `16` | Not used yet. |
| `BiolumBodyGlowMax` | int | `128` | Not used yet. |
| **Sounds** | | | |
| `MonsterSoundsEnabled` | bool | `true` | Sea monster sounds. The serpent plays ambient dread sounds while it stalks, plus action stingers when it surfaces, dives, and bites. Only one sound plays at a time per player; a bite overrides whatever is playing. |
| `MonsterSoundVolume` | float | `1.0f` | Global volume applied to every monster sound. Each individual sound below has its own multiplier on top of this, so you can balance them. |
| `MonsterSoundBelow1Volume` | float | `1.0f` | Per-sound volume multipliers (multiplied with MonsterSoundVolume). 1.0 = no change. The screech has its own multiplier further down. |
| `MonsterSoundBelow2Volume` | float | `1.0f` | Multiplier for the second ambient clip. |
| `MonsterSoundNearbyVolume` | float | `1.0f` | Multiplier for the sound of a creature close by at the surface. |
| `MonsterSoundDiveVolume` | float | `1.0f` | Multiplier for the dive sound. |
| `MonsterSoundBiteVolume` | float | `0.75f` | Multiplier for the bite. |
| `MonsterSoundScreechVolume` | float | `1.0f` | The surface screech is the dramatic moment. It plays as a non-positional 2D sound (so it is clearly audible, not directional) for players within MonsterSoundScreechRange. Volume is full at the creature and drops with distance down to MonsterSoundScreechMinVolumeFactor of full at the edge. 1.0 and 0.2 since 0.20.0 (was 1.5 and 0.5: "too loud and does not fade" was the most common sound complaint). |
| `MonsterSoundScreechRange` | float | `25f` | How far the screech carries, in blocks. |
| `MonsterSoundScreechMinVolumeFactor` | float | `0.2f` | Share of full volume at the edge of that range. |
| `ScreechVolumeMigrated` | bool | `false` | One-shot upgrade marker. |
| `MonsterSoundRange` | float | `48f` | Audible range (blocks) for the positional monster sounds. Players within this range of the creature receive and hear them. |
| `MonsterSoundSurfaceThreshold` | float | `2.5f` | Distance (blocks) below sea level within which the monster counts as "at the surface" for the nearby surface sound and the screech. |
| `MonsterSoundNearbyRange` | float | `10f` | The "nearby at surface" sound needs the monster within this range. |
| `MonsterSoundBelowMinRange` | float | `5f` | The two "below" ambient sounds will not play when the monster is closer than this (the nearby sound covers close range instead). |
| `MonsterSoundAmbientGapMin` | float | `14f` | Random gap (seconds) between ambient dread sounds, so they play occasionally rather than constantly. After each ambient sound the creature waits a random time in this range before the next one. |
| `MonsterSoundAmbientGapMax` | float | `34f` | Longest gap, in seconds. The setting above is the shortest. |

## Ruin loot tables

`RuinLootChestsPerStructure` and `RuinIngotPilesPerStructure` map each structure name (`ruin`, `portal`, `shipwreck-small`, `shipwreck-medium`, `shipwreck-huge`, `city`) to a `{ Min, Max }` count. `RuinIngotTypes` maps a metal name (any `game:ingot-<metal>`) to `{ Weight, CountMin, CountMax }`; a pile picks its metal by weight and its size uniformly between the two counts. Set a structure's pile count to 0 to give it no ingots.

Since 0.20.0 a pile's metal and size are rolled from the current config when a player first comes near the ruin, so changing `RuinIngotTypes` also changes every ruin nobody has visited yet. The number of chests and piles per structure is fixed when the chunk generates. Chests are vanilla stack randomizers and follow the vanilla loot tables; the chest count is the only lever for them.
