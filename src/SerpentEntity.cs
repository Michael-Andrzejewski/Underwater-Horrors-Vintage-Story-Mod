using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;

namespace UnderwaterHorrors;

/// <summary>
/// Base entity class for both sea serpent variants.  Exists to:
///
/// 1. Enlarge the frustum cull sphere so the long 9-block body doesn't
///    get culled when only the middle is slightly outside the view
///    frustum — the head/tail parts would visibly pop out of existence.
///    Default FrustumSphereRadius is max(3, max(hitbox X, Y)) = 3 for
///    our serpent, which is far smaller than the actual rendered body.
///
/// 2. Keep the entity always-active regardless of proximity to players,
///    so AI and animations don't suspend at range — the serpent is
///    supposed to orbit up to 80 blocks out.
/// </summary>
public class SerpentEntity : EntityAgent
{
    // Half the longest visible extent of the model (~9 blocks tip-to-tip)
    // plus a safety margin.  The renderer culls a sphere of this radius
    // centered on Pos; anything inside the radius gets rendered.
    public override double FrustumSphereRadius => 15.0;

    // Disables the server-side proximity suspension — without this, when
    // the entity is >32 blocks from any player, VS pauses its
    // behavior ticks and simulation, which we don't want for a stalking
    // predator that spawns at long range.
    public override bool AlwaysActive => true;
}

/// <summary>
/// Deep serpent variant.  Inherits the render/activity overrides above
/// AND additionally disables VS's motion-driven body rotation hacks
/// that fire inside EntityShapeRenderer:
///
/// - CanStepPitch: VS measures (Pos.Y - prevY) every frame and injects
///   up to ±0.3 rad of Z-axis rotation (roll) into the model matrix.
///   Designed for four-legged land animals leaning when stepping up a
///   block — disastrous for a long horizontal serpent, where any Y
///   jitter snaps the head/tail toward the sky.  Only kill switch is
///   overriding this virtual to false.
///
/// - CanSwivel / CanSwivelNow: VS injects up to ±0.4 rad of X-axis
///   rotation based on motion-direction changes (leaning into turns).
///   Visible as head/tail flipping up during heading adjustments.
///
/// The regular serpent keeps these enabled — its AI writes pitch
/// intentionally for surfacing/hiss/attack poses, and the step-pitch
/// feels natural for a surface swimmer.
/// </summary>
public class DeepSerpentEntity : SerpentEntity
{
    public override bool CanStepPitch => false;
    public override bool CanSwivel => false;
    public override bool CanSwivelNow => false;
}
