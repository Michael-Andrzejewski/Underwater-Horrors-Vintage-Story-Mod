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
    Stalling,    // Player out of water OR on a boat — slow drift/orbit, 30s timeout
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

    // The tip uses krakententsegment_mid_claw — a copy of segment_mid with the
    // claw cubes baked in as additional top-level shape elements (so the claw
    // is rigidly locked to the trunk in the SAME shape; they rotate together
    // automatically and can't drift apart). Its trunk height is the same as
    // a regular mid (9 voxels = 0.84 blocks), so spacing matches the rest
    // of the chain. The claw decorations extend above the trunk top — that's
    // intentional, the AI-controlled krakententacle entity at the actual
    // spline tip is now invisible (krakententinvisible shape) so only the
    // chain renders the claw.
    private const double TipMidClawVisualHeight = 0.84;

    private TentacleState state = TentacleState.Idle;
    private float stateTimer;
    private bool speedDebuffApplied;

    // Kraken-death handling: once the body is dead, AI logic stops, no
    // new entities spawn, claws/lights are cleaned up, and the tentacle
    // falls passively for TentacleKrakenDeathFallDuration seconds before
    // calling Die. Latches via a flag so cleanup runs exactly once.
    private bool krakenDeathHandled;
    private float krakenDeathTimer;

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

    // Cached AssetLocations. The tip uses krakententsegment_mid_claw — the
    // mid shape with claw geometry baked in — so the claw can't drift away
    // from the trunk (same rendered mesh).
    private static readonly AssetLocation SegmentInnerAsset = new AssetLocation("underwaterhorrors", "krakententsegment");
    private static readonly AssetLocation SegmentMidAsset   = new AssetLocation("underwaterhorrors", "krakententsegment_mid");
    private static readonly AssetLocation TipMidClawAsset   = new AssetLocation("underwaterhorrors", "krakententsegment_mid_claw");
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

        // Kraken-death short-circuit. If the body died this tick (or any
        // earlier tick) we run cleanup once, then this branch every tick:
        // skip ALL state logic, biolum spawning, claw spawning, respawn
        // signals, etc. The chain still updates so segments visibly fall
        // with the tentacle. After TentacleKrakenDeathFallDuration the
        // tentacle dies cleanly; the chain.Despawn in OnEntityDespawn
        // takes the segments with it.
        Entity body = GetBody();
        if (body == null || !body.Alive)
        {
            if (!krakenDeathHandled)
            {
                krakenDeathHandled = true;
                krakenDeathTimer = 0f;
                if (clawsSpawned) DespawnClaws();
                RemoveSpeedDebuff();
                if (config.DebugLogging)
                    UnderwaterHorrorsModSystem.DebugLog(entity.Api,
                        "Tentacle: kraken body dead, falling passively (no new spawns).");
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

        // Stall trigger. The moment the player exits the water OR mounts
        // a boat during Reaching/Dragging, the tentacle stops actively
        // chasing and enters Stalling — slow drift toward body if on
        // land, slow orbit around the boat if mounted. Stalling has its
        // own 30s despawn timer (TentacleStallDespawnSeconds); if the
        // player returns to a chase-able state before then, Stalling
        // hands control back to Reaching.
        //
        // (IsInShallowWater explicitly returns false when mounted, so
        // we OR with a separate mount check here. UpdateShallowWaterCheck
        // is throttled to 0.5s — cheap to call every tick.)
        if (state == TentacleState.Reaching || state == TentacleState.Dragging)
        {
            UpdateShallowWaterCheck(deltaTime);
            bool playerMounted = targetPlayer?.Entity?.MountedOn != null;
            if (IsInShallowWater || playerMounted)
            {
                if (config.DebugLogging)
                    UnderwaterHorrorsModSystem.DebugLog(entity.Api,
                        $"Tentacle: stalling (shallowWater={IsInShallowWater}, mounted={playerMounted})");
                TransitionTo(TentacleState.Stalling);
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
            case TentacleState.Stalling:
                OnStalling(deltaTime);
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

    /// <summary>
    /// Fires when the head entity dies via damage (player kill). Without
    /// this, the head's deaddecay keeps AllowDespawn=false so the corpse
    /// sticks around for the configured decay window — and OnEntityDespawn
    /// won't fire until then, leaving the entire chain/claws dangling. We
    /// run cleanup right away and force the head itself to despawn.
    /// </summary>
    public override void OnEntityDeath(DamageSource damageSourceForDeath)
    {
        base.OnEntityDeath(damageSourceForDeath);
        RemoveSpeedDebuff();
        DespawnBiolumLights();
        chain?.Despawn();
        DespawnClaws();
        if (entity is EntityAgent agent) agent.AllowDespawn = true;
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
            SegmentInnerAsset, SegmentMidAsset, TipMidClawAsset, TipMidClawVisualHeight);
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
        if (clawIds == null) return;

        // Force AllowDespawn=true on every claw, alive or dead.
        // EntityBehaviorDeadDecay sets AllowDespawn=false during init so
        // player-killed corpses won't despawn until its decay timer fires.
        // The reported "floating static claw" was the player's own kill —
        // CheckAnyClawDead transitioned the tentacle to Sinking, but the
        // dead corpse stayed because its AllowDespawn was still false.
        // Setting it true here lets the server's ShouldDespawn check
        // remove the corpse on the next tick.
        for (int i = 0; i < clawIds.Length; i++)
        {
            long id = clawIds[i];
            if (id == 0) continue;
            Entity claw = entity.World.GetEntityById(id);
            if (claw == null) continue;
            if (claw is EntityAgent agent) agent.AllowDespawn = true;
            if (claw.Alive)
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

            // Pos.SetPos rather than TeleportToDouble — claws follow the
            // player one block out and never cross unloaded chunks; we
            // don't need teleport semantics or chunk-load priority.
            claw.Pos.SetPos(cx, cy, cz);
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

            // Move light to segment position. Pos.SetPos avoids the
            // chunk-load-priority + teleport-flag overhead — lights
            // shadow segments which are already at loaded positions.
            light.Pos.SetPos(seg.Pos.X, seg.Pos.Y, seg.Pos.Z);

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
            // Signal ambient tentacles to scatter (fan out across the sea
            // floor instead of sinking + despawning). The kraken body's
            // ambient siblings poll this attribute every tick.
            Entity body = GetBody();
            if (body != null && body.Alive)
            {
                body.WatchedAttributes.SetBool("underwaterhorrors:scatterAmbient", true);
                body.WatchedAttributes.MarkPathDirty("underwaterhorrors:scatterAmbient");

                if (config.DebugLogging)
                    UnderwaterHorrorsModSystem.DebugLog(entity.Api, "Attack tentacle: signaled ambient tentacles to scatter");
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

        // Note: mounted/shallow-water transitions are handled by the
        // Stalling check at the top of OnGameTick — this state body
        // only runs when the player is actually chase-able.

        double clampedY = Math.Min(targetPlayer.Entity.Pos.Y, config.CreatureMaxY);
        MoveToward(targetPlayer.Entity.Pos.X, clampedY, targetPlayer.Entity.Pos.Z, config.TentacleReachSpeed);

        double dist = entity.Pos.DistanceTo(targetPlayer.Entity.Pos.XYZ);
        if (dist < config.TentacleGrabRange)
        {
            TransitionTo(TentacleState.Dragging);
        }
    }

    /// <summary>
    /// Player is out of water (on land/beach) OR mounted on a boat.
    /// The tentacle holds station nearby — slowly drifting back toward
    /// the kraken body if the player is on land, or orbiting the boat
    /// if mounted — for up to TentacleStallDespawnSeconds. If the
    /// player returns to a chase-able state inside that window the
    /// tentacle resumes Reaching from where it stalled. Otherwise it
    /// transitions to Retreating and despawns.
    /// </summary>
    private void OnStalling(float deltaTime)
    {
        if (targetPlayer?.Entity == null || !targetPlayer.Entity.Alive)
        {
            TransitionTo(TentacleState.Sinking);
            return;
        }

        // Despawn once the stall window expires.
        if (stateTimer >= config.TentacleStallDespawnSeconds)
        {
            if (config.DebugLogging)
                UnderwaterHorrorsModSystem.DebugLog(entity.Api,
                    $"Tentacle stalled for {stateTimer:F1}s, retreating + despawning");
            TransitionTo(TentacleState.Retreating);
            return;
        }

        // Recompute the trigger conditions each tick. The shallow-water
        // check is throttled to 0.5s so it's cheap.
        UpdateShallowWaterCheck(deltaTime);
        bool playerMounted = targetPlayer.Entity.MountedOn != null;
        bool playerOutOfWater = IsInShallowWater;

        // Player came back into chase-able state — resume Reaching.
        if (!playerMounted && !playerOutOfWater)
        {
            if (config.DebugLogging)
                UnderwaterHorrorsModSystem.DebugLog(entity.Api,
                    "Tentacle: player back in deep water + dismounted, resuming Reaching");
            TransitionTo(TentacleState.Reaching);
            return;
        }

        if (playerMounted)
        {
            // Slow circular orbit around the boat at slightly below
            // the player's Y, capped at the surface so the tentacle
            // doesn't poke above water.
            float orbitAngle = stateTimer * config.TentacleStallOrbitSpeed;
            double radius = config.TentacleStallOrbitRadius;
            double tx = targetPlayer.Entity.Pos.X + Math.Cos(orbitAngle) * radius;
            double ty = Math.Min(targetPlayer.Entity.Pos.Y - 1.5, config.CreatureMaxY);
            double tz = targetPlayer.Entity.Pos.Z + Math.Sin(orbitAngle) * radius;
            MoveToward(tx, ty, tz, config.TentacleStallBoatSpeed);
        }
        else
        {
            // Drift slowly back toward the kraken body. Visible "I'm
            // giving up but still around" motion rather than a hard
            // retreat.
            Entity body = GetBody();
            if (body != null && body.Alive)
            {
                MoveToward(body.Pos.X, body.Pos.Y + 1, body.Pos.Z, config.TentacleStallDriftSpeed);
            }
            else
            {
                entity.Pos.Motion.X = 0;
                entity.Pos.Motion.Y = -config.TentacleStallDriftSpeed;
                entity.Pos.Motion.Z = 0;
            }
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

        // Keep tentacle tip below player. Pos.SetPos rather than
        // TeleportToDouble — the head stays one block from the player,
        // who already loaded the chunk, so we don't need teleport
        // semantics. Saves a LoadChunkColumnPriority call per tick.
        entity.Pos.SetPos(
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
