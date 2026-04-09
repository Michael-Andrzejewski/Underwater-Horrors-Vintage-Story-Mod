using System;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Datastructures;

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
