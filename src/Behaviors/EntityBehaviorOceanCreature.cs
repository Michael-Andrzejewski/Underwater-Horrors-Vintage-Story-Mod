using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Datastructures;

namespace UnderwaterHorrors;

public class EntityBehaviorOceanCreature : EntityBehavior
{
    protected IPlayer targetPlayer;
    protected bool targetResolved;

    public EntityBehaviorOceanCreature(Entity entity) : base(entity) { }

    public override void Initialize(EntityProperties properties, JsonObject attributes)
    {
    }

    public override void OnGameTick(float deltaTime)
    {
        if (!entity.Alive) return;
        if (entity.Api.Side != EnumAppSide.Server) return;
    }

    protected IPlayer ResolveTargetPlayer()
    {
        if (targetResolved) return targetPlayer;
        targetResolved = true;

        targetPlayer = TargetingHelper.ResolveTarget(entity);
        return targetPlayer;
    }

    protected bool IsTargetOnBoat()
    {
        var player = ResolveTargetPlayer();
        return player?.Entity?.MountedOn != null;
    }

    protected bool IsTargetInWater()
    {
        var player = ResolveTargetPlayer();
        if (player?.Entity == null) return false;

        var pos = player.Entity.SidedPos.AsBlockPos;
        var block = entity.World.BlockAccessor.GetBlock(pos);
        return block != null && block.Code?.Path?.StartsWith("saltwater") == true;
    }

    protected double DistanceToTarget()
    {
        var player = ResolveTargetPlayer();
        if (player?.Entity == null) return double.MaxValue;

        return entity.SidedPos.DistanceTo(player.Entity.SidedPos.XYZ);
    }

    protected double HorizontalDistanceToTarget()
    {
        var player = ResolveTargetPlayer();
        if (player?.Entity == null) return double.MaxValue;

        double dx = entity.SidedPos.X - player.Entity.SidedPos.X;
        double dz = entity.SidedPos.Z - player.Entity.SidedPos.Z;
        return System.Math.Sqrt(dx * dx + dz * dz);
    }

    public override string PropertyName() => "underwaterhorrors:oceancreature";
}
