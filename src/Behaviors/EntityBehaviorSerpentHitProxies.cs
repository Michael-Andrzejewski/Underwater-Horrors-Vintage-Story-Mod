using System;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Datastructures;

namespace UnderwaterHorrors;

/// <summary>
/// Spawns and drives a row of invisible hit-proxy entities along the
/// serpent's spine so melee swings and projectiles connect with the
/// long visible body instead of only the 2-block hitbox at the entity
/// center. Each proxy relays its damage to the serpent via
/// EntityBehaviorDamageRelay.
///
/// Why proxies and not the vanilla "selectionboxes" behavior (as used
/// by boats): the server validates attacks against the TARGET ENTITY's
/// base selection box (ServerSystemEntitySimulation.HandleEntityInteraction
/// rejects hits farther than 2x weapon range from that box), so
/// attachment-point boxes 10+ blocks from the serpent's center would
/// get picked on the client and then silently discarded on the server.
/// Projectiles likewise only test per-entity AABBs. Separate proxy
/// entities positioned on the body pass both checks naturally.
///
/// Offsets are in blocks along the facing axis (positive = toward the
/// head) and come from the entity JSON, because the serpent models
/// extend differently around the entity origin (seaserpent/seaserpent2
/// have the head ~10 blocks ahead of the origin; seaserpent3 has the
/// head at the origin with the body trailing behind).
/// </summary>
public class EntityBehaviorSerpentHitProxies : EntityBehavior
{
    private static readonly AssetLocation ProxyAsset =
        new AssetLocation("underwaterhorrors", "serpenthitbox");

    private float[] offsets = Array.Empty<float>();
    private float spineHeight = 0.75f;

    private long[] proxyIds;
    private Entity[] proxies;
    private bool spawned;

    // Missing-proxy respawn throttle. A proxy can be lost to a chunk
    // unload at the loaded-area edge; rather than checking every tick,
    // retry every few seconds.
    private float respawnCheckTimer;
    private const float RespawnCheckInterval = 5f;

    public EntityBehaviorSerpentHitProxies(Entity entity) : base(entity) { }

    public override void Initialize(EntityProperties properties, JsonObject attributes)
    {
        base.Initialize(properties, attributes);
        offsets = attributes["offsets"].AsArray<float>(Array.Empty<float>());
        spineHeight = attributes["spineHeight"].AsFloat(0.75f);
    }

    public override void OnGameTick(float deltaTime)
    {
        if (entity.Api.Side != EnumAppSide.Server) return;
        if (!entity.Alive) return;
        if (offsets.Length == 0) return;

        if (!spawned)
        {
            spawned = true;
            proxyIds = new long[offsets.Length];
            proxies = new Entity[offsets.Length];
        }

        respawnCheckTimer -= deltaTime;
        bool maySpawn = respawnCheckTimer <= 0;
        if (maySpawn) respawnCheckTimer = RespawnCheckInterval;

        UpdateProxies(maySpawn);
    }

    private void UpdateProxies(bool maySpawn)
    {
        // Forward axis: same convention as the AIs' GetHeadPosition —
        // forward = (sin(yaw), 0, cos(yaw)). Positive pitch tilts the
        // nose down (see UpdateFacing), so forward Y = -sin(pitch).
        float yaw = entity.Pos.Yaw;
        float pitch = entity.Pos.Pitch;
        double cosP = Math.Cos(pitch);
        double fx = Math.Sin(yaw) * cosP;
        double fy = -Math.Sin(pitch);
        double fz = Math.Cos(yaw) * cosP;

        for (int i = 0; i < offsets.Length; i++)
        {
            Entity proxy = proxies[i];
            if (proxy == null || !proxy.Alive)
            {
                proxy = proxyIds[i] != 0 ? entity.World.GetEntityById(proxyIds[i]) : null;
                proxies[i] = proxy;
            }

            if (proxy == null || !proxy.Alive)
            {
                if (!maySpawn) continue;
                proxy = SpawnProxyAt(i);
                if (proxy == null) continue;
            }

            double off = offsets[i];
            // Entity position is the bottom center of the hitbox; place
            // the box so its vertical middle sits on the spine line.
            double halfHeight = proxy.SelectionBox != null ? proxy.SelectionBox.Y2 / 2.0 : 1.1;
            proxy.Pos.SetPos(
                entity.Pos.X + fx * off,
                entity.Pos.Y + spineHeight + fy * off - halfHeight,
                entity.Pos.Z + fz * off);
        }
    }

    private Entity SpawnProxyAt(int index)
    {
        EntityProperties props = entity.World.GetEntityType(ProxyAsset);
        if (props == null) return null;

        Entity proxy = entity.World.ClassRegistry.CreateEntity(props);
        proxy.Pos.SetPos(entity.Pos.X, entity.Pos.Y, entity.Pos.Z);
        proxy.Pos.Dimension = entity.Pos.Dimension;
        proxy.Pos.SetFrom(proxy.Pos);
        proxy.WatchedAttributes.SetLong(EntityBehaviorDamageRelay.TargetAttr, entity.EntityId);
        entity.World.SpawnEntity(proxy);

        proxyIds[index] = proxy.EntityId;
        proxies[index] = proxy;
        return proxy;
    }

    private void KillProxies()
    {
        if (proxyIds == null) return;
        for (int i = 0; i < proxyIds.Length; i++)
        {
            long id = proxyIds[i];
            if (id == 0) continue;
            Entity proxy = entity.World.GetEntityById(id);
            if (proxy != null && proxy.Alive)
            {
                proxy.Die(EnumDespawnReason.Expire);
            }
            proxyIds[i] = 0;
            proxies[i] = null;
        }
    }

    public override void OnEntityDeath(DamageSource damageSourceForDeath)
    {
        base.OnEntityDeath(damageSourceForDeath);
        if (entity.Api.Side == EnumAppSide.Server) KillProxies();
    }

    public override void OnEntityDespawn(EntityDespawnData despawn)
    {
        if (entity.Api.Side == EnumAppSide.Server) KillProxies();
        base.OnEntityDespawn(despawn);
    }

    public override string PropertyName() => "underwaterhorrors:serpenthitproxies";
}
