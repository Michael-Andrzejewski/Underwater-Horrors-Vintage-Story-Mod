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

    // How many blocks below the player the serpent should cruise.
    private const float SubmergeDepth = 3.0f;

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

    // ── Boat boredom ───────────────────────────────────────────────────
    private float mountedCircleTimer;
    private float mountedCheckTimer;

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
        if (!entity.Alive) return;
        if (entity.Api.Side != EnumAppSide.Server) return;

        // ── Debug animation mode: freeze in place, replay anim ──
        if (debugAnimName != null)
        {
            entity.ServerPos.Motion.X = 0;
            entity.ServerPos.Motion.Y = 0;
            entity.ServerPos.Motion.Z = 0;

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
            spawnX = entity.ServerPos.X;
            spawnY = entity.ServerPos.Y;
            spawnZ = entity.ServerPos.Z;
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

        // Boat boredom
        if (state == SerpentState.Stalking || state == SerpentState.Attacking)
        {
            if (targetPlayer?.Entity?.MountedOn != null)
            {
                mountedCircleTimer += deltaTime;
                if (mountedCircleTimer >= 60f)
                {
                    mountedCheckTimer += deltaTime;
                    if (mountedCheckTimer >= 30f)
                    {
                        mountedCheckTimer = 0;
                        if (entity.World.Rand.NextDouble() < 0.5)
                        {
                            if (config.DebugLogging)
                                UnderwaterHorrorsModSystem.DebugLog(entity.Api,
                                    $"Serpent bored after {mountedCircleTimer:F0}s, retreating");
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
        surfaceX = targetPlayer.Entity.SidedPos.X + Math.Cos(angle) * dist;
        surfaceZ = targetPlayer.Entity.SidedPos.Z + Math.Sin(angle) * dist;

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
        double mx = entity.ServerPos.Motion.X;
        double mz = entity.ServerPos.Motion.Z;
        double my = entity.ServerPos.Motion.Y;
        double horizSpeedSq = mx * mx + mz * mz;

        // Yaw — skip update when locked (during windup/strike)
        if (!lockFacing)
        {
            float targetYaw;

            if (faceTarget && targetPlayer?.Entity != null)
            {
                // During attack charge: aim directly at the player
                double dx = targetPlayer.Entity.SidedPos.X - entity.ServerPos.X;
                double dz = targetPlayer.Entity.SidedPos.Z - entity.ServerPos.Z;
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

            entity.ServerPos.Yaw = smoothedYaw;
        }

        // Pitch
        const float maxPitchRad = 0.17f;
        double horizSpeed = Math.Sqrt(horizSpeedSq);
        float targetPitch = 0f;
        if (!lockFacing && (horizSpeed > 0.001 || Math.Abs(my) > 0.001))
        {
            targetPitch = -(float)Math.Atan2(my, Math.Max(horizSpeed, 0.001));
            targetPitch = GameMath.Clamp(targetPitch, -maxPitchRad, maxPitchRad);
        }
        entity.ServerPos.Pitch += (targetPitch - entity.ServerPos.Pitch) * Math.Min(1f, deltaTime * 2f);

        if (entity.ServerPos.Pitch > 0.02f)
        {
            float sinkBoost = entity.ServerPos.Pitch * 3f;
            entity.ServerPos.Motion.Y -= sinkBoost * deltaTime;
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
                break;

            case SerpentState.Attacking:
                PlayAnimation(AnimSlither);
                faceTarget = true;  // Turn toward the player during charge
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
    }

    // ═══════════════════════════════════════════════════════════════════
    //  State: Rising
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
    //  State: Surfacing
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
    //  State: Stalking
    // ═══════════════════════════════════════════════════════════════════
    private void OnStalking(float deltaTime)
    {
        if (targetPlayer?.Entity == null) return;

        float radius;
        if (useSpiralApproach)
        {
            radiusTransitionTime += deltaTime;
            float t = Math.Min(1f, radiusTransitionTime / radiusTransitionDuration);
            radius = orbitRadiusStart + (orbitRadiusEnd - orbitRadiusStart) * t;

            if (t >= 1f)
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

        double targetX = targetPlayer.Entity.SidedPos.X + Math.Cos(orbitAngle) * radius;
        double targetZ = targetPlayer.Entity.SidedPos.Z + Math.Sin(orbitAngle) * radius;
        double targetY = targetPlayer.Entity.SidedPos.Y - SubmergeDepth;

        MoveToward(targetX, targetY, targetZ, config.SerpentApproachSpeed * 2, 0.5);

        if (!useSpiralApproach && stateTimer >= stalkDuration)
        {
            TransitionTo(SerpentState.Attacking);
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

        double px = targetPlayer.Entity.SidedPos.X;
        double py = targetPlayer.Entity.SidedPos.Y;
        double pz = targetPlayer.Entity.SidedPos.Z;

        if (!isWindingUp && !isStriking)
        {
            // ── Charge phase ──
            // Move entity center so the HEAD arrives at the player.
            // Target = player position offset BACK by HeadForwardOffset
            // along the entity→player direction.
            double adx = px - entity.ServerPos.X;
            double adz = pz - entity.ServerPos.Z;
            double aDist = Math.Sqrt(adx * adx + adz * adz);

            if (aDist > 0.1)
            {
                double offsetX = px - (adx / aDist) * HeadForwardOffset;
                double offsetZ = pz - (adz / aDist) * HeadForwardOffset;
                MoveToward(offsetX, py - SubmergeDepth, offsetZ, config.SerpentAttackSpeed, 0.5);
            }
            else
            {
                MoveToward(px, py - SubmergeDepth, pz, config.SerpentAttackSpeed, 0.5);
            }

            attackCooldownTimer -= deltaTime;

            // Check HEAD distance to player — this is the real trigger
            double headDist = HeadDistToPlayer();

            if (headDist < HeadAttackTriggerRange && attackCooldownTimer <= 0)
            {
                isWindingUp = true;
                lockFacing = true;
                faceTarget = false;  // Stop targeting, freeze current facing
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
                lockFacing = false;
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
        if (targetPlayer?.Entity != null && targetPlayer.Entity.Alive &&
            targetPlayer.Entity.MountedOn == null && !IsInShallowWater)
        {
            if (config.DebugLogging)
                UnderwaterHorrorsModSystem.DebugLog(entity.Api,
                    "Serpent: player back in deep water, resuming stalking");
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
