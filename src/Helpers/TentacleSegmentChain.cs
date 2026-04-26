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
    private readonly double tipOuterVisualHeight;
    private readonly AssetLocation baseSegmentAsset;
    private readonly AssetLocation midSegmentAsset;
    private readonly AssetLocation tipOuterAsset;

    private long[] segmentIds;
    private Entity[] segmentEntities;
    private bool spawned;

    // Spline arc-length sample arrays (allocated once, reused each tick).
    private const int ArcSamples = 24;
    private double[] cumulativeArc;
    private Vec3d[] sampledPos;

    private readonly Vec3d reusableAnchor = new Vec3d();

    // VS's EntityShapeRenderer adds (shape.rotateY + 90)*π/180 to Pos.Yaw
    // before composing the model matrix. Our segment shape has no top-level
    // rotateY, so a baked +π/2 yaw is always applied. With Pos.Yaw=0 the
    // effective yaw is +π/2, which rotates our pitch+roll math 90° around
    // world Y — when the spline curves toward +X the trunk visibly tilts
    // toward -Z instead. Setting Pos.Yaw=-π/2 cancels the baked offset so
    // the effective yaw is 0 and the pitch+roll decomposition produces the
    // intended trunk direction. (The trunk is rotationally symmetric so
    // this 90° around Y is invisible at default-tilt.)
    private const float YawCancelBakedOffset = -(float)(Math.PI / 2.0);

    public int Count => segmentCount;
    public Entity[] Segments => segmentEntities;
    public long[] SegmentIds => segmentIds;
    public bool Spawned => spawned;

    /// <param name="tipOuterAsset">
    /// Optional shape used for the last chain position (closest to the
    /// rising tip). Lets the visible "tip" of the chain be a different
    /// model — e.g. the wider segment_outer piece — while the rest of
    /// the chain stays as mid segments. Pass null to use mid for every
    /// non-base position.
    /// </param>
    /// <param name="tipOuterVisualHeight">
    /// Visual block-height of the tip-outer model along its trunk axis.
    /// Used to space chain[N-1] flush against whatever sits at the tip
    /// (e.g. the krakententacle/claw entity), and chain[N-2] flush
    /// against chain[N-1]. Pass &lt;= 0 to fall back to segmentVisualHeight.
    /// </param>
    public TentacleSegmentChain(Entity tipEntity, int segmentCount, double segmentVisualHeight,
        AssetLocation baseSegmentAsset, AssetLocation midSegmentAsset,
        AssetLocation tipOuterAsset = null, double tipOuterVisualHeight = -1)
    {
        this.tipEntity = tipEntity;
        this.segmentCount = segmentCount;
        this.segmentVisualHeight = segmentVisualHeight;
        this.baseSegmentAsset = baseSegmentAsset;
        this.midSegmentAsset = midSegmentAsset;
        this.tipOuterAsset = tipOuterAsset;
        this.tipOuterVisualHeight = tipOuterVisualHeight > 0 ? tipOuterVisualHeight : segmentVisualHeight;
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
        EntityProperties tipProps  = tipOuterAsset != null
            ? tipEntity.World.GetEntityType(tipOuterAsset)
            : null;

        if (baseProps == null) return;

        for (int i = 0; i < segmentCount; i++)
        {
            // i=0           : base segment, sits on top of the body block
            // 1 .. N-2      : mid (continuous trunk) — bulk of the chain
            // i = N-1       : optional tip-outer (e.g. segment_outer) closest
            //                 to the rising tip; falls back to mid if not set
            EntityProperties props;
            if (i == 0)
            {
                props = baseProps;
            }
            else if (i == segmentCount - 1 && tipProps != null)
            {
                props = tipProps;
            }
            else
            {
                props = midProps ?? baseProps;
            }

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
    ///
    /// Forces AllowDespawn=true on every segment, alive or dead. The
    /// EntityBehaviorDeadDecay sets AllowDespawn=false during init so
    /// that player-killed corpses linger (the decay timer eventually
    /// flips it back). Without forcing it here, segments killed by
    /// player damage (HP=1, easy to hit) stayed in the world for the
    /// full decay duration after the parent tentacle dispatched them
    /// — that's the "floating static claw" the user was seeing on the
    /// chain tip.
    /// </summary>
    public void Despawn()
    {
        if (segmentIds == null) return;

        for (int i = 0; i < segmentIds.Length; i++)
        {
            long id = segmentIds[i];
            if (id == 0) continue;
            Entity seg = tipEntity.World.GetEntityById(id);
            if (seg == null) continue;
            if (seg is EntityAgent agent) agent.AllowDespawn = true;
            if (seg.Alive)
            {
                seg.Die(EnumDespawnReason.Expire);
            }
        }
    }

    /// <summary>
    /// Updates segment positions and orientations along the spline from the
    /// given body anchor to the current tip position.
    ///
    /// Two-pass:
    ///  1. Compute every segment's EXACT position by evaluating the cubic
    ///     Bezier at its arc-length's t parameter (no linear sample-table
    ///     interpolation, so positions sit on the actual curve).
    ///  2. Each segment's tangent is the chord to the NEXT segment's
    ///     position (or to the spline tip for chain[N-1]). With chord
    ///     tangents the trunk's top — which extends `segmentVisualHeight`
    ///     in the tangent direction — lands exactly on the next segment's
    ///     bottom, so consecutive segments visibly touch instead of leaving
    ///     a stair-step gap. The earlier B'(t) tangent gave each segment a
    ///     mathematically smooth direction but didn't enforce that the top
    ///     of one segment land at the bottom of the next, hence the gaps.
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
        EnsurePositionBuffer(N);

        // PASS 1 — compute every segment's exact world position.
        // Segments whose cumulative offset exceeds the current spline length
        // haven't emerged yet; they all stack at the body anchor (which
        // becomes the chain's "loading dock" for un-emerged segments).
        for (int i = 0; i < N; i++)
        {
            if (i == 0)
            {
                segmentPositions[i].Set(reusableAnchor.X, reusableAnchor.Y, reusableAnchor.Z);
                segmentEmerged[i] = false; // keep upright
                continue;
            }
            double distFromTip = DistFromTipFor(i);
            if (distFromTip > splineLength)
            {
                segmentPositions[i].Set(reusableAnchor.X, reusableAnchor.Y, reusableAnchor.Z);
                segmentEmerged[i] = false;
            }
            else
            {
                double arcFromBase = splineLength - distFromTip;
                double t = GetTValueAtArcLength(arcFromBase);
                Vec3d p = SplineHelper.EvalCubicBezier(reusableAnchor, b1, b2, tip, t);
                segmentPositions[i].Set(p.X, p.Y, p.Z);
                segmentEmerged[i] = true;
            }
        }

        // PASS 2 — teleport + orient. Tangent is the chord to the next
        // segment's position (or to the tip for the topmost emerged segment).
        for (int i = 0; i < N; i++)
        {
            Entity seg = segmentEntities[i];
            if (seg == null || !seg.Alive)
            {
                seg = tipEntity.World.GetEntityById(segmentIds[i]);
                segmentEntities[i] = seg;
                if (seg == null || !seg.Alive) continue;
            }

            Vec3d sp = segmentPositions[i];
            seg.TeleportToDouble(sp.X, sp.Y, sp.Z);

            if (i == 0 || !segmentEmerged[i])
            {
                SetOrientation(seg, YawCancelBakedOffset, 0f, 0f);
                continue;
            }

            // Pick the "next" point: the next emerged segment's position, or
            // the spline tip if this is the topmost emerged segment.
            double nx, ny, nz;
            if (i + 1 < N && segmentEmerged[i + 1])
            {
                Vec3d np = segmentPositions[i + 1];
                nx = np.X; ny = np.Y; nz = np.Z;
            }
            else
            {
                nx = tip.X; ny = tip.Y; nz = tip.Z;
            }

            double tx = nx - sp.X;
            double ty = ny - sp.Y;
            double tz = nz - sp.Z;
            double tlen = Math.Sqrt(tx * tx + ty * ty + tz * tz);
            if (tlen > 1e-6)
            {
                tx /= tlen; ty /= tlen; tz /= tlen;

                double yzLen = Math.Sqrt(ty * ty + tz * tz);
                float roll  = (float)Math.Atan2(-tx, yzLen);
                float pitch = (float)Math.Atan2(tz, ty);

                SetOrientation(seg, YawCancelBakedOffset, pitch, roll);
            }
        }
    }

    private double DistFromTipFor(int i)
    {
        // chain[N-1] sits at tipOuterVisualHeight back from the tip; each
        // lower index stacks one segmentVisualHeight farther.
        return tipOuterVisualHeight + Math.Max(0, segmentCount - 1 - i) * segmentVisualHeight;
    }

    // Per-segment world positions, computed in pass 1 and consumed in pass 2.
    private Vec3d[] segmentPositions;
    private bool[] segmentEmerged;

    private void EnsurePositionBuffer(int n)
    {
        if (segmentPositions != null && segmentPositions.Length == n) return;
        segmentPositions = new Vec3d[n];
        segmentEmerged = new bool[n];
        for (int i = 0; i < n; i++) segmentPositions[i] = new Vec3d();
    }

    /// <summary>
    /// Inverts the arc-length parameterisation: given a distance along the
    /// spline from the base, returns the corresponding t in [0,1] for the
    /// underlying cubic Bezier, by linearly interpolating between the two
    /// arc samples that bracket the distance.
    /// </summary>
    private double GetTValueAtArcLength(double dist)
    {
        if (dist <= 0) return 0;
        double maxArc = cumulativeArc[ArcSamples];
        if (dist >= maxArc) return 1;

        for (int i = 1; i <= ArcSamples; i++)
        {
            if (cumulativeArc[i] >= dist)
            {
                double bucket = cumulativeArc[i] - cumulativeArc[i - 1];
                double frac = (bucket > 1e-9) ? (dist - cumulativeArc[i - 1]) / bucket : 0;
                double t0 = (double)(i - 1) / ArcSamples;
                double t1 = (double)i / ArcSamples;
                return t0 + frac * (t1 - t0);
            }
        }
        return 1;
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
