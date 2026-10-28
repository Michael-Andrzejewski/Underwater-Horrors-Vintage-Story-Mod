using System;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Datastructures;

namespace UnderwaterHorrors;

/// <summary>
/// Hit proxies for the giant serpent, laid along its curved body instead of
/// a straight line, each box the size of the body where it sits.
///
/// The "proxies" table in the entity file comes from the rest model
/// (tools/giantserpent/build_shape.py): one row per box, in blocks at entity
/// size 1 (the entity size scales them): arc behind the mouth (negative for
/// the snout, which reaches past it), box width, and the bottom and top of
/// what is drawn there relative to the spine line: the body with its fins,
/// frills and spikes, not the whiskers. The boxes run from the snout tip to
/// the tip of the tail fin, 4 blocks wide at the snout, 27 across the side
/// fins and 2.5 at the tail tip, and overlap along the body whichever way it
/// points. Each sits
/// on the same trail the client bends the spine along, side wave included
/// (EntityBehaviorGiantSerpentAI.SpinePoint). A dead giant's trail is
/// frozen, so the boxes keep the corpse's last curve and sink with it.
/// </summary>
public class EntityBehaviorGiantSerpentHitProxies : EntityBehaviorSerpentHitProxies
{
    private EntityBehaviorGiantSerpentAI ai;
    private double size = 10;

    // Per proxy, in blocks at entity size 1.
    private float[] widths = Array.Empty<float>();
    private float[] heights = Array.Empty<float>();
    private float[] centers = Array.Empty<float>();

    public EntityBehaviorGiantSerpentHitProxies(Entity entity) : base(entity) { }

    public override void Initialize(EntityProperties properties, JsonObject attributes)
    {
        base.Initialize(properties, attributes);
        size = properties.Client?.Size ?? 10;

        double[][] rows = GiantSerpentRig.Rows(attributes["proxies"]);
        int n = rows.Length;
        offsets = new float[n];
        widths = new float[n];
        heights = new float[n];
        centers = new float[n];
        for (int i = 0; i < n; i++)
        {
            double[] r = rows[i];
            offsets[i] = (float)r[0];
            widths[i] = (float)r[1];
            heights[i] = (float)(r[3] - r[2]);
            centers[i] = (float)((r[2] + r[3]) / 2);
        }
    }

    protected override void PrepareProxy(Entity proxy, int index)
    {
        proxy.WatchedAttributes.SetAttribute(EntitySerpentHitProxy.BoxAttr, new FloatArrayAttribute(new[]
        {
            (float)(widths[index] * size), (float)(heights[index] * size)
        }));
        // A boss fight: its health shows in survival too.
        proxy.WatchedAttributes.SetBool(EntitySerpentHitProxy.AlwaysShowHealthAttr, true);
    }

    protected override double ProxyCenterOffset(int index) => centers[index] * size;

    public override void GetSpinePoint(int index, out double x, out double y, out double z)
    {
        ai ??= entity.GetBehavior<EntityBehaviorGiantSerpentAI>();
        double s = offsets[index] * size;
        if (ai != null)
        {
            ai.SpinePoint(s, out x, out y, out z);
            return;
        }
        x = entity.Pos.X - Math.Sin(entity.Pos.Yaw) * s;
        y = entity.Pos.Y;
        z = entity.Pos.Z - Math.Cos(entity.Pos.Yaw) * s;
    }

    public override string PropertyName() => "underwaterhorrors:giantserpenthitproxies";
}
