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
    /// <summary>
    /// Set by the kraken body when this tentacle was promoted because a
    /// player was standing right next to it. Starts the state machine in
    /// Reaching instead of Idle, so it lunges rather than performing the
    /// full surface-and-loom routine at someone already in its face.
    /// </summary>
    public const string HuntImmediatelyAttr = "underwaterhorrors:huntImmediately";

    // Coverage = SegmentCount * SegmentVisualHeight. The kraken sits on the
    // sea floor and the tip rises to the surface (CreatureCeilingY), so the
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

    // Server-tuning knob: scales every movement that goes through the
    // base-class movers (rise, linger drift, pursuit, drag). The direct
    // Motion writes for sinking and retreating are deliberately left
    // alone, since those are despawn animations rather than hunting.
    protected override double SpeedScale => config?.TentacleSpeedMultiplier ?? 1.0;

    private TentacleState state = TentacleState.Idle;
    private float stateTimer;
    private bool speedDebuffApplied;

    // Seconds since this tentacle last hurt the player it is holding.
    private float grabDamageAccum;

    // Latches once this tentacle starts hunting; see SignalScatterOnce.
    private bool scatterSignalled;

    // Sink-to-floor bookkeeping, used while the kraken body is dead.
    // -1 means "not scanned yet"; see TentacleRemains.TickSink.
    private double sinkFloorY = -1;
    private float sinkScanTimer;
    private bool remainsLeft;

    // Kraken-death handling: once the body is dead, AI logic stops, no
    // new entities spawn, claws/lights are cleaned up, and the tentacle
    // sinks to the sea floor, leaves its remains and dies. Latches via a
    // flag so cleanup runs exactly once; krakenDeathTimer is the elapsed
    // counter TentacleRemains.TickSink uses for its give-up timeout.
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

    // The mount that carries the player during Dragging. See
    // EntityBehaviorTentacleGrip for why a mount and not a teleport.
    // Resolved lazily rather than in Initialize: behaviors are created and
    // initialized one at a time in JSON order, so looking it up early would
    // silently return null if someone reorders krakententacle.json.
    private EntityBehaviorTentacleGrip gripCache;
    private EntityBehaviorTentacleGrip Grip =>
        gripCache ??= entity.GetBehavior<EntityBehaviorTentacleGrip>();

    // Surface point for the Rising/Lingering phases
    private double surfaceX, surfaceY, surfaceZ;
    private bool surfacePointPicked;

    // Static flag is set once at spawn (e.g. by /uh kraken show) and
    // never changes. Cache to avoid per-tick WatchedAttributes lookup.
    private bool isStatic;

    // Offsets for 4 claws: +X, -X, +Z, -Z (1 block out from player)
    private static readonly double[][] ClawOffsets = new double[][]
    {
        new double[] {  1.0, 0.5,  0.0 },  // East
        new double[] { -1.0, 0.5,  0.0 },  // West
        new double[] {  0.0, 0.5,  1.0 },  // South
        new double[] {  0.0, 0.5, -1.0 },  // North
    };

    public EntityBehaviorTentacle(Entity entity) : base(entity) { }

    public override void Initialize(EntityProperties properties, JsonObject attributes)
    {
        // CRITICAL: must forward to OceanCreature.Initialize, which sets
        // up the shared `config` field. Skipping base call leaves config
        // null and ClampHeight crashes on the first tick.
        base.Initialize(properties, attributes);
        isStatic = entity.WatchedAttributes.GetBool("underwaterhorrors:static", false);

        if (entity.WatchedAttributes.GetBool(HuntImmediatelyAttr, false))
        {
            // Straight to the hunt. surfacePointPicked is latched so the
            // Rising/Lingering surface point is never chosen; those states
            // are skipped entirely for this tentacle's whole life.
            state = TentacleState.Reaching;
            surfacePointPicked = true;
        }

        // Publish the server's grab offset so riding clients seat the player
        // at the same height the server puts the claws. TentacleGrabYOffset
        // is signed head-relative-to-player; the seat wants the inverse.
        if (entity.Api.Side == EnumAppSide.Server && config != null)
        {
            Grip?.SetRiderYOffset(-config.TentacleGrabYOffset);
        }
    }

    public override void OnGameTick(float deltaTime)
    {
        // Vanilla gate: skip the entire AI tick when the entity is Inactive
        // (no client within SimulationRange = 128 blocks). Same pattern
        // vsessentialsmod's BehaviorTaskAI uses on line 1 of its tick body.
        // Without this, every tentacle keeps running full state-machine +
        // chain updates even on a server with no nearby players.
        if (entity.State != EnumEntityState.Active) return;
        if (!entity.Alive) return;
        if (entity.Api.Side != EnumAppSide.Server) return;
        if (isStatic) return;

        ResolveTarget();
        ClampHeight();

        // Safety net: nobody should be seated on this tentacle unless it is
        // actively dragging. Covers the paths TransitionTo cannot see — a
        // server restart while a player was held (state resets to Idle but
        // the rider's saved mountedOn attribute re-seats them), or the
        // kraken-death branch below returning before the state machine runs.
        // A rider stuck mounted to an idle tentacle would be frozen for good.
        if (state != TentacleState.Dragging && Grip != null && Grip.AnyMounted())
        {
            Grip.Release();
        }

        // Kraken-death short-circuit. If the body died this tick (or any
        // earlier tick) we run cleanup once, then this branch every tick:
        // skip ALL state logic, biolum spawning, claw spawning, respawn
        // signals, etc. The chain still updates so segments visibly fall
        // with the tentacle.
        //
        // The tentacle now sinks all the way to the sea floor rather than
        // falling for a fixed few seconds and vanishing mid-water, and
        // leaves its remains where it touches down. TickSink's timeout is
        // what guarantees this ends.
        Entity body = GetBody();
        if (body == null || !body.Alive)
        {
            if (!krakenDeathHandled)
            {
                krakenDeathHandled = true;
                krakenDeathTimer = 0f;
                Grip?.Release();
                if (clawsSpawned) DespawnClaws();
                RemoveSpeedDebuff();
                if (config.DebugLogging)
                    UnderwaterHorrorsModSystem.DebugLog(entity.Api,
                        "Tentacle: kraken body dead, sinking to the sea floor.");
            }

            if (TentacleRemains.TickSink(entity, deltaTime, ref krakenDeathTimer,
                    config.TentacleDeathSinkSpeed, config.TentacleSinkToFloorTimeout,
                    ref sinkFloorY, ref sinkScanTimer))
            {
                LeaveRemainsOnce();
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
            // Our own grip mounts the player too, so "is the player on a
            // boat" has to exclude this tentacle's seat, or the drag would
            // stall itself on the very tick it grabs someone.
            IMountableSeat mount = targetPlayer?.Entity?.MountedOn;
            bool playerMounted = mount != null && mount.Entity != entity;
            if (IsInShallowWater || playerMounted)
            {
                if (config.DebugLogging)
                    UnderwaterHorrorsModSystem.DebugLog(entity.Api,
                        $"Tentacle: stalling (shallowWater={IsInShallowWater}, mounted={playerMounted})");
                TransitionTo(TentacleState.Stalling);
            }
        }

        // Idempotent, and covers both routes into the hunt: the Lingering
        // timeout and a proximity promotion that started here.
        if (state == TentacleState.Reaching || state == TentacleState.Dragging)
        {
            SignalScatterOnce();
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
        // Reaching only. Once the grab lands the player rides the head, so
        // aiming at them would be aiming at a point roughly half a block
        // away and the heading would spin on rounding noise. The tangent
        // branch below points the head down the drag path toward the body,
        // which is what a tentacle hauling something looks like anyway.
        if (state == TentacleState.Reaching
            && targetPlayer?.Entity != null && !TargetIsPassiveObserver)
        {
            TentacleHeadAlignment.AlignToward(entity,
                targetPlayer.Entity.Pos.X,
                targetPlayer.Entity.Pos.Y,
                targetPlayer.Entity.Pos.Z);
        }
        else
        {
            GetBodyAnchor(out double anchorX, out double anchorY, out double anchorZ);
            TentacleHeadAlignment.AlignToTangent(entity, anchorX, anchorY, anchorZ, config.TentacleArchHeightFactor);
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

        // Killed outright rather than sinking with a dead body. The whole
        // segment chain goes with it on this tick, so there is nothing left
        // to animate downward; the remains go straight to the floor beneath
        // where it died.
        LeaveRemainsOnce();

        if (entity is EntityAgent agent) agent.AllowDespawn = true;
    }

    /// <summary>
    /// Bone pile plus rusted machinery on the sea floor below. Latched, so
    /// a tentacle that both sinks and then dies only leaves one set.
    /// </summary>
    private void LeaveRemainsOnce()
    {
        if (remainsLeft) return;
        remainsLeft = true;
        TentacleRemains.Leave(entity, config);
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

        GetGrabPoint(out double px, out double py, out double pz);

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

    /// <summary>
    /// Where the tentacle is holding the player: the head plus the seat's
    /// rider offset. Derived from the head rather than read off the player,
    /// because a mounted player's server-side position is whatever their
    /// client last reported and so trails the head by a round trip. The
    /// claws have to sit exactly where the grab is, not where it was.
    /// </summary>
    private void GetGrabPoint(out double x, out double y, out double z)
    {
        x = entity.Pos.X;
        y = entity.Pos.Y + (Grip?.RiderYOffset ?? EntityBehaviorTentacleGrip.DefaultRiderYOffset);
        z = entity.Pos.Z;
    }

    private void PositionClaws()
    {
        if (clawEntities == null) return;

        GetGrabPoint(out double px, out double py, out double pz);

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

    /// <summary>
    /// Crushes the held player on a timer. The kraken body's contact
    /// damage explicitly skips mounted players, so without this a grabbed
    /// player takes nothing until they drown; this makes the grab a clock
    /// you have to beat by killing a claw.
    /// </summary>
    private void ApplyGrabDamage(float deltaTime)
    {
        if (!config.TentacleGrabDamageEnabled || config.TentacleGrabDamage <= 0) return;
        if (targetPlayer?.Entity == null || !targetPlayer.Entity.Alive) return;

        grabDamageAccum += deltaTime;
        if (grabDamageAccum < config.TentacleGrabDamageIntervalSeconds) return;
        grabDamageAccum = 0f;

        targetPlayer.Entity.ReceiveDamage(new DamageSource
        {
            Source = EnumDamageSource.Entity,
            SourceEntity = entity,
            Type = EnumDamageType.PiercingAttack,
            DamageTier = config.KrakenDamageTier
        }, config.TentacleGrabDamage);

        if (config.DebugLogging)
            UnderwaterHorrorsModSystem.DebugLog(entity.Api,
                $"Tentacle crushed {targetPlayer.PlayerName} for {config.TentacleGrabDamage}");
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
            Grip?.Release();
            RemoveSpeedDebuff();
            DespawnClaws();
        }

        // Close the last of the reach before seating anyone. Reaching stops
        // as soon as the head is within TentacleGrabRange, so without this
        // the mount would yank the player up to two blocks sideways on the
        // tick the grab lands - the one snap the whole rework exists to
        // remove. Moving the invisible head instead costs nothing visible.
        if (newState == TentacleState.Dragging && oldState != TentacleState.Dragging
            && targetPlayer?.Entity != null)
        {
            // Fresh grab, fresh crush clock: the player gets the full
            // interval before the first hit rather than inheriting
            // whatever was left over from a previous grab.
            grabDamageAccum = 0f;

            entity.Pos.SetPos(
                targetPlayer.Entity.Pos.X,
                targetPlayer.Entity.Pos.Y + config.TentacleGrabYOffset,
                targetPlayer.Entity.Pos.Z
            );
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
            surfaceY = Math.Min(targetPlayer.Entity.Pos.Y, CreatureCeilingY);
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

    /// <summary>
    /// Gentle drift around the picked surface point. Used while
    /// Lingering, and while Reaching when the target is a creative
    /// observer (the tentacle sways in place instead of chasing).
    /// </summary>
    private void DriftAroundSurfacePoint()
    {
        double bobX = surfaceX + Math.Sin(stateTimer * 0.5) * 2.0;
        double bobZ = surfaceZ + Math.Cos(stateTimer * 0.5) * 2.0;
        double bobY = surfaceY + Math.Sin(stateTimer * 0.7) * 1.0;

        double dx = bobX - entity.Pos.X;
        double dy = bobY - entity.Pos.Y;
        double dz = bobZ - entity.Pos.Z;

        entity.Pos.Motion.X = dx * 0.05;
        entity.Pos.Motion.Y = dy * 0.05;
        entity.Pos.Motion.Z = dz * 0.05;
    }

    private void OnLingering(float deltaTime)
    {
        if (targetPlayer?.Entity == null || !targetPlayer.Entity.Alive)
        {
            TransitionTo(TentacleState.Sinking);
            return;
        }

        DriftAroundSurfacePoint();

        if (stateTimer >= config.TentacleLingerDuration)
        {
            TransitionTo(TentacleState.Reaching);
        }
    }

    /// <summary>
    /// Signal ambient tentacles to scatter (fan out across the sea floor
    /// instead of sinking + despawning). The kraken body's ambient siblings
    /// poll this attribute every tick.
    ///
    /// Fired once, when this tentacle first starts hunting, from wherever
    /// that happens: normally the Lingering timeout, but a proximity-
    /// promoted tentacle begins life in Reaching and would otherwise never
    /// raise it, leaving its siblings orbiting through the whole fight.
    /// </summary>
    private void SignalScatterOnce()
    {
        if (scatterSignalled) return;
        scatterSignalled = true;

        Entity body = GetBody();
        if (body == null || !body.Alive) return;

        body.WatchedAttributes.SetBool("underwaterhorrors:scatterAmbient", true);
        body.WatchedAttributes.MarkPathDirty("underwaterhorrors:scatterAmbient");

        if (config.DebugLogging)
            UnderwaterHorrorsModSystem.DebugLog(entity.Api, "Attack tentacle: signaled ambient tentacles to scatter");
    }

    private void OnReaching(float deltaTime)
    {
        if (targetPlayer?.Entity == null || !targetPlayer.Entity.Alive)
        {
            TransitionTo(TentacleState.Sinking);
            return;
        }

        // Creative observer: sway near the surface point instead of
        // chasing; never reaches grab range. Checked live, so switching
        // out of creative resumes the chase from wherever it drifted.
        if (TargetIsPassiveObserver)
        {
            DriftAroundSurfacePoint();
            return;
        }

        // Note: mounted/shallow-water transitions are handled by the
        // Stalling check at the top of OnGameTick — this state body
        // only runs when the player is actually chase-able.

        double clampedY = Math.Min(targetPlayer.Entity.Pos.Y, CreatureCeilingY);
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
            double ty = Math.Min(targetPlayer.Entity.Pos.Y - 1.5, CreatureCeilingY);
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

        // Motion is in blocks per physics step and the physics integrator
        // advances by Motion * dt * 60, so a speed expressed in blocks per
        // second converts by dividing by 60. TentacleDragSpeed keeps its
        // original blocks-per-second meaning from when the drag moved the
        // player by hand, so existing configs still mean what they say.
        const double BlocksPerSecondToMotion = 1.0 / 60.0;

        // Target switched to creative/spectator mid-drag (or the toggle
        // was just enabled): release the grip. TransitionTo restores the
        // speed debuff and despawns the claws; Reaching then sees the
        // passive target and just sways.
        if (TargetIsPassiveObserver)
        {
            if (config.DebugLogging)
                UnderwaterHorrorsModSystem.DebugLog(entity.Api,
                    $"Tentacle: {targetPlayer.PlayerName} is a creative observer, releasing grip");
            TransitionTo(TentacleState.Reaching);
            return;
        }

        ApplySpeedDebuff();

        // Seat the player on the head. From here the client positions
        // itself from the seat every physics tick and interpolates the
        // head between server updates every render frame, so the drag is
        // as smooth as riding a horse. Called every tick because the grab
        // has to survive a rider who disconnects and reconnects mid-drag.
        if (Grip != null && targetPlayer.Entity is EntityAgent rider && !Grip.Grip(rider))
        {
            if (config.DebugLogging)
                UnderwaterHorrorsModSystem.DebugLog(entity.Api, "Tentacle: could not seat the player, releasing");
            TransitionTo(TentacleState.Sinking);
            return;
        }

        ApplyGrabDamage(deltaTime);

        // Spawn claws around the grab point on first drag tick
        if (!clawsSpawned)
        {
            SpawnClaws();
        }

        // Position claws around the grab point
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

        // The head is what moves now; the rider comes along with it. That
        // inversion is the whole fix. Moving the player from the server
        // fought their own client-side physics, whereas the head is a
        // plain server-owned entity that nothing else is simulating, so
        // its path is smooth by construction. It also means the head's
        // controlledphysics does terrain collision for the drag, which is
        // strictly better than the old passability probe.
        double dx = body.Pos.X - entity.Pos.X;
        double dy = body.Pos.Y - entity.Pos.Y;
        double dz = body.Pos.Z - entity.Pos.Z;
        double dist = Math.Sqrt(dx * dx + dy * dy + dz * dz);

        if (dist > 0.5)
        {
            MoveToward(body.Pos.X, body.Pos.Y, body.Pos.Z,
                config.TentacleDragSpeed * BlocksPerSecondToMotion);
        }
        else
        {
            entity.Pos.Motion.Set(0, 0, 0);
        }
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
