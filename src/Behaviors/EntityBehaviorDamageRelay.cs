using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;

namespace UnderwaterHorrors;

/// <summary>
/// Forwards all damage this entity receives to another entity (the
/// "relay target", set via WatchedAttributes when the owner spawns it).
/// Used by the serpent hit proxies and the kraken tentacle tip claw so
/// that hitting the visible body part damages the actual creature —
/// which then shows the hurt flash on its full model — while the proxy
/// itself absorbs nothing and needs no health behavior.
/// </summary>
public class EntityBehaviorDamageRelay : EntityBehavior
{
    public const string TargetAttr = "underwaterhorrors:relayTargetId";

    private long cachedTargetId;
    private Entity cachedTarget;

    public EntityBehaviorDamageRelay(Entity entity) : base(entity) { }

    public override void OnEntityReceiveDamage(DamageSource damageSource, ref float damage)
    {
        if (entity.Api.Side != EnumAppSide.Server) return;
        if (damage <= 0f) return;
        if (damageSource?.Type == EnumDamageType.Heal) return;

        long targetId = entity.WatchedAttributes.GetLong(TargetAttr);
        // Zero the local damage in every path: the relay carrier is an
        // invisible helper that should never accumulate hurt state or
        // knockback, whether or not a live target exists.
        float forwarded = damage;
        damage = 0f;

        if (targetId == 0 || targetId == entity.EntityId) return;

        Entity target = cachedTarget;
        if (target == null || cachedTargetId != targetId || !target.Alive)
        {
            target = entity.World.GetEntityById(targetId);
            cachedTarget = target;
            cachedTargetId = targetId;
        }

        if (target == null || !target.Alive) return;

        target.ReceiveDamage(damageSource, forwarded);
    }

    public override string PropertyName() => "underwaterhorrors:damagerelay";
}
