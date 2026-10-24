using Vintagestory.API.Common.Entities;
using Vintagestory.API.Datastructures;

namespace UnderwaterHorrors;

/// <summary>
/// Hit proxies for the giant serpent, laid along its curved body instead of
/// a straight line. Here the "offsets" are arc lengths behind the mouth at
/// entity size 1 (the entity size scales them), and each proxy sits on the
/// same trail the client bends the spine along, side wave included, so the
/// boxes stay on the visible body. A dead giant's trail is frozen, so the
/// boxes keep the corpse's last curve and sink with it.
/// </summary>
public class EntityBehaviorGiantSerpentHitProxies : EntityBehaviorSerpentHitProxies
{
    private EntityBehaviorGiantSerpentAI ai;
    private double size = 10;
    private double waveAmplitude, waveLength, bodyLength;

    private readonly double[] arc = new double[1];
    private readonly double[] sx = new double[1], sy = new double[1], sz = new double[1];
    private readonly double[] so = new double[1], stx = new double[1], stz = new double[1];

    public EntityBehaviorGiantSerpentHitProxies(Entity entity) : base(entity) { }

    public override void Initialize(EntityProperties properties, JsonObject attributes)
    {
        base.Initialize(properties, attributes);
        size = properties.Client?.Size ?? 10;
        waveAmplitude = GiantSerpentRig.WaveAmplitude(properties);
        waveLength = GiantSerpentRig.WaveLength(properties);
        bodyLength = GiantSerpentRig.BodyLength(properties);
    }

    public override void GetSpinePoint(int index, out double x, out double y, out double z)
    {
        ai ??= entity.GetBehavior<EntityBehaviorGiantSerpentAI>();
        SerpentTrail trail = ai?.Trail;
        double s = offsets[index] * size;
        if (trail == null || !trail.Initialized)
        {
            // Before the AI's first tick: straight behind the mouth.
            x = entity.Pos.X - System.Math.Sin(entity.Pos.Yaw) * s;
            y = entity.Pos.Y;
            z = entity.Pos.Z - System.Math.Cos(entity.Pos.Yaw) * s;
            return;
        }

        arc[0] = s;
        trail.SampleSmooth(arc, 1, EntityBehaviorGiantSerpentBody.TangentSpan * size, sx, sy, sz, so, stx, stz);
        double off = SerpentTrail.WaveOffset(s, so[0], waveAmplitude * size, waveLength * size, bodyLength * size);
        // The trail's mouth can lag the entity: a corpse sinks after the
        // trail froze. Shift by the difference so the boxes move with it.
        x = sx[0] + stz[0] * off + (entity.Pos.X - trail.HeadX);
        y = sy[0] + (entity.Pos.Y - trail.HeadY);
        z = sz[0] - stx[0] * off + (entity.Pos.Z - trail.HeadZ);
    }

    public override string PropertyName() => "underwaterhorrors:giantserpenthitproxies";
}
