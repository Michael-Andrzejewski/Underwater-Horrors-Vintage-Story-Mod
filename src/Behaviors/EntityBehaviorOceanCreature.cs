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
        if (entity.SidedPos.Y > maxY)
        {
            entity.SidedPos.Y = maxY;
            if (entity.SidedPos.Motion.Y > 0) entity.SidedPos.Motion.Y = 0;
        }
    }

    protected void MoveToward(double targetX, double targetY, double targetZ, double speed, double minDist = 0.1)
    {
        double dx = targetX - entity.SidedPos.X;
        double dy = targetY - entity.SidedPos.Y;
        double dz = targetZ - entity.SidedPos.Z;
        double dist = Math.Sqrt(dx * dx + dy * dy + dz * dz);

        if (dist < minDist) return;

        entity.SidedPos.Motion.X = (dx / dist) * speed;
        entity.SidedPos.Motion.Y = (dy / dist) * speed;
        entity.SidedPos.Motion.Z = (dz / dist) * speed;
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
        double dx = targetX - entity.SidedPos.X;
        double dy = targetY - entity.SidedPos.Y;
        double dz = targetZ - entity.SidedPos.Z;

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

        entity.SidedPos.Motion.X = mx;
        entity.SidedPos.Motion.Y = my;
        entity.SidedPos.Motion.Z = mz;
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

    public override string PropertyName() => "underwaterhorrors:oceancreature";
}
