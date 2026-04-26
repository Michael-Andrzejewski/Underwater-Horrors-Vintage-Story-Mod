using System;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;

namespace UnderwaterHorrors;

public enum SerpentState
{
    Rising,
    Surfacing,
    Stalking,
    Attacking,
    Retreating
}

public class EntityBehaviorSerpentAI : EntityBehaviorOceanCreature
{
    private SerpentState state = SerpentState.Rising;
    private float stateTimer;
    private float stalkDuration;
    private float orbitAngle;
    private float attackCooldownTimer;

    // ── Animation codes (match the shape file) ─────────────────────────
    private const string AnimSwim = "swim";
    private const string AnimSlither = "slither";
    private const string AnimHiss = "hiss";
    private const string AnimStandAndHiss = "standandhiss";
    private const string AnimWindupRight = "windupattackright";
    private const string AnimWindupLeft = "windupattackleft";
    private const string AnimAttack = "attack1";

    private string currentAnim;

    // ── Attack ──────────────────────────────────────────────────────────
    // windupattack: 39 frames ≈ 1.3 s at 30 fps
    // attack1: 15 frames ≈ 0.5 s — damage dealt midway through
    private const float WindupDuration = 1.3f;
    private const float StrikeDuration = 0.5f;
    private const float StrikeDamageTime = 0.25f;

    private bool isWindingUp;
    private bool isStriking;
    private float attackAnimTimer;
    private bool strikeDamageDealt;
    private bool attackFromRight;

    // How close the HEAD needs to be to the player to start a windup.
    // Once triggered, damage is guaranteed — the proximity check IS
    // the hit check.
    private const float WindupTriggerRange = 4.0f;

    // Approximate head offset used only during the CHARGE phase to keep
    // the body behind the player so the head arrives first.
    private const float HeadForwardOffset = 9.0f;

    // Per-spiral-step flag: when true, this orbit rises to surface
    // depth (fin-above-waves effect).  When false, stays at the normal
    // (deeper) cruise depth.  Rolled in SetNextSpiralStep.
    private bool currentStepAtSurface;

    // ── Head position (computed from entity yaw + forward offset) ──────
    // The head trigger range: how close the head must be to the player
    // before an attack animation fires.  Once triggered, damage is
    // guaranteed — the proximity check IS the hit check.
    private const float HeadAttackTriggerRange = 4.0f;

    // ── Surfacing spot ─────────────────────────────────────────────────
    private double surfaceX, surfaceZ;
    private bool surfacePointPicked;
    private const float SurfaceDistMin = 10f;
    private const float SurfaceDistMax = 30f;

    // ── Facing direction ───────────────────────────────────────────────
    private const float ModelYawOffset = 0f;
    private float smoothedYaw;
    private bool yawInitialized;

    // When true, UpdateFacing freezes yaw so the serpent doesn't jerk
    // around during windup/strike.
    private bool lockFacing;

    // When true, UpdateFacing aims the yaw at the target player instead
    // of deriving it from the motion vector.  Used during the attack
    // charge so the head points at the player, not along the orbit tangent.
    private bool faceTarget;

    // ── Spiral approach fields ─────────────────────────────────────────
    private bool useSpiralApproach;
    private float orbitRadiusStart;
    private float orbitRadiusEnd;
    private float radiusTransitionTime;
    private float radiusTransitionDuration;

    // ── Spawn position for retreat ─────────────────────────────────────
    private double spawnX, spawnY, spawnZ;
    private bool spawnRecorded;

    // ── Boat boredom ──────────────────────────────────────────────────
    // After BoatBoredomGraceSeconds mounted, periodically roll to give
    // up and retreat.  The ModSystem spawn loop can then spawn a
    // fresh creature to replace this one.
    private float mountedCircleTimer;
    private float mountedCheckTimer;
    // Set when a retreat was triggered by boat boredom.  Prevents the
    // OnRetreating "resume stalking if player dismounts" shortcut, so
    // briefly dismounting can't cancel the retreat — the serpent fully
    // commits to leaving.
    private bool committedRetreat;

    // ── Proximity-based aggro ─────────────────────────────────────────
    // Player within head range → immediate aggro.
    // Player within body range for a randomized dwell → aggro.
    private float proximityBodyDwellTimer;
    private float proximityBodyDwellThreshold;

    // ── Debug animation mode ───────────────────────────────────────────
    private string debugAnimName;
    private float debugAnimTimer;
    private const float DebugAnimInterval = 5f;
    public static float DebugAnimIntervalPublic => DebugAnimInterval;

    public EntityBehaviorSerpentAI(Entity entity) : base(entity) { }

    public override void Initialize(EntityProperties properties, JsonObject attributes)
    {
        base.Initialize(properties, attributes);
        orbitAngle = (float)(entity.World.Rand.NextDouble() * Math.PI * 2);

        if (entity.Api.Side == EnumAppSide.Server)
        {
            PlayAnimation(AnimSwim);
        }
    }

    // ═══════════════════════════════════════════════════════════════════
    //  Head position — computed from entity position + yaw * offset
    //  Used for attack triggering and spectral debug rendering.
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
    //  Debug animation control (called from /uh serpent anim command)
    // ═══════════════════════════════════════════════════════════════════
    public void SetDebugAnimation(string animName)
    {
        if (animName == null || animName == "off")
        {
            debugAnimName = null;
            entity.AnimManager.StopAllAnimations();
            currentAnim = null;
            TransitionTo(state);
            return;
        }
        debugAnimName = animName;
        debugAnimTimer = 0;

        // Stop everything, then start the requested animation fresh
        entity.AnimManager.StopAllAnimations();
        currentAnim = animName;
        entity.AnimManager.StartAnimation(new AnimationMetaData
        {
            Animation = animName,
            Code = animName,
            AnimationSpeed = 1f,
            EaseInSpeed = 999f,
            BlendMode = EnumAnimationBlendMode.Average
        }.Init());
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
        if (entity.Api.Side != EnumAppSide.Server) return;

        // ── Debug animation mode: freeze in place, replay anim ──
        if (debugAnimName != null)
        {
            entity.Pos.Motion.X = 0;
            entity.Pos.Motion.Y = 0;
            entity.Pos.Motion.Z = 0;

            debugAnimTimer += deltaTime;
            if (debugAnimTimer >= DebugAnimInterval)
            {
                debugAnimTimer = 0;
                // ResetAnimation replays a Hold animation from frame 0
                // without needing a stop/start cycle
                entity.AnimManager.ResetAnimation(debugAnimName);
            }
            return;
        }

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

        // Pick a surfacing spot once we have a target
        if (!surfacePointPicked && targetPlayer?.Entity != null)
        {
            PickSurfacePoint();
        }

        // Throttled shallow water check
        if (state != SerpentState.Rising &&
            state != SerpentState.Surfacing &&
            state != SerpentState.Retreating)
        {
            UpdateShallowWaterCheck(deltaTime);

            if (IsInShallowWater)
            {
                if (config.DebugLogging)
                    UnderwaterHorrorsModSystem.DebugLog(entity.Api,
                        "Serpent: player in shallow water, retreating to spawn");
                TransitionTo(SerpentState.Retreating);
            }
        }

        // Boat boredom: after ~2 min of the player being mounted, roll
        // every 30 s to retreat.  A replacement may spawn via the main
        // spawn loop (mounted spawns are no longer blocked).
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
                                    $"Serpent bored after {mountedCircleTimer:F0}s mounted, retreating");
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
            case SerpentState.Attacking: OnAttacking(deltaTime);  break;
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

        if (config.DebugLogging)
            UnderwaterHorrorsModSystem.DebugLog(entity.Api,
                $"Serpent surface point: ({surfaceX:F0}, {surfaceZ:F0}), " +
                $"{dist:F0} blocks from player at angle {angle * 180 / Math.PI:F0}°");
    }

    // ═══════════════════════════════════════════════════════════════════
    //  Facing — head points toward movement, locked during attacks
    // ═══════════════════════════════════════════════════════════════════
    private void UpdateFacing(float deltaTime)
    {
        double mx = entity.Pos.Motion.X;
        double mz = entity.Pos.Motion.Z;
        double my = entity.Pos.Motion.Y;
        double horizSpeedSq = mx * mx + mz * mz;

        // Yaw — skip update when locked (during windup/strike)
        if (!lockFacing)
        {
            float targetYaw;

            if (faceTarget && targetPlayer?.Entity != null)
            {
                // During attack charge: aim directly at the player
                double dx = targetPlayer.Entity.Pos.X - entity.Pos.X;
                double dz = targetPlayer.Entity.Pos.Z - entity.Pos.Z;
                targetYaw = (float)Math.Atan2(dx, dz) + ModelYawOffset;
            }
            else if (horizSpeedSq > 0.00001)
            {
                // Normal: derive yaw from motion direction
                targetYaw = (float)Math.Atan2(mx, mz) + ModelYawOffset;
            }
            else
            {
                // No motion and not targeting — keep current yaw
                targetYaw = smoothedYaw;
            }

            if (!yawInitialized)
            {
                smoothedYaw = targetYaw;
                yawInitialized = true;
            }
            else
            {
                // Faster turn rate when facing target (attack charge)
                float turnRate = faceTarget ? 8f : 5f;
                float diff = GameMath.AngleRadDistance(smoothedYaw, targetYaw);
                smoothedYaw += diff * Math.Min(1f, deltaTime * turnRate);
            }

            entity.Pos.Yaw = smoothedYaw;
        }

        // Pitch
        //   Normal stalk: clamped to ~10° so the body stays near-
        //     horizontal and the Sea-habitat step-pitch hack doesn't
        //     amplify small motion-derived tilts into visible wobble.
        //   Attack phases: unclamped up to ~60° so the head can aim
        //     directly at the player even when the player is above or
        //     below the serpent.  When faceTarget is true, we point the
        //     pitch directly at the player's eye line.
        bool inAttack = faceTarget || isWindingUp || isStriking;
        float maxPitchRad = inAttack ? 1.0f : 0.17f;  // ~57° during attack
        double horizSpeed = Math.Sqrt(horizSpeedSq);
        float targetPitch = 0f;

        if (!lockFacing)
        {
            if (inAttack && targetPlayer?.Entity != null)
            {
                // Aim pitch directly at the player (mouth-to-target).
                double tdx = targetPlayer.Entity.Pos.X - entity.Pos.X;
                double tdy = targetPlayer.Entity.Pos.Y - entity.Pos.Y;
                double tdz = targetPlayer.Entity.Pos.Z - entity.Pos.Z;
                double horizToTarget = Math.Sqrt(tdx * tdx + tdz * tdz);
                targetPitch = -(float)Math.Atan2(tdy, Math.Max(horizToTarget, 0.001));
                targetPitch = GameMath.Clamp(targetPitch, -maxPitchRad, maxPitchRad);
            }
            else if (horizSpeed > 0.001 || Math.Abs(my) > 0.001)
            {
                targetPitch = -(float)Math.Atan2(my, Math.Max(horizSpeed, 0.001));
                targetPitch = GameMath.Clamp(targetPitch, -maxPitchRad, maxPitchRad);
            }
        }

        // Faster interpolation during attack so the head snaps to
        // the player in time for the strike.
        float pitchLerpRate = inAttack ? 6f : 2f;
        entity.Pos.Pitch += (targetPitch - entity.Pos.Pitch) *
            Math.Min(1f, deltaTime * pitchLerpRate);

        // Sink boost is useful for the regular serpent pitching down
        // to dive, but skip during attack so we don't fight the
        // aimed-at-player pitch.
        if (!inAttack && entity.Pos.Pitch > 0.02f)
        {
            float sinkBoost = entity.Pos.Pitch * 3f;
            entity.Pos.Motion.Y -= sinkBoost * deltaTime;
        }
    }

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

    /// <summary>
    /// Force-restart an animation even if it is already the current one.
    /// Uses ResetAnimation for Hold animations that are frozen on the last frame.
    /// </summary>
    private void ForcePlayAnimation(string code, float speed = 1f)
    {
        if (currentAnim == code && entity.AnimManager.IsAnimationActive(code))
        {
            // Same animation already active (possibly frozen on last Hold frame)
            // — reset it back to frame 0 instead of stop/start
            entity.AnimManager.ResetAnimation(code);
            return;
        }

        // Different animation — stop old, start new
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
                $"Serpent state: {oldState} → {newState} (target: {playerName})");
        }

        switch (newState)
        {
            case SerpentState.Rising:
                PlayAnimation(AnimSwim);
                break;

            case SerpentState.Surfacing:
                bool onBoat = targetPlayer?.Entity?.MountedOn != null;
                PlayAnimation(onBoat ? AnimStandAndHiss : AnimHiss);
                if (config.DebugLogging)
                    UnderwaterHorrorsModSystem.DebugLog(entity.Api,
                        $"Serpent surfacing: {(onBoat ? "standing hiss (boat)" : "hiss")}");
                break;

            case SerpentState.Stalking:
                PlayAnimation(AnimSlither);
                // Roll a random dwell threshold for body-proximity aggro.
                proximityBodyDwellTimer = 0;
                proximityBodyDwellThreshold =
                    config.SerpentProximityBodyDwellMin +
                    (float)(entity.World.Rand.NextDouble() *
                        (config.SerpentProximityBodyDwellMax -
                         config.SerpentProximityBodyDwellMin));
                // Seed slew limiter so we don't ramp from old Motion.Y.
                lastCommandedMotionY = entity.Pos.Motion.Y;
                break;

            case SerpentState.Attacking:
                PlayAnimation(AnimSlither);
                faceTarget = true;  // Turn toward the player during charge
                lastCommandedMotionY = entity.Pos.Motion.Y;
                break;

            case SerpentState.Retreating:
                PlayAnimation(AnimSwim);
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
    //  Spiral helpers
    // ═══════════════════════════════════════════════════════════════════
    private void SetupSpiralApproach(bool fullApproach)
    {
        var rand = entity.World.Rand;

        if (fullApproach)
        {
            orbitRadiusStart = config.SerpentInitialOrbitRadiusMin +
                (float)(rand.NextDouble() *
                    (config.SerpentInitialOrbitRadiusMax - config.SerpentInitialOrbitRadiusMin));
        }
        else
        {
            orbitRadiusStart = config.SerpentOrbitRadius * (2f + (float)rand.NextDouble());
        }

        SetNextSpiralStep();

        if (config.DebugLogging)
            UnderwaterHorrorsModSystem.DebugLog(entity.Api,
                $"Serpent spiral: start radius {orbitRadiusStart:F1}, " +
                $"first target {orbitRadiusEnd:F1} over {radiusTransitionDuration:F1}s");
    }

    private void SetNextSpiralStep()
    {
        var rand = entity.World.Rand;
        float reduction = config.SerpentSpiralReductionMin +
            (float)(rand.NextDouble() *
                (config.SerpentSpiralReductionMax - config.SerpentSpiralReductionMin));
        orbitRadiusEnd = Math.Max(config.SerpentOrbitRadius, orbitRadiusStart - reduction);
        radiusTransitionDuration = config.SerpentSpiralStepDurationMin +
            (float)(rand.NextDouble() *
                (config.SerpentSpiralStepDurationMax - config.SerpentSpiralStepDurationMin));
        radiusTransitionTime = 0;

        // Roll whether this step rises to the surface (fin-above-waves).
        currentStepAtSurface = rand.NextDouble() < config.SerpentSurfacePeekChance;
        if (config.DebugLogging && currentStepAtSurface)
            UnderwaterHorrorsModSystem.DebugLog(entity.Api,
                $"Serpent: surface-peek step ({radiusTransitionDuration:F0}s)");
    }

    // ═══════════════════════════════════════════════════════════════════
    //  State: Rising
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
    //  State: Surfacing
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
    //  State: Stalking
    // ═══════════════════════════════════════════════════════════════════
    private void OnStalking(float deltaTime)
    {
        if (targetPlayer?.Entity == null) return;

        bool playerMounted = targetPlayer.Entity.MountedOn != null;

        float radius;
        if (useSpiralApproach)
        {
            // When mounted, freeze the spiral at its current radius
            // instead of tightening toward attack range.
            if (!playerMounted)
            {
                radiusTransitionTime += deltaTime;
            }
            float t = Math.Min(1f, radiusTransitionTime / radiusTransitionDuration);
            radius = orbitRadiusStart + (orbitRadiusEnd - orbitRadiusStart) * t;

            if (!playerMounted && t >= 1f)
            {
                orbitRadiusStart = orbitRadiusEnd;
                if (orbitRadiusStart <= config.SerpentOrbitRadius)
                {
                    if (config.DebugLogging)
                        UnderwaterHorrorsModSystem.DebugLog(entity.Api,
                            "Serpent spiral complete, attacking");
                    TransitionTo(SerpentState.Attacking);
                    return;
                }
                SetNextSpiralStep();
                if (config.DebugLogging)
                    UnderwaterHorrorsModSystem.DebugLog(entity.Api,
                        $"Serpent spiral step: {orbitRadiusStart:F1} → " +
                        $"{orbitRadiusEnd:F1} over {radiusTransitionDuration:F1}s");
            }
        }
        else
        {
            radius = config.SerpentOrbitRadius;
        }

        float effectiveOrbitSpeed = config.SerpentOrbitSpeed * config.SerpentOrbitRadius / radius;
        orbitAngle += effectiveOrbitSpeed * deltaTime;

        double targetX = targetPlayer.Entity.Pos.X + Math.Cos(orbitAngle) * radius;
        double targetZ = targetPlayer.Entity.Pos.Z + Math.Sin(orbitAngle) * radius;

        // Target Y is relative to the actual water surface so the body
        // is reliably submerged regardless of whether the player is
        // swimming or sitting on a boat.  Depth depends on state:
        //   Mounted  → always surface (show itself near the boat)
        //   SurfaceStep → surface peek (fin above waves)
        //   Otherwise → normal (deeper) cruise depth
        double pX = targetPlayer.Entity.Pos.X;
        double pY = targetPlayer.Entity.Pos.Y;
        double pZ = targetPlayer.Entity.Pos.Z;
        int waterY = FindWaterSurfaceYBelow(pX, pY, pZ, targetPlayer.Entity.Pos.Dimension);
        float depthBelowSurface = (playerMounted || currentStepAtSurface)
            ? config.SerpentSurfaceSubmergeDepth
            : config.SerpentNormalSubmergeDepth;
        double targetY = waterY - depthBelowSurface;

        // Damped approach: no bang-bang bob, vertical motion capped
        // separately for a smooth surface-level glide.
        MoveTowardDamped(targetX, targetY, targetZ,
            config.SerpentApproachSpeed * 2,
            config.SerpentMaxVerticalSpeed,
            config.SerpentVerticalSlewPerSec,
            deltaTime);

        // Proximity aggro and stalk-timeout attack transitions only
        // fire when the player is NOT mounted.  While mounted, the
        // serpent just circles harmlessly at the surface.
        if (!playerMounted)
        {
            // ── Proximity aggro ──
            double headDistNow = HeadDistToPlayer();
            if (headDistNow < config.SerpentProximityHeadTriggerRange)
            {
                if (config.DebugLogging)
                    UnderwaterHorrorsModSystem.DebugLog(entity.Api,
                        $"Serpent: player in head range ({headDistNow:F1}), aggroing");
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
                            $"Serpent: player dwelled {proximityBodyDwellTimer:F1}s in body range, aggroing");
                    TransitionTo(SerpentState.Attacking);
                    return;
                }
            }
            else
            {
                proximityBodyDwellTimer = 0;
            }

            if (!useSpiralApproach && stateTimer >= stalkDuration)
            {
                TransitionTo(SerpentState.Attacking);
            }
        }
        else
        {
            // Mounted: reset dwell timer so it doesn't accumulate in the
            // background and instantly trigger on dismount.
            proximityBodyDwellTimer = 0;
        }
    }

    // ═══════════════════════════════════════════════════════════════════
    //  State: Attacking – charge → windup → strike
    //
    //  Charge:  slither toward the player so the HEAD arrives first.
    //           The entity center targets a point HeadForwardOffset
    //           behind the player (along the entity→player line).
    //  Windup:  full stop, lock facing, play windup animation.
    //           Triggered when head is within HeadAttackTriggerRange.
    //  Strike:  play attack1, deal GUARANTEED damage (the proximity
    //           check that triggered the windup IS the hit check).
    // ═══════════════════════════════════════════════════════════════════
    private void OnAttacking(float deltaTime)
    {
        if (targetPlayer?.Entity == null) return;

        if (targetPlayer.Entity.MountedOn != null)
        {
            if (config.DebugLogging)
                UnderwaterHorrorsModSystem.DebugLog(entity.Api,
                    $"Serpent: {targetPlayer.PlayerName} is mounted, reverting to Stalking");
            TransitionTo(SerpentState.Stalking);
            return;
        }

        double px = targetPlayer.Entity.Pos.X;
        double py = targetPlayer.Entity.Pos.Y;
        double pz = targetPlayer.Entity.Pos.Z;

        if (!isWindingUp && !isStriking)
        {
            // ── Charge phase ──
            // Move entity center so the HEAD arrives at the player.
            // Target = player position offset BACK by HeadForwardOffset
            // along the entity→player direction.
            double adx = px - entity.Pos.X;
            double adz = pz - entity.Pos.Z;
            double aDist = Math.Sqrt(adx * adx + adz * adz);

            if (aDist > 0.1)
            {
                double offsetX = px - (adx / aDist) * HeadForwardOffset;
                double offsetZ = pz - (adz / aDist) * HeadForwardOffset;
                // Attack charge: high vertical budget so the head can
                // rise/descend to match the player even from depth.
                MoveTowardDamped(offsetX, py - config.SerpentSurfaceSubmergeDepth, offsetZ,
                    config.SerpentAttackSpeed,
                    config.SerpentAttackSpeed,
                    config.SerpentVerticalSlewPerSec * 4,
                    deltaTime);
            }
            else
            {
                MoveTowardDamped(px, py - config.SerpentSurfaceSubmergeDepth, pz,
                    config.SerpentAttackSpeed,
                    config.SerpentAttackSpeed,
                    config.SerpentVerticalSlewPerSec * 4,
                    deltaTime);
            }

            attackCooldownTimer -= deltaTime;

            // Check HEAD distance to player — this is the real trigger
            double headDist = HeadDistToPlayer();

            if (headDist < HeadAttackTriggerRange && attackCooldownTimer <= 0)
            {
                isWindingUp = true;
                // Keep facing the player through windup AND strike.
                // lockFacing stays false so UpdateFacing continues
                // tracking the player; faceTarget stays true.
                attackAnimTimer = 0;
                strikeDamageDealt = false;
                attackFromRight = !attackFromRight;
                ForcePlayAnimation(attackFromRight ? AnimWindupRight : AnimWindupLeft);
                if (config.DebugLogging)
                    UnderwaterHorrorsModSystem.DebugLog(entity.Api,
                        $"Serpent: winding up ({(attackFromRight ? "right" : "left")}), " +
                        $"headDist={headDist:F1}");
            }
        }
        else if (isWindingUp)
        {
            // ── Windup phase: full stop, facing locked ──
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
                if (config.DebugLogging)
                    UnderwaterHorrorsModSystem.DebugLog(entity.Api, "Serpent: striking!");
            }
        }
        else if (isStriking)
        {
            // ── Strike phase: guaranteed damage ──
            // The head was close enough to trigger the windup, so the
            // strike always connects. No further distance check.
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
                {
                    double headDist = HeadDistToPlayer();
                    UnderwaterHorrorsModSystem.DebugLog(entity.Api,
                        $"Serpent hit {targetPlayer.PlayerName} " +
                        $"for {config.SerpentAttackDamage} dmg (headDist: {headDist:F1})");
                }
            }

            if (attackAnimTimer >= StrikeDuration)
            {
                isStriking = false;
                attackCooldownTimer = config.SerpentAttackCooldown;
                PlayAnimation(AnimSlither);

                if (entity.World.Rand.NextDouble() < config.SerpentReStalkChance)
                {
                    var rand = entity.World.Rand;
                    stalkDuration = config.SerpentStalkDurationMin +
                        (float)(rand.NextDouble() *
                            (config.SerpentStalkDurationMax - config.SerpentStalkDurationMin));
                    if (config.DebugLogging)
                        UnderwaterHorrorsModSystem.DebugLog(entity.Api,
                            $"Serpent disengaging, re-stalk for {stalkDuration:F1}s");
                    TransitionTo(SerpentState.Stalking);
                }
            }
        }
    }

    // ═══════════════════════════════════════════════════════════════════
    //  State: Retreating
    // ═══════════════════════════════════════════════════════════════════
    private void OnRetreating(float deltaTime)
    {
        UpdateShallowWaterCheck(deltaTime);
        // Boredom-committed retreats don't revert — they see it through.
        if (!committedRetreat &&
            targetPlayer?.Entity != null && targetPlayer.Entity.Alive &&
            targetPlayer.Entity.MountedOn == null && !IsInShallowWater)
        {
            if (config.DebugLogging)
                UnderwaterHorrorsModSystem.DebugLog(entity.Api,
                    "Serpent: player back in deep water, resuming stalking");
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
            if (config.DebugLogging)
                UnderwaterHorrorsModSystem.DebugLog(entity.Api,
                    "Serpent reached spawn point, despawning");
            entity.Die(EnumDespawnReason.Expire);
            return;
        }

        if (stateTimer >= 30f)
        {
            if (config.DebugLogging)
                UnderwaterHorrorsModSystem.DebugLog(entity.Api,
                    "Serpent retreat timeout, despawning");
            entity.Die(EnumDespawnReason.Expire);
        }
    }

    public override string PropertyName() => "underwaterhorrors:serpentai";
}
