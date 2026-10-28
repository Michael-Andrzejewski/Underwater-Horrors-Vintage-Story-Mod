using System;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Datastructures;
using Vintagestory.API.Util;
using Vintagestory.GameContent;

namespace UnderwaterHorrors;

/// <summary>
/// The game's carcass harvest (hold shift and right click with a knife, then
/// the carcass contents dialog), for the mod's creatures and what is left of
/// them:
///
/// - a dead serpent, from any point on its body: its hit proxies
///   (EntitySerpentHitProxy) pass the knife and the click on to here, and the
///   dialog stays with the proxy that was clicked, since the serpent's own
///   position may be 20 blocks away;
/// - each piece of a broken-up Rust Leviathan (GiantSerpentPieceEntity), one
///   at a time. A piece is remains, not a dead creature, so it is harvestable
///   while "alive". Emptied, it crumbles into a pile of bones;
/// - a dead giant anglerfish.
///
/// The loot is the JSON "drops" plus whatever the entity and its behaviors
/// add through IHarvestableDrops (the serpent and piece loot, which follow the
/// config). Only a real death is harvested: a serpent that was removed or
/// unloaded is not a carcass.
/// </summary>
public class EntityBehaviorRemainsHarvestable : EntityBehaviorHarvestable, IHarvestable
{
    public EntityBehaviorRemainsHarvestable(Entity entity) : base(entity) { }

    private bool Remains => entity is GiantSerpentPieceEntity;

    public override void Initialize(EntityProperties properties, JsonObject typeAttributes)
    {
        base.Initialize(properties, typeAttributes);
        if (entity.World.Side != EnumAppSide.Server) return;
        // The drops may all come from IHarvestableDrops.
        jsonDrops ??= Array.Empty<BlockDropItemStack>();
        if (Remains)
        {
            inv.OnInventoryClosed += player =>
            {
                if (IsHarvested && inv.Empty) ((GiantSerpentPieceEntity)entity).Crumble();
            };
        }
    }

    public new bool IsHarvestable(ItemSlot slot, Entity forEntity)
    {
        if (IsHarvested) return false;
        if (Remains) return true;
        if (entity.Alive) return false;
        return entity.GetBehavior<EntityBehaviorSerpentDeath>() == null
            || entity.WatchedAttributes.GetBool(EntityBehaviorSerpentDeath.DiedForRealAttr);
    }

    /// <summary>
    /// Opens what the harvest yielded, for the player who clicked. The dialog
    /// keeps to <paramref name="anchor"/>: it closes once the player is out of
    /// reach of it, and sends its slot clicks to it.
    /// </summary>
    public void OpenContents(EntityAgent byEntity, Entity anchor)
    {
        if (!IsHarvested || byEntity is not EntityPlayer byPlayer) return;
        IPlayer player = entity.World.PlayerByUid(byPlayer.PlayerUID);
        if (player == null) return;
        player.InventoryManager.OpenInventory(inv);
        if (entity.World.Side != EnumAppSide.Client || dlg != null) return;
        var capi = (ICoreClientAPI)entity.Api;
        dlg = new GuiDialogCreatureContents(inv, anchor, capi, "carcasscontents");
        if (dlg.TryOpen()) capi.Network.SendPacketClient(inv.Open(player));
        dlg.OnClosed += () =>
        {
            dlg.Dispose();
            dlg = null;
        };
    }

    public override void OnInteract(EntityAgent byEntity, ItemSlot itemslot, Vintagestory.API.MathTools.Vec3d hitPosition, EnumInteractMode mode, ref EnumHandling handled)
    {
        // A piece lies across many blocks; the base measures from its pivot.
        if (Remains && mode == EnumInteractMode.Interact) OpenContents(byEntity, entity);
        else base.OnInteract(byEntity, itemslot, hitPosition, mode, ref handled);
    }

    public override WorldInteraction[] GetInteractionHelp(IClientWorldAccessor world, EntitySelection es, IClientPlayer player, ref EnumHandling handled)
    {
        if (!Remains) return base.GetInteractionHelp(world, es, player, ref handled);
        if (IsHarvested) return null;
        return new[]
        {
            new WorldInteraction
            {
                ActionLangCode = "blockhelp-creature-harvest",
                MouseButton = EnumMouseButton.Right,
                HotKeyCode = "shift",
                Itemstacks = ObjectCacheUtil.GetToolStacks(world.Api, EnumTool.Knife)
            }
        };
    }

    public override string PropertyName() => "underwaterhorrors:harvestable";
}
