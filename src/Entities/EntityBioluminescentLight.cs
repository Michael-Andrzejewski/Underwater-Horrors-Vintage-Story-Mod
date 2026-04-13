using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Datastructures;

namespace UnderwaterHorrors;

/// <summary>
/// A tiny invisible entity whose sole purpose is to emit dynamic light
/// via the VS lighting engine. Each tentacle segment spawns one of these
/// on the server, and the server continuously updates the HSV value stored
/// in WatchedAttributes. The client reads it and applies it to LightHsv,
/// which the engine uses to illuminate surrounding blocks/water.
///
/// This is the same pattern used by the Lantern Projection mod.
/// </summary>
public class EntityBioluminescentLight : EntityAgent
{
    public override void Initialize(EntityProperties properties, ICoreAPI api, long InChunkIndex3d)
    {
        base.Initialize(properties, api, InChunkIndex3d);
        ApplyHsvFromAttributes();
    }

    public override void OnGameTick(float dt)
    {
        base.OnGameTick(dt);
        ApplyHsvFromAttributes();
    }

    private void ApplyHsvFromAttributes()
    {
        byte[] hsv = WatchedAttributes.GetBytes("hsv");
        if (hsv != null && hsv.Length >= 3)
        {
            LightHsv = hsv;
        }
    }
}
