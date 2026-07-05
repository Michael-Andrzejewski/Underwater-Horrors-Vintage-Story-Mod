using System;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;

namespace UnderwaterHorrors;

/// <summary>
/// Death presentation for the serpents. Vanilla Die() clears the active
/// animations and starts "die", which the serpent shapes don't have, so
/// without this the last swim/slither loop keeps playing on the client
/// while the corpse sinks. This behavior:
///
///  - marks real (damage) deaths in WatchedAttributes so harvest and
///    bone-pile logic can tell a kill from a retreat-despawn (vanilla
///    only fires OnEntityDeath for EnumDespawnReason.Death),
///  - force-stops all animations on whichever side it runs so the body
///    goes limp instead of looping,
///  - slowly barrel-rolls the corpse belly-up. The server writes
///    Pos.Roll (== ServerPos in 1.22) and the client's
///    interpolateposition behavior lerps it, so the turn renders
///    smoothly. Pitch eases back to level at the same time. Sinking is
///    left entirely to physics, which already pulls the corpse down.
/// </summary>
public class EntityBehaviorSerpentDeath : EntityBehavior
{
    public const string DiedForRealAttr = "underwaterhorrors:diedForReal";

    // ~6 seconds to turn fully belly-up.
    private const float RollRateRadPerSec = 0.5f;
    private const float TargetRoll = (float)Math.PI;

    private bool animsStopped;

    public EntityBehaviorSerpentDeath(Entity entity) : base(entity) { }

    public override void OnEntityDeath(DamageSource damageSourceForDeath)
    {
        base.OnEntityDeath(damageSourceForDeath);
        if (entity.Api.Side == EnumAppSide.Server)
        {
            entity.WatchedAttributes.SetBool(DiedForRealAttr, true);
        }
    }

    public override void OnGameTick(float deltaTime)
    {
        if (entity.Alive)
        {
            // Covers the (edge) case of a corpse behavior instance being
            // reused after a revive-by-command.
            animsStopped = false;
            return;
        }

        // Die() clears the animation sync attribute, but the client-side
        // animator can keep the last loop running; stopping locally on
        // each side is the reliable kill switch.
        if (!animsStopped)
        {
            animsStopped = true;
            entity.AnimManager?.StopAllAnimations();
        }

        if (entity.Api.Side != EnumAppSide.Server) return;
        if (!entity.WatchedAttributes.GetBool(DiedForRealAttr)) return;

        var pos = entity.Pos;
        if (pos.Roll < TargetRoll)
        {
            pos.Roll = Math.Min(TargetRoll, pos.Roll + RollRateRadPerSec * deltaTime);
        }
        pos.Pitch += (0f - pos.Pitch) * Math.Min(1f, deltaTime * 2f);
        pos.HeadPitch = 0f;
    }

    public override string PropertyName() => "underwaterhorrors:serpentdeath";
}
