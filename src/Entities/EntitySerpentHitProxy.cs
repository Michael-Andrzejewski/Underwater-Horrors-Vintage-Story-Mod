using Vintagestory.API.Common;

namespace UnderwaterHorrors;

/// <summary>
/// Invisible hit-proxy entity positioned along a serpent's body by
/// EntityBehaviorSerpentHitProxies. Interactable (so melee and arrows
/// connect) but never persisted: the owning serpent respawns a fresh
/// row of proxies after a reload, so saving them would only produce
/// orphaned duplicates.
/// </summary>
public class EntitySerpentHitProxy : EntityAgent
{
    public override bool StoreWithChunk => false;

    // The serpent that drives this proxy is AlwaysActive and can be up
    // to ~80 blocks from the player; keep the proxy active too so its
    // position updates are never suspended.
    public override bool AlwaysActive => true;
}
