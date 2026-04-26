using System;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;

namespace UnderwaterHorrors;

public enum AmbientTentacleState
{
    Rising,                // Riser only: moving up to the surface point
    Orbiting,              // Riser only: orbiting at surface point
    Wandering,             // Ground only: crawling toward a sea-floor target
    WanderingIdle,         // Ground only: brief pause at the target
    Scattering,            // Riser only: brief descent after scatter signal
    SurfaceWandering,      // Riser only: hypnotic midwater wander above body
    SurfaceWanderingIdle,  // Riser only: brief pause at the midwater target
    Sinking,               // Emergency despawn (kraken body died, etc.)
}

public class EntityBehaviorAmbientTentacle : EntityBehaviorOceanCreature
{
    // Same chain config as the attack tentacle — shape and arch logic match.
    // See EntityBehaviorTentacle / TentacleSegmentChain for the math.
    private const int SegmentCount = 96;
    private const double SegmentVisualHeight = 0.84;
    private const double TipMidClawVisualHeight = 0.84;

    private static readonly AssetLocation SegmentInnerAsset = new AssetLocation("underwaterhorrors", "krakententsegment");
    private static readonly AssetLocation SegmentMidAsset   = new AssetLocation("underwaterhorrors", "krakententsegment_mid");
    private static readonly AssetLocation TipMidClawAsset   = new AssetLocation("underwaterhorrors", "krakententsegment_mid_claw");

    private AmbientTentacleState state = AmbientTentacleState.Rising;
    private float stateTimer;
    private bool initialized;
    private bool groundMode;

    // Surface (riser) target.
    private double surfaceX, surfaceY, surfaceZ;

    // Sea-floor wander target.
    private double wanderX, wanderY, wanderZ;
    private float wanderIdleDuration;

    // Randomized rise speed for this specific tentacle
    private float riseSpeed;

    // Orbit phase offset (radians) so tentacles don't overlap
    private float orbitPhase;

    // Cached kraken body reference
    private long cachedBodyId;
    private Entity cachedBody;

    // Chain of segment entities filling the spline from kraken body to tip.
    private TentacleSegmentChain chain;

    // Kraken-death short-circuit (mirrors EntityBehaviorTentacle).
    private bool krakenDeathHandled;
    private float krakenDeathTimer;

    public EntityBehaviorAmbientTentacle(Entity entity) : base(entity) { }

    public override void OnGameTick(float deltaTime)
    {
        if (!entity.Alive) return;
        if (entity.Api.Side != EnumAppSide.Server) return;
        if (entity.WatchedAttributes.GetBool("underwaterhorrors:static", false)) return;

        ClampHeight();

        if (!initialized)
        {
            initialized = true;
            Initialize();
        }

        // Kraken-death short-circuit — fall passively, don't despawn the
        // chain immediately, no further state logic.
        Entity body = GetBody();
        if (body == null || !body.Alive)
        {
            if (!krakenDeathHandled)
            {
                krakenDeathHandled = true;
                krakenDeathTimer = 0f;
                if (config.DebugLogging)
                    UnderwaterHorrorsModSystem.DebugLog(entity.Api,
                        "Ambient tentacle: kraken body dead, falling passively.");
            }
            krakenDeathTimer += deltaTime;
            if (krakenDeathTimer > config.TentacleKrakenDeathFallDuration)
            {
                entity.Die(EnumDespawnReason.Expire);
                return;
            }
            UpdateChainPositions();
            UpdateHeadFacing();
            return;
        }

        EnsureChainCreated();
        chain?.EnsureSpawned();

        // Detect the scatter signal — when the attack tentacle starts
        // pursuing, surface-orbiting risers run a brief Scattering descent
        // and then settle into SurfaceWandering (midwater hypnotic wander
        // above the kraken body). Ground tentacles already wander on the
        // floor and ignore this signal.
        if ((state == AmbientTentacleState.Orbiting || state == AmbientTentacleState.Rising)
            && body.WatchedAttributes.GetBool("underwaterhorrors:scatterAmbient", false))
        {
            TransitionTo(AmbientTentacleState.Scattering);
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
            case AmbientTentacleState.Wandering:
                OnWandering(deltaTime);
                break;
            case AmbientTentacleState.WanderingIdle:
                OnWanderingIdle(deltaTime);
                break;
            case AmbientTentacleState.Scattering:
                OnScattering(deltaTime);
                break;
            case AmbientTentacleState.SurfaceWandering:
                OnSurfaceWandering(deltaTime);
                break;
            case AmbientTentacleState.SurfaceWanderingIdle:
                OnSurfaceWanderingIdle(deltaTime);
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

    /// <summary>
    /// Fires when the ambient head dies via damage. Force-despawn the
    /// chain and the head itself; otherwise deaddecay holds the corpse
    /// (and the visible chain) for the full decay window.
    /// </summary>
    public override void OnEntityDeath(DamageSource damageSourceForDeath)
    {
        base.OnEntityDeath(damageSourceForDeath);
        chain?.Despawn();
        if (entity is EntityAgent agent) agent.AllowDespawn = true;
    }

    private void EnsureChainCreated()
    {
        if (chain != null) return;
        chain = new TentacleSegmentChain(entity, SegmentCount, SegmentVisualHeight,
            SegmentInnerAsset, SegmentMidAsset, TipMidClawAsset, TipMidClawVisualHeight);
    }

    private void UpdateChainPositions()
    {
        if (chain == null) return;
        GetBodyAnchor(out double anchorX, out double anchorY, out double anchorZ);
        chain.UpdatePositions(anchorX, anchorY, anchorZ, config.TentacleArchHeightFactor);
    }

    private void UpdateHeadFacing()
    {
        GetBodyAnchor(out double anchorX, out double anchorY, out double anchorZ);
        var anchor = new Vec3d(anchorX, anchorY, anchorZ);
        TentacleHeadAlignment.AlignToTangent(entity, anchor, config.TentacleArchHeightFactor);
    }

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
        groundMode = entity.WatchedAttributes.GetBool("underwaterhorrors:groundMode", false);

        // Riser surface point: rise straight to the surface above the
        // current spawn position. We DON'T cap at the player's Y like
        // earlier versions did — that made tentacles stop at the player
        // height instead of climbing all the way to the surface, which
        // looked like a despawn.
        surfaceX = entity.Pos.X;
        surfaceY = config.CreatureMaxY;
        surfaceZ = entity.Pos.Z;

        // Randomize rise speed: base speed * random factor (0.8 to 1.3)
        float factor = 0.8f + (float)(entity.World.Rand.NextDouble() * 0.5);
        riseSpeed = config.AmbientTentacleRiseSpeed * factor;

        if (groundMode)
        {
            // Skip the rise — start crawling along the sea floor right away.
            PickWanderTarget();
            state = AmbientTentacleState.Wandering;
            stateTimer = 0;
        }
        else
        {
            state = AmbientTentacleState.Rising;
        }

        if (config.DebugLogging)
            UnderwaterHorrorsModSystem.DebugLog(entity.Api,
                $"Ambient tentacle initialized (mode={(groundMode ? "ground" : "riser")}): " +
                $"surface ({surfaceX:F1}, {surfaceY:F1}, {surfaceZ:F1}), " +
                $"first wander target ({wanderX:F1}, {wanderY:F1}, {wanderZ:F1}), " +
                $"riseSpeed={riseSpeed:F3}, phase={orbitPhase:F2}");
    }

    private void TransitionTo(AmbientTentacleState newState)
    {
        state = newState;
        stateTimer = 0;

        if (config.DebugLogging)
            UnderwaterHorrorsModSystem.DebugLog(entity.Api, $"Ambient tentacle state: {newState}");
    }

    // Ambient tentacles use TELEPORT-based motion, not Motion-based.
    // Reason: their entities have controlledphysics with stepHeight=0,
    // which means a one-block bump on the sea floor is enough to halt
    // them. With wander targets up to 80 blocks away across uneven
    // terrain that practically guarantees they'll freeze near spawn.
    // Teleport-stepping per tick bypasses collision entirely so they
    // glide past obstacles, which also looks correct for "ghost-like"
    // tentacles.

    private void OnRising(float deltaTime)
    {
        double dx = surfaceX - entity.Pos.X;
        double dy = surfaceY - entity.Pos.Y;
        double dz = surfaceZ - entity.Pos.Z;
        double dist = Math.Sqrt(dx * dx + dy * dy + dz * dz);

        if (dist < 2.0)
        {
            TransitionTo(AmbientTentacleState.Orbiting);
            return;
        }

        StepTowardTeleport(surfaceX, surfaceY, surfaceZ, riseSpeed);
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

        // 0.1 blocks/tick orbit step (≈3 b/s at 30Hz) keeps the visible
        // motion smooth without snapping at the orbit perimeter.
        StepTowardTeleport(targetX, targetY, targetZ, 0.1);
    }

    private void OnWandering(float deltaTime)
    {
        double dx = wanderX - entity.Pos.X;
        double dz = wanderZ - entity.Pos.Z;
        // Use HORIZONTAL distance for "reached target" — the Y is being
        // continuously re-clamped to the local sea floor below, so the
        // vertical component oscillates with terrain and shouldn't gate
        // arrival.
        double horizDistSq = dx * dx + dz * dz;
        if (horizDistSq < 9.0)
        {
            wanderIdleDuration = config.AmbientTentacleWanderIdleMin
                + (float)(entity.World.Rand.NextDouble()
                          * (config.AmbientTentacleWanderIdleMax - config.AmbientTentacleWanderIdleMin));
            TransitionTo(AmbientTentacleState.WanderingIdle);
            return;
        }

        // Step the tip toward the wander target, then ease Y toward the
        // local sea floor. The chain's anchor stays at the kraken body
        // and its tip follows here, so the visible chain arches between
        // body and tip across the floor.
        double horizDist = Math.Sqrt(horizDistSq);
        double step = config.AmbientTentacleWanderSpeed;
        double frac = Math.Min(1.0, step / horizDist);
        double newX = entity.Pos.X + dx * frac;
        double newZ = entity.Pos.Z + dz * frac;
        double targetY = FindSeaFloorYBelow(newX, entity.Pos.Y + 4, newZ, entity.Pos.Dimension);
        double newY = SmoothYStep(entity.Pos.Y, targetY);

        entity.TeleportToDouble(newX, newY, newZ);
        ClearMotion();
    }

    private void OnWanderingIdle(float deltaTime)
    {
        // Sit on the local sea floor with a tiny vertical bob so the tip
        // looks alive while it picks its next target.
        double bob = Math.Sin(stateTimer * 0.7) * 0.25;
        double targetY = FindSeaFloorYBelow(entity.Pos.X, entity.Pos.Y + 4, entity.Pos.Z, entity.Pos.Dimension) + bob;
        double newY = SmoothYStep(entity.Pos.Y, targetY);
        entity.TeleportToDouble(entity.Pos.X, newY, entity.Pos.Z);
        ClearMotion();

        if (stateTimer >= wanderIdleDuration)
        {
            PickWanderTarget();
            TransitionTo(AmbientTentacleState.Wandering);
        }
    }

    /// <summary>
    /// Caps a per-tick Y change so terrain rises/drops produce a smooth
    /// climb instead of an instant teleport. Returns the new Y.
    /// </summary>
    private double SmoothYStep(double currentY, double targetY)
    {
        double dy = targetY - currentY;
        double cap = config.AmbientTentacleVerticalStepMax;
        if (dy >  cap) return currentY + cap;
        if (dy < -cap) return currentY - cap;
        return targetY;
    }

    /// <summary>
    /// Brief descent phase that runs when the attack tentacle starts
    /// pursuing. The riser glides down toward a midwater point above the
    /// kraken body for AmbientScatterSinkDuration seconds, then hands off
    /// to SurfaceWandering. Visually reads as "the riser breaks orbit
    /// and dives" before the hypnotic wander begins.
    /// </summary>
    private void OnScattering(float deltaTime)
    {
        Entity body = GetBody();
        double bx = body != null && body.Alive ? body.Pos.X : entity.Pos.X;
        double bz = body != null && body.Alive ? body.Pos.Z : entity.Pos.Z;
        // Aim for the bottom of the midwater band (deepest legal Y) so
        // the descent is unambiguous before the wander kicks in.
        double midY = config.CreatureMaxY - config.AmbientSurfaceWanderDepthMax;
        StepTowardTeleport(bx, midY, bz, riseSpeed);

        if (stateTimer >= config.AmbientScatterSinkDuration)
        {
            PickSurfaceWanderTarget();
            TransitionTo(AmbientTentacleState.SurfaceWandering);
        }
    }

    private void OnSurfaceWandering(float deltaTime)
    {
        double dx = wanderX - entity.Pos.X;
        double dy = wanderY - entity.Pos.Y;
        double dz = wanderZ - entity.Pos.Z;
        double distSq = dx * dx + dy * dy + dz * dz;
        if (distSq < 9.0)
        {
            wanderIdleDuration = config.AmbientTentacleWanderIdleMin
                + (float)(entity.World.Rand.NextDouble()
                          * (config.AmbientTentacleWanderIdleMax - config.AmbientTentacleWanderIdleMin));
            TransitionTo(AmbientTentacleState.SurfaceWanderingIdle);
            return;
        }

        // Same per-tick step as the ground wanderer for matching pacing,
        // but free 3D movement (no terrain-clamp on Y). The chain still
        // arches from the body up to the moving tip.
        StepTowardTeleport(wanderX, wanderY, wanderZ, config.AmbientTentacleWanderSpeed);
    }

    private void OnSurfaceWanderingIdle(float deltaTime)
    {
        // Tiny midwater bob — keeps the tip looking alive between targets.
        double bobY = wanderY + Math.Sin(stateTimer * 0.7) * 0.5;
        StepTowardTeleport(wanderX, bobY, wanderZ, 0.05);

        if (stateTimer >= wanderIdleDuration)
        {
            PickSurfaceWanderTarget();
            TransitionTo(AmbientTentacleState.SurfaceWandering);
        }
    }

    /// <summary>
    /// Picks a random midwater point inside a horizontal disk around the
    /// kraken body, with Y somewhere in [surface - DepthMax, surface].
    /// </summary>
    private void PickSurfaceWanderTarget()
    {
        Entity body = GetBody();
        double centerX = body != null && body.Alive ? body.Pos.X : entity.Pos.X;
        double centerZ = body != null && body.Alive ? body.Pos.Z : entity.Pos.Z;

        var rand = entity.World.Rand;
        double angle = rand.NextDouble() * Math.PI * 2;
        double range = config.AmbientSurfaceWanderRangeMin
            + rand.NextDouble() * (config.AmbientSurfaceWanderRangeMax - config.AmbientSurfaceWanderRangeMin);
        wanderX = centerX + Math.Cos(angle) * range;
        wanderZ = centerZ + Math.Sin(angle) * range;
        // Y in [surface - DepthMax, surface]. Uniform random across the
        // band so some tendrils pop near the surface and others lurk
        // deeper, which is what the user described as "moving on the
        // surface or below it (20 blocks below)".
        double depth = rand.NextDouble() * config.AmbientSurfaceWanderDepthMax;
        wanderY = config.CreatureMaxY - depth;
    }

    /// <summary>
    /// Step the entity toward (tx, ty, tz) by at most `step` blocks
    /// using TeleportToDouble — phases through terrain, ignoring the
    /// controlledphysics collision check. Caller is responsible for
    /// any Y-clamping (e.g. snap to sea floor).
    /// </summary>
    private void StepTowardTeleport(double tx, double ty, double tz, double step)
    {
        double dx = tx - entity.Pos.X;
        double dy = ty - entity.Pos.Y;
        double dz = tz - entity.Pos.Z;
        double dist = Math.Sqrt(dx * dx + dy * dy + dz * dz);
        if (dist < 1e-6) { ClearMotion(); return; }
        double frac = Math.Min(1.0, step / dist);
        entity.TeleportToDouble(
            entity.Pos.X + dx * frac,
            entity.Pos.Y + dy * frac,
            entity.Pos.Z + dz * frac);
        ClearMotion();
    }

    private void ClearMotion()
    {
        // controlledphysics still ticks; explicitly zero out Motion each
        // tick so it doesn't try to apply lingering drift on top of our
        // teleport.
        entity.Pos.Motion.X = 0;
        entity.Pos.Motion.Y = 0;
        entity.Pos.Motion.Z = 0;
    }

    /// <summary>
    /// Picks a random horizontal direction + distance from the kraken
    /// body and snaps the target Y to whatever the sea floor is at that
    /// (X, Z). The tip will crawl toward this point along the sea floor.
    /// </summary>
    private void PickWanderTarget()
    {
        Entity body = GetBody();
        double centerX, centerZ;
        int centerY;
        int dim;
        if (body != null && body.Alive)
        {
            centerX = body.Pos.X;
            centerZ = body.Pos.Z;
            centerY = (int)body.Pos.Y;
            dim = body.Pos.Dimension;
        }
        else
        {
            centerX = entity.Pos.X;
            centerZ = entity.Pos.Z;
            centerY = (int)entity.Pos.Y;
            dim = entity.Pos.Dimension;
        }

        var rand = entity.World.Rand;
        double angle = rand.NextDouble() * Math.PI * 2;
        double range = config.AmbientTentacleWanderRangeMin
            + rand.NextDouble() * (config.AmbientTentacleWanderRangeMax - config.AmbientTentacleWanderRangeMin);
        wanderX = centerX + Math.Cos(angle) * range;
        wanderZ = centerZ + Math.Sin(angle) * range;
        // Sea floor is at most ~80 blocks below the body; FindSeaFloorYBelow
        // returns the empty space just above the floor.
        wanderY = FindSeaFloorYBelow(wanderX, centerY + 4, wanderZ, dim);
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

    private Entity GetBody()
    {
        if (cachedBody != null && cachedBody.Alive && cachedBody.EntityId == cachedBodyId)
            return cachedBody;

        cachedBody = cachedBodyId != 0 ? entity.World.GetEntityById(cachedBodyId) : null;
        return cachedBody;
    }

    public override string PropertyName() => "underwaterhorrors:ambienttentacle";
}
