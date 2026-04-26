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
    /// <summary>
    /// Aligns the head's trunk with the spline's tangent at the tip.
    /// The tangent at t=1 is proportional to (tip - b2) of the cubic Bezier.
    /// </summary>
    public static void AlignToTangent(Entity tipEntity, Vec3d bodyAnchor, float archHeightFactor)
    {
        Vec3d tip = tipEntity.Pos.XYZ;
        SplineHelper.ComputeTentacleControlPoints(bodyAnchor, tip, archHeightFactor, out Vec3d _, out Vec3d b2);

        AlignAlongDirection(tipEntity,
            tip.X - b2.X,
            tip.Y - b2.Y,
            tip.Z - b2.Z);
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
