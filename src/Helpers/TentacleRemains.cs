using System;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.MathTools;

namespace UnderwaterHorrors;

/// <summary>
/// Sinking and remains for dead tentacles, shared by the attack tentacle
/// and the ambient ones so both die the same way.
///
/// When the kraken body dies its arms lose their animating force and sink;
/// where they touch down they leave a bone pile and the rusted machinery
/// the thing had swallowed. A tentacle killed on its own leaves the same
/// remains directly beneath it, since being destroyed takes its whole
/// segment chain with it and there is nothing left to animate downward.
/// </summary>
internal static class TentacleRemains
{
    private static readonly AssetLocation BonePileAsset = new("game", "bonyremains-ribcage");
    private static readonly AssetLocation ScrapAsset = new("game", "metal-scraps");

    // Vanilla's cave-generated rusty gear piles, in five sizes.
    private const int LooseGearsVariants = 5;

    /// <summary>
    /// Drives one tick of a dying tentacle's descent.
    ///
    /// Returns true once the head is resting on the sea floor, or once
    /// timeoutSeconds has elapsed, whichever comes first. The timeout is
    /// not just belt and braces: a tentacle can be boxed in by terrain, or
    /// be over a spot with no floor within scanning range, and a dying
    /// tentacle that never finishes would hold its ~96 segment entities
    /// alive forever.
    /// </summary>
    internal static bool TickSink(Entity head, float deltaTime, ref float elapsed,
        double sinkSpeed, float timeoutSeconds, ref double cachedFloorY, ref float scanTimer)
    {
        elapsed += deltaTime;

        head.Pos.Motion.X = 0;
        head.Pos.Motion.Y = -sinkSpeed;
        head.Pos.Motion.Z = 0;

        // Rescanning 80 blocks downward every tick, per tentacle, is a lot
        // of block reads for a value that barely moves. Four times a second
        // is plenty when the head is only falling a few blocks a second.
        scanTimer -= deltaTime;
        if (scanTimer <= 0f || cachedFloorY < 0)
        {
            scanTimer = 0.25f;
            cachedFloorY = FloorYBelow(head);
        }

        if (cachedFloorY >= 0 && head.Pos.Y <= cachedFloorY + 0.5)
        {
            return true;
        }

        return elapsed >= timeoutSeconds;
    }

    /// <summary>
    /// Places a bone pile where the tentacle came to rest, plus a patch of
    /// rusty gears or scrap beside it. Safe to call more than once per
    /// tentacle: the caller is expected to latch, but this also refuses to
    /// overwrite anything that is not replaceable, so a repeat is inert.
    /// </summary>
    internal static void Leave(Entity head, UnderwaterHorrorsConfig config)
    {
        if (config != null && !config.TentacleRemainsEnabled) return;
        if (head?.World == null) return;

        // Placing blocks is a server job. OnEntityDeath can run on either
        // side depending on who called Die, and a client-side placement
        // would desync until the chunk reloaded.
        if (head.Api.Side != EnumAppSide.Server) return;

        IWorldAccessor world = head.World;
        var accessor = world.BlockAccessor;
        var rand = world.Rand;

        int floorY = (int)FloorYBelow(head);
        if (floorY < 0) return;

        int dim = head.Pos.Dimension;
        BlockPos restPos = new((int)head.Pos.X, floorY, (int)head.Pos.Z, dim);

        PlaceIfFree(accessor, restPos, world.GetBlock(BonePileAsset));

        // The rusted parts land next to the bones rather than under them,
        // so both are visible. Picked from the eight surrounding cells and
        // never the centre, which the bone pile now occupies. If that cell
        // is taken the scrap is simply skipped; a tentacle dying against a
        // rock face should not carve a hole in it.
        int neighbour = rand.Next(8);
        if (neighbour >= 4) neighbour++;    // skip index 4, the centre
        BlockPos sidePos = restPos.AddCopy(neighbour / 3 - 1, 0, neighbour % 3 - 1);

        Block scrap = rand.NextDouble() < 0.5
            ? world.GetBlock(new AssetLocation("game", "loosegears-" + (1 + rand.Next(LooseGearsVariants))))
            : world.GetBlock(ScrapAsset);

        PlaceIfFree(accessor, sidePos, scrap);
    }

    private static void PlaceIfFree(IBlockAccessor accessor, BlockPos pos, Block block)
    {
        if (block == null) return;

        Block present = accessor.GetBlock(pos);
        if (present == null || !present.IsReplacableBy(block)) return;

        accessor.SetBlock(block.BlockId, pos);
        accessor.MarkBlockDirty(pos);
    }

    /// <summary>
    /// Y of the first free space above solid ground beneath the head, or -1
    /// when there is none within scanning range. Shares the serpent's
    /// scanner so a tentacle and a serpent settle by identical rules.
    /// </summary>
    private static double FloorYBelow(Entity head)
    {
        return EntityBehaviorSerpentHarvest.FindRestingYBelow(
            head.World.BlockAccessor,
            head.Pos.X,
            head.Pos.Y + 1,
            head.Pos.Z,
            head.Pos.Dimension);
    }
}
