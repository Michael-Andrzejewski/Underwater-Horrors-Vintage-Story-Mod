using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;

namespace UnderwaterHorrors;

/// <summary>
/// Base class for kraken helper entities that should be completely
/// transparent to interaction: chain segments, the invisible tentacle
/// head controllers, and the base stubs. IsInteractable=false removes
/// them from melee picking (SystemMouseInWorldInteractions filters on
/// it) and from projectile hits (EntityProjectile.CanHitTarget checks
/// it), so the only hittable kraken parts are the visible claws and
/// the body itself.
/// </summary>
public class EntityKrakenPart : EntityAgent
{
    public override bool IsInteractable => false;

    // Never written to the savegame. See KrakenPartStorage.
    public override bool StoreWithChunk => KrakenPartStorage.ShouldStore(this);
}

/// <summary>
/// The kraken parts a player is meant to hit: the claws that appear around
/// a gripped player and the claw tip of each tentacle chain. Interactable,
/// unlike EntityKrakenPart, but just as temporary.
/// </summary>
public class EntityKrakenClaw : EntityAgent
{
    public override bool StoreWithChunk => KrakenPartStorage.ShouldStore(this);
}

/// <summary>
/// Whether a kraken helper entity belongs in the savegame.
///
/// Only the kraken body is saved. Its tentacles, chain segments, claws and
/// lights are rebuilt from scratch by the body (EntityBehaviorKrakenBody)
/// whenever it loads, because every link between them (the body's list of
/// tentacles, each tentacle's list of segments) lives in memory only. A
/// saved part used to come back after a restart or chunk reload with
/// nothing driving it, frozen in the water forever, while the body grew a
/// complete second set beside it.
///
/// Static display pieces from /uh kraken show have no body to rebuild them
/// and are kept.
/// </summary>
public static class KrakenPartStorage
{
    public const string StaticAttr = "underwaterhorrors:static";

    // Every kraken helper code. Used to clear out parts that older versions
    // of the mod saved before this rule existed.
    public static readonly string[] TransientCodes =
    {
        "krakententacle", "krakenambienttentacle", "krakententacleclaw",
        "krakententsegment", "krakententsegment_mid", "krakententsegment_mid_claw",
        "krakententsegment_outer", "biolight"
    };

    public static bool ShouldStore(Entity entity) =>
        entity.WatchedAttributes.GetBool(StaticAttr, false);

    public static bool IsTransientCode(string path) =>
        System.Array.IndexOf(TransientCodes, path) >= 0;
}
