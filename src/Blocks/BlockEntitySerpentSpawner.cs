using System;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;

namespace UnderwaterHorrors;

/// <summary>
/// Block entity for the creative serpent spawner. Ticks once a second on
/// the server. When a player is in the water within
/// <see cref="UnderwaterHorrorsConfig.SpawnerTriggerRange"/> blocks it
/// spawns one sea serpent, tags that serpent with this block's position and
/// code, and removes its own block so nothing floats in the dungeon while
/// the serpent hunts.
///
/// The block reappears via <see cref="EntityBehaviorSpawnerReturn"/>: when
/// the spawned serpent despawns on its own (a graceful Expire, e.g. the
/// player left the water and it retreated) that behavior restores this
/// block at the recorded spot. A serpent that is killed instead does not
/// restore the block, so clearing the encounter is permanent until the
/// block is placed again.
/// </summary>
public class BlockEntitySerpentSpawner : BlockEntity
{
    private static readonly AssetLocation SerpentAsset =
        new AssetLocation("underwaterhorrors", "seaserpent");

    public const string FromSpawnerAttr = "underwaterhorrors:fromSpawner";
    public const string SpawnerXAttr = "underwaterhorrors:spawnerX";
    public const string SpawnerYAttr = "underwaterhorrors:spawnerY";
    public const string SpawnerZAttr = "underwaterhorrors:spawnerZ";
    public const string SpawnerDimAttr = "underwaterhorrors:spawnerDim";
    public const string SpawnerBlockCodeAttr = "underwaterhorrors:spawnerBlockCode";

    public override void Initialize(ICoreAPI api)
    {
        base.Initialize(api);
        if (api.Side == EnumAppSide.Server)
        {
            // One check a second is plenty for a proximity trap and keeps
            // the cost trivial even with many spawners placed in a dungeon.
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

        SpawnSerpent(sapi, target);
    }

    /// <summary>
    /// Nearest online player who is alive, in this block's dimension, within
    /// trigger range, and standing in water. When IgnoreCreativePlayers is
    /// on, creative and spectator players are skipped so a builder can work
    /// near a spawner without setting it off.
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

    private void SpawnSerpent(ICoreServerAPI sapi, IPlayer target)
    {
        EntityProperties props = sapi.World.GetEntityType(SerpentAsset);
        if (props == null)
        {
            UnderwaterHorrorsModSystem.DebugLog(sapi, $"Serpent spawner: entity type {SerpentAsset} not found");
            return;
        }

        Entity serpent = sapi.World.ClassRegistry.CreateEntity(props);
        serpent.Pos.SetPos(Pos.X + 0.5, Pos.Y + 0.5, Pos.Z + 0.5);
        serpent.Pos.Dimension = Pos.dimension;

        serpent.WatchedAttributes.SetString("underwaterhorrors:targetPlayerUid", target.PlayerUID);
        serpent.WatchedAttributes.SetBool(FromSpawnerAttr, true);
        serpent.WatchedAttributes.SetInt(SpawnerXAttr, Pos.X);
        serpent.WatchedAttributes.SetInt(SpawnerYAttr, Pos.Y);
        serpent.WatchedAttributes.SetInt(SpawnerZAttr, Pos.Z);
        serpent.WatchedAttributes.SetInt(SpawnerDimAttr, Pos.dimension);
        serpent.WatchedAttributes.SetString(SpawnerBlockCodeAttr, Block.Code.ToString());

        sapi.World.SpawnEntity(serpent);

        // DebugLog is a no-op unless DebugLogging is on, so no guard needed.
        UnderwaterHorrorsModSystem.DebugLog(sapi, $"Serpent spawner at {Pos} triggered by {target.PlayerName}, block hidden until it despawns");

        // Remove our own block last. This disposes this block entity, so we
        // must not touch instance state afterward. The solid layer becomes
        // air; any water in the fluid layer stays, so the cage's spot simply
        // becomes open water.
        sapi.World.BlockAccessor.SetBlock(0, Pos);
        sapi.World.BlockAccessor.MarkBlockDirty(Pos);
    }
}
