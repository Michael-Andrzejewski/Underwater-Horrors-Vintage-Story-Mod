using System;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.GameContent;

namespace UnderwaterHorrors;

/// <summary>A creature that turns on whoever tries to cage it.</summary>
public interface ICageRage
{
    void OnCageAttempt(IPlayer byPlayer);
}

/// <summary>
/// Stops the Animal Cages mod from throwing on our creatures. Attacking with
/// its medium cage reads the health behavior of whatever was hit, and the
/// serpents' hit proxies have none. The null reference ends up in the server's
/// packet loop, where a dedicated server disconnects the player for "an
/// unhandled exception". Most swings at a serpent land on a proxy.
///
/// This handler runs before the attack, so it needs nothing from that mod. It
/// runs for every click on any entity, and leaves after two string compares
/// unless a cage has swung at one of our creatures. Server only.
/// </summary>
public static class CageGuard
{
    private const string CageDomain = "animalcages";
    private const string OurDomain = "underwaterhorrors";
    // Animal Cages keeps its catch here. A full cage ignores what it hits.
    private const string CapturedAttr = "capturedEntity";

    public static void OnPlayerInteractEntity(Entity entity, IPlayer byPlayer, ItemSlot slot, Vec3d hitPosition, int mode, ref EnumHandling handling)
    {
        if (mode != 0) return;
        ItemStack stack = slot?.Itemstack;
        if (stack == null || !string.Equals(stack.Collectible?.Code?.Domain, CageDomain, StringComparison.Ordinal)) return;
        if (entity is not EntityAgent || !string.Equals(entity.Code?.Domain, OurDomain, StringComparison.Ordinal)) return;
        if (stack.Attributes.HasAttribute(CapturedAttr)) return;

        UnderwaterHorrorsConfig config = UnderwaterHorrorsModSystem.Config;
        string response = config?.CageAttemptResponse ?? UnderwaterHorrorsConfig.CageRage;

        if (response == UnderwaterHorrorsConfig.CageAllow)
        {
            // Animal Cages decides, as its own config says. A part with no
            // health behavior hands the swing to the creature it belongs to.
            if (entity.GetBehavior<EntityBehaviorHealth>() != null) return;
            handling = EnumHandling.PreventDefault;
            OwnerOf(entity)?.OnInteract(byPlayer.Entity, slot, hitPosition, EnumInteractMode.Attack);
            return;
        }

        handling = EnumHandling.PreventDefault;
        bool rage = response == UnderwaterHorrorsConfig.CageRage && !IsIgnoredObserver(byPlayer, config);
        if (rage) FindRager(entity)?.OnCageAttempt(byPlayer);
        (byPlayer as IServerPlayer)?.SendIngameError("uh-cage", rage
            ? "The cage will not hold this creature. Now it is coming for you."
            : "The cage will not hold this creature.");
    }

    private static bool IsIgnoredObserver(IPlayer player, UnderwaterHorrorsConfig config)
    {
        if (config?.IgnoreCreativePlayers != true) return false;
        EnumGameMode? gameMode = player?.WorldData?.CurrentGameMode;
        return gameMode == EnumGameMode.Creative || gameMode == EnumGameMode.Spectator;
    }

    // The creature behind what was hit: a hit proxy or claw tip to its owner, a
    // tentacle to its kraken, a lure to its fish. Three steps at most.
    private static ICageRage FindRager(Entity hit)
    {
        for (int i = 0; i < 4 && hit != null; i++)
        {
            foreach (EntityBehavior behavior in hit.SidedProperties.Behaviors)
            {
                if (behavior is ICageRage rager) return rager;
            }
            hit = OwnerOf(hit);
        }
        return null;
    }

    private static Entity OwnerOf(Entity part)
    {
        var attrs = part.WatchedAttributes;
        long id = attrs.GetLong(EntityBehaviorDamageRelay.TargetAttr);
        if (id == 0) id = attrs.GetLong("underwaterhorrors:krakenBodyId");
        if (id == 0) id = attrs.GetLong(EntityBehaviorAnglerfishAI.OwnerIdAttr);
        return id != 0 && id != part.EntityId ? part.World.GetEntityById(id) : null;
    }
}
