using System;
using Vintagestory.API.MathTools;

namespace UnderwaterHorrors;

public static class SplineHelper
{
    /// <summary>
    /// Allocating wrapper, kept for ad-hoc callers. Hot paths should use
    /// the EvalCubicBezierInto overload below to avoid per-call Vec3d
    /// allocations (≈25 evals per chain per tick × 8 tentacles).
    /// </summary>
    public static Vec3d EvalCubicBezier(Vec3d b0, Vec3d b1, Vec3d b2, Vec3d b3, double t)
    {
        Vec3d r = new Vec3d();
        EvalCubicBezierInto(b0, b1, b2, b3, t, r);
        return r;
    }

    /// <summary>
    /// Writes the cubic Bezier evaluation into <paramref name="output"/>.
    /// Caller-provided buffer; zero allocations.
    /// </summary>
    public static void EvalCubicBezierInto(Vec3d b0, Vec3d b1, Vec3d b2, Vec3d b3, double t, Vec3d output)
    {
        double u = 1.0 - t;
        double uu = u * u;
        double uuu = uu * u;
        double tt = t * t;
        double ttt = tt * t;

        output.X = uuu * b0.X + 3 * uu * t * b1.X + 3 * u * tt * b2.X + ttt * b3.X;
        output.Y = uuu * b0.Y + 3 * uu * t * b1.Y + 3 * u * tt * b2.Y + ttt * b3.Y;
        output.Z = uuu * b0.Z + 3 * uu * t * b1.Z + 3 * u * tt * b2.Z + ttt * b3.Z;
    }

    /// <summary>
    /// Writes b1/b2 into caller-provided buffers — zero allocations.
    ///
    /// Arch height scales with HORIZONTAL distance only. When the tentacle
    /// is rising straight up (dx=dz=0), horizDist=0 so the arch height
    /// collapses to zero and the four control points collapse onto the
    /// line between anchor and tip — the Bezier degenerates to a straight
    /// vertical segment with no overshoot. The previous formula scaled by
    /// total 3D distance, so a 50-block straight rise placed b2 several
    /// blocks above the tip; the Bezier then overshot and curled back
    /// down, packing the topmost segments at non-monotonic Y on the same
    /// XZ axis (the "broken" cluster near the claw).
    /// </summary>
    public static void ComputeTentacleControlPoints(Vec3d anchor, Vec3d tip, float archHeightFactor,
        Vec3d b1Out, Vec3d b2Out)
    {
        double dx = tip.X - anchor.X;
        double dy = tip.Y - anchor.Y;
        double dz = tip.Z - anchor.Z;
        double horizDist = Math.Sqrt(dx * dx + dz * dz);
        double archHeight = horizDist * archHeightFactor;

        // Both control points sit on the linear interpolation between
        // anchor and tip, lifted by archHeight (b1) / half archHeight (b2)
        // so the curve bows upward without overshooting either endpoint.
        b1Out.X = anchor.X + dx * 0.33;
        b1Out.Y = anchor.Y + dy * 0.33 + archHeight;
        b1Out.Z = anchor.Z + dz * 0.33;

        b2Out.X = anchor.X + dx * 0.67;
        b2Out.Y = anchor.Y + dy * 0.67 + archHeight * 0.5;
        b2Out.Z = anchor.Z + dz * 0.67;
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
