using System;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;

namespace UnderwaterHorrors;

/// <summary>
/// The anglerfish's lure: a glowing temporal gear on a rope from the tip of
/// the pole on its head.
///
/// - With nobody to bait it sits docked on the pole tip and the rope is
///   reeled in, out of sight inside the head.
/// - For a player at the surface (swimming, in a boat, or on the shore) it
///   hangs above the waves between them and the fish; for a player who
///   dives it leads them on down underwater.
/// - It has 10 health. Any hit startles the fish, which reels it in and
///   swims off; destroying it drops two temporal gears by the attacker.
/// </summary>
public sealed class EntityBehaviorAnglerfishLure : EntityBehavior
{
    public const string TipIdAttr = "underwaterhorrors:anglerTipId";
    private static readonly AssetLocation SegmentAsset = new("underwaterhorrors", "anglerfishluresegment");
    private static readonly AssetLocation TemporalGear = new("game", "gear-temporal");
    private const int GearsDropped = 2;
    private TentacleSegmentChain chain;
    private float elapsed, orphanTime;
    private readonly AnglerfishLureCycle cycle = new();
    private EntityBehaviorAnglerfishAI ownerAi;

    public EntityBehaviorAnglerfishLure(Entity entity) : base(entity) { }
    public override string PropertyName() => "anglerfishlure";

    /// <summary>True while it sits on the pole tip with nobody to bait.</summary>
    public bool Docked { get; private set; }

    public bool Owns(Entity segment)
    {
        if (chain?.Segments == null) return false;
        foreach (var part in chain.Segments) if (part?.EntityId == segment.EntityId) return true;
        return false;
    }

    public override void OnGameTick(float dt)
    {
        if (entity.Api.Side != EnumAppSide.Server || !entity.Alive) return;
        var owner = entity.World.GetEntityById(entity.WatchedAttributes.GetLong(EntityBehaviorAnglerfishAI.OwnerIdAttr));
        var ai = owner?.GetBehavior<EntityBehaviorAnglerfishAI>();
        if (owner == null || ai == null)
        {
            orphanTime += dt; if (orphanTime > 5) Expire(); return;
        }
        if (!owner.Alive || owner.Pos.Dimension != entity.Pos.Dimension
            || owner.WatchedAttributes.GetLong(EntityBehaviorAnglerfishAI.LureIdAttr) != entity.EntityId)
        { Expire(); return; }
        ownerAi = ai;
        orphanTime = 0; dt = Math.Clamp(dt, 0, .25f); elapsed += dt;
        if (chain == null)
        {
            chain = new TentacleSegmentChain(entity, AnglerfishLureCycle.SegmentCount, AnglerfishLureCycle.SegmentLength, SegmentAsset, SegmentAsset);
            chain.EnsureSpawned();
            foreach (var segment in chain.Segments)
                segment?.WatchedAttributes.SetLong(TipIdAttr, entity.EntityId);
        }
        var anchor = ai.LureAnchor();
        IPlayer bait = ai.BaitTarget();
        var goal = anchor.Clone();
        bool engaging = bait != null;
        if (engaging)
        {
            owner.WatchedAttributes.SetString("underwaterhorrors:targetPlayerUid", bait.PlayerUID);
            var feet = bait.Entity.Pos.XYZ;
            if (ai.ValidTarget(bait) && !SurfaceUnder(feet, out _))
                // A diver: lead them on down underwater.
                goal = AnglerfishLureCycle.BaitGoal(feet, anchor, elapsed);
            else
            {
                // At the surface, in a boat, or ashore: over the waves.
                double surface = SurfaceUnder(feet, out double s) ? s : entity.World.SeaLevel;
                goal = AnglerfishLureCycle.HoverGoal(feet, anchor, surface, elapsed);
                // Ashore, the point toward the fish may still be over land:
                // walk it on toward the fish until there is water under it.
                for (int i = 0; i < 48 && !OverWater(goal) && !IsWater(goal); i++)
                {
                    double dx = anchor.X - goal.X, dz = anchor.Z - goal.Z, len = Math.Sqrt(dx * dx + dz * dz);
                    if (len < 1) break;
                    goal.X += dx / len; goal.Z += dz / len;
                }
            }
        }
        // Correct the goal to water, or to the air just over water; but
        // near the pole tip anything open will do, so it can always dock.
        bool nearPole = goal.DistanceTo(anchor) < 3 * ai.Scale;
        if (!nearPole && !OverWater(goal))
        {
            for (int depth = 0; depth < 16 && !IsWater(goal); depth++) goal.Y -= .5;
            if (!IsWater(goal)) goal = entity.Pos.XYZ;
        }
        if (entity.Pos.DistanceTo(anchor) > AnglerfishLureCycle.MaxReach) goal = anchor.Clone();

        // Reeled in fast while the fish flees.
        double speed = engaging ? AnglerfishLureCycle.MaxSpeed : ai.IsFleeing ? AnglerfishLureCycle.MaxSpeed * 1.5 : AnglerfishLureCycle.CruiseSpeed;
        if (!engaging && entity.Pos.DistanceTo(anchor) < .6)
        {
            // Docked: ride on the pole tip.
            entity.Pos.SetPos(anchor.X, anchor.Y, anchor.Z);
            cycle.Velocity.Set(0, 0, 0);
            Docked = true;
        }
        else
        {
            Docked = false;
            var next = cycle.Step(entity.Pos.XYZ, goal, dt, speed);
            double dx = next.X - entity.Pos.X, dy = next.Y - entity.Pos.Y, dz = next.Z - entity.Pos.Z;
            int steps = Math.Max(1, (int)Math.Ceiling(Math.Sqrt(dx * dx + dy * dy + dz * dz) / .25));
            for (int i = 0; i < steps; i++)
            {
                var point = new Vec3d(entity.Pos.X + dx / steps, entity.Pos.Y + dy / steps, entity.Pos.Z + dz / steps);
                // Through water, or air with water a little under it; near the
                // pole, through any open space. Never through terrain.
                bool open = IsWater(point) || OverWater(point) || (point.DistanceTo(anchor) < 3 * ai.Scale && !IsSolid(point));
                if (!open) { cycle.Velocity.Set(0, 0, 0); break; }
                entity.Pos.SetPos(point.X, point.Y, point.Z);
            }
        }
        entity.Pos.Yaw = AnglerfishAttackCycle.SmoothYaw(entity.Pos.Yaw, owner.Pos.Yaw, dt, 45);
        entity.Pos.Roll = Docked ? 0 : (float)Math.Sin(elapsed * .8) * .12f;
        entity.Pos.Motion.Set(0, 0, 0);
        // Long casts sag underwater instead of arching above the surface.
        chain.UpdateRope(anchor.X, anchor.Y, anchor.Z, ai.LureStack(), -.015f);
    }

    public override void OnEntityReceiveDamage(DamageSource damageSource, ref float damage)
    {
        base.OnEntityReceiveDamage(damageSource, ref damage);
        if (entity.Api.Side != EnumAppSide.Server || damage <= 0 || damageSource?.Type == EnumDamageType.Heal) return;
        // Only a blow from something (a player, an arrow) startles the fish.
        var cause = damageSource?.GetCauseEntity();
        if (cause != null) ownerAi?.Startle(cause);
    }

    public override void OnEntityDeath(DamageSource damageSource)
    {
        if (entity.Api.Side == EnumAppSide.Server)
        {
            chain?.Despawn();
            var cause = damageSource?.GetCauseEntity();
            if (damageSource != null && damageSource.Type != EnumDamageType.Heal) DropGears(cause);
            ownerAi?.OnLureDestroyed(cause);
            // A broken gear leaves nothing behind but its drops.
            if (entity.World is IServerWorldAccessor sw)
                entity.World.RegisterCallback(_ => sw.DespawnEntity(entity, new EntityDespawnData { Reason = EnumDespawnReason.Expire }), 50);
        }
        base.OnEntityDeath(damageSource);
    }

    public override void OnEntityDespawn(EntityDespawnData data)
    {
        if (entity.Api.Side == EnumAppSide.Server) chain?.Despawn();
        base.OnEntityDespawn(data);
    }

    // Two temporal gears for whoever broke it. Over open water they would
    // sink out of reach, so they land at the attacker's feet when the
    // attacker is close; otherwise where the lure was.
    private void DropGears(Entity attacker)
    {
        var item = entity.World.GetItem(TemporalGear);
        if (item == null) return;
        var at = entity.Pos.XYZ.Add(0, .5, 0);
        if (attacker != null && attacker.Pos.Dimension == entity.Pos.Dimension && attacker.Pos.DistanceTo(entity.Pos.XYZ) < 48)
            at = attacker.Pos.XYZ.Add(0, .6, 0);
        entity.World.SpawnItemEntity(new ItemStack(item, GearsDropped), at, new Vec3d(0, .05, 0));
    }

    // True when the player is at the surface: feet no more than 3 blocks
    // under it, or up to 4 above it (a boat). surface: top of the top water block.
    private bool SurfaceUnder(Vec3d feet, out double surface)
    {
        surface = 0;
        var p = new BlockPos((int)Math.Floor(feet.X), (int)Math.Floor(feet.Y) + 4, (int)Math.Floor(feet.Z), entity.Pos.Dimension);
        for (int i = 0; i < 9; i++, p.Y--)
        {
            if (p.Y <= 0 || p.Y >= entity.World.BlockAccessor.MapSizeY - 1) continue;
            if (!WaterHelper.IsWaterBlock(entity.World.BlockAccessor.GetBlock(p))) continue;
            surface = p.Y + 1;
            return feet.Y >= surface - 3;
        }
        return false;
    }

    // Air with water within HoverHeight + 3 blocks below.
    private bool OverWater(Vec3d point)
    {
        if (!IsAir(point)) return false;
        var below = point.Clone();
        for (int i = 0; i < (int)AnglerfishLureCycle.HoverHeight + 4; i++)
        {
            below.Y -= 1;
            if (IsWater(below)) return true;
            if (!IsAir(below)) return false;
        }
        return false;
    }

    private Block BlockAt(Vec3d point)
    {
        var p = new BlockPos((int)Math.Floor(point.X), (int)Math.Floor(point.Y), (int)Math.Floor(point.Z), entity.Pos.Dimension);
        if (p.Y <= 0 || p.Y >= entity.World.BlockAccessor.MapSizeY - 1) return null;
        return entity.World.BlockAccessor.GetBlock(p);
    }

    private bool IsWater(Vec3d point) { var b = BlockAt(point); return b != null && WaterHelper.IsWaterBlock(b); }
    private bool IsAir(Vec3d point) { var b = BlockAt(point); return b != null && b.Id == 0; }
    private bool IsSolid(Vec3d point) { var b = BlockAt(point); return b == null || (b.Id != 0 && !WaterHelper.IsWaterBlock(b)); }

    private void Expire()
    {
        chain?.Despawn();
        if (entity is EntityAgent agent) agent.AllowDespawn = true;
        if (entity.Alive) entity.Die(EnumDespawnReason.Expire);
    }
}

/// <summary>Self-cleans saved rope pieces after owner death/unload/replacement.</summary>
public sealed class EntityBehaviorAnglerfishLureSegment : EntityBehavior
{
    private float timer;
    public EntityBehaviorAnglerfishLureSegment(Entity entity) : base(entity) { }
    public override string PropertyName() => "anglerfishluresegment";
    public override void OnGameTick(float dt)
    {
        if (entity.Api.Side != EnumAppSide.Server) return;
        timer += dt; if (timer < 5) return; timer = 0;
        var tip = entity.World.GetEntityById(entity.WatchedAttributes.GetLong(EntityBehaviorAnglerfishLure.TipIdAttr));
        // A reload rebuilds the transient chain. Its old segments must disappear
        // even when the same saved tip ID was restored successfully.
        if (tip?.Alive == true && tip.GetBehavior<EntityBehaviorAnglerfishLure>()?.Owns(entity) == true) return;
        if (entity is EntityAgent agent) agent.AllowDespawn = true;
        entity.Die(EnumDespawnReason.Expire);
    }
}
