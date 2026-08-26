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
/// kraken has no such rule, so the despawn sweep in
/// UnderwaterHorrorsModSystem.SweepCreatureDespawns removes both when the
/// target player has been out of the water long enough.
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

        // /uh kraken spawns off keeps kraken spawner blocks dormant. Worldgen
        // ruins place these, so servers that opt out of the kraken (it is a
        // roughly 780-entity encounter) must not trip one in a ruin. The block
        // stays in place and re-arms if the toggle comes back on.
        if (isKraken && !config.KrakenNaturalSpawnEnabled) return;

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

            // In the water, or on a boat floating on it. Requiring feet in
            // water let players row over a ruin and clean it out without
            // the guardian ever spawning; a serpent handles boat targets
            // fine (it circles and menaces them), so a mounted player over
            // water arms the spawner too.
            Block feet = sapi.World.BlockAccessor.GetBlock(p.Entity.Pos.AsBlockPos);
            bool inWater = feet != null && WaterHelper.IsWaterBlock(feet);
            if (!inWater && !IsMountedOverWater(sapi, p)) continue;

            best = p;
            bestDistSq = distSq;
        }

        return best;
    }

    /// <summary>
    /// True for a player riding something (a boat) with water directly
    /// below within a few blocks. Boats sit a block or two above the
    /// surface, so their rider's own block is air.
    /// </summary>
    private static bool IsMountedOverWater(ICoreServerAPI sapi, IPlayer p)
    {
        if ((p.Entity as EntityAgent)?.MountedOn == null) return false;

        BlockPos feet = p.Entity.Pos.AsBlockPos;
        var probe = new BlockPos(feet.X, 0, feet.Z, feet.dimension);
        int limit = Math.Max(0, feet.Y - 5);
        for (int y = feet.Y; y >= limit; y--)
        {
            probe.Y = y;
            Block b = sapi.World.BlockAccessor.GetBlock(probe);
            if (b == null) continue;
            if (WaterHelper.IsWaterBlock(b)) return true;
            if (b.Id != 0) return false;   // solid ground before any water
        }
        return false;
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
        // Serpents are the opposite: they are long and spawn embedded in the
        // ruin when the spawner sits at floor level, so rise through the open
        // water column above the block and spawn up there instead.
        if (isKraken)
        {
            int fy = FindFloorYBelow(sapi);
            if (fy >= 0) spawnY = fy;
        }
        else
        {
            // Preferred spawn: raycast down from the sky for an open water
            // column where the serpent's center has SerpentSpawnWaterClearance
            // blocks of water on every side, as high above the floor as the
            // water allows, so nothing can trap it in sand or ruin walls.
            // The spawner's own column is tried first, then rings of nearby
            // columns; only when every one is blocked does it fall back to
            // the best the local water offers.
            var config2 = UnderwaterHorrorsModSystem.Config;
            int clearance = config2?.SerpentSpawnWaterClearance ?? 5;
            if (TryFindSerpentSpawnPos(sapi, clearance, ref spawnX, ref spawnY, ref spawnZ))
            {
                UnderwaterHorrorsModSystem.DebugLog(sapi,
                    $"Creature spawner at {Pos}: open-water spawn at ({spawnX:F1}, {spawnY:F1}, {spawnZ:F1})");
            }
            else
            {
                // Every nearby column is blocked (a very shallow or roofed
                // ruin). Best effort in the spawner's own column: a partly
                // buried serpent still hunts, and the AI's floor clamp
                // lifts it as it swims.
                spawnY = FindOpenWaterYAbove(sapi) + 0.5;
                SerpentPlacement.TryClearSpawnY(sapi.World, props, spawnX, ref spawnY, spawnZ, Pos.dimension);
                UnderwaterHorrorsModSystem.DebugLog(sapi,
                    $"Creature spawner at {Pos}: no open-water column nearby, spawning best-effort at {spawnY:F1}");
            }
        }

        // Never spawn into air. A spawner can end up dry (a legacy ruin
        // generated too shallow, or an exposed wreck on a shore bank); a
        // creature spawned there flops in the open, retreats, and expires,
        // which put the block back and made it spawn again — the "monster
        // appears for a second and vanishes" loop players reported. Stay
        // armed and silent until there is water to spawn into.
        var spawnCell = new BlockPos(
            (int)Math.Floor(spawnX), (int)Math.Floor(spawnY), (int)Math.Floor(spawnZ), Pos.dimension);
        // At the spawner's own cell the solid layer is this block itself,
        // so ask the fluid layer — that is what the cell becomes once the
        // block removes itself.
        bool ownCell = spawnCell.X == Pos.X && spawnCell.Y == Pos.Y && spawnCell.Z == Pos.Z;
        Block cell = ownCell
            ? sapi.World.BlockAccessor.GetBlock(spawnCell, BlockLayersAccess.Fluid)
            : sapi.World.BlockAccessor.GetBlock(spawnCell);
        if (cell == null || !WaterHelper.IsWaterBlock(cell))
        {
            UnderwaterHorrorsModSystem.DebugLog(sapi,
                $"Creature spawner at {Pos}: spawn point {spawnCell} is not water, staying dormant");
            return;
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
        UnderwaterHorrorsModSystem.ApplyConfiguredHealth(creature);

        // DebugLog is a no-op unless DebugLogging is on, so no guard needed.
        UnderwaterHorrorsModSystem.DebugLog(sapi, $"Creature spawner at {Pos} spawned {spawnAsset.Path} for {target.PlayerName}, block hidden until it despawns");

        // Remove our own block last. This disposes this block entity, so we
        // must not touch instance state afterward. The solid layer becomes
        // air; any water in the fluid layer stays, so the cage's spot simply
        // becomes open water.
        sapi.World.BlockAccessor.SetBlock(0, Pos);
        sapi.World.BlockAccessor.MarkBlockDirty(Pos);
    }

    /// <summary>
    /// Searches for an open-water serpent spawn near this spawner: its own
    /// column first, then 8 directions at 6, 12, 18 and 24 blocks out.
    /// The first column with enough all-around water wins, at the highest
    /// valid center. Returns false when every candidate is blocked.
    /// </summary>
    private bool TryFindSerpentSpawnPos(ICoreServerAPI sapi, int clearance,
        ref double spawnX, ref double spawnY, ref double spawnZ)
    {
        double baseX = Pos.X + 0.5, baseZ = Pos.Z + 0.5;
        for (int ring = 0; ring <= 4; ring++)
        {
            int candidates = ring == 0 ? 1 : 8;
            double radius = ring * 6.0;
            for (int i = 0; i < candidates; i++)
            {
                double angle = Math.PI * 2 * i / candidates;
                double cx = baseX + Math.Cos(angle) * radius;
                double cz = baseZ + Math.Sin(angle) * radius;
                if (!SerpentPlacement.TryFindOpenWaterColumn(
                    sapi.World, cx, cz, Pos.dimension, clearance, out _, out int yMax)) continue;

                spawnX = cx;
                spawnZ = cz;
                spawnY = yMax + 0.5;
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Y of the highest open-water block in the column directly above this
    /// block, scanning at most 10 up (a ceiling or the surface stops the
    /// scan). Falls back to the block's own Y when there is no water above.
    /// </summary>
    private int FindOpenWaterYAbove(ICoreServerAPI sapi)
    {
        var accessor = sapi.World.BlockAccessor;
        var pos = new BlockPos(Pos.X, Pos.Y, Pos.Z, Pos.dimension);
        int best = Pos.Y;
        for (int y = Pos.Y + 1; y <= Pos.Y + 10; y++)
        {
            pos.Y = y;
            Block b = accessor.GetBlock(pos);
            if (b == null || !WaterHelper.IsWaterBlock(b)) break;
            best = y;
        }
        return best;
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
