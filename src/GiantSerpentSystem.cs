using Vintagestory.API.Common;

namespace UnderwaterHorrors;

/// <summary>
/// Registers the giant serpent's entity class and behaviors. Spawning (the
/// natural roll and /uh spawn giantserpent) lives with the other creatures
/// in UnderwaterHorrorsModSystem.
/// </summary>
public class GiantSerpentSystem : ModSystem
{
    public override void Start(ICoreAPI api)
    {
        api.RegisterEntity("GiantSerpentEntity", typeof(GiantSerpentEntity));
        api.RegisterEntityBehaviorClass("underwaterhorrors:giantserpentai", typeof(EntityBehaviorGiantSerpentAI));
        api.RegisterEntityBehaviorClass("underwaterhorrors:giantserpentbody", typeof(EntityBehaviorGiantSerpentBody));
        api.RegisterEntityBehaviorClass("underwaterhorrors:giantserpenthitproxies", typeof(EntityBehaviorGiantSerpentHitProxies));
    }
}
