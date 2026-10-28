using System;
using System.Collections.Generic;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.API.Util;
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
/// pile of bones.
///
/// Each piece holds its share of the giant's hoard (the GiantSerpentPiece
/// loot settings). One right click with a knife anywhere on it cuts it open:
/// the loot spills out along its body into the water, and the piece crumbles
/// at once. The game itself only picks entities whose position is within
/// reach of the eye, and a piece's position is its pivot at one end of a body
/// up to 20 blocks long, so the client casts the click against the piece's
/// box itself (ClientTryCut) and sends it as a SegmentCutMessage, whose reach
/// the server also measures to the body. The selection box is the box around
/// its body at its current heading, which is roomy whenever it lies
/// diagonally.
///
/// It moves with the giant's own client smoothing
/// (giantserpentmotion, keeping its tilt), and each client rolls it onto its
/// side or its back about the body as it sinks; the roll comes from the
/// entity id, so the server rests it at the same height every client draws.
///
/// The pieces are saved with their chunk, cannot be hit, and keep ticking
/// while they sink, however far the player is.
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
    // Durability a cut costs the knife, as much as a vanilla harvest.
    private const int KnifeWear = 3;
    private static readonly AssetLocation CutSound = new("game", "sounds/player/scrape");

    private float sinkSpeed = 1f;
    private double driftX, driftZ;
    private float spin;
    private double restY = double.NaN;
    private float rescanTimer, decayTimer;
    private readonly BlockPos tmpPos = new(0);

    private float clientRoll;
    private bool clientRollStarted;
    private float boxYaw = float.NaN;

    public bool Settled => WatchedAttributes.GetBool(SettledAttr);

    public override bool IsInteractable => true;

    public override bool ShouldReceiveDamage(DamageSource damageSource, float damage) => false;

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
        // A knife's reach is measured to this box, before the first tick too.
        UpdateBox();
        if (api.Side != EnumAppSide.Server) return;
        sinkSpeed = SinkSpeedMin + (float)api.World.Rand.NextDouble() * (SinkSpeedMax - SinkSpeedMin);
        driftX = Attributes.GetDouble("driftX");
        driftZ = Attributes.GetDouble("driftZ");
        spin = Attributes.GetFloat("spin");
    }

    public override void OnGameTick(float dt)
    {
        base.OnGameTick(dt);
        UpdateBox();
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
    //  Selection box (both sides)
    // ═══════════════════════════════════════════════════════════════════
    // The box around the body at its heading: along its length from the
    // pivot, as wide as it is, and as deep as its widest part either way,
    // since it lies rolled.
    private void UpdateBox()
    {
        float[] e = Extent;
        if (e == null || Math.Abs(Pos.Yaw - boxYaw) < 0.02f) return;
        boxYaw = Pos.Yaw;
        double fx = -Math.Sin(Pos.Yaw), fz = -Math.Cos(Pos.Yaw);
        float r = Math.Max(e[4], Math.Max(Math.Abs(e[2]), Math.Abs(e[3])));
        float x1 = (float)Math.Min(fx * e[0], fx * e[1]) - r, x2 = (float)Math.Max(fx * e[0], fx * e[1]) + r;
        float z1 = (float)Math.Min(fz * e[0], fz * e[1]) - r, z2 = (float)Math.Max(fz * e[0], fz * e[1]) + r;
        SelectionBox = new Cuboidf(x1, -r, z1, x2, r, z2);
        OriginSelectionBox = SelectionBox.Clone();
    }

    // ═══════════════════════════════════════════════════════════════════
    //  The knife
    // ═══════════════════════════════════════════════════════════════════
    private static bool IsKnife(ItemSlot slot) => slot?.Itemstack?.Collectible?.Tool == EnumTool.Knife;

    public override void OnInteract(EntityAgent byEntity, ItemSlot slot, Vec3d hitPosition, EnumInteractMode mode)
    {
        if (mode != EnumInteractMode.Interact || !IsKnife(slot))
        {
            base.OnInteract(byEntity, slot, hitPosition, mode);
            return;
        }
        if (World.Side == EnumAppSide.Client)
        {
            ((ICoreClientAPI)Api).Network.GetChannel("underwaterhorrors").SendPacket(new SegmentCutMessage { EntityId = EntityId });
        }
        else if (byEntity is EntityPlayer player)
        {
            // The game's own click, when it reaches the server; the message
            // above then finds the piece gone.
            TryCutOpen(player);
        }
    }

    /// <summary>
    /// Cuts the piece open for a player holding a knife within reach of its
    /// body: its loot spills out and it crumbles. False if it cannot.
    /// </summary>
    public bool TryCutOpen(EntityPlayer byPlayer)
    {
        if (!Alive || World.Side != EnumAppSide.Server || byPlayer?.Player is not IServerPlayer player) return false;
        ItemSlot slot = player.InventoryManager.ActiveHotbarSlot;
        if (!IsKnife(slot)) return false;
        Cuboidd body = SelectionBox.ToDouble().Translate(Pos.X, Pos.Y, Pos.Z);
        Vec3d eye = byPlayer.Pos.XYZ.Add(byPlayer.LocalEyePos);
        if (body.ShortestDistanceFrom(eye) > player.WorldData.PickingRange + 1) return false;

        SpillLoot();
        World.PlaySoundAt(CutSound, byPlayer, null, true, 16);
        slot.Itemstack.Collectible.DamageItem(World, byPlayer, slot, KnifeWear);
        Crumble();
        return true;
    }

    /// <summary>
    /// The client's right click, before the game handles it: with a knife in
    /// hand, the player's look against every nearby piece's box. When a piece
    /// is the nearest thing it hits, the cut goes to the server and the
    /// click is taken.
    /// </summary>
    internal static bool ClientTryCut(ICoreClientAPI capi)
    {
        IClientPlayer plr = capi.World.Player;
        if (plr?.Entity == null || !IsKnife(plr.InventoryManager.ActiveHotbarSlot)) return false;
        EntityPlayer ep = plr.Entity;
        Vec3d eye = ep.Pos.XYZ.Add(ep.LocalEyePos);
        // The ray the game picks with (GameMain.RayTraceForSelection).
        Ray ray = Ray.FromAngles(eye, ep.Pos.Pitch, ep.Pos.Yaw, plr.WorldData.PickingRange);

        GiantSerpentPieceEntity hit = null;
        double best = double.MaxValue;
        foreach (Entity e in capi.World.LoadedEntities.Values)
        {
            if (e is not GiantSerpentPieceEntity piece || !piece.Alive) continue;
            if (piece.Pos.XYZ.SquareDistanceTo(eye) > ClientCutSearch * ClientCutSearch) continue;
            Cuboidd box = piece.SelectionBox.ToDouble().Translate(piece.Pos.X, piece.Pos.Y, piece.Pos.Z);
            if (RayEnters(box, ray, out double d) && d < best)
            {
                best = d;
                hit = piece;
            }
        }
        if (hit == null) return false;

        // Not through a block, or a creature the game picked nearer.
        BlockSelection bs = plr.CurrentBlockSelection;
        if (bs != null && new Vec3d(bs.Position.X, bs.Position.InternalY, bs.Position.Z).Add(bs.HitPosition).DistanceTo(eye) < best) return false;
        EntitySelection es = plr.CurrentEntitySelection;
        if (es != null && es.Entity != hit && es.Entity.Pos.XYZ.Add(es.HitPosition).DistanceTo(eye) < best) return false;

        capi.Network.GetChannel("underwaterhorrors").SendPacket(new SegmentCutMessage { EntityId = hit.EntityId });
        return true;
    }

    // Pivots this far from the eye are looked at: the longest piece, plus reach.
    private const double ClientCutSearch = 30;

    // How far along the ray it enters the box, within the ray's length.
    private static bool RayEnters(Cuboidd box, Ray ray, out double distance)
    {
        double t0 = 0, t1 = 1;
        Vec3d o = ray.origin, d = ray.dir;
        distance = 0;
        if (!Slab(o.X, d.X, box.X1, box.X2, ref t0, ref t1)) return false;
        if (!Slab(o.Y, d.Y, box.Y1, box.Y2, ref t0, ref t1)) return false;
        if (!Slab(o.Z, d.Z, box.Z1, box.Z2, ref t0, ref t1)) return false;
        distance = t0 * ray.Length;
        return true;
    }

    private static bool Slab(double o, double d, double min, double max, ref double t0, ref double t1)
    {
        if (Math.Abs(d) < 1e-9) return o >= min && o <= max;
        double a = (min - o) / d, b = (max - o) / d;
        if (a > b) (a, b) = (b, a);
        t0 = Math.Max(t0, a);
        t1 = Math.Min(t1, b);
        return t0 <= t1;
    }

    public override WorldInteraction[] GetInteractionHelp(IClientWorldAccessor world, EntitySelection es, IClientPlayer player)
    {
        return new[]
        {
            new WorldInteraction
            {
                ActionLangCode = "underwaterhorrors:piecehelp-cut",
                MouseButton = EnumMouseButton.Right,
                Itemstacks = ObjectCacheUtil.GetToolStacks(world.Api, EnumTool.Knife)
            }
        };
    }

    // ═══════════════════════════════════════════════════════════════════
    //  Loot and decay (server)
    // ═══════════════════════════════════════════════════════════════════
    /// <summary>A fresh roll of this piece's share of the hoard.</summary>
    public ItemStack[] RollLoot()
    {
        var cfg = UnderwaterHorrorsModSystem.Config;
        var loot = new List<ItemStack>();
        if (cfg == null) return loot.ToArray();
        IWorldAccessor world = World;
        var rand = world.Rand;
        void Add(AssetLocation code, int min, int max) =>
            EntityBehaviorSerpentHarvest.AddStacks(world, loot, code, min + rand.Next(Math.Max(0, max - min) + 1));

        Add(EntityBehaviorSerpentHarvest.GearAsset, cfg.GiantSerpentPieceRustyGearsMin, cfg.GiantSerpentPieceRustyGearsMax);
        Add(new AssetLocation("game", "gear-temporal"), cfg.GiantSerpentPieceTemporalGearsMin, cfg.GiantSerpentPieceTemporalGearsMax);
        foreach (string type in EntityBehaviorSerpentHarvest.JonasPartTypes)
            Add(new AssetLocation("game", "jonasparts-" + type), cfg.GiantSerpentPieceJonasPartsMin, cfg.GiantSerpentPieceJonasPartsMax);
        Add(EntityBehaviorSerpentHarvest.FishAsset, cfg.GiantSerpentPieceFishMin, cfg.GiantSerpentPieceFishMax);
        Add(EntityBehaviorSerpentHarvest.BoneAsset, cfg.GiantSerpentPieceBonesMin, cfg.GiantSerpentPieceBonesMax);
        return loot.ToArray();
    }

    // Each stack comes out somewhere along the body with a little push up
    // and out; the water does the rest: fish and bone float up, metal sinks.
    private void SpillLoot()
    {
        float[] e = Extent;
        var rand = World.Rand;
        double fx = -Math.Sin(Pos.Yaw), fz = -Math.Cos(Pos.Yaw);
        double from = e?[0] ?? 0, to = e?[1] ?? 0, spread = e == null ? 0.5 : e[4] * 0.6;
        foreach (ItemStack stack in RollLoot())
        {
            double along = from + (to - from) * rand.NextDouble();
            var at = new Vec3d(
                Pos.X + fx * along + (rand.NextDouble() - 0.5) * spread,
                Pos.Y + 0.5,
                Pos.Z + fz * along + (rand.NextDouble() - 0.5) * spread);
            OutOfTheFloor(at);
            var push = new Vec3d((rand.NextDouble() - 0.5) * 0.1, 0.05 + rand.NextDouble() * 0.08, (rand.NextDouble() - 0.5) * 0.1);
            World.SpawnItemEntity(stack, at, push);
        }
        double mid = (from + to) / 2;
        EntityBehaviorGiantSerpentBreakup.Burst(World, Pos.X + fx * mid, Pos.Y, Pos.Z + fz * mid, e == null ? 1f : e[4]);
    }

    // Up out of any solid block the body lies against.
    private void OutOfTheFloor(Vec3d at)
    {
        for (int i = 0; i < 6; i++)
        {
            tmpPos.Set((int)Math.Floor(at.X), (int)Math.Floor(at.Y), (int)Math.Floor(at.Z));
            tmpPos.dimension = Pos.Dimension;
            Cuboidf[] boxes = World.BlockAccessor.GetBlock(tmpPos).CollisionBoxes;
            if (boxes == null || boxes.Length == 0) return;
            at.Y = tmpPos.Y + 1.2;
        }
    }

    private void CheckDecay()
    {
        float hours = UnderwaterHorrorsModSystem.Config?.GiantSerpentPieceDecayHours ?? 48f;
        if (hours <= 0) return;
        double now = World.Calendar.TotalHours;
        if (now - WatchedAttributes.GetDouble(BornAttr, now) < hours) return;
        Crumble();
    }

    /// <summary>Leaves a pile of bones under its middle and goes.</summary>
    public void Crumble()
    {
        if (!Alive || World.Side != EnumAppSide.Server) return;
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
