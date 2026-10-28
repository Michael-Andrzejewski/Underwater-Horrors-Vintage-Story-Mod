using System.Text;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Config;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.GameContent;

namespace UnderwaterHorrors;

/// <summary>
/// Invisible hit-proxy entity positioned along a serpent's body by
/// EntityBehaviorSerpentHitProxies. Interactable (so melee and arrows
/// connect) but never persisted: the owning serpent respawns a fresh
/// row of proxies after a reload, so saving them would only produce
/// orphaned duplicates.
///
/// A dead serpent is harvested from any point on its body: the knife asks
/// the proxy (it is IHarvestable) and the proxy asks the serpent's carcass
/// (EntityBehaviorRemainsHarvestable); a right click opens the carcass
/// contents, and the dialog stays with this proxy.
///
/// Looking at a proxy names the serpent and shows its health. The serpent
/// writes its health onto every proxy, so this works even when the
/// serpent's own position (its mouth, up to 200 blocks along the Rust
/// Leviathan) is beyond the client's view of entities.
/// </summary>
public class EntitySerpentHitProxy : EntityAgent, IHarvestable
{
    /// <summary>
    /// Width and height of this proxy's box, when it differs from the
    /// entity type's. The giant serpent sizes each box to the body where it
    /// sits. Set before the proxy spawns, so clients get it with the spawn;
    /// Combat Overhaul tests its swings and arrows against the client's
    /// collision box, and vanilla attacks pick the selection box.
    /// </summary>
    public const string BoxAttr = "underwaterhorrors:proxyBox";
    /// <summary>The serpent's health and max health, written by the serpent.</summary>
    public const string OwnerHealthAttr = "underwaterhorrors:ownerHealth";
    /// <summary>The serpent's entity code, for its name when it is out of view.</summary>
    public const string OwnerCodeAttr = "underwaterhorrors:ownerCode";
    /// <summary>Show the health in every game mode, not just creative (the Rust Leviathan's).</summary>
    public const string AlwaysShowHealthAttr = "underwaterhorrors:alwaysShowHealth";

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

    // ═══════════════════════════════════════════════════════════════════
    //  Harvesting, for the serpent
    // ═══════════════════════════════════════════════════════════════════
    private EntityBehaviorRemainsHarvestable Carcass => Owner?.GetBehavior<EntityBehaviorRemainsHarvestable>();

    public bool IsHarvestable(ItemSlot slot, Entity forEntity) => Carcass?.IsHarvestable(slot, forEntity) == true;

    public float GetHarvestDuration(ItemSlot slot, Entity forEntity) => Carcass?.GetHarvestDuration(slot, forEntity) ?? 5f;

    public void SetHarvested(IPlayer byPlayer, float dropQuantityMultiplier = 1f) => Carcass?.SetHarvested(byPlayer, dropQuantityMultiplier);

    public override void OnInteract(EntityAgent byEntity, ItemSlot itemslot, Vec3d hitPosition, EnumInteractMode mode)
    {
        if (mode == EnumInteractMode.Interact && Carcass is { IsHarvested: true } carcass)
        {
            carcass.OpenContents(byEntity, this);
            return;
        }
        base.OnInteract(byEntity, itemslot, hitPosition, mode);
    }

    // The contents dialog sends its slot clicks to the entity it stays with.
    public override void OnReceivedClientPacket(IServerPlayer player, int packetid, byte[] data)
    {
        Entity owner = Owner;
        if (owner != null && Carcass != null) owner.OnReceivedClientPacket(player, packetid, data);
        else base.OnReceivedClientPacket(player, packetid, data);
    }

    // ═══════════════════════════════════════════════════════════════════
    //  Name and health
    // ═══════════════════════════════════════════════════════════════════
    public override string GetName()
    {
        Entity owner = Owner;
        if (owner != null) return owner.GetName();
        string code = WatchedAttributes.GetString(OwnerCodeAttr);
        if (code == null) return base.GetName();
        var loc = new AssetLocation(code);
        return Lang.GetMatching(loc.Domain + ":item-creature-" + loc.Path);
    }

    public override string GetInfoText()
    {
        string ownerText = Owner?.GetInfoText();
        bool creative = (Api as ICoreClientAPI)?.World.Player?.WorldData?.CurrentGameMode == EnumGameMode.Creative;
        bool always = WatchedAttributes.GetBool(AlwaysShowHealthAttr);
        // In creative the serpent's own text already has its health.
        if (ownerText != null && (creative || !always)) return ownerText;
        var text = new StringBuilder();
        if ((creative || always) && WatchedAttributes[OwnerHealthAttr] is FloatArrayAttribute h && h.value.Length >= 2)
            text.AppendLine(Lang.Get("Health: {0}/{1}", System.Math.Round(h.value[0], 1), System.Math.Round(h.value[1], 1)));
        if (ownerText != null) text.Append(ownerText);
        return text.ToString();
    }

    private Entity Owner
    {
        get
        {
            long targetId = WatchedAttributes.GetLong(EntityBehaviorDamageRelay.TargetAttr);
            return targetId != 0 && targetId != EntityId ? World?.GetEntityById(targetId) : null;
        }
    }
}
