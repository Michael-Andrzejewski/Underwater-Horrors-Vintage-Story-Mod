using System;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.MathTools;

namespace UnderwaterHorrors;

/// <summary>The kraken's actual spline/segment chain, ending in a blue gear.</summary>
public sealed class EntityBehaviorAnglerfishLure : EntityBehavior
{
    public const string TipIdAttr="underwaterhorrors:anglerTipId";
    private static readonly AssetLocation SegmentAsset=new("underwaterhorrors","anglerfishluresegment");
    private TentacleSegmentChain chain;
    private float elapsed, orphanTime;
    private readonly AnglerfishLureCycle cycle = new();

    private string targetUid;
    public EntityBehaviorAnglerfishLure(Entity entity):base(entity) { }
    public override string PropertyName()=>"anglerfishlure";
    public bool Owns(Entity segment)
    {
        if(chain?.Segments==null)return false;
        foreach(var part in chain.Segments)if(part?.EntityId==segment.EntityId)return true;
        return false;
    }

    public override void OnGameTick(float dt)
    {
        if(entity.Api.Side!=EnumAppSide.Server)return;
        var owner=entity.World.GetEntityById(entity.WatchedAttributes.GetLong(EntityBehaviorAnglerfishAI.OwnerIdAttr));
        var ai=owner?.GetBehavior<EntityBehaviorAnglerfishAI>();
        if(owner==null || ai==null)
        {
            orphanTime+=dt;if(orphanTime>5)Expire();return;
        }
        if(!owner.Alive || owner.Pos.Dimension!=entity.Pos.Dimension
            || owner.WatchedAttributes.GetLong(EntityBehaviorAnglerfishAI.LureIdAttr)!=entity.EntityId)
        { Expire(); return; }
        orphanTime=0;dt=Math.Clamp(dt,0,.25f);elapsed+=dt;
        if(chain==null)
        {
            chain=new TentacleSegmentChain(entity,AnglerfishLureCycle.SegmentCount,AnglerfishLureCycle.SegmentLength,SegmentAsset,SegmentAsset);
            chain.EnsureSpawned();
            foreach(var segment in chain.Segments)
                segment?.WatchedAttributes.SetLong(TipIdAttr,entity.EntityId);
        }
        var anchor=ai.LureAnchor();
        var tracked=targetUid==null?null:entity.World.PlayerByUid(targetUid);
        if(!ai.ValidTarget(tracked) || anchor.DistanceTo(tracked.Entity.Pos.XYZ)>AnglerfishLureCycle.MaxReach)
            tracked=ai.NearestTarget(out _);
        // Another swimmer approaching the gear becomes the bait's audience.
        double nearest=ai.ValidTarget(tracked)?entity.Pos.DistanceTo(tracked.Entity.Pos.XYZ):double.MaxValue;
        foreach(var player in entity.World.AllOnlinePlayers)
        {
            if(!ai.ValidTarget(player))continue;
            double distance=entity.Pos.DistanceTo(player.Entity.Pos.XYZ);
            if(distance<nearest && distance<AnglerfishLureCycle.MaxDistance){tracked=player;nearest=distance;}
        }
        bool engaging=ai.ValidTarget(tracked)
            && anchor.DistanceTo(tracked.Entity.Pos.XYZ)<=AnglerfishLureCycle.MaxReach;
        var goal=anchor.Clone();
        if(engaging)
        {
            targetUid=tracked.PlayerUID;
            owner.WatchedAttributes.SetString("underwaterhorrors:targetPlayerUid",targetUid);
            goal=AnglerfishLureCycle.BaitGoal(tracked.Entity.Pos.XYZ,anchor,elapsed);
        }
        // Correct the goal to submerged water, never the tip's actual position.
        // Even a moving owner or lost target must be followed without teleporting.
        for(int depth=0;depth<16 && !IsWater(goal);depth++)goal.Y-=.5;
        if(!IsWater(goal))goal=entity.Pos.XYZ;
        if(entity.Pos.DistanceTo(anchor)>AnglerfishLureCycle.MaxReach)goal=anchor.Clone();
        var next=cycle.Step(entity.Pos.XYZ,goal,dt,engaging?AnglerfishLureCycle.MaxSpeed:AnglerfishLureCycle.CruiseSpeed);
        double dx=next.X-entity.Pos.X,dy=next.Y-entity.Pos.Y,dz=next.Z-entity.Pos.Z;
        int steps=Math.Max(1,(int)Math.Ceiling(Math.Sqrt(dx*dx+dy*dy+dz*dz)/.25));
        for(int i=0;i<steps;i++)
        {
            var point=new Vec3d(entity.Pos.X+dx/steps,entity.Pos.Y+dy/steps,entity.Pos.Z+dz/steps);
            // A surfaced anchor can leave the tip briefly in air. Permit a
            // downward exit through air, but never through solid terrain.
            if(!IsWater(point) && !(dy<0 && IsAir(point)))
            { cycle.Velocity.Set(0,0,0);break; }
            entity.Pos.SetPos(point.X,point.Y,point.Z);
        }
        entity.Pos.Yaw=AnglerfishAttackCycle.SmoothYaw(entity.Pos.Yaw,owner.Pos.Yaw,dt,45);
        entity.Pos.Roll=(float)Math.Sin(elapsed*.8)*.12f;
        entity.Pos.Motion.Set(0,0,0);
        // Long casts sag underwater instead of arching above the surface.
        chain.UpdatePositions(anchor.X,anchor.Y,anchor.Z,-.015f);
    }
    private bool IsWater(Vec3d point)
    {
        var p=new BlockPos((int)Math.Floor(point.X),(int)Math.Floor(point.Y),(int)Math.Floor(point.Z),entity.Pos.Dimension);
        return p.Y>0 && p.Y<entity.World.BlockAccessor.MapSizeY-1
            && WaterHelper.IsWaterBlock(entity.World.BlockAccessor.GetBlock(p));
    }
    private bool IsAir(Vec3d point)
    {
        var p=new BlockPos((int)Math.Floor(point.X),(int)Math.Floor(point.Y),(int)Math.Floor(point.Z),entity.Pos.Dimension);
        return p.Y>0 && p.Y<entity.World.BlockAccessor.MapSizeY-1 && entity.World.BlockAccessor.GetBlock(p).Id==0;
    }
    private void Expire()
    {
        chain?.Despawn();
        if(entity is EntityAgent agent)agent.AllowDespawn=true;
        if(entity.Alive)entity.Die(EnumDespawnReason.Expire);
    }
    public override void OnEntityDeath(DamageSource source){if(entity.Api.Side==EnumAppSide.Server)chain?.Despawn();base.OnEntityDeath(source);}
    public override void OnEntityDespawn(EntityDespawnData data){if(entity.Api.Side==EnumAppSide.Server)chain?.Despawn();base.OnEntityDespawn(data);}
}

/// <summary>Self-cleans saved rope pieces after owner death/unload/replacement.</summary>
public sealed class EntityBehaviorAnglerfishLureSegment : EntityBehavior
{
    private float timer;
    public EntityBehaviorAnglerfishLureSegment(Entity entity):base(entity) { }
    public override string PropertyName()=>"anglerfishluresegment";
    public override void OnGameTick(float dt)
    {
        if(entity.Api.Side!=EnumAppSide.Server)return;
        timer+=dt;if(timer<5)return;timer=0;
        var tip=entity.World.GetEntityById(entity.WatchedAttributes.GetLong(EntityBehaviorAnglerfishLure.TipIdAttr));
        // A reload rebuilds the transient chain. Its old segments must disappear
        // even when the same saved tip ID was restored successfully.
        if(tip?.Alive==true && tip.GetBehavior<EntityBehaviorAnglerfishLure>()?.Owns(entity)==true)return;
        if(entity is EntityAgent agent)agent.AllowDespawn=true;
        entity.Die(EnumDespawnReason.Expire);
    }
}
