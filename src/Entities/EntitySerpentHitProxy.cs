using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.MathTools;

namespace UnderwaterHorrors;

/// <summary>
/// Invisible hit-proxy entity positioned along a serpent's body by
/// EntityBehaviorSerpentHitProxies. Interactable (so melee and arrows
/// connect) but never persisted: the owning serpent respawns a fresh
/// row of proxies after a reload, so saving them would only produce
/// orphaned duplicates.
///
/// Right-clicking a proxy forwards to the owning serpent's harvest
/// behavior, so a dead serpent can be harvested from any point on its
/// body rather than only the small hitbox at the entity center.
/// </summary>
public class EntitySerpentHitProxy : EntityAgent
{
    public override bool StoreWithChunk => false;

    // The serpent that drives this proxy is AlwaysActive and can be up
    // to ~80 blocks from the player; keep the proxy active too so its
    // position updates are never suspended.
    public override bool AlwaysActive => true;

    public override void OnInteract(EntityAgent byEntity, ItemSlot itemslot, Vec3d hitPosition, EnumInteractMode mode)
    {
        if (mode == EnumInteractMode.Interact && Api.Side == EnumAppSide.Server)
        {
            long targetId = WatchedAttributes.GetLong(EntityBehaviorDamageRelay.TargetAttr);
            Entity target = targetId != 0 ? World.GetEntityById(targetId) : null;
            var harvest = target?.GetBehavior<EntityBehaviorSerpentHarvest>();
            if (harvest != null && harvest.TryHarvest(byEntity, itemslot, Pos.XYZ)) return;
        }

        base.OnInteract(byEntity, itemslot, hitPosition, mode);
    }
}
