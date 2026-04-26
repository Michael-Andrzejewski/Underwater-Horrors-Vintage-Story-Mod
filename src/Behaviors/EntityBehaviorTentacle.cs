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
    // Coverage = SegmentCount * SegmentVisualHeight. The kraken sits on the
    // sea floor and the tip rises to the surface (CreatureMaxY=110), so the
    // spline can be 50+ blocks of vertical chord plus arch. With 96 segments
    // at 0.84 blocks each we cover ~80 blocks of arc length — enough for
    // even deep-ocean spawns.
    private const int SegmentCount = 96;
    private const int ClawCount = 4;

    // Visual height of one mid segment in world blocks. Cube4 trunk is 9 voxels
    // tall; entity client.size is 1.5; 16 voxels per block: 9 * 1.5 / 16 = 0.84.
    private const double SegmentVisualHeight = 0.84;

    private TentacleState state = TentacleState.Idle;
    private float stateTimer;
    private bool speedDebuffApplied;

    // Chain of segment entities that fills the spline from body to tip.
    // See TentacleSegmentChain for the trail-follow + pitch+roll math.
    private TentacleSegmentChain chain;

    // Claw entities (spawned around player during Dragging) — cached references
    private long[] clawIds;
    private Entity[] clawEntities;
    private bool clawsSpawned;

    // Bioluminescent light entities — one per segment, track position and pulse HSV
    private long[] biolumIds;
    private Entity[] biolumEntities;
    private bool biolumsSpawned;

    // Biolum pulse timer — only update HSV a few times per second to limit network traffic
    private float biolumTickAccum;
    private const float BiolumTickInterval = 0.2f; // 5 Hz

    // Biolum HSV constants: base color matches creativeglow-45 [26, 7, 4]
    private const byte BiolumHue = 26;
    private const byte BiolumSat = 7;
    private const byte BiolumVStatic = 4;
    private const byte BiolumVMin = 1;
    private const byte BiolumVMax = 4;

    // Phase offset per segment — wave ripples outward from body to tip
    private const float BiolumPhaseStep = 0.8f;

    // Cached body entity reference
    private long cachedBodyId;
    private Entity cachedBody;

    // Cached AssetLocations
    private static readonly AssetLocation SegmentInnerAsset = new AssetLocation("underwaterhorrors", "krakententsegment");
    private static readonly AssetLocation SegmentMidAsset   = new AssetLocation("underwaterhorrors", "krakententsegment_mid");
    private static readonly AssetLocation ClawAsset = new AssetLocation("underwaterhorrors", "krakententacleclaw");
    private static readonly AssetLocation BiolightAsset = new AssetLocation("underwaterhorrors", "biolight");

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
        if (entity.WatchedAttributes.GetBool("underwaterhorrors:static", false)) return;

        ResolveTarget();
        ClampHeight();

        EnsureChainCreated();
        chain?.EnsureSpawned();

        // Spawn biolum lights if enabled and segments exist
        if (!biolumsSpawned && chain != null && chain.Spawned && config.BiolumActive)
        {
            SpawnBiolumLights();
        }

        // Update biolum HSV pulsing at throttled rate
        if (biolumsSpawned)
        {
            if (!config.BiolumActive)
            {
                DespawnBiolumLights();
            }
            else
            {
                biolumTickAccum += deltaTime;
                if (biolumTickAccum >= BiolumTickInterval)
                {
                    biolumTickAccum = 0f;
                    UpdateBiolumPulse();
                }
            }
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

        UpdateChainPositions();
        UpdateHeadFacing();
    }

    // Snap the head to face the spline tangent (or the target player when
    // attacking). Same world-axis pitch+roll decomposition as the segment
    // chain — see TentacleHeadAlignment / TentacleSegmentChain for the math.
    //
    // No lerp: writing Pos.Pitch/Roll/Yaw directly each tick means there's
    // no carry-over from the previous frame, so the head doesn't drift on
    // its own momentum and doesn't lag behind the tangent direction.
    private void UpdateHeadFacing()
    {
        if ((state == TentacleState.Reaching || state == TentacleState.Dragging) && targetPlayer?.Entity != null)
        {
            TentacleHeadAlignment.AlignToward(entity,
                targetPlayer.Entity.Pos.X,
                targetPlayer.Entity.Pos.Y,
                targetPlayer.Entity.Pos.Z);
        }
        else
        {
            GetBodyAnchor(out double anchorX, out double anchorY, out double anchorZ);
            var anchor = new Vec3d(anchorX, anchorY, anchorZ);
            TentacleHeadAlignment.AlignToTangent(entity, anchor, config.TentacleArchHeightFactor);
        }
    }

    public override void OnEntityDespawn(EntityDespawnData despawn)
    {
        RemoveSpeedDebuff();
        DespawnBiolumLights();
        chain?.Despawn();
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

    /// <summary>
    /// Anchor point for the spline base. Normally the kraken body block;
    /// falls back to a point below the tip if the body is gone (so the
    /// spline still has somewhere to root while the tentacle sinks).
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

        double px = targetPlayer.Entity.Pos.X;
        double py = targetPlayer.Entity.Pos.Y;
        double pz = targetPlayer.Entity.Pos.Z;

        for (int i = 0; i < ClawCount; i++)
        {
            Entity claw = entity.World.ClassRegistry.CreateEntity(clawProps);
            double cx = px + ClawOffsets[i][0];
            double cy = py + ClawOffsets[i][1];
            double cz = pz + ClawOffsets[i][2];

            claw.Pos.SetPos(cx, cy, cz);
            claw.Pos.Dimension = entity.Pos.Dimension;
            claw.Pos.SetFrom(claw.Pos);
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

        double px = targetPlayer.Entity.Pos.X;
        double py = targetPlayer.Entity.Pos.Y;
        double pz = targetPlayer.Entity.Pos.Z;

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

    // --- Bioluminescent light entities ---

    private void SpawnBiolumLights()
    {
        if (chain == null || !chain.Spawned) return;

        EntityProperties lightProps = entity.World.GetEntityType(BiolightAsset);
        if (lightProps == null)
        {
            if (config.DebugLogging)
                UnderwaterHorrorsModSystem.DebugLog(entity.Api, "ERROR: Could not find entity type underwaterhorrors:biolight");
            return;
        }

        int count = chain.Count;
        biolumIds = new long[count];
        biolumEntities = new Entity[count];

        for (int i = 0; i < count; i++)
        {
            Entity seg = chain.Segments[i];
            if (seg == null || !seg.Alive) continue;

            Entity light = entity.World.ClassRegistry.CreateEntity(lightProps);
            light.Pos.SetPos(seg.Pos.X, seg.Pos.Y, seg.Pos.Z);
            light.Pos.Dimension = entity.Pos.Dimension;
            light.Pos.SetFrom(light.Pos);

            // Initial HSV — static brightness unless pulsing is enabled
            light.WatchedAttributes.SetBytes("hsv", new byte[] { BiolumHue, BiolumSat, BiolumVStatic });

            entity.World.SpawnEntity(light);
            biolumIds[i] = light.EntityId;
            biolumEntities[i] = light;
        }

        biolumsSpawned = true;

        if (config.DebugLogging)
            UnderwaterHorrorsModSystem.DebugLog(entity.Api, $"Tentacle spawned {count} biolum lights");
    }

    private void DespawnBiolumLights()
    {
        if (biolumEntities == null) return;

        for (int i = 0; i < biolumEntities.Length; i++)
        {
            Entity light = biolumEntities[i];
            if (light != null && light.Alive)
            {
                light.Die(EnumDespawnReason.Expire);
            }
        }

        biolumIds = null;
        biolumEntities = null;
        biolumsSpawned = false;
    }

    /// <summary>
    /// Updates biolum light positions to match their parent segment and
    /// modulates the V (brightness) component via a sine wave with
    /// per-segment phase offset, creating an outward-rippling glow.
    /// </summary>
    private void UpdateBiolumPulse()
    {
        if (biolumEntities == null || chain == null || !chain.Spawned) return;

        bool pulsing = config.BiolumPulsing;
        float t = (float)entity.World.ElapsedMilliseconds / 1000f;
        float speed = config.BiolumPulseSpeed;

        int count = chain.Count;
        for (int i = 0; i < count; i++)
        {
            Entity light = biolumEntities[i];
            // Re-validate cached reference if stale
            if (light == null || !light.Alive)
            {
                if (biolumIds != null)
                {
                    light = entity.World.GetEntityById(biolumIds[i]);
                    biolumEntities[i] = light;
                }
                if (light == null || !light.Alive) continue;
            }

            Entity seg = chain.Segments[i];
            if (seg == null || !seg.Alive)
            {
                seg = entity.World.GetEntityById(chain.SegmentIds[i]);
                chain.Segments[i] = seg;
                if (seg == null || !seg.Alive) continue;
            }

            // Move light to segment position
            light.TeleportToDouble(seg.Pos.X, seg.Pos.Y, seg.Pos.Z);

            if (pulsing)
            {
                float phase = t * speed - i * BiolumPhaseStep;
                float wave = 0.5f + 0.5f * (float)Math.Sin(phase);
                byte v = (byte)(BiolumVMin + (BiolumVMax - BiolumVMin) * wave);
                light.WatchedAttributes.SetBytes("hsv", new byte[] { BiolumHue, BiolumSat, v });
            }
        }
    }

    /// <summary>
    /// Called externally (e.g. from toggle command) to force-spawn or force-despawn
    /// biolum lights on an already-living tentacle.
    /// </summary>
    public void SetBiolumActive(bool active)
    {
        if (active && !biolumsSpawned && chain != null && chain.Spawned)
        {
            SpawnBiolumLights();
        }
        else if (!active && biolumsSpawned)
        {
            DespawnBiolumLights();
        }
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

            surfaceX = targetPlayer.Entity.Pos.X + Math.Cos(angle) * dist;
            // Target sea-surface Y, not player Y — otherwise the surface
            // point chases the player onto cliffs or deep underwater.
            // Dragging phase still tracks the player directly via a
            // separate code path, so grabs still work when diving.
            surfaceY = Math.Min(targetPlayer.Entity.Pos.Y, config.CreatureMaxY);
            surfaceZ = targetPlayer.Entity.Pos.Z + Math.Sin(angle) * dist;
        }
        else
        {
            surfaceX = entity.Pos.X;
            surfaceY = entity.Pos.Y + 20;
            surfaceZ = entity.Pos.Z;
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

        double dx = surfaceX - entity.Pos.X;
        double dy = surfaceY - entity.Pos.Y;
        double dz = surfaceZ - entity.Pos.Z;
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

        double dx = bobX - entity.Pos.X;
        double dy = bobY - entity.Pos.Y;
        double dz = bobZ - entity.Pos.Z;

        entity.Pos.Motion.X = dx * 0.05;
        entity.Pos.Motion.Y = dy * 0.05;
        entity.Pos.Motion.Z = dz * 0.05;

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

        double clampedY = Math.Min(targetPlayer.Entity.Pos.Y, config.CreatureMaxY);
        MoveToward(targetPlayer.Entity.Pos.X, clampedY, targetPlayer.Entity.Pos.Z, config.TentacleReachSpeed);

        double dist = entity.Pos.DistanceTo(targetPlayer.Entity.Pos.XYZ);
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

        double playerX = targetPlayer.Entity.Pos.X;
        double playerY = targetPlayer.Entity.Pos.Y;
        double playerZ = targetPlayer.Entity.Pos.Z;

        double dx = body.Pos.X - playerX;
        double dy = body.Pos.Y - playerY;
        double dz = body.Pos.Z - playerZ;
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
            targetPlayer.Entity.Pos.X,
            targetPlayer.Entity.Pos.Y + config.TentacleGrabYOffset,
            targetPlayer.Entity.Pos.Z
        );
    }

    private bool IsPositionPassable(double x, double y, double z)
    {
        reusablePassabilityPos.Set((int)x, (int)y, (int)z);
        Block block = entity.World.BlockAccessor.GetBlock(reusablePassabilityPos);
        // VS 1.22: EnumBlockMaterial.Liquid was split into Water/Lava. Water is passable
        // for the tentacle; lava is not (and shouldn't be water-dwelling anyway).
        return block == null || block.BlockMaterial == EnumBlockMaterial.Water || !block.SideSolid[BlockFacing.UP.Index];
    }

    private void OnSinking(float deltaTime)
    {
        // Sink downward toward sea floor
        entity.Pos.Motion.X = 0;
        entity.Pos.Motion.Y = -config.RetreatSpeed;
        entity.Pos.Motion.Z = 0;

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
            MoveToward(body.Pos.X, body.Pos.Y, body.Pos.Z, config.TentacleReachSpeed);
        }
        else
        {
            entity.Pos.Motion.X = 0;
            entity.Pos.Motion.Y = -config.RetreatSpeed;
            entity.Pos.Motion.Z = 0;
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
