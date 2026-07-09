using System;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;

namespace UnderwaterHorrors;

/// <summary>
/// Block entity for the creative creature spawner blocks. Ticks once a
/// second on the server. When a player is in the water within
/// <see cref="UnderwaterHorrorsConfig.SpawnerTriggerRange"/> blocks it
/// spawns one creature, tags it with this block's position and code, and
/// removes its own block so nothing floats while the creature hunts.
///
/// Which creature it spawns is read from the block's JSON "spawnEntity"
/// attribute (default "seaserpent"; the kraken spawner sets "krakenbody").
///
/// The block reappears via <see cref="EntityBehaviorSpawnerReturn"/>: when
/// the spawned creature despawns on its own (a graceful Expire) that
/// behavior restores this block. A creature that is killed does not restore
/// it. Serpents despawn themselves when the player leaves the water; the
/// kraken has no such rule, so the spawner despawn timer in
/// UnderwaterHorrorsModSystem.OnDespawnCheck removes both when the target
/// player has been out of the water long enough.
/// </summary>
public class BlockEntityCreatureSpawner : BlockEntity
{
    public const string FromSpawnerAttr = "underwaterhorrors:fromSpawner";
    public const string SpawnerXAttr = "underwaterhorrors:spawnerX";
    public const string SpawnerYAttr = "underwaterhorrors:spawnerY";
    public const string SpawnerZAttr = "underwaterhorrors:spawnerZ";
    public const string SpawnerDimAttr = "underwaterhorrors:spawnerDim";
    public const string SpawnerBlockCodeAttr = "underwaterhorrors:spawnerBlockCode";

    private AssetLocation spawnAsset;
    private bool isKraken;

    public override void Initialize(ICoreAPI api)
    {
        base.Initialize(api);

        string code = "seaserpent";
        if (Block?.Attributes != null)
        {
            code = Block.Attributes["spawnEntity"].AsString("seaserpent");
        }
        spawnAsset = new AssetLocation("underwaterhorrors", code);
        isKraken = code == "krakenbody";

        if (api.Side == EnumAppSide.Server)
        {
            // One check a second is plenty for a proximity trap and keeps the
            // cost trivial even with many spawners placed in a dungeon.
            RegisterGameTickListener(OnServerTick, 1000);
        }
    }

    private void OnServerTick(float dt)
    {
        if (Api is not ICoreServerAPI sapi) return;

        UnderwaterHorrorsConfig config = UnderwaterHorrorsModSystem.Config;
        if (config == null) return;

        IPlayer target = FindEligiblePlayer(sapi, config);
        if (target == null) return;

        SpawnCreature(sapi, target);
    }

    /// <summary>
    /// Nearest online player who is alive, in this block's dimension, within
    /// trigger range, and standing in water. When IgnoreCreativePlayers is
    /// on, creative and spectator players are skipped so a builder can work
    /// near a spawner without setting it off. Flip that toggle live with
    /// /uh observer.
    /// </summary>
    private IPlayer FindEligiblePlayer(ICoreServerAPI sapi, UnderwaterHorrorsConfig config)
    {
        double cx = Pos.X + 0.5, cy = Pos.Y + 0.5, cz = Pos.Z + 0.5;
        double rangeSq = config.SpawnerTriggerRange * config.SpawnerTriggerRange;

        IPlayer best = null;
        double bestDistSq = double.MaxValue;

        foreach (IPlayer p in sapi.World.AllOnlinePlayers)
        {
            if (p?.Entity == null || !p.Entity.Alive) continue;
            if (p.Entity.Pos.Dimension != Pos.dimension) continue;

            if (config.IgnoreCreativePlayers)
            {
                EnumGameMode mode = p.WorldData.CurrentGameMode;
                if (mode == EnumGameMode.Creative || mode == EnumGameMode.Spectator) continue;
            }

            double dx = p.Entity.Pos.X - cx;
            double dy = p.Entity.Pos.Y - cy;
            double dz = p.Entity.Pos.Z - cz;
            double distSq = dx * dx + dy * dy + dz * dz;
            if (distSq > rangeSq || distSq >= bestDistSq) continue;

            Block feet = sapi.World.BlockAccessor.GetBlock(p.Entity.Pos.AsBlockPos);
            if (feet == null || !WaterHelper.IsWaterBlock(feet)) continue;

            best = p;
            bestDistSq = distSq;
        }

        return best;
    }

    private void SpawnCreature(ICoreServerAPI sapi, IPlayer target)
    {
        EntityProperties props = sapi.World.GetEntityType(spawnAsset);
        if (props == null)
        {
            UnderwaterHorrorsModSystem.DebugLog(sapi, $"Creature spawner: entity type {spawnAsset} not found");
            return;
        }

        double spawnX = Pos.X + 0.5, spawnY = Pos.Y + 0.5, spawnZ = Pos.Z + 0.5;

        // The kraken anchors its tentacles to wherever the body sits, so drop
        // it to the sea floor under the block rather than leaving it floating.
        if (isKraken)
        {
            int fy = FindFloorYBelow(sapi);
            if (fy >= 0) spawnY = fy;
        }

        Entity creature = sapi.World.ClassRegistry.CreateEntity(props);
        creature.Pos.SetPos(spawnX, spawnY, spawnZ);
        creature.Pos.Dimension = Pos.dimension;

        creature.WatchedAttributes.SetString("underwaterhorrors:targetPlayerUid", target.PlayerUID);
        creature.WatchedAttributes.SetBool(FromSpawnerAttr, true);
        creature.WatchedAttributes.SetInt(SpawnerXAttr, Pos.X);
        creature.WatchedAttributes.SetInt(SpawnerYAttr, Pos.Y);
        creature.WatchedAttributes.SetInt(SpawnerZAttr, Pos.Z);
        creature.WatchedAttributes.SetInt(SpawnerDimAttr, Pos.dimension);
        creature.WatchedAttributes.SetString(SpawnerBlockCodeAttr, Block.Code.ToString());

        // Match the natural kraken: night spawns get the bioluminescent flag
        // the client renderer reads for the pulsing cyan glow.
        if (isKraken)
        {
            UnderwaterHorrorsConfig config = UnderwaterHorrorsModSystem.Config;
            double hour = sapi.World.Calendar.HourOfDay;
            bool isDay = config != null && hour >= config.DayKrakenStartHour && hour < config.DayKrakenEndHour;
            if (!isDay) creature.WatchedAttributes.SetBool("underwaterhorrors:bioluminescent", true);
        }

        sapi.World.SpawnEntity(creature);

        // DebugLog is a no-op unless DebugLogging is on, so no guard needed.
        UnderwaterHorrorsModSystem.DebugLog(sapi, $"Creature spawner at {Pos} spawned {spawnAsset.Path} for {target.PlayerName}, block hidden until it despawns");

        // Remove our own block last. This disposes this block entity, so we
        // must not touch instance state afterward. The solid layer becomes
        // air; any water in the fluid layer stays, so the cage's spot simply
        // becomes open water.
        sapi.World.BlockAccessor.SetBlock(0, Pos);
        sapi.World.BlockAccessor.MarkBlockDirty(Pos);
    }

    /// <summary>Y of the first empty space above the sea floor below this block, or -1.</summary>
    private int FindFloorYBelow(ICoreServerAPI sapi)
    {
        var accessor = sapi.World.BlockAccessor;
        var pos = new BlockPos(Pos.X, Pos.Y, Pos.Z, Pos.dimension);
        int limit = Math.Max(1, Pos.Y - 60);
        for (int y = Pos.Y - 1; y >= limit; y--)
        {
            pos.Y = y;
            Block b = accessor.GetBlock(pos);
            if (b != null && b.Id != 0 && !WaterHelper.IsWaterBlock(b)) return y + 1;
        }
        return -1;
    }
}
