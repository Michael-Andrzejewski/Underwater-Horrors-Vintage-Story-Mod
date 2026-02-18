using System;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;

namespace UnderwaterHorrors;

public enum TentacleState
{
    Idle,
    Reaching,
    Grabbing,
    Dragging,
    Cooldown,
    Retreating
}

public class EntityBehaviorTentacle : EntityBehavior
{
    private TentacleState state = TentacleState.Idle;
    private float stateTimer;
    private float cooldownDuration;
    private float accumulatedDamage;
    private float lastKnownHealth;
    private bool healthInitialized;
    private UnderwaterHorrorsConfig config;
    private IPlayer targetPlayer;
    private bool targetResolved;
    private bool speedDebuffApplied;

    public EntityBehaviorTentacle(Entity entity) : base(entity) { }

    public override void Initialize(EntityProperties properties, JsonObject attributes)
    {
        config = UnderwaterHorrorsModSystem.Config;
    }

    public override void OnGameTick(float deltaTime)
    {
        if (!entity.Alive) return;
        if (entity.Api.Side != EnumAppSide.Server) return;

        ResolveTarget();
        ClampHeight();
        TrackDamage();

        // Check shallow water retreat (not during Idle, Cooldown, or already Retreating, and not when player is on a boat)
        if (state == TentacleState.Reaching || state == TentacleState.Grabbing || state == TentacleState.Dragging)
        {
            if (targetPlayer?.Entity?.MountedOn == null &&
                TargetingHelper.IsPlayerInShallowWater(entity, targetPlayer, config.ShallowWaterThreshold))
            {
                UnderwaterHorrorsModSystem.DebugLog(entity.Api, "Tentacle: player in shallow water, retreating");
                TransitionTo(TentacleState.Retreating);
            }
        }

        stateTimer += deltaTime;

        switch (state)
        {
            case TentacleState.Idle:
                OnIdle(deltaTime);
                break;
            case TentacleState.Reaching:
                OnReaching(deltaTime);
                break;
            case TentacleState.Grabbing:
                OnGrabbing(deltaTime);
                break;
            case TentacleState.Dragging:
                OnDragging(deltaTime);
                break;
            case TentacleState.Cooldown:
                OnCooldown(deltaTime);
                break;
            case TentacleState.Retreating:
                OnRetreating(deltaTime);
                break;
        }
    }

    public override void OnEntityDespawn(EntityDespawnData despawn)
    {
        RemoveSpeedDebuff();
        base.OnEntityDespawn(despawn);
    }

    private void ResolveTarget()
    {
        if (targetResolved) return;
        targetResolved = true;

        targetPlayer = TargetingHelper.ResolveTarget(entity);
    }

    private void TrackDamage()
    {
        float currentHealth = entity.WatchedAttributes.GetFloat("health");
        if (!healthInitialized)
        {
            lastKnownHealth = currentHealth;
            healthInitialized = true;
            return;
        }

        if (currentHealth < lastKnownHealth)
        {
            float dmg = lastKnownHealth - currentHealth;
            accumulatedDamage += dmg;
            UnderwaterHorrorsModSystem.DebugLog(entity.Api, $"Tentacle took {dmg:F1} damage (accumulated: {accumulatedDamage:F1} of {config.TentacleReleaseDamageThreshold} to release)");
        }
        lastKnownHealth = currentHealth;

        if (accumulatedDamage >= config.TentacleReleaseDamageThreshold && (state == TentacleState.Grabbing || state == TentacleState.Dragging))
        {
            UnderwaterHorrorsModSystem.DebugLog(entity.Api, $"Tentacle releasing player: damage threshold reached ({accumulatedDamage:F1} of {config.TentacleReleaseDamageThreshold})");
            TransitionTo(TentacleState.Cooldown);
        }
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

    private void ApplySpeedDebuff()
    {
        if (speedDebuffApplied || targetPlayer?.Entity == null) return;
        speedDebuffApplied = true;
        targetPlayer.Entity.Stats.Set("walkspeed", "tentacledrag", -0.9f);
        UnderwaterHorrorsModSystem.DebugLog(entity.Api, "Tentacle: slowing player movement");
    }

    private void RemoveSpeedDebuff()
    {
        if (!speedDebuffApplied || targetPlayer?.Entity == null) return;
        speedDebuffApplied = false;
        targetPlayer.Entity.Stats.Remove("walkspeed", "tentacledrag");
        UnderwaterHorrorsModSystem.DebugLog(entity.Api, "Tentacle: restored player movement");
    }

    private void TransitionTo(TentacleState newState)
    {
        TentacleState oldState = state;

        // Clean up speed debuff when leaving grab/drag states
        if ((oldState == TentacleState.Grabbing || oldState == TentacleState.Dragging) &&
            newState != TentacleState.Grabbing && newState != TentacleState.Dragging)
        {
            RemoveSpeedDebuff();
        }

        state = newState;
        stateTimer = 0;

        string playerName = targetPlayer?.PlayerName ?? "unknown";
        UnderwaterHorrorsModSystem.DebugLog(entity.Api, $"Tentacle state: {oldState} to {newState} (target: {playerName})");

        if (newState == TentacleState.Cooldown)
        {
            accumulatedDamage = 0;
            var rand = entity.World.Rand;
            cooldownDuration = config.TentacleCooldownMin + (float)(rand.NextDouble() * (config.TentacleCooldownMax - config.TentacleCooldownMin));
            UnderwaterHorrorsModSystem.DebugLog(entity.Api, $"Tentacle cooldown: {cooldownDuration:F1}s");
        }
    }

    private void OnIdle(float deltaTime)
    {
        if (stateTimer >= config.TentacleIdleDuration)
        {
            TransitionTo(TentacleState.Reaching);
        }
    }

    private void OnReaching(float deltaTime)
    {
        if (targetPlayer?.Entity == null || !targetPlayer.Entity.Alive)
        {
            TransitionTo(TentacleState.Cooldown);
            return;
        }

        if (targetPlayer.Entity.MountedOn != null)
        {
            UnderwaterHorrorsModSystem.DebugLog(entity.Api, $"Tentacle: {targetPlayer.PlayerName} is mounted, entering cooldown");
            TransitionTo(TentacleState.Cooldown);
            return;
        }

        double clampedY = Math.Min(targetPlayer.Entity.SidedPos.Y, config.CreatureMaxY);
        MoveToward(targetPlayer.Entity.SidedPos.X, clampedY, targetPlayer.Entity.SidedPos.Z, config.TentacleReachSpeed);

        double dist = entity.SidedPos.DistanceTo(targetPlayer.Entity.SidedPos.XYZ);
        if (dist < config.TentacleGrabRange)
        {
            TransitionTo(TentacleState.Grabbing);
        }
    }

    private void OnGrabbing(float deltaTime)
    {
        if (targetPlayer?.Entity == null || !targetPlayer.Entity.Alive)
        {
            TransitionTo(TentacleState.Cooldown);
            return;
        }

        ApplySpeedDebuff();

        // Lock position below player so they can still see
        entity.SidedPos.X = targetPlayer.Entity.SidedPos.X;
        entity.SidedPos.Y = targetPlayer.Entity.SidedPos.Y + config.TentacleGrabYOffset;
        entity.SidedPos.Z = targetPlayer.Entity.SidedPos.Z;
        entity.SidedPos.Motion.Set(0, 0, 0);

        if (stateTimer >= config.TentacleGrabDuration)
        {
            TransitionTo(TentacleState.Dragging);
        }
    }

    private void OnDragging(float deltaTime)
    {
        if (targetPlayer?.Entity == null || !targetPlayer.Entity.Alive)
        {
            TransitionTo(TentacleState.Cooldown);
            return;
        }

        ApplySpeedDebuff();

        // Find kraken body position
        long bodyId = entity.WatchedAttributes.GetLong("underwaterhorrors:krakenBodyId");
        Entity body = entity.World.GetEntityById(bodyId);

        if (body == null || !body.Alive)
        {
            UnderwaterHorrorsModSystem.DebugLog(entity.Api, "Tentacle: kraken body gone, entering cooldown");
            TransitionTo(TentacleState.Cooldown);
            return;
        }

        double playerX = targetPlayer.Entity.SidedPos.X;
        double playerY = targetPlayer.Entity.SidedPos.Y;
        double playerZ = targetPlayer.Entity.SidedPos.Z;

        double dx = body.SidedPos.X - playerX;
        double dy = body.SidedPos.Y - playerY;
        double dz = body.SidedPos.Z - playerZ;
        double dist = Math.Sqrt(dx * dx + dy * dy + dz * dz);

        if (dist > 0.5)
        {
            double dragStep = config.TentacleDragSpeed * deltaTime;
            double nx = dx / dist;
            double ny = dy / dist;
            double nz = dz / dist;

            double newX = playerX + nx * dragStep;
            double newY = playerY + ny * dragStep;
            double newZ = playerZ + nz * dragStep;

            // Check for solid blocks at destination to avoid dragging through the sea floor
            if (!IsPositionPassable(newX, newY, newZ))
            {
                // Try horizontal only
                newY = playerY;
                if (!IsPositionPassable(newX, newY, newZ))
                {
                    // Path fully blocked, skip movement this tick
                    goto UpdateTentaclePos;
                }
            }

            // Save player's current facing direction before teleport
            float yaw = targetPlayer.Entity.Pos.Yaw;
            float pitch = targetPlayer.Entity.Pos.Pitch;
            float roll = targetPlayer.Entity.Pos.Roll;
            float headYaw = targetPlayer.Entity.Pos.HeadYaw;
            float headPitch = targetPlayer.Entity.Pos.HeadPitch;

            targetPlayer.Entity.TeleportToDouble(newX, newY, newZ);

            // Restore rotation so the teleport doesn't snap the player's view
            targetPlayer.Entity.ServerPos.Yaw = yaw;
            targetPlayer.Entity.ServerPos.Pitch = pitch;
            targetPlayer.Entity.ServerPos.Roll = roll;
            targetPlayer.Entity.ServerPos.HeadYaw = headYaw;
            targetPlayer.Entity.ServerPos.HeadPitch = headPitch;
            targetPlayer.Entity.Pos.Yaw = yaw;
            targetPlayer.Entity.Pos.Pitch = pitch;
            targetPlayer.Entity.Pos.Roll = roll;
            targetPlayer.Entity.Pos.HeadYaw = headYaw;
            targetPlayer.Entity.Pos.HeadPitch = headPitch;
        }

        UpdateTentaclePos:
        // Keep tentacle below player's current position
        entity.TeleportToDouble(
            targetPlayer.Entity.SidedPos.X,
            targetPlayer.Entity.SidedPos.Y + config.TentacleGrabYOffset,
            targetPlayer.Entity.SidedPos.Z
        );
    }

    private bool IsPositionPassable(double x, double y, double z)
    {
        BlockPos pos = new BlockPos((int)x, (int)y, (int)z, 0);
        Block block = entity.World.BlockAccessor.GetBlock(pos);
        // Passable if the block is liquid or air (not solid)
        return block == null || block.BlockMaterial == EnumBlockMaterial.Liquid || !block.SideSolid[BlockFacing.UP.Index];
    }

    private void OnCooldown(float deltaTime)
    {
        if (stateTimer >= cooldownDuration)
        {
            TransitionTo(TentacleState.Reaching);
        }
    }

    private void OnRetreating(float deltaTime)
    {
        // Move downward toward kraken body or just sink
        long bodyId = entity.WatchedAttributes.GetLong("underwaterhorrors:krakenBodyId");
        Entity body = entity.World.GetEntityById(bodyId);

        if (body != null && body.Alive)
        {
            MoveToward(body.SidedPos.X, body.SidedPos.Y, body.SidedPos.Z, config.TentacleReachSpeed);
        }
        else
        {
            entity.SidedPos.Motion.X = 0;
            entity.SidedPos.Motion.Y = -config.RetreatSpeed;
            entity.SidedPos.Motion.Z = 0;
        }

        if (stateTimer >= config.RetreatDuration)
        {
            UnderwaterHorrorsModSystem.DebugLog(entity.Api, "Tentacle retreat complete, despawning");
            entity.Die(EnumDespawnReason.Expire);
        }
    }

    private void MoveToward(double targetX, double targetY, double targetZ, float speed)
    {
        double dx = targetX - entity.SidedPos.X;
        double dy = targetY - entity.SidedPos.Y;
        double dz = targetZ - entity.SidedPos.Z;
        double dist = Math.Sqrt(dx * dx + dy * dy + dz * dz);

        if (dist < 0.1) return;

        entity.SidedPos.Motion.X = (dx / dist) * speed;
        entity.SidedPos.Motion.Y = (dy / dist) * speed;
        entity.SidedPos.Motion.Z = (dz / dist) * speed;
    }

    public override string PropertyName() => "underwaterhorrors:tentacle";
}
