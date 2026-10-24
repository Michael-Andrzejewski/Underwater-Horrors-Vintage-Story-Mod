using System;
using System.Collections.Generic;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.GameContent;

namespace UnderwaterHorrors;

public enum GiantSerpentState
{
    Approach,
    Circle,
    Setup,
    Strike,
    Breach,
    Retreat
}

/// <summary>
/// The giant serpent: a rust serpent five times the size that hunts boats.
///
/// It swims like a snake rather than like the smaller serpents: the entity
/// position is the middle of its mouth, which always moves forward along its
/// heading and only turns at a limited rate, and the body is laid along the
/// path the mouth swam (<see cref="Trail"/>, posed on the client by
/// EntityBehaviorGiantSerpentBody). It has no physics behavior; this AI
/// moves it directly and keeps it between the sea floor and the surface.
///
/// Unlike the rust serpent it never rises to hiss or shows its fins between
/// strikes. It circles deep under its prey (Circle), dives and lines up
/// (Setup), then rushes up at it mouth first (Strike). A boat is the prey
/// when its player sits in one: a raft goes in one gulp, a sailboat breaks
/// after a few bites, and the riders land in the water. Swimmers are bitten.
/// After every strike it carries on up out of the water and arcs back down
/// (Breach), then circles again. It leaves (Retreat) when its prey is gone,
/// stays over shallow water, or after GiantSerpentMaxStaySeconds, and when
/// badly hurt.
/// </summary>
public class EntityBehaviorGiantSerpentAI : EntityBehaviorOceanCreature
{
    public const string BitesAttr = "underwaterhorrors:giantSerpentBites";

    private const string AnimIdle = "idle";
    private const string AnimGape = "gape";
    private const string AnimGulp = "gulp";
    private const string AnimDead = "dead";

    // Seconds between server copies of the trail for newly watching clients.
    private const float SeedInterval = 3f;

    // How long the head keeps climbing out of the water after a strike.
    private const float BreachClimbSeconds = 1.0f;

    /// <summary>
    /// Set on a giant for screenshots and model inspection: it circles its
    /// player forever with its back out of the water and never strikes or
    /// leaves. Nothing in normal play sets it.
    /// </summary>
    public const string ShowcaseAttr = "underwaterhorrors:giantShowcase";

    public SerpentTrail Trail { get; private set; }
    public GiantSerpentState State => state;

    private double size = 10;
    private double bodyLength = 44.5;   // size-1 blocks, from the entity attributes

    private GiantSerpentState state = GiantSerpentState.Approach;
    private float stateTimer;
    private bool initialized;
    private float lifeTimer;
    private float seedTimer;

    // Heading in the game's convention (yaw 0 = +Z), pitch above 0 = nose up.
    private double yaw, pitch, speed;

    // Circling
    private int orbitDir = 1;
    private double orbitStart, orbitEnd;
    private double circleDuration;
    private bool firstCircle = true;
    private float unstrikeableTimer;

    // Setup and strike
    private bool setupGainingRoom;
    private bool gaping;
    private double missSide;   // a creative observer gets a near miss on this side

    // Prey: the boat the target player sits in, or the player.
    private Entity prey;
    private Entity preyBoat;
    private double preyVelX, preyVelY, preyVelZ;
    private double lastPreyX, lastPreyY, lastPreyZ;
    private bool hasLastPrey;
    private double surfaceY;
    private int preyWaterDepth;
    private float envScanTimer;

    // Sea floor under the mouth and ahead of it.
    private int floorY = -1, floorAheadY = -1, floorFarY = -1;
    private double smoothLowY = double.NaN;
    private float floorScanTimer;

    public EntityBehaviorGiantSerpentAI(Entity entity) : base(entity) { }

    // ── Sizes, all scaled by the entity size so one number resizes it ──
    private double CruiseDepth => Showcase ? 0.5 * size : 2.8 * size;     // mouth below the surface while circling
    private double StrikeDepth => 4.2 * size;     // depth it dives to before a strike
    private double MinDepthBelowSurface => Showcase ? 0.3 * size : 2.4 * size;  // keeps the dorsal spikes under
    private bool Showcase => entity.WatchedAttributes.GetBool(ShowcaseAttr);
    private double FloorClearance => 1.3 * size;  // belly clear of the sea floor
    private double GulpRange => 0.9 * size;
    private double OuterOrbit => 14 * size;
    private double InnerOrbit => 8 * size;

    public override void Initialize(EntityProperties properties, JsonObject attributes)
    {
        base.Initialize(properties, attributes);
        bodyLength = GiantSerpentRig.BodyLength(properties);
    }

    private void Init()
    {
        initialized = true;
        size = entity.Properties.Client?.Size ?? 10;
        surfaceY = entity.World.SeaLevel;
        yaw = entity.Pos.Yaw;
        pitch = 0;
        speed = config.GiantSerpentCruiseSpeed;
        orbitDir = entity.World.Rand.NextDouble() < 0.5 ? -1 : 1;
        Trail = new SerpentTrail((bodyLength + 4) * size);
        Trail.Reset(entity.Pos.X, entity.Pos.Y, entity.Pos.Z, yaw, 0);
        StartAnim(AnimIdle, 1f);
    }

    public override void OnGameTick(float deltaTime)
    {
        if (entity.Api.Side != EnumAppSide.Server) return;
        if (!entity.Alive) return;
        // A lag spike must not fling a 200-block animal through the sea floor.
        float dt = Math.Min(deltaTime, 0.2f);

        if (!initialized) Init();
        lifeTimer += dt;
        stateTimer += dt;

        ResolveTarget();
        UpdatePrey(dt);
        UpdateFloor(dt);
        UpdateAmbientSound(dt);

        if (state != GiantSerpentState.Retreat)
        {
            if (prey == null)
            {
                Log("prey gone, retreating");
                TransitionTo(GiantSerpentState.Retreat);
            }
            else if (lifeTimer > config.GiantSerpentMaxStaySeconds && !Showcase &&
                     (state == GiantSerpentState.Circle || state == GiantSerpentState.Approach))
            {
                Log($"stayed {lifeTimer:F0}s, retreating");
                TransitionTo(GiantSerpentState.Retreat);
            }
            else if (HealthFraction() < 0.25f)
            {
                Log("badly hurt, retreating");
                TransitionTo(GiantSerpentState.Retreat);
            }
        }

        switch (state)
        {
            case GiantSerpentState.Approach: OnApproach(dt); break;
            case GiantSerpentState.Circle: OnCircle(dt); break;
            case GiantSerpentState.Setup: OnSetup(dt); break;
            case GiantSerpentState.Strike: OnStrike(dt); break;
            case GiantSerpentState.Breach: OnBreach(dt); break;
            case GiantSerpentState.Retreat: OnRetreat(dt); break;
        }
        if (!entity.Alive) return;

        Move(dt);
        Trail.Push(entity.Pos.X, entity.Pos.Y, entity.Pos.Z, yaw);

        seedTimer -= dt;
        if (seedTimer <= 0)
        {
            seedTimer = SeedInterval;
            entity.WatchedAttributes.SetAttribute(EntityBehaviorGiantSerpentBody.SeedAttr,
                new FloatArrayAttribute(Trail.ToSeed(bodyLength * size, EntityBehaviorGiantSerpentBody.SeedSpacing)));
            entity.WatchedAttributes.MarkPathDirty(EntityBehaviorGiantSerpentBody.SeedAttr);
        }
    }

    // ═══════════════════════════════════════════════════════════════════
    //  Prey and surroundings
    // ═══════════════════════════════════════════════════════════════════
    private void UpdatePrey(float dt)
    {
        EntityAgent player = targetPlayer?.Entity;
        if (player == null || !player.Alive)
        {
            prey = preyBoat = null;
            hasLastPrey = false;
            return;
        }

        Entity mount = player.MountedOn?.Entity;
        preyBoat = mount != null && IsBoat(mount) ? mount : null;
        Entity now = preyBoat ?? player;
        if (now != prey) hasLastPrey = false;
        prey = now;

        // Velocity from position changes, smoothed, for leading the strike.
        var p = prey.Pos;
        if (hasLastPrey && dt > 0)
        {
            double k = Math.Min(1, dt * 3);
            preyVelX += ((p.X - lastPreyX) / dt - preyVelX) * k;
            preyVelY += ((p.Y - lastPreyY) / dt - preyVelY) * k;
            preyVelZ += ((p.Z - lastPreyZ) / dt - preyVelZ) * k;
        }
        else
        {
            preyVelX = preyVelY = preyVelZ = 0;
        }
        lastPreyX = p.X; lastPreyY = p.Y; lastPreyZ = p.Z;
        hasLastPrey = true;

        envScanTimer -= dt;
        if (envScanTimer <= 0)
        {
            envScanTimer = 1f;
            surfaceY = WaterSurfaceAt(p.X, p.Y, p.Z, p.Dimension);
            int floor = EntityBehaviorSerpentHarvest.FindRestingYBelow(
                entity.World.BlockAccessor, p.X, surfaceY - 1, p.Z, p.Dimension);
            preyWaterDepth = floor < 0 ? 80 : (int)surfaceY - floor;
        }

        // Swimmers in the shallows or ashore are out of reach, as with the
        // other serpents; boats are judged by the depth under them.
        if (preyBoat == null) UpdateShallowWaterCheck(dt);
    }

    internal static bool IsBoat(Entity e)
    {
        string path = e?.Code?.Path;
        return path != null && (e is EntityBoat || path.StartsWith("boat"));
    }

    private static bool IsRaft(Entity boat) => boat.Code?.Path?.Contains("raft") == true;

    private bool PreyStrikeable()
    {
        if (prey == null) return false;
        if (preyWaterDepth < config.GiantSerpentMinStrikeDepth) return false;
        if (preyBoat == null && IsInShallowWater && !LingerInsteadOfRetreat(0f)) return false;
        return true;
    }

    /// <summary>
    /// The water surface (top of the highest water block, +1) at a column,
    /// searching up from y through water or down a few blocks from a boat.
    /// </summary>
    private double WaterSurfaceAt(double x, double y, double z, int dim)
    {
        var acc = entity.World.BlockAccessor;
        var bp = new BlockPos((int)Math.Floor(x), (int)Math.Floor(y), (int)Math.Floor(z), dim);
        if (WaterHelper.IsWaterBlock(acc.GetBlock(bp)))
        {
            for (int i = 0; i < 128 && WaterHelper.IsWaterBlock(acc.GetBlock(bp.UpCopy())); i++) bp.Y++;
            return bp.Y + 1;
        }
        for (int i = 0; i < 8; i++)
        {
            bp.Y--;
            if (WaterHelper.IsWaterBlock(acc.GetBlock(bp))) return bp.Y + 1;
        }
        return entity.World.SeaLevel;
    }

    private void UpdateFloor(float dt)
    {
        floorScanTimer -= dt;
        if (floorScanTimer <= 0)
        {
            floorScanTimer = 0.25f;
            var acc = entity.World.BlockAccessor;
            var pos = entity.Pos;
            double scanFrom = Math.Max(pos.Y, surfaceY) + 1;
            double sx = Math.Sin(yaw), sz = Math.Cos(yaw);
            floorY = EntityBehaviorSerpentHarvest.FindRestingYBelow(acc, pos.X, scanFrom, pos.Z, pos.Dimension);
            floorAheadY = EntityBehaviorSerpentHarvest.FindRestingYBelow(acc,
                pos.X + sx * 2 * size, scanFrom, pos.Z + sz * 2 * size, pos.Dimension);
            floorFarY = EntityBehaviorSerpentHarvest.FindRestingYBelow(acc,
                pos.X + sx * 4 * size, scanFrom, pos.Z + sz * 4 * size, pos.Dimension);
        }

        // The scans step a whole block at a time, and further ahead the
        // floor can jump by many. Following them directly made the mouth
        // lurch up and down every scan, and the body carried each lurch
        // along as a tilt. Ease toward them instead, rising for a shoal a
        // little quicker than settling into a trench.
        int floor = Math.Max(floorY, Math.Max(floorAheadY, floorFarY));
        if (floor < 0) return;
        double target = floor + FloorClearance;
        if (double.IsNaN(smoothLowY)) smoothLowY = target;
        double rate = target > smoothLowY ? 1.5 : 0.5;
        smoothLowY += (target - smoothLowY) * (1 - Math.Exp(-rate * dt));
    }

    // The lowest the mouth keeps to: clear of the sea floor here and ahead.
    private double LowestY => double.IsNaN(smoothLowY) ? double.NegativeInfinity : smoothLowY;

    // Keeps a cruising depth above the sea floor and under the surface.
    private double CruiseY(double depthBelowSurface)
    {
        double y = surfaceY - depthBelowSurface;
        double low = LowestY;
        double high = surfaceY - MinDepthBelowSurface;
        if (y < low) y = low;
        if (y > high) y = Math.Max(high, low);
        return y;
    }

    // ═══════════════════════════════════════════════════════════════════
    //  States
    // ═══════════════════════════════════════════════════════════════════
    private void TransitionTo(GiantSerpentState next)
    {
        var at = entity.Pos;
        Log($"{state} -> {next} at ({at.X:F0}, {at.Y:F0}, {at.Z:F0}), {surfaceY - at.Y:F0} below the surface");
        state = next;
        stateTimer = 0;

        switch (next)
        {
            case GiantSerpentState.Circle:
            {
                double h = HorizontalDistToPrey();
                orbitStart = firstCircle ? OuterOrbit : GameMath.Clamp(h, InnerOrbit, OuterOrbit);
                orbitEnd = InnerOrbit;
                circleDuration = firstCircle
                    ? Rand(config.GiantSerpentStalkSecondsMin, config.GiantSerpentStalkSecondsMax)
                    : Rand(config.GiantSerpentStrikeCooldownMin, config.GiantSerpentStrikeCooldownMax);
                firstCircle = false;
                unstrikeableTimer = 0;
                break;
            }
            case GiantSerpentState.Setup:
                setupGainingRoom = false;
                break;
            case GiantSerpentState.Strike:
                gaping = false;
                missSide = entity.World.Rand.NextDouble() < 0.5 ? -1 : 1;
                break;
            case GiantSerpentState.Breach:
                StopAnim(AnimGape);
                StartAnim(AnimGulp, 1f, easeIn: 999f);
                break;
            case GiantSerpentState.Retreat:
                StopAnim(AnimGape);
                PlayDive();
                break;
        }
    }

    private void OnApproach(float dt)
    {
        if (prey == null) return;
        double dx = entity.Pos.X - prey.Pos.X, dz = entity.Pos.Z - prey.Pos.Z;
        double h = Math.Sqrt(dx * dx + dz * dz);
        if (h <= OuterOrbit + 2 * size)
        {
            TransitionTo(GiantSerpentState.Circle);
            return;
        }
        // Head for the near side of the outer circle.
        double tx = prey.Pos.X + dx / h * OuterOrbit;
        double tz = prey.Pos.Z + dz / h * OuterOrbit;
        Steer(tx, CruiseY(CruiseDepth), tz, config.GiantSerpentCruiseSpeed * 1.3, 14, 10, 25, dt);
    }

    private void OnCircle(float dt)
    {
        if (prey == null) return;
        double t = circleDuration > 0 ? Math.Min(1, stateTimer / circleDuration) : 1;
        double radius = orbitStart + (orbitEnd - orbitStart) * t;

        // Aim a little ahead of where it is on the circle, in its direction.
        double ang = Math.Atan2(entity.Pos.Z - prey.Pos.Z, entity.Pos.X - prey.Pos.X);
        ang += orbitDir * 25 * GameMath.DEG2RAD;
        double tx = prey.Pos.X + Math.Cos(ang) * radius;
        double tz = prey.Pos.Z + Math.Sin(ang) * radius;
        Steer(tx, CruiseY(CruiseDepth), tz, config.GiantSerpentCruiseSpeed, 14, 10, 25, dt);

        bool strikeable = PreyStrikeable();
        unstrikeableTimer = strikeable ? 0 : unstrikeableTimer + dt;
        if (unstrikeableTimer > 30f && !Showcase)
        {
            Log("prey out of reach for 30s, retreating");
            TransitionTo(GiantSerpentState.Retreat);
            return;
        }
        if (t >= 1 && strikeable && !Showcase) TransitionTo(GiantSerpentState.Setup);
    }

    private void OnSetup(float dt)
    {
        if (prey == null) return;
        if (!PreyStrikeable())
        {
            TransitionTo(GiantSerpentState.Circle);
            return;
        }

        // Dive to strike depth while turning toward the prey, so the strike
        // comes from below and in front. Too close for a run-up, it swims
        // on straight until it has room, then turns back.
        double hx = prey.Pos.X - entity.Pos.X, hz = prey.Pos.Z - entity.Pos.Z;
        double h = Math.Sqrt(hx * hx + hz * hz);
        double deepY = CruiseY(StrikeDepth);
        if (h < 3 * size) setupGainingRoom = true;
        else if (h > 7 * size) setupGainingRoom = false;

        if (setupGainingRoom)
        {
            Steer(entity.Pos.X + Math.Sin(yaw) * 100, deepY, entity.Pos.Z + Math.Cos(yaw) * 100,
                config.GiantSerpentCruiseSpeed * 1.3, 0, 14, 35, dt);
        }
        else
        {
            Steer(prey.Pos.X, deepY, prey.Pos.Z, config.GiantSerpentCruiseSpeed * 1.3, 18, 14, 35, dt);
        }

        double headingDiff = Math.Abs(GameMath.AngleRadDistance((float)yaw, (float)Math.Atan2(hx, hz)));
        bool lined = headingDiff < 35 * GameMath.DEG2RAD;
        bool deep = entity.Pos.Y <= deepY + 0.8 * size || stateTimer > 20f;
        if ((lined && deep && !setupGainingRoom && h <= 12 * size) || stateTimer > 40f)
        {
            Log($"striking from {h:F0} blocks out, {surfaceY - entity.Pos.Y:F0} deep");
            TransitionTo(GiantSerpentState.Strike);
        }
    }

    private void OnStrike(float dt)
    {
        if (prey == null) return;

        GetPreyCenter(out double px, out double py, out double pz);
        double dx = px - entity.Pos.X, dy = py - entity.Pos.Y, dz = pz - entity.Pos.Z;
        double dist = Math.Sqrt(dx * dx + dy * dy + dz * dz);

        // Lead a moving boat; a creative observer is aimed beside, not at.
        double lead = Math.Min(2.0, dist / Math.Max(1, config.GiantSerpentStrikeSpeed));
        double ax = px + preyVelX * lead, ay = py + preyVelY * lead, az = pz + preyVelZ * lead;
        if (TargetIsPassiveObserver)
        {
            double h = Math.Max(1e-3, Math.Sqrt(dx * dx + dz * dz));
            ax += dz / h * missSide * 2 * GulpRange;
            az -= dx / h * missSide * 2 * GulpRange;
        }
        Steer(ax, ay, az, config.GiantSerpentStrikeSpeed, 35, 50, 70, dt);

        if (!gaping && dist < 7 * size)
        {
            gaping = true;
            StartAnim(AnimGape, 1f, easeIn: 4f);
        }

        if (!TargetIsPassiveObserver && dist <= GulpRange)
        {
            Gulp();
            TransitionTo(GiantSerpentState.Breach);
            return;
        }

        // Missed: it has gone past (the prey is behind the mouth), or the
        // strike is dragging on.
        double fx = Math.Sin(yaw) * Math.Cos(pitch), fy = Math.Sin(pitch), fz = Math.Cos(yaw) * Math.Cos(pitch);
        bool past = dist > GulpRange && (fx * dx + fy * dy + fz * dz) < 0;
        if (past || stateTimer > 20f)
        {
            Log(past ? "strike went past" : "strike timed out");
            TransitionTo(GiantSerpentState.Breach);
        }
    }

    private void OnBreach(float dt)
    {
        // Climb on for a second so the head rears well clear of the water,
        // then arc over and dive back down ahead. The body follows the
        // mouth's path, so it rises out of the sea in one long arch.
        double cruiseY = CruiseY(CruiseDepth);
        if (stateTimer < BreachClimbSeconds)
        {
            double up = Math.Max(pitch, 45 * GameMath.DEG2RAD);
            Steer(entity.Pos.X + Math.Sin(yaw) * 100, entity.Pos.Y + Math.Tan(up) * 100,
                entity.Pos.Z + Math.Cos(yaw) * 100, config.GiantSerpentStrikeSpeed * 0.85, 5, 40, 70, dt);
        }
        else
        {
            double ahead = 8 * size;
            Steer(entity.Pos.X + Math.Sin(yaw) * ahead, cruiseY - 2 * size, entity.Pos.Z + Math.Cos(yaw) * ahead,
                config.GiantSerpentCruiseSpeed * 1.5, 10, 45, 70, dt);
        }

        bool backDown = stateTimer > BreachClimbSeconds && entity.Pos.Y < cruiseY + 0.5 * size && pitch <= 0;
        if (backDown || stateTimer > 15f)
        {
            StopAnim(AnimGulp);
            TransitionTo(GiantSerpentState.Circle);
        }
    }

    private void OnRetreat(float dt)
    {
        double ax, az;
        if (prey != null)
        {
            ax = entity.Pos.X - prey.Pos.X;
            az = entity.Pos.Z - prey.Pos.Z;
        }
        else
        {
            ax = Math.Sin(yaw);
            az = Math.Cos(yaw);
        }
        double h = Math.Max(1e-3, Math.Sqrt(ax * ax + az * az));
        Steer(entity.Pos.X + ax / h * 100, LowestY, entity.Pos.Z + az / h * 100,
            config.GiantSerpentCruiseSpeed * 1.4, 12, 12, 30, dt);

        double far = prey == null ? 0 : Math.Sqrt(Sq(entity.Pos.X - prey.Pos.X) + Sq(entity.Pos.Z - prey.Pos.Z));
        if (stateTimer > 45f || far > 30 * size)
        {
            Log("gone");
            entity.Die(EnumDespawnReason.Expire);
        }
    }

    // ═══════════════════════════════════════════════════════════════════
    //  The bite
    // ═══════════════════════════════════════════════════════════════════
    private void Gulp()
    {
        PlayBite();
        SplashAt(entity.Pos.X, Math.Min(entity.Pos.Y, surfaceY), entity.Pos.Z);

        // Everything in the mouth: the prey and anyone else unlucky enough
        // to be swimming or sailing next to it. Swimmers are picked before
        // any boat goes down, so a rider thrown out of a swallowed boat
        // takes the rider damage only, not a second, full bite.
        Entity[] near = entity.World.GetEntitiesAround(entity.Pos.XYZ, (float)GulpRange + 3, (float)GulpRange + 3,
            e => e.Alive && (e is EntityPlayer || IsBoat(e)));
        var swimmers = new List<EntityPlayer>();
        var boats = new List<Entity>();
        foreach (Entity e in near)
        {
            if (e is EntityPlayer ep)
            {
                if (ep.MountedOn?.Entity != null && IsBoat(ep.MountedOn.Entity)) continue;   // their boat takes the bite
                if (IsPassiveObserver(ep)) continue;
                if (ep.Pos.DistanceTo(entity.Pos.XYZ) <= GulpRange + 1) swimmers.Add(ep);
            }
            else if (e.Pos.DistanceTo(entity.Pos.XYZ) <= GulpRange + 2)
            {
                boats.Add(e);
            }
        }
        foreach (EntityPlayer swimmer in swimmers) BitePlayer(swimmer, config.GiantSerpentBiteDamage);
        foreach (Entity boat in boats) BiteBoat(boat);
    }

    private void BiteBoat(Entity boat)
    {
        var riders = new List<EntityPlayer>();
        foreach (IPlayer p in entity.World.AllOnlinePlayers)
        {
            if (p.Entity?.MountedOn?.Entity == boat) riders.Add(p.Entity);
        }
        if (riders.Exists(IsPassiveObserver)) return;

        int bites = boat.WatchedAttributes.GetInt(BitesAttr) + 1;
        bool raft = IsRaft(boat);
        if (raft || bites >= config.GiantSerpentBitesToSinkBoat)
        {
            Log($"swallowed {boat.Code} ({riders.Count} rider(s))");
            // Death unmounts the riders and drops anything stored aboard,
            // the same as a boat burning down.
            boat.Die(EnumDespawnReason.Death, new DamageSource
            {
                Source = EnumDamageSource.Entity,
                SourceEntity = entity,
                Type = EnumDamageType.PiercingAttack
            });
            foreach (EntityPlayer rider in riders)
            {
                if (rider.MountedOn != null) rider.TryUnmount();
                BitePlayer(rider, config.GiantSerpentRiderDamage);
            }
        }
        else
        {
            boat.WatchedAttributes.SetInt(BitesAttr, bites);
            Log($"bit {boat.Code}, {bites}/{config.GiantSerpentBitesToSinkBoat} bites to sink it");
        }
    }

    private void BitePlayer(EntityPlayer player, float damage)
    {
        if (damage <= 0) return;
        player.ReceiveDamage(new DamageSource
        {
            Source = EnumDamageSource.Entity,
            SourceEntity = entity,
            Type = EnumDamageType.PiercingAttack,
            DamageTier = config.GiantSerpentDamageTier
        }, damage);
        Log($"bit {player.Player?.PlayerName} for {damage}");
    }

    private bool IsPassiveObserver(EntityPlayer player)
    {
        if (!config.IgnoreCreativePlayers) return false;
        EnumGameMode? mode = player?.Player?.WorldData?.CurrentGameMode;
        return mode == EnumGameMode.Creative || mode == EnumGameMode.Spectator;
    }

    private void SplashAt(double x, double y, double z)
    {
        double r = GulpRange;
        var splash = new SimpleParticleProperties(
            120, 160, ColorUtil.ToRgba(200, 210, 230, 255),
            new Vec3d(x - r, y - 1, z - r), new Vec3d(x + r, y + 1, z + r),
            new Vec3f(-4, 6, -4), new Vec3f(4, 14, 4),
            2.5f, 1f, 0.6f, 1.6f, EnumParticleModel.Quad);
        splash.WithTerrainCollision = false;
        entity.World.SpawnParticles(splash);
    }

    // ═══════════════════════════════════════════════════════════════════
    //  Movement
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Turns the heading toward a point at limited yaw and pitch rates (in
    /// degrees per second) and eases the speed toward wantSpeed (blocks per
    /// second). Never moves sideways or backward: the body has to be able
    /// to follow the mouth's path.
    /// </summary>
    private void Steer(double tx, double ty, double tz, double wantSpeed,
        double yawRateDeg, double pitchRateDeg, double maxPitchDeg, float dt)
    {
        double dx = tx - entity.Pos.X, dy = ty - entity.Pos.Y, dz = tz - entity.Pos.Z;
        double h = Math.Sqrt(dx * dx + dz * dz);
        double maxPitch = maxPitchDeg * GameMath.DEG2RAD;

        if (h > 1e-3)
        {
            double wantYaw = Math.Atan2(dx, dz);
            double turn = GameMath.AngleRadDistance((float)yaw, (float)wantYaw);
            double maxTurn = yawRateDeg * GameMath.DEG2RAD * dt;
            yaw = GameMath.Mod(yaw + Math.Clamp(turn, -maxTurn, maxTurn), GameMath.TWOPI);
        }

        double wantPitch = Math.Clamp(Math.Atan2(dy, Math.Max(h, 0.5)), -maxPitch, maxPitch);
        double maxPitchStep = pitchRateDeg * GameMath.DEG2RAD * dt;
        pitch += Math.Clamp(wantPitch - pitch, -maxPitchStep, maxPitchStep);
        pitch = Math.Clamp(pitch, -maxPitch, maxPitch);

        // Big animals change speed slowly; it takes a few seconds to reach a
        // full strike.
        double accel = 0.8 * size * dt;
        speed += Math.Clamp(wantSpeed - speed, -accel, accel);
    }

    private void Move(float dt)
    {
        var pos = entity.Pos;
        double cp = Math.Cos(pitch);
        double vx = Math.Sin(yaw) * cp * speed;
        double vy = Math.Sin(pitch) * speed;
        double vz = Math.Cos(yaw) * cp * speed;

        pos.X += vx * dt;
        pos.Y += vy * dt;
        pos.Z += vz * dt;

        // Between the sea floor and, outside a strike, under the surface.
        // Out of bounds it is eased back over a second or so and levels
        // out, rather than snapped back: a snap kinks the mouth's path, and
        // the body would carry every kink along behind it.
        bool striking = state == GiantSerpentState.Strike || state == GiantSerpentState.Breach;
        double low = LowestY;
        double high = striking ? surfaceY + 2.5 * size : surfaceY - MinDepthBelowSurface;
        if (high < low) high = low;
        double ease = 0.5 * size * dt;
        double level = 25 * GameMath.DEG2RAD * dt;
        if (pos.Y < low)
        {
            pos.Y += Math.Min(low - pos.Y, Math.Max(0, -vy) * dt + ease);
            if (pitch < 0) pitch = Math.Min(0, pitch + level);
        }
        else if (pos.Y > high)
        {
            pos.Y -= Math.Min(pos.Y - high, Math.Max(0, vy) * dt + ease);
            if (pitch > 0) pitch = Math.Max(0, pitch - level);
        }
        // Never into the sea floor itself, whatever the easing says.
        if (floorY >= 0 && pos.Y < floorY + 0.4 * size) pos.Y = floorY + 0.4 * size;

        // The model turns with yaw only; the spine takes every bend, so the
        // entity itself always stays level.
        pos.Yaw = (float)yaw;
        pos.Pitch = 0;
        pos.Roll = 0;
        // Physics units are blocks per 1/60 s.
        pos.Motion.Set(vx / 60, vy / 60, vz / 60);
    }

    // ═══════════════════════════════════════════════════════════════════
    //  Damage, death, helpers
    // ═══════════════════════════════════════════════════════════════════
    public override void OnEntityReceiveDamage(DamageSource damageSource, ref float damage)
    {
        base.OnEntityReceiveDamage(damageSource, ref damage);
        if (entity.Api.Side != EnumAppSide.Server || !entity.Alive) return;
        if (damage <= 0 || damageSource?.Type == EnumDamageType.Heal) return;

        // Hurting it cuts its patience short: the next strike comes soon.
        if (state == GiantSerpentState.Circle && circleDuration - stateTimer > 5)
        {
            circleDuration = stateTimer + 5;
        }
    }

    public override void OnEntityDeath(DamageSource damageSourceForDeath)
    {
        base.OnEntityDeath(damageSourceForDeath);
        if (entity.Api.Side != EnumAppSide.Server) return;
        // The corpse keeps its last curve: "dead" names every spine bone,
        // which keeps the client's bend offsets drawn after the swim stops.
        StopAnim(AnimIdle);
        StopAnim(AnimGape);
        StopAnim(AnimGulp);
        StartAnim(AnimDead, 1f);
    }

    private float HealthFraction()
    {
        var health = entity.GetBehavior<EntityBehaviorHealth>();
        if (health == null || health.MaxHealth <= 0) return 1f;
        return health.Health / health.MaxHealth;
    }

    private void GetPreyCenter(out double x, out double y, out double z)
    {
        x = prey.Pos.X;
        y = prey.Pos.Y + (preyBoat != null ? 0.5 : 0.9);
        z = prey.Pos.Z;
    }

    private double HorizontalDistToPrey()
    {
        if (prey == null) return OuterOrbit;
        return Math.Sqrt(Sq(entity.Pos.X - prey.Pos.X) + Sq(entity.Pos.Z - prey.Pos.Z));
    }

    private void StartAnim(string code, float speed, float easeIn = 10f)
    {
        entity.AnimManager.StartAnimation(new AnimationMetaData
        {
            Animation = code,
            Code = code,
            AnimationSpeed = speed,
            EaseInSpeed = easeIn,
            EaseOutSpeed = 999f,
            BlendMode = EnumAnimationBlendMode.Add
        }.Init());
    }

    private void StopAnim(string code)
    {
        if (entity.AnimManager.IsAnimationActive(code)) entity.AnimManager.StopAnimation(code);
    }

    private double Rand(float min, float max) => min + entity.World.Rand.NextDouble() * (max - min);

    private static double Sq(double v) => v * v;

    private void Log(string msg)
    {
        if (config?.DebugLogging == true)
            UnderwaterHorrorsModSystem.DebugLog(entity.Api, "Giant serpent: " + msg);
    }

    // ═══════════════════════════════════════════════════════════════════
    //  Spawning
    // ═══════════════════════════════════════════════════════════════════
    public static readonly AssetLocation EntityCode = new("underwaterhorrors", "giantserpent");

    /// <summary>
    /// Spawns a giant serpent in open water between minDist and maxDist
    /// blocks from the player, facing them, at its cruising depth. Returns
    /// null when no column that far out is open deep water.
    /// </summary>
    public static Entity SpawnNear(ICoreServerAPI sapi, IServerPlayer player, double minDist, double maxDist)
    {
        var cfg = UnderwaterHorrorsModSystem.Config;
        EntityProperties props = sapi.World.GetEntityType(EntityCode);
        if (props == null || player?.Entity == null || cfg == null) return null;

        double size = props.Client?.Size ?? 10;
        var rand = sapi.World.Rand;
        var ppos = player.Entity.Pos;
        int dim = ppos.Dimension;

        for (int attempt = 0; attempt < 16; attempt++)
        {
            double angle = rand.NextDouble() * Math.PI * 2;
            double dist = minDist + rand.NextDouble() * Math.Max(0, maxDist - minDist);
            double x = ppos.X + Math.Cos(angle) * dist;
            double z = ppos.Z + Math.Sin(angle) * dist;
            if (!SerpentPlacement.TryFindOpenWaterColumn(sapi.World, x, z, dim,
                cfg.SerpentSpawnWaterClearance, out int yMin, out int yMax)) continue;

            double surface = yMax + cfg.SerpentSpawnWaterClearance + 1;
            double y = Math.Clamp(surface - 2.8 * size, yMin + 0.5, yMax + 0.5);

            Entity giant = sapi.World.ClassRegistry.CreateEntity(props);
            giant.Pos.SetPos(x, y, z);
            giant.Pos.Dimension = dim;
            giant.Pos.Yaw = (float)Math.Atan2(ppos.X - x, ppos.Z - z);
            giant.Pos.SetFrom(giant.Pos);
            giant.WatchedAttributes.SetString("underwaterhorrors:targetPlayerUid", player.PlayerUID);
            sapi.World.SpawnEntity(giant);
            UnderwaterHorrorsModSystem.ApplyConfiguredHealth(giant);

            if (cfg.DebugLogging)
                UnderwaterHorrorsModSystem.DebugLog(sapi,
                    $"SPAWNED giant serpent targeting {player.PlayerName} at ({x:F0}, {y:F0}, {z:F0}), {dist:F0} blocks out");
            return giant;
        }
        return null;
    }

    public override string PropertyName() => "underwaterhorrors:giantserpentai";
}
