using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.MathTools;

namespace UnderwaterHorrors;

/// <summary>
/// Restores a serpent spawner block when a serpent that came from one
/// despawns gracefully. Attached to the base sea serpent; inert unless the
/// serpent carries the fromSpawner tag written by
/// <see cref="BlockEntityCreatureSpawner"/>.
///
/// Only a graceful Expire (the serpent retreated because the player left
/// the water, hit a timeout, etc.) brings the block back. A kill fires with
/// reason Death and a chunk unload with reason Unload, and neither restores
/// the block: killing the serpent clears the encounter for good, and an
/// unload leaves the still-alive serpent on disk to reload later without a
/// duplicate block.
/// </summary>
public class EntityBehaviorSpawnerReturn : EntityBehavior
{
    public EntityBehaviorSpawnerReturn(Entity entity) : base(entity) { }

    public override string PropertyName() => "underwaterhorrors:spawnerreturn";

    public override void OnEntityDespawn(EntityDespawnData despawn)
    {
        if (entity.Api.Side == EnumAppSide.Server
            && despawn != null
            && despawn.Reason == EnumDespawnReason.Expire
            && entity.WatchedAttributes.GetBool(BlockEntityCreatureSpawner.FromSpawnerAttr))
        {
            RestoreSpawnerBlock();
        }

        base.OnEntityDespawn(despawn);
    }

    private void RestoreSpawnerBlock()
    {
        var wa = entity.WatchedAttributes;

        string code = wa.GetString(BlockEntityCreatureSpawner.SpawnerBlockCodeAttr);
        if (string.IsNullOrEmpty(code)) return;

        Block block = entity.World.GetBlock(new AssetLocation(code));
        if (block == null) return;

        var pos = new BlockPos(
            wa.GetInt(BlockEntityCreatureSpawner.SpawnerXAttr),
            wa.GetInt(BlockEntityCreatureSpawner.SpawnerYAttr),
            wa.GetInt(BlockEntityCreatureSpawner.SpawnerZAttr),
            wa.GetInt(BlockEntityCreatureSpawner.SpawnerDimAttr));

        var accessor = entity.World.BlockAccessor;

        // Only restore into empty space so we never overwrite something a
        // player built where the spawner used to be.
        Block current = accessor.GetBlock(pos);
        if (current != null && current.Id != 0 && !current.IsReplacableBy(block)) return;

        accessor.SetBlock(block.BlockId, pos);
        accessor.MarkBlockDirty(pos);
    }
}
