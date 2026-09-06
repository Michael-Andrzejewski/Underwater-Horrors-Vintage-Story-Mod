# Underwater Horrors config reference

File: `VintagestoryData/ModConfig/UnderwaterHorrorsConfig.json`. Created with defaults on first run. Edit it with the server stopped, or use the `/uh` commands where one exists and the mod writes the file for you.

Generated from the source comments by `tools/gen_config_doc.py`; do not edit by hand.

Properties ending in `Migrated` or `Applied` are one-shot upgrade markers. Leave them alone.

| Setting | Type | Default | What it does |
|---|---|---|---|
| **Debug** | | | |
| `DebugLogging` | bool | `false` |  |
| `DebugLoggingResetApplied` | bool | `false` | One-shot migration flag. Earlier mod versions shipped with DebugLogging=true by default, which spammed chat for everyone. The first load after upgrading flips DebugLogging to false and sets this flag to true; from then on we never touch DebugLogging again, so /uh debug on (or hand-editing the config) sticks across restarts. |
| `GlowDebugActive` | bool | `false` |  |
| `SpectralDebugActive` | bool | `false` |  |
| `SpawnCheckIntervalSeconds` | float | `5f` | Spawn system How often (seconds) each player in deep water is rolled for a spawn. |
| `MinSaltwaterDepth` | int | `50` | Water under the player must be at least this deep for anything to spawn. Counted straight down from the surface; 50 is open ocean. |
| `AllowFreshwaterSpawns` | bool | `false` | Normally only saltwater counts, so a big inland lake never grows a serpent. Turn this on and any water deep enough (MinSaltwaterDepth) qualifies, fresh or salt. Ruins still generate only in the ocean. |
| `SpawnChancePerCheck` | float | `0.1f` | Chance (0 to 1) that one spawn check produces a creature. At 0.1 and a 5 second interval a swimmer meets something within about a minute. |
| `MaxLivingCreatures` | int | `1` | Global soft cap on living serpents/krakens. Once this many are alive in the world, normal per-player spawning stops; instead at most ONE rare extra spawn is rolled per spawn check across the whole server (see OverCapSpawnChance). This keeps a second serpent very rare even with many players in the ocean or in boats, rather than one per player. |
| `OverCapSpawnChance` | float | `0.002f` | Single global per-check chance to spawn an extra creature while already at MaxLivingCreatures. One roll per check regardless of player count, so the rate does not scale up on busy servers. Very low on purpose. |
| `KrakenNaturalSpawnEnabled` | bool | `true` | Master switch for kraken in NATURAL spawn checks. /uh spawn kraken always works; this just gates the random per-player spawn roll from picking a kraken. The kraken is a heavyweight encounter (one body plus 8 tentacles, each with a 96-segment chain, roughly 780 entities) so it is kept rare via the day/night chances below and can be turned off entirely here if a server or client struggles near it. |
| `KrakenSpawnTuningApplied` | bool | `false` | One-shot migration flag: configs written before natural kraken spawning was enabled by default carry the old false value. The first load flips KrakenNaturalSpawnEnabled to true and sets this flag, after which user tuning is left alone. |
| `KrakenSpawnChanceDay` | float | `0.05f` | Chance that a successful natural hostile spawn is a kraken instead of a serpent. Rolled once per spawn, so serpent frequency is barely affected: 0.05 means 1 in 20 hostile spawns is a kraken by day, 0.10 means 1 in 10 at night. "Night" reuses the same DayKrakenStartHour/DayKrakenEndHour window as the night glow, so the more common night krakens are the bioluminescent ones. (These replace the old SerpentSpawnWeight split.) |
| `KrakenSpawnChanceNight` | float | `0.10f` |  |
| `RustSerpentMaxHealth` | float | `200f` | ─── Server tuning: one-stop serpent knobs ─────────────────────────── Max health applied to serpents when they spawn (already-living creatures keep their current health; they despawn quickly anyway). The entity JSON defaults are 200 rust and 100 deep, matching these. |
| `DeepSerpentMaxHealth` | float | `100f` |  |
| `SerpentSpeedMultiplier` | float | `1f` | Movement speed multiplier for both serpent variants. One knob that scales every serpent motion together: approach, attack lunge, orbit, rise, and retreat. 1 = default, 2 = twice as fast, 0.5 = half speed. Values far from 1 can make orbits and turns look a little odd since turn smoothing is tuned for the defaults. |
| `SerpentAggressionMultiplier` | float | `1f` | Aggression multiplier for both serpent variants. Above 1 the serpent closes its spiral in faster, waits less before striking, bites more often (attack cooldown shrinks), is more likely to re-attack after a strike and to turn on whoever hits it, and is less likely to flee when hurt. Below 1 gives a more skittish serpent. 1 = default. Attack damage and armor piercing are separate: SerpentAttackDamage and SerpentDamageTier further down. |
| `DayKrakenStartHour` | float | `6f` | Day/night threshold for kraken bioluminescence. Krakens spawning during [DayKrakenStartHour, DayKrakenEndHour) on the VS calendar get NO glow; outside this window they get a pulsing cyan glow matching biolumtest mode 10. Defaults: 6 AM to 10 PM = "day". |
| `DayKrakenEndHour` | float | `22f` |  |
| `SerpentSpawnDepthMin` | int | `25` | Serpent spawn offsets. Horizontal position is randomized uniformly within a circle of the given max radius around the player. |
| `SerpentSpawnDepthMax` | int | `40` |  |
| `SerpentSpawnHorizontalRadiusMax` | int | `50` |  |
| `SerpentGroundClearance` | int | `2` | How many blocks of clear water a serpent keeps under itself. A serpent is a single entity but its body reaches 12-17 blocks out along its facing axis, so a spawn point that merely happens to be water can still bury most of the animal in the sea floor. At spawn the whole body's footprint is checked and the point is raised until it clears; in flight the center is clamped so the AI cannot swim it back into the ground. Set to 0 to disable both. |
| `SerpentSpawnMaxRise` | int | `40` | How far a spawn point may be raised looking for that clearance. Spawner blocks sit in sea-floor ruins, so the lift is often most of the way to the surface; the search stops at the waterline regardless. |
| `SerpentSpawnWaterClearance` | int | `5` | Open-water requirement at the spawn point: the serpent's center must have this many blocks of water on every side (above, below, and all around). Spawn placement raycasts down from the sky to find the water column and prefers the highest position that satisfies this, so a serpent starts well clear of any sand or ruin walls that could trap it. Spawner blocks try nearby columns when their own is blocked, and only fall back to a best-effort spawn when every one is. |
| `KrakenSpawnHorizontalRadiusMax` | int | `20` | Kraken spawn horizontal radius. Spawns on the sea floor within this radius of the player (vs always directly below). |
| `SerpentProximityHeadTriggerRange` | float | `6f` | Proximity-based aggro: player getting near the serpent forces a transition to Attacking regardless of spiral state. HeadTriggerRange: head-distance that immediately triggers aggro. BodyTriggerRange: body-distance at which the dwell timer starts. BodyDwellDurationMin/Max: random dwell time (seconds) before aggro kicks in if the player stays within body range. |
| `SerpentProximityBodyTriggerRange` | float | `5f` |  |
| `SerpentProximityBodyDwellMin` | float | `5f` |  |
| `SerpentProximityBodyDwellMax` | float | `15f` |  |
| `SerpentNormalSubmergeDepth` | float | `5f` | Stalk depth configuration. NormalSubmergeDepth — the regular serpent's usual cruise depth below the water surface.  Deeper = less visible. SurfaceSubmergeDepth — depth used during a "surface peek" orbit step (serpent briefly rises for visibility) and when the target player is mounted (both variants rise to boat level). SerpentSurfacePeekChance / DeepSerpentSurfacePeekChance — per-spiral-step probability that the next orbit is at surface depth instead of normal depth.  Higher for the regular serpent; the deep variant should mostly stay deep. |
| `SerpentSurfaceSubmergeDepth` | float | `1f` |  |
| `SerpentSurfacePeekChance` | float | `0.4f` |  |
| `DeepSerpentSurfacePeekChance` | float | `0.1f` |  |
| `BoatBoredomMinSeconds` | float | `90f` | Boat boredom: while the player stays in a boat, the serpent circles for a random time in this range, then gives up and retreats so a single creature doesn't haunt forever (a new one can spawn later). The time is rolled once when the serpent starts circling the boat. |
| `BoatBoredomMaxSeconds` | float | `240f` |  |
| `BoatSurfaceDurationMin` | float | `5f` | Boat surface/submerge oscillation: while the player is in a boat, the serpent surfaces and circles for a random SurfaceDuration, then sinks below the water for a random SubmergeDuration, looping. (This is the up/down cycle; the boat boredom above is the separate eventual give-up.) |
| `BoatSurfaceDurationMax` | float | `15f` |  |
| `BoatSubmergeDurationMin` | float | `15f` |  |
| `BoatSubmergeDurationMax` | float | `75f` |  |
| `SecondCreatureSpawnChance` | float | `0.005f` | Chance per spawn check that a SECOND creature spawns even if the player already has one tracked.  Untracked — the second creature manages its own lifecycle via the AI state machine. |
| `SerpentMaxVerticalSpeed` | float | `0.012f` | Vertical-motion smoothing for the regular serpent.  Matches the deep variant — playtesting showed 2x leeway was still too jittery on the long body. |
| `SerpentVerticalSlewPerSec` | float | `0.04f` |  |
| `SerpentTurnSpeedDegPerSec` | float | `45f` | Maximum turning speed (degrees per second) for both serpent variants. Caps how fast the body heading can sweep, so target jumps (spiral steps, state changes) produce a wide smooth arc instead of a snap turn. The attack value applies while the serpent is charging a player and needs to track them. |
| `SerpentAttackTurnSpeedDegPerSec` | float | `120f` |  |
| `SerpentFleeChancePerDamage` | float | `0.10f` | Flee-after-damage: every hit rolls (damage x chance-per-damage) to make the serpent break off, swim away, and resume circling at its wide initial orbit. 0.10 means 1 damage = 10% flee chance and 5 damage = 50%, so real weapons drive it off far more reliably than fists. The rust (aggressive surface) serpent uses the Rust value, which is halved so it presses the attack harder. |
| `RustSerpentFleeChancePerDamage` | float | `0.05f` |  |
| `SerpentFleeAggroSuppressSeconds` | float | `12f` | After a successful flee roll, proximity aggro and stalk-timeout attacks are suppressed for this many seconds so the serpent actually leaves instead of instantly re-aggroing off the player being inside head range. |
| `SerpentProvokeChance` | float | `0.5f` | Provoke-on-hit: when a hit neither kills nor triggers a flee and the serpent is NOT already attacking (circling, stalking, or showing off at the surface), it rolls this chance to turn on its attacker immediately. This also overrides the post-flee aggro suppression, so chasing a fleeing serpent and hitting it again can provoke a counterattack, which can then flee again. |
| `SerpentEnrageSeconds` | float | `10f` | How long a provoked serpent stays enraged. While enraged it will not roll flee-from-damage and will not disengage after a strike, so it keeps pressing repeat attacks for at least this long. |
| `IgnoreCreativePlayers` | bool | `true` | Observer mode for creative players: when true, the creatures never attack a player who is in creative or spectator mode. Serpents skip every attack trigger (spiral-complete strike, proximity aggro, stalk-timeout, provoke-on-hit) and break off mid-attack; the kraken deals no contact damage to such players and its attack tentacle sways in place instead of chasing and grabbing. Everything else stays natural: circling, surface peeks, ambient tentacles, sounds, and the usual despawn rules when the player leaves the water. On by default since 0.20.0 (the 0.12 changelog announced it as on, and every report since was a creative player who did not know it was a switch). Servers that want creative admins hunted can turn it off, or use /uh observer. |
| `IgnoreCreativePlayersMigrated` | bool | `false` | One-shot: configs written before 0.20.0 carry the old false default and are moved to true once; after that the value is never touched. |
| `SpawnerTriggerRange` | float | `40f` | Serpent spawner block. A creative-only block (looks like a vanilla locust nest cage) that watches for a player who is in the water and within SpawnerTriggerRange blocks, then spawns one sea serpent and removes itself. When that serpent despawns on its own (the player left the water, so it retreats) the block reappears in the exact same spot and re-arms. If the serpent is killed instead, the block stays gone. This gives persistent, place-anywhere serpent encounters in dedicated areas such as the /uh dungeon ruins. When IgnoreCreativePlayers is on, a creative or spectator player standing in range will not arm the spawner. |
| `SpawnerDespawnAfterLeaveSeconds` | float | `20f` | How long the target player must be out of the water before a spawner-spawned creature despawns (and its block reappears). Serpents already retreat and despawn on their own via their AI when the player leaves the water; the kraken has no such rule, so this timer is what makes a spawner-kraken sink away and its block return. Kept short so the encounter resets soon after the player gives up and leaves. |
| `UnderwaterRuinsEnabled` | bool | `true` | ─── Underwater ruins worldgen ─────────────────────────────────────── Sunken ruins, portals, shipwrecks and a drowned city that generate rarely on deep ocean floors, each with collapsed loot chests and a chance of a serpent (or, rarely, kraken) spawner inside. |
| `RuinRarity` | int | `107` | Average of one ruin per this many deep-ocean chunk columns. Higher = rarer. Drop it to see them more often. |
| `RuinRarityMigrated` | bool | `false` | One-shot migration: configs written with the old sparser default (320) are bumped to the new one on first load; see Validate(). |
| `RuinMinOceanDepth` | int | `12` | The sea floor must be at least this many blocks below sea level for a ruin to place, so they only appear in genuinely deep water. Each structure additionally requires enough depth to fit fully underwater (tall structures like the city and the shipwrecks only generate where the ocean is deep enough to cover them), and the water around the structure's footprint is checked too, so ruins no longer generate on shore banks with their tops poking out of the waves. |
| `RuinGhostlightsEnabled` | bool | `true` | The glowing ghostlight orbs placed inside generated ruins. Turn this off to generate ruins without any lights (existing ruins keep the lights they already have; remove those by breaking the blocks). |
| `RuinLootChestsPerStructure` | Dictionary<string, MinMaxCount> | `DefaultChestCounts()` | Loot amount per structure type. A target count is rolled between Min and Max each time a structure generates and that many randomly chosen scripted spots are used; the rest stay empty. Counts are capped at how many spots the structure actually defines, and the defaults below ARE those spot counts, so out of the box every spot is used. Structures missing from the map use every spot too. |
| `RuinIngotPilesPerStructure` | Dictionary<string, MinMaxCount> | `DefaultIngotPileCounts()` |  |
| `RuinIngotTypes` | Dictionary<string, IngotPileType> | `see table below` | What an ingot pile contains, per metal: relative pick weight plus the pile size range. Keys are vanilla ingot codes (game:ingot-<key>). Defaults are HALF the pre-0.15 pile sizes. Add or remove metals freely; setting a structure's pile count to 0 disables its ingots. |
| `RuinLootRebalanceMigrated` | bool | `false` |  |
| `RuinSpawnerChance` | float | `0.85f` | Chance (0 to 1) that an auto spawner spot in a generated structure gets a creature spawner block. Spots explicitly typed serpent or kraken in a script (the /uh dungeon uses those) always place. |
| `RuinKrakenVariantChance` | float | `0.05f` | Chance (0 to 1) that a structure rolls kraken mode, which makes every auto spawner in it a kraken spawner instead of a serpent spawner. |
| `CreatureMaxY` | double | `-1` | Movement limits. The highest Y the creatures may swim to. -1 (the default) means the world's actual sea level, whatever the map height. Earlier versions hardcoded 110, which is sea level only on a default 256-tall world; on taller worlds it pinned every serpent far below the surface (or inside the sea floor), which is the "serpents won't rise / stuck at one depth / stuck underground" bug. Set an absolute Y here only if you deliberately want a different ceiling. |
| `CreatureMaxYMigrated` | bool | `false` | One-shot migration: configs still carrying the old hardcoded 110 are moved to the sea-level default on first load; see Validate(). |
| **Despawn system** | | | |
| `DespawnCheckIntervalSeconds` | float | `2f` |  |
| `DespawnAfterLandSeconds` | float | `30f` |  |
| `DespawnMaxDistance` | float | `500f` | Despawn immediately if creature drifts farther than this from its target player (e.g. player escaped by boat, or respawned far after death). A new creature can then spawn naturally near the player. |
| `SerpentCorpseFloats` | bool | `false` | Off (default): a killed serpent sinks to the sea floor and has to be dived for. On: the corpse rises to just under the surface so a kill from a boat can be harvested from the boat. The author prefers the sink; the switch is there for servers that do not. |
| **Sea serpent** | | | |
| `SerpentOrbitRadius` | float | `8f` |  |
| `SerpentOrbitSpeed` | float | `0.5f` |  |
| `SerpentStalkDurationMin` | float | `15f` |  |
| `SerpentStalkDurationMax` | float | `45f` |  |
| `SerpentRiseSpeed` | float | `0.04f` |  |
| `SerpentApproachSpeed` | float | `0.03f` |  |
| `SerpentAttackSpeed` | float | `0.08f` |  |
| `SerpentAttackDamage` | float | `10f` |  |
| `SerpentAttackCooldown` | float | `2f` |  |
| `SerpentAttackRange` | float | `2.5f` |  |
| `SerpentReStalkChance` | float | `0.5f` |  |
| `SerpentDamageTier` | int | `3` |  |
| **Kraken damage tier** | | | |
| `KrakenDamageTier` | int | `3` |  |
| **Serpent spiral approach** | | | |
| `SerpentInitialOrbitRadiusMin` | float | `30f` |  |
| `SerpentInitialOrbitRadiusMax` | float | `50f` |  |
| `SerpentSpiralStepDurationMin` | float | `5f` |  |
| `SerpentSpiralStepDurationMax` | float | `15f` |  |
| `SerpentSpiralReductionMin` | float | `5f` |  |
| `SerpentSpiralReductionMax` | float | `15f` |  |
| `DeepSerpentSpawnWeight` | float | `0.9f` | Deep serpent variant (stays deep, orbits in huge arcs, rises only to strike) 90% deep, 10% rust (aggressive surface) serpent |
| `RustSerpentTuningApplied` | bool | `false` | One-shot migration flag: configs written before the rust serpent rebalance carry the old 0.75 weight; the first load raises it to 0.9 and sets this flag, after which user tuning is left alone. |
| `RuinIngotIronMigrated` | bool | `false` | One-shot migration flag: configs whose ingot map still carries the exact old default steel entry get it swapped for iron; see Validate(). |
| `DeepSerpentStalkDepthMin` | float | `10f` | 10 blocks below surface |
| `DeepSerpentStalkDepthMax` | float | `30f` | 30 blocks below surface |
| `DeepSerpentOrbitRadius` | float | `15f` | final approach radius |
| `DeepSerpentInitialOrbitRadiusMin` | float | `50f` |  |
| `DeepSerpentInitialOrbitRadiusMax` | float | `80f` |  |
| `DeepSerpentSpiralStepDurationMin` | float | `15f` |  |
| `DeepSerpentSpiralStepDurationMax` | float | `30f` |  |
| `DeepSerpentSpiralReductionMin` | float | `5f` |  |
| `DeepSerpentSpiralReductionMax` | float | `15f` |  |
| `DeepSerpentMaxPitchRad` | float | `0.005f` | ~0.3° — nearly horizontal |
| `DeepSerpentPitchInterpRate` | float | `0.3f` | very slow tilt lerp |
| `DeepSerpentMaxVerticalSpeed` | float | `0.012f` | Vertical-motion smoothing for the damped controller. DeepSerpentMaxVerticalSpeed: hard cap on \|Motion.Y\| (blocks/tick units that VS physics uses).  Much smaller than horizontal speed so the body glides up/down very slowly even when dy is large. DeepSerpentVerticalSlewPerSec: max change in Motion.Y per second. Prevents snap from e.g. +0.01 to -0.01 between ticks, smoothing the moment where the serpent crosses through its target depth. |
| `DeepSerpentVerticalSlewPerSec` | float | `0.04f` |  |
| **Kraken body** | | | |
| `KrakenContactDamage` | float | `25f` |  |
| `KrakenContactRange` | float | `3f` |  |
| `KrakenAmbientTentacleCount` | int | `3` | 4 ambient risers + 4 ground wanderers + 1 attack tentacle = 9 total (3 ambient risers in older versions; the 4th makes the kraken feel larger when one of the 3 surface risers transitions into the attack tentacle slot via promotion). |
| `KrakenGroundTentacleCount` | int | `4` | Ground tentacles slither across the sea floor instead of rising. They make the kraken's footprint feel huge and obviously alive. |
| `KrakenTentacleSpawnRadius` | float | `5f` |  |
| `TentacleSpeedMultiplier` | float | `1f` | Global speed dial for EVERY tentacle, attack and ambient alike: rising, orbiting, wandering, pursuing and dragging all scale by it. 2 makes the whole kraken twice as quick. Raise this rather than editing the individual speeds below if you just want a livelier kraken, since it keeps their relative pacing intact. |
| **Attack tentacle** | | | |
| `TentacleIdleDuration` | float | `2f` |  |
| `TentacleReachSpeed` | float | `0.06f` | Pursuit speed: how fast the attack tentacle closes on the player once it has surfaced and started hunting. Scaled by TentacleSpeedMultiplier on top of this. |
| `TentacleGrabRange` | float | `2f` |  |
| `TentacleDragSpeed` | float | `2.0f` |  |
| `TentacleGrabYOffset` | float | `-0.5f` |  |
| `TentacleSinkDuration` | float | `30f` |  |
| `TentacleGrabDamageEnabled` | bool | `true` | Damage dealt on a timer while the tentacle has hold of the player, so a grab is a countdown rather than just an inconvenience. The kraken body's own contact damage skips mounted players, so this is what hurts you while you are being dragged. |
| `TentacleGrabDamage` | float | `1f` |  |
| `TentacleGrabDamageIntervalSeconds` | float | `2f` |  |
| `TentacleProximityAggroEnabled` | bool | `true` | Proximity aggro. When the kraken picks its next attack tentacle, a player standing within this many blocks of a tentacle gets that one (nearest wins) instead of a random one, and it skips the rise and linger build-up to hunt immediately. Swim into a tentacle and it is the one that comes for you. The promotion cooldown below is still honoured first, so this changes WHICH tentacle moves in, not when. |
| `TentacleProximityAggroRange` | float | `3f` |  |
| `TentacleRemainsEnabled` | bool | `true` | Where a dead tentacle's remains land, and what it leaves. Tentacles sink to the sea floor when the kraken body dies; on touching down (or on being killed outright) they leave a bone pile and rusty gears or scrap where they fell. |
| `TentacleSinkToFloorTimeout` | float | `30f` | Safety cap on the sink. If a tentacle somehow cannot reach the floor within this long it gives up, drops its remains on the floor beneath it anyway, and despawns. |
| `TentacleDeathSinkSpeed` | float | `0.08f` | Motion units, as with the other tentacle speeds: roughly 60x this many blocks per second, so 0.08 is a heavy limp arm coming down at about 5 blocks a second. From the surface to a deep floor that is ten to fifteen seconds, well inside the timeout above. |
| **Tentacle spline rendering** | | | |
| `TentacleArchHeightFactor` | float | `0.4f` |  |
| `TentacleTipLerpSpeed` | float | `5f` |  |
| `AmbientTentacleRiseSpeed` | float | `0.04f` | Ambient tentacle - rising and orbiting |
| `AmbientTentacleOrbitRadius` | float | `4f` |  |
| `AmbientTentacleOrbitSpeed` | float | `0.4f` |  |
| `AmbientTentacleBobAmplitude` | float | `1.5f` |  |
| `AmbientTentacleBobSpeed` | float | `0.7f` |  |
| `AmbientTentacleSurfaceRange` | float | `10f` |  |
| `TentacleRiseSpeed` | float | `0.025f` | Attack tentacle - rising and lingering |
| `TentacleLingerDuration` | float | `7f` |  |
| `TentacleSurfaceRange` | float | `10f` |  |
| **Shallow water retreat** | | | |
| `ShallowWaterThreshold` | int | `3` |  |
| `RetreatSpeed` | float | `0.06f` |  |
| `RetreatDuration` | float | `8f` |  |
| `TentacleStallDespawnSeconds` | float | `30f` | Stalling state: the attack tentacle enters Stalling the moment the player leaves the water OR mounts a boat. While stalling it either drifts slowly back toward the kraken body (player on land) or wanders in a slow circle around the player (player on boat) — it does NOT actively chase. If the player returns to a chase-able state (back in water, dismounted) before the timer expires the tentacle resumes Reaching from where it stalled. Otherwise the tentacle transitions to Retreating and despawns gracefully. |
| `TentacleStallOrbitRadius` | float | `7f` | 7 keeps the orbiting tentacle clear of a boat's hull (4 had it grinding along the side of any boat the player was sitting in). |
| `TentacleStallOrbitMigrated` | bool | `false` |  |
| `TentacleStallOrbitSpeed` | float | `0.3f` |  |
| `TentacleStallDriftSpeed` | float | `0.018f` | Drift speeds during Stalling. Slow on purpose so the visible intent reads as "lurking" rather than "leaving". |
| `TentacleStallBoatSpeed` | float | `0.03f` |  |
| `AmbientTentacleWanderRangeMin` | float | `5f` | Ambient tentacle wandering — used for both the post-rise scatter (when the attack tentacle starts pursuing) and for the 4 ground tentacles that crawl the sea floor from the moment they spawn. Range is centered on the kraken body. Min=5 so they pick varied distances (not always the perimeter), giving the floor-crawling motion a more natural, hypnotic feel as they reach + re-target continuously. Idle is short (1.5–4s) so they're nearly always in motion. |
| `AmbientTentacleWanderRangeMax` | float | `80f` |  |
| `AmbientTentacleWanderSpeed` | float | `0.05f` |  |
| `AmbientTentacleWanderIdleMin` | float | `1.5f` |  |
| `AmbientTentacleWanderIdleMax` | float | `4f` |  |
| `AmbientTentacleVerticalStepMax` | float | `0.1f` | Maximum per-tick Y change while a ground tentacle is wandering. When terrain rises/drops between two adjacent XZ steps, FindSeaFloorYBelow returns a different floor height — applying it raw makes the tip teleport up by the full delta, which the user described as "kind of teleport up when something is in the way". Clamping the per-tick Y delta turns that into a smooth climb (≈3 blocks/s at 30Hz). |
| `AmbientScatterSinkDuration` | float | `10f` | Post-scatter risers: when the attack tentacle starts pursuing, the 3 surface-orbiting risers leave Orbiting and run a two-phase sequence — first a brief sink (visually "they're done with the surface, retreating") and then a midwater wander that mirrors the ground tentacles' hypnotic crawl but in open water above the body.  ScatterSinkDuration: seconds spent descending toward midwater before the SurfaceWandering wander kicks in. SurfaceWanderRangeMin/Max: horizontal radius (blocks) around the kraken body for picking the next midwater target. SurfaceWanderDepthMax: how far below the surface the deepest midwater target can be (Y in [surface - DepthMax, surface]). |
| `AmbientSurfaceWanderRangeMin` | float | `5f` |  |
| `AmbientSurfaceWanderRangeMax` | float | `30f` |  |
| `AmbientSurfaceWanderDepthMax` | float | `20f` |  |
| `AmbientPromoteToAttackDelayMin` | float | `5f` | Promotion: when the attack tentacle dies, kraken body waits this long, then picks a surviving ambient tentacle (any of them, risers and ground crawlers alike) and respawns a new attack tentacle at its position, killing the chosen ambient. This is the breathing room you get after killing a grabber; TentacleProximityAggroRange decides which tentacle steps up once it elapses. |
| `AmbientPromoteToAttackDelayMax` | float | `20f` |  |
| `PromoteDelayMigrated` | bool | `false` | One-shot migration: configs written with the old 30/120 defaults are moved to the new, much shorter ones on first load; see Validate(). |
| `BiolumActive` | bool | `false` | Bioluminescence — pulsing glow that travels along tentacles |
| `BiolumPulsing` | bool | `false` |  |
| `BiolumPulseSpeed` | float | `1.4f` |  |
| `BiolumGlowMin` | int | `32` |  |
| `BiolumGlowMax` | int | `200` |  |
| `BiolumBodyGlowMin` | int | `16` |  |
| `BiolumBodyGlowMax` | int | `128` |  |
| `MonsterSoundsEnabled` | bool | `true` | Sea monster sounds. The serpent plays ambient dread sounds while it stalks, plus action stingers when it surfaces, dives, and bites. Only one sound plays at a time per player; a bite overrides whatever is playing. |
| `MonsterSoundVolume` | float | `1.0f` | Global volume applied to every monster sound. Each individual sound below has its own multiplier on top of this, so you can balance them. |
| `MonsterSoundBelow1Volume` | float | `1.0f` | Per-sound volume multipliers (multiplied with MonsterSoundVolume). 1.0 = no change. The screech has its own multiplier further down. |
| `MonsterSoundBelow2Volume` | float | `1.0f` |  |
| `MonsterSoundNearbyVolume` | float | `1.0f` |  |
| `MonsterSoundDiveVolume` | float | `1.0f` |  |
| `MonsterSoundBiteVolume` | float | `0.75f` |  |
| `MonsterSoundScreechVolume` | float | `1.0f` | The surface screech is the dramatic moment. It plays as a non-positional 2D sound (so it is clearly audible, not directional) for players within MonsterSoundScreechRange. Volume is full at the creature and drops with distance down to MonsterSoundScreechMinVolumeFactor of full at the edge. 1.0 and 0.2 since 0.20.0 (was 1.5 and 0.5: "too loud and does not fade" was the most common sound complaint). |
| `MonsterSoundScreechRange` | float | `25f` |  |
| `MonsterSoundScreechMinVolumeFactor` | float | `0.2f` |  |
| `ScreechVolumeMigrated` | bool | `false` |  |
| `MonsterSoundRange` | float | `48f` | Audible range (blocks) for the positional monster sounds. Players within this range of the creature receive and hear them. |
| `MonsterSoundSurfaceThreshold` | float | `2.5f` | Distance (blocks) below sea level within which the monster counts as "at the surface" for the nearby surface sound and the screech. |
| `MonsterSoundNearbyRange` | float | `10f` | The "nearby at surface" sound needs the monster within this range. |
| `MonsterSoundBelowMinRange` | float | `5f` | The two "below" ambient sounds will not play when the monster is closer than this (the nearby sound covers close range instead). |
| `MonsterSoundAmbientGapMin` | float | `14f` | Random gap (seconds) between ambient dread sounds, so they play occasionally rather than constantly. After each ambient sound the creature waits a random time in this range before the next one. |
| `MonsterSoundAmbientGapMax` | float | `34f` |  |

## Ruin loot tables

`RuinLootChestsPerStructure` and `RuinIngotPilesPerStructure` map each structure name (`ruin`, `portal`, `shipwreck-small`, `shipwreck-medium`, `shipwreck-huge`, `city`) to a `{ Min, Max }` count. `RuinIngotTypes` maps a metal name (any `game:ingot-<metal>`) to `{ Weight, CountMin, CountMax }`; a pile picks its metal by weight and its size uniformly between the two counts. Set a structure's pile count to 0 to give it no ingots.

Since 0.20.0 a pile's metal and size are rolled from the current config when a player first comes near the ruin, so changing `RuinIngotTypes` also changes every ruin nobody has visited yet. The number of chests and piles per structure is fixed when the chunk generates. Chests are vanilla stack randomizers and follow the vanilla loot tables; the chest count is the only lever for them.
