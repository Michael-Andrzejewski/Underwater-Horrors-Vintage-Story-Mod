using System;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;

namespace UnderwaterHorrors;

/// <summary>
/// Deep serpent variant. Stays much deeper below the surface (10-30 blocks),
/// orbits in much larger arcs (up to ~80 blocks), stalks for up to two
/// minutes, and rises to the surface only when directly attacking.
/// Body stays nearly horizontal at all times (pitch is clamped to a
/// fraction of a degree and interpolated very slowly).
/// </summary>
public class EntityBehaviorDeepSerpentAI : EntityBehaviorOceanCreature
{
    private SerpentState state = SerpentState.Rising;
    private float stateTimer;
    private float orbitAngle;
    private float attackCooldownTimer;

    // ── Animation codes (match the new shape file) ───────────────────
    private const string AnimFastSwim = "fastswim";
    private const string AnimSlowSwim = "slowswim";
    private const string AnimHiss = "hiss";
    private const string AnimStandAndHiss = "standandhiss";
    private const string AnimWindupRight = "windupattackright";
    private const string AnimWindupLeft = "windupattackleft";
    private const string AnimAttack = "attack1";

    private string currentAnim;

    // ── Attack ─────────────────────────────────────────────────────────
    private const float WindupDuration = 1.3f;
    private const float StrikeDuration = 0.5f;
    private const float StrikeDamageTime = 0.25f;

    private bool isWindingUp;
    private bool isStriking;
    private float attackAnimTimer;
    private bool strikeDamageDealt;
    private bool attackFromRight;

    // How close the HEAD must be to the player to trigger the windup.
    // Once triggered, damage is guaranteed.
    private const float HeadAttackTriggerRange = 4.0f;

    // Model offset matching the other serpent.
    private const float HeadForwardOffset = 9.0f;

    // Depth chosen at spawn (between config min/max). The serpent cruises
    // this many blocks BELOW the player's Y during Stalking.
    private float stalkDepth;

    // ── Facing (minimal pitch) ────────────────────────────────────────
    private const float ModelYawOffset = 0f;
    private float smoothedYaw;
    private bool yawInitialized;
    private bool lockFacing;
    private bool faceTarget;

    // ── Spiral approach fields ─────────────────────────────────────────
    private bool useSpiralApproach;
    private float orbitRadiusStart;
    private float orbitRadiusEnd;
    private float radiusTransitionTime;
    private float radiusTransitionDuration;
    // Per-spiral-step flag: when true, this orbit rises to the surface
    // (rarer on the deep variant).  Rolled in SetNextSpiralStep.
    private bool currentStepAtSurface;

    // ── Surface point (for initial hiss) ──────────────────────────────
    private double surfaceX, surfaceZ;
    private bool surfacePointPicked;
    private const float SurfaceDistMin = 10f;
    private const float SurfaceDistMax = 30f;

    // ── Spawn position for retreat ─────────────────────────────────────
    private double spawnX, spawnY, spawnZ;
    private bool spawnRecorded;

    // ── Boat boredom ──────────────────────────────────────────────────
    private float mountedCircleTimer;
    private float mountedCheckTimer;
    private bool committedRetreat;

    // ── Proximity-based aggro ─────────────────────────────────────────
    private float proximityBodyDwellTimer;
    private float proximityBodyDwellThreshold;

    /// <summary>
    /// True while the serpent is in any attack phase (charging, winding
    /// up, or striking).  Exposed so HorizontalLockRenderer can read it
    /// and skip zeroing pitch while the head needs to aim at the player.
    /// </summary>
    public bool IsInAttackPhase => faceTarget || isWindingUp || isStriking;

    // Slew-limiter state `lastCommandedMotionY` is inherited from
    // EntityBehaviorOceanCreature and shared with MoveTowardDamped.

    private HorizontalLockRenderer lockRenderer;

    public EntityBehaviorDeepSerpentAI(Entity entity) : base(entity) { }

    public override void Initialize(EntityProperties properties, JsonObject attributes)
    {
        base.Initialize(properties, attributes);
        orbitAngle = (float)(entity.World.Rand.NextDouble() * Math.PI * 2);

        if (entity.Api.Side == EnumAppSide.Server)
        {
            // Pick a stalking depth for this instance (10-30 blocks below)
            stalkDepth = config.DeepSerpentStalkDepthMin +
                (float)(entity.World.Rand.NextDouble() *
                    (config.DeepSerpentStalkDepthMax - config.DeepSerpentStalkDepthMin));

            PlayAnimation(AnimFastSwim);
        }
        else
        {
            // Client: install a renderer that zeroes Pitch / HeadPitch / Roll
            // every render frame, AFTER InterpolatePosition runs.  This is
            // the strongest available lock — any other system that writes
            // these fields during the tick gets overwritten before draw.
            if (entity.Api is Vintagestory.API.Client.ICoreClientAPI capi)
            {
                lockRenderer = new HorizontalLockRenderer(capi, entity, this);
                capi.Event.RegisterRenderer(lockRenderer,
                    Vintagestory.API.Client.EnumRenderStage.Before,
                    "underwaterhorrors-horizlock");
            }
        }
    }

    public override void OnEntityDespawn(EntityDespawnData despawn)
    {
        if (lockRenderer != null && entity.Api is Vintagestory.API.Client.ICoreClientAPI capi)
        {
            capi.Event.UnregisterRenderer(lockRenderer,
                Vintagestory.API.Client.EnumRenderStage.Before);
            lockRenderer = null;
        }
        base.OnEntityDespawn(despawn);
    }

    // ═══════════════════════════════════════════════════════════════════
    //  Head position helper (matches EntityBehaviorSerpentAI)
    // ═══════════════════════════════════════════════════════════════════
    private void GetHeadPosition(out double hx, out double hy, out double hz)
    {
        float yaw = entity.Pos.Yaw;
        hx = entity.Pos.X + Math.Sin(yaw) * HeadForwardOffset;
        hy = entity.Pos.Y;
        hz = entity.Pos.Z + Math.Cos(yaw) * HeadForwardOffset;
    }

    private double HeadDistToPlayer()
    {
        if (targetPlayer?.Entity == null) return double.MaxValue;
        GetHeadPosition(out double hx, out double hy, out double hz);
        double dx = hx - targetPlayer.Entity.Pos.X;
        double dy = hy - targetPlayer.Entity.Pos.Y;
        double dz = hz - targetPlayer.Entity.Pos.Z;
        return Math.Sqrt(dx * dx + dy * dy + dz * dz);
    }

    // ═══════════════════════════════════════════════════════════════════
    //  Main tick
    // ═══════════════════════════════════════════════════════════════════
    public override void OnGameTick(float deltaTime)
    {
        // Vanilla gate: skip when no client within SimulationRange.
        // See EntityBehaviorTentacle.OnGameTick for rationale.
        if (entity.State != EnumEntityState.Active) return;
        if (!entity.Alive) return;

        // HORIZONTAL LOCK — zeroes Pitch/Roll/HeadPitch.  Skipped
        // during attack phases so the mouth can aim at the player.
        // HorizontalLockRenderer (client-side) performs the same check.
        if (!IsInAttackPhase)
        {
            ForceHorizontal(entity.Pos);
            ForceHorizontal(entity.Pos);
        }

        if (entity.Api.Side != EnumAppSide.Server) return;

        // Record spawn position on first tick
        if (!spawnRecorded)
        {
            spawnRecorded = true;
            spawnX = entity.Pos.X;
            spawnY = entity.Pos.Y;
            spawnZ = entity.Pos.Z;
        }

        ResolveTarget();
        ClampHeight();

        if (!surfacePointPicked && targetPlayer?.Entity != null)
        {
            PickSurfacePoint();
        }

        if (state != SerpentState.Rising &&
            state != SerpentState.Surfacing &&
            state != SerpentState.Retreating)
        {
            UpdateShallowWaterCheck(deltaTime);
            if (IsInShallowWater)
            {
                if (config.DebugLogging)
                    UnderwaterHorrorsModSystem.DebugLog(entity.Api,
                        "DeepSerpent: player in shallow water, retreating");
                TransitionTo(SerpentState.Retreating);
            }
        }

        // Boat boredom — see regular serpent for commentary.
        if (state == SerpentState.Stalking || state == SerpentState.Attacking)
        {
            if (targetPlayer?.Entity?.MountedOn != null)
            {
                mountedCircleTimer += deltaTime;
                if (mountedCircleTimer >= config.BoatBoredomGraceSeconds)
                {
                    mountedCheckTimer += deltaTime;
                    if (mountedCheckTimer >= 30f)
                    {
                        mountedCheckTimer = 0;
                        if (entity.World.Rand.NextDouble() < config.BoatBoredomRetreatRollChance)
                        {
                            if (config.DebugLogging)
                                UnderwaterHorrorsModSystem.DebugLog(entity.Api,
                                    $"DeepSerpent bored after {mountedCircleTimer:F0}s mounted, retreating");
                            committedRetreat = true;
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
            case SerpentState.Rising:    OnRising(deltaTime);    break;
            case SerpentState.Surfacing: OnSurfacing(deltaTime); break;
            case SerpentState.Stalking:  OnStalking(deltaTime);  break;
            case SerpentState.Attacking: OnAttacking(deltaTime); break;
            case SerpentState.Retreating:OnRetreating(deltaTime); break;
        }

        UpdateFacing(deltaTime);
    }

    // ═══════════════════════════════════════════════════════════════════
    //  Surface point
    // ═══════════════════════════════════════════════════════════════════
    private void PickSurfacePoint()
    {
        surfacePointPicked = true;
        var rand = entity.World.Rand;
        float angle = (float)(rand.NextDouble() * Math.PI * 2);
        float dist = SurfaceDistMin + (float)(rand.NextDouble() * (SurfaceDistMax - SurfaceDistMin));
        surfaceX = targetPlayer.Entity.Pos.X + Math.Cos(angle) * dist;
        surfaceZ = targetPlayer.Entity.Pos.Z + Math.Sin(angle) * dist;
    }

    // ═══════════════════════════════════════════════════════════════════
    //  Facing — minimal pitch (fraction of a degree, very slow interp)
    // ═══════════════════════════════════════════════════════════════════
    private void UpdateFacing(float deltaTime)
    {
        double mx = entity.Pos.Motion.X;
        double mz = entity.Pos.Motion.Z;
        double my = entity.Pos.Motion.Y;
        double horizSpeedSq = mx * mx + mz * mz;

        if (!lockFacing)
        {
            float targetYaw;
            if (faceTarget && targetPlayer?.Entity != null)
            {
                double dx = targetPlayer.Entity.Pos.X - entity.Pos.X;
                double dz = targetPlayer.Entity.Pos.Z - entity.Pos.Z;
                targetYaw = (float)Math.Atan2(dx, dz) + ModelYawOffset;
            }
            else if (horizSpeedSq > 0.00001)
            {
                targetYaw = (float)Math.Atan2(mx, mz) + ModelYawOffset;
            }
            else
            {
                targetYaw = smoothedYaw;
            }

            if (!yawInitialized)
            {
                smoothedYaw = targetYaw;
                yawInitialized = true;
            }
            else
            {
                float turnRate = faceTarget ? 8f : 5f;
                float diff = GameMath.AngleRadDistance(smoothedYaw, targetYaw);
                smoothedYaw += diff * Math.Min(1f, deltaTime * turnRate);
            }

            entity.Pos.Yaw = smoothedYaw;
        }

        // Pitch:
        //   Stalking: hard-locked to 0 by ForceHorizontal (top of tick)
        //     AND by HorizontalLockRenderer (every render frame).
        //   Attack phases: aim directly at the player so the head can
        //     tilt up/down to strike a target above or below.
        if (IsInAttackPhase && targetPlayer?.Entity != null && !lockFacing)
        {
            double tdx = targetPlayer.Entity.Pos.X - entity.Pos.X;
            double tdy = targetPlayer.Entity.Pos.Y - entity.Pos.Y;
            double tdz = targetPlayer.Entity.Pos.Z - entity.Pos.Z;
            double horizToTarget = Math.Sqrt(tdx * tdx + tdz * tdz);
            float targetPitch = -(float)Math.Atan2(tdy, Math.Max(horizToTarget, 0.001));
            targetPitch = GameMath.Clamp(targetPitch, -1.0f, 1.0f);  // ~57° max
            entity.Pos.Pitch += (targetPitch - entity.Pos.Pitch) *
                Math.Min(1f, deltaTime * 6f);
        }
    }

    /// <summary>
    /// Zero every rotation component except Yaw. Intended to be called as
    /// the last step before network/render consumption.
    /// </summary>
    internal static void ForceHorizontal(EntityPos pos)
    {
        if (pos == null) return;
        pos.Pitch = 0f;
        pos.Roll = 0f;
        pos.HeadPitch = 0f;
    }

    // ═══════════════════════════════════════════════════════════════════
    //  MoveTowardDamped — proportional approach with NO dead zone.
    //  The base MoveToward() is bang-bang + minDist dead zone, which
    //  hysteresis-oscillates against controlledphysics water buoyancy and
    //  causes visible vertical bobbing of the long body.  Here Motion
    //  tapers smoothly to zero at the target.
    // ═══════════════════════════════════════════════════════════════════
    // MoveTowardDamped is inherited from EntityBehaviorOceanCreature.

    // ═══════════════════════════════════════════════════════════════════
    //  Animation helpers
    // ═══════════════════════════════════════════════════════════════════
    private void PlayAnimation(string code, float speed = 1f)
    {
        if (currentAnim == code) return;
        if (currentAnim != null)
            entity.AnimManager.StopAnimation(currentAnim);
        currentAnim = code;
        entity.AnimManager.StartAnimation(new AnimationMetaData
        {
            Animation = code,
            Code = code,
            AnimationSpeed = speed,
            BlendMode = EnumAnimationBlendMode.Add
        }.Init());
    }

    private void ForcePlayAnimation(string code, float speed = 1f)
    {
        if (currentAnim == code && entity.AnimManager.IsAnimationActive(code))
        {
            entity.AnimManager.ResetAnimation(code);
            return;
        }
        if (currentAnim != null)
            entity.AnimManager.StopAnimation(currentAnim);
        currentAnim = code;
        entity.AnimManager.StartAnimation(new AnimationMetaData
        {
            Animation = code,
            Code = code,
            AnimationSpeed = speed,
            EaseInSpeed = 999f,
            BlendMode = EnumAnimationBlendMode.Add
        }.Init());
    }

    // ═══════════════════════════════════════════════════════════════════
    //  State transitions
    // ═══════════════════════════════════════════════════════════════════
    private void TransitionTo(SerpentState newState)
    {
        SerpentState oldState = state;
        state = newState;
        stateTimer = 0;
        isWindingUp = false;
        isStriking = false;
        strikeDamageDealt = false;
        lockFacing = false;
        faceTarget = false;

        if (config.DebugLogging)
        {
            string playerName = targetPlayer?.PlayerName ?? "unknown";
            UnderwaterHorrorsModSystem.DebugLog(entity.Api,
                $"DeepSerpent state: {oldState} → {newState} (target: {playerName})");
        }

        switch (newState)
        {
            case SerpentState.Rising:
                PlayAnimation(AnimFastSwim);
                break;
            case SerpentState.Surfacing:
                bool onBoat = targetPlayer?.Entity?.MountedOn != null;
                PlayAnimation(onBoat ? AnimStandAndHiss : AnimHiss);
                break;
            case SerpentState.Stalking:
                PlayAnimation(AnimSlowSwim);
                lastCommandedMotionY = entity.Pos.Motion.Y;
                proximityBodyDwellTimer = 0;
                proximityBodyDwellThreshold =
                    config.SerpentProximityBodyDwellMin +
                    (float)(entity.World.Rand.NextDouble() *
                        (config.SerpentProximityBodyDwellMax -
                         config.SerpentProximityBodyDwellMin));
                break;
            case SerpentState.Attacking:
                PlayAnimation(AnimFastSwim);
                faceTarget = true;
                lastCommandedMotionY = entity.Pos.Motion.Y;
                break;
            case SerpentState.Retreating:
                PlayAnimation(AnimFastSwim);
                break;
        }

        if (newState == SerpentState.Stalking)
        {
            if (oldState == SerpentState.Rising || oldState == SerpentState.Surfacing)
            {
                useSpiralApproach = true;
                SetupSpiralApproach(true);
            }
            else if (oldState == SerpentState.Attacking)
            {
                useSpiralApproach = true;
                SetupSpiralApproach(false);
            }
        }
    }

    // ═══════════════════════════════════════════════════════════════════
    //  Spiral helpers — much larger radii, longer steps
    // ═══════════════════════════════════════════════════════════════════
    private void SetupSpiralApproach(bool fullApproach)
    {
        var rand = entity.World.Rand;

        if (fullApproach)
        {
            orbitRadiusStart = config.DeepSerpentInitialOrbitRadiusMin +
                (float)(rand.NextDouble() *
                    (config.DeepSerpentInitialOrbitRadiusMax - config.DeepSerpentInitialOrbitRadiusMin));
        }
        else
        {
            orbitRadiusStart = config.DeepSerpentOrbitRadius * (2f + (float)rand.NextDouble());
        }

        SetNextSpiralStep();

        if (config.DebugLogging)
            UnderwaterHorrorsModSystem.DebugLog(entity.Api,
                $"DeepSerpent spiral: start radius {orbitRadiusStart:F1}, " +
                $"first target {orbitRadiusEnd:F1} over {radiusTransitionDuration:F1}s");
    }

    private void SetNextSpiralStep()
    {
        var rand = entity.World.Rand;
        float reduction = config.DeepSerpentSpiralReductionMin +
            (float)(rand.NextDouble() *
                (config.DeepSerpentSpiralReductionMax - config.DeepSerpentSpiralReductionMin));
        orbitRadiusEnd = Math.Max(config.DeepSerpentOrbitRadius, orbitRadiusStart - reduction);
        radiusTransitionDuration = config.DeepSerpentSpiralStepDurationMin +
            (float)(rand.NextDouble() *
                (config.DeepSerpentSpiralStepDurationMax - config.DeepSerpentSpiralStepDurationMin));
        radiusTransitionTime = 0;

        // Rare surface peek — deep variant mostly stays deep.
        currentStepAtSurface = rand.NextDouble() < config.DeepSerpentSurfacePeekChance;
        if (config.DebugLogging && currentStepAtSurface)
            UnderwaterHorrorsModSystem.DebugLog(entity.Api,
                $"DeepSerpent: surface-peek step ({radiusTransitionDuration:F0}s)");
    }

    // ═══════════════════════════════════════════════════════════════════
    //  Rising — come up for the initial hiss.  Same as regular serpent.
    // ═══════════════════════════════════════════════════════════════════
    private void OnRising(float deltaTime)
    {
        if (targetPlayer?.Entity == null) return;

        double targetY = targetPlayer.Entity.Pos.Y;
        entity.Pos.Motion.Y = config.SerpentRiseSpeed;

        double dx = surfaceX - entity.Pos.X;
        double dz = surfaceZ - entity.Pos.Z;
        double horizDist = Math.Sqrt(dx * dx + dz * dz);

        if (horizDist > 1)
        {
            entity.Pos.Motion.X = (dx / horizDist) * config.SerpentApproachSpeed;
            entity.Pos.Motion.Z = (dz / horizDist) * config.SerpentApproachSpeed;
        }

        if (entity.Pos.Y >= targetY - 2)
        {
            TransitionTo(SerpentState.Surfacing);
        }
    }

    // ═══════════════════════════════════════════════════════════════════
    //  Surfacing — drift toward player, hiss. Same as regular serpent.
    // ═══════════════════════════════════════════════════════════════════
    private void OnSurfacing(float deltaTime)
    {
        if (targetPlayer?.Entity == null) return;

        double dx = targetPlayer.Entity.Pos.X - entity.Pos.X;
        double dz = targetPlayer.Entity.Pos.Z - entity.Pos.Z;
        double dist = Math.Sqrt(dx * dx + dz * dz);

        if (dist > 0.5)
        {
            entity.Pos.Motion.X = (dx / dist) * config.SerpentApproachSpeed * 0.2;
            entity.Pos.Motion.Z = (dz / dist) * config.SerpentApproachSpeed * 0.2;
        }
        else
        {
            entity.Pos.Motion.X = 0;
            entity.Pos.Motion.Z = 0;
        }
        entity.Pos.Motion.Y = 0;

        if (stateTimer >= 2.5f)
        {
            TransitionTo(SerpentState.Stalking);
        }
    }

    // ═══════════════════════════════════════════════════════════════════
    //  Stalking — large arc orbit, DEEP below surface.
    //  Slowly spirals inward over minutes before attacking.
    // ═══════════════════════════════════════════════════════════════════
    private void OnStalking(float deltaTime)
    {
        if (targetPlayer?.Entity == null) return;

        bool playerMounted = targetPlayer.Entity.MountedOn != null;

        float radius;
        if (useSpiralApproach)
        {
            // Freeze spiral when player is mounted — serpent keeps
            // circling at current orbit rather than closing in.
            if (!playerMounted)
            {
                radiusTransitionTime += deltaTime;
            }
            float t = Math.Min(1f, radiusTransitionTime / radiusTransitionDuration);
            radius = orbitRadiusStart + (orbitRadiusEnd - orbitRadiusStart) * t;

            if (!playerMounted && t >= 1f)
            {
                orbitRadiusStart = orbitRadiusEnd;
                if (orbitRadiusStart <= config.DeepSerpentOrbitRadius)
                {
                    if (config.DebugLogging)
                        UnderwaterHorrorsModSystem.DebugLog(entity.Api,
                            "DeepSerpent spiral complete, attacking");
                    TransitionTo(SerpentState.Attacking);
                    return;
                }
                SetNextSpiralStep();
            }
        }
        else
        {
            radius = config.DeepSerpentOrbitRadius;
        }

        // Slower orbital angular speed (radius is much larger so linear
        // speed stays reasonable, but we cap it a bit for leisure).
        float effectiveOrbitSpeed = config.SerpentOrbitSpeed * config.DeepSerpentOrbitRadius / radius;
        orbitAngle += effectiveOrbitSpeed * deltaTime;

        double targetX = targetPlayer.Entity.Pos.X + Math.Cos(orbitAngle) * radius;
        double targetZ = targetPlayer.Entity.Pos.Z + Math.Sin(orbitAngle) * radius;

        // Deep normally stays 10-30 blocks below the player.  Two cases
        // bring it to the surface:
        //   1. Player mounted — rises to just below boat, strongly.
        //   2. Surface-peek step (rare roll) — briefly shows itself.
        double targetY;
        double vMax, vSlew;
        if (playerMounted || currentStepAtSurface)
        {
            double pX = targetPlayer.Entity.Pos.X;
            double pY = targetPlayer.Entity.Pos.Y;
            double pZ = targetPlayer.Entity.Pos.Z;
            int waterY = FindWaterSurfaceYBelow(pX, pY, pZ, targetPlayer.Entity.Pos.Dimension);
            targetY = waterY - config.SerpentSurfaceSubmergeDepth;

            // Boost vertical budget so the rise from depth is visible
            // within a reasonable window (~15 s for 30 blocks).
            vMax = config.DeepSerpentMaxVerticalSpeed * 6f;
            vSlew = config.DeepSerpentVerticalSlewPerSec * 6f;
        }
        else
        {
            targetY = targetPlayer.Entity.Pos.Y - stalkDepth;
            vMax = config.DeepSerpentMaxVerticalSpeed;
            vSlew = config.DeepSerpentVerticalSlewPerSec;
        }

        MoveTowardDamped(targetX, targetY, targetZ,
            config.SerpentApproachSpeed * 2,
            vMax,
            vSlew,
            deltaTime);

        // Proximity aggro disabled when mounted.
        if (!playerMounted)
        {
            double headDistNow = HeadDistToPlayer();
            if (headDistNow < config.SerpentProximityHeadTriggerRange)
            {
                if (config.DebugLogging)
                    UnderwaterHorrorsModSystem.DebugLog(entity.Api,
                        $"DeepSerpent: player in head range ({headDistNow:F1}), aggroing");
                TransitionTo(SerpentState.Attacking);
                return;
            }

            double bodyDx = targetPlayer.Entity.Pos.X - entity.Pos.X;
            double bodyDy = targetPlayer.Entity.Pos.Y - entity.Pos.Y;
            double bodyDz = targetPlayer.Entity.Pos.Z - entity.Pos.Z;
            double bodyDist = Math.Sqrt(bodyDx * bodyDx + bodyDy * bodyDy + bodyDz * bodyDz);
            if (bodyDist < config.SerpentProximityBodyTriggerRange)
            {
                proximityBodyDwellTimer += deltaTime;
                if (proximityBodyDwellTimer >= proximityBodyDwellThreshold)
                {
                    if (config.DebugLogging)
                        UnderwaterHorrorsModSystem.DebugLog(entity.Api,
                            $"DeepSerpent: player dwelled {proximityBodyDwellTimer:F1}s in body range, aggroing");
                    TransitionTo(SerpentState.Attacking);
                    return;
                }
            }
            else
            {
                proximityBodyDwellTimer = 0;
            }
        }
        else
        {
            proximityBodyDwellTimer = 0;
        }
    }

    // ═══════════════════════════════════════════════════════════════════
    //  Attacking — rise to player while charging.
    //  Charge → windup → guaranteed-damage strike.
    // ═══════════════════════════════════════════════════════════════════
    private void OnAttacking(float deltaTime)
    {
        if (targetPlayer?.Entity == null) return;

        if (targetPlayer.Entity.MountedOn != null)
        {
            TransitionTo(SerpentState.Stalking);
            return;
        }

        double px = targetPlayer.Entity.Pos.X;
        double py = targetPlayer.Entity.Pos.Y;
        double pz = targetPlayer.Entity.Pos.Z;

        if (!isWindingUp && !isStriking)
        {
            // ── Charge phase: rise toward player while moving to strike range ──
            double adx = px - entity.Pos.X;
            double adz = pz - entity.Pos.Z;
            double aDist = Math.Sqrt(adx * adx + adz * adz);

            if (aDist > 0.1)
            {
                double offsetX = px - (adx / aDist) * HeadForwardOffset;
                double offsetZ = pz - (adz / aDist) * HeadForwardOffset;
                // Target player's Y directly — rising from depth.
                // Attack phase uses much higher vertical budget so the
                // serpent can actually reach the surface to strike.
                MoveTowardDamped(offsetX, py, offsetZ,
                    config.SerpentAttackSpeed,
                    config.SerpentAttackSpeed,              // full vertical during attack
                    config.DeepSerpentVerticalSlewPerSec * 4,
                    deltaTime);
            }
            else
            {
                MoveTowardDamped(px, py, pz,
                    config.SerpentAttackSpeed,
                    config.SerpentAttackSpeed,
                    config.DeepSerpentVerticalSlewPerSec * 4,
                    deltaTime);
            }

            attackCooldownTimer -= deltaTime;
            double headDist = HeadDistToPlayer();

            if (headDist < HeadAttackTriggerRange && attackCooldownTimer <= 0)
            {
                isWindingUp = true;
                // Keep facing the player through windup AND strike.
                attackAnimTimer = 0;
                strikeDamageDealt = false;
                attackFromRight = !attackFromRight;
                ForcePlayAnimation(attackFromRight ? AnimWindupRight : AnimWindupLeft);
                if (config.DebugLogging)
                    UnderwaterHorrorsModSystem.DebugLog(entity.Api,
                        $"DeepSerpent: winding up ({(attackFromRight ? "right" : "left")}), " +
                        $"headDist={headDist:F1}");
            }
        }
        else if (isWindingUp)
        {
            entity.Pos.Motion.X = 0;
            entity.Pos.Motion.Y = 0;
            entity.Pos.Motion.Z = 0;

            attackAnimTimer += deltaTime;
            if (attackAnimTimer >= WindupDuration)
            {
                isWindingUp = false;
                isStriking = true;
                attackAnimTimer = 0;
                ForcePlayAnimation(AnimAttack);
            }
        }
        else if (isStriking)
        {
            attackAnimTimer += deltaTime;

            if (!strikeDamageDealt && attackAnimTimer >= StrikeDamageTime)
            {
                strikeDamageDealt = true;

                targetPlayer.Entity.ReceiveDamage(new DamageSource
                {
                    Source = EnumDamageSource.Entity,
                    SourceEntity = entity,
                    Type = EnumDamageType.PiercingAttack,
                    DamageTier = config.SerpentDamageTier
                }, config.SerpentAttackDamage);

                if (config.DebugLogging)
                    UnderwaterHorrorsModSystem.DebugLog(entity.Api,
                        $"DeepSerpent hit {targetPlayer.PlayerName} for {config.SerpentAttackDamage} dmg");
            }

            if (attackAnimTimer >= StrikeDuration)
            {
                isStriking = false;
                attackCooldownTimer = config.SerpentAttackCooldown;
                PlayAnimation(AnimSlowSwim);

                // After striking, go back to deep stalking (spiral out)
                TransitionTo(SerpentState.Stalking);
            }
        }
    }

    // ═══════════════════════════════════════════════════════════════════
    //  Retreating — back to spawn, then despawn.
    // ═══════════════════════════════════════════════════════════════════
    private void OnRetreating(float deltaTime)
    {
        UpdateShallowWaterCheck(deltaTime);
        // Boredom-committed retreats see it through; don't revert.
        if (!committedRetreat &&
            targetPlayer?.Entity != null && targetPlayer.Entity.Alive &&
            targetPlayer.Entity.MountedOn == null && !IsInShallowWater)
        {
            TransitionTo(SerpentState.Stalking);
            return;
        }

        double dx = spawnX - entity.Pos.X;
        double dy = spawnY - entity.Pos.Y;
        double dz = spawnZ - entity.Pos.Z;
        double dist = Math.Sqrt(dx * dx + dy * dy + dz * dz);

        if (dist > 3.0)
        {
            MoveToward(spawnX, spawnY, spawnZ, config.SerpentApproachSpeed * 2);
        }
        else
        {
            entity.Die(EnumDespawnReason.Expire);
            return;
        }

        if (stateTimer >= 30f)
        {
            entity.Die(EnumDespawnReason.Expire);
        }
    }

    public override string PropertyName() => "underwaterhorrors:deepserpentai";
}
