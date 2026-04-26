using System;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.MathTools;

namespace UnderwaterHorrors;

/// <summary>
/// Aligns a tentacle head/tip entity (krakententacle / krakenambienttentacle)
/// so its trunk +Y axis points along a target direction. Used for the
/// "grabber faces along the spline tangent" effect — when the trunk aims
/// up the spline, the bell + grabber at the top of the head model end up
/// pointing wherever the spline is going.
///
/// Same world-axis pitch+roll decomposition as TentacleSegmentChain (see
/// that file for the math derivation). Snaps directly — no lerp — so the
/// head doesn't carry visible momentum from frame to frame.
/// </summary>
public static class TentacleHeadAlignment
{
    // Reusable scratch buffers — server-side only, single-threaded usage.
    // Avoid allocating three Vec3d objects per AlignToTangent call (one
    // per active tentacle per tick).
    private static readonly Vec3d _scratchAnchor = new Vec3d();
    private static readonly Vec3d _scratchTip = new Vec3d();
    private static readonly Vec3d _scratchB1 = new Vec3d();
    private static readonly Vec3d _scratchB2 = new Vec3d();

    /// <summary>
    /// Aligns the head's trunk with the spline's tangent at the tip.
    /// The tangent at t=1 is proportional to (tip - b2) of the cubic Bezier.
    /// </summary>
    public static void AlignToTangent(Entity tipEntity, double anchorX, double anchorY, double anchorZ, float archHeightFactor)
    {
        _scratchAnchor.Set(anchorX, anchorY, anchorZ);
        _scratchTip.Set(tipEntity.Pos.X, tipEntity.Pos.Y, tipEntity.Pos.Z);
        SplineHelper.ComputeTentacleControlPoints(_scratchAnchor, _scratchTip, archHeightFactor, _scratchB1, _scratchB2);

        AlignAlongDirection(tipEntity,
            _scratchTip.X - _scratchB2.X,
            _scratchTip.Y - _scratchB2.Y,
            _scratchTip.Z - _scratchB2.Z);
    }

    /// <summary>
    /// Aligns the head's trunk so it points from the entity toward
    /// <paramref name="targetX/Y/Z"/>. Used when the head should aim
    /// at a player (Reaching/Dragging).
    /// </summary>
    public static void AlignToward(Entity tipEntity, double targetX, double targetY, double targetZ)
    {
        AlignAlongDirection(tipEntity,
            targetX - tipEntity.Pos.X,
            targetY - tipEntity.Pos.Y,
            targetZ - tipEntity.Pos.Z);
    }

    private static void AlignAlongDirection(Entity tipEntity, double dx, double dy, double dz)
    {
        double dist = Math.Sqrt(dx * dx + dy * dy + dz * dz);
        if (dist < 1e-6) return;
        dx /= dist; dy /= dist; dz /= dist;

        double yzLen = Math.Sqrt(dy * dy + dz * dz);
        float roll  = (float)Math.Atan2(-dx, yzLen);
        float pitch = (float)Math.Atan2(dz, dy);

        tipEntity.Pos.Yaw   = 0f;
        tipEntity.Pos.Pitch = pitch;
        tipEntity.Pos.Roll  = roll;
    }
}
