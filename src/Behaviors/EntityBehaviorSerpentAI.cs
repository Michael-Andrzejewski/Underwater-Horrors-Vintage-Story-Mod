using System;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Datastructures;

namespace UnderwaterHorrors;

public enum SerpentState
{
    Rising,
    Stalking,
    Attacking,
    Retreating
}

public class EntityBehaviorSerpentAI : EntityBehavior
{
    private SerpentState state = SerpentState.Rising;
    private float stateTimer;
    private float stalkDuration;
    private float orbitAngle;
    private float attackCooldownTimer;
    private UnderwaterHorrorsConfig config;
    private IPlayer targetPlayer;
    private bool targetResolved;

    // Spiral approach fields
    private bool useSpiralApproach;
    private float orbitRadiusStart;
    private float orbitRadiusEnd;
    private float radiusTransitionTime;
    private float radiusTransitionDuration;

    // Spawn position for retreat
    private double spawnX, spawnY, spawnZ;
    private bool spawnRecorded;

    // Boat boredom: serpent gives up after circling a mounted player too long
    private float mountedCircleTimer;
    private float mountedCheckTimer;

    public EntityBehaviorSerpentAI(Entity entity) : base(entity) { }

    public override void Initialize(EntityProperties properties, JsonObject attributes)
    {
        config = UnderwaterHorrorsModSystem.Config;
        orbitAngle = (float)(entity.World.Rand.NextDouble() * Math.PI * 2);
    }

    public override void OnGameTick(float deltaTime)
    {
        if (!entity.Alive) return;
        if (entity.Api.Side != EnumAppSide.Server) return;

        // Record spawn position on first tick
        if (!spawnRecorded)
        {
            spawnRecorded = true;
            spawnX = entity.ServerPos.X;
            spawnY = entity.ServerPos.Y;
            spawnZ = entity.ServerPos.Z;
        }

        ResolveTarget();
        ClampHeight();

        // Check shallow water retreat (not during Rising or already Retreating)
        if (state != SerpentState.Rising && state != SerpentState.Retreating)
        {
            if (TargetingHelper.IsPlayerInShallowWater(entity, targetPlayer, config.ShallowWaterThreshold))
            {
                UnderwaterHorrorsModSystem.DebugLog(entity.Api, "Serpent: player in shallow water, retreating to spawn");
                TransitionTo(SerpentState.Retreating);
            }
        }

        // Boat boredom: if player stays mounted, serpent eventually gives up
        if (state == SerpentState.Stalking || state == SerpentState.Attacking)
        {
            if (targetPlayer?.Entity?.MountedOn != null)
            {
                mountedCircleTimer += deltaTime;
                if (mountedCircleTimer >= 60f)
                {
                    mountedCheckTimer += deltaTime;
                    if (mountedCheckTimer >= 30f)
                    {
                        mountedCheckTimer = 0;
                        if (entity.World.Rand.NextDouble() < 0.5)
                        {
                            UnderwaterHorrorsModSystem.DebugLog(entity.Api,
                                $"Serpent bored of circling boat after {mountedCircleTimer:F0}s, retreating to spawn");
                            TransitionTo(SerpentState.Retreating);
                        }
                    }
                }
            }
            else
            {
                mountedCircleTimer = 0;
                mountedCheckTimer = 0;
            }
        }

        stateTimer += deltaTime;

        switch (state)
        {
            case SerpentState.Rising:
                OnRising(deltaTime);
                break;
            case SerpentState.Stalking:
                OnStalking(deltaTime);
                break;
            case SerpentState.Attacking:
                OnAttacking(deltaTime);
                break;
            case SerpentState.Retreating:
                OnRetreating(deltaTime);
                break;
        }
    }

    private void ResolveTarget()
    {
        if (targetResolved) return;
        targetResolved = true;

        targetPlayer = TargetingHelper.ResolveTarget(entity);
    }

    private void ClampHeight()
    {
        double maxY = config.CreatureMaxY;
        if (entity.SidedPos.Y > maxY)
        {
            entity.SidedPos.Y = maxY;
            if (entity.SidedPos.Motion.Y > 0) entity.SidedPos.Motion.Y = 0;
        }
    }

    private void TransitionTo(SerpentState newState)
    {
        SerpentState oldState = state;
        state = newState;
        stateTimer = 0;

        string playerName = targetPlayer?.PlayerName ?? "unknown";
        UnderwaterHorrorsModSystem.DebugLog(entity.Api, $"Serpent state: {oldState} to {newState} (target: {playerName})");

        if (newState == SerpentState.Stalking)
        {
            if (oldState == SerpentState.Rising)
            {
                // First approach: full spiral from far away
                useSpiralApproach = true;
                SetupSpiralApproach(true);
            }
            else if (oldState == SerpentState.Attacking)
            {
                // Re-stalk: short spiral from moderate distance
                useSpiralApproach = true;
                SetupSpiralApproach(false);
            }
        }
    }

    private void SetupSpiralApproach(bool fullApproach)
    {
        var rand = entity.World.Rand;

        if (fullApproach)
        {
            orbitRadiusStart = config.SerpentInitialOrbitRadiusMin +
                (float)(rand.NextDouble() * (config.SerpentInitialOrbitRadiusMax - config.SerpentInitialOrbitRadiusMin));
        }
        else
        {
            // Re-stalk: start at 2-3x the final orbit radius
            orbitRadiusStart = config.SerpentOrbitRadius * (2f + (float)rand.NextDouble());
        }

        SetNextSpiralStep();

        UnderwaterHorrorsModSystem.DebugLog(entity.Api,
            $"Serpent spiral: starting at radius {orbitRadiusStart:F1}, first target {orbitRadiusEnd:F1} over {radiusTransitionDuration:F1}s");
    }

    private void SetNextSpiralStep()
    {
        var rand = entity.World.Rand;
        float reduction = config.SerpentSpiralReductionMin +
            (float)(rand.NextDouble() * (config.SerpentSpiralReductionMax - config.SerpentSpiralReductionMin));
        orbitRadiusEnd = Math.Max(config.SerpentOrbitRadius, orbitRadiusStart - reduction);
        radiusTransitionDuration = config.SerpentSpiralStepDurationMin +
            (float)(rand.NextDouble() * (config.SerpentSpiralStepDurationMax - config.SerpentSpiralStepDurationMin));
        radiusTransitionTime = 0;
    }

    private void OnRising(float deltaTime)
    {
        if (targetPlayer?.Entity == null) return;

        double playerY = targetPlayer.Entity.SidedPos.Y;
        double dy = playerY - entity.SidedPos.Y;

        // Move upward toward player depth
        entity.SidedPos.Motion.Y = config.SerpentRiseSpeed;

        // Also approach horizontally
        double dx = targetPlayer.Entity.SidedPos.X - entity.SidedPos.X;
        double dz = targetPlayer.Entity.SidedPos.Z - entity.SidedPos.Z;
        double horizDist = Math.Sqrt(dx * dx + dz * dz);

        if (horizDist > 1)
        {
            entity.SidedPos.Motion.X = (dx / horizDist) * config.SerpentApproachSpeed;
            entity.SidedPos.Motion.Z = (dz / horizDist) * config.SerpentApproachSpeed;
        }

        // Transition when near player depth
        if (Math.Abs(dy) < 3)
        {
            TransitionTo(SerpentState.Stalking);
        }
    }

    private void OnStalking(float deltaTime)
    {
        if (targetPlayer?.Entity == null) return;

        // Calculate current orbit radius (smooth interpolation for spiral)
        float radius;
        if (useSpiralApproach)
        {
            radiusTransitionTime += deltaTime;
            float t = Math.Min(1f, radiusTransitionTime / radiusTransitionDuration);
            radius = orbitRadiusStart + (orbitRadiusEnd - orbitRadiusStart) * t;

            if (t >= 1f)
            {
                orbitRadiusStart = orbitRadiusEnd;
                if (orbitRadiusStart <= config.SerpentOrbitRadius)
                {
                    UnderwaterHorrorsModSystem.DebugLog(entity.Api, "Serpent spiral complete, attacking");
                    TransitionTo(SerpentState.Attacking);
                    return;
                }
                SetNextSpiralStep();
                UnderwaterHorrorsModSystem.DebugLog(entity.Api,
                    $"Serpent spiral step: radius {orbitRadiusStart:F1} to {orbitRadiusEnd:F1} over {radiusTransitionDuration:F1}s");
            }
        }
        else
        {
            radius = config.SerpentOrbitRadius;
        }

        // Scale orbit speed inversely with radius so tangential speed stays constant
        float effectiveOrbitSpeed = config.SerpentOrbitSpeed * config.SerpentOrbitRadius / radius;
        orbitAngle += effectiveOrbitSpeed * deltaTime;

        double targetX = targetPlayer.Entity.SidedPos.X + Math.Cos(orbitAngle) * radius;
        double targetZ = targetPlayer.Entity.SidedPos.Z + Math.Sin(orbitAngle) * radius;
        double targetY = targetPlayer.Entity.SidedPos.Y;

        double dx = targetX - entity.SidedPos.X;
        double dy = targetY - entity.SidedPos.Y;
        double dz = targetZ - entity.SidedPos.Z;
        double dist = Math.Sqrt(dx * dx + dy * dy + dz * dz);

        if (dist > 0.5)
        {
            double speed = config.SerpentApproachSpeed * 2;
            entity.SidedPos.Motion.X = (dx / dist) * speed;
            entity.SidedPos.Motion.Y = (dy / dist) * speed;
            entity.SidedPos.Motion.Z = (dz / dist) * speed;
        }

        // For non-spiral re-stalk, use timed duration
        if (!useSpiralApproach && stateTimer >= stalkDuration)
        {
            TransitionTo(SerpentState.Attacking);
        }
    }

    private void OnAttacking(float deltaTime)
    {
        if (targetPlayer?.Entity == null) return;

        if (targetPlayer.Entity.MountedOn != null)
        {
            UnderwaterHorrorsModSystem.DebugLog(entity.Api, $"Serpent: {targetPlayer.PlayerName} is mounted, reverting to Stalking");
            TransitionTo(SerpentState.Stalking);
            return;
        }

        double dx = targetPlayer.Entity.SidedPos.X - entity.SidedPos.X;
        double dy = targetPlayer.Entity.SidedPos.Y - entity.SidedPos.Y;
        double dz = targetPlayer.Entity.SidedPos.Z - entity.SidedPos.Z;
        double dist = Math.Sqrt(dx * dx + dy * dy + dz * dz);

        // Rush toward player
        if (dist > 0.5)
        {
            entity.SidedPos.Motion.X = (dx / dist) * config.SerpentAttackSpeed;
            entity.SidedPos.Motion.Y = (dy / dist) * config.SerpentAttackSpeed;
            entity.SidedPos.Motion.Z = (dz / dist) * config.SerpentAttackSpeed;
        }

        // Deal damage when close
        attackCooldownTimer -= deltaTime;
        if (dist < config.SerpentAttackRange && attackCooldownTimer <= 0)
        {
            targetPlayer.Entity.ReceiveDamage(new DamageSource
            {
                Source = EnumDamageSource.Entity,
                SourceEntity = entity,
                Type = EnumDamageType.BluntAttack
            }, config.SerpentAttackDamage);
            attackCooldownTimer = config.SerpentAttackCooldown;
            UnderwaterHorrorsModSystem.DebugLog(entity.Api, $"Serpent hit {targetPlayer.PlayerName} for {config.SerpentAttackDamage} damage (dist: {dist:F1})");

            // Chance to disengage and circle back to stalking
            if (entity.World.Rand.NextDouble() < config.SerpentReStalkChance)
            {
                var rand = entity.World.Rand;
                stalkDuration = config.SerpentStalkDurationMin + (float)(rand.NextDouble() * (config.SerpentStalkDurationMax - config.SerpentStalkDurationMin));
                UnderwaterHorrorsModSystem.DebugLog(entity.Api, $"Serpent disengaging, returning to stalk for {stalkDuration:F1}s");
                TransitionTo(SerpentState.Stalking);
            }
        }
    }

    private void OnRetreating(float deltaTime)
    {
        // Swim toward spawn position
        double dx = spawnX - entity.SidedPos.X;
        double dy = spawnY - entity.SidedPos.Y;
        double dz = spawnZ - entity.SidedPos.Z;
        double dist = Math.Sqrt(dx * dx + dy * dy + dz * dz);

        if (dist > 3.0)
        {
            double speed = config.SerpentApproachSpeed * 2;
            entity.SidedPos.Motion.X = (dx / dist) * speed;
            entity.SidedPos.Motion.Y = (dy / dist) * speed;
            entity.SidedPos.Motion.Z = (dz / dist) * speed;
        }
        else
        {
            UnderwaterHorrorsModSystem.DebugLog(entity.Api, "Serpent reached spawn point, despawning");
            entity.Die(EnumDespawnReason.Expire);
            return;
        }

        // Safety timeout: despawn after 30s even if not at spawn
        if (stateTimer >= 30f)
        {
            UnderwaterHorrorsModSystem.DebugLog(entity.Api, "Serpent retreat timeout, despawning");
            entity.Die(EnumDespawnReason.Expire);
        }
    }

    public override string PropertyName() => "underwaterhorrors:serpentai";
}
