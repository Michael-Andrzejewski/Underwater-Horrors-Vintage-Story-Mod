using System.Collections.Generic;
using Vintagestory.API.Common;

namespace UnderwaterHorrors;

public static class WaterHelper
{
    private static readonly HashSet<int> saltwaterBlockIds = new();
    private static readonly HashSet<int> nonSaltwaterBlockIds = new();
    private static readonly HashSet<int> waterBlockIds = new();
    private static readonly HashSet<int> nonWaterBlockIds = new();

    /// <summary>
    /// Checks if a block is saltwater using cached block ID lookups,
    /// falling back to string comparison only on first encounter.
    /// </summary>
    public static bool IsSaltwater(Block block)
    {
        int id = block.Id;
        if (id == 0) return false;

        if (saltwaterBlockIds.Contains(id)) return true;
        if (nonSaltwaterBlockIds.Contains(id)) return false;

        string path = block.Code?.Path;
        if (path != null && path.StartsWith("saltwater"))
        {
            saltwaterBlockIds.Add(id);
            return true;
        }
        else
        {
            nonSaltwaterBlockIds.Add(id);
            return false;
        }
    }

    /// <summary>
    /// Checks if a block is any kind of water (salt or fresh) using cached ID lookups.
    /// </summary>
    public static bool IsWaterBlock(Block block)
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
}
