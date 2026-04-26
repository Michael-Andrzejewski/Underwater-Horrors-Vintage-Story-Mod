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
    public float SerpentSpawnWeight { get; set; } = 0.6f;

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

    // Boat boredom: player mounted for this long → roll for retreat
    // every 30 s.  Existing behavior restored so a single creature
    // doesn't circle forever; new spawns can still occur to replace it.
    public float BoatBoredomGraceSeconds { get; set; } = 120f;
    public float BoatBoredomRetreatRollChance { get; set; } = 0.5f;

    // Chance per spawn check that a SECOND creature spawns even if the
    // player already has one tracked.  Untracked — the second creature
    // manages its own lifecycle via the AI state machine.
    public float SecondCreatureSpawnChance { get; set; } = 0.005f;

    // Vertical-motion smoothing for the regular serpent.  Matches the
    // deep variant — playtesting showed 2x leeway was still too jittery
    // on the long body.
    public float SerpentMaxVerticalSpeed { get; set; } = 0.012f;
    public float SerpentVerticalSlewPerSec { get; set; } = 0.04f;

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
    public float DeepSerpentSpawnWeight { get; set; } = 0.75f;      // 75% deep, 25% regular when a serpent is picked
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

    // Bioluminescent glow renderer test mode. 0 disables the renderer.
    // 1..N selects a different render-stage / blend-mode / shader variant
    // for testing which combination actually shows up through deep water
    // at night. See BioluminescentGlowRenderer for mode descriptions and
    // change with /uh biolum testmode <n>. Persisted only so the active
    // test survives a server restart.
    public int BiolumTestMode { get; set; } = 0;
}
