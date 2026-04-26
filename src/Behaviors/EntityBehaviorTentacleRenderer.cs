using System;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;

namespace UnderwaterHorrors;

public class EntityBehaviorTentacleRenderer : EntityBehavior
{
    private const int SegmentCount = 10;
    private const double SegmentHeight = 0.5; // 8 pixels = 0.5 blocks per segment

    private double smoothBodyX, smoothBodyY, smoothBodyZ;
    private bool initialized;
    private float tipLerpSpeed;
    private float archHeightFactor;

    // Pre-cached pose name strings to avoid "seg" + i allocation every frame
    private static readonly string[] PoseNames = new string[SegmentCount];

    static EntityBehaviorTentacleRenderer()
    {
        for (int i = 0; i < SegmentCount; i++)
        {
            PoseNames[i] = "seg" + i;
        }
    }

    public EntityBehaviorTentacleRenderer(Entity entity) : base(entity) { }

    public override void Initialize(EntityProperties properties, JsonObject attributes)
    {
        var config = UnderwaterHorrorsModSystem.Config;
        if (config != null)
        {
            tipLerpSpeed = config.TentacleTipLerpSpeed;
            archHeightFactor = config.TentacleArchHeightFactor;
        }
        else
        {
            tipLerpSpeed = 5f;
            archHeightFactor = 0.4f;
        }
    }

    public override void OnGameTick(float deltaTime)
    {
        if (entity.Api.Side != EnumAppSide.Client) return;
        if (entity.WatchedAttributes.GetBool("underwaterhorrors:static", false))
        {
            ClearPoses();
            return;
        }

        // Read synced body position from WatchedAttributes
        double bodyX = entity.WatchedAttributes.GetDouble("underwaterhorrors:bodyX", entity.Pos.X);
        double bodyY = entity.WatchedAttributes.GetDouble("underwaterhorrors:bodyY", entity.Pos.Y - 10);
        double bodyZ = entity.WatchedAttributes.GetDouble("underwaterhorrors:bodyZ", entity.Pos.Z);

        if (!initialized)
        {
            smoothBodyX = bodyX;
            smoothBodyY = bodyY;
            smoothBodyZ = bodyZ;
            initialized = true;
        }
        else
        {
            double lerpFactor = Math.Min(1.0, tipLerpSpeed * deltaTime);
            smoothBodyX += (bodyX - smoothBodyX) * lerpFactor;
            smoothBodyY += (bodyY - smoothBodyY) * lerpFactor;
            smoothBodyZ += (bodyZ - smoothBodyZ) * lerpFactor;
        }

        // Entity position is the tentacle base (shape origin)
        // Body is below somewhere — compute direction from entity toward body
        double dx = smoothBodyX - entity.Pos.X;
        double dy = smoothBodyY - entity.Pos.Y;
        double dz = smoothBodyZ - entity.Pos.Z;
        double dist = Math.Sqrt(dx * dx + dy * dy + dz * dz);

        if (dist < 0.5)
        {
            // Body too close, no bending needed — clear any previous rotations
            ClearPoses();
            return;
        }

        // Normalize direction toward body
        double ndx = dx / dist;
        double ndy = dy / dist;
        double ndz = dz / dist;

        // The shape default direction is +Y (upward). We want the base segments to
        // lean toward the body and the tip segments to remain upright.
        double targetPitchDeg = Math.Atan2(-ndz, -ndy) * (180.0 / Math.PI);
        double targetRollDeg = Math.Atan2(ndx, -ndy) * (180.0 / Math.PI);

        // Clamp maximum bend per segment to avoid extreme distortion
        double maxBendPerSeg = 25.0;
        targetPitchDeg = Clamp(targetPitchDeg, -maxBendPerSeg * SegmentCount, maxBendPerSeg * SegmentCount);
        targetRollDeg = Clamp(targetRollDeg, -maxBendPerSeg * SegmentCount, maxBendPerSeg * SegmentCount);

        var animator = entity.AnimManager?.Animator;
        if (animator == null) return;

        // Distribute bend across segments with graduated falloff
        for (int seg = 0; seg < SegmentCount; seg++)
        {
            double weight = 1.0 - (double)seg / SegmentCount;
            weight *= weight;

            float degX = (float)(targetPitchDeg * weight / SegmentCount);
            float degZ = (float)(targetRollDeg * weight / SegmentCount);

            var pose = animator.GetPosebyName(PoseNames[seg]);
            if (pose != null)
            {
                pose.degOffX = degX;
                pose.degOffZ = degZ;
            }
        }
    }

    private void ClearPoses()
    {
        var animator = entity.AnimManager?.Animator;
        if (animator == null) return;

        for (int seg = 0; seg < SegmentCount; seg++)
        {
            var pose = animator.GetPosebyName(PoseNames[seg]);
            if (pose != null)
            {
                pose.degOffX = 0;
                pose.degOffZ = 0;
            }
        }
    }

    private static double Clamp(double val, double min, double max)
    {
        if (val < min) return min;
        if (val > max) return max;
        return val;
    }

    public override string PropertyName() => "underwaterhorrors:tentaclerenderer";
}
