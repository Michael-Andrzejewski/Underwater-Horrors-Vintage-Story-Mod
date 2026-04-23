using System;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Datastructures;
using Vintagestory.API.Server;

namespace UnderwaterHorrors;

public class EntityBehaviorKrakenBody : EntityBehavior
{
    private bool tentaclesSpawned;
    private float contactDamageCooldown;
    private long attackTentacleId;
    private Entity cachedAttackTentacle;
    private float tentacleRespawnTimer;
    private bool waitingToRespawnTentacle;
    private UnderwaterHorrorsConfig config;

    // Cached AssetLocations
    private static readonly AssetLocation AmbientTentacleAsset = new AssetLocation("underwaterhorrors", "krakenambienttentacle");
    private static readonly AssetLocation AttackTentacleAsset = new AssetLocation("underwaterhorrors", "krakententacle");

    public EntityBehaviorKrakenBody(Entity entity) : base(entity) { }

    public override void Initialize(EntityProperties properties, JsonObject attributes)
    {
        config = UnderwaterHorrorsModSystem.Config;
    }

    public override void OnGameTick(float deltaTime)
    {
        if (!entity.Alive) return;
        if (entity.Api.Side != EnumAppSide.Server) return;

        // Stay stationary
        entity.Pos.Motion.X = 0;
        entity.Pos.Motion.Y = 0;
        entity.Pos.Motion.Z = 0;

        if (!tentaclesSpawned)
        {
            tentaclesSpawned = true;
            SpawnTentacles();
        }

        // Check if attack tentacle died and needs respawning
        CheckAttackTentacle(deltaTime);

        contactDamageCooldown -= deltaTime;
        if (contactDamageCooldown <= 0)
        {
            DealContactDamage();
            contactDamageCooldown = 1f;
        }
    }

    private void CheckAttackTentacle(float deltaTime)
    {
        if (waitingToRespawnTentacle)
        {
            tentacleRespawnTimer -= deltaTime;
            if (tentacleRespawnTimer <= 0)
            {
                waitingToRespawnTentacle = false;
                SpawnAttackTentacle();
            }
            return;
        }

        if (attackTentacleId == 0) return;

        // Use cached reference, re-validate if needed
        Entity tentacle = cachedAttackTentacle;
        if (tentacle == null || tentacle.EntityId != attackTentacleId)
        {
            tentacle = entity.World.GetEntityById(attackTentacleId);
            cachedAttackTentacle = tentacle;
        }

        // Start respawn timer when tentacle is dead OR sinking
        bool needsRespawn = (tentacle == null || !tentacle.Alive);
        if (!needsRespawn && tentacle != null)
        {
            needsRespawn = tentacle.WatchedAttributes.GetBool("underwaterhorrors:sinking", false);
        }

        if (needsRespawn)
        {
            var rand = entity.World.Rand;
            float delay = config.TentacleRespawnDelayMin + (float)(rand.NextDouble() * (config.TentacleRespawnDelayMax - config.TentacleRespawnDelayMin));
            if (config.DebugLogging)
                UnderwaterHorrorsModSystem.DebugLog(entity.Api, $"Kraken attack tentacle sinking/dead, new tentacle in {delay:F1}s");
            waitingToRespawnTentacle = true;
            tentacleRespawnTimer = delay;
            attackTentacleId = 0;
            cachedAttackTentacle = null;
        }
    }

    private void SpawnTentacles()
    {
        if (config.DebugLogging)
            UnderwaterHorrorsModSystem.DebugLog(entity.Api, $"Kraken spawning tentacles at ({entity.Pos.X:F1}, {entity.Pos.Y:F1}, {entity.Pos.Z:F1})");

        SpawnAttackTentacle();

        // Spawn ambient tentacles evenly spaced around body
        string targetUid = entity.WatchedAttributes.GetString("underwaterhorrors:targetPlayerUid");
        EntityProperties ambientProps = entity.World.GetEntityType(AmbientTentacleAsset);
        if (ambientProps != null)
        {
            int count = config.KrakenAmbientTentacleCount;
            float radius = config.KrakenTentacleSpawnRadius;

            for (int i = 0; i < count; i++)
            {
                double angle = (2 * Math.PI / count) * i;
                double spawnX = entity.Pos.X + Math.Cos(angle) * radius;
                double spawnZ = entity.Pos.Z + Math.Sin(angle) * radius;

                Entity ambient = entity.World.ClassRegistry.CreateEntity(ambientProps);
                ambient.Pos.SetPos(spawnX, entity.Pos.Y + 1, spawnZ);
                ambient.Pos.Dimension = entity.Pos.Dimension;
                ambient.Pos.SetFrom(ambient.Pos);

                if (!string.IsNullOrEmpty(targetUid))
                {
                    ambient.WatchedAttributes.SetString("underwaterhorrors:targetPlayerUid", targetUid);
                }
                ambient.WatchedAttributes.SetFloat("underwaterhorrors:orbitPhase", (float)angle);
                ambient.WatchedAttributes.SetLong("underwaterhorrors:krakenBodyId", entity.EntityId);

                entity.World.SpawnEntity(ambient);
            }
            if (config.DebugLogging)
                UnderwaterHorrorsModSystem.DebugLog(entity.Api, $"Kraken spawned {count} ambient tentacles (radius: {radius})");
        }
    }

    private void SpawnAttackTentacle()
    {
        // Reset the sink signal so the next cycle of ambient tentacles works
        entity.WatchedAttributes.SetBool("underwaterhorrors:sinkAmbient", false);

        string targetUid = entity.WatchedAttributes.GetString("underwaterhorrors:targetPlayerUid");

        EntityProperties attackProps = entity.World.GetEntityType(AttackTentacleAsset);
        if (attackProps == null) return;

        Entity tentacle = entity.World.ClassRegistry.CreateEntity(attackProps);
        tentacle.Pos.SetPos(entity.Pos.X, entity.Pos.Y + 1, entity.Pos.Z);
        tentacle.Pos.Dimension = entity.Pos.Dimension;
        tentacle.Pos.SetFrom(tentacle.Pos);
        if (!string.IsNullOrEmpty(targetUid))
        {
            tentacle.WatchedAttributes.SetString("underwaterhorrors:targetPlayerUid", targetUid);
        }
        tentacle.WatchedAttributes.SetLong("underwaterhorrors:krakenBodyId", entity.EntityId);
        entity.World.SpawnEntity(tentacle);
        attackTentacleId = tentacle.EntityId;
        cachedAttackTentacle = tentacle;

        if (config.DebugLogging)
            UnderwaterHorrorsModSystem.DebugLog(entity.Api, "Kraken spawned attack tentacle");
    }

    private void DealContactDamage()
    {
        float range = config.KrakenContactRange;
        float rangeSq = range * range;
        float damage = config.KrakenContactDamage;

        foreach (IPlayer player in entity.World.AllOnlinePlayers)
        {
            if (player.Entity == null || !player.Entity.Alive) continue;
            if (player.Entity.MountedOn != null) continue;

            // Use squared distance to avoid sqrt when out of range
            double dx = entity.Pos.X - player.Entity.Pos.X;
            double dy = entity.Pos.Y - player.Entity.Pos.Y;
            double dz = entity.Pos.Z - player.Entity.Pos.Z;
            double distSq = dx * dx + dy * dy + dz * dz;

            if (distSq < rangeSq)
            {
                player.Entity.ReceiveDamage(new DamageSource
                {
                    Source = EnumDamageSource.Entity,
                    SourceEntity = entity,
                    Type = EnumDamageType.PiercingAttack,
                    DamageTier = config.KrakenDamageTier
                }, damage);
                if (config.DebugLogging)
                    UnderwaterHorrorsModSystem.DebugLog(entity.Api, $"Kraken body hit {player.PlayerName} for {damage} contact damage (dist: {Math.Sqrt(distSq):F1})");
            }
        }
    }

    public override string PropertyName() => "underwaterhorrors:krakenbody";
}
