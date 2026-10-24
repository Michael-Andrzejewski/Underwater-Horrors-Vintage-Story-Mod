using System;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;

namespace UnderwaterHorrors;

public sealed class EntityBehaviorAnglerfishAI : EntityBehavior
{
    public const string LureIdAttr = "underwaterhorrors:anglerLureId";
    public const string OwnerIdAttr = "underwaterhorrors:anglerOwnerId";
    // Set on anglerfish from natural spawning; they leave at daybreak.
    public const string NaturalAttr = "underwaterhorrors:anglerNatural";
    private static readonly AssetLocation LureAsset = new("underwaterhorrors", "anglerfishlure");
    private readonly AnglerfishAttackCycle cycle = new();
    private readonly Vec3d home = new(), goal = new();
    private readonly Vec3d retreat = new(), patrolGoal = new();
    private readonly BlockPos scan = new(0,0,0,0);
    // Block offsets from the entity's centre, matching tools/anglerfish/build_angler.py:
    // the body runs from the jutting chin 8.5 blocks ahead to the tail stalk 23 behind,
    // about 10 wide and 11.5 tall, its belly at the entity's feet.
    private static readonly double[] BodyAlong={-7,-2,3,9,15,21};
    private static readonly double[] BodySides={-4.5,0,4.5};
    private static readonly double[] BodyHeights={.5,5.5,10.5};
    // Middle of the gape's front, ahead of and above the entity's centre,
    // and the middle of the body, which sits at the lurk depth.
    private const double MouthForward=6.5, MouthUp=4.75, BodyCenterUp=5.5;
    private bool initialized;
    private float lureRetry;
    private float patrolTimer;
    private bool diving, patrolling;
    private IPlayer target;
    public bool IsLunging => cycle.Phase == AnglerfishPhase.Lunging;
    public bool IsRetreating => diving || cycle.Phase == AnglerfishPhase.Recovering || cycle.Phase == AnglerfishPhase.Returning;
    public EntityBehaviorAnglerfishAI(Entity entity) : base(entity) { }
    public override string PropertyName() => "anglerfishai";

    public override void OnGameTick(float deltaTime)
    {
        if (entity.Api.Side != EnumAppSide.Server || !entity.Alive) return;
        float dt = Math.Clamp(deltaTime, 0, .25f);
        if (!initialized)
        {
            initialized=true;
            if(!entity.Attributes.GetBool("anglerHomeSet"))
            {
                entity.Attributes.SetBool("anglerHomeSet",true);
                entity.Attributes.SetDouble("anglerHomeX",entity.Pos.X);
                entity.Attributes.SetDouble("anglerHomeY",entity.Pos.Y);
                entity.Attributes.SetDouble("anglerHomeZ",entity.Pos.Z);
            }
            home.Set(entity.Attributes.GetDouble("anglerHomeX"),entity.Attributes.GetDouble("anglerHomeY"),entity.Attributes.GetDouble("anglerHomeZ"));
            retreat.Set(home.X,home.Y,home.Z);
        }
        entity.Pos.Motion.Set(0,0,0);
        if (entity.WatchedAttributes.GetBool("underwaterhorrors:static")) return;
        EnsureLure(dt);
        var animation=entity.GetBehavior<EntityBehaviorAnglerfishAnimation>();
        if (animation?.IsPreviewing == true) return;

        bool submerged=HasRoom(entity.World,entity.Pos.X,entity.Pos.Y,entity.Pos.Z,entity.Pos.Yaw,entity.Pos.Dimension,true);
        if(!IsLunging && !submerged)diving=true;
        double distance=double.MaxValue;
        if (!IsLunging)
        {
            target=NearestTarget(out distance);
            cycle.TriggerRange=Config?.AnglerfishStrikeRange ?? AnglerfishAttackCycle.DefaultTriggerRange;
            if (!IsRetreating && target != null && cycle.TryBegin(StrikeDistance(target)))
            {
                retreat.Set(entity.Pos.X,entity.Pos.Y,entity.Pos.Z);
                entity.WatchedAttributes.SetString("underwaterhorrors:targetPlayerUid",target.PlayerUID);
                // Face the victim before computing the mouth offset. The body
                // origin is below the rendered head, not its visual center.
                double dx=target.Entity.Pos.X-entity.Pos.X,dz=target.Entity.Pos.Z-entity.Pos.Z;
                float yaw=(float)Math.Atan2(dx,dz);
                if (!HasRoom(entity.World,entity.Pos.X,entity.Pos.Y,entity.Pos.Z,yaw,entity.Pos.Dimension,false))
                { cycle.Finish(); return; }
                entity.Pos.Yaw=yaw;
                goal.Set(target.Entity.Pos.X-Math.Sin(yaw)*MouthForward,
                    target.Entity.Pos.Y+.6-MouthUp,target.Entity.Pos.Z-Math.Cos(yaw)*MouthForward);
                animation?.PlayLunge();
            }
        }
        if (IsLunging)
        {
            if (!ValidTarget(target)) cycle.Finish();
            else
            {
                bool clear=MoveToward(goal,(Config?.AnglerfishStrikeSpeed ?? AnglerfishAttackCycle.LungeSpeed)*dt,true);
                if (!clear || entity.Pos.DistanceTo(goal)<.1) cycle.Finish();
            }
        }
        // Do not spend the cooldown hanging above the water after a chomp.
        // Recovery movement starts in the very tick that the strike finishes.
        patrolling=false;
        if (!IsLunging && IsRetreating)
        {
            var destination=submerged?retreat.Clone():new Vec3d(entity.Pos.X,Math.Min(retreat.Y,entity.Pos.Y-12),entity.Pos.Z);
            TurnToward(destination,dt,false);
            if(!MoveToward(destination,AnglerfishAttackCycle.DiveSpeed*dt,false))
                MoveToward(retreat,AnglerfishAttackCycle.DiveSpeed*dt,false);
            diving=!HasRoom(entity.World,entity.Pos.X,entity.Pos.Y,entity.Pos.Z,entity.Pos.Yaw,entity.Pos.Dimension,true);
            if(!diving && entity.Pos.DistanceTo(retreat)<.2)cycle.ArriveHome();
        }
        else if(!IsLunging && (target==null || distance>AnglerfishLureCycle.MaxReach))
        {
            Patrol(dt);
        }
        else if(!IsLunging)
        {
            // Lying in wait: hold its depth and slowly turn its mouth
            // toward the prey the lure is leading in.
            TurnToward(target.Entity.Pos.XYZ,dt,true,20);
        }
        animation?.SetLocomotion(IsLunging ? "swimfast" : "swim",
            IsLunging || IsRetreating || patrolling ? 1f : .3f);
        cycle.Tick(dt);
    }

    public IPlayer NearestTarget(out double distance)
    {
        IPlayer best=null;distance=double.MaxValue;
        // Capsule along the thick head/trunk, excluding fins, lure and thin tail.
        ModelPoint(-4.75,5.5,out var a);
        ModelPoint(14,5.5,out var b);
        foreach(var p in entity.World.AllOnlinePlayers)
        {
            if(!ValidTarget(p))continue;
            double d=Math.Max(0,AnglerfishAttackCycle.DistanceToSegment(p.Entity.Pos.X,p.Entity.Pos.Y+.6,p.Entity.Pos.Z,a.X,a.Y,a.Z,b.X,b.Y,b.Z)-4);
            if(d<distance){distance=d;best=p;}
        }
        return best;
    }

    private static UnderwaterHorrorsConfig Config => UnderwaterHorrorsModSystem.Config;

    // From the prey's middle to the front of the gape.
    public double MouthDistance(IPlayer p)
    {
        var e=p.Entity.Pos;
        double yaw=entity.Pos.Yaw;
        double dx=e.X-(entity.Pos.X+Math.Sin(yaw)*MouthForward),dy=e.Y+.6-(entity.Pos.Y+MouthUp),dz=e.Z-(entity.Pos.Z+Math.Cos(yaw)*MouthForward);
        return Math.Sqrt(dx*dx+dy*dy+dz*dz);
    }

    // The prey's distance as the strike sees it, compared with the strike
    // range: a ball of that radius round the mouth, stretched upward into a
    // column AnglerfishStrikeReachUp tall. Prey on the surface above the
    // mouth, which is 10 to 20 blocks down, is within reach.
    public double StrikeDistance(IPlayer p)
    {
        var e=p.Entity.Pos;
        double yaw=entity.Pos.Yaw;
        double dx=e.X-(entity.Pos.X+Math.Sin(yaw)*MouthForward),dy=e.Y+.6-(entity.Pos.Y+MouthUp),dz=e.Z-(entity.Pos.Z+Math.Cos(yaw)*MouthForward);
        double sideways=Math.Sqrt(dx*dx+dz*dz);
        if(dy<=0)return Math.Sqrt(sideways*sideways+dy*dy);
        double range=Config?.AnglerfishStrikeRange ?? AnglerfishAttackCycle.DefaultTriggerRange;
        double up=Config?.AnglerfishStrikeReachUp ?? 24;
        return Math.Max(sideways,dy*range/up);
    }

    public bool ValidTarget(IPlayer p)
    {
        if(p?.Entity==null || !p.Entity.Alive || p.Entity.Pos.Dimension!=entity.Pos.Dimension)return false;
        if(entity.World.PlayerByUid(p.PlayerUID)?.Entity!=p.Entity)return false;
        // Preserve the mod's live creative/spectator observer setting.
        if(UnderwaterHorrorsModSystem.Config?.IgnoreCreativePlayers != false
            && (p.WorldData.CurrentGameMode==EnumGameMode.Creative || p.WorldData.CurrentGameMode==EnumGameMode.Spectator))return false;
        scan.Set((int)Math.Floor(p.Entity.Pos.X),(int)Math.Floor(p.Entity.Pos.Y),(int)Math.Floor(p.Entity.Pos.Z));
        scan.dimension=p.Entity.Pos.Dimension;
        for(int i=0;i<=3;i++)
        {
            var block=entity.World.BlockAccessor.GetBlock(scan);
            if(WaterHelper.IsWaterBlock(block))return true;
            if(block.Id!=0)return false;
            scan.Y--;
        }
        return false;
    }

    private void ModelPoint(double modelXOffset,double modelY,out Vec3d point)
    {
        // Arguments are already scaled block offsets from the entity's center.
        double yaw=entity.Pos.Yaw;
        point=new Vec3d(entity.Pos.X-Math.Sin(yaw)*modelXOffset,entity.Pos.Y+modelY,entity.Pos.Z-Math.Cos(yaw)*modelXOffset);
    }

    public Vec3d LureAnchor()
    {
        // Tip of the rod stub on the snout; the rope continues from here.
        ModelPoint(-5.75,9.75,out var anchor);
        return anchor;
    }

    private bool MoveToward(Vec3d destination,double travel,bool strike,bool waterOnly=false)
    {
        double dx=destination.X-entity.Pos.X,dy=destination.Y-entity.Pos.Y,dz=destination.Z-entity.Pos.Z;
        double length=Math.Sqrt(dx*dx+dy*dy+dz*dz);
        if(length<.001)
        {
            if(strike)TryMouthHit(entity.Pos.X+Math.Sin(entity.Pos.Yaw)*MouthForward,entity.Pos.Y+MouthUp,entity.Pos.Z+Math.Cos(entity.Pos.Yaw)*MouthForward,0,0,0);
            return true;
        }
        travel=Math.Min(travel,length);
        int steps=Math.Max(1,(int)Math.Ceiling(travel/.5));
        double sx=dx/length*travel/steps,sy=dy/length*travel/steps,sz=dz/length*travel/steps;
        for(int i=0;i<steps;i++)
        {
            double x=entity.Pos.X+sx,y=entity.Pos.Y+sy,z=entity.Pos.Z+sz;
            if(!HasRoom(entity.World,x,y,z,entity.Pos.Yaw,entity.Pos.Dimension,waterOnly))return false;
            double mx=entity.Pos.X+Math.Sin(entity.Pos.Yaw)*MouthForward,my=entity.Pos.Y+MouthUp,mz=entity.Pos.Z+Math.Cos(entity.Pos.Yaw)*MouthForward;
            entity.Pos.SetPos(x,y,z);
            // Swept mouth test: never jump over the player on a fast/lagged tick.
            if(strike)
            {
                TryMouthHit(mx,my,mz,sx,sy,sz);
                if(!IsLunging)return true;
            }
        }
        return true;
    }

    private void TryMouthHit(double mx,double my,double mz,double sx,double sy,double sz)
    {
            if(ValidTarget(target) && AnglerfishAttackCycle.DistanceToSegment(
                target.Entity.Pos.X,target.Entity.Pos.Y+.6,target.Entity.Pos.Z,mx,my,mz,mx+sx,my+sy,mz+sz)<=3
                && ClearMouthLine(mx+sx,my+sy,mz+sz) && cycle.TryHit())
            {
                target.Entity.ReceiveDamage(new DamageSource { Source=EnumDamageSource.Entity,SourceEntity=entity,Type=EnumDamageType.PiercingAttack },Config?.AnglerfishBiteDamage ?? AnglerfishAttackCycle.Damage);
                cycle.Finish();
            }
    }

    private void TurnToward(Vec3d destination,float dt,bool waterOnly,float degreesPerSecond=0)
    {
        double dx=destination.X-entity.Pos.X,dz=destination.Z-entity.Pos.Z;
        if(dx*dx+dz*dz<.01)return;
        float yaw=AnglerfishAttackCycle.SmoothYaw(entity.Pos.Yaw,(float)Math.Atan2(dx,dz),dt,
            degreesPerSecond>0?degreesPerSecond:UnderwaterHorrorsModSystem.Config?.SerpentTurnSpeedDegPerSec??45);
        if(HasRoom(entity.World,entity.Pos.X,entity.Pos.Y,entity.Pos.Z,yaw,entity.Pos.Dimension,waterOnly))entity.Pos.Yaw=yaw;
    }

    private void Patrol(float dt)
    {
        patrolling=true;
        patrolTimer-=dt;
        if(patrolTimer<=0 || entity.Pos.DistanceTo(patrolGoal)<3)
        {
            patrolTimer=10;
            if(entity.Pos.DistanceTo(home)>48)patrolGoal.Set(home.X,home.Y,home.Z);
            else
            {
                bool found=false;
                for(int i=0;i<8;i++)
                {
                    double yaw=entity.Pos.Yaw+(entity.World.Rand.NextDouble()-.5)*Math.PI;
                    var candidate=new Vec3d(entity.Pos.X+Math.Sin(yaw)*28,
                        Math.Clamp(entity.Pos.Y+(entity.World.Rand.NextDouble()-.5)*4,home.Y-2,home.Y+2),
                        entity.Pos.Z+Math.Cos(yaw)*28);
                    if(!HasRoom(entity.World,candidate.X,candidate.Y,candidate.Z,(float)yaw,entity.Pos.Dimension,true))continue;
                    patrolGoal.Set(candidate.X,candidate.Y,candidate.Z);found=true;break;
                }
                if(!found){patrolTimer=1;return;}
            }
        }
        TurnToward(patrolGoal,dt,true);
        // Follow the curved heading instead of sliding sideways to a waypoint.
        double travel=AnglerfishAttackCycle.PatrolSpeed*dt;
        var next=new Vec3d(entity.Pos.X+Math.Sin(entity.Pos.Yaw)*travel,
            entity.Pos.Y+Math.Clamp(patrolGoal.Y-entity.Pos.Y,-.4*dt,.4*dt),
            entity.Pos.Z+Math.Cos(entity.Pos.Yaw)*travel);
        if(!MoveToward(next,travel,false,true))patrolTimer=0;
    }

    private bool ClearMouthLine(double x,double y,double z)
    {
        double dx=target.Entity.Pos.X-x,dy=target.Entity.Pos.Y+.6-y,dz=target.Entity.Pos.Z-z;
        int count=Math.Max(1,(int)Math.Ceiling(Math.Sqrt(dx*dx+dy*dy+dz*dz)*4));
        scan.dimension=entity.Pos.Dimension;
        for(int i=0;i<=count;i++)
        {
            double t=(double)i/count;
            scan.Set((int)Math.Floor(x+dx*t),(int)Math.Floor(y+dy*t),(int)Math.Floor(z+dz*t));
            var block=entity.World.BlockAccessor.GetBlock(scan);
            if(block.Id!=0 && !WaterHelper.IsWaterBlock(block))return false;
        }
        return true;
    }

    private void EnsureLure(float dt)
    {
        lureRetry-=dt;if(lureRetry>0)return;lureRetry=1;
        long id=entity.WatchedAttributes.GetLong(LureIdAttr);
        var existing=entity.World.GetEntityById(id);
        if(existing?.Alive==true)return;
        var props=entity.World.GetEntityType(LureAsset);if(props==null)return;
        var lure=entity.World.ClassRegistry.CreateEntity(props);var anchor=LureAnchor();
        lure.Pos.SetPos(anchor.X,anchor.Y,anchor.Z);lure.Pos.Dimension=entity.Pos.Dimension;
        lure.WatchedAttributes.SetLong(OwnerIdAttr,entity.EntityId);
        entity.World.SpawnEntity(lure);
        entity.WatchedAttributes.SetLong(LureIdAttr,lure.EntityId);
    }

    private void Cleanup()
    {
        if(entity.Api.Side!=EnumAppSide.Server)return;
        var lure=entity.World.GetEntityById(entity.WatchedAttributes.GetLong(LureIdAttr));
        if(lure is EntityAgent agent)agent.AllowDespawn=true;
        if(lure?.Alive==true)lure.Die(EnumDespawnReason.Expire);
        entity.WatchedAttributes.SetLong(LureIdAttr,0);
    }
    public override void OnEntityDeath(DamageSource source){Cleanup();base.OnEntityDeath(source);}
    public override void OnEntityDespawn(EntityDespawnData data){Cleanup();base.OnEntityDespawn(data);}

    public static bool HasRoom(IWorldAccessor world,double x,double y,double z,float yaw,int dimension,bool waterOnly)
    {
        var p=new BlockPos(0,0,0,dimension);
        // Sample the thick body and mouth; finite substeps also prevent tunneling
        // during a charge. Fins are decorative and have no collision volume.
        foreach(double along in BodyAlong)
        foreach(double side in BodySides)
        foreach(double height in BodyHeights)
        {
            p.Set((int)Math.Floor(x-Math.Sin(yaw)*along+Math.Cos(yaw)*side),(int)Math.Floor(y+height),(int)Math.Floor(z-Math.Cos(yaw)*along-Math.Sin(yaw)*side));
            if(p.Y<1 || p.Y>=world.BlockAccessor.MapSizeY-1)return false;
            var block=world.BlockAccessor.GetBlock(p);
            if(!WaterHelper.IsWaterBlock(block) && (waterOnly || block.Id!=0))return false;
        }
        return true;
    }

    /// <summary>
    /// Places an anglerfish in open water minDist to maxDist blocks from the
    /// player, facing them. The middle of its body lurks AnglerfishLurkDepthMin
    /// to AnglerfishLurkDepthMax blocks below the surface; a shallower depth in
    /// that range is tried when the deep one has no room. Returns null when no
    /// column out there has room for it.
    /// </summary>
    public static Entity Spawn(ICoreServerAPI api,IServerPlayer player,double minDist=24,double maxDist=36,bool natural=false)
    {
        var props=api.World.GetEntityType(new AssetLocation("underwaterhorrors","giantanglerfish"));
        if(props==null)return null;
        var cfg=UnderwaterHorrorsModSystem.Config;
        double lurkMin=cfg?.AnglerfishLurkDepthMin??10,lurkMax=cfg?.AnglerfishLurkDepthMax??20;
        double lurk=lurkMin+api.World.Rand.NextDouble()*(lurkMax-lurkMin);
        var pos=player.Entity.Pos;
        var p=new BlockPos(0,0,0,pos.Dimension);
        for(int attempt=0;attempt<16;attempt++)
        {
            double angle=pos.Yaw+attempt*Math.PI/8,radius=minDist+(maxDist-minDist)*((attempt/8)+api.World.Rand.NextDouble())/2;
            double x=pos.X+Math.Sin(angle)*radius,z=pos.Z+Math.Cos(angle)*radius;
            int surface=-1;
            for(int y=Math.Min(api.World.BlockAccessor.MapSizeY-2,(int)pos.Y+48);y>=Math.Max(1,(int)pos.Y-12);y--)
            {
                p.Set((int)Math.Floor(x),y,(int)Math.Floor(z));
                if(WaterHelper.IsWaterBlock(api.World.BlockAccessor.GetBlock(p))){surface=y+1;break;}
            }
            if(surface<0)continue;
            float yaw=(float)Math.Atan2(pos.X-x,pos.Z-z);
            for(double depth=lurk;depth>=lurkMin-.01;depth-=2)
            {
                double y=surface-depth-BodyCenterUp;
                if(!HasRoom(api.World,x,y,z,yaw,pos.Dimension,true))continue;
                var fish=api.World.ClassRegistry.CreateEntity(props);
                fish.Pos.SetPos(x,y,z);fish.Pos.Yaw=yaw;fish.Pos.Dimension=pos.Dimension;
                fish.WatchedAttributes.SetString("underwaterhorrors:targetPlayerUid",player.PlayerUID);
                if(natural)fish.WatchedAttributes.SetBool(NaturalAttr,true);
                api.World.SpawnEntity(fish);return fish;
            }
        }
        return null;
    }
}
