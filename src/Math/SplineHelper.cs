using System;
using Vintagestory.API.MathTools;

namespace UnderwaterHorrors;

public static class SplineHelper
{
    public static Vec3d EvalCubicBezier(Vec3d b0, Vec3d b1, Vec3d b2, Vec3d b3, double t)
    {
        double u = 1.0 - t;
        double uu = u * u;
        double uuu = uu * u;
        double tt = t * t;
        double ttt = tt * t;

        return new Vec3d(
            uuu * b0.X + 3 * uu * t * b1.X + 3 * u * tt * b2.X + ttt * b3.X,
            uuu * b0.Y + 3 * uu * t * b1.Y + 3 * u * tt * b2.Y + ttt * b3.Y,
            uuu * b0.Z + 3 * uu * t * b1.Z + 3 * u * tt * b2.Z + ttt * b3.Z
        );
    }

    public static void ComputeTentacleControlPoints(Vec3d anchor, Vec3d tip, float archHeightFactor,
        out Vec3d b1, out Vec3d b2)
    {
        double dx = tip.X - anchor.X;
        double dy = tip.Y - anchor.Y;
        double dz = tip.Z - anchor.Z;
        double horizDist = Math.Sqrt(dx * dx + dz * dz);
        double totalDist = Math.Sqrt(dx * dx + dy * dy + dz * dz);

        double archHeight = totalDist * archHeightFactor;

        // B1: rises from anchor, 1/3 along toward tip horizontally, with arch height added
        b1 = new Vec3d(
            anchor.X + dx * 0.33,
            anchor.Y + archHeight,
            anchor.Z + dz * 0.33
        );

        // B2: near tip, 2/3 along, slightly above tip approaching from above
        b2 = new Vec3d(
            anchor.X + dx * 0.67,
            tip.Y + archHeight * 0.3,
            anchor.Z + dz * 0.67
        );
    }

    /// <summary>
    /// Decomposes a world-space direction vector into pitch (degOffX) and roll (degOffZ)
    /// relative to a parent's accumulated rotation, for VS ElementPose.
    /// Returns angles in degrees.
    /// </summary>
    public static void DirectionToLocalAngles(Vec3d worldDir, double parentPitchRad, double parentRollRad,
        out float degOffX, out float degOffZ)
    {
        double len = worldDir.Length();
        if (len < 1e-8)
        {
            degOffX = 0;
            degOffZ = 0;
            return;
        }

        double nx = worldDir.X / len;
        double ny = worldDir.Y / len;
        double nz = worldDir.Z / len;

        // World-space pitch and roll of this direction
        // Pitch = rotation around X axis (tilt forward/back in Y-Z plane relative to up)
        // Roll = rotation around Z axis (tilt left/right in X-Y plane)
        // The tentacle's default direction is +Y (upward), so we compute angles relative to that
        double worldPitch = Math.Atan2(-nz, ny);
        double worldRoll = Math.Atan2(nx, ny);

        // Local angles = world angles minus parent accumulated angles
        double localPitch = worldPitch - parentPitchRad;
        double localRoll = worldRoll - parentRollRad;

        degOffX = (float)(localPitch * 180.0 / Math.PI);
        degOffZ = (float)(localRoll * 180.0 / Math.PI);
    }
}
