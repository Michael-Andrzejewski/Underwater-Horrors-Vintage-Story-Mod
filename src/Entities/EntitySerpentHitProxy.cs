using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Datastructures;
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
    /// <summary>
    /// Width and height of this proxy's box, when it differs from the
    /// entity type's. The giant serpent sizes each box to the body where it
    /// sits. Set before the proxy spawns, so clients get it with the spawn;
    /// Combat Overhaul tests its swings and arrows against the client's
    /// collision box, and vanilla attacks pick the selection box.
    /// </summary>
    public const string BoxAttr = "underwaterhorrors:proxyBox";

    private bool boxApplied;

    public override bool StoreWithChunk => false;

    public override void Initialize(EntityProperties properties, ICoreAPI api, long InChunkIndex3d)
    {
        base.Initialize(properties, api, InChunkIndex3d);
        ApplyBox();
    }

    public override void OnGameTick(float dt)
    {
        if (!boxApplied) ApplyBox();
        base.OnGameTick(dt);
    }

    private void ApplyBox()
    {
        if (WatchedAttributes[BoxAttr] is not FloatArrayAttribute box || box.value.Length < 2) return;
        SetCollisionBox(box.value[0], box.value[1]);
        SetSelectionBox(box.value[0], box.value[1]);
        boxApplied = true;
    }

    // The serpent that drives this proxy is AlwaysActive and can be up
    // to ~80 blocks from the player; keep the proxy active too so its
    // position updates are never suspended.
    public override bool AlwaysActive => true;

    public override void OnInteract(EntityAgent byEntity, ItemSlot itemslot, Vec3d hitPosition, EnumInteractMode mode)
    {
        if (mode == EnumInteractMode.Interact && Api.Side == EnumAppSide.Server)
        {
            var harvest = Owner?.GetBehavior<EntityBehaviorSerpentHarvest>();
            if (harvest != null && harvest.TryHarvest(byEntity, itemslot, Pos.XYZ)) return;
        }

        base.OnInteract(byEntity, itemslot, hitPosition, mode);
    }

    // Looking at any part of the body names the serpent and shows its
    // health, instead of the proxy's own untranslated entity code.
    public override string GetName() => Owner?.GetName() ?? base.GetName();

    public override string GetInfoText() => Owner?.GetInfoText() ?? base.GetInfoText();

    private Entity Owner
    {
        get
        {
            long targetId = WatchedAttributes.GetLong(EntityBehaviorDamageRelay.TargetAttr);
            return targetId != 0 && targetId != EntityId ? World?.GetEntityById(targetId) : null;
        }
    }
}
