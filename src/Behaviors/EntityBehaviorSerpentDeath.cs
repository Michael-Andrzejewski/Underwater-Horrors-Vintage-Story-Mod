using System;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.GameContent;

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
///  - barrel-rolls the corpse belly-up around its LONG BODY AXIS. No
///    EntityPos field maps to that axis: in EntityShapeRenderer's
///    matrix, Pos.Roll is applied around the model-local Z axis (a
///    nose-over-tail somersault for a body that lies along local X)
///    and Pos.Pitch around the world X axis. The renderer's public
///    xangle field is the innermost rotation, around model-local X —
///    exactly the body axis — so each client drives it 0 → π locally.
///    (xangle is otherwise only written by the waterBobbing wobble,
///    which serpents don't enable; it is forced off anyway.)
///  - sinks the corpse to the sea floor. Physics leaves a dead
///    sea-habitat entity inert (no gravity, no buoyancy integration),
///    so the server lowers Pos.Y directly each tick until a down-scan
///    finds the first solid block, and zeroes Motion so nothing else
///    can drift the body. Direct server Pos writes are polled and
///    broadcast by the entity tracker, and interpolateposition lerps
///    them client-side, so the descent renders smoothly.
/// </summary>
public class EntityBehaviorSerpentDeath : EntityBehavior
{
    public const string DiedForRealAttr = "underwaterhorrors:diedForReal";

    // ~6 seconds to turn fully belly-up.
    private const float RollRateRadPerSec = 0.5f;
    private const float TargetRoll = (float)Math.PI;

    // Slow descent (blocks per second) and how often the floor scan
    // refreshes while sinking.
    private const float SinkSpeedPerSec = 0.4f;
    private const float FloatSpeedPerSec = 0.8f;
    private const float FloorRescanInterval = 1f;

    private bool animsStopped;

    // Client-side barrel roll angle written to the renderer.
    private float clientRoll;

    // Server-side sink target. int.MinValue = not yet scanned or no
    // floor found within scan range (keep sinking and rescan).
    private int restY = int.MinValue;
    private float rescanTimer;

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
            if (animsStopped)
            {
                animsStopped = false;
                clientRoll = 0f;
                restY = int.MinValue;
                rescanTimer = 0f;
                if (entity.Properties?.Client?.Renderer is EntityShapeRenderer esr)
                {
                    esr.xangle = 0f;
                }
            }
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

        if (!entity.WatchedAttributes.GetBool(DiedForRealAttr)) return;

        if (entity.Api.Side == EnumAppSide.Client)
        {
            // Body-axis barrel roll, computed locally on every client.
            if (entity.Properties?.Client?.Renderer is EntityShapeRenderer renderer)
            {
                renderer.waterBobbing = false;   // sole other writer of xangle
                clientRoll = Math.Min(TargetRoll, clientRoll + RollRateRadPerSec * deltaTime);
                renderer.xangle = clientRoll;
                renderer.yangle = 0f;
                renderer.zangle = 0f;
            }
            return;
        }

        var pos = entity.Pos;

        // Level the body: a kill mid-attack can leave aim pitch behind,
        // and older saves may carry a nonzero Roll from the previous
        // (somersaulting) death animation.
        pos.Pitch += (0f - pos.Pitch) * Math.Min(1f, deltaTime * 2f);
        pos.Roll += (0f - pos.Roll) * Math.Min(1f, deltaTime * 2f);
        pos.HeadPitch = 0f;

        var cfg = UnderwaterHorrorsModSystem.Config;
        if (cfg != null && cfg.SerpentCorpseFloats)
        {
            // Rise to just under the surface so a kill from a boat can be
            // harvested from the boat. Stops under any solid ceiling.
            pos.Motion.Set(0, 0, 0);
            double surfaceY = entity.World.SeaLevel - 0.6;
            if (pos.Y < surfaceY)
            {
                var above = new Vintagestory.API.MathTools.BlockPos(
                    (int)Math.Floor(pos.X), (int)Math.Floor(pos.Y + 1.5), (int)Math.Floor(pos.Z), pos.Dimension);
                Block roof = entity.World.BlockAccessor.GetBlock(above);
                bool blocked = roof != null && roof.Id != 0 && !roof.IsLiquid() && roof.Replaceable < 6000;
                if (!blocked) pos.Y = Math.Min(pos.Y + FloatSpeedPerSec * deltaTime, surfaceY);
            }
            return;
        }

        // Sink to the sea floor. Rescan periodically so a corpse over a
        // ledge (or beyond the initial 80-block scan) still finds bottom.
        rescanTimer -= deltaTime;
        if (rescanTimer <= 0f)
        {
            rescanTimer = FloorRescanInterval;
            restY = EntityBehaviorSerpentHarvest.FindRestingYBelow(
                entity.World.BlockAccessor, pos.X, pos.Y + 1, pos.Z, pos.Dimension);
        }

        pos.Motion.Set(0, 0, 0);

        if (restY < 0 || pos.Y > restY)
        {
            double newY = pos.Y - SinkSpeedPerSec * deltaTime;
            if (restY >= 0 && newY < restY) newY = restY;
            pos.Y = newY;
        }
    }

    public override string PropertyName() => "underwaterhorrors:serpentdeath";
}
