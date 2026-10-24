using System;
using System.Reflection;
using Pilot;
using UnderwaterHorrors;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;

namespace AnglerfishPilotSmoke;

// Separate test-only mod, loaded with --addModPath into a copied test save.
public sealed class Smoke : ModSystem
{
    public override bool ShouldLoad(EnumAppSide side)=>side==EnumAppSide.Server;
    public override void StartServerSide(ICoreServerAPI api)
    {
        api.Event.ServerRunPhase(EnumServerRunPhase.RunGame,()=>
        {
            var pilot=api.ModLoader.GetModSystem<PilotModSystem>();
            pilot.Register("anglerfish_patrol_live","anglerfish",Patrol);
            pilot.Register("anglerfish_dive_live","anglerfish",Dive);
            pilot.Register("anglerfish_lure_live","anglerfish",Lure);
        });
    }
    private sealed class Fixture : IDisposable
    {
        public readonly TestContext Context;
        public readonly Entity Fish;
        public readonly EntityBehaviorAnglerfishAI Ai;
        private readonly bool observer;
        private readonly EnumGameMode mode;
        private readonly EntityPos position;
        public Fixture(TestContext ctx)
        {
            Context=ctx;observer=UnderwaterHorrorsModSystem.Config.IgnoreCreativePlayers;
            mode=ctx.Player.WorldData.CurrentGameMode;position=ctx.Player.Entity.Pos.Copy();
            ctx.Cmd("/gamemode creative");
            UnderwaterHorrorsModSystem.Config.IgnoreCreativePlayers=true;
            Fish=EntityBehaviorAnglerfishAI.Spawn(ctx.Api,ctx.Player);
            if(Fish==null){Dispose();ctx.Skip("Pilot player must be beside deep open water for this fixture.");}
            Ai=Fish.GetBehavior<EntityBehaviorAnglerfishAI>();
            Ai.OnGameTick(.05f);
        }
        public void Dispose()
        {
            if(Fish is EntityAgent agent)agent.AllowDespawn=true;
            if(Fish?.Alive==true)Fish.Die(EnumDespawnReason.Expire);
            UnderwaterHorrorsModSystem.Config.IgnoreCreativePlayers=observer;
            Context.Player.Entity.Pos.SetFrom(position);
            Context.Cmd("/gamemode "+mode.ToString().ToLowerInvariant());
        }
    }
    private static void Patrol(TestContext ctx)
    {
        using var f=new Fixture(ctx);
        var initial=f.Fish.Pos.XYZ;
        double maxTurn=0;
        for(int i=0;i<160;i++)
        {
            float before=f.Fish.Pos.Yaw;f.Ai.OnGameTick(.05f);
            maxTurn=Math.Max(maxTurn,Math.Abs(GameMath.AngleRadDistance(before,f.Fish.Pos.Yaw)));
            ctx.Assert(EntityBehaviorAnglerfishAI.HasRoom(ctx.Api.World,f.Fish.Pos.X,f.Fish.Pos.Y,f.Fish.Pos.Z,f.Fish.Pos.Yaw,f.Fish.Pos.Dimension,true),"Patrol emerged from water");
        }
        ctx.Assert(f.Fish.Pos.DistanceTo(initial)>2,"Body did not patrol");
        ctx.Assert(maxTurn<=UnderwaterHorrorsModSystem.Config.SerpentTurnSpeedDegPerSec*GameMath.DEG2RAD*.05+.0001,"Patrol snapped its turn");
        ctx.Note($"Patrol displacement {f.Fish.Pos.DistanceTo(initial):F2} blocks; max turn {maxTurn/GameMath.DEG2RAD:F2} deg/tick");
    }
    private static void Dive(TestContext ctx)
    {
        using var f=new Fixture(ctx);
        var p=f.Fish.Pos.AsBlockPos.Copy();
        while(p.Y<ctx.Api.World.BlockAccessor.MapSizeY-1 && WaterHelper.IsWaterBlock(ctx.Api.World.BlockAccessor.GetBlock(p)))p.Y++;
        f.Fish.Pos.Y=p.Y-8;
        double start=f.Fish.Pos.Y;
        var cycle=(AnglerfishAttackCycle)typeof(EntityBehaviorAnglerfishAI).GetField("cycle",BindingFlags.NonPublic|BindingFlags.Instance).GetValue(f.Ai);
        cycle.TryBegin(0);cycle.Finish();
        f.Ai.OnGameTick(.05f);
        ctx.Assert(f.Fish.Pos.Y<start,"Surface recovery waited instead of diving immediately");
        for(int i=0;i<80;i++)f.Ai.OnGameTick(.05f);
        ctx.Assert(EntityBehaviorAnglerfishAI.HasRoom(ctx.Api.World,f.Fish.Pos.X,f.Fish.Pos.Y,f.Fish.Pos.Z,f.Fish.Pos.Yaw,f.Fish.Pos.Dimension,true),"Fish remained above water after recovery");
        ctx.Note($"Dived {start-f.Fish.Pos.Y:F2} blocks during cooldown");
    }
    private static void Lure(TestContext ctx)
    {
        using var f=new Fixture(ctx);
        UnderwaterHorrorsModSystem.Config.IgnoreCreativePlayers=false;
        var water=ctx.Player.Entity.Pos.AsBlockPos.Copy();
        for(int i=0;i<16 && !WaterHelper.IsWaterBlock(ctx.Api.World.BlockAccessor.GetBlock(water));i++)water.Y--;
        ctx.Player.Entity.Pos.Y=water.Y+.5;
        ctx.Note($"Player alive={ctx.Player.Entity.Alive}, valid={f.Ai.ValidTarget(ctx.Player)}, water={WaterHelper.IsWaterBlock(ctx.Api.World.BlockAccessor.GetBlock(water))}");
        ctx.Assert(f.Ai.ValidTarget(ctx.Player),"Live lure fixture does not have an alive swimmer target");
        var lure=ctx.Api.World.GetEntityById(f.Fish.WatchedAttributes.GetLong(EntityBehaviorAnglerfishAI.LureIdAttr));
        ctx.Assert(lure!=null,"No live lure spawned");
        var behavior=lure.GetBehavior<EntityBehaviorAnglerfishLure>();
        double maxStep=0;
        for(int i=0;i<800;i++)
        {
            var before=lure.Pos.XYZ;behavior.OnGameTick(.05f);
            maxStep=Math.Max(maxStep,lure.Pos.DistanceTo(before));
        }
        double range=lure.Pos.DistanceTo(ctx.Player.Entity.Pos.XYZ);
        ctx.Note($"Tip alive={lure.Alive}, position={lure.Pos.XYZ}; body alive={f.Fish.Alive}; player={ctx.Player.Entity.Pos.XYZ}; max step={maxStep:F3}");
        ctx.Assert(maxStep<=.4001,"Live lure snapped beyond its speed limit");
        ctx.Assert(range>=8 && range<=15,$"Live lure failed to settle in bait band: {range:F2}");
        ctx.Note($"Live lure spacing {range:F2}; largest .05-second step {maxStep:F3} blocks");
    }
}
