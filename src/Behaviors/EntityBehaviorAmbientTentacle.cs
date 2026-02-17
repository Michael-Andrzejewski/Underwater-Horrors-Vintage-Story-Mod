using System;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Datastructures;

namespace UnderwaterHorrors;

public class EntityBehaviorAmbientTentacle : EntityBehavior
{
    private double homeX, homeY, homeZ;
    private bool homeRecorded;
    private float elapsed;
    private UnderwaterHorrorsConfig config;

    public EntityBehaviorAmbientTentacle(Entity entity) : base(entity) { }

    public override void Initialize(EntityProperties properties, JsonObject attributes)
    {
        config = UnderwaterHorrorsModSystem.Config;
    }

    public override void OnGameTick(float deltaTime)
    {
        if (!entity.Alive) return;
        if (entity.Api.Side != EnumAppSide.Server) return;

        if (!homeRecorded)
        {
            homeX = entity.SidedPos.X;
            homeY = entity.SidedPos.Y;
            homeZ = entity.SidedPos.Z;
            homeRecorded = true;
        }

        elapsed += deltaTime;
        float amp = config.AmbientTentacleAmplitude;
        float speed = config.AmbientTentacleDriftSpeed;

        double targetX = homeX + Math.Sin(elapsed * speed) * amp;
        double targetY = homeY + Math.Sin(elapsed * speed * 0.7) * amp;
        double targetZ = homeZ + Math.Cos(elapsed * speed * 1.3) * amp;

        double dx = targetX - entity.SidedPos.X;
        double dy = targetY - entity.SidedPos.Y;
        double dz = targetZ - entity.SidedPos.Z;

        entity.SidedPos.Motion.X = dx * 0.05;
        entity.SidedPos.Motion.Y = dy * 0.05;
        entity.SidedPos.Motion.Z = dz * 0.05;
    }

    public override string PropertyName() => "underwaterhorrors:ambienttentacle";
}
