using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;

namespace UnderwaterHorrors;

/// <summary>
/// Custom EntityAgent for the serpents.  Exists solely to disable
/// VS's built-in motion-driven rotation hacks that fire inside
/// EntityShapeRenderer:
///
/// 1. <see cref="CanStepPitch"/> — VS measures (entity.Pos.Y - prevY)
///    each render frame and injects up to ±0.3 rad (~17°) of Z-axis
///    rotation (roll) into the model matrix.  For a long horizontal
///    serpent that translates directly to "head or tail going toward
///    the sky" on any vertical jitter.  The only switch is this
///    property: when false, EntityShapeRenderer.updateStepPitch early-
///    returns with stepPitch = 0.
///
/// 2. <see cref="CanSwivel"/> — VS injects up to ±0.4 rad of X-axis
///    rotation based on motion direction changes (the
///    "sidewaysSwivelAngle" that leans animals into turns).  On a
///    9-block-long body this is visible as head/tail flipping up
///    when the AI adjusts heading.
///
/// Both are strictly render-time hacks meant for four-legged land
/// mobs.  Not appropriate for a large, always-horizontal sea
/// creature.
/// </summary>
public class SerpentEntity : EntityAgent
{
    public override bool CanStepPitch => false;
    public override bool CanSwivel => false;
    public override bool CanSwivelNow => false;
}
