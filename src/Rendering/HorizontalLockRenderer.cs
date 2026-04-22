using Vintagestory.API.Client;
using Vintagestory.API.Common.Entities;

namespace UnderwaterHorrors;

/// <summary>
/// Runs on the client after InterpolatePosition's lerp (which registers at
/// stage Before with RenderOrder 0.0).  This renderer uses RenderOrder 1.0
/// within the same stage, so it executes strictly AFTER the interpolator
/// has written Pos.Pitch/Roll/HeadPitch — and zeroes them before the
/// entity is actually drawn.
///
/// Why not a game-tick hook instead?  Because InterpolatePosition's
/// OnRenderFrame re-writes Pos.Pitch every single render frame based on
/// server snapshots.  A game-tick write happens less often than render
/// frames, so anything we set during a tick would immediately be
/// overwritten on the next render.  A renderer at later order is the only
/// reliable override point.
/// </summary>
public class HorizontalLockRenderer : IRenderer
{
    private readonly ICoreClientAPI capi;
    private readonly Entity entity;

    public HorizontalLockRenderer(ICoreClientAPI capi, Entity entity)
    {
        this.capi = capi;
        this.entity = entity;
    }

    // Run after InterpolatePosition (which uses 0.0).
    public double RenderOrder => 1.0;

    // Far enough to always apply while entity is tracked.
    public int RenderRange => 9999;

    public void OnRenderFrame(float deltaTime, EnumRenderStage stage)
    {
        if (entity == null || !entity.Alive) return;
        EntityBehaviorDeepSerpentAI.ForceHorizontal(entity.Pos);
        EntityBehaviorDeepSerpentAI.ForceHorizontal(entity.ServerPos);
    }

    public void Dispose() { }
}
