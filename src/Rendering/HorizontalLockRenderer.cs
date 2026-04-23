using Vintagestory.API.Client;
using Vintagestory.API.Common.Entities;

namespace UnderwaterHorrors;

/// <summary>
/// Client-side renderer that runs after InterpolatePosition (which
/// registers at stage Before with RenderOrder 0.0).  Uses RenderOrder
/// 1.0, so this executes strictly AFTER the interpolator has written
/// Pos.Pitch/Roll/HeadPitch each frame — and zeroes them before the
/// entity is drawn.
///
/// Skipped during attack phases so the head can aim at the player.
/// </summary>
public class HorizontalLockRenderer : IRenderer
{
    private readonly ICoreClientAPI capi;
    private readonly Entity entity;
    private readonly EntityBehaviorDeepSerpentAI behavior;

    public HorizontalLockRenderer(ICoreClientAPI capi, Entity entity, EntityBehaviorDeepSerpentAI behavior)
    {
        this.capi = capi;
        this.entity = entity;
        this.behavior = behavior;
    }

    public double RenderOrder => 1.0;
    public int RenderRange => 9999;

    public void OnRenderFrame(float deltaTime, EnumRenderStage stage)
    {
        if (entity == null || !entity.Alive) return;
        // During attack, let the AI-computed pitch through so the mouth
        // can point at the player.
        if (behavior != null && behavior.IsInAttackPhase) return;
        EntityBehaviorDeepSerpentAI.ForceHorizontal(entity.Pos);
        EntityBehaviorDeepSerpentAI.ForceHorizontal(entity.Pos);
    }

    public void Dispose() { }
}
