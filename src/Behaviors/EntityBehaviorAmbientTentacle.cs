using System;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;

namespace UnderwaterHorrors;

public enum AmbientTentacleState
{
    Rising,
    Orbiting,
    Sinking
}

public class EntityBehaviorAmbientTentacle : EntityBehaviorOceanCreature
{
    // Same chain config as the attack tentacle — shape and arch logic match.
    // See EntityBehaviorTentacle / TentacleSegmentChain for the math.
    private const int SegmentCount = 96;
    private const double SegmentVisualHeight = 0.84;

    private static readonly AssetLocation SegmentInnerAsset = new AssetLocation("underwaterhorrors", "krakententsegment");
    private static readonly AssetLocation SegmentMidAsset   = new AssetLocation("underwaterhorrors", "krakententsegment_mid");

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

    // Chain of segment entities filling the spline from kraken body to tip.
    private TentacleSegmentChain chain;

    public EntityBehaviorAmbientTentacle(Entity entity) : base(entity) { }

    public override void OnGameTick(float deltaTime)
    {
        if (!entity.Alive) return;
        if (entity.Api.Side != EnumAppSide.Server) return;
        if (entity.WatchedAttributes.GetBool("underwaterhorrors:static", false)) return;

        // Cap at sea level so the tentacle can't fly out of water
        // (attack tentacle already does this; ambient was missing it).
        ClampHeight();

        if (!initialized)
        {
            initialized = true;
            Initialize();
        }

        EnsureChainCreated();
        chain?.EnsureSpawned();

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

        UpdateChainPositions();
        UpdateHeadFacing();
    }

    public override void OnEntityDespawn(EntityDespawnData despawn)
    {
        chain?.Despawn();
        base.OnEntityDespawn(despawn);
    }

    private void EnsureChainCreated()
    {
        if (chain != null) return;
        chain = new TentacleSegmentChain(entity, SegmentCount, SegmentVisualHeight,
            SegmentInnerAsset, SegmentMidAsset);
    }

    private void UpdateChainPositions()
    {
        if (chain == null) return;
        GetBodyAnchor(out double anchorX, out double anchorY, out double anchorZ);
        chain.UpdatePositions(anchorX, anchorY, anchorZ, config.TentacleArchHeightFactor);
    }

    private void UpdateHeadFacing()
    {
        // Ambient tentacles don't attack, so always align with the spline
        // tangent at the tip — the head bell points along where the spline
        // is going (toward the surface, leaning over the orbit point).
        GetBodyAnchor(out double anchorX, out double anchorY, out double anchorZ);
        var anchor = new Vec3d(anchorX, anchorY, anchorZ);
        TentacleHeadAlignment.AlignToTangent(entity, anchor, config.TentacleArchHeightFactor);
    }

    /// <summary>
    /// Anchor point for the spline base. Normally the kraken body block;
    /// falls back to a point below the tip if the body is gone.
    /// </summary>
    private void GetBodyAnchor(out double x, out double y, out double z)
    {
        Entity body = GetBody();
        if (body != null && body.Alive)
        {
            x = body.Pos.X;
            y = body.Pos.Y + 1;
            z = body.Pos.Z;
        }
        else
        {
            x = entity.Pos.X;
            y = entity.Pos.Y - 5;
            z = entity.Pos.Z;
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

            surfaceX = targetPlayer.Entity.Pos.X + Math.Cos(angle) * dist;
            // Target sea-surface Y, not player Y — otherwise tentacles
            // chase the player onto cliffs or deep underwater.
            surfaceY = Math.Min(targetPlayer.Entity.Pos.Y, config.CreatureMaxY);
            surfaceZ = targetPlayer.Entity.Pos.Z + Math.Sin(angle) * dist;
        }
        else
        {
            // Fallback: rise straight up from current position
            surfaceX = entity.Pos.X;
            surfaceY = entity.Pos.Y + 20;
            surfaceZ = entity.Pos.Z;
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

        double dx = surfaceX - entity.Pos.X;
        double dy = surfaceY - entity.Pos.Y;
        double dz = surfaceZ - entity.Pos.Z;
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

        double dx = targetX - entity.Pos.X;
        double dy = targetY - entity.Pos.Y;
        double dz = targetZ - entity.Pos.Z;

        entity.Pos.Motion.X = dx * 0.1;
        entity.Pos.Motion.Y = dy * 0.1;
        entity.Pos.Motion.Z = dz * 0.1;
    }

    private void OnSinking(float deltaTime)
    {
        entity.Pos.Motion.X = 0;
        entity.Pos.Motion.Y = -config.RetreatSpeed;
        entity.Pos.Motion.Z = 0;

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
