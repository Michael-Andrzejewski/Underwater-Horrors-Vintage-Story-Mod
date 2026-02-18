using System;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;

namespace UnderwaterHorrors;

public class EntityBehaviorTentacleRenderer : EntityBehavior
{
    private const int SegmentCount = 10;
    private const int SampleCount = SegmentCount + 1; // 11 points for 10 segments
    private const double SegmentHeight = 0.5; // 8 pixels = 0.5 blocks per segment

    private Vec3d smoothedTip;
    private bool initialized;
    private float tipLerpSpeed;
    private float archHeightFactor;

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

        // Read synced tip position from WatchedAttributes
        double tipX = entity.WatchedAttributes.GetDouble("underwaterhorrors:tipX", entity.Pos.X);
        double tipY = entity.WatchedAttributes.GetDouble("underwaterhorrors:tipY", entity.Pos.Y + SegmentCount * SegmentHeight);
        double tipZ = entity.WatchedAttributes.GetDouble("underwaterhorrors:tipZ", entity.Pos.Z);

        if (!initialized)
        {
            smoothedTip = new Vec3d(tipX, tipY, tipZ);
            initialized = true;
        }
        else
        {
            // Lerp toward target tip for smooth motion
            double lerpFactor = Math.Min(1.0, tipLerpSpeed * deltaTime);
            smoothedTip.X += (tipX - smoothedTip.X) * lerpFactor;
            smoothedTip.Y += (tipY - smoothedTip.Y) * lerpFactor;
            smoothedTip.Z += (tipZ - smoothedTip.Z) * lerpFactor;
        }

        Vec3d anchor = entity.Pos.XYZ;

        // Compute Bezier control points
        SplineHelper.ComputeTentacleControlPoints(anchor, smoothedTip, archHeightFactor, out Vec3d b1, out Vec3d b2);

        // Sample 11 points along the spline
        Vec3d[] samples = new Vec3d[SampleCount];
        for (int i = 0; i < SampleCount; i++)
        {
            double t = (double)i / SegmentCount;
            samples[i] = SplineHelper.EvalCubicBezier(anchor, b1, b2, smoothedTip, t);
        }

        // Compute per-segment directions and rotations
        double accumPitch = 0;
        double accumRoll = 0;

        for (int seg = 0; seg < SegmentCount; seg++)
        {
            Vec3d dir = samples[seg + 1].SubCopy(samples[seg]);

            SplineHelper.DirectionToLocalAngles(dir, accumPitch, accumRoll,
                out float degX, out float degZ);

            // Accumulate for next child (since segments are nested)
            accumPitch += degX * Math.PI / 180.0;
            accumRoll += degZ * Math.PI / 180.0;

            // Apply to element pose
            string poseName = "seg" + seg;
            var pose = entity.AnimManager?.Animator?.GetPosebyName(poseName);
            if (pose != null)
            {
                pose.degOffX = degX;
                pose.degOffZ = degZ;
            }
        }
    }

    public override string PropertyName() => "underwaterhorrors:tentaclerenderer";
}
