using System;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.MathTools;

namespace UnderwaterHorrors;

/// <summary>
/// Manages a chain of segment entities that follow a tentacle's spline from
/// a body anchor up to the rising tip. Used by both attack tentacles
/// (EntityBehaviorTentacle) and ambient tentacles (EntityBehaviorAmbientTentacle)
/// so the chain logic lives in one place.
///
/// Trail-follow positioning: the tip-most segment sits one segment-height
/// behind the tip along the spline, the next two heights behind, etc.
/// segment[0] is pinned to the body anchor. As the tip rises, segments
/// emerge from the body in sequence and track the tip at fixed offsets.
///
/// Orientation: each segment's local +Y trunk axis is rotated to align
/// with the local spline tangent. Decomposition uses pitch + roll with
/// yaw=0 because VS's renderer applies pitch around the world X axis
/// AFTER yaw (not in the entity's facing frame), so an FPS-style
/// yaw+pitch makes every segment tilt in the same world direction
/// regardless of where the chain is curving. With yaw=0:
///   trunk_x = -sin(roll)
///   trunk_y =  cos(roll) * cos(pitch)
///   trunk_z =  cos(roll) * sin(pitch)
/// → roll  = atan2(-tx, sqrt(ty² + tz²))
/// → pitch = atan2(tz, ty)
/// The trunk is rotationally symmetric so yaw=0 is invisible.
/// </summary>
public class TentacleSegmentChain
{
    private readonly Entity tipEntity;
    private readonly int segmentCount;
    private readonly double segmentVisualHeight;
    private readonly AssetLocation baseSegmentAsset;
    private readonly AssetLocation midSegmentAsset;

    private long[] segmentIds;
    private Entity[] segmentEntities;
    private bool spawned;

    // Spline arc-length sample arrays (allocated once, reused each tick).
    private const int ArcSamples = 24;
    private double[] cumulativeArc;
    private Vec3d[] sampledPos;

    private readonly Vec3d reusableAnchor = new Vec3d();

    public int Count => segmentCount;
    public Entity[] Segments => segmentEntities;
    public long[] SegmentIds => segmentIds;
    public bool Spawned => spawned;

    public TentacleSegmentChain(Entity tipEntity, int segmentCount, double segmentVisualHeight,
        AssetLocation baseSegmentAsset, AssetLocation midSegmentAsset)
    {
        this.tipEntity = tipEntity;
        this.segmentCount = segmentCount;
        this.segmentVisualHeight = segmentVisualHeight;
        this.baseSegmentAsset = baseSegmentAsset;
        this.midSegmentAsset = midSegmentAsset;
    }

    /// <summary>
    /// Spawns all segment entities at the tip's current position. Idempotent.
    /// </summary>
    public void EnsureSpawned()
    {
        if (spawned) return;
        spawned = true;

        segmentIds = new long[segmentCount];
        segmentEntities = new Entity[segmentCount];

        EntityProperties baseProps = tipEntity.World.GetEntityType(baseSegmentAsset);
        EntityProperties midProps  = tipEntity.World.GetEntityType(midSegmentAsset);

        if (baseProps == null) return;

        for (int i = 0; i < segmentCount; i++)
        {
            // i=0 is the base segment that sits on top of the body block.
            // i>=1 use the mid (continuous trunk) shape and stack along the spline.
            EntityProperties props = (i == 0) ? baseProps : (midProps ?? baseProps);

            Entity seg = tipEntity.World.ClassRegistry.CreateEntity(props);
            seg.Pos.SetPos(tipEntity.Pos.X, tipEntity.Pos.Y, tipEntity.Pos.Z);
            seg.Pos.Dimension = tipEntity.Pos.Dimension;
            seg.Pos.SetFrom(seg.Pos);
            tipEntity.World.SpawnEntity(seg);
            segmentIds[i] = seg.EntityId;
            segmentEntities[i] = seg;
        }
    }

    /// <summary>
    /// Kills all segment entities. Safe to call multiple times.
    /// </summary>
    public void Despawn()
    {
        if (segmentEntities == null) return;

        for (int i = 0; i < segmentEntities.Length; i++)
        {
            Entity seg = segmentEntities[i];
            if (seg != null && seg.Alive)
            {
                seg.Die(EnumDespawnReason.Expire);
            }
        }
    }

    /// <summary>
    /// Updates segment positions and orientations along the spline from the
    /// given body anchor to the current tip position.
    /// </summary>
    public void UpdatePositions(double anchorX, double anchorY, double anchorZ, float archHeightFactor)
    {
        if (!spawned || segmentEntities == null) return;

        reusableAnchor.Set(anchorX, anchorY, anchorZ);
        Vec3d tip = tipEntity.Pos.XYZ;
        SplineHelper.ComputeTentacleControlPoints(reusableAnchor, tip, archHeightFactor, out Vec3d b1, out Vec3d b2);

        EnsureArcArrays();
        double splineLength = SampleSpline(reusableAnchor, b1, b2, tip);

        int N = segmentEntities.Length;
        for (int i = 0; i < N; i++)
        {
            Entity seg = segmentEntities[i];
            if (seg == null || !seg.Alive)
            {
                seg = tipEntity.World.GetEntityById(segmentIds[i]);
                segmentEntities[i] = seg;
                if (seg == null || !seg.Alive) continue;
            }

            if (i == 0)
            {
                // Base segment: always sits on top of the body block, upright.
                seg.TeleportToDouble(reusableAnchor.X, reusableAnchor.Y, reusableAnchor.Z);
                SetOrientation(seg, 0f, 0f, 0f);
                continue;
            }

            // Arc-length distance back from the tip. Higher index = closer to tip;
            // segment[N-1] leads at one segment-height behind the tip, segment[1]
            // trails at (N-1) segment-heights behind.
            double distFromTip = (N - i) * segmentVisualHeight;

            if (distFromTip > splineLength)
            {
                // Spline isn't long enough yet — segment hasn't emerged from the body.
                seg.TeleportToDouble(reusableAnchor.X, reusableAnchor.Y, reusableAnchor.Z);
                SetOrientation(seg, 0f, 0f, 0f);
            }
            else
            {
                double arcFromBase = splineLength - distFromTip;
                GetPositionAtArcLength(arcFromBase, out double sx, out double sy, out double sz);
                seg.TeleportToDouble(sx, sy, sz);

                // Sample one segment-height ahead along the spline to compute the
                // local tangent. Aligning the trunk (+Y) with the tangent makes
                // adjacent segments form a continuous chain along the curve.
                double aheadDist = arcFromBase + segmentVisualHeight;
                if (aheadDist > splineLength) aheadDist = splineLength;
                GetPositionAtArcLength(aheadDist, out double ax, out double ay, out double az);

                double tx = ax - sx;
                double ty = ay - sy;
                double tz = az - sz;
                double tlen = Math.Sqrt(tx * tx + ty * ty + tz * tz);
                if (tlen > 1e-6)
                {
                    tx /= tlen; ty /= tlen; tz /= tlen;

                    double yzLen = Math.Sqrt(ty * ty + tz * tz);
                    float roll  = (float)Math.Atan2(-tx, yzLen);
                    float pitch = (float)Math.Atan2(tz, ty);

                    SetOrientation(seg, 0f, pitch, roll);
                }
            }
        }
    }

    private static void SetOrientation(Entity seg, float yaw, float pitch, float roll)
    {
        seg.Pos.Yaw = yaw;
        seg.Pos.Pitch = pitch;
        seg.Pos.Roll = roll;
    }

    private void EnsureArcArrays()
    {
        if (cumulativeArc == null) cumulativeArc = new double[ArcSamples + 1];
        if (sampledPos == null)
        {
            sampledPos = new Vec3d[ArcSamples + 1];
            for (int i = 0; i <= ArcSamples; i++) sampledPos[i] = new Vec3d();
        }
    }

    private double SampleSpline(Vec3d a, Vec3d b1, Vec3d b2, Vec3d tip)
    {
        sampledPos[0].Set(a.X, a.Y, a.Z);
        cumulativeArc[0] = 0;
        for (int i = 1; i <= ArcSamples; i++)
        {
            double s = (double)i / ArcSamples;
            Vec3d p = SplineHelper.EvalCubicBezier(a, b1, b2, tip, s);
            sampledPos[i].Set(p.X, p.Y, p.Z);
            cumulativeArc[i] = cumulativeArc[i - 1] + sampledPos[i].DistanceTo(sampledPos[i - 1]);
        }
        return cumulativeArc[ArcSamples];
    }

    private void GetPositionAtArcLength(double dist, out double x, out double y, out double z)
    {
        if (dist <= 0)
        {
            x = sampledPos[0].X;
            y = sampledPos[0].Y;
            z = sampledPos[0].Z;
            return;
        }
        double maxArc = cumulativeArc[ArcSamples];
        if (dist >= maxArc)
        {
            x = sampledPos[ArcSamples].X;
            y = sampledPos[ArcSamples].Y;
            z = sampledPos[ArcSamples].Z;
            return;
        }

        // Linear search through the sample buckets (24 of them — too few for
        // binary search to be worth the code).
        for (int i = 1; i <= ArcSamples; i++)
        {
            if (cumulativeArc[i] >= dist)
            {
                double bucket = cumulativeArc[i] - cumulativeArc[i - 1];
                double frac = (bucket > 1e-9) ? (dist - cumulativeArc[i - 1]) / bucket : 0;
                x = sampledPos[i - 1].X + (sampledPos[i].X - sampledPos[i - 1].X) * frac;
                y = sampledPos[i - 1].Y + (sampledPos[i].Y - sampledPos[i - 1].Y) * frac;
                z = sampledPos[i - 1].Z + (sampledPos[i].Z - sampledPos[i - 1].Z) * frac;
                return;
            }
        }

        // Unreachable since dist is bounded above
        x = sampledPos[ArcSamples].X;
        y = sampledPos[ArcSamples].Y;
        z = sampledPos[ArcSamples].Z;
    }
}
