using System;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;

namespace UnderwaterHorrors;

/// <summary>
/// One piece of a killed giant serpent: a single spine bone with its fins,
/// spikes and teeth, in its own shape (shapes/entity/giantserpentpiece,
/// built by tools/giantserpent/build_shape.py, pivoted at the bone's
/// head-side joint). EntityBehaviorGiantSerpentBreakup lays it where that
/// bone was drawn, tilted with the body's curve.
///
/// On the server it drifts a little way from its neighbours, levels out and
/// sinks to the sea floor, where it comes to rest on its belly: the floor's
/// highest point under its two ends and middle, plus the depth of its body
/// below the pivot once rolled (it falls through air quickly). There it
/// lies until GiantSerpentPieceDecayHours have passed, then crumbles into a
/// pile of bones. It moves with the giant's own client smoothing
/// (giantserpentmotion, keeping its tilt), and each client rolls it onto its
/// side or its back about the body as it sinks; the roll comes from the
/// entity id, so the server rests it at the same height every client draws.
///
/// The pieces are saved with their chunk, cannot be hit or looked at, and
/// keep ticking while they sink, however far the player is.
/// </summary>
public class GiantSerpentPieceEntity : Entity
{
    /// <summary>The piece's solid body around its pivot, in blocks, in its
    /// rest frame: back from, back to, below, above, half width.</summary>
    public const string ExtentAttr = "underwaterhorrors:pieceExtent";
    /// <summary>Calendar hour it broke off, for the decay.</summary>
    public const string BornAttr = "underwaterhorrors:pieceBornHours";
    public const string SettledAttr = "underwaterhorrors:pieceSettled";

    // Blocks per second through water, and through air above it.
    private const float SinkSpeedMin = 0.7f, SinkSpeedMax = 1.3f;
    private const float FallSpeed = 8f;
    // Per second: how fast the drift and spin die away, how fast it levels
    // out, and the client's barrel roll in radians.
    private const float DriftDecay = 0.4f;
    private const float LevelRate = 0.35f;
    private const float RollRate = 0.5f;

    private float sinkSpeed = 1f;
    private double driftX, driftZ;
    private float spin;
    private double restY = double.NaN;
    private float rescanTimer, decayTimer;
    private readonly BlockPos tmpPos = new(0);

    private float clientRoll;
    private bool clientRollStarted;

    public bool Settled => WatchedAttributes.GetBool(SettledAttr);

    public override bool IsInteractable => false;

    public override bool AlwaysActive => !Settled;

    public override bool ApplyGravity => false;

    public override double FrustumSphereRadius
    {
        get
        {
            float[] e = Extent;
            return e == null ? 40 : Math.Max(Math.Abs(e[0]), Math.Abs(e[1])) + e[4] + 4;
        }
    }

    private float[] Extent => WatchedAttributes[ExtentAttr] is FloatArrayAttribute a && a.value.Length >= 5 ? a.value : null;

    public override void Initialize(EntityProperties properties, ICoreAPI api, long InChunkIndex3d)
    {
        base.Initialize(properties, api, InChunkIndex3d);
        // Clients keep the far end of a 200-block body in sight too.
        SimulationRange = Math.Max(SimulationRange, 300);
        if (api.Side != EnumAppSide.Server) return;
        sinkSpeed = SinkSpeedMin + (float)api.World.Rand.NextDouble() * (SinkSpeedMax - SinkSpeedMin);
        driftX = Attributes.GetDouble("driftX");
        driftZ = Attributes.GetDouble("driftZ");
        spin = Attributes.GetFloat("spin");
    }

    public override void OnGameTick(float dt)
    {
        base.OnGameTick(dt);
        if (World.Side == EnumAppSide.Client)
        {
            RollOver(dt);
            return;
        }
        if (!Alive) return;
        if (!Settled) Sink(Math.Min(dt, 0.2f));

        decayTimer -= dt;
        if (decayTimer <= 0)
        {
            decayTimer = 5f;
            CheckDecay();
        }
    }

    // ═══════════════════════════════════════════════════════════════════
    //  Sinking (server)
    // ═══════════════════════════════════════════════════════════════════
    private void Sink(float dt)
    {
        var pos = Pos;
        rescanTimer -= dt;
        if (rescanTimer <= 0)
        {
            rescanTimer = 0.5f;
            restY = RestingY();
        }

        double fade = Math.Exp(-DriftDecay * dt);
        driftX *= fade;
        driftZ *= fade;
        spin *= (float)fade;
        pos.X += driftX * dt;
        pos.Z += driftZ * dt;
        pos.Yaw = GameMath.Mod(pos.Yaw + spin * dt, GameMath.TWOPI);
        pos.Roll *= (float)Math.Exp(-LevelRate * dt);

        tmpPos.Set((int)Math.Floor(pos.X), (int)Math.Floor(pos.Y), (int)Math.Floor(pos.Z));
        tmpPos.dimension = pos.Dimension;
        bool inWater = World.BlockAccessor.GetBlock(tmpPos, BlockLayersAccess.Fluid)?.IsLiquid() == true;
        double oldY = pos.Y;
        double y = oldY - (inWater ? sinkSpeed : FallSpeed) * dt;

        bool down = !double.IsNaN(restY) && y <= restY;
        if (down) y = restY;
        pos.Y = y;

        // It lies flat on the floor: settle once it has levelled out too.
        if (down && Math.Abs(pos.Roll) < 0.02f)
        {
            pos.Roll = 0;
            pos.Motion.Set(0, 0, 0);
            WatchedAttributes.SetBool(SettledAttr, true);
            return;
        }
        // Physics units are blocks per 1/60 s; the client dead-reckons on them.
        pos.Motion.Set(driftX / 60, (y - oldY) / dt / 60, driftZ / 60);
    }

    /// <summary>
    /// Where the pivot comes to rest: its belly on the highest floor under
    /// its two ends and its middle. NaN while no floor is in scan range.
    /// </summary>
    private double RestingY()
    {
        float[] e = Extent;
        var pos = Pos;
        if (e == null)
        {
            int f = EntityBehaviorSerpentHarvest.FindRestingYBelow(World.BlockAccessor, pos.X, pos.Y + 2, pos.Z, pos.Dimension);
            return f < 0 ? double.NaN : f;
        }

        // The model runs tailward along +x, which the renderer turns to
        // (-sin yaw, 0, -cos yaw).
        double fx = -Math.Sin(pos.Yaw), fz = -Math.Cos(pos.Yaw);
        int best = -1;
        foreach (double along in new[] { (double)e[0], (e[0] + e[1]) / 2.0, e[1] })
        {
            int f = EntityBehaviorSerpentHarvest.FindRestingYBelow(World.BlockAccessor,
                pos.X + fx * along, pos.Y + 2, pos.Z + fz * along, pos.Dimension);
            best = Math.Max(best, f);
        }
        if (best < 0) return double.NaN;

        // Lowest point of the cross-section once rolled about the body:
        // y' = y cos a - z sin a over its four corners.
        double a = RollFor(EntityId), c = Math.Cos(a), s = Math.Abs(Math.Sin(a));
        double low = Math.Min(e[2] * c, e[3] * c) - e[4] * s;
        return best - low;
    }

    /// <summary>
    /// The piece's final roll about its body: somewhere between lying on its
    /// side and belly up, picked from its entity id so the server and every
    /// client agree.
    /// </summary>
    internal static double RollFor(long id)
    {
        uint h = unchecked((uint)(id * 2654435761L) ^ (uint)(id >> 32));
        double u = (h & 0xFFFF) / 65535.0;
        double sign = ((h >> 16) & 1) == 0 ? 1 : -1;
        return sign * (0.4 + 0.6 * u) * Math.PI;
    }

    // ═══════════════════════════════════════════════════════════════════
    //  Decay (server)
    // ═══════════════════════════════════════════════════════════════════
    private void CheckDecay()
    {
        float hours = UnderwaterHorrorsModSystem.Config?.GiantSerpentPieceDecayHours ?? 48f;
        if (hours <= 0) return;
        double now = World.Calendar.TotalHours;
        if (now - WatchedAttributes.GetDouble(BornAttr, now) < hours) return;

        float[] e = Extent;
        double mid = e == null ? 0 : (e[0] + e[1]) / 2.0;
        double x = Pos.X - Math.Sin(Pos.Yaw) * mid, z = Pos.Z - Math.Cos(Pos.Yaw) * mid;
        EntityBehaviorSerpentHarvest.PlaceBonePile(World, x, Pos.Y + 1, z, Pos.Dimension);
        Die(EnumDespawnReason.Expire);
    }

    // ═══════════════════════════════════════════════════════════════════
    //  The roll (client)
    // ═══════════════════════════════════════════════════════════════════
    // The renderer's xangle is its innermost turn, about the model's own
    // long axis: a barrel roll. Nothing else writes it while waterBobbing is
    // off. A piece first seen already on the floor shows its final roll.
    private void RollOver(float dt)
    {
        if (Properties.Client?.Renderer is not EntityShapeRenderer esr) return;
        float target = (float)RollFor(EntityId);
        if (!clientRollStarted)
        {
            clientRollStarted = true;
            if (Settled) clientRoll = target;
        }
        float step = RollRate * dt;
        clientRoll = Math.Abs(target - clientRoll) <= step ? target : clientRoll + Math.Sign(target - clientRoll) * step;
        esr.waterBobbing = false;
        esr.xangle = clientRoll;
    }
}
