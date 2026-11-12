using System;
using System.Collections.Generic;

namespace UnderwaterHorrors;

/// <summary>Inclusive count range used by the per-structure loot settings.</summary>
public class MinMaxCount
{
    public int Min { get; set; }
    public int Max { get; set; }
}

/// <summary>
/// One entry in RuinIngotTypes: how likely this metal is to be picked for a
/// pile (Weight, relative to the other entries) and how many ingots the pile
/// holds (uniform between CountMin and CountMax, capped at 64, the ground
/// storage limit).
/// </summary>
public class IngotPileType
{
    public float Weight { get; set; } = 1f;
    public int CountMin { get; set; } = 6;
    public int CountMax { get; set; } = 20;
}

public class UnderwaterHorrorsConfig
{
    // ─── Debug ────────────────────────────────────────
    // Sends the mod's debug messages to chat. /uh debug sets it. Noisy, so
    // leave it off in normal play.
    public bool DebugLogging { get; set; } = false;

    // One-shot migration flag. Earlier mod versions shipped with
    // DebugLogging=true by default, which spammed chat for everyone.
    // The first load after upgrading flips DebugLogging to false and sets
    // this flag to true; from then on we never touch DebugLogging again,
    // so /uh debug on (or hand-editing the config) sticks across restarts.
    public bool DebugLoggingResetApplied { get; set; } = false;
    // Debug view from /uh glow: all the mod's creatures at full brightness.
    // Kept across restarts.
    public bool GlowDebugActive { get; set; } = false;
    // Debug view from /uh spectral: outlines through blocks around the mod's
    // creatures and spawner blocks. Kept across restarts.
    public bool SpectralDebugActive { get; set; } = false;

    // ─── Spawn system ────────────────────────────────────────
    // How often (seconds) each player in deep water is rolled for a spawn.
    public float SpawnCheckIntervalSeconds { get; set; } = 5f;
    // Water under the player must be at least this deep for anything to
    // spawn. Counted straight down from the surface; 50 is open ocean.
    public int MinSaltwaterDepth { get; set; } = 50;
    // Normally only saltwater counts, so a big inland lake never grows a
    // serpent. Turn this on and any water deep enough (MinSaltwaterDepth)
    // qualifies, fresh or salt. Ruins still generate only in the ocean.
    public bool AllowFreshwaterSpawns { get; set; } = false;
    // Chance (0 to 1) that one spawn check produces a creature. At 0.1 and
    // a 5 second interval a swimmer meets something within about a minute.
    public float SpawnChancePerCheck { get; set; } = 0.1f;
    // Global soft cap on living serpents/krakens. Once this many are alive in
    // the world, normal per-player spawning stops; instead at most ONE rare
    // extra spawn is rolled per spawn check across the whole server (see
    // OverCapSpawnChance). This keeps a second serpent very rare even with
    // many players in the ocean or in boats, rather than one per player.
    public int MaxLivingCreatures { get; set; } = 1;
    // Single global per-check chance to spawn an extra creature while already
    // at MaxLivingCreatures. One roll per check regardless of player count, so
    // the rate does not scale up on busy servers. Very low on purpose.
    public float OverCapSpawnChance { get; set; } = 0.002f;
    // Master switch for kraken in NATURAL spawn checks. /uh spawn kraken
    // always works; this just gates the random per-player spawn roll
    // from picking a kraken. The kraken is a heavyweight encounter (one
    // body plus 8 tentacles, each with a chain of 36 pieces, roughly 300
    // entities) so it is kept rare via the day/night chances below and
    // can be turned off entirely here if a server or client struggles
    // near it.
    public bool KrakenNaturalSpawnEnabled { get; set; } = true;
    // One-shot migration flag: configs written before natural kraken
    // spawning was enabled by default carry the old false value. The
    // first load flips KrakenNaturalSpawnEnabled to true and sets this
    // flag, after which user tuning is left alone.
    public bool KrakenSpawnTuningApplied { get; set; } = false;
    // Chance that a successful natural hostile spawn is a kraken
    // instead of a serpent. Rolled once per spawn, so serpent frequency
    // is barely affected: 0.05 means 1 in 20 hostile spawns is a kraken
    // by day, 0.10 means 1 in 10 at night. "Night" reuses the same
    // DayKrakenStartHour/DayKrakenEndHour window as the night glow, so
    // the more common night krakens are the bioluminescent ones.
    // (These replace the old SerpentSpawnWeight split.)
    public float KrakenSpawnChanceDay { get; set; } = 0.05f;
    // The same chance at night.
    public float KrakenSpawnChanceNight { get; set; } = 0.10f;

    // ─── Temporal storms ─────────────────────────────────────────────────
    // While a temporal storm rages, the sea turns on anyone out on it. The
    // settings below apply only during a storm; turn this off to keep
    // storms like any other time. /uh stormfrenzy on|off|auto forces it for
    // the session, for testing.
    public bool StormFrenzyEnabled { get; set; } = true;
    // The chance of NOT spawning is divided by this, for the spawn roll
    // (SpawnChancePerCheck) and the extra-creature roll
    // (SecondCreatureSpawnChance): with 5, 0.1 becomes 0.82 and 0.75
    // becomes 0.95. A chance set to 0 stays 0. Which creature comes
    // (kraken, anglerfish, serpent) keeps its usual odds, except the
    // leviathan below.
    public float StormSpawnOddsFactor { get; set; } = 5f;
    // Water under the player must be this deep for a serpent, instead of
    // MinSaltwaterDepth. The kraken and the anglerfish still need
    // MinSaltwaterDepth, and the leviathan open ocean.
    public int StormMinSaltwaterDepth { get; set; } = 15;
    // Hard cap on living creatures in the whole world during a storm, in
    // place of MaxLivingCreatures. Nothing spawns past it.
    public int StormMaxLivingCreatures { get; set; } = 5;
    // Every serpent that spawns is a rust serpent.
    public bool StormRustSerpentsOnly { get; set; } = true;
    // Every serpent and kraken tentacle moves this much faster while the
    // storm lasts (on top of SerpentSpeedMultiplier and
    // TentacleSpeedMultiplier). The leviathan and the anglerfish do not.
    public float StormSerpentSpeedMultiplier { get; set; } = 1.5f;
    // The same for kraken tentacles.
    public float StormTentacleSpeedMultiplier { get; set; } = 3f;
    // GiantSerpentSpawnChance during a storm. Its open-ocean test, its kill
    // cooldown and its behavior stay the same.
    public float StormGiantSerpentSpawnChance { get; set; } = 0.25f;

    /// <summary>A spawn chance during a storm: the chance of not spawning
    /// divided by StormSpawnOddsFactor. A chance set to 0 stays 0, so a roll
    /// turned off stays off.</summary>
    public float StormOdds(float chance) =>
        chance <= 0f ? 0f : 1f - (1f - Math.Min(chance, 1f)) / StormSpawnOddsFactor;

    // ─── Server tuning: one-stop serpent knobs ───────────────────────────
    // Max health applied to serpents when they spawn (already-living
    // creatures keep their current health; they despawn quickly anyway).
    // The entity JSON defaults are 200 rust and 100 deep, matching these.
    public float RustSerpentMaxHealth { get; set; } = 200f;
    // The same for the deep sea serpent.
    public float DeepSerpentMaxHealth { get; set; } = 100f;

    // Movement speed multiplier for both serpent variants. One knob that
    // scales every serpent motion together: approach, attack lunge, orbit,
    // rise, and retreat. 1 = default, 2 = twice as fast, 0.5 = half speed.
    // Values far from 1 can make orbits and turns look a little odd since
    // turn smoothing is tuned for the defaults.
    public float SerpentSpeedMultiplier { get; set; } = 1f;

    // Aggression multiplier for both serpent variants. Above 1 the serpent
    // closes its spiral in faster, waits less before striking, bites more
    // often (attack cooldown shrinks), is more likely to re-attack after a
    // strike and to turn on whoever hits it, and is less likely to flee
    // when hurt. Below 1 gives a more skittish serpent. 1 = default.
    // Attack damage and armor piercing are separate: SerpentAttackDamage
    // and SerpentDamageTier further down.
    public float SerpentAggressionMultiplier { get; set; } = 1f;

    /// <summary>Aggression-scaled wait: higher aggression = shorter delays.</summary>
    public float AggroTime(float seconds) => seconds / SerpentAggressionMultiplier;
    /// <summary>Aggression-scaled probability: higher aggression = more likely.</summary>
    public float AggroChance(float chance) => chance * SerpentAggressionMultiplier;
    /// <summary>Inverse-scaled probability for timid reactions like fleeing: higher aggression = less likely.</summary>
    public float AggroInverseChance(float chance) => chance / SerpentAggressionMultiplier;

    // Hour the kraken's day starts. Day/night threshold for kraken bioluminescence. Krakens spawning
    // during [DayKrakenStartHour, DayKrakenEndHour) on the VS calendar
    // get NO glow; outside this window they get a pulsing cyan glow
    // matching biolumtest mode 10. Defaults: 6 AM to 10 PM = "day".
    public float DayKrakenStartHour { get; set; } = 6f;
    // Hour the kraken's day ends.
    public float DayKrakenEndHour { get; set; } = 22f;

    // Fewest blocks below the player a natural serpent spawns. The spot is
    // then moved into open water (SerpentSpawnWaterClearance).
    public int SerpentSpawnDepthMin { get; set; } = 25;
    // Most blocks below the player a natural serpent spawns.
    public int SerpentSpawnDepthMax { get; set; } = 40;
    // Farthest a natural serpent spawns from the player, sideways, in
    // blocks.
    public int SerpentSpawnHorizontalRadiusMax { get; set; } = 50;

    // How many blocks of clear water a serpent keeps under itself. A
    // serpent is a single entity but its body reaches 12-17 blocks out
    // along its facing axis, so a spawn point that merely happens to be
    // water can still bury most of the animal in the sea floor. At spawn
    // the whole body's footprint is checked and the point is raised until
    // it clears; in flight the center is clamped so the AI cannot swim it
    // back into the ground. Set to 0 to disable both.
    public int SerpentGroundClearance { get; set; } = 2;

    // How far a spawn point may be raised looking for that clearance.
    // Spawner blocks sit in sea-floor ruins, so the lift is often most of
    // the way to the surface; the search stops at the waterline regardless.
    public int SerpentSpawnMaxRise { get; set; } = 40;

    // Open-water requirement at the spawn point: the serpent's center must
    // have this many blocks of water on every side (above, below, and all
    // around). Spawn placement raycasts down from the sky to find the water
    // column and prefers the highest position that satisfies this, so a
    // serpent starts well clear of any sand or ruin walls that could trap
    // it. Spawner blocks try nearby columns when their own is blocked, and
    // only fall back to a best-effort spawn when every one is.
    public int SerpentSpawnWaterClearance { get; set; } = 5;

    // Kraken spawn horizontal radius. Spawns on the sea floor within
    // this radius of the player (vs always directly below).
    public int KrakenSpawnHorizontalRadiusMax { get; set; } = 20;

    // Proximity-based aggro: player getting near the serpent forces
    // a transition to Attacking regardless of spiral state.
    // HeadTriggerRange: head-distance that immediately triggers aggro.
    // BodyTriggerRange: body-distance at which the dwell timer starts.
    // BodyDwellDurationMin/Max: random dwell time (seconds) before aggro
    //   kicks in if the player stays within body range.
    public float SerpentProximityHeadTriggerRange { get; set; } = 6f;
    // Blocks from its body at which the wait below starts.
    public float SerpentProximityBodyTriggerRange { get; set; } = 5f;
    // Shortest wait in seconds before it turns on a player who stays that
    // close.
    public float SerpentProximityBodyDwellMin { get; set; } = 5f;
    // Longest wait in seconds before it turns on a player who stays that
    // close.
    public float SerpentProximityBodyDwellMax { get; set; } = 15f;

    // The rust serpent's usual depth below the surface while it circles, in
    // blocks. Deeper is harder to see.
    public float SerpentNormalSubmergeDepth { get; set; } = 5f;
    // Depth during a surface peek, when a serpent briefly rises into view, and
    // whenever its target rides a boat (both serpents rise to boat level).
    public float SerpentSurfaceSubmergeDepth { get; set; } = 1f;
    // Chance (0 to 1), each spiral step, that the rust serpent circles at
    // surface depth for that step.
    public float SerpentSurfacePeekChance { get; set; } = 0.4f;
    // The same chance for the deep sea serpent, which mostly stays deep.
    public float DeepSerpentSurfacePeekChance { get; set; } = 0.1f;

    // Boat boredom: while the player stays in a boat, the serpent circles for
    // a random time in this range, then gives up and retreats so a single
    // creature doesn't haunt forever (a new one can spawn later). The time is
    // rolled once when the serpent starts circling the boat.
    public float BoatBoredomMinSeconds { get; set; } = 90f;
    // Longest it circles a boat before it gives up, in seconds.
    public float BoatBoredomMaxSeconds { get; set; } = 240f;

    // Boat surface/submerge oscillation: while the player is in a boat, the
    // serpent surfaces and circles for a random SurfaceDuration, then sinks
    // below the water for a random SubmergeDuration, looping. (This is the
    // up/down cycle; the boat boredom above is the separate eventual give-up.)
    public float BoatSurfaceDurationMin { get; set; } = 5f;
    // Longest surface phase, in seconds.
    public float BoatSurfaceDurationMax { get; set; } = 15f;
    // Shortest time below the water between surface phases, in seconds.
    public float BoatSubmergeDurationMin { get; set; } = 15f;
    // Longest time below the water between surface phases, in seconds.
    public float BoatSubmergeDurationMax { get; set; } = 75f;

    // Chance per spawn check that a SECOND creature spawns even if the
    // player already has one. It looks after itself: the mod does not count
    // it as that player's creature.
    public float SecondCreatureSpawnChance { get; set; } = 0.005f;

    // Fastest the rust serpent moves up or down while circling, in blocks per
    // 1/60 second. Any faster looked jittery on the long body.
    public float SerpentMaxVerticalSpeed { get; set; } = 0.012f;
    // Most that vertical speed may change per second, so it does not bob.
    public float SerpentVerticalSlewPerSec { get; set; } = 0.04f;

    // Maximum turning speed (degrees per second) for both serpent
    // variants. Caps how fast the body heading can sweep, so target
    // jumps (spiral steps, state changes) produce a wide smooth arc
    // instead of a snap turn. The attack value applies while the
    // serpent is charging a player and needs to track them.
    public float SerpentTurnSpeedDegPerSec { get; set; } = 45f;
    // The same limit while it charges a player.
    public float SerpentAttackTurnSpeedDegPerSec { get; set; } = 120f;

    // Flee-after-damage: every hit rolls (damage x chance-per-damage)
    // to make the serpent break off, swim away, and resume circling at
    // its wide initial orbit. 0.10 means 1 damage = 10% flee chance and
    // 5 damage = 50%, so real weapons drive it off far more reliably
    // than fists. The rust (aggressive surface) serpent uses the Rust
    // value, which is halved so it presses the attack harder.
    public float SerpentFleeChancePerDamage { get; set; } = 0.10f;
    // The same chance for the rust serpent, half as likely.
    public float RustSerpentFleeChancePerDamage { get; set; } = 0.05f;
    // After a successful flee roll, proximity aggro and stalk-timeout
    // attacks are suppressed for this many seconds so the serpent
    // actually leaves instead of instantly re-aggroing off the player
    // being inside head range.
    public float SerpentFleeAggroSuppressSeconds { get; set; } = 12f;
    // Provoke-on-hit: when a hit neither kills nor triggers a flee and
    // the serpent is NOT already attacking (circling, stalking, or
    // showing off at the surface), it rolls this chance to turn on its
    // attacker immediately. This also overrides the post-flee aggro
    // suppression, so chasing a fleeing serpent and hitting it again
    // can provoke a counterattack, which can then flee again.
    public float SerpentProvokeChance { get; set; } = 0.5f;
    // How long a provoked serpent stays enraged. While enraged it will
    // not roll flee-from-damage and will not disengage after a strike,
    // so it keeps pressing repeat attacks for at least this long.
    public float SerpentEnrageSeconds { get; set; } = 10f;
    // What happens when a player swings an Animal Cages cage at one of these
    // creatures. "rage" (default): the cage catches nothing and the creature
    // turns on that player. "refuse": the cage catches nothing and the creature
    // does not react. "allow": Animal Cages decides, following its own config.
    public string CageAttemptResponse { get; set; } = "rage";
    public const string CageRage = "rage", CageRefuse = "refuse", CageAllow = "allow";
    // Observer mode for creative players: when true, the creatures
    // never attack a player who is in creative or spectator mode.
    // Serpents skip every attack trigger (spiral-complete strike,
    // proximity aggro, stalk-timeout, provoke-on-hit) and break off
    // mid-attack; the kraken deals no contact damage to such players
    // and its attack tentacle sways in place instead of chasing and
    // grabbing. Everything else stays natural: circling, surface
    // peeks, ambient tentacles, sounds, and the usual despawn rules
    // when the player leaves the water. On by default since 0.20.0 (the
    // 0.12 changelog announced it as on, and every report since was a
    // creative player who did not know it was a switch). Servers that
    // want creative admins hunted can turn it off, or use /uh observer.
    public bool IgnoreCreativePlayers { get; set; } = true;
    // Off (default): a player riding anything (a boat, a raft or a creature)
    // is safe from the serpents and the kraken, which circle and give up. On:
    // they attack riders wherever the water under them is at least
    // ShallowWaterThreshold deep. Serpents bite the rider and leave the boat alone,
    // the kraken's tentacle pulls the rider off into the water, and the kraken
    // body's contact damage hits them too. A rider on land or in the shallows
    // is as safe as anyone on foot there. The Rust Leviathan and the anglerfish
    // attack boats either way, by their own rules.
    public bool AttackMountedPlayers { get; set; } = false;
    // One-shot: configs written before 0.20.0 carry the old false default
    // and are moved to true once; after that the value is never touched.
    public bool IgnoreCreativePlayersMigrated { get; set; } = false;

    // ─── Spawner blocks ────────────────────────────────────────
    // Serpent spawner block. A block (it looks like a vanilla locust nest
    // cage) that watches for a player who is in the water and
    // within SpawnerTriggerRange blocks, then spawns one rust serpent and
    // removes itself. When that serpent despawns on its own (the player
    // left the water, so it retreats) the block reappears in the exact
    // same spot and re-arms. If the serpent is killed instead, the block
    // stays gone. This gives persistent, place-anywhere serpent encounters
    // in dedicated areas such as the /uh dungeon ruins. When
    // IgnoreCreativePlayers is on, a creative or spectator player standing
    // in range will not arm the spawner.
    public float SpawnerTriggerRange { get; set; } = 40f;

    // How long the target player must be out of the water before a
    // spawner-spawned creature despawns (and its block reappears). Serpents
    // already retreat and despawn on their own via their AI when the player
    // leaves the water; the kraken has no such rule, so this timer is what
    // makes a spawner-kraken sink away and its block return. Kept short so
    // the encounter resets soon after the player gives up and leaves.
    public float SpawnerDespawnAfterLeaveSeconds { get; set; } = 20f;

    // ─── Underwater ruins worldgen ───────────────────────────────────────
    // Sunken ruins, portals, shipwrecks and a drowned city that generate
    // rarely on deep ocean floors, each with collapsed loot chests and a
    // chance of a serpent (or, rarely, kraken) spawner inside.
    public bool UnderwaterRuinsEnabled { get; set; } = true;
    // Average of one ruin per this many ocean chunk columns. Higher is rarer:
    // 107 is about one column in 107, and 10 gives about ten times as many
    // ruins, not fewer. Only chunks generated after the change are affected.
    public int RuinRarity { get; set; } = 107;
    // One-shot migration: configs written with the old sparser default (320)
    // are bumped to the new one on first load; see Validate().
    public bool RuinRarityMigrated { get; set; } = false;
    // The sea floor under a ruin's center must be at least this many blocks
    // below sea level. Each structure also needs enough depth to fit fully
    // underwater, so the shipwrecks need 19 to 30 and the city 18. Water at
    // least half this deep (4 at the least) must reach the structure's edge,
    // checked out to 24 blocks at most. At the default a small ruin can sit on
    // a steep coast. Raise this to keep ruins further out in new chunks.
    public int RuinMinOceanDepth { get; set; } = 12;
    // The glowing ghostlight orbs placed inside generated ruins. Turn this
    // off to generate ruins without any lights (existing ruins keep the
    // lights they already have; remove those by breaking the blocks).
    public bool RuinGhostlightsEnabled { get; set; } = true;
    // Loot amount per structure type. A target count is rolled between Min
    // and Max each time a structure generates and that many randomly chosen
    // scripted spots are used; the rest stay empty. Counts are capped at how
    // many spots the structure actually defines. Structures missing from the
    // map use every spot. Fixed when the chunk generates.
    public Dictionary<string, MinMaxCount> RuinLootChestsPerStructure { get; set; } = DefaultChestCounts();
    // Ingot piles per structure, in the same form as the chest counts above.
    // See the table below.
    public Dictionary<string, MinMaxCount> RuinIngotPilesPerStructure { get; set; } = DefaultIngotPileCounts();

    // What an ingot pile contains, per metal: relative pick weight plus the
    // pile size range. Keys are vanilla ingot codes (game:ingot-<key>). Add
    // or remove metals freely; setting a structure's pile count to 0
    // disables its ingots.
    public Dictionary<string, IngotPileType> RuinIngotTypes { get; set; } = DefaultIngotTypes();

    // 0.20.0 cut the big structures to about a third of the 0.15 counts.
    // At the old numbers a single huge wreck averaged about two gold piles
    // of 24 to 32 ingots plus 270 other ingots, which is where the "70
    // gold from one ship" reports came from.
    public static Dictionary<string, MinMaxCount> DefaultChestCounts() => new()
    {
        ["ruin"] = new MinMaxCount { Min = 10, Max = 10 },
        ["portal"] = new MinMaxCount { Min = 9, Max = 9 },
        ["shipwreck-small"] = new MinMaxCount { Min = 6, Max = 6 },
        ["shipwreck-medium"] = new MinMaxCount { Min = 8, Max = 8 },
        ["shipwreck-huge"] = new MinMaxCount { Min = 10, Max = 10 },
        ["city"] = new MinMaxCount { Min = 16, Max = 16 },
    };

    public static Dictionary<string, MinMaxCount> DefaultIngotPileCounts() => new()
    {
        ["ruin"] = new MinMaxCount { Min = 3, Max = 3 },
        ["portal"] = new MinMaxCount { Min = 3, Max = 3 },
        ["shipwreck-small"] = new MinMaxCount { Min = 4, Max = 4 },
        ["shipwreck-medium"] = new MinMaxCount { Min = 5, Max = 5 },
        ["shipwreck-huge"] = new MinMaxCount { Min = 8, Max = 8 },
        ["city"] = new MinMaxCount { Min = 6, Max = 6 },
    };

    // The counts every 0.15 to 0.19 config was written with. Validate()
    // swaps an entry for the new default only when it still holds exactly
    // this value, so a server owner's own numbers are left alone.
    private static readonly Dictionary<string, int> OldChestCounts = new()
    {
        ["ruin"] = 14, ["portal"] = 13, ["shipwreck-small"] = 8,
        ["shipwreck-medium"] = 15, ["shipwreck-huge"] = 30, ["city"] = 49,
    };
    private static readonly Dictionary<string, int> OldIngotPileCounts = new()
    {
        ["ruin"] = 5, ["portal"] = 4, ["shipwreck-small"] = 6,
        ["shipwreck-medium"] = 12, ["shipwreck-huge"] = 24, ["city"] = 18,
    };
    public bool RuinLootRebalanceMigrated { get; set; } = false;

    // No steel by default: finding worked steel in a pre-steel-age wreck
    // upset the balance for several servers, so the rare small pile is iron
    // now. Servers that want steel back just add a "steel" entry here.
    public static Dictionary<string, IngotPileType> DefaultIngotTypes() => new()
    {
        ["copper"] = new IngotPileType { Weight = 25, CountMin = 6, CountMax = 20 },
        ["silver"] = new IngotPileType { Weight = 25, CountMin = 6, CountMax = 20 },
        ["molybdochalkos"] = new IngotPileType { Weight = 26, CountMin = 6, CountMax = 20 },
        ["gold"] = new IngotPileType { Weight = 9, CountMin = 6, CountMax = 12 },
        ["iron"] = new IngotPileType { Weight = 15, CountMin = 3, CountMax = 8 },
    };
    // Chance (0 to 1) that an auto spawner spot in a generated structure
    // gets a creature spawner block. Spots explicitly typed serpent or
    // kraken in a script (the /uh dungeon uses those) always place.
    public float RuinSpawnerChance { get; set; } = 0.85f;
    // Chance (0 to 1) that a structure rolls kraken mode, which makes every
    // auto spawner in it a kraken spawner instead of a serpent spawner.
    public float RuinKrakenVariantChance { get; set; } = 0.05f;

    // Movement limits. The highest Y the creatures may swim to. -1 (the
    // default) means the world's actual sea level, whatever the map height.
    // Earlier versions hardcoded 110, which is sea level only on a default
    // 256-tall world; on taller worlds it pinned every serpent far below
    // the surface (or inside the sea floor), which is the "serpents won't
    // rise / stuck at one depth / stuck underground" bug. Set an absolute
    // Y here only if you deliberately want a different ceiling.
    public double CreatureMaxY { get; set; } = -1;
    // One-shot migration: configs still carrying the old hardcoded 110 are
    // moved to the sea-level default on first load; see Validate().
    public bool CreatureMaxYMigrated { get; set; } = false;

    // ─── Despawn system ────────────────────────────────────────
    // Seconds between the sweeps that decide which creatures to remove.
    public float DespawnCheckIntervalSeconds { get; set; } = 2f;
    // Seconds a creature stays after its target leaves the water. Then it is
    // removed.
    public float DespawnAfterLandSeconds { get; set; } = 30f;
    // Despawn immediately if creature drifts farther than this from its
    // target player (e.g. player escaped by boat, or respawned far after
    // death). A new creature can then spawn naturally near the player.
    public float DespawnMaxDistance { get; set; } = 500f;

    // Off (default): a killed serpent sinks to the sea floor and has to be
    // dived for. On: the corpse rises to just under the surface so a kill
    // from a boat can be harvested from the boat.
    public bool SerpentCorpseFloats { get; set; } = false;

    // ─── Sea serpents, rust and deep ────────────────────────────────────────
    // The rust serpent's closest circle, in blocks. When its spiral reaches
    // this it attacks.
    public float SerpentOrbitRadius { get; set; } = 8f;
    // How fast both serpents circle at their closest orbit. Wider orbits
    // turn more slowly in proportion.
    public float SerpentOrbitSpeed { get; set; } = 0.5f;
    // Shortest time the rust serpent circles after backing off from a bite,
    // in seconds.
    public float SerpentStalkDurationMin { get; set; } = 15f;
    // Longest time the rust serpent circles after backing off from a bite,
    // in seconds.
    public float SerpentStalkDurationMax { get; set; } = 45f;
    // How fast both serpents rise toward the surface, in blocks per 1/60
    // second. 0.04 is 2.4 blocks a second.
    public float SerpentRiseSpeed { get; set; } = 0.04f;
    // How fast both serpents swim toward their surfacing spot, in the same
    // units. Circling and retreating use twice this.
    public float SerpentApproachSpeed { get; set; } = 0.03f;
    // How fast both serpents charge a player, in the same units. 0.08 is 4.8
    // blocks a second.
    public float SerpentAttackSpeed { get; set; } = 0.08f;
    // Damage of one bite, for both serpents.
    public float SerpentAttackDamage { get; set; } = 10f;
    // Seconds between bites, for both serpents.
    public float SerpentAttackCooldown { get; set; } = 2f;
    // Not used yet.
    public float SerpentAttackRange { get; set; } = 2.5f;
    // Chance (0 to 1) that the rust serpent backs off to circle after a bite
    // instead of pressing on.
    public float SerpentReStalkChance { get; set; } = 0.5f;
    // Armor piercing tier of both serpents' bites.
    public int SerpentDamageTier { get; set; } = 3;

    // Armor piercing tier of the kraken's contact damage.
    public int KrakenDamageTier { get; set; } = 3;

    // ─── Rust serpent spiral ────────────────────────────────────────
    // It closes in on its prey in a shrinking spiral, one step at a time.
    // Smallest circle it starts from, in blocks.
    public float SerpentInitialOrbitRadiusMin { get; set; } = 30f;
    // Largest circle it starts from, in blocks.
    public float SerpentInitialOrbitRadiusMax { get; set; } = 50f;
    // Shortest time for one step, in seconds.
    public float SerpentSpiralStepDurationMin { get; set; } = 5f;
    // Longest time for one step, in seconds.
    public float SerpentSpiralStepDurationMax { get; set; } = 15f;
    // Fewest blocks the circle tightens in one step.
    public float SerpentSpiralReductionMin { get; set; } = 5f;
    // Most blocks the circle tightens in one step.
    public float SerpentSpiralReductionMax { get; set; } = 15f;

    // ─── Deep sea serpent ────────────────────────────────────────
    // It stays deep, circles in huge arcs and rises only to strike.
    // Share of natural serpents that are this one. 0.9 is 90% deep, 10% rust.
    public float DeepSerpentSpawnWeight { get; set; } = 0.9f;
    // One-shot migration flag: configs written before the rust serpent
    // rebalance carry the old 0.75 weight; the first load raises it to
    // 0.9 and sets this flag, after which user tuning is left alone.
    public bool RustSerpentTuningApplied { get; set; } = false;
    // One-shot migration flag: configs whose ingot map still carries the
    // exact old default steel entry get it swapped for iron; see Validate().
    public bool RuinIngotIronMigrated { get; set; } = false;
    // Shallowest it circles, in blocks below the surface. It picks one depth
    // for its life.
    public float DeepSerpentStalkDepthMin { get; set; } = 10f;
    // Deepest it circles, in blocks below the surface.
    public float DeepSerpentStalkDepthMax { get; set; } = 30f;
    // Its closest circle, in blocks. When its spiral reaches this it attacks.
    public float DeepSerpentOrbitRadius { get; set; } = 15f;
    // Smallest circle it starts from, in blocks.
    public float DeepSerpentInitialOrbitRadiusMin { get; set; } = 50f;
    // Largest circle it starts from, in blocks.
    public float DeepSerpentInitialOrbitRadiusMax { get; set; } = 80f;
    // Shortest time for one spiral step, in seconds.
    public float DeepSerpentSpiralStepDurationMin { get; set; } = 15f;
    // Longest time for one spiral step, in seconds.
    public float DeepSerpentSpiralStepDurationMax { get; set; } = 30f;
    // Fewest blocks the circle tightens in one step.
    public float DeepSerpentSpiralReductionMin { get; set; } = 5f;
    // Most blocks the circle tightens in one step.
    public float DeepSerpentSpiralReductionMax { get; set; } = 15f;
    // Not used yet.
    public float DeepSerpentMaxPitchRad { get; set; } = 0.005f;
    // Not used yet.
    public float DeepSerpentPitchInterpRate { get; set; } = 0.3f;

    // Fastest it moves up or down while circling, in blocks per 1/60 second.
    // Much slower than it swims, so the long body glides.
    public float DeepSerpentMaxVerticalSpeed { get; set; } = 0.012f;
    // Most that vertical speed may change per second, so it does not snap as
    // it crosses its target depth.
    public float DeepSerpentVerticalSlewPerSec { get; set; } = 0.04f;

    // ─── Giant serpent ───────────────────────────────────────────────────
    // A very rare rust serpent five times the size (the entity size in
    // giantserpent.json). It never surfaces to hiss like the rust serpent:
    // it circles far below its prey, then rises mouth first from the deep.
    // It bites swimmers, swallows rafts whole and breaks bigger boats in a
    // few bites. It never leaves the water: when it strikes, only its head
    // and the arch of its back break the surface.
    // Patches of cyan bioluminescence grow over its hide and light the
    // water around it.
    // Chance that a natural serpent spawn in open ocean is the giant
    // instead. Open ocean: the player's own column and four random columns
    // within GiantSerpentOpenOceanRadius blocks are all saltwater at least
    // GiantSerpentOpenOceanDepth deep. 0 turns natural giants off; /uh spawn
    // giantserpent always works.
    public float GiantSerpentSpawnChance { get; set; } = 0.05f;
    // How far around the player the open-ocean test looks, in blocks.
    public float GiantSerpentOpenOceanRadius { get; set; } = 50f;
    // How deep the saltwater must be at the player and at every tested column.
    public int GiantSerpentOpenOceanDepth { get; set; } = 50;
    public bool GiantSerpentChanceMigrated { get; set; } = false;
    // Its health. It can only be hit while its body passes the surface after
    // a strike, about 20 seconds a pass; a player must finish it before it
    // sinks their boat and kills them in the water. Giants already out keep
    // the health they came with.
    public float GiantSerpentMaxHealth { get; set; } = 1500f;
    public bool GiantSerpentHealth1500Migrated { get; set; } = false;
    // Once its health falls below this fraction it flees, once: it swims off
    // to GiantSerpentFleeDistance blocks from its prey, circles there until
    // GiantSerpentFleeSeconds have passed, then comes back and fights to the
    // death. 0 turns fleeing off.
    public float GiantSerpentFleeHealthFraction { get; set; } = 0.25f;
    // Seconds from the start of its flight until it comes back.
    public float GiantSerpentFleeSeconds { get; set; } = 60f;
    // How far from its prey it flees, in blocks. Kept under 80% of
    // DespawnMaxDistance, so fleeing never gets it removed. At 200 its deep
    // silhouette (drawn out to 230 blocks) stays in sight.
    public float GiantSerpentFleeDistance { get; set; } = 200f;
    // Damage of a bite on a swimmer, and its armor piercing tier.
    public float GiantSerpentBiteDamage { get; set; } = 20f;
    // Armor piercing tier of its bite.
    public int GiantSerpentDamageTier { get; set; } = 5;
    public bool GiantSerpentTierMigrated { get; set; } = false;
    // Whether the giant swallows rafts and breaks other boats. Off, boats
    // are never harmed: every bite on a boat hurts its riders by
    // GiantSerpentRiderDamage instead, and they stay aboard.
    public bool GiantSerpentDestroysBoats { get; set; } = true;
    // Damage to each rider when the giant swallows or breaks their boat.
    // They land in the water, where it can come back for them. With
    // GiantSerpentDestroysBoats off, the damage of each bite on their boat.
    public float GiantSerpentRiderDamage { get; set; } = 8f;
    // Bites it takes to break a boat that is not a raft (the sailboat).
    // A raft always goes down in one gulp.
    public int GiantSerpentBitesToSinkBoat { get; set; } = 3;
    // Damage on a boat fades, since nothing can repair it. One bite is
    // forgiven for every this many in-game hours since the boat's last bite,
    // so 24 is one bite a day. 0 means bites stay on the boat for good. The
    // count and the time are kept on the boat, in the save.
    public float GiantSerpentBoatBiteForgiveHours { get; set; } = 24f;
    // Swimming speeds in blocks per second: cruising and circling, and the
    // final rush at its prey.
    public float GiantSerpentCruiseSpeed { get; set; } = 7f;
    // Speed of the final rush at its prey, in blocks per second.
    public float GiantSerpentStrikeSpeed { get; set; } = 24f;
    // Seconds it circles below before the first strike, and between strikes.
    public float GiantSerpentStalkSecondsMin { get; set; } = 25f;
    // Longest it circles before its first strike, in seconds.
    public float GiantSerpentStalkSecondsMax { get; set; } = 50f;
    // Shortest it circles between strikes, in seconds.
    public float GiantSerpentStrikeCooldownMin { get; set; } = 15f;
    // Longest it circles between strikes, in seconds.
    public float GiantSerpentStrikeCooldownMax { get; set; } = 30f;
    // It only strikes prey over water at least this deep. When its prey is
    // over shallower water or ashore, it swims off into the deep, and comes
    // back if its prey returns to deep water.
    public int GiantSerpentMinStrikeDepth { get; set; } = 20;
    // Seconds its prey can stay ashore before the giant is removed. It is
    // also removed once it has swum out of sight of every player.
    public float GiantSerpentDespawnAfterLandSeconds { get; set; } = 120f;
    // It swims off after this many seconds even if its prey is still at sea,
    // unless a player has hurt it in the last two minutes: then it stays and
    // fights.
    public float GiantSerpentMaxStaySeconds { get; set; } = 480f;
    // Pitch of its bite and other sounds: the rust serpent's, played slower
    // and deeper. 1 is the rust serpent's own pitch.
    public float GiantSerpentSoundPitch { get; set; } = 0.6f;
    // After a player kills one, no natural giant comes anywhere on the server
    // for this many real minutes. The time is kept in the save, so it counts
    // on while the server is off. /uh giantcooldown shows or clears it;
    // /uh spawn giantserpent ignores it.
    public float GiantSerpentKillCooldownMinutes { get; set; } = 120f;
    // A killed giant breaks apart into its 22 body segments, which sink to
    // the sea floor. One right click with a knife cuts a segment open and
    // spills its share into the water, a random count between the two
    // numbers: rusty gears, temporal gears, 1 to 3 of every kind of Jonas
    // part, raw fish and bones.
    public int GiantSerpentPieceRustyGearsMin { get; set; } = 10;
    // Most rusty gears in one segment.
    public int GiantSerpentPieceRustyGearsMax { get; set; } = 15;
    // Fewest temporal gears in one segment.
    public int GiantSerpentPieceTemporalGearsMin { get; set; } = 1;
    // Most temporal gears in one segment.
    public int GiantSerpentPieceTemporalGearsMax { get; set; } = 4;
    // Fewest of each kind of Jonas part in one segment.
    public int GiantSerpentPieceJonasPartsMin { get; set; } = 1;
    // Most of each kind of Jonas part in one segment.
    public int GiantSerpentPieceJonasPartsMax { get; set; } = 3;
    // Fewest raw fish in one segment.
    public int GiantSerpentPieceFishMin { get; set; } = 20;
    // Most raw fish in one segment.
    public int GiantSerpentPieceFishMax { get; set; } = 50;
    // Fewest bones in one segment.
    public int GiantSerpentPieceBonesMin { get; set; } = 20;
    // Most bones in one segment.
    public int GiantSerpentPieceBonesMax { get; set; } = 50;
    // In-game hours its segments lie on the sea floor before they crumble,
    // each leaving a pile of bones. 0 keeps them for good.
    public float GiantSerpentPieceDecayHours { get; set; } = 48f;

    // ─── Giant anglerfish ────────────────────────────────────────────────
    // A gigantic angler some 100 blocks long. Like a wobbegong it lies on
    // the sea floor near a coast and creeps toward a player heading for
    // land, casting a glowing temporal gear on a rope from the pole on its
    // head. When its prey comes close to its mouth, it swoops and gulps.
    // The first blow on the gear knocks a temporal gear loose and sends the
    // fish away; breaking the gear (10 health) drops another.
    // Chance that a natural spawn at night is the anglerfish instead. Night
    // is outside DayKrakenStartHour..DayKrakenEndHour. It needs a coast
    // within AnglerfishCoastRange blocks of the player with water at least
    // AnglerfishMinWaterDepth deep off it. 0 turns natural anglerfish off;
    // /uh spawn anglerfish always works. A natural one sinks back into the
    // deep at daybreak.
    public float AnglerfishNightSpawnChance { get; set; } = 0.005f;
    // A natural anglerfish leaves after this many in-game hours, even with a
    // player close by (it also leaves at daybreak).
    public float AnglerfishMaxStayHours { get; set; } = 3f;
    // How far from the player it looks for a coast, in blocks.
    public float AnglerfishCoastRange { get; set; } = 160f;
    // Water it needs to lie in, in blocks from the surface to the floor.
    public int AnglerfishMinWaterDepth { get; set; } = 40;
    // Whether its lure gives temporal gears: one on the first blow and one
    // when it breaks. Off, the lure gives none. Killing the fish still gives
    // the temporal gear in its carcass.
    public bool AnglerfishLureDropsGears { get; set; } = true;
    // A player on land at night by deep coastal water is checked about this
    // often (seconds): a few columns of water around them are tested, and
    // with AnglerfishShoreSpawnChance an anglerfish comes to lie in one,
    // baiting them from the shore. 0 turns these off.
    public float AnglerfishShoreCheckSeconds { get; set; } = 60f;
    // Chance (0 to 1) that one such check brings an anglerfish. 0 turns
    // these off.
    public float AnglerfishShoreSpawnChance { get; set; } = 0.025f;
    // It swoops once its prey is this close to its mouth, in blocks, or
    // within that range sideways and up to AnglerfishStrikeReachUp blocks
    // above its mouth: it rises from the floor at prey on the surface.
    public float AnglerfishStrikeRange { get; set; } = 28f;
    // How far above its mouth it can reach prey, in blocks.
    public float AnglerfishStrikeReachUp { get; set; } = 60f;
    // Speed of the swoop, in blocks per second.
    public float AnglerfishStrikeSpeed { get; set; } = 40f;
    // Damage of the gulp.
    public float AnglerfishBiteDamage { get; set; } = 50f;
    // Armor piercing tier of the gulp.
    public int AnglerfishDamageTier { get; set; } = 3;
    public bool AnglerfishGiantMigrated { get; set; } = false;
    // One-shot migration flag for the ten times rarer anglerfish; see Validate().
    public bool AnglerfishRarityMigrated { get; set; } = false;

    // ─── Kraken body ────────────────────────────────────────
    // Damage each second to a player touching the kraken's body.
    public float KrakenContactDamage { get; set; } = 25f;
    // How close to the body counts as touching it, in blocks.
    public float KrakenContactRange { get; set; } = 3f;
    // Arms that rise to the surface and circle. With the ground tentacles
    // below and the one attack tentacle, the default is 8 arms.
    public int KrakenAmbientTentacleCount { get; set; } = 3;
    // Ground tentacles slither across the sea floor instead of rising.
    // They make the kraken's footprint feel huge and obviously alive.
    public int KrakenGroundTentacleCount { get; set; } = 4;
    // How far from the body its arms start, in blocks.
    public float KrakenTentacleSpawnRadius { get; set; } = 5f;

    // (TentacleKrakenDeathFallDuration was removed here. Tentacles no
    // longer vanish after a fixed fall when the body dies; they sink until
    // they reach the sea floor, bounded by TentacleSinkToFloorTimeout.)

    // Global speed dial for EVERY tentacle, attack and ambient alike:
    // rising, orbiting, wandering, pursuing and dragging all scale by it.
    // 2 makes the whole kraken twice as quick. Raise this rather than
    // editing the individual speeds below if you just want a livelier
    // kraken, since it keeps their relative pacing intact.
    public float TentacleSpeedMultiplier { get; set; } = 1f;

    // ─── Attack tentacle ────────────────────────────────────────
    // Seconds a new attack tentacle waits before it starts to rise.
    public float TentacleIdleDuration { get; set; } = 2f;
    // Pursuit speed: how fast the attack tentacle closes on the player
    // once it has surfaced and started hunting. Scaled by
    // TentacleSpeedMultiplier on top of this.
    public float TentacleReachSpeed { get; set; } = 0.06f;
    // How close to the player it grabs, in blocks.
    public float TentacleGrabRange { get; set; } = 2f;
    // How fast a grabbed player is dragged to the body, in blocks per
    // second.
    public float TentacleDragSpeed { get; set; } = 2.0f;
    // Height of the tentacle's head relative to the gripped player's feet,
    // in blocks.
    public float TentacleGrabYOffset { get; set; } = -0.5f;
    // Seconds it sinks after letting go, before it is removed.
    public float TentacleSinkDuration { get; set; } = 30f;
    // (TentacleRespawnDelayMin/Max were removed here. They read like the
    // wait before a new attack tentacle appears, but nothing ever consulted
    // them; that timing is AmbientPromoteToAttackDelayMin/Max further down.)

    // Damage dealt on a timer while the tentacle has hold of the player,
    // so a grab is a countdown rather than just an inconvenience. The
    // kraken body's own contact damage skips mounted players, so this is
    // what hurts you while you are being dragged.
    public bool TentacleGrabDamageEnabled { get; set; } = true;
    // Damage of each squeeze.
    public float TentacleGrabDamage { get; set; } = 1f;
    // Seconds between squeezes.
    public float TentacleGrabDamageIntervalSeconds { get; set; } = 2f;

    // Proximity aggro. When the kraken picks its next attack tentacle, a
    // player standing within this many blocks of a tentacle gets that one
    // (nearest wins) instead of a random one, and it skips the rise and
    // linger build-up to hunt immediately. Swim into a tentacle and it is
    // the one that comes for you. The promotion cooldown below is still
    // honoured first, so this changes WHICH tentacle moves in, not when.
    public bool TentacleProximityAggroEnabled { get; set; } = true;
    // How close a player must be to a tentacle for that, in blocks.
    public float TentacleProximityAggroRange { get; set; } = 3f;

    // Where a dead tentacle's remains land, and what it leaves. Tentacles
    // sink to the sea floor when the kraken body dies; on touching down
    // (or on being killed outright) they leave a bone pile and rusty
    // gears or scrap where they fell.
    public bool TentacleRemainsEnabled { get; set; } = true;
    // Safety cap on the sink. If a tentacle somehow cannot reach the
    // floor within this long it gives up, drops its remains on the floor
    // beneath it anyway, and despawns.
    public float TentacleSinkToFloorTimeout { get; set; } = 30f;
    // Motion units, as with the other tentacle speeds: roughly 60x this
    // many blocks per second, so 0.08 is a heavy limp arm coming down at
    // about 5 blocks a second. From the surface to a deep floor that is
    // ten to fifteen seconds, well inside the timeout above.
    public float TentacleDeathSinkSpeed { get; set; } = 0.08f;

    // ─── Tentacle shape ────────────────────────────────────────
    // How high an arm curves up between the body and its tip, as a share of
    // the distance between them.
    public float TentacleArchHeightFactor { get; set; } = 0.4f;
    // Not used.
    public float TentacleTipLerpSpeed { get; set; } = 5f;

    // ─── Risers: arms that rise to the surface and circle ────────────────────────────────────────
    // How fast a riser climbs, in blocks per 1/60 second.
    public float AmbientTentacleRiseSpeed { get; set; } = 0.04f;
    // Size of the circle a riser swims at the surface, in blocks.
    public float AmbientTentacleOrbitRadius { get; set; } = 4f;
    // How fast it circles.
    public float AmbientTentacleOrbitSpeed { get; set; } = 0.4f;
    // How far it bobs up and down, in blocks.
    public float AmbientTentacleBobAmplitude { get; set; } = 1.5f;
    // How fast it bobs.
    public float AmbientTentacleBobSpeed { get; set; } = 0.7f;
    // Not used yet.
    public float AmbientTentacleSurfaceRange { get; set; } = 10f;

    // ─── Attack tentacle at the surface ────────────────────────────────────────
    // How fast it rises, in blocks per 1/60 second.
    public float TentacleRiseSpeed { get; set; } = 0.025f;
    // Seconds it lingers at the surface before it hunts.
    public float TentacleLingerDuration { get; set; } = 7f;
    // How far from the player it comes up, at most, in blocks.
    public float TentacleSurfaceRange { get; set; } = 10f;

    // ─── Shallow water retreat ────────────────────────────────────────
    // Water around the player counts as shallow when it is fewer than this
    // many blocks deep. Serpents and tentacles turn back there.
    public int ShallowWaterThreshold { get; set; } = 3;
    // How fast a retreating tentacle sinks, in blocks per 1/60 second.
    public float RetreatSpeed { get; set; } = 0.06f;
    // Seconds it sinks before it is removed.
    public float RetreatDuration { get; set; } = 8f;

    // Stalling state: the attack tentacle enters Stalling the moment
    // the player leaves the water OR mounts a boat. While stalling it
    // either drifts slowly back toward the kraken body (player on land)
    // or wanders in a slow circle around the player (player on boat). It
    // does NOT actively chase. If the player returns to a chase-able
    // state (back in water, dismounted) before the timer expires the
    // tentacle resumes Reaching from where it stalled. Otherwise the
    // tentacle transitions to Retreating and despawns gracefully.
    public float TentacleStallDespawnSeconds { get; set; } = 30f;
    // 7 keeps the orbiting tentacle clear of a boat's hull (4 had it
    // grinding along the side of any boat the player was sitting in).
    public float TentacleStallOrbitRadius   { get; set; } = 7f;
    public bool TentacleStallOrbitMigrated  { get; set; } = false;
    // How fast its circle around a boat turns.
    public float TentacleStallOrbitSpeed    { get; set; } = 0.3f;
    // Drift speeds during Stalling. Slow on purpose so the visible
    // intent reads as "lurking" rather than "leaving".
    public float TentacleStallDriftSpeed    { get; set; } = 0.018f;
    // How fast it swims on that circle, in blocks per 1/60 second.
    public float TentacleStallBoatSpeed     { get; set; } = 0.03f;

    // How the ground tentacles crawl the sea floor, here and in the settings
    // below. Nearest point they pick to crawl to, in blocks from the body.
    public float AmbientTentacleWanderRangeMin { get; set; } = 5f;
    // Farthest point they pick, in blocks from the body.
    public float AmbientTentacleWanderRangeMax { get; set; } = 80f;
    // How fast they crawl, in blocks per 1/60 second.
    public float AmbientTentacleWanderSpeed { get; set; } = 0.05f;
    // Shortest pause between crawls, in seconds.
    public float AmbientTentacleWanderIdleMin { get; set; } = 1.5f;
    // Longest pause between crawls, in seconds.
    public float AmbientTentacleWanderIdleMax { get; set; } = 4f;
    // Most a ground tentacle may move up or down each tick. Where the floor
    // jumps, this makes it climb smoothly (about 3 blocks a second) instead
    // of leaping.
    public float AmbientTentacleVerticalStepMax { get; set; } = 0.1f;

    // When the attack tentacle starts hunting, the risers leave the surface.
    // They sink toward midwater for this many seconds, then wander there.
    public float AmbientScatterSinkDuration { get; set; } = 10f;
    // Nearest midwater point they pick, in blocks from the body.
    public float AmbientSurfaceWanderRangeMin { get; set; } = 5f;
    // Farthest midwater point they pick, in blocks from the body.
    public float AmbientSurfaceWanderRangeMax { get; set; } = 30f;
    // Deepest midwater point they pick, in blocks below the surface.
    public float AmbientSurfaceWanderDepthMax { get; set; } = 20f;
    // Promotion: when the attack tentacle dies, kraken body waits this
    // long, then picks a surviving ambient tentacle (any of them, risers
    // and ground crawlers alike) and respawns a new attack tentacle at
    // its position, killing the chosen ambient. This is the breathing
    // room you get after killing a grabber; TentacleProximityAggroRange
    // decides which tentacle steps up once it elapses.
    public float AmbientPromoteToAttackDelayMin { get; set; } = 5f;
    // Longest wait in seconds. The setting above is the shortest.
    public float AmbientPromoteToAttackDelayMax { get; set; } = 20f;
    // One-shot migration: configs written with the old 30/120 defaults are
    // moved to the new, much shorter ones on first load; see Validate().
    public bool PromoteDelayMigrated { get; set; } = false;

    // ─── Kraken glow ────────────────────────────────────────
    // A pulsing glow that travels along the tentacles. /uh kraken biolum sets it.
    public bool BiolumActive { get; set; } = false;
    // Whether the glow pulses. /uh kraken biolum pulse sets it.
    public bool BiolumPulsing { get; set; } = false;
    // How fast it pulses.
    public float BiolumPulseSpeed { get; set; } = 1.4f;
    // Not used yet.
    public int BiolumGlowMin { get; set; } = 32;
    // Not used yet.
    public int BiolumGlowMax { get; set; } = 200;
    // Not used yet.
    public int BiolumBodyGlowMin { get; set; } = 16;
    // Not used yet.
    public int BiolumBodyGlowMax { get; set; } = 128;

    // ─── Sounds ────────────────────────────────────────
    // Sea monster sounds. The serpent plays ambient dread sounds while it
    // stalks, plus action stingers when it surfaces, dives, and bites.
    // Only one sound plays at a time per player; a bite overrides whatever
    // is playing.
    public bool MonsterSoundsEnabled { get; set; } = true;
    // Global volume for every creature sound: all the serpents and the Rust
    // Leviathan, its death included (the kraken and the anglerfish make no
    // sounds). 0.5 is half, 0 is silent, and above 1 is no louder, because the
    // game's sound engine stops at 1. /uh sound volume sets it live. Each
    // sound below has its own multiplier on top of this, so you can balance
    // them. Players can also lower the game's own sliders: Entity Sound level
    // for these, and Effects Sound level for the screech.
    public float MonsterSoundVolume { get; set; } = 1.0f;
    // Per-sound volume multipliers (multiplied with MonsterSoundVolume). 1.0 = no
    // change. The screech has its own multiplier further down.
    public float MonsterSoundBelow1Volume { get; set; } = 1.0f;
    // The second clip is about 5 dB louder than the first as recorded, so it
    // plays at 0.55 to match it.
    public float MonsterSoundBelow2Volume { get; set; } = 0.55f;
    public bool MonsterSoundBelow2Migrated { get; set; } = false;
    public float MonsterSoundNearbyVolume { get; set; } = 1.0f;
    public float MonsterSoundDiveVolume { get; set; } = 1.0f;
    public float MonsterSoundBiteVolume { get; set; } = 0.75f;
    // The surface screech is the dramatic moment. It plays as a non-positional
    // 2D sound (so it is clearly audible, not directional) for players within
    // MonsterSoundScreechRange. Volume is full at the creature and drops with
    // distance down to MonsterSoundScreechMinVolumeFactor of full at the edge.
    // 1.0 and 0.2 since 0.20.0 (was 1.5 and 0.5: "too loud and does not
    // fade" was the most common sound complaint).
    public float MonsterSoundScreechVolume { get; set; } = 1.0f;
    // How far the screech carries, in blocks.
    public float MonsterSoundScreechRange { get; set; } = 25f;
    // Share of full volume at the edge of that range.
    public float MonsterSoundScreechMinVolumeFactor { get; set; } = 0.2f;
    public bool ScreechVolumeMigrated { get; set; } = false;
    // Audible range (blocks) for the positional monster sounds. Players
    // within this range of the creature receive and hear them.
    public float MonsterSoundRange { get; set; } = 48f;
    // Distance (blocks) below sea level within which the monster counts as
    // "at the surface" for the nearby surface sound and the screech.
    public float MonsterSoundSurfaceThreshold { get; set; } = 2.5f;
    // The "nearby at surface" sound needs the monster within this range.
    public float MonsterSoundNearbyRange { get; set; } = 10f;
    // The two "below" ambient sounds will not play when the monster is
    // closer than this (the nearby sound covers close range instead).
    public float MonsterSoundBelowMinRange { get; set; } = 5f;
    // Random gap (seconds) between ambient dread sounds, so they play
    // occasionally rather than constantly. After each ambient sound the
    // creature waits a random time in this range before the next one.
    public float MonsterSoundAmbientGapMin { get; set; } = 14f;
    // Longest gap, in seconds. The setting above is the shortest.
    public float MonsterSoundAmbientGapMax { get; set; } = 34f;

    /// <summary>
    /// Clamp fields that are used as divisors, radii, or probabilities so a
    /// hand-edited config can't produce divide-by-zero / NaN motion (which
    /// teleports a creature to an invalid position and locks it up) or a
    /// nonsensical spawn rate. Called once right after the config is loaded.
    /// Only safety floors/ceilings are applied here; normal tuning is untouched.
    /// </summary>
    public void Validate()
    {
        // Orbit radii are divisors in the serpents' orbit-speed math.
        SerpentOrbitRadius = Math.Max(0.5f, SerpentOrbitRadius);
        DeepSerpentOrbitRadius = Math.Max(0.5f, DeepSerpentOrbitRadius);

        // Spiral-step durations are divisors in the radius-transition lerp.
        SerpentSpiralStepDurationMin = Math.Max(0.1f, SerpentSpiralStepDurationMin);
        SerpentSpiralStepDurationMax = Math.Max(SerpentSpiralStepDurationMin, SerpentSpiralStepDurationMax);
        DeepSerpentSpiralStepDurationMin = Math.Max(0.1f, DeepSerpentSpiralStepDurationMin);
        DeepSerpentSpiralStepDurationMax = Math.Max(DeepSerpentSpiralStepDurationMin, DeepSerpentSpiralStepDurationMax);

        // Tentacle counts feed an angular step of 2*pi/count; never negative.
        if (KrakenAmbientTentacleCount < 0) KrakenAmbientTentacleCount = 0;
        if (KrakenGroundTentacleCount < 0) KrakenGroundTentacleCount = 0;

        // Probabilities compared against a 0..1 roll. Out-of-range values make
        // a roll always or never succeed.
        SpawnChancePerCheck = Math.Clamp(SpawnChancePerCheck, 0f, 1f);
        OverCapSpawnChance = Math.Clamp(OverCapSpawnChance, 0f, 1f);
        SecondCreatureSpawnChance = Math.Clamp(SecondCreatureSpawnChance, 0f, 1f);
        KrakenSpawnChanceDay = Math.Clamp(KrakenSpawnChanceDay, 0f, 1f);
        KrakenSpawnChanceNight = Math.Clamp(KrakenSpawnChanceNight, 0f, 1f);
        DeepSerpentSpawnWeight = Math.Clamp(DeepSerpentSpawnWeight, 0f, 1f);
        SerpentFleeChancePerDamage = Math.Clamp(SerpentFleeChancePerDamage, 0f, 1f);
        RustSerpentFleeChancePerDamage = Math.Clamp(RustSerpentFleeChancePerDamage, 0f, 1f);
        SerpentFleeAggroSuppressSeconds = Math.Max(0f, SerpentFleeAggroSuppressSeconds);
        SerpentProvokeChance = Math.Clamp(SerpentProvokeChance, 0f, 1f);
        SerpentEnrageSeconds = Math.Max(0f, SerpentEnrageSeconds);
        CageAttemptResponse = CageAttemptResponse?.Trim().ToLowerInvariant();
        if (CageAttemptResponse != CageRefuse && CageAttemptResponse != CageAllow) CageAttemptResponse = CageRage;

        // Negative drag speed would push the player away / invert the grab.
        TentacleDragSpeed = Math.Max(0f, TentacleDragSpeed);

        // Zero would freeze every tentacle; the cap keeps them from
        // teleporting past their own collision checks.
        TentacleSpeedMultiplier = Math.Clamp(TentacleSpeedMultiplier, 0.1f, 10f);

        // A zero or negative interval would fire the grab damage every
        // single tick, which is a very different creature than intended.
        TentacleGrabDamage = Math.Max(0f, TentacleGrabDamage);
        TentacleGrabDamageIntervalSeconds = Math.Max(0.1f, TentacleGrabDamageIntervalSeconds);

        // Negative range would never match; zero simply disables it.
        TentacleProximityAggroRange = Math.Max(0f, TentacleProximityAggroRange);

        // A tentacle that cannot sink never leaves remains, so keep both
        // the speed and the give-up timer positive.
        TentacleDeathSinkSpeed = Math.Max(0.01f, TentacleDeathSinkSpeed);
        TentacleSinkToFloorTimeout = Math.Max(1f, TentacleSinkToFloorTimeout);

        // Promotion window must be a real range, and a negative wait would
        // promote on the same tick the grabber died.
        AmbientPromoteToAttackDelayMin = Math.Max(0f, AmbientPromoteToAttackDelayMin);
        AmbientPromoteToAttackDelayMax =
            Math.Max(AmbientPromoteToAttackDelayMin, AmbientPromoteToAttackDelayMax);

        // Zero or negative turn speed would freeze the serpent's heading.
        SerpentTurnSpeedDegPerSec = Math.Max(5f, SerpentTurnSpeedDegPerSec);
        SerpentAttackTurnSpeedDegPerSec = Math.Max(5f, SerpentAttackTurnSpeedDegPerSec);

        // MaxLivingCreatures of 0 would make the over-cap path the only way to
        // ever spawn; at least 1 keeps normal spawning meaningful.
        if (MaxLivingCreatures < 1) MaxLivingCreatures = 1;

        // A zero/negative trigger range would make the spawner block inert.
        SpawnerTriggerRange = Math.Max(1f, SpawnerTriggerRange);
        SpawnerDespawnAfterLeaveSeconds = Math.Max(1f, SpawnerDespawnAfterLeaveSeconds);

        // A rarity below 1 would divide-by-zero the placement roll.
        if (RuinRarity < 1) RuinRarity = 1;
        if (RuinMinOceanDepth < 2) RuinMinOceanDepth = 2;

        // Server-tuning knobs. HP below 1 is instantly dead; the multipliers
        // are divisors in the aggro helpers so zero would NaN the AI.
        RustSerpentMaxHealth = Math.Max(1f, RustSerpentMaxHealth);
        DeepSerpentMaxHealth = Math.Max(1f, DeepSerpentMaxHealth);
        SerpentSpeedMultiplier = Math.Clamp(SerpentSpeedMultiplier, 0.1f, 10f);

        // Giant serpent: a chance is a 0..1 roll, the stalk and cooldown
        // windows must be real ranges, and a giant that cannot move or
        // never breaks a boat is not the creature described above.
        GiantSerpentSpawnChance = Math.Clamp(GiantSerpentSpawnChance, 0f, 1f);
        GiantSerpentOpenOceanRadius = Math.Clamp(GiantSerpentOpenOceanRadius, 0f, 200f);
        GiantSerpentOpenOceanDepth = Math.Max(1, GiantSerpentOpenOceanDepth);
        GiantSerpentMaxHealth = Math.Max(1f, GiantSerpentMaxHealth);
        GiantSerpentFleeHealthFraction = Math.Clamp(GiantSerpentFleeHealthFraction, 0f, 1f);
        GiantSerpentFleeSeconds = Math.Max(0f, GiantSerpentFleeSeconds);
        GiantSerpentFleeDistance = Math.Max(20f, GiantSerpentFleeDistance);
        GiantSerpentBiteDamage = Math.Max(0f, GiantSerpentBiteDamage);
        GiantSerpentRiderDamage = Math.Max(0f, GiantSerpentRiderDamage);
        GiantSerpentBitesToSinkBoat = Math.Max(1, GiantSerpentBitesToSinkBoat);
        GiantSerpentBoatBiteForgiveHours = Math.Max(0f, GiantSerpentBoatBiteForgiveHours);
        GiantSerpentCruiseSpeed = Math.Clamp(GiantSerpentCruiseSpeed, 0.5f, 60f);
        GiantSerpentStrikeSpeed = Math.Clamp(GiantSerpentStrikeSpeed, GiantSerpentCruiseSpeed, 80f);
        GiantSerpentStalkSecondsMin = Math.Max(0f, GiantSerpentStalkSecondsMin);
        GiantSerpentStalkSecondsMax = Math.Max(GiantSerpentStalkSecondsMin, GiantSerpentStalkSecondsMax);
        GiantSerpentStrikeCooldownMin = Math.Max(0f, GiantSerpentStrikeCooldownMin);
        GiantSerpentStrikeCooldownMax = Math.Max(GiantSerpentStrikeCooldownMin, GiantSerpentStrikeCooldownMax);
        GiantSerpentMinStrikeDepth = Math.Max(1, GiantSerpentMinStrikeDepth);
        GiantSerpentDespawnAfterLandSeconds = Math.Max(0f, GiantSerpentDespawnAfterLandSeconds);
        GiantSerpentMaxStaySeconds = Math.Max(30f, GiantSerpentMaxStaySeconds);
        GiantSerpentSoundPitch = Math.Clamp(GiantSerpentSoundPitch, 0.25f, 2f);
        GiantSerpentKillCooldownMinutes = Math.Max(0f, GiantSerpentKillCooldownMinutes);
        StormSpawnOddsFactor = Math.Clamp(StormSpawnOddsFactor, 1f, 1000f);
        StormMinSaltwaterDepth = Math.Max(1, StormMinSaltwaterDepth);
        StormMaxLivingCreatures = Math.Max(1, StormMaxLivingCreatures);
        StormSerpentSpeedMultiplier = Math.Clamp(StormSerpentSpeedMultiplier, 0.1f, 10f);
        StormTentacleSpeedMultiplier = Math.Clamp(StormTentacleSpeedMultiplier, 0.1f, 10f);
        StormGiantSerpentSpawnChance = Math.Clamp(StormGiantSerpentSpawnChance, 0f, 1f);
        GiantSerpentPieceRustyGearsMin = Math.Max(0, GiantSerpentPieceRustyGearsMin);
        GiantSerpentPieceRustyGearsMax = Math.Max(GiantSerpentPieceRustyGearsMin, GiantSerpentPieceRustyGearsMax);
        GiantSerpentPieceTemporalGearsMin = Math.Max(0, GiantSerpentPieceTemporalGearsMin);
        GiantSerpentPieceTemporalGearsMax = Math.Max(GiantSerpentPieceTemporalGearsMin, GiantSerpentPieceTemporalGearsMax);
        GiantSerpentPieceJonasPartsMin = Math.Max(0, GiantSerpentPieceJonasPartsMin);
        GiantSerpentPieceJonasPartsMax = Math.Max(GiantSerpentPieceJonasPartsMin, GiantSerpentPieceJonasPartsMax);
        GiantSerpentPieceFishMin = Math.Max(0, GiantSerpentPieceFishMin);
        GiantSerpentPieceFishMax = Math.Max(GiantSerpentPieceFishMin, GiantSerpentPieceFishMax);
        GiantSerpentPieceBonesMin = Math.Max(0, GiantSerpentPieceBonesMin);
        GiantSerpentPieceBonesMax = Math.Max(GiantSerpentPieceBonesMin, GiantSerpentPieceBonesMax);
        GiantSerpentPieceDecayHours = Math.Max(0f, GiantSerpentPieceDecayHours);

        MonsterSoundVolume = Math.Max(0f, MonsterSoundVolume);

        // Anglerfish: it must stay under water (its body is about 35 blocks
        // tall), and a swoop needs some reach and speed.
        AnglerfishNightSpawnChance = Math.Clamp(AnglerfishNightSpawnChance, 0f, 1f);
        AnglerfishMinWaterDepth = Math.Max(16, AnglerfishMinWaterDepth);
        AnglerfishCoastRange = Math.Clamp(AnglerfishCoastRange, 32f, 400f);
        AnglerfishShoreCheckSeconds = Math.Max(5f, AnglerfishShoreCheckSeconds);
        AnglerfishShoreSpawnChance = Math.Clamp(AnglerfishShoreSpawnChance, 0f, 1f);
        AnglerfishMaxStayHours = Math.Clamp(AnglerfishMaxStayHours, 0.25f, 24f);
        AnglerfishDamageTier = Math.Clamp(AnglerfishDamageTier, 0, 10);
        AnglerfishStrikeRange = Math.Clamp(AnglerfishStrikeRange, 3f, 40f);
        AnglerfishStrikeReachUp = Math.Clamp(AnglerfishStrikeReachUp, AnglerfishStrikeRange, 60f);
        AnglerfishStrikeSpeed = Math.Clamp(AnglerfishStrikeSpeed, 4f, 80f);
        AnglerfishBiteDamage = Math.Max(0f, AnglerfishBiteDamage);

        // 0 disables the clearance entirely; the upper bound keeps the
        // per-spawn footprint scan bounded. A rise of 0 would make the
        // spawn search test exactly one height, which is a valid (if
        // strict) choice, so only negatives are corrected.
        SerpentGroundClearance = Math.Clamp(SerpentGroundClearance, 0, 16);
        SerpentSpawnMaxRise = Math.Clamp(SerpentSpawnMaxRise, 0, 128);
        SerpentSpawnWaterClearance = Math.Clamp(SerpentSpawnWaterClearance, 1, 8);
        SerpentAggressionMultiplier = Math.Clamp(SerpentAggressionMultiplier, 0.1f, 10f);
        RuinSpawnerChance = Math.Clamp(RuinSpawnerChance, 0f, 1f);
        RuinKrakenVariantChance = Math.Clamp(RuinKrakenVariantChance, 0f, 1f);

        // Per-structure loot maps: a nulled-out map is restored to defaults;
        // individual entries just get their ranges sanitized.
        RuinLootChestsPerStructure ??= DefaultChestCounts();
        RuinIngotPilesPerStructure ??= DefaultIngotPileCounts();
        if (RuinIngotTypes == null || RuinIngotTypes.Count == 0) RuinIngotTypes = DefaultIngotTypes();
        foreach (var map in new[] { RuinLootChestsPerStructure, RuinIngotPilesPerStructure })
        {
            foreach (MinMaxCount m in map.Values)
            {
                if (m == null) continue;
                if (m.Min < 0) m.Min = 0;
                if (m.Max < m.Min) m.Max = m.Min;
            }
        }
        foreach (IngotPileType t in RuinIngotTypes.Values)
        {
            if (t == null) continue;
            t.Weight = Math.Max(0f, t.Weight);
            t.CountMin = Math.Clamp(t.CountMin, 1, 64);
            t.CountMax = Math.Clamp(t.CountMax, t.CountMin, 64);
        }

        // One-shot: bump the old sparse default (320) to the new default so
        // existing worlds get ~3x more ruins without hand-editing, but leave
        // any other user-chosen value alone.
        if (!RuinRarityMigrated)
        {
            if (RuinRarity == 320) RuinRarity = 107;
            RuinRarityMigrated = true;
        }

        // One-shot: the old 30-120s wait meant a killed grabber was rarely
        // replaced before the fight was over. Move configs still carrying
        // those exact values to the new 5-20s window, leaving any
        // hand-picked numbers alone.
        if (!PromoteDelayMigrated)
        {
            if (AmbientPromoteToAttackDelayMin == 30f) AmbientPromoteToAttackDelayMin = 5f;
            if (AmbientPromoteToAttackDelayMax == 120f) AmbientPromoteToAttackDelayMax = 20f;
            PromoteDelayMigrated = true;
        }

        // One-shot: 110 was the old hardcoded ceiling, correct only on
        // default-height worlds. Move configs still carrying it to the
        // follow-sea-level default; any other value was hand-picked and
        // stays. Negative values other than the -1 sentinel are noise.
        if (!CreatureMaxYMigrated)
        {
            if (CreatureMaxY == 110) CreatureMaxY = -1;
            CreatureMaxYMigrated = true;
        }
        if (CreatureMaxY < 0) CreatureMaxY = -1;

        // One-shot: swap the exact old default steel pile for the iron
        // default. A steel entry the server owner tuned themselves (any
        // other weight or count) is deliberate and stays.
        if (!RuinIngotIronMigrated)
        {
            if (RuinIngotTypes != null
                && RuinIngotTypes.TryGetValue("steel", out IngotPileType steel)
                && steel != null && steel.Weight == 15 && steel.CountMin == 2 && steel.CountMax == 5)
            {
                RuinIngotTypes.Remove("steel");
                if (!RuinIngotTypes.ContainsKey("iron"))
                    RuinIngotTypes["iron"] = new IngotPileType { Weight = 15, CountMin = 3, CountMax = 8 };
            }
            RuinIngotIronMigrated = true;
        }

        // 0.20.0 one-shots. Each only moves a value that still sits at the
        // exact old default, then never runs again.
        if (!IgnoreCreativePlayersMigrated)
        {
            IgnoreCreativePlayers = true;
            IgnoreCreativePlayersMigrated = true;
        }
        if (!AnglerfishGiantMigrated)
        {
            // The anglerfish grew to three times its size: settings still at
            // the smaller fish's defaults move to the new ones.
            if (AnglerfishStrikeRange == 14f) AnglerfishStrikeRange = 28f;
            if (AnglerfishStrikeReachUp == 24f) AnglerfishStrikeReachUp = 60f;
            if (AnglerfishStrikeSpeed == 28f) AnglerfishStrikeSpeed = 40f;
            if (AnglerfishMinWaterDepth == 32) AnglerfishMinWaterDepth = 40;
            AnglerfishGiantMigrated = true;
        }
        if (!AnglerfishRarityMigrated)
        {
            // Natural anglerfish became ten times rarer: chances still at the
            // old defaults move to the new ones. Tuned values stay.
            if (AnglerfishNightSpawnChance == 0.05f) AnglerfishNightSpawnChance = 0.005f;
            if (AnglerfishShoreSpawnChance == 0.25f) AnglerfishShoreSpawnChance = 0.025f;
            AnglerfishRarityMigrated = true;
        }
        if (!GiantSerpentTierMigrated)
        {
            // Its bite pierces up to tier 5 armor now (was 4).
            if (GiantSerpentDamageTier == 4) GiantSerpentDamageTier = 5;
            GiantSerpentTierMigrated = true;
        }
        if (!GiantSerpentHealth1500Migrated)
        {
            // 0.23.0 and 0.24.0 lowered its health to 600; 0.24.1 is back to
            // 1500. A config still at 600 moves up once.
            if (GiantSerpentMaxHealth == 600f) GiantSerpentMaxHealth = 1500f;
            GiantSerpentHealth1500Migrated = true;
        }
        if (!GiantSerpentChanceMigrated)
        {
            // 0.22.0 to 0.22.4 rolled the giant on any natural serpent spawn,
            // at 0.02; now only in open ocean, at 0.05.
            if (GiantSerpentSpawnChance == 0.02f) GiantSerpentSpawnChance = 0.05f;
            GiantSerpentChanceMigrated = true;
        }
        if (!ScreechVolumeMigrated)
        {
            if (MonsterSoundScreechVolume == 1.5f) MonsterSoundScreechVolume = 1.0f;
            if (MonsterSoundScreechMinVolumeFactor == 0.5f) MonsterSoundScreechMinVolumeFactor = 0.2f;
            ScreechVolumeMigrated = true;
        }
        if (!MonsterSoundBelow2Migrated)
        {
            if (MonsterSoundBelow2Volume == 1f) MonsterSoundBelow2Volume = 0.55f;
            MonsterSoundBelow2Migrated = true;
        }
        if (!TentacleStallOrbitMigrated)
        {
            if (TentacleStallOrbitRadius == 4f) TentacleStallOrbitRadius = 7f;
            TentacleStallOrbitMigrated = true;
        }
        if (!RuinLootRebalanceMigrated)
        {
            var newChests = DefaultChestCounts();
            var newPiles = DefaultIngotPileCounts();
            if (RuinLootChestsPerStructure != null)
                foreach (var kv in OldChestCounts)
                    if (RuinLootChestsPerStructure.TryGetValue(kv.Key, out MinMaxCount c) && c != null
                        && c.Min == kv.Value && c.Max == kv.Value && newChests.ContainsKey(kv.Key))
                        RuinLootChestsPerStructure[kv.Key] = newChests[kv.Key];
            if (RuinIngotPilesPerStructure != null)
                foreach (var kv in OldIngotPileCounts)
                    if (RuinIngotPilesPerStructure.TryGetValue(kv.Key, out MinMaxCount c) && c != null
                        && c.Min == kv.Value && c.Max == kv.Value && newPiles.ContainsKey(kv.Key))
                        RuinIngotPilesPerStructure[kv.Key] = newPiles[kv.Key];
            if (RuinIngotTypes != null && RuinIngotTypes.TryGetValue("gold", out IngotPileType gold)
                && gold != null && gold.Weight == 9 && gold.CountMin == 24 && gold.CountMax == 32)
            {
                gold.CountMin = 6;
                gold.CountMax = 12;
            }
            RuinLootRebalanceMigrated = true;
        }
    }

    /// <summary>
    /// Resolves CreatureMaxY against the running world: the configured
    /// absolute Y, or the world's sea level when left at the -1 default.
    /// A method rather than a property so StoreModConfig does not write
    /// the resolved value back into the config file.
    /// </summary>
    public double ResolveCreatureMaxY(int worldSeaLevel)
        => CreatureMaxY >= 0 ? CreatureMaxY : worldSeaLevel;
}
