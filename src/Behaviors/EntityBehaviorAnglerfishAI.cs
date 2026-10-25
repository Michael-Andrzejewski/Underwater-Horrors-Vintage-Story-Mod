using System;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.GameContent;

namespace UnderwaterHorrors;

public enum AnglerfishMood { Lurking, Creeping, Striking, Returning, Fleeing }

/// <summary>
/// The giant anglerfish. Like a wobbegong it rests on the sea floor near a
/// coast and lets its lure do the work:
///
/// - Lurking: it lies on the floor, turns to face any player within
///   FaceRange of its body (else the one its lure is baiting), and drifts
///   back to its lair when nobody is around.
/// - Creeping: while its prey is out of reach it inches along the floor
///   toward them, over slopes and between blocks, but never into water too
///   shallow for it and never far from its lair.
/// - Striking: once its prey is within AnglerfishStrikeRange of its mouth,
///   or that far sideways and up to AnglerfishStrikeReachUp above it, it
///   swoops and gulps. It only strikes prey roughly ahead of it, so it
///   turns first rather than snapping round.
/// - Returning: back down to where the strike began.
/// - Fleeing: a hit on its lure, or losing most of its health, sends it off
///   into deeper water away from the attacker. It is removed once nobody is
///   within FleeGoneRange, or after FleeSeconds.
///
/// Offsets below are for the model at entity size 2, in blocks; Scale
/// stretches them to the entity's real size.
/// </summary>
public sealed class EntityBehaviorAnglerfishAI : EntityBehavior
{
    public const string LureIdAttr = "underwaterhorrors:anglerLureId";
    public const string OwnerIdAttr = "underwaterhorrors:anglerOwnerId";
    // Set on anglerfish from natural spawning; they leave at daybreak.
    public const string NaturalAttr = "underwaterhorrors:anglerNatural";
    private static readonly AssetLocation LureAsset = new("underwaterhorrors", "anglerfishlure");

    // tools/anglerfish/build_angler.py, at entity size 2: the front of the
    // gape, the middle and top of the body, the tip of the lure pole, and a
    // point deep in the skull where unused rope pieces wait.
    private const double MouthForward = 6.5, MouthUp = 4.75, BodyCenterUp = 5.5, BodyTop = 11.5;
    private const double PoleForward = 6.625, PoleUp = 14.375, StackForward = 2.25, StackUp = 6.25;
    // The thick head and trunk, sampled for room: along (negative ahead),
    // sideways, and up from the belly.
    private static readonly double[] BodyAlong = { -7, -2, 3, 9, 15, 21 };
    private static readonly double[] BodySides = { -4.5, 0, 4.5 };
    private static readonly double[] BodyHeights = { .5, 5.5, 10.5 };

    // Blocks from its body within which it turns to face a player.
    public const double FaceRange = 15;
    // Blocks from the pole tip within which the lure baits a player, even
    // one on the shore.
    public const double BaitRange = 160;
    // It creeps toward prey within this many blocks of its lair.
    private const double LairRange = 64;
    private const double CreepSpeed = 1.5, FleeSpeed = 10, DiveSpeed = 12;
    private const float FleeSeconds = 60;
    // Samples (of 54) a fleeing fish may clip through terrain.
    private const int FleeTolerance = 8;
    private const double FleeGoneRange = 128;
    // It only starts a strike at prey within this angle of straight ahead.
    private const double StrikeConeDeg = 75;

    private readonly AnglerfishAttackCycle cycle = new();
    private readonly Vec3d home = new(), goal = new(), retreat = new();
    private readonly BlockPos scan = new(0, 0, 0, 0);
    private AnglerfishMood mood = AnglerfishMood.Lurking;
    private bool initialized, lureLost;
    private float lureRetry, stuckTime, creepPause, fleeTime, driftTimer;
    private double fleeYaw;
    private Entity lastAttacker;
    private IPlayer target;

    public double Scale { get; private set; } = 1;
    public AnglerfishMood Mood => mood;
    public bool IsLunging => mood == AnglerfishMood.Striking;
    public bool IsRetreating => mood == AnglerfishMood.Returning;
    public bool IsFleeing => mood == AnglerfishMood.Fleeing;
    /// <summary>What its last look at its prey decided, for tests and debugging.</summary>
    public string StrikeCheck { get; private set; } = "";

    public EntityBehaviorAnglerfishAI(Entity entity) : base(entity) { }
    public override string PropertyName() => "anglerfishai";

    private static UnderwaterHorrorsConfig Config => UnderwaterHorrorsModSystem.Config;
    private double StrikeRange => Config?.AnglerfishStrikeRange ?? 28;

    public override void Initialize(EntityProperties properties, JsonObject attributes)
    {
        base.Initialize(properties, attributes);
        Scale = ScaleOf(properties);
    }

    public static double ScaleOf(EntityProperties properties) => (properties?.Client?.Size ?? 2f) / 2.0;

    public override void OnGameTick(float deltaTime)
    {
        if (entity.Api.Side != EnumAppSide.Server || !entity.Alive) return;
        float dt = Math.Clamp(deltaTime, 0, .25f);
        if (!initialized)
        {
            initialized = true;
            if (!entity.Attributes.GetBool("anglerHomeSet"))
            {
                entity.Attributes.SetBool("anglerHomeSet", true);
                entity.Attributes.SetDouble("anglerHomeX", entity.Pos.X);
                entity.Attributes.SetDouble("anglerHomeY", entity.Pos.Y);
                entity.Attributes.SetDouble("anglerHomeZ", entity.Pos.Z);
            }
            home.Set(entity.Attributes.GetDouble("anglerHomeX"), entity.Attributes.GetDouble("anglerHomeY"), entity.Attributes.GetDouble("anglerHomeZ"));
            retreat.Set(home.X, home.Y, home.Z);
        }
        entity.Pos.Motion.Set(0, 0, 0);
        if (entity.WatchedAttributes.GetBool("underwaterhorrors:static")) return;
        if (!lureLost && mood != AnglerfishMood.Fleeing) EnsureLure(dt);
        var animation = entity.GetBehavior<EntityBehaviorAnglerfishAnimation>();
        if (animation?.IsPreviewing == true) return;
        cycle.TriggerRange = StrikeRange;

        // Badly hurt, it gives up and leaves.
        var health = entity.GetBehavior<EntityBehaviorHealth>();
        if (mood != AnglerfishMood.Fleeing && health != null && health.Health < health.MaxHealth * .35f)
            StartFleeing(lastAttacker);

        switch (mood)
        {
            case AnglerfishMood.Fleeing: Flee(dt); break;
            case AnglerfishMood.Striking: Strike(dt); break;
            case AnglerfishMood.Returning: Return(dt); break;
            default: Hunt(dt); break;
        }
        switch (mood)
        {
            case AnglerfishMood.Striking:
            case AnglerfishMood.Fleeing: animation?.SetLocomotion("swimfast", 1f); break;
            case AnglerfishMood.Returning: animation?.SetLocomotion("swim", 1f); break;
            case AnglerfishMood.Creeping: animation?.SetLocomotion("swim", .5f); break;
            default: animation?.SetLocomotion("idle", 1f); break;
        }
        cycle.Tick(dt);
    }

    // ── Lurking and creeping ─────────────────────────────────────────────

    private void Hunt(float dt)
    {
        target = NearestTarget(out _);
        IPlayer face = NearestPlayerNearBody(FaceRange);
        IPlayer bait = BaitTarget();

        // Strike prey in reach of the mouth, once it is roughly ahead.
        if (target == null) StrikeCheck = "no prey in the water";
        else if (cycle.Cooldown > 0) StrikeCheck = $"cooling down {cycle.Cooldown:F1}s";
        else if (StrikeDistance(target) > StrikeRange) StrikeCheck = $"out of reach ({StrikeDistance(target):F1})";
        else if (TryStartStrike(target)) return;

        IPlayer look = face ?? target ?? bait;
        if (look != null) TurnToward(look.Entity.Pos.XYZ, dt, face != null ? 35 : 15);

        IPlayer prey = target ?? bait;
        creepPause = Math.Max(0, creepPause - dt);
        if (prey != null)
        {
            double mouthSideways = SidewaysFromMouth(prey.Entity.Pos.XYZ);
            mood = mouthSideways > StrikeRange * .6 && creepPause <= 0 && Creep(prey.Entity.Pos.XYZ, dt)
                ? AnglerfishMood.Creeping : AnglerfishMood.Lurking;
        }
        else
        {
            mood = AnglerfishMood.Lurking;
            DriftHome(dt);
        }
        if (mood == AnglerfishMood.Lurking) Settle(dt);
    }

    // Lying in wait it sinks back onto the floor, after lifting off it to
    // turn, a strike that ended high, or a floor that fell away.
    private void Settle(float dt)
    {
        if (!RestHeight(entity.World, entity.Pos.X, entity.Pos.Z, entity.Pos.Dimension, Scale, out double floorY)) return;
        if (entity.Pos.Y - floorY < .3) return;
        double y = Math.Max(floorY, entity.Pos.Y - 1.5 * dt);
        if (Fits(entity.World, entity.Pos.X, y, entity.Pos.Z, entity.Pos.Yaw, entity.Pos.Dimension, Scale, false, true))
            entity.Pos.SetPos(entity.Pos.X, y, entity.Pos.Z);
    }

    // One step along the floor toward a point (keeping the mouth toward it).
    // False when there is no way forward; after a few seconds of that it
    // stops trying for a while instead of jittering against a slope.
    private bool Creep(Vec3d toward, float dt)
    {
        double s = Scale;
        double yaw = entity.Pos.Yaw;
        double gx = toward.X - Math.Sin(yaw) * MouthForward * s, gz = toward.Z - Math.Cos(yaw) * MouthForward * s;
        double dx = gx - entity.Pos.X, dz = gz - entity.Pos.Z;
        if (dx * dx + dz * dz < 1) return false;
        // Never leave the lair far behind.
        if (Math.Sqrt((gx - home.X) * (gx - home.X) + (gz - home.Z) * (gz - home.Z)) > LairRange && HomeDistance() > LairRange * .8) return false;
        double want = Math.Atan2(dx, dz);
        // Crawl only roughly where it faces; Hunt does the turning.
        if (Math.Abs(GameMath.AngleRadDistance((float)yaw, (float)want)) > 50 * GameMath.DEG2RAD) return true;
        double step = CreepSpeed * dt;
        foreach (double off in new[] { 0.0, 25, -25, 50, -50 })
        {
            double h = yaw + off * GameMath.DEG2RAD;
            double nx = entity.Pos.X + Math.Sin(h) * step, nz = entity.Pos.Z + Math.Cos(h) * step;
            if (!RestHeight(entity.World, nx, nz, entity.Pos.Dimension, s, out double ny)) continue;
            // Ease up and down slopes rather than snapping to them.
            ny = entity.Pos.Y + Math.Clamp(ny - entity.Pos.Y, -2 * dt * s, 2 * dt * s);
            if (!Fits(entity.World, nx, ny, nz, (float)yaw, entity.Pos.Dimension, s, false, true)) continue;
            entity.Pos.SetPos(nx, ny, nz);
            stuckTime = 0;
            return true;
        }
        stuckTime += dt;
        if (stuckTime > 3) { stuckTime = 0; creepPause = 8; }
        return false;
    }

    // With nobody around it drifts back to its lair and settles on the floor.
    private void DriftHome(float dt)
    {
        driftTimer -= dt;
        if (HomeDistance() <= 6 || driftTimer > 0) return;
        TurnToward(home, dt, 15);
        double s = Scale;
        // Creep steers the mouth; aim it one mouth-length past home.
        var mouthAtHome = home.AddCopy(Math.Sin(entity.Pos.Yaw) * MouthForward * s, 0, Math.Cos(entity.Pos.Yaw) * MouthForward * s);
        if (Creep(mouthAtHome, dt)) mood = AnglerfishMood.Creeping;
        else driftTimer = 5;
    }

    private double HomeDistance() =>
        Math.Sqrt((entity.Pos.X - home.X) * (entity.Pos.X - home.X) + (entity.Pos.Z - home.Z) * (entity.Pos.Z - home.Z));

    // ── Striking and returning ───────────────────────────────────────────

    private bool TryStartStrike(IPlayer prey)
    {
        double dx = prey.Entity.Pos.X - entity.Pos.X, dz = prey.Entity.Pos.Z - entity.Pos.Z;
        float yaw = (float)Math.Atan2(dx, dz);
        // Turn first rather than snapping a body this size round.
        double off = Math.Abs(GameMath.AngleRadDistance(entity.Pos.Yaw, yaw)) * GameMath.RAD2DEG;
        if (off > StrikeConeDeg) { StrikeCheck = $"turning first ({off:F0} degrees off)"; return false; }
        if (!Fits(entity.World, entity.Pos.X, entity.Pos.Y, entity.Pos.Z, yaw, entity.Pos.Dimension, Scale, true, true)) { StrikeCheck = "no room to turn for the strike"; return false; }
        if (!cycle.TryBegin(StrikeDistance(prey))) { StrikeCheck = "attack cycle refused"; return false; }
        StrikeCheck = "striking";
        retreat.Set(entity.Pos.X, entity.Pos.Y, entity.Pos.Z);
        entity.WatchedAttributes.SetString("underwaterhorrors:targetPlayerUid", prey.PlayerUID);
        entity.Pos.Yaw = yaw;
        double s = Scale;
        goal.Set(prey.Entity.Pos.X - Math.Sin(yaw) * MouthForward * s,
            prey.Entity.Pos.Y + .6 - MouthUp * s, prey.Entity.Pos.Z - Math.Cos(yaw) * MouthForward * s);
        double speed = Config?.AnglerfishStrikeSpeed ?? AnglerfishAttackCycle.LungeSpeed;
        entity.GetBehavior<EntityBehaviorAnglerfishAnimation>()?.PlayLunge((float)(entity.Pos.DistanceTo(goal) / speed));
        target = prey;
        mood = AnglerfishMood.Striking;
        return true;
    }

    private void Strike(float dt)
    {
        if (cycle.Phase != AnglerfishPhase.Lunging || !ValidTarget(target)) { EndStrike(); return; }
        double speed = Config?.AnglerfishStrikeSpeed ?? AnglerfishAttackCycle.LungeSpeed;
        bool clear = MoveToward(goal, speed * dt, true, true);
        if (!clear) StrikeCheck = $"strike blocked {entity.Pos.DistanceTo(goal):F1} short";
        if (!clear || entity.Pos.DistanceTo(goal) < .1 || cycle.Phase != AnglerfishPhase.Lunging) EndStrike();
    }

    private void EndStrike()
    {
        cycle.Finish();
        if (mood == AnglerfishMood.Striking) mood = AnglerfishMood.Returning;
    }

    private void Return(float dt)
    {
        if (!MoveToward(retreat, DiveSpeed * dt, false, true))
        {
            // Something is in the way: settle on the floor where it is.
            if (RestHeight(entity.World, entity.Pos.X, entity.Pos.Z, entity.Pos.Dimension, Scale, out double y)
                && Fits(entity.World, entity.Pos.X, y, entity.Pos.Z, entity.Pos.Yaw, entity.Pos.Dimension, Scale, false, true))
                retreat.Set(entity.Pos.X, y, entity.Pos.Z);
            else retreat.Set(entity.Pos.X, entity.Pos.Y, entity.Pos.Z);
        }
        if (entity.Pos.DistanceTo(retreat) < .3) { cycle.ArriveHome(); mood = AnglerfishMood.Lurking; }
    }

    // ── Fleeing ─────────────────────────────────────────────────────────

    /// <summary>Its lure was hit: it reels it in and swims off.</summary>
    public void Startle(Entity attacker)
    {
        if (attacker != null) lastAttacker = attacker;
        StartFleeing(attacker);
    }

    /// <summary>Its lure was destroyed; it grows no new one.</summary>
    public void OnLureDestroyed(Entity attacker)
    {
        lureLost = true;
        StartFleeing(attacker ?? lastAttacker);
    }

    private void StartFleeing(Entity from)
    {
        if (mood == AnglerfishMood.Fleeing) return;
        if (mood == AnglerfishMood.Striking) cycle.Finish();
        mood = AnglerfishMood.Fleeing;
        fleeTime = 0;
        fleeYaw = PickFleeYaw(from);
    }

    // Toward deep water: of twelve headings all round, the one whose water
    // 40 and 80 blocks out is deepest, with a lean away from the attacker
    // (or the nearest player). Never a heading that passes within 12 blocks
    // of them. Off a coast that may well be sideways or past them.
    private double PickFleeYaw(Entity from)
    {
        var src = from?.Pos.XYZ ?? NearestPlayerPos();
        double away = src == null ? entity.Pos.Yaw + Math.PI : Math.Atan2(entity.Pos.X - src.X, entity.Pos.Z - src.Z);
        double best = away, bestScore = double.MinValue;
        var w = entity.World; int dim = entity.Pos.Dimension;
        for (int i = 0; i < 12; i++)
        {
            double h = away + i * 30 * GameMath.DEG2RAD;
            double sx = Math.Sin(h), sz = Math.Cos(h);
            if (src != null)
            {
                // Closest approach to the attacker along the first 60 blocks.
                double t = Math.Clamp((src.X - entity.Pos.X) * sx + (src.Z - entity.Pos.Z) * sz, 0, 60);
                double cx = entity.Pos.X + sx * t - src.X, cz = entity.Pos.Z + sz * t - src.Z;
                if (t > 0 && cx * cx + cz * cz < 144) continue;
            }
            double near = WaterDepth(w, entity.Pos.X + sx * 40, entity.Pos.Z + sz * 40, dim, out _, out _);
            double far = WaterDepth(w, entity.Pos.X + sx * 80, entity.Pos.Z + sz * 80, dim, out _, out _);
            double score = Math.Min(near, far) + 15 * Math.Cos(i * 30 * GameMath.DEG2RAD);
            if (score > bestScore) { bestScore = score; best = h; }
        }
        return best;
    }

    private void Flee(float dt)
    {
        fleeTime += dt;
        if (fleeTime > FleeSeconds || !AnyPlayerWithin(FleeGoneRange)) { entity.Die(EnumDespawnReason.Expire); return; }
        double s = Scale;
        var ahead = new Vec3d(entity.Pos.X + Math.Sin(fleeYaw) * 40, entity.Pos.Y, entity.Pos.Z + Math.Cos(fleeYaw) * 40);
        // Off the floor to mid-water, where there is room to turn and swim.
        double y = entity.Pos.Y;
        if (WaterDepth(entity.World, entity.Pos.X, entity.Pos.Z, entity.Pos.Dimension, out double surface, out double floor) > 0)
            y += Math.Clamp((floor + surface) / 2 - BodyCenterUp * s - y, -3 * dt, 3 * dt);
        if (y != entity.Pos.Y && Fits(entity.World, entity.Pos.X, y, entity.Pos.Z, entity.Pos.Yaw, entity.Pos.Dimension, s, false, true))
            entity.Pos.SetPos(entity.Pos.X, y, entity.Pos.Z);
        TurnToward(ahead, dt, 45, FleeTolerance);
        // Swim only once it has turned mostly away, never first toward the attacker.
        double off = Math.Abs(GameMath.AngleRadDistance(entity.Pos.Yaw, (float)fleeYaw));
        if (off > 60 * GameMath.DEG2RAD)
        {
            stuckTime += dt;
            if (stuckTime > 4) { stuckTime = 0; fleeYaw = PickFleeYaw(lastAttacker) + (entity.World.Rand.NextDouble() - .5) * Math.PI / 2; }
            return;
        }
        double step = FleeSpeed * dt * (off < 30 * GameMath.DEG2RAD ? 1 : .4);
        var next = new Vec3d(entity.Pos.X + Math.Sin(entity.Pos.Yaw) * step, entity.Pos.Y, entity.Pos.Z + Math.Cos(entity.Pos.Yaw) * step);
        if (MoveToward(next, step, false, false, FleeTolerance)) { stuckTime = 0; return; }
        stuckTime += dt;
        if (stuckTime > 1) { stuckTime = 0; fleeYaw = PickFleeYaw(lastAttacker) + (entity.World.Rand.NextDouble() - .5) * Math.PI / 2; }
    }

    // ── Who it sees ─────────────────────────────────────────────────────

    /// <summary>The nearest player it may strike: in the water or a boat.</summary>
    public IPlayer NearestTarget(out double distance)
    {
        IPlayer best = null; distance = double.MaxValue;
        foreach (var p in entity.World.AllOnlinePlayers)
        {
            if (!ValidTarget(p)) continue;
            double d = BodyDistance(p.Entity.Pos.XYZ);
            if (d < distance) { distance = d; best = p; }
        }
        return best;
    }

    /// <summary>
    /// The player its lure baits: the one it could strike, or else the
    /// nearest player within BaitRange of the pole tip, ashore or afloat.
    /// Nobody while it flees.
    /// </summary>
    public IPlayer BaitTarget()
    {
        if (mood == AnglerfishMood.Fleeing) return null;
        var anchor = LureAnchor();
        if (ValidTarget(target) && anchor.DistanceTo(target.Entity.Pos.XYZ) <= AnglerfishLureCycle.MaxReach) return target;
        IPlayer best = null; double bestD = BaitRange;
        foreach (var p in entity.World.AllOnlinePlayers)
        {
            if (!Eligible(p)) continue;
            double d = anchor.DistanceTo(p.Entity.Pos.XYZ);
            if (d < bestD) { bestD = d; best = p; }
        }
        return best;
    }

    private IPlayer NearestPlayerNearBody(double range)
    {
        IPlayer best = null; double bestD = range;
        foreach (var p in entity.World.AllOnlinePlayers)
        {
            if (!Eligible(p)) continue;
            double d = BodyDistance(p.Entity.Pos.XYZ);
            if (d < bestD) { bestD = d; best = p; }
        }
        return best;
    }

    private Vec3d NearestPlayerPos()
    {
        Vec3d best = null; double bestD = double.MaxValue;
        foreach (var p in entity.World.AllOnlinePlayers)
        {
            if (!Eligible(p)) continue;
            double d = entity.Pos.SquareDistanceTo(p.Entity.Pos.XYZ);
            if (d < bestD) { bestD = d; best = p.Entity.Pos.XYZ; }
        }
        return best;
    }

    private bool AnyPlayerWithin(double range)
    {
        foreach (var p in entity.World.AllOnlinePlayers)
        {
            var e = p?.Entity;
            if (e != null && e.Alive && e.Pos.Dimension == entity.Pos.Dimension && e.Pos.SquareDistanceTo(entity.Pos.XYZ) <= range * range) return true;
        }
        return false;
    }

    // From a point to the surface of the thick head and trunk.
    private double BodyDistance(Vec3d p)
    {
        double s = Scale;
        ModelPoint(-4.75 * s, BodyCenterUp * s, out var a);
        ModelPoint(14 * s, BodyCenterUp * s, out var b);
        return Math.Max(0, AnglerfishAttackCycle.DistanceToSegment(p.X, p.Y + .6, p.Z, a.X, a.Y, a.Z, b.X, b.Y, b.Z) - 4 * s);
    }

    /// <summary>A player it notices at all: alive, here, and not a creative
    /// player the observer setting hides.</summary>
    public bool Eligible(IPlayer p)
    {
        if (p?.Entity == null || !p.Entity.Alive || p.Entity.Pos.Dimension != entity.Pos.Dimension) return false;
        if (entity.World.PlayerByUid(p.PlayerUID)?.Entity != p.Entity) return false;
        return Config?.IgnoreCreativePlayers == false
            || (p.WorldData.CurrentGameMode != EnumGameMode.Creative && p.WorldData.CurrentGameMode != EnumGameMode.Spectator);
    }

    /// <summary>A player it may strike: eligible and in the water (or on a
    /// boat or raft over it).</summary>
    public bool ValidTarget(IPlayer p)
    {
        if (!Eligible(p)) return false;
        scan.Set((int)Math.Floor(p.Entity.Pos.X), (int)Math.Floor(p.Entity.Pos.Y), (int)Math.Floor(p.Entity.Pos.Z));
        scan.dimension = p.Entity.Pos.Dimension;
        for (int i = 0; i <= 3; i++)
        {
            var block = entity.World.BlockAccessor.GetBlock(scan);
            if (WaterHelper.IsWaterBlock(block)) return true;
            if (block.Id != 0) return false;
            scan.Y--;
        }
        return false;
    }

    // ── Geometry ────────────────────────────────────────────────────────

    private void ModelPoint(double modelXOffset, double modelY, out Vec3d point)
    {
        // Block offsets from the entity's centre; negative is ahead.
        double yaw = entity.Pos.Yaw;
        point = new Vec3d(entity.Pos.X - Math.Sin(yaw) * modelXOffset, entity.Pos.Y + modelY, entity.Pos.Z - Math.Cos(yaw) * modelXOffset);
    }

    /// <summary>The tip of the lure pole: where the gear docks and the rope starts.</summary>
    public Vec3d LureAnchor()
    {
        ModelPoint(-PoleForward * Scale, PoleUp * Scale, out var anchor);
        return anchor;
    }

    /// <summary>Deep in the skull, where rope pieces the line does not use wait.</summary>
    public Vec3d LureStack()
    {
        ModelPoint(-StackForward * Scale, StackUp * Scale, out var stack);
        return stack;
    }

    private Vec3d Mouth()
    {
        ModelPoint(-MouthForward * Scale, MouthUp * Scale, out var mouth);
        return mouth;
    }

    // From the prey's middle to the front of the gape.
    public double MouthDistance(IPlayer p) => Mouth().DistanceTo(p.Entity.Pos.X, p.Entity.Pos.Y + .6, p.Entity.Pos.Z);

    private double SidewaysFromMouth(Vec3d p)
    {
        var m = Mouth();
        return Math.Sqrt((p.X - m.X) * (p.X - m.X) + (p.Z - m.Z) * (p.Z - m.Z));
    }

    /// <summary>
    /// The prey's distance as the strike sees it, compared with the strike
    /// range: a ball of that radius round the mouth, stretched upward into
    /// a column AnglerfishStrikeReachUp tall, so prey on the surface above
    /// a deep mouth is within reach.
    /// </summary>
    public double StrikeDistance(IPlayer p)
    {
        var m = Mouth();
        double dx = p.Entity.Pos.X - m.X, dy = p.Entity.Pos.Y + .6 - m.Y, dz = p.Entity.Pos.Z - m.Z;
        double sideways = Math.Sqrt(dx * dx + dz * dz);
        if (dy <= 0) return Math.Sqrt(sideways * sideways + dy * dy);
        double up = Config?.AnglerfishStrikeReachUp ?? 60;
        return Math.Max(sideways, dy * StrikeRange / up);
    }

    // ── Movement ────────────────────────────────────────────────────────

    private void TurnToward(Vec3d destination, float dt, float degreesPerSecond, int tolerance = 0)
    {
        double dx = destination.X - entity.Pos.X, dz = destination.Z - entity.Pos.Z;
        if (dx * dx + dz * dz < .01) return;
        float yaw = AnglerfishAttackCycle.SmoothYaw(entity.Pos.Yaw, (float)Math.Atan2(dx, dz), dt, degreesPerSecond);
        var w = entity.World; int dim = entity.Pos.Dimension; double s = Scale;
        if (Fits(w, entity.Pos.X, entity.Pos.Y, entity.Pos.Z, yaw, dim, s, false, true, tolerance)) { entity.Pos.Yaw = yaw; return; }
        // Blocked by the floor or a reef: lift a little off it to turn.
        for (int up = 1; up <= 3; up++)
        {
            double y = entity.Pos.Y + up * .5 * s;
            if (Fits(w, entity.Pos.X, y, entity.Pos.Z, yaw, dim, s, false, true, tolerance)) { entity.Pos.SetPos(entity.Pos.X, y, entity.Pos.Z); entity.Pos.Yaw = yaw; return; }
        }
    }

    private bool MoveToward(Vec3d destination, double travel, bool strike, bool allowAir, int tolerance = 0)
    {
        double dx = destination.X - entity.Pos.X, dy = destination.Y - entity.Pos.Y, dz = destination.Z - entity.Pos.Z;
        double length = Math.Sqrt(dx * dx + dy * dy + dz * dz);
        double s = Scale;
        if (length < .001)
        {
            if (strike) { var m = Mouth(); TryMouthHit(m.X, m.Y, m.Z, 0, 0, 0); }
            return true;
        }
        travel = Math.Min(travel, length);
        int steps = Math.Max(1, (int)Math.Ceiling(travel / .5));
        double sx = dx / length * travel / steps, sy = dy / length * travel / steps, sz = dz / length * travel / steps;
        for (int i = 0; i < steps; i++)
        {
            double x = entity.Pos.X + sx, y = entity.Pos.Y + sy, z = entity.Pos.Z + sz;
            // A strike may break the surface; the belly may brush the floor.
            if (!Fits(entity.World, x, y, z, entity.Pos.Yaw, entity.Pos.Dimension, s, allowAir, true, tolerance)) return false;
            var mouth = Mouth();
            entity.Pos.SetPos(x, y, z);
            // Swept mouth test: never jump over the player on a fast/lagged tick.
            if (strike)
            {
                TryMouthHit(mouth.X, mouth.Y, mouth.Z, sx, sy, sz);
                if (cycle.Phase != AnglerfishPhase.Lunging) return true;
            }
        }
        return true;
    }

    private void TryMouthHit(double mx, double my, double mz, double sx, double sy, double sz)
    {
        // The gape is wide: anything within a few blocks of its middle is in.
        double reach = 3 * Scale;
        if (ValidTarget(target) && AnglerfishAttackCycle.DistanceToSegment(
                target.Entity.Pos.X, target.Entity.Pos.Y + .6, target.Entity.Pos.Z, mx, my, mz, mx + sx, my + sy, mz + sz) <= reach
            && ClearMouthLine(mx + sx, my + sy, mz + sz) && cycle.TryHit())
        {
            target.Entity.ReceiveDamage(new DamageSource
            {
                Source = EnumDamageSource.Entity, SourceEntity = entity, Type = EnumDamageType.PiercingAttack,
                DamageTier = Config?.AnglerfishDamageTier ?? 3,
            }, Config?.AnglerfishBiteDamage ?? AnglerfishAttackCycle.Damage);
            EndStrike();
        }
    }

    private bool ClearMouthLine(double x, double y, double z)
    {
        double dx = target.Entity.Pos.X - x, dy = target.Entity.Pos.Y + .6 - y, dz = target.Entity.Pos.Z - z;
        int count = Math.Max(1, (int)Math.Ceiling(Math.Sqrt(dx * dx + dy * dy + dz * dz) * 4));
        scan.dimension = entity.Pos.Dimension;
        for (int i = 0; i <= count; i++)
        {
            double t = (double)i / count;
            scan.Set((int)Math.Floor(x + dx * t), (int)Math.Floor(y + dy * t), (int)Math.Floor(z + dz * t));
            var block = entity.World.BlockAccessor.GetBlock(scan);
            if (block.Id != 0 && !WaterHelper.IsWaterBlock(block)) return false;
        }
        return true;
    }

    // ── Lure ────────────────────────────────────────────────────────────

    private void EnsureLure(float dt)
    {
        lureRetry -= dt; if (lureRetry > 0) return; lureRetry = 1;
        long id = entity.WatchedAttributes.GetLong(LureIdAttr);
        var existing = entity.World.GetEntityById(id);
        if (existing?.Alive == true) return;
        var props = entity.World.GetEntityType(LureAsset); if (props == null) return;
        var lure = entity.World.ClassRegistry.CreateEntity(props); var anchor = LureAnchor();
        lure.Pos.SetPos(anchor.X, anchor.Y, anchor.Z); lure.Pos.Dimension = entity.Pos.Dimension;
        lure.WatchedAttributes.SetLong(OwnerIdAttr, entity.EntityId);
        entity.World.SpawnEntity(lure);
        entity.WatchedAttributes.SetLong(LureIdAttr, lure.EntityId);
    }

    private void Cleanup()
    {
        if (entity.Api.Side != EnumAppSide.Server) return;
        var lure = entity.World.GetEntityById(entity.WatchedAttributes.GetLong(LureIdAttr));
        if (lure is EntityAgent agent) agent.AllowDespawn = true;
        if (lure?.Alive == true) lure.Die(EnumDespawnReason.Expire);
        entity.WatchedAttributes.SetLong(LureIdAttr, 0);
    }

    public override void OnEntityReceiveDamage(DamageSource damageSource, ref float damage)
    {
        base.OnEntityReceiveDamage(damageSource, ref damage);
        var cause = damageSource?.GetCauseEntity();
        if (cause != null && damage > 0) lastAttacker = cause;
    }

    public override void OnEntityDeath(DamageSource source) { Cleanup(); base.OnEntityDeath(source); }
    public override void OnEntityDespawn(EntityDespawnData data) { Cleanup(); base.OnEntityDespawn(data); }

    // ── Room and water ──────────────────────────────────────────────────

    /// <summary>
    /// Whether the body fits at a spot. Every sample must be water (or air
    /// too, with allowAir, for a strike that breaks the surface). With
    /// restingOnFloor the lowest row may touch the floor, so it can lie on
    /// slopes and among blocks like a wobbegong. tolerance lets that many
    /// other samples clip terrain: a fleeing fish brushes past a reef rather
    /// than sticking on it.
    /// </summary>
    public static bool Fits(IWorldAccessor world, double x, double y, double z, float yaw, int dimension, double scale, bool allowAir, bool restingOnFloor, int tolerance = 0)
    {
        var p = new BlockPos(0, 0, 0, dimension);
        int blocked = 0;
        for (int h = 0; h < BodyHeights.Length; h++)
        foreach (double along in BodyAlong)
        foreach (double side in BodySides)
        {
            double a = along * scale, sd = side * scale;
            p.Set((int)Math.Floor(x - Math.Sin(yaw) * a + Math.Cos(yaw) * sd), (int)Math.Floor(y + BodyHeights[h] * scale), (int)Math.Floor(z - Math.Cos(yaw) * a - Math.Sin(yaw) * sd));
            if (p.Y < 1 || p.Y >= world.BlockAccessor.MapSizeY - 1) return false;
            var block = world.BlockAccessor.GetBlock(p);
            if (WaterHelper.IsWaterBlock(block)) continue;
            if (allowAir && block.Id == 0) continue;
            if (restingOnFloor && h == 0) continue;
            if (++blocked > tolerance) return false;
        }
        return true;
    }

    /// <summary>
    /// The height at which the body would rest on the floor below (x, z), if
    /// the water there is deep enough to cover its back.
    /// </summary>
    public static bool RestHeight(IWorldAccessor world, double x, double z, int dimension, double scale, out double y)
    {
        y = 0;
        double depth = WaterDepth(world, x, z, dimension, out double surface, out double floor);
        if (depth <= 0 || surface - floor < BodyTop * scale + 1) return false;
        y = floor - .6;
        return true;
    }

    /// <summary>Water at a column: its depth, its surface (top of the top
    /// water block) and its floor (top of the first solid block below).</summary>
    public static double WaterDepth(IWorldAccessor world, double x, double z, int dimension, out double surface, out double floor)
    {
        surface = floor = 0;
        var acc = world.BlockAccessor;
        var p = new BlockPos((int)Math.Floor(x), Math.Min(acc.MapSizeY - 2, world.SeaLevel + 6), (int)Math.Floor(z), dimension);
        if (acc.GetChunkAtBlockPos(p) == null) return 0;
        while (p.Y > 1 && !WaterHelper.IsWaterBlock(acc.GetBlock(p)))
        {
            if (acc.GetBlock(p).Id != 0) return 0;
            p.Y--;
        }
        if (p.Y <= 1) return 0;
        surface = p.Y + 1;
        while (p.Y > 1 && WaterHelper.IsWaterBlock(acc.GetBlock(p))) p.Y--;
        floor = p.Y + 1;
        return surface - floor;
    }

    // ── Spawning ────────────────────────────────────────────────────────

    /// <summary>
    /// Lays an anglerfish on the floor minDist to maxDist blocks from the
    /// player, facing them, where the water is AnglerfishMinWaterDepth deep.
    /// Null when no column out there is deep enough.
    /// </summary>
    public static Entity Spawn(ICoreServerAPI api, IServerPlayer player, double minDist = 50, double maxDist = 80, bool natural = false)
    {
        var props = api.World.GetEntityType(new AssetLocation("underwaterhorrors", "giantanglerfish"));
        if (props == null) return null;
        var pos = player.Entity.Pos;
        for (int attempt = 0; attempt < 24; attempt++)
        {
            double angle = pos.Yaw + attempt * Math.PI / 6;
            double radius = minDist + (maxDist - minDist) * api.World.Rand.NextDouble();
            var fish = PlaceAt(api, props, player, pos.X + Math.Sin(angle) * radius, pos.Z + Math.Cos(angle) * radius, natural);
            if (fish != null) return fish;
        }
        return null;
    }

    /// <summary>
    /// For a player at sea near land: finds the nearest coast within
    /// AnglerfishCoastRange, then the first water off it deep enough for the
    /// fish, and lays it on the floor there facing the player. Null when
    /// there is no coast in range or no deep water off it.
    /// </summary>
    public static Entity SpawnNearCoast(ICoreServerAPI api, IServerPlayer player, bool natural = true)
    {
        var props = api.World.GetEntityType(new AssetLocation("underwaterhorrors", "giantanglerfish"));
        if (props == null) return null;
        var cfg = UnderwaterHorrorsModSystem.Config;
        double range = cfg?.AnglerfishCoastRange ?? 160;
        int need = cfg?.AnglerfishMinWaterDepth ?? 40;
        var w = api.World; var pos = player.Entity.Pos; int dim = pos.Dimension; double scale = ScaleOf(props);
        double bestCoast = double.MaxValue, cx = 0, cz = 0, ux = 0, uz = 0;
        for (int i = 0; i < 16; i++)
        {
            double a = i * Math.PI / 8, dx = Math.Sin(a), dz = Math.Cos(a);
            for (double d = 16; d <= range && d < bestCoast; d += 4)
            {
                double x = pos.X + dx * d, z = pos.Z + dz * d;
                if (!IsLand(w, x, z, dim)) continue;
                bestCoast = d; cx = x; cz = z; ux = dx; uz = dz;
                break;
            }
        }
        if (bestCoast == double.MaxValue) { CoastLog(api, $"no coast within {range:F0} blocks of {player.PlayerName}"); return null; }
        // Search the water off that coast, nearest the coast first, along it
        // to either side: the first spot deep enough, far enough from the
        // player and with room for the body.
        double px = -uz, pz = ux, minDist = MinSpawnDistance(scale);
        int tooClose = 0, tooShallow = 0, noRoom = 0;
        for (double back = 8; back < bestCoast + 24; back += 6)
        {
            foreach (double lateral in new[] { 0.0, 16, -16, 32, -32, 48, -48, 64, -64 })
            {
                double x = cx - ux * back + px * lateral, z = cz - uz * back + pz * lateral;
                if (Math.Sqrt((x - pos.X) * (x - pos.X) + (z - pos.Z) * (z - pos.Z)) < minDist) { tooClose++; continue; }
                if (WaterDepth(w, x, z, dim, out _, out _) < need) { tooShallow++; continue; }
                var fish = PlaceAt(api, props, player, x, z, natural);
                if (fish != null) return fish;
                noRoom++;
            }
        }
        CoastLog(api, $"coast {bestCoast:F0} blocks from {player.PlayerName}, but no lair off it: {tooShallow} spots under {need} deep, {tooClose} within {minDist:F0} of the player, {noRoom} without room for the body");
        return null;
    }

    private static void CoastLog(ICoreServerAPI api, string message)
    {
        if (UnderwaterHorrorsModSystem.Config?.DebugLogging == true) api.Logger.Notification("[UH] anglerfish: " + message);
    }

    // Its mouth's reach plus the mouth's own distance ahead plus some room.
    private static double MinSpawnDistance(double scale) =>
        (UnderwaterHorrorsModSystem.Config?.AnglerfishStrikeRange ?? 28) + MouthForward * scale + 15;

    private static bool IsLand(IWorldAccessor world, double x, double z, int dim)
    {
        var acc = world.BlockAccessor;
        var p = new BlockPos((int)Math.Floor(x), world.SeaLevel, (int)Math.Floor(z), dim);
        if (acc.GetChunkAtBlockPos(p) == null) return false;
        var b = acc.GetBlock(p);
        return b.Id != 0 && !WaterHelper.IsWaterBlock(b);
    }

    private static Entity PlaceAt(ICoreServerAPI api, EntityProperties props, IServerPlayer player, double x, double z, bool natural)
    {
        int need = UnderwaterHorrorsModSystem.Config?.AnglerfishMinWaterDepth ?? 40;
        var pos = player.Entity.Pos; int dim = pos.Dimension; double scale = ScaleOf(props);
        if (WaterDepth(api.World, x, z, dim, out _, out _) < need) return null;
        if (!RestHeight(api.World, x, z, dim, scale, out double y)) return null;
        // Facing the player if it fits; off a sloping coast its tail would
        // reach into the shallows, so else at an angle, or along the coast.
        // It turns to face them as they come near.
        float toward = (float)Math.Atan2(pos.X - x, pos.Z - z), yaw = toward;
        bool fits = false;
        foreach (double turn in new[] { 0.0, 45, -45, 90, -90 })
        {
            yaw = toward + (float)(turn * GameMath.DEG2RAD);
            if (fits = Fits(api.World, x, y, z, yaw, dim, scale, false, true)) break;
        }
        if (!fits) return null;
        var fish = api.World.ClassRegistry.CreateEntity(props);
        fish.Pos.SetPos(x, y, z); fish.Pos.Yaw = yaw; fish.Pos.Dimension = dim;
        fish.WatchedAttributes.SetString("underwaterhorrors:targetPlayerUid", player.PlayerUID);
        if (natural) fish.WatchedAttributes.SetBool(NaturalAttr, true);
        api.World.SpawnEntity(fish);
        return fish;
    }
}
