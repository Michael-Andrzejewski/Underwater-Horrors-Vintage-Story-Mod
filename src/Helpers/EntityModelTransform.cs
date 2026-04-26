using System;
using Vintagestory.API.Common.Entities;

namespace UnderwaterHorrors;

/// <summary>
/// Replicates VS's EntityShapeRenderer.loadModelMatrix transform in
/// pure C# math, applied to a single point in mesh-local space, to
/// produce the world-space position where that point ends up after
/// the model matrix is applied.
///
/// Used by both BiolumTextureRenderer (which renders the actual mesh
/// at the proper transform) and BioluminescentGlowRenderer (which
/// places billboards at the texture-centroid world position).
///
/// Why this is non-trivial: VS's matrix has a (-0.5, 0, -0.5) trailing
/// translate to center authored 0..16 voxel models on the entity's
/// origin, plus a hitbox-Y pivot wrapping the rotation. Without
/// replicating both, anything you place at "entity.Pos + offset"
/// will sit ~0.75 blocks horizontally away from where VS draws the
/// visible textured mesh.
/// </summary>
public static class EntityModelTransform
{
    /// <summary>
    /// Apply VS's loadModelMatrix transform (in C#, no GPU) to a point
    /// in MESH-LOCAL space (post-tessellator block units, so e.g. the
    /// trunk midpoint of a segment_mid is at (0, 0.281, 0)).
    /// Returns the world-space position.
    ///
    /// scale = entity.Properties.Client.Size (1.5 for our segments).
    /// </summary>
    public static void ApplyModelMatrix(
        Entity entity, float scale,
        double localX, double localY, double localZ,
        out double worldX, out double worldY, out double worldZ)
    {
        double halfH = entity.SelectionBox != null
            ? entity.SelectionBox.Y2 / 2.0
            : 0.05;

        // Step 1 - innermost: T(-0.5, 0, -0.5) (the block-center shift VS
        // applies to every entity model at the end of its matrix build).
        double mx = localX - 0.5;
        double my = localY;
        double mz = localZ - 0.5;

        // Step 2 - Scale by entity size.
        mx *= scale;
        my *= scale;
        mz *= scale;

        // Step 3 - Pre-rotation translate by -halfH so rotation pivots
        // through the hitbox center.
        my -= halfH;

        // Step 4 - Rotation. VS uses Quaterniond X-Y-Z; the resulting
        // matrix is M = Rx * Ry * Rz, which when applied to a vertex v
        // means v gets rotated by Rz first, then Ry, then Rx.
        float pitch       = entity.Pos.Pitch;
        float effectiveYaw = entity.Pos.Yaw + (float)(Math.PI / 2.0);
        float roll        = entity.Pos.Roll;

        double cosR = Math.Cos(roll), sinR = Math.Sin(roll);
        double t1x = mx * cosR - my * sinR;
        double t1y = mx * sinR + my * cosR;
        double t1z = mz;

        double cosY = Math.Cos(effectiveYaw), sinY = Math.Sin(effectiveYaw);
        double t2x =  t1x * cosY + t1z * sinY;
        double t2y =  t1y;
        double t2z = -t1x * sinY + t1z * cosY;

        double cosP = Math.Cos(pitch), sinP = Math.Sin(pitch);
        double t3x =  t2x;
        double t3y =  t2y * cosP - t2z * sinP;
        double t3z =  t2y * sinP + t2z * cosP;

        // Step 5 - Post-rotation translate by +halfH (undo the pivot shift).
        t3y += halfH;

        // Step 6 - Translate by entity.Pos. Y uses InternalY for cross-
        // dimension correctness; X/Z are flat world coords.
        worldX = t3x + entity.Pos.X;
        worldY = t3y + entity.Pos.InternalY;
        worldZ = t3z + entity.Pos.Z;
    }
}
