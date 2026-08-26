using System;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;

namespace UnderwaterHorrors;

/// <summary>
/// Spawn placement for the serpents.
///
/// A serpent is one entity with a 2x1.5 hitbox, but its body renders and
/// takes hits up to 12 blocks either side of the origin (17 behind, for
/// seaserpent3) via the serpenthitproxies offsets. Picking a spawn point
/// that merely happens to be a water block therefore leaves most of the
/// animal buried in the sea floor, and because the serpents use
/// controlledphysics with a step height of 0 a center that lands inside
/// terrain has nothing able to push it back out. Spawner blocks are the
/// worst case: they are placed in sea-floor ruins, so every serpent they
/// trigger starts embedded in the sand.
///
/// So before spawning, raise the point until every column the body can
/// occupy has clear water beneath it. The test covers a disc of the body's
/// reach rather than a line along one yaw, because a serpent turns freely
/// and starts turning on its first tick, so whichever way it happens to be
/// pointing at spawn tells us nothing about where its tail will be a second
/// later.
/// </summary>
internal static class SerpentPlacement
{
    private const string HitProxiesBehavior = "underwaterhorrors:serpenthitproxies";

    // Rays sampled outward from the spawn point, and roughly how far apart
    // the samples along each ray are. Eight rays at ~2 blocks is 49 columns
    // for a 12-block body: cheap enough for a one-off spawn check, fine
    // enough that only something narrower than a 2-block spire can slip
    // between the rays.
    private const int Rays = 8;
    private const double SampleSpacing = 2.0;

    /// <summary>
    /// Finds the range of Y positions at (x, z) where a serpent's center
    /// would have <paramref name="clearance"/> blocks of water on every
    /// side. Raycasts down FROM THE SKY: every column in the footprint must
    /// be open to the surface (first non-air block from above is water) and
    /// its water must run deep enough. Air pockets, ruin roofs, sand banks
    /// and shore overhangs all shrink or empty the range. Returns false
    /// when no Y qualifies. <paramref name="yMax"/> is the highest valid
    /// center: spawning there puts the serpent as far above anything that
    /// could trap it as the water allows.
    /// </summary>
    internal static bool TryFindOpenWaterColumn(IWorldAccessor world,
        double x, double z, int dimension, int clearance, out int yMin, out int yMax)
    {
        yMin = 0;
        yMax = 0;
        var accessor = world.BlockAccessor;
        // "The sky" in practice: comfortably above any wave or structure.
        // Scanning from the true world ceiling would be hundreds of air
        // reads per column for nothing.
        int skyY = Math.Min(accessor.MapSizeY - 2, world.SeaLevel + 32);
        var probe = new BlockPos(0, 0, 0, dimension);

        int cx = Floor(x), cz = Floor(z);
        int lowestSurface = int.MaxValue;
        int highestFloor = int.MinValue;

        for (int dx = -clearance; dx <= clearance; dx++)
        {
            for (int dz = -clearance; dz <= clearance; dz++)
            {
                // Raycast down from the sky: skip air, and require the first
                // real block to be water. Anything else means this column is
                // land, a structure, or roofed over.
                int colX = cx + dx, colZ = cz + dz;
                int surfaceY = -1;
                for (int y = skyY; y > 0; y--)
                {
                    probe.Set(colX, y, colZ);
                    Block block = accessor.GetBlock(probe);
                    if (block == null || block.Id == 0) continue;
                    if (WaterHelper.IsWaterBlock(block)) surfaceY = y;
                    break;
                }
                if (surfaceY < 0) return false;

                // Walk down through the water to the column's floor (or an
                // air pocket, which ends the usable water just the same).
                int bottomY = surfaceY;
                for (int y = surfaceY - 1; y > 0; y--)
                {
                    probe.Set(colX, y, colZ);
                    Block block = accessor.GetBlock(probe);
                    if (block == null || !WaterHelper.IsWaterBlock(block)) break;
                    bottomY = y;
                }

                if (surfaceY < lowestSurface) lowestSurface = surfaceY;
                if (bottomY > highestFloor) highestFloor = bottomY;
            }
        }

        // The center needs `clearance` water cells above and below in every
        // column, so it must sit at least that far inside the shared span.
        yMin = highestFloor + clearance;
        yMax = lowestSurface - clearance;
        return yMin <= yMax;
    }

    /// <summary>
    /// Raises <paramref name="y"/> until the whole body clears the sea floor
    /// by the configured margin, and returns true. Returns false and leaves
    /// <paramref name="y"/> alone when there is no such height within the
    /// configured rise (a shallow sea, or a ruin roofed over) — the caller
    /// decides whether to spawn anyway or pick another spot.
    /// </summary>
    internal static bool TryClearSpawnY(IWorldAccessor world, EntityProperties props,
        double x, ref double y, double z, int dimension)
    {
        UnderwaterHorrorsConfig config = UnderwaterHorrorsModSystem.Config;
        int clearance = config?.SerpentGroundClearance ?? 2;
        if (clearance <= 0) return true;

        int maxRise = config?.SerpentSpawnMaxRise ?? 40;
        double reach = BodyReach(props);

        // A reach of 0 degrades this to a single-column check, which is the
        // buried-serpent bug all over again. That can only happen if the
        // hit-proxy behavior is renamed or dropped from the entity JSON, so
        // say so rather than quietly going back to the old behavior.
        if (reach <= 0)
        {
            world.Logger.Warning(
                "[underwaterhorrors] {0} has no {1} offsets, spawning it without a body clearance check",
                props?.Code, HitProxiesBehavior);
        }

        var accessor = world.BlockAccessor;
        BlockPos probe = new(0, 0, 0, dimension);

        for (int rise = 0; rise <= maxRise; rise++)
        {
            double candidate = y + rise;
            if (!IsClearAt(accessor, probe, x, candidate, z, reach, clearance)) continue;

            y = candidate;
            return true;
        }

        return false;
    }

    /// <summary>
    /// How far the visible body reaches from the entity origin, in blocks,
    /// read from the entity type's hit-proxy offsets so the two serpent
    /// shapes (body centered on the origin) and the serpent3 prototype
    /// (body trailing behind it) each get their own real extent.
    /// </summary>
    internal static double BodyReach(EntityProperties props)
    {
        JsonObject[] behaviors = props?.Server?.BehaviorsAsJsonObj;
        if (behaviors == null) return 0;

        foreach (JsonObject behavior in behaviors)
        {
            if (behavior == null) continue;
            if (behavior["code"].AsString() != HitProxiesBehavior) continue;

            float[] offsets = behavior["offsets"].AsArray<float>(Array.Empty<float>());
            double reach = 0;
            foreach (float offset in offsets) reach = Math.Max(reach, Math.Abs(offset));
            return reach;
        }

        return 0;
    }

    /// <summary>
    /// True when a serpent centered here would be in water with
    /// <paramref name="clearance"/> blocks of clear space under every part
    /// of its body.
    /// </summary>
    private static bool IsClearAt(IBlockAccessor accessor, BlockPos probe,
        double x, double y, double z, double reach, int clearance)
    {
        // The serpent's own cell must be water. Lifting it out of the sea
        // is not a fix, and an air pocket inside a ruin is not somewhere a
        // sea serpent belongs — both are why the rise stops at the
        // waterline without needing a separate surface scan.
        probe.Set(Floor(x), Floor(y), Floor(z));
        Block here = accessor.GetBlock(probe);
        if (here == null || !WaterHelper.IsWaterBlock(here)) return false;

        if (!IsColumnClear(accessor, probe, x, y, z, clearance)) return false;
        if (reach <= 0) return true;

        // Sample count from the reach so the outermost ring always lands on
        // the body's tip, whatever the shape's length.
        int rings = Math.Max(1, (int)Math.Ceiling(reach / SampleSpacing));
        for (int ring = 1; ring <= rings; ring++)
        {
            double radius = reach * ring / rings;
            for (int ray = 0; ray < Rays; ray++)
            {
                double angle = 2 * Math.PI * ray / Rays;
                double sx = x + Math.Cos(angle) * radius;
                double sz = z + Math.Sin(angle) * radius;
                if (!IsColumnClear(accessor, probe, sx, y, sz, clearance)) return false;
            }
        }

        return true;
    }

    /// <summary>
    /// True when the cell at (x, y, z) and the <paramref name="clearance"/>
    /// cells below it are all water or air. Checking down to y - clearance
    /// is what makes the gap to the first solid block at least
    /// <paramref name="clearance"/> blocks.
    /// </summary>
    private static bool IsColumnClear(IBlockAccessor accessor, BlockPos probe,
        double x, double y, double z, int clearance)
    {
        int top = Floor(y);
        probe.X = Floor(x);
        probe.Z = Floor(z);

        for (int cellY = top; cellY >= top - clearance; cellY--)
        {
            probe.Y = cellY;
            Block block = accessor.GetBlock(probe);
            if (block == null) continue;
            if (block.Id != 0 && !block.IsLiquid() && block.Replaceable < 6000) return false;
        }

        return true;
    }

    // A plain (int) cast truncates toward zero, which is off by one for
    // every negative coordinate — half the world.
    private static int Floor(double v) => (int)Math.Floor(v);
}
