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

    // ── Surface point (for initial hiss) ──────────────────────────────
    private double surfaceX, surfaceZ;
    private bool surfacePointPicked;
    private const float SurfaceDistMin = 10f;
    private const float SurfaceDistMax = 30f;

    // ── Spawn position for retreat ─────────────────────────────────────
    private double spawnX, spawnY, spawnZ;
    private bool spawnRecorded;

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
        float yaw = entity.ServerPos.Yaw;
        hx = entity.ServerPos.X + Math.Sin(yaw) * HeadForwardOffset;
        hy = entity.ServerPos.Y;
        hz = entity.ServerPos.Z + Math.Cos(yaw) * HeadForwardOffset;
    }

    private double HeadDistToPlayer()
    {
        if (targetPlayer?.Entity == null) return double.MaxValue;
        GetHeadPosition(out double hx, out double hy, out double hz);
        double dx = hx - targetPlayer.Entity.SidedPos.X;
        double dy = hy - targetPlayer.Entity.SidedPos.Y;
        double dz = hz - targetPlayer.Entity.SidedPos.Z;
        return Math.Sqrt(dx * dx + dy * dy + dz * dz);
    }

    // ═══════════════════════════════════════════════════════════════════
    //  Main tick
    // ═══════════════════════════════════════════════════════════════════
    public override void OnGameTick(float deltaTime)
    {
        if (!entity.Alive) return;

        // HORIZONTAL LOCK — zeroes Pitch/Roll/HeadPitch.  Skipped
        // during attack phases so the mouth can aim at the player.
        // HorizontalLockRenderer (client-side) performs the same check.
        if (!IsInAttackPhase)
        {
            ForceHorizontal(entity.ServerPos);
            ForceHorizontal(entity.Pos);
        }

        if (entity.Api.Side != EnumAppSide.Server) return;

        // Record spawn position on first tick
        if (!spawnRecorded)
        {
            spawnRecorded = true;
            spawnX = entity.ServerPos.X;
            spawnY = entity.ServerPos.Y;
            spawnZ = entity.ServerPos.Z;
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

        // When mounted: circle indefinitely at the surface.  No boredom
        // retreat — serpent orbits until player dismounts.

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
        surfaceX = targetPlayer.Entity.SidedPos.X + Math.Cos(angle) * dist;
        surfaceZ = targetPlayer.Entity.SidedPos.Z + Math.Sin(angle) * dist;
    }

    // ═══════════════════════════════════════════════════════════════════
    //  Facing — minimal pitch (fraction of a degree, very slow interp)
    // ═══════════════════════════════════════════════════════════════════
    private void UpdateFacing(float deltaTime)
    {
        double mx = entity.ServerPos.Motion.X;
        double mz = entity.ServerPos.Motion.Z;
        double my = entity.ServerPos.Motion.Y;
        double horizSpeedSq = mx * mx + mz * mz;

        if (!lockFacing)
        {
            float targetYaw;
            if (faceTarget && targetPlayer?.Entity != null)
            {
                double dx = targetPlayer.Entity.SidedPos.X - entity.ServerPos.X;
                double dz = targetPlayer.Entity.SidedPos.Z - entity.ServerPos.Z;
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

            entity.ServerPos.Yaw = smoothedYaw;
        }

        // Pitch:
        //   Stalking: hard-locked to 0 by ForceHorizontal (top of tick)
        //     AND by HorizontalLockRenderer (every render frame).
        //   Attack phases: aim directly at the player so the head can
        //     tilt up/down to strike a target above or below.
        if (IsInAttackPhase && targetPlayer?.Entity != null && !lockFacing)
        {
            double tdx = targetPlayer.Entity.SidedPos.X - entity.ServerPos.X;
            double tdy = targetPlayer.Entity.SidedPos.Y - entity.ServerPos.Y;
            double tdz = targetPlayer.Entity.SidedPos.Z - entity.ServerPos.Z;
            double horizToTarget = Math.Sqrt(tdx * tdx + tdz * tdz);
            float targetPitch = -(float)Math.Atan2(tdy, Math.Max(horizToTarget, 0.001));
            targetPitch = GameMath.Clamp(targetPitch, -1.0f, 1.0f);  // ~57° max
            entity.ServerPos.Pitch += (targetPitch - entity.ServerPos.Pitch) *
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
                lastCommandedMotionY = entity.ServerPos.Motion.Y;
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
                lastCommandedMotionY = entity.ServerPos.Motion.Y;
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
    }

    // ═══════════════════════════════════════════════════════════════════
    //  Rising — come up for the initial hiss.  Same as regular serpent.
    // ═══════════════════════════════════════════════════════════════════
    private void OnRising(float deltaTime)
    {
        if (targetPlayer?.Entity == null) return;

        double targetY = targetPlayer.Entity.SidedPos.Y;
        entity.ServerPos.Motion.Y = config.SerpentRiseSpeed;

        double dx = surfaceX - entity.ServerPos.X;
        double dz = surfaceZ - entity.ServerPos.Z;
        double horizDist = Math.Sqrt(dx * dx + dz * dz);

        if (horizDist > 1)
        {
            entity.ServerPos.Motion.X = (dx / horizDist) * config.SerpentApproachSpeed;
            entity.ServerPos.Motion.Z = (dz / horizDist) * config.SerpentApproachSpeed;
        }

        if (entity.ServerPos.Y >= targetY - 2)
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

        double dx = targetPlayer.Entity.SidedPos.X - entity.ServerPos.X;
        double dz = targetPlayer.Entity.SidedPos.Z - entity.ServerPos.Z;
        double dist = Math.Sqrt(dx * dx + dz * dz);

        if (dist > 0.5)
        {
            entity.ServerPos.Motion.X = (dx / dist) * config.SerpentApproachSpeed * 0.2;
            entity.ServerPos.Motion.Z = (dz / dist) * config.SerpentApproachSpeed * 0.2;
        }
        else
        {
            entity.ServerPos.Motion.X = 0;
            entity.ServerPos.Motion.Z = 0;
        }
        entity.ServerPos.Motion.Y = 0;

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

        double targetX = targetPlayer.Entity.SidedPos.X + Math.Cos(orbitAngle) * radius;
        double targetZ = targetPlayer.Entity.SidedPos.Z + Math.Sin(orbitAngle) * radius;

        // Deep normally stays 10-30 blocks below the player, BUT when
        // the player is mounted (on a boat at the surface) the deep
        // serpent rises to just below the water surface to show itself.
        double targetY;
        double vMax, vSlew;
        if (playerMounted)
        {
            // Use actual water-surface Y — the boat is ~1-2 blocks
            // above water, so playerY - offset would leave the body
            // partly rendered above the waterline.
            double pX = targetPlayer.Entity.SidedPos.X;
            double pY = targetPlayer.Entity.SidedPos.Y;
            double pZ = targetPlayer.Entity.SidedPos.Z;
            int waterY = FindWaterSurfaceYBelow(pX, pY, pZ, targetPlayer.Entity.SidedPos.Dimension);
            targetY = waterY - config.SerpentSurfaceSubmergeDepth;

            // Boost the vertical speed while surfacing so the rise
            // from 30 blocks deep takes ~15 seconds instead of 2
            // minutes.  Still damped, so no whip — just more eager.
            vMax = config.DeepSerpentMaxVerticalSpeed * 6f;
            vSlew = config.DeepSerpentVerticalSlewPerSec * 6f;
        }
        else
        {
            targetY = targetPlayer.Entity.SidedPos.Y - stalkDepth;
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

            double bodyDx = targetPlayer.Entity.SidedPos.X - entity.ServerPos.X;
            double bodyDy = targetPlayer.Entity.SidedPos.Y - entity.ServerPos.Y;
            double bodyDz = targetPlayer.Entity.SidedPos.Z - entity.ServerPos.Z;
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

        double px = targetPlayer.Entity.SidedPos.X;
        double py = targetPlayer.Entity.SidedPos.Y;
        double pz = targetPlayer.Entity.SidedPos.Z;

        if (!isWindingUp && !isStriking)
        {
            // ── Charge phase: rise toward player while moving to strike range ──
            double adx = px - entity.ServerPos.X;
            double adz = pz - entity.ServerPos.Z;
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
            entity.ServerPos.Motion.X = 0;
            entity.ServerPos.Motion.Y = 0;
            entity.ServerPos.Motion.Z = 0;

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
        if (targetPlayer?.Entity != null && targetPlayer.Entity.Alive &&
            targetPlayer.Entity.MountedOn == null && !IsInShallowWater)
        {
            TransitionTo(SerpentState.Stalking);
            return;
        }

        double dx = spawnX - entity.ServerPos.X;
        double dy = spawnY - entity.ServerPos.Y;
        double dz = spawnZ - entity.ServerPos.Z;
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
