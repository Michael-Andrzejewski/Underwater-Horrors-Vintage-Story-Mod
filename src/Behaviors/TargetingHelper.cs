using System.Collections.Generic;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.MathTools;

namespace UnderwaterHorrors;

public static class TargetingHelper
{
    // Cache for shallow water checks: avoids scanning the full water column every tick.
    // Key: entity ID (of the creature doing the check), Value: (result, expiryTime, lastPlayerY)
    private static readonly Dictionary<long, ShallowWaterCache> shallowWaterCache = new();

    // Block ID caches for water checks (shared across all callers)
    private static readonly HashSet<int> waterBlockIds = new();
    private static readonly HashSet<int> nonWaterBlockIds = new();

    // Reusable BlockPos for water scanning
    private static readonly BlockPos scanPos = new BlockPos(0, 0, 0, 0);

    private struct ShallowWaterCache
    {
        public bool isShallow;
        public double expiryTime;
        public int lastPlayerBlockY;
    }

    /// <summary>
    /// Returns true if the player is in shallow water (the total saltwater column at the
    /// player's position — both above and below — is fewer than threshold blocks).
    /// Results are cached for 1 second per creature, invalidated if the player moves vertically.
    /// </summary>
    public static bool IsPlayerInShallowWater(Entity entity, IPlayer player, int threshold)
    {
        if (player?.Entity == null) return true;

        long entityId = entity.EntityId;
        int playerBlockY = (int)player.Entity.SidedPos.Y;
        double now = entity.World.ElapsedMilliseconds;

        // Check cache: valid if not expired and player hasn't moved vertically
        if (shallowWaterCache.TryGetValue(entityId, out ShallowWaterCache cached))
        {
            if (now < cached.expiryTime && playerBlockY == cached.lastPlayerBlockY)
            {
                return cached.isShallow;
            }
        }

        // Perform the actual scan
        bool result = ScanWaterColumn(entity, player, threshold);

        // Cache for 1 second (1000ms)
        shallowWaterCache[entityId] = new ShallowWaterCache
        {
            isShallow = result,
            expiryTime = now + 1000,
            lastPlayerBlockY = playerBlockY
        };

        return result;
    }

    private static bool ScanWaterColumn(Entity entity, IPlayer player, int threshold)
    {
        var accessor = entity.World.BlockAccessor;
        int mapHeight = accessor.MapSizeY;
        int startX = (int)player.Entity.SidedPos.X;
        int startY = (int)player.Entity.SidedPos.Y;
        int startZ = (int)player.Entity.SidedPos.Z;

        scanPos.Set(startX, startY, startZ);
        scanPos.dimension = player.Entity.SidedPos.Dimension;

        int waterCount = 0;

        // Count water below (including player's block)
        for (int y = startY; y >= 0; y--)
        {
            scanPos.Y = y;
            Block block = accessor.GetBlock(scanPos);
            if (block == null || !IsWaterBlock(block)) break;
            waterCount++;
            // Early exit: if we already know it's deep enough, no need to keep counting
            if (waterCount >= threshold) return false;
        }

        // Count water above
        for (int y = startY + 1; y < mapHeight; y++)
        {
            scanPos.Y = y;
            Block block = accessor.GetBlock(scanPos);
            if (block == null || !IsWaterBlock(block)) break;
            waterCount++;
            if (waterCount >= threshold) return false;
        }

        return waterCount < threshold;
    }

    private static bool IsWaterBlock(Block block)
    {
        int id = block.Id;
        if (id == 0) return false;

        if (waterBlockIds.Contains(id)) return true;
        if (nonWaterBlockIds.Contains(id)) return false;

        string path = block.Code?.Path;
        if (path != null && (path.StartsWith("saltwater") || path.StartsWith("water")))
        {
            waterBlockIds.Add(id);
            return true;
        }
        else
        {
            nonWaterBlockIds.Add(id);
            return false;
        }
    }

    /// <summary>
    /// Clears the shallow water cache for a specific entity (call on entity despawn).
    /// </summary>
    public static void ClearCache(long entityId)
    {
        shallowWaterCache.Remove(entityId);
    }

    /// <summary>
    /// Resolves the target player for an entity. Tries the stored UID attribute first,
    /// then falls back to the nearest online player.
    /// </summary>
    public static IPlayer ResolveTarget(Entity entity)
    {
        string uid = entity.WatchedAttributes.GetString("underwaterhorrors:targetPlayerUid");

        // Try to find the specific target player by UID (direct lookup)
        if (!string.IsNullOrEmpty(uid))
        {
            IPlayer player = entity.World.PlayerByUid(uid);
            if (player != null)
            {
                if (UnderwaterHorrorsModSystem.Config?.DebugLogging == true)
                    UnderwaterHorrorsModSystem.DebugLog(entity.Api, $"{entity.Code} resolved target by UID: {player.PlayerName}");
                return player;
            }
        }

        // Fallback: find nearest online player
        double closestDist = double.MaxValue;
        IPlayer closest = null;

        foreach (IPlayer player in entity.World.AllOnlinePlayers)
        {
            if (player.Entity == null || !player.Entity.Alive) continue;

            double dist = entity.SidedPos.DistanceTo(player.Entity.SidedPos.XYZ);
            if (dist < closestDist)
            {
                closestDist = dist;
                closest = player;
            }
        }

        if (closest != null)
        {
            // Store the UID so child entities (tentacles) also inherit it
            entity.WatchedAttributes.SetString("underwaterhorrors:targetPlayerUid", closest.PlayerUID);
            if (UnderwaterHorrorsModSystem.Config?.DebugLogging == true)
                UnderwaterHorrorsModSystem.DebugLog(entity.Api, $"{entity.Code} resolved target by proximity: {closest.PlayerName} (dist: {closestDist:F1})");
        }
        else
        {
            if (UnderwaterHorrorsModSystem.Config?.DebugLogging == true)
                UnderwaterHorrorsModSystem.DebugLog(entity.Api, $"{entity.Code} could not resolve any target player");
        }

        return closest;
    }
}
