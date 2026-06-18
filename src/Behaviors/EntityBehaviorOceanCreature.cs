using System;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;

namespace UnderwaterHorrors;

public class EntityBehaviorOceanCreature : EntityBehavior
{
    protected UnderwaterHorrorsConfig config;
    protected IPlayer targetPlayer;
    protected bool targetResolved;

    // Shallow water check throttle
    private float shallowWaterCheckTimer;
    private const float ShallowWaterCheckInterval = 0.5f;
    private bool lastShallowWaterResult;

    public EntityBehaviorOceanCreature(Entity entity) : base(entity) { }

    public override void Initialize(EntityProperties properties, JsonObject attributes)
    {
        config = UnderwaterHorrorsModSystem.Config;
    }

    public override void OnEntityDespawn(EntityDespawnData despawn)
    {
        TargetingHelper.ClearCache(entity.EntityId);

        // Cut any sound this creature was playing and free its server-side
        // sound bookkeeping, so a serpent killed mid-screech doesn't leave the
        // sound hanging in empty water and the channel dictionary stays bounded.
        if (entity.Api.Side == EnumAppSide.Server)
        {
            UnderwaterHorrorsModSystem.ServerInstance?.OnCreatureGone(entity.EntityId);
        }

        base.OnEntityDespawn(despawn);
    }

    protected void ResolveTarget()
    {
        if (targetResolved) return;
        targetResolved = true;

        targetPlayer = TargetingHelper.ResolveTarget(entity);
    }

    protected void ClampHeight()
    {
        double maxY = config.CreatureMaxY;
        if (entity.Pos.Y > maxY)
        {
            entity.Pos.Y = maxY;
            if (entity.Pos.Motion.Y > 0) entity.Pos.Motion.Y = 0;
        }
    }

    protected void MoveToward(double targetX, double targetY, double targetZ, double speed, double minDist = 0.1)
    {
        double dx = targetX - entity.Pos.X;
        double dy = targetY - entity.Pos.Y;
        double dz = targetZ - entity.Pos.Z;
        double dist = Math.Sqrt(dx * dx + dy * dy + dz * dz);

        if (dist < minDist) return;

        entity.Pos.Motion.X = (dx / dist) * speed;
        entity.Pos.Motion.Y = (dy / dist) * speed;
        entity.Pos.Motion.Z = (dz / dist) * speed;
    }

    // Slew-rate limiter state for MoveTowardDamped.  Shared between
    // subclass states — reset by the subclass when transitioning.
    protected double lastCommandedMotionY;

    // Reusable BlockPos for the water-surface scan below.  Dimension
    // is set per-call via scanPos.dimension = dim.
    private readonly BlockPos scanPos = new(0);

    /// <summary>
    /// Scans down from <paramref name="fromY"/> to find the highest
    /// water block at (x, z).  Returns that block's Y, or
    /// <paramref name="fromY"/> if no water found within
    /// <paramref name="maxScan"/> blocks.  Used so the serpent can
    /// cruise relative to the actual water surface rather than the
    /// player's (possibly boat-elevated) Y coordinate.
    /// </summary>
    protected int FindWaterSurfaceYBelow(double x, double fromY, double z, int dimension, int maxScan = 5)
    {
        var accessor = entity.World.BlockAccessor;
        int startY = (int)fromY;
        int limit = Math.Max(0, startY - maxScan);
        scanPos.Set((int)x, startY, (int)z);
        scanPos.dimension = dimension;
        for (int y = startY; y >= limit; y--)
        {
            scanPos.Y = y;
            Block block = accessor.GetBlock(scanPos);
            if (block != null && block.IsLiquid()) return y;
        }
        return (int)fromY;
    }

    /// <summary>
    /// Scans down from <paramref name="fromY"/> to find the sea floor
    /// at (x, z) — the first solid (non-water/non-air) block. Returns
    /// the Y of the SPACE just above that solid block, so a tentacle
    /// targeting this Y will sit ON the floor rather than inside it.
    /// Returns <paramref name="fromY"/> if no floor found within
    /// <paramref name="maxScan"/> blocks (open chunk).
    /// </summary>
    protected int FindSeaFloorYBelow(double x, double fromY, double z, int dimension, int maxScan = 80)
    {
        var accessor = entity.World.BlockAccessor;
        int startY = (int)fromY;
        int limit = Math.Max(0, startY - maxScan);
        scanPos.Set((int)x, startY, (int)z);
        scanPos.dimension = dimension;
        for (int y = startY; y >= limit; y--)
        {
            scanPos.Y = y;
            Block block = accessor.GetBlock(scanPos);
            if (block == null) continue;
            // Treat anything that isn't liquid, isn't air (id != 0), and
            // isn't replaceable as the sea floor.
            if (block.Id != 0 && !block.IsLiquid() && block.Replaceable < 6000)
            {
                return y + 1;
            }
        }
        return (int)fromY;
    }

    /// <summary>
    /// Proportional (gain 0.4 ≈ 3-tick convergence with no overshoot)
    /// controller with a tighter cap on vertical speed and a slew-rate
    /// limiter on Motion.Y.  Eliminates the bang-bang limit-cycle that
    /// makes long horizontal bodies bob visibly when approaching a
    /// target depth.
    /// </summary>
    protected void MoveTowardDamped(
        double targetX, double targetY, double targetZ,
        double horizSpeed, double maxVerticalSpeed, double verticalSlewPerSec,
        float deltaTime)
    {
        double dx = targetX - entity.Pos.X;
        double dy = targetY - entity.Pos.Y;
        double dz = targetZ - entity.Pos.Z;

        const double gain = 0.4;

        double mx = Math.Clamp(dx * gain, -horizSpeed, horizSpeed);
        double mz = Math.Clamp(dz * gain, -horizSpeed, horizSpeed);

        double myTarget = Math.Clamp(dy * gain, -maxVerticalSpeed, maxVerticalSpeed);
        double maxDelta = verticalSlewPerSec * Math.Max(0.001, deltaTime);
        double myDelta = myTarget - lastCommandedMotionY;
        if (myDelta > maxDelta) myDelta = maxDelta;
        else if (myDelta < -maxDelta) myDelta = -maxDelta;
        double my = lastCommandedMotionY + myDelta;
        lastCommandedMotionY = my;

        entity.Pos.Motion.X = mx;
        entity.Pos.Motion.Y = my;
        entity.Pos.Motion.Z = mz;
    }

    /// <summary>
    /// Throttled shallow water check. Updates at ShallowWaterCheckInterval and caches result.
    /// Skips check if player is mounted (on boat). Call UpdateShallowWaterCheck(deltaTime) each
    /// tick, then read this property.
    /// </summary>
    protected bool IsInShallowWater => lastShallowWaterResult;

    /// <summary>
    /// Decrements the throttle timer and re-evaluates shallow water status when it expires.
    /// </summary>
    protected void UpdateShallowWaterCheck(float deltaTime)
    {
        shallowWaterCheckTimer -= deltaTime;
        if (shallowWaterCheckTimer <= 0)
        {
            shallowWaterCheckTimer = ShallowWaterCheckInterval;
            lastShallowWaterResult = targetPlayer?.Entity?.MountedOn == null &&
                TargetingHelper.IsPlayerInShallowWater(entity, targetPlayer, config.ShallowWaterThreshold);
        }
    }

    // ═══════════════════════════════════════════════════════════════════
    //  Sea monster sounds (shared by the regular and deep serpent)
    // ═══════════════════════════════════════════════════════════════════

    // Countdown (seconds) until the next ambient sound is allowed. Negative
    // means "not yet seeded" so the first gap is randomized on the first tick.
    private float ambientCooldown = -1f;
    // Set true after the first surface screech so it only plays once.
    private bool hasScreeched;

    /// <summary>
    /// Drives the creature's sea monster sounds each tick. Plays a one-time
    /// screech the first time it breaches near the stalked player, then ambient
    /// dread sounds spaced by a random gap. Sounds are positional (anyone nearby
    /// hears them) and the director's per-creature channel rule prevents overlap.
    /// </summary>
    protected void UpdateAmbientSound(float deltaTime)
    {
        var mod = UnderwaterHorrorsModSystem.ServerInstance;
        var cfg = UnderwaterHorrorsModSystem.Config;
        if (mod == null || cfg == null || !cfg.MonsterSoundsEnabled) return;
        if (targetPlayer?.Entity == null) return;

        double dx = entity.Pos.X - targetPlayer.Entity.Pos.X;
        double dy = entity.Pos.Y - targetPlayer.Entity.Pos.Y;
        double dz = entity.Pos.Z - targetPlayer.Entity.Pos.Z;
        double dist = Math.Sqrt(dx * dx + dy * dy + dz * dz);

        bool atSurface = entity.Pos.Y >= entity.World.SeaLevel - cfg.MonsterSoundSurfaceThreshold;

        // Ambient dread on a random gap so it plays occasionally, not constantly.
        if (ambientCooldown < 0f) ambientCooldown = NextAmbientGap(cfg);
        ambientCooldown -= deltaTime;
        if (ambientCooldown > 0f) return;
        ambientCooldown = NextAmbientGap(cfg);

        if (dist <= cfg.MonsterSoundNearbyRange && atSurface)
        {
            mod.PlayMonsterSound(entity, UnderwaterHorrorsModSystem.SndNearbySurface,
                UnderwaterHorrorsModSystem.DurNearbySurface, volumeMul: cfg.MonsterSoundNearbyVolume);
        }
        else if (dist > cfg.MonsterSoundBelowMinRange)
        {
            bool one = entity.World.Rand.NextDouble() < 0.5;
            mod.PlayMonsterSound(entity,
                one ? UnderwaterHorrorsModSystem.SndAmbientBelow1 : UnderwaterHorrorsModSystem.SndAmbientBelow2,
                one ? UnderwaterHorrorsModSystem.DurAmbientBelow1 : UnderwaterHorrorsModSystem.DurAmbientBelow2,
                volumeMul: one ? cfg.MonsterSoundBelow1Volume : cfg.MonsterSoundBelow2Volume);
        }
        // Within MonsterSoundBelowMinRange but below the surface: stay silent.
    }

    private float NextAmbientGap(UnderwaterHorrorsConfig cfg)
    {
        float min = cfg.MonsterSoundAmbientGapMin;
        float max = Math.Max(min, cfg.MonsterSoundAmbientGapMax);
        return min + (float)(entity.World.Rand.NextDouble() * (max - min));
    }

    /// <summary>
    /// Plays the surface screech once per lifetime, called from the Surfacing state so it
    /// syncs with the hiss animation. 2D and loud so it is clearly audible.
    /// </summary>
    protected void TriggerScreech()
    {
        if (hasScreeched) return;
        hasScreeched = true;
        UnderwaterHorrorsModSystem.ServerInstance?.PlayScreech(entity);
    }

    /// <summary>Plays the dive sound as the creature retreats below the surface.</summary>
    protected void PlayDive()
    {
        var cfg = UnderwaterHorrorsModSystem.Config;
        UnderwaterHorrorsModSystem.ServerInstance?.PlayMonsterSound(
            entity, UnderwaterHorrorsModSystem.SndDive,
            UnderwaterHorrorsModSystem.DurDive,
            volumeMul: cfg?.MonsterSoundDiveVolume ?? 1f);
    }

    /// <summary>Plays the bite sound, overriding whatever this creature was playing.</summary>
    protected void PlayBite()
    {
        var cfg = UnderwaterHorrorsModSystem.Config;
        UnderwaterHorrorsModSystem.ServerInstance?.PlayMonsterSound(
            entity, UnderwaterHorrorsModSystem.SndBite,
            UnderwaterHorrorsModSystem.DurBite, bite: true,
            volumeMul: cfg?.MonsterSoundBiteVolume ?? 1f);
    }

    // ═══════════════════════════════════════════════════════════════════
    //  Boat surface/submerge oscillation (shared by both serpents)
    // ═══════════════════════════════════════════════════════════════════

    private bool boatSubmergedPhase;
    private float boatPhaseTimer;
    private float boatPhaseDuration;

    /// <summary>
    /// True while the current boat phase is the submerged (below water) one.
    /// The serpents' depth logic reads this to dive instead of staying surfaced.
    /// </summary>
    protected bool BoatSubmergedPhase => boatSubmergedPhase;

    /// <summary>
    /// Advances the boat surface/submerge cycle. Call each tick while the
    /// player is mounted and the creature is circling. Starts on the surface
    /// phase, then alternates using the configured random durations.
    /// </summary>
    protected void UpdateBoatPhase(float deltaTime)
    {
        // First mounted tick: begin on the surface phase.
        if (boatPhaseDuration <= 0f)
        {
            boatSubmergedPhase = false;
            boatPhaseTimer = 0f;
            boatPhaseDuration = RandRange(config.BoatSurfaceDurationMin, config.BoatSurfaceDurationMax);
            return;
        }

        boatPhaseTimer += deltaTime;
        if (boatPhaseTimer < boatPhaseDuration) return;

        boatPhaseTimer = 0f;
        boatSubmergedPhase = !boatSubmergedPhase;
        boatPhaseDuration = boatSubmergedPhase
            ? RandRange(config.BoatSubmergeDurationMin, config.BoatSubmergeDurationMax)
            : RandRange(config.BoatSurfaceDurationMin, config.BoatSurfaceDurationMax);

        if (config.DebugLogging)
        {
            UnderwaterHorrorsModSystem.DebugLog(entity.Api,
                $"Serpent (boat): {(boatSubmergedPhase ? "submerging" : "surfacing")} for {boatPhaseDuration:F0}s");
        }
    }

    /// <summary>Resets the boat cycle so the next mount starts fresh on the surface.</summary>
    protected void ResetBoatPhase()
    {
        boatSubmergedPhase = false;
        boatPhaseTimer = 0f;
        boatPhaseDuration = 0f;
    }

    private float boatBoredomTimer;
    private float boatBoredomTarget = -1f;

    /// <summary>
    /// Tracks how long the serpent has been circling a boat and returns true once it
    /// reaches a random give-up time in [BoatBoredomMinSeconds, BoatBoredomMaxSeconds],
    /// rolled once when circling begins. Call each tick while the player is mounted.
    /// </summary>
    protected bool UpdateBoatBoredom(float deltaTime)
    {
        if (boatBoredomTarget < 0f)
            boatBoredomTarget = RandRange(config.BoatBoredomMinSeconds, config.BoatBoredomMaxSeconds);

        boatBoredomTimer += deltaTime;
        return boatBoredomTimer >= boatBoredomTarget;
    }

    /// <summary>How long the serpent has circled the current boat (for logging).</summary>
    protected float BoatBoredomElapsed => boatBoredomTimer;

    /// <summary>Resets the boredom timer so the next mount rolls a fresh give-up time.</summary>
    protected void ResetBoatBoredom()
    {
        boatBoredomTimer = 0f;
        boatBoredomTarget = -1f;
    }

    private float RandRange(float min, float max)
    {
        if (max <= min) return min;
        return min + (float)(entity.World.Rand.NextDouble() * (max - min));
    }

    public override string PropertyName() => "underwaterhorrors:oceancreature";
}
