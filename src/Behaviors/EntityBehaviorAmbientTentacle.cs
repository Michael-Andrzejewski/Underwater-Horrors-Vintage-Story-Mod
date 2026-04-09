using System;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Datastructures;

namespace UnderwaterHorrors;

public enum AmbientTentacleState
{
    Rising,
    Orbiting,
    Sinking
}

public class EntityBehaviorAmbientTentacle : EntityBehaviorOceanCreature
{
    private AmbientTentacleState state = AmbientTentacleState.Rising;
    private float stateTimer;
    private bool initialized;

    // Surface point this tentacle is heading toward / orbiting around
    private double surfaceX, surfaceY, surfaceZ;

    // Randomized rise speed for this specific tentacle
    private float riseSpeed;

    // Orbit phase offset (radians) so tentacles don't overlap
    private float orbitPhase;

    // Cached kraken body reference for checking the sink signal
    private long cachedBodyId;
    private Entity cachedBody;

    public EntityBehaviorAmbientTentacle(Entity entity) : base(entity) { }

    public override void OnGameTick(float deltaTime)
    {
        if (!entity.Alive) return;
        if (entity.Api.Side != EnumAppSide.Server) return;

        if (!initialized)
        {
            initialized = true;
            Initialize();
        }

        // Check sink signal from kraken body every tick during Orbiting
        if (state == AmbientTentacleState.Orbiting && ShouldSink())
        {
            TransitionTo(AmbientTentacleState.Sinking);
        }

        stateTimer += deltaTime;

        switch (state)
        {
            case AmbientTentacleState.Rising:
                OnRising(deltaTime);
                break;
            case AmbientTentacleState.Orbiting:
                OnOrbiting(deltaTime);
                break;
            case AmbientTentacleState.Sinking:
                OnSinking(deltaTime);
                break;
        }
    }

    private void Initialize()
    {
        ResolveTarget();

        orbitPhase = entity.WatchedAttributes.GetFloat("underwaterhorrors:orbitPhase", 0f);
        cachedBodyId = entity.WatchedAttributes.GetLong("underwaterhorrors:krakenBodyId");

        // Pick a random surface point within config range of the player
        if (targetPlayer?.Entity != null)
        {
            var rand = entity.World.Rand;
            double range = config.AmbientTentacleSurfaceRange;
            double angle = rand.NextDouble() * Math.PI * 2;
            double dist = rand.NextDouble() * range;

            surfaceX = targetPlayer.Entity.SidedPos.X + Math.Cos(angle) * dist;
            surfaceY = targetPlayer.Entity.SidedPos.Y;
            surfaceZ = targetPlayer.Entity.SidedPos.Z + Math.Sin(angle) * dist;
        }
        else
        {
            // Fallback: rise straight up from current position
            surfaceX = entity.SidedPos.X;
            surfaceY = entity.SidedPos.Y + 20;
            surfaceZ = entity.SidedPos.Z;
        }

        // Randomize rise speed: base speed * random factor (0.8 to 1.3)
        float factor = 0.8f + (float)(entity.World.Rand.NextDouble() * 0.5);
        riseSpeed = config.AmbientTentacleRiseSpeed * factor;

        if (config.DebugLogging)
            UnderwaterHorrorsModSystem.DebugLog(entity.Api,
                $"Ambient tentacle initialized: surface ({surfaceX:F1}, {surfaceY:F1}, {surfaceZ:F1}), riseSpeed={riseSpeed:F3}, phase={orbitPhase:F2}");
    }

    private void TransitionTo(AmbientTentacleState newState)
    {
        state = newState;
        stateTimer = 0;

        if (config.DebugLogging)
            UnderwaterHorrorsModSystem.DebugLog(entity.Api, $"Ambient tentacle state: {newState}");
    }

    private void OnRising(float deltaTime)
    {
        MoveToward(surfaceX, surfaceY, surfaceZ, riseSpeed);

        double dx = surfaceX - entity.SidedPos.X;
        double dy = surfaceY - entity.SidedPos.Y;
        double dz = surfaceZ - entity.SidedPos.Z;
        double dist = Math.Sqrt(dx * dx + dy * dy + dz * dz);

        if (dist < 2.0)
        {
            TransitionTo(AmbientTentacleState.Orbiting);
        }
    }

    private void OnOrbiting(float deltaTime)
    {
        float radius = config.AmbientTentacleOrbitRadius;
        float speed = config.AmbientTentacleOrbitSpeed;
        float bobAmp = config.AmbientTentacleBobAmplitude;
        float bobSpeed = config.AmbientTentacleBobSpeed;

        float orbitAngle = orbitPhase + stateTimer * speed;

        double targetX = surfaceX + Math.Cos(orbitAngle) * radius;
        double targetZ = surfaceZ + Math.Sin(orbitAngle) * radius;
        double targetY = surfaceY + Math.Sin(stateTimer * bobSpeed) * bobAmp;

        double dx = targetX - entity.SidedPos.X;
        double dy = targetY - entity.SidedPos.Y;
        double dz = targetZ - entity.SidedPos.Z;

        entity.SidedPos.Motion.X = dx * 0.1;
        entity.SidedPos.Motion.Y = dy * 0.1;
        entity.SidedPos.Motion.Z = dz * 0.1;
    }

    private void OnSinking(float deltaTime)
    {
        entity.SidedPos.Motion.X = 0;
        entity.SidedPos.Motion.Y = -config.RetreatSpeed;
        entity.SidedPos.Motion.Z = 0;

        if (stateTimer >= config.RetreatDuration)
        {
            entity.Die(EnumDespawnReason.Expire);
        }
    }

    private bool ShouldSink()
    {
        Entity body = GetBody();
        if (body == null || !body.Alive) return true;
        return body.WatchedAttributes.GetBool("underwaterhorrors:sinkAmbient", false);
    }

    private Entity GetBody()
    {
        if (cachedBody != null && cachedBody.Alive && cachedBody.EntityId == cachedBodyId)
            return cachedBody;

        cachedBody = cachedBodyId != 0 ? entity.World.GetEntityById(cachedBodyId) : null;
        return cachedBody;
    }

    public override string PropertyName() => "underwaterhorrors:ambienttentacle";
}
