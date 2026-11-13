using System.Collections.Generic;
using Vintagestory.API.Common;

namespace UnderwaterHorrors;

public static class WaterHelper
{
    // No cache of block ids here. There used to be four static sets, filled
    // on first sight of each id. They were written from the worldgen thread,
    // the server thread and (in single player) the render thread with no
    // lock, and block ids differ between savegames, so a second world opened
    // in the same game session could read water as land. Two prefix tests on
    // the block's code are cheap enough to do every time.

    /// <summary>True for saltwater of any height or flow.</summary>
    public static bool IsSaltwater(Block block)
    {
        if (block == null || block.Id == 0) return false;
        string path = block.Code?.Path;
        return path != null && path.StartsWith("saltwater");
    }

    /// <summary>
    /// What fills a cell, for "is this water, air or solid" tests: the water
    /// when the cell holds any, otherwise the block. The plain read returns
    /// the seaweed or coral growing in a water cell, which then looks solid.
    /// </summary>
    public static Block Cell(IBlockAccessor accessor, Vintagestory.API.MathTools.BlockPos pos)
    {
        Block fluid = accessor.GetBlock(pos, BlockLayersAccess.Fluid);
        return IsWaterBlock(fluid) ? fluid : accessor.GetBlock(pos);
    }

    /// <summary>True for any kind of water, salt or fresh.</summary>
    public static bool IsWaterBlock(Block block)
    {
        if (block == null || block.Id == 0) return false;
        string path = block.Code?.Path;
        // "water-", not "water": a water lily is not water.
        return path != null && (path.StartsWith("saltwater") || path.StartsWith("water-"));
    }
}
