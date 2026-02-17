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
    private float tentacleRespawnTimer;
    private bool waitingToRespawnTentacle;
    private UnderwaterHorrorsConfig config;

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
        entity.SidedPos.Motion.X = 0;
        entity.SidedPos.Motion.Y = 0;
        entity.SidedPos.Motion.Z = 0;

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

        Entity tentacle = entity.World.GetEntityById(attackTentacleId);
        if (tentacle == null || !tentacle.Alive)
        {
            UnderwaterHorrorsModSystem.DebugLog(entity.Api, $"Kraken attack tentacle died, respawning in {config.TentacleRespawnDelay:F1}s");
            waitingToRespawnTentacle = true;
            tentacleRespawnTimer = config.TentacleRespawnDelay;
            attackTentacleId = 0;
        }
    }

    private void SpawnTentacles()
    {
        UnderwaterHorrorsModSystem.DebugLog(entity.Api, $"Kraken spawning tentacles at ({entity.ServerPos.X:F1}, {entity.ServerPos.Y:F1}, {entity.ServerPos.Z:F1})");

        SpawnAttackTentacle();

        // Spawn ambient tentacles evenly spaced around body
        EntityProperties ambientProps = entity.World.GetEntityType(new AssetLocation("underwaterhorrors", "krakenambienttentacle"));
        if (ambientProps != null)
        {
            int count = config.KrakenAmbientTentacleCount;
            float radius = config.KrakenTentacleSpawnRadius;

            for (int i = 0; i < count; i++)
            {
                double angle = (2 * Math.PI / count) * i;
                double spawnX = entity.ServerPos.X + Math.Cos(angle) * radius;
                double spawnZ = entity.ServerPos.Z + Math.Sin(angle) * radius;

                Entity ambient = entity.World.ClassRegistry.CreateEntity(ambientProps);
                ambient.ServerPos.SetPos(spawnX, entity.ServerPos.Y + 1, spawnZ);
                ambient.ServerPos.Dimension = entity.ServerPos.Dimension;
                ambient.Pos.SetFrom(ambient.ServerPos);
                entity.World.SpawnEntity(ambient);
            }
            UnderwaterHorrorsModSystem.DebugLog(entity.Api, $"Kraken spawned {count} ambient tentacles (radius: {radius})");
        }
    }

    private void SpawnAttackTentacle()
    {
        string targetUid = entity.WatchedAttributes.GetString("underwaterhorrors:targetPlayerUid");

        EntityProperties attackProps = entity.World.GetEntityType(new AssetLocation("underwaterhorrors", "krakententacle"));
        if (attackProps == null) return;

        Entity tentacle = entity.World.ClassRegistry.CreateEntity(attackProps);
        tentacle.ServerPos.SetPos(entity.ServerPos.X, entity.ServerPos.Y + 1, entity.ServerPos.Z);
        tentacle.ServerPos.Dimension = entity.ServerPos.Dimension;
        tentacle.Pos.SetFrom(tentacle.ServerPos);
        if (!string.IsNullOrEmpty(targetUid))
        {
            tentacle.WatchedAttributes.SetString("underwaterhorrors:targetPlayerUid", targetUid);
        }
        tentacle.WatchedAttributes.SetLong("underwaterhorrors:krakenBodyId", entity.EntityId);
        entity.World.SpawnEntity(tentacle);
        attackTentacleId = tentacle.EntityId;

        UnderwaterHorrorsModSystem.DebugLog(entity.Api, "Kraken spawned attack tentacle");
    }

    private void DealContactDamage()
    {
        float range = config.KrakenContactRange;
        float damage = config.KrakenContactDamage;

        foreach (IPlayer player in entity.World.AllOnlinePlayers)
        {
            if (player.Entity == null || !player.Entity.Alive) continue;
            if (player.Entity.MountedOn != null) continue;

            double dist = entity.SidedPos.DistanceTo(player.Entity.SidedPos.XYZ);
            if (dist < range)
            {
                player.Entity.ReceiveDamage(new DamageSource
                {
                    Source = EnumDamageSource.Entity,
                    SourceEntity = entity,
                    Type = EnumDamageType.PiercingAttack,
                    DamageTier = config.KrakenDamageTier
                }, damage);
                UnderwaterHorrorsModSystem.DebugLog(entity.Api, $"Kraken body hit {player.PlayerName} for {damage} contact damage (dist: {dist:F1})");
            }
        }
    }

    public override string PropertyName() => "underwaterhorrors:krakenbody";
}
