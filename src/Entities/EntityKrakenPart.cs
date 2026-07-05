using Vintagestory.API.Common;

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
}
