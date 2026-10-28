using System;
using System.Linq;
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

    /// <summary>
    /// True when IgnoreCreativePlayers is enabled and the target player
    /// is in creative or spectator mode. The serpent AIs and the kraken
    /// attack tentacle treat such a player as something to swim around,
    /// never something to attack. Checked live each time so switching
    /// game mode mid-stalk (or toggling the config) takes effect
    /// immediately.
    /// </summary>
    protected bool TargetIsPassiveObserver
    {
        get
        {
            if (config == null || !config.IgnoreCreativePlayers) return false;
            EnumGameMode? mode = targetPlayer?.WorldData?.CurrentGameMode;
            return mode == EnumGameMode.Creative || mode == EnumGameMode.Spectator;
        }
    }

    /// <summary>
    /// The highest Y this creature may swim to. Resolved against the live
    /// world so the -1 config default tracks the actual sea level on any
    /// map height, instead of assuming the default 256-tall world.
    /// </summary>
    protected double CreatureCeilingY => config.ResolveCreatureMaxY(entity.World.SeaLevel);

    protected void ClampHeight()
    {
        double maxY = CreatureCeilingY;
        if (entity.Pos.Y > maxY)
        {
            entity.Pos.Y = maxY;
            if (entity.Pos.Motion.Y > 0) entity.Pos.Motion.Y = 0;
        }
    }

    // Floor-clamp scan cache. -1 means "nothing scanned yet".
    private double floorClampX = double.NaN, floorClampZ;
    private int floorClampY = -1;
    private float floorClampTimer;

    /// <summary>
    /// The other half of <see cref="ClampHeight"/>: keeps the creature from
    /// sinking into the sea floor.
    ///
    /// The serpent AIs pick their depth purely from the player's Y, with no
    /// idea where the ground is, so a player wading over a sandbank or
    /// standing in a shallow bay hands them a target depth that is inside
    /// the terrain. They swim down to it, and because the serpents run
    /// controlledphysics with a step height of 0 there is nothing to push
    /// them back out — they simply stop, which is the "stuck in the sand"
    /// failure. Clamping the center to the same margin the spawn placement
    /// uses turns that into a floor they skim along instead.
    ///
    /// Only the center column is tested; scanning the whole body every tick
    /// would be far too expensive, and the spawn check has already put the
    /// body somewhere sane.
    /// </summary>
    protected void ClampAboveSeaFloor(float deltaTime)
    {
        double clearance = config.SerpentGroundClearance;
        if (clearance <= 0) return;

        // Scanning up to 80 blocks down every tick is wasteful for a value
        // that only changes as the creature travels. Rescan four times a
        // second, or immediately once it has moved off the scanned column.
        floorClampTimer -= deltaTime;
        if (floorClampTimer <= 0f
            || double.IsNaN(floorClampX)
            || Math.Abs(entity.Pos.X - floorClampX) > 2.0
            || Math.Abs(entity.Pos.Z - floorClampZ) > 2.0)
        {
            floorClampTimer = 0.25f;
            floorClampX = entity.Pos.X;
            floorClampZ = entity.Pos.Z;

            // Scan from just above the creature so a center that is already
            // inside a block finds that block and gets lifted out of it,
            // rather than scanning past it to the floor further down.
            floorClampY = EntityBehaviorSerpentHarvest.FindRestingYBelow(
                entity.World.BlockAccessor,
                entity.Pos.X, entity.Pos.Y + 1, entity.Pos.Z, entity.Pos.Dimension);
        }

        // No floor within scan range: open water, nothing to clamp against.
        if (floorClampY < 0) return;

        double minY = floorClampY + clearance;

        // In water too shallow to hold the margin, hugging the bottom beats
        // being launched into the air. The serpents retreat from shallow
        // water on their own anyway.
        double ceiling = CreatureCeilingY;
        if (minY > ceiling) minY = ceiling;

        if (entity.Pos.Y < minY)
        {
            entity.Pos.Y = minY;
            if (entity.Pos.Motion.Y < 0) entity.Pos.Motion.Y = 0;
        }
    }

    // Water at least this deep (the same as ShallowWaterThreshold's default)
    // counts as somewhere a serpent may swim.
    private const int ShoreStopDepth = 3;
    // How fast a serpent with its head or tail over the shore backs off it,
    // in blocks per 1/60 s (9 blocks a second): quick enough to undo a turn
    // that swings an end over the sand.
    private const double ShoreBackOff = 0.15;
    // How far short of the shore its head and tail keep: it backs off
    // before an end reaches land, not after.
    private const double ShoreMargin = 5;
    private const double ShoreSampleStep = 3;
    private readonly BlockPos shorePos = new(0);
    private double bodyFront = double.NaN, bodyBack;

    /// <summary>
    /// Keeps a serpent in the sea. Its AI steers straight at the player with
    /// no idea where the shore is, over a beach the floor clamp only holds it
    /// at sea level, and circling a player by the shore it swept its body
    /// over the sand. So it slid out onto low ground.
    ///
    /// Each tick this checks for ShoreStopDepth of water below sea level:
    /// ahead along its horizontal motion, past its head and half a second
    /// further, where land cancels the motion; and every few blocks along its
    /// body (the span of its hit proxies, or <paramref name="headReach"/>
    /// each way, and ShoreMargin past each end), where any land drops the
    /// motion toward it and backs the serpent off it, toward the water side.
    /// About thirty block reads a tick. Call after the state handlers have
    /// set the motion.
    /// </summary>
    protected void KeepOffLand(double headReach)
    {
        if (double.IsNaN(bodyFront))
        {
            float[] offsets = entity.GetBehavior<EntityBehaviorSerpentHitProxies>()?.Offsets;
            bodyFront = offsets is { Length: > 0 } ? Math.Max(headReach, offsets.Max()) : headReach;
            bodyBack = offsets is { Length: > 0 } ? Math.Max(0, -offsets.Min()) : headReach;
        }
        var pos = entity.Pos;
        var motion = pos.Motion;

        double h = Math.Sqrt(motion.X * motion.X + motion.Z * motion.Z);
        if (h > 1e-5)
        {
            // Motion is in blocks per 1/60 s.
            double ahead = bodyFront + 2 + h * 60 * 0.5;
            if (!DeepEnoughAt(pos.X + motion.X / h * ahead, pos.Z + motion.Z / h * ahead))
            {
                motion.X = 0;
                motion.Z = 0;
            }
        }

        // Its body lies along its facing, head forward (the hit proxies'
        // convention). Sample it every ShoreSampleStep blocks.
        double fx = Math.Sin(pos.Yaw), fz = Math.Cos(pos.Yaw);
        double from = -(bodyBack + ShoreMargin), to = bodyFront + ShoreMargin;
        int dry = 0, wet = 0;
        double dryX = 0, dryZ = 0, wetX = 0, wetZ = 0;
        for (double k = from; k <= to + 1e-6; k += ShoreSampleStep)
        {
            double x = pos.X + fx * k, z = pos.Z + fz * k;
            if (DeepEnoughAt(x, z)) { wet++; wetX += x; wetZ += z; }
            else { dry++; dryX += x; dryZ += z; }
        }
        if (dry == 0) return;

        // Off the land, toward the water side of its body.
        double ax, az;
        if (wet > 0) { ax = wetX / wet - dryX / dry; az = wetZ / wet - dryZ / dry; }
        else { ax = pos.X - dryX / dry; az = pos.Z - dryZ / dry; }
        double al = Math.Sqrt(ax * ax + az * az);
        if (al < 1e-3) { ax = -fx; az = -fz; al = 1; }
        ax /= al; az /= al;
        double toward = -(motion.X * ax + motion.Z * az);
        if (toward > 0)
        {
            motion.X += toward * ax;
            motion.Z += toward * az;
        }
        motion.X += ax * ShoreBackOff;
        motion.Z += az * ShoreBackOff;
    }

    /// <summary>
    /// A spot to come up at: the spot and four points a body length (12
    /// blocks) around it all have ShoreStopDepth of water. Up to eight random
    /// spots dist blocks from (cx, cz) are tried; with none, the creature's
    /// own position, which is in the water.
    /// </summary>
    protected void PickDeepSpot(double cx, double cz, float distMin, float distMax, out double x, out double z)
    {
        var rand = entity.World.Rand;
        for (int i = 0; i < 8; i++)
        {
            double angle = rand.NextDouble() * Math.PI * 2;
            double dist = distMin + rand.NextDouble() * (distMax - distMin);
            x = cx + Math.Cos(angle) * dist;
            z = cz + Math.Sin(angle) * dist;
            if (DeepEnoughAt(x, z) && DeepEnoughAt(x + 12, z) && DeepEnoughAt(x - 12, z)
                && DeepEnoughAt(x, z + 12) && DeepEnoughAt(x, z - 12)) return;
        }
        x = entity.Pos.X;
        z = entity.Pos.Z;
    }

    protected bool DeepEnoughAt(double x, double z)
    {
        var accessor = entity.World.BlockAccessor;
        int top = entity.World.SeaLevel - 1;
        shorePos.Set((int)Math.Floor(x), top, (int)Math.Floor(z));
        shorePos.dimension = entity.Pos.Dimension;
        // Not loaded: no way to tell, so do not hold it back.
        if (accessor.GetChunkAtBlockPos(shorePos) == null) return true;
        for (int i = 0; i < ShoreStopDepth; i++)
        {
            shorePos.Y = top - i;
            // The fluid layer: seaweed and coral sit in the water, and the
            // plain block read would return the plant, not the water.
            if (!WaterHelper.IsWaterBlock(accessor.GetBlock(shorePos, BlockLayersAccess.Fluid))) return false;
        }
        return true;
    }

    // Movement speed scale applied inside MoveToward / MoveTowardDamped.
    // 1 for everything except the two serpent AIs and the kraken's
    // tentacles, which override it with SerpentSpeedMultiplier and
    // TentacleSpeedMultiplier (times their temporal storm multipliers), so
    // one knob rescales every motion of theirs coherently. The leviathan and
    // the anglerfish move at their own speeds.
    protected virtual double SpeedScale => 1.0;

    protected void MoveToward(double targetX, double targetY, double targetZ, double speed, double minDist = 0.1)
    {
        speed *= SpeedScale;

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
        // Scale caps and slew together so a sped-up serpent keeps the same
        // strike angles instead of moving fast horizontally but rising slowly.
        horizSpeed *= SpeedScale;
        maxVerticalSpeed *= SpeedScale;
        verticalSlewPerSec *= SpeedScale;

        double dx = targetX - entity.Pos.X;
        double dy = targetY - entity.Pos.Y;
        double dz = targetZ - entity.Pos.Z;

        const double gain = 0.4;

        // Clamp the horizontal motion VECTOR, not each axis separately.
        // Per-axis clamping locks far-away targets onto 45-degree
        // diagonals (both axes saturated) and snaps 90 degrees whenever
        // one axis desaturates — the visible "right angle" turns.
        double mx = dx * gain;
        double mz = dz * gain;
        double horizLen = Math.Sqrt(mx * mx + mz * mz);
        if (horizLen > horizSpeed)
        {
            double horizScale = horizSpeed / horizLen;
            mx *= horizScale;
            mz *= horizScale;
        }

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
    // A spawner can tell its serpent to hold its ground for a while when
    // the target steps into the shallows or onto land, instead of turning
    // back at once (the spawner's "stay while ashore" setting).
    private float lingerElapsed;

    /// <summary>
    /// True while a spawner serpent should keep hunting although its target
    /// is in shallow water or ashore: it has linger seconds left and the
    /// target is within its linger range. Call each tick the target is in
    /// the shallows; call <see cref="ResetLinger"/> when they are back in
    /// deep water.
    /// </summary>
    protected bool LingerInsteadOfRetreat(float deltaTime)
    {
        var wa = entity.WatchedAttributes;
        float linger = wa.GetFloat(BlockEntityCreatureSpawner.LingerSecondsAttr, 0f);
        if (linger <= 0 || targetPlayer?.Entity == null) return false;
        float range = wa.GetFloat(BlockEntityCreatureSpawner.LingerRangeAttr, 0f);
        if (range > 0 && entity.Pos.DistanceTo(targetPlayer.Entity.Pos) > range) return false;
        lingerElapsed += deltaTime;
        return lingerElapsed < linger;
    }

    protected void ResetLinger() => lingerElapsed = 0;

    protected void UpdateShallowWaterCheck(float deltaTime)
    {
        shallowWaterCheckTimer -= deltaTime;
        if (shallowWaterCheckTimer <= 0)
        {
            shallowWaterCheckTimer = ShallowWaterCheckInterval;

            // "Mounted" here means a boat or a horse, which the caller
            // handles separately. A player riding THIS creature is being
            // dragged by it, so the shallow water check still applies to
            // them - otherwise a grab would never release in shallow water.
            IMountableSeat mount = targetPlayer?.Entity?.MountedOn;
            bool onSomethingElse = mount != null && mount.Entity != entity;

            lastShallowWaterResult = !onSomethingElse &&
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
    /// <summary>
    /// Playback pitch of this creature's sounds (1 as recorded), and how far
    /// they carry at full volume, in blocks. The giant serpent sounds deeper
    /// and carries further.
    /// </summary>
    protected virtual float SoundPitch => 1f;
    protected virtual float SoundReach => 8f;
    // The bite clip and its length in seconds at pitch 1.
    protected virtual string BiteSound => UnderwaterHorrorsModSystem.SndBite;
    protected virtual float BiteDuration => UnderwaterHorrorsModSystem.DurBite;

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
                UnderwaterHorrorsModSystem.DurNearbySurface / SoundPitch, volumeMul: cfg.MonsterSoundNearbyVolume,
                refDistance: SoundReach, pitch: SoundPitch);
        }
        else if (dist > cfg.MonsterSoundBelowMinRange)
        {
            bool one = entity.World.Rand.NextDouble() < 0.5;
            mod.PlayMonsterSound(entity,
                one ? UnderwaterHorrorsModSystem.SndAmbientBelow1 : UnderwaterHorrorsModSystem.SndAmbientBelow2,
                (one ? UnderwaterHorrorsModSystem.DurAmbientBelow1 : UnderwaterHorrorsModSystem.DurAmbientBelow2) / SoundPitch,
                volumeMul: one ? cfg.MonsterSoundBelow1Volume : cfg.MonsterSoundBelow2Volume,
                refDistance: SoundReach, pitch: SoundPitch);
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
        UnderwaterHorrorsModSystem.ServerInstance?.PlayScreech(entity, SoundPitch);
    }

    /// <summary>Plays the dive sound as the creature retreats below the surface.</summary>
    protected void PlayDive()
    {
        var cfg = UnderwaterHorrorsModSystem.Config;
        UnderwaterHorrorsModSystem.ServerInstance?.PlayMonsterSound(
            entity, UnderwaterHorrorsModSystem.SndDive,
            UnderwaterHorrorsModSystem.DurDive / SoundPitch,
            volumeMul: cfg?.MonsterSoundDiveVolume ?? 1f, refDistance: SoundReach, pitch: SoundPitch);
    }

    /// <summary>Plays the bite sound, overriding whatever this creature was playing.</summary>
    protected void PlayBite()
    {
        var cfg = UnderwaterHorrorsModSystem.Config;
        UnderwaterHorrorsModSystem.ServerInstance?.PlayMonsterSound(
            entity, BiteSound,
            BiteDuration / SoundPitch, bite: true,
            volumeMul: cfg?.MonsterSoundBiteVolume ?? 1f, refDistance: SoundReach, pitch: SoundPitch);
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
