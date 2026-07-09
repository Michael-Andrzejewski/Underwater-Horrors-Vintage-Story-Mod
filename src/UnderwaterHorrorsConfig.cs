using System;

namespace UnderwaterHorrors;

public class UnderwaterHorrorsConfig
{
    // Debug
    public bool DebugLogging { get; set; } = false;

    // One-shot migration flag. Earlier mod versions shipped with
    // DebugLogging=true by default, which spammed chat for everyone.
    // The first load after upgrading flips DebugLogging to false and sets
    // this flag to true; from then on we never touch DebugLogging again,
    // so /uh debug on (or hand-editing the config) sticks across restarts.
    public bool DebugLoggingResetApplied { get; set; } = false;
    public bool GlowDebugActive { get; set; } = false;
    public bool SpectralDebugActive { get; set; } = false;

    // Spawn system
    public float SpawnCheckIntervalSeconds { get; set; } = 5f;
    public int MinSaltwaterDepth { get; set; } = 50;
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
    // body plus 8 tentacles, each with a 96-segment chain, roughly 780
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
    public float KrakenSpawnChanceNight { get; set; } = 0.10f;

    // Day/night threshold for kraken bioluminescence. Krakens spawning
    // during [DayKrakenStartHour, DayKrakenEndHour) on the VS calendar
    // get NO glow; outside this window they get a pulsing cyan glow
    // matching biolumtest mode 10. Defaults: 6 AM to 10 PM = "day".
    public float DayKrakenStartHour { get; set; } = 6f;
    public float DayKrakenEndHour { get; set; } = 22f;

    // Serpent spawn offsets. Horizontal position is randomized uniformly
    // within a circle of the given max radius around the player.
    public int SerpentSpawnDepthMin { get; set; } = 25;
    public int SerpentSpawnDepthMax { get; set; } = 40;
    public int SerpentSpawnHorizontalRadiusMax { get; set; } = 50;

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
    public float SerpentProximityBodyTriggerRange { get; set; } = 5f;
    public float SerpentProximityBodyDwellMin { get; set; } = 5f;
    public float SerpentProximityBodyDwellMax { get; set; } = 15f;

    // Stalk depth configuration.
    //   NormalSubmergeDepth — the regular serpent's usual cruise
    //     depth below the water surface.  Deeper = less visible.
    //   SurfaceSubmergeDepth — depth used during a "surface peek"
    //     orbit step (serpent briefly rises for visibility) and when
    //     the target player is mounted (both variants rise to boat
    //     level).
    //   SerpentSurfacePeekChance / DeepSerpentSurfacePeekChance —
    //     per-spiral-step probability that the next orbit is at
    //     surface depth instead of normal depth.  Higher for the
    //     regular serpent; the deep variant should mostly stay deep.
    public float SerpentNormalSubmergeDepth { get; set; } = 5f;
    public float SerpentSurfaceSubmergeDepth { get; set; } = 1f;
    public float SerpentSurfacePeekChance { get; set; } = 0.4f;
    public float DeepSerpentSurfacePeekChance { get; set; } = 0.1f;

    // Boat boredom: while the player stays in a boat, the serpent circles for
    // a random time in this range, then gives up and retreats so a single
    // creature doesn't haunt forever (a new one can spawn later). The time is
    // rolled once when the serpent starts circling the boat.
    public float BoatBoredomMinSeconds { get; set; } = 90f;
    public float BoatBoredomMaxSeconds { get; set; } = 240f;

    // Boat surface/submerge oscillation: while the player is in a boat, the
    // serpent surfaces and circles for a random SurfaceDuration, then sinks
    // below the water for a random SubmergeDuration, looping. (This is the
    // up/down cycle; the boat boredom above is the separate eventual give-up.)
    public float BoatSurfaceDurationMin { get; set; } = 5f;
    public float BoatSurfaceDurationMax { get; set; } = 15f;
    public float BoatSubmergeDurationMin { get; set; } = 15f;
    public float BoatSubmergeDurationMax { get; set; } = 75f;

    // Chance per spawn check that a SECOND creature spawns even if the
    // player already has one tracked.  Untracked — the second creature
    // manages its own lifecycle via the AI state machine.
    public float SecondCreatureSpawnChance { get; set; } = 0.005f;

    // Vertical-motion smoothing for the regular serpent.  Matches the
    // deep variant — playtesting showed 2x leeway was still too jittery
    // on the long body.
    public float SerpentMaxVerticalSpeed { get; set; } = 0.012f;
    public float SerpentVerticalSlewPerSec { get; set; } = 0.04f;

    // Maximum turning speed (degrees per second) for both serpent
    // variants. Caps how fast the body heading can sweep, so target
    // jumps (spiral steps, state changes) produce a wide smooth arc
    // instead of a snap turn. The attack value applies while the
    // serpent is charging a player and needs to track them.
    public float SerpentTurnSpeedDegPerSec { get; set; } = 45f;
    public float SerpentAttackTurnSpeedDegPerSec { get; set; } = 120f;

    // Flee-after-damage: every hit rolls (damage x chance-per-damage)
    // to make the serpent break off, swim away, and resume circling at
    // its wide initial orbit. 0.10 means 1 damage = 10% flee chance and
    // 5 damage = 50%, so real weapons drive it off far more reliably
    // than fists. The rust (aggressive surface) serpent uses the Rust
    // value, which is halved so it presses the attack harder.
    public float SerpentFleeChancePerDamage { get; set; } = 0.10f;
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
    // Observer mode for creative players: when true, the creatures
    // never attack a player who is in creative or spectator mode.
    // Serpents skip every attack trigger (spiral-complete strike,
    // proximity aggro, stalk-timeout, provoke-on-hit) and break off
    // mid-attack; the kraken deals no contact damage to such players
    // and its attack tentacle sways in place instead of chasing and
    // grabbing. Everything else stays natural: circling, surface
    // peeks, ambient tentacles, sounds, and the usual despawn rules
    // when the player leaves the water. Off by default so switching
    // an admin to creative mid-fight doesn't silently change behavior
    // on servers that don't want this.
    public bool IgnoreCreativePlayers { get; set; } = false;

    // Serpent spawner block. A creative-only block (looks like a vanilla
    // locust nest cage) that watches for a player who is in the water and
    // within SpawnerTriggerRange blocks, then spawns one sea serpent and
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

    // Movement limits
    public double CreatureMaxY { get; set; } = 110;

    // Despawn system
    public float DespawnCheckIntervalSeconds { get; set; } = 2f;
    public float DespawnAfterLandSeconds { get; set; } = 30f;
    // Despawn immediately if creature drifts farther than this from its
    // target player (e.g. player escaped by boat, or respawned far after
    // death). A new creature can then spawn naturally near the player.
    public float DespawnMaxDistance { get; set; } = 500f;

    // Sea serpent
    public float SerpentOrbitRadius { get; set; } = 8f;
    public float SerpentOrbitSpeed { get; set; } = 0.5f;
    public float SerpentStalkDurationMin { get; set; } = 15f;
    public float SerpentStalkDurationMax { get; set; } = 45f;
    public float SerpentRiseSpeed { get; set; } = 0.04f;
    public float SerpentApproachSpeed { get; set; } = 0.03f;
    public float SerpentAttackSpeed { get; set; } = 0.08f;
    public float SerpentAttackDamage { get; set; } = 10f;
    public float SerpentAttackCooldown { get; set; } = 2f;
    public float SerpentAttackRange { get; set; } = 2.5f;
    public float SerpentReStalkChance { get; set; } = 0.5f;
    public int SerpentDamageTier { get; set; } = 3;

    // Kraken damage tier
    public int KrakenDamageTier { get; set; } = 3;

    // Serpent spiral approach
    public float SerpentInitialOrbitRadiusMin { get; set; } = 30f;
    public float SerpentInitialOrbitRadiusMax { get; set; } = 50f;
    public float SerpentSpiralStepDurationMin { get; set; } = 5f;
    public float SerpentSpiralStepDurationMax { get; set; } = 15f;
    public float SerpentSpiralReductionMin { get; set; } = 5f;
    public float SerpentSpiralReductionMax { get; set; } = 15f;

    // Deep serpent variant (stays deep, orbits in huge arcs, rises only to strike)
    public float DeepSerpentSpawnWeight { get; set; } = 0.9f;       // 90% deep, 10% rust (aggressive surface) serpent
    // One-shot migration flag: configs written before the rust serpent
    // rebalance carry the old 0.75 weight; the first load raises it to
    // 0.9 and sets this flag, after which user tuning is left alone.
    public bool RustSerpentTuningApplied { get; set; } = false;
    public float DeepSerpentStalkDepthMin { get; set; } = 10f;      // 10 blocks below surface
    public float DeepSerpentStalkDepthMax { get; set; } = 30f;      // 30 blocks below surface
    public float DeepSerpentOrbitRadius { get; set; } = 15f;        // final approach radius
    public float DeepSerpentInitialOrbitRadiusMin { get; set; } = 50f;
    public float DeepSerpentInitialOrbitRadiusMax { get; set; } = 80f;
    public float DeepSerpentSpiralStepDurationMin { get; set; } = 15f;
    public float DeepSerpentSpiralStepDurationMax { get; set; } = 30f;
    public float DeepSerpentSpiralReductionMin { get; set; } = 5f;
    public float DeepSerpentSpiralReductionMax { get; set; } = 15f;
    public float DeepSerpentMaxPitchRad { get; set; } = 0.005f;     // ~0.3° — nearly horizontal
    public float DeepSerpentPitchInterpRate { get; set; } = 0.3f;   // very slow tilt lerp

    // Vertical-motion smoothing for the damped controller.
    // DeepSerpentMaxVerticalSpeed: hard cap on |Motion.Y| (blocks/tick
    //   units that VS physics uses).  Much smaller than horizontal speed
    //   so the body glides up/down very slowly even when dy is large.
    // DeepSerpentVerticalSlewPerSec: max change in Motion.Y per second.
    //   Prevents snap from e.g. +0.01 to -0.01 between ticks, smoothing
    //   the moment where the serpent crosses through its target depth.
    public float DeepSerpentMaxVerticalSpeed { get; set; } = 0.012f;
    public float DeepSerpentVerticalSlewPerSec { get; set; } = 0.04f;

    // Kraken body
    public float KrakenContactDamage { get; set; } = 25f;
    public float KrakenContactRange { get; set; } = 3f;
    // 4 ambient risers + 4 ground wanderers + 1 attack tentacle = 9 total
    // (3 ambient risers in older versions; the 4th makes the kraken feel
    // larger when one of the 3 surface risers transitions into the attack
    // tentacle slot via promotion).
    public int KrakenAmbientTentacleCount { get; set; } = 3;
    // Ground tentacles slither across the sea floor instead of rising.
    // They make the kraken's footprint feel huge and obviously alive.
    public int KrakenGroundTentacleCount { get; set; } = 4;
    public float KrakenTentacleSpawnRadius { get; set; } = 5f;

    // How long after the kraken body dies before each tentacle and its
    // chain are removed. Zero state logic runs during this window — the
    // tentacle just falls passively under whatever motion remains.
    public float TentacleKrakenDeathFallDuration { get; set; } = 6f;

    // Attack tentacle
    public float TentacleIdleDuration { get; set; } = 2f;
    public float TentacleReachSpeed { get; set; } = 0.06f;
    public float TentacleGrabRange { get; set; } = 2f;
    public float TentacleDragSpeed { get; set; } = 2.0f;
    public float TentacleGrabYOffset { get; set; } = -0.5f;
    public float TentacleSinkDuration { get; set; } = 30f;
    public float TentacleRespawnDelayMin { get; set; } = 30f;
    public float TentacleRespawnDelayMax { get; set; } = 60f;

    // Tentacle spline rendering
    public float TentacleArchHeightFactor { get; set; } = 0.4f;
    public float TentacleTipLerpSpeed { get; set; } = 5f;

    // Ambient tentacle - rising and orbiting
    public float AmbientTentacleRiseSpeed { get; set; } = 0.04f;
    public float AmbientTentacleOrbitRadius { get; set; } = 4f;
    public float AmbientTentacleOrbitSpeed { get; set; } = 0.4f;
    public float AmbientTentacleBobAmplitude { get; set; } = 1.5f;
    public float AmbientTentacleBobSpeed { get; set; } = 0.7f;
    public float AmbientTentacleSurfaceRange { get; set; } = 10f;

    // Attack tentacle - rising and lingering
    public float TentacleRiseSpeed { get; set; } = 0.025f;
    public float TentacleLingerDuration { get; set; } = 7f;
    public float TentacleSurfaceRange { get; set; } = 10f;

    // Shallow water retreat
    public int ShallowWaterThreshold { get; set; } = 3;
    public float RetreatSpeed { get; set; } = 0.06f;
    public float RetreatDuration { get; set; } = 8f;

    // Stalling state: the attack tentacle enters Stalling the moment
    // the player leaves the water OR mounts a boat. While stalling it
    // either drifts slowly back toward the kraken body (player on land)
    // or wanders in a slow circle around the player (player on boat) —
    // it does NOT actively chase. If the player returns to a chase-able
    // state (back in water, dismounted) before the timer expires the
    // tentacle resumes Reaching from where it stalled. Otherwise the
    // tentacle transitions to Retreating and despawns gracefully.
    public float TentacleStallDespawnSeconds { get; set; } = 30f;
    public float TentacleStallOrbitRadius   { get; set; } = 4f;
    public float TentacleStallOrbitSpeed    { get; set; } = 0.3f;
    // Drift speeds during Stalling. Slow on purpose so the visible
    // intent reads as "lurking" rather than "leaving".
    public float TentacleStallDriftSpeed    { get; set; } = 0.018f;
    public float TentacleStallBoatSpeed     { get; set; } = 0.03f;

    // Ambient tentacle wandering — used for both the post-rise scatter
    // (when the attack tentacle starts pursuing) and for the 4 ground
    // tentacles that crawl the sea floor from the moment they spawn.
    // Range is centered on the kraken body. Min=5 so they pick varied
    // distances (not always the perimeter), giving the floor-crawling
    // motion a more natural, hypnotic feel as they reach + re-target
    // continuously. Idle is short (1.5–4s) so they're nearly always in
    // motion.
    public float AmbientTentacleWanderRangeMin { get; set; } = 5f;
    public float AmbientTentacleWanderRangeMax { get; set; } = 80f;
    public float AmbientTentacleWanderSpeed { get; set; } = 0.05f;
    public float AmbientTentacleWanderIdleMin { get; set; } = 1.5f;
    public float AmbientTentacleWanderIdleMax { get; set; } = 4f;
    // Maximum per-tick Y change while a ground tentacle is wandering. When
    // terrain rises/drops between two adjacent XZ steps, FindSeaFloorYBelow
    // returns a different floor height — applying it raw makes the tip
    // teleport up by the full delta, which the user described as "kind of
    // teleport up when something is in the way". Clamping the per-tick Y
    // delta turns that into a smooth climb (≈3 blocks/s at 30Hz).
    public float AmbientTentacleVerticalStepMax { get; set; } = 0.1f;

    // Post-scatter risers: when the attack tentacle starts pursuing, the
    // 3 surface-orbiting risers leave Orbiting and run a two-phase
    // sequence — first a brief sink (visually "they're done with the
    // surface, retreating") and then a midwater wander that mirrors the
    // ground tentacles' hypnotic crawl but in open water above the body.
    //
    // ScatterSinkDuration: seconds spent descending toward midwater
    // before the SurfaceWandering wander kicks in.
    // SurfaceWanderRangeMin/Max: horizontal radius (blocks) around the
    // kraken body for picking the next midwater target.
    // SurfaceWanderDepthMax: how far below the surface the deepest
    // midwater target can be (Y in [surface - DepthMax, surface]).
    public float AmbientScatterSinkDuration { get; set; } = 10f;
    public float AmbientSurfaceWanderRangeMin { get; set; } = 5f;
    public float AmbientSurfaceWanderRangeMax { get; set; } = 30f;
    public float AmbientSurfaceWanderDepthMax { get; set; } = 20f;
    // Promotion: when the attack tentacle dies, kraken body waits this
    // long, then picks a random surviving ambient tentacle and respawns
    // a new attack tentacle at its position (killing the chosen ambient).
    public float AmbientPromoteToAttackDelayMin { get; set; } = 30f;
    public float AmbientPromoteToAttackDelayMax { get; set; } = 120f;

    // Bioluminescence — pulsing glow that travels along tentacles
    public bool BiolumActive { get; set; } = false;
    public bool BiolumPulsing { get; set; } = false;
    public float BiolumPulseSpeed { get; set; } = 1.4f;
    public int BiolumGlowMin { get; set; } = 32;
    public int BiolumGlowMax { get; set; } = 200;
    public int BiolumBodyGlowMin { get; set; } = 16;
    public int BiolumBodyGlowMax { get; set; } = 128;

    // Sea monster sounds. The serpent plays ambient dread sounds while it
    // stalks, plus action stingers when it surfaces, dives, and bites.
    // Only one sound plays at a time per player; a bite overrides whatever
    // is playing.
    public bool MonsterSoundsEnabled { get; set; } = true;
    // Global volume applied to every monster sound. Each individual sound below
    // has its own multiplier on top of this, so you can balance them.
    public float MonsterSoundVolume { get; set; } = 1.0f;
    // Per-sound volume multipliers (multiplied with MonsterSoundVolume). 1.0 = no
    // change. The screech has its own multiplier further down.
    public float MonsterSoundBelow1Volume { get; set; } = 1.0f;
    public float MonsterSoundBelow2Volume { get; set; } = 1.0f;
    public float MonsterSoundNearbyVolume { get; set; } = 1.0f;
    public float MonsterSoundDiveVolume { get; set; } = 1.0f;
    public float MonsterSoundBiteVolume { get; set; } = 0.75f;
    // The surface screech is the dramatic moment. It plays as a non-positional
    // 2D sound (so it is clearly audible, not directional) for players within
    // MonsterSoundScreechRange. Volume is full at the creature and drops with
    // distance down to MonsterSoundScreechMinVolumeFactor of full at the edge.
    public float MonsterSoundScreechVolume { get; set; } = 1.5f;
    public float MonsterSoundScreechRange { get; set; } = 25f;
    public float MonsterSoundScreechMinVolumeFactor { get; set; } = 0.5f;
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

        // Negative drag speed would push the player away / invert the grab.
        TentacleDragSpeed = Math.Max(0f, TentacleDragSpeed);

        // Zero or negative turn speed would freeze the serpent's heading.
        SerpentTurnSpeedDegPerSec = Math.Max(5f, SerpentTurnSpeedDegPerSec);
        SerpentAttackTurnSpeedDegPerSec = Math.Max(5f, SerpentAttackTurnSpeedDegPerSec);

        // MaxLivingCreatures of 0 would make the over-cap path the only way to
        // ever spawn; at least 1 keeps normal spawning meaningful.
        if (MaxLivingCreatures < 1) MaxLivingCreatures = 1;

        // A zero/negative trigger range would make the spawner block inert.
        SpawnerTriggerRange = Math.Max(1f, SpawnerTriggerRange);
        SpawnerDespawnAfterLeaveSeconds = Math.Max(1f, SpawnerDespawnAfterLeaveSeconds);
    }
}
