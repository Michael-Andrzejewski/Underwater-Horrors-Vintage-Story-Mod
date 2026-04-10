using System;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;

namespace UnderwaterHorrors;

public enum TentacleState
{
    Idle,
    Rising,
    Lingering,
    Reaching,
    Dragging,
    Sinking,
    Retreating
}

public class EntityBehaviorTentacle : EntityBehaviorOceanCreature
{
    private const int SegmentCount = 8;
    private const int ClawCount = 4;

    private TentacleState state = TentacleState.Idle;
    private float stateTimer;
    private bool speedDebuffApplied;

    // Chain segment entities — cached references to avoid GetEntityById every frame
    private long[] segmentIds;
    private Entity[] segmentEntities;
    private bool segmentsSpawned;

    // Claw entities (spawned around player during Dragging) — cached references
    private long[] clawIds;
    private Entity[] clawEntities;
    private bool clawsSpawned;

    // Cached body entity reference
    private long cachedBodyId;
    private Entity cachedBody;

    // Reusable Vec3d for spline calculations to avoid allocation per frame
    private Vec3d reusableAnchor = new Vec3d();

    // Cached AssetLocations — three segment variants for bioluminescent wave phasing
    private static readonly AssetLocation SegmentInnerAsset = new AssetLocation("underwaterhorrors", "krakententsegment");
    private static readonly AssetLocation SegmentMidAsset   = new AssetLocation("underwaterhorrors", "krakententsegment_mid");
    private static readonly AssetLocation SegmentOuterAsset = new AssetLocation("underwaterhorrors", "krakententsegment_outer");
    private static readonly AssetLocation ClawAsset = new AssetLocation("underwaterhorrors", "krakententacleclaw");

    // Reusable BlockPos for passability checks to avoid allocation per frame
    private readonly BlockPos reusablePassabilityPos = new BlockPos(0, 0, 0, 0);

    // Surface point for the Rising/Lingering phases
    private double surfaceX, surfaceY, surfaceZ;
    private bool surfacePointPicked;

    // Offsets for 4 claws: +X, -X, +Z, -Z (1 block out from player)
    private static readonly double[][] ClawOffsets = new double[][]
    {
        new double[] {  1.0, 0.5,  0.0 },  // East
        new double[] { -1.0, 0.5,  0.0 },  // West
        new double[] {  0.0, 0.5,  1.0 },  // South
        new double[] {  0.0, 0.5, -1.0 },  // North
    };

    public EntityBehaviorTentacle(Entity entity) : base(entity) { }

    public override void OnGameTick(float deltaTime)
    {
        if (!entity.Alive) return;
        if (entity.Api.Side != EnumAppSide.Server) return;

        ResolveTarget();
        ClampHeight();

        if (!segmentsSpawned)
        {
            segmentsSpawned = true;
            SpawnSegments();
        }

        // Check if any claw died during Dragging -> release and sink
        if (state == TentacleState.Dragging && CheckAnyClawDead())
        {
            if (config.DebugLogging)
                UnderwaterHorrorsModSystem.DebugLog(entity.Api, "Tentacle: a claw was killed, releasing player and sinking");
            TransitionTo(TentacleState.Sinking);
        }

        // Throttled shallow water retreat check
        if (state == TentacleState.Reaching || state == TentacleState.Dragging)
        {
            UpdateShallowWaterCheck(deltaTime);

            if (IsInShallowWater)
            {
                if (config.DebugLogging)
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
            case TentacleState.Rising:
                OnRising(deltaTime);
                break;
            case TentacleState.Lingering:
                OnLingering(deltaTime);
                break;
            case TentacleState.Reaching:
                OnReaching(deltaTime);
                break;
            case TentacleState.Dragging:
                OnDragging(deltaTime);
                break;
            case TentacleState.Sinking:
                OnSinking(deltaTime);
                break;
            case TentacleState.Retreating:
                OnRetreating(deltaTime);
                break;
        }

        PositionSegments();
    }

    public override void OnEntityDespawn(EntityDespawnData despawn)
    {
        RemoveSpeedDebuff();
        DespawnSegments();
        DespawnClaws();
        base.OnEntityDespawn(despawn);
    }

    // --- Cached body lookup ---

    private Entity GetBody()
    {
        long bodyId = entity.WatchedAttributes.GetLong("underwaterhorrors:krakenBodyId");
        if (bodyId == cachedBodyId && cachedBody != null && cachedBody.Alive)
            return cachedBody;

        cachedBodyId = bodyId;
        cachedBody = bodyId != 0 ? entity.World.GetEntityById(bodyId) : null;
        return cachedBody;
    }

    // --- Chain segments ---

    private void SpawnSegments()
    {
        segmentIds = new long[SegmentCount];
        segmentEntities = new Entity[SegmentCount];

        // Resolve all three segment variants
        EntityProperties innerProps = entity.World.GetEntityType(SegmentInnerAsset);
        EntityProperties midProps   = entity.World.GetEntityType(SegmentMidAsset);
        EntityProperties outerProps = entity.World.GetEntityType(SegmentOuterAsset);

        if (innerProps == null)
        {
            if (config.DebugLogging)
                UnderwaterHorrorsModSystem.DebugLog(entity.Api, "ERROR: Could not find entity type underwaterhorrors:krakententsegment");
            return;
        }

        for (int i = 0; i < SegmentCount; i++)
        {
            // Pick segment variant by position: inner (0-2), mid (3-4), outer (5-7)
            EntityProperties segProps;
            if (i <= 2)
                segProps = innerProps;
            else if (i <= 4)
                segProps = midProps ?? innerProps;
            else
                segProps = outerProps ?? innerProps;

            Entity seg = entity.World.ClassRegistry.CreateEntity(segProps);
            seg.ServerPos.SetPos(entity.ServerPos.X, entity.ServerPos.Y, entity.ServerPos.Z);
            seg.ServerPos.Dimension = entity.ServerPos.Dimension;
            seg.Pos.SetFrom(seg.ServerPos);
            entity.World.SpawnEntity(seg);
            segmentIds[i] = seg.EntityId;
            segmentEntities[i] = seg;
        }

        if (config.DebugLogging)
            UnderwaterHorrorsModSystem.DebugLog(entity.Api, $"Tentacle spawned {SegmentCount} chain segments (inner/mid/outer)");
    }

    private void DespawnSegments()
    {
        if (segmentEntities == null) return;

        for (int i = 0; i < segmentEntities.Length; i++)
        {
            Entity seg = segmentEntities[i];
            if (seg != null && seg.Alive)
            {
                seg.Die(EnumDespawnReason.Expire);
            }
        }
    }

    private void PositionSegments()
    {
        if (segmentEntities == null) return;

        Entity body = GetBody();

        if (body != null && body.Alive)
        {
            reusableAnchor.Set(body.SidedPos.X, body.SidedPos.Y + 1, body.SidedPos.Z);
        }
        else
        {
            reusableAnchor.Set(entity.SidedPos.X, entity.SidedPos.Y - 5, entity.SidedPos.Z);
        }

        Vec3d tip = entity.SidedPos.XYZ;

        SplineHelper.ComputeTentacleControlPoints(reusableAnchor, tip, config.TentacleArchHeightFactor, out Vec3d b1, out Vec3d b2);

        for (int i = 0; i < segmentEntities.Length; i++)
        {
            Entity seg = segmentEntities[i];
            // Re-validate cached reference if stale
            if (seg == null || !seg.Alive)
            {
                seg = entity.World.GetEntityById(segmentIds[i]);
                segmentEntities[i] = seg;
                if (seg == null || !seg.Alive) continue;
            }

            double t = (double)(i + 1) / (SegmentCount + 1);
            Vec3d pos = SplineHelper.EvalCubicBezier(reusableAnchor, b1, b2, tip, t);

            seg.TeleportToDouble(pos.X, pos.Y, pos.Z);
        }
    }

    // --- Claw entities (hittable pieces around player during drag) ---

    private void SpawnClaws()
    {
        if (clawsSpawned) return;
        clawsSpawned = true;

        clawIds = new long[ClawCount];
        clawEntities = new Entity[ClawCount];

        EntityProperties clawProps = entity.World.GetEntityType(ClawAsset);
        if (clawProps == null)
        {
            if (config.DebugLogging)
                UnderwaterHorrorsModSystem.DebugLog(entity.Api, "ERROR: Could not find entity type underwaterhorrors:krakententacleclaw");
            return;
        }

        double px = targetPlayer.Entity.SidedPos.X;
        double py = targetPlayer.Entity.SidedPos.Y;
        double pz = targetPlayer.Entity.SidedPos.Z;

        for (int i = 0; i < ClawCount; i++)
        {
            Entity claw = entity.World.ClassRegistry.CreateEntity(clawProps);
            double cx = px + ClawOffsets[i][0];
            double cy = py + ClawOffsets[i][1];
            double cz = pz + ClawOffsets[i][2];

            claw.ServerPos.SetPos(cx, cy, cz);
            claw.ServerPos.Dimension = entity.ServerPos.Dimension;
            claw.Pos.SetFrom(claw.ServerPos);
            entity.World.SpawnEntity(claw);

            clawIds[i] = claw.EntityId;
            clawEntities[i] = claw;
        }

        if (config.DebugLogging)
            UnderwaterHorrorsModSystem.DebugLog(entity.Api, "Tentacle spawned 4 claws around player");
    }

    private void DespawnClaws()
    {
        if (clawEntities == null) return;

        for (int i = 0; i < clawEntities.Length; i++)
        {
            Entity claw = clawEntities[i];
            if (claw != null && claw.Alive)
            {
                claw.Die(EnumDespawnReason.Expire);
            }
        }

        clawIds = null;
        clawEntities = null;
        clawsSpawned = false;
    }

    private void PositionClaws()
    {
        if (clawEntities == null || targetPlayer?.Entity == null) return;

        double px = targetPlayer.Entity.SidedPos.X;
        double py = targetPlayer.Entity.SidedPos.Y;
        double pz = targetPlayer.Entity.SidedPos.Z;

        for (int i = 0; i < clawEntities.Length; i++)
        {
            Entity claw = clawEntities[i];
            // Re-validate cached reference if stale
            if (claw == null || !claw.Alive)
            {
                claw = entity.World.GetEntityById(clawIds[i]);
                clawEntities[i] = claw;
                if (claw == null || !claw.Alive) continue;
            }

            double cx = px + ClawOffsets[i][0];
            double cy = py + ClawOffsets[i][1];
            double cz = pz + ClawOffsets[i][2];

            claw.TeleportToDouble(cx, cy, cz);
        }
    }

    private bool CheckAnyClawDead()
    {
        if (clawEntities == null) return false;

        for (int i = 0; i < clawEntities.Length; i++)
        {
            Entity claw = clawEntities[i];
            // Re-validate cached reference
            if (claw == null || !claw.Alive)
            {
                claw = entity.World.GetEntityById(clawIds[i]);
                clawEntities[i] = claw;
                if (claw == null || !claw.Alive) return true;
            }
        }
        return false;
    }

    // --- Speed debuff ---

    private void ApplySpeedDebuff()
    {
        if (speedDebuffApplied || targetPlayer?.Entity == null) return;
        speedDebuffApplied = true;
        targetPlayer.Entity.Stats.Set("walkspeed", "tentacledrag", -0.9f);
        if (config.DebugLogging)
            UnderwaterHorrorsModSystem.DebugLog(entity.Api, "Tentacle: slowing player movement");
    }

    private void RemoveSpeedDebuff()
    {
        if (!speedDebuffApplied || targetPlayer?.Entity == null) return;
        speedDebuffApplied = false;
        targetPlayer.Entity.Stats.Remove("walkspeed", "tentacledrag");
        if (config.DebugLogging)
            UnderwaterHorrorsModSystem.DebugLog(entity.Api, "Tentacle: restored player movement");
    }

    // --- State transitions ---

    private void TransitionTo(TentacleState newState)
    {
        TentacleState oldState = state;

        // Clean up when leaving drag state
        if (oldState == TentacleState.Dragging && newState != TentacleState.Dragging)
        {
            RemoveSpeedDebuff();
            DespawnClaws();
        }

        state = newState;
        stateTimer = 0;

        if (config.DebugLogging)
        {
            string playerName = targetPlayer?.PlayerName ?? "unknown";
            UnderwaterHorrorsModSystem.DebugLog(entity.Api, $"Tentacle state: {oldState} to {newState} (target: {playerName})");
        }

        if (newState == TentacleState.Sinking)
        {
            // Signal to the kraken body that this tentacle is done
            entity.WatchedAttributes.SetBool("underwaterhorrors:sinking", true);
        }
    }

    // --- State handlers ---

    private void OnIdle(float deltaTime)
    {
        if (stateTimer >= config.TentacleIdleDuration)
        {
            TransitionTo(TentacleState.Rising);
        }
    }

    private void PickSurfacePoint()
    {
        if (surfacePointPicked) return;
        surfacePointPicked = true;

        ResolveTarget();

        if (targetPlayer?.Entity != null)
        {
            var rand = entity.World.Rand;
            double range = config.TentacleSurfaceRange;
            double angle = rand.NextDouble() * Math.PI * 2;
            double dist = rand.NextDouble() * range;

            surfaceX = targetPlayer.Entity.SidedPos.X + Math.Cos(angle) * dist;
            surfaceY = targetPlayer.Entity.SidedPos.Y;
            surfaceZ = targetPlayer.Entity.SidedPos.Z + Math.Sin(angle) * dist;
        }
        else
        {
            surfaceX = entity.SidedPos.X;
            surfaceY = entity.SidedPos.Y + 20;
            surfaceZ = entity.SidedPos.Z;
        }

        if (config.DebugLogging)
            UnderwaterHorrorsModSystem.DebugLog(entity.Api,
                $"Attack tentacle surface point: ({surfaceX:F1}, {surfaceY:F1}, {surfaceZ:F1})");
    }

    private void OnRising(float deltaTime)
    {
        PickSurfacePoint();

        if (targetPlayer?.Entity == null || !targetPlayer.Entity.Alive)
        {
            TransitionTo(TentacleState.Sinking);
            return;
        }

        MoveToward(surfaceX, surfaceY, surfaceZ, config.TentacleRiseSpeed);

        double dx = surfaceX - entity.SidedPos.X;
        double dy = surfaceY - entity.SidedPos.Y;
        double dz = surfaceZ - entity.SidedPos.Z;
        double dist = Math.Sqrt(dx * dx + dy * dy + dz * dz);

        if (dist < 2.0)
        {
            TransitionTo(TentacleState.Lingering);
        }
    }

    private void OnLingering(float deltaTime)
    {
        if (targetPlayer?.Entity == null || !targetPlayer.Entity.Alive)
        {
            TransitionTo(TentacleState.Sinking);
            return;
        }

        // Gentle drift around the surface point
        double bobX = surfaceX + Math.Sin(stateTimer * 0.5) * 2.0;
        double bobZ = surfaceZ + Math.Cos(stateTimer * 0.5) * 2.0;
        double bobY = surfaceY + Math.Sin(stateTimer * 0.7) * 1.0;

        double dx = bobX - entity.SidedPos.X;
        double dy = bobY - entity.SidedPos.Y;
        double dz = bobZ - entity.SidedPos.Z;

        entity.SidedPos.Motion.X = dx * 0.05;
        entity.SidedPos.Motion.Y = dy * 0.05;
        entity.SidedPos.Motion.Z = dz * 0.05;

        if (stateTimer >= config.TentacleLingerDuration)
        {
            // Signal ambient tentacles to sink
            Entity body = GetBody();
            if (body != null && body.Alive)
            {
                body.WatchedAttributes.SetBool("underwaterhorrors:sinkAmbient", true);
                body.WatchedAttributes.MarkPathDirty("underwaterhorrors:sinkAmbient");

                if (config.DebugLogging)
                    UnderwaterHorrorsModSystem.DebugLog(entity.Api, "Attack tentacle: signaled ambient tentacles to sink");
            }

            TransitionTo(TentacleState.Reaching);
        }
    }

    private void OnReaching(float deltaTime)
    {
        if (targetPlayer?.Entity == null || !targetPlayer.Entity.Alive)
        {
            TransitionTo(TentacleState.Sinking);
            return;
        }

        if (targetPlayer.Entity.MountedOn != null)
        {
            if (config.DebugLogging)
                UnderwaterHorrorsModSystem.DebugLog(entity.Api, $"Tentacle: {targetPlayer.PlayerName} is mounted, sinking");
            TransitionTo(TentacleState.Sinking);
            return;
        }

        double clampedY = Math.Min(targetPlayer.Entity.SidedPos.Y, config.CreatureMaxY);
        MoveToward(targetPlayer.Entity.SidedPos.X, clampedY, targetPlayer.Entity.SidedPos.Z, config.TentacleReachSpeed);

        double dist = entity.SidedPos.DistanceTo(targetPlayer.Entity.SidedPos.XYZ);
        if (dist < config.TentacleGrabRange)
        {
            TransitionTo(TentacleState.Dragging);
        }
    }

    private void OnDragging(float deltaTime)
    {
        if (targetPlayer?.Entity == null || !targetPlayer.Entity.Alive)
        {
            TransitionTo(TentacleState.Sinking);
            return;
        }

        ApplySpeedDebuff();

        // Spawn claws around the player on first drag tick
        if (!clawsSpawned)
        {
            SpawnClaws();
        }

        // Position claws around the player
        PositionClaws();

        // Find kraken body position (cached)
        Entity body = GetBody();

        if (body == null || !body.Alive)
        {
            if (config.DebugLogging)
                UnderwaterHorrorsModSystem.DebugLog(entity.Api, "Tentacle: kraken body gone, sinking");
            TransitionTo(TentacleState.Sinking);
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

            bool canMove = IsPositionPassable(newX, newY, newZ);
            if (!canMove)
            {
                // Try without vertical movement
                newY = playerY;
                canMove = IsPositionPassable(newX, newY, newZ);
            }

            if (canMove)
            {
                targetPlayer.Entity.TeleportToDouble(newX, newY, newZ);
            }
        }

        // Keep tentacle tip below player
        entity.TeleportToDouble(
            targetPlayer.Entity.SidedPos.X,
            targetPlayer.Entity.SidedPos.Y + config.TentacleGrabYOffset,
            targetPlayer.Entity.SidedPos.Z
        );
    }

    private bool IsPositionPassable(double x, double y, double z)
    {
        reusablePassabilityPos.Set((int)x, (int)y, (int)z);
        Block block = entity.World.BlockAccessor.GetBlock(reusablePassabilityPos);
        return block == null || block.BlockMaterial == EnumBlockMaterial.Liquid || !block.SideSolid[BlockFacing.UP.Index];
    }

    private void OnSinking(float deltaTime)
    {
        // Sink downward toward sea floor
        entity.SidedPos.Motion.X = 0;
        entity.SidedPos.Motion.Y = -config.RetreatSpeed;
        entity.SidedPos.Motion.Z = 0;

        if (stateTimer >= config.TentacleSinkDuration)
        {
            if (config.DebugLogging)
                UnderwaterHorrorsModSystem.DebugLog(entity.Api, "Tentacle sink complete, despawning");
            entity.Die(EnumDespawnReason.Expire);
        }
    }

    private void OnRetreating(float deltaTime)
    {
        Entity body = GetBody();

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
            if (config.DebugLogging)
                UnderwaterHorrorsModSystem.DebugLog(entity.Api, "Tentacle retreat complete, despawning");
            entity.Die(EnumDespawnReason.Expire);
        }
    }

    public override string PropertyName() => "underwaterhorrors:tentacle";
}
