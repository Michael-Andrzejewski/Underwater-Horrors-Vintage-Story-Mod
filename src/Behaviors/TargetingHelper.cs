using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.MathTools;

namespace UnderwaterHorrors;

public static class TargetingHelper
{
    /// <summary>
    /// Returns true if the player is in shallow water (fewer than threshold water blocks
    /// below them, or able to stand with head above water).
    /// </summary>
    public static bool IsPlayerInShallowWater(Entity entity, IPlayer player, int threshold)
    {
        if (player?.Entity == null) return true;

        var accessor = entity.World.BlockAccessor;
        BlockPos pos = player.Entity.SidedPos.AsBlockPos.Copy();

        int waterBelow = 0;
        int startY = pos.Y;

        for (int y = startY; y >= 0 && y >= startY - threshold; y--)
        {
            pos.Y = y;
            Block block = accessor.GetBlock(pos);
            string code = block?.Code?.Path ?? "";
            if (code.StartsWith("saltwater") || code.StartsWith("water"))
            {
                waterBelow++;
            }
            else
            {
                break;
            }
        }

        return waterBelow < threshold;
    }

    /// <summary>
    /// Resolves the target player for an entity. Tries the stored UID attribute first,
    /// then falls back to the nearest online player.
    /// </summary>
    public static IPlayer ResolveTarget(Entity entity)
    {
        string uid = entity.WatchedAttributes.GetString("underwaterhorrors:targetPlayerUid");

        // Try to find the specific target player by UID
        if (!string.IsNullOrEmpty(uid))
        {
            foreach (IPlayer player in entity.World.AllOnlinePlayers)
            {
                if (player.PlayerUID == uid)
                {
                    UnderwaterHorrorsModSystem.DebugLog(entity.Api, $"{entity.Code} resolved target by UID: {player.PlayerName}");
                    return player;
                }
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
            UnderwaterHorrorsModSystem.DebugLog(entity.Api, $"{entity.Code} resolved target by proximity: {closest.PlayerName} (dist: {closestDist:F1})");
        }
        else
        {
            UnderwaterHorrorsModSystem.DebugLog(entity.Api, $"{entity.Code} could not resolve any target player");
        }

        return closest;
    }
}
