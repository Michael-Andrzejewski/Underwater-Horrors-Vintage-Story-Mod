namespace UnderwaterHorrors;

public class UnderwaterHorrorsConfig
{
    // Debug
    public bool DebugLogging { get; set; } = true;
    public bool GlowDebugActive { get; set; } = false;
    public bool SpectralDebugActive { get; set; } = false;

    // Spawn system
    public float SpawnCheckIntervalSeconds { get; set; } = 5f;
    public int MinSaltwaterDepth { get; set; } = 50;
    public float SpawnChancePerCheck { get; set; } = 0.1f;
    public float SerpentSpawnWeight { get; set; } = 0.6f;

    // Serpent spawn offsets
    public int SerpentSpawnDepthMin { get; set; } = 25;
    public int SerpentSpawnDepthMax { get; set; } = 40;
    public int SerpentSpawnHorizontalOffset { get; set; } = 15;

    // Movement limits
    public double CreatureMaxY { get; set; } = 110;

    // Despawn system
    public float DespawnCheckIntervalSeconds { get; set; } = 2f;
    public float DespawnAfterLandSeconds { get; set; } = 30f;

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
    public float DeepSerpentSpawnWeight { get; set; } = 0.5f;       // 50% chance when a serpent is picked
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
    public int KrakenAmbientTentacleCount { get; set; } = 3;
    public float KrakenTentacleSpawnRadius { get; set; } = 5f;

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

    // Bioluminescence — pulsing glow that travels along tentacles
    public bool BiolumActive { get; set; } = false;
    public bool BiolumPulsing { get; set; } = false;
    public float BiolumPulseSpeed { get; set; } = 1.4f;
    public int BiolumGlowMin { get; set; } = 32;
    public int BiolumGlowMax { get; set; } = 200;
    public int BiolumBodyGlowMin { get; set; } = 16;
    public int BiolumBodyGlowMax { get; set; } = 128;
}
