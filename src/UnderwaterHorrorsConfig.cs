namespace UnderwaterHorrors;

public class UnderwaterHorrorsConfig
{
    // Debug
    public bool DebugLogging { get; set; } = true;

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

    // Kraken body
    public float KrakenContactDamage { get; set; } = 25f;
    public float KrakenContactRange { get; set; } = 3f;
    public int KrakenAmbientTentacleCount { get; set; } = 3;
    public float KrakenTentacleSpawnRadius { get; set; } = 5f;

    // Attack tentacle
    public float TentacleIdleDuration { get; set; } = 2f;
    public float TentacleReachSpeed { get; set; } = 0.06f;
    public float TentacleGrabRange { get; set; } = 2f;
    public float TentacleGrabDuration { get; set; } = 0.5f;
    public float TentacleDragSpeed { get; set; } = 2.0f;
    public float TentacleCooldownMin { get; set; } = 15f;
    public float TentacleCooldownMax { get; set; } = 30f;
    public float TentacleReleaseDamageThreshold { get; set; } = 15f;
    public float TentacleGrabYOffset { get; set; } = -2.0f;
    public float TentacleRespawnDelay { get; set; } = 3f;

    // Ambient tentacle
    public float AmbientTentacleAmplitude { get; set; } = 3f;
    public float AmbientTentacleDriftSpeed { get; set; } = 1f;

    // Shallow water retreat
    public int ShallowWaterThreshold { get; set; } = 3;
    public float RetreatSpeed { get; set; } = 0.06f;
    public float RetreatDuration { get; set; } = 8f;
}
