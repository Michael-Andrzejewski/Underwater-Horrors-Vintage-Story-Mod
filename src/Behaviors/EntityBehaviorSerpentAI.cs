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

public class EntityBehaviorSerpentAI : EntityBehaviorOceanCreature
{
    private SerpentState state = SerpentState.Rising;
    private float stateTimer;
    private float stalkDuration;
    private float orbitAngle;
    private float attackCooldownTimer;

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
        base.Initialize(properties, attributes);
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

        // Throttled shallow water check (not during Rising or already Retreating)
        if (state != SerpentState.Rising && state != SerpentState.Retreating)
        {
            UpdateShallowWaterCheck(deltaTime);

            if (IsInShallowWater)
            {
                if (config.DebugLogging)
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
                            if (config.DebugLogging)
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

    private void TransitionTo(SerpentState newState)
    {
        SerpentState oldState = state;
        state = newState;
        stateTimer = 0;

        if (config.DebugLogging)
        {
            string playerName = targetPlayer?.PlayerName ?? "unknown";
            UnderwaterHorrorsModSystem.DebugLog(entity.Api, $"Serpent state: {oldState} to {newState} (target: {playerName})");
        }

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

        if (config.DebugLogging)
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
        if (Math.Abs(playerY - entity.SidedPos.Y) < 3)
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
                    if (config.DebugLogging)
                        UnderwaterHorrorsModSystem.DebugLog(entity.Api, "Serpent spiral complete, attacking");
                    TransitionTo(SerpentState.Attacking);
                    return;
                }
                SetNextSpiralStep();
                if (config.DebugLogging)
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

        MoveToward(targetX, targetY, targetZ, config.SerpentApproachSpeed * 2, 0.5);

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
            if (config.DebugLogging)
                UnderwaterHorrorsModSystem.DebugLog(entity.Api, $"Serpent: {targetPlayer.PlayerName} is mounted, reverting to Stalking");
            TransitionTo(SerpentState.Stalking);
            return;
        }

        MoveToward(
            targetPlayer.Entity.SidedPos.X,
            targetPlayer.Entity.SidedPos.Y,
            targetPlayer.Entity.SidedPos.Z,
            config.SerpentAttackSpeed, 0.5);

        // Deal damage when close
        double dist = entity.SidedPos.DistanceTo(targetPlayer.Entity.SidedPos.XYZ);
        attackCooldownTimer -= deltaTime;
        if (dist < config.SerpentAttackRange && attackCooldownTimer <= 0)
        {
            targetPlayer.Entity.ReceiveDamage(new DamageSource
            {
                Source = EnumDamageSource.Entity,
                SourceEntity = entity,
                Type = EnumDamageType.PiercingAttack,
                DamageTier = config.SerpentDamageTier
            }, config.SerpentAttackDamage);
            attackCooldownTimer = config.SerpentAttackCooldown;
            if (config.DebugLogging)
                UnderwaterHorrorsModSystem.DebugLog(entity.Api, $"Serpent hit {targetPlayer.PlayerName} for {config.SerpentAttackDamage} damage (dist: {dist:F1})");

            // Chance to disengage and circle back to stalking
            if (entity.World.Rand.NextDouble() < config.SerpentReStalkChance)
            {
                var rand = entity.World.Rand;
                stalkDuration = config.SerpentStalkDurationMin + (float)(rand.NextDouble() * (config.SerpentStalkDurationMax - config.SerpentStalkDurationMin));
                if (config.DebugLogging)
                    UnderwaterHorrorsModSystem.DebugLog(entity.Api, $"Serpent disengaging, returning to stalk for {stalkDuration:F1}s");
                TransitionTo(SerpentState.Stalking);
            }
        }
    }

    private void OnRetreating(float deltaTime)
    {
        // Throttled check: if player is back in deep water, cancel retreat and re-engage
        UpdateShallowWaterCheck(deltaTime);
        if (targetPlayer?.Entity != null && targetPlayer.Entity.Alive &&
            targetPlayer.Entity.MountedOn == null && !IsInShallowWater)
        {
            if (config.DebugLogging)
                UnderwaterHorrorsModSystem.DebugLog(entity.Api, "Serpent: player back in deep water, resuming stalking");
            TransitionTo(SerpentState.Stalking);
            return;
        }

        // Swim toward spawn position
        double dx = spawnX - entity.SidedPos.X;
        double dy = spawnY - entity.SidedPos.Y;
        double dz = spawnZ - entity.SidedPos.Z;
        double dist = Math.Sqrt(dx * dx + dy * dy + dz * dz);

        if (dist > 3.0)
        {
            MoveToward(spawnX, spawnY, spawnZ, config.SerpentApproachSpeed * 2);
        }
        else
        {
            if (config.DebugLogging)
                UnderwaterHorrorsModSystem.DebugLog(entity.Api, "Serpent reached spawn point, despawning");
            entity.Die(EnumDespawnReason.Expire);
            return;
        }

        // Safety timeout: despawn after 30s even if not at spawn
        if (stateTimer >= 30f)
        {
            if (config.DebugLogging)
                UnderwaterHorrorsModSystem.DebugLog(entity.Api, "Serpent retreat timeout, despawning");
            entity.Die(EnumDespawnReason.Expire);
        }
    }

    public override string PropertyName() => "underwaterhorrors:serpentai";
}
