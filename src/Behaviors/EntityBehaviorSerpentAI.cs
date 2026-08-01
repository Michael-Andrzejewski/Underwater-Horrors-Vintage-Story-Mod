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
    // Server-tuning knob: scales all movement done through the base-class
    // movers; the direct Motion writes below multiply it in themselves.
    protected override double SpeedScale => config?.SerpentSpeedMultiplier ?? 1.0;

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

    /// <summary>
    /// True while charging, winding up, or striking.  Outside these
    /// phases the body is hard-locked level (ForceHorizontal), matching
    /// the deep serpent.
    /// </summary>
    public bool IsInAttackPhase => faceTarget || isWindingUp || isStriking;

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
    // After a random circling time (see UpdateBoatBoredom in the base) the
    // serpent gives up and retreats. The ModSystem spawn loop can then spawn
    // a fresh creature to replace this one.
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

    // ── Flee after damage ─────────────────────────────────────────────
    // While > 0, proximity aggro and stalk-timeout attacks are
    // suppressed so a fresh flee actually widens the distance instead
    // of instantly re-aggroing while the serpent is still close.
    private float fleeAggroSuppressTimer;

    // ── Enrage after provoke ──────────────────────────────────────────
    // While > 0, the serpent is committed: flee-from-damage rolls are
    // skipped and a finished strike never rolls the re-stalk disengage.
    private float enrageTimer;

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

        // HORIZONTAL LOCK — same treatment as the deep serpent: the long
        // body stays level except while an attack needs the mouth aimed
        // at the player. Kills the up/down flopping that motion-derived
        // pitch caused on the rust serpent.
        if (!IsInAttackPhase)
        {
            EntityBehaviorDeepSerpentAI.ForceHorizontal(entity.Pos);
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

        UpdateAmbientSound(deltaTime);

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
                UpdateBoatPhase(deltaTime);
                if (UpdateBoatBoredom(deltaTime))
                {
                    if (config.DebugLogging)
                        UnderwaterHorrorsModSystem.DebugLog(entity.Api,
                            $"Serpent bored after {BoatBoredomElapsed:F0}s mounted, retreating");
                    committedRetreat = true;
                    TransitionTo(SerpentState.Retreating);
                }
            }
            else
            {
                ResetBoatPhase();
                ResetBoatBoredom();
            }
        }

        stateTimer += deltaTime;
        if (fleeAggroSuppressTimer > 0) fleeAggroSuppressTimer -= deltaTime;
        if (enrageTimer > 0) enrageTimer -= deltaTime;

        switch (state)
        {
            case SerpentState.Rising:    OnRising(deltaTime);    break;
            case SerpentState.Surfacing: OnSurfacing(deltaTime); break;
            case SerpentState.Stalking:  OnStalking(deltaTime);  break;
            case SerpentState.Attacking: OnAttacking(deltaTime);  break;
            case SerpentState.Retreating:OnRetreating(deltaTime); break;
        }

        // After the state handlers, so the motion they just commanded is
        // what gets cancelled. Clamping first would let the same tick
        // command another dive straight back into the sea floor.
        ClampAboveSeaFloor(deltaTime);

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
                float step = diff * Math.Min(1f, deltaTime * turnRate);
                // Hard cap on angular speed. The exponential approach
                // above alone turns a 180-degree heading flip into a
                // whip-fast spin (diff * rate can exceed 900 deg/s);
                // capping the per-tick step makes big turns sweep at a
                // constant, snake-like rate instead.
                float maxStep = (faceTarget
                    ? config.SerpentAttackTurnSpeedDegPerSec
                    : config.SerpentTurnSpeedDegPerSec) * GameMath.DEG2RAD * deltaTime;
                smoothedYaw += GameMath.Clamp(step, -maxStep, maxStep);
            }

            entity.Pos.Yaw = smoothedYaw;
        }

        // Pitch:
        //   Outside attack phases the body is hard-locked level by
        //     ForceHorizontal at the top of the tick (same as the deep
        //     serpent), so no motion-derived pitch is computed here.
        //   Attack phases: aim directly at the player, up to ~57°, so
        //     the mouth can strike a target above or below the serpent.
        if (IsInAttackPhase && targetPlayer?.Entity != null && !lockFacing)
        {
            double tdx = targetPlayer.Entity.Pos.X - entity.Pos.X;
            double tdy = targetPlayer.Entity.Pos.Y - entity.Pos.Y;
            double tdz = targetPlayer.Entity.Pos.Z - entity.Pos.Z;
            double horizToTarget = Math.Sqrt(tdx * tdx + tdz * tdz);
            float targetPitch = -(float)Math.Atan2(tdy, Math.Max(horizToTarget, 0.001));
            targetPitch = GameMath.Clamp(targetPitch, -1.0f, 1.0f);
            entity.Pos.Pitch += (targetPitch - entity.Pos.Pitch) *
                Math.Min(1f, deltaTime * 6f);
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
                TriggerScreech();
                if (config.DebugLogging)
                    UnderwaterHorrorsModSystem.DebugLog(entity.Api,
                        $"Serpent surfacing: {(onBoat ? "standing hiss (boat)" : "hiss")}");
                break;

            case SerpentState.Stalking:
                PlayAnimation(AnimSlither);
                // Roll a random dwell threshold for body-proximity aggro.
                proximityBodyDwellTimer = 0;
                proximityBodyDwellThreshold = config.AggroTime(
                    config.SerpentProximityBodyDwellMin +
                    (float)(entity.World.Rand.NextDouble() *
                        (config.SerpentProximityBodyDwellMax -
                         config.SerpentProximityBodyDwellMin)));
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
                PlayDive();
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
        radiusTransitionDuration = config.AggroTime(
            config.SerpentSpiralStepDurationMin +
            (float)(rand.NextDouble() *
                (config.SerpentSpiralStepDurationMax - config.SerpentSpiralStepDurationMin)));
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

        entity.Pos.Motion.Y = config.SerpentRiseSpeed * SpeedScale;

        double dx = surfaceX - entity.Pos.X;
        double dz = surfaceZ - entity.Pos.Z;
        double horizDist = Math.Sqrt(dx * dx + dz * dz);

        if (horizDist > 1)
        {
            entity.Pos.Motion.X = (dx / horizDist) * config.SerpentApproachSpeed * SpeedScale;
            entity.Pos.Motion.Z = (dz / horizDist) * config.SerpentApproachSpeed * SpeedScale;
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
            entity.Pos.Motion.X = (dx / dist) * config.SerpentApproachSpeed * 0.2 * SpeedScale;
            entity.Pos.Motion.Z = (dz / dist) * config.SerpentApproachSpeed * 0.2 * SpeedScale;
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
                    if (TargetIsPassiveObserver)
                    {
                        // Creative observer: no strike at the end of the
                        // spiral. Swim back out and start a fresh wide
                        // approach, so the serpent keeps cruising in and
                        // out around the player indefinitely.
                        SetupSpiralApproach(true);
                    }
                    else
                    {
                        if (config.DebugLogging)
                            UnderwaterHorrorsModSystem.DebugLog(entity.Api,
                                "Serpent spiral complete, attacking");
                        TransitionTo(SerpentState.Attacking);
                        return;
                    }
                }
                else
                {
                    SetNextSpiralStep();
                    if (config.DebugLogging)
                        UnderwaterHorrorsModSystem.DebugLog(entity.Api,
                            $"Serpent spiral step: {orbitRadiusStart:F1} → " +
                            $"{orbitRadiusEnd:F1} over {radiusTransitionDuration:F1}s");
                }
            }
        }
        else
        {
            radius = config.SerpentOrbitRadius;
        }

        float effectiveOrbitSpeed = config.SerpentOrbitSpeed * config.SerpentOrbitRadius / radius * (float)SpeedScale;
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
        // While mounted, oscillate: surface during the surface phase, dive
        // during the submerged phase. While not mounted, surface only on a
        // surface-peek step.
        bool wantSurface = playerMounted ? !BoatSubmergedPhase : currentStepAtSurface;
        float depthBelowSurface = wantSurface
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
        // serpent just circles harmlessly at the surface.  Also skipped
        // right after a flee so the serpent commits to leaving, and for
        // creative/spectator observers when the ignore toggle is on.
        if (!playerMounted && fleeAggroSuppressTimer <= 0 && !TargetIsPassiveObserver)
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

        // Target is (or just became) a creative/spectator observer:
        // break off cleanly, even mid-windup or mid-strike.
        if (TargetIsPassiveObserver)
        {
            if (config.DebugLogging)
                UnderwaterHorrorsModSystem.DebugLog(entity.Api,
                    $"Serpent: {targetPlayer.PlayerName} is a creative observer, breaking off attack");
            enrageTimer = 0;
            TransitionTo(SerpentState.Stalking);
            return;
        }

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
                // Bite sound starts the moment it commits to the strike (windup),
                // leading the actual hit. Overrides whatever was playing.
                PlayBite();
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
                attackCooldownTimer = config.AggroTime(config.SerpentAttackCooldown);
                PlayAnimation(AnimSlither);

                // Enraged serpents never disengage after a strike. Aggression
                // scales the disengage roll down, so an aggressive serpent
                // keeps pressing instead of backing off to circle.
                if (enrageTimer <= 0 &&
                    entity.World.Rand.NextDouble() < config.AggroInverseChance(config.SerpentReStalkChance))
                {
                    var rand = entity.World.Rand;
                    stalkDuration = config.AggroTime(
                        config.SerpentStalkDurationMin +
                        (float)(rand.NextDouble() *
                            (config.SerpentStalkDurationMax - config.SerpentStalkDurationMin)));
                    if (config.DebugLogging)
                        UnderwaterHorrorsModSystem.DebugLog(entity.Api,
                            $"Serpent disengaging, re-stalk for {stalkDuration:F1}s");
                    TransitionTo(SerpentState.Stalking);
                }
            }
        }
    }

    // ═══════════════════════════════════════════════════════════════════
    //  Damage response — flee or provoke.
    //  Every hit first rolls damage x chance-per-damage to break off and
    //  resume circling at the wide initial orbit. This is the rust
    //  (aggressive) serpent, so it uses the halved
    //  RustSerpentFleeChancePerDamage. If the flee roll misses and the
    //  serpent is not already attacking, it rolls the provoke chance to
    //  turn on its attacker instead, so chasing and hitting a circling
    //  (or freshly fled) serpent can trigger a counterattack. A provoked
    //  serpent stays enraged for SerpentEnrageSeconds: no flee rolls and
    //  no post-strike disengage until the window expires.
    // ═══════════════════════════════════════════════════════════════════
    public override void OnEntityReceiveDamage(DamageSource damageSource, ref float damage)
    {
        base.OnEntityReceiveDamage(damageSource, ref damage);

        if (entity.Api.Side != EnumAppSide.Server) return;
        if (!entity.Alive) return;
        if (damage <= 0f || damageSource?.Type == EnumDamageType.Heal) return;
        if (state != SerpentState.Surfacing &&
            state != SerpentState.Stalking &&
            state != SerpentState.Attacking) return;

        // Enraged serpents are committed and skip the flee roll entirely.
        if (enrageTimer <= 0)
        {
            double chance = damage * config.AggroInverseChance(config.RustSerpentFleeChancePerDamage);
            double roll = entity.World.Rand.NextDouble();
            if (roll < chance)
            {
                if (config.DebugLogging)
                    UnderwaterHorrorsModSystem.DebugLog(entity.Api,
                        $"Serpent fleeing after {damage:F1} damage (roll {roll:F2} vs {chance:F2})");

                // Overrides windup/strike/charge: TransitionTo clears all attack
                // flags, then the spiral is reset to its wide initial radius so
                // the serpent swims out and resumes circling at a distance.
                TransitionTo(SerpentState.Stalking);
                useSpiralApproach = true;
                SetupSpiralApproach(true);
                fleeAggroSuppressTimer = config.SerpentFleeAggroSuppressSeconds;
                attackCooldownTimer = Math.Max(attackCooldownTimer, config.SerpentAttackCooldown);
                PlayDive();
                return;
            }
        }

        // Provoke: a hit that didn't drive it off can enrage it instead.
        // Clearing the suppress timer lets a post-flee serpent re-engage.
        // Creative/spectator observers can't provoke it while the ignore
        // toggle is on.
        if (state != SerpentState.Attacking &&
            !TargetIsPassiveObserver &&
            entity.World.Rand.NextDouble() < config.AggroChance(config.SerpentProvokeChance))
        {
            if (config.DebugLogging)
                UnderwaterHorrorsModSystem.DebugLog(entity.Api,
                    $"Serpent provoked by {damage:F1} damage, " +
                    $"enraged for {config.SerpentEnrageSeconds:F0}s");
            fleeAggroSuppressTimer = 0;
            enrageTimer = config.SerpentEnrageSeconds;
            TransitionTo(SerpentState.Attacking);
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
