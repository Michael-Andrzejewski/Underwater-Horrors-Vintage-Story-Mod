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

    // Tip position (synced to client via WatchedAttributes)
    private double tipX, tipY, tipZ;
    private bool tipInitialized;

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
        AnchorToBody();
        TrackDamage();

        // Initialize tip above body on first tick
        if (!tipInitialized)
        {
            tipX = entity.SidedPos.X;
            tipY = entity.SidedPos.Y + 3.0;
            tipZ = entity.SidedPos.Z;
            tipInitialized = true;
            SyncTipPosition();
        }

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

    private void AnchorToBody()
    {
        long bodyId = entity.WatchedAttributes.GetLong("underwaterhorrors:krakenBodyId");
        Entity body = entity.World.GetEntityById(bodyId);

        if (body != null && body.Alive)
        {
            entity.SidedPos.X = body.SidedPos.X;
            entity.SidedPos.Y = body.SidedPos.Y + 1;
            entity.SidedPos.Z = body.SidedPos.Z;
            entity.SidedPos.Motion.Set(0, 0, 0);
        }
    }

    private void SyncTipPosition()
    {
        entity.WatchedAttributes.SetDouble("underwaterhorrors:tipX", tipX);
        entity.WatchedAttributes.SetDouble("underwaterhorrors:tipY", tipY);
        entity.WatchedAttributes.SetDouble("underwaterhorrors:tipZ", tipZ);
        entity.WatchedAttributes.MarkPathDirty("underwaterhorrors:tipX");
        entity.WatchedAttributes.MarkPathDirty("underwaterhorrors:tipY");
        entity.WatchedAttributes.MarkPathDirty("underwaterhorrors:tipZ");
    }

    private void MoveTipToward(double targetX, double targetY, double targetZ, float speed, float deltaTime)
    {
        double dx = targetX - tipX;
        double dy = targetY - tipY;
        double dz = targetZ - tipZ;
        double dist = Math.Sqrt(dx * dx + dy * dy + dz * dz);

        if (dist < 0.1)
        {
            tipX = targetX;
            tipY = targetY;
            tipZ = targetZ;
        }
        else
        {
            double step = speed * deltaTime;
            if (step > dist) step = dist;
            tipX += (dx / dist) * step;
            tipY += (dy / dist) * step;
            tipZ += (dz / dist) * step;
        }

        SyncTipPosition();
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
        // Gentle sway above body
        double swayX = Math.Sin(stateTimer * 1.2) * config.TentacleIdleSwayAmplitude;
        double swayZ = Math.Cos(stateTimer * 0.9) * config.TentacleIdleSwayAmplitude;
        double swayY = Math.Sin(stateTimer * 0.7) * config.TentacleIdleSwayAmplitude * 0.3;

        tipX = entity.SidedPos.X + swayX;
        tipY = entity.SidedPos.Y + 3.0 + swayY;
        tipZ = entity.SidedPos.Z + swayZ;
        SyncTipPosition();

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
        MoveTipToward(
            targetPlayer.Entity.SidedPos.X,
            clampedY,
            targetPlayer.Entity.SidedPos.Z,
            config.TentacleReachSpeed * 60f, // convert from per-tick motion to per-second speed
            deltaTime
        );

        // Check grab range from tip to player
        double dx = tipX - targetPlayer.Entity.SidedPos.X;
        double dy = tipY - targetPlayer.Entity.SidedPos.Y;
        double dz = tipZ - targetPlayer.Entity.SidedPos.Z;
        double dist = Math.Sqrt(dx * dx + dy * dy + dz * dz);

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

        // Lock tip below player so they can still see
        tipX = targetPlayer.Entity.SidedPos.X;
        tipY = targetPlayer.Entity.SidedPos.Y + config.TentacleGrabYOffset;
        tipZ = targetPlayer.Entity.SidedPos.Z;
        SyncTipPosition();

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
                    goto UpdateTipPos;
                }
            }

            // Set pitch on ServerPos BEFORE teleport so it's included in the packet
            float yaw = targetPlayer.Entity.Pos.Yaw;
            float headYaw = targetPlayer.Entity.Pos.HeadYaw;
            float headPitch = targetPlayer.Entity.Pos.HeadPitch;
            float downPitch = (float)(Math.PI / 2); // 90 degrees — straight down

            targetPlayer.Entity.ServerPos.Yaw = yaw;
            targetPlayer.Entity.ServerPos.Pitch = downPitch;
            targetPlayer.Entity.ServerPos.HeadYaw = headYaw;
            targetPlayer.Entity.ServerPos.HeadPitch = downPitch;

            targetPlayer.Entity.TeleportToDouble(newX, newY, newZ);
        }

        UpdateTipPos:
        // Keep tip tracking player's current position
        tipX = targetPlayer.Entity.SidedPos.X;
        tipY = targetPlayer.Entity.SidedPos.Y + config.TentacleGrabYOffset;
        tipZ = targetPlayer.Entity.SidedPos.Z;
        SyncTipPosition();
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
        // Slowly retract tip toward body during cooldown
        MoveTipToward(
            entity.SidedPos.X,
            entity.SidedPos.Y + 3.0,
            entity.SidedPos.Z,
            config.TentacleRetractSpeed,
            deltaTime
        );

        if (stateTimer >= cooldownDuration)
        {
            TransitionTo(TentacleState.Reaching);
        }
    }

    private void OnRetreating(float deltaTime)
    {
        // Retract tip toward body
        MoveTipToward(
            entity.SidedPos.X,
            entity.SidedPos.Y,
            entity.SidedPos.Z,
            config.TentacleRetractSpeed,
            deltaTime
        );

        if (stateTimer >= config.RetreatDuration)
        {
            UnderwaterHorrorsModSystem.DebugLog(entity.Api, "Tentacle retreat complete, despawning");
            entity.Die(EnumDespawnReason.Expire);
        }
    }

    public override string PropertyName() => "underwaterhorrors:tentacle";
}
