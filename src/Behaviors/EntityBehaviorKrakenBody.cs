using System;
using System.Collections.Generic;
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

    // All ambient tentacles ever spawned (risers + ground). When the
    // attack tentacle dies and the promote timer fires, one of these
    // (whichever's still alive) is selected, killed, and replaced by
    // a fresh attack tentacle at its position. Stale IDs are pruned
    // lazily inside PickAlivePromotionCandidate.
    private readonly List<long> ambientTentacleIds = new();

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
        // Vanilla gate: skip when no client within SimulationRange.
        // See EntityBehaviorTentacle.OnGameTick for rationale.
        if (entity.State != EnumEntityState.Active) return;
        if (!entity.Alive) return;
        if (entity.Api.Side != EnumAppSide.Server) return;
        if (entity.WatchedAttributes.GetBool("underwaterhorrors:static", false)) return;

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
                PromoteAmbientToAttack();
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
            // Promotion delay (30-120s by default) — long enough that the
            // player gets some breathing room after killing the attacker
            // but short enough that a wandering tentacle eventually steps
            // up. Falls back to spawning at body if no ambients are alive.
            var rand = entity.World.Rand;
            float delay = config.AmbientPromoteToAttackDelayMin
                + (float)(rand.NextDouble()
                          * (config.AmbientPromoteToAttackDelayMax - config.AmbientPromoteToAttackDelayMin));
            if (config.DebugLogging)
                UnderwaterHorrorsModSystem.DebugLog(entity.Api,
                    $"Kraken attack tentacle gone, promoting an ambient in {delay:F1}s");
            waitingToRespawnTentacle = true;
            tentacleRespawnTimer = delay;
            attackTentacleId = 0;
            cachedAttackTentacle = null;
        }
    }

    private void SpawnTentacles()
    {
        if (config.DebugLogging)
            UnderwaterHorrorsModSystem.DebugLog(entity.Api,
                $"Kraken spawning tentacles at ({entity.Pos.X:F1}, {entity.Pos.Y:F1}, {entity.Pos.Z:F1}) — " +
                $"1 attack + {config.KrakenAmbientTentacleCount} risers + {config.KrakenGroundTentacleCount} ground");

        SpawnAttackTentacle();

        string targetUid = entity.WatchedAttributes.GetString("underwaterhorrors:targetPlayerUid");
        EntityProperties ambientProps = entity.World.GetEntityType(AmbientTentacleAsset);
        if (ambientProps == null) return;

        // Risers: spaced evenly around the body, rise to the surface.
        int riserCount = config.KrakenAmbientTentacleCount;
        float radius = config.KrakenTentacleSpawnRadius;
        for (int i = 0; i < riserCount; i++)
        {
            double angle = (2 * Math.PI / riserCount) * i;
            double spawnX = entity.Pos.X + Math.Cos(angle) * radius;
            double spawnZ = entity.Pos.Z + Math.Sin(angle) * radius;
            SpawnAmbientTentacle(ambientProps, spawnX, entity.Pos.Y + 1, spawnZ, (float)angle, targetUid, groundMode: false);
        }

        // Ground tentacles: also spaced around the body but flagged as
        // ground-mode so they skip Rising and immediately start crawling
        // along the sea floor toward random distant points. Phase-offset
        // so their initial angles are between the risers' (so the kraken
        // looks evenly "spidered" on the floor).
        int groundCount = config.KrakenGroundTentacleCount;
        for (int i = 0; i < groundCount; i++)
        {
            double angle = (2 * Math.PI / groundCount) * i + (Math.PI / groundCount);
            double spawnX = entity.Pos.X + Math.Cos(angle) * radius;
            double spawnZ = entity.Pos.Z + Math.Sin(angle) * radius;
            SpawnAmbientTentacle(ambientProps, spawnX, entity.Pos.Y + 1, spawnZ, (float)angle, targetUid, groundMode: true);
        }
    }

    private void SpawnAmbientTentacle(EntityProperties props, double x, double y, double z,
        float orbitPhase, string targetUid, bool groundMode)
    {
        Entity ambient = entity.World.ClassRegistry.CreateEntity(props);
        ambient.Pos.SetPos(x, y, z);
        ambient.Pos.Dimension = entity.Pos.Dimension;
        ambient.Pos.SetFrom(ambient.Pos);

        if (!string.IsNullOrEmpty(targetUid))
        {
            ambient.WatchedAttributes.SetString("underwaterhorrors:targetPlayerUid", targetUid);
        }
        ambient.WatchedAttributes.SetFloat("underwaterhorrors:orbitPhase", orbitPhase);
        ambient.WatchedAttributes.SetLong("underwaterhorrors:krakenBodyId", entity.EntityId);
        if (groundMode)
        {
            ambient.WatchedAttributes.SetBool("underwaterhorrors:groundMode", true);
        }
        // Propagate the day/night bioluminescent flag from the body so
        // the renderer can decide per-entity whether to draw the glow.
        if (entity.WatchedAttributes.GetBool("underwaterhorrors:bioluminescent", false))
        {
            ambient.WatchedAttributes.SetBool("underwaterhorrors:bioluminescent", true);
        }

        entity.World.SpawnEntity(ambient);
        ambientTentacleIds.Add(ambient.EntityId);
    }

    private void SpawnAttackTentacle()
    {
        // Reset the scatter signal so newly-spawned ambients (if any
        // are spawned later via expansion) start in their normal state.
        entity.WatchedAttributes.SetBool("underwaterhorrors:scatterAmbient", false);

        SpawnAttackTentacleAt(entity.Pos.X, entity.Pos.Y + 1, entity.Pos.Z);
    }

    private void SpawnAttackTentacleAt(double x, double y, double z)
    {
        string targetUid = entity.WatchedAttributes.GetString("underwaterhorrors:targetPlayerUid");

        EntityProperties attackProps = entity.World.GetEntityType(AttackTentacleAsset);
        if (attackProps == null) return;

        Entity tentacle = entity.World.ClassRegistry.CreateEntity(attackProps);
        tentacle.Pos.SetPos(x, y, z);
        tentacle.Pos.Dimension = entity.Pos.Dimension;
        tentacle.Pos.SetFrom(tentacle.Pos);
        if (!string.IsNullOrEmpty(targetUid))
        {
            tentacle.WatchedAttributes.SetString("underwaterhorrors:targetPlayerUid", targetUid);
        }
        tentacle.WatchedAttributes.SetLong("underwaterhorrors:krakenBodyId", entity.EntityId);
        // Propagate bioluminescent flag (day vs night kraken).
        if (entity.WatchedAttributes.GetBool("underwaterhorrors:bioluminescent", false))
        {
            tentacle.WatchedAttributes.SetBool("underwaterhorrors:bioluminescent", true);
        }
        entity.World.SpawnEntity(tentacle);
        attackTentacleId = tentacle.EntityId;
        cachedAttackTentacle = tentacle;

        if (config.DebugLogging)
            UnderwaterHorrorsModSystem.DebugLog(entity.Api,
                $"Kraken spawned attack tentacle at ({x:F1}, {y:F1}, {z:F1})");
    }

    /// <summary>
    /// Picks a random surviving ambient tentacle, kills it, and spawns
    /// a new attack tentacle at its position. If no ambients are alive,
    /// falls back to spawning the attack at the body. Also resets the
    /// scatter signal so the OTHER survivors stay in wandering mode
    /// rather than instantly converting (only the new attack tentacle's
    /// own Lingering→Reaching transition re-asserts the scatter signal).
    /// </summary>
    private void PromoteAmbientToAttack()
    {
        Entity chosen = PickAlivePromotionCandidate();
        if (chosen != null)
        {
            double x = chosen.Pos.X;
            double y = chosen.Pos.Y;
            double z = chosen.Pos.Z;
            ambientTentacleIds.Remove(chosen.EntityId);
            chosen.Die(EnumDespawnReason.Expire);
            if (config.DebugLogging)
                UnderwaterHorrorsModSystem.DebugLog(entity.Api,
                    $"Kraken promoted ambient {chosen.EntityId} → new attack tentacle at ({x:F1}, {y:F1}, {z:F1})");
            // Reset scatter so the chosen ambient's siblings keep wandering;
            // they'll only re-scatter when the new attack tentacle finishes
            // its own rise/linger and starts Reaching.
            entity.WatchedAttributes.SetBool("underwaterhorrors:scatterAmbient", false);
            SpawnAttackTentacleAt(x, y, z);
        }
        else
        {
            if (config.DebugLogging)
                UnderwaterHorrorsModSystem.DebugLog(entity.Api,
                    "Kraken: no ambients alive to promote, falling back to body-spawn");
            SpawnAttackTentacle();
        }
    }

    private Entity PickAlivePromotionCandidate()
    {
        // Prune dead/missing IDs in place; collect the live ones.
        List<Entity> alive = null;
        for (int i = ambientTentacleIds.Count - 1; i >= 0; i--)
        {
            long id = ambientTentacleIds[i];
            Entity e = entity.World.GetEntityById(id);
            if (e == null || !e.Alive)
            {
                ambientTentacleIds.RemoveAt(i);
                continue;
            }
            alive ??= new List<Entity>();
            alive.Add(e);
        }
        if (alive == null || alive.Count == 0) return null;
        return alive[entity.World.Rand.Next(alive.Count)];
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
