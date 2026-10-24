using System;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;

namespace UnderwaterHorrors;

/// <summary>
/// Client side: moves the giant serpent smoothly between server updates,
/// in place of the game's interpolateposition behavior.
///
/// The stock interpolation assumes every position packet is 1/15 s after
/// the last. The giant has no physics behavior, and the server sends
/// position packets for such entities whenever a physics tick passed, about
/// 30 a second. Interpolated at the assumed 15 a second, the mouth lagged
/// further and further behind, sped up to catch up, then jumped when the
/// queue overflowed. The body follows the mouth's path, so every hitch bent
/// the spine: the stutter and the twitching between segments.
///
/// This instead dead-reckons: the mouth keeps moving at the velocity of the
/// last packet (the AI writes its true velocity into Pos.Motion), and is
/// eased onto the predicted server position. The giant swims at a steady
/// speed and turns slowly, so the prediction is close and the correction
/// small, whatever the packet rate.
/// </summary>
public class EntityBehaviorGiantSerpentMotion : EntityBehavior, IRenderer
{
    // How fast the drawn position converges on the predicted server
    // position, per second, and the error past which it just snaps.
    private const double Convergence = 4.0;
    private const double YawConvergence = 5.0;
    private const double SnapDistance = 40.0;
    // Never extrapolate further than this past the last packet.
    private const double MaxPredictSeconds = 0.5;

    private ICoreClientAPI capi;

    private double serverX, serverY, serverZ;
    private double velX, velY, velZ;
    private float serverYaw;
    private long receivedMs;

    private double drawX, drawY, drawZ;
    private float drawYaw;
    private bool started;

    public EntityBehaviorGiantSerpentMotion(Entity entity) : base(entity) { }

    public double RenderOrder => 0.0;
    public int RenderRange => 9999;

    public override void Initialize(EntityProperties properties, JsonObject attributes)
    {
        base.Initialize(properties, attributes);
        capi = entity.Api as ICoreClientAPI;
        capi?.Event.RegisterRenderer(this, EnumRenderStage.Before, "underwaterhorrors-giantserpentmotion");
        TakeServerState();
        Snap();
    }

    public override void OnEntityDespawn(EntityDespawnData despawn)
    {
        capi?.Event.UnregisterRenderer(this, EnumRenderStage.Before);
        base.OnEntityDespawn(despawn);
    }

    public override void OnReceivedServerPos(bool isTeleport, ref EnumHandling handled)
    {
        // The packet has just been written into entity.Pos.
        TakeServerState();
        if (isTeleport || !started) Snap();

        // Put the drawn position back until the next frame moves it, so
        // nothing reads the raw packet position in between.
        var pos = entity.Pos;
        pos.SetPos(drawX, drawY, drawZ);
        pos.Yaw = drawYaw;
        pos.Pitch = 0;
        pos.Roll = 0;
    }

    private void TakeServerState()
    {
        var pos = entity.Pos;
        serverX = pos.X;
        serverY = pos.Y;
        serverZ = pos.Z;
        serverYaw = pos.Yaw;
        // Physics units are blocks per 1/60 s.
        velX = pos.Motion.X * 60;
        velY = pos.Motion.Y * 60;
        velZ = pos.Motion.Z * 60;
        receivedMs = capi?.ElapsedMilliseconds ?? 0;
    }

    private void Snap()
    {
        drawX = serverX;
        drawY = serverY;
        drawZ = serverZ;
        drawYaw = serverYaw;
        started = true;
    }

    public void OnRenderFrame(float deltaTime, EnumRenderStage stage)
    {
        if (capi.IsGamePaused || !started) return;
        double dt = Math.Min(deltaTime, 0.1);

        double age = Math.Clamp((capi.ElapsedMilliseconds - receivedMs) / 1000.0, 0, MaxPredictSeconds);
        double px = serverX + velX * age;
        double py = serverY + velY * age;
        double pz = serverZ + velZ * age;

        // Move on at the server's velocity, then ease onto the prediction.
        drawX += velX * dt;
        drawY += velY * dt;
        drawZ += velZ * dt;
        double ex = px - drawX, ey = py - drawY, ez = pz - drawZ;
        if (ex * ex + ey * ey + ez * ez > SnapDistance * SnapDistance)
        {
            drawX = px; drawY = py; drawZ = pz;
        }
        else
        {
            double k = 1 - Math.Exp(-Convergence * dt);
            drawX += ex * k;
            drawY += ey * k;
            drawZ += ez * k;
        }

        double ky = 1 - Math.Exp(-YawConvergence * dt);
        drawYaw = GameMath.Mod(drawYaw + GameMath.AngleRadDistance(drawYaw, serverYaw) * (float)ky, GameMath.TWOPI);

        var pos = entity.Pos;
        pos.SetPos(drawX, drawY, drawZ);
        pos.Yaw = drawYaw;
        pos.Pitch = 0;
        pos.Roll = 0;
    }

    public void Dispose() { }

    public override string PropertyName() => "underwaterhorrors:giantserpentmotion";
}
